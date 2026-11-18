using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Cli.Verbs;
using Ah.Core.Json;
using Ah.Platform;
using Ah.StateDb;
using Ah.TestKit;

namespace Ah.Cli.Tests;

/// <summary><c>ah backlog</c> against a temp home's state database, through the production composition.</summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class BacklogVerbTests : IDisposable
{
    private readonly Sandbox _sandbox = new();
    private readonly ServiceGraph _graph;
    private readonly string _dir;

    public BacklogVerbTests()
    {
        _dir = Path.Combine(_sandbox.Root, "work", ".claude", "ah2");
        Directory.CreateDirectory(_dir);
        using (var open = StateDbModule.Open(_sandbox.AhHome, mayCreate: true))
        {
            Assert.Equal(StateDbAvailability.Ready, open.Status.Availability);
        }

        var variables = new Dictionary<string, string?> { ["AH_TEST"] = "1", ["AH_HOME"] = _sandbox.AhHome, ["HOME"] = _sandbox.Home };
        var env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables, new PluginInfo(_sandbox.PluginRoot, "wrangl", "1.0.0", "1"));
        _graph = ServiceGraph.Build(PlatformModule.CreateIo(null), env, new FakeRunner(), new FakeDaemonClient());
    }

    public void Dispose() => _sandbox.Dispose();

    private IReadOnlyList<Ah.Hierarchy.PlanItem> _dirItems = [];

    private static Ah.Hierarchy.PlanItem Child(string state, string parent = "b1") =>
        new("c" + state, "T", state, "2026-11-04T00:00:00.000Z", null, null, [], null, null, null) { Parent = parent };

    private string StateIn(params string[] args) =>
        JsonText.Parse(System.Text.Encoding.UTF8.GetBytes(Ah("show", "b1", "--json").Out)).Member("state")!.Value.AsString();

    [Theory]
    [InlineData("in_progress", "in_progress")]
    [InlineData("blocked", "in_progress")]
    [InlineData("in_review", "in_review")]
    [InlineData("approved", "in_review")]
    public void ARowReadsAsItsItems(string item, string row)
    {
        Assert.Equal(0, Ah("add", "x").Code);
        _dirItems = [Child(item)];

        Assert.Equal(row, StateIn());
        Assert.Contains("b1", Ah("list", "--state", row).Out, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not_started")]
    [InlineData("next")]
    public void NegativeARowWhoseItemIsOnlyQueuedStaysNotStarted(string item)
    {
        Assert.Equal(0, Ah("add", "x").Code);
        _dirItems = [Child(item)];

        Assert.Equal("not_started", StateIn());
    }

    [Fact]
    public void NegativeAnItemOfAnotherRowOrDroppedMovesNothing()
    {
        Assert.Equal(0, Ah("add", "x").Code);
        _dirItems = [Child("in_progress", "b2"), Child("dropped")];

        Assert.Equal("not_started", StateIn());
    }

    [Fact]
    public void AQueuedItemBesideOneInReviewIsInProgress()
    {
        Assert.Equal(0, Ah("add", "x").Code);
        _dirItems = [Child("in_review"), Child("next")];

        Assert.Equal("in_progress", StateIn());
    }

    [Fact]
    public void TestingMarksEachRowUntilCleared()
    {
        Assert.Equal(0, Ah("add", "x").Code);
        Assert.Equal(0, Ah("add", "y").Code);
        _dirItems = [Child("in_progress")];

        Assert.Equal(0, Ah("testing", "abc1234", "b1", "b2").Code);
        Assert.Equal("testing", StateIn());
        Assert.Contains("testing abc1234", Ah("show", "b1", "--json").Out, StringComparison.Ordinal);
        Assert.Contains("b2", Ah("list", "--state", "testing").Out, StringComparison.Ordinal);

        Assert.Equal(0, Ah("testing", "--clear", "b1").Code);
        Assert.Equal("in_progress", StateIn());
    }

    [Fact]
    public void NegativeTestingRefusesAClosedRowAndClearRefusesAnUnmarkedOne()
    {
        Assert.Equal(0, Ah("add", "x").Code);
        Assert.Equal(3, Ah("testing", "--clear", "b1").Code);

        Assert.Equal(0, Ah("done", "b1", "--commit", "abc1234").Code);
        Assert.Equal(3, Ah("testing", "abc1234", "b1").Code);
        Assert.Equal("done", StateIn());
    }

    [Fact]
    public void ADoneOfARowMarkedTestingClosesIt()
    {
        Assert.Equal(0, Ah("add", "x").Code);
        Assert.Equal(0, Ah("testing", "abc1234", "b1").Code);

        Assert.Equal(0, Ah("done", "b1", "--commit", "abc1234").Code);
        Assert.Equal("done", StateIn());
    }

    private (int Code, string Out, string Err) AhAs(string by, params string[] args)
    {
        var deps = new BacklogDeps(_graph.Writer, _graph.Hierarchy.Messages.PoolContext(_dir), _sandbox.AhHome, _dir, "git@github.com:o/r.git", by, DirItems: () => _dirItems);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var code = BacklogVerb.Run(deps, args, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private (int Code, string Out, string Err) Ah(params string[] args) => AhAs("impl-1", args);

    private string Add(params string[] args)
    {
        var r = Ah(["add", .. args]);
        Assert.True(r.Code == 0, r.Err);
        return r.Out.Trim();
    }

    private JsonValue Json(params string[] args)
    {
        var r = Ah([.. args, "--json"]);
        Assert.True(r.Code == 0, r.Err);
        return JsonText.Parse(r.Out);
    }

    private static string? S(JsonValue v, string key) => v.Member(key) is { IsText: true } t ? t.AsString() : null;

    private List<string> Ids(params string[] args) => [.. Json(["list", .. args]).AsArray().Select(i => S(i, "item")!)];

    [Fact]
    public void AShippedRowClosesWithItsCommitAndNegativeAClosedOrUnknownRowIsLeftAlone()
    {
        var row = Add("Ship it");
        const string Sha = "abc1234def";

        Assert.Null(BacklogVerb.CloseShipped(_graph, _sandbox.AhHome, _dir, row, Sha));

        var done = Json("show", row);
        Assert.Equal("done", S(done, "state"));
        Assert.Equal([Sha], done.Member("commits")!.Value.AsArray().Select(static c => c.AsString()));

        // closed already: a second ship keeps the first commit
        Assert.Null(BacklogVerb.CloseShipped(_graph, _sandbox.AhHome, _dir, row, "fff1234"));
        Assert.Equal([Sha], Json("show", row).Member("commits")!.Value.AsArray().Select(static c => c.AsString()));

        Assert.Null(BacklogVerb.CloseShipped(_graph, _sandbox.AhHome, _dir, "b999", Sha));
    }

    [Fact]
    public void AShipKeepsAnItemWithAnOpenSubItemOpenAndNegativeClosesItOnceTheSubItemIsDone()
    {
        Assert.Equal("b1", Add("Epic", "--kind", "epic"));
        Assert.Equal("b2", Add("Sub", "--parent", "b1"));

        Assert.Null(BacklogVerb.CloseShipped(_graph, _sandbox.AhHome, _dir, "b1", "abc1234def"));
        Assert.Equal("not_started", S(Json("show", "b1"), "state"));

        Assert.Equal(0, Ah("done", "b2", "--commit", "def5678").Code);
        Assert.Null(BacklogVerb.CloseShipped(_graph, _sandbox.AhHome, _dir, "b1", "abc1234def"));
        Assert.Equal("done", S(Json("show", "b1"), "state"));
    }

    private IReadOnlyList<object?[]> Q(string sql, params object?[] args)
    {
        using var reader = StateDbModule.OpenReader(_sandbox.AhHome).Reader!;
        return reader.Query(sql, args);
    }

    [Fact]
    public void AddListsTheItemWithItsFieldsInTheDatabase()
    {
        Assert.Equal("b1", Add("First", "--outcome", "it works", "--theme", "db", "--size", "m", "--wave", "Now", "--source", "#1", "--source", "chat"));
        var item = Json("list").AsArray().Single();
        Assert.Equal("b1", S(item, "item"));
        Assert.Equal("not_started", S(item, "state"));
        Assert.Equal("it works", S(item, "outcome"));
        Assert.Equal("db", S(item, "theme"));
        Assert.Equal("M", S(item, "size"));
        Assert.Equal("now", S(item, "wave"));
        Assert.Equal(["#1", "chat"], item.Member("sources")!.Value.AsArray().Select(s => s.AsString()));

        // the seam: rows in plan_events and plan_items, and the change token bumped
        Assert.True((long)Q("SELECT COUNT(*) FROM plan_events WHERE stream = 'backlog'")[0][0]! >= 6);
        Assert.Equal("b1", Q("SELECT item FROM plan_items WHERE stream = 'backlog'").Single()[0]);
        Assert.Equal(1L, Q("SELECT seq FROM domain_seq WHERE domain = 'backlog'")[0][0]);
    }

    [Fact]
    public void NewItemsLandUntriagedAndLastInTheirWave()
    {
        Add("A", "--wave", "next");
        Add("B");
        Add("C", "--wave", "now");
        Add("D", "--wave", "next");
        Assert.Equal(["b3", "b1", "b4", "b2"], Ids());
        Assert.Equal("untriaged", S(Json("show", "b2"), "wave"));
    }

    [Fact]
    public void SetWaveAndAfterReorder()
    {
        Add("A", "--wave", "now");
        Add("B", "--wave", "now");
        Add("C", "--wave", "later");
        Assert.Equal(0, Ah("set", "b3", "--wave", "now").Code);
        Assert.Equal(["b1", "b2", "b3"], Ids());
        Assert.Equal(0, Ah("set", "b3", "--after", "b1").Code);
        Assert.Equal(["b1", "b3", "b2"], Ids());
        Assert.Equal(0, Ah("set", "b2", "--after", "b1").Code);
        Assert.Equal(["b1", "b2", "b3"], Ids());
        Assert.Equal(["b1", "b2", "b3"], Ids("--wave", "now"));
    }

    [Fact]
    public void NextHonoursDependsHolderAndUntriaged()
    {
        Add("untriaged one");
        Assert.Equal(1, Ah("next").Code);
        Assert.Equal(string.Empty, Ah("next").Out);

        Add("blocked", "--wave", "now", "--depends", "b3");
        Add("dep", "--wave", "later");

        // all candidates hold unmet depends or are untriaged: b3 is a candidate itself, so take it first
        Assert.Equal("b3", S(Json("next"), "item"));
        Assert.Equal(0, Ah("claim", "b3", "--holder", "impl-1").Code);
        Assert.Equal(1, Ah("next").Code);
        Assert.Equal(0, Ah("done", "b3", "--commit", "abcdef1").Code);
        Assert.Equal("b2", S(Json("next"), "item"));
    }

    [Fact]
    public void ClaimByAnotherHolderExitsThree()
    {
        Add("A", "--wave", "now");
        Assert.Equal(0, Ah("claim", "b1", "--holder", "impl-1").Code);
        Assert.Equal("in_progress", S(Json("show", "b1"), "state"));
        Assert.Equal(0, Ah("claim", "b1", "--holder", "impl-1").Code);
        var r = Ah("claim", "b1", "--holder", "impl-2");
        Assert.Equal(3, r.Code);
        Assert.Contains("held by impl-1", r.Out, StringComparison.Ordinal);
    }

    [Fact]
    public void DoneNeedsACommitAndDropNeedsANote()
    {
        Add("A");
        Add("B");
        Assert.Equal(2, Ah("done", "b1").Code);
        Assert.Equal(0, Ah("done", "b1", "--commit", "abcdef1", "--note", "shipped").Code);
        Assert.Equal("done", S(Json("show", "b1"), "state"));
        Assert.Equal(2, Ah("drop", "b2").Code);
        Assert.Equal(0, Ah("drop", "b2", "--note", "dup of b1").Code);
        Assert.Equal("dropped", S(Json("show", "b2"), "state"));
    }

    [Fact]
    public void SetStateDropsOrBlocksWithANoteAndNamesTheVerbsOtherwise()
    {
        Add("A");
        Add("B");
        Assert.Equal(2, Ah("set", "b1", "--state", "dropped").Code);
        Assert.Equal(0, Ah("set", "b1", "--state", "dropped", "--note", "dup").Code);
        Assert.Equal("dropped", S(Json("show", "b1"), "state"));
        Assert.Equal(0, Ah("set", "b2", "--state", "blocked", "--note", "waits on b1").Code);
        Assert.Equal("blocked", S(Json("show", "b2"), "state"));

        // negative: a state with its own verb changes nothing and names the verbs
        var done = Ah("set", "b2", "--state", "done", "--note", "x");
        Assert.Equal(2, done.Code);
        Assert.Contains("done <id>", done.Err, StringComparison.Ordinal);
        Assert.Equal("blocked", S(Json("show", "b2"), "state"));
    }

    [Fact]
    public void ShowHistoryListsTheEventsWithBy()
    {
        Add("A");
        Assert.Equal(0, AhAs("orch", "set", "b1", "--wave", "now").Code);
        var history = Json("show", "b1", "--history").Member("history")!.Value.AsArray();
        Assert.Equal("add", S(history[0], "event"));
        Assert.Equal("impl-1", S(history[0], "by"));
        Assert.Contains(history, e => S(e, "event") == "wave" && S(e, "by") == "orch");
    }

    [Fact]
    public void AddWithExtUpsertsAndWritesOnlyWhatDiffers()
    {
        Assert.Equal("b1", Add("X", "--ext", "github:o/r#458", "--wave", "now"));
        var before = (long)Q("SELECT COUNT(*) FROM plan_events")[0][0]!;
        Assert.Equal("b1", Add("X", "--ext", "github:o/r#458", "--wave", "now"));
        Assert.Equal(before, (long)Q("SELECT COUNT(*) FROM plan_events")[0][0]!);
        Assert.Equal("b1", Add("Y", "--ext", "github:o/r#458"));
        Assert.Equal(before + 1, (long)Q("SELECT COUNT(*) FROM plan_events")[0][0]!);
        var item = Json("show", "b1");
        Assert.Equal("Y", S(item, "title"));
        Assert.Equal("now", S(item, "wave"));
        Assert.Equal(458, (int)item.Member("issue")!.Value.Member("number")!.Value.AsNumber());
        Assert.Equal("https://github.com/o/r/issues/458", S(item.Member("issue")!.Value, "url"));
    }

    [Fact]
    public void AddWithoutExtTwiceMakesTwoItems()
    {
        Assert.Equal("b1", Add("X"));
        Assert.Equal("b2", Add("X"));
    }

    [Fact]
    public void ExtHeldByAnotherItemExitsThree()
    {
        Add("A", "--ext", "linear:A-1");
        Add("B");
        var r = Ah("set", "b2", "--ext", "linear:A-1");
        Assert.Equal(3, r.Code);
        Assert.Contains("ext held by b1", r.Out, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyAnOriginGithubExtSetsTheIssue()
    {
        Add("A", "--ext", "linear:ABC-12");
        Add("B", "--ext", "github:other/repo#9");
        Assert.True(Json("show", "b1").Member("issue")!.Value.IsNull);
        Assert.True(Json("show", "b2").Member("issue")!.Value.IsNull);
        Assert.Equal("linear:ABC-12", S(Json("show", "b1"), "ext"));
    }

    [Fact]
    public void ExportSinceRoundTripsToEmpty()
    {
        Add("A");
        Add("B");
        Ah("drop", "b2", "--note", "no");
        var all = Json("export");
        Assert.Equal(2, all.Member("items")!.Value.AsArray().Count);
        var asOf = (long)all.Member("as_of_ms")!.Value.AsNumber();
        Assert.Empty(Json("export", "--since", asOf.ToString(System.Globalization.CultureInfo.InvariantCulture)).Member("items")!.Value.AsArray());
        Thread.Sleep(5);
        Ah("set", "b1", "--wave", "now");
        var later = Json("export", "--since", asOf.ToString(System.Globalization.CultureInfo.InvariantCulture)).Member("items")!.Value.AsArray();
        Assert.Equal("b1", S(later.Single(), "item"));
        Assert.Contains("b2 dropped untriaged - B", Ah("export").Out, StringComparison.Ordinal);
    }

    [Fact]
    public void GroomedRecordsAStreamLevelEvent()
    {
        Assert.Equal(0, Ah("groomed", "--summary", "{\"untriaged\":0,\"stale\":0,\"changed\":2,\"decisions\":1}").Code);
        Assert.Contains("\"event\":\"groom\"", (string)Q("SELECT row FROM plan_events").Single()[0]!, StringComparison.Ordinal);
        Assert.Equal(2, Ah("groomed", "--summary", "[1]").Code);
    }

    private Hierarchy.BacklogHealth Health(long nowMs, long? stampMs)
    {
        using var reader = StateDbModule.OpenReader(_sandbox.AhHome).Reader!;
        return Hierarchy.HierarchyModule.BacklogHealthOf(reader.Query, _dir, nowMs, stampMs);
    }

    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private const long Hour = 3600 * 1000L;

    [Fact]
    public void HealthIsDueAfterADayAnUntriagedAddOrAStaleItemAndAGroomClearsIt()
    {
        // never groomed: due
        Assert.True(Health(Now, Now).Due);
        Assert.Equal(0, Ah("groomed", "--summary", "{}").Code);
        var fresh = Health(Now, Now);
        Assert.False(fresh.Due);
        Assert.Null(fresh.DueSinceMs);
        Assert.False(fresh.Stalled);

        // 24 h after the groom: due, since then
        var day = Health(Now + (25 * Hour), Now + (25 * Hour));
        Assert.True(day.Due);
        Assert.Contains("24 h", day.Reason, StringComparison.Ordinal);

        // an untriaged item: due now
        Add("new");
        var untriaged = Health(Now, Now);
        Assert.True(untriaged.Due);
        Assert.Equal(1, untriaged.Untriaged);
        Assert.Equal(0, Ah("set", "b1", "--wave", "now").Code);
        Assert.Equal(0, Ah("groomed", "--summary", "{}").Code);
        Assert.False(Health(Now, Now).Due);

        // a not-started item in now untouched 14 days is stale; 13 days is not (the nearest input that must not match)
        Assert.Equal(0, Health(Now + (13 * 24 * Hour), Now + (13 * 24 * Hour)).Stale);
        var stale = Health(Now + (15 * 24 * Hour), Now + (15 * 24 * Hour));
        Assert.Equal(1, stale.Stale);
        Assert.True(stale.Due);
    }

    [Fact]
    public void HealthUsesTheReadState()
    {
        Add("a", "--wave", "now");
        Ah("groomed", "--summary", "{}");
        using var reader = StateDbModule.OpenReader(_sandbox.AhHome).Reader!;
        Hierarchy.BacklogHealth At(long nowMs, params Hierarchy.PlanItem[] items) => Hierarchy.HierarchyModule.BacklogHealthOf(reader.Query, _dir, nowMs, Now, items);

        // in review is not stale; the counts say so
        var review = At(Now + (20 * 24 * Hour), Child("in_review"));
        Assert.Equal(0, review.Stale);
        Assert.Equal(1, review.Items["now"]["in_review"]);
        Assert.False(review.Items["now"].ContainsKey("not_started"));

        // read in progress: three days from the latest child start, not from the row
        var started = Child("in_progress") with { StartedAt = DateTimeOffset.FromUnixTimeMilliseconds(Now + (20 * 24 * Hour)).ToString("O", System.Globalization.CultureInfo.InvariantCulture) };
        Assert.Equal(0, At(Now + (22 * 24 * Hour), started).Stale);
        Assert.Equal(1, At(Now + (24 * 24 * Hour), started).Stale);

        // negative: an item only queued leaves the row waiting, and it goes stale on the waiting clock
        Assert.Equal(0, At(Now + (13 * 24 * Hour), Child("next")).Stale);
        Assert.Equal(1, At(Now + (15 * 24 * Hour), Child("next")).Stale);
        Assert.Equal(1, At(Now, Child("next")).Items["now"]["not_started"]);
    }

    [Fact]
    public void NextPassesARowItsChecklistStarted()
    {
        Add("a", "--wave", "now");
        Add("b", "--wave", "now");

        _dirItems = [Child("in_progress")];
        Assert.Equal("b2", S(Json("next"), "item"));
        _dirItems = [Child("in_review")];
        Assert.Equal("b2", S(Json("next"), "item"));

        // negative: an item only queued, or dropped, has not started the row
        _dirItems = [Child("next")];
        Assert.Equal("b1", S(Json("next"), "item"));
        _dirItems = [Child("dropped")];
        Assert.Equal("b1", S(Json("next"), "item"));
    }

    [Fact]
    public void AReadInProgressRowInALaterWaveCanGoStale()
    {
        Add("a", "--wave", "later");
        Ah("groomed", "--summary", "{}");
        using var reader = StateDbModule.OpenReader(_sandbox.AhHome).Reader!;
        var later = Now + (20 * 24 * Hour);

        Assert.Equal(1, Hierarchy.HierarchyModule.BacklogHealthOf(reader.Query, _dir, later, Now, [Child("in_progress")]).Stale);
        // negative: waiting in a later wave never does
        Assert.Equal(0, Hierarchy.HierarchyModule.BacklogHealthOf(reader.Query, _dir, later, Now, [Child("next")]).Stale);
    }

    [Fact]
    public void InProgressIsStaleAfterThreeDaysAndALaterItemNever()
    {
        Add("a", "--wave", "now");
        Add("b", "--wave", "later");
        Assert.Equal(0, Ah("claim", "b1", "--holder", "impl-1").Code);
        Ah("groomed", "--summary", "{}");
        Assert.Equal(0, Health(Now + (2 * 24 * Hour), Now).Stale);
        Assert.Equal(1, Health(Now + (4 * 24 * Hour), Now).Stale);
        Assert.Equal(1, Health(Now + (20 * 24 * Hour), Now).Stale);
    }

    [Fact]
    public void AStaleStampOrADayDueIsStalled()
    {
        Ah("groomed", "--summary", "{}");
        Assert.False(Health(Now, Now - (29 * 60 * 1000)).Stalled);
        Assert.True(Health(Now, Now - (31 * 60 * 1000)).Stalled);
        Assert.True(Health(Now, null).Stalled);
        Assert.True(Health(Now + (49 * Hour), Now + (49 * Hour)).Stalled);
        Assert.False(Health(Now + (47 * Hour), Now + (47 * Hour)).Stalled);
    }

    private void PlantTicks(string text) =>
        Assert.True(global::Ah.StateDb.StateDbModule.OpenDirect(_sandbox.AhHome).Write(tx =>
        {
            tx.Execute("INSERT OR REPLACE INTO meta(key, value) VALUES (?1, ?2)", global::Ah.Cli.Composition.StallStore.Ticks, text);
            return true;
        }).Ok);

    [Fact]
    public void HealthVerbReadsTheJobStampAndSaysStalledWhenItIsOld()
    {
        Ah("groomed", "--summary", "{\"untriaged\":0}");
        PlantTicks("{\"backlog-watch\":{\"last_run_at\":\"" + DateTimeOffset.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture) + "\",\"daemon_pid\":1}}");
        var ok = Ah("health");
        Assert.Contains("backlog: ok", ok.Out, StringComparison.Ordinal);
        Assert.Contains("loop: ok", ok.Out, StringComparison.Ordinal);
        PlantTicks("{\"backlog-watch\":{\"last_run_at\":\"2020-01-01T00:00:00Z\",\"daemon_pid\":1}}");
        Assert.Contains("loop: STALLED", Ah("health").Out, StringComparison.Ordinal);
        Assert.True(Json("health").Member("stalled")!.Value.IsTruthy);
    }

    [Fact]
    public void StatusCarriesTheBacklogBlockAndTheDueLineIsSaidOncePerEpisode()
    {
        Assert.False(_graph.Hierarchy.Status.ComputeStatus(null, dir: _dir).ContainsKey("backlog"));
        Add("new");
        var block = _graph.Hierarchy.Status.ComputeStatus(null, dir: _dir).Get("backlog")!.Value;
        Assert.True(block.Member("due")!.Value.IsTruthy);
        Assert.Equal(1, (int)block.Member("untriaged")!.Value.AsNumber());

        var sid = JsonValue.FromString("sess-1");
        var line = _graph.Hierarchy.Peers.TakeBacklogDueLine(_dir, sid);
        Assert.StartsWith("ah: backlog groom due (1 untriaged", line, StringComparison.Ordinal);
        Assert.Null(_graph.Hierarchy.Peers.TakeBacklogDueLine(_dir, sid));
        Assert.NotNull(_graph.Hierarchy.Peers.TakeBacklogDueLine(_dir, JsonValue.FromString("sess-2")));

        // a groom ends the episode; nothing due, no line
        Ah("set", "b1", "--wave", "now");
        Ah("groomed", "--summary", "{}");
        Assert.Null(_graph.Hierarchy.Peers.TakeBacklogDueLine(_dir, sid));
        Assert.False(_graph.Hierarchy.Status.ComputeStatus(null, dir: _dir).Get("backlog")!.Value.Member("due")!.Value.IsTruthy);
    }

    [Fact]
    public void NeverGroomedIsDueFromTheFirstAddAndStallsOnlyADayLater()
    {
        var before = Now;
        Add("a", "--wave", "now");
        var h = Health(Now, Now);
        Assert.True(h.Due);
        Assert.True(h.DueSinceMs >= before - 1000, $"due_since {h.DueSinceMs} is before the first add {before}");
        Assert.False(h.Stalled);
        Assert.False(Health(Now + (23 * Hour), Now + (23 * Hour)).Stalled);
        Assert.True(Health(Now + (25 * Hour), Now + (25 * Hour)).Stalled);
    }

    [Fact]
    public void AGroomIsKeptInAMetaRowThatHealthReads()
    {
        Assert.Equal(0, Ah("groomed", "--summary", "{\"changed\":2}").Code);
        var row = Assert.Single(Q("SELECT value FROM meta WHERE key LIKE 'backlog.groom:%'"));
        var kept = JsonText.Parse((string)row[0]!);
        var h = Health(Now, Now);
        Assert.Equal((long)kept.Member("ms")!.Value.AsNumber(), h.LastGroomMs);
        Assert.Equal(2, (int)h.LastGroomSummary!.Value.Member("changed")!.Value.AsNumber());
    }

    [Fact]
    public void ARerankMoveKeepsTheMovedItemsChangeTimeAndAnUntriagedEditKeepsDueSince()
    {
        Add("a", "--wave", "now");
        Add("b", "--wave", "now");
        var bWas = (long)Q("SELECT updated_ms FROM plan_items WHERE item = 'b2'")[0][0]!;
        Thread.Sleep(5);
        for (var i = 0; i < 12; i++)
        {
            Add("c" + i, "--wave", "now", "--after", "b1");
        }

        var moved = Q("SELECT rank, updated_ms FROM plan_items WHERE item = 'b2'")[0];
        Assert.NotEqual(2048L, (long)moved[0]!);
        Assert.Equal(bWas, (long)moved[1]!);

        Ah("groomed", "--summary", "{}");
        var item = Add("new");
        var since = Health(Now, Now).DueSinceMs;
        Thread.Sleep(20);
        Assert.Equal(0, Ah("set", item, "--title", "renamed").Code);
        Assert.Equal(since, Health(Now, Now).DueSinceMs);
    }

    [Fact]
    public void CallerIsTheMemberElseTheSessionRoleElseUser()
    {
        global::Ah.Abstractions.IAhEnvironment Env(params (string K, string V)[] vars) =>
            new FakeEnv(_sandbox.Home, _sandbox.AhHome, vars.ToDictionary(static v => v.K, static v => (string?)v.V), null);
        Assert.Equal("impl-7", global::Ah.Cli.Verbs.BacklogVerb.Caller(Env(("AH_MEMBER_NAME", "impl-7"), ("CLAUDE_CODE_SESSION_ID", "s1")), _graph, _dir));
        Assert.Equal("user", global::Ah.Cli.Verbs.BacklogVerb.Caller(Env(), _graph, _dir));

        // a session with no recorded role and no team that names it is not the Orchestrator (the nearest input that must not match)
        Assert.Equal("user", global::Ah.Cli.Verbs.BacklogVerb.Caller(Env(("CLAUDE_CODE_SESSION_ID", "s-unknown")), _graph, _dir));
    }

    [Fact]
    public void AReimportRewritesTheDependsOfSkippedItems()
    {
        var file = Path.Combine(_sandbox.Root, "groom-1.json");
        File.WriteAllText(file, """[{"id":"N1","title":"one"},{"id":"N2","title":"two"}]""");
        Assert.Equal(0, Ah("import", file).Code);
        File.WriteAllText(file, """[{"id":"N1","title":"one"},{"id":"N2","title":"two","depends_on":["N1"]}]""");
        var again = Ah("import", file);
        Assert.Contains("imported 0, skipped 2", again.Out, StringComparison.Ordinal);
        Assert.Equal(["b1"], Json("show", "b2").Member("depends")!.Value.AsArray().Select(s => s.AsString()));
    }

    [Fact]
    public void AnImportWhereTwoElementsTakeTheSameIssueIsRefusedWhole()
    {
        var file = Path.Combine(_sandbox.Root, "groom-2.json");
        File.WriteAllText(file, """[{"id":"N1","title":"one","sources":["#5"]},{"id":"N2","title":"two","sources":["#5"]}]""");
        var r = Ah("import", file);
        Assert.Equal(2, r.Code);
        Assert.Contains("N1 and N2", r.Err, StringComparison.Ordinal);
        Assert.Empty(Json("list").AsArray());

        // different issues are fine (the nearest input that must not be refused)
        File.WriteAllText(file, """[{"id":"N1","title":"one","sources":["#5"]},{"id":"N2","title":"two","sources":["#6"]}]""");
        Assert.Equal(0, Ah("import", file).Code);
    }

    [Fact]
    public void ExportAsOfIsTheNewestChangeOfTheRowsItReturned()
    {
        Add("a");
        Add("b");
        var export = Json("export");
        Assert.Equal((long)Q("SELECT MAX(updated_ms) FROM plan_items")[0][0]!, (long)export.Member("as_of_ms")!.Value.AsNumber());
        var asOf = ((long)export.Member("as_of_ms")!.Value.AsNumber()).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var later = Json("export", "--since", asOf);
        Assert.Empty(later.Member("items")!.Value.AsArray());
        Assert.Equal(asOf, ((long)later.Member("as_of_ms")!.Value.AsNumber()).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void ImportMapsIdsRewritesDependsAndIsIdempotent()
    {
        var file = Path.Combine(_sandbox.Root, "groom-1.json");
        File.WriteAllText(file, """
            [
              {"id":"N1","title":"one","outcome":"o1","theme":"DB/perf","size":"L","wave":"Now","depends_on":[],"sources":["#458","branch x","#459"]},
              {"id":"N2","title":"two","outcome":"o2","theme":"t","size":"S","wave":"Next","depends_on":["N1"],"sources":[]},
              {"id":"X9","title":"nine","outcome":"o9","theme":"t","size":"M","wave":"Later","depends_on":["N2","Q1"],"sources":[]}
            ]
            """);
        var r = Ah("import", file);
        Assert.True(r.Code == 0, r.Err);
        Assert.Contains("imported 3, skipped 0", r.Out, StringComparison.Ordinal);

        var one = Json("show", "b1");
        Assert.Equal("github:o/r#458", S(one, "ext"));
        Assert.Equal(["branch x", "#459", "groom-1:N1"], one.Member("sources")!.Value.AsArray().Select(s => s.AsString()));
        Assert.Equal(["b1"], Json("show", "b2").Member("depends")!.Value.AsArray().Select(s => s.AsString()));
        var nine = Json("show", "b3");
        Assert.Equal(["b2"], nine.Member("depends")!.Value.AsArray().Select(s => s.AsString()));
        Assert.Contains("question:Q1", nine.Member("sources")!.Value.AsArray().Select(s => s.AsString()));
        Assert.Equal("waits on Q1", S(nine, "note"));
        Assert.Equal("later", S(nine, "wave"));

        var again = Ah("import", file);
        Assert.Contains("imported 0, skipped 3", again.Out, StringComparison.Ordinal);
        Assert.Equal(3, Json("list").AsArray().Count);
    }

    private long EventCount() => (long)Q("SELECT COUNT(*) FROM plan_events WHERE stream = 'backlog'")[0][0]!;

    /// <summary>Epic b1, deliverable b2 under it, task b3 under b2.</summary>
    private void Chain()
    {
        Assert.Equal("b1", Add("Epic", "--kind", "EPIC"));
        Assert.Equal("b2", Add("Deliverable", "--kind", "deliverable", "--parent", "b1"));
        Assert.Equal("b3", Add("Task", "--parent", "b2"));
    }

    [Fact]
    public void KindAndParentRoundTripThroughListShowAndExport()
    {
        Chain();
        var epic = Json("show", "b1");
        Assert.Equal("epic", S(epic, "kind"));
        Assert.Null(S(epic, "parent"));
        Assert.Equal(["b2"], epic.Member("children")!.Value.AsArray().Select(s => s.AsString()));
        var deliverable = Json("list").AsArray().Single(i => S(i, "item") == "b2");
        Assert.Equal("deliverable", S(deliverable, "kind"));
        Assert.Equal("b1", S(deliverable, "parent"));
        var task = Json("export").Member("items")!.Value.AsArray().Single(i => S(i, "item") == "b3");
        Assert.Null(S(task, "kind"));
        Assert.Equal("b2", S(task, "parent"));
        Assert.Null(Json("show", "b3").Member("children"));
        Assert.Contains("\"parent\":\"b2\"", (string)Q("SELECT doc FROM plan_items WHERE item = 'b3'")[0][0]!, StringComparison.Ordinal);
        Assert.Contains("\"children\"", Ah("show", "b2").Out, StringComparison.Ordinal);
    }

    [Fact]
    public void AnItemWithNoKindOrParentShowsNeither()
    {
        Add("Plain");
        var json = Ah("show", "b1", "--json").Out;
        Assert.DoesNotContain("kind", json, StringComparison.Ordinal);
        Assert.DoesNotContain("parent", json, StringComparison.Ordinal);
        Assert.DoesNotContain("children", json, StringComparison.Ordinal);
    }

    [Fact]
    public void KindTaskIsOmittedAndParentNoneClears()
    {
        Chain();
        Assert.Equal(0, Ah("set", "b2", "--kind", "task", "--parent", "b1").Code);
        Assert.Null(S(Json("show", "b2"), "kind"));
        Assert.Equal(0, Ah("set", "b3", "--parent", "none").Code);
        Assert.Null(S(Json("show", "b3"), "parent"));
    }

    [Fact]
    public void ListKindFiltersAndTaskTakesTheUntyped()
    {
        Chain();
        Assert.Equal(["b2"], Ids("--kind", "deliverable"));
        Assert.Equal(["b1"], Ids("--kind", "Epic"));
        Assert.Equal(["b3"], Ids("--kind", "task"));
        Assert.Empty(Ids("--kind", "saga"));
    }

    [Theory]
    [InlineData("set", "b3", "--parent", "12")] // not b<N>
    [InlineData("set", "b3", "--parent", "b99")] // missing
    [InlineData("set", "b3", "--parent", "b3")] // self
    [InlineData("set", "b3", "--parent", "b4")] // task under a task
    [InlineData("set", "b2", "--parent", "b5")] // deliverable under a deliverable
    [InlineData("set", "b3", "--kind", "epic")] // a kind change that its existing parent no longer outranks
    [InlineData("set", "b4", "--kind", "epic", "--parent", "b1")] // an epic can have no parent
    [InlineData("set", "b4", "--kind", "saga")] // not a kind
    [InlineData("add", "New", "--parent", "b4")] // a new task under a task
    public void ABrokenParentLinkIsExitTwoWithOneLineAndNothingWritten(params string[] args)
    {
        Chain();
        Assert.Equal("b4", Add("Other task"));
        Assert.Equal("b5", Add("Other deliverable", "--kind", "deliverable"));
        var before = EventCount();
        var docs = Q("SELECT item, doc FROM plan_items ORDER BY item").Select(r => (string)r[0]! + (string)r[1]!).ToList();

        var refused = Ah(args);

        Assert.Equal(2, refused.Code);
        Assert.Single(refused.Err.TrimEnd('\n').Split('\n'));
        Assert.Equal(before, EventCount());
        Assert.Equal(docs, Q("SELECT item, doc FROM plan_items ORDER BY item").Select(r => (string)r[0]! + (string)r[1]!).ToList());
        Assert.Equal(5, Json("list").AsArray().Count);
    }

    [Fact]
    public void AKindChangeThatOrphansChildrenIsAllowed()
    {
        Chain();
        Assert.Equal(0, Ah("set", "b1", "--kind", "task").Code);
        Assert.Null(S(Json("show", "b1"), "kind"));
        Assert.Equal("b1", S(Json("show", "b2"), "parent"));
    }

    [Fact]
    public void AnImportTakesKindAndParent()
    {
        Assert.Equal("b1", Add("Epic", "--kind", "epic"));
        var file = Path.Combine(_sandbox.Root, "epics.json");
        File.WriteAllText(file, """[{"id":"D1","title":"deliver","kind":"Deliverable","parent":"b1"}]""");
        Assert.Equal(0, Ah("import", file).Code);
        var d = Json("show", "b2");
        Assert.Equal("deliverable", S(d, "kind"));
        Assert.Equal("b1", S(d, "parent"));
    }
}
