using System.Text;
using Ah.Core.Json;
using Ah.Hierarchy;
using Ah.Watch.Model;

namespace Ah.Watch.Tests;

/// <summary>What the board and the item panel are served from the work registry (ah-work-items S5a).</summary>
public sealed class ItemLogLaneTests
{
    private const string Dir = "/repo/.claude/ah2";
    private const string Other = "/other/.claude/ah2";

    private static string Id(string dir, long n) => "item-log:" + ActivityLane.DirId(dir) + "/" + n;

    private static ItemLogLane Lane(Func<string, bool> subscribed, Func<string, long, JsonObject?>? probe = null) =>
        new(static () => [Dir, Other], subscribed, probe ?? (static (_, n) => new JsonObject { ["n"] = n }));

    [Theory]
    [InlineData("1")]
    [InlineData("42")]
    public void AnItemsTokenNamesItsKey(string n) =>
        Assert.NotNull(Lane(static _ => true).KeyOfToken("item-log:" + ActivityLane.DirId(Dir) + "/" + n));

    [Theory]
    [InlineData("backlog:x")]
    [InlineData("item-log:")]
    [InlineData("item-log:nosuchdir/1")]
    public void AnotherTokenNamesNoKey(string token) => Assert.Null(Lane(static _ => true).KeyOfToken(token));

    [Theory]
    [InlineData("x")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1/2")]
    public void ABadNumberNamesNoKey(string n) => Assert.Null(Lane(static _ => true).KeyOfToken("item-log:" + ActivityLane.DirId(Dir) + "/" + n));

    [Fact]
    public void TheDescriptionLaneServesItsOwnSmallSliceAndNotTheLog()
    {
        var lane = new ItemLogLane(static () => [Dir], static _ => true, static (_, n) => new JsonObject { ["id"] = "w-" + n, ["description"] = "Why" }, ItemLogLane.DescName);
        var desc = "item-desc:" + ActivityLane.DirId(Dir) + "/3";

        Assert.Null(lane.KeyOfToken(Id(Dir, 3)));
        var change = lane.Reread(lane.KeyOfToken(desc)!, 0).Changes.Single();

        Assert.Equal((ItemLogLane.DescName, desc), (lane.Name, change.Id));
        Assert.Equal("{\"id\":\"w-3\",\"description\":\"Why\"}", Encoding.UTF8.GetString(change.Body!));
    }

    [Fact]
    public void AHeldItemServesThatItemsBodyAndAMissingItemServesNone()
    {
        var lane = Lane(static _ => true, static (_, n) => n == 1 ? new JsonObject { ["id"] = "w-1" } : null);

        var held = lane.Reread(lane.KeyOfToken(Id(Dir, 1))!, 0).Changes.Single();
        var missing = lane.Reread(lane.KeyOfToken(Id(Dir, 2))!, 0).Changes.Single();

        Assert.Equal(Id(Dir, 1), held.Id);
        Assert.Equal("{\"id\":\"w-1\"}", Encoding.UTF8.GetString(held.Body!));
        Assert.Equal("null", Encoding.UTF8.GetString(missing.Body!));
    }

    [Fact]
    public void AnItemDeletedWhileHeldServesTheNullBodyAndStaysHeld()
    {
        var present = true;
        var lane = Lane(static _ => true, (_, _) => present ? new JsonObject { ["id"] = "w-1" } : null);
        _ = lane.Reread(lane.KeyOfToken(Id(Dir, 1))!, 0);
        present = false;

        var result = lane.Reread(Dir, 0);

        Assert.Equal("null", Encoding.UTF8.GetString(result.Changes.Single().Body!));
        Assert.Empty(result.Evict);
        Assert.Equal([Id(Dir, 1)], lane.SliceIds(Dir));
    }

    [Fact]
    public void ADirectoryKeyRereadsEveryHeldItemOfThatDirectoryAndNoOther()
    {
        var lane = Lane(static _ => true);
        _ = lane.Reread(lane.KeyOfToken(Id(Dir, 1))!, 0);
        _ = lane.Reread(lane.KeyOfToken(Id(Dir, 2))!, 0);
        _ = lane.Reread(lane.KeyOfToken(Id(Other, 3))!, 0);

        var changed = lane.Reread(Dir, 0).Changes.Select(static c => c.Id).Order(StringComparer.Ordinal).ToList();

        Assert.Equal([Id(Dir, 1), Id(Dir, 2)], changed);
        Assert.Equal(changed, lane.SliceIds(Dir).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void AReleasedItemIsDroppedAndEvicted()
    {
        var live = true;
        var lane = Lane(_ => live);
        var key = lane.KeyOfToken(Id(Dir, 1))!;
        _ = lane.Reread(key, 0);
        live = false;

        var result = lane.Reread(Dir, 0);

        Assert.Empty(result.Changes);
        Assert.Equal([Id(Dir, 1)], result.Evict);
        Assert.Empty(lane.Keys());
    }

    private static readonly WorkCard Card = new("w-7", "LIN-1", "https://example.com/1", 2);

    private static PlanItem Item(string key) => new(key, "T", "not_started", "2026-11-01T00:00:00Z", null, null, [], null, null, null);

    private static JsonValue BacklogItem(BacklogFacts facts) => JsonValue.FromObject(BacklogLane.BodyOf(facts)).Member("items")!.Value.AsArray().Single();

    [Fact]
    public void ABacklogRowCarriesItsCardAndOneWithNoneCarriesNoField()
    {
        var facts = new BacklogFacts([Item("b1")], null, null);

        var with = BacklogItem(facts with { Work = new Dictionary<string, WorkCard> { ["b1"] = Card } });
        var without = BacklogItem(facts with { Work = new Dictionary<string, WorkCard> { ["b2"] = Card } });

        Assert.Equal("w-7", with.Member("work_id")!.Value.AsString());
        Assert.Equal(2, with.Member("blocked_by_open")!.Value.AsNumber());
        Assert.Equal("LIN-1", with.Member("ext_id")!.Value.AsString());
        Assert.Equal("https://example.com/1", with.Member("ext_url")!.Value.AsString());
        foreach (var field in new[] { "work_id", "blocked_by_open", "ext_id", "ext_url" })
        {
            Assert.Null(without.Member(field));
        }
    }

    [Fact]
    public void APlanItemCarriesItsCardAndWithoutACardItsBytesAreAsBefore()
    {
        JsonValue Body() => JsonValue.FromObject(new JsonObject { ["stream"] = "s", ["truncated"] = false, ["items"] = new JsonArray { JsonValue.FromObject(new JsonObject { ["key"] = "a", ["state"] = "next" }) } });

        var with = PlanItemsLane.Placed(Body(), null, 0, new Dictionary<string, WorkCard> { ["a"] = Card with { ExtId = null, ExtUrl = null } });
        var other = PlanItemsLane.Placed(Body(), null, 0, new Dictionary<string, WorkCard> { ["b"] = Card });
        var none = PlanItemsLane.Placed(Body(), null, 0);

        var item = with.Member("items")!.Value.AsArray().Single();
        Assert.Equal("w-7", item.Member("work_id")!.Value.AsString());
        Assert.Null(item.Member("ext_id"));
        Assert.Null(item.Member("ext_url"));
        Assert.Equal(JsonText.Stringify(none, 0), JsonText.Stringify(other, 0));
        Assert.DoesNotContain("work_id", JsonText.Stringify(none, 0), StringComparison.Ordinal);
    }
}
