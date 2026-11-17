using System.Globalization;
using System.Text;
using Ah.Abstractions;
using Ah.Platform;
using Ah.TestKit;

namespace Ah.DaemonClient.Tests;

/// <summary>
/// The heal's wiring over a throwaway state home and a scripted daemon: the probe, the identity, the verb's refusal and single
/// flight, the detached launch and the doctor row. Nothing here reaches a real daemon or signals a real process.
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class AhdHealWiringTests : IDisposable
{
    private const int DaemonPid = 4242;
    private const string StartedAt = "2020-01-01T00:00:00.000Z";

    private readonly string _home = Directory.CreateTempSubdirectory("ah-heal-").FullName;
    private readonly PlatformIo _io = PlatformModule.CreateIo(null);
    private readonly Rig _rig = new();

    public void Dispose() => Directory.Delete(_home, true);

    /// <summary>The scripted world: what the socket answers, which pids live, what <c>ps</c> prints, and every signal and spawn asked for.</summary>
    private sealed class Rig : IUnixHttpTransport, IProcessRunner, IProcessInspector, IDetachedSpawner
    {
        internal Func<HttpReply> Answer { get; set; } = static () => throw new TimeoutException("no answer");

        internal bool Alive { get; set; } = true;

        internal string? CommandLineOfDaemon { get; set; }

        internal List<string> Signals { get; } = [];

        internal List<IReadOnlyList<string>> Spawns { get; } = [];

        public Task<HttpReply> SendAsync(
            string socketPath, string method, string pathAndQuery, ReadOnlyMemory<byte> body, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            try
            {
                return Task.FromResult(Answer());
            }
            catch (Exception e) when (e is TimeoutException or IOException)
            {
                return Task.FromException<HttpReply>(e);
            }
        }

        public ProcessResult Run(ProcessRequest request)
        {
            if (request.Argv[0] == "kill")
            {
                Signals.Add(string.Join(' ', request.Argv));
                return new ProcessResult(0, null, false, false, default, default);
            }

            // ps -p <pid> -o command=
            var text = CommandLineOfDaemon is { } line ? line + "\n" : string.Empty;
            return new ProcessResult(text.Length > 0 ? 0 : 1, null, false, false, Encoding.UTF8.GetBytes(text), default);
        }

        public bool PidAlive(int pid) => Alive && pid == DaemonPid;

        public bool PidOwned(int pid) => PidAlive(pid);

        public string? CommandLine(int pid) => CommandLineOfDaemon;

        public int SpawnDetached(DetachedSpawnRequest request)
        {
            Spawns.Add(request.Argv);
            return 7777;
        }
    }

    private DaemonClientDependencies Deps(bool? callerIsUser = false, Func<int, string?>? startTime = null, bool daemonOff = false) =>
        new(_io.Files, _io.Writer, _io.Appender, _io.Locks, _rig, _rig, _rig, _rig, _io.Clock,
            new FakeEnvironment(new Dictionary<string, string> { ["AH_HOME"] = _home }),
            daemonOff ? new Dictionary<string, string?> { ["AH_DAEMON"] = "off" } : new Dictionary<string, string?>(), "/x/ahd")
        {
            CallerIsUser = () => callerIsUser,
            StartTime = startTime,
            Signal = (pid, signal) =>
            {
                _rig.Signals.Add($"signal {signal.ToString(CultureInfo.InvariantCulture)} to {pid.ToString(CultureInfo.InvariantCulture)}");
                return true;
            },
        };

    private void RecordDaemon(string startedAt = StartedAt)
    {
        Directory.CreateDirectory(Path.Combine(_home, "run"));
        File.WriteAllText(
            Path.Combine(_home, "run", "ahd.json"),
            $"{{\"pid\":{DaemonPid.ToString(CultureInfo.InvariantCulture)},\"started_at\":\"{startedAt}\",\"socket\":\"{_home}/run/ahd.sock\"}}");
        _rig.CommandLineOfDaemon = $"/x/ahd serve --ah-home {_home}";
    }

    private static HttpReply Reply(int status, string body) => new(status, Encoding.UTF8.GetBytes(body));

    private static AhdHealEnvironment Environment(DaemonClientDependencies deps)
    {
        var client = new AhdClient(deps);
        return new AhdHealEnvironment(client, deps, client.Paths, static _ => { });
    }

    [Theory]
    [InlineData(200, "{\"pid\":1}")]
    [InlineData(503, "{\"error\":\"draining\"}")]
    [InlineData(500, "not json at all")]
    public async Task AnyCompleteReplyIsAnAnswerWhateverItsStatusOrBody(int status, string body)
    {
        _rig.Answer = () => Reply(status, body);

        Assert.True((await Environment(Deps()).ProbeAsync(TimeSpan.FromSeconds(10))).Replied);
    }

    [Fact]
    public async Task ATimeoutOrARefusedConnectionIsAMiss()
    {
        _rig.Answer = static () => throw new TimeoutException("no answer");
        var timeout = await Environment(Deps()).ProbeAsync(TimeSpan.FromSeconds(10));
        _rig.Answer = static () => throw new IOException("refused");
        var refused = await Environment(Deps()).ProbeAsync(TimeSpan.FromSeconds(10));

        Assert.False(timeout.Replied);
        Assert.False(refused.Replied);
    }

    [Fact]
    public void TheIdentityHoldsOnlyForTheRecordedLivingDaemonOfThisHomeWithTheSameStart()
    {
        RecordDaemon();
        var start = "Mon Jan  1 00:00:00 2030";
        var environment = Environment(Deps(startTime: _ => start));
        var target = environment.ReadTarget()!;

        Assert.Equal(DaemonPid, target.Pid);
        Assert.True(environment.IdentityHolds(target));

        // negative: the pid now runs something else, ps cannot say, the record names another start, the pid was reused, it died
        _rig.CommandLineOfDaemon = "/usr/bin/vim notes.txt";
        Assert.False(environment.IdentityHolds(target));
        _rig.CommandLineOfDaemon = null;
        Assert.False(environment.IdentityHolds(target));
        RecordDaemon();
        Assert.False(environment.IdentityHolds(target with { StartedAt = "2019-12-31T00:00:00.000Z" }));
        start = "Tue Jan  2 09:00:00 2030";
        Assert.False(environment.IdentityHolds(target));
        start = "Mon Jan  1 00:00:00 2030";
        _rig.Alive = false;
        Assert.False(environment.IdentityHolds(target));
    }

    [Fact]
    public void ASignalIsOnePlatformCallForThatPidNeverForPidOneOrLessAndTheLogCopyIsTheTail()
    {
        RecordDaemon();
        Directory.CreateDirectory(Path.Combine(_home, "logs"));
        File.WriteAllText(Path.Combine(_home, "logs", "ahd.log"), "line one\nline two\n");
        var environment = Environment(Deps());

        environment.Signal(DaemonPid, kill: false);
        environment.Signal(DaemonPid, kill: true);
        environment.Signal(1, kill: true);
        environment.Signal(0, kill: true);
        environment.Signal(-1, kill: true);
        var copy = environment.CopyLog();

        Assert.Equal(["signal 15 to 4242", "signal 9 to 4242"], _rig.Signals);
        Assert.StartsWith(Path.Combine(_home, "logs", "ahd-hung-"), copy, StringComparison.Ordinal);
        Assert.Equal("line one\nline two\n", File.ReadAllText(copy));
    }

    [Fact]
    public async Task ForceFromACallerThatIsNotTheUserIsRefusedBeforeAnythingRuns()
    {
        RecordDaemon();
        var lifecycle = new AhdLifecycle(new AhdClient(Deps(callerIsUser: false)));
        var stderr = new StringWriter();

        var code = await lifecycle.RunAsync("heal", ["--force"], new StringWriter(), stderr, TestContext.Current.CancellationToken);

        Assert.Equal(1, code);
        Assert.Contains("only the user", stderr.ToString(), StringComparison.Ordinal);
        Assert.Empty(_rig.Signals);
        Assert.False(File.Exists(Path.Combine(_home, "run", "heal.lock")));
    }

    [Fact]
    public async Task ForceIsRefusedWhenNobodyCanTellWhoIsAsking()
    {
        RecordDaemon();
        var lifecycle = new AhdLifecycle(new AhdClient(Deps(callerIsUser: null)));
        var stderr = new StringWriter();

        var code = await lifecycle.RunAsync("heal", ["--force"], new StringWriter(), stderr, TestContext.Current.CancellationToken);

        Assert.Equal(1, code);
        Assert.Contains("could not tell who is asking", stderr.ToString(), StringComparison.Ordinal);
        Assert.Empty(_rig.Signals);
        Assert.False(File.Exists(Path.Combine(_home, "run", "heal.lock")));
    }

    [Fact]
    public void AfterTermGoneAndSameProcessAreJudgedByThePidAndItsStartTimeNotByTheRecord()
    {
        RecordDaemon();
        string? start = "Mon Jan  1 00:00:00 2030";
        var environment = Environment(Deps(startTime: _ => start));
        var target = environment.ReadTarget()!;

        // the daemon dropped its record while handling TERM and is still running: not gone, and still the process to kill
        File.Delete(Path.Combine(_home, "run", "ahd.json"));
        Assert.False(environment.IdentityHolds(target));
        Assert.False(environment.Gone(target));
        Assert.True(environment.SameProcess(target));

        // the process table does not answer: doubt, so neither gone nor confirmed
        start = null;
        Assert.False(environment.Gone(target));
        Assert.False(environment.SameProcess(target));

        // the pid now shows another start time: a living pid is never gone (a start time from ps moves when the clock steps), and
        // it is not the process to kill either
        start = "Tue Jan  2 09:00:00 2030";
        Assert.False(environment.Gone(target));
        Assert.False(environment.SameProcess(target));

        // the pid is dead: gone
        start = "Mon Jan  1 00:00:00 2030";
        _rig.Alive = false;
        Assert.True(environment.Gone(target));
        Assert.False(environment.SameProcess(target));
    }

    [Fact]
    public void WithNoStartTimeFromTheProcessTableTheSameProcessIsWhatTheFullIdentityCheckSays()
    {
        RecordDaemon();
        var environment = Environment(Deps(startTime: static _ => null));
        var target = environment.ReadTarget()!;
        Assert.Null(target.ProcStart);

        // no start time to compare: the record, the pid and the command line decide, as before any signal
        Assert.True(environment.IdentityHolds(target));
        Assert.True(environment.SameProcess(target));

        // the record is gone: with nothing else to tell the process by, it is not confirmed, so no KILL
        File.Delete(Path.Combine(_home, "run", "ahd.json"));
        Assert.False(environment.SameProcess(target));
        Assert.False(environment.Gone(target));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"restarts\":\"none\"}")]
    [InlineData("{\"restarts\":[\"2030-01-01T00:00:00Z\",\"yesterday\"]}")]
    [InlineData("{\"restarts\":[1]}")]
    public void AStateFileOfTheWrongShapeIsUnreadableNotEmpty(string text)
    {
        var path = Path.Combine(_home, "heal-state-shape.json");
        File.WriteAllText(path, text);

        Assert.Null(HealStateFile.Read(_io.Files, path));
    }

    [Fact]
    public void ARecordedStartFarInThePastDoesNotShortenTheGraceTheProcessTablesLaterStartCounts()
    {
        RecordDaemon(startedAt: "2001-01-01T00:00:00.000Z");
        var environment = Environment(Deps(startTime: static _ => "Tue Jan  1 00:00:00 2030"));

        Assert.Equal(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero), environment.ReadTarget()!.StartedUtc);
    }

    [Fact]
    public async Task TheUserMayForceAndASlowDaemonIsStillNotSignalled()
    {
        RecordDaemon();
        _rig.Answer = static () => Reply(200, "{}");
        var lifecycle = new AhdLifecycle(new AhdClient(Deps(callerIsUser: true)));
        var stdout = new StringWriter();

        var code = await lifecycle.HealAsync(force: true, "manual", stdout, new StringWriter(), Fast);

        Assert.Equal(0, code);
        Assert.Contains("not hung (replied)", stdout.ToString(), StringComparison.Ordinal);
        Assert.Empty(_rig.Signals);
        Assert.False(File.Exists(Path.Combine(_home, "run", "heal.pid")));
    }

    [Fact]
    public async Task ASecondHealWhileOneHoldsTheLockExitsZeroAndDoesNothing()
    {
        RecordDaemon();
        var run = Path.Combine(_home, "run");
        using var held = _io.Locks.TryHold(Path.Combine(run, "heal.lock"));
        File.WriteAllText(Path.Combine(run, "heal.pid"), "31337\n");
        var lifecycle = new AhdLifecycle(new AhdClient(Deps()));
        var stdout = new StringWriter();

        var code = await lifecycle.HealAsync(force: false, "failsafe", stdout, new StringWriter(), Fast);

        Assert.NotNull(held);
        Assert.Equal(0, code);
        Assert.Equal("heal in progress (pid 31337)\n", stdout.ToString());
        Assert.Empty(_rig.Signals);
    }

    [Fact]
    public async Task NoDaemonIsStartedOverOneThatStillHoldsItsLifeLockWithNoRecord()
    {
        _rig.Alive = false;
        var warnings = new List<string>();
        var options = new EnsureOptions(BudgetMs: 400, Hook: true, Warn: (text, _) => warnings.Add(text));

        using (var life = _io.Locks.TryHold(Path.Combine(_home, "run", "ahd.life.lock")))
        {
            Assert.NotNull(life);
            Assert.Null(await new AhdClient(Deps() with { DrainWaitMs = 300 }).EnsureAsync(options with { Hook = false }, TestContext.Current.CancellationToken));
            Assert.Empty(_rig.Spawns);

            // the last warning is the one a caller like `ahd start` keeps
            Assert.Contains("holds run/ahd.life.lock", warnings[^1], StringComparison.Ordinal);
        }

        // the kernel drops it when the old daemon exits: the next ensure starts one
        _ = await new AhdClient(Deps()).EnsureAsync(options, TestContext.Current.CancellationToken);
        Assert.Single(_rig.Spawns);
    }

    [Fact]
    public async Task ARequestThatMayNotStartTheDaemonDoesNotWaitForADrain()
    {
        RecordDaemon();
        _rig.Answer = static () => Reply(503, "{\"error\":{\"code\":\"draining\"}}");
        var warnings = new List<string>();
        var clock = System.Diagnostics.Stopwatch.StartNew();

        // the pid never exits, so a wait would take the whole default DrainWaitMs (12 s)
        var found = await new AhdClient(Deps()).EnsureAsync(
            new EnsureOptions(Start: false, Warn: (text, _) => warnings.Add(text)), TestContext.Current.CancellationToken);

        Assert.Null(found);
        Assert.True(clock.ElapsedMilliseconds < 1000, $"took {clock.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture)} ms");
        Assert.Equal("the daemon is still draining", warnings[^1]);
        Assert.Empty(_rig.Spawns);
    }

    [Fact]
    public async Task ARequestThatMayStartTheDaemonStillWaitsForADrain()
    {
        RecordDaemon();
        _rig.Answer = static () => Reply(503, "{\"error\":{\"code\":\"draining\"}}");
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var found = await new AhdClient(Deps() with { DrainWaitMs = 300 }).EnsureAsync(
            new EnsureOptions(Start: true), TestContext.Current.CancellationToken);

        Assert.Null(found);
        Assert.True(clock.ElapsedMilliseconds >= 250, $"took {clock.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture)} ms");
        Assert.Empty(_rig.Spawns);
    }

    [Fact]
    public void AHealIsLaunchedOnlyForARecordedLivingDaemonThatDoesNotAnswer()
    {
        var client = new AhdClient(Deps());

        // no daemon recorded: nothing to judge
        Assert.Null(client.LaunchHeal("direct-write"));

        RecordDaemon();
        // it answers discovery: not the heal's business
        _rig.Answer = () => Reply(200, $"{{\"pid\":{DaemonPid.ToString(CultureInfo.InvariantCulture)}}}");
        Assert.Null(client.LaunchHeal("direct-write"));

        // recorded, alive and silent: one detached heal, named with its trigger
        _rig.Answer = static () => throw new TimeoutException("no answer");
        Assert.Equal(7777, client.LaunchHeal("direct-write"));
        Assert.Equal(["/x/ahd", "heal", "--trigger", "direct-write"], Assert.Single(_rig.Spawns));

        // AH_DAEMON=off probes nothing, so there is no failed probe to act on
        Assert.Null(new AhdClient(Deps(daemonOff: true)).LaunchHeal("direct-write"));
        _ = Assert.Single(_rig.Spawns);

        // recorded but dead: starting a dead daemon is the normal client path's job
        _rig.Alive = false;
        Assert.Null(client.LaunchHeal("failsafe"));
        _ = Assert.Single(_rig.Spawns);
    }

    [Fact]
    public void TheDoctorRowReadsTheStateFileAndStartsNothing()
    {
        var now = new DateTimeOffset(2030, 1, 2, 0, 0, 0, TimeSpan.Zero);
        var run = Path.Combine(_home, "run");
        Directory.CreateDirectory(run);
        (string Level, string Detail) Row() => DaemonClientModule.HealDoctorRow(_io.Files, _rig, _home, now);
        void State(HealState state) => File.WriteAllBytes(Path.Combine(run, "heal-state.json"), HealStateFile.Bytes(state));

        Assert.Equal("ok", Row().Level);
        Assert.Contains("ahd heal", Row().Detail, StringComparison.Ordinal);

        State(new HealState([now - TimeSpan.FromHours(2), now - TimeSpan.FromHours(30)], null, new HealLast("restarted", now - TimeSpan.FromHours(2), "ahd pid 1 replaced by 2 (H1, TERM)", "/l/ahd-hung.log")));
        Assert.Equal("warn", Row().Level);
        Assert.Contains("1 time(s)", Row().Detail, StringComparison.Ordinal);

        State(new HealState([now - TimeSpan.FromHours(1)], null, new HealLast("failed-unkillable", now - TimeSpan.FromHours(1), "ahd pid 1 is still alive after KILL", "/l/ahd-hung.log")));
        Assert.Equal("error", Row().Level);
        Assert.Contains("/l/ahd-hung.log", Row().Detail, StringComparison.Ordinal);
        Assert.Contains("ahd heal --force", Row().Detail, StringComparison.Ordinal);

        // a failure older than a day is no longer reported; a file that is not JSON is an error that says heal sends no signal, not a crash
        State(new HealState([], null, new HealLast("capped", now - TimeSpan.FromHours(25), "old", string.Empty)));
        Assert.Equal("ok", Row().Level);
        File.WriteAllText(Path.Combine(run, "heal-state.json"), "not json");
        Assert.Equal("error", Row().Level);
        Assert.Contains("sends no signal", Row().Detail, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(run, "heal.lock")));
        Assert.Empty(_rig.Spawns);
    }

    /// <summary>The production rules with millisecond times, so a verb-level run takes no real time.</summary>
    private static HealTiming Fast => HealTiming.Production with
    {
        Grace = TimeSpan.Zero,
        ProbeDeadline = TimeSpan.FromMilliseconds(5),
        ProbeEvery = TimeSpan.FromMilliseconds(1),
        Window = TimeSpan.FromMilliseconds(1),
        TermWait = TimeSpan.FromMilliseconds(5),
        KillWait = TimeSpan.FromMilliseconds(5),
        StartVerify = TimeSpan.FromMilliseconds(5),
        Poll = TimeSpan.FromMilliseconds(1),
    };
}
