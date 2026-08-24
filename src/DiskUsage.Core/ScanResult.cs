namespace DiskUsage.Core;

public sealed record ScanResult(
    string RootPath,
    UsageNode Root,
    long Files,
    long Directories,
    long Bytes,
    long Skipped,
    TimeSpan Elapsed);
