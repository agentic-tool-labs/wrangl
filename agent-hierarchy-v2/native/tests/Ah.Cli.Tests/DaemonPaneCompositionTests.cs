using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Core.Json;
using Ah.Mesh;
using Ah.Platform;
using Ah.TestKit;

namespace Ah.Cli.Tests;

/// <summary>
/// The daemon's view routes as the production composition registers them: a member row carries the launch layer's real placement,
/// and no route, view or action reads a pane.
/// </summary>
public sealed class DaemonPaneCompositionTests : IDisposable
{
    private readonly Sandbox _sandbox = new();

    public void Dispose() => _sandbox.Dispose();

    private sealed class Routes : IRouteRegistry
    {
        internal Dictionary<string, Func<RouteRequest, CancellationToken, Task<RouteResponse>>> Handlers { get; } = [];

        public void Add(string pattern, Func<RouteRequest, CancellationToken, Task<RouteResponse>> handler) => Handlers.Add(pattern, handler);
    }

    private sealed class Rig
    {
        internal required Routes Routes { get; init; }

        internal async Task<JsonObject> Get(string route, string path)
        {
            var request = new RouteRequest("GET", route[4..], "path=" + Uri.EscapeDataString(path), new Dictionary<string, string>(), default, new Dictionary<string, string>());
            var response = await Routes.Handlers[route](request, TestContext.Current.CancellationToken);
            return JsonText.Parse(response.Body.ToArray()).AsObject();
        }

        internal async Task<string> MemberPath(string name)
        {
            var root = await Get("GET /v1/view", "/");
            var orch = ChildPath(root, "orchestrator");
            var team = ChildPath(await Get("GET /v1/view", orch), "team");
            var members = (await Get("GET /v1/view", team))["children"].AsArray().Select(static c => c.AsObject()).ToList();
            return members.Single(m => m["kind"].AsString() == "member" && m["id"].AsString() == name)["path"].AsString();
        }

        internal async Task<string> OrchestratorPath() => ChildPath(await Get("GET /v1/view", "/"), "orchestrator");

        private static string ChildPath(JsonObject view, string kind) =>
            view["children"].AsArray().Select(static c => c.AsObject()).First(c => c["kind"].AsString() == kind)["path"].AsString();
    }

    private Rig Compose(string transport, string transportId)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        var hier = Path.Combine(_sandbox.Home, "proj", ".claude", "ah2");
        Directory.CreateDirectory(Path.Combine(hier, "teams"));
        var teamFile = Path.Combine(hier, "teams", "tm.json");
        var id = transportId.Length == 0 ? string.Empty : $"\"transport_id\":\"{transportId}\",";
        var variables = new Dictionary<string, string?> { ["AH_HOME"] = _sandbox.AhHome, ["HOME"] = _sandbox.Home };
        var env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables);
        var runner = new FakeRunner();
        var graph = ServiceGraph.Build(PlatformModule.CreateIo(null), env, runner, new FakeDaemonClient());
        TestPlant.Team(
            graph.Roster,
            graph.Writer,
            teamFile,
            $"{{\"team_id\":\"t-tm\",\"transport\":\"{transport}\",\"mesh\":{{\"policy\":\"open\"}},"
            + $"\"orchestrator\":{{\"pid\":{Environment.ProcessId},\"session_id\":\"orch-tm\"}},"
            + $"\"members\":[{{\"role\":\"implementor\",\"name\":\"m1\",{id}\"paste\":false}}]}}\n");
        var orchBox = graph.Mailbox.BoxDirFor(teamFile, "orchestrator");
        Assert.True(graph.Mailbox.WriteRegistration(new RegistrationWrite("orch-tm", [new BoxRef("orchestrator@tm", orchBox)], teamFile, "stub", Environment.ProcessId)));
        var routes = new Routes();
        new DaemonServices(new Lazy<ServiceGraph>(graph), env).RegisterRoutes(routes);
        return new Rig { Routes = routes };
    }

    private static async Task<RouteResponse> GetActions(Rig rig, string id)
    {
        var request = new RouteRequest("GET", "/v1/actions/" + id, string.Empty, new Dictionary<string, string>(), default, new Dictionary<string, string> { ["id"] = id });
        return await rig.Routes.Handlers["GET /v1/actions/{*id}"](request, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TheActionsRouteAnswersTheBytesOfTheActionsDocumentTheViewRouteServesForTheSameMember()
    {
        var rig = Compose("tmux", "%1");
        var memberPath = await rig.MemberPath("m1");
        var hierarchyDir = Path.Combine(_sandbox.Home, "proj", ".claude", "ah2");
        var dirId = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(hierarchyDir)))[..8].ToLowerInvariant();

        var viewed = await rig.Get("GET /v1/view", "/actions" + memberPath);
        var served = await GetActions(rig, "member:" + dirId + "/tm/m1");
        var unknown = await GetActions(rig, "member:" + dirId + "/tm/ghost");
        var notAnId = await GetActions(rig, "m1");

        Assert.Equal(200, served.Status);
        Assert.Equal(JsonText.ToUtf8(viewed["body"], 0), served.Body.ToArray());
        Assert.Equal(404, unknown.Status);
        Assert.Equal(400, notAnId.Status);
    }

    [Fact]
    public async Task AMemberRowCarriesTheLaunchLayersPlacementNotTheStub()
    {
        var rig = Compose("tmux", "%1");

        var view = await rig.Get("GET /v1/view", await rig.MemberPath("m1"));
        var placement = view["body"].AsObject()["placement"].AsObject();

        Assert.Equal("tmux", placement["transport"].AsString());
        Assert.Equal("%1", placement["pane_id"].AsString());
        Assert.True(placement["pasteable"].AsBool());
    }

    [Theory]
    [InlineData("sess:0.1")]
    [InlineData("")]
    public async Task AMemberWithNoCanonicalPaneHasNoPaneAndSaysSoWithoutTheStubsReason(string transportId)
    {
        var rig = Compose("tmux", transportId);

        var placement = (await rig.Get("GET /v1/view", await rig.MemberPath("m1")))["body"].AsObject()["placement"].AsObject();

        Assert.Equal(JsonKind.Null, placement["pane_id"].Kind);
        Assert.False(placement["pasteable"].AsBool());
        Assert.Equal("no-pane", placement["reason"].AsString());
    }

    [Fact]
    public async Task TheDaemonServesNoPaneRouteOrPaneViewAndAnOrchestratorViewHasNoPlacement()
    {
        var rig = Compose("tmux", "%1");
        var memberPath = await rig.MemberPath("m1");
        var request = new RouteRequest("GET", "/view", "path=" + Uri.EscapeDataString("/pane" + memberPath), new Dictionary<string, string>(), default, new Dictionary<string, string>());

        var paneView = await rig.Routes.Handlers["GET /v1/view"](request, TestContext.Current.CancellationToken);
        var orchestrator = await rig.Get("GET /v1/view", await rig.OrchestratorPath());
        var memberActions = await rig.Get("GET /v1/view", "/actions" + memberPath);

        Assert.DoesNotContain("GET /v1/pane", rig.Routes.Handlers.Keys);
        Assert.Contains("GET /v1/view", rig.Routes.Handlers.Keys);
        Assert.Equal(404, paneView.Status);
        Assert.False(orchestrator["body"].AsObject().ContainsKey("placement"));
        var ids = memberActions["body"].AsObject()["actions"].AsArray().Select(static a => a.AsObject()["id"].AsString()).ToList();
        Assert.Contains("member.detail", ids);
        Assert.DoesNotContain("member.show_pane", ids);
        Assert.DoesNotContain("member.go_live", ids);
        Assert.DoesNotContain("member.jump", ids);
    }

    [Fact]
    public void TheHerdrEnvironmentDropsTheInheritedPaneVariablesAndPinsTheInstance()
    {
        var inherited = new Dictionary<string, string?>
        {
            ["PATH"] = "/usr/bin",
            ["HERDR_HOME"] = "/h",
            ["HERDR_SOCKET_PATH"] = "/a.sock",
            ["HERDR_PANE_ID"] = "w9:p9",
            ["HERDR_ENV"] = "1",
            ["HERDR_CLIENT_SOCKET_PATH"] = "/c.sock",
        };

        var pinned = DaemonServices.HerdrCallEnvironment(inherited, "/b.sock");
        var none = DaemonServices.HerdrCallEnvironment(inherited, null);

        Assert.Equal(["HERDR_HOME", "HERDR_SOCKET_PATH", "PATH"], pinned.Keys.Order());
        Assert.Equal("/b.sock", pinned["HERDR_SOCKET_PATH"]);
        Assert.Equal("/h", pinned["HERDR_HOME"]);
        Assert.Equal(["HERDR_HOME", "PATH"], none.Keys.Order());
    }
}
