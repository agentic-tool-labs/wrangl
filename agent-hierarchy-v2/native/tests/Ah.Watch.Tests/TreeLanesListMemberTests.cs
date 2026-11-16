using System.Collections.Immutable;
using Ah.Core.Json;
using Ah.Watch.Model;

namespace Ah.Watch.Tests;

/// <summary>The <c>member=</c> filter of the team exchange pager: it resolves parties with the one resolver over the roster published with the slices.</summary>
public sealed partial class TreeLanesTests
{
    private static ExchangeListPager PagerOver(ReadModel model, Func<string, string, JsonValue?>? cold = null) =>
        new(TeamExchangeListComposer.ExchangesPrefix + ":", () => model.Store.Current, cold ?? ((_, _) => null));

    private static async Task<ListPage> MemberPage(ExchangeListPager pager, string member, int limit = 200, string? after = null, string? before = null)
    {
        var request = new ListRequest(ExchangesListId, after, before, limit, new Dictionary<string, string> { ["member"] = member });
        return Assert.IsType<ListResult.Found>(await pager.PageAsync(request, CancellationToken.None)).Page;
    }

    private static string[] RowIdsOf(ListPage page) => [.. page.Rows.Select(static r => r.Member("body")!.Value.Member("id")!.Value.AsString())];

    private static string[] LastExchangeIds(ReadModel model, string member) =>
        [.. JsonText.Parse(Body(model, MemberId(member))).Member("last_exchanges")!.Value.AsArray().Select(static e => e.AsString()["exchange:".Length..].Split('/')[1])];

    [Fact]
    public async Task TheMemberPageAgreesWithTheMembersLastExchangesThroughTheOneResolver()
    {
        var reader = Truth();
        reader.Exchanges["a"] = Msg("developer", "orchestrator", "dev", null, "default");
        reader.Exchanges["b"] = Msg("tester", "developer", "qa", "dev", "default");
        reader.Exchanges["c"] = Msg("tester", "orchestrator", "qa", null, "default");
        reader.Exchanges["d"] = Msg("developer", "orchestrator", null, null, "default");
        var (model, _) = Seeded(reader);
        using var owner = model;
        var pager = PagerOver(model);

        var dev = await MemberPage(pager, MemberId("dev"));
        var qa = await MemberPage(pager, MemberId("qa"));

        Assert.Equal(["d", "b", "a"], RowIdsOf(dev));
        Assert.Equal(LastExchangeIds(model, "dev"), RowIdsOf(dev));
        Assert.Equal(["c", "b"], RowIdsOf(qa));
        Assert.Equal(LastExchangeIds(model, "qa"), RowIdsOf(qa));
        Assert.Equal(3, dev.Total);
        Assert.Equal(0, dev.ColdReads);
    }

    [Theory]
    [InlineData("dev")]
    [InlineData("member:x")]
    [InlineData("member:d1/default")]
    [InlineData("member:d1/default/dev/extra")]
    [InlineData("team:d1/default/dev")]
    [InlineData("")]
    public async Task AMemberThatIsNotAFullMemberIdIsBadMember(string member)
    {
        var reader = Truth();
        reader.Exchanges["a"] = Msg("developer", "orchestrator", "dev", null, "default");
        var (model, _) = Seeded(reader);
        using var owner = model;
        var request = new ListRequest(ExchangesListId, null, null, 10, new Dictionary<string, string> { ["member"] = member });

        var result = await PagerOver(model).PageAsync(request, CancellationToken.None);

        Assert.Equal("bad_member", Assert.IsType<ListResult.Invalid>(result).Reason);
    }

    [Fact]
    public async Task AMemberOfAnotherDirectoryOrTeamIsBadMemberButTheListsOwnMemberIsNot()
    {
        var reader = Truth();
        reader.Exchanges["a"] = Msg("developer", "orchestrator", "dev", null, "default");
        var (model, _) = Seeded(reader);
        using var owner = model;
        var pager = PagerOver(model);
        Task<ListResult> Ask(string member) => pager.PageAsync(new ListRequest(ExchangesListId, null, null, 10, new Dictionary<string, string> { ["member"] = member }), CancellationToken.None).AsTask();

        var otherDir = await Ask("member:other/default/dev");
        var otherTeam = await Ask("member:" + D + "/elsewhere/dev");
        var own = await Ask(MemberId("dev"));

        Assert.Equal("bad_member", Assert.IsType<ListResult.Invalid>(otherDir).Reason);
        Assert.Equal("bad_member", Assert.IsType<ListResult.Invalid>(otherTeam).Reason);
        Assert.IsType<ListResult.Found>(own);
    }

    [Fact]
    public async Task ADirectoryListTakesAMemberOfAnyTeamOfItsDirectoryAndRefusesAnotherDirectory()
    {
        var reader = Truth();
        reader.Exchanges["a"] = Msg("developer", "orchestrator", "dev", null, "default");
        var (model, _) = Seeded(reader);
        using var owner = model;
        var listId = ExchangeListComposer.ExchangesPrefix + ":" + D;
        var withDirList = model.Store.Current with { Slices = model.Store.Current.Slices.Add(listId, model.Store.Current.Slices[ExchangesListId] with { Id = listId }) };
        var pager = new ExchangeListPager(ExchangeListComposer.ExchangesPrefix + ":", () => withDirList, (_, _) => null, teamScoped: false);
        Task<ListResult> Ask(string member) => pager.PageAsync(new ListRequest(listId, null, null, 10, new Dictionary<string, string> { ["member"] = member }), CancellationToken.None).AsTask();

        var found = Assert.IsType<ListResult.Found>(await Ask(MemberId("dev")));
        var otherDir = await Ask("member:other/default/dev");

        Assert.Equal(["a"], RowIdsOf(found.Page));
        Assert.Equal("bad_member", Assert.IsType<ListResult.Invalid>(otherDir).Reason);
    }

    [Fact]
    public async Task AFilterThatMatchesNothingReadsAtMostTheColdBudgetPerPageAndTheCursorsCarryTheScanToTheEnd()
    {
        var reader = Truth();
        for (var n = 1; n <= 150; n++)
        {
            reader.Exchanges["x" + n.ToString("000", System.Globalization.CultureInfo.InvariantCulture)] = n == 75
                ? Msg("tester", "orchestrator", "qa", null, "default")
                : Msg("developer", "orchestrator", "dev", null, "default");
        }

        var (model, _) = Seeded(reader);
        using var owner = model;
        var held = model.Store.Current;
        var evicted = held with { Slices = held.Slices.Where(static p => !p.Key.StartsWith("exchange:", StringComparison.Ordinal)).ToImmutableDictionary(StringComparer.Ordinal) };
        var reads = 0;
        var pager = new ExchangeListPager(TeamExchangeListComposer.ExchangesPrefix + ":", () => evicted, (_, id) => { reads++; return JsonText.Parse(Body(model, "exchange:" + D + "/" + id)); });

        var found = new List<string>();
        string? after = null;
        var pages = 0;
        do
        {
            reads = 0;
            var page = await MemberPage(pager, MemberId("qa"), limit: 10, after: after);
            Assert.True(reads <= ExchangeListPager.MaxColdScansPerPage, "cold reads in one page: " + reads);
            found.AddRange(RowIdsOf(page));
            pages++;
            if (page.Next == after)
            {
                break;
            }

            after = page.Next;
        }
        while (pages < 50);

        Assert.Equal(["x075"], found);
        Assert.True(pages >= 3, "the scan should have been spread over pages, took " + pages);
        Assert.True(pages < 50);
    }

    [Fact]
    public async Task AnExchangeServedHotAndTheSameExchangeEvictedHaveAByteIdenticalRowBuiltByTheSameBodyFunction()
    {
        var reader = Truth();
        reader.Exchanges["a"] = Msg("developer", "orchestrator", "dev", null, "default");
        reader.Exchanges["b"] = Msg("tester", "developer", "qa", "dev", "default");
        var (model, _) = Seeded(reader);
        using var owner = model;
        var held = model.Store.Current;
        var evicted = held with { Slices = held.Slices.Where(static p => !p.Key.StartsWith("exchange:", StringComparison.Ordinal)).ToImmutableDictionary(StringComparer.Ordinal) };
        var hotPager = PagerOver(model);
        var coldPager = new ExchangeListPager(TeamExchangeListComposer.ExchangesPrefix + ":", () => evicted, (_, id) => JsonValue.FromObject(MessagesLane.BodyOf(Canon, id, JsonText.Parse(System.Text.Encoding.UTF8.GetBytes(reader.Exchanges[id])))));
        var request = new ListRequest(ExchangesListId, null, null, 10, new Dictionary<string, string>());

        var hot = Assert.IsType<ListResult.Found>(await hotPager.PageAsync(request, CancellationToken.None)).Page;
        var cold = Assert.IsType<ListResult.Found>(await coldPager.PageAsync(request, CancellationToken.None)).Page;

        Assert.Equal(0, hot.ColdReads);
        Assert.Equal(2, cold.ColdReads);
        Assert.Equal(["exchange:" + D + "/b", "exchange:" + D + "/a"], hot.Rows.Select(static r => r.Member("id")!.Value.AsString()));
        Assert.Equal(hot.Rows.Select(static r => JsonText.ToUtf8(r, 0).Length), cold.Rows.Select(static r => JsonText.ToUtf8(r, 0).Length));
        Assert.All(hot.Rows.Zip(cold.Rows), pair => Assert.True(JsonText.ToUtf8(pair.First, 0).AsSpan().SequenceEqual(JsonText.ToUtf8(pair.Second, 0)), "hot and cold rows differ"));
    }

    [Fact]
    public async Task AMemberNoExchangeNamesGetsAnEmptyPageAndAnUnknownMemberToo()
    {
        var reader = Truth();
        reader.Exchanges["a"] = Msg("developer", "orchestrator", "dev", null, "default");
        var (model, _) = Seeded(reader);
        using var owner = model;
        var pager = PagerOver(model);

        var qa = await MemberPage(pager, MemberId("qa"));
        var nobody = await MemberPage(pager, MemberId("ghost"));

        Assert.Empty(qa.Rows);
        Assert.Equal(0, qa.Total);
        Assert.Empty(nobody.Rows);
        Assert.Null(qa.Next);
        Assert.Null(qa.Prev);
    }

    [Fact]
    public async Task TheOrchestratorMemberFiltersByTheOrchestratorRole()
    {
        var reader = Truth();
        reader.Exchanges["a"] = Msg("developer", "orchestrator", "dev", null, "default");
        reader.Exchanges["b"] = Msg("tester", "developer", "qa", "dev", "default");
        var (model, _) = Seeded(reader);
        using var owner = model;

        var page = await MemberPage(PagerOver(model), Orch);

        Assert.Equal(["a"], RowIdsOf(page));
    }

    [Fact]
    public async Task APageOfAMemberFilterHoldsLimitMatchesAndTheCursorsSkipTheRest()
    {
        var reader = Truth();
        foreach (var (id, toName) in new[] { ("a", "dev"), ("b", "qa"), ("c", "dev"), ("d", "qa"), ("e", "dev") })
        {
            reader.Exchanges[id] = Msg(toName == "dev" ? "developer" : "tester", "orchestrator", toName, null, "default");
        }

        var (model, _) = Seeded(reader);
        using var owner = model;
        var pager = PagerOver(model);

        var first = await MemberPage(pager, MemberId("dev"), limit: 2);
        var second = await MemberPage(pager, MemberId("dev"), limit: 2, after: first.Next);

        Assert.Equal(["e", "c"], RowIdsOf(first));
        Assert.Equal(["a"], RowIdsOf(second));
        Assert.Null(second.Next);
        Assert.NotNull(first.Next);
    }

    [Fact]
    public async Task TheTotalOfAMemberFilterIsNullWhenAnIdIsNotHeld()
    {
        var reader = Truth();
        reader.Exchanges["a"] = Msg("developer", "orchestrator", "dev", null, "default");
        reader.Exchanges["b"] = Msg("developer", "orchestrator", "dev", null, "default");
        var (model, _) = Seeded(reader);
        using var owner = model;
        var held = model.Store.Current;
        var evicted = held with { Slices = held.Slices.Remove("exchange:" + D + "/a") };
        var pager = new ExchangeListPager(TeamExchangeListComposer.ExchangesPrefix + ":", () => evicted, (_, id) => JsonText.Parse(Body(model, "exchange:" + D + "/" + id)));

        var page = await MemberPage(pager, MemberId("dev"));

        Assert.Null(page.Total);
        Assert.Equal(["b", "a"], RowIdsOf(page));
        Assert.Equal(1, page.ColdReads);
    }

    [Fact]
    public async Task APageReadsTheRosterOfTheStateItIsGivenAndNotTheLiveRecordStore()
    {
        var reader = Truth();
        reader.Exchanges["a"] = Msg("developer", "orchestrator", "dev", null, "default");
        var (model, _) = Seeded(reader);
        using var owner = model;
        var withoutRoster = model.Store.Current with { Rosters = ModelState.NoRosters };
        var pager = new ExchangeListPager(TeamExchangeListComposer.ExchangesPrefix + ":", () => withoutRoster, (_, _) => null);

        var page = await MemberPage(pager, MemberId("dev"));

        Assert.Empty(page.Rows);
        Assert.Equal(["a"], RowIdsOf(await MemberPage(PagerOver(model), MemberId("dev"))));
    }

    [Fact]
    public void TheProjectorPublishesTheRosterWithTheSlicesAndARosterChangeReplacesItWithoutMovingTheCounter()
    {
        var reader = Truth();
        var (model, clock) = Seeded(reader);
        using var owner = model;
        var before = model.Store.Current;

        Assert.Equal(["so", "dev", "qa"], before.Rosters[(D, "default")].MemberNames.Select(static n => n == TreeIds.Orchestrator ? "so" : n));

        reader.Teams[TeamPath] = Doc("so", "dev");
        model.Emit(TeamLane.LaneName, TeamKey());
        Drain(model, clock);
        var after = model.Store.Current;

        Assert.DoesNotContain("qa", after.Rosters[(D, "default")].MemberNames);
        Assert.NotSame(before.Rosters, after.Rosters);
        Assert.True(after.Publish >= before.Publish);
    }

    [Fact]
    public void ASliceOnlyPublishKeepsTheRosterReferenceAndARosterOnlySwapKeepsTheCounter()
    {
        var store = new SliceStore("e");
        var roster = ImmutableDictionary<(string DirId, string Team), TeamEntry>.Empty.Add(("d", "t"), new TeamEntry("/x", "/x/team.json", "t", JsonText.Parse("{}"), 0));

        _ = store.Publish([new SliceChange("a", [1], 1)], 0, roster);
        var withRoster = store.Current;
        _ = store.Publish([new SliceChange("a", [2], 1)], 0);
        Assert.Same(roster, store.Current.Rosters);

        var counter = store.Current.Publish;
        var other = roster.Add(("d", "u"), new TeamEntry("/x", "/x/teams/u.json", "u", JsonText.Parse("{}"), 0));
        _ = store.Publish([], 0, other);

        Assert.Same(other, store.Current.Rosters);
        Assert.Equal(counter, store.Current.Publish);
        Assert.Same(roster, withRoster.Rosters);
    }

    private static Task<ListResult> AskWith(ExchangeListPager pager, string? member, string? with, string? after = null, int limit = 200)
    {
        var filters = new Dictionary<string, string>();
        if (member is not null)
        {
            filters["member"] = member;
        }

        if (with is not null)
        {
            filters["with"] = with;
        }

        return pager.PageAsync(new ListRequest(ExchangesListId, after, null, limit, filters), CancellationToken.None).AsTask();
    }

    private static TreeReader PairTruth()
    {
        var reader = Truth();
        reader.Exchanges["a"] = Msg("developer", "orchestrator", "dev", null, "default");
        reader.Exchanges["b"] = Msg("tester", "developer", "qa", "dev", "default");
        reader.Exchanges["c"] = Msg("tester", "orchestrator", "qa", null, "default");
        reader.Exchanges["d"] = Msg("developer", "tester", "dev", "qa", "default");
        reader.Exchanges["e"] = Msg("developer", "orchestrator", "dev", null, "default");
        return reader;
    }

    [Fact]
    public async Task WithKeepsTheExchangesBetweenTheTwoInEitherDirectionNewestFirstAndDropsOneWithAThirdMember()
    {
        var (model, _) = Seeded(PairTruth());
        using var owner = model;

        var page = Assert.IsType<ListResult.Found>(await AskWith(PagerOver(model), MemberId("dev"), MemberId("qa"))).Page;
        var reversed = Assert.IsType<ListResult.Found>(await AskWith(PagerOver(model), MemberId("qa"), MemberId("dev"))).Page;

        Assert.Equal(["d", "b"], RowIdsOf(page));
        Assert.Equal(RowIdsOf(page), RowIdsOf(reversed));
        Assert.Equal(2, page.Total);
    }

    [Fact]
    public async Task WithWithoutMemberIsBadMember()
    {
        var (model, _) = Seeded(PairTruth());
        using var owner = model;

        var result = await AskWith(PagerOver(model), null, MemberId("qa"));

        Assert.Equal("bad_member", Assert.IsType<ListResult.Invalid>(result).Reason);
    }

    [Theory]
    [InlineData("qa")]
    [InlineData("member:d1/default")]
    [InlineData("member:other/default/qa")]
    [InlineData("member:d1/elsewhere/qa")]
    public async Task AWithThatIsMalformedOrOfAnotherDirectoryOrTeamIsBadMemberButTheTeamsOwnMemberIsNot(string with)
    {
        var (model, _) = Seeded(PairTruth());
        using var owner = model;
        var pager = PagerOver(model);

        var bad = await AskWith(pager, MemberId("dev"), with);
        var own = await AskWith(pager, MemberId("dev"), MemberId("qa"));

        Assert.Equal("bad_member", Assert.IsType<ListResult.Invalid>(bad).Reason);
        Assert.IsType<ListResult.Found>(own);
    }

    [Fact]
    public async Task OnADirectoryListWithMustNameTheTeamOfMember()
    {
        var (model, _) = Seeded(PairTruth());
        using var owner = model;
        var listId = ExchangeListComposer.ExchangesPrefix + ":" + D;
        var state = model.Store.Current with { Slices = model.Store.Current.Slices.Add(listId, model.Store.Current.Slices[ExchangesListId] with { Id = listId }) };
        var pager = new ExchangeListPager(ExchangeListComposer.ExchangesPrefix + ":", () => state, (_, _) => null, teamScoped: false);
        Task<ListResult> Ask(string with) => pager.PageAsync(new ListRequest(listId, null, null, 10, new Dictionary<string, string> { ["member"] = MemberId("dev"), ["with"] = with }), CancellationToken.None).AsTask();

        var same = Assert.IsType<ListResult.Found>(await Ask(MemberId("qa")));
        var otherTeam = await Ask("member:" + D + "/elsewhere/qa");

        Assert.Equal(["d", "b"], RowIdsOf(same.Page));
        Assert.Equal("bad_member", Assert.IsType<ListResult.Invalid>(otherTeam).Reason);
    }

    [Fact]
    public async Task AWellFormedWithNotInTheRosterIsAnEmptyPageWithTotalZero()
    {
        var (model, _) = Seeded(PairTruth());
        using var owner = model;

        var page = Assert.IsType<ListResult.Found>(await AskWith(PagerOver(model), MemberId("dev"), MemberId("nobody"))).Page;

        Assert.Empty(page.Rows);
        Assert.Equal(0, page.Total);
        Assert.Null(page.Next);
        Assert.Null(page.Prev);
    }

    [Fact]
    public async Task WithEqualToMemberGivesWhatMemberAloneGives()
    {
        var (model, _) = Seeded(PairTruth());
        using var owner = model;
        var pager = PagerOver(model);

        var alone = Assert.IsType<ListResult.Found>(await AskWith(pager, MemberId("dev"), null)).Page;
        var both = Assert.IsType<ListResult.Found>(await AskWith(pager, MemberId("dev"), MemberId("dev"))).Page;

        Assert.Equal(["e", "d", "b", "a"], RowIdsOf(alone));
        Assert.Equal(RowIdsOf(alone), RowIdsOf(both));
    }

    [Fact]
    public async Task PagingAPairWithAfterContinuesWithoutDuplicates()
    {
        var (model, _) = Seeded(PairTruth());
        using var owner = model;
        var pager = PagerOver(model);

        var first = Assert.IsType<ListResult.Found>(await AskWith(pager, MemberId("dev"), MemberId("qa"), limit: 1)).Page;
        var second = Assert.IsType<ListResult.Found>(await AskWith(pager, MemberId("dev"), MemberId("qa"), after: first.Next, limit: 1)).Page;

        Assert.Equal(["d"], RowIdsOf(first));
        Assert.Equal(["b"], RowIdsOf(second));
    }

    [Fact]
    public void OnlyTheExchangePagerAcceptsWithAndTheOpenListStaysBadFilter()
    {
        var exchanges = new ExchangeListPager("list:exchanges:team:", () => null!, (_, _) => null);
        var open = new OpenListPager(() => null!, (_, _) => null);

        Assert.Contains("with", exchanges.Filters);
        Assert.DoesNotContain("with", open.Filters);
    }
}
