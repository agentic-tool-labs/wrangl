using System.Globalization;
using System.Text.RegularExpressions;
using Ah.Core.Json;
using Ah.Core.Shell;

namespace Ah.Watch;

/// <summary>A row the registry could not use, and why.</summary>
/// <param name="File">The file that holds it.</param>
/// <param name="Reason">What is wrong with it.</param>
public sealed record TestRegistryIssue(string File, string Reason);

/// <summary>One layer of the registry and how many rows it contributed.</summary>
/// <param name="Name">shipped, user or repo.</param>
/// <param name="Path">The file, or <c>(shipped)</c>.</param>
/// <param name="Rows">The rows that were usable.</param>
public sealed record TestRegistryLayer(string Name, string Path, int Rows);

/// <summary>Whether a test run covers a whole project or suite (<c>Full</c>) or a selection of it (<c>Narrow</c>).</summary>
internal enum TestScope
{
    Full,
    Narrow,
}

/// <summary>A command of a test run: which row matched, in which command, and whether it is a targeted run.</summary>
internal sealed class TestMatch
{
    internal TestMatch(TestRow row, CommandSegment segment, int consumed, bool light)
    {
        Row = row;
        Segment = segment;
        Consumed = consumed;
        Light = light;
    }

    /// <summary>The row's id.</summary>
    public string Id => Row.Id;

    /// <summary>Whether the command is a targeted run (the row's <c>light_if</c> matched).</summary>
    public bool Light { get; }

    /// <summary>Narrow when the row's <c>light_if</c> matched the command, else Full; a row without <c>light_if</c> has no narrow form.</summary>
    public TestScope Scope => Light ? TestScope.Narrow : TestScope.Full;

    /// <summary>The command that matched.</summary>
    public CommandSegment Segment { get; }

    internal TestRow Row { get; }

    /// <summary>The number of words the row's program part spans.</summary>
    internal int Consumed { get; }

    /// <summary>The tokens this run asks for in a bucket of <paramref name="capacity"/>: 2 when targeted, else the row's.</summary>
    public int Tokens(int capacity)
    {
        var cap = Math.Max(1, capacity);
        if (Light)
        {
            return Math.Min(2, cap);
        }

        return Row.Tokens == "half" ? Math.Min(Math.Min(8, (cap + 1) / 2), cap) : Math.Min(int.Parse(Row.Tokens, CultureInfo.InvariantCulture), cap);
    }
}

/// <summary>What the bucket does to a command it is about to run: its row, its tokens, the environment to add and the argv to run instead.</summary>
/// <param name="Row">The first matching row's id, or null when none matches.</param>
/// <param name="Tokens">The tokens the row asks for, or 0 when no row matches.</param>
/// <param name="Env">The variables to export, in order.</param>
/// <param name="Argv">The words to run instead of the given ones, or null when they stay as they are.</param>
public sealed record TestPlan(string? Row, int Tokens, IReadOnlyList<KeyValuePair<string, string>> Env, IReadOnlyList<string>? Argv);

internal sealed record TestAdapterArgs(Regex? Unless, Regex? Requires, IReadOnlyList<string> Add, string At, int Skip);

internal sealed record TestRow(
    string Id,
    string Layer,
    Regex? Program,
    Regex? Match,
    string Tokens,
    Regex? LightIf,
    IReadOnlyList<KeyValuePair<string, string>> Env,
    IReadOnlyList<KeyValuePair<string, string>> EnvAppend,
    IReadOnlyList<TestAdapterArgs> Args,
    bool Exempt);

/// <summary>
/// What counts as a test run: rows loaded from three layers, the repository's (it can only add), the user's (it can add and exempt)
/// and the shipped defaults. The first row that matches a command of the text wins. Rows are data, so a new stack is one row.
/// </summary>
internal sealed class TestRegistry
{
    private const string ResourceName = "test-commands.json";

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    private static readonly Lazy<TestRegistry> ShippedOnly = new(() => Load(null, null));

    private readonly List<TestRow> _rows;

    private TestRegistry(List<TestRow> rows, List<TestRegistryLayer> layers, List<TestRegistryIssue> issues, string? repoDir)
    {
        _rows = rows;
        Layers = layers;
        Issues = issues;
        RepoDir = repoDir;
    }

    /// <summary>The directory of the repository overlay (the repository's own hierarchy directory); null when there is no repository.</summary>
    public string? RepoDir { get; }

    /// <summary>The ids of the repository overlay's rows that carry an environment or arguments for the test run.</summary>
    public IReadOnlyList<string> RepoAdapterRows => _rows.Where(static r => r.Layer == "repo" && (r.Env.Count > 0 || r.EnvAppend.Count > 0 || r.Args.Count > 0)).Select(static r => r.Id).ToList();

    /// <summary>The shipped rows alone, for callers that have no home or repository to read overlays from.</summary>
    public static TestRegistry Shipped => ShippedOnly.Value;

    /// <summary>The layers that were read, highest first.</summary>
    public IReadOnlyList<TestRegistryLayer> Layers { get; }

    /// <summary>The rows that were left out, and why.</summary>
    public IReadOnlyList<TestRegistryIssue> Issues { get; }

    /// <summary>Loads the three layers: the shipped rows, <c>&lt;home&gt;/.claude/ah2/test-commands.json</c> and <c>&lt;repoRoot&gt;/.claude/ah2/test-commands.json</c>.</summary>
    public static TestRegistry Load(string? home, string? repoRoot)
    {
        var issues = new List<TestRegistryIssue>();
        var shipped = Parse(ShippedText(), "shipped", "(shipped)", issues);
        var userPath = home is null ? null : Path.Join(home, ".claude", "ah2", "test-commands.json");
        var user = userPath is null ? [] : Parse(ReadOrNull(userPath), "user", userPath, issues);
        var repoPath = repoRoot is null ? null : Path.Join(repoRoot, ".claude", "ah2", "test-commands.json");
        var repo = repoPath is null ? [] : Parse(ReadOrNull(repoPath), "repo", repoPath, issues);

        var exemptIds = user.Where(static r => r.Exempt && r.Program is null).Select(static r => r.Id).ToHashSet(StringComparer.Ordinal);
        var shippedIds = shipped.Select(static r => r.Id).ToHashSet(StringComparer.Ordinal);
        var rows = new List<TestRow>();
        foreach (var r in repo)
        {
            if (r.Exempt)
            {
                issues.Add(new TestRegistryIssue(repoPath!, $"row {r.Id}: a repository overlay cannot exempt a command; ignored"));
            }
            else if (shippedIds.Contains(r.Id))
            {
                issues.Add(new TestRegistryIssue(repoPath!, $"row {r.Id}: the id is a shipped row's; a repository overlay can only add rows; ignored"));
            }
            else if (r.Program is null)
            {
                issues.Add(new TestRegistryIssue(repoPath!, $"row {r.Id}: no program; ignored"));
            }
            else
            {
                rows.Add(r);
            }
        }

        rows.AddRange(user.Where(static r => r.Program is not null));
        rows.AddRange(shipped.Select(r => exemptIds.Contains(r.Id) ? r with { Exempt = true } : r));
        foreach (var id in exemptIds.Where(id => !shippedIds.Contains(id)))
        {
            issues.Add(new TestRegistryIssue(userPath!, $"row {id}: exempt by id, but no shipped row has that id"));
        }

        var layers = new List<TestRegistryLayer>
        {
            new("repo", repoPath ?? "(none)", rows.Count(static r => r.Layer == "repo")),
            new("user", userPath ?? "(none)", rows.Count(static r => r.Layer == "user")),
            new("shipped", "(shipped)", shipped.Count),
        };
        return new TestRegistry(rows, layers, issues, repoRoot is null ? null : Path.GetDirectoryName(repoPath));
    }

    /// <summary>The registry for a session working in <paramref name="cwd"/>: the repository is the nearest directory above it that holds <c>.git</c>.</summary>
    public static TestRegistry For(string? home, string? cwd)
    {
        string? root = null;
        for (var dir = cwd; !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
        {
            if (Directory.Exists(Path.Join(dir, ".git")) || File.Exists(Path.Join(dir, ".git")))
            {
                root = dir;
                break;
            }
        }

        return Load(home, root);
    }

    /// <summary>Every command of <paramref name="command"/> that a non-exempt row matches.</summary>
    public IReadOnlyList<TestMatch> Matches(string? command) => MatchSegments(CommandSegments.Of(command), false);

    /// <summary>Every command of <paramref name="command"/> that a non-exempt row matches, including those already running under the test bucket.</summary>
    internal IReadOnlyList<TestMatch> MatchesIncludingBucketed(string? command) => MatchSegments(CommandSegments.Of(command), true);

    /// <summary>The commands of one argv that a non-exempt row matches.</summary>
    internal IReadOnlyList<TestMatch> MatchesOfWords(IReadOnlyList<string> argv) => MatchSegments(CommandSegments.OfWords(argv), true);

    /// <summary>Whether any command of <paramref name="command"/> is a test run.</summary>
    public bool IsTestRun(string? command) => Matches(command).Count > 0;

    /// <summary>
    /// What to do with the command <paramref name="argv"/> in a bucket of <paramref name="capacity"/> tokens that grants <paramref name="grant"/>:
    /// the row, its tokens, the row's environment and the argv with the row's arguments added where they are absent.
    /// </summary>
    public TestPlan Plan(IReadOnlyList<string> argv, int capacity, int? grant, Func<string, string?> env, bool repoTrusted = false)
    {
        ArgumentNullException.ThrowIfNull(argv);
        ArgumentNullException.ThrowIfNull(env);
        var matches = MatchSegments(CommandSegments.OfWords(argv), false);
        if (matches.Count == 0)
        {
            return new TestPlan(null, 0, [], null);
        }

        var tokens = matches.Max(m => m.Tokens(capacity));
        var k = Math.Max(1, grant ?? tokens).ToString(CultureInfo.InvariantCulture);
        var vars = new List<KeyValuePair<string, string>>();
        foreach (var m in matches.Where(m => repoTrusted || m.Row.Layer != "repo"))
        {
            foreach (var (name, value) in m.Row.Env)
            {
                if (!vars.Any(v => v.Key == name))
                {
                    vars.Add(new(name, value.Replace("{k}", k, StringComparison.Ordinal)));
                }
            }

            foreach (var (name, value) in m.Row.EnvAppend)
            {
                if (!vars.Any(v => v.Key == name))
                {
                    var current = env(name);
                    var add = value.Replace("{k}", k, StringComparison.Ordinal);
                    vars.Add(new(name, string.IsNullOrWhiteSpace(current) ? add : current + " " + add));
                }
            }
        }

        var top = matches.FirstOrDefault(m => !m.Segment.Nested && (repoTrusted || m.Row.Layer != "repo"));
        var adapted = top is null ? null : Adapt(argv, top, k);
        return new TestPlan(matches[0].Id, tokens, vars, adapted);
    }

    private static List<string>? Adapt(IReadOnlyList<string> argv, TestMatch m, string k)
    {
        var words = argv.ToList();
        var changed = false;
        foreach (var a in m.Row.Args)
        {
            var from = m.Segment.Program + m.Consumed;
            var rest = words.Skip(from).ToList();
            var text = string.Join(' ', rest);
            if (a.Unless is not null && rest.Any(w => Hit(a.Unless, w)))
            {
                continue;
            }

            if (a.Requires is not null && !Hit(a.Requires, text))
            {
                continue;
            }

            var add = a.Add.Select(x => x.Replace("{k}", k, StringComparison.Ordinal)).ToList();
            var dashes = rest.IndexOf("--");
            int at;
            switch (a.At)
            {
                case "start":
                    at = Math.Min(from + a.Skip, words.Count);
                    break;
                case "after_dashdash":
                    if (dashes < 0)
                    {
                        add.Insert(0, "--");
                    }

                    at = words.Count;
                    break;
                default:
                    at = dashes < 0 ? words.Count : from + dashes;
                    break;
            }

            words.InsertRange(at, add);
            changed = true;
        }

        return changed ? words : null;
    }

    private List<TestMatch> MatchSegments(IReadOnlyList<CommandSegment> segments, bool includeBucketed)
    {
        var found = new List<TestMatch>();
        foreach (var seg in segments)
        {
            if (seg.Program >= seg.Words.Count || (!includeBucketed && seg.Wrappers.Contains("bucket")))
            {
                continue;
            }

            var text = seg.ProgramName + (seg.Words.Count > seg.Program + 1 ? " " + string.Join(' ', seg.Rest) : string.Empty);
            foreach (var row in _rows)
            {
                if (row.Program is null || Prefix(row.Program, text) is not { } prefix)
                {
                    continue;
                }

                var rest = text[prefix.Length..].TrimStart();
                if (row.Match is not null && !Hit(row.Match, rest))
                {
                    continue;
                }

                if (!row.Exempt)
                {
                    found.Add(new TestMatch(row, seg, prefix.Trim().Split(' ').Length, row.LightIf is not null && Hit(row.LightIf, rest)));
                }

                break;
            }
        }

        return found;
    }

    private static bool Hit(Regex pattern, string text)
    {
        try
        {
            return pattern.IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary>The leading part of <paramref name="text"/> that the program pattern matches as whole words, or null.</summary>
    private static string? Prefix(Regex program, string text)
    {
        try
        {
            var m = program.Match(text);
            return m.Success ? m.Value : null;
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    private static string? ShippedText()
    {
        using var stream = typeof(TestRegistry).Assembly.GetManifestResourceStream(ResourceName);
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string? ReadOrNull(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length <= 1 << 20 ? File.ReadAllText(path) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static List<TestRow> Parse(string? text, string layer, string file, List<TestRegistryIssue> issues)
    {
        var rows = new List<TestRow>();
        if (text is null)
        {
            return rows;
        }

        JsonValue doc;
        try
        {
            doc = JsonText.Parse(text);
        }
        catch (JsonParseException e)
        {
            issues.Add(new TestRegistryIssue(file, "not valid JSON: " + e.Message));
            return rows;
        }

        if (doc.Member("rows") is not { IsArray: true } array)
        {
            issues.Add(new TestRegistryIssue(file, "no \"rows\" array"));
            return rows;
        }

        foreach (var item in array.AsArray())
        {
            if (!item.IsMap)
            {
                issues.Add(new TestRegistryIssue(file, "a row that is not an object"));
                continue;
            }

            try
            {
                rows.Add(Row(item.AsObject(), layer));
            }
            catch (Exception e) when (e is FormatException or ArgumentException)
            {
                issues.Add(new TestRegistryIssue(file, $"row {item.Member("id")?.NonEmptyText ?? "?"}: {e.Message}"));
            }
        }

        return rows;
    }

    private static TestRow Row(JsonObject o, string layer)
    {
        var id = o.Get("id")?.NonEmptyText ?? throw new FormatException("no id");
        var exempt = o.Get("exempt") is { IsBool: true } e && e.AsBool();
        var program = o.Get("program")?.NonEmptyText;
        var tokens = "half";
        if (o.Get("tokens") is { } t)
        {
            tokens = t.IsNumber ? ((int)t.AsNumber()).ToString(CultureInfo.InvariantCulture) : t.IsText ? t.AsString() : throw new FormatException("tokens is not \"half\" or a number");
            if (tokens != "half" && !(int.TryParse(tokens, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= 1))
            {
                throw new FormatException("tokens must be \"half\" or a whole number of at least 1");
            }
        }

        var adapter = o.Get("adapter") is { IsMap: true } a ? a.AsObject() : null;
        return new TestRow(
            id,
            layer,
            program is null ? null : Compile("^(?:" + program + ")(?=\\s|\\z)"),
            o.Get("match")?.NonEmptyText is { } m ? Compile(m) : null,
            tokens,
            o.Get("light_if")?.NonEmptyText is { } l ? Compile(l) : null,
            Pairs(adapter?.Get("env")),
            Pairs(adapter?.Get("env_append")),
            ArgRules(adapter?.Get("args")),
            exempt);
    }

    private static Regex Compile(string pattern) => new(pattern, RegexOptions.CultureInvariant, RegexTimeout);

    private static List<KeyValuePair<string, string>> Pairs(JsonValue? v)
    {
        var list = new List<KeyValuePair<string, string>>();
        if (v is { IsMap: true } map)
        {
            foreach (var key in map.AsObject().Keys)
            {
                list.Add(new(key, map.AsObject()[key].IsText ? map.AsObject()[key].AsString() : throw new FormatException($"env {key} is not text")));
            }
        }

        return list;
    }

    private static List<TestAdapterArgs> ArgRules(JsonValue? v)
    {
        var list = new List<TestAdapterArgs>();
        if (v is not { IsArray: true } array)
        {
            return list;
        }

        foreach (var item in array.AsArray())
        {
            if (!item.IsMap)
            {
                throw new FormatException("an adapter args entry that is not an object");
            }

            var o = item.AsObject();
            var add = o.Get("add") is { IsArray: true } adds ? adds.AsArray().Where(static x => x.IsText).Select(static x => x.AsString()).ToList() : throw new FormatException("an adapter args entry with no add list");
            var at = o.Get("at")?.NonEmptyText ?? "end";
            if (at is not ("start" or "end" or "after_dashdash"))
            {
                throw new FormatException($"adapter position {at} is not start, end or after_dashdash");
            }

            list.Add(new TestAdapterArgs(
                o.Get("unless")?.NonEmptyText is { } u ? Compile(u) : null,
                o.Get("requires")?.NonEmptyText is { } r ? Compile(r) : null,
                add,
                at,
                o.Get("skip") is { IsNumber: true } s ? (int)s.AsNumber() : 0));
        }

        return list;
    }
}
