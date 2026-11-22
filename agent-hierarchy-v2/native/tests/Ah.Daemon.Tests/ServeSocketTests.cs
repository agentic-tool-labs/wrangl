#pragma warning disable xUnit1051 // the deadline here is the test's own

using System.Net.Sockets;
using Ah.Abstractions;
using Ah.Platform;

namespace Ah.Daemon.Tests;

/// <summary><c>serve</c> binds before it collects its jobs and routes, and a client may take the socket path away while it collects.</summary>
public sealed class ServeSocketTests
{
    private static async Task<(int Code, string Log)> ServeAsync(Sandbox box, Action<IJobRegistry>? whileCollecting, Func<int, string?>? startTime = null, ILockProvider? locks = null)
    {
        var o = new HarnessOptions();
        if (startTime is not null)
        {
            o.UiStart = new UiStartSupport(new FakeSpawner(static _ => 0),"/usr/bin/ahd", startTime, () => new Dictionary<string, string?>(), "fakeopen");
        }

        var io = PlatformModule.CreateIo(null);
        var env = new TestEnv(box.AhHome, o.Vars, new PluginInfo(o.PluginRoot, "wrangl", o.Version, "1"));
        var deps = new DaemonDependencies(io.Files, io.Writer, io.Clock, env, null, 424242, o.State, o.Peers, o.Rejections, o.Principal, null, o.ParentOf, o.UiStart) { Locks = locks };
        var settings = new DaemonSettings(DaemonTimings.Default with { FenceInterval = TimeSpan.FromMilliseconds(100) }, HandleSignals: false, IdleExitMs: 300);
        var log = new StringWriter();
        var code = await Task.Run(() => ServeVerb.RunToExit(deps, [], settings, log, whileCollecting)).WaitAsync(TimeSpan.FromSeconds(30));
        return (code, log.ToString());
    }

    [Fact]
    public async Task ADaemonWhoseSocketIsReplacedWhileItCollectsLeavesWithoutListeningOrTakingTheFiles()
    {
        var box = Sandbox.Create();
        using var other = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

        var (code, log) = await ServeAsync(box, _ =>
        {
            File.Delete(box.Sock);
            other.Bind(new UnixDomainSocketEndPoint(box.Sock));
            other.Listen();
        });

        Assert.Equal(0, code);
        Assert.Contains("fenced out (the socket was replaced before this daemon listened)", log, StringComparison.Ordinal);
        Assert.DoesNotContain("listening on", log, StringComparison.Ordinal);
        Assert.False(File.Exists(box.Info));

        // The fenced daemon's socket is unreachable now; a collection finalizes it, as one can at any moment under load.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await client.ConnectAsync(new UnixDomainSocketEndPoint(box.Sock));
    }

    [Fact]
    public async Task ACollectThatThrowsTakesItsOwnSocketFileAway()
    {
        var box = Sandbox.Create();

        await Assert.ThrowsAsync<InvalidOperationException>(() => ServeAsync(box, static _ => throw new InvalidOperationException("collect failed")));

        Assert.False(File.Exists(box.Sock));
    }

    [Fact]
    public async Task ACollectThatThrowsLeavesAReplacedSocketAlone()
    {
        var box = Sandbox.Create();
        using var other = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

        await Assert.ThrowsAsync<InvalidOperationException>(() => ServeAsync(box, _ =>
        {
            File.Delete(box.Sock);
            other.Bind(new UnixDomainSocketEndPoint(box.Sock));
            throw new InvalidOperationException("collect failed");
        }));

        Assert.True(File.Exists(box.Sock));
    }

    [Fact]
    public async Task ADaemonWhoseSocketIsLeftAloneWhileItCollectsListens()
    {
        var box = Sandbox.Create();

        var (code, log) = await ServeAsync(box, static _ => { });

        Assert.Equal(0, code);
        Assert.Contains("listening on", log, StringComparison.Ordinal);
        Assert.DoesNotContain("fenced out", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStaleSocketIsReplacedOnlyWhenNoDaemonIsLive()
    {
        var box = Sandbox.Create();
        Directory.CreateDirectory(Path.GetDirectoryName(box.Sock)!, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        // Bound and never listening refuses every connection, as a killed daemon's leftover file does (disposing would unlink it).
        using var dead = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        dead.Bind(new UnixDomainSocketEndPoint(box.Sock));

        // Refused, but ahd.pid names a live daemon (mid-start, backlog full): the file stays.
        File.WriteAllText(PidFile(box), "{\"pid\":777,\"start\":\"T777\"}\n");
        var (refusedCode, refusedLog) = await ServeAsync(box, null, pid => pid == 777 ? "T777" : "T-self");
        Assert.Equal(ServeVerb.StartFailed, refusedCode);
        Assert.Contains("EADDRINUSE", refusedLog, StringComparison.Ordinal);
        Assert.True(File.Exists(box.Sock));

        // Refused and no daemon on file: replaced.
        File.Delete(PidFile(box));
        var (code, log) = await ServeAsync(box, null);
        Assert.Equal(0, code);
        Assert.Contains("listening on", log, StringComparison.Ordinal);

        // A listening socket is never replaced, even with no daemon on file.
        using var live = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        File.Delete(box.Sock);
        live.Bind(new UnixDomainSocketEndPoint(box.Sock));
        live.Listen();
        using var second = SocketBinder.Bind(box.Sock, out var liveError, static () => true);
        Assert.Null(second);
        Assert.Contains("EADDRINUSE", liveError, StringComparison.Ordinal);
    }

    private static string PidFile(Sandbox box) => Path.Combine(box.AhHome, "run", "ahd.pid");

    [Fact]
    public async Task ASecondServeWhosePidFileNamesALiveDaemonLeavesBeforeCollecting()
    {
        var box = Sandbox.Create();
        File.WriteAllText(PidFile(box), "{\"pid\":777,\"start\":\"T777\"}\n");
        var collected = false;

        var (code, log) = await ServeAsync(box, _ => collected = true, pid => pid == 777 ? "T777" : "T-self");

        Assert.Equal(ServeVerb.StartFailed, code);
        Assert.Contains("another daemon (pid 777) is live; exiting without migrating", log, StringComparison.Ordinal);
        Assert.False(collected);
        Assert.False(File.Exists(box.Sock));
    }

    [Fact]
    public async Task AServeThatFindsTheLifeLockHeldLeavesBeforeBindingOrCollecting()
    {
        var box = Sandbox.Create();
        var locks = PlatformModule.CreateIo(null).Locks;
        Directory.CreateDirectory(Path.Combine(box.AhHome, "run"));
        using var life = locks.TryHold(Path.Combine(box.AhHome, "run", "ahd.life.lock"));
        Assert.NotNull(life);
        var collected = false;

        var (code, log) = await ServeAsync(box, _ => collected = true, locks: locks);

        Assert.Equal(ServeVerb.StartFailed, code);
        Assert.Contains("holds run/ahd.life.lock; exiting without migrating", log, StringComparison.Ordinal);
        Assert.False(collected);
        Assert.False(File.Exists(box.Sock));
        Assert.False(File.Exists(PidFile(box)));
    }

    [Fact]
    public async Task APidFileWhosePidWasReusedDoesNotStopTheServe()
    {
        var box = Sandbox.Create();
        File.WriteAllText(PidFile(box), "{\"pid\":777,\"start\":\"T777\"}\n");

        var (code, log) = await ServeAsync(box, static _ => { }, pid => pid == 777 ? "T-reused" : "T-self");

        Assert.Equal(0, code);
        Assert.Contains("listening on", log, StringComparison.Ordinal);
        // Cleanup removes ahd.pid only while it names this daemon, so its absence shows the serve rewrote it and took it away at exit.
        Assert.False(File.Exists(PidFile(box)));
    }
}
