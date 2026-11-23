using System.Globalization;
using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Cli.Verbs.Msg;
using Ah.Core.Json;
using Ah.Hierarchy;
using Ah.Platform;
using Ah.TestKit;

namespace Ah.Cli.Tests;

/// <summary><c>ah msg decision answer</c>: the user's answer to a parked decision, logged with the caller as the one who relayed it.</summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class MsgDecisionAnswerTests : IDisposable
{
    private const string Common = "\"item\":\"i1\",\"source\":\"s\",\"options\":[\"x\",\"y\"],\"default\":null,\"rationale\":\"r\",\"revert\":\"\",\"review\":false,\"dangerous\":false,\"exchange\":null";

    private readonly Sandbox _sandbox = new();
    private readonly CliContext _context;
    private readonly string _hierarchy;
    private readonly ServiceGraph _graph;

    public MsgDecisionAnswerTests()
    {
        _hierarchy = Path.Combine(_sandbox.Root, "hier");
        var teamFile = Path.Combine(_hierarchy, "teams", "tm.json");
        _sandbox.PlantTeam(
            teamFile,
            "{\"team_id\":\"t-tm\",\"orchestrator\":{\"pid\":" + System.Environment.ProcessId.ToString(CultureInfo.InvariantCulture)
            + ",\"pid_start\":\"" + FakeRunner.SelfStart + "\",\"session_id\":\"o\"},\"members\":[{\"role\":\"implementor\",\"name\":\"tm-implementor\",\"kind\":\"claude\",\"transport_id\":\"p1\"}]}\n");
        var variables = new Dictionary<string, string?>
        {
            ["AH_TEST"] = "1",
            ["AH_HOME"] = _sandbox.AhHome,
            ["HOME"] = _sandbox.Home,
            ["WRANGL_HIERARCHY_DIR"] = _hierarchy,
        };
        var env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables, new PluginInfo(_sandbox.PluginRoot, "wrangl", "1.0.0", "1"));
        var graph = ServiceGraph.Build(PlatformModule.CreateIo(null), env, FakeRunner.KnowingSelf(), new FakeDaemonClient { Reply = _ => new DaemonReply(0, default, "down") });
        FakeRunner.RegisterSelf(graph.Mailbox, "o", teamFile, "orchestrator@tm");
        _graph = graph;
        _context = new CliContext(env, new RecordingExec(), new FakeRunner(), "ahd", _sandbox.Executable("install/ahd"), _sandbox.Home, 501, null, new Lazy<ServiceGraph>(graph));
    }

    public void Dispose() => _sandbox.Dispose();

    private (int Exit, string Out, string Err) Run(string? stdin, params string[] args) => RunAs("tm", stdin, args);

    private (int Exit, string Out, string Err) RunAs(string? team, string? stdin, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var context = stdin is null ? _context : _context with { ReadStdin = () => System.Text.Encoding.UTF8.GetBytes(stdin) };
        string[] scope = team is null ? ["--cwd", _sandbox.Root] : ["--cwd", _sandbox.Root, "--team", team];
        var exit = MsgVerb.Run(context, [.. args, .. scope], stdout, stderr);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private void OpenRun() =>
        Assert.Equal(0, Run(null, "new", "--type", "request", "--to", "orchestrator", "--from", "orchestrator", "--slug", HierarchyModule.PipelineAnchorSlug, "--title", "Fix test titles", "--id", "20260601-000001-aaaa").Exit);

    private static string Parked() =>
        "{\"kind\":\"parked\",\"question\":\"Which?\",\"choice\":null,\"decider\":null,\"why_user\":\"unsure\"," + Common + "}";

    [Fact]
    public void AnAnswerIsLoggedAndDropsTheWaitingCount()
    {
        OpenRun();
        Assert.Equal(0, Run(Parked(), "decision", "add").Exit);

        var (exit, stdout, stderr) = Run(null, "decision", "answer", "d1", "--choice", "other:later");

        Assert.True(exit == 0, stderr);
        Assert.Equal("d2", JsonText.Parse(stdout).AsObject()["id"].AsString());
        var summary = JsonText.Parse(Run(null, "decision", "list", "--summary").Out).AsObject();
        Assert.Equal(0, summary["waiting"].AsNumber());
        var logged = JsonText.Parse(Run(null, "decision", "list").Out).AsObject()["decisions"].AsArray()[1];
        Assert.Equal(("answered", "other: later", "user"), (logged.Member("kind")!.Value.AsString(), logged.Member("decision")!.Value.AsString(), logged.Member("answered_by")!.Value.AsString()));
        Assert.StartsWith("orchestrator@tm", logged.Member("recorded_by")!.Value.AsString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ADoubleAnswerAnUnknownIdAndAMissingChoiceAreRefusedWithNothingWritten()
    {
        OpenRun();
        _ = Run(Parked(), "decision", "add");
        _ = Run(null, "decision", "answer", "d1", "--choice", "a");

        var again = Run(null, "decision", "answer", "d1", "--choice", "b");
        var unknown = Run(null, "decision", "answer", "d7", "--choice", "a");
        var noChoice = Run(null, "decision", "answer", "d1");

        Assert.Equal(2, again.Exit);
        Assert.Contains("already closed", again.Err, StringComparison.Ordinal);
        Assert.Equal(2, unknown.Exit);
        Assert.Contains("unknown-id", unknown.Err, StringComparison.Ordinal);
        Assert.NotEqual(0, noChoice.Exit);
        Assert.Equal(2, JsonText.Parse(Run(null, "decision", "list").Out).AsObject()["decisions"].AsArray().Count);
    }

    [Fact]
    public void DecisionAddRefusesAnAnsweredLineAndWritesNothingWhileAParkedLineIsStillAccepted()
    {
        OpenRun();
        var forged = "{\"kind\":\"answered\",\"answers\":\"d1\",\"choice\":\"x\",\"answered_by\":\"user\",\"recorded_by\":\"user (forged)\"}";

        var parkedFirst = Run(Parked(), "decision", "add");
        var refused = Run(forged, "decision", "add");

        Assert.Equal(0, parkedFirst.Exit);
        Assert.Equal(2, refused.Exit);
        Assert.Contains("decision answer", refused.Err, StringComparison.Ordinal);
        var written = JsonText.Parse(Run(null, "decision", "list").Out).AsObject()["decisions"].AsArray().Count;
        Assert.Equal(1, written);
    }

    private const string RunId = "20260601-000001-aaaa";

    private static string MultiParked() =>
        "{\"kind\":\"parked\",\"question\":\"Which ones?\",\"choice\":null,\"decider\":null,\"why_user\":\"unsure\",\"multi\":true," + Common + "}";

    private static string Decided() =>
        "{\"kind\":\"decided\",\"question\":\"Settled?\",\"choice\":\"a\",\"decider\":{\"role\":\"architect\",\"name\":null,\"model\":null},\"why_user\":null," + Common + "}";

    private int Count() => JsonText.Parse(Run(null, "decision", "list").Out).AsObject()["decisions"].AsArray().Count;

    /// <summary>The raw lines of the run's log, as written (per-dir-files-to-db S9: the run's <c>decisions</c> rows).</summary>
    private string[] LogLines() => RunLines(RunId);

    private string[] RunLines(string run) => [.. Ah.StateDb.StateDbModule.CreateDecisions(_sandbox.AhHome).Bodies(_hierarchy, run) ?? []];

    private JsonValue Line(int index) => JsonText.Parse(LogLines()[index]);

    private static string Reason(string stdout) => JsonText.Parse(stdout).AsObject()["reason"].AsString();

    [Fact]
    public void ASingleChoiceAnswerLineHasTodaysFieldsInTodaysOrderAndNoChoicesOrMulti()
    {
        OpenRun();
        _ = Run(Parked(), "decision", "add");

        var (exit, stdout, stderr) = Run(null, "decision", "answer", "d1", "--choice", "b");

        Assert.True(exit == 0, stderr);
        Assert.Equal("y", JsonText.Parse(stdout).AsObject()["decision"].AsString());
        Assert.DoesNotContain("multi", LogLines()[0], StringComparison.Ordinal);
        var line = Line(1).AsObject();
        Assert.Equal(
            ["id", "time", "run", "kind", "answers", "item", "question", "qkey", "options", "choice", "decision", "answered_by", "recorded_by", "caller", "op_id"],
            line.Keys.ToArray());
        Assert.Equal(("b", "y"), (line["choice"].AsString(), line["decision"].AsString()));
    }

    [Fact]
    public void AMatchingRunAnswersAndAStaleRunIsRefusedWithNothingWritten()
    {
        OpenRun();
        _ = Run(Parked(), "decision", "add");

        var stale = Run(null, "decision", "answer", "d1", "--choice", "a", "--run", "20260601-000002-bbbb");
        Assert.Equal(2, stale.Exit);
        Assert.Equal("stale-run", Reason(stale.Out));
        Assert.Contains("open run is " + RunId, stale.Out, StringComparison.Ordinal);
        Assert.Equal(1, Count());

        var matching = Run(null, "decision", "answer", "d1", "--choice", "a", "--run", RunId);
        Assert.True(matching.Exit == 0, matching.Err);
        Assert.Equal(2, Count());
    }

    [Fact]
    public void AnAnswerWithNoOpenRunIsRefusedNoRun()
    {
        var (exit, stdout, _) = Run(null, "decision", "answer", "d1", "--choice", "a");

        Assert.Equal(2, exit);
        Assert.Equal("no-run", Reason(stdout));
    }

    private const string Operational =
        "{\"class\":\"operational\",\"o\":\"O1\",\"question\":\"Who?\",\"decision\":\"me\",\"rationale\":\"r\",\"revert\":\"v\",\"decider\":{\"role\":\"orchestrator\",\"self\":true}}";

    [Fact]
    public void WithNoOpenRunAnOperationalLineIsLoggedAgainstTheTeamAndShowsInTheSummary()
    {
        Assert.Equal(0, Run(null, "new", "--type", "request", "--to", "orchestrator", "--from", "orchestrator", "--slug", "other", "--title", "Not a run", "--id", "20260601-000003-cccc").Exit);
        var (exit, stdout, stderr) = Run(Operational, "decision", "add");

        Assert.True(exit == 0, stderr + stdout);
        Assert.Equal("o1", JsonText.Parse(stdout).AsObject()["id"].AsString());
        Assert.NotEmpty(RunLines("team-tm"));
        Assert.Equal(1, JsonText.Parse(Run(null, "decision", "list", "--summary").Out).AsObject()["operational"].AsNumber());
    }

    [Fact]
    public void WithNoOpenRunAParkedLineIsStillRefusedNoRunAndNothingIsWritten()
    {
        var (exit, stdout, _) = Run(Parked(), "decision", "add");

        Assert.Equal(2, exit);
        Assert.Equal("no-run", Reason(stdout));
        Assert.False(Directory.Exists(Path.Combine(_hierarchy, "pipeline", "team-tm")));
    }

    [Fact]
    public void SeveralChoicesOnAMultiQuestionAreWrittenInOptionOrderOtherLast()
    {
        OpenRun();
        Assert.Equal(0, Run(MultiParked(), "decision", "add").Exit);
        Assert.True(Line(0).Member("multi")!.Value.AsBool());

        var (exit, _, stderr) = Run(null, "decision", "answer", "d1", "--choice", "other:later", "--choice", "b", "--choice", "a");

        Assert.True(exit == 0, stderr);
        var line = Line(1);
        Assert.Equal("a,b,other: later", line.Member("choice")!.Value.AsString());
        Assert.Equal("x; y; other: later", line.Member("decision")!.Value.AsString());
        Assert.Equal(["a", "b", "other: later"], line.Member("choices")!.Value.AsArray().Select(static c => c.AsString()));
    }

    [Fact]
    public void OneChoiceOnAMultiQuestionWritesAOneEntryChoices()
    {
        OpenRun();
        _ = Run(MultiParked(), "decision", "add");

        var (exit, _, stderr) = Run(null, "decision", "answer", "d1", "--choice", "b");

        Assert.True(exit == 0, stderr);
        Assert.Equal(["b"], Line(1).Member("choices")!.Value.AsArray().Select(static c => c.AsString()));
        Assert.Equal(("b", "y"), (Line(1).Member("choice")!.Value.AsString(), Line(1).Member("decision")!.Value.AsString()));
    }

    [Fact]
    public void TwoChoicesOnASingleQuestionAreRefusedNotMultiWithNothingWritten()
    {
        OpenRun();
        _ = Run(Parked(), "decision", "add");

        var (exit, stdout, _) = Run(null, "decision", "answer", "d1", "--choice", "a", "--choice", "b");

        Assert.Equal(2, exit);
        Assert.Equal("not-multi", Reason(stdout));
        Assert.Equal(1, Count());
    }

    [Fact]
    public void ADuplicateChoiceTwoOthersAndALetterBeyondTheOptionsAreRefusedOnAMultiQuestion()
    {
        OpenRun();
        _ = Run(MultiParked(), "decision", "add");

        var duplicate = Run(null, "decision", "answer", "d1", "--choice", "a", "--choice", "a");
        var twoOthers = Run(null, "decision", "answer", "d1", "--choice", "other: p", "--choice", "other: q");
        var beyond = Run(null, "decision", "answer", "d1", "--choice", "a", "--choice", "c");
        var emptyOther = Run(null, "decision", "answer", "d1", "--choice", "a", "--choice", "other: ");

        Assert.Equal((2, "duplicate-choice"), (duplicate.Exit, Reason(duplicate.Out)));
        Assert.Equal(2, twoOthers.Exit);
        Assert.Contains("at most one other", twoOthers.Out, StringComparison.Ordinal);
        Assert.Equal(2, beyond.Exit);
        Assert.Equal(2, emptyOther.Exit);
        Assert.Equal(1, Count());
    }

    [Fact]
    public void MultiIsRefusedOnADecidedLineAndAMultiFalseParkedLineIsAcceptedWithoutIt()
    {
        OpenRun();
        var decidedMulti = Decided().Replace("\"why_user\":null,", "\"why_user\":null,\"multi\":true,", StringComparison.Ordinal);

        var refused = Run(decidedMulti, "decision", "add");
        var single = Run(Parked().Replace("\"why_user\":\"unsure\",", "\"why_user\":\"unsure\",\"multi\":false,", StringComparison.Ordinal), "decision", "add");

        Assert.Equal(2, refused.Exit);
        Assert.Contains("multi only on parked", refused.Out, StringComparison.Ordinal);
        Assert.Equal(0, single.Exit);
        Assert.Single(LogLines());
        Assert.Null(Line(0).Member("multi"));
    }

    [Fact]
    public void ADecidedIdIsAlreadyClosedAndAnAnsweredLinesOwnIdIsUnknown()
    {
        OpenRun();
        _ = Run(Parked(), "decision", "add");
        Assert.Equal(0, Run(Decided(), "decision", "add").Exit);
        _ = Run(null, "decision", "answer", "d1", "--choice", "a");

        var decided = Run(null, "decision", "answer", "d2", "--choice", "a");
        var answeredId = Run(null, "decision", "answer", "d3", "--choice", "a");

        Assert.Equal("already-closed", Reason(decided.Out));
        Assert.Equal("unknown-id", Reason(answeredId.Out));
        Assert.Equal(3, Count());
    }

    // ---------------------------------------------------------------- the shared answer core, as the UI route drives it

    private static JsonObject UiStamp() => new() { ["answered_by"] = "user", ["recorded_by"] = "ui", ["caller"] = "web" };

    private JsonObject? Answer(Func<string, string, JsonObject?> prove, Func<string, string, string, string?>? notify, out string? eventError, string? run = null, params string[] choices) =>
        _graph.Hierarchy.Decisions.AnswerParked(
            _hierarchy, "tm", run, "d1", choices, false, prove, (path, runId, input) => _graph.Hierarchy.StateCalls.AppendDecision(path, runId, input), notify, _graph.Hierarchy.Progress, out eventError);

    // ---------------------------------------------------------------- a decision naming items, and an item answered itself (spec ah-ui-answer §4)

    private void Waits(string stream, string item, params string[] options)
    {
        var rows = new PlanInput[] { new(item, "add", "Item " + item), new(item, "start"), new(item, "wait-user", Note: "Which?") { Options = options.Length > 0 ? options : null } };
        Assert.Null(_graph.Hierarchy.Progress.RecordChecklist(_hierarchy, "tm", stream, "orchestrator@tm", rows).Refused);
    }

    private PlanItem Entry(string stream, string item) =>
        HierarchyModule.ChecklistEntries(_graph.Hierarchy.Progress, _hierarchy, "tm").Single(e => e.Stream == stream && e.Item.Item == item).Item;

    [Fact]
    public void ADecisionAnsweredMovesEveryWaitingItemItNamesWithTheAnswer()
    {
        OpenRun();
        _ = Run(Parked(), "decision", "add");
        Waits("s1", "i1");
        Waits("s2", "i1");

        _ = Answer((_, _) => UiStamp(), null, out var eventError, RunId, "other:soon");

        Assert.Null(eventError);
        foreach (var stream in new[] { "s1", "s2" })
        {
            var item = Entry(stream, "i1");
            Assert.Equal(("in_progress", "other: soon", "Which?"), (item.State, item.Answer, item.Note));
        }
    }

    [Fact]
    public void ADecisionAnsweredThroughTheVerbMovesTheItemToo()
    {
        OpenRun();
        _ = Run(Parked(), "decision", "add");
        Waits("s1", "i1");

        var answered = Run(null, "decision", "answer", "d1", "--choice", "a", "--team", "tm");

        Assert.Equal(0, answered.Exit);
        Assert.Equal("in_progress", Entry("s1", "i1").State);
    }

    [Fact]
    public void NegativeADecisionNamingAnItemThatDoesNotWaitLeavesItAlone()
    {
        OpenRun();
        _ = Run(Parked(), "decision", "add");
        Assert.Null(_graph.Hierarchy.Progress.RecordChecklist(_hierarchy, "tm", "s1", "orchestrator@tm", [new("i1", "add", "Item"), new("i1", "start")]).Refused);

        _ = Answer((_, _) => UiStamp(), null, out _, RunId, "a");

        Assert.Equal(("in_progress", (string?)null), (Entry("s1", "i1").State, Entry("s1", "i1").Answer));
    }

    [Fact]
    public void NegativeADecisionNamingAnotherItemMovesNoItem()
    {
        OpenRun();
        _ = Run(Parked().Replace("\"item\":\"i1\"", "\"item\":\"other\"", StringComparison.Ordinal), "decision", "add");
        Waits("s1", "i1");

        var res = Answer((_, _) => UiStamp(), null, out var eventError, RunId, "a");

        Assert.False(res!.ContainsKey("refused"));
        Assert.Null(eventError);
        Assert.Equal("waiting_user", Entry("s1", "i1").State);
    }

    private PlanItem DefaultTeamWaits()
    {
        var rows = new PlanInput[] { new("ask", "add", "Item ask"), new("ask", "start"), new("ask", "wait-user", Note: "Which?") };
        Assert.Null(_graph.Hierarchy.Progress.RecordChecklist(_hierarchy, "_default", "s1", "orchestrator@default", rows).Refused);
        return HierarchyModule.ChecklistEntries(_graph.Hierarchy.Progress, _hierarchy, "_default").Single().Item;
    }

    [Fact]
    public void AnItemOfTheTeamNamedDefaultIsFoundInItsFolder()
    {
        var since = DefaultTeamWaits().StateSince!;

        var res = HierarchyModule.ChecklistAnswer(_graph.Hierarchy.Progress, _hierarchy, "default", "s1:ask", since, "other: soon", DateTimeOffset.UtcNow, null, TeamTm);

        Assert.Equal("soon", res["decision"].AsString());
        Assert.Equal("in_progress", HierarchyModule.ChecklistEntries(_graph.Hierarchy.Progress, _hierarchy, "_default").Single().Item.State);
    }

    [Fact]
    public void ADecisionOfTheTeamNamedDefaultMovesTheItemInItsFolder()
    {
        var opened = RunAs("default", null, "new", "--type", "request", "--to", "orchestrator", "--from", "orchestrator", "--slug", HierarchyModule.PipelineAnchorSlug, "--title", "Fix test titles", "--id", "20260601-000001-aaaa");
        Assert.True(opened.Exit == 0, opened.Err);
        // appended directly: the verb's proof of the run's Orchestrator is set up for team tm only
        var parked = JsonText.Parse(Parked().Replace("\"item\":\"i1\"", "\"item\":\"ask\"", StringComparison.Ordinal)).AsObject();
        var added = _graph.Hierarchy.StateCalls.AppendDecision(_graph.Hierarchy.Decisions.DecisionLogPath(_hierarchy, RunId), RunId, parked);
        Assert.False(added.ContainsKey("refused"));
        _ = DefaultTeamWaits();

        var res = _graph.Hierarchy.Decisions.AnswerParked(
            _hierarchy, "default", RunId, "d1", ["a"], false, (_, _) => UiStamp(), (path, runId, input) => _graph.Hierarchy.StateCalls.AppendDecision(path, runId, input), null, _graph.Hierarchy.Progress, out var eventError);

        Assert.False(res!.ContainsKey("refused"));
        Assert.Null(eventError);
        Assert.Equal("in_progress", HierarchyModule.ChecklistEntries(_graph.Hierarchy.Progress, _hierarchy, "_default").Single().Item.State);
    }

    [Fact]
    public void NegativeANamedTeamDoesNotReadTheDefaultFolder()
    {
        var since = DefaultTeamWaits().StateSince!;

        var res = ItemAnswer(since, "other: soon", "s1:ask");

        Assert.Equal("unknown-item", res["refused"].Member("reason")!.Value.AsString());
    }

    [Fact]
    public void NegativeARefusedDecisionMovesNoItem()
    {
        OpenRun();
        _ = Run(Parked(), "decision", "add");
        Waits("s1", "i1");

        var res = Answer((_, _) => UiStamp(), null, out _, "20990101-000000-zzzz", "a");

        Assert.Equal("stale-run", res!["refused"].Member("reason")!.Value.AsString());
        Assert.Equal("waiting_user", Entry("s1", "i1").State);
    }

    // team tm's record lists one member, m
    private static JsonValue TeamTm(string? team) =>
        team == "tm" ? JsonText.Parse("{\"members\":[{\"name\":\"m\"}]}") : JsonValue.Null;

    private JsonObject ItemAnswer(string since, string choice, string key = "s1:ask", Func<string, string, string, string?>? notify = null) =>
        HierarchyModule.ChecklistAnswer(_graph.Hierarchy.Progress, _hierarchy, "tm", key, since, choice, DateTimeOffset.UtcNow, notify, TeamTm);

    [Fact]
    public void AnItemAnsweredByLetterMovesItAndTellsTheOrchestrator()
    {
        Waits("s1", "ask", "Ship", "Hold");
        var sent = new List<(string To, string Event, string Text)>();

        var res = ItemAnswer(Entry("s1", "ask").StateSince!, "b", notify: (to, ev, text) => { sent.Add((to, ev, text)); return null; });

        Assert.Equal("Hold", res["decision"].AsString());
        Assert.Equal(("in_progress", "Hold", "Which?"), (Entry("s1", "ask").State, Entry("s1", "ask").Answer, Entry("s1", "ask").Note));
        Assert.Equal(("orchestrator@tm", "question-answered", "s1:ask answered: Hold"), Assert.Single(sent));
    }

    private void AskFor(string item, string? askedFor) =>
        Assert.Null(_graph.Hierarchy.Progress.RecordChecklist(_hierarchy, "tm", "s1", "orchestrator@tm", [new PlanInput(item, "wait-user", Note: "Which?") { Options = ["Ship", "Hold"], AskedFor = askedFor }]).Refused);

    [Fact]
    public void AnItemAskedForAMemberTellsTheOrchestratorAndTheMember()
    {
        Waits("s1", "ask", "Ship", "Hold");
        AskFor("ask", "m@tm");
        var sent = new List<(string To, string Event, string Text)>();

        _ = ItemAnswer(Entry("s1", "ask").StateSince!, "a", notify: (to, ev, text) => { sent.Add((to, ev, text)); return null; });

        Assert.Equal([("orchestrator@tm", "question-answered", "s1:ask answered: Ship"), ("m@tm", "question-answered", "s1:ask answered: Ship")], sent);
    }

    // ah2-510 L3: tm as lane s1's child team of manager proj
    private static JsonValue LaneTm(string? team) =>
        team == "tm" ? JsonText.Parse("{\"members\":[{\"name\":\"m\"}],\"parent\":{\"team\":\"proj\",\"stream\":\"s1\"}}") : JsonValue.Null;

    [Fact]
    public void ALaneItemAnsweredTellsTheLeadOnceAndCopiesTheTopOrchestratorOnce()
    {
        Waits("s1", "ask", "Ship", "Hold");
        var sent = new List<string>();

        var res = HierarchyModule.ChecklistAnswer(_graph.Hierarchy.Progress, _hierarchy, "tm", "s1:ask", Entry("s1", "ask").StateSince!, "a", DateTimeOffset.UtcNow, (to, _, _) => { sent.Add(to); return null; }, LaneTm);

        Assert.Equal("Ship", res["decision"].AsString());
        Assert.Equal(["orchestrator@tm", "orchestrator@proj"], sent);
    }

    [Fact]
    public void NegativeAnItemOfATeamThatNamesNoParentGetsNoTopCopy()
    {
        Waits("s1", "ask", "Ship", "Hold");
        var sent = new List<string>();

        _ = ItemAnswer(Entry("s1", "ask").StateSince!, "a", notify: (to, _, _) => { sent.Add(to); return null; });

        Assert.Equal("orchestrator@tm", Assert.Single(sent));
    }

    [Fact]
    public void NegativeAReAskWithoutForTellsTheOrchestratorOnly()
    {
        Waits("s1", "ask", "Ship", "Hold");
        AskFor("ask", "m@tm");
        AskFor("ask", null);
        var sent = new List<string>();

        _ = ItemAnswer(Entry("s1", "ask").StateSince!, "a", notify: (to, _, _) => { sent.Add(to); return null; });

        Assert.Equal("orchestrator@tm", Assert.Single(sent));
    }

    // ---------------------------------------------------------------- the terminal list and item answer (spec b19 slice 1)

    [Fact]
    public void ListWaitingGivesTheDecisionTheItemNoDecisionNamesAndTheParkedQuestionEachWithItsAnswerCommand()
    {
        OpenRun();
        _ = Run(Parked(), "decision", "add");
        Waits("s1", "i1");
        Waits("s1", "ask", "Ship", "Hold");
        var question = new JsonObject { ["question"] = "Go?", ["header"] = "Scope", ["multiSelect"] = false, ["options"] = new JsonArray { new JsonObject { ["label"] = "Yes" }, new JsonObject { ["label"] = "No" } } };
        _graph.Hierarchy.StateCalls.AppendParkedQuestion(_hierarchy, ParkedQuestions.ParkedLine("q1", "2026-10-26T01:09:00.000Z", "tm", "o", null, 0, question, null, "it waits"));

        var (exit, stdout, stderr) = Run(null, "decision", "list", "--waiting", "--json");

        Assert.True(exit == 0, stderr);
        var body = JsonText.Parse(stdout).AsObject();
        var rows = body["waiting"].AsArray();
        // i1 is named by d1, so it is listed once, as the decision, as the web list has it
        Assert.Equal(["decision d1", "item s1:ask", "question q1"], rows.Select(r => r.Member("kind")!.Value.AsString() + " " + r.Member("id")!.Value.AsString()));
        Assert.Equal(3, body["total"].AsNumber());
        Assert.Equal(
            ["ah msg decision answer d1 --choice a --team tm", "ah msg decision answer s1:ask --choice a --team tm", "ah msg decision answer q1 \"Yes\" --team tm"],
            rows.Select(r => r.Member("answer_command")!.Value.AsString()));
    }

    [Fact]
    public void NegativeListWaitingWithNothingWaitingIsZeroAndExitZero()
    {
        var (exit, stdout, _) = Run(null, "decision", "list", "--waiting", "--json");

        Assert.Equal((0, 0d), (exit, JsonText.Parse(stdout).AsObject()["total"].AsNumber()));
    }

    /// <summary>Runs as a caller of <paramref name="kind"/>, as the ancestry proof would classify it.</summary>
    private (int Exit, string Out, string Err) RunAs(CutoverCallerKind kind, params string[] args)
    {
        var context = _context with { DecisionCaller = () => new DecisionCaller(new CutoverCaller(kind, "who"), [], "o", System.Environment.ProcessId, "start", "ttys000") };
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = MsgVerb.Run(context, [.. args, "--cwd", _sandbox.Root, "--team", "tm"], stdout, stderr);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public void AnItemAnsweredFromTheTerminalWritesTheWebRowsAndMailsOneEvent()
    {
        Waits("s1", "ask", "Ship", "Hold");

        var (exit, stdout, stderr) = RunAs(CutoverCallerKind.User, "decision", "answer", "s1:ask", "Hold");

        Assert.True(exit == 0, stderr);
        Assert.Equal(("Hold", "cli"), (JsonText.Parse(stdout).Member("decision")!.Value.AsString(), JsonText.Parse(stdout).Member("recorded_by")!.Value.AsString()));
        Assert.Equal(("in_progress", "Hold", "answer user: ask"), (Entry("s1", "ask").State, Entry("s1", "ask").Answer, Entry("s1", "ask").Todo));
        Assert.Equal("orchestrator@tm", Assert.Single(Mailed("s1:ask answered: Hold")).To);
    }

    [Fact]
    public void NegativeAnItemNoLongerWaitingOrAnsweredByAMemberWritesNothing()
    {
        Waits("s1", "ask", "Ship", "Hold");

        var byMember = RunAs(CutoverCallerKind.Member, "decision", "answer", "s1:ask", "--choice", "a");

        Assert.Equal(3, byMember.Exit);
        Assert.Equal("waiting_user", Entry("s1", "ask").State);
        Assert.Equal(0, RunAs(CutoverCallerKind.User, "decision", "answer", "s1:ask", "--choice", "a").Exit);
        var again = RunAs(CutoverCallerKind.User, "decision", "answer", "s1:ask", "--choice", "b");
        Assert.Equal(2, again.Exit);
        Assert.Equal("not-waiting", Reason(again.Out));
        Assert.Equal("Ship", Entry("s1", "ask").Answer);
    }

    [Theory]
    [InlineData("review", "in_review")]
    [InlineData("approve", "approved")]
    public void AnAnswerPutsTheItemBackInTheStateItHeldBeforeTheQuestion(string before, string after)
    {
        var rows = new PlanInput[] { new("ask", "add", "Item ask"), new("ask", "start"), new("ask", before), new("ask", "wait-user", Note: "Which?") };
        Assert.Null(_graph.Hierarchy.Progress.RecordChecklist(_hierarchy, "tm", "s1", "orchestrator@tm", rows).Refused);

        _ = ItemAnswer(Entry("s1", "ask").StateSince!, "other: soon");

        Assert.Equal(after, Entry("s1", "ask").State);
        Assert.Null(Entry("s1", "ask").OverrideAt);
        Assert.Equal("answer user: ask", Entry("s1", "ask").Todo);
    }

    [Fact]
    public void AnItemAnsweredInTheOtherFormCarriesTheText()
    {
        Waits("s1", "ask");

        var res = ItemAnswer(Entry("s1", "ask").StateSince!, "other:  soon ");

        Assert.Equal("soon", res["decision"].AsString());
        Assert.Equal("soon", Entry("s1", "ask").Answer);
    }

    [Fact]
    public void AnItemAnsweredTwiceIsOneRowAndTheSecondIsNotWaiting()
    {
        Waits("s1", "ask", "Ship");
        var since = Entry("s1", "ask").StateSince!;

        _ = ItemAnswer(since, "a");
        var second = ItemAnswer(since, "a");

        Assert.Equal("not-waiting", second["refused"].Member("reason")!.Value.AsString());
        Assert.Single(HierarchyModule.ChecklistEntries(_graph.Hierarchy.Progress, _hierarchy, "tm"));
    }

    [Fact]
    public void NegativeAnAnswerToAnOldQuestionOrAnUnknownItemIsRefusedAndMovesNothing()
    {
        Waits("s1", "ask", "Ship");

        var changed = ItemAnswer("1999-01-01T00:00:00Z", "a");
        var unknown = ItemAnswer("1999-01-01T00:00:00Z", "a", "s1:nope");

        Assert.Equal("question-changed", changed["refused"].Member("reason")!.Value.AsString());
        Assert.Equal("unknown-item", unknown["refused"].Member("reason")!.Value.AsString());
        Assert.Equal("waiting_user", Entry("s1", "ask").State);
    }

    [Theory]
    [InlineData("e")]
    [InlineData("b")]
    [InlineData("A")]
    [InlineData("other:   ")]
    [InlineData("Ship")]
    public void NegativeAChoiceNamingNoOptionIsABadRequestAndMovesNothing(string choice)
    {
        Waits("s1", "ask", "Ship");

        var res = ItemAnswer(Entry("s1", "ask").StateSince!, choice);

        Assert.True(res.ContainsKey("bad_request"));
        Assert.Equal("waiting_user", Entry("s1", "ask").State);
    }

    [Fact]
    public void NegativeALetterWithNoOptionsAndOtherOverTheCapAreBadRequests()
    {
        Waits("s1", "ask");
        var since = Entry("s1", "ask").StateSince!;

        Assert.True(ItemAnswer(since, "a").ContainsKey("bad_request"));
        Assert.True(ItemAnswer(since, "other: " + new string('x', 2001)).ContainsKey("bad_request"));
        Assert.Equal("x", ItemAnswer(since, "other: x")["decision"].AsString());
    }

    [Fact]
    public void AnEventThatThrowsStillAnswersAndIsReported()
    {
        Waits("s1", "ask", "Ship");

        var res = ItemAnswer(Entry("s1", "ask").StateSince!, "a", notify: (_, _, _) => throw new TimeoutException("disk"));

        Assert.Equal("disk", res["event_error"].AsString());
        Assert.Equal("in_progress", Entry("s1", "ask").State);
    }

    [Fact]
    public void AUserAnswerThroughTheCoreWritesTheUiStampAndTellsTheOrchestrator()
    {
        OpenRun();
        _ = Run(Parked(), "decision", "add");
        var sent = new List<(string To, string Event, string Text)>();

        var res = Answer((_, _) => UiStamp(), (to, ev, text) => { sent.Add((to, ev, text)); return null; }, out var eventError, RunId, "other:soon");

        Assert.Equal("d2", res!["id"].AsString());
        Assert.Null(eventError);
        Assert.Equal(("orchestrator@tm", "question-answered", "d1 answered: other: soon"), Assert.Single(sent));
        var line = Line(1);
        Assert.Equal(("user", "ui", "web", "other: soon"), (line.Member("answered_by")!.Value.AsString(), line.Member("recorded_by")!.Value.AsString(), line.Member("caller")!.Value.AsString(), line.Member("choice")!.Value.AsString()));
    }

    [Fact]
    public void AnOrchestratorRecordedAnswerTellsNoOne()
    {
        OpenRun();
        _ = Run(Parked(), "decision", "add");
        var sent = 0;
        var stamp = new JsonObject { ["answered_by"] = "user", ["recorded_by"] = "orchestrator@tm", ["caller"] = "top-orchestrator" };

        var res = Answer((_, _) => stamp, (_, _, _) => { sent++; return null; }, out _, null, "a");

        Assert.False(res!.ContainsKey("refused"));
        Assert.Equal(0, sent);
    }

    [Fact]
    public void AFailedOrThrowingEventLeavesTheLineAndReportsSuccess()
    {
        OpenRun();
        _ = Run(Parked(), "decision", "add");
        _ = Run(MultiParked().Replace("Which ones?", "Second?", StringComparison.Ordinal), "decision", "add");

        var failed = Answer((_, _) => UiStamp(), (_, _, _) => "unknown_address: nobody", out var failedError, null, "a");
        var thrown = _graph.Hierarchy.Decisions.AnswerParked(
            _hierarchy, "tm", null, "d2", ["a"], false, (_, _) => UiStamp(), (path, runId, input) => _graph.Hierarchy.StateCalls.AppendDecision(path, runId, input),
            (_, _, _) => throw new TimeoutException("disk"), null, out var thrownError);

        Assert.Equal(("d3", "unknown_address: nobody"), (failed!["id"].AsString(), failedError));
        Assert.Equal(("d4", "disk"), (thrown!["id"].AsString(), thrownError));
        Assert.Equal(4, LogLines().Length);
    }

    // ---------------------------------------------------------------- the daemon's composed answer route support

    /// <summary>The mailbox entries sent so far whose text names <paramref name="text"/>, each with the address of the box it is in, read from the store.</summary>
    private List<(string To, JsonValue Entry)> Mailed(string text) =>
        [.. _graph.Mailbox.ListBoxes()
            .SelectMany(b => _graph.Mailbox.ReadEntries(b.Dir).Entries.Select(e => (b.Address, e)))
            .Where(m => JsonText.Stringify(m.e, 0).Contains(text, StringComparison.Ordinal))];

    [Fact]
    public void TheComposedRouteAnswersAsTheUiThroughTheInProcessOpAndTellsTheOrchestratorOnce()
    {
        OpenRun();
        _ = Run(Parked(), "decision", "add");
        var support = Composition.DaemonComposition.UiAnswer(new Lazy<ServiceGraph>(_graph), () => [_hierarchy]);
        JsonObject Input() => new() { ["dir"] = _hierarchy, ["team"] = "tm", ["run"] = RunId, ["id"] = "d1", ["choice"] = "b" };

        var first = support.Answer(Input());
        var second = support.Answer(Input());

        Assert.Equal(("d2", "d1", "y"), (first["id"].AsString(), first["answers"].AsString(), first["decision"].AsString()));
        Assert.False(first.ContainsKey("event_error"), first.ToString());
        Assert.Equal("already-closed", second["refused"].Member("reason")!.Value.AsString());
        var line = Line(1);
        Assert.Equal(("user", "ui", "web"), (line.Member("answered_by")!.Value.AsString(), line.Member("recorded_by")!.Value.AsString(), line.Member("caller")!.Value.AsString()));
        Assert.Equal(2, LogLines().Length);
        var sent = Assert.Single(Mailed("d1 answered: y"));
        Assert.Equal("orchestrator@tm", sent.To);
    }

    [Fact]
    public void TheComposedAnswerNamesItsStoredEventWhichReadsSeenOnlyOnceTheOrchestratorAcksIt()
    {
        OpenRun();
        _ = Run(Parked(), "decision", "add");
        var support = Composition.DaemonComposition.UiAnswer(new Lazy<ServiceGraph>(_graph), () => [_hierarchy]);
        string Send(string eventName, string text) =>
            _graph.Mailbox.Send(Ah.Mesh.MeshModule.EventRequest("orchestrator@tm", "user", _hierarchy, _hierarchy, eventName, text)).Value!["entry"].Member("id")!.Value.AsString();

        var eventId = support.Answer(new JsonObject { ["dir"] = _hierarchy, ["team"] = "tm", ["run"] = RunId, ["id"] = "d1", ["choice"] = "b" })["event_id"].AsString();
        var box = _graph.Mailbox.ListBoxes().Single(static b => b.Address == "orchestrator@tm").Dir;
        var before = support.Status!("tm", eventId)!;
        _ = _graph.Mailbox.AckIds(box, [eventId], "cli");
        var after = support.Status("tm", eventId)!;
        var superseded = Send("question-answered", "d9 answered: x");
        _ = _graph.Mailbox.AckIds(box, [superseded], "superseded");
        var resolved = Send("question-answered", "d8 answered: x");
        _ = _graph.Mailbox.AckIds(box, [resolved], "adapter", null, "resolved");

        Assert.Equal(Assert.Single(Mailed("d1 answered: y")).Entry.Member("id")!.Value.AsString(), eventId);
        Assert.False(before["seen"].AsBool());
        Assert.False(before.ContainsKey("seen_at"));
        Assert.Equal((true, "acked"), (after["seen"].AsBool(), after["state"].AsString()));
        Assert.True(after["seen_at"].AsNumber() > 0);
        Assert.False(support.Status("tm", superseded)!["seen"].AsBool());
        Assert.False(support.Status("tm", resolved)!["seen"].AsBool());
        Assert.Null(support.Status("other", eventId));
        Assert.Null(support.Status("tm", Send("other-event", "not an answer")));
    }

    [Fact]
    public void WithNoTeamNamedATeamsRunIsNotFoundAndNothingIsWrittenOrSent()
    {
        OpenRun();
        _ = Run(Parked(), "decision", "add");
        var sent = 0;

        var res = _graph.Hierarchy.Decisions.AnswerParked(
            _hierarchy, null, null, "d1", ["a"], false, (_, _) => UiStamp(), (path, runId, input) => _graph.Hierarchy.StateCalls.AppendDecision(path, runId, input),
            (_, _, _) => { sent++; return null; }, null, out var eventError);

        Assert.Equal("no-run", res!["refused"].Member("reason")!.Value.AsString());
        Assert.Null(eventError);
        Assert.Equal(0, sent);
        Assert.Single(LogLines());
    }

    [Fact]
    public void TheComposedRouteResolvesOnlyTheDirsItIsGivenAndTakesSeveralChoices()
    {
        OpenRun();
        _ = Run(MultiParked(), "decision", "add");
        var support = Composition.DaemonComposition.UiAnswer(new Lazy<ServiceGraph>(_graph), () => [_hierarchy]);
        var choices = new JsonArray();
        choices.Add("b");
        choices.Add("a");

        var res = support.Answer(new JsonObject { ["dir"] = _hierarchy, ["team"] = "tm", ["run"] = RunId, ["id"] = "d1", ["choices"] = choices });

        // a slice key's dirId: the first 8 hex digits, lower case, of the dir's SHA-256
        static string IdOf(string dir) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(dir)))[..8].ToLowerInvariant();
        Assert.Equal(_hierarchy, support.ResolveDir(IdOf(_hierarchy)));
        Assert.Null(support.ResolveDir(IdOf("/not/a/known/dir")));
        Assert.Equal("x; y", res["decision"].AsString());
        Assert.Equal("a,b", Line(1).Member("choice")!.Value.AsString());
    }

    [Fact]
    public void ARefusedProofWritesNothingAndARefusedAppendSendsNoEvent()
    {
        OpenRun();
        _ = Run(Parked(), "decision", "add");
        var sent = 0;

        var unproven = Answer((_, _) => null, (_, _, _) => { sent++; return null; }, out _, null, "a");
        var badLetter = Answer((_, _) => UiStamp(), (_, _, _) => { sent++; return null; }, out _, null, "e");

        Assert.Null(unproven);
        Assert.True(badLetter!.ContainsKey("refused"));
        Assert.Equal(0, sent);
        Assert.Single(LogLines());
    }

    // ---- ah msg decision ui-answer (spec-herdr-desktop.md P3.2) ----

    private string HierId() => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(_hierarchy)))[..8].ToLowerInvariant();

    private (int Exit, string Out, string Err) UiAnswer(bool proven, string body)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = MsgVerb.Run(_context with { UiAnswerCaller = () => proven }, ["decision", "ui-answer", "--json", body, "--cwd", _sandbox.Root], stdout, stderr);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public void UiAnswerWritesTheRoutesAnsweredLineStampedHerdrAndPrintsTheRouteReply()
    {
        OpenRun();
        _ = Run(Parked(), "decision", "add");

        var (exit, stdout, stderr) = UiAnswer(true, $"{{\"dir\":\"{HierId()}\",\"team\":\"tm\",\"run\":\"{RunId}\",\"id\":\"d1\",\"choice\":\"b\"}}");

        Assert.True(exit == 0, stderr + stdout);
        var reply = JsonText.Parse(stdout).AsObject();
        Assert.Equal((true, "d2", "y"), (reply["ok"].AsBool(), reply["id"].AsString(), reply["decision"].AsString()));
        var line = Line(1);
        Assert.Equal(("answered", "user", "ui", "herdr"), (line.Member("kind")!.Value.AsString(), line.Member("answered_by")!.Value.AsString(), line.Member("recorded_by")!.Value.AsString(), line.Member("caller")!.Value.AsString()));
    }

    [Fact]
    public void UiAnswerNamingAStaleRunGivesTheRoutesRefusalWithExit2()
    {
        OpenRun();
        _ = Run(Parked(), "decision", "add");

        var (exit, stdout, _) = UiAnswer(true, $"{{\"dir\":\"{HierId()}\",\"team\":\"tm\",\"run\":\"20260101-000000-zzzz\",\"id\":\"d1\",\"choice\":\"b\"}}");

        Assert.Equal(2, exit);
        var reply = JsonText.Parse(stdout).AsObject();
        Assert.Equal((true, "stale-run"), (reply["refused"].AsBool(), reply["reason"].AsString()));
        Assert.Single(LogLines());
    }

    [Fact]
    public void UiAnswerFromAnUnprovenCallerExits3AndWritesNothing()
    {
        OpenRun();
        _ = Run(Parked(), "decision", "add");

        var (exit, stdout, _) = UiAnswer(false, $"{{\"dir\":\"{HierId()}\",\"team\":\"tm\",\"run\":\"{RunId}\",\"id\":\"d1\",\"choice\":\"b\"}}");

        Assert.Equal(3, exit);
        Assert.Equal("unproven", JsonText.Parse(stdout).AsObject()["reason"].AsString());
        Assert.Single(LogLines());
    }

    [Fact]
    public void UiAnswerPrintsTheBodyTheRouteTableGivesForTheSameRequest()
    {
        const string Bad = "{\"dir\":\"x\",\"team\":\"tm\",\"run\":\"r\",\"id\":\"d1\",\"choice\":\"a\",\"choices\":[\"b\"]}";
        var support = new Ah.Daemon.UiAnswerSupport(_ => null, _ => throw new InvalidOperationException("not reached"));

        var routed = Ah.Daemon.DaemonModule.RunUiAnswer(support, System.Text.Encoding.UTF8.GetBytes(Bad));
        var (exit, stdout, _) = UiAnswer(true, Bad);

        Assert.Equal((400, 2), (routed.Status, exit));
        Assert.Equal(JsonText.Stringify(routed.Reply!, 0), stdout.TrimEnd('\n'));
    }
}
