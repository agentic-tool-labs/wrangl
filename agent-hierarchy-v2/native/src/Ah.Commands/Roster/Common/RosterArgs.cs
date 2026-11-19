namespace Ah.Commands;

/// <summary>
/// The roster CLI's argument parser: every <c>--key</c> takes the next word as its value unless the key is a boolean flag
/// or the next word is itself a <c>--flag</c>, in which case it is true. Words that are not flags are positional.
/// A key given twice keeps the position of its first appearance and the value of its last.
/// </summary>
internal sealed class RosterArgs
{
    /// <summary>The flags that never take a value.</summary>
    internal static readonly IReadOnlySet<string> BoolFlags = new HashSet<string>(StringComparer.Ordinal)
    {
        "plain", "json", "plan", "commit", "partial", "manual", "next", "apply", "kill", "keep-sessions", "spawn", "dry-run",
        "new-tab", "new-workspace", "allow-global", "cancel", "clear", "close", "confirm", "also-config", "no-spawn",
        "allow-roster-edit", "no-legwork-handoff", "wait-only", "no-worktree", "yes", "fix", "all", "status",
        "checkpoint", "relaunch-missing", "revoke-all", "list", "user-choice", "hold", "brief", "failed", "here", "despite-load",
    };

    private readonly List<string> _keys = [];
    private readonly Dictionary<string, string?> _values = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _all = new(StringComparer.Ordinal);

    /// <summary>The words that are not flags, in order.</summary>
    internal List<string> Positionals { get; } = [];

    /// <summary>The flag names in the order they first appeared.</summary>
    internal IReadOnlyList<string> Keys => _keys;

    internal static RosterArgs Parse(IReadOnlyList<string> argv)
    {
        ArgumentNullException.ThrowIfNull(argv);
        var args = new RosterArgs();
        for (var i = 0; i < argv.Count; i++)
        {
            var word = argv[i];
            if (!word.StartsWith("--", StringComparison.Ordinal))
            {
                args.Positionals.Add(word);
                continue;
            }

            var key = word[2..];
            var next = i + 1 < argv.Count ? argv[i + 1] : null;
            if (!BoolFlags.Contains(key) && next is not null && !next.StartsWith("--", StringComparison.Ordinal))
            {
                args.Set(key, next);
                i++;
            }
            else
            {
                args.Set(key, null);
            }
        }

        return args;
    }

    /// <summary>Whether the flag was given at all, with or without a value.</summary>
    internal bool Has(string key) => _values.ContainsKey(key);

    /// <summary>The flag's value, or null when it was not given or was given without one.</summary>
    internal string? Text(string key) => _values.TryGetValue(key, out var v) ? v : null;

    /// <summary>Whether the flag was given with no value (a boolean <c>true</c>).</summary>
    internal bool IsTrue(string key) => _values.TryGetValue(key, out var v) && v is null;

    /// <summary>Every value the flag was given, in order (a repeatable flag such as <c>--ignore-release &lt;name&gt;</c>).</summary>
    internal IReadOnlyList<string> All(string key) => _all.TryGetValue(key, out var list) ? list : [];

    /// <summary>The flags that were given more than once, in the order they first repeated.</summary>
    internal IReadOnlyList<string> Repeated => _repeated;

    private readonly List<string> _repeated = [];

    private void Set(string key, string? value)
    {
        if (!_values.ContainsKey(key))
        {
            _keys.Add(key);
        }
        else if (!_repeated.Contains(key))
        {
            _repeated.Add(key);
        }

        _values[key] = value;
        if (value is not null)
        {
            if (!_all.TryGetValue(key, out var list))
            {
                _all[key] = list = [];
            }

            list.Add(value);
        }
    }
}
