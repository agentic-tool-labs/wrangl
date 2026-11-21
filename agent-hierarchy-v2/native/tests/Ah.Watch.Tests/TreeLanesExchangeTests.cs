using Ah.Core.Json;
using Ah.Watch.Model;

namespace Ah.Watch.Tests;

/// <summary>The team-keyed exchange lists and the member last three: filing by team and party, projection equals rebuild across the messages and teams lanes, the cap, one composer for both prefixes, and the read and idle ceilings.</summary>
public sealed partial class TreeLanesTests
{
    private static string ExchangesListId => TeamExchangeListComposer.ExchangesPrefix + ":" + D + "/default";

    private static string FeedListId => TeamExchangeListComposer.FeedPrefix + ":" + D + "/default";

    private static string MemberId(string member) => "member:" + D + "/default/" + member;

    private static string Quoted(string? text) => text is null ? "null" : "\"" + text + "\"";

    private static string Msg(string to, string from, string? toName, string? fromName, string? team, bool open = true) =>
        "{\"slug\":\"s\",\"to\":\"" + to + "\",\"from\":\"" + from + "\",\"to_name\":" + Quoted(toName) + ",\"from_name\":" + Quoted(fromName)
        + ",\"team\":" + Quoted(team) + ",\"created\":\"2026-06-01T11:00:00Z\",\"has_request\":true,\"has_response\":" + (open ? "false" : "true") + "}";

    private static void Send(ReadModel model, Clock clock, TreeReader reader, string id, string json)
    {
        reader.Exchanges[id] = json;
        model.Emit("messages", MessagesLane.KeyOf(Canon, id));
        Drain(model, clock);
    }

    private static void Retract(ReadModel model, Clock clock, TreeReader reader, string id)
    {
        _ = reader.Exchanges.Remove(id);
        model.Emit("messages", MessagesLane.KeyOf(Canon, id));
        Drain(model, clock);
    }

    // The ids part of a team exchange list; its pair counts are held by the pairs tests.
    private static string IdsOnly(string body)
    {
        var list = JsonText.Parse(body);
        return System.Text.Encoding.UTF8.GetString(JsonText.ToUtf8(JsonValue.FromObject(new JsonObject { ["team"] = list.Member("team")!.Value, ["ids"] = list.Member("ids")!.Value }), 0));
    }

    private static string ListBody(string team, params string[] ids) =>
        "{\"team\":\"team:" + D + "/" + team + "\",\"ids\":[" + string.Join(",", ids.Select(static i => "\"" + i + "\"")) + "]}";

    private static string Last(params string[] ids) =>
        "\"last_exchanges\":[" + string.Join(",", ids.Select(static i => "\"exchange:" + D + "/" + i + "\"")) + "]";

    [Fact]
    public void TheTeamListsAndTheMemberLastExchangesFileExchangesByTeamAndParty()
    {
        var reader = Truth();
        reader.Exchanges["a"] = Msg("developer", "orchestrator", "dev", null, "default");
        reader.Exchanges["b"] = Msg("tester", "developer", "qa", "dev", "default");
        reader.Exchanges["c"] = Msg("developer", "orchestrator", "dev", null, null);
        reader.Exchanges["d"] = Msg("developer", "orchestrator", "dev", null, "other");
        var (model, _) = Seeded(reader);
        using var owner = model;

        Assert.Equal(ListBody("default", "c", "b", "a"), IdsOnly(Body(model, ExchangesListId)));
        Assert.Equal(Body(model, ExchangesListId), Body(model, FeedListId));
        Assert.Contains(Last("c", "b", "a"), Body(model, MemberId("dev")), StringComparison.Ordinal);
        Assert.Contains(Last("b"), Body(model, MemberId("qa")), StringComparison.Ordinal);
        Assert.Contains(Last("c", "a"), Body(model, MemberId(TreeIds.Orchestrator)), StringComparison.Ordinal);
        Assert.DoesNotContain("exchange:" + D + "/d", Body(model, MemberId("dev")), StringComparison.Ordinal);
        Assert.Contains("exchange:" + D + "/c", model.Store.Current.Slices.Keys);
        Assert.DoesNotContain(model.Store.Current.Slices.Keys, id => id.StartsWith("list:exchanges:team:" + D + "/other", StringComparison.Ordinal));
    }

    [Fact]
    public void AnExchangeWithNoTeamIsTheDefaultTeamsWhenTheDirectoryHasOneAndNobodysWhenItHasNot()
    {
        var withDefault = Truth();
        withDefault.Exchanges["a"] = Msg("developer", "orchestrator", "dev", null, null);
        var (model, _) = Seeded(withDefault);
        using var owner = model;
        Assert.Equal(ListBody("default", "a"), IdsOnly(Body(model, ExchangesListId)));
        Assert.Contains(Last("a"), Body(model, MemberId("dev")), StringComparison.Ordinal);

        var noDefault = Truth();
        _ = noDefault.Teams.Remove(TeamPath);
        noDefault.Teams[AliasPath] = Doc("so2", "dev");
        noDefault.Exchanges["a"] = Msg("developer", "orchestrator", "dev", null, null);
        var (other, _) = Seeded(noDefault);
        using var otherOwner = other;
        Assert.DoesNotContain(other.Store.Current.Slices.Keys, static id => id.StartsWith("list:exchanges:team:", StringComparison.Ordinal));
        Assert.DoesNotContain(other.Store.Current.Slices.Keys, static id => id.StartsWith("list:feed:team:", StringComparison.Ordinal));
        Assert.Contains(Last(), Body(other, "member:" + D + "/rel/dev"), StringComparison.Ordinal);
    }

    [Fact]
    public void AnExchangeNamingATeamIsThatTeamsAndNotTheDefaultTeams()
    {
        var reader = Truth();
        reader.Teams[AliasPath] = Doc("so2", "dev");
        reader.Exchanges["a"] = Msg("developer", "orchestrator", "dev", null, "rel");
        reader.Exchanges["b"] = Msg("developer", "orchestrator", "dev", null, null);
        var (model, _) = Seeded(reader);
        using var owner = model;

        Assert.Equal(ListBody("rel", "a"), IdsOnly(Body(model, TeamExchangeListComposer.ExchangesPrefix + ":" + D + "/rel")));
        Assert.Equal(ListBody("default", "b"), IdsOnly(Body(model, ExchangesListId)));
        Assert.Contains(Last("a"), Body(model, "member:" + D + "/rel/dev"), StringComparison.Ordinal);
        Assert.Contains(Last("b"), Body(model, MemberId("dev")), StringComparison.Ordinal);
    }

    [Fact]
    public void TheLastExchangesAreTheThreeNewestOfTheMember()
    {
        var reader = Truth();
        foreach (var id in new[] { "e1", "e2", "e3", "e4" })
        {
            reader.Exchanges[id] = Msg("developer", "orchestrator", "dev", null, "default");
        }

        var (model, _) = Seeded(reader);
        using var owner = model;

        Assert.Contains(Last("e4", "e3", "e2"), Body(model, MemberId("dev")), StringComparison.Ordinal);
        Assert.DoesNotContain("exchange:" + D + "/e1", Body(model, MemberId("dev")), StringComparison.Ordinal);
        Assert.Equal(ListBody("default", "e4", "e3", "e2", "e1"), IdsOnly(Body(model, ExchangesListId)));
    }

    [Fact]
    public void AnOrchestratorRoleNamedOrchestratorIsTheOrchestratorOneNamingAMemberIsThatMemberAndARealMemberNamedOrchestratorIsItsOwnMember()
    {
        var reader = Truth();
        reader.Teams[TeamPath] = Doc("so", "dev", "orchestrator");
        reader.Exchanges["p"] = Msg("developer", "orchestrator", "dev", "orchestrator", "default");
        reader.Exchanges["q"] = Msg("tester", "developer", "orchestrator", "dev", "default");
        reader.Exchanges["r"] = Msg("reviewer", "orchestrator", null, "dev", "default");
        var (model, _) = Seeded(reader);
        using var owner = model;

        // ah2-367 Amendment 4: r's orchestrator end names the member dev, so it is dev's (was the orchestrator's whatever its name)
        Assert.Contains(Last("p"), Body(model, MemberId(TreeIds.Orchestrator)), StringComparison.Ordinal);
        Assert.Contains(Last("q"), Body(model, MemberId(TreeIds.ReservedName)), StringComparison.Ordinal);
        Assert.Contains(Last("r", "q", "p"), Body(model, MemberId("dev")), StringComparison.Ordinal);
    }

    [Fact]
    public void ATeamDocumentAppearingOrGoneRefilesTheListsAndEqualsARebuild()
    {
        var reader = Truth();
        reader.Exchanges["a"] = Msg("developer", "orchestrator", "dev", null, "default");
        var (model, clock) = Seeded(reader);
        using var owner = model;
        Assert.Contains(ExchangesListId, model.Store.Current.Slices.Keys);

        _ = reader.Teams.Remove(TeamPath);
        model.Emit("teams", TeamKey());
        Drain(model, clock);
        Assert.DoesNotContain(ExchangesListId, model.Store.Current.Slices.Keys);
        Assert.DoesNotContain(FeedListId, model.Store.Current.Slices.Keys);
        AssertEqualsRebuild(model, reader);

        reader.Teams[TeamPath] = Doc("so", "dev", "qa");
        model.Emit("teams", TeamKey());
        Drain(model, clock);
        Assert.Equal(ListBody("default", "a"), IdsOnly(Body(model, FeedListId)));
        AssertEqualsRebuild(model, reader);
    }

    [Fact]
    public void AMemberAddedToTheTeamFilesItsExistingExchangesAndEqualsARebuild()
    {
        var reader = Truth();
        reader.Exchanges["a"] = Msg("developer", "orchestrator", "ops", null, "default");
        var (model, clock) = Seeded(reader);
        using var owner = model;
        Assert.DoesNotContain(MemberId("ops"), model.Store.Current.Slices.Keys);

        reader.Teams[TeamPath] = Doc("so", "dev", "qa", "ops");
        model.Emit("teams", TeamKey());
        Drain(model, clock);

        Assert.Contains(Last("a"), Body(model, MemberId("ops")), StringComparison.Ordinal);
        Assert.Contains(Last(), Body(model, MemberId("dev")), StringComparison.Ordinal);
        AssertEqualsRebuild(model, reader);
    }

    [Fact]
    public void AnExchangeSentAndThenGoneUpdatesTheListsAndTheMembersAndEqualsARebuild()
    {
        var reader = Truth();
        var (model, clock) = Seeded(reader);
        using var owner = model;
        Assert.DoesNotContain(ExchangesListId, model.Store.Current.Slices.Keys);

        Send(model, clock, reader, "a", Msg("developer", "orchestrator", "dev", null, "default"));
        Assert.Equal(ListBody("default", "a"), IdsOnly(Body(model, ExchangesListId)));
        Assert.Contains(Last("a"), Body(model, MemberId("dev")), StringComparison.Ordinal);
        AssertEqualsRebuild(model, reader);

        Send(model, clock, reader, "a", Msg("developer", "orchestrator", "qa", null, "default"));
        Assert.Contains(Last(), Body(model, MemberId("dev")), StringComparison.Ordinal);
        Assert.Contains(Last("a"), Body(model, MemberId("qa")), StringComparison.Ordinal);
        AssertEqualsRebuild(model, reader);

        Retract(model, clock, reader, "a");
        Assert.DoesNotContain(ExchangesListId, model.Store.Current.Slices.Keys);
        Assert.Contains(Last(), Body(model, MemberId("qa")), StringComparison.Ordinal);
        AssertEqualsRebuild(model, reader);
    }

    [Fact]
    public void ATeamFileRenamedToAnotherAliasMovesTheListAndEqualsARebuild()
    {
        var reader = Truth();
        reader.Teams[AliasPath] = Doc("so2", "dev");
        reader.Exchanges["a"] = Msg("developer", "orchestrator", "dev", null, "rel");
        var (model, clock) = Seeded(reader);
        using var owner = model;
        var relList = TeamExchangeListComposer.ExchangesPrefix + ":" + D + "/rel";
        Assert.Equal(ListBody("rel", "a"), IdsOnly(Body(model, relList)));

        var renamed = Canon + "/teams/rel2.json";
        reader.Teams[renamed] = reader.Teams[AliasPath];
        _ = reader.Teams.Remove(AliasPath);
        model.Emit("teams", TeamKey(AliasPath));
        model.Emit("teams", TeamKey(renamed));
        Drain(model, clock);

        Assert.DoesNotContain(relList, model.Store.Current.Slices.Keys);
        Assert.DoesNotContain(model.Store.Current.Slices.Keys, id => id.StartsWith("list:exchanges:team:" + D + "/rel2", StringComparison.Ordinal));
        AssertEqualsRebuild(model, reader);
    }

    private static RecordStore ListRecords(int count)
    {
        var store = new RecordStore();
        store.Set(TeamLane.LaneName, TeamKey(), new LaneRecord(JsonText.Parse(Doc("so", "dev")), 1));
        for (var i = 0; i < count; i++)
        {
            var id = i.ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
            store.Set(MessagesLane.LaneName, MessagesLane.KeyOf(Canon, id), new LaneRecord(JsonText.Parse("{\"dir_id\":\"" + D + "\",\"id\":\"" + id + "\",\"team\":\"default\",\"to\":\"developer\",\"from\":\"orchestrator\",\"to_name\":\"dev\",\"from_name\":null}"), 1));
        }

        return store;
    }

    [Theory]
    [InlineData(5000, 5000, "000000")]
    [InlineData(5001, 5000, "000001")]
    public void ATeamListHoldsAtMostFiveThousandOfTheNewestExchanges(int exchanges, int expected, string oldestKept)
    {
        var composer = new TeamExchangeListComposer(TeamExchangeListComposer.ExchangesPrefix);

        var change = composer.Compose(ExchangesListId, ListRecords(exchanges));

        var ids = JsonText.Parse(System.Text.Encoding.UTF8.GetString(change.Body!)).Member("ids")!.Value.AsArray().Select(static i => i.AsString()).ToList();
        Assert.Equal(expected, ids.Count);
        Assert.Equal((exchanges - 1).ToString("D6", System.Globalization.CultureInfo.InvariantCulture), ids[0]);
        Assert.Equal(oldestKept, ids[^1]);
        if (exchanges > expected)
        {
            Assert.DoesNotContain("000000", ids);
        }
    }

    [Fact]
    public void OneComposerClassServesBothTheExchangesAndTheFeedPrefix()
    {
        var composers = TreeModel.Composers().Composers.Where(static c => c.SlicePrefix is TeamExchangeListComposer.ExchangesPrefix or TeamExchangeListComposer.FeedPrefix).ToList();
        Assert.Equal(2, composers.Count);
        Assert.All(composers, static c => Assert.IsType<TeamExchangeListComposer>(c));

        var records = ListRecords(3);
        var exchanges = composers.Single(static c => c.SlicePrefix == TeamExchangeListComposer.ExchangesPrefix).Compose(ExchangesListId, records);
        var feed = composers.Single(static c => c.SlicePrefix == TeamExchangeListComposer.FeedPrefix).Compose(FeedListId, records);
        Assert.NotNull(exchanges.Body);
        Assert.Equal(exchanges.Body, feed.Body);
        Assert.Equal(exchanges.AsOfMs, feed.AsOfMs);
    }

    [Fact]
    public void AnExchangeChangeAffectsOnlyTheListsOfTeamsTheDirectoryHas()
    {
        var composer = new TeamExchangeListComposer(TeamExchangeListComposer.ExchangesPrefix);
        var records = ListRecords(1);
        LaneRecord Exchange(string? team) => new(JsonText.Parse("{\"dir_id\":\"" + D + "\",\"id\":\"x\",\"team\":" + Quoted(team) + "}"), 1);
        var key = new LaneKey(MessagesLane.LaneName, MessagesLane.KeyOf(Canon, "x"));

        Assert.Equal([ExchangesListId], composer.Affected(key, null, Exchange(null), records));
        Assert.Equal([ExchangesListId], composer.Affected(key, null, Exchange("default"), records));
        Assert.Empty(composer.Affected(key, null, Exchange("other"), records));
        Assert.Equal([ExchangesListId], composer.Affected(key, Exchange("other"), Exchange(null), records));

        var noDefault = new RecordStore();
        noDefault.Set(TeamLane.LaneName, TeamKey(AliasPath), new LaneRecord(JsonText.Parse(Doc("so", "dev")), 1));
        Assert.Empty(composer.Affected(key, null, Exchange(null), noDefault));
    }

    [Fact]
    public void AListIdOfAnUnknownOrMalformedTeamComposesNoSlice()
    {
        var composer = new TeamExchangeListComposer(TeamExchangeListComposer.ExchangesPrefix);
        var records = ListRecords(2);

        Assert.Null(composer.Compose(TeamExchangeListComposer.ExchangesPrefix + ":" + D + "/other", records).Body);
        Assert.Null(composer.Compose(TeamExchangeListComposer.ExchangesPrefix + ":" + D, records).Body);
        Assert.Null(composer.Compose(TeamExchangeListComposer.ExchangesPrefix + ":" + D + "/", records).Body);
        Assert.NotNull(composer.Compose(ExchangesListId, records).Body);
    }

    [Fact]
    public void ATeamEventReadsNoMessagesAndAnExchangeEventReadsOneExchange()
    {
        var reader = Truth();
        reader.Exchanges["a"] = Msg("developer", "orchestrator", "dev", null, "default");
        var (model, clock) = Seeded(reader);
        using var owner = model;

        reader.Calls.Clear();
        reader.Teams[TeamPath] = Doc("so", "dev", "qa", "ops");
        model.Emit("teams", TeamKey());
        Drain(model, clock);
        Assert.Equal(ReadsPerTeamEvent, reader.Total);
        Assert.DoesNotContain(reader.Calls.Keys, static k => k.StartsWith("msgs.", StringComparison.Ordinal));

        reader.Calls.Clear();
        Send(model, clock, reader, "b", Msg("tester", "orchestrator", "qa", null, "default"));
        Assert.Equal(1, reader.Calls.GetValueOrDefault("msgs.read"));
        Assert.Equal(1, reader.Total);
        Assert.Equal(ListBody("default", "b", "a"), IdsOnly(Body(model, ExchangesListId)));
    }

    [Fact]
    public void WithNoClientAHundredExchangeEventsAreAppliedAndNothingPollsEnumeratesOrReconciles()
    {
        var reader = Truth();
        var (model, clock) = Seeded(reader);
        using var owner = model;
        var enumerations = model.Counters.KeySetEnumerations;
        var notices = model.Counters.ReconcileNotices;
        var applied = model.Counters.Applied;
        reader.Calls.Clear();

        for (var i = 0; i < 100; i++)
        {
            reader.Exchanges["x"] = Msg("developer", "orchestrator", "dev", null, i % 2 == 0 ? "default" : null);
            model.Emit("messages", MessagesLane.KeyOf(Canon, "x"));
            Drain(model, clock);
            clock.Now += 3_600_000;
            model.Tick();
            Drain(model, clock);
        }

        Assert.Equal(applied + 100, model.Counters.Applied);
        Assert.Equal(100, reader.Calls.GetValueOrDefault("msgs.read"));
        Assert.Equal(100, reader.Total);
        Assert.Equal(0, model.Counters.Polls);
        Assert.Equal(enumerations, model.Counters.KeySetEnumerations);
        Assert.Equal(notices, model.Counters.ReconcileNotices);
    }

    [Fact]
    public void ANamelessPartyIsTheSingleHolderOfItsRoleAndNobodyWhenTheRoleHasNoneOrSeveral()
    {
        var reader = Truth();
        reader.Teams[TeamPath] = Doc("so", "dev", "qa", "ops");
        reader.Exchanges["a"] = Msg("developer", "orchestrator", null, null, "default");
        reader.Exchanges["b"] = Msg("tester", "orchestrator", null, null, "default");
        reader.Exchanges["c"] = Msg("reviewer", "orchestrator", null, null, "default");
        var (model, _) = Seeded(reader);
        using var owner = model;

        Assert.Contains(Last("a"), Body(model, MemberId("dev")), StringComparison.Ordinal);
        Assert.Contains(Last(), Body(model, MemberId("qa")), StringComparison.Ordinal);
        Assert.Contains(Last(), Body(model, MemberId("ops")), StringComparison.Ordinal);
        Assert.Contains(Last("c", "b", "a"), Body(model, MemberId(TreeIds.Orchestrator)), StringComparison.Ordinal);
        Assert.Equal(ListBody("default", "c", "b", "a"), IdsOnly(Body(model, ExchangesListId)));
    }

    [Fact]
    public void ANameBeatsARoleWhenBothArePresent()
    {
        var reader = Truth();
        reader.Teams[TeamPath] = Doc("so", "dev", "qa", "ops");
        reader.Exchanges["a"] = Msg("tester", "orchestrator", "dev", null, "default");
        reader.Exchanges["b"] = Msg("developer", "orchestrator", "qa", null, "default");
        var (model, _) = Seeded(reader);
        using var owner = model;

        Assert.Contains(Last("a"), Body(model, MemberId("dev")), StringComparison.Ordinal);
        Assert.Contains(Last("b"), Body(model, MemberId("qa")), StringComparison.Ordinal);
        Assert.Contains(Last(), Body(model, MemberId("ops")), StringComparison.Ordinal);
    }

    [Fact]
    public void ASecondHolderOfARoleDropsTheMappingAndEqualsARebuild()
    {
        var reader = Truth();
        reader.Exchanges["a"] = Msg("tester", "orchestrator", null, null, "default");
        var (model, clock) = Seeded(reader);
        using var owner = model;
        Assert.Contains(Last("a"), Body(model, MemberId("qa")), StringComparison.Ordinal);

        reader.Teams[TeamPath] = Doc("so", "dev", "qa", "ops");
        model.Emit("teams", TeamKey());
        Drain(model, clock);

        Assert.Contains(Last(), Body(model, MemberId("qa")), StringComparison.Ordinal);
        Assert.Contains(Last(), Body(model, MemberId("ops")), StringComparison.Ordinal);
        AssertEqualsRebuild(model, reader);

        reader.Teams[TeamPath] = Doc("so", "dev", "qa");
        model.Emit("teams", TeamKey());
        Drain(model, clock);
        Assert.Contains(Last("a"), Body(model, MemberId("qa")), StringComparison.Ordinal);
        AssertEqualsRebuild(model, reader);
    }

    private sealed class CountingRecords(RecordStore inner) : IRecordView
    {
        public int MessageKeyReads { get; private set; }

        public LaneRecord? Get(string lane, string key) => inner.Get(lane, key);

        public IReadOnlyList<string> Keys(string lane)
        {
            if (lane == MessagesLane.LaneName)
            {
                MessageKeyReads++;
            }

            return inner.Keys(lane);
        }
    }

    private static CountingRecords ThreeMemberRecords()
    {
        var store = ListRecords(20);
        store.Set(TeamLane.LaneName, TeamKey(), new LaneRecord(JsonText.Parse(Doc("so", "dev", "qa", "ops")), 1));
        return new CountingRecords(store);
    }

    [Fact]
    public void AllTheMembersAndTheListOfATeamShareOneBuildOfItsExchangesInAPass()
    {
        var counting = ThreeMemberRecords();
        var pass = new OverlayRecordView(counting);
        var composers = TreeModel.Composers();

        foreach (var id in new[] { MemberId("dev"), MemberId("qa"), MemberId("ops"), MemberId(TreeIds.Orchestrator), ExchangesListId, FeedListId })
        {
            Assert.NotNull(composers.Compose(id, pass).Body);
        }

        Assert.Equal(1, counting.MessageKeyReads);
    }

    [Fact]
    public void APendingRecordSetAfterTheKeysWereAskedIsInTheNextAnswer()
    {
        var pass = new OverlayRecordView(ListRecords(1));
        pass.Set(TeamLane.LaneName, "zz-first", new LaneRecord(JsonText.Parse("{}"), 1));
        var before = pass.Keys(TeamLane.LaneName);
        Assert.Same(before, pass.Keys(TeamLane.LaneName));

        pass.Set(TeamLane.LaneName, "zz-second", new LaneRecord(JsonText.Parse("{}"), 1));
        pass.Set(TeamLane.LaneName, "zz-first", LaneRecord.Absent);

        string[] expected = [.. before.Where(static k => k != "zz-first").Append("zz-second").Order(StringComparer.Ordinal)];
        Assert.Equal(expected, pass.Keys(TeamLane.LaneName));
    }

    [Fact]
    public void WithoutAPassEveryComposeBuildsTheExchangesAgain()
    {
        var counting = ThreeMemberRecords();
        var composers = TreeModel.Composers();

        foreach (var id in new[] { MemberId("dev"), MemberId("qa"), MemberId("ops") })
        {
            Assert.NotNull(composers.Compose(id, counting).Body);
        }

        Assert.Equal(3, counting.MessageKeyReads);
    }

    [Fact]
    public void ARebuildComposesEveryTeamSliceFromOneBuildOfTheExchanges()
    {
        var counting = ThreeMemberRecords();

        var all = TreeModel.Composers().ComposeAll(counting);

        Assert.Contains(all, c => c.Id == MemberId("dev") && c.Body is not null);
        Assert.Equal(4, counting.MessageKeyReads); // one key listing for each of the three team list composers' AllIds, and one build of the team's exchanges
    }
}
