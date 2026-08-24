namespace DiskUsage.Core;

public sealed record ScanProgress(
    long Files,
    long Directories,
    long Bytes,
    long Skipped,
    string CurrentPath,
    TimeSpan Elapsed,
    UsageNode? Root = null,
    bool IsComplete = false);
