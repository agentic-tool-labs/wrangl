namespace Ah.Cli.Verbs.Qa;

/// <summary>Thrown to end the command with an exit code after its message was written, like a process exit.</summary>
internal sealed class QaExit(int code) : Exception
{
    internal int Code { get; } = code;
}

/// <summary>
/// The command line of <c>qa</c>: every <c>--name</c> takes the next word as its value unless that word starts with <c>--</c> or
/// the name is a boolean flag, in which case it is <c>true</c>. Words that belong to no flag are positional.
/// </summary>
internal sealed class QaOptions
{
    private static readonly HashSet<string> BoolFlags = new(StringComparer.Ordinal) { "json", "all", "fresh", "help" };

    private readonly Dictionary<string, string?> _values = new(StringComparer.Ordinal);

    private QaOptions(List<string> positional) => Positional = positional;

    internal List<string> Positional { get; }

    internal IReadOnlyCollection<string> Keys => _values.Keys;

    internal static QaOptions Parse(IReadOnlyList<string> argv)
    {
        var opts = new QaOptions([]);
        for (var i = 0; i < argv.Count; i++)
        {
            var a = argv[i];
            if (!a.StartsWith("--", StringComparison.Ordinal))
            {
                opts.Positional.Add(a);
                continue;
            }

            var key = a[2..];
            if (!BoolFlags.Contains(key) && i + 1 < argv.Count && !argv[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                opts._values[key] = argv[i + 1];
                i++;
            }
            else
            {
                opts._values[key] = null;
            }
        }

        return opts;
    }

    /// <summary>Whether the flag was given at all, with or without a value.</summary>
    internal bool Has(string key) => _values.ContainsKey(key);

    /// <summary>The flag's value, or null when it is absent or bare.</summary>
    internal string? Text(string key) => _values.TryGetValue(key, out var v) ? v : null;
}

/// <summary>Writes a command's results and its one-line failures.</summary>
internal sealed class QaOutput(TextWriter stdout, TextWriter stderr)
{
    internal void Line(string text) => stdout.Write(text + "\n");

    /// <summary>A usage error: one line on stderr, then exit 2.</summary>
    internal QaExit Usage(string message)
    {
        stderr.Write($"ah qa: {message}\n");
        return new QaExit(2);
    }

    /// <summary>A refusal by the daemon, or no daemon: one line on stderr, then exit 1.</summary>
    internal QaExit Refused(string code, string message)
    {
        stderr.Write(message.Length == 0 ? $"error: {code}\n" : $"error: {code}: {message}\n");
        return new QaExit(1);
    }
}
