using System.CommandLine;
using System.CommandLine.Help;
using DiskUsage.Core;

namespace DiskUsage.Cli;

internal sealed class CliCommandDefinition
{
    // An explicitly named root keeps help identical in the installed tool and test host.
    public Command Root { get; } = new("diskusage", "Find large files, analyze disk usage, and export file inventories.");

    public CliCommandDefinition()
    {
        Root.Options.Add(new HelpOption { Recursive = true });
        Root.Options.Add(new Option<bool>("--version") { Description = "Show the diskusage version.", Recursive = true, Arity = ArgumentArity.Zero });
        Root.SetAction(_ => 0); // Execution remains in CliApplication.

        var browse = AddCommand("browse", "Explore disk usage interactively; redirected input/output prints a plain summary.");
        var scan = AddCommand("scan", "Print a human-readable size tree and counts (not JSON).");
        var export = AddCommand("export", "Write CSV, TSV, or Parquet file records to a file or stdout.");
        var upload = AddCommand("upload", "Export file records and upload to Amazon S3 or MinIO.");
        foreach (var command in new[] { browse, scan, export, upload }) AddScanOptions(command);

        AddInteger(scan, "depth", "Report depth; 0 prints only root/summary. Does not limit scanning.", 0, 1);
        AddInteger(scan, "top", "Display limit per directory, separately for directories/files; 0 hides children.", 0, 50);
        AddFlag(scan, "include-files", "Include individual files in the report (default: off).");

        foreach (var command in new[] { export, upload })
        {
            var format = AddText(command, "format", "Parquet, CSV, TSV, or csv/tsv with .br, .gz, .zip; NOT inferred from output filename.", "FORMAT", "parquet");
            format.CompletionSources.Add("parquet", "csv", "tsv", "csv.br", "tsv.br", "csv.gz", "tsv.gz", "csv.zip", "tsv.zip");
            var compression = AddText(command, "compression", "CSV/TSV only: none, br, gz, zip. Must agree with --format; plain unless specified.", "TYPE");
            compression.CompletionSources.Add("none", "br", "gz", "zip");
            AddInteger(command, "compression-level", "Parquet: 1-3 => Zstd 1, 4-7 => 3, 8-9 => 19; default Optimal/Zstd 3. Brotli 0-11; gzip/ZIP 0-9.", 0);
            AddRepeated(command, "extensions", "Final extensions, comma-separated (txt,.csv,*.log); case-insensitive; repeat for OR; <none> matches no extension.", "LIST");
            AddRepeated(command, "size", "Quoted comparison such as \">=100MiB\"; <, <=, =, ==, !=, >=, >; repeat for AND.", "COMPARISON");
            AddText(command, "min-size", "Inclusive lower bound; bytes or KB/MB/GB/TB (decimal), KiB/MiB/GiB/TiB (binary).", "SIZE");
            AddText(command, "max-size", "Inclusive upper bound; units as for --min-size.", "SIZE");
            AddInteger(command, "top", "Globally largest N matching files, descending size; ties by ordinal path. Default: all matches.", 1);
        }

        AddText(export, "output", "Destination file, or - for stdout. Omitted: stdout if redirected, otherwise a generated filename.", "FILE|-");
        AddFlag(export, "stdout", "Export bytes only on stdout; summary on stderr. Cannot combine with --output FILE.");

        AddText(upload, "bucket", "Destination bucket (required).", "NAME").Required = true;
        AddText(upload, "endpoint", "S3-compatible endpoint URL; omit for Amazon S3.", "URL");
        AddText(upload, "key", "Object key; default: diskusage/<machine>-<UTC time>.<format>.", "KEY");
        AddText(upload, "region", "AWS region/signing region.", "REGION", "us-east-1");
        AddText(upload, "access-key", "Access key; supply together with --secret-key. Omit to use AWS credential chain.", "VALUE");
        AddText(upload, "secret-key", "Secret key; supply together with --access-key.", "VALUE");
        AddText(upload, "session-token", "Optional temporary credential token for explicit keys.", "VALUE");
        AddFlag(upload, "virtual-hosted-style", "Use virtual-hosted addressing (default: path-style).");
    }

    private Command AddCommand(string name, string description)
    {
        var command = new Command(name, description);
        Root.Subcommands.Add(command);
        return command;
    }

    private static void AddScanOptions(Command command)
    {
        command.Arguments.Add(new Argument<string>("path")
        {
            Description = "Directory to scan (default: current directory).",
            Arity = ArgumentArity.ZeroOrOne
        });
        AddFlag(command, "include-hidden", "Include hidden files/directories (default: excluded).");
        AddFlag(command, "follow-links", "Traverse linked directories, detecting cycles (default: off).");
        AddInteger(command, "threads", "Parallel directory workers; capped at 32. Use 1 for sequential scanning.", 1, ScanOptions.DefaultMaxDegreeOfParallelism);
    }

    private static void AddFlag(Command command, string name, string description)
    {
        var option = new Option<bool>("--" + name) { Description = description, Arity = ArgumentArity.Zero };
        RejectDuplicates(option);
        command.Options.Add(option);
    }

    private static Option<string> AddText(Command command, string name, string description, string helpName, string? defaultValue = null)
    {
        var option = new Option<string>("--" + name) { Description = description, HelpName = helpName, Arity = ArgumentArity.ExactlyOne };
        if (defaultValue is not null) option.DefaultValueFactory = _ => defaultValue;
        option.Validators.Add(result =>
        {
            if (result.Tokens.Any(token => string.IsNullOrWhiteSpace(token.Value))) result.AddError($"Missing value for --{name}.");
        });
        RejectDuplicates(option);
        command.Options.Add(option);
        return option;
    }

    private static void AddInteger(Command command, string name, string description, int minimum, int? defaultValue = null)
    {
        var option = new Option<int>("--" + name) { Description = description, HelpName = "N", Arity = ArgumentArity.ExactlyOne };
        if (defaultValue is { } value) option.DefaultValueFactory = _ => value;
        option.Validators.Add(result =>
        {
            // Leave conversion errors to the framework; reading a failed typed conversion throws.
            if (result.Tokens.Count == 1 && int.TryParse(result.Tokens[0].Value,
                System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed) && parsed < minimum)
                result.AddError($"--{name} must be a {(minimum == 0 ? "non-negative" : "positive")} integer.");
        });
        RejectDuplicates(option);
        command.Options.Add(option);
    }

    private static void AddRepeated(Command command, string name, string description, string helpName)
    {
        var option = new Option<string[]>("--" + name)
        {
            Description = description, HelpName = helpName,
            Arity = ArgumentArity.OneOrMore, AllowMultipleArgumentsPerToken = false
        };
        option.Validators.Add(result =>
        {
            // One value per occurrence preserves repeated filters without consuming the path.
            if (result.Tokens.Count != result.IdentifierTokenCount || result.Tokens.Any(token => string.IsNullOrWhiteSpace(token.Value)))
                result.AddError($"Each --{name} requires one value.");
        });
        command.Options.Add(option);
    }

    private static void RejectDuplicates(Option option) => option.Validators.Add(result =>
    {
        if (result.IdentifierTokenCount > 1) result.AddError($"Option {option.Name} was specified more than once.");
    });

    internal string[] Normalize(string[] args)
    {
        var normalized = args.ToArray();
        // Compatibility aliases only: System.CommandLine handles tokenization and validation.
        if (normalized.Length > 0 && normalized[0].Equals("help", StringComparison.OrdinalIgnoreCase))
        {
            if (normalized.Length > 2) throw new ArgumentException("Usage: diskusage help [command]");
            var topic = normalized.ElementAtOrDefault(1)?.ToLowerInvariant();
            if (topic is null or "help" or "--help" or "-h") return ["--help"];
            if (!Root.Subcommands.Any(command => command.Name == topic)) throw new ArgumentException($"Unknown help command '{topic}'.");
            return [topic, "--help"];
        }
        if (normalized.Length > 0 && Root.Subcommands.Any(command => command.Name.Equals(normalized[0], StringComparison.OrdinalIgnoreCase)))
            normalized[0] = normalized[0].ToLowerInvariant();

        var options = Root.Options.Concat(Root.Subcommands.SelectMany(command => command.Options)).ToArray();
        var knownOptions = options.SelectMany(option => option.Aliases.Prepend(option.Name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var flags = options.Where(option => option.ValueType == typeof(bool))
            .SelectMany(option => option.Aliases.Prepend(option.Name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < normalized.Length; index++)
        {
            var token = normalized[index];
            if (token == "--") break;
            if (!token.StartsWith("--", StringComparison.Ordinal)) continue;
            var separator = token.IndexOfAny(['=', ':']);
            var name = separator < 0 ? token : token[..separator];
            // Arity-zero booleans discard attached values in the framework; do not silently ignore them.
            if (separator >= 0 && flags.Contains(name)) throw new ArgumentException($"{name} does not accept a value.");
            if (knownOptions.Contains(name))
                normalized[index] = name.ToLowerInvariant() + (separator < 0 ? "" : token[separator..]);
        }
        // Route the historical diskusage [path] shorthand to the browse command.
        // Keeping the path off the root prevents accepting/ignoring an extra path before a verb.
        if (normalized.Length == 0 ||
            (!Root.Subcommands.Any(command => command.Name == normalized[0]) &&
             normalized[0] is not ("--help" or "-h" or "-?" or "/h" or "/?" or "--version")))
            return ["browse", .. normalized];
        return normalized;
    }
}
