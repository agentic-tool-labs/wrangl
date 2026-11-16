using System.Text;
using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Core.Json;
using Ah.Daemon;
using Ah.Platform;
using Ah.TestKit;

namespace Ah.Cli.Tests;

/// <summary>The daemon's answer to a pending-rows question is the library's answer, and only a served dir is asked about.</summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class PeerPendingRouteTests : IDisposable
{
    private const string Served = "/served/hier";

    private readonly Sandbox _sandbox = new();

    public void Dispose() => _sandbox.Dispose();

    private ServiceGraph Graph()
    {
        var variables = new Dictionary<string, string?> { ["AH_TEST"] = "1", ["AH_HOME"] = _sandbox.AhHome, ["HOME"] = _sandbox.Home };
        var env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables, new PluginInfo(_sandbox.PluginRoot, "wrangl", "1.0.0", "1"));
        return ServiceGraph.Build(PlatformModule.CreateIo(null), env, FakeRunner.KnowingSelf(), new FakeDaemonClient());
    }

    private sealed class Routes : IRouteRegistry
    {
        public Func<RouteRequest, CancellationToken, Task<RouteResponse>>? Handler { get; private set; }

        public string? Pattern { get; private set; }

        public void Add(string pattern, Func<RouteRequest, CancellationToken, Task<RouteResponse>> handler)
        {
            Pattern = pattern;
            Handler = handler;
        }
    }

    private static Func<RouteRequest, CancellationToken, Task<RouteResponse>> Register(Func<string, bool> served, Func<JsonValue, IReadOnlyList<JsonValue>> pendingFor, Func<int?, string, bool>? mayRead = null)
    {
        var routes = new Routes();
        DaemonModule.RegisterPeerPending(routes, served, mayRead ?? ((_, _) => true), pendingFor);
        Assert.Equal("GET /v1/peers/pending", routes.Pattern);
        return routes.Handler!;
    }

    private static Func<RouteRequest, CancellationToken, Task<RouteResponse>> Route(ServiceGraph graph, List<string>? asked = null) =>
        Register(
            dir =>
            {
                asked?.Add(dir);
                return dir == Served;
            },
            session => graph.Hierarchy.Peers.PendingFor(session));

    private static RouteResponse Get(Func<RouteRequest, CancellationToken, Task<RouteResponse>> route, string query) =>
        route(
            new RouteRequest("GET", "/v1/peers/pending", query, new Dictionary<string, string>(), ReadOnlyMemory<byte>.Empty, new Dictionary<string, string>()),
            CancellationToken.None).GetAwaiter().GetResult();

    private static string Row(string session, string task, string status = "pending") =>
        $"{{\"session_id\":\"{session}\",\"task\":\"{task}\",\"from\":\"a\",\"status\":\"{status}\",\"msg\":\"m-{task}\"}}";

    private static void Write(ServiceGraph graph, IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            graph.Hierarchy.StateCalls.AppendPeerRecord(JsonText.Parse(line));
        }
    }

    private static string Body(RouteResponse response) => Encoding.UTF8.GetString(response.Body.Span);

    [Fact]
    public void TheRowsAreTheLibrarysRowsInTheLibrarysOrderAndTheCountIsTheirNumber()
    {
        var graph = Graph();
        var lines = new List<string> { "{\"type\":\"turn\",\"session_id\":\"s0\",\"task\":\"t\",\"from\":\"a\",\"status\":\"pending\"}" };
        for (var s = 0; s < 30; s++)
        {
            lines.Add(Row("s" + s, "t1"));
            lines.Add(Row("s" + s, "t2"));
            lines.Add(Row("s" + s, "t1", "resolved"));
        }

        Write(graph, lines);
        var route = Route(graph);
        for (var s = 0; s < 30; s++)
        {
            var expected = JsonText.Stringify(graph.Hierarchy.Peers.PendingFor(JsonValue.FromString("s" + s)), 0);
            var response = Get(route, $"dir={Uri.EscapeDataString(Served)}&session=s{s}");

            Assert.Equal(200, response.Status);
            Assert.Equal($"{{\"rows\":{expected}}}", Body(response));
            Assert.Equal("{\"count\":1}", Body(Get(route, $"dir={Uri.EscapeDataString(Served)}&session=s{s}&count=1")));
        }

        Assert.Equal("{\"rows\":[]}", Body(Get(route, $"dir={Uri.EscapeDataString(Served)}&session=nobody")));
        Assert.Equal("{\"count\":0}", Body(Get(route, $"dir={Uri.EscapeDataString(Served)}&session=nobody&count=1")));
    }

    [Fact]
    public void ARowAppendedAtOnceIsInTheNextAnswer()
    {
        var graph = Graph();
        Write(graph, [Row("s", "t1")]);
        var route = Route(graph);
        var query = $"dir={Uri.EscapeDataString(Served)}&session=s&count=1";
        Assert.Equal("{\"count\":1}", Body(Get(route, query)));

        Write(graph, [Row("s", "t2")]);

        Assert.Equal("{\"count\":2}", Body(Get(route, query)));
    }

    [Theory]
    [InlineData("/etc")]
    [InlineData("/served/hier/../../etc")]
    [InlineData("/served")]
    [InlineData("/served/hier/")]
    public void ADirThatIsNotServedIsA404AndNoPeerLogIsRead(string dir)
    {
        var graph = Graph();
        Write(graph, [Row("s", "t1")]);
        var read = false;
        var route = Register(d => d == Served, session => { read = true; return graph.Hierarchy.Peers.PendingFor(session); });

        var response = Get(route, $"dir={Uri.EscapeDataString(dir)}&session=s");

        Assert.Equal(404, response.Status);
        Assert.False(read);
        Assert.Contains("not_found", Body(response), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACallerThatIsNeitherTheUserNorTheSessionAskedAboutGetsA403AndNothingIsRead()
    {
        var graph = Graph();
        Write(graph, [Row("s-other", "t1"), Row("s-own", "t1")]);
        var read = new List<string>();
        var route = Register(
            d => d == Served,
            session =>
            {
                read.Add(session.AsString());
                return graph.Hierarchy.Peers.PendingFor(session);
            },
            (pid, session) => pid == 1 && session == "s-own");

        var refused = await route(
            new RouteRequest("GET", "/v1/peers/pending", $"dir={Uri.EscapeDataString(Served)}&session=s-other", new Dictionary<string, string>(), ReadOnlyMemory<byte>.Empty, new Dictionary<string, string>(), 1),
            TestContext.Current.CancellationToken);
        var anonymous = await route(
            new RouteRequest("GET", "/v1/peers/pending", $"dir={Uri.EscapeDataString(Served)}&session=s-own&count=1", new Dictionary<string, string>(), ReadOnlyMemory<byte>.Empty, new Dictionary<string, string>()),
            TestContext.Current.CancellationToken);
        var own = await route(
            new RouteRequest("GET", "/v1/peers/pending", $"dir={Uri.EscapeDataString(Served)}&session=s-own", new Dictionary<string, string>(), ReadOnlyMemory<byte>.Empty, new Dictionary<string, string>(), 1),
            TestContext.Current.CancellationToken);

        Assert.Equal(403, refused.Status);
        Assert.Equal(403, anonymous.Status);
        Assert.Equal(200, own.Status);
        Assert.Equal(["s-own"], read);
    }

    [Theory]
    [InlineData("session=s")]
    [InlineData("dir=relative/hier&session=s")]
    [InlineData("dir=%2Fserved%2Fhier")]
    [InlineData("dir=%2Fserved%2Fhier&session=")]
    public void ARequestMissingItsDirOrSessionIsRefusedBeforeAnythingIsAsked(string query)
    {
        var graph = Graph();
        var asked = new List<string>();

        var response = Get(Route(graph, asked), query);

        Assert.Equal(400, response.Status);
        Assert.Empty(asked);
    }
}
