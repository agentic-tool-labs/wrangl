using Ah.Watch.Model;

namespace Ah.Watch.Tests;

/// <summary>What every <see cref="ITimelineStore"/> must do. A store implementation gets a concrete subclass that supplies <see cref="Create"/>.</summary>
public abstract class TimelineStoreContract
{
    private const long Day = 86_400_000L;

    internal abstract ITimelineStore Create();

    private static TimelineRow Row(string dir, string subject, string? from, string to, long at) => new(0, dir, subject, from, to, at);

    [Fact]
    public void RangeIsInArrivalOrderAndPagesByCursor()
    {
        using var store = Create();
        store.Append([Row("d", "a", null, "busy", 30), Row("d", "b", null, "busy", 10), Row("d", "a", "busy", "idle", 20), Row("d", "c", null, "busy", 10)]);

        var all = store.Range("d", 0, long.MaxValue, 0, 0, 200);
        Assert.Equal([30L, 10L, 20L, 10L], all.Select(static r => r.AtMs));
        Assert.Equal(["a", "b"], all.Take(2).Select(static r => r.Subject));
        Assert.Equal(all.OrderBy(static r => r.Seq).Select(static r => r.Seq), all.Select(static r => r.Seq));

        var page = store.Range("d", 0, long.MaxValue, 0, 0, 2);
        var next = store.Range("d", 0, long.MaxValue, page[^1].Seq, 0, 2);
        Assert.Equal(all.Skip(2).Select(static r => r.Seq), next.Select(static r => r.Seq));
        Assert.Equal(all.Where(r => r.Seq < all[1].Seq).Select(static r => r.Seq), store.Range("d", 0, long.MaxValue, 0, all[1].Seq, 200).Select(static r => r.Seq));
    }

    [Fact]
    public void NegativeRangeAndCountStayInsideTheDirAndTheTimeWindowAndTheLimitIsCapped()
    {
        using var store = Create();
        store.Append([.. Enumerable.Range(1, 250).Select(i => Row("d", "s" + i, null, "busy", i)), Row("e", "x", null, "busy", 5)]);

        Assert.Equal(200, store.Range("d", 0, long.MaxValue, 0, 0, 5000).Count);
        Assert.Equal(11, store.Count("d", 10, 20));
        Assert.Equal(250, store.Count("d", 0, long.MaxValue));
        Assert.Equal(0, store.Count("none", 0, long.MaxValue));
        Assert.DoesNotContain(store.Range("d", 0, long.MaxValue, 0, 0, 200), static r => r.DirId != "d");
    }

    [Fact]
    public void RecentIsTheStateJustBeforeTheSinceTimeThenTheSubjectsOwnTransitionsFromItOldestFirst()
    {
        using var store = Create();
        store.Append([Row("d", "a", null, "busy", 10), Row("d", "a", "busy", "idle", 20), Row("d", "a", "idle", "busy", 40), Row("d", "a", "busy", "idle", 50), Row("d", "b", null, "busy", 45), Row("e", "a", null, "failed", 45)]);

        var recent = store.Recent("d", "a", 30);

        Assert.Equal([("idle", 20L), ("busy", 40L), ("idle", 50L)], recent.Select(static s => (s.State, s.AtMs)));
        Assert.All(recent, static s => Assert.Equal("a", s.Subject));
        Assert.Equal([("busy", 10L), ("idle", 20L), ("busy", 40L), ("idle", 50L)], store.Recent("d", "a", 0).Select(static s => (s.State, s.AtMs)));
        Assert.Equal([("idle", 20L)], store.Recent("d", "a", 30).Take(1).Select(static s => (s.State, s.AtMs)));
        Assert.Equal([("idle", 50L)], store.Recent("d", "a", 51).Select(static s => (s.State, s.AtMs)));
        Assert.Empty(store.Recent("d", "none", 0));
        Assert.Empty(store.Recent("other", "a", 0));
    }

    [Fact]
    public void AppendingTheSameTransitionAgainAddsNothing()
    {
        using var store = Create();
        var row = Row("d", "a", null, "busy", 10);

        store.Append([row]);
        store.Append([row, row with { From = "idle" }]);

        Assert.Equal(1, store.Count("d", 0, long.MaxValue));
    }

    [Fact]
    public void PruneDeletesTheOldestRowsFirstInBatches()
    {
        using var store = Create();
        store.Append([Row("d", "a", null, "busy", 10), Row("d", "b", null, "busy", 11), Row("d", "c", null, "busy", 20), Row("d", "d", null, "busy", 30)]);

        Assert.Equal(2, store.Prune(30, 2));
        Assert.Equal(["c", "d"], store.Range("d", 0, long.MaxValue, 0, 0, 200).Select(static r => r.Subject));
        Assert.Equal(1, store.Prune(30, 2));
        Assert.Equal(0, store.Prune(30, 2));
        Assert.Equal(["d"], store.Range("d", 0, long.MaxValue, 0, 0, 200).Select(static r => r.Subject));
    }

    [Fact]
    public void LastStateIsTheNewestRowsStateOfThatSubjectAndNullForAnUnknownOne()
    {
        using var store = Create();
        store.Append([Row("d", "a", null, "busy", 10), Row("d", "b", null, "idle", 11), Row("d", "a", "busy", "idle", 12), Row("e", "a", null, "gone", 13)]);

        Assert.Equal("idle", store.LastState("d", "a"));
        Assert.Equal("idle", store.LastState("d", "b"));
        Assert.Equal("gone", store.LastState("e", "a"));
        Assert.Null(store.LastState("d", "nobody"));
        Assert.Null(store.LastState("none", "a"));
    }

    [Fact]
    public void NegativePruningNothingOlderThanTheCutoffDeletesNothing()
    {
        using var store = Create();
        store.Append([Row("d", "a", null, "busy", 10 * Day)]);

        Assert.Equal(0, store.Prune(10 * Day, 1000));
        Assert.Equal(1, store.Count("d", 0, long.MaxValue));
    }
}

/// <summary>The contract against the in-memory store the tests and the wiring use until the file-backed one lands.</summary>
public sealed class MemoryTimelineStoreContract : TimelineStoreContract
{
    internal override ITimelineStore Create() => new TimelineTests.MemoryStore();
}
