using BenchmarkDotNet.Running;

namespace DiskUsage.Benchmarks;

internal static class Program
{
    public static async Task Main(string[] args)
    {
        if (args is ["parquet-export", .. var benchmarkArgs])
        {
            await ParquetExportExperiments.RunAsync(benchmarkArgs, CancellationToken.None);
            return;
        }

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
    }
}
