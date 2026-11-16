using Ah.Core.Json;
using Ah.Watch.Model;

namespace Ah.Watch.Tests;

/// <summary>
/// ah2-52 item 2: when the message store version moved, a dir none of whose messages changed since its hold keeps its held keys without a
/// listing; any other dir is listed. After every write the keys are exactly what a lane with no hold lists. Real store, real msgs ops.
/// </summary>
public sealed class MessagesLaneChangedSkipTests
{
    private static string Request(RealStateRig rig, string pool, string slug) =>
        rig.Services.Messages.CreateMessage(pool, new JsonObject { ["type"] = "request", ["to"] = "implementor", ["from"] = "orchestrator", ["slug"] = slug }).Member("path")!.Value.AsString();

    /// <summary>Calls <paramref name="held"/>'s <c>Keys()</c> once and asserts it equals a lane with no hold.</summary>
    private static void AssertSameAsFresh(RealStateRig rig, MessagesLane held, IReadOnlyList<string> pools, string what)
    {
        var keys = held.Keys();
        var fresh = new MessagesLane(rig.Reader(), () => pools).Keys();
        Assert.True(fresh.SequenceEqual(keys), what + ": held " + string.Join(",", keys) + " fresh " + string.Join(",", fresh));
    }

    [Fact]
    public void ADirWhosePoolDidNotChangeKeepsItsKeysAndOneThatDidIsListedAndBothMatchAFreshListing()
    {
        var rig = RealStateRig.Make();
        var a = rig.Pool;
        var b = rig.NewPool("b");
        var c = rig.NewPool("c");
        string[] pools = [a, b, c];
        _ = Request(rig, a, "a-one");
        _ = Request(rig, b, "b-one");
        var lane = new MessagesLane(rig.Reader(), () => pools);

        _ = lane.Keys();
        Assert.Equal(3, lane.KeyReads);
        AssertSameAsFresh(rig, lane, pools, "seeded");
        var reads = lane.KeyReads;

        // a write to b: only b is listed again
        _ = Request(rig, b, "b-two");
        AssertSameAsFresh(rig, lane, pools, "b written");
        Assert.Equal(reads + 1, lane.KeyReads);
        reads = lane.KeyReads;

        // a write to a: a is listed again, and its new request is there
        var made = Request(rig, a, "a-two");
        AssertSameAsFresh(rig, lane, pools, "a written");
        Assert.Equal(reads + 1, lane.KeyReads);
        reads = lane.KeyReads;

        // an archive in a stamps the row it moves: a is listed again and the request is gone from its keys
        rig.Services.StateCalls.ArchiveMessages(a, [made]);
        AssertSameAsFresh(rig, lane, pools, "a archived");
        Assert.Equal(reads + 1, lane.KeyReads);
        reads = lane.KeyReads;

        // nothing written: nothing listed
        _ = lane.Keys();
        Assert.Equal(reads, lane.KeyReads);
    }

    [Fact]
    public void NegativeAChangedReadThatFailsListsTheDir()
    {
        var rig = RealStateRig.Make();
        var a = rig.Pool;
        var b = rig.NewPool("b");
        string[] pools = [a, b];
        _ = Request(rig, a, "a-one");
        var lane = new MessagesLane(new FailingChanged(rig.Reader()), () => pools);
        _ = lane.Keys();
        var reads = lane.KeyReads;

        _ = Request(rig, b, "b-one");
        AssertSameAsFresh(rig, lane, pools, "changed unreadable");

        Assert.Equal(reads + 2, lane.KeyReads);
    }

    private sealed class FailingChanged(IViewStateReader inner) : IViewStateReader
    {
        public JsonValue Read(string domain, string op, string hierarchyDir, JsonObject args) =>
            op == "changed" ? throw new InvalidOperationException("busy") : inner.Read(domain, op, hierarchyDir, args);
    }
}
