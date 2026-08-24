using System.IO.Compression;
using DiskUsage.Cli;
using DiskUsage.Core;
using Parquet;
using Parquet.Meta;

namespace DiskUsage.Core.Tests;

[TestClass]
public sealed class FileListExporterTests
{
    [TestMethod]
    public async Task ExportAsync_writes_tsv_inventory()
    {
        using var fixture = new ExportFixture();
        var output = Path.Combine(fixture.Path, "inventory.tsv");

        var result = await Export(fixture, output, ExportFormat.Tsv);
        var text = await File.ReadAllTextAsync(output);

        Assert.AreEqual(1, result.Files);
        StringAssert.StartsWith(text, "full_path\tsize_bytes\tcreated_utc\tmodified_utc");
        StringAssert.Contains(text, "payload.txt");
    }

    [TestMethod]
    public async Task ExportAsync_writes_plain_csv_inventory()
    {
        using var fixture = new ExportFixture();
        var output = Path.Combine(fixture.Path, "inventory.csv");

        await Export(fixture, output, ExportFormat.Csv);
        var text = await File.ReadAllTextAsync(output);

        StringAssert.StartsWith(text, "full_path,size_bytes,created_utc,modified_utc");
        StringAssert.Contains(text, "payload.txt");
    }

    [TestMethod]
    public async Task ExportAsync_writes_brotli_compressed_csv_inventory()
    {
        using var fixture = new ExportFixture();
        var output = Path.Combine(fixture.Path, "inventory.csv.br");

        await Export(fixture, output, ExportFormat.CsvBrotli);

        await using var file = File.OpenRead(output);
        await using var brotli = new BrotliStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(brotli);
        var text = await reader.ReadToEndAsync();
        StringAssert.StartsWith(text, "full_path,size_bytes,created_utc,modified_utc");
        StringAssert.Contains(text, "payload.txt");
    }

    [TestMethod]
    public async Task ExportAsync_writes_gzip_compressed_tsv_at_requested_level()
    {
        using var fixture = new ExportFixture();
        var output = Path.Combine(fixture.Path, "inventory.tsv.gz");

        await Export(fixture, output, ExportFormat.TsvGzip, compressionLevel: 6);

        await using var file = File.OpenRead(output);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        var text = await reader.ReadToEndAsync();
        StringAssert.StartsWith(text, "full_path\tsize_bytes\tcreated_utc\tmodified_utc");
        StringAssert.Contains(text, "payload.txt");
    }

    [TestMethod]
    public async Task ExportAsync_writes_zip_archive_with_csv_entry()
    {
        using var fixture = new ExportFixture();
        var output = Path.Combine(fixture.Path, "inventory.csv.zip");

        await Export(fixture, output, ExportFormat.CsvZip, compressionLevel: 9);

        using var archive = ZipFile.OpenRead(output);
        Assert.HasCount(1, archive.Entries);
        var entry = archive.Entries.Single();
        Assert.AreEqual("inventory.csv", entry.Name);
        using var reader = new StreamReader(entry.Open());
        var text = await reader.ReadToEndAsync();
        StringAssert.StartsWith(text, "full_path,size_bytes,created_utc,modified_utc");
    }

    [TestMethod]
    public async Task ExportAsync_writes_readable_parquet_inventory()
    {
        using var fixture = new ExportFixture();
        var output = Path.Combine(fixture.Path, "inventory.parquet");

        await Export(fixture, output, ExportFormat.Parquet);

        await using var reader = await ParquetReader.CreateAsync(output);
        Assert.AreEqual(1, reader.RowGroupCount);
        CollectionAssert.AreEqual(
            new[] { "full_path", "size_bytes", "created_utc", "modified_utc" },
            reader.Schema.GetDataFields().Select(field => field.Name).ToArray());
        Assert.IsNotNull(reader.Metadata);
        var metadata = reader.Metadata!.RowGroups[0].Columns[0].MetaData;
        Assert.IsNotNull(metadata);
        Assert.AreEqual(CompressionCodec.ZSTD, metadata.Codec);

        var sizeMetadata = reader.Metadata.RowGroups[0].Columns
            .Select(column => column.MetaData)
            .Single(column => column is not null && column.PathInSchema.SequenceEqual(["size_bytes"]));
        CollectionAssert.Contains(sizeMetadata!.Encodings, Parquet.Meta.Encoding.BYTE_STREAM_SPLIT);
    }

    [TestMethod]
    public async Task ExportAsync_streams_plain_text_without_closing_destination()
    {
        using var fixture = new ExportFixture();
        await using var output = new MemoryStream();

        var result = await new FileListExporter().ExportAsync(
            fixture.SourcePath,
            output,
            "inventory.tsv",
            ExportFormat.Tsv,
            new ScanOptions { IncludeHidden = true },
            progress: null,
            CancellationToken.None);

        Assert.AreEqual(1, result.Files);
        Assert.IsTrue(output.CanWrite);
        output.Position = 0;
        using var reader = new StreamReader(output, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024, leaveOpen: true);
        var text = await reader.ReadToEndAsync();
        StringAssert.StartsWith(text, "full_path\tsize_bytes\tcreated_utc\tmodified_utc");
    }

    [TestMethod]
    public async Task ExportRecordsAsync_writes_cached_records_without_a_source_directory()
    {
        using var fixture = new ExportFixture();
        var output = Path.Combine(fixture.Path, "cached.csv");
        var cachedPath = Path.Combine(fixture.Path, "deleted-source", "cached.txt");
        var timestamp = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var updates = new List<ScanProgress>();

        var result = await new FileListExporter().ExportRecordsAsync(
            [new FileRecord(cachedPath, fixture.Path, 42, timestamp, timestamp, FileAttributes.Archive)],
            output,
            ExportFormat.Csv,
            new InlineProgress<ScanProgress>(updates.Add),
            CancellationToken.None);

        var text = await File.ReadAllTextAsync(output);
        Assert.AreEqual(1, result.Files);
        StringAssert.Contains(text, cachedPath);
        Assert.IsTrue(updates[^1].IsComplete);
        Assert.AreEqual(1, updates[^1].Files);
    }

    [TestMethod]
    public async Task ExportRecordsAsync_preserves_destination_and_removes_temp_file_when_canceled()
    {
        using var fixture = new ExportFixture();
        var output = Path.Combine(fixture.Path, "inventory.csv");
        await File.WriteAllTextAsync(output, "existing valid export");
        using var cancellation = new CancellationTokenSource();
        var timestamp = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

        IEnumerable<FileRecord> CancelingRecords()
        {
            cancellation.Cancel();
            yield return new FileRecord("cached.txt", fixture.Path, 42, timestamp, timestamp, FileAttributes.Normal);
        }

        try
        {
            await new FileListExporter().ExportRecordsAsync(
                CancelingRecords(),
                output,
                ExportFormat.Csv,
                progress: null,
                cancellation.Token);
            Assert.Fail("The canceled export should not complete.");
        }
        catch (OperationCanceledException)
        {
        }

        Assert.AreEqual("existing valid export", await File.ReadAllTextAsync(output));
        Assert.IsEmpty(Directory.GetFiles(fixture.Path, ".diskusage-*.tmp"));
    }

    [TestMethod]
    public void CommandArguments_accepts_equals_and_separate_option_values()
    {
        var arguments = CommandArguments.Parse([
            "upload", ".", "--format=csv.br", "--bucket", "inventory", "--include-hidden"
        ]);

        Assert.AreEqual("upload", arguments.Command);
        Assert.AreEqual(".", arguments.Positionals.Single());
        Assert.AreEqual("csv.br", arguments.Get("format"));
        Assert.AreEqual("inventory", arguments.Get("bucket"));
        Assert.IsTrue(arguments.Has("include-hidden"));
    }

    [TestMethod]
    public void CommandArguments_does_not_consume_path_after_boolean_switch()
    {
        var arguments = CommandArguments.Parse(["scan", "--include-hidden", "C:\\data"]);

        Assert.IsTrue(arguments.Has("include-hidden"));
        Assert.AreEqual("C:\\data", arguments.Positionals.Single());
    }

    [TestMethod]
    public void ExportFormatParser_supports_shorthand_and_separate_compression()
    {
        Assert.AreEqual(ExportFormat.CsvBrotli, ExportFormatParser.Parse("csv.br"));
        Assert.AreEqual(ExportFormat.TsvGzip, ExportFormatParser.Parse("tsv", "gz"));
        Assert.AreEqual(ExportFormat.CsvZip, ExportFormatParser.Parse("csv", "zip"));
        Assert.AreEqual("tsv.gz", ExportFormat.TsvGzip.Extension());
    }

    [TestMethod]
    public void ExportFormatParser_adds_compound_extensions_exactly_once()
    {
        Assert.AreEqual("inventory.csv.br", ExportFormatParser.EnsureFileExtension("inventory", ExportFormat.CsvBrotli));
        Assert.AreEqual("inventory.csv.br", ExportFormatParser.EnsureFileExtension("inventory.csv.br", ExportFormat.CsvBrotli));
        Assert.AreEqual("inventory.csv.br", ExportFormatParser.EnsureFileExtension("inventory.csv.br.csv.br", ExportFormat.CsvBrotli));
        Assert.AreEqual("inventory.tsv.gz", ExportFormatParser.EnsureFileExtension("inventory.tsv.gz.tsv.gz", ExportFormat.TsvGzip));
        Assert.AreEqual("inventory.parquet", ExportFormatParser.EnsureFileExtension("inventory.parquet.parquet", ExportFormat.Parquet));
    }

    [TestMethod]
    public void ExportFormat_validates_codec_specific_levels()
    {
        ExportFormat.CsvBrotli.ValidateCompressionLevel(11);
        ExportFormat.TsvGzip.ValidateCompressionLevel(9);
        ExportFormat.Parquet.ValidateCompressionLevel(9);
        Assert.Throws<ArgumentOutOfRangeException>(() => ExportFormat.CsvBrotli.ValidateCompressionLevel(12));
        Assert.Throws<ArgumentOutOfRangeException>(() => ExportFormat.CsvZip.ValidateCompressionLevel(10));
        Assert.Throws<ArgumentOutOfRangeException>(() => ExportFormat.Parquet.ValidateCompressionLevel(10));
        Assert.Throws<ArgumentException>(() => ExportFormat.Csv.ValidateCompressionLevel(1));
    }

    [TestMethod]
    public void CommandArguments_reads_optional_compression_level()
    {
        var arguments = CommandArguments.Parse(["export", ".", "--format", "csv", "--compression", "gz", "--compression-level", "7"]);

        Assert.AreEqual("gz", arguments.Get("compression"));
        Assert.AreEqual(7, arguments.GetOptionalInt("compression-level"));
    }

    private static Task<ExportResult> Export(ExportFixture fixture, string output, ExportFormat format, int? compressionLevel = null) =>
        new FileListExporter().ExportAsync(
            fixture.SourcePath,
            output,
            format,
            new ScanOptions { IncludeHidden = true },
            progress: null,
            CancellationToken.None,
            compressionLevel);

    private sealed class ExportFixture : IDisposable
    {
        public ExportFixture()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"diskusage-export-tests-{Guid.NewGuid():N}");
            SourcePath = System.IO.Path.Combine(Path, "source");
            Directory.CreateDirectory(SourcePath);
            File.WriteAllText(System.IO.Path.Combine(SourcePath, "payload.txt"), "hello, parquet");
        }

        public string Path { get; }

        public string SourcePath { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
