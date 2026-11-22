using System.Globalization;
using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Cli.Verbs.Msg;
using Ah.Core.Json;
using Ah.Hierarchy;
using Ah.Platform;
using Ah.TestKit;

namespace Ah.Cli.Tests;

/// <summary><c>ah msg decision answer q&lt;k&gt;</c>: the user's answer to a question parked while work was in flight, recorded as a harness answer would be.</summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class MsgQuestionAnswerTests : IDisposable
{
    private readonly Sandbox _sandbox = new();
    private readonly string _hierarchy;
    private readonly ServiceGraph _graph;
    private readonly FakeEnv _env;
    private readonly string _teamFile;

    public MsgQuestionAnswerTests()
    {
        _hierarchy = Path.Combine(_sandbox.Root, "hier");
        _teamFile = Path.Combine(_hierarchy, "teams", "tm.json");
        Directory.CreateDirectory(Path.GetDirectoryName(_teamFile)!);
        File.WriteAllText(
            _teamFile,
            "{\"team_id\":\"t-tm\",\"orchestrator\":{\"pid\":" + System.Environment.ProcessId.ToString(CultureInfo.InvariantCulture)
            + ",\"pid_start\":\"" + FakeRunner.SelfStart + "\",\"session_id\":\"o\"},\"members\":[]}\n");
        var variables = new Dictionary<string, string?>
        {
            ["AH_TEST"] = "1",
            ["AH_HOME"] = _sandbox.AhHome,
            ["HOME"] = _sandbox.Home,
            ["WRANGL_HIERARCHY_DIR"] = _hierarchy,
        };
        _env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables, new PluginInfo(_sandbox.PluginRoot, "wrangl", "1.0.0", "1"));
        _graph = ServiceGraph.Build(PlatformModule.CreateIo(null), _env, FakeRunner.KnowingSelf(), new FakeDaemonClient { Reply = _ => new DaemonReply(0, default, "down") });
    }

    public void Dispose() => _sandbox.Dispose();

    private CutoverCallerKind _caller = CutoverCallerKind.User;

    private string _detail = "who";

    private CliContext Context(ServiceGraph graph) =>
        new(_env, new RecordingExec(), new FakeRunner(), "ahd", _sandbox.Executable("install/ahd"), _sandbox.Home, 501, null, new Lazy<ServiceGraph>(graph),
            () => new DecisionCaller(new CutoverCaller(_caller, _detail), [], "o", System.Environment.ProcessId, "start", "ttys000"));

    private void AsOrchestrator(string team = "tm")
    {
        _caller = CutoverCallerKind.TopOrchestrator;
        _detail = "orchestrator@" + team;
    }

    private (int Exit, string Out, string Err) Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = MsgVerb.Run(Context(_graph), [.. args, "--cwd", _sandbox.Root, "--team", "tm"], stdout, stderr);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private string StorePath => ParkedQuestions.PathIn(_hierarchy);

    private string AsksPath => Path.Combine(_hierarchy, "asks.jsonl");

    private static JsonObject Question(string text, string header, params string[] labels)
    {
        var options = new JsonArray();
        foreach (var label in labels)
        {
            options.Add(new JsonObject { ["label"] = label, ["description"] = "d" });
        }

        return new JsonObject { ["question"] = text, ["header"] = header, ["multiSelect"] = false, ["options"] = options };
    }

    private void Park(string id, string source = "ah:bootstrap:autonomous-pipeline:authority", string session = "o", string at = "2026-10-26T01:09:00.000Z", string header = "Authority")
    {
        var line = ParkedQuestions.ParkedLine(id, at, "tm", session, "toolu_1", 0, Question("Who decides?", header, "Orchestrator acts for me (this run)", "Members ask me"), source, "authority");
        _graph.Hierarchy.StateCalls.AppendParkedQuestion(_hierarchy, line);
    }

    /// <summary>An asks log's text as the graph reads it (per-dir-files-to-db S10: the asks table once a database exists), a line each.</summary>
    private string Log(string path) =>
        _graph.Io.Files.ReadAskLog(path).TryLines(out var lines) ? string.Concat(lines.Select(static l => l + "\n")) : throw new IOException($"{path} could not be read");

    private bool Logged(string path) => Log(path).Length > 0;

    private List<JsonValue> Asks() => [.. Log(AsksPath).Split('\n').Where(l => l.Length > 0).Select(l => JsonText.Parse(l))];

    [Fact]
    public void AParkedQuestionTellsTheOrchestratorToCarryOnAndTheUsersAnswerClosesIt()
    {
        AsOrchestrator();

        var (exit, stdout, stderr) = Run("decision", "park", "--header", "Scope", "--question", "Ship it?", "--option", "Yes", "--option", "No", "--default", "it waits on the branch");

        Assert.True(exit == 0, stderr);
        Assert.Equal("q1", JsonText.Parse(stdout).Member("id")!.Value.AsString());
        Assert.Contains("Until it is answered: it waits on the branch", JsonText.Parse(stdout).Member("next")!.Value.AsString(), StringComparison.Ordinal);
        _caller = CutoverCallerKind.User;
        Assert.Equal(0, Run("decision", "answer", "q1", "Yes").Exit);
        Assert.False(Assert.Single(ParkedQuestions.Fold(Log(StorePath))).Open);
    }

    /// <summary><c>ah msg decision ui-answer</c>, the request table <c>POST /ui/v1/answer</c> shares, with a body of <paramref name="json"/>.</summary>
    private (int Exit, string Out, string Err) UiAnswer(string json)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = MsgVerb.Run(Context(_graph) with { UiAnswerCaller = () => true }, ["decision", "ui-answer", "--json", json, "--cwd", _sandbox.Root], stdout, stderr);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private string HierId() => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(_hierarchy)))[..8].ToLowerInvariant();

    [Fact]
    public void ANoRunUiBodyForAnOpenParkedQuestionIsAcceptedAndTheQuestionReadsAsAnswered()
    {
        AsOrchestrator();
        Assert.Equal(0, Run("decision", "park", "--header", "Scope", "--question", "Ship it?", "--option", "Yes", "--option", "No", "--default", "d").Exit);

        var (exit, stdout, stderr) = UiAnswer($$"""{"dir":"{{HierId()}}","team":"tm","id":"q1","choice":"b"}""");

        Assert.True(exit == 0, stderr + stdout);
        var reply = JsonText.Parse(stdout).AsObject();
        Assert.True(reply["ok"].AsBool());
        Assert.Equal("q1", reply["id"].AsString());
        Assert.Equal(["No"], reply["answers"].AsArray().Select(static a => a.AsString()));
        var q1 = Assert.Single(ParkedQuestions.Fold(Log(StorePath)));
        Assert.False(q1.Open);
        Assert.Equal(["No"], q1.AnsweredLabels);

        // the negative: the same body again is the core's already_answered, a 409, and writes nothing more
        var again = UiAnswer($$"""{"dir":"{{HierId()}}","team":"tm","id":"q1","choice":"a"}""");
        Assert.Equal(2, again.Exit);
        Assert.Equal("already_answered", JsonText.Parse(again.Out).Member("reason")!.Value.AsString());
        Assert.Equal(["No"], Assert.Single(ParkedQuestions.Fold(Log(StorePath))).AnsweredLabels);
    }

    [Fact]
    public void ANoRunUiBodyForATerminalOnlyQuestionIsRefusedAndTheQuestionStaysOpen()
    {
        Park("q1");
        Assert.True(ParkedQuestions.IsTerminalOnly(Assert.Single(ParkedQuestions.Fold(Log(StorePath)))));

        var answer = UiAnswer($$"""{"dir":"{{HierId()}}","team":"tm","id":"q1","choice":"a"}""");
        var dismiss = UiAnswer($$"""{"dir":"{{HierId()}}","team":"tm","id":"q1","dismiss":"Already done"}""");

        Assert.Equal((2, 2), (answer.Exit, dismiss.Exit));
        Assert.Equal("answer_relay_reserved", JsonText.Parse(answer.Out).Member("reason")!.Value.AsString());
        Assert.Equal("answer_relay_reserved", JsonText.Parse(dismiss.Out).Member("reason")!.Value.AsString());
        Assert.True(Assert.Single(ParkedQuestions.Fold(Log(StorePath))).Open);
        Assert.Empty(Asks());
    }

    [Fact]
    public void NegativeANoRunUiBodyForADecisionIdIsStillABadRequest()
    {
        var (exit, stdout, _) = UiAnswer($$"""{"dir":"{{HierId()}}","team":"tm","id":"d1","choice":"a"}""");

        Assert.Equal(2, exit);
        Assert.Equal("run must be a non-empty string", JsonText.Parse(stdout).Member("detail")!.Value.AsString());
    }

    [Fact]
    public void AChoiceAnswersAParkedQuestionAsTheWaitingListPrintsIt()
    {
        AsOrchestrator();
        Assert.Equal(0, Run("decision", "park", "--header", "Scope", "--question", "Ship it?", "--option", "Yes", "--option", "No", "--default", "d").Exit);
        Assert.Equal(0, Run("decision", "park", "--header", "Name", "--question", "Rename it?", "--option", "A", "--option", "B", "--default", "d").Exit);
        _caller = CutoverCallerKind.User;

        // the negative: a letter past the options is no option, and answers nothing
        Assert.Contains("unmatched_label", Run("decision", "answer", "q1", "--choice", "c").Out, StringComparison.Ordinal);

        Assert.Equal(0, Run("decision", "answer", "q1", "--choice", "other:closed as stale").Exit);
        Assert.Equal(0, Run("decision", "answer", "q2", "--choice", "b").Exit);
        var folded = ParkedQuestions.Fold(Log(StorePath)).ToDictionary(q => q.Id);
        Assert.Equal(["closed as stale"], folded["q1"].AnsweredLabels);
        Assert.Equal(["B"], folded["q2"].AnsweredLabels);
    }

    [Fact]
    public void TeamScopesTheQuestionAndWithoutItTheResultNamesTheTeam()
    {
        AsOrchestrator();
        Assert.Equal(0, Run("decision", "park", "--header", "Scope", "--question", "Ship it?", "--option", "Yes", "--option", "No", "--default", "d").Exit);
        _caller = CutoverCallerKind.User;
        (int Exit, string Out, string Err) Bare(params string[] args)
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exit = MsgVerb.Run(Context(_graph), [.. args, "--cwd", _sandbox.Root], stdout, stderr);
            return (exit, stdout.ToString(), stderr.ToString());
        }

        // the negative: another team's q1 is not found under --team
        Assert.Contains("unknown_question", Bare("decision", "answer", "q1", "--choice", "A", "--team", "zz").Out, StringComparison.Ordinal);

        var (exit, stdout, stderr) = Bare("decision", "answer", "q1", "--choice", "A");
        Assert.True(exit == 0, stderr);
        Assert.Equal("tm", JsonText.Parse(stdout).Member("team")!.Value.AsString());
        Assert.Contains("of team tm", stderr, StringComparison.Ordinal);
        Assert.Equal(["Yes"], Assert.Single(ParkedQuestions.Fold(Log(StorePath))).AnsweredLabels);
    }

    [Fact]
    public void ADialogAnswerRecordedButNeverMatchedIsMatchedByTheNextQuestionVerb()
    {
        AsOrchestrator();
        Assert.Equal(0, Run("decision", "park", "--header", "Scope", "--question", "Ship it?", "--option", "Yes", "--option", "No", "--default", "d").Exit);
        Assert.Equal(0, Run("decision", "park", "--header", "Name", "--question", "Rename it?", "--option", "A", "--option", "B", "--default", "d").Exit);

        // the hook recorded these answers while the store could not be read, so it matched neither to its question
        JsonValue Dialog(string question, string label, string option, TimeSpan offset) => HookRow(question, label, option, DateTimeOffset.UtcNow + offset);
        _graph.Hierarchy.StateCalls.AppendAsk(_hierarchy, Dialog("Ship it?", "No", "Yes", TimeSpan.FromMinutes(1)));
        // the negative: an answer given before the question was parked is no answer to it
        _graph.Hierarchy.StateCalls.AppendAsk(_hierarchy, Dialog("Rename it?", "A", "B", TimeSpan.FromHours(-1)));

        _caller = CutoverCallerKind.User;
        var again = Run("decision", "answer", "q1", "Yes");

        Assert.Contains("already_answered", again.Out + again.Err, StringComparison.Ordinal);
        var folded = ParkedQuestions.Fold(Log(StorePath)).ToDictionary(q => q.Id);
        Assert.Equal(["No"], folded["q1"].AnsweredLabels);
        Assert.True(folded["q2"].Open);
    }

    [Fact]
    public void OneRecordedAnswerMarksOnlyOneOfTwoSameTextQuestions()
    {
        AsOrchestrator();
        Assert.Equal(0, Run("decision", "park", "--header", "Scope", "--question", "Ship it?", "--option", "Yes", "--option", "No", "--default", "d").Exit);
        Assert.Equal(0, Run("decision", "park", "--header", "Scope", "--question", "Ship it?", "--option", "Yes", "--option", "No", "--default", "d").Exit);
        _graph.Hierarchy.StateCalls.AppendAsk(_hierarchy, HookRow("Ship it?", "No", "Yes", DateTimeOffset.UtcNow.AddMinutes(1)));

        // two sweeps: the second must not spend the same answer on the other question
        Assert.Equal(0, Run("decision", "list", "--waiting", "--json").Exit);
        Assert.Equal(0, Run("decision", "list", "--waiting", "--json").Exit);

        Assert.Single(ParkedQuestions.Fold(Log(StorePath)), static q => !q.Open);
    }

    [Fact]
    public void AnAnswerRecordedBeforeTheFirstSweepIsNeverReplayed()
    {
        // a question and its unmatched answer, both written before any sweep ran (an upgrade from before the sweep existed)
        var now = DateTimeOffset.UtcNow;
        _graph.Hierarchy.StateCalls.AppendParkedQuestion(_hierarchy, ParkedQuestions.ParkedLine(
            "q1", Ah.Core.Time.Timestamps.ToIsoString(now.AddMinutes(-5)), "tm", "s", null, 0, Question("Ship it?", "Scope", "Yes", "No"), null, "d"));
        _graph.Hierarchy.StateCalls.AppendAsk(_hierarchy, HookRow("Ship it?", "No", "Yes", now.AddMinutes(-1)));

        // the next park is the first sweep that may record the watermark; it and every sweep after it leave that answer alone
        AsOrchestrator();
        Assert.Equal(0, Run("decision", "park", "--header", "Name", "--question", "Rename it?", "--option", "A", "--option", "B", "--default", "d").Exit);
        Assert.Equal(0, Run("decision", "list", "--waiting", "--json").Exit);

        Assert.True(ParkedQuestions.Fold(Log(StorePath)).Single(q => q.Id == "q1").Open);
    }

    /// <summary>A dialog answer row as the ask-record hook writes it; <paramref name="sweep"/> false is an older build's row.</summary>
    private static JsonValue HookRow(string question, string label, string other, DateTimeOffset at, bool sweep = true)
    {
        var row = AskRecord.Row(
            Ah.Core.Time.Timestamps.ToIsoString(at), Ah.Config.ConfigModule.AskRecordWriter, "s",
            JsonText.Parse("[" + JsonText.Stringify(Question(question, "H", label, other), 0) + "]"),
            JsonValue.FromObject(new JsonObject { ["answers"] = new JsonObject { [question] = label } }), null);
        if (sweep)
        {
            row[ParkedQuestions.SweepField] = 1.0;
        }

        return row;
    }

    [Fact]
    public void AnOlderBuildsRowAfterTheMarkIsNeverMatched()
    {
        AsOrchestrator();
        Assert.Equal(0, Run("decision", "park", "--header", "Scope", "--question", "Ship it?", "--option", "Yes", "--option", "No", "--default", "d").Exit);
        _graph.Hierarchy.StateCalls.AppendAsk(_hierarchy, HookRow("Ship it?", "No", "Yes", DateTimeOffset.UtcNow.AddMinutes(1), sweep: false));

        Assert.Equal(0, Run("decision", "list", "--waiting", "--json").Exit);

        // the negative is ADialogAnswerRecordedButNeverMatchedIsMatchedByTheNextQuestionVerb: the same row with the field is matched
        Assert.True(Assert.Single(ParkedQuestions.Fold(Log(StorePath))).Open);
    }

    [Fact]
    public void ASweepOnAWritePathAdvancesTheMarkToTheNewestRowItConsidered()
    {
        AsOrchestrator();
        Assert.Equal(0, Run("decision", "park", "--header", "Scope", "--question", "Ship it?", "--option", "Yes", "--option", "No", "--default", "d").Exit);
        var at = DateTimeOffset.UtcNow.AddMinutes(1);
        _graph.Hierarchy.StateCalls.AppendAsk(_hierarchy, HookRow("Unrelated?", "A", "B", at));
        string LastMark() => Log(StorePath).Split('\n').Last(static l => l.Contains("\"swept\"", StringComparison.Ordinal));

        // the negative: a listing verb does not move it
        var before = LastMark();
        Assert.Equal(0, Run("decision", "list", "--waiting", "--json").Exit);
        Assert.Equal(before, LastMark());

        Assert.Equal(0, Run("decision", "park", "--header", "Name", "--question", "Rename it?", "--option", "A", "--option", "B", "--default", "d").Exit);
        Assert.Contains(Ah.Core.Time.Timestamps.ToIsoString(at), LastMark(), StringComparison.Ordinal);
    }

    [Fact]
    public void AnAnswerAppendedAfterTheMarkWithAnOlderTimeIsStillMatched()
    {
        AsOrchestrator();
        Assert.Equal(0, Run("decision", "park", "--header", "Scope", "--question", "Ship it?", "--option", "Yes", "--option", "No", "--default", "d").Exit);
        _graph.Hierarchy.StateCalls.AppendAsk(_hierarchy, HookRow("Unrelated?", "A", "B", DateTimeOffset.UtcNow.AddMinutes(5)));
        // this park sweeps the unrelated row, so the mark is past it
        Assert.Equal(0, Run("decision", "park", "--header", "Name", "--question", "Rename it?", "--option", "A", "--option", "B", "--default", "d").Exit);
        _graph.Hierarchy.StateCalls.AppendAsk(_hierarchy, HookRow("Ship it?", "No", "Yes", DateTimeOffset.UtcNow.AddMinutes(1)));

        Assert.Equal(0, Run("decision", "list", "--waiting", "--json").Exit);

        var folded = ParkedQuestions.Fold(Log(StorePath)).ToDictionary(q => q.Id);
        Assert.Equal(["No"], folded["q1"].AnsweredLabels);
        Assert.True(folded["q2"].Open);
    }

    [Fact]
    public void NegativeAMemberOrAParkWithOneOptionParksNothing()
    {
        _caller = CutoverCallerKind.Member;
        var member = Run("decision", "park", "--header", "Scope", "--question", "Ship it?", "--option", "Yes", "--option", "No", "--default", "d");
        AsOrchestrator();
        var oneOption = Run("decision", "park", "--header", "Scope", "--question", "Ship it?", "--option", "Yes", "--default", "d");

        Assert.NotEqual(0, member.Exit);
        Assert.NotEqual(0, oneOption.Exit);
        Assert.False(Logged(StorePath));
    }

    [Fact]
    public void NegativeAParkForAnotherTeamsMemberParksNothing()
    {
        AsOrchestrator();

        var (exit, stdout, stderr) = Run("decision", "park", "--header", "Scope", "--question", "Ship it?", "--option", "Yes", "--option", "No", "--default", "d", "--for", "alice@otherteam");

        Assert.NotEqual(0, exit);
        Assert.Contains("not a member of team tm", stdout + stderr, StringComparison.Ordinal);
        Assert.False(Logged(StorePath));
    }

    [Fact]
    public void TheUsersAnswerWritesTheHarnessShapedAsksRowMarksTheQuestionAnsweredAndMailsTheOrchestrator()
    {
        Park("q1");

        var (exit, stdout, stderr) = Run("decision", "answer", "q1", "Orchestrator acts for me (this run)");

        Assert.True(exit == 0, stderr);
        Assert.True(JsonText.Parse(stdout).Member("answered")!.Value.AsBool());
        var row = Assert.Single(Asks());
        Assert.Equal(["at", "writer", "session_id", "questions", "response", "metadata"], row.AsObject().Keys.Take(6));
        Assert.Equal("ans:q1", row.Member("op_id")!.Value.AsString());
        Assert.Equal("decision-answer", row.Member("writer")!.Value.AsString());
        Assert.Equal("o", row.Member("session_id")!.Value.AsString());
        Assert.Equal("Who decides?", row.Member("questions")!.Value.AsArray()[0].Member("question")!.Value.AsString());
        Assert.Equal("Orchestrator acts for me (this run)", row.Member("response")!.Value.Member("answers")!.Value.Member("Who decides?")!.Value.AsString());
        Assert.Equal("ah:bootstrap:autonomous-pipeline:authority;ref=q1", row.Member("metadata")!.Value.Member("source")!.Value.AsString());
        var folded = Assert.Single(ParkedQuestions.Fold(Log(StorePath)));
        Assert.False(folded.Open);
        Assert.Equal(["Orchestrator acts for me (this run)"], folded.AnsweredLabels);
    }

    [Fact]
    public void AnAuthorityAnswerGivenAfterTheRunStartedIsReadAsSurrogateByTheAnchorCheck()
    {
        Park("q1");
        _ = Run("decision", "answer", "q1", "Orchestrator acts for me (this run)");
        var recorded = string.Join('\n', Log(AsksPath), Log(StorePath));

        Assert.Equal("surrogate", HierarchyModule.RecordedAuthority(recorded, "o", null, "2026-10-26T01:10:00.000Z"));
    }

    [Fact]
    public void AnUnansweredAuthorityQuestionLeavesTheAnchorCheckWithNothingRecorded()
    {
        Park("q1");

        Assert.Null(HierarchyModule.RecordedAuthority(Log(StorePath), "o", null, "2026-10-26T01:10:00.000Z"));
    }

    [Fact]
    public void ALabelThatDiffersOnlyInCaseOrSurroundingSpaceIsAccepted()
    {
        Park("q1");

        var (exit, _, stderr) = Run("decision", "answer", "q1", "  MEMBERS ASK ME ");

        Assert.True(exit == 0, stderr);
        Assert.Equal("Members ask me", Asks().Single().Member("response")!.Value.Member("answers")!.Value.Member("Who decides?")!.Value.AsString());
    }

    [Fact]
    public void ASecondAnswerIsRefusedWithTheEarlierOneShownAndNothingIsWritten()
    {
        Park("q1");
        _ = Run("decision", "answer", "q1", "Members ask me");

        var again = Run("decision", "answer", "q1", "Orchestrator acts for me (this run)");

        Assert.Equal(2, again.Exit);
        Assert.Contains("q1 was already answered \"Members ask me\" at ", again.Err, StringComparison.Ordinal);
        Assert.Single(Asks());
    }

    [Fact]
    public void AnUnmatchedLabelIsRefusedWithTheOptionsListedAndNothingIsWritten()
    {
        Park("q1");

        var refused = Run("decision", "answer", "q1", "Maybe");

        Assert.Equal(2, refused.Exit);
        Assert.Contains("\"Maybe\" is not an option for q1. Options: Orchestrator acts for me (this run) | Members ask me.", refused.Err, StringComparison.Ordinal);
        Assert.Empty(Asks());
        Assert.True(ParkedQuestions.Fold(Log(StorePath)).Single().Open);
    }

    [Fact]
    public void AnUnknownQuestionIsRefused()
    {
        Park("q1");

        var refused = Run("decision", "answer", "q9", "Members ask me");

        Assert.Equal(2, refused.Exit);
        Assert.Contains("No parked question q9 for this team.", refused.Err, StringComparison.Ordinal);
        Assert.Empty(Asks());
    }

    [Fact]
    public void ACallerThatIsAnAgentSessionIsRefusedAndNothingIsWritten()
    {
        Park("q1");
        _caller = CutoverCallerKind.TopOrchestrator;

        var refused = Run("decision", "answer", "q1", "Members ask me");

        Assert.Equal(2, refused.Exit);
        Assert.Contains("Only the user can answer q1. Run this in a terminal: ah msg decision answer q1 \"<label>\"", refused.Err, StringComparison.Ordinal);
        Assert.Empty(Asks());
        Assert.True(ParkedQuestions.Fold(Log(StorePath)).Single().Open);
    }

    private const string Label = "Members ask me";

    private void UserTyped(string session = "o", string id = "p1") => _graph.Hierarchy.Peers.AppendUserPromptRecord(session, id);

    private void AssertRelayRefused(int exit, string err, string code)
    {
        Assert.Equal(2, exit);
        Assert.Contains(code, err, StringComparison.Ordinal);
        Assert.Empty(Asks());
        Assert.True(ParkedQuestions.Fold(Log(StorePath)).Single().Open);
    }

    [Fact]
    public void TheOrchestratorRelaysTheUsersAnswerWhenATypedPromptArrivedSinceThePark()
    {
        Park("q1", header: "Plan");
        UserTyped();
        AsOrchestrator();

        var (exit, stdout, stderr) = Run("decision", "answer", "q1", "members ask me");

        Assert.True(exit == 0, stderr);
        Assert.Equal("p1", JsonText.Parse(stdout).Member("relayed_prompt")!.Value.AsString());
        var row = Assert.Single(Asks());
        Assert.Equal("decision-answer-relayed", row.Member("writer")!.Value.AsString());
        Assert.Contains(";relayed-prompt=p1", row.Member("metadata")!.Value.Member("source")!.Value.AsString(), StringComparison.Ordinal);
        var folded = Assert.Single(ParkedQuestions.Fold(Log(StorePath)));
        Assert.False(folded.Open);
        Assert.Equal([Label], folded.AnsweredLabels);
        Assert.Contains("\"writer\":\"decision-answer-relayed\"", Log(StorePath), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Authority", "ah:bootstrap:autonomous-pipeline:authority")]
    [InlineData("Member rules", "ah:R7;consent=member-launch-rules;cv=1")]
    public void ARelayToAQuestionOnlyTheUsersTerminalAnswerGrantsIsRefusedAndTheUserCanStillAnswerIt(string header, string source)
    {
        Park("q1", source: source, header: header);
        UserTyped();
        AsOrchestrator();
        var parked = Log(StorePath);

        var r = Run("decision", "answer", "q1", "Orchestrator acts for me (this run)");

        AssertRelayRefused(r.Exit, r.Err, "answer_relay_reserved");
        Assert.Contains("can only be answered by the user", r.Err, StringComparison.Ordinal);
        Assert.Equal(parked, Log(StorePath));

        _caller = CutoverCallerKind.User;
        var byUser = Run("decision", "answer", "q1", "Orchestrator acts for me (this run)");

        Assert.True(byUser.Exit == 0, byUser.Err);
        Assert.DoesNotContain("relayed_prompt", byUser.Out, StringComparison.Ordinal);
        Assert.Equal("decision-answer", Assert.Single(Asks()).Member("writer")!.Value.AsString());
    }

    [Fact]
    public void TheAwayParkTextInvitesAChatAnswerOnlyForQuestionsARelayCanAnswer()
    {
        Park("q1", header: "Plan");
        Park("q2", header: "Authority");
        var folded = ParkedQuestions.Fold(Log(StorePath));

        var ordinary = ParkedQuestions.ParkedReason([folded[0]], 30);
        var reserved = ParkedQuestions.ParkedReason([folded[1]], 30);

        Assert.Contains("relay: ah msg decision answer q1", ordinary, StringComparison.Ordinal);
        Assert.DoesNotContain("relay:", reserved, StringComparison.Ordinal);
        Assert.Contains("Only the user can answer it, from a terminal", reserved, StringComparison.Ordinal);
    }

    [Fact]
    public void ARelayToAQuestionAlreadyAnsweredIsReportedAsAnsweredBeforeTheReservedCheck()
    {
        Park("q1");
        _ = Run("decision", "answer", "q1", "Members ask me");
        UserTyped();
        AsOrchestrator();

        var r = Run("decision", "answer", "q1", "Members ask me");

        Assert.Contains("already_answered", r.Err, StringComparison.Ordinal);
    }

    [Fact]
    public void OneTypedPromptRelaysTwoQuestionsAndEachSuccessNamesThePrompt()
    {
        Park("q1", header: "Plan");
        Park("q2", header: "Plan");
        UserTyped();
        AsOrchestrator();

        const string OtherLabel = "Orchestrator acts for me (this run)";
        var first = Run("decision", "answer", "q1", Label);
        var second = Run("decision", "answer", "q2", OtherLabel);

        Assert.True(first.Exit == 0 && second.Exit == 0, first.Err + second.Err);
        Assert.Equal("p1", JsonText.Parse(first.Out).Member("relayed_prompt")!.Value.AsString());
        Assert.Equal("p1", JsonText.Parse(second.Out).Member("relayed_prompt")!.Value.AsString());
        var folded = ParkedQuestions.Fold(Log(StorePath)).ToDictionary(q => q.Id);
        Assert.All(folded.Values, q => Assert.False(q.Open));
        Assert.Equal([Label], folded["q1"].AnsweredLabels);
        Assert.Equal([OtherLabel], folded["q2"].AnsweredLabels);
        var answers = Asks().ToDictionary(
            r => r.Member("metadata")!.Value.Member("source")!.Value.AsString().Split(';').Single(p => p.StartsWith("ref=", StringComparison.Ordinal)),
            r => r.Member("response")!.Value.Member("answers")!.Value.Member("Who decides?")!.Value.AsString());
        Assert.Equal(2, answers.Count);
        Assert.Equal(Label, answers["ref=q1"]);
        Assert.Equal(OtherLabel, answers["ref=q2"]);
    }

    [Fact]
    public void TheOrchestratorWithNoTypedPromptIsRefused()
    {
        Park("q1", header: "Plan");
        AsOrchestrator();

        var r = Run("decision", "answer", "q1", Label);

        AssertRelayRefused(r.Exit, r.Err, "No prompt typed by the user");
    }

    [Fact]
    public void ATypedPromptOlderThanTheParkDoesNotCount()
    {
        Park("q1", at: "2099-01-01T00:00:00.000Z", header: "Plan");
        UserTyped();
        AsOrchestrator();

        var r = Run("decision", "answer", "q1", Label);

        AssertRelayRefused(r.Exit, r.Err, "No prompt typed by the user");
    }

    [Fact]
    public void ATypedPromptInAnotherSessionDoesNotCount()
    {
        Park("q1", header: "Plan");
        UserTyped(session: "someone-else");
        AsOrchestrator();

        var r = Run("decision", "answer", "q1", Label);

        AssertRelayRefused(r.Exit, r.Err, "No prompt typed by the user");
    }

    [Fact]
    public void ALabelNotAmongTheOptionsIsRefusedEvenWithATypedPrompt()
    {
        Park("q1", header: "Plan");
        UserTyped();
        AsOrchestrator();

        var r = Run("decision", "answer", "q1", "Maybe");

        AssertRelayRefused(r.Exit, r.Err, "is not an option for q1");
    }

    [Theory]
    [InlineData("Member")]
    [InlineData("SubOrchestrator")]
    [InlineData("Unknown")]
    public void AMemberASubOrchestratorOrAnUnknownCallerIsRefusedEvenWithATypedPrompt(string kind)
    {
        Park("q1");
        UserTyped();
        _caller = Enum.Parse<CutoverCallerKind>(kind);
        _detail = "orchestrator@tm";

        var r = Run("decision", "answer", "q1", Label);

        AssertRelayRefused(r.Exit, r.Err, "Only the user can answer q1");
    }

    [Fact]
    public void AnOrchestratorOfAnotherTeamIsRefusedEvenWithATypedPrompt()
    {
        Park("q1");
        UserTyped();
        AsOrchestrator("other");

        var r = Run("decision", "answer", "q1", Label);

        AssertRelayRefused(r.Exit, r.Err, "Only the user can answer q1");
    }

    [Fact]
    public void TheSummaryListsOpenQuestionsWithNoRunAnchorAndDropsThemOnceAnswered()
    {
        Park("q1");

        var open = JsonText.Parse(Run("decision", "list", "--summary").Out).Member("questions_waiting")!.Value.AsArray();

        var item = Assert.Single(open);
        Assert.Equal("q1", item.Member("id")!.Value.AsString());
        Assert.Equal("Authority", item.Member("header")!.Value.AsString());
        Assert.Equal("members ask you through the orchestrator (authority: user).", item.Member("default")!.Value.AsString());
        Assert.Equal("ah msg decision answer q1 \"Orchestrator acts for me (this run)\"", item.Member("answer")!.Value.AsString());
        _ = Run("decision", "answer", "q1", "Members ask me");
        Assert.Empty(JsonText.Parse(Run("decision", "list", "--summary").Out).Member("questions_waiting")!.Value.AsArray());
    }

    [Fact]
    public void TheDecisionLineCountsAnOpenQuestionAsWaiting()
    {
        Park("q1");

        Assert.Contains("1 waiting", Run("decision", "list", "--line", "--since", "2026-01-01T00:00:00Z").Out, StringComparison.Ordinal);
    }

    [Fact]
    public void ADecisionIdStillGoesToTheRunDecisionAnswer()
    {
        Park("q1");

        var routed = Run("decision", "answer", "d1", "--choice", "a");

        Assert.DoesNotContain("parked question", routed.Err, StringComparison.Ordinal);
        Assert.Empty(Asks());
    }
}
