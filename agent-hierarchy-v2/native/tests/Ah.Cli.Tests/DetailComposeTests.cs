using Ah.Cli.Composition;
using Ah.Core.Json;
using Ah.Mesh;
using Ah.Platform;
using Ah.Roster;
using Ah.TestKit;
using Ah.Watch;

namespace Ah.Cli.Tests;

/// <summary>
/// The orchestrator detail document read through the daemon's own gap services on a real service graph: the open requests, the
/// work trees and the pipeline run each come out of the files the hierarchy writes, not out of a stand-in.
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class DetailComposeTests : IDisposable
{
    private const string Session = "orch-tm";
    private const string RequestId = "20261016-120000-ab12";
    private const string AnchorId = "20261016-110000-cd34";

    private readonly Sandbox _sandbox = new();

    public void Dispose() => _sandbox.Dispose();

    private string Hier => Path.Combine(_sandbox.Home, "proj", ".claude", "ah2");

    private string Worktree => Path.Combine(_sandbox.Home, "wt-impl");

    private static string Request(string pool, string id, string slug, string to)
    {
        _ = Directory.CreateDirectory(Path.Combine(pool, "msgs"));
        var path = Path.Combine(pool, "msgs", $"{id}--{to}--{slug}--request.md");
        var created = DateTimeOffset.UtcNow.AddMinutes(-5).ToString("yyyy-MM-dd'T'HH:mm:sszzz", System.Globalization.CultureInfo.InvariantCulture);
        File.WriteAllText(
            path,
            $"---\nid: {id}\ntype: request\nto: {to}\nfrom: orchestrator\nslug: {slug}\nparent: null\nreason: null\neta: small\nto_name: tm-impl\nfrom_name: null\nteam: tm\ncreated: {created}\n---\n\n## [0] tldr\n- x\n");
        return path;
    }

    private (IViewSource Source, ServiceGraph Graph, string Path) Arranged(bool withRun, bool withDispatch)
    {
        _ = Directory.CreateDirectory(Path.Combine(Worktree, ".git"));
        var teamFile = Path.Combine(Hier, "teams", "tm.json");
        _sandbox.PlantTeam(
            teamFile,
            "{\"team\":\"tm\",\"team_id\":\"t-tm\",\"transport\":\"tmux\",\"mesh\":{\"policy\":\"open\"},"
            + $"\"orchestrator\":{{\"pid\":{Environment.ProcessId},\"session_id\":\"{Session}\"}},"
            + $"\"members\":[{{\"role\":\"implementor\",\"name\":\"tm-impl\",\"route\":\"peer\",\"expected_root\":\"{Worktree}\"}}],\"streams\":{{}}}}\n");
        var variables = new Dictionary<string, string?> { ["AH_TEST"] = "1", ["AH_HOME"] = _sandbox.AhHome, ["HOME"] = _sandbox.Home };
        var env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables);
        var io = PlatformModule.CreateIo(null);
        var graph = ServiceGraph.Build(io, env, new FakeRunner(), new FakeDaemonClient());
        var box = Path.Combine(_sandbox.Home, "proj", "boxes", "orchestrator");
        _ = Directory.CreateDirectory(box);
        Assert.True(graph.Mailbox.WriteRegistration(new RegistrationWrite(Session, [new BoxRef("orchestrator@tm", box)], teamFile, "claude")));
        if (withDispatch)
        {
            var path = Request(Hier, RequestId, "t1", "implementor");
            graph.Hierarchy.Peers.AppendDispatchRecord(Session, RequestId, "implementor", path, "tm-impl@tm");
        }

        if (withRun)
        {
            _ = Request(Hier, AnchorId, Ah.Hierarchy.HierarchyModule.PipelineAnchorSlug, "orchestrator");
            var log = graph.Hierarchy.Decisions.DecisionLogPath(Hier, AnchorId);
            _ = Directory.CreateDirectory(Path.GetDirectoryName(log)!);
            File.WriteAllText(
                log,
                "{\"id\":\"d1\",\"time\":\"2026-10-16T11:10:00.000Z\",\"run\":\"" + AnchorId + "\",\"kind\":\"decided\",\"item\":\"AC-3\",\"question\":null,\"options\":null,\"choice\":\"A\",\"decision\":\"keep\",\"decider\":\"orchestrator\",\"rationale\":\"r\",\"why_user\":null,\"dangerous\":false,\"qkey\":\"k\"}\n");
        }

        var host = new WatchHostAdapter(graph, RosterModule.CreateScreenRules(), io.Clock, WatchHostSeams.Unavailable);
        var links = new Links(Ah.StateDb.StateDbModule.CreateLinks(env.AhHome), env, graph.Roster, graph.Hierarchy.Core, graph.Hierarchy.Messages, io.Clock, io.Random);
        graph.DispatchHost = host;
        var gaps = new DaemonServices.ViewPlacement(graph);
        var source = WatchModule.CreateViewSource(new ViewSourceServices(
            io.Files, new Ah.Hierarchy.StreamDerivations(), gaps, null, new FakeRunner(), _sandbox.Home, State: new StateServiceReader(graph.InternalStateRead), Layout: new MailboxLayout(graph.Mailbox, graph.Environment), Config: new ViewConfig(graph.Config)));
        Ah.TestKit.TestMsgStore.Sync(_sandbox.AhHome, _sandbox.Root); // the hand-written requests, stored as msg new would
        var root = WatchModule.BuildView(source, "/", Now());
        var node = root.Body["children"].AsArray().ToList().Select(static c => c.AsObject()).First();
        return (source, graph, node["path"].AsString());
    }

    private static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static JsonObject Field(JsonObject detail, string key) =>
        detail["body"].AsObject()["fields"].AsArray().ToList().Select(static f => f.AsObject()).Single(f => f["key"].AsString() == key);

    private static JsonObject Value(JsonObject field) => field["value"].AsObject();

    private static JsonObject Run(JsonObject detail) =>
        Field(detail, "pipeline")["value"].AsArray().ToList().Select(static f => f.AsObject()).Single(f => f["key"].AsString() == "run");

    [Fact]
    public void TheOpenRequestTheWorkTreesAndThePipelineRunComeOutOfTheRealGraph()
    {
        var (source, _, path) = Arranged(withRun: true, withDispatch: true);

        var detail = WatchModule.BuildView(source, "/detail" + path, Now()).Body;

        var flight = Field(detail, "flight");
        Assert.True(flight["present"].AsBool());
        Assert.Equal(RequestId, Value(flight)["requests"].AsArray().ToList().Single().AsObject()["id"].AsString());
        var trees = Field(detail, "worktrees");
        Assert.True(trees["present"].AsBool());
        Assert.Contains(Worktree, Value(trees)["trees"].AsArray().ToList().Select(static t => t.AsObject()["path"].AsString()));
        var run = Run(detail);
        Assert.True(run["present"].AsBool());
        Assert.Equal(AnchorId, run["value"].AsObject()["run_id"].AsString());
    }

    [Fact]
    public void NegativeNoDispatchRowAndNoAnchorServeNoRequestAndNoRun()
    {
        var (source, _, path) = Arranged(withRun: false, withDispatch: false);

        var detail = WatchModule.BuildView(source, "/detail" + path, Now()).Body;

        Assert.Empty(Value(Field(detail, "flight"))["requests"].AsArray().ToList());
        Assert.False(Run(detail)["present"].AsBool());
    }
}
