using Ah.Core.Json;

namespace Ah.Hierarchy.Tests;

/// <summary>
/// The read model asks <c>msgs.heard</c> for every open request of every directory, so what one call costs must not grow with the
/// number of requests asked about times the number of peer records.
/// </summary>
public sealed class MsgsHeardCostTests
{
    private static string Iso(int minute) => $"2026-06-01T10:{minute:00}:00.000Z";

    private static double Ms(int minute) => DateTimeOffset.Parse(Iso(minute), System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeMilliseconds();

    private static void Append(HierarchyReadOpsTests.Rig rig, JsonObject row) =>
        rig.Services.Peers.AppendPeerRecord(new FsStoreWriteContext(), JsonValue.FromObject(row));

    private static void Dispatch(HierarchyReadOpsTests.Rig rig, string id, string session, string to) =>
        Append(rig, new JsonObject { ["type"] = "dispatch", ["request_id"] = id, ["session_id"] = session, ["to_addr"] = to });

    private static void Heard(HierarchyReadOpsTests.Rig rig, string session, string from, int minute) =>
        Append(rig, new JsonObject { ["type"] = "heard", ["session_id"] = session, ["from"] = from, ["ts"] = Iso(minute) });

    private static JsonObject HeardOf(HierarchyReadOpsTests.Rig rig, IEnumerable<string> ids)
    {
        var list = new JsonArray();
        foreach (var id in ids)
        {
            list.Add(id);
        }

        var reply = HierarchyReadOpsTests.Parsed(HierarchyReadOpsTests.Read(rig, "msgs", "heard", new JsonObject { ["dir"] = rig.Pool, ["ids"] = list }));
        return reply.Member("heard")!.Value.AsObject();
    }

    [Fact]
    public void EachIdIsAnsweredFromItsOwnLatestDispatchAndTheLatestWordOfThatAddresseeToThatSession()
    {
        var rig = HierarchyReadOpsTests.Make();
        Dispatch(rig, "a", "s9", "x");
        Dispatch(rig, "a", "s1", "x");
        Dispatch(rig, "b", "s2", "y");
        Heard(rig, "s1", "x", 1);
        Heard(rig, "s1", "x", 3);
        Heard(rig, "s1", "x", 2);
        Heard(rig, "s1", "y", 9);
        Heard(rig, "s2", "x", 8);
        Heard(rig, "s2", "y", 5);
        Heard(rig, "s9", "x", 7);

        var heard = HeardOf(rig, ["a", "b", "none"]);

        Assert.Equal(Ms(3), heard.Get("a")!.Value.AsNumber());
        Assert.Equal(Ms(5), heard.Get("b")!.Value.AsNumber());
        Assert.Equal(0, heard.Get("none")!.Value.AsNumber());
    }

    [Fact]
    public void NegativeADispatchedIdWhoseAddresseeWasNeverHeardForThatSessionIsZero()
    {
        var rig = HierarchyReadOpsTests.Make();
        Dispatch(rig, "a", "s1", "x");
        Heard(rig, "s1", "y", 4);
        Heard(rig, "s2", "x", 4);

        Assert.Equal(0, HeardOf(rig, ["a"]).Get("a")!.Value.AsNumber());
    }

    [Fact]
    public void WhatOneCallAllocatesDoesNotGrowWithTheNumberOfIdsAskedAbout()
    {
        var rig = HierarchyReadOpsTests.Make();
        var ids = new List<string>();
        for (var i = 0; i < 100; i++)
        {
            ids.Add("req-" + i);
            Dispatch(rig, "req-" + i, "s" + (i % 5), "addr-" + (i % 7));
        }

        for (var i = 0; i < 2_000; i++)
        {
            Heard(rig, "s" + (i % 5), "addr-" + (i % 7), 1 + (i % 50));
        }

        _ = HeardOf(rig, ids);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var heard = HeardOf(rig, ids);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(100, heard.Keys.Count);
        Assert.True(allocated < 2_000_000, $"one call over 100 ids and 2100 peer records allocated {allocated} bytes");
    }
}
