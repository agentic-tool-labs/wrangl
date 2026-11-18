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
    private const string RequestId = "20261016-140001-abcd";
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

    private string NewRequest(string from, string id = RequestId)
    {
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

    [Theory]
    [InlineData("not-spawned", false)]
    [InlineData("impl-1", true)]
    public void AnUnsentRequestIsWatchedOnlyWhenItsNamedMemberHasASession(string name, bool watched)
    {
        NameAddressee(NewRequest("orchestrator"), name);

        Assert.Equal(watched, Open().Any(d => d.RequestId == RequestId));
    }

    [Fact]
    public void APausedMembersBriefIsNotWatchedAndAResumedOneIsWatchedFromTheResume()
    {
        NameAddressee(NewRequest("orchestrator"), "impl-1");
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
        const string OtherId = "20261016-140002-abce";
        var made = Run("new", "--type", "request", "--to", "implementor", "--from", "orchestrator", "--slug", "t2", "--title", "Other task", "--id", OtherId, "--team", Team);
        var other = _sandbox.MsgFile(made);

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
    private sealed record DaemonUp(ServiceGraph Graph, FakeDaemonClient Daemon, Func<int> Sends);

    private DaemonUp WithDaemonUp()
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
            var answer = routes.All["POST /v1/send"](new RouteRequest("POST", "/v1/send", string.Empty, new Dictionary<string, string>(), r.Body, new Dictionary<string, string>(), Environment.ProcessId), CancellationToken.None).GetAwaiter().GetResult();
            return new DaemonReply(answer.Status, answer.Body);
        };
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
        return new DaemonUp(g, daemon, () => sends);
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

    /// <summary>
    /// ah2-150 D7: the team file still names session "o" as its Orchestrator, so the CLI owns the team by that session
    /// (RosterCore.TeamOwnedBy) and offers orchestrator@tm, but it records another pid, so the route's proof
    /// (CallerIdentity.IsRecordedOrchestrator: pid and start) drops the address: the route refuses from_mismatch.
    /// </summary>
    private void TheRouteNoLongerProvesTheOrchestrator() =>
        _sandbox.PlantTeam(
            Path.Combine(Hierarchy, "teams", Team + ".json"),
            "{\"team_id\":\"t-tm\",\"orchestrator\":{\"pid\":2147483646,\"pid_start\":\"" + FakeRunner.SelfStart + "\",\"session_id\":\"o\"},"
            + "\"members\":[{\"name\":\"impl-1\",\"role\":\"implementor\",\"route\":\"pane\",\"kind\":\"claude\"}],\"streams\":{}}\n");

    [Fact]
    public void AnAutomaticallyChosenOrchestratorTheRouteRefusesIsSentOnceMoreAsUser()
    {
        var request = NewRequest("orchestrator");
        NameAddressee(request, "impl-1");
        AsAdmin();
        TheRouteNoLongerProvesTheOrchestrator();
        var up = WithDaemonUp();

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
        TheRouteNoLongerProvesTheOrchestrator();
        var up = WithDaemonUp();

        var (code, err) = RunOn(up.Graph, "send", "impl-1@" + Team, "--ref", request, "--kind", "brief", "--from", "orchestrator@" + Team);

        Assert.NotEqual(0, code);
        Assert.Contains("only --from user is allowed", err, StringComparison.Ordinal);
        Assert.Equal(0, up.Sends());
        Assert.Null(OnlyFrom(up.Graph));
    }

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
