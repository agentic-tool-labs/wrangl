#pragma warning disable xUnit1051 // the harness owns its own deadlines

using System.Net.Sockets;
using System.Text;
using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.Daemon.Tests;

/// <summary>`POST /v1/ui/start`: the page started by the daemon for the user, or for a session that presents the band token; and the service lease of the holder the daemon starts.</summary>
public sealed class UiStartRouteTests
{
    private const string Started = "2026-01-01T00:00:00.000Z";

    private const int HolderPid = 5150;

    private const int FirstDaemonPid = 7_000_000;

    private sealed class FixedPeer(int pid) : IPeerIdentity
    {
        public int? PeerPid(Socket accepted) => pid;
    }

    /// <summary>Stands for the machine: a holder that reports a live lease at once, and an opener that remembers the page it was given.</summary>
    private sealed class FakeSpawner(string runDir) : IDetachedSpawner
    {
        internal List<IReadOnlyList<string>> Spawned { get; } = [];

        public int SpawnDetached(DetachedSpawnRequest request)
        {
            Spawned.Add(request.Argv);
            if (request.Argv.Contains("--service-hold", StringComparer.Ordinal))
            {
                File.WriteAllText(
                    Path.Combine(runDir, "ui-service.json"),
                    JsonText.Stringify(new JsonObject { ["pid"] = HolderPid, ["pid_start"] = Started, ["port"] = 1234, ["started_at"] = Started }, 0) + "\n");
            }

            return HolderPid;
        }
    }

    private sealed class Rig : IAsyncDisposable
    {
        internal required Harness H { get; init; }

        internal required FakeSpawner Spawner { get; init; }

        internal string TokenPath => Path.Combine(H.Box.AhHome, "run", "band.token");

        internal string Token => H.Server.Band.Current;

        internal int HolderSpawns => Spawner.Spawned.Count(a => a.Contains("--service-hold", StringComparer.Ordinal));

        internal string? OpenedUrl => Spawner.Spawned.Where(a => a[0].EndsWith("/fakeopen", StringComparison.Ordinal)).Select(a => a[1]).LastOrDefault();

        internal Task<Reply> StartAsync(string body) => H.PostAsync("/v1/ui/start", body);

        internal Task<Reply> StartWithAsync(string token) => StartAsync($"{{\"token\":\"{token}\"}}");

        public ValueTask DisposeAsync() => H.DisposeAsync();
    }

    /// <summary>A daemon whose callers are all <paramref name="caller"/>. The daemon's own start time reads <see cref="Started"/> the first time and <paramref name="laterStart"/> after.</summary>
    private static async Task<Rig> StartAsync(PrincipalKind caller, bool withOpener = true, bool withSupport = true, string laterStart = Started, Func<int, PrincipalKind>? principal = null, IPeerIdentity? peers = null, Func<int, int?>? parentOf = null)
    {
        var box = Sandbox.Create();
        var runDir = Path.Combine(box.AhHome, "run");
        var bin = Path.Combine(box.Root, "bin");
        Directory.CreateDirectory(bin);
        if (withOpener)
        {
            var opener = Path.Combine(bin, "fakeopen");
            File.WriteAllText(opener, "#!/bin/sh\n");
            File.SetUnixFileMode(opener, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var spawner = new FakeSpawner(runDir);
        var asked = 0;
        var support = new UiStartSupport(
            spawner,
            "/usr/bin/ahd",
            pid => pid == HolderPid ? Started : pid >= FirstDaemonPid ? (asked++ == 0 ? Started : laterStart) : null,
            () => new Dictionary<string, string?> { ["PATH"] = bin },
            "fakeopen");
        var h = await Harness.StartAsync(box, o =>
        {
            o.Principal = principal is null ? _ => caller : pid => principal(pid ?? 0);
            o.Peers = peers;
            o.ParentOf = parentOf is null ? null : pid => parentOf(pid);
            o.UiStart = withSupport ? support : null;
        });
        return new Rig { H = h, Spawner = spawner };
    }

    private static JsonObject Json(Reply reply) => JsonText.Parse(reply.Body).AsObject();

    // ---------------------------------------------------------------- who may start the page

    [Fact]
    public async Task ASessionWithTheCurrentTokenStartsThePageAndTheTokenIsReplacedBeforeTheReply()
    {
        await using var rig = await StartAsync(PrincipalKind.Session);
        var token = rig.Token;

        var reply = await rig.StartWithAsync(token);

        Assert.Equal(200, reply.Status);
        var body = Json(reply);
        Assert.Equal(["opened", "status"], body.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("started", body.Str("status"));
        Assert.True(body["opened"].AsBool());
        var after = await File.ReadAllTextAsync(rig.TokenPath);
        Assert.NotEqual(token, after);
        Assert.Equal(rig.Token, after);
        Assert.Equal(1, rig.HolderSpawns);
    }

    [Fact]
    public async Task TheReplyNeverCarriesTheAddressOrThePortAndThePlainPageGoesToTheOpenerOnly()
    {
        await using var rig = await StartAsync(PrincipalKind.Session);

        var reply = await rig.StartWithAsync(rig.Token);

        var url = rig.OpenedUrl;
        Assert.NotNull(url);
        Assert.Matches(@"^http://127\.0\.0\.1:\d+/ui/$", url);
        var port = url[(url.LastIndexOf(':') + 1)..url.IndexOf("/ui/", StringComparison.Ordinal)];
        Assert.DoesNotContain("127.0.0.1", reply.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("http", reply.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(port, reply.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATokenUsedOnceIsRefusedTheSecondTimeAndNothingIsStartedAgain()
    {
        await using var rig = await StartAsync(PrincipalKind.Session);
        var used = rig.Token;
        Assert.Equal(200, (await rig.StartWithAsync(used)).Status);
        var after = await File.ReadAllTextAsync(rig.TokenPath);
        var spawns = rig.Spawner.Spawned.Count;

        var again = await rig.StartWithAsync(used);

        Assert.Equal(403, again.Status);
        Assert.Contains("\"user_only\"", again.Body, StringComparison.Ordinal);
        Assert.Equal(after, await File.ReadAllTextAsync(rig.TokenPath));
        Assert.Equal(spawns, rig.Spawner.Spawned.Count);
    }

    [Theory]
    [InlineData("{\"token\":\"0000000000000000000000000000000000000000000000000000000000000000\"}")]
    [InlineData("{\"token\":\"\"}")]
    [InlineData("{\"token\":7}")]
    [InlineData("{\"nope\":\"x\"}")]
    [InlineData("{}")]
    [InlineData("")]
    public async Task AWrongOrMissingTokenIsRefusedAndTheTokenAndTheMachineAreLeftAlone(string body)
    {
        await using var rig = await StartAsync(PrincipalKind.Session);
        var before = await File.ReadAllTextAsync(rig.TokenPath);

        var reply = await rig.StartAsync(body);

        Assert.Equal(403, reply.Status);
        Assert.Contains("\"user_only\"", reply.Body, StringComparison.Ordinal);
        Assert.Equal(before, await File.ReadAllTextAsync(rig.TokenPath));
        Assert.Empty(rig.Spawner.Spawned);
    }

    [Fact]
    public async Task AnUnnamedCallerIsRefusedEvenWithTheRightTokenAndTheTokenStays()
    {
        await using var rig = await StartAsync(PrincipalKind.Anonymous);
        var token = rig.Token;

        var reply = await rig.StartWithAsync(token);

        Assert.Equal(403, reply.Status);
        Assert.Equal(token, await File.ReadAllTextAsync(rig.TokenPath));
        Assert.Empty(rig.Spawner.Spawned);
    }

    [Fact]
    public async Task TheUserNeedsNoTokenAndLeavesTheTokenAlone()
    {
        await using var rig = await StartAsync(PrincipalKind.User);
        var token = rig.Token;

        var reply = await rig.StartAsync("{}");

        Assert.Equal(200, reply.Status);
        Assert.Equal("started", Json(reply).Str("status"));
        Assert.Equal(token, await File.ReadAllTextAsync(rig.TokenPath));
    }

    [Fact]
    public async Task ARunningServiceIsReusedAndNoSecondHolderIsStarted()
    {
        await using var rig = await StartAsync(PrincipalKind.Session);
        Assert.Equal("started", Json(await rig.StartWithAsync(rig.Token)).Str("status"));

        var second = await rig.StartWithAsync(rig.Token);

        Assert.Equal(200, second.Status);
        Assert.Equal("reused", Json(second).Str("status"));
        Assert.Equal(1, rig.HolderSpawns);
    }

    [Fact]
    public async Task ABrowserThatCannotBeOpenedIsAnsweredAsNotOpened()
    {
        await using var rig = await StartAsync(PrincipalKind.Session, withOpener: false);

        var reply = await rig.StartWithAsync(rig.Token);

        Assert.Equal(200, reply.Status);
        Assert.False(Json(reply)["opened"].AsBool());
        Assert.Null(rig.OpenedUrl);
    }

    [Fact]
    public async Task ADaemonComposedWithoutTheMeansToStartThePageAnswers503ToTheUser()
    {
        await using var rig = await StartAsync(PrincipalKind.User, withSupport: false);

        var reply = await rig.StartAsync("{}");

        Assert.Equal(503, reply.Status);
        Assert.Contains("\"ui_unavailable\"", reply.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AGetOfTheRouteIsNotAllowed()
    {
        await using var rig = await StartAsync(PrincipalKind.User);

        var reply = await rig.H.GetAsync("/v1/ui/start");

        Assert.Equal(405, reply.Status);
    }

    [Fact]
    public async Task TheLeaseRouteStillRefusesASessionThatPresentsTheTokenThere()
    {
        await using var rig = await StartAsync(PrincipalKind.Session);

        var lease = await rig.H.PostAsync("/v1/ui/lease", $"{{\"token\":\"{rig.Token}\"}}");

        Assert.Equal(403, lease.Status);
        Assert.Equal(rig.Token, await File.ReadAllTextAsync(rig.TokenPath));
    }

    // ---------------------------------------------------------------- the holder the daemon started

    private static async Task<string> LeaseLineAsync(Harness h)
    {
        using var socket = Raw.Connect(h.Box.Sock);
        _ = await socket.SendAsync(Encoding.Latin1.GetBytes("POST /v1/ui/lease?service=1 HTTP/1.1\r\nHost: ahd\r\nContent-Length: 0\r\n\r\n"));
        var buffer = new byte[4096];
        using var cts = new CancellationTokenSource(Until.Deadline);
        var n = await socket.ReceiveAsync(buffer, cts.Token);
        var text = Encoding.Latin1.GetString(buffer, 0, n);
        return text[..text.IndexOf("\r\n", StringComparison.Ordinal)];
    }

    /// <summary>A holder (pid <see cref="HolderPid"/>) whose parent is <paramref name="parentIsDaemon"/> ? the daemon : another process of class <paramref name="otherClass"/>.</summary>
    private static async Task<Rig> StartWithHolderAsync(bool parentIsDaemon, PrincipalKind otherClass, string laterStart)
    {
        const int Other = 4141;
        var daemon = 0;
        var rig = await StartAsync(
            PrincipalKind.Anonymous,
            laterStart: laterStart,
            principal: pid => pid == HolderPid ? PrincipalKind.Session : pid == Other ? otherClass : PrincipalKind.Anonymous,
            peers: new FixedPeer(HolderPid),
            parentOf: pid => pid == HolderPid ? (parentIsDaemon ? Volatile.Read(ref daemon) : Other) : null);
        Volatile.Write(ref daemon, rig.H.Pid);
        return rig;
    }

    [Fact]
    public async Task AHolderTheDaemonItselfStartedGetsTheServiceLease()
    {
        await using var rig = await StartWithHolderAsync(parentIsDaemon: true, PrincipalKind.Anonymous, Started);

        Assert.StartsWith("HTTP/1.1 200", await LeaseLineAsync(rig.H), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHolderWhoseParentHasTheDaemonsPidButAnotherStartTimeIsRefused()
    {
        await using var rig = await StartWithHolderAsync(parentIsDaemon: true, PrincipalKind.Anonymous, "2026-01-01T00:00:01.000Z");

        Assert.StartsWith("HTTP/1.1 403", await LeaseLineAsync(rig.H), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(PrincipalKind.Session)]
    [InlineData(PrincipalKind.Anonymous)]
    public async Task AHolderStartedByASessionOrAnUnknownProcessIsStillRefused(PrincipalKind parent)
    {
        await using var rig = await StartWithHolderAsync(parentIsDaemon: false, parent, Started);

        Assert.StartsWith("HTTP/1.1 403", await LeaseLineAsync(rig.H), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHolderTheDaemonStartedIsRefusedWhenTheDaemonCannotSayWhenItStarted()
    {
        var box = Sandbox.Create();
        var daemon = 0;
        await using var h = await Harness.StartAsync(box, o =>
        {
            o.Principal = pid => pid == HolderPid ? PrincipalKind.Session : PrincipalKind.Anonymous;
            o.Peers = new FixedPeer(HolderPid);
            o.ParentOf = pid => pid == HolderPid ? Volatile.Read(ref daemon) : null;
            o.UiStart = new UiStartSupport(new FakeSpawner(Path.Combine(box.AhHome, "run")), "/usr/bin/ahd", _ => null, () => new Dictionary<string, string?>(), "fakeopen");
        });
        Volatile.Write(ref daemon, h.Pid);

        Assert.StartsWith("HTTP/1.1 403", await LeaseLineAsync(h), StringComparison.Ordinal);
    }
}
