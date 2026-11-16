using Ah.Hierarchy;
namespace Ah.Watch.Tests;

public sealed class StallDiagnosisTests
{
    private const double Poll = 15_000;
    private static readonly DateTimeOffset Start = new(2026, 10, 21, 8, 0, 0, TimeSpan.Zero);

    private static double Ms(double seconds) => Start.ToUnixTimeMilliseconds() + (seconds * 1000);

    private static EtaStateResult Eta(double nowSeconds) =>
        EtaState.Compute("req-1", "small", 300, Start.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture), "impl@tm", Ms(0), "orch", [], [], false, null, Ms(nowSeconds), Poll);

    private static DiagnoseFacts Facts(
        double nowSeconds = 600,
        bool up = true,
        double? tick = 5_000,
        double? stall = 5_000,
        IReadOnlyList<AhdEventRow>? events = null,
        bool waiter = true) =>
        new(up, 4242, tick, stall, Poll, Eta(nowSeconds), events ?? [], waiter, Ms(nowSeconds));

    [Fact]
    public void NoLiveDaemonPidIsDaemonDownEvenWhenLaterRulesWouldAlsoMatch() =>
        Assert.Equal("daemon-down", StallDiagnosis.Decide(Facts(up: false, tick: 999_999, events: [new AhdEventRow("e1", "req-1", "check-in", null)], waiter: false)).Cause);

    [Fact]
    public void ADaemonThatIsUpWithAStaleWatchTickStampIsJobNotRunningNamingTheJob()
    {
        var verdict = StallDiagnosis.Decide(Facts(tick: (3 * Poll) + 1));

        Assert.Equal("job-not-running", verdict.Cause);
        Assert.Contains("watch-tick", verdict.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void AStaleStallWatchStampIsJobNotRunningNamingStallWatch()
    {
        var verdict = StallDiagnosis.Decide(Facts(stall: null));

        Assert.Equal("job-not-running", verdict.Cause);
        Assert.Contains("stall-watch", verdict.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void AStampExactlyThreePeriodsOldIsStillFresh() =>
        Assert.NotEqual("job-not-running", StallDiagnosis.Decide(Facts(tick: 3 * Poll, stall: 3 * Poll)).Cause);

    [Fact]
    public void FreshJobsAndNoEventPastTheNextStepIsEventNotRaised() =>
        Assert.Equal("event-not-raised", StallDiagnosis.Decide(Facts()).Cause);

    [Fact]
    public void ANextStepThatIsNotDueYetIsUnknownNotEventNotRaised() =>
        Assert.Equal("unknown", StallDiagnosis.Decide(Facts(nowSeconds: 100)).Cause);

    [Fact]
    public void AnUndeliveredEventWithNoLiveWaiterIsWaiterDead() =>
        Assert.Equal("waiter-dead", StallDiagnosis.Decide(Facts(events: [new AhdEventRow("e1", "req-1", "check-in", null)], waiter: false)).Cause);

    [Fact]
    public void AnUndeliveredEventWithALiveWaiterIsEventNotDelivered() =>
        Assert.Equal("event-not-delivered", StallDiagnosis.Decide(Facts(events: [new AhdEventRow("e1", "req-1", "check-in", null)])).Cause);

    [Fact]
    public void ADeliveredEventIsUnknown() =>
        Assert.Equal("unknown", StallDiagnosis.Decide(Facts(events: [new AhdEventRow("e1", "req-1", "check-in", 1)])).Cause);

    [Fact]
    public void WhenTwoRulesMatchTheFirstWins() =>
        Assert.Equal("job-not-running", StallDiagnosis.Decide(Facts(tick: null, events: [new AhdEventRow("e1", "req-1", "check-in", null)], waiter: false)).Cause);
}
