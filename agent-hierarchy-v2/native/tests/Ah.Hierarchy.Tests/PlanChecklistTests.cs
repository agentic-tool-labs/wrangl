using Ah.Core.Json;

namespace Ah.Hierarchy.Tests;

/// <summary>progress.checklists: the stream checklists of a team read as a plan source, and the team-file facts progress.streams adds.</summary>
public sealed class PlanChecklistTests
{
    private const string Marker = "<!-- ah-progress v1.0 kind=stream -->";

    private static string Folder(HierarchyReadOpsTests.Rig rig, string team = "t")
    {
        var folder = Path.Combine(rig.Pool, "progress", team);
        _ = Directory.CreateDirectory(folder);
        return folder;
    }

    private static string Sheet(string checklist, string header = "Owner: impl-1   Branch: ah/x   Work tree: /w/x tree   State: open", string updated = "Updated: 2026-10-20T10:00:00Z", string tail = "", string marker = Marker) =>
        $"{marker}\n# s — Ship it\n{header}\n{updated}\n\n## Checklist\n{checklist}\n## Related\n- Specs: a\n\n## Blockers\n{tail}\n";

    private const string Sections = "### Done\n- [x] shipped a\n### In progress\n- [ ] doing b\n### Not started\n- [ ] later c\n";

    private static JsonArray Read(HierarchyReadOpsTests.Rig rig, JsonObject? known = null, string team = "t")
    {
        var args = new JsonObject { ["dir"] = rig.Pool, ["team"] = team };
        if (known is not null)
        {
            args["known"] = known;
        }

        Ah.TestKit.TestImport.Dir(rig.AhHome, rig.Pool);
        return HierarchyReadOpsTests.Parsed(HierarchyReadOpsTests.Read(rig, "progress", "checklists", args)).Member("streams")!.Value.AsArray();
    }

    private static JsonValue One(JsonArray streams, string name) => streams.Single(s => s.Member("stream")!.Value.AsString() == name);

    private static string[] States(JsonValue stream) => [.. stream.Member("items")!.Value.AsArray().Select(i => i.Member("state")!.Value.AsString())];

    [Fact]
    public void AStreamChecklistGivesItsHeaderFieldsItsItemsByTheirSectionAndABlockerCount()
    {
        var rig = HierarchyReadOpsTests.Make();
        File.WriteAllText(Path.Combine(Folder(rig), "s.md"), Sheet(Sections, tail: "waiting on R3\nand a review\n"));

        var s = One(Read(rig), "s");

        Assert.Equal("Ship it", s.Member("goal")!.Value.AsString());
        Assert.Equal("impl-1", s.Member("owner")!.Value.AsString());
        Assert.Equal("ah/x", s.Member("branch")!.Value.AsString());
        Assert.Equal("/w/x tree", s.Member("work_tree")!.Value.AsString());
        Assert.Equal("open", s.Member("md_state")!.Value.AsString());
        Assert.Equal(DateTimeOffset.Parse("2026-10-20T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeMilliseconds(), (long)s.Member("as_of")!.Value.AsNumber());
        Assert.Equal(["done", "in_progress", "not_started"], States(s));
        Assert.Equal(["shipped a", "doing b", "later c"], s.Member("items")!.Value.AsArray().Select(i => i.Member("title")!.Value.AsString()));
        Assert.Equal(2, s.Member("blockers")!.Value.AsNumber());
        Assert.Equal(0, s.Member("warnings")!.Value.AsNumber());
        Assert.False(s.Member("truncated")!.Value.AsBool());
    }

    [Fact]
    public void OnlyAFileThatOpensWithAStreamMarkerIsAChecklistAndTheRollupAJournalAndAHeaderlessFileAreNot()
    {
        var rig = HierarchyReadOpsTests.Make();
        var folder = Folder(rig);
        File.WriteAllText(Path.Combine(folder, "main.md"), Sheet(string.Empty));
        File.WriteAllText(Path.Combine(folder, "all.md"), "\n\n" + Sheet(Sections));
        File.WriteAllText(Path.Combine(folder, "rollup.md"), Sheet(Sections, marker: "<!-- ah-progress v1.0 kind=rollup -->"));
        File.WriteAllText(Path.Combine(folder, "x.journal.md"), Sheet(Sections));
        File.WriteAllText(Path.Combine(folder, "bare.md"), "# bare — no marker\n## Checklist\n### Done\n- [x] a\n");
        File.WriteAllText(Path.Combine(folder, "kind.md"), Sheet(Sections, marker: "<!-- ah-progress v1.0 kind=rollup -->"));
        File.WriteAllText(Path.Combine(folder, "newer.md"), Sheet(Sections, marker: "<!-- ah-progress v2.0 kind=stream -->"));
        File.WriteAllText(Path.Combine(folder, "late.md"), "intro\n" + Sheet(Sections));

        var streams = Read(rig);

        Assert.Equal(["all", "main"], streams.Select(s => s.Member("stream")!.Value.AsString()));
        Assert.Empty(One(streams, "main").Member("items")!.Value.AsArray());
        Assert.Equal(3, One(streams, "all").Member("items")!.Value.AsArray().Count);
    }

    [Fact]
    public void ACheckboxThatDisagreesWithItsSectionKeepsTheSectionsStateAndAPlainLineIsNotCountedBothOnlyWarn()
    {
        var rig = HierarchyReadOpsTests.Make();
        File.WriteAllText(
            Path.Combine(Folder(rig), "s.md"),
            Sheet("### Done\n- [ ] not ticked\n- [x] ticked\n### In progress\n- [x] ticked early\n- a plain line\n### Not started\n- [ ] fine\n"));

        var s = One(Read(rig), "s");

        Assert.Equal(["done", "done", "in_progress", "not_started"], States(s));
        Assert.Equal(3, s.Member("warnings")!.Value.AsNumber());
    }

    [Fact]
    public void ADashFieldIsNullAnUnreadableUpdatedFallsBackToTheFileTimeAndATitleOver200CharactersIsCut()
    {
        var rig = HierarchyReadOpsTests.Make();
        var path = Path.Combine(Folder(rig), "s.md");
        File.WriteAllText(path, Sheet("### Done\n- [x] " + new string('t', 300) + "\n", header: "Owner: —   Branch: —   Work tree: —   State: —", updated: "Updated: sometime"));
        var when = new DateTime(2026, 10, 19, 8, 30, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, when);

        var s = One(Read(rig), "s");

        Assert.True(s.Member("owner")!.Value.IsNull);
        Assert.True(s.Member("branch")!.Value.IsNull);
        Assert.True(s.Member("work_tree")!.Value.IsNull);
        Assert.True(s.Member("md_state")!.Value.IsNull);
        Assert.Equal(new DateTimeOffset(when).ToUnixTimeMilliseconds(), (long)s.Member("as_of")!.Value.AsNumber());
        Assert.Equal(200, s.Member("items")!.Value.AsArray()[0].Member("title")!.Value.AsString().Length);
    }

    [Fact]
    public void MoreThan500ItemsOrMoreThan256KbIsTruncatedAndExactly500IsNot()
    {
        var rig = HierarchyReadOpsTests.Make();
        var folder = Folder(rig);
        File.WriteAllText(Path.Combine(folder, "exact.md"), Sheet("### Not started\n" + string.Concat(Enumerable.Range(0, 500).Select(n => $"- [ ] item {n}\n"))));
        File.WriteAllText(Path.Combine(folder, "over.md"), Sheet("### Not started\n" + string.Concat(Enumerable.Range(0, 501).Select(n => $"- [ ] item {n}\n"))));
        File.WriteAllText(Path.Combine(folder, "huge.md"), Sheet("### Not started\n- [ ] first\n") + new string('x', 300 * 1024));

        var streams = Read(rig);

        Assert.False(One(streams, "exact").Member("truncated")!.Value.AsBool());
        Assert.Equal(500, One(streams, "exact").Member("items")!.Value.AsArray().Count);
        Assert.True(One(streams, "over").Member("truncated")!.Value.AsBool());
        Assert.Equal(500, One(streams, "over").Member("items")!.Value.AsArray().Count);
        Assert.True(One(streams, "huge").Member("truncated")!.Value.AsBool());
        Assert.Equal("first", One(streams, "huge").Member("items")!.Value.AsArray()[0].Member("title")!.Value.AsString());
    }

    [Fact]
    public void AnItemKeyIsTheStreamAndAHashOfTheNormalisedTitleAndARepeatedTitleGetsASuffix()
    {
        var rig = HierarchyReadOpsTests.Make();
        var folder = Folder(rig);
        File.WriteAllText(Path.Combine(folder, "a.md"), Sheet("### Not started\n- [ ] Same  Title\n- [ ] same title\n- [ ] other\n"));
        File.WriteAllText(Path.Combine(folder, "b.md"), Sheet("### Not started\n- [ ] Same Title\n"));

        var keys = One(Read(rig), "a").Member("items")!.Value.AsArray().Select(i => i.Member("key")!.Value.AsString()).ToList();
        var other = One(Read(rig), "b").Member("items")!.Value.AsArray()[0].Member("key")!.Value.AsString();

        Assert.StartsWith("a:", keys[0], StringComparison.Ordinal);
        Assert.Equal(keys[0] + "~2", keys[1]);
        Assert.NotEqual(keys[0], keys[2]);
        Assert.StartsWith("b:", other, StringComparison.Ordinal);
        Assert.Equal(keys[0][2..], other[2..]);
    }

    [Fact]
    public void AFileWhoseSignatureIsKnownIsAnsweredUnchangedAndAnEditedOneIsReadAgain()
    {
        var rig = HierarchyReadOpsTests.Make();
        var path = Path.Combine(Folder(rig), "s.md");
        File.WriteAllText(path, Sheet(Sections));
        var first = One(Read(rig), "s");
        var sig = first.Member("sig")!.Value.AsString();

        var same = One(Read(rig, new JsonObject { ["s"] = sig }), "s");
        Ah.TestKit.TestProgressDocs.WriteFile(rig.AhHome, path, Sheet(Sections + "- [ ] added d\n"));
        var edited = One(Read(rig, new JsonObject { ["s"] = sig }), "s");

        Assert.True(same.Member("unchanged")!.Value.AsBool());
        Assert.Null(same.Member("items"));
        Assert.Null(edited.Member("unchanged"));
        Assert.Equal(4, edited.Member("items")!.Value.AsArray().Count);
        Assert.NotEqual(first.Member("hash")!.Value.AsString(), edited.Member("hash")!.Value.AsString());
    }

    [Fact]
    public void ATouchChangesTheSignatureButNotTheDigestOfTheText()
    {
        var rig = HierarchyReadOpsTests.Make();
        var path = Path.Combine(Folder(rig), "s.md");
        File.WriteAllText(path, Sheet(Sections));
        var first = One(Read(rig), "s");

        Ah.TestKit.TestProgressDocs.Touch(rig.AhHome, path, DateTimeOffset.UtcNow.AddHours(-3));
        var touched = One(Read(rig, new JsonObject { ["s"] = first.Member("sig")!.Value.AsString() }), "s");

        Assert.Null(touched.Member("unchanged"));
        Assert.NotEqual(first.Member("sig")!.Value.AsString(), touched.Member("sig")!.Value.AsString());
        Assert.Equal(first.Member("hash")!.Value.AsString(), touched.Member("hash")!.Value.AsString());
    }

    [Fact]
    public void ATeamThatIsOffOrHasNoFolderHasNoChecklists()
    {
        var rig = HierarchyReadOpsTests.Make();
        Assert.Empty(Read(rig, team: "none"));
        var folder = Folder(rig, "quiet");
        File.WriteAllText(Path.Combine(folder, "s.md"), Sheet(Sections));
        Ah.TestKit.TestProgressDocs.Off(rig.AhHome, rig.Pool, "quiet");

        Assert.Empty(Read(rig, team: "quiet"));
    }

    [Fact]
    public void ProgressStreamsAlsoGivesTheTeamFilesStreamStateAndTheFullWorkTreePath()
    {
        var rig = HierarchyReadOpsTests.Make();
        File.WriteAllText(Path.Combine(Folder(rig), "s.md"), Sheet(Sections));
        _ = Directory.CreateDirectory(Path.Combine(rig.Pool, "teams"));
        File.WriteAllText(
            Path.Combine(rig.Pool, "teams", "t.json"),
            """{"streams":{"s":{"state":"done","branch":"ah/x","worktree":"/w/lifecycle-optin"},"open-one":{"state":"open"},"bare":{"branch":"ah/q"}},"members":[{"name":"m","stream":"s"}]}""");

        Ah.TestKit.TestImport.Dir(rig.AhHome, rig.Pool);
        var streams = HierarchyReadOpsTests.Parsed(HierarchyReadOpsTests.Read(rig, "progress", "streams", new JsonObject { ["dir"] = rig.Pool, ["team"] = "t" })).Member("streams")!.Value.AsArray();

        Assert.Equal("done", One(streams, "s").Member("state")!.Value.AsString());
        Assert.Equal("/w/lifecycle-optin", One(streams, "s").Member("work_tree_path")!.Value.AsString());
        Assert.Equal("lifecycle-optin", One(streams, "s").Member("work_tree")!.Value.AsString());
        Assert.Equal("open", One(streams, "open-one").Member("state")!.Value.AsString());
        Assert.True(One(streams, "bare").Member("state")!.Value.IsNull);
        Assert.True(One(streams, "bare").Member("work_tree_path")!.Value.IsNull);
    }

    [Fact]
    public void AGoalAndEveryHeaderFieldOf100KbAreCutTo200CharactersSoTheSummaryStaysSmall()
    {
        var rig = HierarchyReadOpsTests.Make();
        var big = new string('g', 100_000);
        var mid = new string('g', 20_000);
        var text = $"{Marker}\n# s — {big}\nOwner: {big}   Branch: {mid}   Work tree: {mid}   State: open\nUpdated: 2026-10-20T10:00:00Z\n\n## Checklist\n### Done\n- [x] {mid}\n";
        File.WriteAllText(Path.Combine(Folder(rig), "s.md"), text);

        var s = One(Read(rig), "s");

        foreach (var field in new[] { "goal", "owner", "branch", "work_tree" })
        {
            Assert.Equal(200, s.Member(field)!.Value.AsString().Length);
        }

        Assert.Equal(200, s.Member("items")!.Value.AsArray()[0].Member("title")!.Value.AsString().Length);
    }

    [Theory]
    [InlineData(198, 200)]
    [InlineData(199, 199)]
    [InlineData(200, 200)]
    public void ACutNeverSplitsASurrogatePair(int plainBefore, int expectedLength)
    {
        var cut = PlanChecklist.Cap(new string('a', plainBefore) + "😀tail");

        Assert.Equal(expectedLength, cut.Length);
        for (var i = 0; i < cut.Length; i++)
        {
            Assert.False(char.IsHighSurrogate(cut[i]) && (i + 1 == cut.Length || !char.IsLowSurrogate(cut[i + 1])));
            Assert.False(char.IsLowSurrogate(cut[i]) && (i == 0 || !char.IsHighSurrogate(cut[i - 1])));
        }
    }

    [Fact]
    public void AShortValueAndAValueOfExactly200AreKeptWhole()
    {
        Assert.Equal("abc", PlanChecklist.Cap("abc"));
        Assert.Equal(200, PlanChecklist.Cap(new string('x', 200)).Length);
        Assert.Equal(200, PlanChecklist.Cap(new string('x', 201)).Length);
        Assert.Equal(199, PlanChecklist.Cap(new string('x', 199) + "😀").Length);
    }
}

/// <summary>The checklist transition table (spec ah-sitrep-events §1, §2): one test per table row, the override rule, and the new states' fold.</summary>
public sealed class PlanTransitionTests
{
    private const string Req = "20261101-100000-aaaa";
    private const string ReviewReq = "20261101-110000-bbbb";

    private static string Row(string ts, string item, string ev, string? extra = null, string by = "orchestrator@tm") =>
        $"{{\"v\":1,\"ts\":\"{ts}\",\"team\":\"tm\",\"stream\":\"s\",\"item\":\"{item}\",\"event\":\"{ev}\"{extra},\"by\":\"{by}\"}}";

    /// <summary>Item a (implementor, ref <see cref="Req"/>, slug x) and item r (reviewer, ref <see cref="ReviewReq"/>), both dispatched at 10:00.</summary>
    private static List<ChecklistEntry> Board(params string[] more) =>
    [
        .. ProgressFormat.FoldPlan(string.Join('\n', new[]
        {
            Row("2026-11-01T10:00:00Z", "a", "add", ",\"title\":\"Build a\""),
            Row("2026-11-01T10:00:00Z", "a", "start"),
            Row("2026-11-01T10:00:00Z", "a", "ref", $",\"ref\":\"{Req}\""),
            Row("2026-11-01T10:00:00Z", "r", "add", ",\"title\":\"Review a\""),
            Row("2026-11-01T10:00:00Z", "r", "start"),
            Row("2026-11-01T10:00:00Z", "r", "ref", $",\"ref\":\"{ReviewReq}\""),
        }.Concat(more))).Items.Select(static i => new ChecklistEntry("s", i)),
    ];

    private static string? Slug(string id) => id is Req or ReviewReq ? "x" : null;

    [Fact]
    public void TheQueueMayHaveItemsOnlyWhenAnItemIsNext()
    {
        static ChecklistEntry Item(string state) => new("s", new PlanItem("i", "t", state, "2026-11-01T10:00:00Z", null, null, [], null, null, null));
        Assert.True(HierarchyModule.QueueMayHaveItems([Item("in_progress"), Item("next")]));
        Assert.False(HierarchyModule.QueueMayHaveItems([Item("in_progress"), Item("not_started"), Item("waiting_orch")]));
        Assert.False(HierarchyModule.QueueMayHaveItems([]));
    }

    private static ChecklistEvent Reply(string request, string text, string role, string at = "2026-11-01T11:00:00Z") =>
        new(HierarchyModule.ChecklistEventReply, at, request, role == "reviewer" ? "tm-reviewer" : "tm-implementor") { Text = text, Role = role };

    private static Dictionary<string, string[]> Moves(IReadOnlyList<(ChecklistEntry Entry, IReadOnlyList<PlanInput> Rows)> moves) =>
        moves.ToDictionary(static m => m.Entry.Item.Item, static m => m.Rows.Select(static r => r.Event).ToArray());

    [Fact]
    public void ADoneReplyMovesItsItemToInReviewWithTheTodoAndACause()
    {
        var moves = HierarchyModule.ChecklistTransitions(Reply(Req, "DONE sitrep\n- built", "implementor"), Board());

        var (entry, rows) = Assert.Single(moves);
        Assert.Equal("a", entry.Item.Item);
        Assert.Equal(["report", "todo"], rows.Select(static r => r.Event));
        Assert.All(rows, static r => Assert.Equal(new PlanCause("reply", Req, "2026-11-01T11:00:00Z"), r.Cause));
    }

    [Fact]
    public void NegativeABlockedReplyIsNotATableEventAndMovesNothing()
    {
        Assert.False(HierarchyModule.ChecklistReplyIsEvent("BLOCKED on the spec", "implementor"));
        Assert.Empty(HierarchyModule.ChecklistTransitions(Reply(Req, "BLOCKED on the spec", "implementor"), Board()));
        Assert.Empty(HierarchyModule.ChecklistTransitions(Reply(Req, "STATUS half way", "implementor"), Board()));
    }

    private static string InReview => Row("2026-11-01T10:30:00Z", "a", "review");

    private static string Reported(string sha) => Row("2026-11-01T10:30:00Z", "a", "review", $",\"commits\":[\"{sha}\"],\"branch\":\"ah/x\"");

    private static string BaseAt(string sha) => Row("2026-11-01T10:00:01Z", "a", "ref", $",\"ref\":\"{Req}\",\"base\":\"{sha}\"");

    private static ChecklistEvent Ack(string text, string request = Req, string at = "2026-11-01T11:00:00Z") =>
        new(HierarchyModule.ChecklistEventAck, at, request, "orchestrator@tm") { Text = text };

    [Theory]
    [InlineData("VERDICT PASS x@abc1234")]
    [InlineData("PASS WITH NITS")]
    public void AnAckOfAPassApproves(string text) =>
        Assert.Equal(["approve"], Moves(HierarchyModule.ChecklistTransitions(Ack(text), Board(InReview)))["a"]);

    [Theory]
    [InlineData("DONE x\n- built")]
    [InlineData("status: DONE")]
    public void NegativeAnAckOfDoneLeavesAnItemInReviewBecauseNoReviewerHasJudgedIt(string text) =>
        Assert.Empty(HierarchyModule.ChecklistTransitions(Ack(text), Board(InReview)));

    [Theory]
    [InlineData("step 1 DONE, step 2 starting")]
    [InlineData("DONE step 1, step 2 starting")]
    [InlineData("status: DONE with step 1 of 2")]
    [InlineData("DONE: part 1. Continuing with the rest")]
    [InlineData("- **Status:** DONE, tests still running")]
    [InlineData("DONE piece 1, the rest next")]
    [InlineData("DONE step 1/3")]
    [InlineData("DONE, work in progress")]
    [InlineData("DONE x, more to come")]
    public void NegativeAPartialReportIsNoDoneAndItsAckLeavesTheItemOpen(string text)
    {
        Assert.False(HierarchyModule.ChecklistReplyIsDone(text));
        Assert.False(HierarchyModule.ChecklistReplyIsEvent(text, "implementor"));
        Assert.Empty(HierarchyModule.ChecklistTransitions(Reply(Req, text, "implementor") with { Sha = "abc1234" }, Board()));
        Assert.Empty(HierarchyModule.ChecklistTransitions(Ack(text), Board()));
    }

    [Theory]
    [InlineData("DONE tui-item-log @ 0ee271674")]
    [InlineData("DONE step 2 of 2")]
    [InlineData("DONE step 3/3")]
    [InlineData("DONE: starting from the base was enough")]
    [InlineData("DONE, nothing in progress")]
    [InlineData("DONE, no more to come")]
    [InlineData("status: DONE for the start failure, one cause")]
    [InlineData("DONE restarting the daemon no longer drops mail")]
    public void AFinishedReportThatMentionsStepsOrStartIsStillADone(string text)
    {
        Assert.True(HierarchyModule.ChecklistReplyIsDone(text));
        Assert.Equal(["report", "todo"], Moves(HierarchyModule.ChecklistTransitions(Reply(Req, text, "implementor"), Board()))["a"]);
    }

    [Fact]
    public void APassOnAnItemWhoseWorkerNeverReportedDoneLeavesItOpenAndNegativeAReviewsOwnItemCloses()
    {
        const string WorkerReq = "20261101-090000-cccc";
        var workerRef = Row("2026-11-01T10:00:01Z", "a", "ref", $",\"ref\":\"{WorkerReq}\"");
        static string? Worker(string id) => id == WorkerReq ? "tm-implementor" : null;
        var pass = Reply(Req, "VERDICT PASS y@abc1234", "reviewer");

        Assert.Empty(HierarchyModule.ChecklistTransitions(pass with { WorkerOfRequest = Worker }, Board(workerRef)));
        Assert.Empty(HierarchyModule.ChecklistTransitions(Ack("VERDICT PASS y@abc1234") with { WorkerOfRequest = Worker }, Board(workerRef)));
        Assert.Equal(["done"], Moves(HierarchyModule.ChecklistTransitions(pass with { WorkerOfRequest = Worker }, Board()))["a"]);
        Assert.Equal(["approve"], Moves(HierarchyModule.ChecklistTransitions(pass with { WorkerOfRequest = Worker }, Board(workerRef, InReview)))["a"]);
    }

    [Theory]
    [InlineData("PASS", "approve")]
    [InlineData("DONE x", "report")]
    public void AnAckMovesAnItemWhoseOnlyWorkerRequestIsTheOneAcked(string text, string move) =>
        Assert.Equal([move], Moves(HierarchyModule.ChecklistTransitions(Ack(text) with { WorkerOfRequest = static _ => "tm-qa" }, Board()))["a"]);

    [Fact]
    public void APassOnPartOfAnItemRestartsItAndClearsItsTodoAndNegativeADoneDoesNot()
    {
        var other = Row("2026-11-01T10:00:01Z", "a", "ref", ",\"ref\":\"20261101-090000-cccc\"");
        var parked = Row("2026-11-01T10:30:00Z", "a", "wait-orch", ",\"note\":\"report\"");
        var owed = Row("2026-11-01T10:30:00Z", "a", "todo", ",\"todo\":\"answer tm-reviewer: report\"");

        Assert.Equal(["start", "todo"], Moves(HierarchyModule.ChecklistTransitions(Ack("PASS") with { WorkerOfRequest = static _ => "tm-qa" }, Board(other, parked, owed)))["a"]);
        Assert.Empty(HierarchyModule.ChecklistTransitions(Ack("DONE x") with { WorkerOfRequest = static _ => "tm-qa" }, Board(other, parked, owed)));
    }

    [Fact]
    public void APassAfterAFailWithNoNewReportDoesNotApproveAndNegativeAReviewersDispatchKeepsTheReport()
    {
        const string WorkerReq = "20261101-090000-cccc";
        var workerRef = Row("2026-11-01T10:00:01Z", "a", "ref", $",\"ref\":\"{WorkerReq}\"");
        static string? Worker(string id) => id == WorkerReq ? "tm-implementor" : null;
        var pass = Reply(Req, "VERDICT PASS y@abc1234", "reviewer") with { WorkerOfRequest = Worker };
        var failed = Row("2026-11-01T10:40:00Z", "a", "start", ",\"note\":\"verdict CHANGES REQUIRED\"");

        Assert.Empty(HierarchyModule.ChecklistTransitions(pass, Board(workerRef, InReview, failed)));
        Assert.Equal(["approve"], Moves(HierarchyModule.ChecklistTransitions(pass, Board(workerRef, InReview, failed, Row("2026-11-01T10:50:00Z", "a", "review"))))["a"]);
        Assert.Equal(["approve"], Moves(HierarchyModule.ChecklistTransitions(pass, Board(workerRef, InReview, Row("2026-11-01T10:40:00Z", "a", "start"))))["a"]);
    }

    [Fact]
    public void NegativeAnAckOfTheSameTextWithAnotherWorkersRequestOnTheItemMovesNothing()
    {
        var other = Row("2026-11-01T10:00:01Z", "a", "ref", ",\"ref\":\"20261101-090000-cccc\"");

        Assert.Empty(HierarchyModule.ChecklistTransitions(Ack("PASS") with { WorkerOfRequest = static _ => "tm-qa" }, Board(other)));
    }

    [Fact]
    public void AnAckClosesAnItemWhoseReportedShaIsItsBaseAndNegativeLeavesOneThatMovedInReview()
    {
        Assert.Equal(["done"], Moves(HierarchyModule.ChecklistTransitions(Ack("DONE x"), Board(BaseAt("abc1234"), Reported("abc1234f"))))["a"]);
        Assert.Empty(HierarchyModule.ChecklistTransitions(Ack("DONE x"), Board(BaseAt("abc1234"), Reported("def5678"))));
        Assert.Equal(["approve"], Moves(HierarchyModule.ChecklistTransitions(Ack("PASS"), Board(BaseAt("abc1234"), Reported("def5678"))))["a"]);
    }

    [Theory]
    [InlineData("CHANGES REQUIRED: two findings")]
    [InlineData("VERDICT CHANGES REQUIRED x@abc1234")]
    [InlineData("BLOCKED on the spec")]
    [InlineData("NEEDS-DECISION which one")]
    public void AnAckOfChangesBlockedOrADecisionGoesBackToTheLeadShowingWhyAndNegativeNeverCloses(string text)
    {
        var (entry, rows) = HierarchyModule.ChecklistTransitions(Ack(text), Board(InReview)).Single(m => m.Entry.Item.Item == "a");

        Assert.Equal("a", entry.Item.Item);
        var row = Assert.Single(rows);
        Assert.Equal("wait-orch", row.Event);
        Assert.Equal(text, row.Note);
    }

    [Theory]
    [InlineData("STATUS half way")]
    [InlineData("a plain note")]
    public void NegativeAnAckOfAStatusOrANoteMovesNothing(string text) =>
        Assert.Empty(HierarchyModule.ChecklistTransitions(Ack(text), Board(InReview)));

    [Fact]
    public void NegativeAnAckOfAReportADispatchHasSinceReplacedMovesNothing()
    {
        var redispatched = Row("2026-11-01T10:45:00Z", "a", "ref", ",\"ref\":\"20261101-104500-cccc\"");

        Assert.Empty(HierarchyModule.ChecklistTransitions(Ack("DONE x"), Board(InReview, redispatched)));
    }

    [Fact]
    public void AChecklistSetAfterTheReportStopsItsAckAndNegativeOneBeforeItDoesNot()
    {
        Assert.Empty(HierarchyModule.ChecklistTransitions(Ack("DONE x"), Board(Manual("2026-11-01T11:30:00Z", "a", "next"))));
        Assert.Equal(["report"], Moves(HierarchyModule.ChecklistTransitions(Ack("DONE x"), Board(Manual("2026-11-01T10:40:00Z", "a", "next"))))["a"]);
    }

    [Fact]
    public void AnAckOfAReviewersChangesPutsTheWorkUnderItsSlugBackWithTheLead()
    {
        var moves = HierarchyModule.ChecklistTransitions(Ack("VERDICT CHANGES REQUIRED x@abc1234", ReviewReq), Board(), Slug);

        Assert.Equal("wait-orch", moves.Single(m => m.Entry.Item.Item == "a").Rows.Single().Event);
    }

    [Fact]
    public void AVerdictOnAnItemThatReportedWorkMovesItAndNegativeDoesNotCloseIt()
    {
        var onSame = new ChecklistEvent(HierarchyModule.ChecklistEventReply, "2026-11-01T11:00:00Z", Req, "tm-reviewer") { Role = "reviewer" };

        Assert.Equal(["approve"], Moves(HierarchyModule.ChecklistTransitions(onSame with { Text = "VERDICT PASS y@abc1234" }, Board(Reported("abc1234"))))["a"]);
        Assert.Equal(["start"], Moves(HierarchyModule.ChecklistTransitions(onSame with { Text = "VERDICT CHANGES REQUIRED y@abc1234" }, Board(Reported("abc1234"))))["a"]);
    }

    [Theory]
    [InlineData("VERDICT PASS y@abc1234", "approve")]
    [InlineData("VERDICT FAIL y@abc1234", "start")]
    [InlineData("VERDICT CHANGES REQUIRED y@abc1234", "start")]
    public void AVerdictOnAnItemThatReportedDoneWithNoShaNeverClosesIt(string text, string move) =>
        Assert.Equal([move], Moves(HierarchyModule.ChecklistTransitions(Reply(Req, text, "reviewer"), Board(InReview)))["a"]);

    [Theory]
    [InlineData("VERDICT PASS y@abc1234", "done")]
    [InlineData("VERDICT FAIL y@abc1234", "start")]
    [InlineData("VERDICT CHANGES REQUIRED y@abc1234", "start")]
    public void OnlyAPassClosesTheReviewsOwnItemAndNegativeAFailReopensIt(string text, string move) =>
        Assert.Equal([move], Moves(HierarchyModule.ChecklistTransitions(Reply(Req, text, "reviewer"), Board()))["a"]);

    [Theory]
    [InlineData("VERDICT NEEDS-EVIDENCE y@abc1234")]
    [InlineData("VERDICT BLOCKED y@abc1234")]
    public void NegativeAnyOtherVerdictWordMovesNothingOnItsOwnItem(string text) =>
        Assert.Empty(HierarchyModule.ChecklistTransitions(Reply(Req, text, "reviewer"), Board()));

    [Theory]
    [InlineData("VERDICT PASS y@abc1234")]
    [InlineData("VERDICT FAIL y@abc1234")]
    public void ALateVerdictLeavesAReworkDispatchAloneAndNegativeTheCurrentOneMoves(string text)
    {
        var newer = Row("2026-11-01T10:40:00Z", "a", "ref", ",\"ref\":\"20991231-000000-zzzz\"");

        Assert.Empty(HierarchyModule.ChecklistTransitions(Reply(Req, text, "reviewer"), Board(Reported("abc1234"), newer)));
        Assert.Single(HierarchyModule.ChecklistTransitions(Reply("20991231-000000-zzzz", text, "reviewer"), Board(Reported("abc1234"), newer)));
    }

    [Fact]
    public void ALateDoneMovesNothingOnceTheItemIsDispatchedAgainAndNegativeTheCurrentDispatchMoves()
    {
        const string newer = "20261101-120000-cccc";
        var redispatched = Row("2026-11-01T10:45:00Z", "a", "ref", $",\"ref\":\"{newer}\"");

        Assert.Empty(HierarchyModule.ChecklistTransitions(Reply(Req, "DONE x", "implementor") with { Sha = "abc1234" }, Board(redispatched)));
        Assert.Contains("report", Moves(HierarchyModule.ChecklistTransitions(Reply(newer, "DONE x", "implementor") with { Sha = "abc1234" }, Board(redispatched)))["a"]);
    }

    [Fact]
    public void ADoneReplyRecordsTheReportedShaAndNotItsBranch()
    {
        var reply = Reply(Req, "DONE x", "implementor") with { Sha = "abc1234" };

        var row = HierarchyModule.ChecklistTransitions(reply, Board()).Single().Rows[0];

        Assert.Equal(["abc1234"], row.Commits);
        Assert.Null(row.Branch);
    }

    [Theory]
    [InlineData("reviewer", true)]
    [InlineData("qa", true)]
    [InlineData("ultra-advisor", true)]
    [InlineData("backlog-groomer", true)]
    [InlineData("architect", false)]
    [InlineData("implementor", false)]
    [InlineData("my-custom-builder", false)]
    [InlineData(null, false)]
    public void AShaAtTheBaseIsRecordedOnlyFromAReadOnlyRole(string? role, bool recorded)
    {
        var reply = Reply(Req, "DONE x", "implementor") with { Sha = "abc1234", Role = role };

        var row = HierarchyModule.ChecklistTransitions(reply, Board(BaseAt("abc1234"))).Single().Rows[0];

        Assert.Equal(recorded, row.Commits is { Count: 1 });
        Assert.Equal(!recorded, row.AtBase); // ah2-312 M3'': a dropped sha is flagged
    }

    [Fact]
    public void NegativeAShaPastTheBaseIsRecordedFromAnyRole()
    {
        var reply = Reply(Req, "DONE x", "implementor") with { Sha = "def5678", Role = "my-custom-builder" };

        var row = HierarchyModule.ChecklistTransitions(reply, Board(BaseAt("abc1234"))).Single().Rows[0];
        Assert.Equal(["def5678"], row.Commits);
        Assert.False(row.AtBase);
    }

    [Fact]
    public void NegativeADoneWithNoShaIsNotFlaggedAtTheBase()
    {
        var reply = Reply(Req, "DONE x", "implementor") with { Sha = null };

        Assert.False(HierarchyModule.ChecklistTransitions(reply, Board(BaseAt("abc1234"))).Single().Rows[0].AtBase);
    }

    private static string WaitingUser => Row("2026-11-01T10:40:00Z", "a", "wait-user", ",\"note\":\"Which?\"");

    [Theory]
    [InlineData("DONE x", "implementor")]
    [InlineData("VERDICT PASS x@abc1234", "reviewer")]
    [InlineData("VERDICT FAIL x@abc1234", "reviewer")]
    public void NegativeAReplyLeavesAnItemWaitingOnTheUserWhereItIs(string text, string role) =>
        Assert.Empty(HierarchyModule.ChecklistTransitions(Reply(Req, text, role) with { Sha = "abc1234" }, Board(WaitingUser)));

    [Fact]
    public void NegativeAShipByTheBranchRuleLeavesAnItemWaitingOnTheUserWhereItIs()
    {
        var ship = new ChecklistEvent(HierarchyModule.ChecklistEventShip, "2026-11-01T12:00:00Z", "abc1234", "orchestrator@tm");

        // board-derive A1: the held item is noted, not closed
        Assert.Equal(["note"], Moves(HierarchyModule.ChecklistTransitions(ship, Board(WaitingUser), contains: static e => e.Item.Item == "a"))["a"]);
        Assert.Single(HierarchyModule.ChecklistTransitions(ship, Board(), contains: static e => e.Item.Item == "a"));
    }

    [Fact]
    public void NegativeAWithdrawnBriefLeavesAnItemWaitingOnTheUserWhereItIs() =>
        Assert.Empty(HierarchyModule.ChecklistTransitions(new ChecklistEvent(HierarchyModule.ChecklistEventWithdraw, "2026-11-01T12:00:00Z", Req, "orchestrator@tm"), Board(WaitingUser)));

    [Fact]
    public void AnAnswerAndAnExplicitSetStillTakeAnItemOffTheUsersList()
    {
        var answered = new ChecklistEvent(HierarchyModule.ChecklistEventAnswered, "2026-11-01T11:05:00Z", "tool-1", "orchestrator@tm") { ItemId = "a" };

        Assert.Equal("unblock", HierarchyModule.ChecklistTransitions(answered, Board(WaitingUser)).Single().Rows.Single().Event);
        Assert.Equal("in_progress", Board(WaitingUser, Manual("2026-11-01T10:50:00Z", "a", "start")).Single(static e => e.Item.Item == "a").Item.State);
    }

    [Theory]
    [InlineData("DONE x")]
    [InlineData("VERDICT PASS x@abc1234")]
    [InlineData("VERDICT CHANGES REQUIRED x@abc1234")]
    [InlineData("BLOCKED x")]
    public void AnAckMovesNothingOffTheUsersListAndNegativeAWaitOnTheLeadStillMoves(string text)
    {
        Assert.Empty(HierarchyModule.ChecklistTransitions(Ack(text), Board(Row("2026-11-01T10:40:00Z", "a", "wait-user", ",\"note\":\"Which?\""))));
        Assert.NotEmpty(HierarchyModule.ChecklistTransitions(Ack(text), Board(Row("2026-11-01T10:40:00Z", "a", "wait-orch", ",\"note\":\"Which?\""))));
    }

    [Fact]
    public void AnAckOfADoneWithNoShaStaysInReviewAndNegativeOneAtTheBaseFromAReadOnlyRoleCloses()
    {
        Assert.Empty(HierarchyModule.ChecklistTransitions(Ack("DONE x"), Board(BaseAt("abc1234"), InReview)));
        Assert.Contains("done", Moves(HierarchyModule.ChecklistTransitions(Ack("DONE x"), Board(BaseAt("abc1234"), Reported("abc1234"))))["a"]);
    }

    [Fact]
    public void NegativeALaterDoneWithNoShaLeavesNoOlderShaForAShipToCloseOn()
    {
        var item = Board(Reported("abc1234"), Row("2026-11-01T10:50:00Z", "a", "review")).Single(e => e.Item.Item == "a").Item;

        Assert.Empty(item.Commits);
    }

    [Fact]
    public void AnAnsweredTaggedQuestionTakesItsItemOffWaitingUserAndNegativeAnUntaggedOneNamesNoItem()
    {
        var answered = new ChecklistEvent(HierarchyModule.ChecklistEventAnswered, "2026-11-01T11:05:00Z", "tool-1", "orchestrator@tm") { ItemId = "a" };

        Assert.Equal("unblock", HierarchyModule.ChecklistTransitions(answered, Board(Row("2026-11-01T11:00:00Z", "a", "wait-user"))).Single().Rows.Single().Event);
        Assert.Empty(HierarchyModule.ChecklistTransitions(answered, Board()));
        Assert.Equal("s/a", HierarchyModule.ChecklistAskItem("ah:x;ref=q1;item=s/a"));
        Assert.Null(HierarchyModule.ChecklistAskItem("ah:x;ref=q1"));
        Assert.Null(HierarchyModule.ChecklistAskItem(null));
    }

    private static string Manual(string ts, string item, string ev) =>
        Row(ts, item, ev, $",\"note\":\"by hand\",\"cause\":{{\"event\":\"manual\",\"ref\":\"\",\"at\":\"{ts}\"}}");

    [Fact]
    public void APassVerdictApprovesTheReviewedItemAndClosesTheReviewersOwn()
    {
        var moves = Moves(HierarchyModule.ChecklistTransitions(Reply(ReviewReq, "VERDICT PASS WITH NITS x@abc1234\n- nit", "reviewer"), Board(InReview), Slug));

        Assert.Equal(["approve"], moves["a"]);
        Assert.Equal(["done"], moves["r"]);
    }

    [Fact]
    public void NegativeAPassVerdictLeavesASameSlugItemThatNeverReportedDone()
    {
        var inProgress = Moves(HierarchyModule.ChecklistTransitions(Reply(ReviewReq, "VERDICT PASS x@abc1234", "reviewer"), Board(), Slug));
        var approved = Moves(HierarchyModule.ChecklistTransitions(Reply(ReviewReq, "VERDICT APPROVED x@abc1234", "reviewer"), Board(InReview, Row("2026-11-01T10:40:00Z", "a", "approve")), Slug));

        Assert.Equal(["r"], inProgress.Keys);
        Assert.Equal(["r"], approved.Keys);
    }

    [Fact]
    public void AFailVerdictSendsAnApprovedItemBackButLeavesAnInProgressOne()
    {
        var approved = Moves(HierarchyModule.ChecklistTransitions(Reply(ReviewReq, "VERDICT FAIL x@abc1234", "reviewer"), Board(InReview, Row("2026-11-01T10:40:00Z", "a", "approve")), Slug));
        var inProgress = Moves(HierarchyModule.ChecklistTransitions(Reply(ReviewReq, "VERDICT FAIL x@abc1234", "reviewer"), Board(), Slug));

        Assert.Equal(["start"], approved["a"]);
        Assert.Equal(["r"], inProgress.Keys);
    }

    [Fact]
    public void AFailingVerdictOnAReviewWithItsOwnSlugFollowsTheChainOrTheCommit()
    {
        IEnumerable<string> Moved(string text, string[] chain, params string[] board) =>
            Moves(HierarchyModule.ChecklistTransitions(Reply(ReviewReq, text, "reviewer") with { Chain = chain }, Board(board), static _ => null)).Keys;

        Assert.Contains("a", Moved("VERDICT FAIL own@abc1234", [Req], InReview));
        Assert.Contains("a", Moved("VERDICT FAIL own@abc1234", [], Reported("abc1234")));
        Assert.Contains("a", Moved("VERDICT FAIL own@abc1234", [], Reported("abc1234567890")));

        // the negatives: no chain and another commit, and a pass never follows a commit
        Assert.DoesNotContain("a", Moved("VERDICT FAIL own@abc1234", [], Reported("def5678")));
        Assert.DoesNotContain("a", Moved("VERDICT PASS own@abc1234", [], Reported("abc1234")));
    }

    [Fact]
    public void AFailingVerdictHandsAnItemTheReviewerHoldsBackToItsWorker()
    {
        PlanInput? Assigned(string holder)
        {
            var ev = Reply(ReviewReq, "VERDICT FAIL x@abc1234", "reviewer") with { WorkerOfRequest = static id => id == Req ? "tm-implementor" : null };
            var moves = HierarchyModule.ChecklistTransitions(ev, Board(InReview, Row("2026-11-01T10:40:00Z", "a", "assign", $",\"holder\":\"{holder}\"")), Slug);
            return moves.Single(static m => m.Entry.Item.Item == "a").Rows.FirstOrDefault(static r => r.Event == "assign");
        }

        Assert.Equal("tm-implementor", Assigned("tm-reviewer")?.Holder);

        // the negative: anyone else keeps it
        Assert.Null(Assigned("tm-other"));
    }

    [Fact]
    public void AChangesRequiredVerdictSendsTheReviewedItemBackToInProgressAndKeepsItsHolder()
    {
        var moves = HierarchyModule.ChecklistTransitions(Reply(ReviewReq, "VERDICT CHANGES REQUIRED x@abc1234", "reviewer"), Board(InReview), Slug);

        var reviewed = moves.Single(m => m.Entry.Item.Item == "a");
        Assert.Equal(["start"], reviewed.Rows.Select(static r => r.Event));
        Assert.DoesNotContain(reviewed.Rows, static r => r.Event == "assign");
        Assert.Contains(moves, m => m.Entry.Item.Item == "r" && m.Rows[0].Event == "start");
    }

    [Fact]
    public void NegativeAnyOtherVerdictMovesNothingAtAll() =>
        Assert.Empty(HierarchyModule.ChecklistTransitions(Reply(ReviewReq, "VERDICT NEEDS-ARCHITECT x@abc1234", "reviewer"), Board(), Slug));

    [Fact]
    public void NegativeAVerdictForASlugWithNoOpenItemClosesOnlyTheReviewersOwn()
    {
        var moves = Moves(HierarchyModule.ChecklistTransitions(Reply(ReviewReq, "VERDICT PASS other@abc1234", "reviewer"), Board(), Slug));

        Assert.Equal(["r"], moves.Keys);
    }

    [Fact]
    public void NegativeAVerdictLineFromANonReviewerIsNotAVerdict()
    {
        Assert.False(HierarchyModule.ChecklistReplyIsEvent("VERDICT PASS x@abc1234", "implementor"));
        Assert.Empty(HierarchyModule.ChecklistTransitions(Reply(Req, "VERDICT PASS x@abc1234", "implementor"), Board(), Slug));
    }

    [Fact]
    public void AShipRowClosesTheItemsWhoseBranchItContains()
    {
        var ev = new ChecklistEvent(HierarchyModule.ChecklistEventShip, "2026-11-01T12:00:00Z", "abc1234def", "orchestrator@tm");
        var moves = HierarchyModule.ChecklistTransitions(ev, Board(), contains: static e => e.Item.Item == "a");

        var (entry, rows) = Assert.Single(moves);
        Assert.Equal("a", entry.Item.Item);
        Assert.Equal("done", rows.Single().Event);
        Assert.Equal("2026-11-01T12:00:00Z", rows.Single().Cause!.At);
    }

    [Fact]
    public void NegativeAMergeRowClosesNothingEvenWhenItContainsTheBranch()
    {
        var ev = new ChecklistEvent(HierarchyModule.ChecklistEventMerge, "2026-11-01T12:00:00Z", "abc1234def", "orchestrator@tm");

        Assert.Empty(HierarchyModule.ChecklistTransitions(ev, Board(), contains: static _ => true));
    }

    [Fact]
    public void NegativeAShipRowWhoseShaDoesNotContainTheBranchMovesNothing()
    {
        var ev = new ChecklistEvent(HierarchyModule.ChecklistEventShip, "2026-11-01T12:00:00Z", "abc1234", "orchestrator@tm");

        Assert.Empty(HierarchyModule.ChecklistTransitions(ev, Board(), contains: static _ => false));
    }

    [Fact]
    public void AWithdrawnBriefDropsItsItem()
    {
        var ev = new ChecklistEvent(HierarchyModule.ChecklistEventWithdraw, "2026-11-01T12:00:00Z", Req, "orchestrator@tm");

        var (entry, rows) = Assert.Single(HierarchyModule.ChecklistTransitions(ev, Board()));
        Assert.Equal("a", entry.Item.Item);
        Assert.Equal("drop", rows.Single().Event);
    }

    [Fact]
    public void AWithdrawnBriefLeavesAnItemAnotherOpenBriefHolds()
    {
        const string other = "20261101-100500-cccc";
        var ev = new ChecklistEvent(HierarchyModule.ChecklistEventWithdraw, "2026-11-01T12:00:00Z", Req, "orchestrator@tm") { RequestOpen = static r => r == other };

        var (_, rows) = Assert.Single(HierarchyModule.ChecklistTransitions(ev, Board(Row("2026-11-01T10:05:00Z", "a", "ref", $",\"ref\":\"{other}\""))));
        Assert.Equal("note", rows.Single().Event);

        // negative: the withdrawn brief is the item's only one, so it drops though that brief still reads open
        var only = ev with { RequestOpen = static _ => true };
        Assert.Equal("drop", Assert.Single(HierarchyModule.ChecklistTransitions(only, Board())).Rows.Single().Event);
    }

    [Fact]
    public void NegativeWithdrawingARequestWithNoItemIsANoOp()
    {
        var ev = new ChecklistEvent(HierarchyModule.ChecklistEventWithdraw, "2026-11-01T12:00:00Z", "20261101-120000-zzzz", "orchestrator@tm");

        Assert.Empty(HierarchyModule.ChecklistTransitions(ev, Board()));
    }

    [Fact]
    public void AnOverrideFollowedByAnOlderEventMovesNothing()
    {
        var board = Board(InReview, Manual("2026-11-01T11:30:00Z", "a", "start"));

        Assert.Empty(HierarchyModule.ChecklistTransitions(Reply(Req, "DONE", "implementor", "2026-11-01T11:00:00Z"), board));
        Assert.DoesNotContain(HierarchyModule.ChecklistTransitions(Reply(ReviewReq, "VERDICT FAIL x@abc1234", "reviewer", "2026-11-01T11:00:00Z"), board, Slug), static m => m.Entry.Item.Item == "a");
    }

    [Fact]
    public void AnOverrideFollowedByANewerOrSameSecondEventMoves()
    {
        var board = Board(Manual("2026-11-01T11:30:00Z", "a", "next"));

        var (newer, _) = Assert.Single(HierarchyModule.ChecklistTransitions(Reply(Req, "DONE", "implementor", "2026-11-01T12:00:00Z"), board));
        var (same, _) = Assert.Single(HierarchyModule.ChecklistTransitions(Reply(Req, "DONE", "implementor", "2026-11-01T11:30:00Z"), board));
        Assert.Equal("a", newer.Item.Item);
        Assert.Equal("a", same.Item.Item);
    }

    [Fact]
    public void NegativeADispatchAnAutoAddAnAutomaticOrAMembersRowIsNotAnOverride()
    {
        var dispatched = Board(Row("2026-11-01T11:30:00Z", "a", "next"), Row("2026-11-01T11:30:00Z", "a", "start"));
        var caused = Board(Row("2026-11-01T11:30:00Z", "a", "review", ",\"cause\":{\"event\":\"reply\",\"ref\":\"x\",\"at\":\"2026-11-01T11:30:00Z\"}"));
        var member = Board(Row("2026-11-01T11:30:00Z", "a", "wait-orch", ",\"note\":\"n\"", by: "tm-implementor@tm"));
        var manual = Board(Manual("2026-11-01T11:30:00Z", "a", "next"));

        Assert.Null(dispatched.Single(static e => e.Item.Item == "a").Item.OverrideAt);
        Assert.Null(caused.Single(static e => e.Item.Item == "a").Item.OverrideAt);
        Assert.Null(member.Single(static e => e.Item.Item == "a").Item.OverrideAt);
        Assert.Equal("2026-11-01T11:30:00Z", manual.Single(static e => e.Item.Item == "a").Item.OverrideAt);
        Assert.Single(HierarchyModule.ChecklistTransitions(Reply(Req, "DONE", "implementor", "2026-11-01T11:00:00Z"), dispatched));
    }

    private const string Ask = ",\"note\":\"Which?\"";

    private static PlanItem ItemA(params string[] more) => Board(more).Single(static e => e.Item.Item == "a").Item;

    private static PlanState AddedA(params string[] more) =>
        ProgressFormat.FoldPlan(string.Join('\n', new[] { Row("2026-11-01T10:00:00Z", "a", "add", ",\"title\":\"Build a\""), Row("2026-11-01T10:00:00Z", "a", "start") }.Concat(more)));

    private static string? Refusal(string row, params string[] before) => ProgressFormat.RefusePlanRow(JsonText.Parse(row), AddedA(before), import: false);

    private static string Answer(string ts, string text = "Ship it") =>
        Row(ts, "a", "wait-orch", $",\"answer\":\"{text}\",\"cause\":{{\"event\":\"ui-answer\",\"ref\":\"\",\"at\":\"{ts}\"}}", "user");

    private static string WaitWithOptions(string ts, string options = "\"x\",\"y\"") => Row(ts, "a", "wait-user", Ask + $",\"options\":[{options}]");

    [Fact]
    public void AQuestionsClassFoldsAReAskWithoutOneReadsSoonAndABlockerSortsFirst()
    {
        Assert.Equal("blocker", ItemA(Row("2026-11-01T11:00:00Z", "a", "wait-user", Ask + ",\"class\":\"blocker\"")).Class);
        var reAsked = ItemA(Row("2026-11-01T11:00:00Z", "a", "wait-user", Ask + ",\"class\":\"blocker\""), Row("2026-11-01T11:10:00Z", "a", "wait-user", Ask));
        Assert.Null(reAsked.Class);
        Assert.Equal("soon", HierarchyModule.ChecklistClassOf(reAsked));

        // the blocker was asked last, so by age alone it would sort after the soon one
        var soon = new ChecklistEntry("s", ItemA(Row("2026-11-01T11:00:00Z", "a", "wait-user", Ask + ",\"class\":\"soon\"")));
        var blocker = new ChecklistEntry("s", ItemA(Row("2026-11-01T12:00:00Z", "a", "wait-user", Ask + ",\"class\":\"blocker\"")) with { Item = "b" });
        var eventually = new ChecklistEntry("s", ItemA(Row("2026-11-01T10:30:00Z", "a", "wait-user", Ask + ",\"class\":\"eventually\"")) with { Item = "c" });
        Assert.Equal(["b", "a", "c"], HierarchyModule.ChecklistShown([soon, eventually, blocker], all: true).Select(static e => e.Item.Item));
        Assert.Contains(" class=blocker ", HierarchyModule.ChecklistShow([blocker], [], 0, all: true)[0], StringComparison.Ordinal);

        // negative: two soon items keep their age order
        var older = new ChecklistEntry("s", ItemA(Row("2026-11-01T10:00:00Z", "a", "wait-user", Ask)) with { Item = "d" });
        Assert.Equal(["d", "a"], HierarchyModule.ChecklistShown([soon, older], all: true).Select(static e => e.Item.Item));
    }

    [Theory]
    [InlineData("wait-user", ",\"note\":\"Which?\",\"class\":\"urgent\"")]
    [InlineData("block", ",\"note\":\"Why\",\"class\":\"blocker\"")]
    [InlineData("note", ",\"note\":\"x\",\"class\":\"soon\"")]
    public void NegativeAClassGoesOnlyOnAWaitUserRowAndOnlyAsAKnownName(string ev, string extra) =>
        Assert.StartsWith("class goes only on a wait-user row", Refusal(Row("2026-11-01T11:00:00Z", "a", ev, extra)), StringComparison.Ordinal);

    [Fact]
    public void AWaitUserRowWithAKnownClassIsAccepted() =>
        Assert.Null(Refusal(Row("2026-11-01T11:00:00Z", "a", "wait-user", Ask + ",\"class\":\"eventually\"")));

    [Fact]
    public void AQuestionsOptionsFoldAndAReAskWithoutThemClearsThem()
    {
        Assert.Equal(["x", "y"], ItemA(WaitWithOptions("2026-11-01T11:00:00Z")).Options);
        Assert.Null(ItemA(WaitWithOptions("2026-11-01T11:00:00Z"), Row("2026-11-01T11:10:00Z", "a", "wait-user", Ask)).Options);
    }

    [Fact]
    public void AnAnswerKeepsTheQuestionMovesTheItemAndALaterQuestionClearsIt()
    {
        var answered = ItemA(WaitWithOptions("2026-11-01T11:00:00Z"), Answer("2026-11-01T11:05:00Z"));
        var again = ItemA(WaitWithOptions("2026-11-01T11:00:00Z"), Answer("2026-11-01T11:05:00Z"), Row("2026-11-01T11:10:00Z", "a", "wait-user", ",\"note\":\"And now?\""));

        Assert.Equal(("waiting_orch", "Ship it", "Which?"), (answered.State, answered.Answer, answered.Note));
        Assert.Null(answered.OverrideAt);
        Assert.Equal(("waiting_user", (string?)null, "And now?"), (again.State, again.Answer, again.Note));
    }

    [Fact]
    public void ALaterAutomaticMoveAppliesToAnAnsweredItem() =>
        Assert.Equal("in_review", ItemA(Row("2026-11-01T11:00:00Z", "a", "wait-user", Ask), Answer("2026-11-01T11:05:00Z"), Row("2026-11-01T11:10:00Z", "a", "review")).State);

    [Fact]
    public void UnblockAfterAnAnswerReturnsToTheStateBeforeTheQuestion() =>
        Assert.Equal("in_progress", ItemA(Row("2026-11-01T11:00:00Z", "a", "wait-user", Ask), Answer("2026-11-01T11:05:00Z"), Row("2026-11-01T11:10:00Z", "a", "unblock")).State);

    [Fact]
    public void AReAskMovesTheStateTimeAndNegativeANoteEditDoesNot()
    {
        var asked = Row("2026-11-01T11:00:00Z", "a", "wait-user", Ask);

        Assert.Equal("2026-11-01T11:10:00Z", ItemA(asked, Row("2026-11-01T11:10:00Z", "a", "wait-user", Ask)).StateSince);
        Assert.Equal("2026-11-01T11:00:00Z", ItemA(asked, Row("2026-11-01T11:10:00Z", "a", "note", ",\"note\":\"Which, exactly?\"")).StateSince);
    }

    [Fact]
    public void TheAnswerRendersUnderInProgress()
    {
        var block = ProgressFormat.RenderChecklistBlock(AddedA(Row("2026-11-01T11:00:00Z", "a", "wait-user", Ask), Answer("2026-11-01T11:05:00Z")));

        Assert.Contains("- [ ] Build a (answered: Ship it)\n", block, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAnswerGoesOnlyOnAWaitingItem()
    {
        var waiting = Row("2026-11-01T11:00:00Z", "a", "wait-user", Ask);

        Assert.Null(Refusal(Answer("2026-11-01T11:05:00Z"), waiting));
        Assert.StartsWith("not-waiting", Refusal(Answer("2026-11-01T11:06:00Z"), waiting, Answer("2026-11-01T11:05:00Z")), StringComparison.Ordinal);
        Assert.StartsWith("not-waiting", Refusal(Answer("2026-11-01T11:05:00Z")), StringComparison.Ordinal);
    }

    [Fact]
    public void ADismissalDropsAWaitingItemWithItsReasonAndNegativeASecondIsNotWaiting()
    {
        var waiting = Row("2026-11-01T11:00:00Z", "a", "wait-user", Ask);
        var dismiss = Row("2026-11-01T11:05:00Z", "a", "drop", ",\"answer\":\"Dismissed: Already done\",\"cause\":{\"event\":\"ui-dismiss\",\"ref\":\"\",\"at\":\"2026-11-01T11:05:00Z\"}", "user");

        Assert.Null(Refusal(dismiss, waiting));
        Assert.Equal(("dropped", "Dismissed: Already done", "Which?"), (ItemA(waiting, dismiss).State, ItemA(waiting, dismiss).Answer, ItemA(waiting, dismiss).Note));
        Assert.StartsWith("not-waiting", Refusal(dismiss, waiting, dismiss), StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeAWaitOrchWithNoNoteAndNoAnswerIsStillRefused() =>
        Assert.Equal("wait-orch needs a note", Refusal(Row("2026-11-01T11:05:00Z", "a", "wait-orch"), Row("2026-11-01T11:00:00Z", "a", "wait-user", Ask)));

    [Fact]
    public void NegativeAnAnswerOnAnotherEventOrBlankOrOverTheCapIsRefused()
    {
        var waiting = Row("2026-11-01T11:00:00Z", "a", "wait-user", Ask);

        Assert.Contains("answer goes only", Refusal(Row("2026-11-01T11:05:00Z", "a", "start", ",\"answer\":\"x\""), waiting), StringComparison.Ordinal);
        Assert.Contains("answer goes only", Refusal(Answer("2026-11-01T11:05:00Z", "  "), waiting), StringComparison.Ordinal);
        Assert.Contains("answer goes only", Refusal(Answer("2026-11-01T11:05:00Z", new string('x', 2049)), waiting), StringComparison.Ordinal);
        Assert.Null(Refusal(Answer("2026-11-01T11:05:00Z", new string('x', 2048)), waiting));
    }

    [Theory]
    [InlineData("\"a\",\"b\",\"c\",\"d\",\"e\"", "1 to 4 options")]
    [InlineData("", "1 to 4 options")]
    [InlineData("\"a\",\"a\"", "repeat")]
    [InlineData("\" \"", "1 to 64")]
    public void BadOptionsAreRefused(string options, string why) =>
        Assert.Contains(why, Refusal(WaitWithOptions("2026-11-01T11:00:00Z", options)), StringComparison.Ordinal);

    [Fact]
    public void AnOptionOfSixtyFourFitsAndNegativeSixtyFiveDoesNot()
    {
        Assert.Null(Refusal(WaitWithOptions("2026-11-01T11:00:00Z", $"\"{new string('x', 64)}\"")));
        Assert.Contains("1 to 64", Refusal(WaitWithOptions("2026-11-01T11:00:00Z", $"\"{new string('x', 65)}\"")), StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeOptionsOnAnotherEventAreRefused() =>
        Assert.Contains("only on a wait-user row", Refusal(Row("2026-11-01T11:00:00Z", "a", "block", Ask + ",\"options\":[\"x\"]")), StringComparison.Ordinal);

    [Fact]
    public void ReviewAndApproveFoldToTheNewStatesAndRenderUnderInProgress()
    {
        var reviewed = Board(Row("2026-11-01T11:00:00Z", "a", "review"));
        var approved = Board(Row("2026-11-01T11:00:00Z", "a", "review"), Row("2026-11-01T11:10:00Z", "a", "approve"));

        Assert.Equal("in_review", reviewed.Single(static e => e.Item.Item == "a").Item.State);
        Assert.Equal("approved", approved.Single(static e => e.Item.Item == "a").Item.State);
        var block = ProgressFormat.RenderChecklistBlock(new PlanState([.. approved.Select(static e => e.Item)], 0));
        Assert.Contains("### In progress\n- [ ] Build a\n", block, StringComparison.Ordinal);
    }

    [Fact]
    public void ARowWithACauseIsAcceptedAndAMalformedCauseIsRefused()
    {
        var state = ProgressFormat.FoldPlan(Row("2026-11-01T10:00:00Z", "a", "add", ",\"title\":\"A\""));
        var good = JsonText.Parse(Row("2026-11-01T11:00:00Z", "a", "review", ",\"cause\":{\"event\":\"reply\",\"ref\":\"r\",\"at\":\"2026-11-01T11:00:00Z\"}"));
        var bad = JsonText.Parse(Row("2026-11-01T11:00:00Z", "a", "review", ",\"cause\":{\"ref\":\"r\"}"));

        Assert.Null(ProgressFormat.RefusePlanRow(good, state, import: false));
        Assert.NotNull(ProgressFormat.RefusePlanRow(bad, state, import: false));
    }

    [Fact]
    public void AnEmptyTodoRowClearsTheTodoAndAWhitespaceOneIsRefused()
    {
        var withTodo = Board(Row("2026-11-01T11:00:00Z", "a", "todo", ",\"todo\":\"answer tm-implementor: report\""));
        var cleared = Board(Row("2026-11-01T11:00:00Z", "a", "todo", ",\"todo\":\"answer tm-implementor: report\""), Row("2026-11-01T11:10:00Z", "a", "todo", ",\"todo\":\"\""));
        var state = ProgressFormat.FoldPlan(Row("2026-11-01T10:00:00Z", "a", "add", ",\"title\":\"A\""));

        Assert.Equal("answer tm-implementor: report", withTodo.Single(static e => e.Item.Item == "a").Item.Todo);
        Assert.Null(cleared.Single(static e => e.Item.Item == "a").Item.Todo);
        Assert.Null(ProgressFormat.RefusePlanRow(JsonText.Parse(Row("2026-11-01T11:00:00Z", "a", "todo", ",\"todo\":\"\"")), state, import: false));
        Assert.NotNull(ProgressFormat.RefusePlanRow(JsonText.Parse(Row("2026-11-01T11:00:00Z", "a", "todo", ",\"todo\":\"   \"")), state, import: false));
    }

    [Fact]
    public void NegativeAStatusReplyAfterDoneLeavesTheItemInReviewAndALaterPassStillApprovesIt()
    {
        const string req = "20261101-100000-aaaa";
        const string review = "20261101-100000-rrrr";
        var rig = HierarchyReadOpsTests.Make();
        var p = rig.Services.Progress;
        Assert.Null(p.RecordChecklist(rig.Pool, "tm", "_run", "orchestrator@tm",
        [
            new PlanInput("a", "add", "A"), new PlanInput("a", "start"), new PlanInput("a", "ref", Ref: req),
            new PlanInput("r", "add", "Review A"), new PlanInput("r", "start"), new PlanInput("r", "ref", Ref: review),
        ]).Refused);
        string? Slug(string id) => id is req or review ? "x" : null;
        PlanItem A() => p.ReadPlan(rig.Pool, "tm", "_run")!.Items.Single(static i => i.Item == "a");
        DateTimeOffset At(int minute) => DateTimeOffset.Parse($"2026-11-01T11:{minute:00}:00Z", System.Globalization.CultureInfo.InvariantCulture);

        Assert.Null(HierarchyModule.ChecklistOnMemberReply(p, rig.Pool, "tm", req, null, "tm-implementor", "implementor", "DONE x\n- built", At(0), null, Slug));
        Assert.Null(HierarchyModule.ChecklistOnMemberReply(p, rig.Pool, "tm", req, null, "tm-implementor", "implementor", "STATUS one more thing", At(1), null, Slug));

        Assert.Equal("reported", A().State);
        Assert.StartsWith("answer tm-implementor", A().Todo, StringComparison.Ordinal);

        Assert.Null(HierarchyModule.ChecklistOnMemberReply(p, rig.Pool, "tm", review, null, "tm-reviewer", "reviewer", "VERDICT PASS x@abc1234", At(2), null, Slug));
        Assert.Null(HierarchyModule.ChecklistOnMemberReply(p, rig.Pool, "tm", req, null, "tm-implementor", "implementor", "a follow-up", At(3), null, Slug));

        Assert.Equal("approved", A().State);
    }

    [Fact]
    public void ARefRowsBaseFoldsOntoTheItemAndKeepsItAndANonShaBaseIsRefused()
    {
        // a re-dispatch with a later base keeps the first one
        var based = Board(Row("2026-11-01T11:00:00Z", "a", "ref", ",\"ref\":\"20261101-100000-aaaa\",\"base\":\"abc1234\""), Row("2026-11-01T11:10:00Z", "a", "todo", ",\"todo\":\"x\""), Row("2026-11-01T11:20:00Z", "a", "ref", ",\"ref\":\"20261101-110000-bbbb\",\"base\":\"def5678\""));
        var unbased = Board(Row("2026-11-01T11:00:00Z", "a", "ref", ",\"ref\":\"20261101-100000-aaaa\""));
        var state = ProgressFormat.FoldPlan(Row("2026-11-01T10:00:00Z", "a", "add", ",\"title\":\"A\""));

        Assert.Equal("abc1234", based.Single(static e => e.Item.Item == "a").Item.Base);
        Assert.Null(unbased.Single(static e => e.Item.Item == "a").Item.Base);
        Assert.Null(ProgressFormat.RefusePlanRow(JsonText.Parse(Row("2026-11-01T11:00:00Z", "a", "ref", ",\"ref\":\"r\",\"base\":\"abc1234\"")), state, import: false));
        Assert.NotNull(ProgressFormat.RefusePlanRow(JsonText.Parse(Row("2026-11-01T11:00:00Z", "a", "ref", ",\"ref\":\"r\",\"base\":\"main\"")), state, import: false));
        Assert.NotNull(ProgressFormat.RefusePlanRow(JsonText.Parse(Row("2026-11-01T11:00:00Z", "a", "ref", ",\"ref\":\"r\",\"base\":\"ABC1234\"")), state, import: false));
    }

    [Fact]
    public void AnOldLogWithNoNewRowsFoldsTheSameStates()
    {
        var states = Board(Row("2026-11-01T11:00:00Z", "a", "wait-orch", ",\"note\":\"n\""), Row("2026-11-01T11:10:00Z", "r", "done", ",\"commits\":[\"abc1234\"]"))
            .Select(static e => e.Item.State);

        Assert.Equal(["waiting_orch", "done"], states);
    }

    [Fact]
    public void ATrailRowCarriesTheRoleAndHolderAfterIt()
    {
        var log = string.Join('\n', Row("2026-11-01T10:00:00Z", "a", "add", ",\"title\":\"A\""), Row("2026-11-01T10:01:00Z", "a", "role", ",\"role\":\"implementor\""), Row("2026-11-01T10:02:00Z", "a", "assign", ",\"holder\":\"tm-impl\""));

        var trail = HierarchyModule.ChecklistTrail(log, "a");

        Assert.Equal([null, "implementor", "implementor"], trail.Select(static r => r.Role));
        Assert.Equal([null, null, "tm-impl"], trail.Select(static r => r.Holder));
    }

    [Theory]
    [InlineData("dispatch", false)]
    [InlineData("reply", true)]
    public void AMoversAuditStampIsNotAnAutomaticMove(string cause, bool automatic) =>
        Assert.Equal(automatic, HierarchyModule.ChecklistIsAutomaticMove(new ChecklistTrailRow("2026-11-01T10:00:00Z", "start", "o", new PlanCause(cause, "r", "2026-11-01T10:00:00Z"), "not_started", "in_progress")));

    [Fact]
    public void ADispatchToQaThatFindsNoItemWritesNoQaStart()
    {
        var rig = HierarchyReadOpsTests.Make();
        var p = rig.Services.Progress;

        Assert.NotNull(HierarchyModule.ChecklistOnDispatch(p, rig.Pool, "tm", "missing", "tm-qa", ReviewReq, "orchestrator@tm", null, role: "qa", slug: "x"));

        Assert.DoesNotContain(p.ReadLedger(rig.Pool, "tm"), static r => r.Member("kind")?.AsString() == "qa");
    }

    [Fact]
    public void ADispatchToQaWritesQaStartAndItsReportQaEndAndOtherRolesOrAStatusWriteNone()
    {
        var rig = HierarchyReadOpsTests.Make();
        var p = rig.Services.Progress;
        Assert.Null(p.RecordChecklist(rig.Pool, "tm", "_run", "orchestrator@tm", [new PlanInput("q", "add", "QA x"), new PlanInput("i", "add", "Build x")]).Refused);
        var at = DateTimeOffset.Parse("2026-11-01T11:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        string[] Qa() => [.. p.ReadLedger(rig.Pool, "tm").Where(static r => r.Member("kind")?.AsString() == "qa").Select(static r => r.Member("phase")!.Value.AsString() + " " + r.Member("ref")!.Value.AsString())];

        Assert.Null(HierarchyModule.ChecklistOnDispatch(p, rig.Pool, "tm", "i", "tm-implementor", Req, "orchestrator@tm", null, role: "implementor", slug: "x"));
        Assert.Null(HierarchyModule.ChecklistOnMemberReply(p, rig.Pool, "tm", Req, null, "tm-implementor", "implementor", "DONE x", at, null, Slug));
        Assert.Empty(Qa());

        Assert.Null(HierarchyModule.ChecklistOnDispatch(p, rig.Pool, "tm", "q", "tm-qa", ReviewReq, "orchestrator@tm", null, role: "qa", slug: "x", sha: "abc1234"));
        Assert.Null(HierarchyModule.ChecklistOnMemberReply(p, rig.Pool, "tm", ReviewReq, null, "tm-qa", "qa", "STATUS half the suite", at, null, Slug));
        Assert.Equal(["start " + ReviewReq], Qa());

        Assert.Null(HierarchyModule.ChecklistOnMemberReply(p, rig.Pool, "tm", ReviewReq, null, "tm-qa", "qa", "PASS all green", at, null, Slug));
        Assert.Equal(["start " + ReviewReq, "end " + ReviewReq], Qa());
        var end = p.ReadLedger(rig.Pool, "tm")[^1];
        Assert.Equal("x", end.Member("slug")!.Value.AsString());
        Assert.Equal("qa end", end.Member("text")!.Value.AsString());
    }

    [Theory]
    [InlineData("status: DONE")]
    [InlineData("Status: done, tip abc1234")]
    [InlineData("- STATUS: DONE. Tip 166a6f3c6")]
    [InlineData("- **Status:** DONE")]
    [InlineData("[1] status: DONE")]
    public void ALabelledDoneIsADoneInAnyCaseOrBulletForm(string text)
    {
        Assert.True(HierarchyModule.ChecklistReplyIsDone(text));
        Assert.Equal("report", HierarchyModule.ChecklistReplyKind(text));

        var moves = HierarchyModule.ChecklistTransitions(Reply(Req, text, "implementor") with { Sha = "abc1234" }, Board());

        Assert.Equal(["abc1234"], Assert.Single(moves).Rows[0].Commits);
    }

    [Theory]
    [InlineData("status of the build: DONE")]
    [InlineData("Status is DONE")]
    [InlineData("status: half way, DONE soon")]
    [InlineData("status: DONE-ish")]
    [InlineData("The status: DONE")]
    public void NegativeStatusInProseIsNoDone(string text)
    {
        Assert.False(HierarchyModule.ChecklistReplyIsDone(text));
        Assert.Empty(HierarchyModule.ChecklistTransitions(Reply(Req, text, "implementor"), Board()));
    }

    [Fact]
    public void NegativeALabelledDoneFromADirtyTreeRecordsNoSha() =>
        Assert.Null(Assert.Single(HierarchyModule.ChecklistTransitions(Reply(Req, "status: DONE", "implementor"), Board())).Rows[0].Commits);

    private static string Parked(string kind) => Row("2026-11-01T10:30:00Z", "a", "wait-orch", ",\"note\":\"answer m\"") + "\n" + Row("2026-11-01T10:30:00Z", "a", "todo", $",\"todo\":\"answer m: {kind}\"");

    [Theory]
    [InlineData("Status: NEEDS-DECISION which store", "NEEDS-DECISION which store")]
    [InlineData("- status: blocked on the spec", "blocked on the spec")]
    [InlineData("**STATUS:** BLOCKED", "BLOCKED")]
    [InlineData("Status: NEEDS-EVIDENCE a run", "NEEDS-EVIDENCE a run")]
    [InlineData("status: QUESTION which", "QUESTION which")]
    [InlineData("status: FAIL", "FAIL")]
    [InlineData("status: PARTIAL 3 of 5", "PARTIAL 3 of 5")]
    [InlineData("Status: CHANGES REQUIRED", "CHANGES REQUIRED")]
    public void AnAckOfALabelledQuestionKeepsItWaitingWithTheQuestion(string text, string note)
    {
        var (_, rows) = Assert.Single(HierarchyModule.ChecklistTransitions(Ack(text), Board(Parked("x"))));

        Assert.Equal("wait-orch", Assert.Single(rows).Event);
        Assert.Equal(note, rows[0].Note);
    }

    [Theory]
    [InlineData("Status: half way")]
    [InlineData("status: failed the first run, retrying")]
    [InlineData("status: NEEDS- nothing")]
    public void NegativeAnAckOfAPlainStatusLineStillGoesBackToWork(string text) =>
        Assert.Equal(["start", "note"], Moves(HierarchyModule.ChecklistTransitions(Ack(text), Board(Parked("STATUS"))))["a"]);

    private const string LaterReview = "20261101-120000-cccc";

    private static ChecklistEvent Verdict(string text, params string[] chain) => Reply(LaterReview, text, "reviewer") with { Chain = chain };

    [Theory]
    [InlineData("VERDICT PASS other-slug@abc1234")]
    [InlineData("VERDICT PASS WITH NITS other-slug@abc1234")]
    public void APassMovesTheItemItsRequestChainLeadsToWhateverItsSlug(string text)
    {
        Assert.Equal(["approve"], Moves(HierarchyModule.ChecklistTransitions(Verdict(text, ReviewReq + "x", Req), Board(InReview)))["a"]);
        Assert.Equal(["approve"], Moves(HierarchyModule.ChecklistTransitions(Ack(text, LaterReview) with { Chain = [Req] }, Board(InReview)))["a"]);
    }

    [Fact]
    public void NegativeAPassWhoseChainMissesTheItemMovesNothing() =>
        Assert.Empty(HierarchyModule.ChecklistTransitions(Verdict("VERDICT PASS other-slug@abc1234", "20261101-090000-zzzz"), Board(InReview)));

    [Fact]
    public void AFailFollowsTheChain() =>
        Assert.Equal(["start"], Moves(HierarchyModule.ChecklistTransitions(Verdict("VERDICT CHANGES REQUIRED other-slug@abc1234", Req), Board(InReview)))["a"]);

    [Fact]
    public void NegativeAFailWhoseChainMissesTheItemMovesNothing() =>
        Assert.Empty(HierarchyModule.ChecklistTransitions(Verdict("VERDICT CHANGES REQUIRED other-slug@abc1234", "20261101-090000-zzzz"), Board(InReview)));

    [Fact]
    public void NegativeAPassOnTheChainLeavesANewerDispatchAlone() =>
        Assert.Empty(HierarchyModule.ChecklistTransitions(Verdict("VERDICT PASS other-slug@abc1234", Req), Board(InReview, Row("2026-11-01T13:00:00Z", "a", "ref", ",\"ref\":\"20261101-130000-dddd\""))));

    [Fact]
    public void AReviewersReplyWalksRequestParentsToTheItemAndStopsOnACycle()
    {
        var rig = HierarchyReadOpsTests.Make();
        var p = rig.Services.Progress;
        Assert.Null(p.RecordChecklist(rig.Pool, "tm", "_run", "orchestrator@tm", [new PlanInput("a", "add", "Build A"), new PlanInput("a", "start"), new PlanInput("a", "ref", Ref: Req), new PlanInput("a", "review")]).Refused);
        var parents = new Dictionary<string, string> { [LaterReview] = ReviewReq, [ReviewReq] = Req, [Req] = "null" };
        var at = DateTimeOffset.Parse("2026-11-01T12:30:00Z", System.Globalization.CultureInfo.InvariantCulture);

        Assert.Null(HierarchyModule.ChecklistOnMemberReply(p, rig.Pool, "tm", LaterReview, null, "tm-reviewer", "reviewer", "VERDICT PASS hook-noise-rv4@abc1234", at, null, static _ => null, parentOfRequest: id => parents.GetValueOrDefault(id)));

        Assert.Equal("approved", p.ReadPlan(rig.Pool, "tm", "_run")!.Items.Single().State);

        // a chain that loops ends, and an item off it stays
        Assert.Null(p.RecordChecklist(rig.Pool, "tm", "_run", "orchestrator@tm", [new PlanInput("b", "add", "Build B"), new PlanInput("b", "start"), new PlanInput("b", "ref", Ref: "20261101-100000-bbbb"), new PlanInput("b", "review")]).Refused);
        Assert.Null(HierarchyModule.ChecklistOnMemberReply(p, rig.Pool, "tm", LaterReview, null, "tm-reviewer", "reviewer", "VERDICT PASS hook-noise-rv4@abc1234", at, null, static _ => null, parentOfRequest: static id => id == LaterReview ? ReviewReq : LaterReview));
        Assert.Equal("in_review", p.ReadPlan(rig.Pool, "tm", "_run")!.Items.Single(static i => i.Item == "b").State);
    }

    private static (IHierarchyProgress P, string Pool, Func<PlanItem> A) Dispatched(params PlanInput[] more)
    {
        var rig = HierarchyReadOpsTests.Make();
        var p = rig.Services.Progress;
        Assert.Null(p.RecordChecklist(rig.Pool, "tm", "_run", "orchestrator@tm", [new PlanInput("a", "add", "Build A"), new PlanInput("a", "start"), new PlanInput("a", "ref", Ref: Req), .. more]).Refused);
        return (p, rig.Pool, () => p.ReadPlan(rig.Pool, "tm", "_run")!.Items.Single());
    }

    [Fact]
    public void AnAnswerUserTodoSurvivesALaterReplyWhileTheItemStillMoves()
    {
        var (p, pool, a) = Dispatched(new PlanInput("a", "todo", Todo: "answer user: a"));

        Assert.Null(HierarchyModule.ChecklistOnReply(p, pool, "tm", Req, null, "tm-implementor", "BLOCKED", "tm-implementor@tm", null));

        Assert.Equal(("waiting_orch", "answer user: a"), (a().State, a().Todo));
    }

    [Fact]
    public void AnAnswerUserTodoSurvivesADoneWhileTheItemStillMoves()
    {
        var (p, pool, a) = Dispatched(new PlanInput("a", "todo", Todo: "answer user: a"));

        Assert.Null(HierarchyModule.ChecklistOnMemberReply(p, pool, "tm", Req, "_run/a", "tm-implementor", "implementor", "DONE", DateTimeOffset.UtcNow, null, static _ => null));

        Assert.Equal(("reported", "answer user: a"), (a().State, a().Todo));
    }

    [Fact]
    public void NegativeADoneStillReplacesAnOrdinaryTodo()
    {
        var (p, pool, a) = Dispatched(new PlanInput("a", "todo", Todo: "answer tm-user: a"));

        Assert.Null(HierarchyModule.ChecklistOnMemberReply(p, pool, "tm", Req, "_run/a", "tm-implementor", "implementor", "DONE", DateTimeOffset.UtcNow, null, static _ => null));

        Assert.Equal(("reported", "answer tm-implementor: report"), (a().State, a().Todo));
    }

    [Fact]
    public void AnAnswerUserTodoSurvivesALateQuestionOnAnOldRef()
    {
        var (p, pool, a) = Dispatched(new PlanInput("a", "ref", Ref: LaterReview), new PlanInput("a", "todo", Todo: "answer user: a"));

        Assert.Null(HierarchyModule.ChecklistOnReply(p, pool, "tm", Req, "_run/a", "tm-implementor", "BLOCKED", "tm-implementor@tm", null, question: true));

        Assert.Equal("answer user: a", a().Todo);
    }

    [Fact]
    public void AnAnswerUserTodoSurvivesACloseOnAReviewItem()
    {
        var (p, pool, a) = Dispatched(new PlanInput("a", "review"), new PlanInput("a", "todo", Todo: "answer user: a"));

        Assert.Null(HierarchyModule.ChecklistOnClose(p, pool, "tm", Req, null, "orchestrator@tm", null, "superseded"));

        Assert.Equal(("in_review", "answer user: a"), (a().State, a().Todo));
    }

    [Fact]
    public void NegativeALateQuestionOnAnOldRefStillReplacesAnOrdinaryTodo()
    {
        var (p, pool, a) = Dispatched(new PlanInput("a", "ref", Ref: LaterReview), new PlanInput("a", "todo", Todo: "answer tm-user: a"));

        Assert.Null(HierarchyModule.ChecklistOnReply(p, pool, "tm", Req, "_run/a", "tm-implementor", "BLOCKED", "tm-implementor@tm", null, question: true));

        Assert.Equal("answer tm-implementor: BLOCKED", a().Todo);
    }

    [Fact]
    public void NegativeATodoThatOnlyResemblesAnAnswerUserTodoIsStillReplaced()
    {
        var (p, pool, a) = Dispatched(new PlanInput("a", "todo", Todo: "answer tm-user: a"));

        Assert.Null(HierarchyModule.ChecklistOnReply(p, pool, "tm", Req, null, "tm-implementor", "BLOCKED", "tm-implementor@tm", null));

        Assert.Equal("waiting_orch", a().State);
        Assert.NotEqual("answer tm-user: a", a().Todo);
    }

    [Fact]
    public void ACloseOfTheLatestRefStillParksAnItemInProgress()
    {
        var (p, pool, a) = Dispatched();

        Assert.Null(HierarchyModule.ChecklistOnClose(p, pool, "tm", Req, null, "orchestrator@tm", null, "superseded"));

        Assert.Equal("waiting_orch", a().State);
    }

    [Fact]
    public void NegativeACloseOfAnOldRefLeavesANewerDispatchAlone()
    {
        var (p, pool, a) = Dispatched(new PlanInput("a", "ref", Ref: LaterReview), new PlanInput("a", "review"));

        Assert.Null(HierarchyModule.ChecklistOnClose(p, pool, "tm", Req, "_run/a", "orchestrator@tm", null, "superseded"));

        Assert.Equal("in_review", a().State);
        Assert.Null(a().Todo);
    }

    [Fact]
    public void NegativeACloseDoesNotSendAReviewItemBack()
    {
        var (p, pool, a) = Dispatched(new PlanInput("a", "review"));

        Assert.Null(HierarchyModule.ChecklistOnClose(p, pool, "tm", Req, null, "orchestrator@tm", null, "superseded"));

        Assert.Equal("in_review", a().State);
    }

    [Fact]
    public void NegativeALateReportOnAnOldRefLeavesANewerDispatchAlone()
    {
        var (p, pool, a) = Dispatched(new PlanInput("a", "ref", Ref: LaterReview));

        Assert.Null(HierarchyModule.ChecklistOnMemberReply(p, pool, "tm", Req, "_run/a", "tm-implementor", "implementor", "STATUS still going", DateTimeOffset.UtcNow, null, static _ => null));

        Assert.Equal("in_progress", a().State);
        Assert.Null(a().Todo);
    }

    [Theory]
    [InlineData("NEEDS-DECISION which store")]
    [InlineData("Status: BLOCKED on the spec")]
    public void ALateQuestionOnAnOldRefRecordsItsTodoAndMovesNothing(string text)
    {
        var (p, pool, a) = Dispatched(new PlanInput("a", "ref", Ref: LaterReview));

        Assert.Null(HierarchyModule.ChecklistOnMemberReply(p, pool, "tm", Req, "_run/a", "tm-implementor", "implementor", text, DateTimeOffset.UtcNow, null, static _ => null));

        Assert.Equal("in_progress", a().State);
        Assert.StartsWith("answer tm-implementor", a().Todo, StringComparison.Ordinal);
    }

    [Fact]
    public void ALateReportOnTheLatestRefStillParks()
    {
        var (p, pool, a) = Dispatched();

        Assert.Null(HierarchyModule.ChecklistOnMemberReply(p, pool, "tm", Req, null, "tm-implementor", "implementor", "a plain note", DateTimeOffset.UtcNow, null, static _ => null));

        Assert.Equal("waiting_orch", a().State);
    }

    [Fact]
    public void NegativeACloseOrLateReportNeverMovesAWaitingUserItem()
    {
        var (p, pool, a) = Dispatched(new PlanInput("a", "wait-user", Note: "Which?"));

        Assert.Null(HierarchyModule.ChecklistOnClose(p, pool, "tm", Req, null, "orchestrator@tm", null, "superseded"));
        Assert.Null(HierarchyModule.ChecklistOnMemberReply(p, pool, "tm", Req, null, "tm-implementor", "implementor", "status: DONE", DateTimeOffset.UtcNow, null, static _ => null));

        Assert.Equal("waiting_user", a().State);
    }

    [Fact]
    public void ActingOnAVerdictClosesTheAutoReviewItemButNotARealItem()
    {
        var rig = HierarchyReadOpsTests.Make();
        var p = rig.Services.Progress;
        Assert.Null(p.RecordChecklist(rig.Pool, "tm", "_run", "orchestrator@tm",
        [
            new PlanInput("auto-review-x", "add", "review x"), HierarchyModule.ChecklistAutoMark("auto-review-x"), new PlanInput("auto-review-x", "ref", Ref: ReviewReq), new PlanInput("auto-review-x", "wait-orch", Note: "VERDICT CHANGES REQUIRED x@abc1234"),
            new PlanInput("real", "add", "Real"), new PlanInput("real", "ref", Ref: LaterReview), new PlanInput("real", "wait-orch", Note: "VERDICT CHANGES REQUIRED x@abc1234"),
        ]).Refused);
        string State(string id) => p.ReadPlan(rig.Pool, "tm", "_run")!.Items.Single(i => i.Item == id).State;

        Assert.Null(HierarchyModule.ChecklistOnVerdictActed(p, rig.Pool, "tm", ReviewReq, "orchestrator@tm", null));
        Assert.Null(HierarchyModule.ChecklistOnVerdictActed(p, rig.Pool, "tm", LaterReview, "orchestrator@tm", null));

        Assert.Equal("done", State("auto-review-x"));
        // negative: an item made by hand is the lead's to close
        Assert.Equal("waiting_orch", State("real"));
    }
}
