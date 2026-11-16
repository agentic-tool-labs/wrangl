namespace Ah.Watch.Tests;

/// <summary>ah-ui-ia U0 (b): the team summary names a confirmed lane as <c>lane: {parent_team, stream}</c> and an unconfirmed one as <c>lane_claim</c>, from the team slice's own lane facts.</summary>
public sealed partial class TreeLanesTests
{
    private static string LaneSummaryId(string name) => $"team-summary:{D}/{name}";

    [Fact]
    public void AConfirmedLanesSummaryCarriesOnlyItsParentTeamAndStream()
    {
        var (model, _) = MakeLanes(LaneTruth(Streams(("s1", "open")), ChildDoc("mgr", "s1")));
        using var owner = model;

        var summary = Slice(model, LaneSummaryId("mgr-s1"));
        var lane = summary.Member("lane")!.Value.AsObject();

        Assert.Equal(["parent_team", "stream"], lane.Keys.ToArray());
        Assert.Equal("mgr", lane.Get("parent_team")!.Value.AsString());
        Assert.Equal("s1", lane.Get("stream")!.Value.AsString());
        Assert.Null(summary.Member("lane_claim"));
    }

    [Fact]
    public void NegativeAClaimTheManagerDoesNotConfirmGivesTheSummaryALaneClaimAndNoLane()
    {
        var (model, _) = MakeLanes(LaneTruth(Streams(("s2", "open")), ChildDoc("mgr", "s1")));
        using var owner = model;

        var summary = Slice(model, LaneSummaryId("mgr-s1"));

        Assert.Null(summary.Member("lane"));
        Assert.Equal("mgr", summary.Member("lane_claim")!.Value.Member("parent_team")!.Value.AsString());
    }

    [Fact]
    public void NegativeATopLevelTeamsSummaryCarriesNeitherLaneNorLaneClaim()
    {
        var (model, _) = MakeLanes(LaneTruth(Streams(("s1", "open")), ChildDoc("mgr", "s1")));
        using var owner = model;

        var summary = Slice(model, LaneSummaryId("mgr"));

        Assert.Null(summary.Member("lane"));
        Assert.Null(summary.Member("lane_claim"));
        Assert.Null(summary.Member("streams"));
    }

    [Fact]
    public void TheManagerConfirmingTheStreamRefreshesTheChildsSummaryWithTheChildFileUnchanged()
    {
        var reader = LaneTruth(Streams(("s2", "open")), ChildDoc("mgr", "s1"));
        var (model, clock) = MakeLanes(reader);
        using var owner = model;
        Assert.Null(Slice(model, LaneSummaryId("mgr-s1")).Member("lane"));

        reader.Teams[MgrPath] = MgrDoc(Streams(("s1", "open"), ("s2", "open")));
        model.Emit("teams", TeamKey(MgrPath));
        Drain(model, clock);

        var summary = Slice(model, LaneSummaryId("mgr-s1"));
        Assert.Equal("s1", summary.Member("lane")!.Value.Member("stream")!.Value.AsString());
        Assert.Null(summary.Member("lane_claim"));
    }
}
