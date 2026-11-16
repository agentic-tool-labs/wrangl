namespace Ah.Watch.Tests;

/// <summary>Lane escalations that are not news: a finished lane's dismissed lead (b377).</summary>
public sealed class LaneAlarmTests
{
    private static LaneFact DeadLead(string state, params LaneDispatch[] open) => new("s1", "so", true, false, null, state, null, open, "tm");

    [Fact]
    public void B377ADeadLeadOfADoneLaneWithNothingOpenIsNotEscalated() =>
        Assert.Null(LaneEscalation.ReasonOf(DeadLead("done"), 1000, 0, 0));

    [Fact]
    public void B377NegativeADeadLeadOfADoneLaneWithAnOpenDispatchIsStillDead() =>
        Assert.Equal("E-a", LaneEscalation.ReasonOf(DeadLead("done", new LaneDispatch("r1", "impl", "on-track")), 1000, 0, 0));

    [Fact]
    public void B377NegativeADeadLeadOfAWorkingLaneIsStillDead() =>
        Assert.Equal("E-a", LaneEscalation.ReasonOf(DeadLead("working"), 1000, 0, 0));

    private static string? QuietLead(double? childActiveMs)
    {
        var now = 2 * (LaneEscalation.StaleMs + JobEvents.MarginMs);
        return LaneEscalation.ReasonOf(new LaneFact("s1", "so", false, false, null, "working", 0, [], "tm", ChildActiveMs: childActiveMs), now, now, 0);
    }

    [Fact]
    public void B376ALaneWhoseChildTeamWorkedRecentlyIsNotStale() =>
        Assert.Null(QuietLead(2 * (LaneEscalation.StaleMs + JobEvents.MarginMs) - 1000));

    [Fact]
    public void B376NegativeALaneWhoseChildTeamLastWorkedLongAgoIsStillStale() =>
        Assert.Equal("E-d", QuietLead(1000));
}
