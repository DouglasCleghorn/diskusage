using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using DiskUsage.Core;
using Parquet;
using Parquet.Schema;

namespace DiskUsage.Benchmarks;

internal static class ParquetExportExperiments
{
    private static readonly Experiment[] Experiments =
    [
        new("plain-50k", EncodingHint.Default, 50_000),
        new("byte-split-50k", EncodingHint.ByteSplitStream, 50_000),
        new("delta-50k", EncodingHint.DeltaBinaryPacked, 50_000),
        new("plain-250k", EncodingHint.Default, 250_000),
        new("byte-split-250k", EncodingHint.ByteSplitStream, 250_000),
        new("delta-250k", EncodingHint.DeltaBinaryPacked, 250_000),
        new("plain-1m", EncodingHint.Default, 1_000_000),
        new("byte-split-1m", EncodingHint.ByteSplitStream, 1_000_000),
        new("delta-1m", EncodingHint.DeltaBinaryPacked, 1_000_000)
    ];

    public static async Task RunAsync(string[] args, CancellationToken cancellationToken)
    {
        var rootPath = Path.GetFullPath(args.FirstOrDefault() ?? @"C:\");
        if (!Directory.Exists(rootPath))
        {
            throw new DirectoryNotFoundException($"Benchmark path does not exist: {rootPath}");
        }

        var outputDirectory = Path.GetFullPath(args.Skip(1).FirstOrDefault() ??
            Path.Combine("artifacts", $"parquet-benchmark-{DateTime.UtcNow:yyyyMMdd-HHmmss}"));
        Directory.CreateDirectory(outputDirectory);

        Console.WriteLine($"Scanning {rootPath} once with the production UI scan settings...");
        var lastProgressAt = Stopwatch.StartNew();
        var progress = new InlineProgress<ScanProgress>(value =>
        {
            if (!value.IsComplete && lastProgressAt.Elapsed < TimeSpan.FromSeconds(10))
            {
                return;
            }

            lastProgressAt.Restart();
            Console.WriteLine(
                $"scan: {value.Files:N0} files, {value.Directories:N0} directories, " +
                $"{FormatBytes(value.Bytes)}, {value.Skipped:N0} skipped, {value.Elapsed:g}");
        });

        var scan = await new FileSystemScanner().ScanAsync(
            rootPath,
            new ScanOptions
            {
                IncludeHidden = true,
                FollowLinks = false,
                CollectFiles = true
            },
            progress,
            cancellationToken);

        var records = EnumerateCachedFiles(scan.Root).ToArray();
        if (records.LongLength != scan.Files)
        {
            throw new InvalidDataException(
                $"The retained record count ({records.LongLength:N0}) does not match the scan ({scan.Files:N0}).");
        }

        Console.WriteLine(
            $"Scan complete: {scan.Files:N0} files, {scan.Directories:N0} directories, " +
            $"{FormatBytes(scan.Bytes)}, {scan.Skipped:N0} skipped in {scan.Elapsed:g}.");
        Console.WriteLine($"Reports: {outputDirectory}");

        await WarmUpAsync(records, outputDirectory, cancellationToken);

        var results = new List<ExperimentResult>();
        foreach (var experiment in Experiments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            Console.WriteLine(
                $"Writing {experiment.Name}: {experiment.Encoding}, row group {experiment.RowGroupSize:N0}...");

            var outputPath = Path.Combine(outputDirectory, $"{experiment.Name}.parquet.tmp");
            try
            {
                var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
                var writeStopwatch = Stopwatch.StartNew();
                await WriteAsync(records, outputPath, experiment, cancellationToken);
                writeStopwatch.Stop();
                var allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
                var fileBytes = new FileInfo(outputPath).Length;

                var validationStopwatch = Stopwatch.StartNew();
                var validation = await ValidateAsync(outputPath, records.LongLength, cancellationToken);
                validationStopwatch.Stop();

                var result = new ExperimentResult(
                    experiment.Name,
                    experiment.Encoding.ToString(),
                    experiment.RowGroupSize,
                    records.LongLength,
                    fileBytes,
                    writeStopwatch.Elapsed.TotalSeconds,
                    validationStopwatch.Elapsed.TotalSeconds,
                    records.LongLength / writeStopwatch.Elapsed.TotalSeconds,
                    allocatedBytes,
                    validation.RowGroupCount,
                    validation.Encodings);
                results.Add(result);
                SaveReport(outputDirectory, rootPath, scan, results);

                Console.WriteLine(
                    $"  {FormatBytes(fileBytes)} in {result.WriteSeconds:N2}s " +
                    $"({result.FilesPerSecond:N0} files/s); read-back {result.ValidationSeconds:N2}s; " +
                    $"encodings {result.Encodings}");
            }
            finally
            {
                File.Delete(outputPath);
            }
        }

        var smallest = results.MinBy(result => result.FileBytes)!;
        var fastest = results.MinBy(result => result.WriteSeconds)!;
        Console.WriteLine(
            $"Smallest: {smallest.Name}, {FormatBytes(smallest.FileBytes)}. " +
            $"Fastest: {fastest.Name}, {fastest.WriteSeconds:N2}s.");
        Console.WriteLine($"Final report: {Path.Combine(outputDirectory, "results.json")}");
    }

    private static async Task WarmUpAsync(
        IReadOnlyList<FileRecord> records,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        var sampleCount = Math.Min(records.Count, 10_000);
        var sample = records.Take(sampleCount).ToArray();
        foreach (var encoding in new[]
                 {
                     EncodingHint.Default,
                     EncodingHint.ByteSplitStream,
                     EncodingHint.DeltaBinaryPacked
                 })
        {
            var path = Path.Combine(outputDirectory, $"warmup-{encoding}.parquet.tmp");
            try
            {
                await WriteAsync(sample, path, new Experiment("warmup", encoding, 10_000), cancellationToken);
                await ValidateAsync(path, sampleCount, cancellationToken);
            }
            finally
            {
                File.Delete(path);
            }
        }
    }

    private static async Task WriteAsync(
        IReadOnlyList<FileRecord> records,
        string outputPath,
        Experiment experiment,
        CancellationToken cancellationToken)
    {
        var fullPath = new DataField<string>("full_path");
        var size = new DataField<long>("size_bytes");
        var created = new DataField<DateTime>("created_utc");
        var modified = new DataField<DateTime>("modified_utc");
        var schema = new ParquetSchema(fullPath, size, created, modified);
        var options = new ParquetOptions
        {
            CompressionMethod = CompressionMethod.Zstd,
            CompressionLevel = CompressionLevel.Optimal
        };
        if (experiment.Encoding != EncodingHint.Default)
        {
            options.ColumnEncodingHints[size.Path.ToString()] = experiment.Encoding;
        }

        await using var output = new FileStream(
            outputPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var writer = await ParquetWriter.CreateAsync(
            schema,
            output,
            options,
            append: false,
            cancellationToken);

        for (var offset = 0; offset < records.Count; offset += experiment.RowGroupSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(experiment.RowGroupSize, records.Count - offset);
            var paths = new string[count];
            var sizes = new long[count];
            var createdValues = new DateTime[count];
            var modifiedValues = new DateTime[count];
            for (var index = 0; index < count; index++)
            {
                var record = records[offset + index];
                paths[index] = record.FullPath;
                sizes[index] = record.Size;
                createdValues[index] = record.CreatedUtc;
                modifiedValues[index] = record.ModifiedUtc;
            }

            using var group = writer.CreateRowGroup();
            await group.WriteAsync(fullPath, paths);
            await group.WriteAsync<long>(size, sizes, cancellationToken: cancellationToken);
            await group.WriteAsync<DateTime>(created, createdValues, cancellationToken: cancellationToken);
            await group.WriteAsync<DateTime>(modified, modifiedValues, cancellationToken: cancellationToken);
        }
    }

    private static async Task<ValidationResult> ValidateAsync(
        string outputPath,
        long expectedRows,
        CancellationToken cancellationToken)
    {
        await using var reader = await ParquetReader.CreateAsync(outputPath);
        var fields = reader.Schema.GetDataFields();
        long rows = 0;
        for (var rowGroupIndex = 0; rowGroupIndex < reader.RowGroupCount; rowGroupIndex++)
        {
            using var group = reader.OpenRowGroupReader(rowGroupIndex);
            rows += group.RowCount;
            foreach (var field in fields)
            {
                using var data = await group.ReadRawColumnDataBaseAsync(field, cancellationToken);
            }
        }

        if (rows != expectedRows)
        {
            throw new InvalidDataException($"Expected {expectedRows:N0} rows, read {rows:N0}.");
        }

        var encodings = reader.Metadata!.RowGroups
            .SelectMany(group => group.Columns)
            .Where(column => column.MetaData is not null)
            .GroupBy(column => string.Join('.', column.MetaData!.PathInSchema))
            .OrderBy(group => group.Key)
            .Select(group => $"{group.Key}={string.Join('+', group.SelectMany(column => column.MetaData!.Encodings).Distinct().Order())}");
        return new ValidationResult(reader.RowGroupCount, string.Join("; ", encodings));
    }

    private static IEnumerable<FileRecord> EnumerateCachedFiles(UsageNode root)
    {
        var pending = new Stack<UsageNode>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            foreach (var file in directory.Files)
            {
                yield return file;
            }

            var children = directory.Children;
            for (var index = children.Count - 1; index >= 0; index--)
            {
                pending.Push(children[index]);
            }
        }
    }

    private static void SaveReport(
        string outputDirectory,
        string rootPath,
        ScanResult scan,
        IReadOnlyList<ExperimentResult> results)
    {
        var report = new BenchmarkReport(
            DateTimeOffset.UtcNow,
            Environment.MachineName,
            rootPath,
            scan.Files,
            scan.Directories,
            scan.Bytes,
            scan.Skipped,
            scan.Elapsed.TotalSeconds,
            results);
        File.WriteAllText(
            Path.Combine(outputDirectory, "results.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string FormatBytes(long bytes)
    {
        string[] suffixes = ["B", "KiB", "MiB", "GiB", "TiB"];
        var value = (double)bytes;
        var suffix = 0;
        while (value >= 1024 && suffix < suffixes.Length - 1)
        {
            value /= 1024;
            suffix++;
        }

        return $"{value:N2} {suffixes[suffix]}";
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed record Experiment(string Name, EncodingHint Encoding, int RowGroupSize);

    private sealed record ValidationResult(int RowGroupCount, string Encodings);

    private sealed record ExperimentResult(
        string Name,
        string Encoding,
        int RowGroupSize,
        long Files,
        long FileBytes,
        double WriteSeconds,
        double ValidationSeconds,
        double FilesPerSecond,
        long AllocatedBytes,
        int RowGroupCount,
        string Encodings);

    private sealed record BenchmarkReport(
        DateTimeOffset CompletedUtc,
        string MachineName,
        string RootPath,
        long Files,
        long Directories,
        long SourceBytes,
        long Skipped,
        double ScanSeconds,
        IReadOnlyList<ExperimentResult> Results);
}
