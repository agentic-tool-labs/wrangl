#pragma warning disable xUnit1051 // the rig owns its own deadlines

using System.Net;
using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.Daemon.Tests;

/// <summary><c>POST /ui/v1/team-close</c> and <c>POST /v1/team/close</c> on real listeners against a fake core: the status table and who may call.</summary>
public sealed class UiTeamCloseRouteTests
{
    private const string DirId = "abcd1234";
    private const string Dir = "/repo/.claude/ah2";

    private sealed class FakeClose
    {
        public List<JsonObject> Calls { get; } = [];

        public UiTeamCloseSupport Support => new(id => id == DirId ? Dir : null, input =>
        {
            lock (Calls)
            {
                Calls.Add(input);
            }

            return input.Get("team")!.Value.AsString() switch
            {
                "live" => new JsonObject { ["refused"] = new JsonObject { ["reason"] = "live-changed", ["detail"] = "running: a" }, ["preview"] = new JsonObject { ["live"] = new JsonArray() } },
                "boom" => throw new IOException("disk gone"),
                _ => new JsonObject { ["ok"] = true, ["closed"] = true },
            };
        });
    }

    private static string Body(string team = "t1", string more = "") => $$"""{"dir":"{{DirId}}","team":"{{team}}"{{more}}}""";

    private static async Task<(HttpStatusCode Status, JsonValue Body)> PostAsync(UiRig rig, UiGrant grant, string body, Action<HttpRequestMessage>? tweak = null)
    {
        using var reply = await rig.SendAsync(grant.Port, HttpMethod.Post, "/ui/v1/team-close", request =>
        {
            request.Content = UiRig.Json(body);
            tweak?.Invoke(request);
        });
        var text = await reply.Content.ReadAsStringAsync();
        return (reply.StatusCode, JsonText.TryParse(System.Text.Encoding.UTF8.GetBytes(text), out var parsed) ? parsed : JsonValue.Null);
    }

    private static async Task<(UiRig Rig, UiGrant Grant, FakeClose Fake)> OpenAsync(bool wired = true)
    {
        var fake = new FakeClose();
        var rig = UiRig.Create(teamClose: wired ? fake.Support : null);
        return (rig, await rig.LeaseAsync(), fake);
    }

    [Fact]
    public async Task TheBridgeResolvesTheDirRecordsTheUiAndCarriesTheFlags()
    {
        var (rig, grant, fake) = await OpenAsync();
        await using var owner = rig;

        var (status, body) = await PostAsync(rig, grant, Body(more: ""","preview":true,"confirm_live":["a"]"""));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(body.Member("closed")!.Value.AsBool());
        var call = Assert.Single(fake.Calls);
        Assert.Equal((Dir, "t1", "ui"), (call.Get("dir")!.Value.AsString(), call.Get("team")!.Value.AsString(), call.Get("by")!.Value.AsString()));
        Assert.True(call.Get("preview")!.Value.AsBool());
        Assert.Equal("a", Assert.Single(call.Get("confirm_live")!.Value.AsArray()).AsString());
    }

    [Fact]
    public async Task TheBridgeCarriesAHoldToTheCoreAndRefusesOneThatIsNotABoolean()
    {
        var (rig, grant, fake) = await OpenAsync();
        await using var owner = rig;

        var (status, _) = await PostAsync(rig, grant, Body(more: ""","hold":true"""));
        var (bad, _) = await PostAsync(rig, grant, Body(more: ""","hold":"yes" """));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(HttpStatusCode.BadRequest, bad);
        Assert.True(Assert.Single(fake.Calls).Get("hold")!.Value.AsBool());
    }

    [Fact]
    public async Task TheBridgeRefusesAForeignOriginAndReachesNoCore()
    {
        var (rig, grant, fake) = await OpenAsync();
        await using var owner = rig;

        var (status, _) = await PostAsync(rig, grant, Body(), request => request.Headers.TryAddWithoutValidation("Origin", "http://evil.example"));

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task ALiveChangeIsA409CarryingTheFreshTable()
    {
        var (rig, grant, _) = await OpenAsync();
        await using var owner = rig;

        var (status, body) = await PostAsync(rig, grant, Body("live"));

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("live-changed", body.Member("reason")!.Value.AsString());
        Assert.NotNull(body.Member("preview"));
    }

    [Theory]
    [InlineData("""{"dir":"abcd1234"}""", HttpStatusCode.BadRequest)]
    [InlineData("""{"team":"t1"}""", HttpStatusCode.BadRequest)]
    [InlineData("""{"dir":"abcd1234","team":"t1","reopen":"yes"}""", HttpStatusCode.BadRequest)]
    [InlineData("""{"dir":"abcd1234","team":"t1","confirm_live":"a"}""", HttpStatusCode.BadRequest)]
    [InlineData("""{"dir":"nope","team":"t1"}""", HttpStatusCode.Conflict)]
    public async Task ABadBodyOrAnUnknownDirReachesNoCore(string body, HttpStatusCode expected)
    {
        var (rig, grant, fake) = await OpenAsync();
        await using var owner = rig;

        var (status, _) = await PostAsync(rig, grant, body);

        Assert.Equal(expected, status);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task AFailureOfTheCoreIsA500AndUnwiredIs503()
    {
        var (rig, grant, _) = await OpenAsync();
        await using (rig)
        {
            Assert.Equal(HttpStatusCode.InternalServerError, (await PostAsync(rig, grant, Body("boom"))).Status);
        }

        var (bare, bareGrant, _) = await OpenAsync(wired: false);
        await using (bare)
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await PostAsync(bare, bareGrant, Body())).Status);
        }
    }

    private static async Task<(Harness H, FakeClose Fake)> SocketAsync(PrincipalKind caller)
    {
        var fake = new FakeClose();
        var h = await Harness.StartAsync(Sandbox.Create(), o =>
        {
            o.Principal = _ => caller;
            o.UiTeamClose = fake.Support;
        });
        return (h, fake);
    }

    [Theory]
    [InlineData(PrincipalKind.Session)]
    [InlineData(PrincipalKind.Anonymous)]
    public async Task TheSocketRefusesAnyoneButTheUserAndReachesNoCore(PrincipalKind caller)
    {
        var (h, fake) = await SocketAsync(caller);
        await using var owner = h;

        var reply = await h.PostAsync("/v1/team/close", Body());

        Assert.Equal(403, reply.Status);
        Assert.Contains("user_only", reply.Body, StringComparison.Ordinal);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task TheSocketRunsTheCoreForTheUser()
    {
        var (h, fake) = await SocketAsync(PrincipalKind.User);
        await using var owner = h;

        var reply = await h.PostAsync("/v1/team/close", Body());

        Assert.Equal(200, reply.Status);
        Assert.Equal("user", Assert.Single(fake.Calls).Get("by")!.Value.AsString());
    }

    private sealed class FixedPeer(int pid) : IPeerIdentity
    {
        public int? PeerPid(System.Net.Sockets.Socket socket) => pid;
    }

    /// <summary>The peer 300 under 200 under 100 under pid 1: 300 is <c>ah</c>, 200 the herdr app, 100 <paramref name="above"/>.</summary>
    private sealed class HerdrLine(string above) : IProcessTable
    {
        public IReadOnlyList<int>? Ancestors(int pid) => pid == 300 ? [200, 100, 1] : null;

        public bool? HasAncestor(int pid, int candidate) => pid == 300 && candidate is 200 or 100 or 1;

        public string? StartTime(long pid) => null;

        public string? LocalStartTime(long pid) => null;

        public string? Command(int pid) => pid switch { 300 => "ah roster close t1", 200 => "/Applications/Herdr.app/Contents/MacOS/Herdr", 100 => above, _ => null };
    }

    [Theory]
    [InlineData("launchd-child", 200)] // the herdr app exec'd ah directly: accepted, recorded as the bridge's close is
    [InlineData("claude --resume", 403)] // negative: herdr hosts a Claude pane, so a harness is on the line
    public async Task TheSocketTakesAHerdrAppChildWithNoHarnessOnItsLineAsTheUser(string above, int expected)
    {
        var fake = new FakeClose();
        var table = new HerdrLine(above);
        await using var h = await Harness.StartAsync(Sandbox.Create(), o =>
        {
            o.Principal = _ => PrincipalKind.Anonymous;
            o.Peers = new FixedPeer(300);
            o.HerdrAppChild = pid => Ah.Mesh.MeshModule.IsHerdrAppChild(table, pid, p => table.Command(p));
            o.UiTeamClose = fake.Support;
        });

        var reply = await h.PostAsync("/v1/team/close", Body(more: ""","preview":true"""));

        Assert.Equal(expected, reply.Status);
        if (expected == 200)
        {
            Assert.Equal("ui", Assert.Single(fake.Calls).Get("by")!.Value.AsString());
        }
        else
        {
            Assert.Contains("user_only", reply.Body, StringComparison.Ordinal);
            Assert.Empty(fake.Calls);
        }
    }
}
