using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Time;
using Ah.Hooks.Lifecycle;
using Ah.Hooks.Plumbing;
using Ah.TestKit;

namespace Ah.Hooks.Tests;

public sealed class PermissionDeniedAlarmTests
{
    private const string Denied =
        "Permission for this action was denied by the Claude Code auto mode classifier. Reason: [Auto-Mode Bypass]. The action was blocked.";

    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    private static PermissionDeniedAlarm.Denial Payload(string? toolUseId, string command = "git cherry-pick abc", string reason = Denied, string session = "sess-impl") =>
        new(session, toolUseId, command, reason, "/wt/impl", "/t/x.jsonl", "Bash");

    private static JsonValue Row(string session, string? toolUseId, string command, TimeSpan age, bool mailed, string reasonKey = "Auto-Mode Bypass", string member = "impl") =>
        JsonValue.FromObject(new JsonObject
        {
            ["type"] = "watch-event",
            ["session_id"] = session,
            ["kind"] = "CLASSIFIER-DENIED",
            ["ts"] = Timestamps.ToIsoString(Now - age),
            ["mailed"] = mailed,
            ["tool_use_id"] = toolUseId is null ? JsonValue.Null : JsonValue.FromString(toolUseId),
            ["command"] = command,
            ["member"] = member,
            ["team"] = "beta",
            ["reason_key"] = reasonKey,
        });

    private static PermissionDeniedAlarm.Plan Member(PermissionDeniedAlarm.Denial d, params JsonValue[] rows) =>
        PermissionDeniedAlarm.Decide(rows, d, "impl", "beta", Now)!;

    private static string Text(JsonObject o, string key) => o.Get(key) is { IsText: true } v ? v.AsString() : string.Empty;

    [Fact]
    public void AMembersAlarmPointsTheLeadAtTheUserDirectedChangeRuleAndTheTopOrchestratorsOwnDenialDoesNot()
    {
        var member = Member(Payload("toolu_1"));
        var own = PermissionDeniedAlarm.Decide([], Payload("toolu_2"), null, null, Now)!;

        Assert.Contains("make that exact edit yourself and log it as a decision on the user's behalf", member.Text, StringComparison.Ordinal);
        Assert.Contains("skills/agent-team/reference/escalation.md", member.Text, StringComparison.Ordinal);
        Assert.Contains("in a pipeline run park the question", member.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("on the user's behalf", own.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("park the question", own.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void AMembersFirstDenialIsMailedWithTheCommandTheLabelAndTheDiagnoseHint()
    {
        var plan = Member(Payload("toolu_1"));

        Assert.True(plan.Mail);
        Assert.Equal(Paused + ". " + PermissionDeniedAlarm.UserDirectedHint + " Details: ah roster permissions diagnose impl --team beta --cwd \"/wt/impl\"", plan.Text);
        Assert.Equal("step-needs-user", plan.Event);
        Assert.Equal("impl", Text(plan.Data, "member"));
        Assert.Equal("beta", Text(plan.Data, "team"));
        Assert.Equal("sess-impl", Text(plan.Data, "session_id"));
        Assert.Equal("toolu_1", Text(plan.Data, "tool_use_id"));
        Assert.Equal("Auto-Mode Bypass", Text(plan.Data, "label"));
        Assert.Equal("git cherry-pick abc", Text(plan.Data, "command"));
        Assert.StartsWith("Permission for this action was denied", Text(plan.Data, "reason_text"), StringComparison.Ordinal);
        Assert.Equal(1, plan.Data["count"].AsNumber());
        Assert.False(plan.Data.ContainsKey("commands"));
        Assert.Equal("CLASSIFIER-DENIED", Text(plan.Row, "kind"));
    }

    private const string Paused = "impl paused on Bash: [Auto-Mode Bypass] — a tool call needs the user";

    private const string NoVerdictReason =
        "Permission for this action was denied: the response ended before its verdict arrived. You may try the action again once, as-is.";

    private static JsonValue NoVerdictRow(string session, string id, TimeSpan age, bool mailed)
    {
        var row = Row(session, id, "sed -n 1,5p x", age, mailed).AsObject();
        row["verdict"] = "no-verdict";
        return JsonValue.FromObject(row);
    }

    private static PermissionDeniedAlarm.Plan NoVerdict(string id, params JsonValue[] rows) =>
        PermissionDeniedAlarm.Decide(rows, Payload(id, "sed -n 1,5p x", NoVerdictReason), "impl", "beta", Now)!;

    [Fact]
    public void TheRowCarriesTheMembersRoleAndTheTopOrchestratorsDenialSaysOrchestrator()
    {
        var member = PermissionDeniedAlarm.Decide([], Payload("toolu_1"), "impl", "beta", Now, null, "implementor")!;
        var unknown = PermissionDeniedAlarm.Decide([], Payload("toolu_2"), "impl", "beta", Now)!;
        var top = PermissionDeniedAlarm.Decide([], Payload("toolu_3"), null, null, Now)!;

        Assert.Equal("implementor", Text(member.Row, "role"));
        Assert.Equal("unknown", Text(unknown.Row, "role"));
        Assert.Equal("orchestrator", Text(top.Row, "role"));
    }

    [Fact]
    public void TheMailNamesNeitherTheCauseNorTheRuleLineAndTheTopOrchestratorsOwnCarriesNoDetails()
    {
        var member = PermissionDeniedAlarm.Decide([], Payload("toolu_1"), "impl", "beta", Now, ("launch-file", "restart"), "implementor", "rule that would allow it: Bash(x y:*)")!;
        var top = PermissionDeniedAlarm.Decide([], Payload("toolu_2"), null, null, Now)!;

        Assert.Equal(Paused + ". " + PermissionDeniedAlarm.UserDirectedHint + " Details: ah roster permissions diagnose impl --team beta --cwd \"/wt/impl\"", member.Text);
        Assert.Equal("launch-file", Text(member.Data, "cause"));
        Assert.DoesNotContain("Details", top.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void AMoreCountIsShownBeforeTheDetailsLine()
    {
        var plan = PermissionDeniedAlarm.Decide(
            [Row("sess-impl", "toolu_a", "git a", TimeSpan.FromSeconds(20), mailed: false)], Payload("toolu_b", "git b"), "impl", "beta", Now)!;

        Assert.Equal(Paused + " (+1 more). " + PermissionDeniedAlarm.UserDirectedHint + " Details: ah roster permissions diagnose impl --team beta --cwd \"/wt/impl\"", plan.Text);
    }

    [Fact]
    public void ALabelledReasonIsADeniedVerdictWithTheLabelKeptAndMailed()
    {
        var plan = PermissionDeniedAlarm.Decide([], Payload("toolu_1", reason: "Reason: [Modify Shared Resources]. blocked"), "impl", "beta", Now, ("classifier-decision", "no fix"))!;

        Assert.True(plan.Mail);
        Assert.Equal("denied", Text(plan.Row, "verdict"));
        Assert.Equal("Modify Shared Resources", Text(plan.Row, "label"));
        Assert.StartsWith("impl paused on Bash: [Modify Shared Resources] — a tool call needs the user.", plan.Text, StringComparison.Ordinal);
        Assert.Null(RefusalVocabulary.FirstHit(plan.Text));
    }

    [Theory]
    [InlineData("The server-side auto mode classifier gave no verdict for this action. This is a transient failure of the check, not a judgment.")]
    [InlineData("  the server-side auto mode CLASSIFIER GAVE NO VERDICT; try again")]
    [InlineData("The server-side auto mode classifier gave no verdict (the response ended before its verdict arrived), so auto mode cannot determine the safety of SendMessage. This is a transient failure of the check, not a judgment about the action: a later response may get a verdict. You may try the action again once, as-is. Repeated attempts are slowed by a growing delay, and after 10 responses in a row without a verdict the turn stops.")]
    [InlineData("Classifier unavailable")]
    [InlineData("  classifier UNAVAILABLE \n")]
    public void AClassifierThatGaveNoVerdictIsANoVerdictNotADenialAndRaisesNoNeedsUserEvent(string reason)
    {
        var plan = PermissionDeniedAlarm.Decide([], Payload("toolu_n", "git status", reason), "impl", "beta", Now)!;

        Assert.False(plan.Mail);
        Assert.Equal("no-verdict", Text(plan.Row, "verdict"));
        Assert.Equal(PermissionDeniedAlarm.NoVerdictEventName, plan.Event);
        Assert.DoesNotContain("needs the user", plan.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Permission for this action was denied. Reason: [Irreversible Local Destruction] rm -rf is blocked.")]
    [InlineData("Permission for this action was denied: the classifier blocked it, with a verdict.")]
    [InlineData("Permission for this action was denied. Reason: [Irreversible Local Destruction] echo 'The server-side auto mode classifier gave no verdict' | sh")]
    [InlineData("[Unsafe] the response ended before its verdict arrived, quoted in a command: echo 'Permission for this action was denied: the response ended before its verdict arrived'")]
    [InlineData("Classifier unavailable for this command: it is blocked.")]
    [InlineData("Permission for this action was denied. Reason: [Unsafe] Classifier unavailable")]
    [InlineData("echo 'Classifier unavailable'")]
    public void NegativeARealDenialStillRaisesTheNeedsUserEvent(string reason)
    {
        var plan = PermissionDeniedAlarm.Decide([], Payload("toolu_d", "rm -rf x", reason), "impl", "beta", Now)!;

        Assert.True(plan.Mail);
        Assert.Equal("denied", Text(plan.Row, "verdict"));
        Assert.Equal(PermissionDeniedAlarm.EventName, plan.Event);
        Assert.Contains("needs the user", plan.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ACleanLabelTextKeepsTheMoreCountAndTheDetailsAndNamesTheTool()
    {
        var plan = PermissionDeniedAlarm.Decide(
            [Row("sess-impl", "toolu_a", "git a", TimeSpan.FromMinutes(20), mailed: true, reasonKey: "Data Exfiltration"), Row("sess-impl", "toolu_b", "git b", TimeSpan.FromMinutes(2), mailed: false, reasonKey: "Data Exfiltration")],
            Payload("toolu_c", "git c", reason: "[Data Exfiltration]"),
            "impl",
            "beta",
            Now)!;

        Assert.Equal("impl paused on Bash: [Data Exfiltration] — a tool call needs the user (+1 more). " + PermissionDeniedAlarm.UserDirectedHint + " Details: ah roster permissions diagnose impl --team beta --cwd \"/wt/impl\"", plan.Text);
    }

    [Fact]
    public void AnUnlabelledReasonIsNamedByItsKeyAndNoReasonSaysSo()
    {
        var prose = PermissionDeniedAlarm.Decide([], Payload("toolu_1", reason: "Auto mode could not evaluate this action"), "impl", "beta", Now)!;
        var none = PermissionDeniedAlarm.Decide([], Payload("toolu_2", reason: string.Empty), "impl", "beta", Now)!;

        Assert.Contains("Auto mode could not evaluate this action", prose.Text, StringComparison.Ordinal);
        Assert.Contains("(none given)", none.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ANoVerdictIsRecordedWithItsVerdictAndNotMailedAlone()
    {
        var plan = NoVerdict("toolu_1");

        Assert.False(plan.Mail);
        Assert.Equal("no-verdict", Text(plan.Row, "verdict"));
        Assert.Equal("CLASSIFIER-DENIED", Text(plan.Row, "kind"));
    }

    [Fact]
    public void TwoNoVerdictsAreStillNotMailedAndTheThirdWithinThirtyMinutesIsMailedOnceAsNotAnAhBug()
    {
        var two = NoVerdict("toolu_3", NoVerdictRow("sess-impl", "toolu_1", TimeSpan.FromMinutes(20), mailed: false));
        var three = NoVerdict(
            "toolu_3",
            NoVerdictRow("sess-impl", "toolu_1", TimeSpan.FromMinutes(25), mailed: false),
            NoVerdictRow("sess-impl", "toolu_2", TimeSpan.FromMinutes(10), mailed: false));

        Assert.False(two.Mail);
        Assert.True(three.Mail);
        Assert.Equal("impl: 3 responses ended early in 30 min (harness); not an ah bug.", three.Text);
        Assert.Equal("step-unfinished", three.Event);
        Assert.Null(RefusalVocabulary.FirstHit(three.Text));
    }

    [Fact]
    public void AFourthNoVerdictInTheWindowAfterAMailedOneIsNotMailedAndOldOnesDoNotCount()
    {
        var fourth = NoVerdict(
            "toolu_4",
            NoVerdictRow("sess-impl", "toolu_1", TimeSpan.FromMinutes(25), mailed: false),
            NoVerdictRow("sess-impl", "toolu_2", TimeSpan.FromMinutes(10), mailed: false),
            NoVerdictRow("sess-impl", "toolu_3", TimeSpan.FromMinutes(5), mailed: true));
        var stale = NoVerdict(
            "toolu_4",
            NoVerdictRow("sess-impl", "toolu_1", TimeSpan.FromMinutes(45), mailed: false),
            NoVerdictRow("sess-impl", "toolu_2", TimeSpan.FromMinutes(40), mailed: false));

        Assert.False(fourth.Mail);
        Assert.False(stale.Mail);
    }

    [Fact]
    public void NegativeNoVerdictsNeitherCoalesceTheDeniedAlarmsNorCountAsHeldDenials()
    {
        var plan = Member(Payload("toolu_b", "git b"), NoVerdictRow("sess-impl", "toolu_a", TimeSpan.FromSeconds(10), mailed: true));

        Assert.True(plan.Mail);
        Assert.Equal(1, plan.Data["count"].AsNumber());
    }

    [Theory]
    [InlineData("Reason: [Modify Shared Resources]. blocked", "Modify Shared Resources")]
    [InlineData("Reason: none given", "unknown")]
    [InlineData("Reason: []", "unknown")]
    [InlineData("", "unknown")]
    public void TheLabelIsTheBracketAfterReasonAndUnknownWhenThereIsNone(string reason, string label) =>
        Assert.Equal(label, PermissionDeniedAlarm.LabelOf(reason));

    [Fact]
    public void TheCommandAndTheReasonAreCapped()
    {
        var plan = Member(Payload("toolu_1", command: new string('x', 900), reason: "Reason: [A] " + new string('y', 1500)));

        Assert.Equal(400, Text(plan.Data, "command").Length);
        Assert.Equal(1000, Text(plan.Data, "reason_text").Length);
        Assert.DoesNotContain("xxxx", plan.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ADenialWhoseToolUseIdIsAlreadyOnRecordIsNotPlannedAgain()
    {
        var plan = PermissionDeniedAlarm.Decide([Row("sess-impl", "toolu_1", "git cherry-pick abc", TimeSpan.FromMinutes(5), mailed: true)], Payload("toolu_1"), "impl", "beta", Now);

        Assert.Null(plan);
    }

    [Fact]
    public void ADifferentToolUseIdOrAnotherSessionsIsNotADuplicate()
    {
        var rows = new[]
        {
            Row("sess-impl", "toolu_1", "git cherry-pick abc", TimeSpan.FromMinutes(5), mailed: true),
            Row("sess-other", "toolu_2", "git cherry-pick abc", TimeSpan.FromMinutes(5), mailed: true),
        };

        Assert.NotNull(PermissionDeniedAlarm.Decide(rows, Payload("toolu_2"), "impl", "beta", Now));
    }

    [Fact]
    public void WithoutAToolUseIdTheSameCommandWithinFiveSecondsIsOneDenial()
    {
        var recorded = Row("sess-impl", null, "git cherry-pick abc", TimeSpan.FromSeconds(3), mailed: true);

        Assert.Null(PermissionDeniedAlarm.Decide([recorded], Payload(null), "impl", "beta", Now));
    }

    [Fact]
    public void WithoutAToolUseIdTheSameCommandAfterFiveSecondsOrAnotherCommandIsANewDenial()
    {
        var later = Row("sess-impl", null, "git cherry-pick abc", TimeSpan.FromSeconds(6), mailed: true);
        var other = Row("sess-impl", null, "git rebase main", TimeSpan.FromSeconds(3), mailed: true);

        Assert.NotNull(PermissionDeniedAlarm.Decide([later], Payload(null), "impl", "beta", Now));
        Assert.NotNull(PermissionDeniedAlarm.Decide([other], Payload(null), "impl", "beta", Now));
    }

    [Fact]
    public void ADenialWithinFifteenMinutesOfTheLastMailForTheSameReasonIsHeldNotMailed()
    {
        var plan = Member(Payload("toolu_b", "git b"), Row("sess-impl", "toolu_a", "git a", TimeSpan.FromMinutes(14), mailed: true));

        Assert.False(plan.Mail);
        Assert.Equal("CLASSIFIER-DENIED", Text(plan.Row, "kind"));
    }

    [Fact]
    public void ADenialFifteenMinutesOrMoreAfterTheLastMailForTheSameReasonIsMailedAgain()
    {
        var plan = Member(Payload("toolu_b", "git b"), Row("sess-impl", "toolu_a", "git a", TimeSpan.FromMinutes(15), mailed: true));

        Assert.True(plan.Mail);
    }

    [Fact]
    public void ADifferentReasonKeyIsMailedAtOnceWhileTheFirstIsInItsWindow()
    {
        var plan = Member(
            Payload("toolu_b", "git b", reason: "[Data Exfiltration]"),
            Row("sess-impl", "toolu_a", "git a", TimeSpan.FromMinutes(1), mailed: true));

        Assert.True(plan.Mail);
        Assert.Equal("Data Exfiltration", Text(plan.Row, "reason_key"));
    }

    [Fact]
    public void ThePeerRowsOfAnotherMemberOrTeamDoNotHoldBackThisMembersMail()
    {
        var other = Row("sess-other", "toolu_a", "git a", TimeSpan.FromMinutes(1), mailed: true, member: "reviewer");

        Assert.True(Member(Payload("toolu_b", "git b"), other).Mail);
    }

    [Fact]
    public void ARespawnedSessionOfTheSameMemberIsHeldByTheWindowOfItsPredecessor()
    {
        var plan = Member(Payload("toolu_b", "git b", session: "sess-respawn"), Row("sess-impl", "toolu_a", "git a", TimeSpan.FromMinutes(2), mailed: true));

        Assert.False(plan.Mail);
    }

    [Fact]
    public void ADenialOfAnotherReasonDoesNotCountIntoTheHeldDenialsOfThisOne()
    {
        var plan = Member(
            Payload("toolu_c", "git c"),
            Row("sess-impl", "toolu_a", "git a", TimeSpan.FromMinutes(20), mailed: true),
            Row("sess-impl", "toolu_b", "git b", TimeSpan.FromMinutes(2), mailed: false, reasonKey: "Data Exfiltration"));

        Assert.Equal(1, plan.Data["count"].AsNumber());
    }

    [Theory]
    [InlineData("[Data Exfiltration]", "Data Exfiltration")]
    [InlineData("  [Data Exfiltration] trailing text", "Data Exfiltration")]
    [InlineData("Reason: [X]", "X")]
    [InlineData("see [X] here", "unknown")]
    [InlineData("Auto mode could not evaluate this action", "unknown")]
    public void TheLabelIsTheLeadingBracketOrTheOneAfterReason(string reason, string label) =>
        Assert.Equal(label, PermissionDeniedAlarm.LabelOf(reason));

    [Theory]
    [InlineData("[Data Exfiltration]", "Data Exfiltration")]
    [InlineData("Auto mode could not   evaluate\nthis action ", "Auto mode could not evaluate this action")]
    [InlineData("", "(none given)")]
    [InlineData("   ", "(none given)")]
    public void TheReasonKeyIsTheLabelElseTheCollapsedReasonElseNoneGiven(string reason, string key) =>
        Assert.Equal(key, PermissionDeniedAlarm.ReasonKeyOf(reason));

    [Fact]
    public void TheReasonKeyOfAnUnlabelledReasonIsCappedAtTwoHundredCharacters() =>
        Assert.Equal(200, PermissionDeniedAlarm.ReasonKeyOf(new string('q', 900)).Length);

    [Fact]
    public void AnEventAfterTheWindowMergesTheHeldDenialsIntoItsCountAndItsCommands()
    {
        var plan = Member(
            Payload("toolu_c", "git c"),
            Row("sess-impl", "toolu_a", "git a", TimeSpan.FromMinutes(16), mailed: true),
            Row("sess-impl", "toolu_b", "git b", TimeSpan.FromMinutes(1), mailed: false));

        Assert.True(plan.Mail);
        Assert.Equal(2, plan.Data["count"].AsNumber());
        Assert.Equal(["git b", "git c"], plan.Data["commands"].AsArray().Select(static v => v.AsString()).ToArray());
        Assert.Contains("(+1 more)", plan.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCommandsListKeepsTheLastFive()
    {
        var held = Enumerable.Range(1, 7).Select(i => Row("sess-impl", $"t{i}", $"git {i}", TimeSpan.FromSeconds(100 - i), mailed: false)).ToArray();

        var plan = Member(Payload("t8", "git 8"), held);

        Assert.Equal(8, plan.Data["count"].AsNumber());
        Assert.Equal(["git 4", "git 5", "git 6", "git 7", "git 8"], plan.Data["commands"].AsArray().Select(static v => v.AsString()).ToArray());
    }

    [Fact]
    public void TheTopOrchestratorsOwnDenialIsRecordedAndNeverMailed()
    {
        var plan = PermissionDeniedAlarm.Decide([], Payload("toolu_1", session: "sess-orch"), null, null, Now)!;

        Assert.False(plan.Mail);
        Assert.Equal("orchestrator", Text(plan.Row, "member"));
        Assert.False(plan.Row["mailed"].AsBool());
    }

    [Fact]
    public void AMemberWithNoTeamIsRecordedButNotMailed()
    {
        Assert.False(PermissionDeniedAlarm.Decide([], Payload("toolu_1"), "impl", null, Now)!.Mail);
    }

    [Theory]
    [InlineData("SendMessage", """{"to":"orchestrator@tm","message":"x"}""", "Classifier unavailable", "no-verdict")]
    [InlineData("Bash", """{"command":"git status"}""", "Classifier unavailable", "no-verdict")]
    [InlineData("SendMessage", """{"to":"orchestrator@tm","message":"x"}""", "Classifier unavailable for this call: blocked", "denied")]
    [InlineData("Bash", """{"command":"git status"}""", "Reason: [Unsafe] Classifier unavailable", "denied")]
    public void TheHookRecordsAClassifierThatWasUnavailableAsNoVerdictForEveryToolAndOtherReasonsAsDenied(string tool, string input, string reason, string verdict)
    {
        var rig = new HooksHostTests.Rig();

        var services = rig.Services with { Advisory = StopOrchestratorLivenessTests.Advisory(rig, []) };

        _ = PermissionDeniedAlarm.Run(new GateContext(services, JsonText.Parse(Payload("s1", "t1", tool, input, reason)), "permission-denied-alarm"));

        var rows = rig.Writer.Calls.Select((c, i) => (c, i)).Where(static x => x.c == "peer-records.append")
            .Select(x => JsonText.Parse(rig.Writer.Arguments[x.i]).Member("rec")!.Value)
            .Where(static r => r.Member("kind") is { IsText: true } k && k.AsString() == PermissionDeniedAlarm.Kind)
            .ToList();
        var row = Assert.Single(rows);
        Assert.Equal(verdict, row.Member("verdict")!.Value.AsString());
        Assert.Equal(tool, row.Member("tool_name")!.Value.AsString());
    }

    [Fact]
    public void ADenialEmitsNothingIntoTheDeniedSessionsContext()
    {
        var rig = new HooksHostTests.Rig();
        var cwd = rig.Sandbox.Root + "/proj";

        var outcome = PermissionDeniedAlarm.Run(new GateContext(
            rig.Services,
            JsonText.Parse($$"""{"hook_event_name":"PermissionDenied","session_id":"s1","cwd":"{{cwd}}","tool_name":"Bash","tool_input":{"command":"x"},"tool_use_id":"t9","reason":"Reason: [Security Weaken]"}"""),
            "permission-denied-alarm"));

        Assert.True(outcome.ExitCode == 0 && outcome.Stdout.IsEmpty);
    }

    private static string Payload(string session, string toolUseId, string tool, string input, string reason) =>
        $$"""{"hook_event_name":"PermissionDenied","session_id":"{{session}}","cwd":"/wt","tool_name":"{{tool}}","tool_input":{{input}},"tool_use_id":"{{toolUseId}}","reason":"{{reason}}"}""";

    [Fact]
    public void EveryDenialIsWrittenToHookErrorsWithItsFullReasonAndToolInputIncludingDuplicatesAndSubagents()
    {
        var rig = new HooksHostTests.Rig();
        HookOutcome Run(string json) => PermissionDeniedAlarm.Run(new GateContext(rig.Services, JsonText.Parse(json), "permission-denied-alarm"));

        _ = Run(Payload("s1", "t1", "Write", """{"file_path":"/x/y.cs","content":"abc"}""", "[Data Exfiltration]"));
        _ = Run(Payload("s1", "t1", "Write", """{"file_path":"/x/y.cs","content":"abc"}""", "[Data Exfiltration]"));
        _ = Run(Payload("s1", "t2", "Bash", """{"command":"ls"}""", "[Irreversible Local Destruction]").Replace("\"cwd\"", "\"agent_id\":\"a7\",\"cwd\""));

        var records = rig.Errors.Records.Where(r => r.Hook == "permission-denied-alarm").ToList();
        Assert.Equal(3, records.Count);
        Assert.All(records, r => Assert.Equal("PermissionDenied", r.EventName));
        Assert.Contains("denied Write: reason=\"[Data Exfiltration]\" tool_input=", records[0].Message, StringComparison.Ordinal);
        Assert.Contains("/x/y.cs", records[0].Message, StringComparison.Ordinal);
        Assert.Equal("s1", records[2].SessionId);
        Assert.Contains("Irreversible Local Destruction", records[2].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheHookErrorRecordCapsTheReasonAtAThousandAndTheToolInputAtTwoThousandCharacters()
    {
        var rig = new HooksHostTests.Rig();

        _ = PermissionDeniedAlarm.Run(new GateContext(
            rig.Services,
            JsonText.Parse(Payload("s1", "t1", "Write", "{\"content\":\"" + new string('z', 5000) + "\"}", new string('r', 3000))),
            "permission-denied-alarm"));

        var message = Assert.Single(rig.Errors.Records, r => r.Hook == "permission-denied-alarm").Message;
        var reason = message[(message.IndexOf("reason=", StringComparison.Ordinal) + 7)..message.IndexOf(" tool_input=", StringComparison.Ordinal)];
        var input = message[(message.IndexOf(" tool_input=", StringComparison.Ordinal) + 12)..];
        Assert.InRange(reason.Length, 900, 1010);
        Assert.InRange(input.Length, 1900, 2010);
        Assert.DoesNotContain(new string('z', 2100), message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADenialWithNoReasonIsWrittenAsNoneGiven()
    {
        var rig = new HooksHostTests.Rig();

        _ = PermissionDeniedAlarm.Run(new GateContext(
            rig.Services,
            JsonText.Parse("""{"hook_event_name":"PermissionDenied","session_id":"s1","cwd":"/wt","tool_name":"Bash","tool_input":{"command":"x"},"tool_use_id":"t1"}"""),
            "permission-denied-alarm"));

        Assert.Contains("reason=(none given)", Assert.Single(rig.Errors.Records, r => r.Hook == "permission-denied-alarm").Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEventOtherThanPermissionDeniedWritesNoHookErrorRecord()
    {
        var rig = new HooksHostTests.Rig();

        _ = PermissionDeniedAlarm.Run(new GateContext(rig.Services, JsonText.Parse("""{"hook_event_name":"PostToolUse","session_id":"s1","cwd":"/wt"}"""), "permission-denied-alarm"));

        Assert.DoesNotContain(rig.Errors.Records, r => r.Hook == "permission-denied-alarm");
    }

    [Fact]
    public void ANonShellToolIsNamedWithItsFirstFilePathPathUrlOrPatternAndAToolWithNoneByItsNameAlone()
    {
        static string Of(string tool, string input) => PermissionDeniedAlarm.CommandOf(JsonText.Parse("{\"tool_input\":" + input + "}"), tool);

        Assert.Equal("Write /a/b.cs", Of("Write", """{"file_path":"/a/b.cs","path":"/p"}"""));
        Assert.Equal("Glob /p", Of("Glob", """{"path":"/p","pattern":"zz"}"""));
        Assert.Equal("WebFetch http://h/u", Of("WebFetch", """{"url":"http://h/u","pattern":"zz"}"""));
        Assert.Equal("Grep zz", Of("Grep", """{"pattern":"zz"}"""));
        Assert.Equal("Write", Of("Write", """{"other":1}"""));
        Assert.Equal("ls -l", Of("Bash", """{"command":"ls -l","file_path":"/a"}"""));
    }

    [Fact]
    public void TheHookReadsTheSubagentAndOtherEventsAsNoAction()
    {
        var rig = new HooksHostTests.Rig();
        var cwd = rig.Sandbox.Root + "/proj";

        HookOutcome Run(string json) => PermissionDeniedAlarm.Run(new GateContext(rig.Services, JsonText.Parse(json), "permission-denied-alarm"));

        var subagent = Run($$"""{"hook_event_name":"PermissionDenied","session_id":"s1","agent_id":"a7","cwd":"{{cwd}}","tool_name":"Bash","tool_input":{"command":"x"},"tool_use_id":"t1"}""");
        var other = Run($$"""{"hook_event_name":"PostToolUse","session_id":"s1","cwd":"{{cwd}}"}""");
        var noSession = Run($$"""{"hook_event_name":"PermissionDenied","cwd":"{{cwd}}"}""");

        Assert.True(subagent.ExitCode == 0 && subagent.Stdout.IsEmpty);
        Assert.True(other.ExitCode == 0 && other.Stdout.IsEmpty);
        Assert.True(noSession.ExitCode == 0 && noSession.Stdout.IsEmpty);
    }
}
