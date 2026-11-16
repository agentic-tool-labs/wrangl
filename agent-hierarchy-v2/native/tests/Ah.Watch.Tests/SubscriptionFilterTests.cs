using Ah.Watch.Model;

namespace Ah.Watch.Tests;

public sealed class SubscriptionFilterTests
{
    /// <summary>
    /// ah2-15: the filter is asked once per published slice on every poll wake-up. It answers what the plain rule answers
    /// (a listed id, or an id under a listed view's prefix) and a call allocates nothing (negative: no closure per asked id).
    /// </summary>
    [Fact]
    public void TheFilterAnswersTheListedIdsAndViewPrefixesAndACallAllocatesNothing()
    {
        Assert.Null(ModelViews.Subscription(null));
        var filter = ModelViews.Subscription(["view:tree", "view:status", "exchange:x1", "view:no-such-view"])!;
        string[] prefixes = ["team:", "member:", "topology:", "worktree:", "list:teams", "status:"];
        string[] ids =
        [
            "team:a", "member:a/b", "topology:a", "worktree:w", "list:teams", "list:teams:extra", "status:/p", "exchange:x1",
            "exchange:x2", "exchange:x1:more", "activity:a", "processes:/p", "list:team", "", "view:tree", "no-such-view",
        ];
        foreach (var id in ids)
        {
            Assert.Equal(id == "exchange:x1" || prefixes.Any(p => id.StartsWith(p, StringComparison.Ordinal)), filter(id));
        }

        Assert.False(ModelViews.Subscription([])!("team:a"));

        _ = filter("warm:up");
        var before = GC.GetAllocatedBytesForCurrentThread();
        var hits = 0;
        for (var i = 0; i < 10_000; i++)
        {
            hits += filter(ids[i % ids.Length]) ? 1 : 0;
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.True(hits > 0);
    }
}
