using System.Text;
using Ah.Abstractions;
using Ah.Platform;
using Ah.State;
using Ah.TestKit;

namespace Ah.Daemon.Tests;

/// <summary>What the daemon records of the requests it refuses: the request's line and the reason, never its body.</summary>
public sealed class RejectionLogTests
{
    private sealed class Domain(string name) : IStateDomain
    {
        public string Name { get; } = name;

        public IReadOnlyList<string> StoreNames => [];

        public IReadOnlyList<StateOp> Ops { get; } =
        [
            new("put", OpKind.Write, OpStyle.Overwrite, false, null, true, (call, ctx) =>
            {
                ctx!.Write(call.Context.HierarchyDirectory + "/put.json", call.Context.HierarchyDirectory + "/put.tmp", "{}"u8);
                return new OpResult(true, "{\"written\":true}"u8.ToArray());
            }),
        ];

        public string StateDirectory(RequestContext context) => context.HierarchyDirectory + "/s";

        public void AfterWrite(OpCall operation, OpResult result)
        {
        }
    }

    private sealed class ThrowingLog : IRejectionLog
    {
        public int Calls { get; private set; }

        public void Record(RejectionRecord record)
        {
            Calls++;
            throw new IOException("disk full");
        }
    }

    private static string LogPath(Sandbox box) => Path.Combine(box.AhHome, "logs", "rejected.jsonl");

    private static IStateService Service(Sandbox box, out string hierarchy)
    {
        var io = PlatformModule.CreateIo(null);
        var env = new FakeEnvironment(new Dictionary<string, string> { ["AH_HOME"] = box.AhHome, ["HOME"] = box.Root });
        hierarchy = box.Root + "/proj/.claude/ah2";
        Directory.CreateDirectory(hierarchy);
        Directory.CreateDirectory(box.Root + "/proj/.git");
        var domains = new[] { new Domain("store") };
        var engine = StateModule.CreateEngine(io.Files, io.Writer, io.Appender, io.Locks, io.Clock, domains, 4242);
        return StateModule.CreateService(
            domains, engine, io.Files, PlatformModule.CurrentUid(), Ah.Config.ConfigModule.CreateHierarchyDirPolicy(io.Files, env, null, () => PlatformModule.CurrentUid()), null);
    }

    private static Task<Harness> Start(Sandbox box, IStateService? state, IRejectionLog? log = null) =>
        Harness.StartAsync(box, o =>
        {
            o.State = state;
            o.Peers = PlatformModule.CreatePeerIdentity();
            o.Rejections = log ?? PlatformModule.CreateRejectionLog(PlatformModule.CreateIo(null).Clock, LogPath(box));
        });

    private static string Body(string args, string cwd, string hierarchy) =>
        "{\"args\":" + args + ",\"context\":{\"cwd\":\"" + cwd + "\",\"hierarchy_dir\":\"" + hierarchy + "\"},\"op_id\":\"op-1\"}";

    private static string[] Rows(Sandbox box) => File.Exists(LogPath(box)) ? File.ReadAllLines(LogPath(box)) : [];

    [Fact]
    public async Task ARejectedStateRequestIsRecordedWithItsReason()
    {
        var box = Sandbox.Create();
        var service = Service(box, out var hierarchy);
        await using var h = await Start(box, service);

        var reply = await h.PostAsync("/v1/state/store/put", Body("{}", hierarchy, box.Root + "/missing/.claude/ah2"));

        Assert.Equal(400, reply.Status);
        var row = Ah.Core.Json.JsonText.Parse(Assert.Single(Rows(box)));
        Assert.Equal(400, row.Member("status")!.Value.AsNumber());
        Assert.Equal("bad_request", row.Member("code")!.Value.AsString());
        Assert.Contains("does not exist", row.Member("message")!.Value.AsString(), StringComparison.Ordinal);
        Assert.Equal("/v1/state/store/put", row.Member("path")!.Value.AsString());
        Assert.Equal("POST", row.Member("method")!.Value.AsString());
        Assert.Equal(Environment.ProcessId, (int)row.Member("peer_pid")!.Value.AsNumber());
    }

    private static Task<Harness> StartWithDueRoute(Sandbox box) =>
        Harness.StartAsync(box, o =>
        {
            o.Rejections = PlatformModule.CreateRejectionLog(PlatformModule.CreateIo(null).Clock, LogPath(box));
            o.Routes.Add(new RouteRegistration("GET /v1/due", (_, _) =>
                Task.FromResult(TestRoutes.Json(400, "{\"error\":{\"code\":\"bad_request\",\"message\":\"session must be a non-empty string\"}}"))));
        });

    [Fact]
    public async Task TheDueRouteProbeAnsweredWith400IsNotRecorded()
    {
        var box = Sandbox.Create();
        await using var h = await StartWithDueRoute(box);

        var reply = await h.GetAsync(Ah.Mesh.MeshModule.DueProbePath);

        Assert.Equal(400, reply.Status);
        Assert.Empty(Rows(box));
    }

    [Fact]
    public async Task ADueRequestThatIsNotTheProbeAnsweredWith400IsRecorded()
    {
        var box = Sandbox.Create();
        await using var h = await StartWithDueRoute(box);

        var reply = await h.GetAsync("/v1/due?wait=1&in=%7B%22session%22%3A%22%22%7D");

        Assert.Equal(400, reply.Status);
        var row = Ah.Core.Json.JsonText.Parse(Assert.Single(Rows(box)));
        Assert.Equal("bad_request", row.Member("code")!.Value.AsString());
        Assert.Equal("/v1/due", row.Member("path")!.Value.AsString());
    }

    [Fact]
    public async Task TheProbeLineAnsweredWithAnythingButTheProbeStatusIsStillRecorded()
    {
        var box = Sandbox.Create();
        await using var h = await Harness.StartAsync(box, o =>
        {
            o.Rejections = PlatformModule.CreateRejectionLog(PlatformModule.CreateIo(null).Clock, LogPath(box));
            o.Routes.Add(new RouteRegistration("GET /v1/due", (_, _) => Task.FromResult(TestRoutes.Json(500, "{\"error\":{\"code\":\"internal\",\"message\":\"boom\"}}"))));
        });

        var reply = await h.GetAsync(Ah.Mesh.MeshModule.DueProbePath);

        Assert.Equal(500, reply.Status);
        Assert.Single(Rows(box));
    }

    [Fact]
    public async Task AnUnknownRouteIsRecorded()
    {
        var box = Sandbox.Create();
        await using var h = await Start(box, null);

        var reply = await h.GetAsync("/v1/nothing-here");

        Assert.Equal(404, reply.Status);
        var row = Ah.Core.Json.JsonText.Parse(Assert.Single(Rows(box)));
        Assert.Equal("not_found", row.Member("code")!.Value.AsString());
        Assert.Equal("/v1/nothing-here", row.Member("path")!.Value.AsString());
    }

    [Fact]
    public async Task HealthAndSuccessAreNotRecorded()
    {
        var box = Sandbox.Create();
        var service = Service(box, out var hierarchy);
        await using var h = await Start(box, service);

        var health = await h.GetAsync("/v1/health");
        var ok = await h.PostAsync("/v1/state/store/put", Body("{}", hierarchy, hierarchy));

        Assert.Equal(200, health.Status);
        Assert.Equal(200, ok.Status);
        Assert.Empty(Rows(box));
    }

    [Fact]
    public async Task AFailingRejectionLogDoesNotChangeTheReply()
    {
        var box = Sandbox.Create();
        var failing = new ThrowingLog();
        await using var h = await Start(box, null, failing);

        var reply = await h.GetAsync("/v1/nothing-here");

        Assert.Equal(404, reply.Status);
        Assert.Contains("not_found", reply.Body, StringComparison.Ordinal);
        Assert.Equal(1, failing.Calls);
    }

    [Fact]
    public async Task TheRequestBodyIsNeverRecorded()
    {
        var box = Sandbox.Create();
        var service = Service(box, out var hierarchy);
        await using var h = await Start(box, service);

        _ = await h.PostAsync(
            "/v1/state/store/put", Body("{\"secret\":\"MARKER-9f3c\"}", hierarchy, box.Root + "/missing/.claude/ah2"));

        var text = File.ReadAllText(LogPath(box), Encoding.UTF8);
        Assert.NotEmpty(text);
        Assert.DoesNotContain("MARKER-9f3c", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnsupportedMethodIsRecordedWithItsMethod()
    {
        var box = Sandbox.Create();
        var service = Service(box, out var hierarchy);
        await using var h = await Start(box, service);

        var reply = await h.SendAsync("PUT", "/v1/state/store/put", Body("{}", hierarchy, hierarchy));

        Assert.Equal(405, reply.Status);
        var row = Ah.Core.Json.JsonText.Parse(Assert.Single(Rows(box)));
        Assert.Equal("PUT", row.Member("method")!.Value.AsString());
        Assert.Equal(405, row.Member("status")!.Value.AsNumber());
    }
}
