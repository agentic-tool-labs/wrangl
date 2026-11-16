using Ah.Watch.Model;

namespace Ah.Watch.Tests;

/// <summary>A lane stores its now (<c>done_seen_at</c>) and compares it with event times, so with no clock injected it reads epoch milliseconds, never uptime.</summary>
public sealed class ReadModelClockTests
{
    private sealed class ClockLane : IModelLane
    {
        public long Seen { get; private set; }

        public string Name => "clock";

        public IReadOnlyList<string> SliceIds(string key) => [];

        public IReadOnlyList<string> Keys() => [];

        public LaneResult Reread(string key, long nowMs)
        {
            Seen = nowMs;
            return new LaneResult([]);
        }
    }

    private static long Read(Func<long>? nowMs)
    {
        var lane = new ClockLane();
        using var model = new ReadModel([lane], new ModelOptions { SeedAtStart = false }, nowMs, null, "e", null, new ComposerRegistry());
        model.Emit(lane.Name, "k");
        var deadline = Environment.TickCount64 + 5_000;
        while (lane.Seen == 0 && Environment.TickCount64 < deadline)
        {
            _ = model.Projector.Step();
        }

        return lane.Seen;
    }

    [Fact]
    public void WithNoClockInjectedALaneReadsEpochMillisecondsNotUptime()
    {
        var before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var seen = Read(null);
        Assert.InRange(seen, before, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    [Fact]
    public void NegativeAnInjectedClockIsWhatALaneReads()
    {
        // it moves, since a notice falls due only after its settle time
        var now = 1000L;
        Assert.InRange(Read(() => now += 100), 1100, 100_000);
    }

    /// <summary>On a loaded machine some real notice is nearly always waiting out its coalesce window, and the reconcile is what heals an event the watcher lost there.</summary>
    [Fact]
    public void AReconcileStartsAndIsServedWhileARealNoticeWaitsOutItsWindow()
    {
        var now = 1000L;
        var lane = new ClockLane();
        using var model = new ReadModel([lane], new ModelOptions { SeedAtStart = false }, () => now, null, "e", null, new ComposerRegistry());
        _ = model.Bus.Emit(new LaneKey(lane.Name, "waiting"), NoticeOrigin.Hint, 100);
        model.Scheduler.Touch();

        model.Tick();
        Assert.True(model.Bus.HasKeySet(lane.Name));

        var taken = model.Bus.TryTake();
        Assert.Equal(LaneKey.KeySet, taken!.Key.Key);
        Assert.False(taken.Real);
    }

    [Fact]
    public void NegativeARealNoticeThatIsDueIsServedBeforeAReconcile()
    {
        var now = 1000L;
        using var bus = new ModelBus(() => now);
        _ = bus.Emit(new LaneKey("l", LaneKey.KeySet), NoticeOrigin.Reconcile, 0);
        _ = bus.Emit(new LaneKey("l", "real"), NoticeOrigin.Hint, 100);
        now += 100;

        Assert.Equal("real", bus.TryTake()!.Key.Key);
        Assert.Equal(LaneKey.KeySet, bus.TryTake()!.Key.Key);
    }

    [Fact]
    public void NegativeWithNoClientConnectedNoReconcileStarts()
    {
        var lane = new ClockLane();
        using var model = new ReadModel([lane], new ModelOptions { SeedAtStart = false }, () => 1000L, null, "e", null, new ComposerRegistry());

        model.Tick();

        Assert.False(model.Bus.HasKeySet(lane.Name));
    }

    [Fact]
    public void ALaneIsReconciledAMinuteAfterItsLastOneAndASlowLaneTenTimesItsCost()
    {
        var now = 1000L;
        var lane = new ClockLane();
        using var model = new ReadModel([lane], new ModelOptions { SeedAtStart = false }, () => now, null, "e", null, new ComposerRegistry());

        bool Reconciling()
        {
            model.Scheduler.Touch();
            model.Tick();
            return model.Bus.HasKeySet(lane.Name);
        }

        void Finish(long costMs)
        {
            now += costMs;
            for (var guard = 0; guard < 1000 && model.Bus.HasKeySet(lane.Name); guard++)
            {
                _ = model.Projector.Step();
            }

            model.Tick();
        }

        Assert.True(Reconciling());
        Finish(0);

        now += 59_000;
        Assert.False(Reconciling());
        now += 2_000;
        Assert.True(Reconciling());
        Finish(10_000);

        now += 51_000;
        Assert.True(Reconciling());
        Finish(0);

        now += 61_000;
        Assert.False(Reconciling());
        now += 40_000;
        Assert.True(Reconciling());
    }
}
