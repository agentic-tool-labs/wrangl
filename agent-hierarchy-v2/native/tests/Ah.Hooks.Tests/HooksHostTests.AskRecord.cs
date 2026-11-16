using System.Text;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Mesh;

namespace Ah.Hooks.Tests;

public sealed partial class HooksHostTests
{
    private const string AskEvent =
        """{"hook_event_name":"PostToolUse","session_id":"sess-1","cwd":"CWD","tool_use_id":"toolu_1","tool_name":"AskUserQuestion","tool_input":{"questions":[{"question":"Which?","header":"h","multiSelect":false,"options":[{"label":"a"},{"label":"b"}]}]},"tool_response":{"answers":{"Which?":"a"}}}""";

    /// <summary>The caller helper of a session that is, or is not, the recorded Orchestrator of a team of this hierarchy directory.</summary>
    private sealed class DeciderCallers(bool recorded, string dir) : IMailCallers
    {
        public RequestContext RequestContext(string cwd) => throw new NotSupportedException();

        public IReadOnlyList<CallerBox> ResolveCaller(CallerRequest request) => throw new NotSupportedException();

        public IReadOnlyList<CallerBox> RegisterCaller(CallerRequest request) => throw new NotSupportedException();

        public IReadOnlyList<CallerBox> CallerBoxes(CallerRequest request) => throw new NotSupportedException();

        public IReadOnlyList<DeciderBox> JoinDeciderBoxes(string cwd, string? sessionId = null) => recorded ? [new DeciderBox("orchestrator@tm", "tm", sessionId) { Dir = dir }] : [];

        public AckIdsResult AckRouted(string dir, IReadOnlyList<string> ids, string by, string? sessionId = null) => throw new NotSupportedException();

        public string? ProcessCommand(int pid) => null;
    }

    private static Func<GateServices> AsOrchestrator(Rig rig, bool recorded = true) =>
        () => rig.Services with { Advisory = new AdvisoryServices(null!, null!, new DeciderCallers(recorded, rig.Services.Config.HierarchyDir(rig.Sandbox.Root + "/proj")), null!, null!, null!, null!, null!, null!, null!, null!) };

    private static string AskEventIn(Rig rig, string? replaceKey = null, string? with = null)
    {
        var text = AskEvent.Replace("CWD", rig.Sandbox.Root + "/proj", StringComparison.Ordinal);
        return replaceKey is null ? text : text.Replace(replaceKey, with ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRecordedOrchestratorsAskIsRecordedAsReceivedUnderTheToolUseIdAfterTheDirIsEnsured()
    {
        var rig = new Rig();

        var outcome = rig.Run("posttooluse-ask-record", AskEventIn(rig), services: AsOrchestrator(rig));

        Assert.True(outcome.Stdout.IsEmpty);
        Assert.Equal(0, outcome.ExitCode);
        // the asks row, then its mirror in the question log, then the dir's first sweep watermark (once per dir)
        Assert.Equal(["decisions.ask", "qa-log.append", "decisions.ask"], rig.Writer.Calls.Where(call => call != "progress.ledger-append"));
        var mirror = JsonText.Parse(rig.Writer.Arguments[1]);
        Assert.Equal(rig.Services.Config.HierarchyDir(rig.Sandbox.Root + "/proj"), mirror.Member("dir")!.Value.AsString());
        Assert.Equal("ask", mirror.Member("row")!.Value.Member("kind")!.Value.AsString());
        Assert.Equal("rec:toolu_1", rig.Writer.OperationIds[0]);
        var args = JsonText.Parse(rig.Writer.Arguments[0]);
        Assert.EndsWith("/asks.jsonl", args.Member("path")!.Value.AsString(), StringComparison.Ordinal);
        var row = args.Member("row")!.Value;
        Assert.Equal("2026-10-11T12:00:00.000Z", row.Member("at")!.Value.AsString());
        Assert.Equal("posttooluse-ask-record", row.Member("writer")!.Value.AsString());
        Assert.Equal("sess-1", row.Member("session_id")!.Value.AsString());
        Assert.Equal("Which?", row.Member("questions")!.Value.AsArray()[0].Member("question")!.Value.AsString());
        Assert.Equal("a", row.Member("response")!.Value.Member("answers")!.Value.Member("Which?")!.Value.AsString());
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(args.Member("path")!.Value.AsString())!, ".gitignore")));
        Assert.Empty(rig.Errors.Records);
    }

    [Fact]
    public void OnlyTheSourceOfTheCallsMetadataIsRecordedCappedAndAbsentWhenTheCallHasNone()
    {
        var plain = new Rig();
        _ = plain.Run("posttooluse-ask-record", AskEventIn(plain), services: AsOrchestrator(plain));
        Assert.Null(JsonText.Parse(plain.Writer.Arguments[0]).Member("row")!.Value.Member("metadata"));

        var tagged = new Rig();
        var withMetadata = AskEventIn(tagged, "\"tool_input\":{", "\"tool_input\":{\"metadata\":{\"source\":\"ah:R7;consent=member-launch-rules;cv=1\"},");
        _ = tagged.Run("posttooluse-ask-record", withMetadata, services: AsOrchestrator(tagged));

        var row = JsonText.Parse(tagged.Writer.Arguments[0]).Member("row")!.Value;
        Assert.Equal("ah:R7;consent=member-launch-rules;cv=1", row.Member("metadata")!.Value.Member("source")!.Value.AsString());

        var noisy = new Rig();
        var long_ = new string('x', 500);
        var noisyEvent = AskEventIn(noisy, "\"tool_input\":{", "\"tool_input\":{\"metadata\":{\"source\":\"" + long_ + "\",\"extra\":{\"k\":1}},");
        _ = noisy.Run("posttooluse-ask-record", noisyEvent, services: AsOrchestrator(noisy));
        var kept = JsonText.Parse(noisy.Writer.Arguments[0]).Member("row")!.Value.Member("metadata")!.Value.AsObject();
        Assert.Equal(200, kept["source"].AsString().Length);
        Assert.Equal(["source"], kept.Keys.ToArray());
    }

    [Fact]
    public void ASessionThatIsNotARecordedOrchestratorWritesNothingAndLeavesNoError()
    {
        var notRecorded = new Rig();
        var noServices = new Rig();
        var other = new Rig();

        notRecorded.Run("posttooluse-ask-record", AskEventIn(notRecorded), services: AsOrchestrator(notRecorded, recorded: false));
        noServices.Run("posttooluse-ask-record", AskEventIn(noServices));
        other.Run("posttooluse-ask-record", AskEventIn(other, "\"tool_name\":\"AskUserQuestion\"", "\"tool_name\":\"Bash\""), services: AsOrchestrator(other));

        Assert.Empty(notRecorded.Writer.Calls);
        Assert.Empty(notRecorded.Errors.Records);
        Assert.Empty(noServices.Writer.Calls);
        Assert.Empty(noServices.Errors.Records);
        Assert.Empty(other.Writer.Calls);
        Assert.Empty(other.Errors.Records);
        Assert.False(File.Exists(Path.Combine(notRecorded.Sandbox.Root, "proj", ".claude", "hierarchy", ".gitignore")));
    }

    [Fact]
    public void APayloadWithoutQuestionsOrResponseWritesNothingAndLogsOneError()
    {
        var noQuestions = new Rig();
        var nullResponse = new Rig();
        var nullEvent = new Rig();
        var emptyResponse = new Rig();

        noQuestions.Run("posttooluse-ask-record", AskEventIn(noQuestions, "\"questions\":[", "\"qs\":["), services: AsOrchestrator(noQuestions));
        nullResponse.Run("posttooluse-ask-record", AskEventIn(nullResponse, "\"tool_response\":{\"answers\":{\"Which?\":\"a\"}}", "\"tool_response\":null"), services: AsOrchestrator(nullResponse));
        nullEvent.Run("posttooluse-ask-record", "null", services: AsOrchestrator(nullEvent));
        emptyResponse.Run("posttooluse-ask-record", AskEventIn(emptyResponse, "{\"answers\":{\"Which?\":\"a\"}}", "{}"), services: AsOrchestrator(emptyResponse));

        Assert.Empty(noQuestions.Writer.Calls);
        Assert.Single(noQuestions.Errors.Records);
        Assert.Empty(nullResponse.Writer.Calls);
        Assert.Single(nullResponse.Errors.Records);
        Assert.Empty(nullEvent.Writer.Calls);
        Assert.Single(nullEvent.Errors.Records);
        Assert.Equal(["decisions.ask", "qa-log.append", "decisions.ask"], emptyResponse.Writer.Calls.Where(call => call != "progress.ledger-append"));
        Assert.Empty(emptyResponse.Errors.Records);
    }

    private static List<JsonValue> LedgerRows(Rig rig) =>
        [.. rig.Writer.Calls.Zip(rig.Writer.Arguments).Where(p => p.First == "progress.ledger-append").Select(p => JsonText.Parse(p.Second).Member("row")!.Value)];

    [Fact]
    public void TheRecordedOrchestratorsAnswerIsALedgerRulingOfHeaderAndLabelAndAMembersIsNone()
    {
        var orchestrator = new Rig();
        var member = new Rig();

        orchestrator.Run("posttooluse-ask-record", AskEventIn(orchestrator), services: AsOrchestrator(orchestrator));
        member.Run("posttooluse-ask-record", AskEventIn(member), services: AsOrchestrator(member, recorded: false));

        var row = Assert.Single(LedgerRows(orchestrator));
        Assert.Equal("ruling", row.Member("kind")!.Value.AsString());
        Assert.Equal("h: a", row.Member("text")!.Value.AsString());
        Assert.Equal("toolu_1", row.Member("source")!.Value.Member("tool_use_id")!.Value.AsString());
        Assert.Empty(LedgerRows(member));
    }

    [Fact]
    public void ASubagentsAnswerInTheOwnerSessionIsNotALedgerRuling()
    {
        var sub = new Rig();

        sub.Run("posttooluse-ask-record", AskEventIn(sub).Insert(1, "\"agent_id\":\"agent-7\","), services: AsOrchestrator(sub));

        Assert.Empty(LedgerRows(sub));
    }

    [Fact]
    public void AMultiSelectAnswerJoinsItsLabelsAndAnUnansweredQuestionKeepsOnlyItsHeader()
    {
        var multi = new Rig();
        var none = new Rig();

        multi.Run("posttooluse-ask-record", AskEventIn(multi, "\"Which?\":\"a\"", "\"Which?\":[\"a\",\"b\"]"), services: AsOrchestrator(multi));
        none.Run("posttooluse-ask-record", AskEventIn(none, "{\"answers\":{\"Which?\":\"a\"}}", "{}"), services: AsOrchestrator(none));

        Assert.Equal("h: a, b", Assert.Single(LedgerRows(multi)).Member("text")!.Value.AsString());
        Assert.Equal("h", Assert.Single(LedgerRows(none)).Member("text")!.Value.AsString());
    }

    [Fact]
    public void AFailedWriteIsLoggedAndTheToolIsUnaffected()
    {
        var rig = new Rig();
        rig.Writer.Failing.Add("decisions.ask");

        var outcome = rig.Run("posttooluse-ask-record", AskEventIn(rig), services: AsOrchestrator(rig));

        Assert.Contains("NOT recorded", Encoding.UTF8.GetString(outcome.Stdout.Span), StringComparison.Ordinal);
        Assert.Equal(0, outcome.ExitCode);
        Assert.Single(rig.Errors.Records);
    }

    [Fact]
    public void ADirThatCannotBeEnsuredWritesNothingAndLogsOneError()
    {
        var blocked = Path.Combine(Path.GetTempPath(), "ahprobe-ask-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(blocked, "a file where a directory goes");
        var rig = new Rig(new Dictionary<string, string> { ["WRANGL_HIERARCHY_DIR"] = Path.Combine(blocked, "hier") });

        var outcome = rig.Run("posttooluse-ask-record", AskEventIn(rig), services: AsOrchestrator(rig));

        Assert.Contains("NOT recorded", Encoding.UTF8.GetString(outcome.Stdout.Span), StringComparison.Ordinal);
        Assert.Empty(rig.Writer.Calls);
        Assert.Single(rig.Errors.Records);
        File.Delete(blocked);
    }
}
