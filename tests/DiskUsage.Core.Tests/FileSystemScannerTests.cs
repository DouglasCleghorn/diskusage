using DiskUsage.Core;

namespace DiskUsage.Core.Tests;

[TestClass]
public sealed class FileSystemScannerTests
{
    [TestMethod]
    public async Task ScanAsync_builds_directory_totals_and_file_counts()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(directory.Path, "alpha", "beta"));
        await File.WriteAllBytesAsync(Path.Combine(directory.Path, "root.bin"), new byte[7]);
        await File.WriteAllBytesAsync(Path.Combine(directory.Path, "alpha", "child.bin"), new byte[11]);
        await File.WriteAllBytesAsync(Path.Combine(directory.Path, "alpha", "beta", "leaf.bin"), new byte[13]);

        var result = await new FileSystemScanner().ScanAsync(directory.Path);

        Assert.AreEqual(31, result.Bytes);
        Assert.AreEqual(3, result.Files);
        Assert.AreEqual(3, result.Directories);
        Assert.AreEqual(31, result.Root.TotalSize);
        Assert.AreEqual(3, result.Root.FileCount);
        Assert.AreEqual(2, result.Root.DirectoryCount);
        Assert.AreEqual(7, result.Root.DirectSize);

        var alpha = result.Root.Children.Single();
        Assert.AreEqual("alpha", alpha.Name);
        Assert.AreEqual(24, alpha.TotalSize);
        Assert.AreEqual(2, alpha.FileCount);
        Assert.AreEqual(1, alpha.DirectoryCount);
    }

    [TestMethod]
    public async Task EnumerateFilesAsync_streams_relative_paths_and_metadata()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(directory.Path, "nested"));
        var expectedPath = Path.Combine(directory.Path, "nested", "payload.txt");
        await File.WriteAllTextAsync(expectedPath, "hello");

        var records = new List<FileRecord>();
        await foreach (var record in new FileSystemScanner().EnumerateFilesAsync(directory.Path))
        {
            records.Add(record);
        }

        Assert.HasCount(1, records);
        Assert.AreEqual(Path.Combine("nested", "payload.txt"), records[0].RelativePath);
        Assert.AreEqual(5, records[0].Size);
        Assert.AreEqual(".txt", records[0].Extension);
    }

    [TestMethod]
    public async Task ScanAsync_publishes_live_root_before_final_result()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(directory.Path, "nested"));
        await File.WriteAllBytesAsync(Path.Combine(directory.Path, "nested", "payload.bin"), new byte[19]);
        var updates = new List<ScanProgress>();

        var result = await new FileSystemScanner().ScanAsync(
            directory.Path,
            progress: new InlineProgress<ScanProgress>(updates.Add));

        Assert.IsGreaterThanOrEqualTo(2, updates.Count);
        Assert.IsNotNull(updates[0].Root);
        Assert.IsFalse(updates[0].IsComplete);
        Assert.IsTrue(updates[^1].IsComplete);
        Assert.AreSame(updates[0].Root, updates[^1].Root);
        Assert.AreEqual(19, updates[^1].Root!.TotalSize);
        Assert.AreSame(result.Root, updates[^1].Root);
    }

    [TestMethod]
    public async Task ScanAsync_without_file_retention_preserves_totals()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(directory.Path, "nested"));
        await File.WriteAllBytesAsync(Path.Combine(directory.Path, "nested", "payload.bin"), new byte[23]);

        var result = await new FileSystemScanner().ScanAsync(
            directory.Path,
            new ScanOptions { CollectFiles = false, MaxDegreeOfParallelism = 4 });

        Assert.AreEqual(23, result.Bytes);
        Assert.AreEqual(1, result.Files);
        Assert.AreEqual(23, result.Root.TotalSize);
        Assert.IsEmpty(result.Root.Children.Single().Files);
    }

    [TestMethod]
    public async Task EnumerateFilesAsync_disposes_parallel_producers_when_consumer_stops_early()
    {
        using var directory = new TemporaryDirectory();
        for (var index = 0; index < 20; index++)
        {
            var child = Directory.CreateDirectory(Path.Combine(directory.Path, $"child-{index}"));
            await File.WriteAllBytesAsync(Path.Combine(child.FullName, "payload.bin"), new byte[index + 1]);
        }

        var observed = 0;
        await foreach (var _ in new FileSystemScanner().EnumerateFilesAsync(
                           directory.Path,
                           new ScanOptions { MaxDegreeOfParallelism = 4 }))
        {
            observed++;
            break;
        }

        Assert.AreEqual(1, observed);
    }

    [TestMethod]
    public void SizeFormatter_uses_compact_binary_units()
    {
        Assert.AreEqual("0 B", SizeFormatter.Format(0));
        Assert.AreEqual("1.00 KB", SizeFormatter.Format(1024));
        Assert.AreEqual("1.50 MB", SizeFormatter.Format(1572864));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"diskusage-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

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
