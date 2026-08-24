using System.Runtime.CompilerServices;
using DiskUsage.Core;

namespace DiskUsage.Benchmarks;

internal static class LegacyScanner
{
    public static Task<LegacyScanResult> ScanAsync(string rootPath) => Task.Run(() => Scan(rootPath));

    public static async IAsyncEnumerable<FileRecord> EnumerateFilesAsync(
        string rootPath,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(rootPath);
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(fullPath));
        var visited = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
        {
            fullPath
        };

        await Task.Yield();
        while (pending.TryPop(out var directory))
        {
            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attributes = entry.Attributes;
                if (entry is DirectoryInfo child)
                {
                    if (!attributes.HasFlag(FileAttributes.ReparsePoint) && visited.Add(child.FullName))
                    {
                        pending.Push(child);
                    }
                }
                else if (entry is FileInfo file)
                {
                    yield return new FileRecord(
                        file.FullName,
                        fullPath,
                        file.Length,
                        file.CreationTimeUtc,
                        file.LastWriteTimeUtc,
                        attributes);
                }
            }
        }
    }

    private static LegacyScanResult Scan(string rootPath)
    {
        var rootInfo = new DirectoryInfo(rootPath);
        var root = new LegacyNode(null);
        var pending = new Stack<(DirectoryInfo Directory, LegacyNode Node)>();
        pending.Push((rootInfo, root));
        var visited = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
        {
            rootInfo.FullName
        };
        long fileCount = 0;
        long directoryCount = 1;
        long bytes = 0;

        while (pending.TryPop(out var work))
        {
            foreach (var entry in work.Directory.EnumerateFileSystemInfos())
            {
                var attributes = entry.Attributes;
                if (entry is DirectoryInfo directory)
                {
                    if (attributes.HasFlag(FileAttributes.ReparsePoint) || !visited.Add(directory.FullName))
                    {
                        continue;
                    }

                    var child = new LegacyNode(work.Node);
                    work.Node.Children.Add(child);
                    pending.Push((directory, child));
                    directoryCount++;
                }
                else if (entry is FileInfo file)
                {
                    var record = new FileRecord(
                        file.FullName,
                        rootInfo.FullName,
                        file.Length,
                        file.CreationTimeUtc,
                        file.LastWriteTimeUtc,
                        attributes);
                    work.Node.Files.Add(record);
                    for (var node = work.Node; node is not null; node = node.Parent)
                    {
                        Interlocked.Add(ref node.TotalSize, record.Size);
                        Interlocked.Increment(ref node.FileCount);
                    }

                    fileCount++;
                    bytes += record.Size;
                }
            }
        }

        return new LegacyScanResult(root, fileCount, directoryCount, bytes);
    }

    internal sealed class LegacyNode(LegacyNode? parent)
    {
        public LegacyNode? Parent { get; } = parent;
        public List<LegacyNode> Children { get; } = [];
        public List<FileRecord> Files { get; } = [];
        public long TotalSize;
        public long FileCount;
    }
}

internal sealed record LegacyScanResult(
    LegacyScanner.LegacyNode Root,
    long Files,
    long Directories,
    long Bytes);
