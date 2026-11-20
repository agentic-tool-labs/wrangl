using System.Text;
using Ah.Core.Json;
using Ah.Watch.Model;

namespace Ah.Watch.Tests;

/// <summary>The <c>questions:&lt;dirId&gt;/&lt;team&gt;</c> slice and the <c>list:questions:</c> pager over a team's open pipeline run.</summary>
public sealed class QuestionsLaneTests
{
    private const string Dir = "/repo/.claude/ah2";

    private const string Team = "t1";

    private static readonly string Suffix = ActivityLane.DirId(Dir) + "/" + Team;

    private static readonly string SliceId = "questions:" + Suffix;

    private sealed class Runs(Func<string, JsonValue> byTeam) : IViewStateReader
    {
        public List<(string Domain, string Op, string Dir, JsonObject Args)> Calls { get; } = [];

        public JsonValue Read(string domain, string op, string hierarchyDir, JsonObject args)
        {
            Calls.Add((domain, op, hierarchyDir, args));
            return byTeam(args.Get("team") is { IsText: true } team ? team.AsString() : "");
        }
    }

    private static JsonValue Run(params string[] lines)
    {
        var rows = new JsonArray();
        foreach (var line in lines)
        {
            rows.Add(JsonText.Parse(line));
        }

        return new JsonObject { ["run_id"] = "run-1", ["started_at"] = JsonValue.Null, ["rows"] = rows };
    }

    [Fact]
    public void AnItemWhoseTitleIsItsRawIdReadsAsWords()
    {
        var raw = JsonText.Parse("""{"key":"_run:run-or-skip-it","item":"run-or-skip-it","stream":"_run","title":"_run:run-or-skip-it","note":"Run it?","since":"2026-11-02T11:00:00Z","options":[]}""");
        var body = JsonValue.FromObject(QuestionsLane.Fold(JsonValue.Null, Dir, Waiting(raw)).Body);

        Assert.Equal("run or skip it", body.Member("items")!.Value.AsArray()[0].Member("title")!.Value.AsString());
    }

    private static JsonValue WaitingItem(string item, string since, string note = "Which?", string options = "") =>
        JsonText.Parse($$"""{"key":"s:{{item}}","item":"{{item}}","stream":"s","title":"{{item}} title","state":"waiting_user","owner":"impl","refs":[],"note":"{{note}}","since":"{{since}}","options":[{{options}}]}""");

    private static Ah.Hierarchy.ChecklistWaitingList<JsonValue, JsonValue> Waiting(params JsonValue[] items) => new([], items);

    [Fact]
    public void ARegisteredItemCarriesItsWorkIdSoTheCardCanReadItsDescription()
    {
        var body = JsonValue.FromObject(QuestionsLane.Fold(JsonValue.Null, Dir, Waiting(WaitingItem("asked", "2026-11-02T11:00:00Z"), WaitingItem("bare", "2026-11-02T12:00:00Z")), workId: static (stream, key) => stream == "s" && key == "s:asked" ? "atp-7" : null).Body);

        var rows = body.Member("items")!.Value.AsArray();
        Assert.Equal("atp-7", rows[0].Member("work_id")!.Value.AsString());
        Assert.True(rows[1].Member("work_id")!.Value.IsNull);
    }

    [Fact]
    public void AWaitingItemIsServedWithNoRunWholeAndOldestFirst()
    {
        var options = "\"Ship it\",\"Hold\"";
        var note = new string('n', 300);
        var body = JsonValue.FromObject(QuestionsLane.Fold(JsonValue.Null, Dir, Waiting(WaitingItem("later", "2026-11-02T12:00:00Z"), WaitingItem("first", "2026-11-02T11:00:00Z", note, options))).Body);

        Assert.Null(body.Member("run"));
        Assert.Equal(2, body.Member("waiting_total")!.Value.AsNumber());
        Assert.False(body.Member("truncated")!.Value.AsBool());
        var rows = body.Member("items")!.Value.AsArray();
        Assert.Equal(["s:first", "s:later"], rows.Select(static r => r.Member("key")!.Value.AsString()));
        Assert.Equal((note, "first", "s", "first title", "impl", "2026-11-02T11:00:00Z"), (rows[0].Member("text")!.Value.AsString(), rows[0].Member("item")!.Value.AsString(), rows[0].Member("stream")!.Value.AsString(), rows[0].Member("title")!.Value.AsString(), rows[0].Member("owner")!.Value.AsString(), rows[0].Member("since")!.Value.AsString()));
        Assert.Equal(["Ship it", "Hold"], rows[0].Member("options")!.Value.AsArray().Select(static o => o.AsString()));
    }

    [Fact]
    public void ItemsStopAtTheirCapWithTheTotalWhole()
    {
        var items = Enumerable.Range(0, QuestionsLane.ItemCap + 1).Select(static i => WaitingItem($"i{i:00}", $"2026-11-02T11:00:{i:00}Z", "short")).ToArray();

        var body = JsonValue.FromObject(QuestionsLane.Fold(JsonValue.Null, Dir, Waiting(items)).Body);

        Assert.Equal(QuestionsLane.ItemCap, body.Member("items")!.Value.AsArray().Count);
        Assert.Equal(QuestionsLane.ItemCap + 1, body.Member("waiting_total")!.Value.AsNumber());
        Assert.True(body.Member("truncated")!.Value.AsBool());
        Assert.Equal("s:i00", body.Member("items")!.Value.AsArray()[0].Member("key")!.Value.AsString());
    }

    [Fact]
    public void ItemsStayInsideTheSliceBudget()
    {
        var label = new string('é', 32);
        var options = string.Join(',', Enumerable.Range(0, 4).Select(i => $"\"{label}{i}\""));
        var items = Enumerable.Range(0, QuestionsLane.ItemCap).Select(i => WaitingItem($"i{i:00}", $"2026-11-02T11:00:{i:00}Z", new string('é', 300), options)).ToArray();

        var fold = QuestionsLane.Fold(JsonValue.Null, Dir, Waiting(items));

        Assert.True(JsonText.ToUtf8(JsonValue.FromObject(fold.Body), 0).Length <= QuestionsLane.SliceBudget);
        Assert.True(fold.Body["truncated"].AsBool());
        Assert.Equal(QuestionsLane.ItemCap, fold.Body["waiting_total"].AsNumber());
    }

    [Fact]
    public void NegativeWithNothingWaitingTheItemsAreEmptyAndTheTotalIsTheOpenQuestions()
    {
        var body = JsonValue.FromObject(QuestionsLane.Fold(Run(Parked("d1", "2026-10-31T10:00:00Z"))).Body);

        Assert.Empty(body.Member("items")!.Value.AsArray());
        Assert.Equal(1, body.Member("waiting_total")!.Value.AsNumber());
        Assert.Equal(1, body.Member("open_total")!.Value.AsNumber());
    }

    private static string Parked(string id, string time, string question = "Q?", string extra = "") =>
        $$"""{"id":"{{id}}","time":"{{time}}","run":"run-1","kind":"parked","item":"item-{{id}}","source":"spec","question":"{{question}}","options":["Yes","No"],"default":"A","caller":"orchestrator@t1"{{extra}}}""";

    private static string Answered(string id, string answers, string time, string by = "user", string decision = "\"Yes\"", string extra = "") =>
        $$"""{"id":"{{id}}","time":"{{time}}","run":"run-1","kind":"answered","answers":"{{answers}}","choice":"A","decision":{{decision}},"answered_by":"{{by}}"{{extra}}}""";

    private static (QuestionsLane Lane, Runs Reader) Held(JsonValue run, Func<string, bool>? subscribed = null)
    {
        var reader = new Runs(team => team == Team ? run : JsonValue.Null);
        return (new QuestionsLane(reader, () => [Dir], subscribed ?? (id => id == SliceId)), reader);
    }

    private static JsonValue Body(QuestionsLane lane)
    {
        var change = Assert.Single(lane.Reread(Dir + '\u001f' + Team, 0).Changes);
        Assert.Equal(SliceId, change.Id);
        return JsonText.Parse(change.Body!);
    }

    private static string[] Ids(JsonValue body, string name) => [.. body.Member(name)!.Value.AsArray().Select(static r => r.Member("id")!.Value.AsString())];

    private static async Task<ListPage> Page(QuestionsLane lane, string sliceId, int limit = 50, string? after = null)
    {
        var pager = new QuestionsPager(() => 7, lane.RowsOf);
        var result = await pager.PageAsync(new ListRequest(sliceId, after, null, limit, new Dictionary<string, string>()), CancellationToken.None);
        return Assert.IsType<ListResult.Found>(result).Page;
    }

    [Fact]
    public void AParkedLineIsOpenUntilAnAnswerNamesItAndLineOrderDoesNotMatter()
    {
        var (lane, reader) = Held(Run(
            Answered("d3", "d2", "2026-10-30T10:05:00Z"),
            Parked("d1", "2026-10-30T10:00:00Z"),
            Parked("d2", "2026-10-30T10:01:00Z"),
            """{"id":"d4","time":"2026-10-30T10:06:00Z","run":"run-1","kind":"pushed"}"""));

        var body = Body(lane);

        Assert.Equal(["d1"], Ids(body, "open"));
        Assert.Equal(["d2"], Ids(body, "recent"));
        Assert.Equal(1, body.Member("open_total")!.Value.AsNumber());
        Assert.Null(body.Member("open_count"));
        Assert.False(body.Member("truncated")!.Value.AsBool());
        Assert.Equal("run-1", body.Member("run")!.Value.AsString());
        var call = Assert.Single(reader.Calls, static c => c.Op == "pipeline-run");
        Assert.Equal(("decisions", "pipeline-run", Dir), (call.Domain, call.Op, call.Dir));
        Assert.Equal(Team, call.Args.Get("team")!.Value.AsString());
    }

    private static string QaLine(string kind, string id, long at, string extra) =>
        $$"""{"v":1,"kind":"{{kind}}","id":"{{id}}","at":{{at}},"session_id":"s1","team":"{{Team}}"{{extra}}}""";

    [Fact]
    public async Task TheQuestionLogMergesIntoHistoryAsThreadsNewestFirstWithItsLaterRepliesFolded()
    {
        var t0 = DateTimeOffset.Parse("2026-10-30T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeMilliseconds();
        var log = new JsonArray
        {
            JsonText.Parse(QaLine("prompt", "p1", t0 + 120_000, ""","text":"why did it fail?","question":true""")),
            JsonText.Parse(QaLine("reply", "r1", t0 + 130_000, ",\"text\":\"Dispatched.\",\"prompt_id\":\"p1\",\"trigger\":\"typed\"")),
            JsonText.Parse(QaLine("reply", "r2", t0 + 900_000, ",\"text\":\"It was a missing using." + new string('x', QuestionsLane.ReplyHeadBytes) + "\",\"prompt_id\":\"p1\",\"trigger\":\"later\"")),
            JsonText.Parse(QaLine("prompt", "p2", t0 + 1_000_000, ""","text":"other team","question":false""").Replace(Team, "elsewhere", StringComparison.Ordinal)),
        };
        var run = Run(Parked("d1", "2026-10-30T10:00:00Z"), Answered("d2", "d1", "2026-10-30T10:01:00Z"));
        var reader = new Runs(team => team == Team ? run : team == "" ? new JsonObject { ["rows"] = log } : JsonValue.Null);
        var lane = new QuestionsLane(reader, () => [Dir], id => id == SliceId);

        var body = Body(lane);
        var page = await Page(lane, "list:questions:" + Suffix);

        Assert.Equal(["p1", "d1"], Ids(body, "recent"));
        Assert.Equal("jim", body.Member("recent")!.Value.AsArray()[0].Member("dir")!.Value.AsString());
        // the slice row carries the first reply's head for the list preview, never the later replies; a thread with no reply has none
        Assert.Equal("Dispatched.", body.Member("recent")!.Value.AsArray()[0].Member("reply")!.Value.AsString());
        Assert.Null(body.Member("recent")!.Value.AsArray()[0].Member("later"));
        Assert.Null(body.Member("recent")!.Value.AsArray()[1].Member("reply"));
        var rows = page.Rows;
        Assert.Equal(["p1", "d1"], rows.Select(static r => r.Member("id")!.Value.AsString()));
        var thread = rows[0];
        Assert.Equal("Dispatched.", thread.Member("reply")!.Value.AsString());
        // a long reply is held as a head and the row says so; the whole thread is list:qa-thread's
        var later = Assert.Single(thread.Member("later")!.Value.AsArray()).Member("text")!.Value.AsString();
        Assert.StartsWith("It was a missing using.", later, StringComparison.Ordinal);
        Assert.EndsWith("…", later, StringComparison.Ordinal);
        Assert.True(thread.Member("cut")!.Value.AsBool());
        Assert.True(thread.Member("question")!.Value.AsBool());
    }

    [Fact]
    public async Task AThreadsReplyIsItsDirectRowElseTheTypedTurnRowsFinal()
    {
        var t0 = DateTimeOffset.Parse("2026-10-30T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeMilliseconds();
        var log = new JsonArray
        {
            JsonText.Parse(QaLine("prompt", "p1", t0, ""","text":"why?","question":true""")),
            JsonText.Parse(QaLine("reply", "t1", t0 + 10_000, ",\"text\":\"Looking.\\n\\nAll done.\",\"final\":\"All done.\",\"prompt_id\":\"p1\",\"trigger\":\"typed\"")),
            JsonText.Parse(QaLine("reply", "dp1", t0 + 20_000, ",\"part\":\"direct\",\"text\":\"Looking.\",\"prompt_id\":\"p1\",\"trigger\":\"typed\"")),
            JsonText.Parse(QaLine("prompt", "p2", t0 + 30_000, ""","text":"and then?","question":true""")),
            JsonText.Parse(QaLine("reply", "t2", t0 + 40_000, ",\"text\":\"Ran it.\\n\\nIt passed.\",\"final\":\"It passed.\",\"prompt_id\":\"p2\",\"trigger\":\"typed\"")),
        };
        var reader = new Runs(team => team == Team ? Run(Parked("d1", "2026-10-30T09:00:00Z")) : team == "" ? new JsonObject { ["rows"] = log } : JsonValue.Null);
        var lane = new QuestionsLane(reader, () => [Dir], id => id == SliceId);

        _ = Body(lane);
        var rows = (await Page(lane, "list:questions:" + Suffix)).Rows;

        var p1 = Assert.Single(rows, static r => r.Member("id")!.Value.AsString() == "p1");
        Assert.Equal("Looking.", p1.Member("reply")!.Value.AsString());
        // negative: with no direct row the typed turn row's final is the reply, not its whole text
        var p2 = Assert.Single(rows, static r => r.Member("id")!.Value.AsString() == "p2");
        Assert.Equal("It passed.", p2.Member("reply")!.Value.AsString());
    }

    [Fact]
    public void AnOpenRowCarriesTheParkedFieldsAndARecentRowTheAnswer()
    {
        var (lane, _) = Held(Run(
            Parked("d1", "2026-10-30T10:00:00Z"),
            Parked("d2", "2026-10-30T10:01:00Z"),
            Answered("d3", "d2", "2026-10-30T10:05:00Z")));

        var body = Body(lane);
        var open = body.Member("open")!.Value.AsArray()[0];
        Assert.Equal(
            """{"id":"d1","at":1793354400000,"asker":"orchestrator@t1","source":"spec","header":"item-d1","text":"Q?","options":[{"label":"Yes"},{"label":"No"}],"default":"A"}""",
            Encoding.UTF8.GetString(JsonText.ToUtf8(open, 0)));
        var recent = body.Member("recent")!.Value.AsArray()[0];
        Assert.Equal(
            """{"id":"d2","text":"Q?","header":"item-d2","answer":"Yes","by":"user","at":1793354700000}""",
            Encoding.UTF8.GetString(JsonText.ToUtf8(recent, 0)));
    }

    [Fact]
    public void TheAnswerFallsBackToTheChoiceWhenTheDecisionIsNullOrEmpty()
    {
        var (lane, _) = Held(Run(
            Parked("d1", "2026-10-30T10:00:00Z"),
            Parked("d2", "2026-10-30T10:01:00Z"),
            Parked("d3", "2026-10-30T10:02:00Z"),
            Answered("d4", "d1", "2026-10-30T10:05:00Z", decision: "null"),
            Answered("d5", "d2", "2026-10-30T10:06:00Z", decision: "\"\""),
            Answered("d6", "d3", "2026-10-30T10:07:00Z", decision: "\"No\"")));

        var recent = Body(lane).Member("recent")!.Value.AsArray();

        Assert.Equal(["No", "A", "A"], recent.Select(static r => r.Member("answer")!.Value.AsString()));
    }

    [Fact]
    public void AnIsoTimeBecomesMillisecondsAndAnUnparsableOneIsOmittedAndSortsLast()
    {
        var (lane, _) = Held(Run(
            Parked("d1", "not a time"),
            Parked("d2", "2026-10-30T10:01:00Z"),
            Parked("d3", "2026-10-30T10:00:00Z")));

        var open = Body(lane).Member("open")!.Value.AsArray();

        Assert.Equal(["d3", "d2", "d1"], open.Select(static r => r.Member("id")!.Value.AsString()));
        Assert.Equal(1793354400000, open[0].Member("at")!.Value.AsNumber());
        Assert.Null(open[2].Member("at"));
    }

    [Fact]
    public async Task TheListHoldsParkedAndDecidedRowsNewestFirstWithTheSurrogateClass()
    {
        var (lane, _) = Held(Run(
            Parked("d1", "2026-10-30T10:00:00Z"),
            Answered("d2", "d1", "2026-10-30T10:05:00Z", by: "surrogate", extra: ",\"surrogate_class\":\"D3\""),
            """{"id":"d3","time":"2026-10-30T10:06:00Z","run":"run-1","kind":"decided","item":"i9","question":"Which?","options":["A one","B two"],"choice":"B","decision":"B two","decider":{"role":"architect","name":"architect-11","model":"m"},"rationale":"because"}""",
            Parked("d4", "2026-10-30T10:07:00Z"),
            Answered("d5", "d4", "2026-10-30T10:08:00Z", by: "user", extra: ",\"surrogate_class\":\"D3\"")));
        _ = Body(lane);

        var page = await Page(lane, "list:questions:" + Suffix);

        Assert.Equal(3, page.Total);
        Assert.Equal(
            """{"id":"d4","dir":"to_you","at":1793354820000,"header":"item-d4","text":"Q?","options":["Yes","No"],"run":"run-1","answer":"Yes","by":"user","answered_at":1793354880000}""",
            Encoding.UTF8.GetString(JsonText.ToUtf8(page.Rows[0], 0)));
        Assert.Equal(
            """{"id":"d3","dir":"decided","at":1793354760000,"header":"i9","text":"Which?","options":["A one","B two"],"run":"run-1","answer":"B two","by":"architect-11","rationale":"because"}""",
            Encoding.UTF8.GetString(JsonText.ToUtf8(page.Rows[1], 0)));
        Assert.Equal(
            """{"id":"d1","dir":"to_you","at":1793354400000,"header":"item-d1","text":"Q?","options":["Yes","No"],"run":"run-1","answer":"Yes","by":"surrogate","answered_at":1793354700000,"class":"D3"}""",
            Encoding.UTF8.GetString(JsonText.ToUtf8(page.Rows[2], 0)));
    }

    [Fact]
    public async Task StoredPromptsTheUserDidNotTypeAreHiddenWithTheirRepliesAndPagesStillFill()
    {
        var t0 = DateTimeOffset.Parse("2026-10-30T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeMilliseconds();
        string Prompt(string id, int minute, string text) => QaLine("prompt", id, t0 + (minute * 60_000), ",\"text\":" + JsonText.Stringify(JsonValue.FromString(text), 0));
        var log = new JsonArray
        {
            JsonText.Parse(Prompt("p1", 1, "why did it fail?")),
            JsonText.Parse(Prompt("b1", 2, "<agent-message from=\"aa96e7cdc5d731538\">[Subagent hand-back] The report follows:")),
            JsonText.Parse(QaLine("reply", "rb1", t0 + 150_000, ",\"text\":\"Relayed.\",\"prompt_id\":\"b1\",\"trigger\":\"typed\"")),
            JsonText.Parse(Prompt("p2", 3, "ship it")),
            JsonText.Parse(Prompt("b2", 4, "ah mail for orchestrator@t1 (1):\n- note from imp@t1: done\n[ah-entry e1]")),
            JsonText.Parse(Prompt("p3", 5, "and the docs?")),
            JsonText.Parse(Prompt("b3", 6, "<task-notification>agent finished</task-notification>")),
        };
        var reader = new Runs(team => team == "" ? new JsonObject { ["rows"] = log } : JsonValue.Null);
        var lane = new QuestionsLane(reader, () => [Dir], id => id == SliceId);

        _ = Body(lane);
        var first = await Page(lane, "list:questions:" + Suffix, limit: 2);
        var second = await Page(lane, "list:questions:" + Suffix, limit: 2, after: first.Next);

        Assert.Equal(["p3", "p2"], first.Rows.Select(static r => r.Member("id")!.Value.AsString()));
        Assert.Equal(["p1"], second.Rows.Select(static r => r.Member("id")!.Value.AsString()));
        // the hidden prompt's reply does not resurface as an unprompted thread
        Assert.DoesNotContain(Ah.Hierarchy.HierarchyModule.QaThreads([.. log]), static t => t.Replies.Count > 0);
    }

    [Fact]
    public async Task TheListPagesByCursor()
    {
        var (lane, _) = Held(Run([.. Enumerable.Range(1, 5).Select(i => Parked($"d{i}", $"2026-10-30T10:0{i}:00Z"))]));
        _ = Body(lane);

        var first = await Page(lane, "list:questions:" + Suffix, limit: 2);
        var second = await Page(lane, "list:questions:" + Suffix, limit: 2, after: first.Next);

        Assert.Equal(["d5", "d4"], first.Rows.Select(static r => r.Member("id")!.Value.AsString()));
        Assert.Equal(["d3", "d2"], second.Rows.Select(static r => r.Member("id")!.Value.AsString()));
    }

    [Fact]
    public async Task ThePagerIsNotFoundForASuffixNoClientHolds()
    {
        var (lane, _) = Held(Run(Parked("d1", "2026-10-30T10:00:00Z")));
        var pager = new QuestionsPager(() => 7, lane.RowsOf);

        var unheld = await pager.PageAsync(new ListRequest("list:questions:" + Suffix, null, null, 50, new Dictionary<string, string>()), CancellationToken.None);
        _ = Body(lane);
        var otherTeam = await pager.PageAsync(new ListRequest("list:questions:" + ActivityLane.DirId(Dir) + "/t2", null, null, 50, new Dictionary<string, string>()), CancellationToken.None);

        _ = Assert.IsType<ListResult.NotFound>(unheld);
        _ = Assert.IsType<ListResult.NotFound>(otherTeam);
    }

    [Fact]
    public async Task AReleasedSliceIsEvictedAndItsRowsDropped()
    {
        var held = true;
        var (lane, _) = Held(Run(Parked("d1", "2026-10-30T10:00:00Z")), id => held && id == SliceId);
        _ = Body(lane);
        Assert.Single(lane.Keys());

        held = false;
        var result = lane.Reread(Dir + '\u001f' + Team, 0);

        Assert.Empty(result.Changes);
        Assert.Equal([SliceId], result.Evict);
        Assert.Empty(lane.Keys());
        var page = await new QuestionsPager(() => 7, lane.RowsOf).PageAsync(new ListRequest("list:questions:" + Suffix, null, null, 50, new Dictionary<string, string>()), CancellationToken.None);
        _ = Assert.IsType<ListResult.NotFound>(page);
    }

    [Fact]
    public void ADirRoundTripsThroughItsDirIdAndAnUnknownIdResolvesToNothing()
    {
        string[] dirs = ["/other/.claude/ah2", Dir];

        Assert.Equal(Dir, WatchModule.DirOfId(ActivityLane.DirId(Dir), dirs));
        Assert.Null(WatchModule.DirOfId("00000000", dirs));
        Assert.Null(WatchModule.DirOfId(ActivityLane.DirId(Dir), ["/other/.claude/ah2"]));
    }

    [Fact]
    public void TheSliceCarriesTheResolvedDirBesideTheRunAndTheListRowsDoNot()
    {
        var (lane, _) = Held(Run(Parked("d1", "2026-10-30T10:00:00Z")));

        var body = Body(lane);

        Assert.Equal(Dir, body.Member("dir")!.Value.AsString());
        Assert.Equal(["run", "dir", "open", "recent", "open_total", "truncated", "waiting_total", "items"], body.AsObject().Keys.ToArray());
        Assert.All(lane.RowsOf(Suffix)!, static r => Assert.Equal("to_you", r.Row.Member("dir")!.Value.AsString()));
    }

    [Fact]
    public void AnUnknownDirIdResolvesNoKeyAndADirThatIsNotCanonicalOrHasNoRunGetsNoDir()
    {
        var reader = new Runs(_ => Run(Parked("d1", "2026-10-30T10:00:00Z")));
        var lane = new QuestionsLane(reader, () => [Dir], static _ => true);
        var other = "/elsewhere/.claude/ah2";

        var change = Assert.Single(lane.Reread(other + '\u001f' + Team, 0).Changes);

        Assert.Null(lane.KeyOfToken("questions:00000000/" + Team));
        Assert.Null(JsonText.Parse(change.Body!).Member("dir"));
        Assert.Null(JsonValue.FromObject(QuestionsLane.Fold(JsonValue.Null, Dir).Body).Member("dir"));
    }

    [Fact]
    public void AMultiParkedLineMarksItsRowsAndAnAnswerCarriesItsChoicesToTheListRow()
    {
        var fold = QuestionsLane.Fold(Run(
            Parked("d1", "2026-10-30T10:00:00Z", extra: ",\"multi\":true"),
            Parked("d2", "2026-10-30T10:01:00Z", extra: ",\"multi\":true"),
            Answered("d3", "d2", "2026-10-30T10:02:00Z", decision: "\"Yes; No\"", extra: ",\"choices\":[\"a\",\"b\"]")));

        var open = Assert.Single(fold.Body["open"].AsArray());
        Assert.True(open.Member("multi")!.Value.AsBool());
        var answered = fold.Rows.Single(r => r.Row.Member("id")!.Value.AsString() == "d2").Row;
        Assert.True(answered.Member("multi")!.Value.AsBool());
        Assert.Equal(["a", "b"], answered.Member("choices")!.Value.AsArray().Select(static c => c.AsString()));
        Assert.Equal("Yes; No", answered.Member("answer")!.Value.AsString());
        Assert.Equal("Yes; No", fold.Body["recent"].AsArray()[0].Member("answer")!.Value.AsString());
    }

    [Fact]
    public void ASingleParkedLineAndAMultiFalseLineHaveNoMultiKeyAndAnAnswerWithoutChoicesHasNoChoicesKey()
    {
        var fold = QuestionsLane.Fold(Run(
            Parked("d1", "2026-10-30T10:00:00Z"),
            Parked("d2", "2026-10-30T10:01:00Z", extra: ",\"multi\":false"),
            Parked("d3", "2026-10-30T10:02:00Z"),
            Answered("d4", "d3", "2026-10-30T10:03:00Z")));

        Assert.All(fold.Body["open"].AsArray(), static r => Assert.Null(r.Member("multi")));
        Assert.All(fold.Rows, static r => Assert.Null(r.Row.Member("multi")));
        Assert.All(fold.Rows, static r => Assert.Null(r.Row.Member("choices")));
    }

    [Fact]
    public void AnOptionEndingInRecommendedIsServedStrippedWithItsIndexAndAnUnmarkedTwinIsServedAsToday()
    {
        const string marked = "Spawn the architect per stream (Recommended)";
        const string shown = "Spawn the architect per stream";
        string Ask(string id, string time, string options, string def) =>
            $$"""{"id":"{{id}}","time":"{{time}}","run":"run-1","kind":"parked","question":"Q?","options":[{{options}}],"default":"{{def}}"}""";
        var fold = QuestionsLane.Fold(
            Run(
                Ask("d1", "2026-10-30T10:00:00Z", $"\"{marked}\",\"Wait\"", marked),
                Ask("d2", "2026-10-30T10:01:00Z", $"\"{shown}\",\"Wait\"", shown),
                Ask("d3", "2026-10-30T10:02:00Z", $"\"{marked}\",\"Wait\"", marked),
                Answered("a3", "d3", "2026-10-30T10:03:00Z", decision: $"\"{marked}\""),
                Ask("d4", "2026-10-30T10:04:00Z", $"\"{marked}\",\"Wait\"", marked),
                Answered("a4", "d4", "2026-10-30T10:05:00Z", decision: "\"Other (Recommended)\"")),
            Dir,
            Waiting(
                WaitingItem("marked", "2026-11-02T11:00:00Z", options: $"\"{marked}\",\"Wait\",\"B  (RECOMMENDED) \",7"),
                WaitingItem("twin", "2026-11-02T12:00:00Z", options: "\"(Recommended)\",\"A (Recommended) later\",\"A [Recommended]\",\"Recommended: A\"")));

        string Json(JsonValue v) => Encoding.UTF8.GetString(JsonText.ToUtf8(v, 0));
        var open = fold.Body["open"].AsArray();
        Assert.Equal("""{"id":"d1","at":1793354400000,"text":"Q?","options":[{"label":"Spawn the architect per …"},{"label":"Wait"}],"recommended":[0],"default":"Spawn the architect per stream"}""", Json(open[0]));
        Assert.Equal("""{"id":"d2","at":1793354460000,"text":"Q?","options":[{"label":"Spawn the architect per …"},{"label":"Wait"}],"default":"Spawn the architect per stream"}""", Json(open[1]));

        var items = fold.Body["items"].AsArray();
        Assert.Equal("""["Spawn the architect per stream","Wait","B",7]""", Json(items[0].Member("options")!.Value));
        Assert.Equal("[0,2]", Json(items[0].Member("recommended")!.Value));
        Assert.Equal("""["(Recommended)","A (Recommended) later","A [Recommended]","Recommended: A"]""", Json(items[1].Member("options")!.Value));
        Assert.Null(items[1].Member("recommended"));

        JsonValue Listed(string id) => fold.Rows.Single(r => r.Row.Member("id")!.Value.AsString() == id).Row;
        Assert.Equal("""{"id":"d3","dir":"to_you","at":1793354520000,"text":"Q?","options":["Spawn the architect per stream","Wait"],"recommended":[0],"run":"run-1","answer":"Spawn the architect per stream","by":"user","answered_at":1793354580000}""", Json(Listed("d3")));
        // an answer that only ends in the mark is no option's stored label, so it is served as given
        Assert.Equal("Other (Recommended)", Listed("d4").Member("answer")!.Value.AsString());
        Assert.Null(Listed("d2").Member("recommended"));
        Assert.Equal("""["Spawn the architect per stream","Wait"]""", Json(Listed("d2").Member("options")!.Value));
    }

    private static JsonValue Capped(int open, int answered, string text = "Q?", string answer = "\"Yes\"", int options = 2)
    {
        var lines = new List<string>();
        var labels = string.Join(",", Enumerable.Range(1, options).Select(i => "\"" + new string((char)('a' + i), 200) + "\""));
        for (var i = 1; i <= open + answered; i++)
        {
            var line = Parked($"p{i}", $"2026-10-30T{i / 60:00}:{i % 60:00}:00Z", text);
            lines.Add(options == 2 ? line : line.Replace("[\"Yes\",\"No\"]", "[" + labels + "]", StringComparison.Ordinal));
        }

        for (var i = open + 1; i <= open + answered; i++)
        {
            lines.Add(Answered($"a{i}", $"p{i}", $"2026-10-31T00:{i % 60:00}:00Z", decision: answer));
        }

        return Run([.. lines]);
    }

    [Fact]
    public void ElevenOpenAndElevenAnsweredAreCappedAndTruncated()
    {
        var (lane, _) = Held(Capped(11, 11));
        var body = Body(lane);

        Assert.Equal(10, body.Member("open")!.Value.AsArray().Count);
        Assert.Equal("p1", Ids(body, "open")[0]);
        Assert.Equal(11, body.Member("open_total")!.Value.AsNumber());
        Assert.Equal(10, body.Member("recent")!.Value.AsArray().Count);
        Assert.True(body.Member("truncated")!.Value.AsBool());
    }

    [Fact]
    public void TenOpenAndTenAnsweredAreNotTruncated()
    {
        var (lane, _) = Held(Capped(10, 10));
        var body = Body(lane);

        Assert.Equal(10, body.Member("open_total")!.Value.AsNumber());
        Assert.False(body.Member("truncated")!.Value.AsBool());
    }

    [Fact]
    public void ElevenAnsweredAloneTruncate()
    {
        var body = JsonValue.FromObject(QuestionsLane.Fold(Capped(0, 11)).Body);

        Assert.Equal(0, body.Member("open_total")!.Value.AsNumber());
        Assert.True(body.Member("truncated")!.Value.AsBool());
    }

    [Fact]
    public async Task LongTextIsCutInTheSliceAndKeptWholeInTheList()
    {
        var text = new string('x', 2048);
        var (lane, _) = Held(Capped(1, 1, text, "\"" + new string('z', 61) + "\"", options: 7));

        var body = Body(lane);
        var open = body.Member("open")!.Value.AsArray()[0];
        var recent = body.Member("recent")!.Value.AsArray()[0];
        var rows = (await Page(lane, "list:questions:" + Suffix)).Rows;

        Assert.Equal(new string('x', 200) + "…", open.Member("text")!.Value.AsString());
        var labels = open.Member("options")!.Value.AsArray();
        Assert.Equal(4, labels.Count);
        Assert.Equal(7, open.Member("options_total")!.Value.AsNumber());
        Assert.Equal(new string('b', 24) + "…", labels[0].Member("label")!.Value.AsString());
        Assert.Equal(new string('x', 100) + "…", recent.Member("text")!.Value.AsString());
        Assert.Equal(new string('z', 48) + "…", recent.Member("answer")!.Value.AsString());
        Assert.All(rows, r => Assert.Equal(text, r.Member("text")!.Value.AsString()));
        Assert.All(rows, r => Assert.Equal(7, r.Member("options")!.Value.AsArray().Count));
        Assert.Equal(new string('z', 61), rows[0].Member("answer")!.Value.AsString());
    }

    [Fact]
    public void FourOptionsCarryNoTotal()
    {
        var open = JsonValue.FromObject(QuestionsLane.Fold(Capped(1, 0, options: 4)).Body).Member("open")!.Value.AsArray()[0];

        Assert.Equal(4, open.Member("options")!.Value.AsArray().Count);
        Assert.Null(open.Member("options_total"));
    }

    [Fact]
    public void TheCutCountsUtf8BytesAndKeepsWholeCharacters()
    {
        Assert.Equal(new string('x', 200), QuestionsLane.Cut(new string('x', 200), QuestionsLane.OpenTextBytes));
        Assert.Equal(new string('x', 200) + "…", QuestionsLane.Cut(new string('x', 201), QuestionsLane.OpenTextBytes));
        Assert.Equal(new string('é', 100), QuestionsLane.Cut(new string('é', 100), QuestionsLane.OpenTextBytes));
        Assert.Equal(new string('é', 100) + "…", QuestionsLane.Cut(new string('é', 101), QuestionsLane.OpenTextBytes));
        Assert.Equal("a" + new string('é', 99) + "…", QuestionsLane.Cut("a" + new string('é', 100), QuestionsLane.OpenTextBytes));
        Assert.Equal("😀…", QuestionsLane.Cut("😀😀", 5));
    }

    [Fact]
    public async Task NoOpenRunIsAnEmptyBodyAndAnEmptyList()
    {
        var (lane, _) = Held(JsonValue.Null);

        var body = Body(lane);
        var page = await Page(lane, "list:questions:" + Suffix);

        Assert.Equal("""{"open":[],"recent":[],"open_total":0,"truncated":false,"waiting_total":0,"items":[]}""", Encoding.UTF8.GetString(JsonText.ToUtf8(body, 0)));
        Assert.Equal(0, page.Total);
    }

    [Fact]
    public void ADirTheDaemonDoesNotKnowIsEmptyWithoutARead()
    {
        var reader = new Runs(_ => Run(Parked("d1", "2026-10-30T10:00:00Z")));
        var lane = new QuestionsLane(reader, () => ["/other/.claude/ah2"], id => id == SliceId);

        var body = Body(lane);

        Assert.Empty(reader.Calls);
        Assert.Equal(0, body.Member("open_total")!.Value.AsNumber());
    }

    [Fact]
    public void MalformedRowsAreSkippedAndCountedAndOtherKindsIgnored()
    {
        var (lane, _) = Held(Run(
            "\"a string\"",
            """{"id":"d9","time":"2026-10-30T10:00:00Z"}""",
            """{"time":"2026-10-30T10:00:00Z","kind":"parked","question":"no id"}""",
            """{"id":"o1","time":"2026-10-30T10:00:00Z","run":"run-1","kind":"operational","question":"op?"}""",
            """{"kind":"merge","pr":3,"time":"2026-10-30T10:00:00Z","ran":true}""",
            Parked("d1", "2026-10-30T10:00:00Z")));

        var body = Body(lane);

        Assert.Equal(["d1"], Ids(body, "open"));
        Assert.Equal(3, lane.Malformed);
    }

    [Fact]
    public void AnAnswerNamingNoParkedIdIsIgnoredAndADuplicateParkedIdKeepsTheFirstLine()
    {
        var (lane, _) = Held(Run(
            Parked("d1", "2026-10-30T10:00:00Z", "first"),
            Parked("d1", "2026-10-30T10:01:00Z", "second"),
            Answered("d2", "d77", "2026-10-30T10:05:00Z")));

        var body = Body(lane);
        var rows = QuestionsLane.Fold(Run(
            Parked("d1", "2026-10-30T10:00:00Z", "first"),
            Answered("d2", "d77", "2026-10-30T10:05:00Z"))).Rows;

        Assert.Equal(["d1"], Ids(body, "open"));
        Assert.Equal("first", body.Member("open")!.Value.AsArray()[0].Member("text")!.Value.AsString());
        Assert.Empty(body.Member("recent")!.Value.AsArray());
        Assert.Single(rows);
    }

    [Fact]
    public void TheTokenNamesAKnownDirAndOneTeam()
    {
        var (lane, _) = Held(JsonValue.Null);
        var dirId = ActivityLane.DirId(Dir);

        Assert.Equal(Dir + '\u001f' + Team, lane.KeyOfToken(SliceId));
        Assert.Null(lane.KeyOfToken("questions:" + dirId + "/t1/extra"));
        Assert.Null(lane.KeyOfToken("questions:" + dirId + "/"));
        Assert.Null(lane.KeyOfToken("questions:unknown/t1"));
        Assert.Null(lane.KeyOfToken("plan-items:" + dirId + "/t1"));
    }

    private const int Budget = 10240;

    // The worst case at the caps: 11 open and 11 answered so both caps cut, and every capped field (text, labels, header, asker,
    // source, default, answer, by) 2000 two-byte characters, far past its cut; seven options each.
    private static JsonValue WorstCase()
    {
        var wide = new string('é', 2000);
        var options = string.Join(",", Enumerable.Repeat("\"" + wide + "\"", 7));
        var lines = new List<string>();
        for (var i = 1; i <= 22; i++)
        {
            lines.Add($$"""{"id":"d{{i}}","time":"2026-10-30T10:{{i:00}}:00Z","run":"run-1","kind":"parked","item":"{{wide}}","source":"{{wide}}","question":"{{wide}}","options":[{{options}}],"default":"{{wide}}","caller":"{{wide}}","multi":true}""");
        }

        for (var i = 12; i <= 22; i++)
        {
            lines.Add($$"""{"id":"d{{i + 100}}","time":"2026-10-31T00:{{i:00}}:00Z","run":"run-1","kind":"answered","answers":"d{{i}}","choice":"A","decision":"{{wide}}","answered_by":"{{wide}}"}""");
        }

        return Run([.. lines]);
    }

    [Fact]
    public void TheWorstCaseAtTheCapsFitsTheBudget()
    {
        // every parked line multi, and the slice's dir; the dir is not cut, so a long one eats the headroom (about 350 bytes here)
        var bytes = JsonText.ToUtf8(JsonValue.FromObject(QuestionsLane.Fold(WorstCase(), Dir).Body), 0).Length;

        Assert.True(bytes <= Budget, $"{bytes} bytes");
    }

    [Fact]
    public void NegativeAReplyPreviewNeverCostsAWaitingItemOrTheDir()
    {
        // ten replied threads at the caps beside the worst-case run and twenty waiting items: the previews are what goes
        var t0 = DateTimeOffset.Parse("2026-11-02T09:00:00Z", System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeMilliseconds();
        var wide = new string('é', 2000);
        List<JsonValue> Log(bool replied)
        {
            var log = new List<JsonValue>();
            for (var i = 0; i < 10; i++)
            {
                log.Add(JsonText.Parse(QaLine("prompt", "p" + i, t0 + (i * 1000), ",\"text\":\"" + wide + "\"")));
                if (replied)
                {
                    log.Add(JsonText.Parse(QaLine("reply", "r" + i, t0 + (i * 1000) + 500, ",\"text\":\"" + wide + "\",\"prompt_id\":\"p" + i + "\",\"trigger\":\"typed\"")));
                }
            }

            return log;
        }

        var items = Enumerable.Range(0, 20).Select(i => WaitingItem("w" + i, "2026-11-02T11:00:00Z", new string('n', 300), "\"Ship it\",\"Hold\"")).ToArray();
        JsonObject Body(bool replied) => QuestionsLane.Fold(WorstCase(), Dir, Waiting(items), Ah.Hierarchy.HierarchyModule.QaThreads([.. Log(replied)])).Body;

        var with = Body(true);

        // the rule: while any waiting item is left out, no recent row keeps a preview (a replied row's reply_at and count stay)
        Assert.True(with["items"].AsArray().Count < items.Length);
        Assert.DoesNotContain(with["recent"].AsArray(), static r => r.Member("reply") is not null);
        Assert.Contains(with["recent"].AsArray(), static r => r.Member("reply_at") is not null);
        Assert.True(JsonText.ToUtf8(JsonValue.FromObject(with), 0).Length <= Budget);

        // with room to spare the preview stays
        var roomy = QuestionsLane.Fold(JsonValue.Null, Dir, Waiting(items[0]), Ah.Hierarchy.HierarchyModule.QaThreads([.. Log(true)])).Body;
        Assert.Contains(roomy["recent"].AsArray(), static r => r.Member("reply") is not null);
    }

    [Fact]
    public void ADirThatWouldBreakTheBudgetAtTheCapsIsLeftOutWholeAndAShortOneIsKept()
    {
        var longDir = "/" + new string('p', 1023);

        var dropped = QuestionsLane.Fold(WorstCase(), longDir).Body;
        var kept = QuestionsLane.Fold(Run(Parked("d1", "2026-10-30T10:00:00Z")), longDir).Body;

        Assert.False(dropped.ContainsKey("dir"));
        Assert.True(JsonText.ToUtf8(JsonValue.FromObject(dropped), 0).Length <= Budget);
        Assert.Equal(longDir, kept["dir"].AsString());
        Assert.Equal(Budget, QuestionsLane.SliceBudget);
    }

    [Fact]
    public void TheSameRowsUncappedBreakTheBudget()
    {
        // The list rows of the same run carry the full text and every option, as an uncapped slice would.
        var rows = new JsonArray();
        rows.AddRange(QuestionsLane.Fold(WorstCase()).Rows.Take(QuestionsLane.OpenCap + QuestionsLane.RecentCap).Select(static r => r.Row));

        Assert.True(JsonText.ToUtf8(JsonValue.FromArray(rows), 0).Length > Budget);
    }

    [Fact]
    public void ClosingTheRunEmptiesTheSliceOnTheNextReRead()
    {
        var open = true;
        var reader = new Runs(team => open ? Run(Parked("d1", "2026-10-30T10:00:00Z")) : JsonValue.Null);
        var lane = new QuestionsLane(reader, () => [Dir], id => id == SliceId);
        Assert.Equal(["d1"], Ids(Body(lane), "open"));

        open = false;
        var emitted = new List<(string Lane, string Key)>();
        lane.EmitHeldOfDir(Dir, (l, k) => emitted.Add((l, k)));
        var (name, key) = Assert.Single(emitted);
        var change = Assert.Single(lane.Reread(key, 0).Changes);

        Assert.Equal(QuestionsLane.LaneName, name);
        Assert.Equal("""{"open":[],"recent":[],"open_total":0,"truncated":false,"waiting_total":0,"items":[]}""", Encoding.UTF8.GetString(change.Body!));
    }

    [Fact]
    public void AWriteInAnotherDirReEmitsNothing()
    {
        var (lane, _) = Held(Run(Parked("d1", "2026-10-30T10:00:00Z")));
        _ = Body(lane);
        var emitted = new List<string>();

        lane.EmitHeldOfDir("/elsewhere/.claude/ah2", (_, key) => emitted.Add(key));

        Assert.Empty(emitted);
    }

    [Fact]
    public void ADecisionsReReadNamesItsDirAndOnlyThatDirsHeldTeamsAreReRead()
    {
        var (lane, _) = Held(JsonValue.Null);
        _ = Body(lane);
        var inner = new DecisionsLane(new Runs(_ => JsonValue.Null), () => [Dir]);
        var named = new List<string>();
        var notifier = new DecisionsRereadNotifier(inner, key => named.Add(LaneParts.Split(key).First));

        _ = notifier.Reread(DecisionsLane.KeyOf(Dir, "run-1"), 0);
        _ = notifier.Reread(DecisionsLane.KeyOf("/elsewhere/.claude/ah2", "run-2"), 0);

        Assert.Equal([Dir, "/elsewhere/.claude/ah2"], named);
        Assert.Equal([Dir + '\u001f' + Team], lane.HeldOfDir(named[0]));
        Assert.Empty(lane.HeldOfDir(named[1]));
        Assert.Equal(DecisionsLane.LaneName, notifier.Name);
        Assert.Same(inner, notifier.Inner);
        Assert.Equal(inner.SliceIds(DecisionsLane.KeyOf(Dir, "run-1")), notifier.SliceIds(DecisionsLane.KeyOf(Dir, "run-1")));
    }
}
