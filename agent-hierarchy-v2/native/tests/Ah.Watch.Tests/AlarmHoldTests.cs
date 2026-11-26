using System.Globalization;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Hierarchy;
using Ah.TestKit;

namespace Ah.Watch.Tests;

/// <summary>ah2-639: dispatch and job alarms hold while the member's own job process provably runs.</summary>
public sealed class AlarmHoldTests
{
    private const string RequestId = "20260301-114000-ab12";

    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-03-01T12:00:00Z", CultureInfo.InvariantCulture);

    private static string Iso(DateTimeOffset at) => at.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static JobFact Job(string session = "m1", bool alive = true, JobSlot? slot = null, double sentMs = 0) =>
        new("20260301-120000-jjjj", "beta", "impl", session, "test", "dotnet test", 4321, "medium", sentMs == 0 ? Start.ToUnixTimeMilliseconds() : sentMs, null, null, false, false, [], "start", Slot: slot, Alive: alive);

    private sealed class Rig
    {
        internal FakeHost Host { get; } = new() { MemberPicksUpResumes = false };

        internal FakeClock Clock { get; } = new(Start);

        internal List<JobFact> Jobs { get; } = [];

        internal Rig(DateTimeOffset created)
        {
            Host.Registrations.Add(new WatchRegistration("s1", "claude", 100, "2026-03-01T11:00:00.000Z", [new WatchBox("orchestrator@beta", "/boxes/orchestrator@beta")], "/private/tmp/ahprobe.h"));
            MemberView[] members = [new("impl", new JsonObject { ["role"] = "implementor" }, true, "idle", null, null, "m1")];
            Host.Owned["s1"] = [new OwnedTeam("beta", new JsonObject(), members)];
            Host.Candidates["s1"] = [new DispatchCandidate(RequestId, "implementor", "the-slug", "impl@beta", "impl", "small", Iso(created), true, "/r.md", false, string.Empty, "/private/tmp/ahprobe.h") { Team = "beta" }];
            Host.Activity["m1"] = new JsonObject { ["activity"] = "idle", ["at"] = Iso(Start) };
            Host.FeedBackGates = true;
            Host.Stamp = () => Iso(Clock.UtcNow);
            Host.JobsOf = team => team == "beta" ? [.. Jobs] : [];
        }

        internal WatchTick Tick()
        {
            var files = new NullFiles();
            var env = new MapEnv();
            return new WatchTick(new WatchDependencies(Host, env, Clock, files, HierarchyModule.CreateRecovery(files, env, Clock), WatchModule.CreateStatusDirs(), () => true));
        }

        internal async Task PassAsync(WatchTick tick, int polls = 2, int stepMs = 15_000)
        {
            for (var i = 0; i < polls; i++)
            {
                await tick.RunAsync(CancellationToken.None);
                Clock.UtcNow = Clock.UtcNow.AddMilliseconds(stepMs);
            }
        }

        internal int Count(string name) => Host.Mails.Count(m => m.Event == name);
    }

    [Fact]
    public async Task A1ALiveJobHoldsCheckInAndSilentUntilItEndsThenEachFiresOnce()
    {
        var rig = new Rig(Start.AddMinutes(-20));
        rig.Jobs.Add(Job());
        var tick = rig.Tick();

        await rig.PassAsync(tick, polls: 4);
        Assert.Equal(0, rig.Count("check-in-due"));

        rig.Jobs[0] = Job() with { Ended = true };
        await rig.PassAsync(tick);
        Assert.Equal(1, rig.Count("check-in-due"));

        // the silent step is due five minutes after the check-in
        rig.Jobs[0] = Job();
        await rig.PassAsync(tick, polls: 3, stepMs: 180_000);
        Assert.Equal(0, rig.Count("peer-silent"));

        rig.Jobs[0] = Job() with { Ended = true };
        await rig.PassAsync(tick);
        Assert.Equal(1, rig.Count("peer-silent"));
        Assert.Equal(1, rig.Count("check-in-due"));
    }

    [Fact]
    public async Task NegativeADeadPidJobHoldsNoCheckIn()
    {
        var rig = new Rig(Start.AddMinutes(-20));
        rig.Jobs.Add(Job(alive: false));

        await rig.PassAsync(rig.Tick());

        Assert.Equal(1, rig.Count("check-in-due"));
    }

    [Fact]
    public async Task NegativeALiveJobOfAnotherSessionHoldsNoCheckIn()
    {
        var rig = new Rig(Start.AddMinutes(-20));
        rig.Jobs.Add(Job(session: "other"));

        await rig.PassAsync(rig.Tick());

        Assert.Equal(1, rig.Count("check-in-due"));
    }

    [Fact]
    public async Task NegativeALiveJobHoldsNothingPastTheBusyCeiling()
    {
        var rig = new Rig(Start.AddSeconds(-(HierarchyModule.BusyCeilingSeconds + 60)));
        rig.Jobs.Add(Job());

        await rig.PassAsync(rig.Tick());

        Assert.Equal(1, rig.Count("check-in-due"));
    }

    private static double Ms(double minutesAfterStart) => Start.AddMinutes(minutesAfterStart).ToUnixTimeMilliseconds();

    private static JobOutcome RunJobs(double nowMinutes, JobFact job) =>
        JobEvents.Evaluate("s1", Ms(nowMinutes), 30000, ["beta"], _ => [job], static _ => 600, new HashSet<string>(StringComparer.Ordinal), out _);

    [Fact]
    public void A2ALiveJobIsOverdueAtTwiceItsEtaPlusTheMarginAndADeadOneAtItsEtaPlusTheMargin()
    {
        Assert.Empty(RunJobs(12, Job()).Events);
        Assert.Empty(RunJobs(21.9, Job()).Events);
        var told = Assert.Single(RunJobs(22, Job()).Events);
        Assert.Contains("still running (pid 4321)", told.Text, StringComparison.Ordinal);

        var dead = Assert.Single(RunJobs(12, Job(alive: false)).Events);
        Assert.DoesNotContain("still running", dead.Text, StringComparison.Ordinal);
        Assert.Empty(RunJobs(11.9, Job(alive: false)).Events);
    }

    [Fact]
    public void TheFailsafeOwesALiveJobsOverdueStepAtTheSameLaterTime()
    {
        var item = Assert.Single(WatchModule.OverdueJobItems([Job()], static _ => 600, Ms(0), new MapEnv()), static i => i.Step == "overdue");
        Assert.Equal(Ms(22), item.AtMs);
    }

    [Fact]
    public void NegativeALiveQueuedJobKeepsTheOneHourGiveUp()
    {
        var queued = Job(slot: new JobSlot("queued", Ms(0), 0, "test", string.Empty));
        Assert.Empty(RunJobs(61, queued).Events);
        _ = Assert.Single(RunJobs(62, queued).Events);
    }

    private sealed class Table(string? start) : IProcessTable
    {
        public IReadOnlyList<int>? Ancestors(int pid) => null;

        public bool? HasAncestor(int pid, int candidate) => null;

        public string? StartTime(long pid) => start;
    }

    [Fact]
    public void AJobPidIsLiveOnlyWhenAliveWithItsRecordedStart()
    {
        Assert.True(WatchModule.JobPidLive(Job(), new Table("start"), static _ => true));
        Assert.False(WatchModule.JobPidLive(Job(), new Table("start"), static _ => false));
        Assert.False(WatchModule.JobPidLive(Job(), new Table("later"), static _ => true));
        Assert.False(WatchModule.JobPidLive(Job(), new Table(null), static _ => true));
        Assert.False(WatchModule.JobPidLive(Job() with { PidStart = null }, new Table("start"), static _ => true));
        Assert.False(WatchModule.JobPidLive(Job() with { Pid = null }, new Table("start"), static _ => true));
    }

    private static int IdleAlarms(OverdueTarget target)
    {
        var now = Start.ToUnixTimeMilliseconds();
        var brief = new DispatchCandidate(RequestId, "implementor", "the-slug", "impl@beta", "impl", "small", Iso(Start.AddMinutes(-2)), true, "/r.md", false, string.Empty, "/private/tmp/ahprobe.h") { Team = "beta" };
        var outcome = DispatchClock.Evaluate("s1", now, 30000, [brief], [], _ => [], static _ => 600, _ => target);
        return outcome.Events.Count(static e => e.Kind == "IDLE-WITHOUT-REPORT");
    }

    private static OverdueTarget Idle() => new("impl@beta", "m1", true, StateSinceMs: Start.AddMinutes(-5).ToUnixTimeMilliseconds());

    [Fact]
    public void A3AnIdleTargetHoldingTheSlotGetsNoIdleWithoutReport() =>
        Assert.Equal(0, IdleAlarms(Idle() with { Slot = new SlotState("test", false, Start.AddMinutes(-3).ToUnixTimeMilliseconds(), 0) }));

    [Fact]
    public void AnIdleTargetWaitingForTheSlotOrOnBackgroundWorkOrWithALiveJobGetsNoIdleWithoutReport()
    {
        Assert.Equal(0, IdleAlarms(Idle() with { Slot = new SlotState("test", true, Start.AddMinutes(-3).ToUnixTimeMilliseconds(), 0) }));
        Assert.Equal(0, IdleAlarms(Idle() with { BgWaiting = true }));
        Assert.Equal(0, IdleAlarms(Idle() with { LiveJob = true }));
    }

    [Fact]
    public void NegativeAnIdleTargetWithNoSlotNoBackgroundWorkAndNoLiveJobGetsIdleWithoutReport() =>
        Assert.Equal(1, IdleAlarms(Idle()));
}
