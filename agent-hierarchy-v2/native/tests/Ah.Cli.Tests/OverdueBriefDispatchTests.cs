using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Cli.Verbs.Msg;
using Ah.Platform;
using Ah.TestKit;

namespace Ah.Cli.Tests;

/// <summary>A request made with <c>msg new</c> and sent as a brief is an open dispatch for the overdue watcher until its response lands; a note is never one.</summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class OverdueBriefDispatchTests : IDisposable
{
    private const string Team = "tm";
    /// <summary>A request id of today: the watch and the failsafe count a brief as sent only inside the horizon (ah2-296).</summary>
    private static readonly string RequestId = Fresh("abcd");

    private static string Fresh(string tail) => DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + "-" + tail;
    private readonly Sandbox _sandbox = Sandbox.PlantedFiles();
    private readonly ServiceGraph _graph;
    private readonly FakeEnv _env;
    private readonly FakeRunner _runner = FakeRunner.KnowingSelf();

    public OverdueBriefDispatchTests()
    {
        var variables = new Dictionary<string, string?>
        {
            ["AH_TEST"] = "1",
            ["AH_HOME"] = _sandbox.AhHome,
            ["HOME"] = _sandbox.Home,
            ["WRANGL_HIERARCHY_DIR"] = Hierarchy,
            ["AH_SLOTS_DIR"] = Path.Combine(_sandbox.Root, "slots"),
        };
        _env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables, new PluginInfo(_sandbox.PluginRoot, "wrangl", "1.0.0", "1"));
        _graph = ServiceGraph.Build(PlatformModule.CreateIo(null), _env, _runner, new FakeDaemonClient { Reply = _ => new DaemonReply(0, default, "down") });
        var teams = Path.Combine(Hierarchy, "teams");
        Directory.CreateDirectory(teams);
        var manager = Path.Combine(teams, Team + ".json");
        File.WriteAllText(
            manager,
            "{\"team_id\":\"t-tm\",\"orchestrator\":{\"pid\":" + Environment.ProcessId + ",\"pid_start\":\"" + FakeRunner.SelfStart + "\"},"
            + "\"members\":[{\"name\":\"impl-1\",\"role\":\"implementor\",\"route\":\"pane\",\"kind\":\"claude\"}],\"streams\":{}}\n");
        FakeRunner.RegisterSelf(_graph.Mailbox, "o", manager, "orchestrator@" + Team);
        FakeRunner.RegisterSelf(_graph.Mailbox, "m", manager, "impl-1@" + Team);
    }

    public void Dispose() => _sandbox.Dispose();

    private string Hierarchy => Path.Combine(_sandbox.Root, "hier");

    private string Run(params string[] args)
    {
        var context = new CliContext(_env, new RecordingExec(), new FakeRunner(), "ahd", _sandbox.Executable("install/ahd"), _sandbox.Home, 501, null, new Lazy<ServiceGraph>(_graph));
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        Assert.True(_sandbox.Mirrored(() => MsgVerb.Run(context, [.. args, "--session", "o", "--plain", "--cwd", _sandbox.Root], stdout, stderr)) == 0, stderr.ToString());
        return stdout.ToString();
    }

    private string NewRequest(string from, string? id = null)
    {
        id ??= RequestId;
        var made = Run("new", "--type", "request", "--to", "implementor", "--from", "orchestrator", "--slug", "t1", "--title", "A task", "--id", id, "--team", Team);
        var path = _sandbox.MsgFile(made);
        var edited = File.ReadAllText(path).Replace("from: orchestrator", "from: " + from, StringComparison.Ordinal);

        // the store holds the sender in its row: the hand edit is stored again as the message it now is
        Ah.TestKit.TestMsgStore.Delete(_sandbox.AhHome, path);
        return Ah.TestKit.TestMsgStore.Plant(_sandbox.AhHome, path, edited);
    }

    private IReadOnlyList<Ah.Watch.OverdueDispatch> Open() =>
        _sandbox.Mirrored(() => new WakeComposition(_graph, _env).OpenDispatches(Hierarchy, Team, "o"));

    private void NameAddressee(string request, string name)
    {
        var edited = File.ReadAllText(request).Replace("to_name: null", "to_name: " + name, StringComparison.Ordinal);
        Ah.TestKit.TestMsgStore.Delete(_sandbox.AhHome, request);
        _ = Ah.TestKit.TestMsgStore.Plant(_sandbox.AhHome, request, edited);
    }

    /// <summary>ah2-296 L2: a request nobody sent raises no check-in, whether or not its member has a session, so the failsafe watches none.</summary>
    [Theory]
    [InlineData("not-spawned")]
    [InlineData("impl-1")]
    public void AnUnsentRequestIsNotWatched(string name)
    {
        NameAddressee(NewRequest("orchestrator"), name);

        Assert.DoesNotContain(Open(), static d => d.RequestId == RequestId);
    }

    [Fact]
    public void APausedMembersBriefIsNotWatchedAndAResumedOneIsWatchedFromTheResume()
    {
        var request = NewRequest("orchestrator");
        NameAddressee(request, "impl-1");
        AsAdmin();
        _ = Run("send", "impl-1@" + Team, "--ref", request, "--kind", "brief");
        using (Ah.StateDb.StateDbModule.Open(_sandbox.AhHome, mayCreate: true))
        {
        }

        _graph.Roster.SetPause(Hierarchy, Team, "impl-1", true, 1000);
        Assert.DoesNotContain(Open(), static d => d.RequestId == RequestId);

        var resumedAt = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeMilliseconds();
        _graph.Roster.SetPause(Hierarchy, Team, "impl-1", false, resumedAt);
        Assert.Equal(resumedAt, Assert.Single(Open(), static d => d.RequestId == RequestId).FloorMs);
    }

    [Theory]
    [InlineData("orchestrator")]
    [InlineData("sub-orchestrator")]
    public void ABriefOfARequestIsListedWithItsAddresseeAndClearsWhenTheResponseLands(string from)
    {
        var request = NewRequest(from);
        Assert.Empty(Open());
        AsAdmin();

        _ = Run("send", "impl-1@" + Team, "--ref", request, "--kind", "brief");

        var dispatch = Assert.Single(Open());
        Assert.Equal("impl-1", dispatch.ToName);
        Assert.Equal(RequestId, dispatch.RequestId);

        File.WriteAllText(request.Replace("--request.md", "--response.md", StringComparison.Ordinal), $"---\nid: {RequestId}\ntype: response\n---\n\n## [0] tldr\n- [1] status: DONE\n");

        Assert.Empty(Open());
    }

    [Fact]
    public void NegativeANoteNeverCountsAndARequestFromAnotherRoleIsNotCountedEvenWhenBriefed()
    {
        var request = NewRequest("implementor");
        _ = Run("send", "impl-1@" + Team, "--ref", request, "--kind", "brief");
        _ = Run("send", "impl-1@" + Team, "--text", "just a note", "--kind", "note");

        Assert.Empty(Open());
    }

    [Fact]
    public void NegativeABriefOfAnotherRequestGivesThisOneNoAddressee()
    {
        _ = NewRequest("orchestrator");
        var OtherId = Fresh("abce");
        var made = Run("new", "--type", "request", "--to", "implementor", "--from", "orchestrator", "--slug", "t2", "--title", "Other task", "--id", OtherId, "--team", Team);
        var other = _sandbox.MsgFile(made);
        AsAdmin();

        _ = Run("send", "impl-1@" + Team, "--ref", other, "--kind", "brief");

        Assert.Equal(OtherId, Assert.Single(Open()).RequestId);
    }

    /// <summary>ah2-150 D2': the admin that owns the team sends its brief, with no --from, as orchestrator@team, so the watch counts it as delivered.</summary>
    [Fact]
    public void TheAdminOrchestratorsBriefIsFromTheOrchestratorAndNotUndelivered()
    {
        var fresh = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + "-abcf";
        var request = NewRequest("orchestrator", fresh);
        NameAddressee(request, "impl-1");
        AsAdmin();

        _ = Run("send", "impl-1@" + Team, "--ref", request, "--kind", "brief");

        var box = Assert.Single(_graph.Mailbox.ListBoxes(), static b => b.Address == "impl-1@" + Team);
        Assert.Equal("orchestrator@" + Team, Assert.Single(_graph.Mailbox.ReadEntries(box.Dir).Entries).Member("from")!.Value.AsString());
        var host = new WatchHostAdapter(_graph, Ah.Roster.RosterModule.CreateScreenRules(), _graph.Io.Clock, WatchHostSeams.Unavailable);
        var now = (double)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        Assert.False(Assert.Single(_sandbox.Mirrored(() => host.Dispatches("o", now))!, c => c.RequestId == fresh).Undelivered);
    }

    /// <summary>ah2-296 L2: a brief sent with no dispatch row (the CLI send by itself, as `msg send --ref &lt;id&gt;` is) is timed from its entry; negative: unsent, it has no send time.</summary>
    [Fact]
    public void ABriefSentWithoutADispatchRowIsTimedFromItsEntryAndNegativeAnUnsentOneHasNoSendTime()
    {
        var fresh = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + "-abce";
        var request = NewRequest("orchestrator", fresh);
        NameAddressee(request, "impl-1");
        AsAdmin();
        var host = new WatchHostAdapter(_graph, Ah.Roster.RosterModule.CreateScreenRules(), _graph.Io.Clock, WatchHostSeams.Unavailable);
        Ah.Watch.DispatchCandidate Candidate() =>
            Assert.Single(_sandbox.Mirrored(() => host.Dispatches("o", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))!, c => c.RequestId == fresh);

        Assert.Null(Candidate().SentMs);

        _ = Run("send", "impl-1@" + Team, "--ref", request, "--kind", "brief");

        var box = Assert.Single(_graph.Mailbox.ListBoxes(), static b => b.Address == "impl-1@" + Team);
        var entryCreated = Assert.Single(_graph.Mailbox.ReadEntries(box.Dir).Entries).Member("created")!.Value.AsString();
        Assert.True(Ah.Core.Time.Timestamps.TryParseMs(entryCreated, out var sentMs));
        Assert.Equal(sentMs, Candidate().SentMs);
    }

    /// <summary>ah2-296 L3: an open request to a reviewer quiets only the request it reviews.</summary>
    [Theory]
    [InlineData("implementor", "a1", "reviewer", "R", null, true)] // its parent is the request
    [InlineData("implementor", "a1", "reviewer", null, "a1", true)] // no parent, same item, the request is not to a reviewer
    [InlineData("reviewer", "a1", "reviewer", null, "a1", false)] // an idle reviewer that has not reported is not quieted by another review on its item
    [InlineData("docs-writer", "a1", "reviewer", "OTHER", "a1", false)] // another role on the item: that review is of another request
    [InlineData("docs-writer", "a1", "reviewer", null, "a1", true)] // no parent: the review cannot say whose work it is, so another role on the item is quieted too (N1)
    [InlineData("implementor", "a1", "qa", "R", "a1", false)] // not to a reviewer
    public void AReviewQuietsOnlyTheRequestItReviews(string requestTo, string item, string reviewTo, string? parent, string? reviewItem, bool quiets)
    {
        Assert.Equal(quiets, WatchHostAdapter.IsReviewOf("R", requestTo, item, "V", reviewTo, parent, reviewItem));
        Assert.False(WatchHostAdapter.IsReviewOf("R", "reviewer", item, "R", "reviewer", null, item));
    }

    /// <summary>ah2-296 L1: a part is read from the message store, with no message file on disk; negative: before it, no part time.</summary>
    [Fact]
    public void APartIsReadFromTheStoreWithNoFileOnDiskAndNegativeNoPartNoTime()
    {
        var fresh = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + "-abcd";
        var request = NewRequest("orchestrator", fresh);
        NameAddressee(request, "impl-1");
        AsAdmin();
        _ = Run("send", "impl-1@" + Team, "--ref", request, "--kind", "brief");
        var host = new WatchHostAdapter(_graph, Ah.Roster.RosterModule.CreateScreenRules(), _graph.Io.Clock, WatchHostSeams.Unavailable);
        Ah.Watch.DispatchCandidate Candidate() =>
            Assert.Single(_sandbox.Mirrored(() => host.Dispatches("o", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))!, c => c.RequestId == fresh);

        Assert.Null(Candidate().PartMs);

        var response = Ah.TestKit.TestMsgStore.Plant(
            _sandbox.AhHome, request.Replace("--request.md", "--response.md", StringComparison.Ordinal), $"---\nid: {fresh}\ntype: response\npartial: true\n---\n\n## part 1\n- first deliverable\n");
        File.Delete(response);

        Assert.False(File.Exists(response));
        Assert.Equal(_graph.Hierarchy.Core.MsgTimeMs(response), Candidate().PartMs);
        Assert.NotNull(Candidate().PartMs);
    }

    /// <summary>ah2-32: this test process is the Claude harness and the user granted it admin rights; it is the user holding session "o".</summary>
    private void AsAdmin()
    {
        _runner.Ps = argv => argv.Contains("command=") && argv[^1] == Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) ? "claude\n" : FakeRunner.SelfOnly(argv);
        _graph.HomeDocs.Write("home.admin-grant", "{\"pid\":" + Environment.ProcessId + ",\"pid_start\":\"" + FakeRunner.SelfStart + "\",\"session_id\":\"o\"}");
        Assert.Equal(PrincipalKind.User, _graph.Mailbox.PrincipalOf(Environment.ProcessId).Kind);
    }

    private sealed class Routes : IRouteRegistry
    {
        internal Dictionary<string, Func<RouteRequest, CancellationToken, Task<RouteResponse>>> All { get; } = new(StringComparer.Ordinal);

        public void Add(string pattern, Func<RouteRequest, CancellationToken, Task<RouteResponse>> handler) => All.Add(pattern, handler);
    }

    private sealed class AnyContext : IRequestContextChecker
    {
        public RequestContextProblem? Check(RequestContext context) => null;
    }

    /// <summary>A graph whose daemon is up and serves the real /v1/send route (as DaemonServices.RegisterMesh wires it); every other call falls back to files.</summary>
    private sealed record DaemonUp(ServiceGraph Graph, FakeDaemonClient Daemon, Func<int> Sends, Func<string, RouteResponse> Route);

    /// <param name="refuseFirst">The first send is answered from_mismatch without reaching the route.</param>
    private DaemonUp WithDaemonUp(bool refuseFirst = false)
    {
        var routes = new Routes();
        var sends = 0;
        var daemon = new FakeDaemonClient { Info = FakeDaemonClient.Up() };
        daemon.Reply = r =>
        {
            if (r.PathAndQuery == "/v1/health")
            {
                return new DaemonReply(200, "{\"impl\":\"native\",\"protocols\":[1,2]}"u8.ToArray());
            }

            if (!r.PathAndQuery.StartsWith("/v1/send", StringComparison.Ordinal))
            {
                throw new IOException("only the send route is served here; the client writes state for itself");
            }

            sends++;
            if (refuseFirst && sends == 1)
            {
                return new DaemonReply(403, "{\"error\":{\"code\":\"from_mismatch\",\"message\":\"injected\"}}"u8.ToArray());
            }

            var answer = Route(r.Body);
            return new DaemonReply(answer.Status, answer.Body);
        };
        RouteResponse Route(ReadOnlyMemory<byte> body) =>
            routes.All["POST /v1/send"](new RouteRequest("POST", "/v1/send", string.Empty, new Dictionary<string, string>(), body, new Dictionary<string, string>(), Environment.ProcessId), CancellationToken.None).GetAwaiter().GetResult();
        var g = ServiceGraph.Build(PlatformModule.CreateIo(null), _env, _runner, daemon);
        var scope = new RequestScope();
        var rule = Ah.Mesh.MeshModule.CreateMeshRule(g.Links, _env);
        var effects = new MeshEffects(g, scope);
        var lookups = Ah.Mesh.MeshModule.CreateContractLookups(g.Mailbox, g.Hierarchy.Core, g.Config);
        Ah.Mesh.MeshModule.Register(
            routes,
            new Ah.Mesh.MeshRouteDependencies(
                Ah.Mesh.MeshModule.CreateContract(g.Io.Files, _env),
                Ah.Mesh.MeshModule.CreateContractRoles(g.Config, Ah.Config.ConfigModule.CreateRoleContracts(g.Config, g.Io.Files, _env, g.Io.HookErrors)),
                lookups,
                Ah.Mesh.MeshModule.CreateMeshSender(g.Mailbox, rule, g.Links, g.Hierarchy, g.Io.Clock, g.Writer, g.Io.Files, runFacts: g.RunFacts),
                Ah.Mesh.MeshModule.CreateJoinCommands(rule, g.Links, g.Hierarchy.Core, g.Hierarchy.Messages, g.Io.Files, g.Io.Clock, g.Mailbox, lookups, g.Config, _env, g.Io.Processes, effects, effects, g.RunFacts),
                Ah.Mesh.MeshModule.CreateLinkCommands(rule, g.Links, g.Hierarchy.Core, g.Hierarchy.Messages, g.Io.Files, g.Io.Clock, effects, effects, g.RunFacts),
                g.Mailbox,
                new AnyContext(),
                null));
        return new DaemonUp(g, daemon, () => sends, body => _sandbox.Mirrored(() => Route(System.Text.Encoding.UTF8.GetBytes(body))));
    }

    private (int Code, string Err) RunOn(ServiceGraph g, params string[] args)
    {
        var context = new CliContext(_env, new RecordingExec(), new FakeRunner(), "ahd", _sandbox.Executable("install/ahd"), _sandbox.Home, 501, null, new Lazy<ServiceGraph>(g));
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = _sandbox.Mirrored(() => MsgVerb.Run(context, [.. args, "--session", "o", "--plain", "--cwd", _sandbox.Root], stdout, stderr));
        return (code, stderr.ToString());
    }

    private static string? OnlyFrom(ServiceGraph g) =>
        g.Mailbox.ListBoxes().FirstOrDefault(static b => b.Address == "impl-1@" + Team) is { } box && g.Mailbox.ReadEntries(box.Dir).Entries is { Count: 1 } entries
            ? entries[0].Member("from")!.Value.AsString()
            : null;

    /// <summary>ah2-150 D6: with ahd up the admin's brief goes through the real /v1/send route and is stored from the Orchestrator, as with ahd down.</summary>
    [Fact]
    public void TheAdminOrchestratorsBriefIsFromTheOrchestratorWithTheDaemonUp()
    {
        var request = NewRequest("orchestrator");
        NameAddressee(request, "impl-1");
        AsAdmin();
        var up = WithDaemonUp();

        var (code, err) = RunOn(up.Graph, "send", "impl-1@" + Team, "--ref", request, "--kind", "brief");

        Assert.True(code == 0, err + string.Join(" ", up.Daemon.Requests.Select(static q => q.Method + " " + q.PathAndQuery)));
        Assert.Equal(1, up.Sends());
        Assert.Equal("orchestrator@" + Team, OnlyFrom(up.Graph));
    }

    /// <summary>The team file names <paramref name="session"/> as its Orchestrator under a pid that is not this process (a resumed harness when it is "o").</summary>
    private void TheRecordNames(string session) =>
        _sandbox.PlantTeam(
            Path.Combine(Hierarchy, "teams", Team + ".json"),
            "{\"team_id\":\"t-tm\",\"orchestrator\":{\"pid\":2147483646,\"pid_start\":\"" + FakeRunner.SelfStart + "\",\"session_id\":\"" + session + "\"},"
            + "\"members\":[{\"name\":\"impl-1\",\"role\":\"implementor\",\"route\":\"pane\",\"kind\":\"claude\"}],\"streams\":{}}\n");

    /// <summary>ah2-158 D1/D4: the record still names session "o" under its old pid; the route proves "o" by its role, so the brief is stored from the Orchestrator on the first try and the fallback never fires.</summary>
    [Fact]
    public void AResumedOrchestratorsBriefIsFromTheOrchestratorOnTheFirstTry()
    {
        var request = NewRequest("orchestrator");
        NameAddressee(request, "impl-1");
        AsAdmin();
        TheRecordNames("o");
        var up = WithDaemonUp();

        var (code, err) = RunOn(up.Graph, "send", "impl-1@" + Team, "--ref", request, "--kind", "brief");

        Assert.True(code == 0, err);
        Assert.Equal((1, "orchestrator@" + Team), (up.Sends(), OnlyFrom(up.Graph)));
    }

    /// <summary>ah2-150 D7, kept as a safety net (ah2-158 D4): an automatically chosen Orchestrator the route refuses is sent once more as user. The refusal is injected, since the CLI and the route now share one predicate.</summary>
    [Fact]
    public void AnAutomaticallyChosenOrchestratorTheRouteRefusesIsSentOnceMoreAsUser()
    {
        var request = NewRequest("orchestrator");
        NameAddressee(request, "impl-1");
        AsAdmin();
        var up = WithDaemonUp(refuseFirst: true);

        var (code, err) = RunOn(up.Graph, "send", "impl-1@" + Team, "--ref", request, "--kind", "brief");

        Assert.True(code == 0, err);
        Assert.Equal((2, "user"), (up.Sends(), OnlyFrom(up.Graph)));
    }

    /// <summary>
    /// ah2-150 D7 negative: an explicit --from is never retried. The admin's explicit --from orchestrator@tm is refused by the CLI itself
    /// (ChooseFrom: a caller with no address may only say user), so nothing is sent at all.
    /// </summary>
    [Fact]
    public void NegativeAnExplicitFromIsNeverRetried()
    {
        var request = NewRequest("orchestrator");
        NameAddressee(request, "impl-1");
        AsAdmin();
        TheRecordNames("o");
        var up = WithDaemonUp(refuseFirst: true);

        var (code, err) = RunOn(up.Graph, "send", "impl-1@" + Team, "--ref", request, "--kind", "brief", "--from", "orchestrator@" + Team);

        Assert.NotEqual(0, code);
        Assert.Contains("only --from user is allowed", err, StringComparison.Ordinal);
        Assert.Equal(0, up.Sends());
        Assert.Null(OnlyFrom(up.Graph));
    }

    /// <summary>ah2-158 negative: the caller holds session "o" with orchestrator@tm registered, but the record names another session under another pid, so the route refuses it.</summary>
    [Fact]
    public void NegativeAHeldSessionTheRecordDoesNotNameIsNotTheOrchestratorAtTheRoute()
    {
        TheRecordNames("x");
        var up = WithDaemonUp();

        var r = up.Route("{\"to\":\"impl-1@" + Team + "\",\"text\":\"hello\",\"from\":\"orchestrator@" + Team + "\",\"session_id\":\"o\"," + ContextBody + "}");

        Assert.Equal((403, "from_mismatch"), (r.Status, ErrorCode(r)));
        Assert.Null(OnlyFrom(up.Graph));
    }

    /// <summary>ah2-158 negative (D3): the record names session "z", registered to another process; a caller that does not hold "z" naming it in the body gains nothing.</summary>
    [Fact]
    public void NegativeTheRecordsSessionNamedInTheBodyByACallerThatDoesNotHoldItIsRefused()
    {
        var teamFile = Path.Combine(Hierarchy, "teams", Team + ".json");
        TheRecordNames("z");
        Assert.True(_graph.Mailbox.WriteRegistration(new Ah.Mesh.RegistrationWrite(
            "z", [new Ah.Mesh.BoxRef("orchestrator@" + Team, _graph.Mailbox.BoxDirFor(teamFile, "orchestrator"))], teamFile, "claude", 2147483646, PidStart: FakeRunner.SelfStart, Bound: true)));
        var up = WithDaemonUp();

        var r = up.Route("{\"to\":\"impl-1@" + Team + "\",\"text\":\"hello\",\"from\":\"orchestrator@" + Team + "\",\"session_id\":\"z\"," + ContextBody + "}");

        Assert.Equal((403, "not_your_session"), (r.Status, ErrorCode(r)));
        Assert.Null(OnlyFrom(up.Graph));
    }

    /// <summary>ah2-158 D2: the CLI's owner predicate and the daemon's orchestrator@tm proof agree: same pid, resumed pid, another session.</summary>
    [Theory]
    [InlineData("same-pid", true)]
    [InlineData("o", true)]
    [InlineData("x", false)]
    public void TheCliAndTheDaemonAgreeOnTheOrchestrator(string recorded, bool owns)
    {
        if (recorded != "same-pid")
        {
            TheRecordNames(recorded);
        }

        var record = _graph.Roster.ReadTeamFileAt(Path.Combine(Hierarchy, "teams", Team + ".json"));
        var cli = _graph.Roster.IsTeamOwnedBy(record, new TeamInvoker(Environment.ProcessId, null, FakeRunner.SelfStart));
        var daemon = Assert.Single(_graph.Mailbox.PrincipalOf(Environment.ProcessId).Sessions, static s => s.SessionId == "o").Addresses.Contains("orchestrator@" + Team);

        Assert.Equal((owns, owns), (cli, daemon));
    }

    private string ContextBody => "\"cwd\":" + Ah.Core.Json.JsonText.Stringify(_sandbox.Root, 0) + ",\"hierarchy_dir\":" + Ah.Core.Json.JsonText.Stringify(Hierarchy, 0);

    private static string? ErrorCode(RouteResponse r) =>
        Ah.Core.Json.JsonText.Parse(System.Text.Encoding.UTF8.GetString(r.Body.Span)).Member("error")?.Member("code") is { IsText: true } c ? c.AsString() : null;

    /// <summary>b271: the watch keeps the brief listing across ticks while no mail is written; an ack is a write, so the next tick reads the brief as read.</summary>
    [Fact]
    public void ABriefKeptAcrossTicksIsReadOnceItsEntryIsAcked()
    {
        // the derived rows skip an id stamped past the horizon: this request is made now
        var fresh = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + "-abcd";
        _ = Run("send", "impl-1@" + Team, "--ref", NewRequest("orchestrator", fresh), "--kind", "brief");

        // both sessions are this process; the watch reads the orchestrator's teams only while its registration is the live one
        FakeRunner.RegisterSelf(_graph.Mailbox, "o", Path.Combine(Hierarchy, "teams", Team + ".json"), "orchestrator@" + Team);
        var host = new WatchHostAdapter(_graph, Ah.Roster.RosterModule.CreateScreenRules(), _graph.Io.Clock, WatchHostSeams.Unavailable);
        var now = (double)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        bool Unread(double at) => Assert.Single(_sandbox.Mirrored(() => host.Dispatches("o", at))!, c => c.RequestId == fresh).BriefUnread;
        Assert.NotNull(_graph.Mailbox.MailChangeToken());
        Assert.True(Unread(now));
        Assert.True(Unread(now + 1));

        var box = Assert.Single(_graph.Mailbox.ListBoxes(), static b => b.Address == "impl-1@" + Team);
        var id = Assert.Single(_graph.Mailbox.ReadEntries(box.Dir).Entries).Member("id")!.Value.AsString();
        Assert.Single(_graph.Mailbox.AckIds(box.Dir, [id], "impl-1").Acked);

        Assert.False(Unread(now + 2));
    }

    /// <summary>
    /// b283: the watch skips the box of a member with no roster row, no live exchange and no entry inside the horizon. Still read: the box of a dismissed
    /// member a live request names, and one whose brief is of a request the read set does not attribute to it (the foreign-pool path).
    /// </summary>
    [Theory]
    [InlineData("gone-2", false, true)]
    [InlineData("gone-1", false, true)]
    [InlineData("gone-1", true, false)]
    public void AClosedMembersBoxIsSkippedButNotOneALiveRequestNames(string box, bool oldEntry, bool read)
    {
        // gone-1 and gone-2 have no roster row; the open request is to gone-2, its brief sits in the box under test
        var fresh = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + "-abcd";
        var request = NewRequest("orchestrator", fresh);
        NameAddressee(request, "gone-2");
        var teamFile = Path.Combine(Hierarchy, "teams", Team + ".json");
        if (oldEntry)
        {
            // the box's last mail is a week old: planted, since a send stamps its entry now
            var entry = new Ah.Core.Json.JsonObject { ["id"] = "20261001-000000-0old", ["kind"] = "brief", ["ref"] = request, ["from"] = "orchestrator@" + Team, ["to"] = box + "@" + Team };
            Ah.TestKit.TestPlant.Mail(_graph.Writer, _graph.Mailbox.BoxDirFor(teamFile, box), entry, _sandbox.Root);
        }
        else
        {
            var roster = File.ReadAllText(teamFile);
            const string Row = "{\"name\":\"impl-1\",\"role\":\"implementor\",\"route\":\"pane\",\"kind\":\"claude\"}";
            _sandbox.PlantTeam(teamFile, roster.Replace(Row, Row + "," + Row.Replace("impl-1", box, StringComparison.Ordinal), StringComparison.Ordinal));
            _ = Run("send", box + "@" + Team, "--ref", request, "--kind", "brief");
            _sandbox.PlantTeam(teamFile, roster); // dismissed: the row is gone, the box stays
        }

        FakeRunner.RegisterSelf(_graph.Mailbox, "o", teamFile, "orchestrator@" + Team);
        var host = new WatchHostAdapter(_graph, Ah.Roster.RosterModule.CreateScreenRules(), _graph.Io.Clock, WatchHostSeams.Unavailable);
        var candidate = Assert.Single(_sandbox.Mirrored(() => host.Dispatches("o", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))!, c => c.RequestId == fresh);

        Assert.Equal(read ? box + "@" + Team : null, candidate.ToAddr);
    }
}
