namespace Ah.Watch.Tests;

/// <summary>A job queued for the machine-wide test slot is open but not overdue; its clock starts when it acquires the slot.</summary>
public sealed class JobSlotTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    private static double Ms(double minutesAfterT0) => T0.AddMinutes(minutesAfterT0).ToUnixTimeMilliseconds();

    private static double Seconds(string eta) => eta switch { "large" => 1200, "medium" => 600, _ => 300 };

    private static JobFact Job(JobSlot? slot, string eta = "medium", double? capAt = null) =>
        new("20261022-120000-aaaa", "tm", "impl", "sess-impl", "test", "with-slot.sh dotnet test", 4321, eta, Ms(0), null, capAt is null ? null : Ms(capAt.Value), false, false, [], Slot: slot);

    private static JobSlot Queued(double sinceMinutes) => new("queued", Ms(sinceMinutes), 0, "test", "dotnet test x (pid 99)");

    private static JobSlot Running(double sinceMinutes, double waitedMinutes) => new("running", Ms(sinceMinutes), waitedMinutes * 60000, "test", string.Empty);

    private static JobOutcome Run(double nowMinutes, JobFact job) =>
        JobEvents.Evaluate("s1", Ms(nowMinutes), 30000, ["tm"], _ => [job], Seconds, new HashSet<string>(StringComparer.Ordinal), out _);

    [Fact]
    public void AQueuedJobIsQuietHoweverLongPastItsEtaItWasSentAndNegativeTheSameJobWithNoSlotRecordAlarms()
    {
        Assert.Empty(Run(15, Job(Queued(0))).Events);
        Assert.Empty(Run(40, Job(Queued(0))).Events);
        Assert.Single(Run(15, Job(null)).Events);
    }

    [Fact]
    public void ARunningJobsClockStartsWhenItAcquiredTheSlotAndTheAlarmSaysHowLongItWaited()
    {
        var job = Job(Running(20, 20));

        Assert.Empty(Run(25, job).Events);
        Assert.Empty(Run(31, job).Events);
        var alarm = Assert.Single(Run(33, job).Events);
        Assert.Contains("waited", alarm.Text, StringComparison.Ordinal);
        Assert.Contains("for the test slot", alarm.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeARunningJobThatNeverWaitedSaysNothingOfAWait()
    {
        var alarm = Assert.Single(Run(15, Job(Running(0, 0))).Events);

        Assert.DoesNotContain("waited", alarm.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void AJobQueuedLongerThanTheScriptsOwnLimitAlarmsOnceWithTheQueuedText()
    {
        var job = Job(Queued(0));

        Assert.Empty(Run(61, job).Events);
        var alarm = Assert.Single(Run(63, job).Events);
        Assert.Contains("queued for the test slot", alarm.Text, StringComparison.Ordinal);
        Assert.Contains("longer than with-slot's own limit", alarm.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(15, false)]
    [InlineData(63, true)]
    public void TheAlarmTellsTheLeadToAskTheOwnerToExtendAndTheOwnersOwnMailStillGivesTheCommand(double minutes, bool queued)
    {
        var outcome = Run(minutes, Job(queued ? Queued(0) : Running(0, 0)));

        var alarm = Assert.Single(outcome.Events);
        Assert.Contains("only impl can extend it: tell them to run ah roster job extend 20261022-120000-aaaa", alarm.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("wait (ah roster job extend)", alarm.Text, StringComparison.Ordinal);
        foreach (var mail in outcome.OwnerMails)
        {
            Assert.Contains("ah roster job extend 20261022-120000-aaaa", mail.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("tell them", mail.Text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ACapNearAlarmCountsFromAcquisitionAndNeverFiresWhileQueued()
    {
        var capped = Job(Running(20, 20), capAt: 60);
        var queued = Job(Queued(0), capAt: 60);

        Assert.DoesNotContain(Run(53, capped).Events, e => e.Text.Contains("near its hard cap", StringComparison.Ordinal));
        Assert.Contains(Run(57, capped).Events, e => e.Text.Contains("near its hard cap", StringComparison.Ordinal));
        Assert.DoesNotContain(Run(57, queued).Events, e => e.Text.Contains("near its hard cap", StringComparison.Ordinal));
    }

    [Fact]
    public void ThePidJoinPicksTheRecordOfThatProcessAndNamesTheHolderOfAQueuedOne()
    {
        var holder = new SlotRecord(new SlotState("test", false, Ms(0), 0), ["a"], 99, "dotnet test x", "start-99");
        var waiter = new SlotRecord(new SlotState("test", true, Ms(5), 0), ["b"], 4321, "dotnet test y", "start-4321");
        var otherPool = new SlotRecord(new SlotState("pool-x", false, Ms(1), 0), ["c"], 77, "other", "start-77");

        var queued = WatchModule.JobSlotFor([holder, waiter, otherPool], 4321, "start-4321");
        var running = WatchModule.JobSlotFor([holder, waiter, otherPool], 99, "start-99");

        Assert.Equal("queued", queued!.Phase);
        Assert.Equal("test", queued.Pool);
        Assert.Equal("dotnet test x (pid 99) \u00d71", queued.Holder);
        Assert.Equal("running", running!.Phase);
        Assert.Null(WatchModule.JobSlotFor([holder, waiter], 555, "start-555"));
        Assert.Null(WatchModule.JobSlotFor([holder, waiter], null, "start-4321"));
    }

    [Fact]
    public void NegativeARecordOfTheSamePidWithAnotherStartTimeOrAJobWithNoStartTimeJoinsNothing()
    {
        var waiter = new SlotRecord(new SlotState("test", true, Ms(5), 0), ["b"], 4321, "dotnet test y", "start-4321");

        Assert.Null(WatchModule.JobSlotFor([waiter], 4321, "a-reused-pids-start"));
        Assert.Null(WatchModule.JobSlotFor([waiter], 4321, null));
        Assert.Null(WatchModule.JobSlotFor([waiter], 4321, string.Empty));
        Assert.NotNull(WatchModule.JobSlotFor([waiter], 4321, "start-4321"));
    }

    [Fact]
    public void AnExtensionMovesTheQueuedLimitOutAndNegativeAnEarlierExtensionDoesNot()
    {
        var extended = Job(Queued(0)) with { ExtendedAtMs = Ms(30) };
        var stale = Job(Queued(10)) with { ExtendedAtMs = Ms(5) };

        Assert.Empty(Run(65, extended).Events);
        Assert.Single(Run(95, extended).Events);
        Assert.Single(Run(73, stale).Events);
    }

    private static string LaneText(bool waitingOnSlot)
    {
        var lane = new LaneFact("s1", "so", true, false, null, null, null, [], "tm", WaitingOnSlot: waitingOnSlot);
        var events = LaneEscalation.Evaluate("sess", Ms(0), 0, [lane], new LaneEscalation.State(), out _);
        return Assert.Single(events).Text;
    }

    [Fact]
    public void AnEscalatedLaneWhoseSubOrchestratorWaitsOnTheSlotSaysSoAndOneThatDoesNotSaysNothing()
    {
        Assert.Contains("waiting-on-slot", LaneText(true), StringComparison.Ordinal);
        Assert.DoesNotContain("waiting-on-slot", LaneText(false), StringComparison.Ordinal);
    }

    [Fact]
    public void ASlotPhaseSurvivesTheJobRecordAndIsClearedWhenItIsGone()
    {
        var record = new Ah.Core.Json.JsonObject { ["id"] = "x" };

        var slotted = WatchModule.JobSlotted(record, Queued(3));
        var cleared = WatchModule.JobSlotted(slotted, null);

        Assert.Equal("queued", slotted["slot"].AsObject()["phase"].AsString());
        Assert.Null(cleared.Get("slot"));
    }
}
