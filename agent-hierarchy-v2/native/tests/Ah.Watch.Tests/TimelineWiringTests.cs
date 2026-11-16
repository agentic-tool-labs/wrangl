using Ah.Core.Json;
using Ah.Watch.Model;

namespace Ah.Watch.Tests;

/// <summary>The model as assembled with a timeline store: transitions are recorded hot, the count slice is lazy.</summary>
public sealed class TimelineWiringTests
{
    private const string Dir = "/h/.claude/ah2";

    private sealed class Reader : IViewStateReader
    {
        public string State { get; set; } = "busy";

        public string? ToolAt { get; set; }

        public static readonly string Stamp = DateTimeOffset.UtcNow.AddHours(-1).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);

        public JsonValue Read(string domain, string op, string hierarchyDir, JsonObject args)
        {
            return (domain + "." + op) switch
            {
                "activity.keys" => new JsonObject { ["subjects"] = JsonValue.FromArray([JsonValue.FromString("s1")]) },
                "activity.read" => new JsonObject { ["activity"] = State, ["tool_at"] = ToolAt ?? Stamp },
                "status.read" => JsonValue.Null,
                _ => throw new InvalidOperationException("unexpected read " + domain + "." + op),
            };
        }
    }

    private static void Drain(ReadModel model, Func<bool> done)
    {
        var until = Environment.TickCount64 + 3000;
        while (Environment.TickCount64 < until && !done())
        {
            if (!model.Projector.Step())
            {
                Thread.Sleep(10);
            }
        }
    }

    [Fact]
    public void ATransitionIsRecordedWithNoClientAndTheCountSliceAppearsOnTheFirstRequest()
    {
        var store = new TimelineTests.MemoryStore();
        var reader = new Reader();
        using var model = WatchModule.Assemble(reader, () => [Dir], null, store, () => 7);
        var inner = LazyAwareModel.Unwrap(model)!;

        model.EmitKeySet("activity");
        Drain(inner, () => store.Live.Count == 1);

        var row = Assert.Single(store.Live);
        Assert.Equal((null, "busy", DateTimeOffset.Parse(Reader.Stamp, System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeMilliseconds()), (row.From, row.To, row.AtMs));
        Assert.Equal(0, store.Counts);
        Assert.DoesNotContain(inner.Store.Current.Slices.Keys, static id => id.StartsWith("timeline:", StringComparison.Ordinal));

        Assert.Equal(1, store.Count(ActivityLane.DirId(Dir), 0, long.MaxValue));
        store.ResetCounts();
        _ = model.Manifest("members");
        Drain(inner, () => inner.Store.Current.Slices.ContainsKey(TimelineLane.SliceIdOf(Dir)));

        var body = JsonText.Parse(inner.Store.Current.Slices[TimelineLane.SliceIdOf(Dir)].Body);
        Assert.Equal(1, body.Member("total")!.Value.AsNumber());
        Assert.Equal(row.AtMs, body.Member("oldest_at")!.Value.AsNumber());
        Assert.Equal(row.Seq, body.Member("head_cursor")!.Value.AsNumber());
    }

    [Fact]
    public void ARowOlderThanTheRetentionWindowIsPrunedWhenAClientArrivesAndLeavesTheLiveCount()
    {
        var store = new TimelineTests.MemoryStore();
        var reader = new Reader { ToolAt = "2026-06-01T12:00:00Z" };
        using var model = WatchModule.Assemble(reader, () => [Dir], null, store, () => 7);
        var inner = LazyAwareModel.Unwrap(model)!;
        model.EmitKeySet("activity");
        Drain(inner, () => store.Live.Count == 1);

        _ = model.Manifest("members");
        Drain(inner, () => store.Live.Count == 0 && inner.Store.Current.Slices.TryGetValue(TimelineLane.SliceIdOf(Dir), out var s) && JsonText.Parse(s.Body).Member("total")!.Value.AsNumber() == 0);

        Assert.Empty(store.Live);
    }

    [Fact]
    public void NegativeAModelWithoutAStoreHasNoTimelineLaneAndRecordsNothing()
    {
        var reader = new Reader();
        using var model = WatchModule.Assemble(reader, () => [Dir], null, null, null);
        var inner = LazyAwareModel.Unwrap(model)!;

        model.EmitKeySet("activity");
        Drain(inner, () => inner.Store.Current.Slices.Keys.Any(static id => id.StartsWith("activity:", StringComparison.Ordinal)));

        Assert.DoesNotContain(inner.Store.Current.Slices.Keys, static id => id.StartsWith("timeline:", StringComparison.Ordinal));
    }
}
