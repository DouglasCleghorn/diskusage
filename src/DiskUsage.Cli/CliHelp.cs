using System.CommandLine;
using System.CommandLine.Help;

namespace DiskUsage.Cli;

internal static class CliHelp
{
    public static string? GetRequestedText(string[] args, CommandArguments parsed) =>
        parsed.Has("help") ? Render(parsed.Symbol) : null;

    public static string GetText(string? command)
    {
        var definition = new CliCommandDefinition();
        var symbol = command is null || command.Equals("help", StringComparison.OrdinalIgnoreCase)
            ? definition.Root
            : definition.Root.Subcommands.FirstOrDefault(value => value.Name.Equals(command, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"Unknown help command '{command}'.");
        return Render(symbol);
    }

    private static string Render(Command command)
    {
        using var writer = new StringWriter();
        // Option descriptions, required values, defaults and syntax come from the parser model.
        var help = command.Parse(["--help"]);
        if (help.Action is HelpAction action) action.MaxWidth = 110;
        help.Invoke(new InvocationConfiguration { Output = writer, Error = writer });
        writer.WriteLine();
        writer.WriteLine(command.Name switch
        {
            "browse" => Browse,
            "scan" => Scan,
            "export" => Export,
            "upload" => Upload,
            _ => Overview
        });
        if (command.Name is "export" or "upload")
        {
            writer.WriteLine();
            writer.WriteLine(ExportNotes);
        }
        writer.WriteLine();
        writer.WriteLine(ExitCodes);
        writer.WriteLine();
        writer.WriteLine("Automation reference: https://github.com/DouglasCleghorn/diskusage/blob/master/AUTOMATION.md");
        return writer.ToString();
    }

    private const string Overview = """
        diskusage [path] is shorthand for diskusage browse [path].
        The default command is browse. For automation use scan or export;
        browse is interactive unless input or output is redirected.
        Command help: diskusage export --help (or diskusage help export).

        Examples:
          diskusage scan . --depth 2 --top 10
          diskusage export . --top 20 --format csv --stdout
          diskusage upload . --format csv.gz --bucket inventories --key inventory.csv.gz
        """;

    private const string Browse = """
        Scan, then open the interactive browser. Arrow keys or j/k move;
        Enter opens a directory, Backspace goes up, and q quits.
        Redirected stdin/stdout prints a plain tree and summary instead.
        Progress is on stderr. Use export for machine-readable file records.

        Example: diskusage browse .
        """;

    private const string Scan = """
        Depth/top limit the report, not the scan. Output is human-readable, not JSON.
        The size tree and final counts (including skipped entries) go to stdout;
        progress goes to stderr. Use export --top for globally largest file records.

        Example: diskusage scan . --depth 2 --top 10
        """;

    private const string Export = """
        When output is omitted: redirected stdout receives the export; otherwise a
        diskusage-yyyyMMdd-HHmmss.<format> file is created in the current directory.
        File exports use atomic replacement and print their completion summary on stdout.
        --stdout cannot be combined with --output FILE (except --output -).
        CSV/TSV stream; Parquet stdout is staged in a temporary file before copying.
        Keep shell-redirected destinations outside the scanned directory. Interrupted
        pipelines can leave partial files; atomic replacement applies to --output FILE.

        Example: diskusage export . --top 20 --format csv --stdout
        """;

    private const string Upload = """
        Credentials can come from AWS environment variables, profiles, or workload roles.
        Prepares a local temporary export, then sends one S3 PUT; no multipart upload.
        Uploading to an existing key can replace the object. Live S3/MinIO integration
        has not yet been validated. Progress goes to stderr; completion goes to stdout.

        Example: diskusage upload . --format csv.gz --bucket inventories --key inventory.csv.gz
        """;

    private const string ExportNotes = """
        Filters combine as AND (extension alternatives use OR). Size units default to
        bytes; KB/MB/GB/TB are decimal and KiB/MiB/GiB/TiB are binary.
        Extension and size filters apply during enumeration. Directories are still
        traversed. Top-N scans all candidates, retains only N, and outputs descending
        size. Without top-N, enumeration order is unspecified.

        Parquet uses 250,000-row groups and byte-stream-split encoding for file sizes.
        Avoid Parquet compression level 0: it does not reliably mean uncompressed.
        ZIP contains one CSV/TSV entry. Plain CSV/TSV does not accept a compression level.

        Output columns (in order):
          full_path              Absolute file path (string)
          size_bytes             File size in bytes (64-bit integer)
          created_utc            UTC creation timestamp
          modified_utc           UTC last-write timestamp

        CSV/TSV: UTF-8 with header; quoted fields can contain delimiters/newlines.
        Timestamps use ISO 8601 round-trip format. Use a CSV/TSV parser, not line splitting.
        Empty results are valid. Inaccessible entries are skipped; export/upload final
        summaries do not currently include skipped counts. Exit 0 can mean a partial scan.
        """;

    private const string ExitCodes = """
        Exit codes:
          0  Completed (including help or no matches; skipped entries may be omitted)
          1  Invalid arguments or operation failed; error text on stderr
          130  Canceled via Ctrl+C
        """;
}
