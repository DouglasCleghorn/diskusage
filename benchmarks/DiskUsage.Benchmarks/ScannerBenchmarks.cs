using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using DiskUsage.Core;

namespace DiskUsage.Benchmarks;

[MemoryDiagnoser]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class ScannerBenchmarks
{
    private string _rootPath = null!;
    private bool _ownsFixture;

    [GlobalSetup]
    public void Setup()
    {
        var suppliedPath = Environment.GetEnvironmentVariable("DISKUSAGE_BENCHMARK_PATH");
        if (!string.IsNullOrWhiteSpace(suppliedPath))
        {
            _rootPath = Path.GetFullPath(suppliedPath);
            if (!Directory.Exists(_rootPath))
            {
                throw new DirectoryNotFoundException($"DISKUSAGE_BENCHMARK_PATH does not exist: {_rootPath}");
            }

            return;
        }

        _ownsFixture = true;
        _rootPath = Path.Combine(Path.GetTempPath(), $"diskusage-benchmark-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_rootPath);

        var payload = new byte[128];
        for (var directoryIndex = 0; directoryIndex < 32; directoryIndex++)
        {
            var directory = Path.Combine(_rootPath, $"group-{directoryIndex:D2}");
            var nested = Path.Combine(directory, "nested");
            Directory.CreateDirectory(nested);
            for (var fileIndex = 0; fileIndex < 128; fileIndex++)
            {
                var parent = fileIndex % 2 == 0 ? directory : nested;
                File.WriteAllBytes(Path.Combine(parent, $"item-{fileIndex:D3}.bin"), payload);
            }
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        if (_ownsFixture && Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Tree")]
    public async Task<long> LegacyTreeScan() => (await LegacyScanner.ScanAsync(_rootPath)).Bytes;

    [Benchmark]
    [BenchmarkCategory("Tree")]
    public async Task<long> OptimizedTreeScan() => (await new FileSystemScanner().ScanAsync(
        _rootPath,
        new ScanOptions
        {
            IncludeHidden = true,
            FollowLinks = false,
            CollectFiles = true,
            MaxDegreeOfParallelism = 1
        })).Bytes;

    [Benchmark]
    [BenchmarkCategory("Tree")]
    public async Task<long> ParallelTreeScan() => (await new FileSystemScanner().ScanAsync(
        _rootPath,
        new ScanOptions
        {
            IncludeHidden = true,
            FollowLinks = false,
            CollectFiles = true,
            MaxDegreeOfParallelism = 4
        })).Bytes;

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Inventory")]
    public async Task<long> LegacyInventoryScan()
    {
        long bytes = 0;
        await foreach (var file in LegacyScanner.EnumerateFilesAsync(_rootPath))
        {
            bytes += file.Size;
        }

        return bytes;
    }

    [Benchmark]
    [BenchmarkCategory("Inventory")]
    public async Task<long> OptimizedInventoryScan()
    {
        long bytes = 0;
        await foreach (var file in new FileSystemScanner().EnumerateFilesAsync(
                           _rootPath,
                           new ScanOptions
                           {
                               IncludeHidden = true,
                               FollowLinks = false,
                               CollectFiles = false,
                               MaxDegreeOfParallelism = 1
                           }))
        {
            bytes += file.Size;
        }

        return bytes;
    }

    [Benchmark]
    [BenchmarkCategory("Inventory")]
    public async Task<long> ParallelInventoryScan()
    {
        long bytes = 0;
        await foreach (var file in new FileSystemScanner().EnumerateFilesAsync(
                           _rootPath,
                           new ScanOptions
                           {
                               IncludeHidden = true,
                               FollowLinks = false,
                               CollectFiles = false,
                               MaxDegreeOfParallelism = 4
                           }))
        {
            bytes += file.Size;
        }

        return bytes;
    }
}
