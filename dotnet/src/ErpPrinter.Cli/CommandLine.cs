namespace ErpPrinter.Cli;

/// <summary>
/// The parsed command line: <c>erpprinter &lt;command&gt; [--option value] [--flag] [Name=Value ...]</c>.
/// <c>Name=Value</c> pairs exist because msiexec hands installer properties over that way (an empty value arrives as <c>Name=</c>).
/// </summary>
public sealed class CommandLine
{
    private readonly Dictionary<string, string?> _options = new(StringComparer.OrdinalIgnoreCase);

    private CommandLine(string command, IReadOnlyDictionary<string, string> pairs, IReadOnlyList<string> positional)
    {
        Command = command;
        Pairs = pairs;
        Positional = positional;
    }

    public string Command { get; }
    public IReadOnlyDictionary<string, string> Pairs { get; }
    public IReadOnlyList<string> Positional { get; }

    public static CommandLine Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0) return new CommandLine("help", new Dictionary<string, string>(), []);
        var pairs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var positional = new List<string>();
        var parsed = new CommandLine(args[0].ToLowerInvariant(), pairs, positional);
        for (var i = 1; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                var name = arg[2..];
                var eq = name.IndexOf('=');
                if (eq >= 0) parsed._options[name[..eq]] = name[(eq + 1)..];
                else if (i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal) && !IsPair(args[i + 1])) parsed._options[name] = args[++i];
                else parsed._options[name] = null;
            }
            else if (IsPair(arg))
            {
                var eq = arg.IndexOf('=');
                pairs[arg[..eq]] = arg[(eq + 1)..];
            }
            else
            {
                positional.Add(arg);
            }
        }
        return parsed;
    }

    private static bool IsPair(string arg)
    {
        var eq = arg.IndexOf('=');
        return eq > 0 && arg[..eq].All(char.IsLetter);
    }

    /// <summary>True when <c>--name</c> was given with or without a value.</summary>
    public bool Has(string name) => _options.ContainsKey(name);

    /// <summary>The value of <c>--name</c>, or null when absent or given as a bare flag.</summary>
    public string? Get(string name) => _options.TryGetValue(name, out var value) ? value : null;

    public int GetInt(string name, int fallback) => int.TryParse(Get(name), out var n) ? n : fallback;

    /// <summary>The profile named by <c>--profile</c>, or the first positional argument.</summary>
    public string? Profile => Get("profile") ?? Positional.FirstOrDefault();
}
