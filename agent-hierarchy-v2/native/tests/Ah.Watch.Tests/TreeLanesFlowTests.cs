using System.Globalization;
using System.Text;
using Ah.Core.Json;
using Ah.Watch.Model;

namespace Ah.Watch.Tests;

/// <summary>The run flow (<c>flow:</c>, <c>flow-page:</c>) and item message (<c>item-msgs:</c>) composers: run window, kinds, pages, caps, the byte gate, eviction, counts.</summary>
public sealed partial class TreeLanesTests
{
    private static readonly DateTimeOffset FlowT0 = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private const string Run = "20260601-120000-anch";

    private static string FlowAt(long seconds, int ms = 0) => FlowT0.AddSeconds(seconds).AddMilliseconds(ms).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    private static string FlowHeadId => FlowHeadComposer.IdOf(D, "default");

    private static string Party(string field, string name) =>
        name == "orch" ? "\"" + field + "\":\"orchestrator\",\"" + field + "_name\":null" : "\"" + field + "\":\"tester\",\"" + field + "_name\":\"" + name + "\"";

    private static string Ex(long created, string from, string to, long? responded = null, bool withdrawn = false, string slug = "s1", string? item = null, int ms = 0) =>
        "{\"team\":null,\"slug\":\"" + slug + "\"," + Party("from", from) + "," + Party("to", to) + ",\"created\":\"" + FlowAt(created, ms) + "\""
        + ",\"response_created\":" + (responded is { } r ? "\"" + FlowAt(r) + "\"" : "null") + ",\"open\":" + (responded is null ? "true" : "false")
        + ",\"withdrawn\":" + (withdrawn ? "true" : "false") + ",\"item\":" + Quoted(item) + "}";

    private static RecordStore FlowStore(string? run, IEnumerable<string> members, IEnumerable<(string Id, string Json)> exchanges)
    {
        var store = new RecordStore();
        store.Set(TeamLane.LaneName, TeamKey(), new LaneRecord(JsonText.Parse(Doc("so", [.. members])), 1));
        if (run is not null)
        {
            store.Set(DecisionsLane.LaneName, LaneParts.Join(Canon, run), new LaneRecord(JsonText.Parse("{\"run\":\"" + run + "\",\"last_decision_at\":\"2026-06-01T12:00:00Z\"}"), 1));
        }

        foreach (var (id, json) in exchanges)
        {
            store.Set(MessagesLane.LaneName, MessagesLane.KeyOf(Canon, id), new LaneRecord(JsonText.Parse(json), 1));
        }

        return store;
    }

    private static JsonValue Parsed(SliceChange change) => JsonText.Parse(Encoding.UTF8.GetString(change.Body!));

    private static JsonValue Head(IRecordView store) => Parsed(new FlowHeadComposer().Compose(FlowHeadId, store));

    private static List<long[]> Rows(JsonValue body, string name) =>
        [.. body.Member(name)!.Value.AsArray().Select(static r => r.AsArray().Select(static n => (long)n.AsNumber()).ToArray())];

    private static List<string?> Names(JsonValue head) => [.. head.Member("names")!.Value.AsArray().Select(static n => n.IsNull ? null : n.AsString())];

    private static string M(string name) => TreeIds.Member(Canon, "default", name == "orch" ? TreeIds.Orchestrator : name);

    private static (string, string) Anchor(long created = 0) => (Run, Ex(created, "orch", "orch"));

    [Fact]
    public void FlowKeepsOnlyEventsFromTheRunStart()
    {
        var store = FlowStore(Run, ["dev"], [Anchor(100), ("x1", Ex(99, "orch", "dev")), ("x2", Ex(160, "orch", "dev"))]);

        var head = Head(store);

        Assert.Equal(Run, head.Member("run")!.Value.AsString());
        Assert.Equal(FlowT0.AddSeconds(100).ToUnixTimeMilliseconds(), (long)head.Member("start_ms")!.Value.AsNumber());
        var tail = Rows(head, "tail");
        Assert.DoesNotContain(tail, static e => e[0] < 0);
        Assert.Equal([0L, 60L], [.. tail.Select(static e => e[0])]);
        Assert.Equal(2, tail.Count);
    }

    [Fact]
    public void WithNoRunTheWindowIsTheHourUpToTheNewestEventAndThereAreNoPages()
    {
        var exchanges = Enumerable.Range(0, 450).Select(i => ("x" + i.ToString("D4", CultureInfo.InvariantCulture), Ex(3600 + i, "orch", "dev"))).ToList();
        exchanges.Add(("old-edge", Ex(449, "orch", "dev")));
        exchanges.Add(("in-edge", Ex(450, "orch", "dev")));

        var head = Head(FlowStore(null, ["dev"], exchanges));

        Assert.True(head.Member("run")!.Value.IsNull);
        Assert.Empty(head.Member("pages")!.Value.AsArray());
        Assert.Empty(new FlowPageComposer().AllIds(FlowStore(null, ["dev"], exchanges)));
        Assert.Equal(FlowT0.AddSeconds(449).ToUnixTimeMilliseconds(), (long)head.Member("start_ms")!.Value.AsNumber());
        var tail = Rows(head, "tail");
        Assert.Equal(451, tail.Count);
        Assert.Equal(1, tail[0][0]);
    }

    [Fact]
    public void AnUnheldAnchorStartsTheRunAtItsIdStampInLocalTime()
    {
        var head = Head(FlowStore(Run, ["dev"], [("x1", Ex(0, "orch", "dev"))]));

        var local = new DateTimeOffset(new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Local)).ToUnixTimeMilliseconds();
        Assert.Equal(local, (long)head.Member("start_ms")!.Value.AsNumber());
        Assert.Null(RunFlow.AnchorStamp("not-a-stamp"));
    }

    [Fact]
    public void ARequestIsABriefAndAResponseIsAReportBackAndWithdrawnGivesNothing()
    {
        var store = FlowStore(Run, ["dev"], [Anchor(), ("x1", Ex(10, "orch", "dev", responded: 30)), ("x2", Ex(20, "dev", "orch", responded: 25, withdrawn: true))]);

        var head = Head(store);
        var names = Names(head);
        var orch = names.IndexOf(M("orch"));
        var dev = names.IndexOf(M("dev"));
        var tail = Rows(head, "tail").Where(static e => e[0] > 0).ToList();

        Assert.Equal(2, tail.Count);
        Assert.Equal([10L, orch, dev, 0L], tail[0]);
        Assert.Equal([30L, dev, orch, 1L], tail[1]);
    }

    private static List<(string, string)> Briefs(int count, int spacing = 1, string to = "dev") =>
        [Anchor(), .. Enumerable.Range(1, count).Select(i => ("x" + i.ToString("D5", CultureInfo.InvariantCulture), Ex(i * spacing, "orch", to)))];

    [Fact]
    public void FullPagesHoldExactlyTheirEventsAndANewEventChangesOnlyTheHead()
    {
        var exchanges = Briefs(2 * RunFlow.PageSize + 49);
        var store = FlowStore(Run, ["dev"], exchanges);
        var head = Head(store);
        var pageIds = new FlowPageComposer().AllIds(store);
        Assert.Equal([FlowPageComposer.IdOf(D, "default", Run, 0), FlowPageComposer.IdOf(D, "default", Run, 1)], pageIds);
        var page1 = new FlowPageComposer().Compose(pageIds[1], store);
        var events = Rows(Parsed(page1), "events");

        Assert.Equal(RunFlow.PageSize, events.Count);
        Assert.Equal(RunFlow.PageSize, events[0][0]);
        Assert.Equal(2 * RunFlow.PageSize - 1, events[^1][0]);
        Assert.Equal([0L, RunFlow.PageSize], head.Member("pages")!.Value.AsArray().Select(static p => (long)p.AsNumber()));
        Assert.Equal(50, Rows(head, "tail").Count);
        Assert.Null(new FlowPageComposer().Compose(FlowPageComposer.IdOf(D, "default", Run, 2), store).Body);
        Assert.Null(new FlowPageComposer().Compose(FlowPageComposer.IdOf(D, "default", "other-run", 0), store).Body);

        var headBefore = new FlowHeadComposer().Compose(FlowHeadId, store);
        store.Set(MessagesLane.LaneName, MessagesLane.KeyOf(Canon, "x99999"), new LaneRecord(JsonText.Parse(Ex(5000, "orch", "dev")), 1));
        var page1After = new FlowPageComposer().Compose(pageIds[1], store);
        var headAfter = new FlowHeadComposer().Compose(FlowHeadId, store);

        Assert.Equal(page1.Body, page1After.Body);
        Assert.Equal(page1.AsOfMs, page1After.AsOfMs);
        Assert.NotEqual(headBefore.Body, headAfter.Body);
        Assert.Equal(Parsed(headBefore).Member("pages_rev")!.Value.AsString(), Parsed(headAfter).Member("pages_rev")!.Value.AsString());
    }

    [Fact]
    public void WithdrawingAnExchangeOfAFullPageChangesThatPageAndPagesRev()
    {
        var store = FlowStore(Run, ["dev"], Briefs(2 * RunFlow.PageSize + 49));
        var pageId = FlowPageComposer.IdOf(D, "default", Run, 0);
        var before = new FlowPageComposer().Compose(pageId, store);
        var revBefore = Head(store).Member("pages_rev")!.Value.AsString();

        store.Set(MessagesLane.LaneName, MessagesLane.KeyOf(Canon, "x00010"), new LaneRecord(JsonText.Parse(Ex(10, "orch", "dev", responded: 11, withdrawn: true)), 1));
        var after = new FlowPageComposer().Compose(pageId, store);
        var head = Head(store);

        Assert.NotEqual(before.Body, after.Body);
        Assert.DoesNotContain(Rows(Parsed(after), "events"), static e => e[0] == 10);
        Assert.NotEqual(revBefore, head.Member("pages_rev")!.Value.AsString());
        Assert.False(head.Member("truncated")!.Value.AsBool());
    }

    [Fact]
    public void DismissingAMemberMidRunChangesPagesRevEvenWhenNoIndexMoves()
    {
        // dev is name 1 throughout page 0; once dismissed it re-resolves to "other", which takes the same index 1.
        var store = FlowStore(Run, ["dev", "qa"], Briefs(RunFlow.PageSize + 9));
        var revBefore = Head(store).Member("pages_rev")!.Value.AsString();
        var pageId = FlowPageComposer.IdOf(D, "default", Run, 0);
        var pageBefore = new FlowPageComposer().Compose(pageId, store);

        store.Set(TeamLane.LaneName, TeamKey(), new LaneRecord(JsonText.Parse(Doc("so", "qa")), 2));
        var head = Head(store);

        Assert.Equal(pageBefore.Body, new FlowPageComposer().Compose(pageId, store).Body);
        Assert.Equal(RunFlow.Other, Names(head)[1]);
        Assert.NotEqual(revBefore, head.Member("pages_rev")!.Value.AsString());
    }

    [Fact]
    public void EventsSortByTheirMillisecondsBeforeTheirIds()
    {
        var head = Head(FlowStore(Run, ["dev", "qa"], [Anchor(), ("x-b", Ex(5, "orch", "dev", ms: 100)), ("x-a", Ex(5, "orch", "qa", ms: 900))]));
        var names = Names(head);
        var tail = Rows(head, "tail");

        Assert.Equal(names.IndexOf(M("dev")), (int)tail[1][2]);
        Assert.Equal(names.IndexOf(M("qa")), (int)tail[2][2]);
    }

    [Fact]
    public void TheSixtyFifthMemberAndAnUnresolvedPartyFoldIntoOther()
    {
        var members = Enumerable.Range(0, 65).Select(static i => "m" + i.ToString("D2", CultureInfo.InvariantCulture)).ToList();
        var exchanges = new List<(string, string)> { Anchor() };
        exchanges.AddRange(members.Select((m, i) => ("x" + i.ToString("D3", CultureInfo.InvariantCulture), Ex(i + 1, "orch", m))));
        var head = Head(FlowStore(Run, members, exchanges));
        var names = Names(head);

        Assert.Equal(RunFlow.MaxNames + 1, names.Count);
        Assert.Equal(RunFlow.Other, names[^1]);
        Assert.Equal(64, Rows(head, "tail")[^1][2]);
        Assert.Equal(63, names.IndexOf(M("m62")));

        var few = Head(FlowStore(Run, ["dev"], [Anchor(), ("x1", Ex(1, "orch", "nobody"))]));
        var tail = Rows(few, "tail");
        Assert.Equal(2, tail.Count);
        Assert.Equal(RunFlow.Other, Names(few)[(int)tail[1][2]]);
        Assert.DoesNotContain(RunFlow.Other, Names(Head(FlowStore(Run, ["dev"], [Anchor(), ("x1", Ex(1, "orch", "dev"))]))));
    }

    private static List<(string, string)> Pairs(int pairs, List<string> members)
    {
        var exchanges = new List<(string, string)> { Anchor() };
        var n = 0;
        for (var d = 1; n < pairs; d++)
        {
            for (var i = 0; i < members.Count && n < pairs; i++, n++)
            {
                exchanges.Add(("x" + n.ToString("D5", CultureInfo.InvariantCulture), Ex(n + 1, members[i], members[(i + d) % members.Count])));
            }
        }

        return exchanges;
    }

    [Theory]
    [InlineData(199, false)]
    [InlineData(200, true)]
    public void PastTwoHundredPairsTheRestIsSummedIntoAnOverflowRow(int memberPairs, bool overflows)
    {
        // The anchor adds one pair (orchestrator to itself).
        var members = Enumerable.Range(0, 20).Select(static i => "m" + i.ToString("D2", CultureInfo.InvariantCulture)).ToList();
        var totals = Rows(Head(FlowStore(Run, members, Pairs(memberPairs, members))), "totals");

        Assert.Equal(overflows, totals.Any(static r => r[0] == -1 && r[1] == -1));
        Assert.True(totals.Count <= RunFlow.MaxTotals);
        Assert.Equal(memberPairs + 1, totals.Sum(static r => r[2] + r[3]));
    }

    private static List<string> LongMembers(int count) =>
        [.. Enumerable.Range(0, count).Select(static i => "ah2-tm4-implementor-with-a-long-name-" + i.ToString("D2", CultureInfo.InvariantCulture))];

    [Fact]
    public void AWorstCaseHeadIsTrimmedUnderTheGateOldestTailFirst()
    {
        var members = LongMembers(64);
        var exchanges = new List<(string, string)> { Anchor() };
        // Anchor + 4000 answered + 198 open = 8199 events: 40 full pages and a full 199-event tail.
        for (var i = 0; i < 4198; i++)
        {
            var from = members[i % 64];
            var to = members[(i + 1 + (i / 64 % 5)) % 64];
            exchanges.Add(("x" + i.ToString("D5", CultureInfo.InvariantCulture), Ex(i * 200L, from, to, responded: i < 4000 ? i * 200L + 100 : null)));
        }

        var store = FlowStore(Run, members, exchanges);
        var change = new FlowHeadComposer().Compose(FlowHeadId, store);
        var head = Parsed(change);
        var tail = Rows(head, "tail");

        Assert.True(change.Body!.Length <= RunFlow.SliceBudget, change.Body.Length.ToString(CultureInfo.InvariantCulture));
        Assert.True(head.Member("truncated")!.Value.AsBool());
        Assert.Equal(4197 * 200L, tail[^1][0]);
        Assert.True(tail.Count < RunFlow.PageSize - 1);
        foreach (var id in new FlowPageComposer().AllIds(store))
        {
            Assert.True(new FlowPageComposer().Compose(id, store).Body!.Length <= RunFlow.SliceBudget);
        }
    }

    [Fact]
    public void WhenTailAndTotalsAreNotEnoughNamesOnlyTheTrimmedTailUsedAreDroppedAndPageNamesKeepTheirIndex()
    {
        // Page 0 names orch and dev; the tail alone names 60 very long members.
        var members = Enumerable.Range(0, 60).Select(static i => new string('n', 150) + i.ToString("D2", CultureInfo.InvariantCulture)).ToList();
        var exchanges = Briefs(RunFlow.PageSize - 1);
        exchanges.AddRange(members.Select((m, i) => ("y" + i.ToString("D3", CultureInfo.InvariantCulture), Ex(1000 + i, "orch", m))));
        var store = FlowStore(Run, ["dev", .. members], exchanges);
        var change = new FlowHeadComposer().Compose(FlowHeadId, store);
        var head = Parsed(change);

        Assert.True(change.Body!.Length <= RunFlow.SliceBudget);
        Assert.True(head.Member("truncated")!.Value.AsBool());
        Assert.Equal([M("orch"), M("dev")], Names(head));
        Assert.Empty(Rows(head, "tail"));
        Assert.Equal(1, Rows(Parsed(new FlowPageComposer().Compose(FlowPageComposer.IdOf(D, "default", Run, 0), store)), "events")[^1][2]);
    }

    [Fact]
    public void ASmallHeadIsNotTruncated()
    {
        var head = Head(FlowStore(Run, ["dev"], Briefs(10)));

        Assert.False(head.Member("truncated")!.Value.AsBool());
        Assert.Equal(RunFlow.PageSize, (int)head.Member("page_size")!.Value.AsNumber());
        Assert.False(head.AsObject().ContainsKey("now_ms"));
    }

    [Theory]
    [InlineData(MessagesLane.MaxExchangesPerDir, true)]
    [InlineData(MessagesLane.MaxExchangesPerDir - 1, false)]
    public void RunEventsOlderThanAFullLaneSetTruncatedAndOldest(int held, bool truncated)
    {
        // The anchor is not held, so the run starts at its id stamp, before every held exchange.
        var exchanges = Enumerable.Range(0, held).Select(i => ("x" + i.ToString("D5", CultureInfo.InvariantCulture), Ex(36_000 + i, "orch", "dev"))).ToList();
        var head = Head(FlowStore(Run, ["dev"], exchanges));

        Assert.Equal(truncated, head.Member("truncated")!.Value.AsBool());
        Assert.Equal(FlowT0.AddSeconds(36_000).ToUnixTimeMilliseconds(), (long)head.Member("oldest_ms")!.Value.AsNumber());
    }

    [Fact]
    public void AFullLaneThatHoldsTheAnchorIsNotTruncated()
    {
        var exchanges = Briefs(MessagesLane.MaxExchangesPerDir - 1);

        Assert.False(Head(FlowStore(Run, ["dev"], exchanges)).Member("truncated")!.Value.AsBool());
    }

    [Fact]
    public void ALargeRunPagesEveryEventUnderTheGate()
    {
        var members = LongMembers(30);
        var exchanges = new List<(string, string)> { Anchor() };
        for (var i = 0; i < MessagesLane.MaxExchangesPerDir - 1; i++)
        {
            exchanges.Add(("x" + i.ToString("D5", CultureInfo.InvariantCulture), Ex(i * 10L + 1, members[i % 30], members[(i * 7 + 1) % 30], responded: i * 10L + 5)));
        }

        var store = FlowStore(Run, members, exchanges);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var head = new FlowHeadComposer().Compose(FlowHeadId, store);
        var ids = new FlowPageComposer().AllIds(store);
        var pages = ids.Select(id => new FlowPageComposer().Compose(id, store)).ToList();
        watch.Stop();

        Assert.True(head.Body!.Length <= RunFlow.SliceBudget);
        Assert.All(pages, static p => Assert.True(p.Body!.Length <= RunFlow.SliceBudget));
        var events = pages.Sum(p => Rows(Parsed(p), "events").Count) + Rows(Parsed(head), "tail").Count;
        Assert.Equal(1 + (2 * (MessagesLane.MaxExchangesPerDir - 1)), events);
        Assert.Equal(events / RunFlow.PageSize, ids.Count);
    }

    [Fact]
    public void TheFlowComposersReadOnlyRecords()
    {
        // A record view with no reader and no backing directory is all the composers get.
        var store = FlowStore(Run, ["dev"], Briefs(3));

        Assert.NotNull(new FlowHeadComposer().Compose(FlowHeadId, store).Body);
        Assert.NotNull(new ItemMsgsComposer().Compose(ItemMsgsComposer.IdOf(D, "_default"), ItemStore([("x1", Ex(1, "orch", "dev", item: "S3"))])).Body);
    }

    [Fact]
    public void AFlowChangeAffectsTheExchangesTeamAndADecisionAffectsEveryTeamOfTheDir()
    {
        var store = FlowStore(Run, ["dev"], Briefs(1));
        var record = store.Get(MessagesLane.LaneName, MessagesLane.KeyOf(Canon, "x00001"));

        Assert.Equal([FlowHeadId], new FlowHeadComposer().Affected(new LaneKey(MessagesLane.LaneName, MessagesLane.KeyOf(Canon, "x00001")), null, record, store));
        Assert.Equal([FlowHeadId], new FlowHeadComposer().Affected(new LaneKey(DecisionsLane.LaneName, LaneParts.Join(Canon, Run)), null, null, store));
        var otherTeam = new LaneRecord(JsonText.Parse(Ex(1, "orch", "dev").Replace("\"team\":null", "\"team\":\"elsewhere\"", StringComparison.Ordinal)), 1);
        Assert.Empty(new FlowHeadComposer().Affected(new LaneKey(MessagesLane.LaneName, MessagesLane.KeyOf(Canon, "y")), null, otherTeam, store));
    }

    [Fact]
    public void TheNewSlicesAreRegisteredWithTheirLanes()
    {
        Assert.Equal(["decisions", "messages", "teams"], SliceDependencies.FedBy("flow:<dirId>/<team>").Order(StringComparer.Ordinal));
        Assert.Equal(["decisions", "messages", "teams"], SliceDependencies.FedBy("flow-page:<dirId>/<team>/<run>/<k>").Order(StringComparer.Ordinal));
        Assert.Equal(["messages", "progress"], SliceDependencies.FedBy("item-msgs:<dirId>/<team>").Order(StringComparer.Ordinal));
        _ = new ComposerRegistry().Register(new FlowHeadComposer()).Register(new FlowPageComposer()).Register(new ItemMsgsComposer());
    }

    private static RecordStore ItemStore(IEnumerable<(string Id, string Json)> exchanges, params string[] streams)
    {
        var store = new RecordStore();
        var listed = string.Join(",", (streams.Length == 0 ? ["s1", "s2"] : streams).Select(static s => "{\"stream\":\"" + s + "\"}"));
        store.Set(ProgressLane.LaneName, ProgressLane.KeyOf(Canon, "_default"), new LaneRecord(JsonText.Parse("{\"streams\":[" + listed + "]}"), 1));
        foreach (var (id, json) in exchanges)
        {
            store.Set(MessagesLane.LaneName, MessagesLane.KeyOf(Canon, id), new LaneRecord(JsonText.Parse(json), 1));
        }

        return store;
    }

    private static string ItemCountsId => ItemMsgsComposer.IdOf(D, "_default");

    [Fact]
    public void ItemMsgsCountsByItemAndByStreamSlug()
    {
        var store = ItemStore(
        [
            ("x1", Ex(1, "orch", "dev", item: "A")),
            ("x2", Ex(2, "orch", "dev", item: "A", slug: "s2")),
            ("x3", Ex(3, "orch", "dev")),
            ("x4", Ex(4, "orch", "dev", item: "B", slug: "not-a-stream")),
            ("x5", Ex(5, "orch", "dev", item: "A", responded: 6, withdrawn: true)),
        ]);

        var body = Parsed(new ItemMsgsComposer().Compose(ItemCountsId, store));

        Assert.Equal("{\"A\":2}", Encoding.UTF8.GetString(JsonText.ToUtf8(body.Member("counts")!.Value, 0)));
        Assert.Equal("{\"s1\":2,\"s2\":1}", Encoding.UTF8.GetString(JsonText.ToUtf8(body.Member("by_stream")!.Value, 0)));
        Assert.False(body.Member("truncated")!.Value.AsBool());
        Assert.Null(new ItemMsgsComposer().Compose(ItemMsgsComposer.IdOf(D, "_default", "B"), store).Body);
        Assert.Null(new ItemMsgsComposer().Compose(ItemMsgsComposer.IdOf(D, "nobody"), store).Body);
    }

    [Fact]
    public void APerItemSliceHoldsTheCountAndTheFourteenNewestIds()
    {
        var store = ItemStore(Enumerable.Range(0, 20).Select(i => ("x" + i.ToString("D2", CultureInfo.InvariantCulture), Ex(i, "orch", "dev", item: "A"))));

        var change = new ItemMsgsComposer().Compose(ItemMsgsComposer.IdOf(D, "_default", "A"), store);
        var body = Parsed(change);
        var ids = body.Member("ids")!.Value.AsArray().Select(static i => i.AsString()).ToList();

        Assert.Equal(20, (int)body.Member("n")!.Value.AsNumber());
        Assert.Equal(ItemMsgsComposer.MaxIds, ids.Count);
        Assert.Equal("x19", ids[0]);
        Assert.Equal("x06", ids[^1]);
        Assert.True(change.Body!.Length <= 600);
    }

    [Theory]
    [InlineData(400, 64, false)]
    [InlineData(401, 64, true)]
    [InlineData(400, 65, true)]
    public void ItemMsgsCapsItemsAndStreams(int items, int streams, bool truncated)
    {
        var names = Enumerable.Range(0, streams).Select(static i => "s" + i.ToString("D2", CultureInfo.InvariantCulture)).ToArray();
        var exchanges = Enumerable.Range(0, Math.Max(items, streams))
            .Select(i => ("x" + i.ToString("D4", CultureInfo.InvariantCulture), Ex(i, "orch", "dev", slug: names[i % streams], item: i < items ? "I" + i.ToString("D3", CultureInfo.InvariantCulture) : null)));
        var change = new ItemMsgsComposer().Compose(ItemCountsId, ItemStore(exchanges, names));
        var body = Parsed(change);

        Assert.Equal(truncated, body.Member("truncated")!.Value.AsBool());
        Assert.Equal(Math.Min(items, ItemMsgsComposer.MaxItems), body.Member("counts")!.Value.AsObject().Count);
        Assert.Equal(Math.Min(streams, ItemMsgsComposer.MaxStreams), body.Member("by_stream")!.Value.AsObject().Count);
        Assert.True(change.Body!.Length <= RunFlow.SliceBudget);
        if (items > ItemMsgsComposer.MaxItems)
        {
            // Newest-active first: the oldest item is the one dropped, from the counts and as a per-item slice.
            Assert.False(body.Member("counts")!.Value.AsObject().ContainsKey("I000"));
            var store = ItemStore(exchanges, names);
            Assert.Null(new ItemMsgsComposer().Compose(ItemMsgsComposer.IdOf(D, "_default", "I000"), store).Body);
            Assert.NotNull(new ItemMsgsComposer().Compose(ItemMsgsComposer.IdOf(D, "_default", "I001"), store).Body);
        }
    }

    [Fact]
    public void ItemMsgsLeaveOutWithdrawnExchanges()
    {
        var store = ItemStore([("x1", Ex(1, "orch", "dev", item: "A")), ("x2", Ex(2, "orch", "dev", item: "A", responded: 3, withdrawn: true)), ("x3", Ex(4, "orch", "dev", item: "W", responded: 5, withdrawn: true))]);

        var counts = Parsed(new ItemMsgsComposer().Compose(ItemCountsId, store));
        var one = Parsed(new ItemMsgsComposer().Compose(ItemMsgsComposer.IdOf(D, "_default", "A"), store));

        Assert.Equal(1, (int)counts.Member("counts")!.Value.Member("A")!.Value.AsNumber());
        Assert.False(counts.Member("counts")!.Value.AsObject().ContainsKey("W"));
        Assert.Equal(1, (int)counts.Member("by_stream")!.Value.Member("s1")!.Value.AsNumber());
        Assert.Equal(1, (int)one.Member("n")!.Value.AsNumber());
        Assert.Equal(["x1"], one.Member("ids")!.Value.AsArray().Select(static i => i.AsString()));
        Assert.Null(new ItemMsgsComposer().Compose(ItemMsgsComposer.IdOf(D, "_default", "W"), store).Body);
    }

    [Fact]
    public void LongItemKeysAreTrimmedUnderTheGate()
    {
        var exchanges = Enumerable.Range(0, 400).Select(i => ("x" + i.ToString("D4", CultureInfo.InvariantCulture), Ex(i, "orch", "dev", item: new string('k', 60) + i.ToString("D3", CultureInfo.InvariantCulture))));
        var change = new ItemMsgsComposer().Compose(ItemCountsId, ItemStore(exchanges));
        var body = Parsed(change);

        Assert.True(change.Body!.Length <= RunFlow.SliceBudget);
        Assert.True(body.Member("truncated")!.Value.AsBool());
        Assert.True(body.Member("counts")!.Value.AsObject().ContainsKey(new string('k', 60) + "399"));
    }

    [Fact]
    public void AProgressChangeAffectsTheCountsAndEveryItemOfTheTeam()
    {
        var store = ItemStore([("x1", Ex(1, "orch", "dev", item: "A")), ("x2", Ex(2, "orch", "dev", item: "B", slug: "gone"))]);

        var ids = new ItemMsgsComposer().Affected(new LaneKey(ProgressLane.LaneName, ProgressLane.KeyOf(Canon, "_default")), null, null, store);

        Assert.Equal([ItemCountsId, ItemMsgsComposer.IdOf(D, "_default", "B"), ItemMsgsComposer.IdOf(D, "_default", "A")], ids);
    }
}
