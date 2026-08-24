namespace DiskUsage.Core;

public sealed class UsageNode
{
    private readonly object _entriesLock = new();
    private readonly List<UsageNode> _children = [];
    private readonly List<FileRecord> _files = [];
    private long _directSize;
    private long _totalSize;
    private long _directFileCount;
    private long _fileCount;
    private long _directoryCount;

    internal UsageNode(string name, string fullPath, UsageNode? parent)
    {
        Name = name;
        FullPath = fullPath;
        Parent = parent;
    }

    public string Name { get; }

    public string FullPath { get; }

    public UsageNode? Parent { get; }

    public IReadOnlyList<UsageNode> Children
    {
        get
        {
            lock (_entriesLock)
            {
                return _children.ToArray();
            }
        }
    }

    public IReadOnlyList<FileRecord> Files
    {
        get
        {
            lock (_entriesLock)
            {
                return _files.ToArray();
            }
        }
    }

    public long DirectSize => Interlocked.Read(ref _directSize);

    public long TotalSize => Interlocked.Read(ref _totalSize);

    public long DirectFileCount => Interlocked.Read(ref _directFileCount);

    public long FileCount => Interlocked.Read(ref _fileCount);

    public long DirectoryCount => Interlocked.Read(ref _directoryCount);

    internal void AddChild(UsageNode child)
    {
        lock (_entriesLock)
        {
            _children.Add(child);
        }

        for (var node = this; node is not null; node = node.Parent)
        {
            Interlocked.Increment(ref node._directoryCount);
        }
    }

    internal void AddFiles(IReadOnlyCollection<FileRecord>? files, long size, long count)
    {
        if (files is { Count: > 0 })
        {
            lock (_entriesLock)
            {
                _files.AddRange(files);
            }
        }

        if (count == 0)
        {
            return;
        }

        Interlocked.Add(ref _directSize, size);
        Interlocked.Add(ref _directFileCount, count);

        for (var node = this; node is not null; node = node.Parent)
        {
            Interlocked.Add(ref node._totalSize, size);
            Interlocked.Add(ref node._fileCount, count);
        }
    }
}
