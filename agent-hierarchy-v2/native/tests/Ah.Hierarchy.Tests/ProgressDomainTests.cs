using Ah.Abstractions;
using Ah.Config;
using Ah.Core.Json;
using Ah.Platform;
using Ah.Roster;
using Ah.TestKit;

namespace Ah.Hierarchy.Tests;

/// <summary>The ops of the <c>progress</c> domain through the real state engine: creation, the journal append, the State rewrite, the nudge set and the session inventory.</summary>
public sealed class ProgressDomainTests
{
    private const string Time = "2026-10-16T00:30:45-04:00";

    internal sealed record Rig(HierarchyServices Services, string Pool, string Home = "")
    {
        public string Folder => Path.Combine(Pool, "progress", "t");

        public string AhHome => Path.Combine(Home, ".claude", "ah2");
    }

    /// <summary>The body of the team's document <paramref name="name"/> (a progress_docs row), "" when there is none.</summary>
    private static string Doc(Rig rig, string name) => TestProgressDocs.ReadFile(rig.AhHome, Path.Combine(rig.Folder, name)) ?? string.Empty;

    private static string[] Lines(Rig rig, string name) => TestProgressDocs.ReadLines(rig.AhHome, Path.Combine(rig.Folder, name));

    private static void Plant(Rig rig, string name, string text) => TestProgressDocs.WriteFile(rig.AhHome, Path.Combine(rig.Folder, name), text);

    internal static Rig Make(DateTimeOffset? now = null, IHookErrorLog? errors = null)
    {
        var sandbox = UnitSandbox.Create();
        var repo = Path.Combine(sandbox.Root, "repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        File.WriteAllText(Path.Combine(repo, ".git", "HEAD"), "ref: refs/heads/main\n");
        var env = new FakeEnvironment(
            new Dictionary<string, string> { ["HOME"] = sandbox.Home, ["TZ"] = "UTC" },
            new PluginInfo(HierarchyTableFns.AhRoot, "wrangl", "0.0.0", "1"));
        var io = PlatformModule.CreateIo(null);
        var facts = new ProcessFacts(repo, 4242);
        IRosterCore roster = null!;
        var config = ConfigModule.CreateCore(io.Files, env, () => roster, facts);
        roster = RosterModule.CreateCore(io.Files, env, config, io.Processes);
        var writer = new InProcessStateWriter();
        var services = HierarchyModule.Create(new HierarchyEnvironment(
            io.Files, env, new FakeClock(now ?? DateTimeOffset.Parse("2026-06-01T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture)),
            new SequenceRandom(), io.Processes, config, roster, writer, facts, HierarchyModule.CreateBootstrap(io.Writer), errors) { MsgsRoute = TestMsgStore.Route(env.AhHome), ProgressLogs = Ah.StateDb.StateDbModule.CreateProgressLogs(env.AhHome), Checkpoints = Ah.StateDb.StateDbModule.CreateCheckpoints(env.AhHome), ProgressDocs = Ah.StateDb.StateDbModule.CreateProgressDocs(env.AhHome) });
        writer.Domains = services.StateDomains;
        writer.AhHome = env.AhHome;
        var pool = Path.Combine(repo, ".claude", "ah2");
        Directory.CreateDirectory(pool);
        return new Rig(services, pool, sandbox.Home);
    }

    private static JsonObject Create(Rig rig, string file, string content, string team = "t") =>
        rig.Services.StateCalls.Progress("create-if-absent", new JsonObject { ["dir"] = rig.Pool, ["team"] = team, ["file"] = file, ["content"] = content }).AsObject();

    private static JsonObject Journal(Rig rig, string requestId = "id1", string sha = "h1", string stream = "main", string status = "DONE", string? streamMd = "# main\n", string? rollupMd = "# roll\n")
    {
        var args = new JsonObject
        {
            ["dir"] = rig.Pool,
            ["team"] = "t",
            ["stream"] = stream,
            ["request_id"] = requestId,
            ["sha"] = sha,
            ["from"] = "m1",
            ["kind"] = "slice",
            ["time"] = Time,
            ["report_path"] = "msgs/r.md",
            ["status"] = status,
            ["tagged"] = new JsonArray { new JsonObject { ["tag"] = "Decision", ["text"] = "go" } },
        };
        if (streamMd is not null)
        {
            args["stream_md"] = streamMd;
        }

        if (rollupMd is not null)
        {
            args["rollup_md"] = rollupMd;
        }

        return rig.Services.StateCalls.Progress("journal-append", args).AsObject();
    }

    private static bool Flag(JsonObject o, string key) => o.Get(key) is { IsBool: true } b && b.AsBool();

    [Fact]
    public void CreateIfAbsentWritesOnceAndLeavesAnExistingFileByteIdentical()
    {
        var rig = Make();

        var first = Create(rig, "main.md", "first\n");
        var second = Create(rig, "main.md", "second\n");

        Assert.True(Flag(first, "created"));
        Assert.False(Flag(second, "created"));
        Assert.Equal("first\n", Doc(rig, "main.md"));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("a/b")]
    [InlineData("")]
    [InlineData("..")]
    [InlineData("_")]
    [InlineData("_other")]
    [InlineData("_default/x")]
    public void ATeamNameThatIsNotAPlainNameIsRefused(string team)
    {
        var rig = Make();

        Assert.ThrowsAny<Exception>(() => Create(rig, "main.md", "x", team));
        Assert.False(Directory.Exists(Path.Combine(rig.Pool, "progress")));
    }

    [Fact]
    public void TheDefaultScopeFolderIsUnderscoreDefault()
    {
        var rig = Make();

        var result = Create(rig, "main.md", "x\n", "_default");

        Assert.True(Flag(result, "created"));
        Assert.True(TestProgressDocs.Exists(rig.AhHome, Path.Combine(rig.Pool, "progress", "_default", "main.md")));
    }

    private static void AppendFirstReport(Rig rig) => AppendReportFrom(rig, "implementor", "DONE", "20261016-120000-abcd", "main", ["x", "y"]);

    private static void AppendReportFrom(Rig rig, string from, string status, string id, string stream, string[]? openItems)
    {
        var response = Path.Combine(rig.Pool, "msgs", $"{id}--{from}--slice--response.md");
        Directory.CreateDirectory(Path.GetDirectoryName(response)!);
        Ah.TestKit.TestMsgStore.Plant(response, $"---\nid: {id}\ntype: response\nfrom: {from}\n---\n\n## [0] tldr\n- [1] status: {status}\n");
        rig.Services.Progress.AppendReport(rig.Pool, "t", stream, new ProgressStreamInfo("—", "—", "—", null), "m1", response);
        if (openItems is not null)
        {
            var plan = Path.Combine(rig.Folder, stream + ".plan.jsonl");
            var rows = openItems.Select(i => $"{{\"v\":\"1.1\",\"ts\":\"2026-10-16T00:00:00Z\",\"team\":\"t\",\"stream\":\"{stream}\",\"item\":\"{i}\",\"event\":\"add\",\"title\":\"Do-{i}\",\"by\":\"orchestrator@t\",\"session\":\"s\"}}\n");
            TestProgressLogs.AppendFile(rig.AhHome, plan, string.Concat(rows));
        }
    }

    [Theory]
    [InlineData("architect", "DONE")]
    [InlineData("reviewer", "CHANGES REQUIRED")]
    [InlineData("reviewer", "PASS")]
    [InlineData("qa", "DONE")]
    [InlineData("sub-orchestrator", "DONE")]
    [InlineData("implementor", "BLOCKED waiting on the spec")]
    [InlineData("implementor", "needs-decision")]
    public void OnlyAnImplementReportThatIsNotBlockedOrNeedingAnswersIsNudgedAbout(string from, string status)
    {
        var rig = Make(DateTimeOffset.UtcNow);

        AppendReportFrom(rig, from, status, "20261016-120000-abcd", "main", ["x"]);

        Assert.Empty(rig.Services.Progress.PendingNudges(rig.Pool, "t"));
    }

    [Theory]
    [InlineData("implementor")]
    [InlineData("ux-engineer")]
    [InlineData("docs-writer")]
    public void AnImplementClassReportWithOpenItemsIsNudgedAboutWithItsStreamRoleAndItems(string from)
    {
        var rig = Make(DateTimeOffset.UtcNow);

        AppendReportFrom(rig, from, "DONE", "20261016-120000-abcd", "lane-a", ["x", "y"]);

        var nudge = Assert.Single(rig.Services.Progress.PendingNudges(rig.Pool, "t"));
        Assert.Equal(("lane-a", from), (nudge.Stream, nudge.Role));
        Assert.Equal(["x", "y"], nudge.Open);
    }

    [Fact]
    public void AnImplementorReportWithNoOpenItemOrNoPlanIsNotNudgedAbout()
    {
        var rig = Make(DateTimeOffset.UtcNow);

        AppendReportFrom(rig, "implementor", "DONE", "20261016-120000-abcd", "main", null);

        Assert.Empty(rig.Services.Progress.PendingNudges(rig.Pool, "t"));
    }

    [Fact]
    public void AReportWhoseFileIsMissingOrHasNoFromIsNotNudgedAboutAndNothingThrows()
    {
        var rig = Make(DateTimeOffset.UtcNow);
        AppendReportFrom(rig, "implementor", "DONE", "20261016-120000-abcd", "main", ["x"]);
        var response = Directory.GetFiles(Path.Combine(rig.Pool, "msgs")).Single();
        TestMsgStore.Plant(response, "## [0] tldr\n- [1] status: DONE\n");

        Assert.Empty(rig.Services.Progress.PendingNudges(rig.Pool, "t"));

        TestMsgStore.Delete(rig.AhHome, response);

        Assert.Empty(rig.Services.Progress.PendingNudges(rig.Pool, "t"));
    }

    [Fact]
    public void AReportFiledInTheStoreIsNamedByIdAndFoundByIdWithNoFileOnDisk()
    {
        var rig = Make(DateTimeOffset.UtcNow);
        const string id = "20261016-120000-abcd";
        AppendReportFrom(rig, "implementor", "DONE", id, "main", ["x"]);
        File.Delete(Directory.GetFiles(Path.Combine(rig.Pool, "msgs")).Single());

        Assert.Contains($"- Report: {id} (ah msg show {id} response)\n", Doc(rig, "main.journal.md"), StringComparison.Ordinal);
        var nudge = Assert.Single(rig.Services.Progress.PendingNudges(rig.Pool, "t"));
        Assert.Equal(("main", "implementor"), (nudge.Stream, nudge.Role));
    }

    [Fact]
    public void AnOldJournalLineHoldingAResponsePathIsReadFromTheStoreByTheFileNamesId()
    {
        var rig = Make(DateTimeOffset.UtcNow);
        const string id = "20261016-120000-abcd";
        AppendReportFrom(rig, "implementor", "DONE", id, "main", ["x"]);
        File.Delete(Directory.GetFiles(Path.Combine(rig.Pool, "msgs")).Single());
        var old = Doc(rig, "main.journal.md").Replace($"- Report: {id} (ah msg show {id} response)", $"- Report: .claude/ah2/msgs/{id}--implementor--slice--response.md", StringComparison.Ordinal);
        Plant(rig, "main.journal.md", old);

        var nudge = Assert.Single(rig.Services.Progress.PendingNudges(rig.Pool, "t"));
        Assert.Equal(("main", "implementor"), (nudge.Stream, nudge.Role));
    }

    [Fact]
    public void AReportLineNamingAnIdTheStoreDoesNotHoldIsNotNudgedAbout()
    {
        var rig = Make(DateTimeOffset.UtcNow);
        const string id = "20261016-120000-abcd";
        AppendReportFrom(rig, "implementor", "DONE", id, "main", ["x"]);
        var other = Doc(rig, "main.journal.md").Replace($"- Report: {id} (ah msg show {id} response)", "- Report: 20261016-120000-zzzz (ah msg show 20261016-120000-zzzz response)", StringComparison.Ordinal);
        Plant(rig, "main.journal.md", other);

        Assert.Empty(rig.Services.Progress.PendingNudges(rig.Pool, "t"));
    }

    [Fact]
    public void ASecondImplementorReportWithTheChecklistUnchangedIsNotNudgedAboutAfterTheFirstWasMarked()
    {
        var rig = Make(DateTimeOffset.UtcNow);
        AppendReportFrom(rig, "implementor", "DONE", "20261016-120000-abcd", "main", ["x"]);
        var first = Assert.Single(rig.Services.Progress.PendingNudges(rig.Pool, "t"));
        rig.Services.Progress.MarkNudged(rig.Pool, "t", first);

        AppendReportFrom(rig, "implementor", "DONE", "20261016-120100-efgh", "main", null);

        Assert.Empty(rig.Services.Progress.PendingNudges(rig.Pool, "t"));
        Assert.Equal(3, Lines(rig, ".nudged").Last().Split(' ').Length);
    }

    [Fact]
    public void AnOldTwoFieldNudgedLineStillSilencesExactlyItsStreamAndReport()
    {
        var rig = Make(DateTimeOffset.UtcNow);
        AppendReportFrom(rig, "implementor", "DONE", "20261016-120000-abcd", "main", ["x"]);
        Plant(rig, ".nudged", "# ah-progress v1 kind=nudged\nmain 20261016-120000-abcd\n");

        Assert.Empty(rig.Services.Progress.PendingNudges(rig.Pool, "t"));

        Plant(rig, ".nudged", "# ah-progress v1 kind=nudged\nmain 20261016-999999-zzzz\nother 20261016-120000-abcd\n");

        Assert.Single(rig.Services.Progress.PendingNudges(rig.Pool, "t"));
    }

    [Fact]
    public void TheReportThatCreatedAChecklistIsNudgedAboutOnAFileSystemWithRealModifiedTimes()
    {
        var rig = Make(DateTimeOffset.UtcNow);

        AppendFirstReport(rig);

        var nudge = Assert.Single(rig.Services.Progress.PendingNudges(rig.Pool, "t"));
        Assert.Equal("main", nudge.Stream);
    }

    [Fact]
    public void AChecklistEditedByHandAfterTheReportIsNotNudgedAbout()
    {
        var rig = Make(DateTimeOffset.UtcNow);
        AppendFirstReport(rig);
        TestProgressDocs.Touch(rig.AhHome, Path.Combine(rig.Folder, "main.md"), DateTimeOffset.UtcNow.AddMinutes(1));

        Assert.Empty(rig.Services.Progress.PendingNudges(rig.Pool, "t"));
    }

    [Fact]
    public void AFileNameWithASeparatorIsRefused()
    {
        var rig = Make();

        Assert.ThrowsAny<Exception>(() => Create(rig, "../main.md", "x"));
        Assert.ThrowsAny<Exception>(() => Create(rig, "sub/main.md", "x"));
    }

    [Fact]
    public void AnOffTeamStopsEveryOpAndWritesNothing()
    {
        var rig = Make();
        Directory.CreateDirectory(rig.Folder);
        Assert.True(Flag(rig.Services.StateCalls.Progress("off-set", new JsonObject { ["dir"] = rig.Pool, ["team"] = "t", ["off"] = true }).AsObject(), "off"));

        Assert.True(Flag(Create(rig, "main.md", "x"), "off"));
        Assert.True(Flag(Journal(rig), "off"));
        Assert.True(Flag(rig.Services.StateCalls.Progress("nudged-append", new JsonObject { ["dir"] = rig.Pool, ["team"] = "t", ["stream"] = "main", ["report_id"] = "r" }).AsObject(), "off"));
        Assert.True(Flag(rig.Services.StateCalls.Progress("session-append", new JsonObject { ["dir"] = rig.Pool, ["team"] = "t", ["row"] = new JsonObject { ["member"] = "m" } }).AsObject(), "off"));
        Assert.Empty(Directory.GetFileSystemEntries(rig.Folder));

        // on reverses it; a hand-made .off file is not the switch (the nearest input that must not turn it off)
        File.WriteAllText(Path.Combine(rig.Folder, ".off"), ProgressFormat.OffMarker() + "\n");
        Assert.False(Flag(rig.Services.StateCalls.Progress("off-set", new JsonObject { ["dir"] = rig.Pool, ["team"] = "t", ["off"] = false }).AsObject(), "off"));
        Assert.False(Flag(Create(rig, "main.md", "x"), "off"));
    }

    [Fact]
    public void TheFirstReportOfATeamWithNoFolderCreatesTheSkeletonAndTheJournal()
    {
        var rig = Make();

        var result = Journal(rig);

        Assert.True(Flag(result, "appended"));
        Assert.Equal("# roll\n", Doc(rig, "rollup.md"));
        Assert.Equal("# main\n", Doc(rig, "main.md"));
        var journal = Doc(rig, "main.journal.md");
        Assert.StartsWith(ProgressFormat.JournalHeader(), journal, StringComparison.Ordinal);
        Assert.Contains("## " + Time + " · m1 · slice id1\n- Status: DONE\n- Decision: go\n- Report: msgs/r.md\n", journal, StringComparison.Ordinal);
    }

    [Fact]
    public void ASecondReportKeepsTheExistingChecklistAndRollupUntouched()
    {
        var rig = Make();
        Journal(rig);
        Plant(rig, "main.md", "# edited by the orchestrator\n");

        Journal(rig, requestId: "id2", sha: "h2", streamMd: "# main\n");

        Assert.Equal("# edited by the orchestrator\n", Doc(rig, "main.md"));
    }

    [Fact]
    public void TheSameReportSentTwiceAppendsOnce()
    {
        var rig = Make();

        var first = Journal(rig);
        var second = Journal(rig);

        Assert.True(Flag(first, "appended"));
        Assert.False(Flag(second, "appended"));
        Assert.True(Flag(second, "duplicate"));
        Assert.Single(ProgressFormat.EntryTimes(Doc(rig, "main.journal.md")));
    }

    [Fact]
    public void ARefiledReportWithChangedContentAppendsAgainMarkedRefiled()
    {
        var rig = Make();
        Journal(rig);

        var again = Journal(rig, sha: "h2", status: "DONE, fixed");

        Assert.True(Flag(again, "appended"));
        Assert.True(Flag(again, "refiled"));
        var journal = Doc(rig, "main.journal.md");
        Assert.Equal(2, ProgressFormat.EntryTimes(journal).Count);
        Assert.Contains("slice id1 (refiled)", journal, StringComparison.Ordinal);
    }

    [Fact]
    public void ADifferentRequestWithTheSameContentHashIsNotADuplicate()
    {
        var rig = Make();
        Journal(rig);

        var other = Journal(rig, requestId: "id2");

        Assert.True(Flag(other, "appended"));
        Assert.False(Flag(other, "refiled"));
    }

    [Fact]
    public void AJournalWrittenByANewerMajorIsNotAppendedTo()
    {
        var rig = Make();
        Directory.CreateDirectory(rig.Folder);
        var path = Path.Combine(rig.Folder, "main.journal.md");
        File.WriteAllText(path, "<!-- ah-progress v2.0 kind=journal -->\n");

        var result = Journal(rig);

        Assert.False(Flag(result, "appended"));
        Assert.Equal("newer-format", result.Get("skipped")!.Value.AsString());
        Assert.Equal("<!-- ah-progress v2.0 kind=journal -->\n", Doc(rig, "main.journal.md"));
    }

    [Fact]
    public void AJournalMinorNewerThanThisBuildIsAppendedToAndItsMarkerKept()
    {
        var rig = Make();
        Directory.CreateDirectory(rig.Folder);
        var path = Path.Combine(rig.Folder, "main.journal.md");
        File.WriteAllText(path, "<!-- ah-progress v1.7 kind=journal -->\n");

        Assert.True(Flag(Journal(rig), "appended"));

        Assert.StartsWith("<!-- ah-progress v1.7 kind=journal -->\n", Doc(rig, "main.journal.md"), StringComparison.Ordinal);
    }

    private static JsonObject State(Rig rig, string expectSha, string state = "done", string stream = "main") =>
        rig.Services.StateCalls.Progress(
            "state-cas",
            new JsonObject { ["dir"] = rig.Pool, ["team"] = "t", ["stream"] = stream, ["expect_sha"] = expectSha, ["state"] = state }).AsObject();

    private const string Checklist = "<!-- ah-progress v1.0 kind=stream -->\n# main — g\nOwner: m1   State: open\nUpdated: 2026-10-16T00:00:00-04:00\n\n## Mystery\nkept: line\n";

    [Fact]
    public void StateCasRewritesOnlyTheStateWordWhenTheHashMatches()
    {
        var rig = Make();
        Create(rig, "main.md", Checklist);

        var result = State(rig, ProgressFormat.Sha256(Checklist));

        Assert.True(Flag(result, "changed"));
        Assert.Equal(Checklist.Replace("State: open", "State: done", StringComparison.Ordinal), Doc(rig, "main.md"));
    }

    [Fact]
    public void StateCasLeavesTheFileAloneWhenItChangedSinceTheHashWasTaken()
    {
        var rig = Make();
        Create(rig, "main.md", Checklist);
        var stale = ProgressFormat.Sha256(Checklist);
        Plant(rig, "main.md", Checklist + "- [ ] added meanwhile\n");

        var result = State(rig, stale);

        Assert.False(Flag(result, "changed"));
        Assert.True(Flag(result, "conflict"));
        Assert.Contains("State: open", Doc(rig, "main.md"), StringComparison.Ordinal);
    }

    [Fact]
    public void StateCasDoesNothingToAFileWithNoStateLine()
    {
        var rig = Make();
        const string bare = "# hand written\nno state here\n";
        Create(rig, "main.md", bare);

        var result = State(rig, ProgressFormat.Sha256(bare));

        Assert.False(Flag(result, "changed"));
        Assert.True(Flag(result, "no_state"));
        Assert.Equal(bare, Doc(rig, "main.md"));
    }

    [Fact]
    public void StateCasDoesNothingToAFileFromANewerMajor()
    {
        var rig = Make();
        var newer = Checklist.Replace("v1.0", "v2.0", StringComparison.Ordinal);
        Create(rig, "main.md", newer);

        var result = State(rig, ProgressFormat.Sha256(newer));

        Assert.False(Flag(result, "changed"));
        Assert.Equal(newer, Doc(rig, "main.md"));
    }

    [Fact]
    public void ANudgeIsRecordedOncePerStreamAndReport()
    {
        var rig = Make();
        JsonObject Nudge(string stream, string report) =>
            rig.Services.StateCalls.Progress(
                "nudged-append",
                new JsonObject { ["dir"] = rig.Pool, ["team"] = "t", ["stream"] = stream, ["report_id"] = report }).AsObject();

        Assert.True(Flag(Nudge("main", "r1"), "appended"));
        Assert.False(Flag(Nudge("main", "r1"), "appended"));
        Assert.True(Flag(Nudge("main", "r2"), "appended"));
        Assert.True(Flag(Nudge("other", "r1"), "appended"));

        Assert.Equal(
            [ProgressFormat.NudgedMarker(), "main r1", "main r2", "other r1"],
            Lines(rig, ".nudged"));
    }

    [Fact]
    public void ASessionRowCarriesTheFormatVersionAndIsAppended()
    {
        var rig = Make();
        JsonObject Session(string member) =>
            rig.Services.StateCalls.Progress(
                "session-append",
                new JsonObject { ["dir"] = rig.Pool, ["team"] = "t", ["row"] = new JsonObject { ["member"] = member, ["event"] = "register" } }).AsObject();

        Session("m1");
        Session("m2");

        var lines = Lines(rig, "sessions.jsonl");
        Assert.Equal(2, lines.Length);
        Assert.Contains("\"v\":\"1.0\"", lines[0], StringComparison.Ordinal);
        Assert.Contains("\"member\":\"m2\"", lines[1], StringComparison.Ordinal);
    }

    private static JsonObject SessionRow(string session, string eventName) =>
        new() { ["session_id"] = session, ["member"] = "orchestrator", ["event"] = eventName };

    private static bool AppendSession(Rig rig, string session, string eventName) =>
        rig.Services.StateCalls.Progress("session-append", new JsonObject { ["dir"] = rig.Pool, ["team"] = "t", ["row"] = SessionRow(session, eventName) }).AsObject()["appended"].AsBool();

    [Fact]
    public void AnOrchestratorStartForASessionAlreadyRecordedIsNotAppendedAgain()
    {
        var rig = Make();

        Assert.True(AppendSession(rig, "s1", "orchestrator-start"));
        Assert.False(AppendSession(rig, "s1", "orchestrator-start"));

        Assert.Single(Lines(rig, "sessions.jsonl"));
    }

    [Fact]
    public void AnOrchestratorStartForAnotherSessionAndARepeatedRegisterAreStillAppended()
    {
        var rig = Make();

        Assert.True(AppendSession(rig, "s1", "orchestrator-start"));
        Assert.True(AppendSession(rig, "s2", "orchestrator-start"));
        Assert.True(AppendSession(rig, "s1", "register"));
        Assert.True(AppendSession(rig, "s1", "register"));

        Assert.Equal(4, Lines(rig, "sessions.jsonl").Length);
    }

    private static JsonObject Upsert(Rig rig, string slug, string status, string from = "tm-implementor", string stream = "main", string time = "2026-10-25T01:00:00Z") =>
        rig.Services.StateCalls.Progress(
            "rollup-block-upsert",
            new JsonObject { ["dir"] = rig.Pool, ["team"] = "t", ["stream"] = stream, ["slug"] = slug, ["status"] = status, ["from"] = from, ["time"] = time, ["path"] = "x (ah msg show x response)" }).AsObject();

    private static string RollupPath(Rig rig) => Path.Combine(rig.Folder, "rollup.md");

    private const string Begin = "<!-- ah:rollup-block:begin (managed by ah; text between the markers is rewritten) -->";

    private const string End = "<!-- ah:rollup-block:end -->";

    [Fact]
    public void AMissingRollupIsCreatedHoldingJustTheBlockWithOneLine()
    {
        var rig = Make();

        Assert.True(Flag(Upsert(rig, "slice-one", "DONE"), "updated"));

        Assert.Equal(
            Begin + "\nmain | slice-one | DONE | tm-implementor | 2026-10-25T01:00:00Z | x (ah msg show x response)\n" + End + "\n",
            Doc(rig, "rollup.md"));
    }

    [Fact]
    public void AReportOnASlugCreatesItsLineAndALaterOneUpdatesItInPlaceWhileAnotherSlugGetsItsOwnLine()
    {
        var rig = Make();

        Upsert(rig, "slice-one", "DONE");
        Upsert(rig, "slice-two", "BLOCKED", from: "tm-reviewer");
        Upsert(rig, "slice-one", "PASS", time: "2026-10-25T02:00:00Z");

        var lines = Lines(rig, "rollup.md");
        Assert.Equal(4, lines.Length);
        Assert.Equal("main | slice-one | PASS | tm-implementor | 2026-10-25T02:00:00Z | x (ah msg show x response)", lines[1]);
        Assert.StartsWith("main | slice-two | BLOCKED | tm-reviewer | ", lines[2], StringComparison.Ordinal);
    }

    [Fact]
    public void TheOrchestratorsOwnTextOutsideTheMarkersIsByteIdenticalAfterTenReports()
    {
        var rig = Make();
        Directory.CreateDirectory(rig.Folder);
        var before = "# tm — progress\r\nGoal: ship  \n\n";
        var after = "\n## Notes\n- mine | not | a | row\nResume point: /x/y.json";
        File.WriteAllText(RollupPath(rig), before + Begin + "\n" + End + "\n" + after);

        for (var i = 0; i < 10; i++)
        {
            Upsert(rig, i % 2 == 0 ? "slice-one" : "slice-two", "S" + i);
        }

        var text = Doc(rig, "rollup.md");
        Assert.StartsWith(before + Begin + "\n", text, StringComparison.Ordinal);
        Assert.EndsWith("\n" + End + "\n" + after, text, StringComparison.Ordinal);
        Assert.Equal(2, text.Split('\n').Count(l => l.StartsWith("main | slice-", StringComparison.Ordinal)));
    }

    [Fact]
    public void ARollupWithNoMarkersGetsTheBlockAppendedOnceAndItsTextIsKept()
    {
        var rig = Make();
        Directory.CreateDirectory(rig.Folder);
        File.WriteAllText(RollupPath(rig), "# mine\nno trailing newline");

        Upsert(rig, "slice-one", "DONE");
        Upsert(rig, "slice-two", "DONE");

        var text = Doc(rig, "rollup.md");
        Assert.StartsWith("# mine\nno trailing newline\n" + Begin + "\n", text, StringComparison.Ordinal);
        Assert.Single(text.Split('\n'), l => l == Begin);
        Assert.Single(text.Split('\n'), l => l == End);
    }

    [Theory]
    [InlineData("a\n" + Begin + "\nx\n")]
    [InlineData("a\n" + End + "\nb\n")]
    [InlineData("a\n" + End + "\n" + Begin + "\n")]
    [InlineData("a\n" + Begin + "\n" + Begin + "\n" + End + "\n")]
    public void ADamagedMarkerPairLeavesTheRollupUntouched(string text)
    {
        var rig = Make();
        Directory.CreateDirectory(rig.Folder);
        File.WriteAllText(RollupPath(rig), text);

        var result = Upsert(rig, "slice-one", "DONE");

        Assert.False(Flag(result, "updated"));
        Assert.Equal(text, Doc(rig, "rollup.md"));
    }

    [Fact]
    public void AnAckModeUpsertOnlyRestampsAnExistingLineAndAddsNoneOrAnyFile()
    {
        var rig = Make();
        JsonObject Ack(string slug) => rig.Services.StateCalls.Progress(
            "rollup-block-upsert",
            new JsonObject { ["dir"] = rig.Pool, ["team"] = "t", ["slug"] = slug, ["ack"] = true, ["time"] = "2026-10-25T09:00:00Z" }).AsObject();

        Assert.True(Flag(Ack("slice-one"), "no_line"));
        Assert.False(TestProgressDocs.Exists(rig.AhHome, RollupPath(rig)));

        Upsert(rig, "slice-one", "DONE", from: "tm-reviewer", stream: "stream-a");
        Assert.True(Flag(Ack("slice-two"), "no_line"));
        Assert.True(Flag(Ack("slice-one"), "updated"));

        var row = Lines(rig, "rollup.md").Single(l => l.Contains(" | slice-", StringComparison.Ordinal));
        Assert.Equal("stream-a | slice-one | acked | tm-reviewer | 2026-10-25T09:00:00Z | x (ah msg show x response)", row);
    }

    [Fact]
    public void AnOffTeamHasNoBlockWritten()
    {
        var rig = Make();
        TestProgressDocs.Off(rig.AhHome, rig.Pool, "t");

        Assert.True(Flag(Upsert(rig, "slice-one", "DONE"), "off"));
        Assert.False(TestProgressDocs.Exists(rig.AhHome, RollupPath(rig)));
    }

    [Fact]
    public void AFieldWithALineBreakOrABarCannotSplitOrForgeARow()
    {
        var rig = Make();

        Upsert(rig, "slice-one", "DONE | forged\nmain | slice-two | PASS");

        var rows = Lines(rig, "rollup.md").Where(l => l.StartsWith("main | ", StringComparison.Ordinal)).ToList();
        Assert.Single(rows);
        Assert.Equal(6, rows[0].Split(" | ").Length);
    }

    [Fact]
    public void AJournalThatIsNotValidUtf8IsAppendedToAtTheByteLevel()
    {
        var rig = Make();
        Directory.CreateDirectory(rig.Folder);
        byte[] old = [.. System.Text.Encoding.UTF8.GetBytes("<!-- ah-progress v1.7 kind=journal -->\n"), 0xFF, (byte)'\n'];
        File.WriteAllBytes(Path.Combine(rig.Folder, "main.journal.md"), old);

        Assert.True(Flag(Journal(rig), "appended"));

        var raw = rig.Services.Progress.Docs(rig.Pool, "t").Single(d => d.Name == "main.journal.md").Raw;
        Assert.NotNull(raw);
        Assert.Equal(old, raw[..old.Length]);
        // the negative: the entry itself is whole UTF-8 after the old bytes, not a re-encoding of them
        Assert.Contains("- Report: msgs/r.md", System.Text.Encoding.UTF8.GetString(raw[old.Length..]), StringComparison.Ordinal);
    }

    /// <summary>per-dir-files-to-db S11d D2: the checklist and journal writes land in progress_docs, not in the folder.</summary>
    [Fact]
    public void AJournalAppendOnANewStreamWritesThreeRowsAndStateCasChecksTheRowBody()
    {
        var rig = Make();
        string? Body(string name) => rig.Services.Progress.Docs(rig.Pool, "t").FirstOrDefault(d => d.Name == name)?.Body;

        var first = Journal(rig, streamMd: Checklist);
        var again = Journal(rig);

        Assert.Equal(["main.journal.md", "main.md", "rollup.md"], rig.Services.Progress.Docs(rig.Pool, "t").Select(static d => d.Name));
        Assert.Equal(Path.Combine(rig.Folder, "main.journal.md"), first["file"].AsString());
        Assert.True(Flag(again, "duplicate"));
        Assert.False(Directory.Exists(rig.Folder) && Directory.EnumerateFiles(rig.Folder).Any());

        // the negative: a stale hash is a conflict and leaves the body; the body's own hash rewrites it
        Assert.True(Flag(State(rig, ProgressFormat.Sha256("stale")), "conflict"));
        Assert.Equal(Checklist, Body("main.md"));
        Assert.True(Flag(State(rig, ProgressFormat.Sha256(Checklist)), "changed"));
        Assert.Contains("State: done", Body("main.md"), StringComparison.Ordinal);

        // the negative: a body written by a newer major is not overwritten
        const string Newer = "<!-- ah-progress v9.0 kind=stream -->\n# other\nState: open\n";
        Create(rig, "other.md", Newer);
        Assert.Equal("newer-format", State(rig, ProgressFormat.Sha256(Newer), stream: "other")["skipped"].AsString());
        Assert.Equal(Newer, Body("other.md"));
    }
}
