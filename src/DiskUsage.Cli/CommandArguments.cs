namespace DiskUsage.Cli;

internal sealed class CommandArguments
{
    private static readonly HashSet<string> BooleanOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "help", "include-files", "include-hidden", "follow-links", "virtual-hosted-style", "stdout"
    };
    private static readonly HashSet<string> ValueOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "format", "compression", "compression-level", "output", "threads", "depth", "top",
        "endpoint", "bucket", "key", "region", "access-key", "secret-key", "session-token",
        "extensions", "size", "min-size", "max-size"
    };

    private readonly Dictionary<string, string?> _options;
    private readonly Dictionary<string, List<string>> _repeatedOptions;

    private CommandArguments(string command, IReadOnlyList<string> positionals, Dictionary<string, string?> options,
        Dictionary<string, List<string>> repeatedOptions)
    {
        Command = command;
        Positionals = positionals;
        _options = options;
        _repeatedOptions = repeatedOptions;
    }

    public string Command { get; }

    public IReadOnlyList<string> Positionals { get; }

    public bool Has(string name) => _options.ContainsKey(name);

    public string? Get(string name) => _options.GetValueOrDefault(name);

    public IReadOnlyList<string> GetValues(string name) =>
        _repeatedOptions.TryGetValue(name, out var values) ? values : [];

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
        var repeatedOptions = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var index = 0;

        void AddOption(string name, string? value)
        {
            if (!BooleanOptions.Contains(name) && !ValueOptions.Contains(name))
                throw new ArgumentException($"Unknown option --{name}. Run 'diskusage help'.");
            if (ValueOptions.Contains(name) && string.IsNullOrWhiteSpace(value))
                throw new ArgumentException($"Missing value for --{name}.");
            if (BooleanOptions.Contains(name) && value is not null)
                throw new ArgumentException($"--{name} does not accept a value.");
            if (name.Equals("extensions", StringComparison.OrdinalIgnoreCase) || name.Equals("size", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"Missing value for --{name}.");
                if (!repeatedOptions.TryGetValue(name, out var values)) repeatedOptions[name] = values = [];
                values.Add(value);
            }
            else if (options.ContainsKey(name))
            {
                throw new ArgumentException($"Option --{name} was specified more than once.");
            }
            options[name] = value;
        }

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
                AddOption(option[..equals], option[(equals + 1)..]);
                continue;
            }

            if (BooleanOptions.Contains(option))
            {
                AddOption(option, null);
                continue;
            }

            if (index < args.Length && !args[index].StartsWith("--", StringComparison.Ordinal))
            {
                AddOption(option, args[index++]);
            }
            else
            {
                AddOption(option, null);
            }
        }

        return new CommandArguments(command, positionals, options, repeatedOptions);
    }
}
