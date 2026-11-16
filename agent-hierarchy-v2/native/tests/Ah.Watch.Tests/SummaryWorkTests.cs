using Ah.Core.Json;
using Ah.Hierarchy;
using Ah.Watch.Model;

namespace Ah.Watch.Tests;

/// <summary>ah-ui-focus §3: what a team's summary counts as waiting and in progress, from its plan items and open decisions.</summary>
public sealed class SummaryWorkTests
{
    private const string Dir = "/canon/hier";
    private const string Name = "ship";

    private sealed class Records : IRecordView
    {
        public Dictionary<(string Lane, string Key), LaneRecord> Rows { get; } = [];

        public LaneRecord? Get(string lane, string key) => Rows.GetValueOrDefault((lane, key));

        public IReadOnlyList<string> Keys(string lane) => [.. Rows.Keys.Where(k => k.Lane == lane).Select(static k => k.Key).Order(StringComparer.Ordinal)];
    }

    private static string Item(string item, string state, string owner = "impl", string refs = "") =>
        $$"""{"key":"s:{{item}}","item":"{{item}}","stream":"s","title":"{{item}} title","state":"{{state}}","owner":"{{owner}}","refs":[{{refs}}],"note":"{{item}} note","since":"2026-11-02T10:00:00Z"}""";

    private static Records With(string[] items, string[]? open = null)
    {
        var records = new Records();
        records.Rows[(ProgressLane.LaneName, ProgressLane.KeyOf(Dir, Name))] = new LaneRecord(JsonText.Parse($$"""{"streams":[],"work_items":[{{string.Join(',', items)}}]}"""), 1);
        records.Rows[(StatusLane.LaneName, Dir)] = new LaneRecord(JsonText.Parse("""{"teams":[{"team":"ship","pipeline":{"anchor_id":"run1"}}]}"""), 1);
        if (open is not null)
        {
            records.Rows[(DecisionsLane.LaneName, DecisionsLane.KeyOf(Dir, "run1"))] = new LaneRecord(JsonText.Parse($$"""{"open":[{{string.Join(',', open)}}]}"""), 1);
        }

        return records;
    }

    private const long Now = 1_800_000_000_000;

    private static (long Waiting, long Working, long Stuck, long Unfinished, IReadOnlyList<JsonObject> Rows) Work(Records records, ChecklistLiveness live) =>
        SummaryData.PlanWork(records, ActivityLane.DirId(Dir), new TeamEntry(Dir, "/t", Name, JsonValue.Null, 1), live, Now);

    private static ChecklistLiveness Live(string[] working, string[]? openTo = null) => new(working.ToHashSet(StringComparer.Ordinal), (openTo ?? []).ToHashSet(StringComparer.Ordinal));

    private static string Dated(string item, string state, long lastMs, string owner = "impl") =>
        Item(item, state, owner).TrimEnd('}') + ",\"last\":\"" + DateTimeOffset.FromUnixTimeMilliseconds(lastMs).ToString("O", System.Globalization.CultureInfo.InvariantCulture) + "\"}";

    [Fact]
    public void ItemsInReviewAndApprovedAreCountedAndNegativeAStateNotListedIsNot()
    {
        var records = new Records();
        records.Rows[(ProgressLane.LaneName, ProgressLane.KeyOf(Dir, Name))] = new LaneRecord(JsonText.Parse("""{"streams":[{"state":"open","counts":{"done":1,"in_review":2,"approved":3,"made_up":9}}]}"""), 1);
        var plan = SummaryData.PlanCounts(records, ActivityLane.DirId(Dir), Name);
        Assert.Equal((1, 6), (plan.ItemsDone, plan.ItemsTotal));
    }

    [Fact]
    public void StuckIsWhatTheStuckColumnDraws()
    {
        var old = Now - (2 * (long)HierarchyModule.ChecklistStaleMs);
        var records = With([Dated("b", "blocked", old), Dated("n", "in_progress", Now), Dated("w", "in_progress", Now, "busy"), Dated("s", "in_progress", old)]);
        // blocked at any age and no one on it count; negative: the one being worked, and the stale one the column hides
        Assert.Equal(2, Work(records, Live(["busy"])).Stuck);
    }

    [Fact]
    public void UnfinishedIsInReviewApprovedWaitingOnTheOrchestratorOrBlocked()
    {
        var old = Now - (2 * (long)HierarchyModule.ChecklistStaleMs);
        var records = With([Dated("r", "in_review", old), Dated("a", "approved", old), Dated("o", "waiting_orch", old), Dated("b", "blocked", old), Dated("n", "in_progress", Now), Dated("s", "in_progress", old), Dated("d", "done", old), Item("c", "in_progress", refs: "\"x\"")]);
        // negative: no one on it, stale, done, and in progress with its exchanges closed
        Assert.Equal(4, Work(records, Live([]) with { Open = new HashSet<string>(StringComparer.Ordinal) }).Unfinished);
    }

    [Fact]
    public void AWaitingItemOfATeamThatNoLongerExistsShowsNowhere()
    {
        var records = With([Item("q", "waiting_user")]);
        Assert.Empty(TreeData.Teams(records));
        Assert.Null(TreeData.FindTeam(records, ActivityLane.DirId(Dir), Name));
        // the item is not deleted: it stays in the plan
        Assert.Single(SummaryData.WorkItems(records, ActivityLane.DirId(Dir), Name));
    }

    private sealed class TeamReader(Func<string, JsonValue> answer) : Ah.Watch.IViewStateReader
    {
        public JsonValue Read(string domain, string op, string hierarchyDir, JsonObject args) => answer(op);
    }

    private static JsonValue Listing(string json) => JsonText.Parse(json);

    private sealed class PlanAndTeamReader(bool listed, string plannedEvent) : Ah.Watch.IViewStateReader
    {
        public JsonValue Read(string domain, string op, string hierarchyDir, JsonObject args) => (domain, op) switch
        {
            ("team", "read") => JsonValue.Null,
            ("team", _) => Listing(listed ? $$"""{"teams":[{"name":"{{Name}}","path":"x"}]}""" : """{"teams":[]}"""),
            (_, "streams") => Listing("""{"streams":[]}"""),
            _ => Listing($$"""{"streams":[{"stream":"s","offset":2,"length":2,"rows":[{"ts":"2026-06-01T10:00:00Z","stream":"s","item":"ask","event":"add","title":"Ask","by":"o@t"},{"ts":"2026-06-01T10:01:00Z","stream":"s","item":"ask","event":"{{plannedEvent}}","note":"Which?","by":"o@t"}]}]}"""),
        };
    }

    [Theory]
    [InlineData(true, "wait-user", true)]
    [InlineData(false, "wait-user", false)] // negative: no file and no row
    [InlineData(true, "start", false)] // negative: nothing waits, so the team is not asked about
    public void ThePlanRecordSaysATeamStillExistsOnlyForAWaitingItem(bool listed, string plannedEvent, bool expected)
    {
        var lane = new ProgressLane(new PlanAndTeamReader(listed, plannedEvent), TestFolds.New, static () => [Dir]);
        var record = lane.Reread(ProgressLane.KeyOf(Dir, Name), Now).Record!;
        Assert.Equal(expected, record.Value.Member("team_exists") is { IsBool: true });
    }

    [Fact]
    public void ATeamWithNoFileAndNoRowIsGone() =>
        Assert.False(ProgressLane.TeamExists(new TeamReader(static op => op == "read" ? JsonValue.Null : Listing("""{"teams":[]}""")), Dir, Name));

    [Fact]
    public void NegativeAFileThatDoesNotReadIsNotGone() =>
        Assert.True(ProgressLane.TeamExists(new TeamReader(op => op == "read" ? JsonValue.Null : Listing($$"""{"teams":[{"name":"{{Name}}","path":"x"}]}""")), Dir, Name));

    [Fact]
    public void NegativeAnotherTeamsFileDoesNotKeepThisOne() =>
        Assert.False(ProgressLane.TeamExists(new TeamReader(static op => op == "read" ? JsonValue.Null : Listing("""{"teams":[{"name":"other","path":"x"},{"name":null,"path":"y"}]}""")), Dir, Name));

    [Fact]
    public void NegativeARowInTheStoreWithNoFileIsNotGone() =>
        Assert.True(ProgressLane.TeamExists(new TeamReader(static op => op == "read" ? Listing("""{"members":[]}""") : Listing("""{"teams":[]}""")), Dir, Name));

    [Fact]
    public void NegativeAReadThatFailsIsNotGone() =>
        Assert.True(ProgressLane.TeamExists(new TeamReader(static _ => throw new InvalidOperationException("busy")), Dir, Name));

    [Fact]
    public void NegativeAWaitingItemWhoseTeamExistsButDoesNotReadStillShows()
    {
        var records = With([Item("q", "waiting_user")]);
        records.Rows[(ProgressLane.LaneName, ProgressLane.KeyOf(Dir, Name))] = new LaneRecord(JsonText.Parse($$"""{"streams":[],"team_exists":true,"work_items":[{{Item("q", "waiting_user")}}]}"""), 2);
        var team = Assert.Single(TreeData.Teams(records));
        Assert.Equal((Dir, Name), (team.Dir, team.Name));
        Assert.Empty(team.MemberNames);
        var id = "team-summary:" + ActivityLane.DirId(Dir) + "/" + Name;
        Assert.True(JsonText.Parse(new TeamSummaryComposer().Compose(id, records).Body!).Member("no_record") is { IsBool: true });
    }

    [Fact]
    public void NegativeAWaitingItemOfATeamWithARecordStillShows()
    {
        var records = With([Item("q", "waiting_user")]);
        records.Rows[(TeamLane.LaneName, TeamLane.KeyOf(Dir, Dir + "/teams/" + Name + ".json"))] = new LaneRecord(JsonText.Parse("{}"), 1);
        var team = Assert.Single(TreeData.Teams(records));
        Assert.Equal(Name, team.Name);
        Assert.Equal(1, SummaryData.Waiting(records, ActivityLane.DirId(Dir), team).Count);
    }

    [Fact]
    public void AnItemWaitingOnTheUserIsCountedWithARowCarryingItsQuestion()
    {
        var work = Work(With([Item("ask", "waiting_user")]), Live([]));

        Assert.Equal(1, work.Waiting);
        var row = Assert.Single(work.Rows);
        Assert.Equal(("item", "s:ask", "ask note"), (row["kind"].AsString(), row["key"].AsString(), row["note"].AsString()));
    }

    [Fact]
    public void ADecisionNamingAnItemIsOneThingWaitingNotTwo()
    {
        var work = Work(With([Item("ask", "waiting_user")], ["""{"id":"d1","question":"Which?","item":"ask","time":"2026-11-02T11:00:00Z"}"""]), Live([]));

        Assert.Equal(1, work.Waiting);
        var row = Assert.Single(work.Rows);
        Assert.Equal(("decision", "d1"), (row["kind"].AsString(), row["key"].AsString()));
    }

    [Fact]
    public void NegativeADecisionNamingAnotherItemDoesNotHideThisOne()
    {
        var work = Work(With([Item("ask", "waiting_user")], ["""{"id":"d1","question":"Which?","item":"other","time":"2026-11-02T11:00:00Z"}"""]), Live([]));

        Assert.Equal(2, work.Waiting);
        Assert.Equal(2, work.Rows.Count);
    }

    [Fact]
    public void TheCountIsTheRowsWhateverTheStatusDocumentSays()
    {
        var records = With([], ["""{"id":"d1","question":"Which?","item":null,"time":"2026-11-02T11:00:00Z"}"""]);
        records.Rows[(StatusLane.LaneName, Dir)] = new LaneRecord(JsonText.Parse("""{"teams":[{"team":"ship","pipeline":{"anchor_id":"run1","waiting_on_user":2}}]}"""), 1);

        var work = Work(records, Live([]));

        Assert.Equal(1, work.Waiting);
        Assert.Single(work.Rows);
    }

    [Fact]
    public void NegativeAStatusNumberWithNothingOpenHasNoCountAndNoRow()
    {
        var records = With([]);
        records.Rows[(StatusLane.LaneName, Dir)] = new LaneRecord(JsonText.Parse("""{"teams":[{"team":"ship","pipeline":{"anchor_id":"run1","waiting_on_user":3}}]}"""), 1);

        var work = Work(records, Live([]));

        Assert.Equal(0, work.Waiting);
        Assert.Empty(work.Rows);
    }

    [Fact]
    public void TheSummaryAndTheQuestionsSliceReadOneNumber()
    {
        var records = With(
            [Item("ask", "waiting_user"), Item("named", "waiting_user"), Item("busy", "in_progress")],
            ["""{"id":"d1","question":"Which?","item":"named","time":"2026-11-02T11:00:00Z"}"""]);
        var team = new TeamEntry(Dir, "/t", Name, JsonValue.Null, 1);

        var work = Work(records, Live([]));
        var waiting = SummaryData.Waiting(records, ActivityLane.DirId(Dir), team);
        var body = JsonValue.FromObject(QuestionsLane.Fold(JsonValue.Null, Dir, waiting).Body);

        Assert.Equal(2, work.Waiting);
        Assert.Equal(work.Rows.Count, work.Waiting);
        Assert.Equal(work.Waiting, (long)body.Member("waiting_total")!.Value.AsNumber());
        Assert.Equal("s:ask", Assert.Single(body.Member("items")!.Value.AsArray()).Member("key")!.Value.AsString());
    }

    [Fact]
    public void InProgressCountsOnlyAnItemSomeoneIsOn()
    {
        var work = Work(With([Item("held", "in_progress"), Item("exchange", "in_progress", "gone", "\"r1\""), Item("nobody", "in_progress", "gone")]), Live(["impl"], ["r1"]));

        Assert.Equal(2, work.Working);
    }

    [Theory]
    [InlineData("blocked")]
    [InlineData("in_review")]
    [InlineData("not_started")]
    [InlineData("next")]
    [InlineData("done")]
    public void NegativeAnItemInAnotherStateIsNeitherWorkingNorWaiting(string state)
    {
        var work = Work(With([Item("x", state)]), Live(["impl"]));

        Assert.Equal((0, 0), (work.Waiting, work.Working));
        Assert.Empty(work.Rows);
    }

    [Fact]
    public void NegativeAnotherTeamsPlanIsNotThisTeams()
    {
        var records = With([]);
        records.Rows[(ProgressLane.LaneName, ProgressLane.KeyOf(Dir, "other"))] = new LaneRecord(JsonText.Parse($$"""{"work_items":[{{Item("ask", "waiting_user")}}]}"""), 1);

        Assert.Equal(0, Work(records, Live([])).Waiting);
    }

    // b517: the parked-question store of a directory: q1 is open and the team's, q2 is answered, q3 is another team's
    private const string ParkedLines =
        """
        {"kind":"parked","id":"q1","at":"2026-11-02T09:00:00Z","team":"ship","question":{"question":"Ship today?","header":"ask","options":[{"label":"Yes"},{"label":"No"}]},"default":"wait","class":"blocker"}
        {"kind":"parked","id":"q2","at":"2026-11-02T09:05:00Z","team":"ship","question":{"question":"Old?","header":"Old","options":[]},"default":"none"}
        {"kind":"answered","id":"q2","at":"2026-11-02T09:06:00Z","labels":["Yes"]}
        {"kind":"parked","id":"q3","at":"2026-11-02T09:07:00Z","team":"other","question":{"question":"Theirs?","header":"Theirs","options":[]},"default":"none"}
        """;

    /// <summary>Stores what the parked lane reads of <paramref name="lines"/> for the directory, as the model does.</summary>
    private static void Park(Records records, string lines)
    {
        var record = new ParkedLane(_ => ParkedQuestions.Fold(lines), static () => [Dir]).Reread(Dir, Now).Record!;
        if (record.IsAbsent)
        {
            _ = records.Rows.Remove((ParkedLane.LaneName, Dir));
        }
        else
        {
            records.Rows[(ParkedLane.LaneName, Dir)] = record;
        }
    }

    [Fact]
    public void AQuestionParkedForTheUserWaitsWithNoRunAndHidesNoItemNamedAsItsHeader()
    {
        var records = With([Item("ask", "waiting_user")]);
        _ = records.Rows.Remove((StatusLane.LaneName, Dir));
        Park(records, ParkedLines);
        var team = new TeamEntry(Dir, "/t", Name, JsonValue.Null, 1);

        var work = Work(records, Live([]));
        var body = JsonValue.FromObject(QuestionsLane.Fold(JsonValue.Null, Dir, SummaryData.Waiting(records, ActivityLane.DirId(Dir), team)).Body);

        Assert.Equal(2, work.Waiting);
        var row = Assert.Single(work.Rows, static r => r["kind"].AsString() == "decision");
        Assert.Equal(("q1", "Ship today?", "parked", "blocker"), (row["key"].AsString(), row["title"].AsString(), row["source"].AsString(), row["class"].AsString()));
        var open = Assert.Single(body.Member("open")!.Value.AsArray());
        Assert.Equal(
            ("q1", "ask", "parked", "Ship today?"),
            (open.Member("id")!.Value.AsString(), open.Member("header")!.Value.AsString(), open.Member("source")!.Value.AsString(), open.Member("text")!.Value.AsString()));
        Assert.Equal(2, open.Member("options")!.Value.AsArray().Count);
        Assert.Equal(2, (long)body.Member("waiting_total")!.Value.AsNumber());
        Assert.Equal(Dir, body.Member("dir")!.Value.AsString());
    }

    [Fact]
    public void NegativeAParkedQuestionTheRunAlreadyListsOrThatIsAnsweredIsNotCountedAgain()
    {
        var listed = With([], ["""{"id":"q1","question":"Ship today?","item":null,"time":"2026-11-02T11:00:00Z"}"""]);
        Park(listed, ParkedLines);

        var work = Work(listed, Live([]));

        Assert.Equal(1, work.Waiting);
        Assert.Equal("q1", Assert.Single(work.Rows)["key"].AsString());

        var answered = With([]);
        Park(answered, ParkedLines + "\n" + """{"kind":"answered","id":"q1","at":"2026-11-02T12:00:00Z","labels":["Yes"]}""");

        Assert.Equal(0, Work(answered, Live([])).Waiting);
    }
}
