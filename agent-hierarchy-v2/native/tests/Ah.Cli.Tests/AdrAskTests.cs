using System.Globalization;
using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Cli.Verbs;
using Ah.Cli.Verbs.Msg;
using Ah.Core.Json;
using Ah.Hierarchy;
using Ah.Mesh;
using Ah.Platform;
using Ah.StateDb;
using Ah.TestKit;

namespace Ah.Cli.Tests;

/// <summary>b195: <c>ah adr ask</c> parks a question for a proposed ADR, and the user's option answer on Needs-you settles it as theirs.</summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class AdrAskTests : IDisposable
{
    private readonly Sandbox _sandbox = new();
    private readonly string _hierarchy;
    private readonly string _teamFile;
    private readonly ServiceGraph _graph;
    private readonly FakeEnv _env;
    private readonly string _orchBox;
    private CutoverCallerKind _caller = CutoverCallerKind.TopOrchestrator;

    public AdrAskTests()
    {
        _hierarchy = Path.Combine(_sandbox.Root, "hier");
        _teamFile = Path.Combine(_hierarchy, "teams", "tm.json");
        Directory.CreateDirectory(Path.GetDirectoryName(_teamFile)!);
        File.WriteAllText(
            _teamFile,
            "{\"team_id\":\"t-tm\",\"orchestrator\":{\"pid\":" + System.Environment.ProcessId.ToString(CultureInfo.InvariantCulture)
            + ",\"pid_start\":\"" + FakeRunner.SelfStart + "\",\"session_id\":\"o\"},\"members\":[]}\n");
        using (var open = StateDbModule.Open(_sandbox.AhHome, mayCreate: true))
        {
            Assert.Equal(StateDbAvailability.Ready, open.Status.Availability);
        }

        var variables = new Dictionary<string, string?>
        {
            ["AH_TEST"] = "1", ["AH_HOME"] = _sandbox.AhHome, ["HOME"] = _sandbox.Home, ["WRANGL_HIERARCHY_DIR"] = _hierarchy,
        };
        _env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables, new PluginInfo(_sandbox.PluginRoot, "wrangl", "1.0.0", "1"));
        _graph = ServiceGraph.Build(PlatformModule.CreateIo(null), _env, FakeRunner.KnowingSelf(), new FakeDaemonClient { Reply = _ => new DaemonReply(0, default, "down") });
        _orchBox = _graph.Mailbox.BoxDirFor(_teamFile, "orchestrator");
        Assert.True(_graph.Mailbox.WriteRegistration(new RegistrationWrite("o", [new BoxRef("orchestrator@tm", _orchBox)], _teamFile, "stub", System.Environment.ProcessId)));
    }

    public void Dispose() => _sandbox.Dispose();

    private CliContext Context() =>
        new(_env, new RecordingExec(), new FakeRunner(), "ah", _sandbox.Executable("install/ah"), _sandbox.Home, 501, null, new Lazy<ServiceGraph>(_graph),
            () => new DecisionCaller(new CutoverCaller(_caller, _caller == CutoverCallerKind.User ? "who" : "orchestrator@tm"), [], "o", System.Environment.ProcessId, "start", "ttys000"));

    private (int Code, string Out, string Err) Adr(params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var code = AdrVerb.Run(Context(), [.. args, "--cwd", _sandbox.Root], stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private (int Exit, string Out, string Err) Msg(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = MsgVerb.Run(Context(), [.. args, "--cwd", _sandbox.Root, "--team", "tm"], stdout, stderr);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    /// <summary>The body <c>POST /ui/v1/answer</c> takes, through the route's own table (<c>ah msg decision ui-answer</c>).</summary>
    private (int Exit, string Out, string Err) UiAnswer(string answer)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var dirId = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(_hierarchy)))[..8].ToLowerInvariant();
        var json = $$"""{"dir":"{{dirId}}","team":"tm","id":"q1",{{answer}}}""";
        var exit = MsgVerb.Run(Context() with { UiAnswerCaller = () => true }, ["decision", "ui-answer", "--json", json, "--cwd", _sandbox.Root], stdout, stderr);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private string Proposed(string title = "Use one table")
    {
        var added = Adr("add", title);
        Assert.True(added.Code == 0, added.Err);
        return added.Out.Trim();
    }

    private JsonValue Shown(string id) => JsonText.Parse(Adr("show", id, "--json").Out);

    private List<ParkedQuestion> Parked() =>
        _graph.Io.Files.ReadAskLog(ParkedQuestions.PathIn(_hierarchy)).TryLines(out var lines) ? [.. ParkedQuestions.Fold(string.Join('\n', lines))] : throw new IOException("store unreadable");

    private string AnsweredText() =>
        _graph.Mailbox.ReadEntries(_orchBox).Entries.Select(static e => e.AsObject()).Single(static e => e["event"].AsString() == "question-answered")["text"].AsString();

    [Fact]
    public void AskParksAQuestionCarryingTheAdrWithAcceptAndReject()
    {
        var id = Proposed();

        var asked = Adr("ask", id);

        Assert.True(asked.Code == 0, asked.Err + asked.Out);
        var q = Assert.Single(Parked());
        Assert.Equal(1, q.Adr);
        Assert.Equal(ParkedQuestions.AdrAskSource, q.Source);
        Assert.Equal(["Accept", "Reject"], q.Labels);

        // negatives: a second ask while q1 is open, and an ADR that is not proposed
        Assert.Contains("adr_already_asked", Adr("ask", id).Err, StringComparison.Ordinal);
        var accepted = Adr("add", "Already decided", "--status", "accepted").Out.Trim();
        Assert.NotEqual(0, Adr("ask", accepted).Code);
        Assert.Single(Parked());
    }

    [Fact]
    public void AcceptByOptionThroughTheUiRouteSettlesAcceptedAsTheUser()
    {
        var id = Proposed();
        Assert.Equal(0, Adr("ask", id).Code);

        var (exit, stdout, stderr) = UiAnswer("\"choice\":\"a\"");

        Assert.True(exit == 0, stderr + stdout);
        var shown = Shown(id);
        Assert.Equal("accepted", shown.Member("status")!.Value.AsString());
        Assert.Equal("user", shown.Member("decided_by")!.Value.AsString());
        var history = shown.Member("history")!.Value.AsArray();
        var settled = history[^1];
        Assert.Equal("ui-answer", settled.Member("by")!.Value.AsString());
        Assert.Contains("q1", settled.Member("note")!.Value.AsString(), StringComparison.Ordinal);
    }

    [Fact]
    public void RejectByOptionSettlesRejected()
    {
        var id = Proposed();
        Assert.Equal(0, Adr("ask", id).Code);

        Assert.Equal(0, UiAnswer("\"choice\":\"Reject\"").Exit);

        Assert.Equal("rejected", Shown(id).Member("status")!.Value.AsString());
    }

    [Fact]
    public void TheOrchestratorsTextNamesTheSettle()
    {
        var id = Proposed();
        Assert.Equal(0, Adr("ask", id).Code);

        Assert.Equal(0, UiAnswer("\"choice\":\"a\"").Exit);

        Assert.EndsWith($"; {id} settled accepted", AnsweredText(), StringComparison.Ordinal);
    }

    [Fact]
    public void AnOtherAnswerSettlesNothingAndIsStillDelivered()
    {
        var id = Proposed();
        Assert.Equal(0, Adr("ask", id).Code);

        Assert.Equal(0, UiAnswer("\"choices\":[\"other: accept it next week\"]").Exit);

        Assert.Equal("proposed", Shown(id).Member("status")!.Value.AsString());
        Assert.False(Assert.Single(Parked()).Open);
        Assert.Contains($"{id} not settled", AnsweredText(), StringComparison.Ordinal);
    }

    [Fact]
    public void BothOptionsAtOnceSettleNothing()
    {
        var id = Proposed();
        Assert.Equal(0, Adr("ask", id).Code);

        Assert.Equal(0, UiAnswer("\"choices\":[\"a\",\"b\"]").Exit);

        Assert.Equal("proposed", Shown(id).Member("status")!.Value.AsString());
    }

    [Fact]
    public void ParkWithTheAdrIdInItsTextSettlesNothing()
    {
        var id = Proposed();
        Assert.Equal(0, Msg("decision", "park", "--header", id, "--question", $"Settle {id}?", "--option", "Accept", "--option", "Reject", "--default", "d").Exit);
        Assert.Null(Assert.Single(Parked()).Adr);

        Assert.Equal(0, UiAnswer("\"choice\":\"a\"").Exit);

        Assert.Equal("proposed", Shown(id).Member("status")!.Value.AsString());
    }

    [Fact]
    public void AnOrchestratorRelayedAnswerSettlesNothing()
    {
        var id = Proposed();
        Assert.Equal(0, Adr("ask", id).Code);
        _graph.Hierarchy.Peers.AppendUserPromptRecord("o", "p1");

        var (exit, stdout, stderr) = Msg("decision", "answer", "q1", "Accept");

        Assert.True(exit == 0, stderr + stdout);
        Assert.Equal("p1", JsonText.Parse(stdout).Member("relayed_prompt")!.Value.AsString());
        Assert.Equal("proposed", Shown(id).Member("status")!.Value.AsString());
    }

    [Fact]
    public void AnAdrFieldOnALineFromAnotherSourceIsIgnored()
    {
        var id = Proposed();
        var options = new JsonArray { new JsonObject { ["label"] = "Accept" }, new JsonObject { ["label"] = "Reject" } };
        var question = new JsonObject { ["question"] = $"Settle {id}?", ["header"] = id, ["multiSelect"] = false, ["options"] = options };
        var line = ParkedQuestions.ParkedLine("q1", "2026-11-22T00:00:00.000Z", "tm", "o", null, 0, question, "ah:other", "d");
        line["adr"] = 1.0;
        _graph.Hierarchy.StateCalls.AppendParkedQuestion(_hierarchy, line);
        Assert.Null(Assert.Single(Parked()).Adr);

        Assert.Equal(0, UiAnswer("\"choice\":\"a\"").Exit);

        Assert.Equal("proposed", Shown(id).Member("status")!.Value.AsString());
    }
}
