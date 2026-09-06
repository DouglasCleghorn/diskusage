using System.CommandLine;
using System.CommandLine.Parsing;

namespace DiskUsage.Cli;

// Keeps filesystem/export code independent of the command-line framework.
internal sealed class CommandArguments
{
    private readonly ParseResult _result;
    private readonly Dictionary<string, Option> _options;

    private CommandArguments(ParseResult result, CliCommandDefinition definition)
    {
        _result = result;
        Symbol = result.CommandResult.Command;
        Command = Symbol == definition.Root ? "browse" : Symbol.Name;
        _options = Symbol.Options.Concat(definition.Root.Options.Where(option => option.Recursive))
            .Distinct().ToDictionary(option => option.Name[2..], StringComparer.OrdinalIgnoreCase);
        var path = Symbol.Arguments.Count == 0 ? null : result.GetValue<string>("path");
        Positionals = path is null ? [] : [path];
    }

    internal Command Symbol { get; }
    public string Command { get; }
    public IReadOnlyList<string> Positionals { get; }

    public bool Has(string name) => Find(name) is { Implicit: false } result &&
        (result.Option is not Option<bool> || result.GetValueOrDefault<bool>());

    public string? Get(string name) => Find(name)?.GetValueOrDefault<string>();
    public IReadOnlyList<string> GetValues(string name) => Find(name)?.GetValueOrDefault<string[]>() ?? [];
    public string Require(string name) => Get(name) ?? throw new ArgumentException($"Missing required option --{name}.");
    public int GetInt(string name, int defaultValue) => Find(name)?.GetValueOrDefault<int>() ?? defaultValue;
    public int? GetOptionalInt(string name) => Has(name) ? Find(name)!.GetValueOrDefault<int>() : null;

    private OptionResult? Find(string name) => _options.TryGetValue(name, out var option) ? _result.GetResult(option) : null;

    public static CommandArguments Parse(string[] args)
    {
        var definition = new CliCommandDefinition();
        var result = definition.Root.Parse(definition.Normalize(args), new ParserConfiguration
        {
            // Literal @-prefixed filesystem paths must not become response files.
            ResponseFileTokenReplacer = null
        });
        var parsed = new CommandArguments(result, definition);
        if (result.Errors.Count > 0 && !parsed.Has("help") && !parsed.Has("version"))
            throw new ArgumentException(string.Join(Environment.NewLine, result.Errors.Select(error => error.Message)));
        // System.CommandLine permits unknown option-like tokens as positional values.
        // Require -- for such paths so misspelled filters never become a scan target.
        if (!parsed.Has("help") && !parsed.Has("version") && parsed.Positionals.FirstOrDefault() is { } path &&
            path.StartsWith('-') && !result.Tokens.SkipWhile(token => token.Type != TokenType.DoubleDash)
                .Any(token => token.Type == TokenType.Argument && token.Value == path))
            throw new ArgumentException($"Unknown option '{path}'. Use -- before a path beginning with '-'.");
        return parsed;
    }
}
