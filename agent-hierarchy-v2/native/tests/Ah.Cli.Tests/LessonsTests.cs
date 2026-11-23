using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Cli.Verbs;
using Ah.Platform;
using Ah.StateDb;
using Ah.TestKit;

namespace Ah.Cli.Tests;

/// <summary>ah2-54 S1: <c>ah lessons propose|list</c> against a real state database, through the verb and the daemon's route.</summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class LessonsTests : IDisposable
{
    private readonly Sandbox _sandbox = new();
    private readonly IStateDb _db;
    private readonly string _cwd;
    private readonly string _dir;

    public LessonsTests()
    {
        _cwd = Path.Combine(_sandbox.Root, "my-cool-repo");
        _dir = Path.Combine(_cwd, ".claude", "ah2");
        Directory.CreateDirectory(Path.Combine(_dir, "teams"));
        File.WriteAllText(Path.Combine(_dir, "teams", "tm.json"), "{\"members\":[]}");
        _db = StateDbModule.Open(_sandbox.AhHome, mayCreate: true);
        Assert.Equal(StateDbAvailability.Ready, _db.Status.Availability);
    }

    public void Dispose()
    {
        _db.Dispose();
        _sandbox.Dispose();
    }

    /// <summary>Who the route proves the caller to be.</summary>
    private CutoverCaller _caller = new(CutoverCallerKind.Member, "impl@tm");

    private static readonly CutoverCaller User = new(CutoverCallerKind.User, "user");
    private static readonly CutoverCaller Orchestrator = new(CutoverCallerKind.TopOrchestrator, "orchestrator@tm");

    /// <summary>Runs <c>ah lessons</c> as <see cref="_caller"/>; <paramref name="inTeam"/> puts the caller in team <c>tm</c> as its team file env does.</summary>
    private (int Code, string Out, string Err) Lessons(bool inTeam, params string[] args) =>
        Run(inTeam, (context, _, stdout, stderr) => LessonsVerb.Run(context, [.. args, "--cwd", _cwd], stdout, stderr));

    private (int Code, string Out, string Err) Run(bool inTeam, Func<CliContext, ServiceGraph, TextWriter, TextWriter, int> verb)
    {
        var vars = new Dictionary<string, string?> { ["AH_TEST"] = "1", ["AH_HOME"] = _sandbox.AhHome, ["HOME"] = _sandbox.Home, ["WRANGL_HIERARCHY_DIR"] = _dir };
        if (inTeam)
        {
            vars["WRANGL_TEAM_FILE"] = Path.Combine(_dir, "teams", "tm.json");
        }

        var env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, vars, new PluginInfo(_sandbox.PluginRoot, "wrangl", "1.0.0", "1"));
        var daemon = new FakeDaemonClient();
        var graph = ServiceGraph.Build(PlatformModule.CreateIo(null), env, FakeRunner.KnowingSelf(), daemon);
        var route = new LessonsRoute(graph, _sandbox.AhHome, () => _db, _ => _caller, () => DateTimeOffset.UtcNow);
        daemon.Reply = request =>
        {
            Assert.Equal("/v1/lessons", request.PathAndQuery);
            var response = route.HandleAsync(new RouteRequest("POST", "/v1/lessons", string.Empty, new Dictionary<string, string>(), request.Body, new Dictionary<string, string>()), CancellationToken.None).GetAwaiter().GetResult();
            return new DaemonReply(response.Status, response.Body);
        };
        var context = new CliContext(env, new RecordingExec(), new FakeRunner(), "ah", _sandbox.Executable("install/ah"), _sandbox.Home, 501, null, new Lazy<ServiceGraph>(graph));
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var code = verb(context, graph, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    /// <summary>A work item, closed with <paramref name="close"/> (<c>done</c> or <c>drop</c>); its id.</summary>
    private string Item(string close)
    {
        var added = Run(true, (c, _, o, e) => WorkVerb.Run(c, ["add", "fix the hook", "--wave", "now", "--cwd", _cwd], o, e));
        Assert.True(added.Code == 0, added.Err);
        var id = added.Out.Trim();
        var closed = Run(true, (c, g, o, e) => BacklogVerb.Run(c, [close, WorkVerb.Resolve(g, _sandbox.AhHome, _dir, id, null).Row!.Item, close == "done" ? "--commit" : "--note", close == "done" ? "abc1234" : "not needed", "--cwd", _cwd], o, e));
        Assert.True(closed.Code == 0, closed.Err);
        return id;
    }

    private (string Scope, string State, string? RetiredBy, long UpdatedMs) Stored(long id)
    {
        var r = Query($"SELECT scope, state, retired_by, updated_ms FROM lessons WHERE id = {id}")[0];
        return ((string)r[0]!, (string)r[1]!, r[2] as string, Convert.ToInt64(r[3], System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void AMemberPromotingIsRefusedAndTheTopOrchestratorPromotesToRepo()
    {
        Assert.Equal(0, Lessons(true, Propose()).Code);

        var refused = Lessons(true, "promote", "L1", "--to", "repo");
        Assert.Equal(2, refused.Code);
        Assert.Contains("only the user or the top-level Orchestrator", refused.Err, StringComparison.Ordinal);
        Assert.Equal("team", Stored(1).Scope);

        foreach (var kind in new[] { CutoverCallerKind.SubOrchestrator, CutoverCallerKind.DeputyChief })
        {
            _caller = new CutoverCaller(kind, "x@tm");
            Assert.Equal(2, Lessons(true, "promote", "L1", "--to", "repo").Code);
        }

        _caller = Orchestrator;
        var promoted = Lessons(true, "promote", "L1", "--to", "repo");
        Assert.True(promoted.Code == 0, promoted.Err);
        Assert.Equal("repo", Stored(1).Scope);
    }

    [Fact]
    public void PromoteToRoleNeedsARole()
    {
        _caller = Orchestrator;
        Assert.Equal(0, Lessons(true, Propose()).Code);

        Assert.Equal(2, Lessons(true, "promote", "L1", "--to", "role").Code);
        Assert.Equal("team", Stored(1).Scope);

        // the nearest that must pass: the role given
        Assert.Equal(0, Lessons(true, "promote", "L1", "--to", "role", "--role", "reviewer").Code);
        Assert.Equal("role", Stored(1).Scope);
        Assert.Equal(1, Lessons(true, "promote", "L9", "--to", "repo").Code);
    }

    [Fact]
    public void TheOrchestratorAndTheUserMayPromoteToGlobal()
    {
        Assert.Equal(0, Lessons(true, Propose()).Code);
        Assert.Equal(0, Lessons(true, Propose()).Code);

        _caller = Orchestrator;
        Assert.Equal(0, Lessons(true, "promote", "L1", "--to", "global").Code);
        _caller = User;
        Assert.Equal(0, Lessons(true, "promote", "L2", "--to", "global").Code);

        Assert.Equal("global", Stored(1).Scope);
        Assert.Equal("global", Stored(2).Scope);
    }

    [Fact]
    public void TheOrchestratorRetiresAGlobalLessonAndItIsRecorded()
    {
        Assert.Equal(0, Lessons(true, Propose()).Code);
        _caller = User;
        Assert.Equal(0, Lessons(true, "promote", "L1", "--to", "global").Code);

        _caller = Orchestrator;
        var retired = Lessons(true, "retire", "L1", "--why", "superseded");
        Assert.True(retired.Code == 0, retired.Err);
        Assert.Equal(("retired", "orchestrator@tm"), (Stored(1).State, Stored(1).RetiredBy));
    }

    [Fact]
    public void RetiringTwiceExits0AndChangesNothing()
    {
        Assert.Equal(0, Lessons(true, Propose()).Code);
        _caller = User;
        Assert.Equal(0, Lessons(true, "retire", "L1", "--why", "wrong").Code);
        var first = Stored(1);

        _caller = Orchestrator;
        var again = Lessons(true, "retire", "L1", "--why", "other");
        Assert.Equal(0, again.Code);
        Assert.Contains("already retired", again.Out, StringComparison.Ordinal);
        Assert.Equal(first, Stored(1));
    }

    [Fact]
    public void AMemberRetiringIsRefused()
    {
        Assert.Equal(0, Lessons(true, Propose()).Code);

        Assert.Equal(2, Lessons(true, "retire", "L1", "--why", "wrong").Code);
        Assert.Equal("open", Stored(1).State);
    }

    [Fact]
    public void ALessonUntilADoneItemIsRetiredAtReadTime()
    {
        var done = Item("done");
        Assert.Equal(0, Lessons(true, Propose("--until", done)).Code);

        Assert.DoesNotContain("L1", Lessons(true, "list").Out, StringComparison.Ordinal);
        Assert.Contains($"[retired: {done} is done]", Lessons(true, "list", "--all").Out, StringComparison.Ordinal);
        Assert.Equal("open", Stored(1).State);
    }

    [Fact]
    public void ALessonUntilADroppedItemStaysOpen()
    {
        var dropped = Item("drop");
        Assert.Equal(0, Lessons(true, Propose("--until", dropped)).Code);

        Assert.Contains("L1 team tm", Lessons(true, "list").Out, StringComparison.Ordinal);
    }

    /// <summary>Amendment 2 F1, F2: a promote records who made it and when, in the one <c>by</c> format; an unpromoted lesson records none.</summary>
    [Fact]
    public void APromotionRecordsWhoMadeItAndWhen()
    {
        Assert.Equal(0, Lessons(true, Propose()).Code);
        Assert.Equal(0, Lessons(true, Propose()).Code);
        _caller = Orchestrator;
        Assert.Equal(0, Lessons(true, "promote", "L1", "--to", "repo").Code);

        var promoted = Query("SELECT promoted_by, promoted_ms FROM lessons ORDER BY id");
        Assert.Equal("orchestrator@tm", promoted[0][0]);
        Assert.NotNull(promoted[0][1]);
        Assert.Null(promoted[1][0]);
        Assert.Equal(["user", "member", "orchestrator"], new[] { User, new CutoverCaller(CutoverCallerKind.Member, "the record of tm does not name this process as its Orchestrator"), new CutoverCaller(CutoverCallerKind.TopOrchestrator, "top-level session with no team") }.Select(Composition.Lessons.ByOf));
    }

    /// <summary>Amendment 2 F3: a lesson whose --until item cannot be resolved is open, and it hides no other lesson.</summary>
    [Fact]
    public void AnUnresolvableUntilItemLeavesTheLessonOpenAndListWhole()
    {
        Assert.Equal(0, Lessons(true, Propose()).Code);
        Assert.Equal(0, Lessons(true, "propose", "--lesson", "tied", "--when", "w", "--pointer", "p").Code);
        Assert.Equal(OnceStatus.Applied, _db.WriteOnce("bad-until", DateTimeOffset.UtcNow.AddSeconds(10), tx =>
        {
            tx.Execute("UPDATE lessons SET until_item = 'no such item ''; at all' WHERE id = 2");
            return string.Empty;
        }).Status);

        var listed = Lessons(true, "list");
        Assert.Equal(0, listed.Code);
        Assert.Contains("L1 team tm", listed.Out, StringComparison.Ordinal);
        Assert.Contains("L2 team tm", listed.Out, StringComparison.Ordinal);
    }

    /// <summary>Amendment 2 F4: a first lesson longer than the cap alone is cut to it, and the overflow line counts the rest.</summary>
    [Fact]
    public void AFirstLessonOverTheCapIsCutNotDropped()
    {
        LessonRow Row(long id, string text) => new(id, "repo", null, null, [], null, text, "w", "p", null, "user", "open", null, null, 1, id);

        var block = LessonsFooter.Render([Row(2, new string('x', 1200)), Row(1, "small")], "20261103-120000-aaaa")!;

        var lines = block.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(LessonsFooter.Header, lines[0]);
        Assert.StartsWith("- L2 ", lines[1], StringComparison.Ordinal);
        Assert.Equal(LessonsFooter.MaxChars, lines[1].Length);
        Assert.EndsWith("…", lines[1], StringComparison.Ordinal);
        Assert.Equal("1 more lessons match: ah lessons list --brief 20261103-120000-aaaa", lines[2]);

        // the nearest that must not be cut: a first lesson that fits stays whole
        Assert.DoesNotContain("…", LessonsFooter.Render([Row(1, "small")], "x")!, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownUntilItemIsRefusedAtPropose()
    {
        var refused = Lessons(true, Propose("--until", "nope-999"));

        Assert.Equal(1, refused.Code);
        Assert.Equal(0, Count());
    }

    private static string[] Propose(params string[] more) => ["propose", "--lesson", "Re-run the shell suite after a hook change", "--when", "a change touches hooks/", "--pointer", "de41502", .. more];

    private IReadOnlyList<object?[]> Query(string sql)
    {
        using var reader = StateDbModule.OpenReader(_sandbox.AhHome).Reader!;
        return reader.Query(sql, []);
    }

    private long Count() => Convert.ToInt64(Query("SELECT COUNT(*) FROM lessons")[0][0], System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void ProposeWritesATeamScopeLessonForTheCallersTeam()
    {
        var proposed = Lessons(true, Propose("--role", "reviewer", "--path", "native/src/Ah.Hooks/"));

        Assert.True(proposed.Code == 0, proposed.Err);
        Assert.Equal("L1\n", proposed.Out.ReplaceLineEndings("\n"));
        var row = Query("SELECT scope, team, role, paths, \"by\", state, hier_dir FROM lessons")[0];
        Assert.Equal(["team", "tm", "reviewer", "native/src/Ah.Hooks/", "impl@tm", "open"], row[..6].Select(v => (string)v!));
        Assert.Equal(Ah.Hierarchy.HierarchyModule.WorkDirKey(_dir), row[6]);
    }

    [Fact]
    public void ProposeWithNoTeamAndNoTeamFlagExits2AndWritesNothing()
    {
        var refused = Lessons(false, Propose());

        Assert.Equal(2, refused.Code);
        Assert.Contains("--team", refused.Err, StringComparison.Ordinal);
        Assert.Equal(0, Count());

        // the nearest that must pass: the same caller naming a team
        Assert.Equal(0, Lessons(false, Propose("--team", "tm")).Code);
        Assert.Equal(1, Count());
    }

    public static TheoryData<string[]> OverCap => new()
    {
        { ["propose", "--lesson", new string('l', 201), "--when", "w", "--pointer", "p"] },
        { ["propose", "--lesson", "   ", "--when", "w", "--pointer", "p"] },
        { ["propose", "--lesson", "l", "--when", new string('w', 121), "--pointer", "p"] },
        { ["propose", "--lesson", "l", "--when", "w", "--pointer", new string('p', 121)] },
        { ["propose", "--lesson", "l", "--when", "w", "--pointer", "p", "--happened", new string('h', 201)] },
        { ["propose", "--lesson", "l", "--when", "w", "--pointer", "p", "--happened", " "] },
        { ["propose", "--lesson", "l", "--when", "w", "--pointer", "p", "--path", "a/", "--path", "b/", "--path", "c/", "--path", "d/"] },
        { ["propose", "--lesson", "l", "--when", "w", "--pointer", "p", "--path", new string('x', 121)] },
        { ["propose", "--lesson", "l", "--when", "w", "--pointer", "p", "--path", "/abs/x"] },
        { ["propose", "--lesson", "l", "--when", "w", "--pointer", "p", "--path", "a/../b"] },
        { ["propose", "--lesson", "l", "--when", "w", "--pointer", "p", "--role", "no-such-role"] },
    };

    [Theory]
    [MemberData(nameof(OverCap))]
    public void EachFieldCapRefusesExit2AndWritesNothing(string[] args)
    {
        var refused = Lessons(true, args);

        Assert.Equal(2, refused.Code);
        Assert.Equal(0, Count());
    }

    [Fact]
    public void EveryFieldAtItsCapIsWritten()
    {
        var atCap = Lessons(true, "propose", "--lesson", new string('l', 200), "--when", new string('w', 120), "--pointer", new string('p', 120), "--happened", new string('h', 200),
            "--path", new string('x', 120), "--path", "b/", "--path", "c..d/", "--role", "implementor");

        Assert.True(atCap.Code == 0, atCap.Err);
        Assert.Equal(1, Count());
    }

    [Fact]
    public void ListByDefaultExcludesRetiredLessons()
    {
        Assert.Equal(0, Lessons(true, Propose()).Code);
        Assert.Equal(0, Lessons(true, "propose", "--lesson", "an old note", "--when", "w", "--pointer", "p").Code);
        Assert.Equal(OnceStatus.Applied, _db.WriteOnce("retire-l2", DateTimeOffset.UtcNow.AddSeconds(10), tx =>
        {
            tx.Execute("UPDATE lessons SET state = 'retired', retired_by = 'user', retired_why = 'no longer true' WHERE id = 2");
            return string.Empty;
        }).Status);

        var listed = Lessons(true, "list");
        Assert.Equal(0, listed.Code);
        Assert.Contains("L1 team tm, by impl@tm, source de41502: Re-run the shell suite after a hook change (when: a change touches hooks/)", listed.Out, StringComparison.Ordinal);
        Assert.DoesNotContain("L2", listed.Out, StringComparison.Ordinal);

        // the negative: --all shows the retired one, with who and why
        Assert.Contains("L2 team tm, by impl@tm, source p: an old note (when: w) [retired by user: no longer true]", Lessons(true, "list", "--all").Out, StringComparison.Ordinal);
    }
}
