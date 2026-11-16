using Ah.Watch.Model;

namespace Ah.Watch.Tests;

/// <summary><c>AnyPendingKey</c> answers what <c>PendingKeys(lane).Count &gt; 0</c> answers, through queued, taken, completed and key-set notices (ah2-52).</summary>
public sealed class ModelBusAnyPendingKeyTests
{
    private static void Same(ModelBus bus, string lane, bool expected)
    {
        Assert.Equal(expected, bus.PendingKeys(lane).Count > 0);
        Assert.Equal(expected, bus.AnyPendingKey(lane));
    }

    [Fact]
    public void AnyPendingKeyFollowsPendingKeysAndNegativeAKeySetOrAnotherLaneIsNotAPendingKey()
    {
        using var bus = new ModelBus(static () => 1000);
        Same(bus, "status", false);

        // negative: only a key-set notice, and a key of another lane
        _ = bus.Emit(new LaneKey("status", LaneKey.KeySet), NoticeOrigin.Routed, 0);
        _ = bus.Emit(new LaneKey("messages", "m1"), NoticeOrigin.Routed, 0);
        Same(bus, "status", false);
        Same(bus, "messages", true);

        _ = bus.Emit(new LaneKey("status", "d1"), NoticeOrigin.Reconcile, 0);
        Same(bus, "status", true);

        // taken (in flight) still counts; completed with nothing new drops it
        TakenNotice? taken;
        while ((taken = bus.TryTake()) is not null && taken.Key != new LaneKey("status", "d1"))
        {
        }

        Assert.NotNull(taken);
        Same(bus, "status", true);
        bus.Complete(taken!.Key, 0);
        Same(bus, "status", false);
    }
}
