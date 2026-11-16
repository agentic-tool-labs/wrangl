using Ah.Core.Json;
using Ah.Hierarchy;
using Ah.Watch.Model;

namespace Ah.Watch.Tests;

/// <summary>The <c>backlog:&lt;dirId&gt;</c> slice: its shape, a re-read after a write, the timed refresh, and an empty backlog.</summary>
public sealed class BacklogLaneTests
{
    private const string Dir = "/repo/.claude/ah2";

    [Fact]
    public void AFailingPollIsSkippedAndLoggedOnceUntilOneSucceeds()
    {
        var failing = true;
        var logged = new List<string>();
        using var poller = new ClientGatedPoller(() => true, TimeSpan.FromHours(1), () => { if (failing) { throw new InvalidOperationException("busy"); } }, log: logged.Add);

        poller.PollNow();
        poller.PollNow();
        Assert.Equal(2, poller.Faults);
        Assert.Contains("busy", Assert.Single(logged), StringComparison.Ordinal);

        failing = false;
        poller.PollNow();
        Assert.Single(logged);

        failing = true;
        poller.PollNow();
        Assert.Equal(2, logged.Count);
    }

    [Fact]
    public void NegativeAPollThatSucceedsLogsNothing()
    {
        var logged = new List<string>();
        using var poller = new ClientGatedPoller(() => true, TimeSpan.FromHours(1), static () => { }, log: logged.Add);

        poller.PollNow();

        Assert.Empty(logged);
    }

    private static readonly string SliceId = "backlog:" + ActivityLane.DirId(Dir);

    private static PlanItem Item(string id, string title, string state = "open", string? wave = null, int? rank = null, string? holder = null, int? issue = null) =>
        new(id, title, state, "2026-10-01T00:00:00Z", null, null, [], null, issue, null, Rank: rank, Holder: holder, Wave: wave);

    private static BacklogHealth Health(int untriaged, int stale, bool due, long? dueSinceMs) =>
        new(new Dictionary<string, IReadOnlyDictionary<string, int>>(), untriaged, stale, null, null, due, dueSinceMs, "r", null, false);

    private static JsonObject Body(BacklogLane lane)
    {
        var change = Assert.Single(lane.Reread(Dir, 0).Changes);
        Assert.Equal(SliceId, change.Id);
        return JsonText.Parse(change.Body!).AsObject();
    }

    [Fact]
    public void AHeldSliceServesTheItemsAndHealthAndAWriteChangesIt()
    {
        var facts = new BacklogFacts(
            [Item("b1", "First", wave: "now", rank: 1, holder: "impl", issue: 7), Item("b2", "Second")],
            Health(2, 1, true, 1_700_000_000_000),
            "https://github.com/o/r.git");
        var lane = new BacklogLane(() => [Dir], id => id == SliceId, (_, _) => facts);

        Assert.Equal(Dir, lane.KeyOfToken(SliceId));
        var body = Body(lane);
        var items = body.Get("items")!.Value.AsArray();
        Assert.Equal(
            """{"key":"b1","title":"First","state":"open","open":true,"wave":"now","planned":false,"holder":"impl","rank":1,"issue":{"url":"https://github.com/o/r/issues/7"}}""",
            JsonText.Stringify(items[0], 0));
        Assert.Equal("""{"key":"b2","title":"Second","state":"open","open":true,"wave":"untriaged","planned":false}""", JsonText.Stringify(items[1], 0));
        var health = body.Get("health")!.Value.AsObject();
        Assert.Equal(2, health.Get("untriaged")!.Value.AsNumber());
        Assert.Equal(1, health.Get("stale")!.Value.AsNumber());
        Assert.True(health.Get("due")!.Value.AsBool());
        Assert.True(health.Get("due_since")!.Value.IsText);
        Assert.Null(health.Get("last_groom_at"));

        facts = facts with { Items = [Item("b1", "First", state: "done", wave: "now", rank: 1)] };
        var after = Body(lane).Get("items")!.Value.AsArray();
        Assert.Equal("done", Assert.Single(after).Member("state")!.Value.AsString());
    }

    [Fact]
    public void KindAndParentAreServedWhenSetAndOmittedWhenNot()
    {
        var facts = new BacklogFacts(
            [Item("b1", "Epic") with { Kind = "epic" }, Item("b2", "Deliverable") with { Kind = "deliverable", Parent = "b1" }, Item("b3", "Task") with { Parent = "b2" }, Item("b4", "Plain")],
            null,
            null);
        var lane = new BacklogLane(() => [Dir], id => id == SliceId, (_, _) => facts);

        var items = Body(lane).Get("items")!.Value.AsArray();
        Assert.Equal("""{"key":"b1","title":"Epic","state":"open","open":true,"wave":"untriaged","planned":false,"kind":"epic"}""", JsonText.Stringify(items[0], 0));
        Assert.Equal("""{"key":"b2","title":"Deliverable","state":"open","open":true,"wave":"untriaged","planned":false,"kind":"deliverable","parent":"b1"}""", JsonText.Stringify(items[1], 0));
        Assert.Equal("""{"key":"b3","title":"Task","state":"open","open":true,"wave":"untriaged","planned":false,"parent":"b2"}""", JsonText.Stringify(items[2], 0));
        Assert.Equal("""{"key":"b4","title":"Plain","state":"open","open":true,"wave":"untriaged","planned":false}""", JsonText.Stringify(items[3], 0));
    }

    [Fact]
    public void ARowAnOpenItemNamesAndItsAncestorsArePlannedAndNegativeItsSiblingAndChildAreNot()
    {
        var facts = new BacklogFacts(
            [Item("root", "Root"), Item("mid", "Mid") with { Parent = "root" }, Item("leaf", "Leaf") with { Parent = "mid" }, Item("other", "Other") with { Parent = "root" }],
            null,
            null) { PlannedParents = ["mid", "not-in-this-backlog"] };
        var planned = BacklogLane.BodyOf(facts).Get("items")!.Value.AsArray().Where(static r => r.Member("planned")!.Value.AsBool()).Select(static r => r.Member("key")!.Value.AsString());
        Assert.Equal(["mid", "root"], planned.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ACycleInTheParentsEnds()
    {
        var facts = new BacklogFacts(
            [Item("a", "A") with { Parent = "b" }, Item("b", "B") with { Parent = "a" }],
            null,
            null) { PlannedParents = ["a"] };
        Assert.Equal(2, BacklogLane.BodyOf(facts).Get("items")!.Value.AsArray().Count(static r => r.Member("planned")!.Value.AsBool()));
    }

    [Fact]
    public void RowsComeInDisplayOrderAndADoneOneIsNotOpen()
    {
        var facts = new BacklogFacts(
            [Item("b10", "Ten", rank: 2), Item("B9", "Nine", rank: 2), Item("b1", "Unranked"), Item("b3", "Done", state: "done", rank: 1), Item("b4", "Dropped", state: "dropped")],
            null,
            null);
        var body = Body(new BacklogLane(() => [Dir], id => id == SliceId, (_, _) => facts));
        Assert.Equal(["b3", "B9", "b10", "b1", "b4"], body.Get("items")!.Value.AsArray().Select(static i => i.Member("key")!.Value.AsString()));
        Assert.Equal(3, body.Get("open")!.Value.AsNumber());
        Assert.Equal(5, body.Get("total")!.Value.AsNumber());
    }

    [Theory]
    [InlineData("now", "now")]
    [InlineData("NEXT", "next")]
    [InlineData("Later", "later")]
    [InlineData("nowish", "untriaged")]
    [InlineData("", "untriaged")]
    [InlineData(null, "untriaged")]
    public void AWaveIsOneOfTheThreeElseUntriaged(string? wave, string expected) => Assert.Equal(expected, BacklogLane.WaveOf(wave));

    [Fact]
    public void ADirWithNoBacklogServesNoItemsAndZeroHealth()
    {
        var lane = new BacklogLane(() => [Dir], id => id == SliceId, (_, _) => new BacklogFacts([], null, null));

        Assert.Equal("""{"items":[],"open":0,"total":0,"health":{"untriaged":0,"stale":0,"due":false}}""", JsonText.Stringify(JsonValue.FromObject(Body(lane)), 0));
    }

    [Fact]
    public void TheRefreshPollerReEmitsAHeldBacklogWhileAClientIsConnectedAndNothingOtherwise()
    {
        var held = true;
        var lane = new BacklogLane(() => [Dir], id => held && id == SliceId, (_, _) => new BacklogFacts([], null, null));
        var emitted = new List<(string Lane, string Key)>();
        var connected = false;
        using var poller = new ClientGatedPoller(() => connected, BacklogLane.RefreshPeriod, () => lane.EmitHeld((l, k) => emitted.Add((l, k))));

        poller.PollNow();
        Assert.Empty(emitted);

        connected = true;
        poller.PollNow();
        Assert.Empty(emitted);

        _ = lane.Reread(Dir, 0);
        poller.PollNow();
        Assert.Equal([("backlog", Dir)], emitted);

        held = false;
        _ = lane.Reread(Dir, 0);
        poller.PollNow();
        Assert.Single(emitted);
    }

    [Fact]
    public void AnUnknownOrEmptyDirIdNamesNoKeyAndAnUnheldSliceIsEvicted()
    {
        var lane = new BacklogLane(() => [Dir], _ => false, (_, _) => new BacklogFacts([], null, null));

        Assert.Null(lane.KeyOfToken("backlog:00000000"));
        Assert.Null(lane.KeyOfToken("backlog:"));
        Assert.Null(lane.KeyOfToken("jobs:" + ActivityLane.DirId(Dir)));
        var result = lane.Reread(Dir, 0);
        Assert.Empty(result.Changes);
        Assert.Equal([SliceId], result.Evict);
    }
}
