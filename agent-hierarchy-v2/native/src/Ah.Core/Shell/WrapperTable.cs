namespace Ah.Core.Shell;

/// <summary>Where a command starts once a wrapper word and its options are stepped over.</summary>
/// <param name="Kind">The wrapper's name: its program word, or <c>bucket</c> for the test bucket in any spelling.</param>
/// <param name="Next">The index of the first word after the wrapper and its options; the index after the last word when the wrapper runs nothing.</param>
/// <param name="PassesThrough">Whether the wrapper hands the next word on as given (it only changes the environment or the privilege), so text that is data to that word stays data.</param>
/// <param name="Reliable">False when an option is not in the wrapper's grammar, or the test bucket's required <c>--</c> is missing: the command cannot be located, so a caller judges every later word as a start.</param>
public sealed record WrapperStep(string Kind, int Next, bool Reliable, bool PassesThrough);

/// <summary>
/// The one table of the words that run another command (a wrapper) and the options each takes before it, and the stepper over them.
/// Every parser that must find the command a shell line runs steps over wrappers here, so a wrapper cannot hide a command from one
/// parser and not another.
/// </summary>
internal static class WrapperTable
{
    /// <summary>The kind the test bucket's three spellings (<c>with-slot.sh</c>, <c>ah-test-run</c>, <c>ah test run</c>) share.</summary>
    internal const string Bucket = "bucket";

    private sealed record Row(string[] Flags, string[] Valued, bool DashDash = false, bool Duration = false, bool NiceLevel = false, bool StopsAtV = false, bool PassesThrough = false, string[]? Script = null);

    private static readonly Row AhTestRun = new([], ["--eta", "--tokens", "--label"], DashDash: true);

    private static readonly Dictionary<string, Row> Rows = new(StringComparer.Ordinal)
    {
        ["env"] = new(["-i", "--ignore-environment", "-0", "--null", "-v", "--debug"], ["-u", "--unset", "-C", "--chdir", "-S", "--split-string", "-P"], DashDash: true, PassesThrough: true, Script: ["-S", "--split-string"]),
        ["timeout"] = new(["--foreground", "--preserve-status", "-v", "--verbose"], ["-s", "--signal", "-k", "--kill-after"], DashDash: true, Duration: true),
        ["time"] = new(["-p", "-l", "-v", "-h"], []),
        ["nice"] = new([], ["-n", "--adjustment"], DashDash: true, NiceLevel: true),
        ["nohup"] = new([], [], PassesThrough: true),
        ["builtin"] = new([], []),
        ["command"] = new(["-p"], [], StopsAtV: true, PassesThrough: true),
        ["exec"] = new(["-c", "-l"], ["-a"], PassesThrough: true),
        ["sudo"] = new(["-E", "-H", "-n", "-S", "-k", "-b", "-s", "-i", "-A", "-B", "--preserve-env"], ["-u", "-g", "-h", "-p", "-C", "-D", "-r", "-t", "-T", "-U", "-R"], DashDash: true, PassesThrough: true),
        ["doas"] = new(["-n", "-s"], ["-u", "-C"], DashDash: true),
        ["ionice"] = new(["-t"], ["-c", "-n", "-p", "-P", "-u"]),
        ["stdbuf"] = new([], ["-i", "-o", "-e"]),
        ["setsid"] = new(["-c", "-f", "-w"], []),
        ["xvfb-run"] = new(["-a", "--auto-servernum"], ["-s", "--server-args", "-e", "--error-file", "-f", "--auth-file", "-n", "--server-num", "-w", "--wait"]),
        ["npx"] = new(["-y", "--yes", "--no", "--no-install", "-q", "--quiet"], ["-p", "--package", "--registry", "--cache", "-c", "--call"], DashDash: true, Script: ["-c", "--call"]),
        ["bunx"] = new(["--bun"], ["-p", "--package"]),
        ["with-slot.sh"] = new([], ["--eta", "--tokens", "--label"], DashDash: true),
        ["ah-test-run"] = new([], ["--eta", "--tokens", "--label"], DashDash: true),
    };

    private static readonly string[] Shells = ["sh", "bash", "zsh", "dash", "ksh"];

    /// <summary>The program word of every wrapper that is one word (the test bucket's three-word spelling <c>ah test run</c> is not listed).</summary>
    internal static IReadOnlyList<string> Words { get; } = [.. Rows.Keys];

    /// <summary>Whether the word at <paramref name="i"/> runs the command after it: a wrapper (as it is used there) or a shell.</summary>
    internal static bool IsLauncher(IReadOnlyList<string> w, int i) => IsShell(BaseOf(w[i])) || Step(w, i) is not null;

    internal static bool IsShell(string name) => Shells.Contains(name, StringComparer.Ordinal);

    internal static bool IsAssignment(string w)
    {
        var eq = w.IndexOf('=', StringComparison.Ordinal);
        if (eq <= 0 || !(char.IsAsciiLetter(w[0]) || w[0] == '_'))
        {
            return false;
        }

        for (var k = 1; k < eq; k++)
        {
            if (!(char.IsAsciiLetterOrDigit(w[k]) || w[k] == '_'))
            {
                return false;
            }
        }

        return true;
    }

    internal static string BaseOf(string word)
    {
        var slash = word.LastIndexOf('/');
        return slash >= 0 ? word[(slash + 1)..] : word;
    }

    /// <summary>
    /// What the word at <paramref name="i"/> wraps, or null when it is not a wrapper (or is <c>ah</c>, <c>pnpm</c> or <c>yarn</c> without
    /// the words that make it one). <c>command -v</c> asks where a program is rather than running it: it steps to the end.
    /// </summary>
    internal static WrapperStep? Step(IReadOnlyList<string> w, int i)
    {
        var name = BaseOf(w[i]);
        var j = i + 1;
        var kind = name;
        Row row;
        switch (name)
        {
            case "ah":
                if (!(j + 1 < w.Count && w[j] == "test" && w[j + 1] == "run"))
                {
                    return null;
                }

                return Walk(w, i, j + 2, AhTestRun, Bucket, requireDashDash: true);
            case "pnpm" or "yarn":
                if (!(j < w.Count && w[j] is "exec" or "dlx"))
                {
                    return null;
                }

                return Walk(w, i, j + 1, Rows["npx"], name, requireDashDash: false);
            case "sh" or "bash" or "zsh" or "dash" or "ksh":
                var plain = true;
                for (var k = j; k < w.Count; k++)
                {
                    if (w[k].Length > 1 && w[k][0] == '-' && w[k][1] != '-' && w[k].Contains('c', StringComparison.Ordinal))
                    {
                        return null;
                    }

                    if (BaseOf(w[k]) is "with-slot.sh" or "ah-test-run")
                    {
                        return plain ? new WrapperStep("shell", k, true, false) : Unreliable("shell", i);
                    }

                    plain &= w[k].StartsWith('-');
                }

                return null;
            case "with-slot.sh" or "ah-test-run":
                kind = Bucket;
                row = Rows[name];
                break;
            default:
                if (!Rows.TryGetValue(name, out row!))
                {
                    return null;
                }

                break;
        }

        return Walk(w, i, j, row, kind, requireDashDash: false);
    }

    private static WrapperStep Walk(IReadOnlyList<string> w, int head, int j, Row row, string kind, bool requireDashDash)
    {
        var sawDashDash = false;
        while (j < w.Count)
        {
            var word = w[j];
            if (word == "--" && row.DashDash)
            {
                sawDashDash = true;
                j++;
                break;
            }

            if (kind == "env" && word.Length > 0 && !word.StartsWith('-') && TableIsAssignment(word))
            {
                j++;
                continue;
            }

            if (kind == "env" && word == "-")
            {
                j++;
                continue;
            }

            if (!word.StartsWith('-') || word.Length < 2)
            {
                break;
            }

            if (row.StopsAtV && (word.Contains('v', StringComparison.Ordinal) || word.Contains('V', StringComparison.Ordinal)))
            {
                return new WrapperStep(kind, w.Count, true, true);
            }

            var eq = word.StartsWith("--", StringComparison.Ordinal) ? word.IndexOf('=', StringComparison.Ordinal) : -1;
            var key = eq > 0 ? word[..eq] : word;
            if (row.Valued.Contains(key, StringComparer.Ordinal))
            {
                j += eq > 0 ? 1 : 2;
                if (j > w.Count)
                {
                    return Unreliable(kind, head);
                }

                continue;
            }

            if (row.Flags.Contains(word, StringComparer.Ordinal) || (row.NiceLevel && IsNiceLevel(word)))
            {
                j++;
                continue;
            }

            return Unreliable(kind, head);
        }

        if (row.Duration)
        {
            if (j >= w.Count || !IsDuration(w[j]))
            {
                return Unreliable(kind, head);
            }

            j++;
        }

        return requireDashDash && !sawDashDash ? Unreliable(kind, head) : new WrapperStep(kind, Math.Min(j, w.Count), true, row.PassesThrough);
    }

    /// <summary>
    /// The word holding a command line that a wrapper in <paramref name="w"/> before <paramref name="program"/> takes as an option value
    /// (<c>env -S</c>, <c>npx -c</c>), and how many leading characters of that word are the option itself; null when there is none.
    /// </summary>
    internal static (int Index, int Prefix)? ScriptWord(IReadOnlyList<string> w, int program)
    {
        for (var k = 0; k < program && k < w.Count; k++)
        {
            if (!Rows.TryGetValue(BaseOf(w[k]), out var row) || row.Script is not { } options)
            {
                continue;
            }

            for (var m = k + 1; m < program && m < w.Count; m++)
            {
                if (options.Contains(w[m], StringComparer.Ordinal) && m + 1 < w.Count)
                {
                    return (m + 1, 0);
                }

                foreach (var o in options)
                {
                    if (o.StartsWith("--", StringComparison.Ordinal) ? w[m].StartsWith(o + "=", StringComparison.Ordinal) : w[m].Length > o.Length && w[m].StartsWith(o, StringComparison.Ordinal))
                    {
                        return (m, o.StartsWith("--", StringComparison.Ordinal) ? o.Length + 1 : o.Length);
                    }
                }
            }
        }

        return null;
    }

    private static WrapperStep Unreliable(string kind, int head) => new(kind, head + 1, false, false);

    private static bool TableIsAssignment(string w) => IsAssignment(w);

    private static bool IsNiceLevel(string w) => w.Length > 1 && w[0] == '-' && w[1..].All(char.IsAsciiDigit);

    private static bool IsDuration(string w)
    {
        if (w.Length == 0)
        {
            return false;
        }

        var end = w.Length - (w[^1] is 's' or 'm' or 'h' or 'd' ? 1 : 0);
        return end > 0 && w[..end].All(c => char.IsAsciiDigit(c) || c == '.') && char.IsAsciiDigit(w[0]);
    }
}
