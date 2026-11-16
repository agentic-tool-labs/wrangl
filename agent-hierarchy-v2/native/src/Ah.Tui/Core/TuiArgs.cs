using Ah.Core.Json;

namespace Ah.Tui.Core;

/// <summary>What the command line asked for, or why it is refused.</summary>
internal sealed record TuiArgs(string? Team, bool Once, bool Plain, string? UsageError, bool Help)
{
    private static readonly HashSet<string> BoolFlags = new(StringComparer.Ordinal)
    {
        "plain", "pending", "acked", "all", "require", "once", "hook", "follow", "classic", "reduced-motion",
    };

    private static readonly HashSet<string> ValuedNeedingValue = new(StringComparer.Ordinal)
    {
        "cwd", "n", "address", "session", "dir", "harness", "path", "budget", "set", "theme", "team",
    };

    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal) { "team", "once", "cwd", "plain", "help" };

    /// <summary>The usage failure as the daemon's control CLI prints it: JSON on stdout, or a line on stderr with <c>--plain</c>; exit 2.</summary>
    internal string UsageText => Plain
        ? $"error: usage: {UsageError}\n"
        : JsonText.Stringify(new JsonObject { ["ok"] = false, ["error"] = new JsonObject { ["code"] = "usage", ["message"] = UsageError ?? string.Empty } }, 0) + "\n";

    /// <summary>
    /// Parses the arguments after <c>tui</c> exactly as <c>ahd-ctl</c> does: a flag that is not known gives <c>unknown flag</c>, a valued
    /// flag with no value gives <c>--x needs a value</c>, a second positional is <c>unexpected argument</c>, and a known flag the verb
    /// does not take is <c>tui does not take --x</c>.
    /// </summary>
    internal static TuiArgs Parse(IReadOnlyList<string> args)
    {
        var plain = args.Contains("--plain", StringComparer.Ordinal);
        TuiArgs Usage(string message) => new(null, false, plain, message, false);

        var keys = new List<string>();
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        string? firstPositional = null;
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            var key = a.StartsWith("--", StringComparison.Ordinal) ? a[2..] : a == "-n" ? "n" : null;
            if (key is null)
            {
                if (a.StartsWith('-') && a.Length > 1)
                {
                    return Usage($"unknown flag {a}");
                }

                firstPositional ??= a;
                continue;
            }

            if (BoolFlags.Contains(key))
            {
                Remember(key, "true");
                continue;
            }

            var next = i + 1 < args.Count ? args[i + 1] : null;
            if (next is null || next.StartsWith("--", StringComparison.Ordinal) || (next.StartsWith('-') && key != "n"))
            {
                Remember(key, null);
                if (key == "help")
                {
                    continue;
                }

                return ValuedNeedingValue.Contains(key) ? Usage($"--{key} needs a value") : Usage($"unknown flag {a}");
            }

            Remember(key, next);
            i++;
        }

        void Remember(string key, string? value)
        {
            if (!values.ContainsKey(key))
            {
                keys.Add(key);
            }

            values[key] = value;
        }

        if (values.ContainsKey("help") || args.Contains("--help", StringComparer.Ordinal))
        {
            return new TuiArgs(null, false, plain, null, Help: true);
        }

        if (firstPositional is not null)
        {
            return Usage($"unexpected argument {JsonText.Stringify(firstPositional, 0)}");
        }

        foreach (var k in keys)
        {
            if (!Allowed.Contains(k))
            {
                return Usage($"tui does not take --{k}");
            }
        }

        return new TuiArgs(values.GetValueOrDefault("team"), values.TryGetValue("once", out var o) && o == "true", plain, null, false);
    }
}
