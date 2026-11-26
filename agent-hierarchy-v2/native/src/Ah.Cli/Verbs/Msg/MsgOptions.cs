using System.Globalization;
using Ah.Core.Json;

namespace Ah.Cli.Verbs.Msg;

/// <summary>Thrown to end the command with an exit code after its message was written, like a process exit.</summary>
internal sealed class MsgExit(int code) : Exception
{
    internal int Code { get; } = code;
}

/// <summary>
/// The command line of <c>msg</c>: every <c>--name</c> takes the next word as its value unless that word starts with
/// <c>--</c> or the name is a boolean flag, in which case it is <c>true</c>. Words that belong to no flag are positional.
/// </summary>
internal sealed class MsgOptions
{
    // json is a switch everywhere except `decision add` and `decision ui-answer`, where it carries the decision or answer object
    private static readonly HashSet<string> BoolFlags = new(StringComparer.Ordinal)
    {
        "plain", "json", "open", "closed", "all", "summary", "overdue", "line", "force", "request", "count", "hold", "waiting", "part", "review", "questions", "answered", "unanswered", "full",
    };

    private readonly Dictionary<string, string?> _values = new(StringComparer.Ordinal);

    private readonly Dictionary<string, List<string>> _repeated = new(StringComparer.Ordinal);

    private MsgOptions(List<string> positional) => Positional = positional;

    internal List<string> Positional { get; }

    internal IReadOnlyCollection<string> Keys => _values.Keys;

    internal static MsgOptions Parse(IReadOnlyList<string> argv)
    {
        var opts = new MsgOptions([]);
        for (var i = 0; i < argv.Count; i++)
        {
            var a = argv[i];
            if (!a.StartsWith("--", StringComparison.Ordinal))
            {
                opts.Positional.Add(a);
                continue;
            }

            var key = a[2..];
            var takesValue = !BoolFlags.Contains(key) || (key == "json" && opts.Positional is ["decision", "add" or "ui-answer", ..]);
            if (takesValue && i + 1 < argv.Count && !argv[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                opts._values[key] = argv[i + 1];
                opts._repeated.TryAdd(key, []);
                opts._repeated[key].Add(argv[i + 1]);
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

    /// <summary>Whether the flag was given as a bare switch (no value taken).</summary>
    internal bool IsBare(string key) => _values.TryGetValue(key, out var v) && v is null;

    /// <summary>The flag's value, or null when it is absent or bare.</summary>
    internal string? Text(string key) => _values.TryGetValue(key, out var v) ? v : null;

    /// <summary>Every value the flag was given, in order, for a flag that may repeat; empty when it is absent or bare.</summary>
    internal IReadOnlyList<string> Texts(string key) => _repeated.TryGetValue(key, out var all) ? all : [];

    /// <summary>Every flag that took a value, by name; bare switches are left out.</summary>
    internal IReadOnlyDictionary<string, string> TextValues() =>
        _values.Where(p => p.Value is not null).ToDictionary(p => p.Key, p => p.Value!, StringComparer.Ordinal);

    /// <summary>JavaScript's <c>Number(x)</c> of the flag's value, or <c>NaN</c> when it is absent, bare or not a number.</summary>
    internal static double ToNumber(string? text)
    {
        if (text is null)
        {
            return double.NaN;
        }

        var t = text.Trim();
        if (t.Length == 0)
        {
            return 0;
        }

        return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && !t.Contains(',', StringComparison.Ordinal)
            ? n
            : double.NaN;
    }
}

/// <summary>Writes a command's results the way the Node CLI does: one JSON line, or one text block with <c>--plain</c>.</summary>
internal sealed class MsgOutput(TextWriter stdout, TextWriter stderr)
{
    internal void Json(JsonValue value) => stdout.Write(JsonText.Stringify(value, 0) + "\n");

    internal void Text(string text) => stdout.Write(text + "\n");

    internal void Note(string line) => stderr.Write(line + "\n");

    /// <summary>A usage error: one line on stderr, then exit 2.</summary>
    internal MsgExit Fail(string message)
    {
        stderr.Write($"ah msg: {message}\n");
        return new MsgExit(2);
    }
}
