#pragma warning disable xUnit1051 // the rig owns its own deadlines

using System.Diagnostics;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.DaemonClient;
using Ah.Platform;

namespace Ah.Daemon.Tests;

internal sealed class FakeSpawner(Func<DetachedSpawnRequest, int> start) : IDetachedSpawner
{
    internal List<DetachedSpawnRequest> Requests { get; } = [];

    public int SpawnDetached(DetachedSpawnRequest request)
    {
        Requests.Add(request);
        return start(request);
    }
}

internal sealed class FakeInspector(Func<int, bool> alive) : IProcessInspector
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, int> _polls = new();

    /// <summary>How many times the client has asked whether <paramref name="pid"/> is alive.</summary>
    internal int Polls(int pid) => _polls.GetValueOrDefault(pid);

    public string? CommandLine(int pid) => alive(pid) ? "fake" : null;

    public bool PidOwned(int pid) => alive(pid);

    public bool PidAlive(int pid)
    {
        _ = _polls.AddOrUpdate(pid, 1, static (_, n) => n + 1);
        return alive(pid);
    }
}

internal sealed class FakeRunner : IProcessRunner
{
    internal List<IReadOnlyList<string>> Calls { get; } = [];

    public ProcessResult Run(ProcessRequest request)
    {
        Calls.Add(request.Argv);
        return new ProcessResult(1, null, false, false, ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty);
    }
}

/// <summary>A daemon client over a sandbox, whose spawner starts in-process daemons.</summary>
internal sealed class ClientRig
{
    private readonly Dictionary<int, Harness> _started = [];

    internal ClientRig(
        Dictionary<string, string?>? vars = null,
        string? ahHome = null,
        Action<HarnessOptions>? daemon = null,
        Func<string, CancellationToken, Task<JsonObject?>>? inspectFiles = null)
    {
        Box = Sandbox.Create();
        Home = ahHome ?? Box.AhHome;
        Vars = new Dictionary<string, string?>
        {
            ["PATH"] = "/usr/bin:/bin",
            ["HOME"] = Path.Combine(Box.Root, "home"),
            ["AH_HOME"] = Home,
            ["AH_TEST"] = "1",
            ["AHD_IDLE_EXIT_MS"] = "60000",
            ["SECRET_TOKEN"] = "must-not-leak",
        };
        if (vars is not null)
        {
            foreach (var (k, v) in vars)
            {
                Vars[k] = v;
            }
        }

        var io = PlatformModule.CreateIo(null);
        Spawner = new FakeSpawner(request =>
        {
            var h = Harness.Launch(Box, o =>
            {
                o.Version = Vars.TryGetValue("DAEMON_VERSION", out var v) && v is not null ? v : "9.9.9";
                foreach (var (k, val) in Vars.Where(kv => kv.Key != "AHD_VERSION_OVERRIDE" && (kv.Key.StartsWith("AHD_", StringComparison.Ordinal) || kv.Key == "AH_TEST")))
                {
                    o.Vars[k] = val;
                }

                daemon?.Invoke(o);
            });
            _started[h.Pid] = h;
            return h.Pid;
        });
        Runner = new FakeRunner();
        Inspector = new FakeInspector(pid => _started.TryGetValue(pid, out var h) && !h.Exited);
        var env = new TestEnv(Home, Vars, new PluginInfo("/private/tmp/ahprobe.plugin", "wrangl", Vars.TryGetValue("AHD_VERSION_OVERRIDE", out var ov) && ov is not null ? "1.0.0" : "9.9.9", "1"));
        Client = DaemonClientModule.Compose(new DaemonClientDependencies(
            io.Files,
            io.Writer,
            io.Appender,
            io.Locks,
            new UnixTransport(),
            Spawner,
            Runner,
            Inspector,
            io.Clock,
            env,
            Vars,
            "/private/tmp/ahprobe.bin/ahd"),
            inspectFiles);
    }

    internal Sandbox Box { get; }

    internal FakeInspector Inspector { get; }

    internal string Home { get; }

    internal Dictionary<string, string?> Vars { get; }

    internal FakeSpawner Spawner { get; }

    internal FakeRunner Runner { get; }

    internal IDaemonClient Client { get; }

    internal IEnumerable<Harness> Daemons => _started.Values;

    internal string MarkerPath => Path.Combine(Home, "run", "ahd.spawn-failed");

    internal static async Task<(int Code, string Out, string Err)> RunAsync(string verb, params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var code = await DaemonClientModule.Lifecycle!.RunAsync(verb, args, o, e);
        return (code, o.ToString(), e.ToString());
    }

    internal static async Task<(int Code, string Out, string Err)> RunMsgAliasAsync(string verb)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var code = await DaemonClientModule.Lifecycle!.RunMsgAliasAsync(verb, o, e);
        return (code, o.ToString(), e.ToString());
    }

    internal async Task StopAllAsync()
    {
        foreach (var h in _started.Values)
        {
            await h.DisposeAsync();
        }
    }
}

/// <summary>The client and verb tests share the process-wide composition, so they run one after another.</summary>
[Collection("daemon-client")]
public sealed class ClientTests
{
    private static JsonObject Parse(string text) => JsonText.Parse(text.TrimEnd('\n')).AsObject();

    [Fact]
    public async Task StartSpawnsTheNativeDaemonWithNodesEnvironmentKeysAndReportsIt()
    {
        var rig = new ClientRig();
        try
        {
            var r = await ClientRig.RunAsync("start");

            Assert.Equal(0, r.Code);
            var body = Parse(r.Out);
            Assert.Equal(["ok", "source", "status", "started", "pid", "version"], body.Keys);
            Assert.Equal("started", body["status"].AsString());
            Assert.True(body["started"].AsBool());
            Assert.Equal("9.9.9", body["version"].AsString());
            var spawn = Assert.Single(rig.Spawner.Requests);
            Assert.Equal(["/private/tmp/ahprobe.bin/ahd", "serve", "--ah-home", rig.Home], spawn.Argv);
            Assert.Equal(rig.Home, spawn.WorkingDirectory);
            Assert.Equal(Path.Combine(rig.Home, "logs", "ahd.log"), spawn.LogPath);
            Assert.Equal(
                ["AHD_IDLE_EXIT_MS", "AH_HOME", "AH_PLUGIN_ROOT", "AH_TEST", "HOME", "PATH"],
                spawn.Environment.Keys.Order(StringComparer.Ordinal));
            Assert.DoesNotContain("SECRET_TOKEN", spawn.Environment.Keys);
            Assert.Equal(rig.Home, spawn.Environment["AH_HOME"]);
        }
        finally
        {
            await rig.StopAllAsync();
        }
    }

    [Fact]
    public async Task StartWithTheIdleOptionSpawnsServeWithItBeforeTheStateHome()
    {
        var rig = new ClientRig();
        try
        {
            var r = await ClientRig.RunAsync("start", "--idle-exit-ms", "4000");

            Assert.Equal(0, r.Code);
            var spawn = Assert.Single(rig.Spawner.Requests);
            Assert.Equal(["/private/tmp/ahprobe.bin/ahd", "serve", "--idle-exit-ms", "4000", "--ah-home", rig.Home], spawn.Argv);
        }
        finally
        {
            await rig.StopAllAsync();
        }
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("abc")]
    [InlineData("1000000000")]
    public async Task StartRefusesAnIdleOptionThatIsNotAPositiveWholeNumber(string value)
    {
        var rig = new ClientRig();
        try
        {
            var r = await ClientRig.RunAsync("start", "--idle-exit-ms", value);

            Assert.Equal(2, r.Code);
            Assert.Empty(rig.Spawner.Requests);
        }
        finally
        {
            await rig.StopAllAsync();
        }
    }

    [Fact]
    public async Task OnlyStartTakesTheIdleOption()
    {
        var rig = new ClientRig();
        try
        {
            var r = await ClientRig.RunAsync("stop", "--idle-exit-ms", "4000");

            Assert.Equal(2, r.Code);
            Assert.Contains("stop does not take --idle-exit-ms", r.Out, StringComparison.Ordinal);
        }
        finally
        {
            await rig.StopAllAsync();
        }
    }

    [Fact]
    public async Task OutsideTestModeNoTestKnobAndNoArbitraryAhdVariableReachesTheSpawnedDaemon()
    {
        var rig = new ClientRig(new Dictionary<string, string?>
        {
            ["AH_TEST"] = null,
            ["AHD_JOIN_MS"] = "1",
            ["AHD_VERSION_OVERRIDE"] = "0.0.1",
            ["AHD_WATCH_POLL_MS"] = "500",
            ["AHD_X"] = "1",
        });
        try
        {
            var r = await ClientRig.RunAsync("start");

            Assert.Equal(0, r.Code);
            var spawn = Assert.Single(rig.Spawner.Requests);
            Assert.Equal(["AH_HOME", "AH_PLUGIN_ROOT", "HOME", "PATH"], spawn.Environment.Keys.Order(StringComparer.Ordinal));
            Assert.Equal(Path.GetDirectoryName(rig.Home), spawn.Environment["HOME"]);
        }
        finally
        {
            await rig.StopAllAsync();
        }
    }

    [Fact]
    public async Task OutsideTestModeTheUiKillSwitchReachesTheSpawnedDaemonAndNoVariableNamedLikeItDoes()
    {
        var rig = new ClientRig(new Dictionary<string, string?> { ["AH_TEST"] = null, ["AH_UI"] = "off", ["AH_UI_X"] = "off" });
        try
        {
            var r = await ClientRig.RunAsync("start");

            Assert.Equal(0, r.Code);
            var spawn = Assert.Single(rig.Spawner.Requests);
            Assert.Equal("off", spawn.Environment["AH_UI"]);
            Assert.DoesNotContain("AH_UI_X", spawn.Environment.Keys);
        }
        finally
        {
            await rig.StopAllAsync();
        }
    }

    [Fact]
    public async Task AVersionOverrideOutsideTestModeIsIgnoredSoTheRunningDaemonIsNotReplaced()
    {
        var rig = new ClientRig(new Dictionary<string, string?>
        {
            ["AH_TEST"] = null,
            ["DAEMON_VERSION"] = "1.0.0",
            ["AHD_VERSION_OVERRIDE"] = "5.5.5",
        });
        try
        {
            _ = await ClientRig.RunAsync("start");
            var again = await ClientRig.RunAsync("start");

            Assert.Equal("running", Parse(again.Out)["status"].AsString());
            Assert.Single(rig.Spawner.Requests);
        }
        finally
        {
            await rig.StopAllAsync();
        }
    }

    [Fact]
    public async Task InTestModeOnlyTheNamedKnobsReachTheSpawnedDaemon()
    {
        var rig = new ClientRig(new Dictionary<string, string?> { ["AHD_JOIN_MS"] = "7", ["AHD_WATCH_POLL_MS"] = "500", ["AHD_X"] = "1" });
        try
        {
            var r = await ClientRig.RunAsync("start");

            Assert.Equal(0, r.Code);
            var spawn = Assert.Single(rig.Spawner.Requests);
            Assert.Equal("7", spawn.Environment["AHD_JOIN_MS"]);
            Assert.Equal("500", spawn.Environment["AHD_WATCH_POLL_MS"]);
            Assert.Equal(Path.GetDirectoryName(rig.Home), spawn.Environment["HOME"]);
            Assert.Equal("1", spawn.Environment["AH_TEST"]);
            Assert.DoesNotContain("AHD_X", spawn.Environment.Keys);
            Assert.DoesNotContain("SECRET_TOKEN", spawn.Environment.Keys);
        }
        finally
        {
            await rig.StopAllAsync();
        }
    }

    [Fact]
    public async Task StartOnARunningDaemonReportsRunningAndSpawnsNothing()
    {
        var rig = new ClientRig();
        try
        {
            _ = await ClientRig.RunAsync("start");
            var again = await ClientRig.RunAsync("start");

            Assert.Equal(0, again.Code);
            Assert.Equal("running", Parse(again.Out)["status"].AsString());
            Assert.False(Parse(again.Out)["started"].AsBool());
            Assert.Single(rig.Spawner.Requests);
        }
        finally
        {
            await rig.StopAllAsync();
        }
    }

    [Fact]
    public async Task HealthIsExit3WhenDownAndTheDaemonsBodyWhenUp()
    {
        var rig = new ClientRig();
        try
        {
            var down = await ClientRig.RunAsync("health");
            Assert.Equal(3, down.Code);
            Assert.Equal(
                "{\"ok\":false,\"source\":\"files\",\"status\":\"down\",\"error\":{\"code\":\"daemon_down\",\"message\":\"the daemon is not running\"}}\n",
                down.Out);

            _ = await ClientRig.RunAsync("start");
            var up = await ClientRig.RunAsync("health");

            Assert.Equal(0, up.Code);
            var body = Parse(up.Out);
            Assert.Equal("up", body["status"].AsString());
            Assert.Equal("native", body["impl"].AsString());
            Assert.Equal(["ok", "source", "status", "pid", "version"], body.Keys.Take(5));
        }
        finally
        {
            await rig.StopAllAsync();
        }
    }

    [Fact]
    public async Task StatusExits0WithUpFalseWhenDownAndPlainSaysDown()
    {
        var rig = new ClientRig();
        try
        {
            var json = await ClientRig.RunAsync("status");
            var plain = await ClientRig.RunAsync("status", "--plain");

            Assert.Equal(0, json.Code);
            Assert.Equal(
                "{\"ok\":true,\"source\":\"files\",\"up\":false,\"registrations\":[],\"overdue\":0,\"timers\":[],\"fallbacks\":{\"count\":0,\"last\":[]}}\n",
                json.Out);
            Assert.Equal(0, plain.Code);
            Assert.Equal(
                "daemon down, showing files\ndown\nregistrations: none\noverdue: 0\nfallbacks to the files: 0\n",
                plain.Out);
        }
        finally
        {
            await rig.StopAllAsync();
        }
    }

    [Fact]
    public async Task StatusWithTheDaemonDownReadsRegistrationsBoxesAndTimersFromTheFilesSeam()
    {
        var asked = new List<string>();
        var rig = new ClientRig(inspectFiles: (path, _) =>
        {
            asked.Add(path);
            return Task.FromResult<JsonObject?>(path switch
            {
                "/v1/inspect/registrations" => Parse("{\"registrations\":[{\"session_id\":\"s1\",\"address\":\"a@t\",\"harness\":\"h\",\"adapter\":\"fresh\",\"live\":true,\"pid\":9},{\"session_id\":\"s2\",\"address\":\"b@t\",\"live\":false}]}"),
                "/v1/inspect/boxes" => Parse("{\"boxes\":[{\"address\":\"a@t\",\"overdue\":2},{\"address\":\"b@t\",\"overdue\":3}]}"),
                "/v1/inspect/timers" => Parse("{\"timers\":[{\"session\":\"s1\",\"owned\":true,\"due\":[1,2]}]}"),
                _ => null,
            });
        });
        try
        {
            var r = await ClientRig.RunAsync("status");

            Assert.Equal(0, r.Code);
            var body = Parse(r.Out);
            Assert.Equal("files", body["source"].AsString());
            Assert.False(body["up"].AsBool());
            var live = Assert.Single(body["registrations"].AsArray()).AsObject();
            Assert.Equal("s1", live["session_id"].AsString());
            Assert.False(live.ContainsKey("pid"));
            Assert.Equal(5, body["overdue"].AsNumber());
            var timer = Assert.Single(body["timers"].AsArray()).AsObject();
            Assert.Equal(2, timer["due"].AsNumber());
            Assert.Equal(["/v1/inspect/registrations", "/v1/inspect/boxes", "/v1/inspect/timers"], asked);
        }
        finally
        {
            await rig.StopAllAsync();
        }
    }

    [Fact]
    public async Task StatusWithTheDaemonDownAndNoFilesSeamReportsNothingRegistered()
    {
        var rig = new ClientRig();
        try
        {
            var r = await ClientRig.RunAsync("status");

            var body = Parse(r.Out);
            Assert.Empty(body["registrations"].AsArray());
            Assert.Equal(0, body["overdue"].AsNumber());
            Assert.Empty(body["timers"].AsArray());
        }
        finally
        {
            await rig.StopAllAsync();
        }
    }

    [Fact]
    public async Task DrainRequireIsExit3WhenDownAndDrainAsksARunningDaemonToExit()
    {
        var rig = new ClientRig();
        try
        {
            var none = await ClientRig.RunAsync("drain", "--require");
            Assert.Equal(3, none.Code);
            Assert.Contains("\"daemon_down\"", none.Out, StringComparison.Ordinal);
            var soft = await ClientRig.RunAsync("drain");
            Assert.Equal(0, soft.Code);
            Assert.Equal("{\"ok\":true,\"source\":\"files\",\"status\":\"not-running\"}\n", soft.Out);

            _ = await ClientRig.RunAsync("start");
            var d = rig.Daemons.Single();
            var drained = await ClientRig.RunAsync("drain");

            Assert.Equal(0, drained.Code);
            Assert.Equal("draining", Parse(drained.Out)["status"].AsString());
            Assert.True(await d.ExitsWithinAsync(TimeSpan.FromSeconds(6)));
        }
        finally
        {
            await rig.StopAllAsync();
        }
    }

    [Fact]
    public async Task MsgDaemonStartPrintsTheVersionAndPidInTheMsgCliText()
    {
        var rig = new ClientRig();
        try
        {
            var r = await ClientRig.RunMsgAliasAsync("start");

            Assert.Equal(0, r.Code);
            Assert.Equal($"ahd 9.9.9 pid {rig.Daemons.Single().Pid}\n", r.Out);
            Assert.Equal(string.Empty, r.Err);
            Assert.Single(rig.Spawner.Requests);
        }
        finally
        {
            await rig.StopAllAsync();
        }
    }

    [Fact]
    public async Task MsgDaemonStartOnARunningDaemonOfTheSameVersionNamesItAndSpawnsNothing()
    {
        var rig = new ClientRig();
        try
        {
            _ = await ClientRig.RunMsgAliasAsync("start");
            var again = await ClientRig.RunMsgAliasAsync("start");

            Assert.Equal(0, again.Code);
            Assert.Equal($"ahd 9.9.9 pid {rig.Daemons.Single().Pid}\n", again.Out);
            Assert.Single(rig.Spawner.Requests);
        }
        finally
        {
            await rig.StopAllAsync();
        }
    }

    [Fact]
    public async Task MsgDaemonStartReplacesARunningDaemonThatIsOlderThanTheClientWhereAhdStartLeavesIt()
    {
        var rig = new ClientRig(new Dictionary<string, string?> { ["DAEMON_VERSION"] = "1.0.0", ["AHD_VERSION_OVERRIDE"] = "1.0.0" });
        try
        {
            _ = await ClientRig.RunAsync("start");
            var first = rig.Daemons.Single();
            rig.Vars["DAEMON_VERSION"] = "2.0.0";
            rig.Vars["AHD_VERSION_OVERRIDE"] = "2.0.0";

            var plain = await ClientRig.RunAsync("start");
            Assert.Equal("running", Parse(plain.Out)["status"].AsString());
            Assert.False(first.Exited);

            var r = await ClientRig.RunMsgAliasAsync("start");

            Assert.Equal(0, r.Code);
            var second = rig.Daemons.Single(d => d.Pid != first.Pid);
            Assert.Equal($"ahd 2.0.0 pid {second.Pid}\n", r.Out);
            Assert.True(first.Exited);
        }
        finally
        {
            await rig.StopAllAsync();
        }
    }

    [Fact]
    public async Task MsgDaemonStartWithTheDaemonOffReportsItOnStderrAndExits1()
    {
        var rig = new ClientRig(new Dictionary<string, string?> { ["AH_DAEMON"] = "off" });
        try
        {
            var r = await ClientRig.RunMsgAliasAsync("start");

            Assert.Equal(1, r.Code);
            Assert.Equal(string.Empty, r.Out);
            Assert.Equal("error: daemon_unavailable: AH_DAEMON=off\n", r.Err);
            Assert.Empty(rig.Spawner.Requests);
        }
        finally
        {
            await rig.StopAllAsync();
        }
    }

    [Fact]
    public async Task MsgDaemonStopSaysStoppedForARunningDaemonAndNotRunningWhenThereIsNone()
    {
        var rig = new ClientRig();
        try
        {
            var none = await ClientRig.RunMsgAliasAsync("stop");
            Assert.Equal(0, none.Code);
            Assert.Equal("not running\n", none.Out);
            Assert.Equal(string.Empty, none.Err);

            _ = await ClientRig.RunMsgAliasAsync("start");
            var d = rig.Daemons.Single();
            var stopped = await ClientRig.RunMsgAliasAsync("stop");

            Assert.Equal(0, stopped.Code);
            Assert.Equal("stopped\n", stopped.Out);
            Assert.True(d.Exited);

            var again = await ClientRig.RunMsgAliasAsync("stop");
            Assert.Equal("not running\n", again.Out);
        }
        finally
        {
            await rig.StopAllAsync();
        }
    }

    [Fact]
    public async Task TheMsgAliasRefusesAVerbOtherThanStartAndStop()
    {
        var rig = new ClientRig();
        try
        {
            await Assert.ThrowsAsync<ArgumentException>(() => ClientRig.RunMsgAliasAsync("restart"));
        }
        finally
        {
            await rig.StopAllAsync();
        }
    }

    [Fact]
    public async Task StopStopsARunningDaemonAndSaysNotRunningWhenThereIsNone()
    {
        var rig = new ClientRig();
        try
        {
            var none = await ClientRig.RunAsync("stop");
            Assert.Equal("{\"ok\":true,\"source\":\"files\",\"status\":\"not-running\"}\n", none.Out);

            _ = await ClientRig.RunAsync("start");
            var d = rig.Daemons.Single();
            var stopped = await ClientRig.RunAsync("stop");

            Assert.Equal(0, stopped.Code);
            var body = Parse(stopped.Out);
            Assert.Equal("stopped", body["status"].AsString());
            Assert.Equal(d.Pid, (int)body["pid"].AsNumber());
            Assert.True(d.Exited);
        }
        finally
        {
            await rig.StopAllAsync();
        }
    }

    /// <summary>The real transport, with the drain request answered by <paramref name="onDrain"/>.</summary>
    private sealed class DrainAnswering(Func<HttpReply> onDrain) : IUnixHttpTransport
    {
        private readonly UnixTransport _real = new();

        public Task<HttpReply> SendAsync(string socketPath, string method, string pathAndQuery, ReadOnlyMemory<byte> body, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            pathAndQuery == "/v1/drain"
                ? Task.FromResult(onDrain())
                : _real.SendAsync(socketPath, method, pathAndQuery, body, timeout, cancellationToken);
    }

    private static HttpReply DrainError(int status, string code, string message) =>
        new(status, System.Text.Encoding.UTF8.GetBytes($"{{\"error\":{{\"code\":\"{code}\",\"message\":\"{message}\"}}}}"));

    private static HttpReply DrainRefused() => DrainError(403, "anonymous", "a drain needs a caller the daemon can name");

    [Fact]
    public async Task ADaemonAlreadyDrainingIsNotARefusalOfADrainOrAStop()
    {
        var rig = new ClientRig();
        try
        {
            _ = await ClientRig.RunAsync("start");
            _ = DaemonClientModule.Compose(DepsWith(rig, rig.Spawner, rig.Inspector) with { Transport = new DrainAnswering(() => DrainError(503, "draining", "the daemon is shutting down")) });

            var drained = await ClientRig.RunAsync("drain");
            var stopped = await ClientRig.RunAsync("stop");

            Assert.Equal(0, drained.Code);
            Assert.Equal("draining", Parse(drained.Out)["status"].AsString());
            Assert.DoesNotContain("drain_refused", stopped.Out, StringComparison.Ordinal);
        }
        finally
        {
            await rig.StopAllAsync();
        }
    }

    [Fact]
    public async Task AnotherErrorWithTheSameStatusAsDrainingIsStillARefusal()
    {
        var rig = new ClientRig();
        try
        {
            _ = await ClientRig.RunAsync("start");
            _ = DaemonClientModule.Compose(DepsWith(rig, rig.Spawner, rig.Inspector) with { Transport = new DrainAnswering(() => DrainError(503, "overloaded", "too busy")) });

            var result = await ClientRig.RunAsync("drain");

            Assert.Equal(1, result.Code);
            Assert.Contains("too busy", result.Out, StringComparison.Ordinal);
        }
        finally
        {
            await rig.StopAllAsync();
        }
    }

    [Theory]
    [InlineData("drain")]
    [InlineData("stop")]
    [InlineData("restart")]
    public async Task ADrainTheDaemonRefusesIsReportedAsARefusalWithItsReason(string verb)
    {
        var rig = new ClientRig();
        try
        {
            _ = await ClientRig.RunAsync("start");
            var d = rig.Daemons.Single();
            _ = DaemonClientModule.Compose(DepsWith(rig, rig.Spawner, rig.Inspector) with { Transport = new DrainAnswering(DrainRefused) });

            var sw = Stopwatch.StartNew();
            var refused = await ClientRig.RunAsync(verb);

            Assert.Equal(1, refused.Code);
            Assert.Contains("\"drain_refused\"", refused.Out, StringComparison.Ordinal);
            Assert.Contains("a drain needs a caller the daemon can name", refused.Out, StringComparison.Ordinal);
            Assert.DoesNotContain("stop_failed", refused.Out, StringComparison.Ordinal);
            Assert.False(d.Exited);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"a refusal waited {sw.Elapsed} for an exit that was never coming");
        }
        finally
        {
            await rig.StopAllAsync();
        }
    }

    [Fact]
    public async Task ADrainWithNoAnswerFromALiveDaemonIsNotReportedAsDraining()
    {
        var rig = new ClientRig();
        try
        {
            _ = await ClientRig.RunAsync("start");
            _ = DaemonClientModule.Compose(DepsWith(rig, rig.Spawner, rig.Inspector) with { Transport = new DrainAnswering(() => throw new IOException("no answer")) });

            var result = await ClientRig.RunAsync("drain");

            Assert.Equal(1, result.Code);
            Assert.Contains("\"drain_no_answer\"", result.Out, StringComparison.Ordinal);
        }
        finally
        {
            await rig.StopAllAsync();
        }
    }

    [Theory]
    [InlineData(20_000, true)]
    [InlineData(100, false)]
    public async Task ADaemonSlowToExitAfterItsSignalIsWaitedForAndOnlyAnExpiredWaitIsAFailure(int waitMs, bool stopped)
    {
        var rig = new ClientRig();
        Task? exit = null;
        var processes = new ScriptedProcesses("/p/ahd serve --ah-home " + rig.Home, () => exit = Task.Delay(6000));
        _ = DaemonClientModule.Compose(DepsWith(rig, rig.Spawner, new FakeInspector(pid => exit is not { IsCompleted: true } && rig.Inspector.PidAlive(pid))) with
        {
            Processes = processes,
            Transport = new DrainAnswering(() => DrainError(503, "overloaded", "too busy")),
            StopExitWaitMs = waitMs,
        });
        try
        {
            _ = await ClientRig.RunAsync("start");

            var result = await ClientRig.RunAsync("stop");

            if (stopped)
            {
                Assert.Equal(0, result.Code);
                Assert.Equal("stopped", Parse(result.Out)["status"].AsString());
            }
            else
            {
                Assert.Equal(1, result.Code);
                Assert.Contains("\"stop_failed\"", result.Out, StringComparison.Ordinal);
            }
        }
        finally
        {
            await rig.StopAllAsync();
        }
    }

    [Fact]
    public async Task ADrainWithNoAnswerBecauseTheDaemonJustExitedIsNotAFailure()
    {
        var rig = new ClientRig();
        try
        {
            _ = await ClientRig.RunAsync("start");
            var alive = true;
            var inspector = new FakeInspector(pid => alive && rig.Inspector.PidAlive(pid));
            _ = DaemonClientModule.Compose(DepsWith(rig, rig.Spawner, inspector) with
            {
                Transport = new DrainAnswering(() =>
                {
                    alive = false;
                    throw new IOException("gone");
                }),
            });

            var result = await ClientRig.RunAsync("drain");

            Assert.Equal(0, result.Code);
            Assert.Equal("not-running", Parse(result.Out)["status"].AsString());
        }
        finally
        {
            await rig.StopAllAsync();
        }
    }

    [Fact]
    public async Task RestartReplacesTheDaemonAndNamesBothPids()
    {
        var rig = new ClientRig();
        try
        {
            _ = await ClientRig.RunAsync("start");
            var first = rig.Daemons.Single();

            var r = await ClientRig.RunAsync("restart");

            Assert.Equal(0, r.Code);
            var body = Parse(r.Out);
            Assert.Equal("restarted", body["status"].AsString());
            Assert.Equal(first.Pid, (int)body["old_pid"].AsNumber());
            Assert.NotEqual(first.Pid, (int)body["new_pid"].AsNumber());
            Assert.True(first.Exited);
            Assert.Equal(["ok", "source", "status", "started", "pid", "version", "old_pid", "new_pid"], body.Keys);
        }
        finally
        {
            await rig.StopAllAsync();
        }
    }

    [Fact]
    public async Task VersionComparesTheClientWithTheDaemon()
    {
        var rig = new ClientRig(new Dictionary<string, string?> { ["DAEMON_VERSION"] = "1.0.0", ["AHD_VERSION_OVERRIDE"] = "1.0.0" });
        try
        {
            var down = await ClientRig.RunAsync("version");
            Assert.Equal("{\"ok\":true,\"source\":\"files\",\"client\":{\"version\":\"1.0.0\"},\"daemon\":null,\"verdict\":null}\n", down.Out);

            _ = await ClientRig.RunAsync("start");
            var up = await ClientRig.RunAsync("version");

            var body = Parse(up.Out);
            Assert.Equal("equal", body["verdict"].AsString());
            Assert.Equal("daemon", body["source"].AsString());
            Assert.Equal(["version", "protocols", "pid"], body["daemon"].AsObject().Keys);
        }
        finally
        {
            await rig.StopAllAsync();
        }
    }

    [Fact]
    public async Task LogsReturnTheLastLinesOfTheDaemonLog()
    {
        var rig = new ClientRig();
        try
        {
            var log = Path.Combine(rig.Home, "logs", "ahd.log");
            File.WriteAllText(log, string.Join('\n', Enumerable.Range(1, 8).Select(i => $"line {i}")) + "\n");

            var r = await ClientRig.RunAsync("logs", "-n", "3");

            Assert.Equal(0, r.Code);
            var body = Parse(r.Out);
            Assert.Equal(["line 6", "line 7", "line 8"], body["lines"].AsArray().Select(v => v.AsString()));
            Assert.Equal(log, body["path"].AsString());
            var plain = await ClientRig.RunAsync("logs", "-n", "2", "--plain");
            Assert.Equal("line 7\nline 8\n", plain.Out);
        }
        finally
        {
            await rig.StopAllAsync();
        }
    }

    [Theory]
    [InlineData("start", new[] { "--bogus" }, "unknown flag --bogus")]
    [InlineData("start", new[] { "extra" }, "unexpected argument \"extra\"")]
    [InlineData("drain", new[] { "--cwd" }, "--cwd needs a value")]
    [InlineData("start", new[] { "--require" }, "start does not take --require")]
    [InlineData("logs", new[] { "-n", "0" }, "-n must be a whole number from 1 to 1000")]
    [InlineData("logs", new[] { "-n", "1001" }, "-n must be a whole number from 1 to 1000")]
    [InlineData("logs", new[] { "-n", "x" }, "-n must be a whole number from 1 to 1000")]
    [InlineData("status", new[] { "--", "cmd" }, "status takes no -- command")]
    public async Task UsageErrorsAreExit2WithNodesMessages(string verb, string[] args, string message)
    {
        var rig = new ClientRig();
        try
        {
            var r = await ClientRig.RunAsync(verb, args);

            Assert.Equal(2, r.Code);
            Assert.Equal($"{{\"ok\":false,\"error\":{{\"code\":\"usage\",\"message\":{JsonText.Stringify(message, 0)}}}}}\n", r.Out);
            Assert.Empty(rig.Spawner.Requests);
        }
        finally
        {
            await rig.StopAllAsync();
        }
    }

    [Fact]
    public async Task APlainUsageErrorGoesToStderr()
    {
        var rig = new ClientRig();

        var r = await ClientRig.RunAsync("start", "--bogus", "--plain");

        Assert.Equal(2, r.Code);
        Assert.Equal(string.Empty, r.Out);
        Assert.Equal("error: usage: unknown flag --bogus\n", r.Err);
    }

    [Fact]
    public async Task AhDaemonOffDisablesDiscoveryAndStart()
    {
        var rig = new ClientRig(new Dictionary<string, string?> { ["AH_DAEMON"] = "off" });

        var started = await ClientRig.RunAsync("start");
        var info = await rig.Client.EnsureDaemonAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(1, started.Code);
        Assert.Equal(
            "{\"ok\":false,\"source\":\"files\",\"error\":{\"code\":\"daemon_unavailable\",\"message\":\"AH_DAEMON=off\"}}\n",
            started.Out);
        Assert.Null(info);
        Assert.Empty(rig.Spawner.Requests);
    }

    [Fact]
    public async Task ASocketPathOver103BytesIsNotASpawnFailure()
    {
        var box = Sandbox.Create();
        var deep = Path.Combine(box.Root, new string('d', 100));
        Directory.CreateDirectory(Path.Combine(deep, "run"));
        var rig = new ClientRig(ahHome: deep);

        var info = await rig.Client.EnsureDaemonAsync(TimeSpan.FromSeconds(2));
        var started = await ClientRig.RunAsync("start");

        Assert.Null(info);
        Assert.Equal(1, started.Code);
        Assert.Contains("socket path is", Parse(started.Out)["error"].AsObject()["message"].AsString(), StringComparison.Ordinal);
        Assert.Empty(rig.Spawner.Requests);
        Assert.False(File.Exists(rig.MarkerPath));
    }

    [Fact]
    public async Task WithNoAhdJsonAPidFileNamingALiveDaemonIsSlowNotDown()
    {
        var rig = new ClientRig();
        File.WriteAllText(Path.Combine(rig.Home, "run", "ahd.pid"), "{\"pid\":777,\"start\":\"T777\"}\n");
        var spawner = new FakeSpawner(_ => 6_999_999);
        var client = DaemonClientModule.Compose(DepsWith(rig, spawner, new FakeInspector(pid => pid == 777)) with { StartTime = pid => pid == 777 ? "T777" : null });

        Assert.Null(await client.EnsureDaemonForHookAsync(TimeSpan.FromSeconds(3)));
        Assert.Empty(spawner.Requests);

        var reused = DaemonClientModule.Compose(DepsWith(rig, spawner, new FakeInspector(pid => pid == 777)) with { StartTime = pid => pid == 777 ? "T-reused" : null });
        _ = await reused.EnsureDaemonForHookAsync(TimeSpan.FromSeconds(3));
        Assert.Single(spawner.Requests);
    }

    [Fact]
    public async Task AFailedSpawnWritesTheMarkerAndImplicitStartsBackOffButAnExplicitStartTries()
    {
        var rig = new ClientRig();
        var deadPid = 6_999_999;
        var spawner = new FakeSpawner(_ => deadPid);
        var client = DaemonClientModule.Compose(DepsWith(rig, spawner, new FakeInspector(_ => false)));

        var sw = Stopwatch.StartNew();
        var first = await client.EnsureDaemonAsync(TimeSpan.FromSeconds(6));
        Assert.Null(first);
        Assert.Single(spawner.Requests);
        Assert.True(File.Exists(rig.MarkerPath));
        var marker = JsonText.Parse(File.ReadAllText(rig.MarkerPath)).AsObject();
        Assert.Equal(["at", "pid", "reason"], marker.Keys);
        Assert.Equal(deadPid, (int)marker["pid"].AsNumber());

        var second = await client.EnsureDaemonAsync(TimeSpan.FromSeconds(6));
        Assert.Null(second);
        Assert.Single(spawner.Requests);

        var explicitStart = await DaemonClientModule.Lifecycle!.RunAsync("start", [], new StringWriter(), new StringWriter());
        Assert.Equal(1, explicitStart);
        Assert.Equal(2, spawner.Requests.Count);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(60), $"took {sw.Elapsed}");
    }

    [Theory]
    [InlineData("/private/tmp/ahprobe.shimroot", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public async Task TheSpawnHandsTheDaemonTheCheckedPluginRootAsALeadingOptionOnly(string? root, bool expectOption)
    {
        var rig = new ClientRig();
        var spawner = new FakeSpawner(_ => 6_999_999);
        var deps = DepsWith(rig, spawner, new FakeInspector(_ => false)) with { SpawnPluginRoot = () => root };
        var client = DaemonClientModule.Compose(deps);

        _ = await client.EnsureDaemonAsync(TimeSpan.FromSeconds(2));

        var argv = Assert.Single(spawner.Requests).Argv;
        if (expectOption)
        {
            Assert.Equal(["/private/tmp/ahprobe.bin/ahd", "--plugin-root", root!, "serve", "--ah-home", rig.Home], argv);
        }
        else
        {
            Assert.Equal(["/private/tmp/ahprobe.bin/ahd", "serve", "--ah-home", rig.Home], argv);
        }
    }

    [Fact]
    public async Task ThePluginRootIsAskedAtSpawnTimeAndNotWhenTheClientIsBuilt()
    {
        var rig = new ClientRig();
        var spawner = new FakeSpawner(_ => 6_999_999);
        var asked = 0;
        var deps = DepsWith(rig, spawner, new FakeInspector(_ => false)) with { SpawnPluginRoot = () => { asked++; return null; } };

        var client = DaemonClientModule.Compose(deps);
        Assert.Equal(0, asked);
        _ = await client.EnsureDaemonAsync(TimeSpan.FromSeconds(2));

        Assert.True(asked > 0);
    }

    [Fact]
    public async Task AMarkerOlderThan60SecondsIsIgnoredAndAHealthyDaemonClearsIt()
    {
        var rig = new ClientRig();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(rig.MarkerPath)!);
            File.WriteAllText(rig.MarkerPath, "{\"at\":\"2020-01-01T00:00:00.000Z\",\"pid\":1,\"reason\":\"old\"}\n");
            var info = await rig.Client.EnsureDaemonAsync(TimeSpan.FromSeconds(6));
            Assert.NotNull(info);
            Assert.False(File.Exists(rig.MarkerPath));

            var now = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);
            File.WriteAllText(rig.MarkerPath, $"{{\"at\":\"{now}\",\"pid\":1,\"reason\":\"fresh\"}}\n");
            var found = await rig.Client.DiscoverAsync(TimeSpan.FromSeconds(1));

            Assert.NotNull(found);
            Assert.False(File.Exists(rig.MarkerPath));
        }
        finally
        {
            await rig.StopAllAsync();
        }
    }

    [Fact]
    public async Task ADaemonOlderThanTheClientIsDrainedAndReplaced()
    {
        var spawns = 0;
        var rig = new ClientRig(daemon: o => o.Version = ++spawns == 1 ? "0.5.0" : "9.9.9");
        try
        {
            var info = await rig.Client.EnsureDaemonAsync(TimeSpan.FromSeconds(10));

            Assert.NotNull(info);
            Assert.Equal("9.9.9", info.Version);
            Assert.Equal(2, rig.Spawner.Requests.Count);
            Assert.True(rig.Daemons.First().Exited);
        }
        finally
        {
            await rig.StopAllAsync();
        }
    }

    [Fact]
    public async Task ANewerDaemonIsLeftAlone()
    {
        var rig = new ClientRig(new Dictionary<string, string?> { ["DAEMON_VERSION"] = "2.0.0", ["AHD_VERSION_OVERRIDE"] = "1.0.0" });
        try
        {
            var first = await rig.Client.EnsureDaemonAsync(TimeSpan.FromSeconds(6));
            var again = await rig.Client.EnsureDaemonAsync(TimeSpan.FromSeconds(6));

            Assert.NotNull(first);
            Assert.NotNull(again);
            Assert.Equal(first.Pid, again.Pid);
            Assert.Single(rig.Spawner.Requests);
        }
        finally
        {
            await rig.StopAllAsync();
        }
    }

    [Fact]
    public async Task AHookMeetingAnOlderDaemonDrainsItAtOnceAndSpawnsNoReplacementWhereACliCallReplacesIt()
    {
        var rig = new ClientRig(daemon: o => o.Version = "0.5.0");
        try
        {
            // The CLI call drains the first old daemon, spawns another that is just as old and reports it, leaving it running.
            var cli = await rig.Client.EnsureDaemonAsync(TimeSpan.FromSeconds(10));
            Assert.Null(cli);
            var spawned = rig.Spawner.Requests.Count;
            var running = rig.Daemons.Single(d => !d.Exited);

            var sw = Stopwatch.StartNew();
            var hook = await rig.Client.EnsureDaemonForHookAsync(TimeSpan.FromSeconds(60));

            Assert.Null(hook);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), $"the hook took {sw.Elapsed} on a 60 s budget");
            Assert.Equal(spawned, rig.Spawner.Requests.Count);
            Assert.True(await running.ExitsWithinAsync(TimeSpan.FromSeconds(30)));
        }
        finally
        {
            await rig.StopAllAsync();
        }
    }

    [Fact]
    public async Task AHookStartsADaemonWhenNoneIsUpAndFindsTheOneOfItsOwnVersionAfterwards()
    {
        var rig = new ClientRig();
        try
        {
            var first = await rig.Client.EnsureDaemonForHookAsync(TimeSpan.FromSeconds(5));
            var again = await rig.Client.EnsureDaemonForHookAsync(TimeSpan.FromSeconds(5));

            Assert.NotNull(first);
            Assert.NotNull(again);
            Assert.Equal(first.Pid, again.Pid);
            Assert.Single(rig.Spawner.Requests);
        }
        finally
        {
            await rig.StopAllAsync();
        }
    }

    /// <summary>A rig whose daemons answer <c>GET /t/hold</c> only once <paramref name="gate"/> opens, so one can be kept draining.</summary>
    private static ClientRig HoldingRig(TaskCompletionSource entered, TaskCompletionSource gate) =>
        new(daemon: o => o.Routes.Add(new RouteRegistration("GET /t/hold", async (request, drain) =>
        {
            _ = entered.TrySetResult();
            await gate.Task;
            return TestRoutes.Json(200, "{\"held\":true}");
        })));

    /// <summary>The drain request is answered before the daemon flags itself as draining; wait until its health answer says so.</summary>
    private static async Task WaitUntilDrainingAsync(Harness daemon)
    {
        var deadline = Stopwatch.StartNew();
        while ((await daemon.GetAsync("/v1/health")).Status != 503)
        {
            Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(10), "the daemon never reported draining");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task ACliCallMeetingADrainingDaemonWaitsForItToExitAndThenStartsTheReplacement()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rig = HoldingRig(entered, gate);
        try
        {
            var first = await rig.Client.EnsureDaemonAsync(TimeSpan.FromSeconds(6));
            Assert.NotNull(first);
            var draining = rig.Daemons.Single();
            var hold = draining.GetAsync("/t/hold", TimeSpan.FromSeconds(20));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            _ = await draining.PostAsync("/v1/drain", "{}");
            await WaitUntilDrainingAsync(draining);

            var polledBefore = rig.Inspector.Polls(draining.Pid);
            var ensure = rig.Client.EnsureDaemonAsync(TimeSpan.FromSeconds(10));
            // The client is waiting for the draining daemon to exit once it has asked ten more times whether it is alive (it asks
            // about every 50 ms, so a client that gives up and spawns after only a few polls is still caught); the deadline only
            // guards a hang and is not the assertion.
            var guard = Stopwatch.StartNew();
            while (rig.Inspector.Polls(draining.Pid) < polledBefore + 10 && !ensure.IsCompleted)
            {
                Assert.True(guard.Elapsed < TimeSpan.FromSeconds(30), "the client never polled the draining daemon");
                await Task.Delay(10);
            }

            Assert.False(ensure.IsCompleted);
            Assert.Single(rig.Spawner.Requests);

            gate.SetResult();
            var replacement = await ensure;

            Assert.NotNull(replacement);
            Assert.NotEqual(first.Pid, replacement.Pid);
            Assert.Equal(2, rig.Spawner.Requests.Count);
            _ = await hold;
        }
        finally
        {
            gate.TrySetResult();
            await rig.StopAllAsync();
        }
    }

    [Fact]
    public async Task AHookMeetingADrainingDaemonStopsWaitingAtItsBudgetAndStartsNothing()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rig = HoldingRig(entered, gate);
        try
        {
            var first = await rig.Client.EnsureDaemonAsync(TimeSpan.FromSeconds(6));
            Assert.NotNull(first);
            var draining = rig.Daemons.Single();
            var hold = draining.GetAsync("/t/hold", TimeSpan.FromSeconds(20));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            _ = await draining.PostAsync("/v1/drain", "{}");
            await WaitUntilDrainingAsync(draining);

            var sw = Stopwatch.StartNew();
            var info = await rig.Client.EnsureDaemonForHookAsync(TimeSpan.FromMilliseconds(900));

            Assert.Null(info);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), $"the hook waited {sw.Elapsed} on a 0.9 s budget with the daemon still draining");
            Assert.Single(rig.Spawner.Requests);
            gate.SetResult();
            _ = await hold;
        }
        finally
        {
            gate.TrySetResult();
            await rig.StopAllAsync();
        }
    }

    private const int HungPid = 4_242;

    /// <summary>A listener nobody accepts from, and the <c>ahd.json</c> naming it: a daemon whose pid is alive and whose health times out.</summary>
    private sealed class HungDaemon : IDisposable
    {
        private readonly System.Net.Sockets.Socket _listener;

        internal HungDaemon(string home)
        {
            var run = Path.Combine(home, "run");
            _ = Directory.CreateDirectory(run);
            var sock = Path.Combine(run, "ahd.sock");
            _listener = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified);
            _listener.Bind(new System.Net.Sockets.UnixDomainSocketEndPoint(sock));
            _listener.Listen(8);
            File.WriteAllText(
                Path.Combine(run, "ahd.json"),
                $"{{\"v\":1,\"pid\":{HungPid},\"version\":\"9.9.9\",\"protocol\":1,\"socket\":\"{sock}\",\"root\":\"/x\",\"started_at\":\"2026-01-01T00:00:00.000Z\"}}\n");
        }

        public void Dispose() => _listener.Dispose();
    }

    /// <summary>Answers <c>ps -p &lt;pid&gt; -o command=</c> with a scripted command line and records every <c>kill</c>.</summary>
    private sealed class ScriptedProcesses(string command, Action onKill) : IProcessRunner
    {
        internal List<IReadOnlyList<string>> Kills { get; } = [];

        public ProcessResult Run(ProcessRequest request)
        {
            if (request.Argv is ["ps", ..])
            {
                return new ProcessResult(0, null, false, false, System.Text.Encoding.UTF8.GetBytes(command + "\n"), ReadOnlyMemory<byte>.Empty);
            }

            if (request.Argv is ["kill", ..])
            {
                Kills.Add(request.Argv);
                onKill();
                return new ProcessResult(0, null, false, false, ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty);
            }

            return new ProcessResult(1, null, false, false, ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty);
        }
    }

    [Fact]
    public async Task ADaemonWhosePidLivesAndThatDoesNotAnswerIsNotReplaced()
    {
        var rig = new ClientRig();
        using var hung = new HungDaemon(rig.Home);
        var spawner = new FakeSpawner(_ => HungPid + 1);
        var client = DaemonClientModule.Compose(DepsWith(rig, spawner, new FakeInspector(pid => pid == HungPid)));

        Assert.Null(await client.EnsureDaemonAsync(TimeSpan.FromSeconds(30)));

        Assert.Empty(spawner.Requests);
    }

    [Fact]
    public async Task NegativeADaemonWhosePidIsGoneIsReplaced()
    {
        var rig = new ClientRig();
        using var hung = new HungDaemon(rig.Home);
        var spawner = new FakeSpawner(_ => HungPid + 1);
        var client = DaemonClientModule.Compose(DepsWith(rig, spawner, new FakeInspector(static _ => false)));

        _ = await client.EnsureDaemonAsync(TimeSpan.FromSeconds(30));

        Assert.NotEmpty(spawner.Requests);
    }

    /// <summary>
    /// A hook's writes each asked a hung daemon and waited out its wait, which together passed the hook's 5 s bound (b307). A hook's
    /// client asks a silent pid once; any other client asks every time, and the hook's asks a different pid afresh.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AHooksClientAsksAHungDaemonOnceAndADifferentPidAgain(bool oneStrike)
    {
        var rig = new ClientRig();
        using var hung = new HungDaemon(rig.Home);
        var client = DaemonClientModule.Compose(DepsWith(rig, new FakeSpawner(_ => HungPid + 1), new FakeInspector(pid => pid == HungPid)) with { OneStrike = oneStrike });
        var wait = TimeSpan.FromMilliseconds(300);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 6; i++)
        {
            Assert.Null(await client.DiscoverAsync(wait, TestContext.Current.CancellationToken));
        }

        var took = clock.Elapsed;
        if (oneStrike)
        {
            Assert.True(took < wait * 3, $"six discoveries took {took}");
        }
        else
        {
            Assert.True(took >= wait * 5, $"six discoveries took {took}");
        }

        // the nearest pid that must not be skipped: another daemon under the same socket
        var info = Path.Combine(rig.Home, "run", "ahd.json");
        File.WriteAllText(info, File.ReadAllText(info).Replace($"\"pid\":{HungPid}", $"\"pid\":{HungPid + 1}", StringComparison.Ordinal));
        clock.Restart();
        Assert.Null(await client.DiscoverAsync(wait, TestContext.Current.CancellationToken));
        Assert.True(clock.Elapsed >= wait * 0.8, $"a new pid was not asked: {clock.Elapsed}");
    }

    [Theory]
    [InlineData("/p/ahd serve --ah-home {HOME}", true)]
    [InlineData("/p/ahd serve --ah-home /some/other/home", false)]
    [InlineData("/p/ahd serve --ah-home {HOME}-x", false)]
    [InlineData("/usr/bin/vim --ah-home {HOME}", false)]
    public async Task StopSignalsAHungDaemonOnlyWhenItsCommandLineIsThisStateHomesDaemon(string template, bool signalled)
    {
        var rig = new ClientRig();
        using var hung = new HungDaemon(rig.Home);
        var alive = true;
        var processes = new ScriptedProcesses(template.Replace("{HOME}", rig.Home, StringComparison.Ordinal), () => alive = false);
        var deps = DepsWith(rig, new FakeSpawner(_ => HungPid), new FakeInspector(pid => pid == HungPid && alive)) with { Processes = processes };
        _ = DaemonClientModule.Compose(deps);

        var result = await ClientRig.RunAsync("stop");

        Assert.Equal(signalled, processes.Kills.Count == 1);
        if (signalled)
        {
            Assert.Equal(["kill", "-TERM", "4242"], processes.Kills[0]);
            Assert.Contains("stopped", result.Out, StringComparison.Ordinal);
        }
        else
        {
            Assert.Empty(processes.Kills);
            Assert.DoesNotContain("\"stopped\":true", result.Out, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestartStopsAHungDaemonNamedOnlyByItsPidFileAndStartsAFreshOne(bool staleInfo)
    {
        var rig = new ClientRig();
        using var hung = new HungDaemon(rig.Home);
        var info = Path.Combine(rig.Home, "run", "ahd.json");
        if (staleInfo)
        {
            // a crashed daemon's ahd.json, naming a pid that is gone
            File.WriteAllText(info, File.ReadAllText(info).Replace($"\"pid\":{HungPid}", "\"pid\":4343", StringComparison.Ordinal));
        }
        else
        {
            File.Delete(info);
        }

        File.WriteAllText(Path.Combine(rig.Home, "run", "ahd.pid"), $"{{\"pid\":{HungPid},\"start\":\"T-hung\"}}\n");
        var alive = true;
        var processes = new ScriptedProcesses($"/p/ahd serve --ah-home {rig.Home}", () => alive = false);
        var deps = DepsWith(rig, rig.Spawner, new FakeInspector(pid => (pid == HungPid && alive) || rig.Inspector.PidAlive(pid))) with
        {
            Processes = processes,
            StartTime = pid => pid == HungPid && alive ? "T-hung" : null,
        };
        _ = DaemonClientModule.Compose(deps);
        try
        {
            var r = await ClientRig.RunAsync("restart");

            Assert.Equal(["kill", "-TERM", "4242"], Assert.Single(processes.Kills));
            Assert.Equal(0, r.Code);
            var body = Parse(r.Out);
            Assert.Equal("restarted", body["status"].AsString());
            Assert.Equal(HungPid, (int)body["old_pid"].AsNumber());
            Assert.Single(rig.Daemons);
        }
        finally
        {
            await rig.StopAllAsync();
        }
    }

    [Fact]
    public async Task AStopTheDaemonRefusesSendsNoSignalAndDoesNotWaitForAnExit()
    {
        var rig = new ClientRig();
        var processes = new ScriptedProcesses($"/p/ahd serve --ah-home {rig.Home}", () => { });
        try
        {
            _ = await ClientRig.RunAsync("start");
            var daemon = rig.Daemons.Single();
            _ = DaemonClientModule.Compose(DepsWith(rig, rig.Spawner, rig.Inspector) with { Transport = new DrainAnswering(DrainRefused), Processes = processes });
            var watch = Stopwatch.StartNew();

            var result = await ClientRig.RunAsync("stop");

            Assert.Empty(processes.Kills);
            Assert.Contains("\"drain_refused\"", result.Out, StringComparison.Ordinal);
            Assert.False(daemon.Exited);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(4), $"a refusal waited {watch.Elapsed}");
        }
        finally
        {
            await rig.StopAllAsync();
        }
    }

    [Fact]
    public async Task AStopAnsweredWithA500IsNoRefusalAndStillSignalsTheDaemon()
    {
        var rig = new ClientRig();
        var processes = new ScriptedProcesses($"/p/ahd serve --ah-home {rig.Home}", () => { });
        try
        {
            _ = await ClientRig.RunAsync("start");
            var daemon = rig.Daemons.Single();
            _ = DaemonClientModule.Compose(DepsWith(rig, rig.Spawner, rig.Inspector) with { Transport = new DrainAnswering(() => DrainError(500, "internal", "internal error")), Processes = processes });

            var result = await ClientRig.RunAsync("stop");

            Assert.Equal(["kill", "-TERM", daemon.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture)], Assert.Single(processes.Kills));
            Assert.DoesNotContain("\"drain_refused\"", result.Out, StringComparison.Ordinal);
        }
        finally
        {
            await rig.StopAllAsync();
        }
    }

    [Fact]
    public async Task AStartMeetingAnOlderDaemonThatRefusesItsDrainNamesTheRefusal()
    {
        var rig = new ClientRig(daemon: o => o.Version = "0.5.0");
        try
        {
            _ = await ClientRig.RunAsync("start");
            var running = rig.Daemons.Single(d => !d.Exited);
            var spawned = rig.Spawner.Requests.Count;
            _ = DaemonClientModule.Compose(DepsWith(rig, rig.Spawner, rig.Inspector) with { Transport = new DrainAnswering(DrainRefused) });
            var watch = Stopwatch.StartNew();

            var result = await ClientRig.RunMsgAliasAsync("start");

            Assert.Contains("refused to drain: a drain needs a caller the daemon can name", result.Err, StringComparison.Ordinal);
            Assert.DoesNotContain("did not exit", result.Err, StringComparison.Ordinal);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(4), $"a refusal waited {watch.Elapsed}");
            Assert.False(running.Exited);
            Assert.Equal(spawned, rig.Spawner.Requests.Count);
        }
        finally
        {
            await rig.StopAllAsync();
        }
    }

    [Fact]
    public async Task AStartMeetingAnOlderDaemonThatAnswersItsDrainWithA500NamesNoRefusal()
    {
        var rig = new ClientRig(daemon: o => o.Version = "0.5.0");
        try
        {
            _ = await ClientRig.RunAsync("start");
            _ = DaemonClientModule.Compose(DepsWith(rig, rig.Spawner, rig.Inspector) with { Transport = new DrainAnswering(() => DrainError(500, "internal", "internal error")) });

            var result = await ClientRig.RunMsgAliasAsync("start");

            Assert.DoesNotContain("refused to drain", result.Err, StringComparison.Ordinal);
            Assert.Contains("did not exit", result.Err, StringComparison.Ordinal);
        }
        finally
        {
            await rig.StopAllAsync();
        }
    }

    [Theory]
    [InlineData("/p/ahd serve --ah-home {HOME}", true)]
    [InlineData("/p/ahd serve --ah-home /some/other/home", false)]
    public async Task StopSignalsABusyDaemonStillRunningAfterItsDrainOnlyWhenItIsThisHomesDaemon(string template, bool signalled)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rig = HoldingRig(entered, gate);
        // a real TERM does not end a drain already running, so the scripted one does not release the held request either
        var processes = new ScriptedProcesses(template.Replace("{HOME}", rig.Home, StringComparison.Ordinal), () => { });
        // the client's drain wait ends well before the held daemon's own drain deadline, so the stop reaches its TERM path
        _ = DaemonClientModule.Compose(DepsWith(rig, rig.Spawner, rig.Inspector) with { Processes = processes, DrainWaitMs = 1000 });
        try
        {
            _ = await ClientRig.RunAsync("start");
            var busy = rig.Daemons.Single();
            var hold = busy.GetAsync("/t/hold", TimeSpan.FromSeconds(30));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            var result = await ClientRig.RunAsync("stop");

            if (signalled)
            {
                Assert.Equal(["kill", "-TERM", busy.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture)], Assert.Single(processes.Kills));
                Assert.Equal("stopped", Parse(result.Out)["status"].AsString());
            }
            else
            {
                Assert.Empty(processes.Kills);
                Assert.NotEqual("stopped", Parse(result.Out)["status"].AsString());
            }

            gate.TrySetResult();
            _ = await hold;
        }
        finally
        {
            gate.TrySetResult();
            await rig.StopAllAsync();
        }
    }

    [Fact]
    public async Task StopDoesNotSignalADaemonThatExitsOnItsDrain()
    {
        var rig = new ClientRig();
        var processes = new ScriptedProcesses($"/p/ahd serve --ah-home {rig.Home}", () => { });
        _ = DaemonClientModule.Compose(DepsWith(rig, rig.Spawner, rig.Inspector) with { Processes = processes });
        try
        {
            _ = await ClientRig.RunAsync("start");

            var result = await ClientRig.RunAsync("stop");

            Assert.Empty(processes.Kills);
            Assert.Equal("stopped", Parse(result.Out)["status"].AsString());
        }
        finally
        {
            await rig.StopAllAsync();
        }
    }

    [Fact]
    public async Task AHookGivesUpOnAHungDaemonWithinItsBudget()
    {
        var rig = new ClientRig();
        using var hung = new HungDaemon(rig.Home);
        var spawner = new FakeSpawner(_ => 6_999_999);
        var client = DaemonClientModule.Compose(DepsWith(rig, spawner, new FakeInspector(pid => pid == HungPid)));

        var sw = Stopwatch.StartNew();
        var info = await client.EnsureDaemonForHookAsync(TimeSpan.FromMilliseconds(1200));

        Assert.Null(info);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), $"the hook took {sw.Elapsed} on a 1.2 s budget against a daemon that never answers");
    }

    [Fact]
    public async Task RequestsGoToTheDaemonAndAnUnserved404MeansUseTheFiles()
    {
        var rig = new ClientRig(daemon: o => o.Routes.Add(new RouteRegistration("GET /t/ok", (_, _) =>
            Task.FromResult(TestRoutes.Json(200, "{\"fine\":true}")))));
        try
        {
            var ok = await rig.Client.RequestAsync(new DaemonRequest("GET", "/t/ok"));
            var missing = await rig.Client.RequestAsync(new DaemonRequest("GET", "/t/missing"));

            Assert.Equal(200, ok.Status);
            Assert.Equal("{\"fine\":true}", System.Text.Encoding.UTF8.GetString(ok.Body.Span));
            Assert.Equal(0, missing.Status);
        }
        finally
        {
            await rig.StopAllAsync();
        }
    }

    [Fact]
    public async Task ARequestThatFallsBackSaysWhy()
    {
        var rig = new ClientRig(daemon: o =>
        {
            o.Routes.Add(new RouteRegistration("GET /t/boom", (_, _) => Task.FromResult(TestRoutes.Json(500, "{}"))));
            o.Routes.Add(new RouteRegistration("GET /t/busy", (_, _) =>
                Task.FromResult(TestRoutes.Json(503, "{\"error\":{\"code\":\"busy\",\"message\":\"b\"}}"))));
            o.Routes.Add(new RouteRegistration("GET /t/slow", async (_, token) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(60), token);
                return TestRoutes.Json(200, "{}");
            }));
        });
        try
        {
            var missing = await rig.Client.RequestAsync(new DaemonRequest("GET", "/t/missing"));
            Assert.Equal(0, missing.Status);
            Assert.Equal("http 404 not_found", missing.FallbackReason);
            Assert.False(missing.TimedOut);
            Assert.NotNull(missing.SentAt);

            var boom = await rig.Client.RequestAsync(new DaemonRequest("GET", "/t/boom"));
            Assert.Equal(0, boom.Status);
            Assert.Equal("http 500 -", boom.FallbackReason);

            var busy = await rig.Client.RequestAsync(new DaemonRequest("GET", "/t/busy"));
            Assert.Equal(0, busy.Status);
            Assert.Equal("http 503 busy", busy.FallbackReason);

            var slow = await rig.Client.RequestAsync(new DaemonRequest("GET", "/t/slow", Timeout: TimeSpan.FromMilliseconds(250)));
            Assert.Equal(0, slow.Status);
            Assert.Equal("timeout", slow.FallbackReason);
            Assert.True(slow.TimedOut);
            Assert.NotNull(slow.SentAt);
        }
        finally
        {
            await rig.StopAllAsync();
        }
    }

    [Fact]
    public async Task AnAnswerTheCallerCanActOnIsNotAFallback()
    {
        var rig = new ClientRig(daemon: o =>
        {
            o.Routes.Add(new RouteRegistration("GET /t/refused", (_, _) =>
                Task.FromResult(TestRoutes.Json(409, "{\"error\":{\"code\":\"conflict\",\"message\":\"c\"}}"))));
            o.Routes.Add(new RouteRegistration("GET /t/mirror", (_, _) =>
                Task.FromResult(TestRoutes.Json(500, "{\"error\":{\"code\":\"mirror_failed\",\"message\":\"m\"}}"))));
        });
        try
        {
            var refused = await rig.Client.RequestAsync(new DaemonRequest("GET", "/t/refused"));
            Assert.Equal(409, refused.Status);
            Assert.Null(refused.FallbackReason);
            Assert.False(refused.TimedOut);

            var mirror = await rig.Client.RequestAsync(new DaemonRequest("GET", "/t/mirror"));
            Assert.Equal(500, mirror.Status);
            Assert.Null(mirror.FallbackReason);
        }
        finally
        {
            await rig.StopAllAsync();
        }
    }

    [Fact]
    public async Task ARequestWithNoDaemonSaysDownAndWasNeverSent()
    {
        var rig = new ClientRig();
        try
        {
            var reply = await rig.Client.RequestAsync(new DaemonRequest("GET", "/v1/health", StartIfDown: false));
            Assert.Equal(0, reply.Status);
            Assert.Equal("down", reply.FallbackReason);
            Assert.False(reply.TimedOut);
            Assert.Null(reply.SentAt);
        }
        finally
        {
            await rig.StopAllAsync();
        }
    }

    private static DaemonClientDependencies DepsWith(ClientRig rig, IDetachedSpawner spawner, IProcessInspector inspector)
    {
        var io = PlatformModule.CreateIo(null);
        return new DaemonClientDependencies(
            io.Files,
            io.Writer,
            io.Appender,
            io.Locks,
            new UnixTransport(),
            spawner,
            rig.Runner,
            inspector,
            io.Clock,
            new TestEnv(rig.Home, rig.Vars, new PluginInfo("/private/tmp/ahprobe.plugin", "wrangl", "9.9.9", "1")),
            rig.Vars,
            "/private/tmp/ahprobe.bin/ahd");
    }
}
