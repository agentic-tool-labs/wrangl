using Ah.Core.Text;

namespace Ah.Hooks.Gates;

/// <summary>One <c>herdr &lt;group&gt; &lt;verb&gt; &lt;name&gt;</c> found in a shell command string.</summary>
internal sealed record HerdrCall(string Group, string Verb, string Name, string? Pane, IReadOnlyList<string> Args);

/// <summary>Which <c>herdr</c> subcommands a Bash command runs.</summary>
internal static class HerdrCalls
{
    // Herdr's global options that take a value; every other option it accepts is a bare flag.
    private static readonly string[] ValueOptions = ["--session", "--remote", "--remote-keybindings"];

    /// <summary>The Herdr CLI verbs that write input into a pane, by group.</summary>
    public static readonly IReadOnlyList<KeyValuePair<string, string[]>> InputVerbs =
    [
        new("agent", ["prompt", "send-keys", "attach"]),
        new("pane", ["send-text", "send-keys", "run"]),
        new("terminal", ["attach", "session control"]),
    ];

    private static bool WordChar(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '.' or '-';

    /// <summary>
    /// The text after each <c>herdr</c> command word: <c>(?&lt;![\w.-])["']?herdr["']?(?=\s)([^;&amp;|\n]*)</c> over the
    /// string with each backslash-newline made a space.
    /// </summary>
    private static List<string> Runs(string s)
    {
        var runs = new List<string>();
        var p = 0;
        while (p < s.Length)
        {
            if (TryMatch(s, p, out var bodyStart, out var end))
            {
                runs.Add(s.Substring(bodyStart, end - bodyStart));
                p = Math.Max(end, p + 1);
            }
            else
            {
                p++;
            }
        }

        return runs;
    }

    private static bool TryMatch(string s, int p, out int bodyStart, out int end)
    {
        bodyStart = 0;
        end = 0;
        if (p > 0 && WordChar(s[p - 1]))
        {
            return false;
        }

        var i = p;
        if (i < s.Length && s[i] is '"' or '\'')
        {
            i++;
        }

        if (string.CompareOrdinal(s, i, "herdr", 0, 5) != 0)
        {
            return false;
        }

        i += 5;
        // an optional closing quote, taken only when whitespace follows it
        if (i < s.Length && s[i] is '"' or '\'' && i + 1 < s.Length && JsText.IsSpace(s[i + 1]))
        {
            i++;
        }
        else if (!(i < s.Length && JsText.IsSpace(s[i])))
        {
            return false;
        }

        bodyStart = i;
        while (i < s.Length && s[i] is not (';' or '&' or '|' or '\n'))
        {
            i++;
        }

        end = i;
        return true;
    }

    private static List<string> Words(string run)
    {
        var words = new List<string>();
        var trimmed = JsText.Trim(run);
        var start = -1;
        for (var i = 0; i <= trimmed.Length; i++)
        {
            var space = i == trimmed.Length || JsText.IsSpace(trimmed[i]);
            if (!space && start < 0)
            {
                start = i;
            }
            else if (space && start >= 0)
            {
                words.Add(Unquote(trimmed.Substring(start, i - start)));
                start = -1;
            }
        }

        return words;
    }

    // t.replace(/^(['"])(.*)\1$/, "$2")
    internal static string Unquote(string t) =>
        t.Length >= 2 && t[0] is '"' or '\'' && t[^1] == t[0] ? t[1..^1] : t;

    /// <summary>
    /// Every <c>herdr &lt;group&gt; &lt;verb&gt;</c> in <paramref name="command"/> whose verb is one of <paramref name="verbs"/>
    /// (a verb may be two words). Herdr takes its global options before the group and between the group and the verb, so
    /// those are stepped over; after the verb a flag takes the next word as its value unless written <c>--flag=value</c>,
    /// and the scan stops at <c>--</c>.
    /// </summary>
    public static IReadOnlyList<HerdrCall> Find(string? command, string group, IReadOnlyList<string> verbs)
    {
        var calls = new List<HerdrCall>();
        if (command is null)
        {
            return calls;
        }

        foreach (var run in Runs(command.Replace("\\\n", " ", StringComparison.Ordinal)))
        {
            var tokens = Words(run);
            var i = 0;

            void SkipOptions()
            {
                while (i < tokens.Count && tokens[i].StartsWith('-') && tokens[i] != "--")
                {
                    var token = tokens[i++];
                    if (ValueOptions.Contains(token))
                    {
                        i++;
                    }
                }
            }

            SkipOptions();
            if (i >= tokens.Count || tokens[i] != group)
            {
                continue;
            }

            i++;
            SkipOptions();
            var verb = verbs.FirstOrDefault(v => v.Split(' ').Select((word, k) => i + k < tokens.Count && tokens[i + k] == word).All(x => x));
            if (verb is null)
            {
                continue;
            }

            string? name = null;
            string? pane = null;
            var args = new List<string>();
            for (i += verb.Split(' ').Length; i < tokens.Count; i++)
            {
                var t = tokens[i];
                if (t == "--")
                {
                    break;
                }

                if (t.StartsWith("--", StringComparison.Ordinal))
                {
                    // t.split("=", 2): the first two pieces, so a second "=" and what follows it are dropped
                    var pieces = t.Split('=');
                    var flag = pieces[0];
                    string? value;
                    if (pieces.Length > 1)
                    {
                        value = pieces[1];
                    }
                    else
                    {
                        i++;
                        value = i < tokens.Count ? tokens[i] : null;
                    }

                    if (flag == "--pane")
                    {
                        pane = string.IsNullOrEmpty(value) ? null : value;
                    }

                    continue;
                }

                if (name is null)
                {
                    name = t;
                }
                else
                {
                    args.Add(t);
                }
            }

            if (name is not null)
            {
                calls.Add(new HerdrCall(group, verb, name, pane, args));
            }
        }

        return calls;
    }

    /// <summary>Every Herdr CLI call in <paramref name="command"/> that writes input into a pane.</summary>
    public static IReadOnlyList<HerdrCall> InputCalls(string? command) =>
        InputVerbs.SelectMany(g => Find(command, g.Key, g.Value)).ToList();
}
