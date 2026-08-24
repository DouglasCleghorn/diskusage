namespace DiskUsage.Core;

public sealed record ScanOptions
{
    public static int DefaultMaxDegreeOfParallelism => Math.Min(4, Math.Max(1, Environment.ProcessorCount));

    public bool IncludeHidden { get; init; }

    public bool FollowLinks { get; init; }

    public bool CollectFiles { get; init; } = true;

    public int MaxDegreeOfParallelism { get; init; } = DefaultMaxDegreeOfParallelism;
}
