using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Enumeration;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace DiskUsage.Core;

public sealed class FileSystemScanner
{
    private static readonly EnumerationOptions EnumerationOptions = new()
    {
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
        IgnoreInaccessible = false,
        AttributesToSkip = 0,
        BufferSize = 64 * 1024
    };

    private static readonly long ProgressIntervalTicks = Stopwatch.Frequency / 2;

    public async IAsyncEnumerable<FileRecord> EnumerateFilesAsync(
        string rootPath,
        ScanOptions? options = null,
        IProgress<ScanProgress>? progress = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var fullPath = ValidateRootPath(rootPath);
        options ??= new ScanOptions();
        if (options.MaxDegreeOfParallelism <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxDegreeOfParallelism must be at least 1.");
        }

        if (options.MaxDegreeOfParallelism > 1)
        {
            await foreach (var record in EnumerateFilesParallelAsync(
                               fullPath,
                               options,
                               progress,
                               cancellationToken).ConfigureAwait(false))
            {
                yield return record;
            }

            yield break;
        }

        var stopwatch = Stopwatch.StartNew();
        var pending = new Stack<string>();
        pending.Push(fullPath);
        var visitedDirectories = CreateVisitedDirectorySet(fullPath, options.FollowLinks);

        long files = 0;
        long directories = 1;
        long bytes = 0;
        long skipped = 0;
        var nextProgressAt = Stopwatch.GetTimestamp() + ProgressIntervalTicks;

        // The OS directory APIs are synchronous. Yield once so callers are never held while
        // the hot enumeration loop stays allocation-light.
        await Task.Yield();
        while (pending.TryPop(out var directoryPath))
        {
            cancellationToken.ThrowIfCancellationRequested();

            IEnumerator<ScanEntry>? enumerator = null;
            try
            {
                enumerator = CreateDirectoryEnumerable(directoryPath, options, collectMetadata: true).GetEnumerator();
                while (true)
                {
                    ScanEntry entry;
                    try
                    {
                        if (!enumerator.MoveNext())
                        {
                            break;
                        }

                        entry = enumerator.Current;
                    }
                    catch (Exception ex) when (IsFileSystemException(ex))
                    {
                        skipped++;
                        break;
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    if (entry.IsDirectory)
                    {
                        if (ShouldVisitDirectory(entry, options.FollowLinks, visitedDirectories, ref skipped))
                        {
                            pending.Push(entry.FullPath);
                            directories++;
                        }

                        continue;
                    }

                    var record = entry.ToFileRecord(fullPath);
                    files++;
                    bytes += record.Size;
                    yield return record;

                    var now = Stopwatch.GetTimestamp();
                    if (now >= nextProgressAt)
                    {
                        progress?.Report(new ScanProgress(files, directories, bytes, skipped, record.FullPath, stopwatch.Elapsed));
                        nextProgressAt = now + ProgressIntervalTicks;
                    }
                }
            }
            finally
            {
                enumerator?.Dispose();
            }
        }

        progress?.Report(new ScanProgress(files, directories, bytes, skipped, fullPath, stopwatch.Elapsed, IsComplete: true));
    }

    private static async IAsyncEnumerable<FileRecord> EnumerateFilesParallelAsync(
        string rootPath,
        ScanOptions options,
        IProgress<ScanProgress>? progress,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var directoryChannel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
        var fileChannel = Channel.CreateBounded<FileRecord>(new BoundedChannelOptions(4096)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
        ConcurrentDictionary<string, byte>? visitedDirectories = null;
        if (options.FollowLinks)
        {
            var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            visitedDirectories = new ConcurrentDictionary<string, byte>(comparer);
            visitedDirectories.TryAdd(GetDirectoryIdentity(rootPath, File.GetAttributes(rootPath)), 0);
        }

        long files = 0;
        long directories = 1;
        long bytes = 0;
        long skipped = 0;
        long outstandingDirectories = 1;
        var nextProgressAt = Stopwatch.GetTimestamp() + ProgressIntervalTicks;
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        directoryChannel.Writer.TryWrite(rootPath);

        async Task WorkerAsync()
        {
            try
            {
                await foreach (var directoryPath in directoryChannel.Reader.ReadAllAsync(linkedCancellation.Token).ConfigureAwait(false))
                {
                    try
                    {
                        foreach (var entry in CreateDirectoryEnumerable(directoryPath, options, collectMetadata: true))
                        {
                            linkedCancellation.Token.ThrowIfCancellationRequested();
                            if (entry.IsDirectory)
                            {
                                if (!ShouldVisitDirectoryParallel(entry, options.FollowLinks, visitedDirectories, ref skipped))
                                {
                                    continue;
                                }

                                Interlocked.Increment(ref directories);
                                Interlocked.Increment(ref outstandingDirectories);
                                if (!directoryChannel.Writer.TryWrite(entry.FullPath))
                                {
                                    Interlocked.Decrement(ref outstandingDirectories);
                                    throw new IOException("The scan work queue was closed unexpectedly.");
                                }
                            }
                            else
                            {
                                var record = entry.ToFileRecord(rootPath);
                                Interlocked.Increment(ref files);
                                Interlocked.Add(ref bytes, record.Size);
                                await fileChannel.Writer.WriteAsync(record, linkedCancellation.Token).ConfigureAwait(false);
                            }

                            TryReportProgress(entry.FullPath);
                        }
                    }
                    catch (Exception ex) when (IsFileSystemException(ex))
                    {
                        Interlocked.Increment(ref skipped);
                    }
                    finally
                    {
                        if (Interlocked.Decrement(ref outstandingDirectories) == 0)
                        {
                            directoryChannel.Writer.TryComplete();
                            fileChannel.Writer.TryComplete();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                directoryChannel.Writer.TryComplete(ex);
                fileChannel.Writer.TryComplete(ex);
                linkedCancellation.Cancel();
                throw;
            }
        }

        void TryReportProgress(string currentPath)
        {
            if (progress is null)
            {
                return;
            }

            var now = Stopwatch.GetTimestamp();
            var due = Volatile.Read(ref nextProgressAt);
            if (now < due || Interlocked.CompareExchange(ref nextProgressAt, now + ProgressIntervalTicks, due) != due)
            {
                return;
            }

            progress.Report(new ScanProgress(
                Interlocked.Read(ref files),
                Interlocked.Read(ref directories),
                Interlocked.Read(ref bytes),
                Interlocked.Read(ref skipped),
                currentPath,
                stopwatch.Elapsed));
        }

        var workerCount = Math.Min(options.MaxDegreeOfParallelism, 32);
        var workers = Enumerable.Range(0, workerCount)
            .Select(_ => Task.Run(WorkerAsync, CancellationToken.None))
            .ToArray();
        var completed = false;
        try
        {
            await foreach (var record in fileChannel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return record;
            }

            await Task.WhenAll(workers).ConfigureAwait(false);
            completed = true;
        }
        finally
        {
            if (!completed)
            {
                linkedCancellation.Cancel();
                directoryChannel.Writer.TryComplete();
                fileChannel.Writer.TryComplete();
                try
                {
                    await Task.WhenAll(workers).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // The consumer stopped enumerating before the producer completed.
                }
            }
        }

        progress?.Report(new ScanProgress(files, directories, bytes, skipped, rootPath, stopwatch.Elapsed, IsComplete: true));
    }

    public Task<ScanResult> ScanAsync(
        string rootPath,
        ScanOptions? options = null,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var fullPath = ValidateRootPath(rootPath);
        options ??= new ScanOptions();
        if (options.MaxDegreeOfParallelism <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxDegreeOfParallelism must be at least 1.");
        }

        if (options.MaxDegreeOfParallelism > 1)
        {
            return ScanParallelAsync(fullPath, options, progress, cancellationToken);
        }

        return Task.Run(
            () => Scan(fullPath, options, progress, cancellationToken),
            cancellationToken);
    }

    private static async Task<ScanResult> ScanParallelAsync(
        string rootPath,
        ScanOptions options,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var rootName = Path.GetFileName(rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var root = new UsageNode(string.IsNullOrWhiteSpace(rootName) ? rootPath : rootName, rootPath, null);
        var channel = Channel.CreateUnbounded<(string DirectoryPath, UsageNode Node)>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
        ConcurrentDictionary<string, byte>? visitedDirectories = null;
        if (options.FollowLinks)
        {
            var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            visitedDirectories = new ConcurrentDictionary<string, byte>(comparer);
            visitedDirectories.TryAdd(GetDirectoryIdentity(rootPath, File.GetAttributes(rootPath)), 0);
        }

        long files = 0;
        long directories = 1;
        long bytes = 0;
        long skipped = 0;
        long outstandingDirectories = 1;
        var nextProgressAt = Stopwatch.GetTimestamp() + ProgressIntervalTicks;

        progress?.Report(new ScanProgress(0, 1, 0, 0, rootPath, stopwatch.Elapsed, root));
        channel.Writer.TryWrite((rootPath, root));
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        async Task WorkerAsync()
        {
            try
            {
                await foreach (var work in channel.Reader.ReadAllAsync(linkedCancellation.Token).ConfigureAwait(false))
                {
                    List<FileRecord>? retainedFiles = options.CollectFiles ? [] : null;
                    long directBytes = 0;
                    long directFiles = 0;

                    try
                    {
                        foreach (var entry in CreateDirectoryEnumerable(work.DirectoryPath, options, options.CollectFiles))
                        {
                            linkedCancellation.Token.ThrowIfCancellationRequested();
                            if (entry.IsDirectory)
                            {
                                if (!ShouldVisitDirectoryParallel(entry, options.FollowLinks, visitedDirectories, ref skipped))
                                {
                                    continue;
                                }

                                var child = new UsageNode(Path.GetFileName(entry.FullPath), entry.FullPath, work.Node);
                                work.Node.AddChild(child);
                                Interlocked.Increment(ref directories);
                                Interlocked.Increment(ref outstandingDirectories);
                                if (!channel.Writer.TryWrite((entry.FullPath, child)))
                                {
                                    Interlocked.Decrement(ref outstandingDirectories);
                                    throw new IOException("The scan work queue was closed unexpectedly.");
                                }
                            }
                            else
                            {
                                var size = entry.Length;
                                if (options.CollectFiles)
                                {
                                    retainedFiles!.Add(entry.ToFileRecord(rootPath));
                                }

                                directFiles++;
                                directBytes += size;
                                Interlocked.Increment(ref files);
                                Interlocked.Add(ref bytes, size);
                            }

                            TryReportParallelProgress(entry.FullPath);
                        }
                    }
                    catch (Exception ex) when (IsFileSystemException(ex))
                    {
                        Interlocked.Increment(ref skipped);
                    }
                    finally
                    {
                        work.Node.AddFiles(retainedFiles, directBytes, directFiles);
                        if (Interlocked.Decrement(ref outstandingDirectories) == 0)
                        {
                            channel.Writer.TryComplete();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                channel.Writer.TryComplete(ex);
                linkedCancellation.Cancel();
                throw;
            }
        }

        void TryReportParallelProgress(string currentPath)
        {
            if (progress is null)
            {
                return;
            }

            var now = Stopwatch.GetTimestamp();
            var due = Volatile.Read(ref nextProgressAt);
            if (now < due || Interlocked.CompareExchange(ref nextProgressAt, now + ProgressIntervalTicks, due) != due)
            {
                return;
            }

            progress.Report(new ScanProgress(
                Interlocked.Read(ref files),
                Interlocked.Read(ref directories),
                Interlocked.Read(ref bytes),
                Interlocked.Read(ref skipped),
                currentPath,
                stopwatch.Elapsed,
                root));
        }

        var workerCount = Math.Min(options.MaxDegreeOfParallelism, 32);
        var workers = Enumerable.Range(0, workerCount)
            .Select(_ => Task.Run(WorkerAsync, CancellationToken.None))
            .ToArray();
        await Task.WhenAll(workers).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        progress?.Report(new ScanProgress(files, directories, bytes, skipped, rootPath, stopwatch.Elapsed, root, IsComplete: true));
        return new ScanResult(rootPath, root, files, directories, bytes, skipped, stopwatch.Elapsed);
    }

    private static ScanResult Scan(
        string rootPath,
        ScanOptions options,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var rootName = Path.GetFileName(rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var root = new UsageNode(string.IsNullOrWhiteSpace(rootName) ? rootPath : rootName, rootPath, null);
        var pending = new Stack<(string DirectoryPath, UsageNode Node)>();
        pending.Push((rootPath, root));
        var visitedDirectories = CreateVisitedDirectorySet(rootPath, options.FollowLinks);

        long files = 0;
        long directories = 1;
        long bytes = 0;
        long skipped = 0;
        var nextProgressAt = Stopwatch.GetTimestamp() + ProgressIntervalTicks;

        progress?.Report(new ScanProgress(0, 1, 0, 0, rootPath, stopwatch.Elapsed, root));

        while (pending.TryPop(out var work))
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<FileRecord>? retainedFiles = options.CollectFiles ? [] : null;
            long directBytes = 0;
            long directFiles = 0;

            try
            {
                foreach (var entry in CreateDirectoryEnumerable(work.DirectoryPath, options, options.CollectFiles))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (entry.IsDirectory)
                    {
                        if (!ShouldVisitDirectory(entry, options.FollowLinks, visitedDirectories, ref skipped))
                        {
                            continue;
                        }

                        var child = new UsageNode(Path.GetFileName(entry.FullPath), entry.FullPath, work.Node);
                        work.Node.AddChild(child);
                        pending.Push((entry.FullPath, child));
                        directories++;
                    }
                    else
                    {
                        var size = entry.Length;
                        if (options.CollectFiles)
                        {
                            retainedFiles!.Add(entry.ToFileRecord(rootPath));
                        }

                        directFiles++;
                        directBytes += size;
                        files++;
                        bytes += size;
                    }

                    var now = Stopwatch.GetTimestamp();
                    if (now >= nextProgressAt)
                    {
                        work.Node.AddFiles(retainedFiles, directBytes, directFiles);
                        retainedFiles?.Clear();
                        directBytes = 0;
                        directFiles = 0;
                        progress?.Report(new ScanProgress(files, directories, bytes, skipped, entry.FullPath, stopwatch.Elapsed, root));
                        nextProgressAt = now + ProgressIntervalTicks;
                    }
                }
            }
            catch (Exception ex) when (IsFileSystemException(ex))
            {
                skipped++;
            }
            finally
            {
                work.Node.AddFiles(retainedFiles, directBytes, directFiles);
            }
        }

        progress?.Report(new ScanProgress(files, directories, bytes, skipped, rootPath, stopwatch.Elapsed, root, IsComplete: true));
        return new ScanResult(rootPath, root, files, directories, bytes, skipped, stopwatch.Elapsed);
    }

    private static string ValidateRootPath(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        var fullPath = Path.GetFullPath(rootPath);
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"Directory not found: {fullPath}");
        }

        return fullPath;
    }

    private static FileSystemEnumerable<ScanEntry> CreateDirectoryEnumerable(
        string path,
        ScanOptions options,
        bool collectMetadata)
    {
        var enumerable = new FileSystemEnumerable<ScanEntry>(
            path,
            collectMetadata ? TransformEntry : TransformEntryWithoutMetadata,
            EnumerationOptions);
        enumerable.ShouldIncludePredicate = options switch
        {
            { IncludeHidden: true, FollowLinks: true } => IncludeAll,
            { IncludeHidden: true, FollowLinks: false } => IncludeWithoutLinks,
            { IncludeHidden: false, FollowLinks: true } => IncludeVisible,
            _ => IncludeVisibleWithoutLinks
        };
        return enumerable;
    }

    private static ScanEntry TransformEntry(ref FileSystemEntry entry)
    {
        var attributes = entry.Attributes;
        var isDirectory = entry.IsDirectory;
        return new ScanEntry(
            entry.ToFullPath(),
            isDirectory,
            isDirectory ? 0 : entry.Length,
            isDirectory ? default : entry.CreationTimeUtc.UtcDateTime,
            isDirectory ? default : entry.LastWriteTimeUtc.UtcDateTime,
            attributes);
    }

    private static ScanEntry TransformEntryWithoutMetadata(ref FileSystemEntry entry)
    {
        var attributes = entry.Attributes;
        var isDirectory = entry.IsDirectory;
        return new ScanEntry(entry.ToFullPath(), isDirectory, isDirectory ? 0 : entry.Length, default, default, attributes);
    }

    private static bool IncludeAll(ref FileSystemEntry entry) => true;

    private static bool IncludeWithoutLinks(ref FileSystemEntry entry) =>
        !entry.IsDirectory || !entry.Attributes.HasFlag(FileAttributes.ReparsePoint);

    private static bool IncludeVisible(ref FileSystemEntry entry) => !IsHidden(ref entry);

    private static bool IncludeVisibleWithoutLinks(ref FileSystemEntry entry) =>
        !IsHidden(ref entry) && (!entry.IsDirectory || !entry.Attributes.HasFlag(FileAttributes.ReparsePoint));

    private static bool IsHidden(ref FileSystemEntry entry) =>
        entry.IsHidden ||
        (!OperatingSystem.IsWindows() && entry.FileName is ['.', ..]);

    private static HashSet<string>? CreateVisitedDirectorySet(string rootPath, bool followLinks)
    {
        if (!followLinks)
        {
            return null;
        }

        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        return new HashSet<string>(comparer) { GetDirectoryIdentity(rootPath, File.GetAttributes(rootPath)) };
    }

    private static bool ShouldVisitDirectory(
        ScanEntry entry,
        bool followLinks,
        HashSet<string>? visitedDirectories,
        ref long skipped)
    {
        if (!followLinks)
        {
            return true;
        }

        try
        {
            return visitedDirectories!.Add(GetDirectoryIdentity(entry.FullPath, entry.Attributes));
        }
        catch (Exception ex) when (IsFileSystemException(ex))
        {
            skipped++;
            return false;
        }
    }

    private static bool ShouldVisitDirectoryParallel(
        ScanEntry entry,
        bool followLinks,
        ConcurrentDictionary<string, byte>? visitedDirectories,
        ref long skipped)
    {
        if (!followLinks)
        {
            return true;
        }

        try
        {
            return visitedDirectories!.TryAdd(GetDirectoryIdentity(entry.FullPath, entry.Attributes), 0);
        }
        catch (Exception ex) when (IsFileSystemException(ex))
        {
            Interlocked.Increment(ref skipped);
            return false;
        }
    }

    private static string GetDirectoryIdentity(string fullPath, FileAttributes attributes)
    {
        if (attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            return Path.GetFullPath(new DirectoryInfo(fullPath).ResolveLinkTarget(true)?.FullName ?? fullPath);
        }

        return fullPath;
    }

    private static bool IsFileSystemException(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or System.Security.SecurityException;

    private readonly record struct ScanEntry(
        string FullPath,
        bool IsDirectory,
        long Length,
        DateTime CreatedUtc,
        DateTime ModifiedUtc,
        FileAttributes Attributes)
    {
        public FileRecord ToFileRecord(string rootPath) =>
            new(FullPath, rootPath, Length, CreatedUtc, ModifiedUtc, Attributes);
    }
}
