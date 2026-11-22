using Ah.Core.Json;

namespace Ah.DaemonClient.Tests;

/// <summary>The heal verdict and restart (crash-resume §14.11, handoff spec ahd-heal), driven on a virtual clock against a scripted daemon: no real waits, no real signals.</summary>
public sealed class AhdHealTests
{
    private static readonly DateTimeOffset T0 = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private sealed class Fake : IHealEnvironment
    {
        private DateTimeOffset _now = T0;
        private readonly List<DateTimeOffset> _signalTimes = [];

        internal List<DateTimeOffset> SignalTimes => _signalTimes;

        internal Func<DateTimeOffset, HealProbe> Script { get; set; } = _ => HealProbe.Miss(10_000);

        internal Func<DateTimeOffset, bool> OursAt { get; set; } = _ => true;

        internal Func<DateTimeOffset, bool> AliveAt { get; set; } = _ => true;

        internal HealTarget? Target { get; set; }

        internal bool IgnoreTerm { get; set; }

        internal bool IgnoreKill { get; set; }

        internal bool StartSucceeds { get; set; } = true;

        internal int StartPid { get; set; } = 9999;

        internal HealState State { get; set; } = HealState.Empty;

        internal List<(int Pid, bool Kill)> Signals { get; } = [];

        internal List<JsonObject> Rows { get; } = [];

        internal List<string> Lines { get; } = [];

        internal List<DateTimeOffset> ProbeStarts { get; } = [];

        internal int Starts { get; private set; }

        internal DateTimeOffset? Died { get; set; }

        internal bool Restarted { get; private set; }

        /// <summary>The wall clock: the monotonic one plus whatever step a test gives it.</summary>
        public DateTimeOffset Now => _now + WallStep(_now);

        internal Func<DateTimeOffset, TimeSpan> WallStep { get; set; } = _ => TimeSpan.Zero;

        public TimeSpan Elapsed => _now - T0;

        /// <summary>After TERM the daemon drops its record and stays alive (it handled TERM and never exited).</summary>
        internal bool RecordGoneAfterTerm { get; set; }

        /// <summary>After TERM the process table stops answering: nothing about the pid is certain but that it is alive.</summary>
        internal bool ProcessTableFailsAfterTerm { get; set; }

        /// <summary>After TERM the pid belongs to another process (another start time): alive, so not gone, and not the same process.</summary>
        internal bool ReplacedAfterTerm { get; set; }

        private bool TermSent => Signals.Count > 0;

        public bool Gone(HealTarget target) => !Alive(target.Pid);

        public string Home => "/fake/home";

        public bool LifeHeld() => false;

        public bool SameProcess(HealTarget target) => Alive(target.Pid) && !(TermSent && (ProcessTableFailsAfterTerm || ReplacedAfterTerm));

        public Task DelayAsync(TimeSpan span)
        {
            _now += span;
            return Task.CompletedTask;
        }

        public HealTarget? ReadTarget() => Target;

        internal int IdentityCalls { get; private set; }

        internal int? IdentityLostFromCall { get; set; }

        internal List<int> IdentityCallsAtSignal { get; } = [];

        public bool IdentityHolds(HealTarget target)
        {
            IdentityCalls++;
            return Alive(target.Pid) && OursAt(_now) && (IdentityLostFromCall is not { } from || IdentityCalls < from)
                && !(TermSent && (RecordGoneAfterTerm || ProcessTableFailsAfterTerm || ReplacedAfterTerm));
        }

        public bool Alive(int pid) => pid == Target?.Pid && Died is null && AliveAt(_now);

        public Task<HealProbe> ProbeAsync(TimeSpan deadline)
        {
            ProbeStarts.Add(_now);
            if (Restarted)
            {
                _now += TimeSpan.FromMilliseconds(5);
                return Task.FromResult(new HealProbe(true, 5));
            }

            var probe = Script(_now);
            // a miss takes its own latency: the whole deadline for a silent daemon, no time at all for a refused connect
            _now += TimeSpan.FromMilliseconds(Math.Min(probe.LatencyMs, (long)deadline.TotalMilliseconds));
            return Task.FromResult(probe);
        }

        public void Signal(int pid, bool kill)
        {
            Signals.Add((pid, kill));
            IdentityCallsAtSignal.Add(IdentityCalls);
            _signalTimes.Add(_now);
            if ((!kill && !IgnoreTerm) || (kill && !IgnoreKill))
            {
                Died = _now;
            }
        }

        internal List<int> IdentityCallsAtCopy { get; } = [];

        internal List<int> IdentityCallsAtWrite { get; } = [];

        public string CopyLog()
        {
            IdentityCallsAtCopy.Add(IdentityCalls);
            return "/logs/ahd-hung.log";
        }

        public Task<int?> StartAsync()
        {
            Starts++;
            Restarted = StartSucceeds;
            return Task.FromResult<int?>(StartSucceeds ? StartPid : null);
        }

        internal bool StateUnreadable { get; set; }

        public HealState? ReadState() => StateUnreadable ? null : State;

        public void WriteState(HealState state)
        {
            IdentityCallsAtWrite.Add(IdentityCalls);
            State = state;
            StateUnreadable = false;
        }

        public void Journal(JsonObject row) => Rows.Add(row);

        public void Progress(string line) => Lines.Add(line);

        internal static Fake HungSince(TimeSpan uptimeAtStart) =>
            new() { Target = new HealTarget(4242, T0.ToString("O"), T0 - uptimeAtStart) };
    }

    private static HealTiming Production => HealTiming.Production;

    private static string Kind(Fake fake) => fake.Rows[^1]["kind"].AsString();

    [Fact]
    public async Task ASlowButAnsweringDaemonIsNeverSignalledHoweverOftenItIsProbed()
    {
        var fake = Fake.HungSince(TimeSpan.FromMinutes(10));
        fake.Script = _ => new HealProbe(true, 8000);

        var outcome = await new AhdHeal(fake, Production).RunAsync(HealTrigger.Manual, force: false);

        Assert.Equal("not-hung", outcome.Kind);
        Assert.Equal("replied", outcome.Detail);
        Assert.Empty(fake.Signals);
        Assert.Equal(8000d, fake.Rows[^1]["slowest_reply_ms"].AsNumber());
    }

    [Fact]
    public async Task APauseThatEndsInAReplyBeforeTheWindowClosesIsNotAHang()
    {
        var fake = Fake.HungSince(TimeSpan.FromMinutes(10));
        var probes = 0;
        fake.Script = _ => ++probes % 3 == 0 ? new HealProbe(true, 9000) : HealProbe.Miss(10_000);

        var outcome = await new AhdHeal(fake, Production).RunAsync(HealTrigger.Manual, force: false);

        Assert.Equal("not-hung", outcome.Kind);
        Assert.Empty(fake.Signals);
    }

    [Fact]
    public async Task NoProbeCountsBeforeThirtySecondsOfUptimeAndNothingIsSignalledBeforeNinety()
    {
        var fake = Fake.HungSince(TimeSpan.FromSeconds(5));

        var outcome = await new AhdHeal(fake, Production).RunAsync(HealTrigger.Manual, force: false);

        Assert.Equal("restarted", outcome.Kind);
        Assert.All(fake.ProbeStarts.Take(1), start => Assert.True(start - (T0 - TimeSpan.FromSeconds(5)) >= TimeSpan.FromSeconds(30)));
        var firstSignal = fake.Signals.Count;
        Assert.Equal(1, firstSignal);
        Assert.True(fake.ProbeStarts.Count >= 5);
        Assert.True(fake.ProbeStarts[^2] - (T0 - TimeSpan.FromSeconds(5)) >= TimeSpan.FromSeconds(90));
    }

    [Fact]
    public async Task AStartRecordedInTheFutureWaitsTheWholeGraceOnceAndStillNeverSignalsBeforeNinetySeconds()
    {
        var fake = Fake.HungSince(-TimeSpan.FromDays(1));

        var outcome = await new AhdHeal(fake, Production).RunAsync(HealTrigger.Manual, force: false);

        Assert.Equal("restarted", outcome.Kind);
        Assert.True(fake.ProbeStarts[0] - T0 >= TimeSpan.FromSeconds(30));
        Assert.True(fake.ProbeStarts[^2] - T0 >= TimeSpan.FromSeconds(90));
    }

    [Fact]
    public async Task ASilentDaemonIsRestartedOnceAfterTheWindowWithTermAndTheEvidenceIsJournalled()
    {
        var fake = Fake.HungSince(TimeSpan.FromMinutes(10));

        var outcome = await new AhdHeal(fake, Production).RunAsync(HealTrigger.DirectWrite, force: false);

        Assert.Equal("restarted", outcome.Kind);
        Assert.Equal([(4242, false)], fake.Signals);
        Assert.Equal(1, fake.Starts);
        Assert.Equal("ahd-hung-restart", Kind(fake));
        var row = fake.Rows[^1];
        Assert.Equal("H1", row["verdict"].AsString());
        Assert.Equal("TERM", row["signal"].AsString());
        Assert.Equal("direct-write", row["trigger"].AsString());
        Assert.Equal(9999d, row["new_pid"].AsNumber());
        Assert.True(row["window_s"].AsNumber() >= 60);
        Assert.True(fake.ProbeStarts[0] >= T0);
        Assert.Single(fake.State.Restarts);
        Assert.Equal("restarted", fake.State.Last?.Kind);
        Assert.Equal("/logs/ahd-hung.log", fake.State.Last?.LogCopy);
    }

    [Fact]
    public async Task ADaemonThatIgnoresTermIsKilledTenSecondsLaterAndTheRowSaysKill()
    {
        var fake = Fake.HungSince(TimeSpan.FromMinutes(10));
        fake.IgnoreTerm = true;

        var outcome = await new AhdHeal(fake, Production).RunAsync(HealTrigger.Manual, force: false);

        Assert.Equal("restarted", outcome.Kind);
        Assert.Equal([(4242, false), (4242, true)], fake.Signals);
        Assert.Equal("KILL", fake.Rows[^1]["signal"].AsString());
    }

    [Fact]
    public async Task ADaemonThatSurvivesKillIsReportedUnkillableAndNothingIsStarted()
    {
        var fake = Fake.HungSince(TimeSpan.FromMinutes(10));
        fake.IgnoreTerm = true;
        fake.IgnoreKill = true;

        var outcome = await new AhdHeal(fake, Production).RunAsync(HealTrigger.Manual, force: false);

        Assert.Equal("failed-unkillable", outcome.Kind);
        Assert.Equal(0, fake.Starts);
        Assert.Equal("ahd-heal-failed", Kind(fake));
        Assert.Equal("unkillable", fake.Rows[^1]["stage"].AsString());
    }

    [Fact]
    public async Task AnotherPathReplacingTheDaemonMidWindowIsNeverSignalled()
    {
        var fake = Fake.HungSince(TimeSpan.FromMinutes(10));
        fake.OursAt = now => now < T0 + TimeSpan.FromSeconds(20);

        var outcome = await new AhdHeal(fake, Production).RunAsync(HealTrigger.Manual, force: false);

        Assert.Equal("not-hung", outcome.Kind);
        Assert.Equal("identity-changed", outcome.Detail);
        Assert.Empty(fake.Signals);
    }

    [Fact]
    public async Task APidThatNowBelongsToAnotherProcessIsNeverSignalled()
    {
        var fake = Fake.HungSince(TimeSpan.FromMinutes(10));
        fake.OursAt = _ => false;

        var outcome = await new AhdHeal(fake, Production).RunAsync(HealTrigger.Manual, force: false);

        Assert.Equal("not-hung", outcome.Kind);
        Assert.Empty(fake.Signals);
        Assert.Equal(0, fake.Starts);
    }

    [Fact]
    public async Task ADaemonThatExitsByItselfMidWindowGetsNoSignalAndNoStart()
    {
        var fake = Fake.HungSince(TimeSpan.FromMinutes(10));
        fake.AliveAt = now => now < T0 + TimeSpan.FromSeconds(30);

        var outcome = await new AhdHeal(fake, Production).RunAsync(HealTrigger.Manual, force: false);

        Assert.Equal("not-hung", outcome.Kind);
        Assert.Equal("exited", outcome.Detail);
        Assert.Empty(fake.Signals);
        Assert.Equal(0, fake.Starts);
    }

    [Fact]
    public async Task ADaemonOverTheThreeRestartCapIsNotSignalledAndTheCappedRowIsWrittenOncePerHour()
    {
        var fake = Fake.HungSince(TimeSpan.FromMinutes(10));
        fake.State = new HealState([T0 - TimeSpan.FromHours(1), T0 - TimeSpan.FromHours(2), T0 - TimeSpan.FromHours(3)], null);

        var capped = await new AhdHeal(fake, Production).RunAsync(HealTrigger.Manual, force: false);
        var again = await new AhdHeal(fake, Production).RunAsync(HealTrigger.Manual, force: false);

        Assert.Equal("capped", capped.Kind);
        Assert.Equal("capped", again.Kind);
        Assert.Empty(fake.Signals);
        Assert.Equal(0, fake.Starts);
        Assert.Single(fake.Rows, row => row["kind"].AsString() == "ahd-heal-capped");
    }

    [Fact]
    public async Task TheBackoffHoldsARestartBackUntilTenMinutesHavePassedAndForceSkipsOnlyTheBackoff()
    {
        var fake = Fake.HungSince(TimeSpan.FromMinutes(10));
        fake.State = new HealState([T0 - TimeSpan.FromMinutes(2)], null);

        var held = await new AhdHeal(fake, Production).RunAsync(HealTrigger.Manual, force: false);
        fake.Died = null;
        fake.Target = new HealTarget(4242, "s", fake.Now - TimeSpan.FromMinutes(10));
        var forced = await new AhdHeal(fake, Production).RunAsync(HealTrigger.Manual, force: true);

        Assert.Equal("capped", held.Kind);
        Assert.Equal("restarted", forced.Kind);
    }

    [Fact]
    public async Task ForceNeverSkipsTheVerdictASlowDaemonStaysUnsignalled()
    {
        var fake = Fake.HungSince(TimeSpan.FromMinutes(10));
        fake.Script = _ => new HealProbe(true, 8000);

        var outcome = await new AhdHeal(fake, Production).RunAsync(HealTrigger.Manual, force: true);

        Assert.Equal("not-hung", outcome.Kind);
        Assert.Empty(fake.Signals);
    }

    [Fact]
    public async Task ARestartedDaemonThatDoesNotAnswerIsRecordedAsFailedAndCountsTowardTheCap()
    {
        var fake = Fake.HungSince(TimeSpan.FromMinutes(10));
        fake.StartSucceeds = false;

        var outcome = await new AhdHeal(fake, Production).RunAsync(HealTrigger.Manual, force: false);

        Assert.Equal("failed-restart", outcome.Kind);
        Assert.Equal("failed-restart", fake.State.Last?.Kind);
        Assert.Single(fake.State.Restarts);
        Assert.Equal("restart", fake.Rows[^1]["stage"].AsString());
    }

    [Fact]
    public async Task AReplyOnTheFinalCheckMeansNotHungAndNothingIsSignalled()
    {
        var fake = Fake.HungSince(TimeSpan.FromMinutes(10));
        var calls = 0;
        fake.Script = _ => ++calls <= 7 ? HealProbe.Miss(10_000) : new HealProbe(true, 100); // seven misses span the 90 s the run must watch; the eighth is the final check

        var outcome = await new AhdHeal(fake, Production).RunAsync(HealTrigger.Manual, force: false);

        Assert.Equal("not-hung", outcome.Kind);
        Assert.Empty(fake.Signals);
    }

    [Fact]
    public async Task ADaemonWhoseIdentityIsLostInTheGapBeforeTermIsNeverSignalledAndTheRestartIsNotCharged()
    {
        var baseline = Fake.HungSince(TimeSpan.FromMinutes(10));
        _ = await new AhdHeal(baseline, Production).RunAsync(HealTrigger.Manual, force: false);
        var lastCheckBeforeTerm = baseline.IdentityCallsAtSignal[0];

        var fake = Fake.HungSince(TimeSpan.FromMinutes(10));
        fake.IdentityLostFromCall = lastCheckBeforeTerm;

        var outcome = await new AhdHeal(fake, Production).RunAsync(HealTrigger.Manual, force: false);

        Assert.Equal("not-hung", outcome.Kind);
        Assert.Empty(fake.Signals);
        Assert.Equal(0, fake.Starts);
        Assert.Empty(fake.State.Restarts);
    }

    [Fact]
    public async Task APidThatBecameAnotherProcessAfterTermIsNeitherGoneNorKilledAndNothingIsStarted()
    {
        var fake = Fake.HungSince(TimeSpan.FromMinutes(10));
        fake.IgnoreTerm = true;
        fake.ReplacedAfterTerm = true;

        var outcome = await new AhdHeal(fake, Production).RunAsync(HealTrigger.Manual, force: false);

        // a living pid is never "gone", whatever start time it shows: no KILL to a process that is not ours, and no daemon started
        Assert.Equal([(4242, false)], fake.Signals);
        Assert.Equal("failed-not-killed", outcome.Kind);
        Assert.Equal(0, fake.Starts);
    }

    [Fact]
    public async Task NothingSitsBetweenTheLastCheckAndKill()
    {
        var fake = Fake.HungSince(TimeSpan.FromMinutes(10));
        fake.IgnoreTerm = true;

        _ = await new AhdHeal(fake, Production).RunAsync(HealTrigger.Manual, force: false);

        Assert.Equal("ahd ignored TERM", fake.Lines[^1]); // the last line written precedes the check; none follows it before the signal
    }

    [Fact]
    public async Task AYoungDaemonWhoseRecordSaysOldIsStillWatchedNinetySecondsBeforeAnySignal()
    {
        // a forward clock step right after the start: every recorded time says the daemon is old, and refused connects take no time
        var fake = Fake.HungSince(TimeSpan.FromMinutes(5));
        fake.Script = _ => HealProbe.Miss(0);

        var outcome = await new AhdHeal(fake, Production).RunAsync(HealTrigger.Manual, force: false);

        Assert.Equal("restarted", outcome.Kind);
        Assert.True(fake.SignalTimes[0] - T0 >= TimeSpan.FromSeconds(90));
    }

    [Fact]
    public async Task ADaemonThatDroppedItsRecordOnTermAndNeverExitedIsStillKilledBeforeAnythingIsStarted()
    {
        var fake = Fake.HungSince(TimeSpan.FromMinutes(10));
        fake.IgnoreTerm = true;
        fake.RecordGoneAfterTerm = true;

        var outcome = await new AhdHeal(fake, Production).RunAsync(HealTrigger.Manual, force: false);

        // the record no longer names it, but the pid is alive with the start time it had: it is not gone, and KILL goes to it
        Assert.Equal([(4242, false), (4242, true)], fake.Signals);
        Assert.Equal("restarted", outcome.Kind);
        Assert.Equal("KILL", fake.Rows[^1]["signal"].AsString());
        Assert.Equal(1, fake.Starts);
    }

    [Fact]
    public async Task NegativeAProcessTableThatStopsAnsweringAfterTermMeansNoKillNoStartAndNoSuccess()
    {
        var fake = Fake.HungSince(TimeSpan.FromMinutes(10));
        fake.IgnoreTerm = true;
        fake.ProcessTableFailsAfterTerm = true;

        var outcome = await new AhdHeal(fake, Production).RunAsync(HealTrigger.Manual, force: false);

        // doubt is not "gone": the whole TERM wait passes, then nothing is sent to a pid that cannot be confirmed
        Assert.Equal([(4242, false)], fake.Signals);
        Assert.True(fake.Elapsed >= TimeSpan.FromSeconds(60 + 10 + 10));
        Assert.Equal("failed-not-killed", outcome.Kind);
        Assert.Equal(0, fake.Starts);
        Assert.Equal("not-killed", fake.Rows[^1]["stage"].AsString());
        Assert.Equal("failed-not-killed", fake.State.Last?.Kind);
        Assert.Single(fake.State.Restarts);
    }

    [Fact]
    public async Task AWallClockThatJumpsForwardCannotShortenTheWindowOrTheProbeSpacing()
    {
        var fake = Fake.HungSince(TimeSpan.FromMinutes(10));
        fake.Script = _ => HealProbe.Miss(0); // a refused connect: a miss that takes no time
        fake.WallStep = now => now > T0 ? TimeSpan.FromHours(1) : TimeSpan.Zero;

        var outcome = await new AhdHeal(fake, Production).RunAsync(HealTrigger.Manual, force: false);

        Assert.Equal("restarted", outcome.Kind);
        for (var i = 1; i < 4; i++)
        {
            Assert.True(fake.ProbeStarts[i] - fake.ProbeStarts[i - 1] >= TimeSpan.FromSeconds(15));
        }

        Assert.True(fake.SignalTimes[0] - fake.ProbeStarts[0] >= TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task AnUnreadableStateFileCountsAsCappedAndOnlyForceStartsAFreshOne()
    {
        var fake = Fake.HungSince(TimeSpan.FromMinutes(10));
        fake.StateUnreadable = true;

        var capped = await new AhdHeal(fake, Production).RunAsync(HealTrigger.Manual, force: false);

        Assert.Equal("capped", capped.Kind);
        Assert.Empty(fake.Signals);
        Assert.True(fake.StateUnreadable); // the file is left as it was found
        Assert.Equal("state-unreadable", fake.Rows[^1]["reason"].AsString());

        var forced = await new AhdHeal(fake, Production).RunAsync(HealTrigger.Manual, force: true);

        Assert.Equal("restarted", forced.Kind);
        Assert.Single(fake.State.Restarts);
    }

    [Fact]
    public async Task AnAnsweringDaemonClearsAnEarlierFailureSoTheDoctorRowStopsReportingIt()
    {
        var fake = Fake.HungSince(TimeSpan.FromMinutes(10));
        fake.State = new HealState([], null, new HealLast("capped", T0 - TimeSpan.FromMinutes(5), "capped earlier", string.Empty));
        fake.Script = _ => new HealProbe(true, 40);

        _ = await new AhdHeal(fake, Production).RunAsync(HealTrigger.Manual, force: false);

        Assert.Null(fake.State.Last);
    }
}
