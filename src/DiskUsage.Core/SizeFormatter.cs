using System.Globalization;

namespace DiskUsage.Core;

public static class SizeFormatter
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB", "EB"];

    public static string Format(long bytes)
    {
        var size = (double)Math.Abs(bytes);
        var unit = 0;

        while (size >= 1024 && unit < Units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        if (bytes < 0)
        {
            size = -size;
        }

        var format = unit == 0 ? "0" : size >= 100 ? "0" : size >= 10 ? "0.0" : "0.00";
        return $"{size.ToString(format, CultureInfo.InvariantCulture)} {Units[unit]}";
    }
}
