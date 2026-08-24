using DiskUsage.Core;

namespace DiskUsage.Cli;

internal static class TerminalBrowser
{
    private sealed record Item(string Name, long Size, UsageNode? Directory, FileRecord? File)
    {
        public bool IsDirectory => Directory is not null;
    }

    public static int Run(ScanResult result)
    {
        var current = result.Root;
        var selected = 0;
        var offset = 0;
        try
        {
            while (true)
            {
                var items = GetItems(current);
                selected = Math.Clamp(selected, 0, Math.Max(0, items.Count - 1));
                Render(current, items, selected, ref offset, result);

                var key = Console.ReadKey(intercept: true).Key;
                switch (key)
                {
                    case ConsoleKey.Q:
                    case ConsoleKey.Escape:
                        return 0;
                    case ConsoleKey.UpArrow:
                    case ConsoleKey.K:
                        selected = Math.Max(0, selected - 1);
                        break;
                    case ConsoleKey.DownArrow:
                    case ConsoleKey.J:
                        selected = Math.Min(Math.Max(0, items.Count - 1), selected + 1);
                        break;
                    case ConsoleKey.PageUp:
                        selected = Math.Max(0, selected - PageSize());
                        break;
                    case ConsoleKey.PageDown:
                        selected = Math.Min(Math.Max(0, items.Count - 1), selected + PageSize());
                        break;
                    case ConsoleKey.Home:
                        selected = 0;
                        break;
                    case ConsoleKey.End:
                        selected = Math.Max(0, items.Count - 1);
                        break;
                    case ConsoleKey.Enter when items.Count > 0 && items[selected].Directory is not null:
                        current = items[selected].Directory!;
                        selected = 0;
                        offset = 0;
                        break;
                    case ConsoleKey.Backspace:
                    case ConsoleKey.LeftArrow when current.Parent is not null:
                        if (current.Parent is not null)
                        {
                            var old = current;
                            current = current.Parent;
                            var parentItems = GetItems(current);
                            selected = Math.Max(0, parentItems.FindIndex(item => ReferenceEquals(item.Directory, old)));
                            offset = 0;
                        }
                        break;
                }
            }
        }
        finally
        {
            Console.ResetColor();
            Console.Clear();
        }
    }

    private static List<Item> GetItems(UsageNode node) =>
        node.Children.Select(directory => new Item(directory.Name, directory.TotalSize, directory, null))
            .Concat(node.Files.Select(file => new Item(file.Name, file.Size, null, file)))
            .OrderByDescending(item => item.Size)
            .ToList();

    private static void Render(UsageNode current, IReadOnlyList<Item> items, int selected, ref int offset, ScanResult result)
    {
        var width = Math.Max(20, Console.WindowWidth);
        var pageSize = PageSize();
        if (selected < offset)
        {
            offset = selected;
        }
        else if (selected >= offset + pageSize)
        {
            offset = selected - pageSize + 1;
        }

        Console.SetCursorPosition(0, 0);
        WriteLine(" diskusage ", width, ConsoleColor.Black, ConsoleColor.Cyan);
        WriteLine($" {current.FullPath}", width, ConsoleColor.White, ConsoleColor.DarkGray);
        WriteLine($" {SizeFormatter.Format(current.TotalSize)}  {current.FileCount:N0} files  {current.DirectoryCount:N0} directories", width, ConsoleColor.Gray, ConsoleColor.Black);
        WriteLine("   Size       %   Name", width, ConsoleColor.DarkGray, ConsoleColor.Black);

        for (var row = 0; row < pageSize; row++)
        {
            var itemIndex = offset + row;
            if (itemIndex >= items.Count)
            {
                WriteLine(string.Empty, width, ConsoleColor.Gray, ConsoleColor.Black);
                continue;
            }

            var item = items[itemIndex];
            var percentage = current.TotalSize == 0 ? 0 : item.Size * 100d / current.TotalSize;
            var marker = itemIndex == selected ? ">" : " ";
            var suffix = item.IsDirectory ? "/" : string.Empty;
            var line = $"{marker} {SizeFormatter.Format(item.Size),9} {percentage,6:0.0}%  {item.Name}{suffix}";
            WriteLine(
                line,
                width,
                itemIndex == selected ? ConsoleColor.Black : item.IsDirectory ? ConsoleColor.Cyan : ConsoleColor.Gray,
                itemIndex == selected ? ConsoleColor.Gray : ConsoleColor.Black);
        }

        WriteLine($" ↑↓/jk move  Enter open  Backspace up  q quit   {result.Skipped:N0} skipped", width, ConsoleColor.Black, ConsoleColor.DarkCyan);
    }

    private static int PageSize() => Math.Max(3, Console.WindowHeight - 6);

    private static void WriteLine(string value, int width, ConsoleColor foreground, ConsoleColor background)
    {
        Console.ForegroundColor = foreground;
        Console.BackgroundColor = background;
        var content = value.Length >= width ? value[..(width - 1)] : value.PadRight(width - 1);
        Console.Write(content);
        Console.ResetColor();
        Console.WriteLine();
    }
}
