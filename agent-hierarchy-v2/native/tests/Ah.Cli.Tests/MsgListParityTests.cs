using System.Globalization;
using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Cli.Verbs.Db;
using Ah.Cli.Verbs.Msg;
using Ah.Core.Text;
using Ah.Platform;
using Ah.StateDb;
using Ah.TestKit;

namespace Ah.Cli.Tests;

/// <summary>ah-msgs-fast S4: <c>msg list --json</c> answers from the store after the msgs cutover, for the default, <c>--to</c>, <c>--team</c> and every team.</summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class MsgListParityTests : IDisposable
{
    private static readonly DateTime Written = new(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);

    private readonly Sandbox _sandbox = new();
    private readonly string _proj;
    private readonly string _main;

    public MsgListParityTests()
    {
        // the real path: the cutover keys a pool by it, so a symlinked cwd (macOS /tmp) would list other request paths after it
        _proj = Path.Combine(PathGuard.RealPath(_sandbox.Root, strict: true)!, "proj");
        _main = Path.Combine(_proj, ".claude", "ah2");
        Directory.CreateDirectory(Path.Combine(_proj, ".git"));
        Directory.CreateDirectory(Path.Combine(_main, "msgs"));
    }

    public void Dispose() => _sandbox.Dispose();

    private string TeamFile(string team) => Path.Combine(_main, "teams", team + ".json");

    /// <summary>The team file; <paramref name="lane"/> gives it a running lane s1 led by so-1, opened before every fixture message.</summary>
    private void WriteTeam(string team, bool lane = false, string members = "")
    {
        _sandbox.PlantTeam(
            TeamFile(team),
            "{\"team_id\":\"t-" + team + "\",\"orchestrator\":{\"pid\":" + System.Environment.ProcessId.ToString(CultureInfo.InvariantCulture)
            + ",\"pid_start\":\"" + FakeRunner.SelfStart + "\",\"session_id\":\"o\"},\"members\":[" + members + "]"
            + (lane ? ",\"streams\":{\"s1\":{\"sub_orchestrator\":\"so-1\",\"state\":\"working\",\"opened\":\"2026-10-16T11:00:00Z\"}}" : string.Empty) + "}\n");
    }

    private void Message(string id, string type, string to, string team, string body, string parent = "null", string from = "orchestrator", string toName = "null")
    {
        var path = Path.Combine(_main, "msgs", $"{id}--{to}--t1--{type}.md");
        File.WriteAllText(
            path,
            $"---\nid: {id}\ntype: {type}\nto: {to}\nfrom: {from}\nslug: t1\nparent: {parent}\nreason: null\neta: small\nto_name: {toName}\nfrom_name: null\nteam: {team}\ncreated: 2026-10-16T12:00:00+00:00\n---\n\n{body}");
        File.SetLastWriteTimeUtc(path, Written);
    }

    private (ServiceGraph Graph, CliContext Context) Compose()
    {
        var variables = new Dictionary<string, string?> { ["AH_TEST"] = "1", ["AH_HOME"] = _sandbox.AhHome, ["HOME"] = _sandbox.Home };
        var env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables, new PluginInfo(_sandbox.PluginRoot, "wrangl", "1.0.0", "1"));
        var graph = ServiceGraph.Build(PlatformModule.CreateIo(null), env, FakeRunner.KnowingSelf(), new FakeDaemonClient { Reply = _ => new DaemonReply(0, default, "down") });
        FakeRunner.RegisterSelf(graph.Mailbox, "o", TeamFile("tm"), "orchestrator@tm");
        return (graph, new CliContext(env, new RecordingExec(), new FakeRunner(), "ahd", _sandbox.Executable("install/ahd"), _sandbox.Home, 501, null, new Lazy<ServiceGraph>(graph)));
    }

    /// <summary>The written files go through the one-way door, an hour ago: closed exchanges are out of the live window, so only the live flag decides the scope.</summary>
    private void CutOverAnHourAgo()
    {
        using (var open = StateDbModule.Open(_sandbox.AhHome, mayCreate: true))
        {
            Assert.Equal(StateDbAvailability.Ready, open.Status.Availability);
        }

        _ = Ah.TestKit.TestMsgStore.FilesRoute(_sandbox.AhHome);
        var (before, _) = Compose();
        var now = DateTimeOffset.UtcNow.AddHours(-1);
        using (var collection = MsgsCollector.Collect(_sandbox.AhHome, [_main], before.Hierarchy.Core, before.Io.Locks, now, TimeSpan.FromSeconds(5), lockRegistered: false, out var error))
        {
            Assert.True(collection is not null, error);
            var result = StateDbModule.CreateMsgsCutover().Import(_sandbox.AhHome, collection.Pools, false, now);
            Assert.True(result.Ok, result.Message);
        }

        Assert.True(StateDbModule.OpenDirect(_sandbox.AhHome).Write(static tx =>
        {
            tx.Execute("INSERT OR REPLACE INTO meta(key, value) VALUES ('mode.msgs', 'db'), ('authority.msgs', 'db')");
            return 0;
        }).Ok);
        Assert.True(StateDbModule.DomainFlipped(_sandbox.AhHome, "msgs"));
    }

    private static readonly string[][] Variants =
    [
        ["list", "--json", "--team", "tm"],
        ["list", "--json", "--team", "tm", "--to", "implementor"],
        ["list", "--json", "--team", "tm2"],
        ["list", "--json"],
    ];

    private string Lists(CliContext context, string[][]? variants = null)
    {
        var text = new System.Text.StringBuilder();
        foreach (var args in variants ?? Variants)
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exit = MsgVerb.Run(context, [.. args, "--orchestrator-pid", System.Environment.ProcessId.ToString(CultureInfo.InvariantCulture), "--cwd", _proj], stdout, stderr);
            Assert.True(exit == 0, string.Join(" ", args) + ": " + stderr);
            text.AppendLine(string.Join(" ", args) + " " + stdout.ToString().Trim());
        }

        return text.ToString();
    }

    private static readonly string[][] HistoryVariants =
    [
        ["list", "--plain", "--all", "--team", "tm"],
        ["list", "--plain", "--closed", "--team", "tm"],
        ["list", "--plain", "--all", "--team", "tm2"],
        ["list", "--plain", "--closed", "--team", "tm2"],
        ["list", "--plain", "--all", "--team", "tm", "--limit", "5"],
        ["list", "--plain", "--closed", "--team", "tm", "--limit", "5", "--to", "reviewer"],
        ["list", "--plain", "--closed", "--team", "tm2", "--limit", "5"],
        ["list", "--plain", "--all", "--team", "tm", "--limit", "0"],
        ["list", "--json", "--all", "--team", "tm", "--limit", "5"],
        ["list", "--json", "--closed", "--team", "tm2"],
    ];

    /// <summary>
    /// ah-msgs-fast-2 S5: <c>--all</c>/<c>--closed</c> page the newest ids, with an "N more" line, for a sparse team, a limit under one
    /// chunk, a recipient filter and <c>--limit 0</c>.
    /// </summary>
    [Fact]
    public void MsgListAllAndClosedPageTheStoreAndCountTheRest()
    {
        WriteTeam("tm", lane: true);
        WriteTeam("tm2");
        const string Report = "Done: the work is finished.\n";
        for (var i = 0; i < 330; i++)
        {
            var id = $"20261016-120000-b{i:D3}";
            var team = i % 97 == 5 ? "tm2" : "tm";
            var to = i % 4 == 0 ? "reviewer" : "implementor";
            Message(id, "request", to, team, "## [0] tldr\n- x\n");
            if (i % 3 != 0)
            {
                Message(id, "response", to, team, Report);
            }
        }

        // review F5: the running lane's charter, answered an hour before the cutover, so out of the live rows; it stays open, so --all
        // shows it open and --closed leaves it out
        Message("20261016-120001-ch01", "request", "sub-orchestrator", "tm", "## [0] tldr\n- x\n");
        Message("20261016-120001-ch01", "response", "sub-orchestrator", "tm", Report);

        CutOverAnHourAgo();

        var (_, afterContext) = Compose();
        var actual = Lists(afterContext, HistoryVariants);
        Assert.Contains("more (--limit N, 0 = all)", actual, StringComparison.Ordinal);
        Assert.Contains("b005", actual, StringComparison.Ordinal);
        Assert.Matches(@"ch01  sub-orchestrator  t1  \S+  open", actual);
    }

    /// <summary>
    /// Orchestrator ruling on ah-msgs-fast-2 §3.4: the default list's downstream section in db mode holds the dispatches of the live scope
    /// and of their descendants, read from the rows, and not the dispatches whose chain reaches no live request.
    /// </summary>
    [Fact]
    public void TheDefaultDownstreamSectionHoldsOnlyTheLiveScopesDispatches()
    {
        WriteTeam("tm", lane: true);
        const string Report = "Done: the work is finished.\n";

        // review F2: the running lane's charter cc06 and its sub-dispatch cc07, both answered an hour ago: the live scope holds the charter, so cc07 stays
        Message("20261016-120000-cc06", "request", "sub-orchestrator", "tm", "## [0] tldr\n- x\n");
        Message("20261016-120000-cc06", "response", "sub-orchestrator", "tm", Report);
        Message("20261016-120000-cc07", "request", "reviewer", "tm", "## [0] tldr\n- x\n", parent: "20261016-120000-cc06", from: "sub-orchestrator");
        Message("20261016-120000-cc07", "response", "reviewer", "tm", Report);
        Message("20261016-120000-cc01", "request", "implementor", "tm", "## [0] tldr\n- x\n");
        Message("20261016-120000-cc02", "request", "reviewer", "tm", "## [0] tldr\n- x\n", parent: "20261016-120000-cc01", from: "implementor");
        Message("20261016-120000-cc03", "request", "reviewer", "tm", "## [0] tldr\n- x\n", parent: "20261016-120000-cc01", from: "implementor");
        Message("20261016-120000-cc03", "response", "reviewer", "tm", Report);
        Message("20261016-120000-cc04", "request", "implementor", "tm", "## [0] tldr\n- x\n");
        Message("20261016-120000-cc04", "response", "implementor", "tm", Report);
        Message("20261016-120000-cc05", "request", "reviewer", "tm", "## [0] tldr\n- x\n", parent: "20261016-120000-cc04", from: "implementor");
        Message("20261016-120000-cc05", "response", "reviewer", "tm", Report);

        CutOverAnHourAgo();

        // cc05's chain reaches only cc04, answered an hour ago: out of the live scope, so out of the section; cc03 is answered too, but its parent is open
        string[][] variants = [["list", "--json", "--team", "tm"]];
        var prefix = string.Join(" ", variants[0]) + " ";
        var (_, afterContext) = Compose();
        var doc = Ah.Core.Json.JsonText.Parse(Lists(afterContext, variants).Trim()[prefix.Length..]).AsObject();
        var ids = doc["downstream"].AsArray().Select(static d => d.Member("id")!.Value.AsString()).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(["20261016-120000-cc02", "20261016-120000-cc03", "20261016-120000-cc07"], ids);
    }

    [Fact]
    public void MsgListJsonListsBothTeamsOpenRequestsAndNoClosedExchange()
    {
        WriteTeam("tm");
        WriteTeam("tm2");
        const string Report = "Done: the work is finished.\n";
        Message("20261016-120000-aa01", "request", "implementor", "tm", "## [0] tldr\n- x\n");
        Message("20261016-120000-aa02", "request", "implementor", "tm", "## [0] tldr\n- x\n");
        Message("20261016-120000-aa02", "response", "implementor", "tm", Report);
        Message("20261016-120000-aa03", "request", "reviewer", "tm2", "## [0] tldr\n- x\n");
        Message("20261016-120000-aa04", "request", "reviewer", "tm2", "## [0] tldr\n- x\n");
        Message("20261016-120000-aa04", "response", "reviewer", "tm2", Report);
        Message("20261016-120000-aa05", "request", "reviewer", "tm", "## [0] tldr\n- x\n");

        CutOverAnHourAgo();
        var (_, afterContext) = Compose();
        var actual = Lists(afterContext);

        // every team lists both teams' open requests; the default lists no closed exchange
        Assert.Contains("\"20261016-120000-aa03\"", actual.Split('\n')[3], StringComparison.Ordinal);
        Assert.Contains("\"20261016-120000-aa05\"", actual.Split('\n')[3], StringComparison.Ordinal);
        Assert.DoesNotContain("\"20261016-120000-aa02\"", actual, StringComparison.Ordinal);
    }

    /// <summary>The bodies <c>/v1/view</c> and <c>/v1/actions</c> serve (ViewService: the "/" body, the "/actions" body's <c>body</c>), built on the graph at <paramref name="nowMs"/>.</summary>
    private string Views(ServiceGraph graph, double nowMs)
    {
        var host = new WatchHostAdapter(graph, Ah.Roster.RosterModule.CreateScreenRules(), graph.Io.Clock, WatchHostSeams.Unavailable);
        graph.DispatchHost = host;
        var source = Ah.Watch.WatchModule.CreateViewSource(new Ah.Watch.ViewSourceServices(
            graph.Io.Files, new Ah.Hierarchy.StreamDerivations(), new DaemonServices.ViewPlacement(graph), null, new FakeRunner(), _sandbox.Home,
            State: new StateServiceReader(graph.InternalStateRead), Layout: new MailboxLayout(graph.Mailbox, graph.Environment), Config: new ViewConfig(graph.Config),
            StoredBytes: graph.Hierarchy.Core.StoredBytes));
        // /v1/view?path= serves any node: the root, the orchestrator, each team (its dispatches); /v1/actions the root's and the lane's
        var text = new System.Text.StringBuilder();
        string Add(string path)
        {
            var body = Ah.Watch.WatchModule.BuildView(source, path, nowMs).Body;
            _ = text.Append(path).Append('\n').Append(Ah.Core.Json.JsonText.Stringify(path.StartsWith("/actions", StringComparison.Ordinal) && body.ContainsKey("body") ? body["body"] : body, 1)).Append('\n');
            return path;
        }

        static IEnumerable<string> Children(Ah.Core.Json.JsonObject body) => body["children"].AsArray().ToList().Select(static c => c.AsObject()["path"].AsString());
        _ = Add("/");
        _ = Add("/actions");
        foreach (var o in Children(Ah.Watch.WatchModule.BuildView(source, "/", nowMs).Body))
        {
            foreach (var t in Children(Ah.Watch.WatchModule.BuildView(source, Add(o), nowMs).Body))
            {
                _ = Add(t);
                _ = Add("/actions" + t + "/s/s1");
            }
        }

        return text.ToString();
    }

    [Fact]
    public void TheClockIsToldWhichOpenRequestIsARunningLanesCharterAndNoOtherIsMarked()
    {
        WriteTeam("tm", lane: true, "{\"role\":\"implementor\",\"name\":\"tm-impl\",\"route\":\"peer\"},{\"role\":\"sub-orchestrator\",\"name\":\"so-1\",\"route\":\"peer\"}");
        Message("20261016-120000-aa01", "request", "sub-orchestrator", "tm", "## [0] tldr\n- x\n", toName: "so-1");
        Message("20261016-120000-aa02", "request", "implementor", "tm", "## [0] tldr\n- x\n", toName: "tm-impl");
        CutOverAnHourAgo();
        var (graph, _) = Compose();
        foreach (var id in new[] { "20261016-120000-aa01", "20261016-120000-aa02" })
        {
            var path = Directory.GetFiles(Path.Combine(_main, "msgs"), id + "--*")[0];
            File.AppendAllText(graph.Hierarchy.Peers.PeerPendingPath(), "{\"type\":\"dispatch\",\"session_id\":\"o\",\"request_id\":\"" + id + "\",\"path\":\"" + path + "\"}\n");
        }

        var host = new WatchHostAdapter(graph, Ah.Roster.RosterModule.CreateScreenRules(), graph.Io.Clock, WatchHostSeams.Unavailable);

        var candidates = host.Dispatches("o", new DateTimeOffset(Written).AddHours(1).ToUnixTimeMilliseconds()).ToDictionary(static c => c.RequestId);

        Assert.True(candidates["20261016-120000-aa01"].LaneCharter);
        Assert.False(candidates["20261016-120000-aa02"].LaneCharter);
    }

    /// <summary>
    /// ah-msgs-fast-2 §4 S3 (review F4): <c>/v1/view</c> and <c>/v1/actions</c> serve stored exchanges, open, answered, charter and sparse-team.
    /// </summary>
    [Fact]
    public void ViewAndActionsJsonServeTheStoredExchanges()
    {
        WriteTeam("tm", lane: true, "{\"role\":\"implementor\",\"name\":\"tm-impl\",\"route\":\"peer\"},{\"role\":\"reviewer\",\"name\":\"tm-rev\",\"route\":\"peer\"},{\"role\":\"sub-orchestrator\",\"name\":\"so-1\",\"route\":\"peer\"}");
        WriteTeam("tm2");
        const string Report = "Done: the work is finished.\n";
        Message("20261016-120000-aa01", "request", "implementor", "tm", "## [0] tldr\n- x\n", toName: "tm-impl");
        Message("20261016-120000-aa02", "request", "implementor", "tm", "## [0] tldr\n- x\n", toName: "tm-impl");
        Message("20261016-120000-aa02", "response", "implementor", "tm", Report);
        Message("20261016-120000-aa03", "request", "reviewer", "tm2", "## [0] tldr\n- x\n");
        Message("20261016-120000-aa06", "request", "sub-orchestrator", "tm", "## [0] tldr\n- x\n", toName: "so-1");
        Message("20261016-120000-aa06", "response", "sub-orchestrator", "tm", Report);
        Message("20261016-120000-aa07", "request", "reviewer", "tm", "## [0] tldr\n- x\n", parent: "20261016-120000-aa06", from: "sub-orchestrator", toName: "tm-rev");

        CutOverAnHourAgo();
        var (after, _) = Compose();

        // the dispatch records make the open requests the members' owed dispatches
        foreach (var (id, to, member) in new[] { ("20261016-120000-aa01", "implementor", "tm-impl@tm"), ("20261016-120000-aa07", "reviewer", "tm-rev@tm") })
        {
            after.Hierarchy.Peers.AppendDispatchRecord("o", id, to, Path.Combine(_main, "msgs", $"{id}--{to}--t1--request.md"), member);
        }

        var actual = Views(after, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Assert.Contains("20261016-120000-aa01", actual, StringComparison.Ordinal);
        Assert.Matches("\"owed\": [1-9]", actual);
    }
}
