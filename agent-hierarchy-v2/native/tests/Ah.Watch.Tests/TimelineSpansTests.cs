using System.Collections.Immutable;
using System.Text;
using Ah.Core.Json;
using Ah.Watch.Model;

namespace Ah.Watch.Tests;

/// <summary>What <c>list:timeline-spans</c> serves: each member's spans over a window, its message marks, and what the window could not hold.</summary>
public sealed class TimelineSpansTests
{
    private const long T0 = 1_800_000_000_000;
    private const long Min = 60_000;
    private const string Dir = "/x/.claude/ah2";
    private const string Team = "t";

    private static readonly string DirId = ActivityLane.DirId(Dir);
    private static readonly string ListId = TimelineSpansPager.ListPrefix + DirId + "/" + Team;

    private static TimelineRow Row(long at, string? from, string to, string subject = "s-a", long seq = 0) => new(seq, DirId, subject, from, to, at);

    private static List<(string State, long Minutes)> Of(List<(string State, long From, long To)> spans) => [.. spans.Select(static s => (s.State, (s.To - s.From) / Min))];

    private sealed class Pages(List<TimelineRow> rows) : ITimelinePages
    {
        private readonly List<TimelineRow> _rows = [.. rows.Select(static (r, i) => r with { Seq = i + 1 })];

        public IReadOnlyList<TimelineRow> Page(string dirId, long fromMs, long toMs, long cursorSeq, bool older, int limit) =>
            [.. _rows.Where(r => r.AtMs >= fromMs && r.AtMs < toMs && (cursorSeq == 0 || r.Seq < cursorSeq)).OrderByDescending(static r => r.Seq).Take(limit)];

        public long CountIn(string dirId, long fromMs, long toMs) => _rows.Count(r => r.AtMs >= fromMs && r.AtMs < toMs);

        public IReadOnlyList<TimelineSeed> Seeds(string dirId, long beforeMs) =>
            [.. _rows.Where(r => r.AtMs < beforeMs).GroupBy(static r => r.Subject).Select(static g => g.MaxBy(static r => r.Seq)).Select(static r => new TimelineSeed(r.Subject, r.To, r.AtMs))];
    }

    private static string Exchange(string id, long created, long? responded = null, string to = "a", string from = "b", string? cls = null) =>
        "{\"id\":\"" + id + "\",\"slug\":\"" + id + "\",\"title\":\"\",\"class\":" + (cls is null ? "null" : "\"" + cls + "\"") + ",\"to\":\"implementor\",\"from\":\"reviewer\",\"to_name\":\"" + to + "\",\"from_name\":\"" + from + "\",\"created\":\"" + Iso(created) + "\""
        + ",\"has_response\":" + (responded is null ? "false" : "true") + ",\"response_created\":" + (responded is { } r ? "\"" + Iso(r) + "\"" : "null") + "}";

    private static string Iso(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture);

    private static ModelState State(string[]? exchanges = null, string? activityA = "idle")
    {
        var slices = ImmutableDictionary<string, Slice>.Empty.WithComparers(StringComparer.Ordinal);
        ImmutableDictionary<string, Slice> With(ImmutableDictionary<string, Slice> s, string id, string body) => s.SetItem(id, new Slice(id, 1, 0, Encoding.UTF8.GetBytes(body), "h"));
        slices = With(slices, TreeIds.Member(Dir, Team, "a"), "{\"name\":\"a\",\"session\":\"s-a\",\"activity\":\"activity:" + DirId + "/s-a\"}");
        slices = With(slices, TreeIds.Member(Dir, Team, "b"), "{\"name\":\"b\",\"session\":\"s-b\",\"activity\":null}");
        if (activityA is not null)
        {
            slices = With(slices, "activity:" + DirId + "/s-a", "{\"activity\":\"" + activityA + "\"}");
        }

        var held = exchanges ?? [];
        var ids = held.Select(static b => JsonText.Parse(Encoding.UTF8.GetBytes(b)).Member("id")!.Value.AsString()).ToList();
        slices = With(slices, "list:exchanges:team:" + DirId + "/" + Team, "{\"ids\":[" + string.Join(',', ids.Select(static i => "\"" + i + "\"")) + "]}");
        for (var i = 0; i < held.Length; i++)
        {
            slices = With(slices, "exchange:" + DirId + "/" + ids[i], held[i]);
        }

        var roster = new TeamEntry(Dir, "/x/t.json", Team, JsonText.Parse(Encoding.UTF8.GetBytes("{\"members\":[{\"name\":\"a\",\"role\":\"implementor\"},{\"name\":\"b\",\"role\":\"reviewer\"}]}")), 0);
        return new ModelState(7, slices, ModelState.NoRosters.Add((DirId, Team), roster));
    }

    private static async Task<JsonValue> Ask(ModelState state, List<TimelineRow>? rows = null, long? from = null, long? to = null, string? after = null, int limit = 50, string id = "")
    {
        var query = new Dictionary<string, string>();
        if (from is { } f)
        {
            query["from_ms"] = f.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (to is { } t)
        {
            query["to_ms"] = t.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        var pager = new TimelineSpansPager(new Pages(rows ?? []), static () => 7, static () => T0, () => state, static (_, _) => null);
        var request = new ListRequest(id.Length > 0 ? id : ListId, after, null, limit, query);
        return Assert.IsType<ListResult.Found>(await pager.PageAsync(request, CancellationToken.None)).Page.ToJson();
    }

    private static JsonValue Lane(JsonValue page, string name) => page.Member("rows")!.Value.AsArray().Single(r => r.Member("name")!.Value.AsString() == name);

    private static List<string> States(JsonValue lane) => [.. lane.Member("spans")!.Value.AsArray().Select(static s => s.Member("state")!.Value.AsString())];

    private static List<string> Marks(JsonValue lane) => [.. lane.Member("marks")!.Value.AsArray().Select(static m => m.Member("slug")!.Value.AsString() + ":" + m.Member("kind")!.Value.AsString())];

    [Fact]
    public void TheStateBeforeTheFirstTransitionIsItsFromStateAndTheLastRunsToTheEnd()
    {
        var spans = TimelineSpansPager.Spans([Row(T0 - (40 * Min), "idle", "working"), Row(T0 - (10 * Min), "working", "blocked")], T0 - (60 * Min), T0, null, null, null);
        Assert.Equal([("idle", 20L), ("working", 30L), ("blocked", 10L)], Of(spans));
    }

    [Fact]
    public void ASpanIsClippedToTheWindowAndNegativeOneEntirelyOutsideOpensNone()
    {
        var clipped = TimelineSpansPager.Spans([Row(T0 + (10 * Min), "working", "idle")], T0 - (30 * Min), T0, null, null, null);
        Assert.Equal([("working", 30L)], Of(clipped));
        Assert.Equal([("unknown", 30L)], Of(TimelineSpansPager.Spans([], T0 - (30 * Min), T0, null, null, null)));
    }

    [Fact]
    public void BeforeTheOldestLoadedRowIsLoadingEvenWithASeedAndNegativeNotOnceAllIsLoaded()
    {
        IReadOnlyList<TimelineRow> rows = [Row(T0 - (20 * Min), "idle", "working")];
        Assert.Equal([("loading", 40L), ("working", 20L)], Of(TimelineSpansPager.Spans(rows, T0 - (60 * Min), T0, "blocked", null, T0 - (20 * Min))));
        Assert.Equal([("blocked", 40L), ("working", 20L)], Of(TimelineSpansPager.Spans(rows, T0 - (60 * Min), T0, "blocked", null, null)));
        Assert.Equal("blocked", TimelineSpansPager.Spans(rows, T0 - (60 * Min), T0, "blocked", null, T0 - (90 * Min))[0].State);
        Assert.Equal([("loading", 40L), ("working", 20L)], Of(TimelineSpansPager.Spans([], T0 - (60 * Min), T0, "idle", "working", T0 - (20 * Min))));
    }

    [Theory]
    [InlineData("failed", "blocked")]
    [InlineData("blocked", "blocked")]
    [InlineData("idle", "idle")]
    [InlineData("unknown", "unknown")]
    [InlineData("", "unknown")]
    [InlineData(null, "unknown")]
    [InlineData("mystery", "working")]
    public void AnActivityIsAStateAndNegativeAnUnlistedOneIsWorkingNotUnknown(string? activity, string state) => Assert.Equal(state, TimelineSpansPager.StateOf(activity));

    [Fact]
    public void AQuietLaneHoldsItsSeedElseItsTailElseUnknownAndNegativeTheTailDoesNotBeatTheSeed()
    {
        var (from, to) = (T0 - (30 * Min), T0);
        Assert.Equal("blocked", TimelineSpansPager.Spans([], from, to, "blocked", null, null).Single().State);
        Assert.Equal("working", TimelineSpansPager.Spans([], from, to, null, "working", null).Single().State);
        Assert.Equal("idle", TimelineSpansPager.Spans([], from, to, "idle", "working", null).Single().State);
    }

    [Fact]
    public async Task EachMemberHasALaneInRosterOrderAndRowsGoToTheirSubjectAndNegativeAStrangersRowIsNobodys()
    {
        var page = await Ask(State(activityA: null), [Row(T0 - (30 * Min), "idle", "working"), Row(T0 - (20 * Min), "working", "idle", "s-b"), Row(T0 - (5 * Min), "idle", "working", "stranger")], from: T0 - (60 * Min));
        Assert.Equal(["a", "b"], page.Member("rows")!.Value.AsArray().Select(static r => r.Member("name")!.Value.AsString()));
        Assert.Equal(["idle", "working"], States(Lane(page, "a")));
        Assert.Equal(["working", "idle"], States(Lane(page, "b")));
        Assert.Equal(T0 - (60 * Min), (long)page.Member("from_ms")!.Value.AsNumber());
        Assert.Equal(T0, (long)page.Member("to_ms")!.Value.AsNumber());
    }

    [Fact]
    public async Task ARangeEndingNowHoldsAQuietMemberAtItsActivityAndNegativeOneEndingEarlierDoesNot()
    {
        var state = State(activityA: "failed");
        Assert.Equal(["blocked"], States(Lane(await Ask(state, from: T0 - (10 * Min)), "a")));
        Assert.Equal(["unknown"], States(Lane(await Ask(state, from: T0 - (10 * Min), to: T0 - Min), "a")));
    }

    [Fact]
    public async Task ASeedIsTheStateBeforeTheWindowAndNegativeARowBeforeItOpensNoSpan()
    {
        var page = await Ask(State(), [Row(T0 - (80 * Min), "idle", "blocked"), Row(T0 - (30 * Min), "blocked", "working")], from: T0 - (60 * Min));
        Assert.Equal(["blocked", "working"], States(Lane(page, "a")));
    }

    [Fact]
    public async Task BothPartiesCarryABriefAndAResponseInTimeOrderAndNegativeOutOfRangeOrUnansweredHaveNone()
    {
        var state = State(
        [
            Exchange("open", T0 - (10 * Min)),
            Exchange("late", T0 - (20 * Min), T0 + Min),
            Exchange("answered", T0 - (50 * Min), T0 - (15 * Min), cls: "internal"),
            Exchange("old", T0 - (90 * Min), T0 - (80 * Min)),
        ]);
        var page = await Ask(state, from: T0 - (60 * Min));
        Assert.Equal(["answered:brief", "late:brief", "answered:response", "open:brief"], Marks(Lane(page, "a")));
        Assert.Equal(Marks(Lane(page, "a")), Marks(Lane(page, "b")));
        Assert.Equal("internal", Lane(page, "a").Member("marks")!.Value.AsArray()[0].Member("class")!.Value.AsString());
        Assert.Null(Lane(page, "a").Member("band"));
    }

    [Fact]
    public async Task TwoExchangesWithOneSlugKeepDistinctKeysAndNegativeAPartyOutsideTheTeamHasNoLane()
    {
        var same = Exchange("x", T0 - (5 * Min)).Replace("\"id\":\"x\"", "\"id\":\"y\"", StringComparison.Ordinal);
        var page = await Ask(State([Exchange("x", T0 - (5 * Min)), same, Exchange("z", T0 - (4 * Min), to: "nobody", from: "nobody2")]), from: T0 - (60 * Min));
        var keys = Lane(page, "a").Member("marks")!.Value.AsArray().Select(static m => m.Member("key")!.Value.AsString()).ToList();
        Assert.Equal(2, keys.Distinct().Count());
        Assert.DoesNotContain(keys, static k => k.Contains(":z", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PastTheExchangeCapALaneSaysWhereMarksStopAndNegativeNotWhenTheWindowIsReached()
    {
        var many = Enumerable.Range(0, TimelineSpansPager.MaxExchanges + 1).Select(static i => Exchange("e" + i.ToString("D4", System.Globalization.CultureInfo.InvariantCulture), T0 - ((i + 1) * 1000L))).ToArray();
        var page = await Ask(State(many), from: T0 - (60 * Min));
        Assert.Equal(T0 - (TimelineSpansPager.MaxExchanges * 1000L), (long)Lane(page, "a").Member("band")!.Value.AsNumber());

        var narrow = await Ask(State(many), from: T0 - (5 * Min));
        Assert.Null(Lane(narrow, "a").Member("band"));
    }

    [Fact]
    public async Task PastTheTransitionPagesTheEnvelopeSaysMoreAndTheRestIsLoadingAndNegativeNotWhenAllFit()
    {
        var count = (TimelineSpansPager.TransitionPages * ListRequest.MaxLimit) + 1;
        var rows = Enumerable.Range(0, count).Select(i => Row(T0 - ((count - i) * 1000L), "working", "idle")).ToList();
        var page = await Ask(State(), rows, from: T0 - (24 * 60 * Min));
        Assert.True(page.Member("more")!.Value.AsBool());
        Assert.Equal("loading", States(Lane(page, "a"))[0]);

        var fits = await Ask(State(), rows.TakeLast(10).ToList(), from: T0 - (24 * 60 * Min));
        Assert.False(fits.Member("more")!.Value.AsBool());
        Assert.NotEqual("loading", States(Lane(fits, "a"))[0]);
    }

    [Fact]
    public async Task AWindowBeforeRetentionIsArchivedAndNegativeOneFlooredToTheMinuteOfItIsNot()
    {
        const long Week = 7 * 24 * 60 * Min;
        Assert.True((await Ask(State(), from: T0 - Week - (2 * Min))).Member("archived")!.Value.AsBool());
        Assert.False((await Ask(State(), from: T0 - Week - (Min / 2))).Member("archived")!.Value.AsBool());
    }

    [Fact]
    public async Task ACursorContinuesAtTheNextMemberAndNegativeOneNamingNoMemberIsRefused()
    {
        var first = await Ask(State(), limit: 1);
        Assert.Equal(["a"], first.Member("rows")!.Value.AsArray().Select(static r => r.Member("name")!.Value.AsString()));
        var next = first.Member("next")!.Value.AsString();
        var second = await Ask(State(), limit: 1, after: next);
        Assert.Equal(["b"], second.Member("rows")!.Value.AsArray().Select(static r => r.Member("name")!.Value.AsString()));
        Assert.True(second.Member("next")!.Value.IsNull);

        var pager = new TimelineSpansPager(new Pages([]), static () => 7, static () => T0, static () => State(), static (_, _) => null);
        var bad = await pager.PageAsync(new ListRequest(ListId, ListCursor.Encode("nobody"), null, 1, new Dictionary<string, string>()), CancellationToken.None);
        Assert.IsType<ListResult.Invalid>(bad);
    }

    [Theory]
    [InlineData("list:timeline-spans:no-such/t")]
    [InlineData("list:timeline-spans:nosilent")]
    public async Task NegativeATeamNotHeldIsNotFound(string id)
    {
        var pager = new TimelineSpansPager(new Pages([]), static () => 7, static () => T0, static () => State(), static (_, _) => null);
        Assert.IsType<ListResult.NotFound>(await pager.PageAsync(new ListRequest(id, null, null, 10, new Dictionary<string, string>()), CancellationToken.None));
    }

    [Fact]
    public async Task ABadWindowIsRefusedByNameAndNegativeTheListPrefixDoesNotNestTheTimelines()
    {
        var pager = new TimelineSpansPager(new Pages([]), static () => 7, static () => T0, static () => State(), static (_, _) => null);
        var bad = await pager.PageAsync(new ListRequest(ListId, null, null, 10, new Dictionary<string, string> { ["from_ms"] = "soon" }), CancellationToken.None);
        Assert.Equal("from_ms", Assert.IsType<ListResult.Invalid>(bad).Reason);

        var registry = new ListPagerRegistry();
        registry.Register(new TimelinePager(new Pages([]), static () => 7, static () => T0, static () => 1));
        registry.Register(pager);
        Assert.IsType<TimelineSpansPager>(registry.Find(ListId));
        Assert.IsType<TimelinePager>(registry.Find("list:timeline:" + DirId));
    }
}
