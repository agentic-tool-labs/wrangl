using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Hierarchy;
using Ah.Mesh;
using Ah.Platform;
using Ah.TestKit;

namespace Ah.Cli.Tests;

/// <summary>The checklist verbs and the reserved loose-end stream, driven through the roster command line.</summary>
public sealed partial class RosterOwnerGuardTests
{
    private string LogOf(string stream) => Path.Combine(ProgressFolder, stream + ".plan.jsonl");

    // per-dir-files-to-db S11c: the plan logs are progress_logs rows; these read and plant them by the old file path
    private string ReadLog(string path) => TestProgressLogs.ReadFile(_sandbox.AhHome, path);

    private string[] ReadLogLines(string path) => ReadLog(path).Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private bool LogExists(string path) => TestProgressLogs.Exists(_sandbox.AhHome, path);

    private void AppendLog(string path, string text) => TestProgressLogs.AppendFile(_sandbox.AhHome, path, text);

    private void WriteLog(string path, string text) => TestProgressLogs.WriteFile(_sandbox.AhHome, path, text);

    /// <summary>Appends a row the transition table would write: an automatic move of <paramref name="item"/> with a cause.</summary>
    private void AutomaticRow(string stream, string item, string ev, string ts, string causeEvent = "reply") =>
        AppendLog(LogOf(stream), $"{{\"v\":1,\"ts\":\"{ts}\",\"team\":\"tm\",\"stream\":\"{stream}\",\"item\":\"{item}\",\"event\":\"{ev}\",\"cause\":{{\"event\":\"{causeEvent}\",\"ref\":\"20261101-100000-aaaa\",\"at\":\"{ts}\"}},\"by\":\"tm-implementor@tm\",\"session\":null}}\n");

    [Fact]
    public void SetAcceptsTheStoredStateNames()
    {
        TwoStreamsWithItemA();

        var inProgress = Run("checklist", "set", "s1/b", "--state", "in_progress", "--team", "tm");
        var review = Run("checklist", "set", "s1/b", "--state", "in_review", "--team", "tm", "--plain");
        var approved = Run("checklist", "set", "s1/b", "--state", "approved", "--team", "tm", "--plain");
        var queued = Run("checklist", "set", "s1/b", "--state", "queued", "--team", "tm", "--plain");

        Assert.Equal(0, inProgress.Code);
        Assert.Contains("dispatched", inProgress.Stdout, StringComparison.Ordinal);
        Assert.Contains("in_review", review.Stdout, StringComparison.Ordinal);
        Assert.Contains("approved", approved.Stdout, StringComparison.Ordinal);
        Assert.Contains("queued", queued.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void SetEchoesTheAskedStateOnlyWhenItIsStoredUnderAnotherName()
    {
        TwoStreamsWithItemA();

        var renamed = Run("checklist", "set", "s1/b", "--state", "in_progress", "--team", "tm");
        var same = Run("checklist", "set", "s1/b", "--state", "queued", "--team", "tm");

        Assert.Equal(0, renamed.Code);
        var json = Ah.Core.Json.JsonText.Parse(renamed.Stdout);
        Assert.Equal("dispatched", json.Member("state")?.AsString());
        Assert.Equal("in_progress", json.Member("asked")?.AsString());
        Assert.Contains("`in_progress` is shown as `dispatched`", renamed.Stderr, StringComparison.Ordinal);
        // negative: the name given is the name stored, so nothing is added
        Assert.Equal(0, same.Code);
        Assert.Null(Ah.Core.Json.JsonText.Parse(same.Stdout).Member("asked"));
        Assert.DoesNotContain("is shown as", same.Stderr, StringComparison.Ordinal);
    }

    private string? AfterOf(string key) =>
        Ah.Core.Json.JsonText.Parse(Run("checklist", "show", "--all", "--team", "tm").Stdout).Member("items")!.Value.AsArray()
            .First(i => i.Member("stream")?.AsString() + "/" + i.Member("id")?.AsString() == key).Member("after") is { IsText: true } after ? after.AsString() : null;

    [Fact]
    public void ACycleASelfReferenceAndAnUnknownItemAreNotStoredAndTheRestOfTheCommandStillApplies()
    {
        TwoStreamsWithItemA();

        var stored = Run("checklist", "set", "s1/b", "--after", "s2/a", "--team", "tm");
        Assert.Equal(0, stored.Code);
        Assert.Equal("s2/a", AfterOf("s1/b"));
        Assert.Contains("waiting on s2/a", Run("checklist", "show", "--plain", "--team", "tm").Stdout, StringComparison.Ordinal);

        var cycle = Run("checklist", "set", "s2/a", "--after", "s1/b", "--todo", "cycle todo", "--team", "tm");
        var self = Run("checklist", "set", "s1/b", "--after", "s1/b", "--todo", "self todo", "--team", "tm");
        var unknown = Run("checklist", "set", "s1/a", "--after", "nope", "--todo", "unknown todo", "--team", "tm");

        Assert.Equal(0, cycle.Code);
        Assert.Contains("--after s1/b not stored", cycle.Stderr, StringComparison.Ordinal);
        Assert.Contains("cycle", cycle.Stderr, StringComparison.Ordinal);
        Assert.Null(AfterOf("s2/a"));
        Assert.Contains("cycle todo", ReadLog(LogOf("s2")), StringComparison.Ordinal);

        Assert.Equal(0, self.Code);
        Assert.Contains("itself", self.Stderr, StringComparison.Ordinal);
        Assert.Equal("s2/a", AfterOf("s1/b"));
        Assert.Contains("self todo", ReadLog(LogOf("s1")), StringComparison.Ordinal);

        Assert.Equal(0, unknown.Code);
        Assert.Contains("no item nope", unknown.Stderr, StringComparison.Ordinal);
        Assert.Null(AfterOf("s1/a"));
        Assert.Contains("unknown todo", ReadLog(LogOf("s1")), StringComparison.Ordinal);

        // none clears it
        Assert.Equal(0, Run("checklist", "set", "s1/b", "--after", "none", "--team", "tm").Code);
        Assert.Null(AfterOf("s1/b"));
    }

    [Fact]
    public void AddAfterLastNamesTheItemThisCallerLastAddedToTheStreamAndIsIgnoredWhenThereIsNone()
    {
        TeamOwnedBy(Self);

        var first = Run("checklist", "add", "Step one", "--after", "last", "--team", "tm");
        var second = Run("checklist", "add", "Step two", "--after", "last", "--team", "tm");

        Assert.Equal(0, first.Code);
        Assert.Equal(0, second.Code);
        var one = "_run/" + Ah.Core.Json.JsonText.Parse(first.Stdout).Member("item")?.AsString();
        var two = "_run/" + Ah.Core.Json.JsonText.Parse(second.Stdout).Member("item")?.AsString();
        Assert.Contains("--after last ignored", first.Stderr, StringComparison.Ordinal);
        Assert.Null(AfterOf(one));
        Assert.Equal(one, AfterOf(two));
    }

    [Fact]
    public void NegativeSetRefusesAnUnknownStateNameAndShowTrailNeedsAnId()
    {
        TwoStreamsWithItemA();

        var unknown = Run("checklist", "set", "s1/b", "--state", "inprogress", "--team", "tm");
        var trail = Run("checklist", "show", "--trail", "--team", "tm");

        Assert.Equal(2, unknown.Code);
        Assert.Contains("in_progress", unknown.Stderr, StringComparison.Ordinal);
        Assert.Equal(2, trail.Code);
    }

    [Fact]
    public void TrailShowsTheFromToAndCauseOfAnAutomaticRowAndTheUndoRestoresThePreviousState()
    {
        TwoStreamsWithItemA();
        Assert.Equal(0, Run("checklist", "set", "s1/b", "--state", "in_progress", "--team", "tm").Code);
        AutomaticRow("s1", "b", "review", "2030-01-01T00:00:00Z");

        var trail = Run("checklist", "show", "s1/b", "--trail", "--plain", "--team", "tm");

        Assert.Equal(0, trail.Code);
        Assert.Contains("in_progress→in_review cause=reply ref=20261101-100000-aaaa at=2030-01-01T00:00:00Z", trail.Stdout, StringComparison.Ordinal);
        Assert.Contains("undo: ah roster checklist set s1/b --state in_progress --note \"revert reply\"", trail.Stdout, StringComparison.Ordinal);

        var undo = Run("checklist", "set", "s1/b", "--state", "in_progress", "--note", "revert reply", "--team", "tm", "--plain");

        Assert.Equal(0, undo.Code);
        Assert.Contains("b → dispatched", undo.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void EachFilterReturnsOnlyTheMatchingItems()
    {
        TwoStreamsWithItemA();
        Assert.Equal(0, Run("checklist", "set", "s1/b", "--state", "in_progress", "--team", "tm").Code);
        AutomaticRow("s1", "b", "review", "2030-01-01T00:00:00Z");
        AppendLog(LogOf("s2"), "{\"v\":1,\"ts\":\"2020-01-01T00:00:00Z\",\"team\":\"tm\",\"stream\":\"s2\",\"item\":\"old\",\"event\":\"add\",\"title\":\"Old\",\"by\":\"orchestrator@tm\",\"session\":null}\n"
            + "{\"v\":1,\"ts\":\"2020-01-01T00:00:00Z\",\"team\":\"tm\",\"stream\":\"s2\",\"item\":\"old\",\"event\":\"start\",\"by\":\"orchestrator@tm\",\"session\":null}\n");

        var byState = Run("checklist", "show", "--state", "in_review", "--team", "tm", "--plain");
        var stale = Run("checklist", "show", "--stale", "--team", "tm", "--plain");
        var since = Run("checklist", "show", "--since", "2029-06-01T00:00:00Z", "--team", "tm", "--plain");
        var both = Run("checklist", "show", "--state", "in_progress", "--stale", "--team", "tm", "--plain");

        Assert.Equal(["b"], Ids(byState.Stdout));
        Assert.Equal(["old"], Ids(stale.Stdout));
        Assert.Equal(["b"], Ids(since.Stdout));
        Assert.Equal(["old"], Ids(both.Stdout));
    }

    [Fact]
    public void NegativeAFilterWithNoMatchListsNothingAndABadSinceIsRefused()
    {
        TwoStreamsWithItemA();

        var none = Run("checklist", "show", "--state", "approved", "--team", "tm", "--plain");
        var bad = Run("checklist", "show", "--since", "yesterday", "--team", "tm");

        Assert.Empty(Ids(none.Stdout));
        Assert.NotEqual(0, bad.Code);
    }

    /// <summary>The item ids <c>show --plain</c> printed, one per item line.</summary>
    private static List<string> Ids(string plain) =>
        [.. plain.Split('\n').Where(static l => l.Contains(" owner=", StringComparison.Ordinal)).Select(static l => l.Split(' ')[0])];

    private void TwoStreamsWithItemA()
    {
        TeamOwnedBy(Self, "{\"s1\":{\"state\":\"open\"},\"s2\":{\"state\":\"open\"}}");
        Run("progress", "item", "add", "s1", "a", "--title", "Do a in s1", "--team", "tm");
        Run("progress", "item", "add", "s2", "a", "--title", "Do a in s2", "--team", "tm");
        Run("progress", "item", "add", "s1", "b", "--title", "Do b", "--team", "tm");
    }

    [Fact]
    public void AnIdInTwoStreamsIsRefusedWithTheCandidatesNamedAndNothingIsWritten()
    {
        TwoStreamsWithItemA();
        var before1 = ReadLog(LogOf("s1"));
        var before2 = ReadLog(LogOf("s2"));

        var set = Run("checklist", "set", "a", "--todo", "send the brief", "--team", "tm");
        var done = Run("checklist", "done", "a", "--team", "tm");

        Assert.NotEqual(0, set.Code);
        Assert.Contains("s1/a", set.Stderr, StringComparison.Ordinal);
        Assert.Contains("s2/a", set.Stderr, StringComparison.Ordinal);
        Assert.NotEqual(0, done.Code);
        Assert.Equal(before1, ReadLog(LogOf("s1")));
        Assert.Equal(before2, ReadLog(LogOf("s2")));
    }

    [Fact]
    public void AStreamQualifiedIdWritesOnlyThatStreamAndAnIdInOneStreamNeedsNoQualifier()
    {
        TwoStreamsWithItemA();
        var before1 = ReadLog(LogOf("s1"));

        var qualified = Run("checklist", "set", "s2/a", "--todo", "send the brief", "--team", "tm");
        var unique = Run("checklist", "set", "b", "--todo", "ping", "--team", "tm");

        Assert.Equal(0, qualified.Code);
        Assert.Contains("send the brief", ReadLog(LogOf("s2")), StringComparison.Ordinal);
        Assert.Equal(0, unique.Code);
        var after1 = ReadLog(LogOf("s1"));
        Assert.DoesNotContain("send the brief", after1, StringComparison.Ordinal);
        Assert.Contains("ping", after1, StringComparison.Ordinal);
        Assert.NotEqual(before1, after1);
    }

    [Fact]
    public void ANoteGivenWithAQueuedOrDispatchedStateIsKept()
    {
        TwoStreamsWithItemA();

        var run = Run("checklist", "set", "s1/b", "--state", "dispatched", "--note", "handed to implementor-3", "--team", "tm");

        Assert.Equal(0, run.Code);
        Assert.Contains("handed to implementor-3", ReadLog(LogOf("s1")), StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeTheRunStreamIsRefusedByStreamOpenAndByEveryProgressVerbAndNothingIsWrittenForIt()
    {
        TeamOwnedBy(Self);

        var open = Run("stream-open", "_run", "--team", "tm");
        var add = Run("progress", "item", "add", "_run", "x", "--title", "X", "--team", "tm");
        var import = Run("progress", "item", "import", "_run", "--team", "tm");

        Assert.NotEqual(0, open.Code);
        Assert.NotEqual(0, add.Code);
        Assert.NotEqual(0, import.Code);
        Assert.NotEqual(string.Empty, open.Stderr);
        Assert.NotEqual(string.Empty, import.Stderr);
        var written = Directory.Exists(ProgressFolder) ? Directory.GetFiles(ProgressFolder, "_*") : [];
        Assert.Empty(written);
    }

    [Fact]
    public void TheChecklistAddsALooseEndToTheRunStreamThroughItsOwnVerb()
    {
        TeamOwnedBy(Self);

        var add = Run("checklist", "add", "Answer the architect", "--team", "tm");

        Assert.Equal(0, add.Code);
        Assert.True(LogExists(LogOf("_run")));
        Assert.False(File.Exists(Path.Combine(ProgressFolder, "_run.md")));
        Assert.Contains("Answer the architect", Run("checklist", "show", "--team", "tm").Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void AChecklistAddNamesTheMemberWhoMadeItAndOtherwiseTheOrchestrator()
    {
        TeamOwnedBy(Self);
        Assert.Equal(0, Run("checklist", "add", "By the owner", "--team", "tm").Code);
        Assert.Contains("\"by\":\"orchestrator@tm\"", ReadLog(LogOf("_run")), StringComparison.Ordinal);

        _env[Ah.Mesh.MeshModule.MemberMarkerEnv] = "tm-impl";
        _nearest = new NearestSession(NearestKind.Member, "tm", "tm-impl", Environment.ProcessId, false);
        Assert.Equal(0, Run("checklist", "add", "By the member", "--team", "tm").Code);

        Assert.Contains("\"by\":\"tm-impl@tm\"", ReadLog(LogOf("_run")), StringComparison.Ordinal);
    }

    [Fact]
    public void AddTakesTheStateAndOwnerOfWorkAlreadyInFlight()
    {
        TeamOwnedBy(Self);

        var add = Run("checklist", "add", "In flight", "--state", "dispatched", "--owner", "impl", "--team", "tm");

        Assert.Equal(0, add.Code);
        Assert.Equal("dispatched", Ah.Core.Json.JsonText.Parse(add.Stdout).Member("state")!.Value.AsString());
        Assert.Contains("\"holder\":\"impl\"", ReadLog(LogOf("_run")), StringComparison.Ordinal);
    }

    [Fact]
    public void AddWithoutThemStaysQueuedAndABadStateWritesNothing()
    {
        TeamOwnedBy(Self);

        Assert.Equal("queued", Ah.Core.Json.JsonText.Parse(Run("checklist", "add", "Not started", "--team", "tm").Stdout).Member("state")!.Value.AsString());
        Assert.NotEqual(0, Run("checklist", "add", "Bad", "--state", "nonsense", "--team", "tm").Code);
        Assert.DoesNotContain("Bad", ReadLog(LogOf("_run")), StringComparison.Ordinal);
    }

    [Fact]
    public void ChecklistParentStoresTheShapeCheckedIdWithoutReadingTheBacklogAndNoneClearsIt()
    {
        TeamOwnedBy(Self);

        // no backlog exists here: only the shape is checked
        var add = Run("checklist", "add", "Build the slice", "--parent", "b12", "--team", "tm");
        Assert.Equal(0, add.Code);
        Assert.Contains("\"event\":\"parent\",\"parent\":\"b12\"", ReadLog(LogOf("_run")), StringComparison.Ordinal);

        var id = Ah.Core.Json.JsonText.Parse(add.Stdout).Member("item")!.Value.AsString();
        Assert.Equal(0, Run("checklist", "set", id, "--parent", "none", "--team", "tm").Code);
        Assert.Contains("\"event\":\"parent\",\"parent\":\"\"", ReadLog(LogOf("_run")), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("12")]
    [InlineData("b")]
    [InlineData("B12")]
    [InlineData("b12x")]
    public void ChecklistParentThatIsNotABacklogIdIsExitTwoAndNothingIsWritten(string parent)
    {
        TeamOwnedBy(Self);
        var first = Run("checklist", "add", "Build the slice", "--team", "tm");
        Assert.Equal(0, first.Code);
        var id = Ah.Core.Json.JsonText.Parse(first.Stdout).Member("item")!.Value.AsString();
        var before = ReadLog(LogOf("_run"));

        var set = Run("checklist", "set", id, "--parent", parent, "--team", "tm");
        var add = Run("checklist", "add", "Another", "--parent", parent, "--team", "tm");

        Assert.Equal(2, set.Code);
        Assert.Equal(2, add.Code);
        Assert.Contains("--parent", set.Stderr, StringComparison.Ordinal);
        Assert.Equal(before, ReadLog(LogOf("_run")));
    }

    private static string Row(string item, string ev, string? extra = null) =>
        $"{{\"v\":1,\"ts\":\"2026-10-11T10:00:00Z\",\"team\":\"tm\",\"stream\":\"_run\",\"item\":\"{item}\",\"event\":\"{ev}\"{extra},\"by\":\"o\",\"session\":null}}";

    [Fact]
    public void ShowPrintsAQueueSectionNamingWhoWaitsOnTheOrchestratorAndNothingForAnItemThatIsDone()
    {
        TeamOwnedBy(Self);
        _ = Directory.CreateDirectory(ProgressFolder);
        WriteLog(
            LogOf("_run"),
            string.Join('\n', Row("ans", "add", ",\"title\":\"Answer\""), Row("ans", "assign", ",\"holder\":\"impl-1\""), Row("ans", "todo", ",\"todo\":\"answer impl-1: NEEDS-DECISION\""), Row("ans", "wait-orch", ",\"note\":\"w\""),
                Row("fin", "add", ",\"title\":\"Finished\""), Row("fin", "done"),
                Row("usr", "add", ",\"title\":\"User\""), Row("usr", "assign", ",\"holder\":\"impl-2\""), Row("usr", "wait-user", ",\"note\":\"w\"")) + "\n");

        var shown = Run("checklist", "show", "--plain", "--team", "tm").Stdout;

        Assert.Contains("Queue:\n  answer impl-1 (NEEDS-DECISION, ", shown, StringComparison.Ordinal);
        Assert.DoesNotContain("fin (", shown.Split("Queue:")[1], StringComparison.Ordinal);
        Assert.DoesNotContain("impl-2", shown.Split("Queue:")[1], StringComparison.Ordinal);
    }

    private void QueuedItem(string item, string role, DateTimeOffset since)
    {
        var at = since.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        string Stamped(string ev, string? extra = null) => Row(item, ev, extra).Replace("2026-10-11T10:00:00Z", at, StringComparison.Ordinal);
        _ = Directory.CreateDirectory(ProgressFolder);
        WriteLog(LogOf("_run"), string.Join('\n', Stamped("add", ",\"title\":\"Build it\""), Stamped("role", $",\"role\":\"{role}\""), Stamped("next")) + "\n");
    }

    [Fact]
    public void ShowPrintsASpawnLineForAQueuedItemWhoseRoleHasNoLiveMember()
    {
        TeamOwnedBy(Self);
        QueuedItem("feat", "implementor", DateTimeOffset.UtcNow.AddMinutes(-30));

        var shown = Run("checklist", "show", "--plain", "--team", "tm").Stdout;

        Assert.Contains("Queue:\n  spawn implementor for _run/feat\n", shown, StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeAQueuedItemThatHasWaitedLessThanTenMinutesGetsNoSpawnLine()
    {
        TeamOwnedBy(Self);
        QueuedItem("feat", "implementor", DateTimeOffset.UtcNow.AddMinutes(-2));

        Assert.DoesNotContain("spawn ", Run("checklist", "show", "--plain", "--team", "tm").Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void ShowPrintsNoQueueSectionWhenNothingWaitsOnTheOrchestrator()
    {
        TeamOwnedBy(Self);
        _ = Run("checklist", "add", "Just a note", "--team", "tm");

        Assert.DoesNotContain("Queue:", Run("checklist", "show", "--plain", "--team", "tm").Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void ARefAnotherItemListsIsAddedAndSetAgainAndEveryItemCarryingItIsShown()
    {
        TwoStreamsWithItemA();
        Assert.Equal(0, Run("checklist", "set", "s1/a", "--ref", "20261016-140001-abcd", "--team", "tm").Code);

        var set = Run("checklist", "set", "s2/a", "--ref", "20261016-140001-abcd", "--team", "tm");
        var add = Run("checklist", "add", "Another", "--ref", "20261016-140001-abcd", "--stream", "s2", "--team", "tm");

        Assert.True(set.Code == 0, set.Stderr);
        Assert.True(add.Code == 0, add.Stderr);
        Assert.Contains("20261016-140001-abcd", ReadLog(LogOf("s1")), StringComparison.Ordinal);
        Assert.Contains("\"another\"", ReadLog(LogOf("s2")), StringComparison.Ordinal);
        var show = Run("checklist", "show", "--team", "tm", "--plain");
        Assert.True(show.Code == 0, show.Stderr);
        Assert.Contains("Another", show.Stdout, StringComparison.Ordinal);
        Assert.Equal(0, Run("checklist", "set", "s1/a", "--ref", "20261016-140001-abcd", "--team", "tm").Code);
    }

    /// <summary>The item <c>ah msg new</c> adds for a request sent without <c>--item</c>: the same events, through the same writer, with its ref.</summary>
    private void AutoItem(string stream, string id, string requestId)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        var env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, new Dictionary<string, string?> { ["AH_TEST"] = "1", ["AH_HOME"] = _sandbox.AhHome, ["HOME"] = _sandbox.Home, ["WRANGL_HIERARCHY_DIR"] = Hierarchy }, new PluginInfo(_sandbox.PluginRoot, "wrangl", "1.0.0", "1"));
        var graph = ServiceGraph.Build(PlatformModule.CreateIo(null), env, _runner, new FakeDaemonClient());
        var written = graph.Hierarchy.Progress.RecordChecklist(Hierarchy, "tm", stream, "orchestrator@tm", [new PlanInput(id, "add", "Make UI"), HierarchyModule.ChecklistAutoMark(id), new PlanInput(id, "next"), new PlanInput(id, "ref", Ref: requestId)]);
        Assert.False(written.Off);
        Assert.Null(written.Refused);
    }

    [Fact]
    public void ARefHeldOnlyByAnAutoItemMovesToTheItemSetWithItAndTheAutoItemIsDroppedAsSuperseded()
    {
        TwoStreamsWithItemA();
        AutoItem("s1", "auto-make-ui", "20261016-140001-abcd");

        var moved = Run("checklist", "set", "s2/a", "--ref", "20261016-140001-abcd", "--team", "tm");

        Assert.True(moved.Code == 0, moved.Stderr);
        var s1 = ReadLog(LogOf("s1"));
        Assert.Contains("\"drop\"", s1, StringComparison.Ordinal);
        Assert.Contains("superseded by s2/a", s1, StringComparison.Ordinal);
        Assert.Contains("20261016-140001-abcd", ReadLog(LogOf("s2")), StringComparison.Ordinal);

        // the ref now sits on a real item: another item may share it, and the real item keeps it
        var again = Run("checklist", "set", "s1/b", "--ref", "20261016-140001-abcd", "--team", "tm");
        Assert.True(again.Code == 0, again.Stderr);
        Assert.DoesNotContain("superseded by s1/b", ReadLog(LogOf("s2")), StringComparison.Ordinal);
    }

    [Fact]
    public void AnAddWithARefHeldOnlyByAnAutoItemMovesItToTheNewItem()
    {
        TwoStreamsWithItemA();
        AutoItem("s1", "auto-make-ui", "20261016-140001-abcd");

        var add = Run("checklist", "add", "Polish the UI", "--ref", "20261016-140001-abcd", "--stream", "s2", "--team", "tm");

        Assert.True(add.Code == 0, add.Stderr);
        Assert.Contains("superseded by s2/", ReadLog(LogOf("s1")), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("progress")]
    [InlineData("add")]
    public void NegativeAnOrdinaryItemWhoseIdStartsWithAutoNeverYieldsItsRef(string madeBy)
    {
        TwoStreamsWithItemA();
        if (madeBy == "progress")
        {
            Assert.Equal(0, Run("progress", "item", "add", "s1", "auto-make-ui", "--title", "Make UI", "--team", "tm").Code);
            Assert.Equal(0, Run("checklist", "set", "s1/auto-make-ui", "--ref", "20261016-140001-abcd", "--team", "tm").Code);
        }
        else
        {
            Assert.Equal(0, Run("checklist", "add", "Auto deploy fix", "--ref", "20261016-140001-abcd", "--stream", "s1", "--team", "tm").Code);
            Assert.Contains("auto-deploy-fix", ReadLog(LogOf("s1")), StringComparison.Ordinal);
        }

        var before = ReadLog(LogOf("s1"));

        var set = Run("checklist", "set", "s2/a", "--ref", "20261016-140001-abcd", "--team", "tm");

        // both items keep the ref: the ordinary item is not dropped as superseded
        Assert.True(set.Code == 0, set.Stderr);
        Assert.Equal(before, ReadLog(LogOf("s1")));
        Assert.Contains("20261016-140001-abcd", ReadLog(LogOf("s2")), StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeARefusedSetLeavesTheAutoItemAndItsRefAsTheyWere()
    {
        TwoStreamsWithItemA();
        AutoItem("s1", "auto-make-ui", "20261016-140001-abcd");
        var before = ReadLog(LogOf("s1")) + ReadLog(LogOf("s2"));

        // a note past the plan's limit refuses the whole set: the ref never moves and the auto item stays
        var set = Run("checklist", "set", "s2/a", "--note", new string('x', 5000), "--ref", "20261016-140001-abcd", "--team", "tm");

        Assert.NotEqual(0, set.Code);
        Assert.Contains("nothing written", set.Stderr, StringComparison.Ordinal);
        Assert.Equal(before, ReadLog(LogOf("s1")) + ReadLog(LogOf("s2")));
    }
}
