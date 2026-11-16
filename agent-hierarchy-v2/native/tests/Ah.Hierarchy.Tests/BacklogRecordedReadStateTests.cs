namespace Ah.Hierarchy.Tests;

/// <summary>A status replays health from recorded facts, so the read state has to survive the recording.</summary>
public sealed class BacklogRecordedReadStateTests
{
    private const long Day = 24 * 3600 * 1000L;

    private static IReadOnlyList<object?[]> Rows(string sql, object?[] args) =>
        sql.Contains("COUNT(*)", StringComparison.Ordinal) ? [["now", "not_started", 1L]]
        : sql.Contains("updated_ms", StringComparison.Ordinal) ? [["now", "not_started", 0L, null, "b1"]]
        : [];

    private static PlanItem Child(string state, string parent = "b1") =>
        new("c1", "T", state, "2026-11-04T00:00:00.000Z", null, null, [], null, null, null) { Parent = parent };

    private static BacklogHealth Replayed(params PlanItem[] items) =>
        Backlog.HealthFromFacts(Backlog.HealthFacts(Rows, "/d", items), "/d", 20 * Day, null);

    [Fact]
    public void ARowInReviewIsNotStaleOnReplay()
    {
        var health = Replayed(Child("in_review"));
        Assert.Equal(0, health.Stale);
        Assert.Equal(1, health.Items["now"]["in_review"]);
    }

    [Fact]
    public void NegativeAQueuedItemOrAnotherRowsItemLeavesTheRowStaleOnReplay()
    {
        Assert.Equal(1, Replayed().Stale);
        Assert.Equal(1, Replayed(Child("next")).Stale);
        Assert.Equal(1, Replayed(Child("in_review", "b2")).Stale);
    }
}
