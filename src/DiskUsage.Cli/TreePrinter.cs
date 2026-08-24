using DiskUsage.Core;

namespace DiskUsage.Cli;

internal static class TreePrinter
{
    public static void Print(UsageNode root, int maxDepth, int maxItems, bool includeFiles)
    {
        Console.WriteLine($"{SizeFormatter.Format(root.TotalSize),10}  {root.FullPath}");
        PrintChildren(root, 0, maxDepth, maxItems, includeFiles);
    }

    private static void PrintChildren(UsageNode node, int depth, int maxDepth, int maxItems, bool includeFiles)
    {
        if (depth >= maxDepth)
        {
            return;
        }

        var directories = node.Children
            .OrderByDescending(child => child.TotalSize)
            .Take(maxItems)
            .ToArray();

        foreach (var child in directories)
        {
            Console.WriteLine($"{new string(' ', (depth + 1) * 2)}{SizeFormatter.Format(child.TotalSize),10}  {child.Name}/");
            PrintChildren(child, depth + 1, maxDepth, maxItems, includeFiles);
        }

        if (!includeFiles)
        {
            return;
        }

        foreach (var file in node.Files.OrderByDescending(file => file.Size).Take(maxItems))
        {
            Console.WriteLine($"{new string(' ', (depth + 1) * 2)}{SizeFormatter.Format(file.Size),10}  {file.Name}");
        }
    }
}
