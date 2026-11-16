using System.Text;
using Ah.Core.Json;
using Ah.Watch.Model;

namespace Ah.Watch.Tests;

/// <summary>The team's open-exchange list: only open exchanges, newest first, capped, with the member ends resolved by the same resolver as the members' last exchanges.</summary>
public sealed partial class TreeLanesTests
{
    private static string OpenListId => OpenExchangeListComposer.Prefix + ":" + D + "/default";

    private static string OpenEntry(string id, string? to, string? from) =>
        "{\"id\":\"exchange:" + D + "/" + id + "\",\"to_member\":" + Quoted(to) + ",\"from_member\":" + Quoted(from) + (to is null ? ",\"unattended_at\":\"2026-06-01T11:00:00Z\"" : string.Empty) + "}";

    private static string OpenBody(params string[] entries) =>
        "{\"team\":\"team:" + D + "/default\",\"open\":[" + string.Join(",", entries) + "]}";

    private static string Orch => MemberId(TreeIds.Orchestrator);

    private static RecordStore OpenRecords(int open, int closedNewer = 0)
    {
        var store = new RecordStore();
        store.Set(TeamLane.LaneName, TeamKey(), new LaneRecord(JsonText.Parse(Doc("so", "dev")), 1));
        for (var i = 0; i < open + closedNewer; i++)
        {
            var id = i.ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
            var isOpen = i < open ? "true" : "false";
            store.Set(MessagesLane.LaneName, MessagesLane.KeyOf(Canon, id), new LaneRecord(JsonText.Parse("{\"dir_id\":\"" + D + "\",\"id\":\"" + id + "\",\"team\":\"default\",\"to\":\"developer\",\"from\":\"orchestrator\",\"to_name\":\"dev\",\"from_name\":null,\"open\":" + isOpen + "}"), 1));
        }

        return store;
    }

    private static List<string> OpenIds(SliceChange change) =>
        [.. JsonText.Parse(System.Text.Encoding.UTF8.GetString(change.Body!)).Member("open")!.Value.AsArray().Select(static e => e.Member("id")!.Value.AsString())];

    [Fact]
    public void TheOpenListHoldsOnlyOpenExchangesOfTheTeamNewestFirstWithResolvedEnds()
    {
        var reader = Truth();
        reader.Exchanges["a"] = Msg("developer", "orchestrator", "dev", null, "default");
        reader.Exchanges["b"] = Msg("tester", "developer", "qa", "dev", "default", open: false);
        reader.Exchanges["c"] = Msg("developer", "orchestrator", "zed", null, null);
        reader.Exchanges["d"] = Msg("developer", "orchestrator", "dev", null, "other");
        var (model, _) = Seeded(reader);
        using var owner = model;

        Assert.Equal(OpenBody(OpenEntry("c", null, Orch), OpenEntry("a", MemberId("dev"), Orch)), Body(model, OpenListId));
        Assert.DoesNotContain("exchange:" + D + "/b", Body(model, OpenListId), StringComparison.Ordinal);
        Assert.DoesNotContain("exchange:" + D + "/d", Body(model, OpenListId), StringComparison.Ordinal);
        Assert.DoesNotContain(model.Store.Current.Slices.Keys, id => id.StartsWith("list:open:team:" + D + "/other", StringComparison.Ordinal));
        AssertEqualsRebuild(model, reader);
    }

    [Fact]
    public void AnOpenExchangeOlderThanTheMembersLastThreeStillAppears()
    {
        var reader = Truth();
        foreach (var id in new[] { "e1", "e2", "e3", "e4" })
        {
            reader.Exchanges[id] = Msg("developer", "orchestrator", "dev", null, "default");
        }

        var (model, _) = Seeded(reader);
        using var owner = model;

        Assert.DoesNotContain("exchange:" + D + "/e1", Body(model, MemberId("dev")), StringComparison.Ordinal);
        Assert.Contains("exchange:" + D + "/e1", Body(model, OpenListId), StringComparison.Ordinal);
    }

    [Fact]
    public void SettlingAnExchangeRemovesItFromTheOpenListInTheNextBatchAndEqualsARebuild()
    {
        var reader = Truth();
        reader.Exchanges["a"] = Msg("developer", "orchestrator", "dev", null, "default");
        reader.Exchanges["b"] = Msg("developer", "orchestrator", "dev", null, "default");
        var (model, clock) = Seeded(reader);
        using var owner = model;

        Send(model, clock, reader, "a", Msg("developer", "orchestrator", "dev", null, "default", open: false));
        Assert.Equal(OpenBody(OpenEntry("b", MemberId("dev"), Orch)), Body(model, OpenListId));
        AssertEqualsRebuild(model, reader);

        Send(model, clock, reader, "b", Msg("developer", "orchestrator", "dev", null, "default", open: false));
        Assert.DoesNotContain(OpenListId, model.Store.Current.Slices.Keys);
        Assert.Contains(ExchangesListId, model.Store.Current.Slices.Keys);
        AssertEqualsRebuild(model, reader);

        Send(model, clock, reader, "a", Msg("developer", "orchestrator", "dev", null, "default"));
        Assert.Equal(OpenBody(OpenEntry("a", MemberId("dev"), Orch)), Body(model, OpenListId));
        AssertEqualsRebuild(model, reader);
    }

    [Fact]
    public void ARosterChangeReResolvesTheOpenListAloneReadsNoMessagesAndEqualsARebuild()
    {
        var reader = Truth();
        reader.Exchanges["a"] = Msg("tester", "orchestrator", null, null, "default");
        var (model, clock) = Seeded(reader);
        using var owner = model;
        Assert.Equal(OpenBody(OpenEntry("a", MemberId("qa"), Orch)), Body(model, OpenListId));
        var exchangeVersion = VersionOf(model, "exchange:" + D + "/a");

        reader.Calls.Clear();
        reader.Teams[TeamPath] = Doc("so", "dev", "qa", "ops");
        model.Emit("teams", TeamKey());
        Drain(model, clock);

        Assert.Equal(OpenBody(OpenEntry("a", null, Orch)), Body(model, OpenListId));
        Assert.Equal(exchangeVersion, VersionOf(model, "exchange:" + D + "/a"));
        Assert.DoesNotContain(reader.Calls.Keys, static k => k.StartsWith("msgs.", StringComparison.Ordinal));
        AssertEqualsRebuild(model, reader);

        reader.Teams[TeamPath] = Doc("so", "dev", "qa");
        model.Emit("teams", TeamKey());
        Drain(model, clock);
        Assert.Equal(OpenBody(OpenEntry("a", MemberId("qa"), Orch)), Body(model, OpenListId));
        AssertEqualsRebuild(model, reader);
    }

    [Fact]
    public void AnOpenExchangeEventReadsOneExchangeAndAnIdleModelPollsNothing()
    {
        var reader = Truth();
        var (model, clock) = Seeded(reader);
        using var owner = model;
        reader.Calls.Clear();

        Send(model, clock, reader, "a", Msg("developer", "orchestrator", "dev", null, "default"));

        Assert.Equal(1, reader.Calls.GetValueOrDefault("msgs.read"));
        Assert.Equal(1, reader.Total);
        Assert.Equal(0, model.Counters.Polls);
        Assert.Equal(OpenBody(OpenEntry("a", MemberId("dev"), Orch)), Body(model, OpenListId));
    }

    [Theory]
    [InlineData(500, 0, 500, "000499")]
    [InlineData(501, 0, 500, "000500")]
    [InlineData(500, 3, 500, "000499")]
    public void TheOpenListHoldsAtMostFiveHundredOfTheNewestOpenExchanges(int open, int closedNewer, int expected, string newest)
    {
        var composer = new OpenExchangeListComposer();

        var ids = OpenIds(composer.Compose(OpenListId, OpenRecords(open, closedNewer)));

        Assert.Equal(expected, ids.Count);
        Assert.Equal("exchange:" + D + "/" + newest, ids[0]);
        Assert.Equal("exchange:" + D + "/" + (open - expected).ToString("D6", System.Globalization.CultureInfo.InvariantCulture), ids[^1]);
        Assert.DoesNotContain(ids, static i => i.EndsWith("/000501", StringComparison.Ordinal) || i.EndsWith("/000502", StringComparison.Ordinal));
    }

    [Fact]
    public void AnEndThatResolvesToNobodyIsNull()
    {
        var reader = Truth();
        reader.Teams[TeamPath] = Doc("so", "dev", "qa", "ops");
        reader.Exchanges["a"] = Msg("developer", "orchestrator", "ghost", null, "default");
        reader.Exchanges["b"] = Msg("reviewer", "tester", null, null, "default");
        reader.Exchanges["c"] = Msg("tester", "developer", null, null, "default");
        var (model, _) = Seeded(reader);
        using var owner = model;

        Assert.Equal(
            OpenBody(OpenEntry("c", null, MemberId("dev")), OpenEntry("b", null, null), OpenEntry("a", null, Orch)),
            Body(model, OpenListId));
    }

    [Fact]
    public void TheOpenListEndsAreTheMembersTheLastExchangesFileTheExchangeUnder()
    {
        var reader = Truth();
        reader.Teams[TeamPath] = Doc("so", "dev", "qa", "ops");
        reader.Exchanges["a"] = Msg("developer", "orchestrator", null, null, "default");
        reader.Exchanges["b"] = Msg("tester", "developer", "qa", "dev", "default");
        reader.Exchanges["c"] = Msg("tester", "orchestrator", null, null, "default");
        reader.Exchanges["d"] = Msg("developer", "orchestrator", "ops", null, "default");
        var (model, _) = Seeded(reader);
        using var owner = model;
        var open = JsonText.Parse(Body(model, OpenListId)).Member("open")!.Value.AsArray();

        foreach (var entry in open)
        {
            var id = entry.Member("id")!.Value.AsString();
            foreach (var member in new[] { "dev", "qa", "ops", TreeIds.Orchestrator })
            {
                var named = entry.Member("to_member")!.Value is { IsText: true } to && to.AsString() == MemberId(member)
                    || entry.Member("from_member")!.Value is { IsText: true } from && from.AsString() == MemberId(member);
                Assert.Equal(named, Body(model, MemberId(member)).Contains(id, StringComparison.Ordinal));
            }
        }
    }

    [Fact]
    public void TheExchangesViewManifestListsTheOpenListAndTheMembersViewDoesNot()
    {
        var reader = Truth();
        reader.Exchanges["a"] = Msg("developer", "orchestrator", "dev", null, "default");
        var (model, _) = Seeded(reader);
        using var owner = model;

        Assert.Contains(OpenListId, Encoding.UTF8.GetString(model.Manifest("exchanges")!), StringComparison.Ordinal);
        Assert.DoesNotContain(OpenListId, Encoding.UTF8.GetString(model.Manifest("members") ?? []), StringComparison.Ordinal);
        Assert.Contains("list:open:", ModelViews.PrefixesOf("exchanges")!);
        Assert.DoesNotContain("list:open:", ModelViews.PrefixesOf("members")!);
    }

    [Fact]
    public void OneComposerRegistersTheOpenListAndItsDependencyRowsExist()
    {
        Assert.Single(TreeModel.Composers().Composers, static c => c.SlicePrefix == OpenExchangeListComposer.Prefix);
        Assert.Equal(["messages", "teams"], SliceDependencies.FedBy("list:open:team:<t>").Order(StringComparer.Ordinal));
    }

    [Fact]
    public void AnOpenListIdOfAnUnknownOrMalformedTeamComposesNoSlice()
    {
        var composer = new OpenExchangeListComposer();
        var records = OpenRecords(2);

        Assert.Null(composer.Compose(OpenExchangeListComposer.Prefix + ":" + D + "/other", records).Body);
        Assert.Null(composer.Compose(OpenExchangeListComposer.Prefix + ":" + D + "/", records).Body);
        Assert.NotNull(composer.Compose(OpenListId, records).Body);
    }

    [Fact]
    public void TheOpenListSlicesMatchTheGolden()
    {
        var reader = Truth();
        reader.Teams[TeamPath] = Doc("so", "dev", "qa", "ops");
        reader.Exchanges["a"] = Msg("developer", "orchestrator", "dev", null, "default");
        reader.Exchanges["b"] = Msg("tester", "developer", "qa", "dev", "default", open: false);
        reader.Exchanges["c"] = Msg("developer", "orchestrator", "ghost", null, null);
        reader.Exchanges["d"] = Msg("tester", "orchestrator", null, null, "default");
        var (model, _) = Seeded(reader);
        using var owner = model;

        var slices = new JsonObject();
        foreach (var id in model.Store.Current.Slices.Keys.Where(static k => k.StartsWith("list:open:team:", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            slices[id] = JsonText.Parse(Body(model, id));
        }

        ModelGolden.Check(new JsonObject { ["slices"] = JsonValue.FromObject(slices) }, "tree-open-exchanges");
    }
}
