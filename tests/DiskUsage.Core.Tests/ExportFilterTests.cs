using System.IO.Compression;
using DiskUsage.Cli;
using DiskUsage.Core;
using Parquet;

namespace DiskUsage.Core.Tests;

[TestClass]
public sealed class ExportFilterTests
{
    [TestMethod]
    [DataRow(1)]
    [DataRow(4)]
    public async Task Filters_preserve_directory_traversal_and_combine_before_top(int threads)
    {
        using var fixture = new Fixture();
        var args = CommandArguments.Parse(["export", "--extensions=txt", "--extensions", "*.CSV",
            "--size", ">=10", "--size=<=30", "--size", "!=20"]);
        var records = new List<FileRecord>();
        await foreach (var record in new FileSystemScanner().EnumerateFilesAsync(fixture.Source,
            new ScanOptions { IncludeHidden = true, MaxDegreeOfParallelism = threads, FileFilter = ExportFilterArguments.Parse(args) }))
            records.Add(record);
        CollectionAssert.AreEquivalent(new[] { "a.txt", "b.TXT", "deep.csv", "small.txt" }, records.Select(r => r.Name).ToArray());
    }

    [TestMethod]
    [DataRow("<10", 9, true)]
    [DataRow("<10", 10, false)]
    [DataRow("<=10", 10, true)]
    [DataRow(">10", 10, false)]
    [DataRow(">10", 11, true)]
    [DataRow(">=10", 10, true)]
    [DataRow("=10", 10, true)]
    [DataRow("==10", 9, false)]
    [DataRow("10", 10, true)]
    [DataRow("!=10", 10, false)]
    [DataRow("!=0", 1, true)]
    public void Size_comparisons_honor_boundaries(string comparison, int size, bool expected)
    {
        var filter = ExportFilterArguments.Parse(CommandArguments.Parse(["export", "--size", comparison]));
        Assert.AreEqual(expected, filter!.MatchesSize(size));
    }

    [TestMethod]
    [DataRow("1.5MiB", 1572864L)]
    [DataRow("1MB", 1000000L)]
    [DataRow(" 2 kib ", 2048L)]
    [DataRow("0", 0L)]
    [DataRow("9223372036854775807B", long.MaxValue)]
    public void Sizes_support_explicit_units_and_int64(string value, long expected) =>
        Assert.AreEqual(expected, ExportFilterArguments.ParseBytes(value));

    [TestMethod]
    [DataRow("-1")]
    [DataRow("NaN")]
    [DataRow("0.5B")]
    [DataRow("1XB")]
    [DataRow("1,5MB")]
    [DataRow("9223372036854775808")]
    [DataRow("999999999999999999999999999TB")]
    public void Invalid_sizes_are_rejected(string value) =>
        Assert.Throws<ArgumentException>(() => ExportFilterArguments.ParseBytes(value));

    [TestMethod]
    public void Extensionless_and_inclusive_bounds_work()
    {
        var filter = ExportFilterArguments.Parse(CommandArguments.Parse(["export", "--extensions", "<none>,.txt",
            "--min-size", "10", "--max-size", "20"]));
        Assert.IsTrue(filter!.MatchesExtension("README"));
        Assert.IsTrue(filter.MatchesExtension("A.TXT"));
        Assert.IsFalse(filter.MatchesExtension("A.txt.gz"));
        Assert.IsFalse(filter.MatchesSize(9));
        Assert.IsTrue(filter.MatchesSize(10));
        Assert.IsTrue(filter.MatchesSize(20));
        Assert.IsFalse(filter.MatchesSize(21));
    }

    [TestMethod]
    [DataRow("extensions")]
    [DataRow("size")]
    [DataRow("min-size")]
    [DataRow("max-size")]
    [DataRow("top")]
    public void Missing_filter_values_are_rejected(string option) =>
        Assert.Throws<ArgumentException>(() => CommandArguments.Parse(["export", "--" + option]));

    [TestMethod]
    public void Invalid_filters_do_not_silently_export_everything()
    {
        Assert.Throws<ArgumentException>(() => CommandArguments.Parse(["export", "--extensons", ".txt"]));
        Assert.Throws<ArgumentException>(() => ExportFilterArguments.ParseTop(CommandArguments.Parse(["export", "--top=0"])));
        Assert.Throws<ArgumentException>(() => ExportFilterArguments.Parse(CommandArguments.Parse(["export", "--extensions=txt,,csv"])));
        Assert.Throws<ArgumentException>(() => ExportFilterArguments.Parse(CommandArguments.Parse(["export", "--extensions=.tar.gz"])));
    }

    [TestMethod]
    [DataRow("csv", 1)]
    [DataRow("tsv", 4)]
    [DataRow("csv.br", 4)]
    [DataRow("csv.gz", 1)]
    [DataRow("csv.zip", 4)]
    [DataRow("parquet", 4)]
    public async Task Cli_exports_largest_matching_records_in_all_formats(string format, int threads)
    {
        using var fixture = new Fixture();
        var output = Path.Combine(fixture.Path, "export." + format);
        var exitCode = await CliApplication.RunAsync(["export", fixture.Source, "--extensions", "txt,csv",
            "--min-size", "10", "--size", "<=30", "--top", "3", "--threads", threads.ToString(),
            "--format", format, "--output", output], CancellationToken.None);
        Assert.AreEqual(0, exitCode);
        var expected = new[] { "a.txt", "b.TXT", "deep.csv" };
        if (format == "parquet")
        {
            await using var reader = await ParquetReader.CreateAsync(output);
            using var group = reader.OpenRowGroupReader(0);
            Assert.AreEqual(3L, group.RowCount);
            var paths = new string?[3];
            var sizes = new long[3];
            var fields = reader.Schema.GetDataFields();
            await group.ReadAsync(fields[0], paths.AsMemory());
            await group.ReadAsync<long>(fields[1], sizes.AsMemory());
            CollectionAssert.AreEqual(expected, paths.Select(Path.GetFileName).ToArray());
            CollectionAssert.AreEqual(new long[] { 30, 30, 25 }, sizes);
        }
        else
        {
            using var file = File.OpenRead(output);
            using var archive = format.EndsWith("zip") ? new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true) : null;
            using Stream decoded = format switch
            {
                "csv.br" => new BrotliStream(file, CompressionMode.Decompress),
                "csv.gz" => new GZipStream(file, CompressionMode.Decompress),
                "csv.zip" => archive!.Entries.Single().Open(),
                _ => file
            };
            using var reader = new StreamReader(decoded);
            var text = await reader.ReadToEndAsync();
            var delimiter = format == "tsv" ? '\t' : ',';
            var rows = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1).ToArray();
            CollectionAssert.AreEqual(expected, rows.Select(row => Path.GetFileName(row.Split(delimiter)[0])).ToArray());
        }
    }

    [TestMethod]
    [DataRow(1, 1)]
    [DataRow(4, 100)]
    public async Task Top_handles_one_and_fewer_matches_than_requested(int threads, int top)
    {
        using var fixture = new Fixture();
        using var output = new MemoryStream();
        var result = await new FileListExporter().ExportAsync(fixture.Source, output, "inventory.tsv", ExportFormat.Tsv,
            new ScanOptions { MaxDegreeOfParallelism = threads, FileFilter = new FileFilter(["csv"]) }, null,
            CancellationToken.None, top: top);
        Assert.AreEqual(1L, result.Files);
        Assert.AreEqual(25L, result.Bytes);
        Assert.IsTrue(output.CanWrite);
    }

    [TestMethod]
    [DataRow("csv")]
    [DataRow("parquet")]
    public async Task Empty_matches_produce_valid_empty_inventory(string format)
    {
        using var fixture = new Fixture();
        var output = Path.Combine(fixture.Path, "empty." + format);
        await CliApplication.RunAsync(["export", fixture.Source, "--size", ">1000", "--top", "5",
            "--format", format, "--output", output], CancellationToken.None);
        if (format == "parquet")
        {
            await using var reader = await ParquetReader.CreateAsync(output);
            Assert.AreEqual(0L, reader.Metadata!.NumRows);
        }
        else Assert.HasCount(1, await File.ReadAllLinesAsync(output));
    }

    [TestMethod]
    public async Task Canceled_top_selection_preserves_existing_destination()
    {
        using var fixture = new Fixture();
        var output = Path.Combine(fixture.Path, "existing.csv");
        await File.WriteAllTextAsync(output, "keep me");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => new FileListExporter().ExportAsync(
            fixture.Source, output, ExportFormat.Csv, new ScanOptions { FileFilter = new FileFilter(["absent"]) },
            null, cancellation.Token, top: 1));
        Assert.AreEqual("keep me", await File.ReadAllTextAsync(output));
        Assert.IsEmpty(Directory.GetFiles(fixture.Path, ".diskusage-*.tmp"));
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(4)]
    public async Task Output_inside_source_excludes_destination_and_temporary_export(int threads)
    {
        using var fixture = new Fixture();
        var output = Path.Combine(fixture.Source, "inventory.csv");
        await File.WriteAllBytesAsync(output, new byte[200]);
        var exporter = new FileListExporter();
        var options = new ScanOptions { IncludeHidden = true, MaxDegreeOfParallelism = threads };
        var result = await exporter.ExportAsync(fixture.Source, output, ExportFormat.Csv, options, null,
            CancellationToken.None, top: 1);
        Assert.AreEqual(100L, result.Bytes);
        StringAssert.Contains(await File.ReadAllTextAsync(output), "huge.bin");
        result = await exporter.ExportAsync(fixture.Source, output, ExportFormat.Csv, options, null, CancellationToken.None);
        Assert.AreEqual(9L, result.Files);
        Assert.HasCount(10, await File.ReadAllLinesAsync(output));
        Assert.IsEmpty(Directory.GetFiles(fixture.Source, ".diskusage-*.tmp"));
    }

    [TestMethod]
    public async Task Bounded_top_matches_full_sort_on_many_ties()
    {
        using var fixture = new Fixture();
        var random = new Random(1234);
        var expected = new List<(string Path, int Size)>();
        for (var i = 0; i < 200; i++)
        {
            var path = Path.Combine(fixture.Source, $"record-{i:D4}.dat");
            var size = random.Next(0, 16);
            await File.WriteAllBytesAsync(path, new byte[size]);
            expected.Add((path, size));
        }
        var output = Path.Combine(fixture.Path, "largest.tsv");
        await new FileListExporter().ExportAsync(fixture.Source, output, ExportFormat.Tsv,
            new ScanOptions { FileFilter = new FileFilter(["dat"]) }, null, CancellationToken.None, top: 17);
        var actual = (await File.ReadAllLinesAsync(output)).Skip(1).Select(line => line.Split('\t')[0]).ToArray();
        CollectionAssert.AreEqual(expected.OrderByDescending(row => row.Size).ThenBy(row => row.Path, StringComparer.Ordinal)
            .Take(17).Select(row => row.Path).ToArray(), actual);
    }

    [TestMethod]
    public async Task Filters_are_rejected_for_browse_and_validated_before_upload()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => CliApplication.RunAsync(
            ["browse", "--extensions", "txt"], CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => CliApplication.RunAsync(
            ["upload", "--size", "bogus", "--bucket", "unused"], CancellationToken.None));
    }

    private sealed class Fixture : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"diskusage-filters-{Guid.NewGuid():N}");
        public string Source => System.IO.Path.Combine(Path, "source");
        public Fixture()
        {
            Directory.CreateDirectory(System.IO.Path.Combine(Source, "nested.nonmatching"));
            foreach (var (name, size) in new[] { ("a.txt", 30), ("b.TXT", 30), ("small.txt", 10), ("middle.txt", 20),
                         ("zero.txt", 0), ("huge.bin", 100), ("README", 15), ("large.txt", 50),
                         (System.IO.Path.Combine("nested.nonmatching", "deep.csv"), 25) })
                File.WriteAllBytes(System.IO.Path.Combine(Source, name), new byte[size]);
        }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
