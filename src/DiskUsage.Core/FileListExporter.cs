using System.IO.Compression;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using DiskUsage.Core;
using Parquet;
using Parquet.Schema;

namespace DiskUsage.Core;

public sealed class FileListExporter
{
    private const int RowGroupSize = 250_000;
    private readonly FileSystemScanner _scanner = new();

    public async Task<ExportResult> ExportAsync(
        string rootPath,
        string outputPath,
        ExportFormat format,
        ScanOptions options,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken,
        int? compressionLevel = null)
    {
        return await ExportAtomicallyAsync(
            outputPath,
            (output, outputName) => ExportAsync(
                rootPath,
                output,
                outputName,
                format,
                options,
                progress,
                cancellationToken,
                compressionLevel),
            cancellationToken);
    }

    public async Task<ExportResult> ExportRecordsAsync(
        IEnumerable<FileRecord> records,
        string outputPath,
        ExportFormat format,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken,
        int? compressionLevel = null)
    {
        ArgumentNullException.ThrowIfNull(records);
        return await ExportAtomicallyAsync(
            outputPath,
            (output, outputName) => ExportRecordsCoreAsync(
                EnumerateCachedRecordsAsync(records, progress, cancellationToken),
                output,
                outputName,
                format,
                compressionLevel,
                cancellationToken),
            cancellationToken);
    }

    public async Task<ExportResult> ExportAsync(
        string rootPath,
        Stream output,
        string outputName,
        ExportFormat format,
        ScanOptions options,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken,
        int? compressionLevel = null)
    {
        var records = _scanner.EnumerateFilesAsync(rootPath, options, progress, cancellationToken);
        return await ExportRecordsCoreAsync(records, output, outputName, format, compressionLevel, cancellationToken);
    }

    private static async Task<ExportResult> ExportAtomicallyAsync(
        string outputPath,
        Func<Stream, string, Task<ExportResult>> writeAsync,
        CancellationToken cancellationToken)
    {
        var fullOutputPath = Path.GetFullPath(outputPath);
        var directory = Path.GetDirectoryName(fullOutputPath) ?? Environment.CurrentDirectory;
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $".diskusage-{Guid.NewGuid():N}.tmp");
        var committed = false;

        try
        {
            ExportResult result;
            await using (var output = new FileStream(
                             tempPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 1024 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                result = await writeAsync(output, Path.GetFileName(fullOutputPath));
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(tempPath, fullOutputPath, overwrite: true);
            committed = true;
            return result with { OutputPath = fullOutputPath };
        }
        finally
        {
            if (!committed)
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // A failed cleanup never replaces the user's existing destination file.
                }
            }
        }
    }

    private static async Task<ExportResult> ExportRecordsCoreAsync(
        IAsyncEnumerable<FileRecord> records,
        Stream output,
        string outputName,
        ExportFormat format,
        int? compressionLevel,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(output);
        if (!output.CanWrite)
        {
            throw new ArgumentException("Output stream must be writable.", nameof(output));
        }

        if (format.DataFormat == ExportDataFormat.Parquet && !output.CanSeek)
        {
            throw new ArgumentException("Parquet output requires a seekable stream.", nameof(output));
        }

        format.ValidateCompressionLevel(compressionLevel);
        var result = format.DataFormat switch
        {
            ExportDataFormat.Parquet => await WriteParquetAsync(records, output, compressionLevel, cancellationToken),
            ExportDataFormat.Csv or ExportDataFormat.Tsv => await WriteDelimitedAsync(
                records,
                outputName,
                output,
                format,
                compressionLevel,
                cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(format.DataFormat))
        };

        await output.FlushAsync(cancellationToken);
        return result with { OutputPath = outputName };
    }

    private static async Task<ExportResult> WriteDelimitedAsync(
        IAsyncEnumerable<FileRecord> records,
        string outputPath,
        Stream output,
        ExportFormat format,
        int? compressionLevel,
        CancellationToken cancellationToken)
    {
        var delimiter = format.Delimiter();
        ZipArchive? archive = null;
        Stream? compressionStream = null;

        try
        {
            var destination = output;
            switch (format.Compression)
            {
                case ExportCompression.Brotli:
                    compressionStream = new BrotliStream(
                        output,
                        new BrotliCompressionOptions { Quality = compressionLevel ?? 4 },
                        leaveOpen: true);
                    destination = compressionStream;
                    break;
                case ExportCompression.Gzip:
                    var gzipOptions = new ZLibCompressionOptions();
                    if (compressionLevel is not null)
                    {
                        gzipOptions.CompressionLevel = compressionLevel.Value;
                    }

                    compressionStream = new GZipStream(output, gzipOptions, leaveOpen: true);
                    destination = compressionStream;
                    break;
                case ExportCompression.Zip:
                    archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
                    var entry = archive.CreateEntry(GetZipEntryName(outputPath, format), GetZipCompressionLevel(compressionLevel));
                    compressionStream = entry.Open();
                    destination = compressionStream;
                    break;
            }

            await using var writer = new StreamWriter(destination, new UTF8Encoding(false), 1024 * 64, leaveOpen: true);

            await writer.WriteLineAsync(string.Join(delimiter,
                "full_path", "size_bytes", "created_utc", "modified_utc"));

            long files = 0;
            long bytes = 0;
            await foreach (var file in records.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var values = new[]
                {
                    file.FullPath,
                    file.Size.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    file.CreatedUtc.ToString("O"),
                    file.ModifiedUtc.ToString("O")
                };
                await writer.WriteLineAsync(string.Join(delimiter, values.Select(value => Escape(value, delimiter))));
                files++;
                bytes += file.Size;
            }

            await writer.FlushAsync(cancellationToken);
            return new ExportResult(string.Empty, files, bytes);
        }
        finally
        {
            if (compressionStream is not null)
            {
                await compressionStream.DisposeAsync();
            }

            archive?.Dispose();
        }
    }

    private static string GetZipEntryName(string outputPath, ExportFormat format)
    {
        var fileName = Path.GetFileName(outputPath);
        if (fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            fileName = fileName[..^4];
        }

        var dataExtension = format.DataFormat == ExportDataFormat.Csv ? ".csv" : ".tsv";
        return fileName.EndsWith(dataExtension, StringComparison.OrdinalIgnoreCase)
            ? fileName
            : fileName + dataExtension;
    }

    private static CompressionLevel GetZipCompressionLevel(int? compressionLevel) => compressionLevel switch
    {
        null => CompressionLevel.Optimal,
        0 => CompressionLevel.NoCompression,
        <= 3 => CompressionLevel.Fastest,
        <= 7 => CompressionLevel.Optimal,
        _ => CompressionLevel.SmallestSize
    };

    private static CompressionLevel GetParquetCompressionLevel(int? compressionLevel) => compressionLevel switch
    {
        null => CompressionLevel.Optimal,
        0 => CompressionLevel.NoCompression,
        <= 3 => CompressionLevel.Fastest,
        <= 7 => CompressionLevel.Optimal,
        _ => CompressionLevel.SmallestSize
    };

    private static async Task<ExportResult> WriteParquetAsync(
        IAsyncEnumerable<FileRecord> records,
        Stream output,
        int? compressionLevel,
        CancellationToken cancellationToken)
    {
        var fullPath = new DataField<string>("full_path");
        var size = new DataField<long>("size_bytes");
        var created = new DataField<DateTime>("created_utc");
        var modified = new DataField<DateTime>("modified_utc");
        var schema = new ParquetSchema(fullPath, size, created, modified);
        var parquetOptions = new ParquetOptions
        {
            CompressionMethod = CompressionMethod.Zstd,
            CompressionLevel = GetParquetCompressionLevel(compressionLevel)
        };
        parquetOptions.ColumnEncodingHints[size.Path.ToString()] = EncodingHint.ByteSplitStream;

        await using var parquetWriter = await ParquetWriter.CreateAsync(
            schema,
            output,
            options: parquetOptions,
            append: false,
            cancellationToken: cancellationToken);

        var batch = new List<FileRecord>(RowGroupSize);
        long files = 0;
        long bytes = 0;
        await foreach (var file in records.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            batch.Add(file);
            files++;
            bytes += file.Size;

            if (batch.Count == RowGroupSize)
            {
                await WriteRowGroupAsync(parquetWriter, batch, fullPath, size, created, modified, cancellationToken);
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            await WriteRowGroupAsync(parquetWriter, batch, fullPath, size, created, modified, cancellationToken);
        }

        return new ExportResult(string.Empty, files, bytes);
    }

    private static async IAsyncEnumerable<FileRecord> EnumerateCachedRecordsAsync(
        IEnumerable<FileRecord> records,
        IProgress<ScanProgress>? progress,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var nextProgressAt = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 2;
        long files = 0;
        long bytes = 0;

        await Task.CompletedTask.ConfigureAwait(false);
        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return record;
            files++;
            bytes += record.Size;

            var now = Stopwatch.GetTimestamp();
            if (now >= nextProgressAt)
            {
                progress?.Report(new ScanProgress(files, 0, bytes, 0, record.FullPath, stopwatch.Elapsed));
                nextProgressAt = now + Stopwatch.Frequency / 2;
            }
        }

        progress?.Report(new ScanProgress(files, 0, bytes, 0, string.Empty, stopwatch.Elapsed, IsComplete: true));
    }

    private static async Task WriteRowGroupAsync(
        ParquetWriter writer,
        IReadOnlyList<FileRecord> batch,
        DataField<string> fullPath,
        DataField<long> size,
        DataField<DateTime> created,
        DataField<DateTime> modified,
        CancellationToken cancellationToken)
    {
        using var group = writer.CreateRowGroup();
        cancellationToken.ThrowIfCancellationRequested();
        await group.WriteAsync(fullPath, batch.Select(file => file.FullPath).ToArray());
        await group.WriteAsync<long>(size, batch.Select(file => file.Size).ToArray(), cancellationToken: cancellationToken);
        await group.WriteAsync<DateTime>(created, batch.Select(file => file.CreatedUtc).ToArray(), cancellationToken: cancellationToken);
        await group.WriteAsync<DateTime>(modified, batch.Select(file => file.ModifiedUtc).ToArray(), cancellationToken: cancellationToken);
    }

    private static string Escape(string value, char delimiter)
    {
        if (!value.Contains(delimiter) && !value.Contains('"') && !value.Contains('\r') && !value.Contains('\n'))
        {
            return value;
        }

        return $"\"{value.Replace("\"", "\"\"")}\"";
    }
}

public sealed record ExportResult(string OutputPath, long Files, long Bytes);
