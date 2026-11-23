using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.Hierarchy.Tests;

/// <summary>The <c>resources</c> domain (ah2-509): one live holder per name, the shared liveness rule, FIFO waiters, release and re-entry.</summary>
public sealed class ResourceStoreTests
{
    private const string Started = "Fri Oct 30 01:00:00 2026";

    private static readonly long Me = Environment.ProcessId;

    /// <summary>Every pid has start <see cref="Started"/>.</summary>
    private sealed class FixedStarts : IProcessTable
    {
        public IReadOnlyList<int>? Ancestors(int pid) => null;

        public bool? HasAncestor(int pid, int candidate) => null;

        public string? StartTime(long pid) => Started;
    }

    private static JsonObject Owner(long pid, string? start) => new() { ["pid"] = pid, ["start"] = start is null ? JsonValue.Null : start };

    private static JsonValue Claim(HierarchyReadOpsTests.Rig rig, JsonObject holder, JsonObject waiter, string who, bool queue = true) =>
        HierarchyReadOpsTests.Parsed(HierarchyReadOpsTests.Read(rig, "resources", "claim", new JsonObject { ["name"] = "Blender", ["holder"] = holder, ["waiter"] = waiter, ["who"] = who, ["queue"] = queue }));

    private static JsonValue Release(HierarchyReadOpsTests.Rig rig, JsonObject holder, bool force) =>
        HierarchyReadOpsTests.Parsed(HierarchyReadOpsTests.Read(rig, "resources", "release", new JsonObject { ["name"] = "blender", ["holder"] = holder, ["force"] = force }));

    private static string State(JsonValue answer) => answer.Member("state")!.Value.AsString();

    private static string? HolderOf(HierarchyReadOpsTests.Rig rig) =>
        Ah.StateDb.StateDbModule.Resources(rig.AhHome).SingleOrDefault(static r => r.Kind == "hold")?.Who;

    [Fact]
    public void AHoldWhosePidNowHasAnotherStartIsFreeAndOneWithTheSameStartIsNot()
    {
        var rig = HierarchyReadOpsTests.Make(processTable: new FixedStarts());
        Assert.Equal("granted", State(Claim(rig, Owner(Me, "Thu Oct 29 01:00:00 2026"), Owner(Me, "w0"), "a@t")));

        // R1: the recorded pid is alive, but its start differs: reused, so the name is free
        Assert.Equal("granted", State(Claim(rig, Owner(Me, Started), Owner(Me, "w1"), "b@t")));

        // nearest input that must not match: the same pid and start is a live holder
        Assert.Equal("held", State(Claim(rig, Owner(Me, "other"), Owner(Me, "w2"), "c@t", queue: false)));
        Assert.Equal("b@t", HolderOf(rig));
    }

    [Fact]
    public void WithTheHolderDeadTheOlderOfTwoWaitersGetsTheHoldOnItsNextPoll()
    {
        var rig = HierarchyReadOpsTests.Make();
        using var holder = System.Diagnostics.Process.Start("/bin/sleep", "60");
        Assert.Equal("granted", State(Claim(rig, Owner(holder.Id, "h"), Owner(holder.Id, "h"), "holder@t")));
        Assert.Equal("held", State(Claim(rig, Owner(Me, "old"), Owner(Me, "old"), "old@t")));
        Assert.Equal("held", State(Claim(rig, Owner(Me, "new"), Owner(Me, "new"), "new@t")));

        // the holder's process ends
        holder.Kill();
        holder.WaitForExit();

        // nearest input that must not match: the younger waiter polls first and is still second in line
        var younger = Claim(rig, Owner(Me, "new"), Owner(Me, "new"), "new@t");
        Assert.Equal("held", State(younger));
        Assert.Equal(2, younger.Member("position")!.Value.AsNumber());

        Assert.Equal("granted", State(Claim(rig, Owner(Me, "old"), Owner(Me, "old"), "old@t")));
        Assert.Equal("old@t", HolderOf(rig));
        Assert.Equal(["new@t"], Ah.StateDb.StateDbModule.Resources(rig.AhHome).Where(static r => r.Kind == "wait").Select(static r => r.Who));
    }

    [Fact]
    public void AReleaseByANonHolderLeavesTheHoldAndForceEndsIt()
    {
        var rig = HierarchyReadOpsTests.Make();
        Assert.Equal("granted", State(Claim(rig, Owner(Me, "a"), Owner(Me, "a"), "a@t")));

        Assert.Equal("held", State(Release(rig, Owner(Me, "b"), force: false)));
        Assert.Equal("a@t", HolderOf(rig));

        Assert.Equal("released", State(Release(rig, Owner(Me, "b"), force: true)));
        Assert.Null(HolderOf(rig));
        Assert.Equal("free", State(Release(rig, Owner(Me, "b"), force: false)));
    }

    [Fact]
    public void TheStatusResourcesOfATeamOmitAHoldWhoseHolderIsDeadAndAWaiterOfAnotherTeam()
    {
        var rig = HierarchyReadOpsTests.Make();
        JsonValue ClaimOf(string name, JsonObject holder, string who) =>
            HierarchyReadOpsTests.Parsed(HierarchyReadOpsTests.Read(rig, "resources", "claim", new JsonObject { ["name"] = name, ["holder"] = holder, ["waiter"] = holder, ["who"] = who, ["queue"] = true }));
        using var gone = System.Diagnostics.Process.Start("/bin/sleep", "60");
        Assert.Equal("granted", State(ClaimOf("maya", Owner(gone.Id, "g"), "user")));
        Assert.Equal("granted", State(ClaimOf("blender", Owner(Me, "u"), "user")));
        Assert.Equal("held", State(ClaimOf("blender", Owner(Me, "e"), "ed@top")));
        Assert.Equal("held", State(ClaimOf("blender", Owner(Me, "x"), "ed@other")));
        gone.Kill();
        gone.WaitForExit();
        var lib = (HierLib)rig.Services.Status;
        var src = new StatusLaneOwnersTests.Source(
            new() { ["top"] = """{"orchestrator":{"pid":1,"session_id":"o-top"},"members":[{"name":"ed","role":"implementor"}]}""" },
            lib.LiveResourceJson);

        var status = HierLib.ComputeStatusFrom(src, "/d", 1_780_000_000_000, "/d");

        var top = status["teams"].AsArray().Single(static t => t.Member("team")?.AsString() == "top");
        var rows = top.Member("resources")!.Value.AsArray().Select(static r => $"{r.Member("name")!.Value.AsString()} {r.Member("role")!.Value.AsString()} {r.Member("who")!.Value.AsString()}");
        Assert.Equal(["blender hold user", "blender wait ed@top"], rows);
    }

    [Fact]
    public void AMembersWaitOnANameHeldOutsideTheTeamCarriesThatHoldMarkedOutside()
    {
        var rig = HierarchyReadOpsTests.Make();
        JsonValue ClaimOf(string name, string start, string who) =>
            HierarchyReadOpsTests.Parsed(HierarchyReadOpsTests.Read(rig, "resources", "claim", new JsonObject { ["name"] = name, ["holder"] = Owner(Me, start), ["waiter"] = Owner(Me, start), ["who"] = who, ["queue"] = true }));
        Assert.Equal("granted", State(ClaimOf("blender", "z", "zed@other")));
        Assert.Equal("held", State(ClaimOf("blender", "e", "ed@top")));

        // nearest input that must not match: a hold outside the team that no member waits on
        Assert.Equal("granted", State(ClaimOf("maya", "z2", "zed@other")));
        var src = new StatusLaneOwnersTests.Source(
            new() { ["top"] = """{"orchestrator":{"pid":1,"session_id":"o-top"},"members":[{"name":"ed","role":"implementor"}]}""" },
            ((HierLib)rig.Services.Status).LiveResourceJson);

        var status = HierLib.ComputeStatusFrom(src, "/d", 1_780_000_000_000, "/d");

        var top = status["teams"].AsArray().Single(static t => t.Member("team")?.AsString() == "top");
        var rows = top.Member("resources")!.Value.AsArray().Select(static r => $"{r.Member("name")!.Value.AsString()} {r.Member("role")!.Value.AsString()} {r.Member("who")!.Value.AsString()} {r.Member("outside")?.AsBool() == true}");
        Assert.Equal(["blender hold zed@other True", "blender wait ed@top False"], rows);
    }

    [Fact]
    public void ASecondClaimByTheHolderIsItsOwnAndLeavesOneHold()
    {
        var rig = HierarchyReadOpsTests.Make();
        Assert.Equal("granted", State(Claim(rig, Owner(Me, "a"), Owner(Me, "w1"), "a@t")));

        // R9: a new waiter process of the same holder (another Bash call of the same session)
        Assert.Equal("mine", State(Claim(rig, Owner(Me, "a"), Owner(Me, "w2"), "a@t")));

        var row = Assert.Single(Ah.StateDb.StateDbModule.Resources(rig.AhHome));
        Assert.Equal("hold", row.Kind);
        Assert.Equal("blender", row.Name);
    }
}
