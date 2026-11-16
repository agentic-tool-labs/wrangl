#pragma warning disable xUnit1051 // the harness owns its own deadlines

using System.Diagnostics;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Platform;

namespace Ah.Daemon.Tests;

/// <summary>
/// <c>ahd ui start</c> as the built binary runs it: its own process launches the detached background holder, and both talk to a real
/// daemon on a real socket. Only the principal is chosen by the test (the test process has no terminal of its own to be the user).
/// </summary>
public sealed class UiServiceEndToEndTests
{
    private static string Ahd => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "Ah.Cli", Path.GetFileName(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory)), "ahd"));

    private static async Task<(int Exit, string Out, string Err, int Pid)> RunAsync(Sandbox box, Action<int>? started, params string[] args)
    {
        var info = new ProcessStartInfo(Ahd) { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = box.Root };
        foreach (var a in args)
        {
            info.ArgumentList.Add(a);
        }

        info.Environment["AH_HOME"] = box.AhHome;
        info.Environment["AH_TEST"] = "1";
        using var process = Process.Start(info)!;
        started?.Invoke(process.Id);
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await process.WaitForExitAsync(timeout.Token);
        return (process.ExitCode, await stdout, await stderr, process.Id);
    }

    private static Task<Harness> StartDaemonAsync(Sandbox box, Func<int, bool> isUser, UiStartSupport? support = null) =>
        Harness.StartAsync(box, o =>
        {
            var table = PlatformModule.CreateProcessTable(PlatformModule.CreateProcessRunner());
            o.Peers = PlatformModule.CreatePeerIdentity();
            o.Principal = pid => pid is { } p && isUser(p) ? PrincipalKind.User : PrincipalKind.Anonymous;
            o.ParentOf = pid => table.AncestorsUntil(pid, static _ => true) is { Count: > 0 } chain ? chain[0] : null;
            if (support is not null)
            {
                o.UiStart = support;
                o.ProcessId = System.Environment.ProcessId;
                o.Routes.Add(new RouteRegistration("GET /v1/view", (_, _) => Task.FromResult(TestRoutes.Json(200, "{}"))));
            }
        });

    private static int HolderPid(Sandbox box)
    {
        var text = File.ReadAllText(Path.Combine(box.AhHome, "run", "ui-service.json"));
        return (int)JsonText.Parse(text).AsObject()["pid"].AsNumber();
    }

    /// <summary>
    /// The reopen's readiness: the holder's lease is up and its live record is written. A reopen that outwaits its own start wait under
    /// load leaves the holder running to report later, so a test waits for this rather than reading the bridge the moment the reopen ends.
    /// </summary>
    private static bool HolderUp(Sandbox box, Harness daemon)
    {
        var path = Path.Combine(box.AhHome, "run", "ui-service.json");
        return daemon.Server.Ui.Active && File.Exists(path) && JsonText.TryParse(File.ReadAllBytes(path), out var v) && v.IsMap
            && v.AsObject().Get("started_at") is not null && v.AsObject().Get("stopped_reason") is null;
    }

    private static bool Gone(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private static void EnsureGone(int pid)
    {
        if (pid > 1 && !Gone(pid))
        {
            using var left = Process.GetProcessById(pid);
            left.Kill();
        }
    }

    [Fact]
    public async Task StartLaunchesTheDetachedHolderWhichGetsItsLeaseFromARealDaemonAndStopEndsIt()
    {
        var box = Sandbox.Create();
        var start = 0;
        await using var daemon = await StartDaemonAsync(box, pid => pid == Volatile.Read(ref start));
        var holder = 0;
        try
        {
            var started = await RunAsync(box, pid => Volatile.Write(ref start, pid), "ui", "start", "--no-open");
            Assert.True(started.Exit == 0, $"start: exit {started.Exit}; out {started.Out}; err {started.Err}; daemon log {daemon.Log}");
            Assert.Contains("UI running · pid ", started.Out, StringComparison.Ordinal);
            Assert.Matches(@"http://127\.0\.0\.1:\d+/ui/\s*$", started.Out);
            holder = HolderPid(box);
            Assert.NotEqual(started.Pid, holder);

            var status = await RunAsync(box, null, "ui", "status");
            Assert.Equal(0, status.Exit);
            Assert.StartsWith($"UI running · pid {holder} · http://127.0.0.1:", status.Out, StringComparison.Ordinal);

            var stopped = await RunAsync(box, null, "ui", "stop");
            Assert.Equal(0, stopped.Exit);
            Assert.Equal($"UI stopped · pid {holder}", stopped.Out.Trim());
            await Until.TrueAsync(() => Gone(holder));
        }
        finally
        {
            EnsureGone(holder);
        }
    }

    private static UiStartSupport RealSupport(Sandbox box, IProcessTable table) => new(
        PlatformModule.CreateReapingDetachedSpawner(),
        Ahd,
        pid => table.StartTime(pid),
        () => new Dictionary<string, string?> { ["AH_HOME"] = box.AhHome, ["AH_TEST"] = "1", ["PATH"] = System.Environment.GetEnvironmentVariable("PATH") },
        "fakeopen");

    [Fact]
    public async Task AKilledHolderTheDaemonSpawnedIsNoZombieAndReadsNotLive()
    {
        var box = Sandbox.Create();
        var table = PlatformModule.CreateProcessTable(PlatformModule.CreateProcessRunner());
        File.WriteAllText(Path.Combine(box.AhHome, "run", "ui-service.json"), "{\"pid\":1,\"pid_start\":\"x\",\"port\":1,\"stopped_reason\":\"daemon_ended\"}\n");
        var holder = 0;
        await using var daemon = await StartDaemonAsync(box, static _ => false, RealSupport(box, table));
        try
        {
            await daemon.Server.UiRestart;
            await Until.TrueAsync(() => HolderUp(box, daemon), because: () => $"no listener: {daemon.Log}");
            holder = HolderPid(box);
            var live = await RunAsync(box, null, "ui", "status");
            Assert.Equal(0, live.Exit);

            using (var process = Process.GetProcessById(holder))
            {
                process.Kill();
            }

            await Until.TrueAsync(() => table.StartTime(holder) is null);

            var status = await RunAsync(box, null, "ui", "status");
            Assert.Equal(3, status.Exit);
            await Until.TrueAsync(() => !daemon.Server.Ui.Active);
        }
        finally
        {
            EnsureGone(holder);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OnlyTheReapingSpawnerWaitsForAChildThatEnds(bool reaping)
    {
        var table = PlatformModule.CreateProcessTable(PlatformModule.CreateProcessRunner());
        var spawner = reaping ? PlatformModule.CreateReapingDetachedSpawner() : PlatformModule.CreateDetachedSpawner();

        var child = spawner.SpawnDetached(new DetachedSpawnRequest(["/usr/bin/true"], "/", new Dictionary<string, string?>(), "/dev/null"));
        await Task.Delay(1000);

        Assert.Equal(reaping, table.StartTime(child) is null);
    }

    /// <summary>The first run of the gate for a holder the daemon itself spawned, across two daemons: the new one is told no caller is the user.</summary>
    [Fact]
    public async Task ANewDaemonBringsBackTheHolderOnTheSamePortAndThePageStillReads()
    {
        var box = Sandbox.Create();
        var table = PlatformModule.CreateProcessTable(PlatformModule.CreateProcessRunner());
        var support = RealSupport(box, table);
        var start = 0;
        var first = 0;
        var second = 0;
        using var http = UiRig.NewClient();
        try
        {
            var a = await StartDaemonAsync(box, pid => pid == Volatile.Read(ref start), support);
            var started = await RunAsync(box, pid => Volatile.Write(ref start, pid), "ui", "start", "--no-open");
            Assert.True(started.Exit == 0, $"start: exit {started.Exit}; out {started.Out}; err {started.Err}; daemon log {a.Log}");
            first = HolderPid(box);
            var port = a.Server.Ui.Port;

            a.Server.RequestShutdown(0);
            await a.Exit;
            await a.DisposeAsync();
            await Until.TrueAsync(() => Gone(first));
            Assert.Equal("daemon_ended", JsonText.Parse(File.ReadAllText(Path.Combine(box.AhHome, "run", "ui-service.json"))).AsObject().Str("stopped_reason"));

            await using var b = await StartDaemonAsync(box, static _ => false, support);
            await b.Server.UiRestart;

            await Until.TrueAsync(() => HolderUp(box, b), because: () =>
                $"no listener: daemon log {b.Log}; holder log {(File.Exists(Path.Combine(box.AhHome, "run", "ui-service.log")) ? File.ReadAllText(Path.Combine(box.AhHome, "run", "ui-service.log")) : "none")}");
            second = HolderPid(box);
            Assert.NotEqual(first, second);
            Assert.Equal(System.Environment.ProcessId, table.AncestorsUntil(second, static _ => true)![0]);
            Assert.Equal(1, b.Server.Ui.Leases);
            Assert.Equal(port, b.Server.Ui.Port);
            using var reply = await http.GetAsync($"http://127.0.0.1:{port}/ui/v1/view?path=/");
            Assert.Equal(System.Net.HttpStatusCode.OK, reply.StatusCode);

            var stopped = await RunAsync(box, null, "ui", "stop");
            Assert.True(stopped.Exit == 0, $"ui stop: exit {stopped.Exit}; err {stopped.Err}");
            Assert.Equal($"UI stopped · pid {second}", stopped.Out.Trim());
            await Until.TrueAsync(() => table.StartTime(second) is null);
        }
        finally
        {
            EnsureGone(first);
            EnsureGone(second);
        }
    }

    [Fact]
    public async Task AStartRunByNoUserLeavesNoHolderAndSaysWhy()
    {
        var box = Sandbox.Create();
        await using var daemon = await StartDaemonAsync(box, static _ => false);
        var holder = 0;
        try
        {
            var started = await RunAsync(box, null, "ui", "start", "--no-open");
            Assert.NotEqual(0, started.Exit);
            Assert.Contains("opened only by the user", started.Err, StringComparison.Ordinal);
            var state = JsonText.Parse(File.ReadAllText(Path.Combine(box.AhHome, "run", "ui-service.json"))).AsObject();
            Assert.Equal("refused", state.Str("stopped_reason"));
            Assert.False(daemon.Server.Ui.Active);
            holder = (int)state["pid"].AsNumber();
        }
        finally
        {
            EnsureGone(holder);
        }
    }
}
