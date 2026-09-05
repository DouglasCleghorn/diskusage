using System.Globalization;
using System.Text.RegularExpressions;
using DiskUsage.Core;

namespace DiskUsage.Cli;

internal static partial class ExportFilterArguments
{
    public static FileFilter? Parse(CommandArguments arguments)
    {
        var extensions = arguments.GetValues("extensions").SelectMany(value => value.Split(',')).ToArray();
        var conditions = arguments.GetValues("size").Select(ParseComparison).ToList();
        if (arguments.Has("min-size"))
            conditions.Add(new(SizeComparison.GreaterThanOrEqual, ParseBytes(arguments.Require("min-size"))));
        if (arguments.Has("max-size"))
            conditions.Add(new(SizeComparison.LessThanOrEqual, ParseBytes(arguments.Require("max-size"))));
        return extensions.Length == 0 && conditions.Count == 0 ? null : new FileFilter(extensions, conditions);
    }

    public static int? ParseTop(CommandArguments arguments)
    {
        var top = arguments.GetOptionalInt("top");
        if (top is <= 0) throw new ArgumentException("--top must be a positive integer for export/upload.");
        return top;
    }

    private static FileSizeCondition ParseComparison(string text)
    {
        var value = text.Trim();
        foreach (var (symbol, comparison) in new (string, SizeComparison)[]
        {
            (">=", SizeComparison.GreaterThanOrEqual), ("<=", SizeComparison.LessThanOrEqual),
            ("!=", SizeComparison.NotEqual), ("==", SizeComparison.Equal),
            (">", SizeComparison.GreaterThan), ("<", SizeComparison.LessThan), ("=", SizeComparison.Equal)
        })
        {
            if (value.StartsWith(symbol, StringComparison.Ordinal))
                return new(comparison, ParseBytes(value[symbol.Length..]));
        }
        return new(SizeComparison.Equal, ParseBytes(value));
    }

    internal static long ParseBytes(string text)
    {
        var match = SizePattern().Match(text.Trim());
        if (!match.Success || !decimal.TryParse(match.Groups[1].Value, NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var number))
            throw InvalidSize(text);
        var multiplier = match.Groups[2].Value.ToUpperInvariant() switch
        {
            "" or "B" => 1m,
            "KB" => 1_000m, "MB" => 1_000_000m, "GB" => 1_000_000_000m, "TB" => 1_000_000_000_000m,
            "KIB" => 1_024m, "MIB" => 1_048_576m, "GIB" => 1_073_741_824m, "TIB" => 1_099_511_627_776m,
            _ => throw InvalidSize(text)
        };
        if (number > long.MaxValue / multiplier) throw InvalidSize(text);
        var bytes = number * multiplier;
        if (decimal.Truncate(bytes) != bytes) throw InvalidSize(text);
        return (long)bytes;
    }

    private static ArgumentException InvalidSize(string value) => new(
        $"Invalid file size '{value}'. Use non-negative whole bytes or B/KB/MB/GB/TB (decimal), KiB/MiB/GiB/TiB (binary), within Int64 range. Quote comparisons, e.g. --size \">=100MiB\".");

    [GeneratedRegex(@"^([0-9]+(?:\.[0-9]+)?)\s*([A-Za-z]*)$", RegexOptions.CultureInvariant)]
    private static partial Regex SizePattern();
}
