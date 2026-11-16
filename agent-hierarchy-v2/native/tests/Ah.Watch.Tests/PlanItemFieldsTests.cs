using Ah.Core.Json;
using Ah.Hierarchy;
using Ah.Watch.Model;

namespace Ah.Watch.Tests;

/// <summary>The checklist fields a <c>plan-items:</c> item carries: kind and parent only when set, every other byte as before.</summary>
public sealed class PlanItemFieldsTests
{
    private static PlanItem Item() =>
        new("a", "Do a", "in_progress", "2026-10-01T00:00:00Z", null, null, [], null, null, null, Role: "implementor", Todo: "send it", Refs: ["r1"], UpdatedAt: "2026-10-02T00:00:00Z", StateSince: "2026-10-02T00:00:00Z");

    private static string Fields(PlanItem item)
    {
        var entry = new JsonObject { ["key"] = item.Item };
        ProgressLane.PlanFields(entry, item);
        return JsonText.Stringify(entry, 0);
    }

    [Theory]
    [InlineData("not_started", false, null, "next")]
    [InlineData("next", false, null, "next")]
    [InlineData("in_progress", false, "working", "progress")]
    [InlineData("in_progress", false, "no_one", "stuck")]
    [InlineData("in_progress", false, "stale", "stuck")]
    [InlineData("in_review", false, "with_lead", "review")]
    [InlineData("approved", false, "with_lead", "review")]
    [InlineData("waiting_orch", false, "with_lead", "review")]
    [InlineData("blocked", false, "blocked", "stuck")]
    [InlineData("waiting_user", false, "waiting_you", "waiting")]
    [InlineData("done", true, null, "done")]
    [InlineData("done", false, null, null)]
    [InlineData("dropped", true, null, null)]
    public void AnItemsColumnComesFromItsWork(string state, bool seen, string? work, string? column) =>
        Assert.Equal(column, PlanItemsLane.ColumnOf(state, seen, work));

    [Fact]
    public void AStaleItemCarriesItsQuietAtAndNegativeANoOneItemDoesNot()
    {
        var now = DateTimeOffset.Parse("2026-11-03T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeMilliseconds();
        JsonValue Placed(string at) => PlanItemsLane.Placed(
            JsonValue.FromObject(new JsonObject { ["stream"] = "s", ["items"] = new JsonArray { JsonValue.FromObject(new JsonObject { ["key"] = "a", ["state"] = "in_progress", ["updated_at"] = at }) }, ["truncated"] = false }), null, now).Member("items")!.Value.AsArray()[0];

        var stale = Placed("2026-11-01T00:00:00Z");
        Assert.Equal("stale", stale.Member("work")!.Value.AsString());
        Assert.Equal("2026-11-02T00:00:00.000Z", stale.Member("quiet_at")!.Value.AsString());
        var fresh = Placed("2026-11-02T12:00:00Z");
        Assert.Equal("no_one", fresh.Member("work")!.Value.AsString());
        Assert.Null(fresh.Member("quiet_at"));
    }

    [Theory]
    [InlineData("waiting_orch", 0)]
    [InlineData("blocked", 1)]
    [InlineData("waiting_user", 2)]
    [InlineData("in_progress", 3)]
    [InlineData("in_review", 4)]
    [InlineData("testing", 5)]
    [InlineData("approved", 6)]
    [InlineData("next", 7)]
    [InlineData("not_started", 7)]
    [InlineData("done", null)]
    [InlineData("dropped", null)]
    public void AnItemsChecklistRankIsForOpenItemsOnly(string state, int? rank) => Assert.Equal(rank, PlanItemsLane.ChecklistRank(state));

    [Fact]
    public void AnItemsOwnerIsItsHolderElseItsStreamsOwnerAndNegativeNoOwnerIsEmpty()
    {
        Assert.Equal("impl", ProgressLane.ItemOwner("impl", "orch", "in_progress"));
        Assert.Equal("orch", ProgressLane.ItemOwner(null, "orch", "in_progress"));
        Assert.Equal(string.Empty, ProgressLane.ItemOwner(null, JsonValue.Null, "in_progress"));
        Assert.Equal(string.Empty, ProgressLane.ItemOwner(null, string.Empty, "in_progress"));
        Assert.Equal("orch", ProgressLane.ItemOwner("—", "orch", "in_progress"));
        Assert.Equal(string.Empty, ProgressLane.ItemOwner("—", "—", "in_progress"));
    }

    [Theory]
    [InlineData("next")]
    [InlineData("not_started")]
    public void AnItemNobodyClaimedHasNoOwnerWhoeverOwnsItsStream(string state)
    {
        Assert.Equal(string.Empty, ProgressLane.ItemOwner(null, "impl-63", state));
        Assert.Equal(string.Empty, ProgressLane.ItemOwner("—", "impl-63", state));
    }

    [Theory]
    [InlineData("blocked")]
    [InlineData("in_review")]
    [InlineData("waiting_user")]
    public void NegativeAnItemPastClaimingStillFallsToItsStreamsOwner(string state) =>
        Assert.Equal("impl-63", ProgressLane.ItemOwner(null, "impl-63", state));

    [Fact]
    public void NegativeAnUnclaimedStateKeepsAHolderTheItemNames() =>
        Assert.Equal("impl", ProgressLane.ItemOwner("impl", "impl-63", "next"));

    [Fact]
    public void ThePlacedBodyServesColumnAndRankAndLeavesTheItemElseAlone()
    {
        var body = JsonValue.FromObject(new JsonObject
        {
            ["stream"] = "s",
            ["items"] = new JsonArray { JsonValue.FromObject(new JsonObject { ["key"] = "a", ["state"] = "in_progress", ["done_seen_at"] = 0 }), JsonValue.FromObject(new JsonObject { ["key"] = "b", ["state"] = "done", ["done_seen_at"] = 5 }) },
            ["truncated"] = false,
        });
        Assert.Equal(
            """{"stream":"s","items":[{"key":"a","state":"in_progress","done_seen_at":0,"work":"no_one","column":"stuck","checklist_rank":3},{"key":"b","state":"done","done_seen_at":5,"work":null,"column":"done","checklist_rank":null}],"truncated":false}""",
            JsonText.Stringify(PlanItemsLane.Placed(body, null, 0), 0));
    }

    [Fact]
    public void AnItemWithNoKindOrParentIsByteIdenticalToBefore() =>
        Assert.Equal(
            """{"key":"a","role":"implementor","todo":"send it","refs":["r1"],"state_since":"2026-10-02T00:00:00Z","updated_at":"2026-10-02T00:00:00Z"}""",
            Fields(Item()));

    [Fact]
    public void KindAndParentAreAddedWhenSet()
    {
        Assert.Equal(
            """{"key":"a","role":"implementor","todo":"send it","refs":["r1"],"state_since":"2026-10-02T00:00:00Z","updated_at":"2026-10-02T00:00:00Z","parent":"b12"}""",
            Fields(Item() with { Parent = "b12" }));
        Assert.EndsWith(""","kind":"deliverable","parent":"b3"}""", Fields(Item() with { Kind = "deliverable", Parent = "b3" }), StringComparison.Ordinal);
    }
}
