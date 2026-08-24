namespace DiskUsage.Core;

public sealed record FileRecord(
    string FullPath,
    string RootPath,
    long Size,
    DateTime CreatedUtc,
    DateTime ModifiedUtc,
    FileAttributes Attributes)
{
    public string RelativePath => Path.GetRelativePath(RootPath, FullPath);

    public string Name => Path.GetFileName(FullPath);

    public string Extension => Path.GetExtension(FullPath);
}
