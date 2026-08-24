namespace DiskUsage.Cli;

internal sealed class CommandArguments
{
    private static readonly HashSet<string> BooleanOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "help", "include-files", "include-hidden", "follow-links", "virtual-hosted-style", "stdout"
    };

    private readonly Dictionary<string, string?> _options;

    private CommandArguments(string command, IReadOnlyList<string> positionals, Dictionary<string, string?> options)
    {
        Command = command;
        Positionals = positionals;
        _options = options;
    }

    public string Command { get; }

    public IReadOnlyList<string> Positionals { get; }

    public bool Has(string name) => _options.ContainsKey(name);

    public string? Get(string name) => _options.GetValueOrDefault(name);

    public string Require(string name) =>
        Get(name) ?? throw new ArgumentException($"Missing required option --{name}.");

    public int GetInt(string name, int defaultValue)
    {
        var value = Get(name);
        if (value is null)
        {
            return defaultValue;
        }

        return int.TryParse(value, out var parsed) && parsed >= 0
            ? parsed
            : throw new ArgumentException($"--{name} must be a non-negative integer.");
    }

    public int? GetOptionalInt(string name)
    {
        if (!Has(name))
        {
            return null;
        }

        var value = Get(name) ?? throw new ArgumentException($"Missing value for --{name}.");
        return int.TryParse(value, out var parsed) && parsed >= 0
            ? parsed
            : throw new ArgumentException($"--{name} must be a non-negative integer.");
    }

    public static CommandArguments Parse(string[] args)
    {
        var command = "browse";
        var positionals = new List<string>();
        var options = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var index = 0;

        if (args.Length > 0 && !args[0].StartsWith("-", StringComparison.Ordinal))
        {
            if (CliApplication.Commands.Contains(args[0]))
            {
                command = args[0].ToLowerInvariant();
                index++;
            }
            else
            {
                positionals.Add(args[0]);
                index++;
            }
        }

        while (index < args.Length)
        {
            var argument = args[index++];
            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                positionals.Add(argument);
                continue;
            }

            var option = argument[2..];
            var equals = option.IndexOf('=');
            if (equals >= 0)
            {
                options[option[..equals]] = option[(equals + 1)..];
                continue;
            }

            if (BooleanOptions.Contains(option))
            {
                options[option] = null;
                continue;
            }

            if (index < args.Length && !args[index].StartsWith("--", StringComparison.Ordinal))
            {
                options[option] = args[index++];
            }
            else
            {
                options[option] = null;
            }
        }

        return new CommandArguments(command, positionals, options);
    }
}
