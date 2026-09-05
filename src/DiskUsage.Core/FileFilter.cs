namespace DiskUsage.Core;

/// <summary>Immutable filters evaluated against directory entries before file metadata is materialized.</summary>
public sealed class FileFilter
{
    private readonly string[] _extensions;
    private readonly FileSizeCondition[] _sizes;

    public FileFilter(IEnumerable<string>? extensions = null, IEnumerable<FileSizeCondition>? sizes = null)
    {
        _extensions = (extensions ?? []).Select(NormalizeExtension)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        _sizes = (sizes ?? []).ToArray();
        foreach (var condition in _sizes)
        {
            if (condition.Bytes < 0 || !Enum.IsDefined(condition.Comparison))
                throw new ArgumentException("Size conditions require non-negative bytes and a valid comparison.", nameof(sizes));
        }
    }

    public bool HasSizeConditions => _sizes.Length > 0;

    public bool MatchesExtension(ReadOnlySpan<char> fileName)
    {
        if (_extensions.Length == 0) return true;
        var extension = Path.GetExtension(fileName);
        foreach (var candidate in _extensions)
        {
            if (extension.Equals(candidate, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    public bool MatchesSize(long bytes)
    {
        foreach (var condition in _sizes)
        {
            var matches = condition.Comparison switch
            {
                SizeComparison.LessThan => bytes < condition.Bytes,
                SizeComparison.LessThanOrEqual => bytes <= condition.Bytes,
                SizeComparison.Equal => bytes == condition.Bytes,
                SizeComparison.NotEqual => bytes != condition.Bytes,
                SizeComparison.GreaterThanOrEqual => bytes >= condition.Bytes,
                SizeComparison.GreaterThan => bytes > condition.Bytes,
                _ => false
            };
            if (!matches) return false;
        }
        return true;
    }

    private static string NormalizeExtension(string value)
    {
        var extension = value.Trim();
        if (extension.Equals("<none>", StringComparison.OrdinalIgnoreCase)) return string.Empty;
        if (extension.StartsWith("*.")) extension = extension[1..];
        if (extension.StartsWith('.')) extension = extension[1..];
        if (extension.Length == 0 || extension.IndexOfAny(['.', '/', '\\', '*', '?', ':']) >= 0 || extension.Any(char.IsWhiteSpace))
            throw new ArgumentException($"Invalid extension '{value}'. Use txt, .txt, *.txt, or <none>; only the final extension is matched.");
        return "." + extension;
    }
}

public enum SizeComparison { LessThan, LessThanOrEqual, Equal, NotEqual, GreaterThanOrEqual, GreaterThan }

public readonly record struct FileSizeCondition(SizeComparison Comparison, long Bytes);
