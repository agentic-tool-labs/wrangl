using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Commands;
using Ah.Config;
using Ah.Core.Json;
using Ah.Hierarchy;
using Ah.Launch;
using Ah.Mesh;
using Ah.Platform;
using Ah.Roster;
using Ah.TestKit;

namespace Ah.Cli.Tests;

/// <summary>
/// Branch hygiene (spec branch-hygiene §6) over a real git repository: golden resolution, <c>stream-open</c>'s default base and
/// <c>stream-done</c>'s unshipped-commits gate and archive. Every git command the verbs run is real; anything else (herdr) fails.
/// Each test pins one row.
/// </summary>
[Collection("RosterServices")]
[UnsupportedOSPlatform("windows")]
public sealed class BranchHygieneTests : IDisposable
{
    private readonly Sandbox _sandbox = new();
    private readonly List<string[]> _calls = [];

    /// <summary>Runs after each real git command; a test uses it to change the store mid-verb.</summary>
    private Action<string[]>? _afterGit;

    /// <summary>When set, the session is on herdr and <c>herdr tab</c> list, create and close are faked.</summary>
    private bool _herdr;

    public BranchHygieneTests()
    {
        Directory.CreateDirectory(Path.Combine(Hier, "teams"));
        Git(Project, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(Project, ".gitignore"), "bin/\nobj/\nTestResults/\n.claude/\n");
        Git(Project, "add", ".gitignore");
        Git(Project, "commit", "-qm", "base");
    }

    private string Project => Path.Combine(_sandbox.Root, "proj");

    private string Hier => Path.Combine(Project, ".claude", "ah2");

    private string TeamFile => Path.Combine(Hier, "teams", "proj.json");

    private static string Today => DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public void Dispose()
    {
        CommandsModule.ComposeRoster(() => null!);
        _sandbox.Dispose();
    }

    // ---------------------------------------------------------------- harness

    private static Dictionary<string, string?> GitEnvironment()
    {
        var environment = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .Where(entry => !((string)entry.Key).StartsWith("GIT_", StringComparison.Ordinal))
            .ToDictionary(entry => (string)entry.Key, entry => (string?)entry.Value);
        environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
        environment["GIT_CONFIG_SYSTEM"] = "/dev/null";
        return environment;
    }

    /// <summary>When this test process started, as the scripted <c>ps</c> says and the team's Orchestrator record names (the owner proof).</summary>
    private const string SelfStart = "2026-10-15T20:38:50.000Z";

    /// <summary>
    /// Real git for every <c>git</c> (or <c>env … git</c>) argv; <c>ps -o lstart=</c> of this process answers <see cref="SelfStart"/>;
    /// anything else fails as an absent tool would.
    /// </summary>
    private sealed class RealGit(List<string[]> calls, string sandboxRoot, Action<string[]>? afterGit = null, bool herdr = false) : IProcessRunner
    {
        private readonly IProcessRunner _real = PlatformModule.CreateProcessRunner();

        /// <summary>The <c>sh -c</c> script that makes this runner throw, as a runner that fails would.</summary>
        internal const string ThrowingScript = "the runner throws";

        /// <summary>The tab a faked <c>herdr tab create</c> answers.</summary>
        internal const string CreatedTab = "t-new";

        public ProcessResult Run(ProcessRequest request)
        {
            var argv = request.Argv.ToArray();
            calls.Add(argv);
            if (herdr && argv is ["herdr", "tab", var op, ..])
            {
                var answer = op switch
                {
                    "list" => "{\"result\":{\"tabs\":[]}}",
                    "create" => $"{{\"result\":{{\"tab\":{{\"tab_id\":\"{CreatedTab}\"}},\"root_pane\":{{\"pane_id\":\"p-new\"}}}}}}",
                    _ => "{}",
                };
                return new ProcessResult(0, null, false, false, Encoding.UTF8.GetBytes(answer), default);
            }

            if (argv.Contains("lstart=") && argv.Contains(Environment.ProcessId.ToString(CultureInfo.InvariantCulture)))
            {
                return new ProcessResult(0, null, false, false, Encoding.UTF8.GetBytes(SelfStart + "\n"), default);
            }

            // a stream_setup command: only sh -c, only in a work tree under this sandbox, with the environment stream-open gave it
            if (argv is ["sh", "-c", var script] && request.WorkingDirectory is { } cwd
                && (cwd.StartsWith(sandboxRoot + "/", StringComparison.Ordinal) || cwd.StartsWith("/private" + sandboxRoot + "/", StringComparison.Ordinal)))
            {
                return script == ThrowingScript ? throw new IOException("the runner failed") : _real.Run(request);
            }

            var isGit = argv.Length > 0 && (argv[0] == "git" || (argv[0] == "env" && argv.Contains("git")));
            if (!isGit)
            {
                return new ProcessResult(1, null, false, false, default, Encoding.UTF8.GetBytes("not available in this test"));
            }

            var result = _real.Run(request with { Environment = GitEnvironment() });
            afterGit?.Invoke(argv);
            return result;
        }
    }

    private sealed class Services(ServiceGraph graph, IFileReader files, IClock clock, IAhEnvironment environment, string cwd) : IRosterCliServices
    {
        public IConfigCore Config => graph.Config;

        public IRosterCore Roster => graph.Roster;

        public HierarchyServices Hierarchy => graph.Hierarchy;

        public IAhEnvironment Environment => environment;

        public IClock Clock => clock;

        public IFileReader Files => files;

        public string CurrentDirectory => cwd;

        public int ParentPid => 4242;

        public ITeardown? Teardown => graph.Launch.Teardown;

        public IRosterLaunch? Launch => graph.Launch;

        public IRosterCallerRegistry? Callers => null;

        public IRosterOwnerProof? OwnerProof { get; } = new AllowingProof();

        public IHomeDocs? HomeDocs => graph.HomeDocs;

        public bool LocalOnly(string cwd, string? team) => RulesReads.LocalOnly(graph, environment.AhHome, cwd, team);
    }

    private sealed class AllowingProof : IRosterOwnerProof
    {
        public OwnerProofResult Check(string dir, string? team) => new(true, null, "orchestrator", false, null);

        public OwnerActor Actor() => new(4242, "Thu Oct 15 20:38:50 2026", [], "ah roster gate");

        public NearestSession NearestAncestor(string dir, IReadOnlyList<string> rowDirs) => new(NearestKind.Session, null, null, Environment.ProcessId);
    }

    private (ServiceGraph Graph, PlatformIo Io, FakeEnv Env) Build()
    {
        var variables = new Dictionary<string, string?>
        {
            ["AH_TEST"] = "1",
            ["AH_HOME"] = _sandbox.AhHome,
            ["HOME"] = _sandbox.Home,
            ["PATH"] = _sandbox.Bin,
            ["WRANGL_HIERARCHY_DIR"] = Hier,
        };
        if (_herdr)
        {
            variables["HERDR_ENV"] = "1";
        }

        var env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables, new PluginInfo(_sandbox.PluginRoot, "wrangl", "1.2.3", "1"));
        var io = PlatformModule.CreateIo(null);
        return (ServiceGraph.Build(io, env, new RealGit(_calls, _sandbox.Root, _afterGit, _herdr), new FakeDaemonClient()), io, env);
    }

    /// <summary>Writes a <c>local-only</c> rule row at <paramref name="level"/>/<paramref name="scope"/> into the sandbox's state database.</summary>
    private void LocalOnlyRule(string level, string scope)
    {
        var (graph, _, _) = Build();
        using var db = Ah.StateDb.StateDbModule.Open(_sandbox.AhHome, mayCreate: true);
        RuleWriteResult? written = null;
        var outcome = db.WriteOnce(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow.AddSeconds(10), tx =>
        {
            written = RulesOfEngagement.Apply(
                (sql, a) => tx.Query(sql, a),
                (sql, a) => tx.Execute(sql, a),
                new RuleWrite("add", Ah.Cli.Verbs.BacklogVerb.DirOf(graph, Project), level, scope, RulesOfEngagement.LocalOnlyKey, "commits local only, never push", "user", null, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), []));
            return string.Empty;
        });
        Assert.Equal(Ah.StateDb.OnceStatus.Applied, outcome.Status);
        Assert.Equal(RulesOfEngagement.Ok, written!.Code);
    }

    private (int Code, string Stdout, string Stderr) Roster(params string[] args)
    {
        var (graph, io, env) = Build();
        var services = new Services(graph, io.Files, io.Clock, env, Project);
        CommandsModule.ComposeRoster(() => services);
        var verb = CommandsModule.Registry.Verbs.Single(v => v.Name == "roster");
        var stdout = new StringWriter(CultureInfo.InvariantCulture);
        var stderr = new StringWriter(CultureInfo.InvariantCulture);
        // stream-list reads every team and takes no --team
        string[] scope = args is ["stream-list", ..] ? ["--cwd", Project] : ["--cwd", Project, "--team", "proj"];
        var code = verb.Run([.. args, .. scope], stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private static JsonObject Parse(string text) => JsonText.Parse(Encoding.UTF8.GetBytes(text)).AsObject();

    private static string Git(string dir, params string[] args)
    {
        var result = PlatformModule.CreateProcessRunner().Run(new ProcessRequest(
            ["git", "-C", dir, "-c", "core.hooksPath=/dev/null", "-c", "user.name=Hygiene Test", "-c", "user.email=hygiene@example.invalid", "-c", "commit.gpgSign=false", .. args],
            Environment: GitEnvironment(),
            Timeout: TimeSpan.FromSeconds(20)));
        Assert.True(result.ExitCode == 0, $"git {string.Join(' ', args)}: {Encoding.UTF8.GetString(result.Stderr.Span)}");
        return Encoding.UTF8.GetString(result.Stdout.Span).Trim();
    }

    private static string Commit(string dir, string file, string content, string message)
    {
        File.WriteAllText(Path.Combine(dir, file), content);
        Git(dir, "add", file);
        Git(dir, "commit", "-qm", message);
        return Git(dir, "rev-parse", "HEAD");
    }

    /// <summary>
    /// Ships <paramref name="sha"/> by cherry-pick onto main, and moves origin/main there. <c>-x</c> changes the message, so the
    /// shipped commit always has a new sha and the same patch-id (a plain pick in the same second can reproduce the same sha).
    /// </summary>
    private void Ship(string sha)
    {
        Git(Project, "cherry-pick", "-x", sha);
        Assert.NotEqual(sha, Git(Project, "rev-parse", "HEAD"));
        OriginMain();
    }

    private void OriginMain() => Git(Project, "update-ref", "refs/remotes/origin/main", "refs/heads/main");

    /// <summary>No golden at all: no origin refs, and the trunk is called neither main nor master.</summary>
    private void NoGolden() => Git(Project, "branch", "-m", "main", "trunk");

    private string AddWorktree(string name, string branch, string from = "HEAD")
    {
        var path = Path.Combine(Project, ".claude", "worktrees", name);
        Git(Project, "worktree", "add", "-q", "-b", branch, path, from);
        return path;
    }

    private static string Stream(string name, string? branch, string? worktree, string state = "open", string extra = "") =>
        $"\"{name}\":{{\"state\":\"{state}\",\"branch\":{Js(branch)},\"base\":\"main\",\"worktree\":{Js(worktree)},\"tab_id\":null,\"opened\":\"2026-10-29T10:00:00-04:00\"{extra}}}";

    private static string Js(string? text) => text is null ? "null" : JsonText.Stringify(JsonValue.FromString(text), 0);

    private void WriteTeam(string streams, string members = "[]", string file = "proj")
    {
        _sandbox.PlantTeam(
            Path.Combine(Hier, "teams", file + ".json"),
            "{\"version\":1,\"team_id\":\"t-" + file + "\",\"transport\":\"tmux\",\"orchestrator\":{\"session_id\":null,\"pid\":" + Environment.ProcessId + ",\"pid_start\":\"" + SelfStart + "\"},"
            + "\"members\":" + members + ",\"streams\":{" + streams + "}}");
    }

    private static string Member(string name, string stream) => $"{{\"name\":\"{name}\",\"role\":\"implementor\",\"route\":\"peer\",\"stream\":\"{stream}\"}}";

    /// <summary>A <c>down</c> row for <paramref name="name"/> whose pid is not running: the member reads proven dead.</summary>
    private void Down(string name) =>
        _sandbox.PlantPeers(
            Hier,
            "{\"type\":\"peer\",\"name\":\"" + name + "\",\"role\":\"implementor\",\"status\":\"down\",\"session_id\":\"s-" + name
            + "\",\"pid\":999999,\"pid_start\":\"Mon Jun  1 11:00:00 2026\",\"ts\":\"2026-06-01T11:50:00.000Z\"}\n");

    private JsonObject StreamRecord(string name) => Parse(StoredTeam())["streams"].AsObject()[name].AsObject();

    /// <summary>A graph over this sandbox's home, to read what the store holds.</summary>
    private ServiceGraph StoreGraph()
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        return ServiceGraph.Build(
            PlatformModule.CreateIo(null), new FakeEnv(_sandbox.Home, _sandbox.AhHome, new Dictionary<string, string?> { ["HOME"] = _sandbox.Home, ["AH_HOME"] = _sandbox.AhHome }), new FakeRunner(), new FakeDaemonClient());
    }

    /// <summary>The team record the store holds at <see cref="TeamFile"/>, compact.</summary>
    private string StoredTeam() => JsonText.Stringify(StoreGraph().Roster.ReadTeamFileAt(TeamFile), 0);

    /// <summary>The digest of that record: what a refusal must leave as it was.</summary>
    private string TeamDigest() => StoreGraph().Roster.ReadTeamWithDigest(TeamFile)["digest"].AsString();

    private string Refs() => Git(Project, "for-each-ref", "--format=%(refname) %(objectname)");

    private bool HasRef(string fullRef) => Refs().Split('\n').Any(l => l.StartsWith(fullRef + " ", StringComparison.Ordinal));

    private int LedgerLines() =>
        Directory.EnumerateFiles(Hier, "*", SearchOption.AllDirectories)
            .Where(f => Path.GetFileName(f).Contains("ledger", StringComparison.Ordinal))
            .Sum(f => File.ReadAllLines(f).Length);

    private static string? Refused(string stdout, string stderr)
    {
        foreach (var text in new[] { stdout, stderr })
        {
            var at = text.IndexOf('{', StringComparison.Ordinal);
            if (at >= 0)
            {
                try
                {
                    if (Parse(text[at..]).Get("refused") is { IsText: true } code)
                    {
                        return code.AsString();
                    }
                }
                catch (FormatException)
                {
                }
            }
        }

        return null;
    }

    private static List<string> Texts(JsonValue? array) => array is { IsArray: true } a ? a.AsArray().Select(v => v.IsText ? v.AsString() : JsonText.Stringify(v, 0)).ToList() : [];

    /// <summary>A feature stream <c>feat</c> on its own worktree with one commit, optionally shipped; origin/main is set.</summary>
    private (string Tree, string Sha) FeatureStream(bool shipped, string members = "[]")
    {
        var tree = AddWorktree("feat", "feat");
        var sha = Commit(tree, "feat.txt", "feature\n", "feat: add feature");
        if (shipped)
        {
            Ship(sha);
        }
        else
        {
            OriginMain();
        }

        WriteTeam(Stream("feat", "feat", tree), members);
        return (tree, sha);
    }

    /// <summary>Asserts a refusal changed nothing: team file bytes, ledger lines and every ref.</summary>
    private void AssertUnchanged(Action act)
    {
        var bytes = TeamDigest();
        var ledger = LedgerLines();
        var refs = Refs();
        act();
        Assert.Equal(bytes, TeamDigest());
        Assert.Equal(ledger, LedgerLines());
        Assert.Equal(refs, Refs());
    }

    private string OpenBase()
    {
        WriteTeam(string.Empty);
        var (code, stdout, stderr) = Roster("stream-open", "fresh", "--dry-run");
        Assert.True(code == 0, stderr);
        var result = Parse(stdout);
        return result["stream"].Member("base")!.Value.AsString() + " " + result["base"].AsString().Split('(', 2)[1].TrimEnd(')');
    }

    // ---------------------------------------------------------------- golden (§2)

    [Fact]
    public void GoldenIsOriginHeadsTargetBeforeOriginMain()
    {
        OriginMain();
        Git(Project, "update-ref", "refs/remotes/origin/develop", "refs/heads/main");
        Git(Project, "symbolic-ref", "refs/remotes/origin/HEAD", "refs/remotes/origin/develop");

        Assert.Equal("origin/develop golden", OpenBase());
    }

    [Fact]
    public void WithNoOriginHeadOriginMasterComesBeforeLocalMain()
    {
        Git(Project, "update-ref", "refs/remotes/origin/master", "refs/heads/main");

        Assert.Equal("origin/master golden", OpenBase());
    }

    [Fact]
    public void ALocalOnlyRepoWithMainHasGoldenMain()
    {
        Assert.Equal("main golden", OpenBase());
    }

    [Fact]
    public void AConfiguredReleaseRefWinsOverOriginHead()
    {
        OriginMain();
        Git(Project, "symbolic-ref", "refs/remotes/origin/HEAD", "refs/remotes/origin/main");
        Git(Project, "branch", "rel", "main");
        Git(Project, "config", "ah.releaseRef", "refs/heads/rel");

        Assert.Equal("refs/heads/rel golden", OpenBase());
    }

    [Fact]
    public void WithGoldenUnresolvedStreamOpenKeepsTheCheckedOutBranchRule()
    {
        NoGolden();

        Assert.Equal("trunk checked-out branch", OpenBase());
        var (_, stdout, _) = Roster("stream-open", "fresh", "--dry-run");
        Assert.Contains(Texts(Parse(stdout)["warnings"]), w => w.StartsWith("note: no --base given; the base is the checked-out branch trunk", StringComparison.Ordinal));
    }

    [Fact]
    public void StreamOpenWithAnExplicitBaseKeepsIt()
    {
        OriginMain();
        Git(Project, "branch", "foo", "main");
        WriteTeam(string.Empty);

        var (code, stdout, stderr) = Roster("stream-open", "fresh", "--base", "foo", "--dry-run");

        Assert.True(code == 0, stderr);
        Assert.Equal("foo", Parse(stdout)["stream"].Member("base")!.Value.AsString());
    }

    [Fact]
    public void AdoptingAnExistingBranchKeepsTheNamedBaseAtItsTipAndSaysTheBaseWasIgnored()
    {
        OriginMain();
        Git(Project, "checkout", "-qb", "adopted", "main");
        Git(Project, "commit", "-q", "--allow-empty", "-m", "ahead of main");
        var tip = Git(Project, "rev-parse", "HEAD").Trim();
        Git(Project, "checkout", "-q", "main");
        WriteTeam(string.Empty);

        var (code, stdout, stderr) = Roster("stream-open", "fresh", "--branch", "adopted", "--base", "main", "--dry-run");
        var (newCode, newOut, newErr) = Roster("stream-open", "fresh", "--branch", "brand-new", "--dry-run");

        Assert.True(code == 0, stderr);
        var adopted = Parse(stdout);
        Assert.Equal("main", adopted["stream"].Member("base")!.Value.AsString());
        Assert.Equal(tip, adopted["stream"].Member("base_sha")!.Value.AsString());
        Assert.EndsWith("(existing branch)", adopted["base"].AsString(), StringComparison.Ordinal);
        Assert.Contains($"branch adopted already existed at {tip[..7]}: up to date with main; --base main ignored", Texts(adopted["warnings"]));

        // negative: a new branch is cut from the golden default, with no adoption warning and no checked-out-branch note
        Assert.True(newCode == 0, newErr);
        var fresh = Parse(newOut);
        Assert.Equal("origin/main", fresh["stream"].Member("base")!.Value.AsString());
        Assert.DoesNotContain(Texts(fresh.Get("warnings")), w => w.StartsWith("branch ", StringComparison.Ordinal) || w.StartsWith("note:", StringComparison.Ordinal));
    }

    [Fact]
    public void AdoptingABranchOneCommitBehindMainSaysSoWithNoBaseGiven()
    {
        Git(Project, "branch", "stale", "main");
        var tip = Git(Project, "rev-parse", "stale").Trim();
        Git(Project, "commit", "-q", "--allow-empty", "-m", "main moves on");
        WriteTeam(string.Empty);

        var (code, stdout, stderr) = Roster("stream-open", "fresh", "--branch", "stale", "--dry-run");

        Assert.True(code == 0, stderr);
        var warnings = Texts(Parse(stdout)["warnings"]);
        Assert.Contains($"branch stale already existed at {tip[..7]}: 1 commit behind main", warnings);
        // negative: no --base was passed, so nothing says one was ignored
        Assert.DoesNotContain(warnings, w => w.Contains("ignored", StringComparison.Ordinal));
    }

    [Fact]
    public void AdoptingABranchCountsBehindTheRemoteBaseWhenOnlyItExists()
    {
        Git(Project, "branch", "stale", "main");
        var tip = Git(Project, "rev-parse", "stale").Trim();
        Git(Project, "commit", "-q", "--allow-empty", "-m", "main moves on");
        OriginMain();
        NoGolden();
        WriteTeam(string.Empty);

        var (code, stdout, stderr) = Roster("stream-open", "fresh", "--branch", "stale", "--base", "main", "--dry-run");
        var (otherCode, otherOut, otherErr) = Roster("stream-open", "other", "--branch", "stale", "--base", "nowhere", "--dry-run");

        Assert.True(code == 0, stderr);
        Assert.Contains($"branch stale already existed at {tip[..7]}: 1 commit behind origin/main; --base main ignored", Texts(Parse(stdout)["warnings"]));
        // negative: a base that resolves to nothing still warns, without a count, and is not refused
        Assert.True(otherCode == 0, otherErr);
        Assert.Contains($"branch stale already existed at {tip[..7]}; --base nowhere ignored", Texts(Parse(otherOut)["warnings"]));
    }

    [Fact]
    public void AnUnresolvableReleaseRefRefusesOpenAndDoneEvenWithAFlag()
    {
        FeatureStream(shipped: true);
        Git(Project, "config", "ah.releaseRef", "origin/nope");

        AssertUnchanged(() =>
        {
            var (_, doneOut, doneErr) = Roster("stream-done", "feat", "--abandon", "dropped");
            Assert.Equal("release-ref-invalid", Refused(doneOut, doneErr));
            var (_, openOut, openErr) = Roster("stream-open", "other");
            Assert.Equal("release-ref-invalid", Refused(openOut, openErr));
        });
    }

    // ---------------------------------------------------------------- a failed stream-open leaves nothing behind (ah2-523)

    /// <summary>Clears the team record right after <c>git worktree add</c>, so the record write that follows is refused; runs <paramref name="also"/> then too.</summary>
    private void RefuseRecordAfterAdd(Action<string[]>? also = null) =>
        _afterGit = argv =>
        {
            if (argv.Contains("worktree") && argv.Contains("add"))
            {
                _sandbox.ClearTeam(TeamFile);
                also?.Invoke(argv);
            }
        };

    [Fact]
    public void ARefusedRecordWriteLeavesNoWorkTreeBranchOrTab()
    {
        // a done stream whose tree, branch and tab are gone: the reopen makes all three before it writes the record
        var tree = Path.Combine(Project, ".claude", "worktrees", "feat");
        _sandbox.PlantTeam(
            TeamFile,
            "{\"version\":1,\"team_id\":\"t-proj\",\"transport\":\"herdr\",\"orchestrator\":{\"session_id\":null,\"pid\":" + Environment.ProcessId + ",\"pid_start\":\"" + SelfStart + "\"},"
            + "\"members\":[],\"streams\":{\"feat\":{\"state\":\"done\",\"branch\":\"feat\",\"base\":\"main\",\"worktree\":" + Js(tree) + ",\"tab_id\":\"t-gone\",\"opened\":\"2026-10-29T10:00:00-04:00\"}}}");
        _herdr = true;
        RefuseRecordAfterAdd();
        var refs = Refs();

        var (code, stdout, stderr) = Roster("stream-open", "feat");

        Assert.NotEqual(0, code);
        Assert.Contains("was not recorded", stdout + stderr, StringComparison.Ordinal);
        Assert.Contains(_calls, a => a is ["git", .., "worktree", "add", "-b", "feat", _, _]);
        Assert.Contains(_calls, a => a is ["herdr", "tab", "create", ..]);
        Assert.Contains(_calls, a => a is ["herdr", "tab", "close", RealGit.CreatedTab]);
        Assert.False(Directory.Exists(tree));
        Assert.False(HasRef("refs/heads/feat"));
        Assert.Equal(refs, Refs());
        Assert.DoesNotContain("left behind", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void ARefusedRecordWriteKeepsABranchThatExistedBefore()
    {
        WriteTeam(string.Empty);
        Git(Project, "branch", "adopted");
        var tip = Git(Project, "rev-parse", "refs/heads/adopted");
        RefuseRecordAfterAdd();

        var (code, stdout, stderr) = Roster("stream-open", "fresh", "--branch", "adopted", "--base", "main");

        Assert.NotEqual(0, code);
        Assert.Contains("was not recorded", stdout + stderr, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(Project, ".claude", "worktrees", "fresh")));
        Assert.Equal(tip, Git(Project, "rev-parse", "refs/heads/adopted"));
        Assert.DoesNotContain(_calls, a => a.Contains("update-ref") || a.Contains("tab"));
        Assert.DoesNotContain("left behind", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void ADirtyNewWorkTreeIsNotForcedAndIsNamedWithItsBranch()
    {
        WriteTeam(string.Empty);
        var tree = Path.Combine(Project, ".claude", "worktrees", "fresh");
        RefuseRecordAfterAdd(_ => File.WriteAllText(Path.Combine(tree, "stray.txt"), "untracked\n"));

        var (code, _, stderr) = Roster("stream-open", "fresh", "--base", "main");

        Assert.NotEqual(0, code);
        Assert.DoesNotContain(_calls, a => a.Contains("--force") || a.Contains("-f"));
        Assert.True(File.Exists(Path.Combine(tree, "stray.txt")));
        Assert.True(HasRef("refs/heads/fresh"));
        Assert.Contains($"left behind: work tree {tree} (", stderr, StringComparison.Ordinal);
        Assert.Contains("; branch fresh in ", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUndoStepThatThrowsIsNamedAndTheOriginalErrorStillReports()
    {
        WriteTeam(string.Empty);
        var tree = Path.Combine(Project, ".claude", "worktrees", "fresh");
        RefuseRecordAfterAdd();
        var refuse = _afterGit!;
        _afterGit = argv =>
        {
            refuse(argv);
            if (argv.Contains("worktree") && argv.Contains("remove"))
            {
                throw new IOException("undo threw");
            }
        };

        var (code, stdout, stderr) = Roster("stream-open", "fresh", "--base", "main");

        Assert.NotEqual(0, code);
        // the original refusal, not the undo's own "the stream was not recorded, and undoing it left behind" line
        Assert.Contains("was not recorded", (stdout + stderr).Replace("the stream was not recorded, and undoing it", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains($"left behind: work tree {tree} (undo threw); branch fresh in ", stderr, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- the unshipped count (§3.2, §7 must-fail)

    [Fact]
    public void ACherryPickedCommitReadsShipped()
    {
        FeatureStream(shipped: true);

        var (code, stdout, stderr) = Roster("stream-done", "feat", "--dry-run");

        Assert.True(code == 0, stderr);
        Assert.Equal("shipped", Parse(stdout)["verdict"].AsString());
        Assert.Equal(0, (int)Parse(stdout)["unshipped"].AsNumber());
    }

    [Fact]
    public void TheSameCommitAmendedReadsOneUnshipped()
    {
        var (tree, _) = FeatureStream(shipped: true);
        File.WriteAllText(Path.Combine(tree, "feat.txt"), "feature, amended\n");
        Git(tree, "add", "feat.txt");
        Git(tree, "commit", "-q", "--amend", "-m", "feat: add feature");

        var (_, stdout, stderr) = Roster("stream-done", "feat");

        Assert.Equal("unshipped-commits", Refused(stdout, stderr));
        Assert.Contains("\"unshipped\":1", (stdout + stderr).Replace(" ", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public void CatchUpMergeCommitsNeverCountSoTheStreamIsShipped()
    {
        var (tree, _) = FeatureStream(shipped: true);
        Commit(Project, "other.txt", "other\n", "main: other work");
        OriginMain();
        Git(tree, "merge", "-q", "--no-edit", "-X", "theirs", "main");

        var (code, stdout, stderr) = Roster("stream-done", "feat");

        Assert.True(code == 0, stderr);
        Assert.Equal("shipped", Parse(stdout)["verdict"].AsString());
    }

    [Fact]
    public void AfterACatchUpMergeACommitNotInGoldenStillCounts()
    {
        var (tree, _) = FeatureStream(shipped: true);
        Commit(tree, "two.txt", "two\n", "feat: unshipped two");
        Commit(Project, "other.txt", "other\n", "main: other work");
        OriginMain();
        Git(tree, "merge", "-q", "--no-edit", "-X", "theirs", "main");

        var (_, stdout, stderr) = Roster("stream-done", "feat");

        Assert.Equal("unshipped-commits", Refused(stdout, stderr));
        Assert.Contains("feat: unshipped two", stdout + stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("feat: add feature", stdout + stderr, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- stream-done (§3.2)

    [Fact]
    public void WithGoldenUnresolvedStreamDoneIsAsTodayVerdictUncheckedAndArchivesNothing()
    {
        NoGolden();
        var tree = AddWorktree("feat", "feat");
        Commit(tree, "feat.txt", "x\n", "feat: unshipped");
        File.WriteAllText(Path.Combine(tree, "notes.txt"), "dirty is not refused when unchecked\n");
        WriteTeam(Stream("feat", "feat", tree));

        var (code, stdout, stderr) = Roster("stream-done", "feat");

        Assert.True(code == 0, stderr);
        var result = Parse(stdout);
        Assert.Contains(Texts(result.Get("warnings")), w => w.StartsWith("golden-unresolved", StringComparison.Ordinal));
        Assert.Equal("unchecked", result["verdict"].AsString());
        Assert.Equal("unchecked", StreamRecord("feat")["verdict"].AsString());
        Assert.True(HasRef("refs/heads/feat"));
        Assert.DoesNotContain("refs/archive/", Refs(), StringComparison.Ordinal);
        Assert.True(Directory.Exists(tree));
    }

    [Fact]
    public void WithGoldenUnresolvedAnAbandonIsRecordedAndArchived()
    {
        NoGolden();
        var tree = AddWorktree("feat", "feat");
        Commit(tree, "feat.txt", "x\n", "feat: dropped");
        WriteTeam(Stream("feat", "feat", tree));

        var (code, stdout, stderr) = Roster("stream-done", "feat", "--abandon", "superseded by v2");

        Assert.True(code == 0, stderr);
        Assert.Equal("abandoned", Parse(stdout)["verdict"].AsString());
        Assert.Equal($"refs/archive/{Today}/feat", Parse(stdout)["archived_ref"].AsString());
        Assert.False(HasRef("refs/heads/feat"));
    }

    [Fact]
    public void WithGoldenUnresolvedMergedIntoAGoldenLikeNameThatIsNoLiveStreamIsInvalid()
    {
        NoGolden();
        var tree = AddWorktree("feat", "feat");
        Commit(tree, "feat.txt", "x\n", "feat: one");
        Git(Project, "merge", "-q", "--ff-only", "feat");
        WriteTeam(Stream("feat", "feat", tree));

        AssertUnchanged(() =>
        {
            var (_, stdout, stderr) = Roster("stream-done", "feat", "--merged-into", "trunk");
            Assert.Equal("merged-into-invalid", Refused(stdout, stderr));
        });
    }

    [Fact]
    public void ANoWorktreeStreamIsVerdictNoBranchAndArchivesNothing()
    {
        OriginMain();
        WriteTeam(Stream("talk", null, null));
        var refs = Refs();

        var (code, stdout, stderr) = Roster("stream-done", "talk");

        Assert.True(code == 0, stderr);
        Assert.Equal("no-branch", Parse(stdout)["verdict"].AsString());
        Assert.Equal(refs, Refs());
    }

    [Fact]
    public void ABranchDeletedBeforeDoneIsVerdictBranchMissingWithAWarning()
    {
        OriginMain();
        WriteTeam(Stream("feat", "feat", Path.Combine(Project, ".claude", "worktrees", "feat")));

        var (code, stdout, stderr) = Roster("stream-done", "feat");

        Assert.True(code == 0, stderr);
        var result = Parse(stdout);
        Assert.Equal("branch-missing", result["verdict"].AsString());
        Assert.Contains(Texts(result.Get("warnings")), w => w.StartsWith("branch-missing", StringComparison.Ordinal));
        Assert.DoesNotContain("refs/archive/", Refs(), StringComparison.Ordinal);
    }

    [Fact]
    public void ShippedWithAMemberAliveKeepsBranchAndWorktreeAndNextIsDismissThenReRun()
    {
        var (tree, _) = FeatureStream(shipped: true, members: "[" + Member("proj-impl", "feat") + "]");

        var (code, stdout, stderr) = Roster("stream-done", "feat");

        Assert.True(code == 0, stderr);
        var result = Parse(stdout);
        Assert.Equal("shipped", result["verdict"].AsString());
        Assert.Equal("member-alive", result["archive_skipped"].AsString());
        Assert.True(HasRef("refs/heads/feat"));
        Assert.True(Directory.Exists(tree));
        var next = Texts(result.Get("next"));
        Assert.StartsWith("ah roster dismiss proj-impl", next[0], StringComparison.Ordinal);
        Assert.StartsWith("ah roster stream-done feat", next[^1], StringComparison.Ordinal);
        Assert.True(StreamRecord("feat")["archived_ref"].IsNull);
    }

    [Fact]
    public void OneMemberAliveAndOneProvenDeadIsNotGone()
    {
        Down("proj-dead");
        FeatureStream(shipped: true, members: "[" + Member("proj-live", "feat") + "," + Member("proj-dead", "feat") + "]");

        var (code, stdout, stderr) = Roster("stream-done", "feat");

        Assert.True(code == 0, stderr);
        Assert.Equal("member-alive", Parse(stdout)["archive_skipped"].AsString());
        Assert.True(HasRef("refs/heads/feat"));
    }

    [Fact]
    public void EveryMemberProvenDeadIsGoneAndTheBranchIsArchived()
    {
        Down("proj-dead");
        FeatureStream(shipped: true, members: "[" + Member("proj-dead", "feat") + "]");

        var (code, stdout, stderr) = Roster("stream-done", "feat");

        Assert.True(code == 0, stderr);
        Assert.Equal($"refs/archive/{Today}/feat", Parse(stdout)["archived_ref"].AsString());
    }

    [Fact]
    public void ShippedWithNoMembersRemovesTheWorktreeAndArchivesTheBranch()
    {
        var (tree, sha) = FeatureStream(shipped: true);

        var (code, stdout, stderr) = Roster("stream-done", "feat");

        Assert.True(code == 0, stderr);
        var archive = $"refs/archive/{Today}/feat";
        Assert.Equal(archive, Parse(stdout)["archived_ref"].AsString());
        Assert.False(Directory.Exists(tree));
        Assert.False(HasRef("refs/heads/feat"));
        Assert.Contains($"{archive} {sha}", Refs(), StringComparison.Ordinal);
        var record = StreamRecord("feat");
        Assert.Equal(archive, record["archived_ref"].AsString());
        Assert.Equal(sha, record["head_sha"].AsString());
        Assert.Equal("shipped", record["verdict"].AsString());
    }

    // ---------------------------------------------------------------- stream-list (D14)

    [Fact]
    public void StreamListPrintsOnlyLiveStreamsWithABranchAcrossTeams()
    {
        WriteTeam(Stream("live", "live-b", "/w/live") + "," + Stream("done", "done-b", "/w/done", state: "done"));
        WriteTeam(Stream("nowt", null, null) + "," + Stream("other", "other-b", null), file: "other");
        var before = TeamDigest();

        var (code, stdout, stderr) = Roster("stream-list");

        Assert.True(code == 0, stderr);
        Assert.Equal(["live-b\t/w/live", "other-b\t-"], stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal));
        Assert.Equal(before, TeamDigest());
    }

    [Fact]
    public void StreamListIsEmptyWithNoStreams()
    {
        WriteTeam(string.Empty);

        var (code, stdout, stderr) = Roster("stream-list");

        Assert.True(code == 0, stderr);
        Assert.Equal(string.Empty, stdout);
    }

    // ---------------------------------------------------------------- work-tree HEAD guard (B1, D13)

    /// <summary>Asserts stream-done finished but step 6 was skipped with <paramref name="skipped"/>: worktree, branch and refs stay.</summary>
    private void AssertGuardSkipped(string tree, string skipped, string warning, params string[] extra)
    {
        var refs = Refs();
        var (code, stdout, stderr) = Roster(["stream-done", "feat", .. extra]);

        Assert.True(code == 0, stderr);
        var result = Parse(stdout);
        Assert.Equal(skipped, result["archive_skipped"].AsString());
        Assert.Contains(warning, stdout, StringComparison.Ordinal);
        Assert.True(Directory.Exists(tree));
        Assert.True(HasRef("refs/heads/feat"));
        Assert.Equal(refs, Refs());
        Assert.Equal("done", StreamRecord("feat")["state"].AsString());
        Assert.False(StreamRecord("feat")["archived_ref"].IsText);
    }

    private void AssertArchived(string tree)
    {
        var (code, stdout, stderr) = Roster("stream-done", "feat");
        Assert.True(code == 0, stderr);
        Assert.Equal($"refs/archive/{Today}/feat", Parse(stdout)["archived_ref"].AsString());
        Assert.False(Directory.Exists(tree));
        Assert.False(HasRef("refs/heads/feat"));
    }

    [Fact]
    public void ADetachedWorktreeWithANewCommitIsDoneButKeptWithItsBranch()
    {
        var (tree, _) = FeatureStream(shipped: true);
        Git(tree, "checkout", "-q", "--detach");
        Commit(tree, "extra.txt", "extra\n", "wip: on no branch");

        AssertGuardSkipped(tree, "worktree-unreachable-commits", "worktree-unreachable-commits 1");
    }

    [Fact]
    public void TheGuardRunsOnADryRunToo()
    {
        var (tree, _) = FeatureStream(shipped: true);
        Git(tree, "checkout", "-q", "--detach");
        Commit(tree, "extra.txt", "extra\n", "wip: on no branch");

        var (code, stdout, stderr) = Roster("stream-done", "feat", "--dry-run");

        Assert.True(code == 0, stderr);
        Assert.DoesNotContain("\"would_archive\"", stdout, StringComparison.Ordinal);
        Assert.Equal("worktree-unreachable-commits", Parse(stdout)["archive_skipped"].AsString());
    }

    [Fact]
    public void ADetachedWorktreeAtAnAncestorOfTheBranchCountsZeroAndIsArchived()
    {
        var (tree, _) = FeatureStream(shipped: true);
        Git(tree, "checkout", "-q", "--detach", "HEAD~1");

        AssertArchived(tree);
    }

    [Theory]
    [InlineData("refs/archive/2026-01-01/x/y")]
    [InlineData("refs/tags/kept")]
    [InlineData("refs/remotes/origin/kept")]
    public void ACommitReachableFromANestedArchiveRefATagOrARemoteCountsZero(string reachableFrom)
    {
        var (tree, _) = FeatureStream(shipped: true);
        Git(tree, "checkout", "-q", "--detach");
        var extra = Commit(tree, "extra.txt", "extra\n", "wip: kept elsewhere");
        Git(Project, "update-ref", reachableFrom, extra);

        AssertArchived(tree);
    }

    [Theory]
    [InlineData("rebase-merge", "rebase", true)]
    [InlineData("rebase-apply", "rebase", true)]
    [InlineData("MERGE_HEAD", "merge", false)]
    [InlineData("CHERRY_PICK_HEAD", "cherry-pick", false)]
    [InlineData("REVERT_HEAD", "revert", false)]
    [InlineData("BISECT_LOG", "bisect", false)]
    public void AGitOperationInProgressKeepsTheWorktree(string marker, string op, bool isDirectory)
    {
        var (tree, sha) = FeatureStream(shipped: true);
        var path = Git(tree, "rev-parse", "--path-format=absolute", "--git-path", marker);
        if (isDirectory)
        {
            Directory.CreateDirectory(path);
        }
        else
        {
            File.WriteAllText(path, sha + "\n");
        }

        AssertGuardSkipped(tree, "worktree-op-in-progress", "worktree-op-in-progress " + op);
    }

    [Fact]
    public void AbandonDoesNotOverrideTheGuard()
    {
        var (tree, _) = FeatureStream(shipped: false);
        Git(tree, "checkout", "-q", "--detach");
        Commit(tree, "extra.txt", "extra\n", "wip: on no branch");

        AssertGuardSkipped(tree, "worktree-unreachable-commits", "worktree-unreachable-commits 1", "--abandon", "dropped");
        Assert.Equal("abandoned", StreamRecord("feat")["verdict"].AsString());
    }

    [Fact]
    public void AReRunAfterTheCommitsAreOnABranchArchives()
    {
        var (tree, _) = FeatureStream(shipped: true);
        Git(tree, "checkout", "-q", "--detach");
        Commit(tree, "extra.txt", "extra\n", "wip: on no branch");
        AssertGuardSkipped(tree, "worktree-unreachable-commits", "worktree-unreachable-commits 1");

        Git(tree, "branch", "saved");

        AssertArchived(tree);
        Assert.True(HasRef("refs/heads/saved"));
    }

    [Fact]
    public void AWorktreeDirectoryAlreadyGonePassesTheGuardAndArchives()
    {
        var (tree, _) = FeatureStream(shipped: true);
        Directory.Delete(tree, recursive: true);

        AssertArchived(tree);
    }

    [Fact]
    public void ALockedRegistrationWhoseDirectoryIsGoneStillHoldsTheBranch()
    {
        var (tree, _) = FeatureStream(shipped: true);
        Git(Project, "worktree", "lock", tree);
        Directory.Delete(tree, recursive: true);

        var (code, stdout, stderr) = Roster("stream-done", "feat");

        Assert.True(code == 0, stderr);
        Assert.Equal("branch-checked-out", Parse(stdout)["archive_skipped"].AsString());
        Assert.True(HasRef("refs/heads/feat"));
    }

    [Fact]
    public void UnshippedWithNoFlagIsRefusedAndChangesNothing()
    {
        FeatureStream(shipped: false);

        AssertUnchanged(() =>
        {
            var (code, stdout, stderr) = Roster("stream-done", "feat");
            Assert.NotEqual(0, code);
            Assert.Equal("unshipped-commits", Refused(stdout, stderr));
            Assert.Contains("feat: add feature", stdout + stderr, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void InALocalOnlyRunUnshippedCommitsAreDoneAndNothingIsArchived()
    {
        var (tree, _) = FeatureStream(shipped: false);
        LocalOnlyRule(RulesOfEngagement.Team, "proj");

        var (code, stdout, stderr) = Roster("stream-done", "feat");

        Assert.True(code == 0, stderr);
        var result = Parse(stdout);
        Assert.Equal("local-only", result["verdict"].AsString());
        Assert.Equal(1, (int)result["unshipped"].AsNumber());
        Assert.Contains(Texts(result.Get("warnings")), w => w.StartsWith("local-only: the run's rules say it never pushes", StringComparison.Ordinal));
        Assert.Equal("done", StreamRecord("feat")["state"].AsString());
        Assert.True(HasRef("refs/heads/feat"));
        Assert.True(Directory.Exists(tree));
        Assert.DoesNotContain("refs/archive/", Refs(), StringComparison.Ordinal);
    }

    [Fact]
    public void ALocalOnlyRuleOfAnotherTeamStillRefusesUnshippedCommits()
    {
        FeatureStream(shipped: false);
        LocalOnlyRule(RulesOfEngagement.Team, "other");

        var (code, stdout, stderr) = Roster("stream-done", "feat");

        Assert.NotEqual(0, code);
        Assert.Equal("unshipped-commits", Refused(stdout, stderr));
    }

    [Fact]
    public void AConflictRewrittenCherryPickIsCoveredByReasonAndArchived()
    {
        var tree = AddWorktree("feat", "feat");
        Commit(tree, "feat.txt", "feature as written\n", "feat: add feature");
        Commit(Project, "feat.txt", "feature as resolved in the conflict\n", "feat: add feature (resolved)");
        OriginMain();
        WriteTeam(Stream("feat", "feat", tree));

        var (code, stdout, stderr) = Roster("stream-done", "feat", "--covered", "shipped as abc1234");

        Assert.True(code == 0, stderr);
        Assert.Equal("covered", Parse(stdout)["verdict"].AsString());
        Assert.Equal("shipped as abc1234", StreamRecord("feat")["reason"].AsString());
        Assert.Equal($"refs/archive/{Today}/feat", Parse(stdout)["archived_ref"].AsString());
    }

    [Fact]
    public void MergedIntoALiveStreamHoldingEveryCommitIsAccepted()
    {
        var tree = AddWorktree("feat", "feat");
        var sha = Commit(tree, "feat.txt", "x\n", "feat: one");
        OriginMain();
        Git(Project, "branch", "lane-x", sha);
        WriteTeam(Stream("feat", "feat", tree) + "," + Stream("lanex", "lane-x", null));

        var (code, stdout, stderr) = Roster("stream-done", "feat", "--merged-into", "lane-x");

        Assert.True(code == 0, stderr);
        Assert.Equal("merged-into:lane-x", Parse(stdout)["verdict"].AsString());
    }

    [Theory]
    [InlineData("done")]
    [InlineData("not-a-stream")]
    public void MergedIntoADoneStreamOrAPlainBranchIsInvalid(string kind)
    {
        var tree = AddWorktree("feat", "feat");
        var sha = Commit(tree, "feat.txt", "x\n", "feat: one");
        OriginMain();
        Git(Project, "branch", "lane-x", sha);
        WriteTeam(Stream("feat", "feat", tree) + (kind == "done" ? "," + Stream("lanex", "lane-x", null, "done") : string.Empty));

        AssertUnchanged(() =>
        {
            var (_, stdout, stderr) = Roster("stream-done", "feat", "--merged-into", "lane-x");
            Assert.Equal("merged-into-invalid", Refused(stdout, stderr));
        });
    }

    [Fact]
    public void MergedIntoAStreamMissingTwoCommitsIsIncompleteTwo()
    {
        var tree = AddWorktree("feat", "feat");
        var first = Commit(tree, "a.txt", "a\n", "feat: a");
        Commit(tree, "b.txt", "b\n", "feat: b");
        Commit(tree, "c.txt", "c\n", "feat: c");
        OriginMain();
        Git(Project, "branch", "lane-x", first);
        WriteTeam(Stream("feat", "feat", tree) + "," + Stream("lanex", "lane-x", null));

        AssertUnchanged(() =>
        {
            var (_, stdout, stderr) = Roster("stream-done", "feat", "--merged-into", "lane-x");
            Assert.Equal("merged-into-incomplete", Refused(stdout, stderr));
            Assert.Contains("merged-into-incomplete 2", stdout + stderr, StringComparison.Ordinal);
        });
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void AnAbandonReasonThatIsEmptyBlankOrOver200CharactersIsRefused(string? reason)
    {
        FeatureStream(shipped: false);

        AssertUnchanged(() =>
        {
            var (_, stdout, stderr) = Roster("stream-done", "feat", "--abandon", reason ?? new string('x', 201));
            Assert.Equal("reason-required", Refused(stdout, stderr));
        });
    }

    [Fact]
    public void AnAbandonReasonOfExactly200CharactersIsAccepted()
    {
        FeatureStream(shipped: false);

        var (code, stdout, stderr) = Roster("stream-done", "feat", "--abandon", new string('x', 200));

        Assert.True(code == 0, stderr);
        Assert.Equal("abandoned", Parse(stdout)["verdict"].AsString());
    }

    [Fact]
    public void TwoOfTheThreeFlagsAreAUsageError()
    {
        FeatureStream(shipped: false);

        AssertUnchanged(() =>
        {
            var (code, stdout, stderr) = Roster("stream-done", "feat", "--covered", "x", "--abandon", "y");
            Assert.NotEqual(0, code);
            Assert.Contains("mutually exclusive", stdout + stderr, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void AnUntrackedFileInTheWorktreeRefusesWorktreeDirtyAndWritesNothing()
    {
        var (tree, _) = FeatureStream(shipped: true);
        File.WriteAllText(Path.Combine(tree, "notes.txt"), "mine\n");

        AssertUnchanged(() =>
        {
            var (_, stdout, stderr) = Roster("stream-done", "feat");
            Assert.Equal("worktree-dirty", Refused(stdout, stderr));
        });
        Assert.True(File.Exists(Path.Combine(tree, "notes.txt")));
    }

    [Fact]
    public void AnUntrackedFileStillRefusesWhenTheRepoHidesUntrackedFiles()
    {
        var (tree, _) = FeatureStream(shipped: true);
        Git(Project, "config", "status.showUntrackedFiles", "no");
        File.WriteAllText(Path.Combine(tree, "notes.txt"), "mine\n");

        AssertUnchanged(() =>
        {
            var (_, stdout, stderr) = Roster("stream-done", "feat");
            Assert.Equal("worktree-dirty", Refused(stdout, stderr));
        });
        Assert.True(File.Exists(Path.Combine(tree, "notes.txt")));
    }

    [Fact]
    public void OnlyIgnoredBuildOutputIsCleanAndTheWorktreeIsRemoved()
    {
        var (tree, _) = FeatureStream(shipped: true);
        foreach (var dir in new[] { "bin", "obj", "TestResults" })
        {
            Directory.CreateDirectory(Path.Combine(tree, dir));
            File.WriteAllText(Path.Combine(tree, dir, "out.dll"), "x");
        }

        var (code, stdout, stderr) = Roster("stream-done", "feat");

        Assert.True(code == 0, stderr);
        Assert.Equal($"refs/archive/{Today}/feat", Parse(stdout)["archived_ref"].AsString());
        Assert.False(File.Exists(Path.Combine(tree, "feat.txt")));
    }

    [Fact]
    public void AnArchiveRefAtTheSameShaIsANoOp()
    {
        var (_, sha) = FeatureStream(shipped: true);
        Git(Project, "update-ref", $"refs/archive/{Today}/feat", sha);

        var (code, stdout, stderr) = Roster("stream-done", "feat");

        Assert.True(code == 0, stderr);
        Assert.Equal($"refs/archive/{Today}/feat", Parse(stdout)["archived_ref"].AsString());
        Assert.False(HasRef("refs/heads/feat"));
    }

    [Fact]
    public void AnArchiveRefAtAnotherShaIsRefusedAndChangesNothing()
    {
        FeatureStream(shipped: true);
        Git(Project, "update-ref", $"refs/archive/{Today}/feat", "refs/heads/main~1");

        AssertUnchanged(() =>
        {
            var (_, stdout, stderr) = Roster("stream-done", "feat");
            Assert.Equal("archive-ref-exists", Refused(stdout, stderr));
        });
    }

    [Fact]
    public void AnotherLiveStreamOnTheSameBranchIsDoneButNotArchived()
    {
        var (tree, _) = FeatureStream(shipped: true);
        WriteTeam(Stream("feat", "feat", tree) + "," + Stream("twin", "feat", null));

        var (code, stdout, stderr) = Roster("stream-done", "feat");

        Assert.True(code == 0, stderr);
        Assert.Equal("branch-shared", Parse(stdout)["archive_skipped"].AsString());
        Assert.True(HasRef("refs/heads/feat"));
    }

    [Fact]
    public void ReRunningOnADoneStreamRetriesOnlyTheArchive()
    {
        var (tree, _) = FeatureStream(shipped: true, members: "[" + Member("proj-impl", "feat") + "]");
        Assert.Equal(0, Roster("stream-done", "feat").Code);
        var ledger = LedgerLines();
        var dismissed = Parse(StoredTeam());
        dismissed["members"] = new JsonArray();
        _sandbox.PlantTeam(TeamFile, JsonText.Stringify(dismissed, 0));

        var (code, stdout, stderr) = Roster("stream-done", "feat");

        Assert.True(code == 0, stderr);
        Assert.True(Parse(stdout)["already_done"].AsBool());
        Assert.Equal($"refs/archive/{Today}/feat", Parse(stdout)["archived_ref"].AsString());
        Assert.False(Directory.Exists(tree));
        Assert.Equal(ledger, LedgerLines());
    }

    [Fact]
    public void DryRunReportsTheVerdictAndWhatTheArchiveWouldDoAndChangesNothing()
    {
        FeatureStream(shipped: true);

        AssertUnchanged(() =>
        {
            var (code, stdout, stderr) = Roster("stream-done", "feat", "--dry-run");
            Assert.True(code == 0, stderr);
            var result = Parse(stdout);
            Assert.Equal("shipped", result["verdict"].AsString());
            Assert.Equal($"refs/archive/{Today}/feat", result["would_archive"].Member("ref")!.Value.AsString());
        });
    }

    // ---------------------------------------------------------------- ah2-513: the stream setup command

    /// <summary>Writes <c>{"stream_setup":{"argv":[sh,-c,script]}}</c> to the config file of <paramref name="level"/> for this checkout.</summary>
    private void SetupAt(string level, string script)
    {
        var config = StoreGraph().Config;
        var path = level == "repo-user"
            ? Ah.Core.Text.AhPaths.RepoUserConfig(_sandbox.Home, config.PathSlug(config.CheckoutRoot(Project)!))
            : config.RosterLevelPaths(Project)[level].AsString();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{\"stream_setup\":{\"argv\":[\"sh\",\"-c\"," + Js(script) + "]}}");
        // the command's PATH is the sandbox's (Amendment 4), so sh is found there
        if (!File.Exists(Path.Combine(_sandbox.Bin, "sh")))
        {
            File.CreateSymbolicLink(Path.Combine(_sandbox.Bin, "sh"), "/bin/sh");
        }
    }

    [Fact]
    public void StreamSetupRunsInTheNewTreeWithItsStreamNameAndReportsOk()
    {
        WriteTeam(string.Empty);
        SetupAt("repo-user", "printf '%s|%s|' \"$AH_STREAM\" \"$HOME\"; pwd -P");

        var (code, stdout, stderr) = Roster("stream-open", "s1");

        Assert.True(code == 0, stderr);
        var result = Parse(stdout);
        Assert.True(result["worktree_created"].AsBool());
        var setup = result["setup"].AsObject();
        Assert.True(setup["ran"].AsBool());
        Assert.True(setup["ok"].AsBool());
        Assert.Equal(0, (int)setup["exit"].AsNumber());
        var printed = setup["stdout_tail"].AsString();
        Assert.StartsWith($"s1|{_sandbox.Home}|", printed, StringComparison.Ordinal); // the sandbox HOME, not the real one
        Assert.EndsWith("/.claude/worktrees/s1\n", printed, StringComparison.Ordinal);
        Assert.DoesNotContain(Texts(result.Get("warnings")), w => w.StartsWith("stream setup failed", StringComparison.Ordinal));
    }

    [Fact]
    public void AFailingStreamSetupLeavesTheStreamOpenWithAWarning()
    {
        WriteTeam(string.Empty);
        SetupAt("repo-user", "echo oops >&2; exit 3");

        var (code, stdout, stderr) = Roster("stream-open", "s1");

        Assert.True(code == 0, stderr);
        var result = Parse(stdout);
        Assert.True(result["opened"].AsBool());
        var setup = result["setup"].AsObject();
        Assert.False(setup["ok"].AsBool());
        Assert.Equal(3, (int)setup["exit"].AsNumber());
        Assert.Equal("oops\n", setup["stderr_tail"].AsString());
        Assert.Contains("stream setup failed (exit 3); the stream is open; see setup.stderr_tail", Texts(result["warnings"]));
        Assert.Equal("open", StreamRecord("s1")["state"].AsString());
    }

    [Fact]
    public void AStreamSetupWhoseRunnerThrowsStillPrintsAnOpenStream()
    {
        WriteTeam(string.Empty);
        SetupAt("repo-user", RealGit.ThrowingScript);

        var (code, stdout, stderr) = Roster("stream-open", "s1");

        Assert.True(code == 0, stderr);
        var result = Parse(stdout);
        Assert.True(result["opened"].AsBool());
        var setup = result["setup"].AsObject();
        Assert.Equal((false, false, "setup-start-failed"), (setup["ran"].AsBool(), setup["ok"].AsBool(), setup["error"].AsString()));
        Assert.Contains("stream setup failed (setup-start-failed); the stream is open; see setup.message", Texts(result["warnings"]));
        Assert.Equal("open", StreamRecord("s1")["state"].AsString());
    }

    [Fact]
    public void StreamSetupDoesNotRunWhenTheTreeAlreadyExists()
    {
        var tree = AddWorktree("s1", "s1");
        WriteTeam(Stream("s1", "s1", tree));
        SetupAt("repo-user", "touch ran");

        var (code, stdout, stderr) = Roster("stream-open", "s1");

        Assert.True(code == 0, stderr);
        var result = Parse(stdout);
        Assert.False(result["worktree_created"].AsBool());
        Assert.Null(result.Get("setup"));
        Assert.False(File.Exists(Path.Combine(tree, "ran")));
    }

    [Fact]
    public void AStreamSetupInTheCheckedInConfigIsIgnoredWithAWarning()
    {
        WriteTeam(string.Empty);
        SetupAt("repo", "touch ran");

        var (code, stdout, stderr) = Roster("stream-open", "s1");

        Assert.True(code == 0, stderr);
        var result = Parse(stdout);
        Assert.True(result["worktree_created"].AsBool());
        Assert.Null(result.Get("setup"));
        Assert.Contains(Texts(result["warnings"]), w => w.StartsWith("stream_setup in ", StringComparison.Ordinal) && w.Contains(" was ignored: it is read only from ", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(Project, ".claude", "worktrees", "s1", "ran")));
    }

    // ---------------------------------------------------------------- status is untouched (§6)

    [Fact]
    public void StatusOutputIsTheSameWithGoldenUnresolvedAndResolved()
    {
        NoGolden();
        Git(Project, "branch", "stray", "trunk");
        WriteTeam(string.Empty);
        var (_, before, _) = Roster("status", "--now", "2026-10-29T12:00:00Z");
        Git(Project, "update-ref", "refs/remotes/origin/main", "refs/heads/trunk");
        Git(Project, "config", "ah.releaseRef", "origin/main");

        var (code, after, stderr) = Roster("status", "--now", "2026-10-29T12:00:00Z");

        Assert.True(code == 0, stderr);
        Assert.Equal(before, after);
    }

    // ---------------------------------------------------------------- ah2-46: the pipeline branch answer lives in the database

    private static List<string> Branches(string stdout) =>
        Parse(stdout).Get("branches")!.Value.AsArray().Select(b => b.AsString()).ToList();

    [Fact]
    public void ThePipelineBranchAnswerIsKeptInTheDatabaseAndAGitConfigValueIsTakenInOnce()
    {
        var (code, none, stderr) = Roster("pipeline-branch", "show");
        Assert.True(code == 0, stderr);
        Assert.Empty(Branches(none));

        Git(Project, "config", "--add", "ah.pipelineBranch", "main"); // an answer recorded by a build before the move
        var (_, imported, _) = Roster("pipeline-branch");
        Assert.Equal(["main"], Branches(imported));
        Assert.Equal(string.Empty, GitOrEmpty("config", "--get-all", "ah.pipelineBranch")); // unset once the database holds it

        var (_, allowed, _) = Roster("pipeline-branch", "allow", "release");
        Assert.Equal(["main", "release"], Branches(allowed));
        Assert.Equal(["main", "release"], Branches(Roster("pipeline-branch", "show").Stdout)); // read back from the database

        Assert.Equal(["release"], Branches(Roster("pipeline-branch", "revoke", "main").Stdout));
        Assert.Empty(Branches(Roster("pipeline-branch", "revoke").Stdout));
        Assert.Empty(Branches(Roster("pipeline-branch", "show").Stdout));
    }

    private string GitOrEmpty(params string[] args)
    {
        var result = PlatformModule.CreateProcessRunner().Run(new ProcessRequest(["git", "-C", Project, .. args], Environment: GitEnvironment(), Timeout: TimeSpan.FromSeconds(20)));
        return Encoding.UTF8.GetString(result.Stdout.Span).Trim();
    }
}
