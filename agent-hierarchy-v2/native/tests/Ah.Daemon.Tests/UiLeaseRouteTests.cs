#pragma warning disable xUnit1051 // the harness owns its own deadlines

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.Daemon.Tests;

/// <summary>The lease route on the daemon's socket, and the bridge under a running daemon: its start, its stop and its effect on the daemon's own lifetime.</summary>
public sealed class UiLeaseRouteTests
{
    private const string ViewBody = "{\"rev\":\"r1\",\"kind\":\"root\"}";

    private static RouteRegistration ViewRoute(Func<RouteRequest, CancellationToken, Task<RouteResponse>>? handler = null) =>
        new("GET /v1/view", handler ?? ((_, _) => Task.FromResult(TestRoutes.Json(200, ViewBody))));

    private static Task<Harness> StartAsync(Action<HarnessOptions>? configure = null, Func<RouteRequest, CancellationToken, Task<RouteResponse>>? view = null) =>
        Harness.StartAsync(Sandbox.Create(), o =>
        {
            // the lease is the user's; a test of another caller says so
            o.Principal = _ => PrincipalKind.User;
            o.Routes.Add(ViewRoute(view));
            configure?.Invoke(o);
        });

    /// <summary>A lease held open on the socket: the first line of its reply, parsed, and the socket that keeps it alive.</summary>
    private sealed class HeldLease(Socket socket, int port, string head) : IDisposable
    {
        internal int Port { get; } = port;

        internal string Head { get; } = head;

        /// <summary>Whether the daemon ends the held reply, or the connection, within <paramref name="within"/>.</summary>
        internal bool Closed(TimeSpan within)
        {
            var buffer = new byte[256];
            var seen = new StringBuilder();
            var end = Environment.TickCount64 + (long)within.TotalMilliseconds;
            try
            {
                while (Environment.TickCount64 < end)
                {
                    socket.ReceiveTimeout = Math.Max(1, (int)(end - Environment.TickCount64));
                    var n = socket.Receive(buffer);
                    if (n == 0)
                    {
                        return true;
                    }

                    _ = seen.Append(Encoding.Latin1.GetString(buffer, 0, n));
                    if (seen.ToString().Contains("0\r\n\r\n", StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }
            catch (SocketException)
            {
                return false;
            }

            return false;
        }

        public void Dispose() => socket.Dispose();
    }

    private static async Task<HeldLease> LeaseAsync(Sandbox box)
    {
        var socket = Raw.Connect(box.Sock);
        _ = await socket.SendAsync(Encoding.Latin1.GetBytes("POST /v1/ui/lease HTTP/1.1\r\nHost: ahd\r\nContent-Length: 0\r\n\r\n"));
        var text = new StringBuilder();
        var buffer = new byte[4096];
        using var cts = new CancellationTokenSource(Until.Deadline);
        while (true)
        {
            var n = await socket.ReceiveAsync(buffer, cts.Token);
            Assert.NotEqual(0, n);
            _ = text.Append(Encoding.Latin1.GetString(buffer, 0, n));
            var all = text.ToString();
            var split = all.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (split < 0)
            {
                continue;
            }

            if (!all.StartsWith("HTTP/1.1 200", StringComparison.Ordinal))
            {
                socket.Dispose();
                throw new InvalidOperationException(all);
            }

            var open = all.IndexOf('{', split);
            var close = open < 0 ? -1 : all.IndexOf('}', open);
            if (close > 0)
            {
                var doc = JsonText.Parse(all[open..(close + 1)]).AsObject();
                return new HeldLease(socket, (int)doc["port"].AsNumber(), all[..split]);
            }
        }
    }

    private static string InfoPath(Sandbox box) => Path.Combine(box.AhHome, "run", "ui.json");

    private static Task<HttpResponseMessage> ViewAsync(HttpClient http, int port, string query = "path=/") =>
        http.GetAsync($"http://127.0.0.1:{port}/ui/v1/view?{query}");

    private static string WithoutAt(string body) => System.Text.RegularExpressions.Regex.Replace(body, "\"at\":\"[^\"]*\"", string.Empty);

    [Fact]
    public async Task TheViewOverTcpIsTheViewOverTheSocketOnceTheAtStampIsRemoved()
    {
        var calls = 0;
        await using var daemon = await StartAsync(view: (request, _) => Task.FromResult(TestRoutes.Json(
            200,
            $"{{\"at\":\"t{Interlocked.Increment(ref calls)}\",\"method\":\"{request.Method}\",\"path\":\"{request.Path}\",\"query\":\"{request.Query}\","
            + $"\"parameters\":{request.PathParameters.Count},\"body\":{request.Body.Length}}}")));
        using var lease = await LeaseAsync(daemon.Box);
        using var http = new HttpClient();

        using var tcp = await ViewAsync(http, lease.Port,"path=/&x=1");
        var overTcp = await tcp.Content.ReadAsStringAsync();
        var overSocket = (await daemon.GetAsync("/v1/view?path=/&x=1")).Body;
        using var other = await ViewAsync(http, lease.Port,"path=/&x=2");
        var otherOverTcp = await other.Content.ReadAsStringAsync();

        Assert.NotEqual(overSocket, overTcp);
        Assert.Equal(WithoutAt(overSocket), WithoutAt(overTcp));
        Assert.NotEqual(WithoutAt(overSocket), WithoutAt(otherOverTcp));
    }

    [Fact]
    public async Task AnIdleCheckThatThrowsAnythingIsLoggedByTypeAndDoesNotEscape()
    {
        var lines = new List<string>();

        await DaemonServer.GuardedIdleCheckAsync(() => throw new NotSupportedException("secret detail"), lines.Add);

        Assert.Equal(["bridge idle check failed: NotSupportedException"], lines);
    }

    [Fact]
    public async Task AnIdleCheckThatSucceedsLogsNothing()
    {
        var lines = new List<string>();

        await DaemonServer.GuardedIdleCheckAsync(() => Task.CompletedTask, lines.Add);

        Assert.Empty(lines);
    }

    // ---------------------------------------------------------------- a daemon with no lease

    [Fact]
    public async Task ADaemonThatHasNotBeenAskedBindsNothingAndWritesNoInfoFile()
    {
        await using var daemon = await StartAsync();

        Assert.False(daemon.Server.Ui.Active);
        Assert.False(File.Exists(InfoPath(daemon.Box)));
        Assert.DoesNotContain("bridge", daemon.Log.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStaleInfoFileIsRemovedWhenTheDaemonStarts()
    {
        var box = Sandbox.Create();
        File.WriteAllText(InfoPath(box), "{\"v\":1,\"port\":1,\"lease_pid\":1,\"started\":\"x\"}");

        await using var daemon = await Harness.StartAsync(box);

        Assert.False(File.Exists(InfoPath(box)));
    }

    // ---------------------------------------------------------------- the lease

    [Fact]
    public async Task ALeaseReturnsThePortOnItsFirstLineAndHoldsTheReplyOpen()
    {
        await using var daemon = await StartAsync();

        using var lease = await LeaseAsync(daemon.Box);

        Assert.True(lease.Port > 0);
        Assert.Contains("HTTP/1.1 200", lease.Head, StringComparison.Ordinal);
        Assert.True(daemon.Server.Ui.Active);
        Assert.Equal(lease.Port, daemon.Server.Ui.Port);
        Assert.True(UiRig.Listening(lease.Port));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(InfoPath(daemon.Box)));
        Assert.False(lease.Closed(TimeSpan.FromMilliseconds(300)));
    }

    [Fact]
    public async Task TheLeaseHoldersPidIsInTheInfoFile()
    {
        await using var daemon = await StartAsync(o => o.Peers = new FixedPeer(4711));

        using var lease = await LeaseAsync(daemon.Box);

        var doc = JsonText.Parse(await File.ReadAllTextAsync(InfoPath(daemon.Box))).AsObject();
        Assert.Equal(4711, (int)doc["lease_pid"].AsNumber());
        Assert.Equal(lease.Port, (int)doc["port"].AsNumber());
    }

    private sealed class FixedPeer(int pid) : IPeerIdentity
    {
        public int? PeerPid(Socket accepted) => pid;
    }

    [Fact]
    public async Task ClosingTheLeaseConnectionClosesThePortAndRemovesTheInfoFile()
    {
        await using var daemon = await StartAsync();
        var lease = await LeaseAsync(daemon.Box);
        var port = lease.Port;

        lease.Dispose();
        await Until.TrueAsync(() => !File.Exists(InfoPath(daemon.Box)));

        Assert.True(await UiRig.Refuses(port));
        Assert.False(daemon.Server.Ui.Active);
        Assert.Contains("bridge close (lease dropped)", daemon.Log.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASecondLeaseReusesTheListenerAndTheFirstExitingLeavesItUp()
    {
        await using var daemon = await StartAsync();
        var first = await LeaseAsync(daemon.Box);
        using var second = await LeaseAsync(daemon.Box);

        first.Dispose();
        await Task.Delay(300);

        Assert.Equal(first.Port, second.Port);
        Assert.True(UiRig.Listening(second.Port));
        Assert.True(File.Exists(InfoPath(daemon.Box)));
        Assert.Equal(1, daemon.Server.Ui.Leases);
    }

    [Fact]
    public async Task TheLastOfTwoLeasesExitingClosesTheListener()
    {
        await using var daemon = await StartAsync();
        var first = await LeaseAsync(daemon.Box);
        var second = await LeaseAsync(daemon.Box);
        var port = first.Port;

        first.Dispose();
        second.Dispose();
        await Until.TrueAsync(() => !File.Exists(InfoPath(daemon.Box)));

        Assert.True(await UiRig.Refuses(port));
    }

    [Fact]
    public async Task TheKillSwitchRefusesTheLeaseWith403AndBindsNothing()
    {
        await using var daemon = await StartAsync(o => o.Vars["AH_UI"] = "off");

        var reply = await daemon.PostAsync("/v1/ui/lease", "{}");

        Assert.Equal(403, reply.Status);
        Assert.Equal("ui_disabled", reply.Json["error"].AsObject().Str("code"));
        Assert.Equal("browser UI disabled", reply.Json["error"].AsObject().Str("message"));
        Assert.False(daemon.Server.Ui.Active);
        Assert.False(File.Exists(InfoPath(daemon.Box)));
    }

    [Fact]
    public async Task AnyOtherValueOfTheKillSwitchLeavesTheBridgeOn()
    {
        await using var daemon = await StartAsync(o => o.Vars["AH_UI"] = "on");

        using var lease = await LeaseAsync(daemon.Box);

        Assert.True(lease.Port > 0);
    }

    [Fact]
    public async Task OnlyPostIsAllowedOnTheLeaseRoute()
    {
        await using var daemon = await StartAsync();

        var reply = await daemon.GetAsync("/v1/ui/lease");

        Assert.Equal(405, reply.Status);
        Assert.Equal("POST", reply.Headers["Allow"]);
        Assert.False(daemon.Server.Ui.Active);
    }

    // ---------------------------------------------------------------- the socket and the listener are separate surfaces

    [Theory]
    [InlineData("/ui/v1/view?path=/")]
    [InlineData("/ui/")]
    [InlineData("/ui/v1/view/")]
    public async Task AUiPathOverTheSocketIs404(string path)
    {
        await using var daemon = await StartAsync();

        var reply = await daemon.GetAsync(path);

        Assert.Equal(404, reply.Status);
    }

    [Fact]
    public async Task TheSocketStillServesTheViewRouteTheListenerMapsTo()
    {
        await using var daemon = await StartAsync();
        using var lease = await LeaseAsync(daemon.Box);
        using var http = UiRig.NewClient();

        var socket = await daemon.GetAsync("/v1/view?path=/");
        using var tcp = await ViewAsync(http, lease.Port);

        Assert.Equal(200, socket.Status);
        Assert.Equal(socket.Body, await tcp.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, tcp.StatusCode);
    }

    [Fact]
    public async Task TheViewHandlerOnTheListenerIsTheOneTheSocketsTableHolds()
    {
        var calls = 0;
        await using var daemon = await StartAsync(view: (request, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(TestRoutes.Json(200, $"{{\"query\":\"{request.Query}\"}}"));
        });
        using var lease = await LeaseAsync(daemon.Box);
        using var http = UiRig.NewClient();

        using var tcp = await ViewAsync(http, lease.Port,"path=/graph/o1&wait=0");

        Assert.Equal("{\"query\":\"path=/graph/o1&wait=0\"}", await tcp.Content.ReadAsStringAsync());
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task TheListenerAnswers404ForARouteTheSocketHas()
    {
        await using var daemon = await StartAsync();
        using var lease = await LeaseAsync(daemon.Box);
        using var http = UiRig.NewClient();

        foreach (var path in new[] { "/v1/health", "/ui/v1/health", "/v1/status", "/v1/drain", "/v1/ui/lease" })
        {
            using var reply = await http.GetAsync($"http://127.0.0.1:{lease.Port}{path}");
            Assert.Equal(HttpStatusCode.NotFound, reply.StatusCode);
        }
    }

    // ---------------------------------------------------------------- the daemon's own lifetime

    [Fact]
    public async Task AHeldLongPollDoesNotHoldTheDaemonsShutdownAndGets503()
    {
        await using var daemon = await StartAsync(view: UiRig.HoldUntilCancelled);
        using var lease = await LeaseAsync(daemon.Box);
        using var http = UiRig.NewClient();
        // longer than the daemon's exit guard, so a slow stop still reads as the stop and not as the poll waited out
        const int pollWaitSeconds = 120;
        var poll = ViewAsync(http, lease.Port,$"path=/events&since=1.0&wait={pollWaitSeconds}");
        await Task.Delay(300);
        var watch = Stopwatch.StartNew();

        daemon.Server.RequestShutdown(0);

        Assert.True(await daemon.ExitsWithinAsync(Until.Deadline), "the daemon did not exit");
        using var reply = await poll.WaitAsync(Until.Deadline);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, reply.StatusCode);
        // the shutdown cut the held poll short: it did not wait it out
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(pollWaitSeconds), $"took {watch.Elapsed}");
        Assert.True(await UiRig.Refuses(lease.Port));
        Assert.False(File.Exists(InfoPath(daemon.Box)));
        Assert.Contains("bridge close (daemon stop)", daemon.Log.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheBridgeStopsBeforeTheDaemonDrainsItsOwnRequests()
    {
        await using var daemon = await StartAsync();
        using var lease = await LeaseAsync(daemon.Box);

        daemon.Server.RequestShutdown(0);

        Assert.True(await daemon.ExitsWithinAsync(Until.Deadline));
        var log = daemon.Log.ToString();
        Assert.True(log.IndexOf("bridge close (daemon stop)", StringComparison.Ordinal) > log.IndexOf("draining", StringComparison.Ordinal));
        Assert.True(lease.Closed(Until.Deadline), "the daemon did not end the lease");
    }

    [Fact]
    public async Task ALeaseRequestedWhileTheDaemonDrainsIsRefused()
    {
        await using var daemon = await StartAsync();
        await daemon.Server.Ui.StopAsync("daemon stop", refuseNewLeases: true);

        var reply = await daemon.PostAsync("/v1/ui/lease", "{}");

        Assert.Equal(503, reply.Status);
        Assert.False(daemon.Server.Ui.Active);
    }

    [Fact]
    public async Task AnOpenLeaseKeepsTheDaemonFromIdleExitingAndOnceItIsDroppedTheDaemonExits()
    {
        var monotonic = new ManualMonotonic();
        await using var daemon = await StartAsync(o =>
        {
            o.Monotonic = monotonic;
            o.IdleExitMs = 600;
            o.Timings = DaemonTimings.Default with { FenceInterval = TimeSpan.FromMilliseconds(100) };
        });
        var lease = await LeaseAsync(daemon.Box);

        monotonic.Advance(TimeSpan.FromMinutes(5));
        Assert.False(await daemon.ExitsWithinAsync(TimeSpan.FromMilliseconds(500)), "the daemon idle-exited with a lease open: " + daemon.Log);

        lease.Dispose();

        Assert.True(await daemon.ExitsWithinAsync(Until.Deadline), "the daemon did not idle-exit once the lease was dropped");
    }
}
