using System.Text;
using System.Text.RegularExpressions;
using Ah.Core.Text;

namespace Ah.Hooks.Gates;

/// <summary>One shell event: a simple command's words, or the events of a <c>$(…)</c>, backtick or <c>( … )</c> body.</summary>
internal abstract record PushEvent;

/// <summary>
/// <paramref name="Redirects"/> are the targets of the command's output redirections, as spelled. <paramref name="Next"/> is the
/// operator that ends the command (<c>;</c>, <c>&amp;&amp;</c>, <c>||</c>, <c>|</c>, <c>&amp;</c> or a newline), null when the text ends.
/// <paramref name="Spans"/> holds the <c>[start, end)</c> offsets of each word in the text that was parsed, one per word; it is null
/// when the command did not come from a parse that tracks them.
/// </summary>

internal sealed record PushCommand(List<string> Words, List<string>? Redirects = null, bool Conditional = false, bool NoAlt = false, string? Next = null, List<(int Start, int End)>? Spans = null) : PushEvent;

internal sealed record PushSub(List<PushEvent> Events) : PushEvent;

/// <summary>Nesting too deep to parse: the walk counts the line as unreadable.</summary>
internal sealed record PushUnreadable : PushEvent;

/// <summary>A <c>git</c> invocation with its effective directory, git-dir and work-tree.</summary>
internal sealed record GitInvocation(string? Sub, List<string> Args, string Cwd, string? GitDir, string? WorkTree, List<GitConfigOption> Config)
{
    /// <summary>Whether the command names a git directory or work tree by <c>--git-dir</c>, <c>--work-tree</c>, <c>GIT_DIR</c> or <c>GIT_WORK_TREE</c>, literal or not.</summary>
    public bool OverridesTree { get; init; }
}

/// <summary>A <c>-c</c> or <c>--config-env</c> option: which one, and its <c>key=value</c> text.</summary>
internal sealed record GitConfigOption(string Option, string KeyValue)
{
    public string Key => KeyValue.Split('=')[0];
}

internal sealed record GhInvocation(List<string> Args, string Cwd);

/// <summary>The most commands one command line may have walked, nested scripts and alternative starts included.</summary>
internal sealed class WorkBudget
{
    public const int Cap = 4096;

    public int Left { get; set; } = Cap;

    public bool Spent => Left < 0;

    /// <summary>The deepest nesting of subshells, scripts and <c>eval</c> that is walked; deeper counts as spent.</summary>
    public const int MaxDepth = 64;
}

/// <summary>The directory a command sequence is in, and the <c>pushd</c> stack.</summary>
internal sealed class PushState(string cwd, List<string>? stack = null, Dictionary<string, string>? exports = null, string? home = null, Dictionary<string, string>? vars = null, HashSet<string>? seen = null, WorkBudget? budget = null)
{
    /// <summary>The commands left to walk, shared by every copy of the state; once it is spent the walk stops and the line counts as unreadable.</summary>
    public WorkBudget Budget { get; } = budget ?? new WorkBudget();

    /// <summary>The scripts already walked, by directory, variables and text, shared by every copy of the state: a script reached from several starts is walked once.</summary>
    public HashSet<string> Seen { get; } = seen ?? new HashSet<string>(StringComparer.Ordinal);

    public bool FirstVisit(string here, string script) =>
        Seen.Add(here + "\0" + string.Join(",", Vars.OrderBy(static v => v.Key, StringComparer.Ordinal).Select(static v => v.Key + "=" + v.Value)) + "\0" + script);

    public string Cwd { get; set; } = cwd;

    public List<string> Stack { get; } = stack ?? [];

    /// <summary>The variables an earlier <c>export</c> of the same command text set.</summary>
    public Dictionary<string, string> Exports { get; } = exports ?? new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The home directory <c>~</c>, <c>$HOME</c> and <c>${HOME}</c> stand for; null when it is not known.</summary>
    public string? Home { get; } = home;

    /// <summary>The variables set earlier in the same command text by a plain <c>NAME=literal</c> or <c>export</c>.</summary>
    public Dictionary<string, string> Vars { get; } = vars ?? new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary><c>HOME</c> was set to something the guard cannot read, so <c>~</c> and <c>$HOME</c> stand for nothing known.</summary>
    public bool HomeUnknown { get; set; }

    /// <summary>How many copies deep this state is.</summary>
    public int Depth { get; init; }

    public PushState Copy(string cwd) =>
        new(cwd, [.. Stack], new Dictionary<string, string>(Exports, StringComparer.Ordinal), Home, new Dictionary<string, string>(Vars, StringComparer.Ordinal), Seen, Budget)
        {
            HomeUnknown = HomeUnknown,
            Depth = Depth + 1,
        };
}

/// <summary>
/// One command the shell would run: its name and arguments, the assignments written before it (<paramref name="Env"/>) and
/// exported earlier (<paramref name="Exported"/>), the directory it runs in, and its output-redirection targets. A command with
/// only redirections has an empty <paramref name="Name"/>. <paramref name="Programs"/> are the words the shell ran as programs (the
/// name and each step wrapper before it) with their offsets in the command text given to <see cref="PushShell.Walk"/>; it is null
/// unless that call asked for them and the command came from that text itself (not from <c>sh -c</c>, <c>eval</c>, a wrapper script or a backtick body).
/// </summary>
internal sealed record ShellCall(
    string Name,
    List<string> Args,
    Dictionary<string, string> Env,
    IReadOnlyDictionary<string, string> Exported,
    string Cwd,
    List<string> Redirects,
    List<(int Start, int End, string Word)>? Programs = null);

/// <summary>
/// Shell text to events, and events to git and gh invocations, for the push guard. Words keep <c>$…</c> and backtick text
/// unexpanded; the parse matches what the guard's text rules need, not a shell's full grammar.
/// </summary>
internal static partial class PushShell
{
    private static readonly HashSet<string> Conditionals = new(StringComparer.Ordinal)
    {
        "if", "then", "elif", "else", "while", "until", "for", "case", "do", "select",
    };

    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal) { "!", "{", "}", "if", "then", "elif", "else", "do", "while", "until" };

    private static readonly HashSet<string> Shells = new(StringComparer.Ordinal) { "bash", "sh", "zsh", "dash", "ksh" };

    private static readonly HashSet<string> GitOptsWithArg = new(StringComparer.Ordinal)
    {
        "-C", "-c", "--git-dir", "--work-tree", "--namespace", "--config-env", "--super-prefix",
    };

    private const string NotLineEnd = "[^\\n\\r\\u2028\\u2029]";

    [GeneratedRegex("^([A-Za-z_][A-Za-z0-9_]*)=(.*)\\z", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex Assignment();

    [GeneratedRegex("^[+-][0-9]+\\z", RegexOptions.CultureInvariant)]
    private static partial Regex StackIndex();

    [GeneratedRegex("^-[A-Za-z]*c[A-Za-z]*\\z", RegexOptions.CultureInvariant)]
    private static partial Regex DashCGroup();

    [GeneratedRegex("^[-+][A-Za-z]*[oO]\\z", RegexOptions.CultureInvariant)]
    private static partial Regex OptionWithArg();

    [GeneratedRegex("^[-+]" + NotLineEnd, RegexOptions.CultureInvariant)]
    private static partial Regex ShellOption();

    [GeneratedRegex("[\\$`~]", RegexOptions.CultureInvariant)]
    private static partial Regex Unliteral();

    private static bool AllDigits(string s) => s.Length > 0 && s.All(c => c is >= '0' and <= '9');

    private static bool IsSpace(char c) => JsText.IsSpace(c);

    // ---------------------------------------------------------------- shell text → events

    private static int SkipHeredoc(string src, int i, string delimiter, bool strip)
    {
        while (i < src.Length)
        {
            var j = src.IndexOf('\n', i);
            if (j < 0)
            {
                j = src.Length;
            }

            var line = src[i..j];
            if (strip)
            {
                line = line.TrimStart('\t');
            }

            i = j + 1;
            if (line == delimiter)
            {
                break;
            }
        }

        return i;
    }

    /// <summary>
    /// Parses <paramref name="src"/> from <paramref name="start"/>; when <paramref name="nested"/> the closing <c>)</c> ends it.
    /// Each command records its words' offsets in <paramref name="src"/> unless <paramref name="track"/> is false.
    /// </summary>
    public static (List<PushEvent> Events, int End) Parse(string src, int start = 0, bool nested = false, int depth = 0, bool track = true)
    {
        if (depth > WorkBudget.MaxDepth)
        {
            return ([new PushUnreadable()], src.Length);
        }

        var events = new List<PushEvent>();
        var heredocs = new List<(string Delimiter, bool Strip)>();
        var words = new List<string>();
        var spans = track ? new List<(int Start, int End)>() : null;
        var redirects = new List<string>();
        string? word = null;
        var wordAt = 0;
        var step = start;
        var drop = false;
        var dropOutput = false;
        var i = start;
        var conditional = false;

        void EndWord()
        {
            if (word is not null)
            {
                if (drop)
                {
                    drop = false;
                    if (dropOutput && !AllDigits(word) && word != "-")
                    {
                        redirects.Add(word);
                    }
                }
                else
                {
                    words.Add(word);
                    spans?.Add((wordAt, Math.Min(i, src.Length)));
                }
            }

            word = null;
        }

        void EndCmd(string? next = null)
        {
            EndWord();
            if (words.Count > 0 || redirects.Count > 0)
            {
                events.Add(new PushCommand(words, redirects.Count > 0 ? redirects : null, conditional, Next: next, Spans: spans));
                if (words.Count > 0 && Conditionals.Contains(words[0]))
                {
                    conditional = true;
                }
            }

            words = [];
            spans = track ? [] : null;
            redirects = [];
            drop = false;
        }

        void Add(string s)
        {
            if (word is null)
            {
                wordAt = step;
            }

            word = (word ?? string.Empty) + s;
        }

        int Substitution(int from)
        {
            var inner = Parse(src, from, true, depth + 1, track);
            events.Add(new PushSub(inner.Events));
            return inner.End;
        }

        int Backtick(int from)
        {
            var j = from;
            while (j < src.Length && src[j] != '`')
            {
                j += src[j] == '\\' ? 2 : 1;
            }

            var end = Math.Min(j, src.Length);
            events.Add(new PushSub(Parse(src[from..end], depth: depth + 1, track: false).Events));
            return j + 1;
        }

        void Embedded()
        {
            var s = i;
            i = src[i] == '`' ? Backtick(i + 1) : Substitution(i + 2);
            Add(src[s..Math.Min(i, src.Length)]);
        }

        char? At(int k) => k < src.Length ? src[k] : null;

        while (i < src.Length)
        {
            step = i;
            var c = src[i];
            if (c == '\\')
            {
                if (At(i + 1) != '\n')
                {
                    Add(At(i + 1) is { } n ? n.ToString() : string.Empty);
                }

                i += 2;
            }
            else if (c == '\'')
            {
                var j = src.IndexOf('\'', i + 1);
                var end = j < 0 ? src.Length : j;
                Add(src[Math.Min(i + 1, end)..end]);
                i = end + 1;
            }
            else if (c == '"')
            {
                Add(string.Empty);
                i++;
                while (i < src.Length && src[i] != '"')
                {
                    if (src[i] == '\\' && (At(i + 1) is null || "$`\"\\\n".Contains(At(i + 1)!.Value, StringComparison.Ordinal)))
                    {
                        // `"…".includes(undefined ?? "")` holds, so a trailing backslash appends the text "undefined".
                        if (At(i + 1) != '\n')
                        {
                            Add(At(i + 1) is { } n ? n.ToString() : "undefined");
                        }

                        i += 2;
                    }
                    else if ((src[i] == '$' && At(i + 1) == '(') || src[i] == '`')
                    {
                        Embedded();
                    }
                    else
                    {
                        Add(src[i++].ToString());
                    }
                }

                i++;
            }
            else if ((c == '$' && At(i + 1) == '(') || c == '`')
            {
                Embedded();
            }
            else if (c == '(')
            {
                EndCmd();
                i = Substitution(i + 1);
            }
            else if (c == ')')
            {
                EndCmd();
                i++;
                if (nested)
                {
                    return (events, i);
                }
            }
            else if (c == '#' && word is null)
            {
                while (i < src.Length && src[i] != '\n')
                {
                    i++;
                }
            }
            else if (c == '\n')
            {
                EndCmd("\n");
                i++;
                var pending = heredocs.ToList();
                heredocs.Clear();
                foreach (var h in pending)
                {
                    i = SkipHeredoc(src, i, h.Delimiter, h.Strip);
                }
            }
            else if (c is '<' or '>' || (c == '&' && At(i + 1) == '>'))
            {
                // A redirection: its fd prefix and its target word are not arguments.
                if (word is not null && AllDigits(word))
                {
                    word = null;
                }
                else
                {
                    EndWord();
                }

                if (string.CompareOrdinal(src, i, "<<<", 0, 3) == 0)
                {
                    i += 3;
                    drop = true;
                    dropOutput = false;
                }
                else if (string.CompareOrdinal(src, i, "<<", 0, 2) == 0)
                {
                    i += 2;
                    var strip = At(i) == '-';
                    if (strip)
                    {
                        i++;
                    }

                    while (At(i) is ' ' or '\t')
                    {
                        i++;
                    }

                    var delimiter = new StringBuilder();
                    for (; i < src.Length && !(IsSpace(src[i]) || ";&|<>()".Contains(src[i], StringComparison.Ordinal)); i++)
                    {
                        if (!"'\"\\".Contains(src[i], StringComparison.Ordinal))
                        {
                            delimiter.Append(src[i]);
                        }
                    }

                    heredocs.Add((delimiter.ToString(), strip));
                }
                else
                {
                    dropOutput = c is '>' or '&';
                    i += c == '&' ? 2 : 1;
                    if (At(i) is { } n && ">&|<".Contains(n, StringComparison.Ordinal))
                    {
                        i++;
                    }

                    drop = true;
                }
            }
            else if (c is ';' or '&' or '|')
            {
                EndCmd(c switch { '&' when At(i + 1) == '&' => "&&", '|' when At(i + 1) == '|' => "||", _ => c.ToString() });
                if (c != ';')
                {
                    conditional = true;
                }

                i++;
            }
            else if (c is ' ' or '\t')
            {
                EndWord();
                i++;
            }
            else
            {
                Add(c.ToString());
                i++;
            }
        }

        EndCmd();
        return (events, i);
    }

    // ---------------------------------------------------------------- events → git and gh invocations

    private static bool LiteralDir(string? w) => !string.IsNullOrEmpty(w) && !Unliteral().IsMatch(w);

    private static string Basename(string w) => w[(w.LastIndexOf('/') + 1)..];

    /// <summary>
    /// Index of the word the shell actually runs, past assignments, reserved words and wrappers (repeatedly, in any order),
    /// the assignments met on the way, and the directory an <c>env -C</c> names. <c>command -v</c> runs nothing, which reads
    /// as an index past the end.
    /// </summary>
    private static (int K, Dictionary<string, string> Env, string? Dir, List<int> Alt, List<int> Wrappers) CommandWord(List<string> w, bool expand = true)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        var alt = new List<int>();
        var wrappers = new List<int>();
        string? dir = null;
        var k = 0;
        while (k < w.Count)
        {
            if (Assignment().Match(w[k]) is { Success: true } m)
            {
                env[m.Groups[1].Value] = m.Groups[2].Value;
                k++;
                continue;
            }

            if (Reserved.Contains(w[k]))
            {
                k++;
                continue;
            }

            if (Ah.Core.CoreModule.StepWrapper(w, k) is not { } step)
            {
                break;
            }

            wrappers.Add(k);
            if (step.Kind == "env")
            {
                for (var j = k + 1; j < step.Next; j++)
                {
                    if (Assignment().Match(w[j]) is { Success: true } kv)
                    {
                        env[kv.Groups[1].Value] = kv.Groups[2].Value;
                    }
                    else if (w[j] is "-C" or "--chdir")
                    {
                        dir = j + 1 < w.Count ? w[j + 1] : null;
                    }
                    else if (w[j].StartsWith("--chdir=", StringComparison.Ordinal))
                    {
                        dir = w[j]["--chdir=".Length..];
                    }
                }
            }

            if (step.Reliable)
            {
                k = step.Next;
                continue;
            }

            k = step.Next;
            if (expand)
            {
                alt.AddRange(Enumerable.Range(k + 1, Math.Max(0, w.Count - k - 1)));
            }

            break;
        }

        return (k, env, dir, alt, wrappers);
    }

    private static GitInvocation MakeGitInvocation(List<string> args, string cwd, Dictionary<string, string> env)
    {
        var config = new List<GitConfigOption>();
        var named = new Dictionary<string, string?>(StringComparer.Ordinal);
        var k = 0;
        for (; k < args.Count && args[k].StartsWith('-'); k++)
        {
            var a = args[k];
            var eq = a.StartsWith("--", StringComparison.Ordinal) ? a.IndexOf('=', StringComparison.Ordinal) : -1;
            var opt = eq > 0 ? a[..eq] : a;
            if (!GitOptsWithArg.Contains(opt))
            {
                continue;
            }

            string? value;
            if (eq > 0)
            {
                value = a[(eq + 1)..];
            }
            else
            {
                k++;
                value = k < args.Count ? args[k] : null;
            }

            if (opt == "-C")
            {
                if (LiteralDir(value))
                {
                    cwd = NodePath.Resolve(cwd, value!);
                }
            }
            else if (opt is "-c" or "--config-env")
            {
                config.Add(new GitConfigOption(opt, value ?? string.Empty));
            }
            else if (opt is "--git-dir" or "--work-tree")
            {
                named[opt] = value;
            }
        }

        // An option overrides the environment variable, as in git; a non-literal value applies neither.
        string? Pick(string opt, string envVar)
        {
            string? v = named.TryGetValue(opt, out var given) ? given : env.GetValueOrDefault(envVar);
            return LiteralDir(v) ? NodePath.Resolve(cwd, v!) : null;
        }

        var gitDir = Pick("--git-dir", "GIT_DIR");
        var workTree = Pick("--work-tree", "GIT_WORK_TREE");
        var rest = k + 1 < args.Count ? args.GetRange(k + 1, args.Count - k - 1) : [];
        var overridesTree = named.Count > 0 || env.ContainsKey("GIT_DIR") || env.ContainsKey("GIT_WORK_TREE");
        return new GitInvocation(k < args.Count ? args[k] : null, rest, cwd, gitDir, workTree, config) { OverridesTree = overridesTree };
    }

    /// <summary>
    /// Whether the line's exit status is the status of each command in it: one simple command, or simple commands joined only by
    /// <c>&amp;&amp;</c>, with no group, substitution, subshell, shell <c>-c</c>, <c>eval</c> or control word. Output redirections do not matter.
    /// A line this parse cannot reduce to simple commands is not status-bearing.
    /// </summary>
    public static bool StatusBearing(List<PushEvent> events)
    {
        var commands = new List<PushCommand>();
        foreach (var e in events)
        {
            if (e is not PushCommand simple)
            {
                return false;
            }

            commands.Add(simple);
        }

        for (var i = 0; i < commands.Count; i++)
        {
            var first = commands[i].Words.Count > 0 ? commands[i].Words[0] : string.Empty;
            if (Reserved.Contains(first) || Conditionals.Contains(first) || first == "eval" || Shells.Contains(Basename(first)))
            {
                return false;
            }

            var closes = i == commands.Count - 1;
            if (closes ? commands[i].Next is not (null or ";" or "\n") : commands[i].Next != "&&")
            {
                return false;
            }
        }

        return commands.Count > 0;
    }

    /// <summary>
    /// <paramref name="state"/> is the directory and <c>pushd</c> stack of this command sequence (a subshell or nested command
    /// string gets a copy). <paramref name="root"/> is the hook's own cwd, where an unknowable directory change falls back to.
    /// </summary>
    public static void Collect(List<PushEvent> events, PushState state, string root, List<GitInvocation> git, List<GhInvocation> gh) =>
        Walk(
            events,
            state,
            root,
            call =>
            {
                if (Basename(call.Name) == "git")
                {
                    git.Add(MakeGitInvocation(call.Args, call.Cwd, call.Env));
                }
                else if (Basename(call.Name) == "gh")
                {
                    gh.Add(new GhInvocation(call.Args, call.Cwd));
                }
            });

    /// <summary>
    /// Visits every command of <paramref name="events"/> in order, tracking <c>cd</c>, <c>pushd</c>, <c>popd</c> and <c>export</c>
    /// and descending into subshells, <c>sh -c</c> bodies and <c>eval</c>. A <c>cd</c> is visited before it moves the directory.
    /// With <paramref name="original"/> the events are those of one <see cref="Parse"/> of the command text, and each call from that
    /// text (not from a nested command string) carries <see cref="ShellCall.Programs"/>.
    /// </summary>
    public static void Walk(List<PushEvent> events, PushState state, string root, Action<ShellCall> visit, bool original = false)
    {
        foreach (var ev in events)
        {
            if (state.Depth > WorkBudget.MaxDepth)
            {
                state.Budget.Left = -1;
            }

            if (--state.Budget.Left < 0)
            {
                return;
            }

            if (ev is PushUnreadable)
            {
                state.Budget.Left = -1;
                return;
            }

            if (ev is PushSub sub)
            {
                Walk(sub.Events, state.Copy(state.Cwd), root, visit, original);
                continue;
            }

            var command = (PushCommand)ev;
            var cmd = command.Words;
            var redirects = command.Redirects ?? [];
            var (k, env, dir, alt, wrappers) = CommandWord(cmd, !command.NoAlt);
            List<(int Start, int End, string Word)>? programs = null;
            if (original && command.Spans is { } spans && spans.Count == cmd.Count)
            {
                programs = wrappers.Append(k).Where(p => p < cmd.Count).Select(p => (spans[p].Start, spans[p].End, cmd[p])).ToList();
            }

            var here = LiteralDir(dir) ? NodePath.Resolve(state.Cwd, dir!) : state.Cwd;
            if (Ah.Core.CoreModule.WrapperScript(cmd, k) is { } wrapperScript && state.FirstVisit(here, wrapperScript))
            {
                Walk(Parse(wrapperScript).Events, state.Copy(here), root, visit);
            }

            if (k >= cmd.Count)
            {
                foreach (var (key, value) in env)
                {
                    SetVariable(state, key, value, command.Conditional);
                }

                if (redirects.Count > 0)
                {
                    visit(new ShellCall(string.Empty, [], env, state.Exports, here, redirects.Select(r => Expand(r, state)).ToList(), programs));
                }

                continue;
            }

            var name = Expand(cmd[k], state);
            var args = cmd.GetRange(k + 1, cmd.Count - k - 1).Select(a => Expand(a, state)).ToList();
            visit(new ShellCall(name, args, env, state.Exports, here, redirects.Select(r => Expand(r, state)).ToList(), programs));
            foreach (var j in alt)
            {
                var words = env.Select(static kv => kv.Key + "=" + kv.Value).Concat(cmd.GetRange(j, cmd.Count - j)).ToList();
                Walk([new PushCommand(words, command.Redirects, command.Conditional, NoAlt: true)], state.Copy(here), root, visit);
            }

            if (name == "cd")
            {
                if (args.Count == 1 && LiteralDir(args[0]) && !args[0].StartsWith('-'))
                {
                    state.Cwd = NodePath.Resolve(state.Cwd, args[0]);
                }
            }
            else if (name == "export")
            {
                foreach (var a in args)
                {
                    if (Assignment().Match(a) is { Success: true } m)
                    {
                        state.Exports[m.Groups[1].Value] = m.Groups[2].Value;
                        SetVariable(state, m.Groups[1].Value, m.Groups[2].Value, command.Conditional);
                    }
                }
            }
            else if (name == "pushd")
            {
                if (args.Count == 1 && !StackIndex().IsMatch(args[0]))
                {
                    state.Stack.Add(state.Cwd);
                    if (LiteralDir(args[0]))
                    {
                        state.Cwd = NodePath.Resolve(state.Cwd, args[0]);
                    }
                }
                else
                {
                    state.Cwd = root;
                }
            }
            else if (name == "popd")
            {
                if (args.Count == 0 && state.Stack.Count > 0)
                {
                    state.Cwd = state.Stack[^1];
                    state.Stack.RemoveAt(state.Stack.Count - 1);
                }
                else
                {
                    state.Cwd = root;
                }
            }
            else if (Shells.Contains(Basename(name)))
            {
                var j = 0;
                var dashC = false;
                for (; j < args.Count && ShellOption().IsMatch(args[j]); j++)
                {
                    if (DashCGroup().IsMatch(args[j]))
                    {
                        dashC = true;
                    }

                    if (OptionWithArg().IsMatch(args[j]) || args[j] is "--rcfile" or "--init-file")
                    {
                        j++;
                    }
                }

                if (dashC && j < args.Count && state.FirstVisit(here, args[j]))
                {
                    Walk(Parse(args[j]).Events, state.Copy(here), root, visit);
                }
            }
            else if (name == "eval" && state.FirstVisit(here, string.Join(' ', args)))
            {
                Walk(Parse(string.Join(' ', args)).Events, state.Copy(here), root, visit);
            }
        }
    }

    private static void SetVariable(PushState state, string name, string value, bool conditional)
    {
        var expanded = Expand(value, state);
        if (conditional || Unliteral().IsMatch(expanded))
        {
            state.Vars.Remove(name);
            if (name == "HOME")
            {
                state.HomeUnknown = true;
            }
        }
        else
        {
            state.Vars[name] = expanded;
            if (name == "HOME")
            {
                state.HomeUnknown = false;
            }
        }
    }

    private static string? EffectiveHome(PushState state) =>
        state.HomeUnknown ? null : state.Vars.TryGetValue("HOME", out var assigned) ? assigned : state.Home;

    /// <summary>
    /// <paramref name="word"/> with a leading <c>~</c>, and every <c>$HOME</c>, <c>$PWD</c> or variable the same command text
    /// set to a literal, replaced by its value. Anything else, <c>$(…)</c> and backticks included, is left as it is.
    /// </summary>
    private static string Expand(string word, PushState state)
    {
        if (word.Length == 0 || (word.IndexOf('$', StringComparison.Ordinal) < 0 && word[0] != '~'))
        {
            return word;
        }

        var text = word;
        if (text[0] == '~' && (text.Length == 1 || text[1] == '/') && EffectiveHome(state) is { Length: > 0 } home)
        {
            text = home + text[1..];
        }

        var builder = new System.Text.StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '$')
            {
                builder.Append(text[i]);
                continue;
            }

            var start = i + 1;
            string name;
            int end;
            if (start < text.Length && text[start] == '{')
            {
                var close = text.IndexOf('}', start);
                if (close < 0)
                {
                    builder.Append('$');
                    continue;
                }

                name = text[(start + 1)..close];
                end = close + 1;
            }
            else
            {
                end = start;
                while (end < text.Length && (char.IsAsciiLetterOrDigit(text[end]) || text[end] == '_'))
                {
                    end++;
                }

                name = text[start..end];
            }

            var value = name switch
            {
                "HOME" => EffectiveHome(state),
                "PWD" => state.Cwd,
                _ => state.Vars.TryGetValue(name, out var known) ? known : null,
            };
            if (name.Length == 0 || value is null)
            {
                builder.Append('$');
                continue;
            }

            builder.Append(value);
            i = end - 1;
        }

        return builder.ToString();
    }

    /// <summary>Whether <paramref name="word"/> is a literal directory name: no <c>$</c>, backtick or <c>~</c>.</summary>
    public static bool IsLiteral(string? word) => LiteralDir(word);

    /// <summary>The last path segment of a command word (<c>/usr/bin/git</c> is <c>git</c>).</summary>
    public static string NameOf(string word) => Basename(word);
}
