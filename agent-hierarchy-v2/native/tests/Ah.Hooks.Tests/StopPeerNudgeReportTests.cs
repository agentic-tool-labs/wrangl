using System.Text;
using Ah.Core.Json;
using Ah.Hierarchy;
using Ah.Mesh;

namespace Ah.Hooks.Tests;

/// <summary>An owed report is satisfied by the report entry already sitting in the sender's box, and a written but unsent one is told to be sent.</summary>
public sealed class StopPeerNudgeReportTests
{
    private const string Session = "sess-1";
    private const string Brief = "/h/msgs/20260101-000000-aa11--implementor--t1--request.md";
    private const string Response = "/h/msgs/20260101-000000-aa11--orchestrator--t1--response.md";

    internal sealed class FakeHooks(ReportState state, bool mailed, List<string?>? scans = null, string? briefPath = null) : IMailboxHooks
    {
        public HookAckResult AckFromPrompt(string? sessionId, string? prompt) => throw new NotSupportedException();

        public IReadOnlyList<string> AckFromSendMessage(string? sessionId, string? recipient, string? message, Ah.Abstractions.RequestContext context) =>
            throw new NotSupportedException();

        public Ah.Mesh.RelayClaimDecision ClaimRelay(string? sessionId, string? recipient, string? message, Ah.Abstractions.RequestContext context, string? toolUseId = null) =>
            throw new NotSupportedException();

        public string? PointerDuplicate(string? message, Ah.Abstractions.RequestContext context) => throw new NotSupportedException();

        public string? SendDenial(string? message, string? sessionId, Ah.Abstractions.RequestContext context, out string? warning) => throw new NotSupportedException();

        public IReadOnlyList<string> ConfirmRelay(string? sessionId, string? recipient, string? message, Ah.Mesh.RelayResult result, string? reason, Ah.Abstractions.RequestContext context, string? msgId = null) =>
            throw new NotSupportedException();

        public IReadOnlyList<string> WithdrawRelay(string? sessionId, string? recipient, string? message, string? toolUseId, Ah.Abstractions.RequestContext context) =>
            throw new NotSupportedException();

        public IReadOnlyList<string> AckFromTranscript(string? sessionId, string? transcriptPath)
        {
            scans?.Add(sessionId + "|" + transcriptPath);
            return [];
        }

        public FullMail FullMailText(string? sessionId, CompactMail compact) => throw new NotSupportedException();

        public IReadOnlyList<OpenBrief> OpenMailBriefs(string? sessionId) => mailed ? [new OpenBrief("e1", briefPath ?? Brief, "orchestrator@tm")] : [];

        public IReadOnlyList<OpenBrief> OwedBriefs(string? sessionId) => throw new NotSupportedException();

        public MailboxSummary Summary(double? nowMs = null) => throw new NotSupportedException();

        public bool ReportDelivered(string? sessionId, string responsePath) => throw new NotSupportedException();

        public Func<string, bool> ReportsDelivered(string? sessionId) => throw new NotSupportedException();

        public Func<string, string, bool> ReportReached(string? sessionId) => throw new NotSupportedException();

        public Func<string, string, bool> ReportReached(string? sessionId, IReachTick tick, IReadOnlyList<Ah.Core.Json.JsonValue>? sessionPeerRecords = null) => throw new NotSupportedException();

        public IReachTick BeginReach() => throw new NotSupportedException();

        public Func<string, bool> SameOrchestrator(string? sessionId) => throw new NotSupportedException();

        public IReadOnlyList<string> OrchestratedTeams(string? sessionId) => throw new NotSupportedException();

        public bool WrittenByOrchestrator(string responsePath) => throw new NotSupportedException();

        public bool EventApplies(string? sessionId, JsonValue entry) => throw new NotSupportedException();

        public void ResolveEvent(string dir, string id, string by, string? sessionId) => throw new NotSupportedException();

        public ReportState ReportStateOf(string? sessionId, OpenBrief brief, out string? responsePath)
        {
            responsePath = state == ReportState.NotWritten ? null : Response;
            return state;
        }
    }

    internal static string Stop(HooksHostTests.Rig rig, ReportState state, bool mailed = true, string? transcript = null, List<string?>? scans = null, IReadOnlyList<CallerBox>? owned = null, string? briefPath = null, bool reStop = false, string? agentType = null, IMailbox? mailbox = null)
    {
        var advisory = new AdvisoryServices(
            mailbox ?? BindingAttestationTests.MailboxFor(rig), new FakeHooks(state, mailed, scans, briefPath), new BindingAttestationTests.Callers(owned ?? []), null!, null!, StopDirective.Instance, null!, null!, new BindingAttestationTests.Daemon(), null!, null!);
        var services = rig.Services with { Advisory = advisory };
        var e = new JsonObject { ["hook_event_name"] = "Stop", ["session_id"] = Session, ["cwd"] = rig.Sandbox.Root + "/proj" };
        if (agentType is not null)
        {
            e["agent_type"] = agentType;
        }
        if (transcript is not null)
        {
            e["transcript_path"] = transcript;
        }

        e["stop_hook_active"] = reStop;
        var outcome = rig.Run("stop-peer-nudge", Encoding.UTF8.GetString(JsonText.ToUtf8(e, 0)), services: () => services);
        return Encoding.UTF8.GetString(outcome.Stdout.Span);
    }

    private static void OwedBySendMessage(HooksHostTests.Rig rig, string? briefPath = null, int nudges = 0)
    {
        var pending = rig.Services.Hierarchy.Peers.PeerPendingPath();
        _ = Directory.CreateDirectory(Path.GetDirectoryName(pending)!);
        var row = new JsonObject
        {
            ["session_id"] = Session,
            ["from"] = "orchestrator@tm",
            ["from_name"] = "orchestrator",
            ["reply_to"] = JsonValue.Null,
            ["task"] = "20260101-000000-aa11",
            ["status"] = "pending",
            ["nudges"] = nudges,
            ["msg"] = briefPath ?? Brief,
            ["ts"] = "2026-01-01T00:00:00.000Z",
        };
        File.AppendAllText(pending, Encoding.UTF8.GetString(JsonText.ToUtf8(row, 0)) + "\n");
    }

    internal static void ChainDownRow(HooksHostTests.Rig rig, string session, string state = "active")
    {
        var pending = rig.Services.Hierarchy.Peers.PeerPendingPath();
        _ = Directory.CreateDirectory(Path.GetDirectoryName(pending)!);
        var row = new JsonObject
        {
            ["type"] = "chain-down",
            ["state"] = state,
            ["session_id"] = session,
            ["member"] = "impl",
            ["team"] = "tm",
            ["link"] = "tm",
            ["why"] = "orphaned",
            ["at"] = "2026-01-01T00:00:00.000Z",
        };
        File.AppendAllText(pending, Encoding.UTF8.GetString(JsonText.ToUtf8(row, 0)) + "\n");
    }

    [Fact]
    public void AnActiveChainDownRowSilencesTheOwedReportNudge()
    {
        var rig = new HooksHostTests.Rig();
        ChainDownRow(rig, Session);

        Assert.Equal(string.Empty, Stop(rig, ReportState.NotWritten));
    }

    [Fact]
    public void AChainDownRowOfAnotherSessionDoesNotSilenceTheNudge()
    {
        var rig = new HooksHostTests.Rig();
        ChainDownRow(rig, "sess-other");

        Assert.Contains("its report is not written yet", Stop(rig, ReportState.NotWritten), StringComparison.Ordinal);
    }

    [Fact]
    public void AChainDownRowThatIsNoLongerActiveDoesNotSilenceTheNudge()
    {
        var rig = new HooksHostTests.Rig();
        ChainDownRow(rig, Session);
        ChainDownRow(rig, Session, "resolved");

        Assert.Contains("its report is not written yet", Stop(rig, ReportState.NotWritten), StringComparison.Ordinal);
    }

    [Fact]
    public void AMailedBriefWhoseReportIsInTheSendersBoxIsNotNudged()
    {
        var rig = new HooksHostTests.Rig();

        Assert.Equal(string.Empty, Stop(rig, ReportState.Sent));
    }

    [Fact]
    public void AWrittenButUnsentReportOfASessionWhoseTeamIsHeldIsNotNudged()
    {
        var rig = HoldFixture.Rig();
        HoldFixture.Orchestrates(rig, Session, "tm");
        HoldFixture.Hold(rig, "tm");

        Assert.Equal(string.Empty, Stop(rig, ReportState.WrittenNotSent));
    }

    [Fact]
    public void AWrittenButUnsentReportIsStillNudgedWhenOnlyAnotherSessionsTeamIsHeld()
    {
        var rig = HoldFixture.Rig();
        HoldFixture.Orchestrates(rig, Session, "tm");
        HoldFixture.Orchestrates(rig, "sess-other", "tm2");
        HoldFixture.Hold(rig, "tm2");

        Assert.Contains("report written, not sent yet", Stop(rig, ReportState.WrittenNotSent), StringComparison.Ordinal);
    }

    [Fact]
    public void AWrittenButUnsentReportGetsTheSendItNudge()
    {
        var rig = new HooksHostTests.Rig();

        var output = Stop(rig, ReportState.WrittenNotSent);

        Assert.Contains("report written, not sent yet: run ah msg send orchestrator@tm --kind report --ref 20260101-000000-aa11", output, StringComparison.Ordinal);
        Assert.DoesNotContain("is not written yet", output, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnwrittenReportKeepsTheOldNudge()
    {
        var rig = new HooksHostTests.Rig();

        // A member's owed brief still blocks; an Orchestrator's goes to the digest (StopPeerNudgeDigestTests).
        var output = Stop(rig, ReportState.NotWritten, agentType: "wrangl:implementor");

        Assert.Contains("its report is not written yet", output, StringComparison.Ordinal);
        Assert.DoesNotContain("written but not sent", output, StringComparison.Ordinal);
    }

    [Fact]
    public void ASendMessagePathObligationIsSatisfiedByAMailedReport()
    {
        var rig = new HooksHostTests.Rig();
        OwedBySendMessage(rig);

        _ = Stop(rig, ReportState.Sent, mailed: false);

        Assert.Contains(
            rig.Writer.Arguments,
            a => a.Contains("\"status\":\"resolved\"", StringComparison.Ordinal) && a.Contains("20260101-000000-aa11", StringComparison.Ordinal));
    }

    private static void OwesARelay(HooksHostTests.Rig rig, string claimState, string holder = "")
    {
        var box = rig.Sandbox.Root + "/box";
        _ = Directory.CreateDirectory(box + "/claims");
        File.WriteAllText(box + "/claims/e1.json", "{\"v\":1,\"id\":\"e1\",\"state\":\"" + claimState + "\",\"carrier\":\"relay\"" + holder + "}\n");
        var pending = rig.Services.Hierarchy.Peers.PeerPendingPath();
        _ = Directory.CreateDirectory(Path.GetDirectoryName(pending)!);
        var row = new JsonObject
        {
            ["session_id"] = Session,
            ["task"] = "relay:e1",
            ["status"] = "pending",
            ["nudges"] = 0,
            ["relay"] = new JsonObject { ["entry"] = "e1", ["address"] = "m1@tm", ["dir"] = box, ["to"] = "m1", ["line"] = "hello [ah-entry e1 to m1@tm]" },
            ["ts"] = "2026-01-01T00:00:00.000Z",
        };
        File.AppendAllText(pending, Encoding.UTF8.GetString(JsonText.ToUtf8(row, 0)) + "\n");
    }

    [Theory]
    [InlineData("relay-claimed")]
    [InlineData("relay-sent")]
    [InlineData("relay-failed")]
    [InlineData("fallback-claimed")]
    [InlineData("delivered")]
    public void ARelayObligationIsMetOnceTheEntrysClaimIsPastPending(string claimState)
    {
        var rig = new HooksHostTests.Rig();
        OwesARelay(rig, claimState);

        _ = Stop(rig, ReportState.Sent, mailed: false);

        Assert.Contains(rig.Writer.Arguments, a => a.Contains("\"status\":\"resolved\"", StringComparison.Ordinal) && a.Contains("relay:e1", StringComparison.Ordinal));
    }

    [Fact]
    public void ARelayObligationWhoseClaimIsStillPendingKeepsNudging()
    {
        var rig = new HooksHostTests.Rig();
        OwesARelay(rig, "pending");

        var output = Stop(rig, ReportState.Sent, mailed: false);

        Assert.Contains("block", output, StringComparison.Ordinal);
        Assert.DoesNotContain(rig.Writer.Arguments, a => a.Contains("\"status\":\"resolved\"", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("delivered", "", "delivered")]
    [InlineData("superseded", ",\"by\":\"e2\"", "superseded e2")]
    [InlineData("declined", ",\"by\":\"orch@tm\",\"reason\":\"withdrawn\"", "declined withdrawn")]
    public void ARelayObligationIsMetOnceTheEntrysLadderHasEnded(string state, string extra, string note)
    {
        var rig = new HooksHostTests.Rig();
        OwesARelay(rig, "pending");
        File.WriteAllText(rig.Sandbox.Root + "/box/attempts.jsonl", "{\"id\":\"e1\",\"ladder\":true,\"seq\":1,\"rung\":3,\"state\":\"" + state + "\"" + extra + "}\n");

        _ = Stop(rig, ReportState.Sent, mailed: false);

        Assert.Contains(rig.Writer.Arguments, a => a.Contains("\"status\":\"resolved\"", StringComparison.Ordinal) && a.Contains("relay:e1", StringComparison.Ordinal) && a.Contains($"\"void\":\"{note}\"", StringComparison.Ordinal));
    }

    [Fact]
    public void ADeclinedRelayIsRecordedAndNeverBlocksTheStopAndASupersededOneIsLikeIt()
    {
        var declined = new HooksHostTests.Rig();
        OwesARelay(declined, "pending");
        File.WriteAllText(declined.Sandbox.Root + "/box/attempts.jsonl", "{\"id\":\"e1\",\"ladder\":true,\"seq\":1,\"rung\":3,\"state\":\"declined\",\"by\":\"orch@tm\",\"reason\":\"withdrawn\"}\n");
        var superseded = new HooksHostTests.Rig();
        OwesARelay(superseded, "pending");
        File.WriteAllText(superseded.Sandbox.Root + "/box/attempts.jsonl", "{\"id\":\"e1\",\"ladder\":true,\"seq\":1,\"rung\":3,\"state\":\"superseded\",\"by\":\"e2\"}\n");

        var afterDecline = Stop(declined, ReportState.Sent, mailed: false);
        var afterSupersede = Stop(superseded, ReportState.Sent, mailed: false);

        Assert.DoesNotContain("declined", afterDecline, StringComparison.Ordinal);
        Assert.Contains(declined.Writer.Arguments, a => a.Contains("\"status\":\"resolved\"", StringComparison.Ordinal) && a.Contains("\"void\":\"declined", StringComparison.Ordinal));
        Assert.DoesNotContain("declined", afterSupersede, StringComparison.Ordinal);
    }

    [Fact]
    public void ARelayShownAsOwedThenAckedIsResolvedByTheNextStop()
    {
        var rig = new HooksHostTests.Rig();
        OwesARelay(rig, "pending");

        var owed = Stop(rig, ReportState.Sent, mailed: false);
        var resolvedBeforeAck = rig.Writer.Arguments.Count(a => a.Contains("\"status\":\"resolved\"", StringComparison.Ordinal));
        _ = Directory.CreateDirectory(rig.Sandbox.Root + "/box/acks");
        File.WriteAllText(rig.Sandbox.Root + "/box/acks/e1", "{\"by\":\"prompt-hook\",\"at\":\"2026-01-01T00:00:05.000Z\"}\n");
        _ = Stop(rig, ReportState.Sent, mailed: false);

        Assert.Contains("mail entry e1", owed, StringComparison.Ordinal);
        Assert.Equal(0, resolvedBeforeAck);
        Assert.Contains(rig.Writer.Arguments, a => a.Contains("\"status\":\"resolved\"", StringComparison.Ordinal) && a.Contains("relay:e1", StringComparison.Ordinal) && a.Contains("\"void\":\"acked 2026-01-01T00:00:05.000Z prompt-hook\"", StringComparison.Ordinal));
    }

    [Fact]
    public void ADeclinedRelaySpendsNoNudgeOfTheCarrier()
    {
        var rig = new HooksHostTests.Rig();
        OwesARelay(rig, "pending");
        File.WriteAllText(rig.Sandbox.Root + "/box/attempts.jsonl", "{\"id\":\"e1\",\"ladder\":true,\"seq\":1,\"rung\":3,\"state\":\"declined\",\"by\":\"orch@tm\",\"reason\":\"withdrawn\"}\n");

        var output = Stop(rig, ReportState.Sent, mailed: false);

        Assert.DoesNotContain("declined", output, StringComparison.Ordinal);
        Assert.DoesNotContain(rig.Writer.Arguments, a => a.Contains("mail-nudge", StringComparison.Ordinal));
        Assert.Contains(rig.Writer.Arguments, a => a.Contains("\"status\":\"resolved\"", StringComparison.Ordinal));
    }

    [Fact]
    public void ARelayObligationWhoseLadderIsStillRunningKeepsNudging()
    {
        var rig = new HooksHostTests.Rig();
        OwesARelay(rig, "pending");
        File.WriteAllText(rig.Sandbox.Root + "/box/attempts.jsonl", "{\"id\":\"e1\",\"ladder\":true,\"seq\":1,\"rung\":3,\"state\":\"sending\"}\n");

        var output = Stop(rig, ReportState.Sent, mailed: false);

        Assert.Contains("block", output, StringComparison.Ordinal);
        Assert.DoesNotContain(rig.Writer.Arguments, a => a.Contains("\"status\":\"resolved\"", StringComparison.Ordinal));
    }

    [Fact]
    public void ASendMessagePathObligationWithNoReportSentStillNudges()
    {
        var rig = new HooksHostTests.Rig();
        OwedBySendMessage(rig);

        var output = Stop(rig, ReportState.WrittenNotSent, mailed: false);

        Assert.Contains("block", output, StringComparison.Ordinal);
        Assert.DoesNotContain(rig.Writer.Arguments, a => a.Contains("\"status\":\"resolved\"", StringComparison.Ordinal));
    }

    [Fact]
    public void AnOwedReportOnAUserDrivenTurnSpendsOneNudgeOfTheSameBudgetAnArmedTurnUses()
    {
        var rig = new HooksHostTests.Rig();
        OwedBySendMessage(rig);

        var output = Stop(rig, ReportState.NotWritten, mailed: false);

        Assert.Contains("ah: your report to orchestrator has not been sent yet:", output, StringComparison.Ordinal);
        Assert.DoesNotContain("last reminder", output, StringComparison.Ordinal);
        Assert.Contains(rig.Writer.Arguments, a => a.Contains("\"nudges\":1", StringComparison.Ordinal));
    }

    [Fact]
    public void TheNudgeThatSpendsTheLastOfTheBudgetSaysSoAndTheNextStopWaivesTheReportWithoutABlock()
    {
        var last = new HooksHostTests.Rig();
        OwedBySendMessage(last, nudges: 1);
        var spent = new HooksHostTests.Rig();
        OwedBySendMessage(spent, nudges: 2);

        var finalNudge = Stop(last, ReportState.NotWritten, mailed: false);
        var afterBudget = Stop(spent, ReportState.NotWritten, mailed: false);

        Assert.Contains("This is the last reminder; after it, the report is marked not delivered.", finalNudge, StringComparison.Ordinal);
        Assert.DoesNotContain("has not been sent yet", afterBudget, StringComparison.Ordinal);
        Assert.Contains(spent.Writer.Arguments, a => a.Contains("\"status\":\"waived\"", StringComparison.Ordinal));
    }

    private static string Holder(string session, string toolUse) => ",\"holder\":{\"session_id\":\"" + session + "\",\"tool_use_id\":\"" + toolUse + "\"}";

    private static string Transcript(HooksHostTests.Rig rig, params string[] rows)
    {
        var path = rig.Sandbox.Root + "/transcript.jsonl";
        File.WriteAllLines(path, rows);
        return path;
    }

    private static string ResultRow(string toolUse, string content, bool isError) =>
        "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"" + toolUse
        + "\",\"content\":\"" + content + "\",\"is_error\":" + (isError ? "true" : "false") + "}]}}";

    private static bool Claimed(HooksHostTests.Rig rig, string action) =>
        rig.Writer.Arguments.Any(a => a.Contains("\"action\":\"" + action + "\"", StringComparison.Ordinal) && a.Contains("\"id\":\"e1\"", StringComparison.Ordinal));

    [Fact]
    public void ACallTheTranscriptShowsDeniedFailsTheClaimItTook()
    {
        var rig = new HooksHostTests.Rig();
        OwesARelay(rig, "relay-claimed", Holder(Session, "toolu_1"));

        _ = Stop(rig, ReportState.Sent, mailed: false, transcript: Transcript(rig, ResultRow("toolu_1", "hook denied the call", isError: true)));

        Assert.True(Claimed(rig, "fail"));
        Assert.False(Claimed(rig, "confirm"));
    }

    [Fact]
    public void ACallTheTranscriptShowsSucceededConfirmsTheClaim()
    {
        var rig = new HooksHostTests.Rig();
        OwesARelay(rig, "relay-claimed", Holder(Session, "toolu_1"));

        _ = Stop(rig, ReportState.Sent, mailed: false, transcript: Transcript(rig, ResultRow("toolu_1", "Message delivered to that session inbox", isError: false)));

        Assert.True(Claimed(rig, "confirm"));
        Assert.False(Claimed(rig, "fail"));
    }

    [Fact]
    public void ATranscriptWithNoResultForTheToolCallMovesNothing()
    {
        var rig = new HooksHostTests.Rig();
        OwesARelay(rig, "relay-claimed", Holder(Session, "toolu_1"));

        _ = Stop(rig, ReportState.Sent, mailed: false, transcript: Transcript(rig, ResultRow("toolu_9", "hook denied the call", isError: true)));

        Assert.False(Claimed(rig, "fail"));
        Assert.False(Claimed(rig, "confirm"));
    }

    [Fact]
    public void AClaimAnotherSessionHoldsIsNotSettledFromThisTranscript()
    {
        var rig = new HooksHostTests.Rig();
        OwesARelay(rig, "relay-claimed", Holder("sess-other", "toolu_1"));

        _ = Stop(rig, ReportState.Sent, mailed: false, transcript: Transcript(rig, ResultRow("toolu_1", "hook denied the call", isError: true)));

        Assert.False(Claimed(rig, "fail"));
    }

    [Fact]
    public void AClaimAlreadySentIsNotSettledAgainFromTheTranscript()
    {
        var rig = new HooksHostTests.Rig();
        OwesARelay(rig, "relay-sent", Holder(Session, "toolu_1"));

        _ = Stop(rig, ReportState.Sent, mailed: false, transcript: Transcript(rig, ResultRow("toolu_1", "hook denied the call", isError: true)));

        Assert.False(Claimed(rig, "fail"));
    }

    private static string SendRow(string toolUse, string to, string message) =>
        "{\"type\":\"assistant\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"tool_use\",\"id\":\"" + toolUse
        + "\",\"name\":\"SendMessage\",\"input\":{\"to\":\"" + to + "\",\"message\":\"" + message + "\"}}]}}";

    private static bool RelayResolved(HooksHostTests.Rig rig) =>
        rig.Writer.Arguments.Any(a => a.Contains("\"status\":\"resolved\"", StringComparison.Ordinal) && a.Contains("relay:e1", StringComparison.Ordinal));

    [Fact]
    public void ARelayObligationIsMetByTheCarriersOwnSuccessfulSendMessageToTheRecipientCarryingTheEntryTag()
    {
        var rig = new HooksHostTests.Rig();
        OwesARelay(rig, "pending");

        _ = Stop(
            rig,
            ReportState.Sent,
            mailed: false,
            transcript: Transcript(rig, SendRow("toolu_2", "m1", "hello [ah-entry e1 to m1@tm]"), ResultRow("toolu_2", "Message delivered to that session inbox", isError: false)));

        Assert.True(RelayResolved(rig));
    }

    [Fact]
    public void ARelayObligationIsNotMetByASendMessageToAnotherRecipient()
    {
        var rig = new HooksHostTests.Rig();
        OwesARelay(rig, "pending");

        var output = Stop(
            rig,
            ReportState.Sent,
            mailed: false,
            transcript: Transcript(rig, SendRow("toolu_2", "m2", "hello [ah-entry e1 to m1@tm]"), ResultRow("toolu_2", "Message delivered to that session inbox", isError: false)));

        Assert.False(RelayResolved(rig));
        Assert.Contains("block", output, StringComparison.Ordinal);
    }

    [Fact]
    public void ARelayObligationIsNotMetByASendMessageThatFailed()
    {
        var rig = new HooksHostTests.Rig();
        OwesARelay(rig, "pending");

        var output = Stop(
            rig,
            ReportState.Sent,
            mailed: false,
            transcript: Transcript(rig, SendRow("toolu_2", "m1", "hello [ah-entry e1 to m1@tm]"), ResultRow("toolu_2", "hook denied the call", isError: true)));

        Assert.False(RelayResolved(rig));
        Assert.Contains("block", output, StringComparison.Ordinal);
    }

    [Fact]
    public void ARelayObligationIsNotMetByASendMessageCarryingTheTagOfAnotherEntryWhoseIdStartsWithThisOne()
    {
        var rig = new HooksHostTests.Rig();
        OwesARelay(rig, "pending");

        var output = Stop(
            rig,
            ReportState.Sent,
            mailed: false,
            transcript: Transcript(rig, SendRow("toolu_2", "m1", "hello [ah-entry e10 to m1@tm]"), ResultRow("toolu_2", "Message delivered to that session inbox", isError: false)));

        Assert.False(RelayResolved(rig));
        Assert.Contains("block", output, StringComparison.Ordinal);
    }

    private static string StampedRow(string row, string stamp) => row.Insert(1, "\"timestamp\":\"" + stamp + "\",");

    [Theory]
    [InlineData("2025-12-31T23:00:00.000Z", false)]
    [InlineData("2026-01-01T00:00:01.000Z", true)]
    public void ARelayObligationIsMetOnlyByASendMessageMadeAfterTheObligationWasFirstRecorded(string stamp, bool resolved)
    {
        var rig = new HooksHostTests.Rig();
        OwesARelay(rig, "pending");
        // A later nudge rewrites the row with a new stamp; the obligation's age is its first row's.
        File.AppendAllText(
            rig.Services.Hierarchy.Peers.PeerPendingPath(),
            "{\"session_id\":\"" + Session + "\",\"task\":\"relay:e1\",\"status\":\"pending\",\"nudges\":1,\"ts\":\"2026-01-05T00:00:00.000Z\",\"relay\":{\"entry\":\"e1\",\"address\":\"m1@tm\",\"dir\":\"" + rig.Sandbox.Root + "/box\",\"to\":\"m1\",\"line\":\"hello [ah-entry e1 to m1@tm]\"}}\n");

        _ = Stop(
            rig,
            ReportState.Sent,
            mailed: false,
            transcript: Transcript(
                rig,
                StampedRow(SendRow("toolu_2", "m1", "hello [ah-entry e1 to m1@tm]"), stamp),
                ResultRow("toolu_2", "Message delivered to that session inbox", isError: false)));

        Assert.Equal(resolved, RelayResolved(rig));
    }

    [Fact]
    public void TheStopHookScansTheSessionsOwnTranscriptForRelayedMessages()
    {
        var rig = new HooksHostTests.Rig();
        var scans = new List<string?>();

        _ = Stop(rig, ReportState.Sent, mailed: false, transcript: "/t/transcript.jsonl", scans: scans);

        Assert.Equal([Session + "|/t/transcript.jsonl"], scans);
    }
    private static IReadOnlyList<CallerBox> ArrangeSubTeam(HooksHostTests.Rig rig, string? parent, string? state)
    {
        var file = rig.Sandbox.Root + "/ws-team.json";
        var team = new JsonObject
        {
            ["orchestrator"] = new JsonObject { ["session_id"] = Session },
            ["members"] = new JsonArray { new JsonObject { ["name"] = "impl", ["role"] = "implementor" } },
        };
        if (parent is not null)
        {
            team["parent"] = new JsonObject { ["team"] = parent, ["stream"] = "ws" };
        }

        if (state is not null)
        {
            team["workstream"] = new JsonObject { ["state"] = state, ["summary"] = "s", ["updated"] = "2026-01-01T00:00:00.000Z" };
        }

        File.WriteAllBytes(file, JsonText.ToUtf8(team, 0));
        return [new CallerBox("orchestrator@ws", "/ah/mailbox/ws~aaaaaaaaaa/orchestrator", "ws", "orchestrator", file)];
    }

    private static HooksHostTests.Rig RigWithMemberUp(bool up) => new(memberUp: (_, _) => up);

    [Theory]
    [InlineData("working")]
    [InlineData("blocked")]
    [InlineData("review")]
    public void ASubOrchestratorWithAnActiveStreamAndALiveChildMemberIsNotNudged(string state)
    {
        var rig = RigWithMemberUp(true);
        var owned = ArrangeSubTeam(rig, parent: "top", state: state);

        Assert.Equal(string.Empty, Stop(rig, ReportState.NotWritten, owned: owned));
    }

    private static void OpenDispatchInLane(HooksHostTests.Rig rig, string team)
    {
        var msgs = rig.Services.Config.HierarchyDir(rig.Sandbox.Root + "/proj") + "/msgs";
        _ = Directory.CreateDirectory(msgs);
        File.WriteAllText(
            $"{msgs}/20261025-120000-ab12--implementor--t1--request.md",
            $"---\nid: 20261025-120000-ab12\ntype: request\nto: implementor\nfrom: orchestrator\nslug: t1\nparent: null\nreason: auto-tracked\neta: large\nto_name: impl\nfrom_name: null\nteam: {team}\ncreated: 2026-10-25T12:00:00.000Z\n---\n\n## [0] message\n- go\n");
    }

    private static void TimerRunning(HooksHostTests.Rig rig)
    {
        var dir = Path.Combine(rig.Services.Environment.AhHome, "wake");
        _ = Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, Session + ".overdue.json"), $"{{\"pid\":{Environment.ProcessId},\"pid_start\":null,\"team\":\"ws\"}}");
    }

    [Fact]
    public void ASubOrchestratorWaitingOnALiveMemberWithOpenDispatchesAndNoTimerIsBlockedOnceNamingTheTimer()
    {
        var rig = RigWithMemberUp(true);
        var owned = ArrangeSubTeam(rig, parent: "top", state: "working");
        OpenDispatchInLane(rig, "ws");

        var first = Stop(rig, ReportState.NotWritten, owned: owned);

        Assert.Contains("\"decision\":\"block\"", first, StringComparison.Ordinal);
        Assert.Contains("the overdue timer", first, StringComparison.Ordinal);
        Assert.Equal(string.Empty, Stop(rig, ReportState.NotWritten, owned: owned, reStop: true));
    }

    [Fact]
    public void ASubOrchestratorWaitingOnALiveMemberWithOpenDispatchesAndItsTimerRunningIsLetThrough()
    {
        var rig = RigWithMemberUp(true);
        var owned = ArrangeSubTeam(rig, parent: "top", state: "working");
        OpenDispatchInLane(rig, "ws");
        TimerRunning(rig);

        Assert.Equal(string.Empty, Stop(rig, ReportState.NotWritten, owned: owned));
    }

    [Fact]
    public void ASubOrchestratorWithNoOpenDispatchInItsLaneIsNotAskedForTheTimer()
    {
        var rig = RigWithMemberUp(true);
        var owned = ArrangeSubTeam(rig, parent: "top", state: "working");
        OpenDispatchInLane(rig, "other");

        Assert.Equal(string.Empty, Stop(rig, ReportState.NotWritten, owned: owned));
    }

    // An Orchestrator's unwritten brief goes to the digest (S7.3, D26); a written, unsent one still blocks, so these use that state.
    private const string WrittenNotSentLine = "report written, not sent yet";

    [Fact]
    public void ASubOrchestratorWhoseChildMembersAreAllGoneIsNudgedAsBefore()
    {
        var rig = RigWithMemberUp(false);
        var owned = ArrangeSubTeam(rig, parent: "top", state: "working");

        Assert.Contains(WrittenNotSentLine, Stop(rig, ReportState.WrittenNotSent, owned: owned), StringComparison.Ordinal);
    }

    [Fact]
    public void ASubOrchestratorWhoseStreamIsDoneIsNudgedAsBefore()
    {
        var rig = RigWithMemberUp(true);
        var owned = ArrangeSubTeam(rig, parent: "top", state: "done");

        Assert.Contains(WrittenNotSentLine, Stop(rig, ReportState.WrittenNotSent, owned: owned), StringComparison.Ordinal);
    }

    [Fact]
    public void ATeamWithNoParentIsNudgedAsBeforeEvenWithALiveMember()
    {
        var rig = RigWithMemberUp(true);
        var owned = ArrangeSubTeam(rig, parent: null, state: "working");

        Assert.Contains(WrittenNotSentLine, Stop(rig, ReportState.WrittenNotSent, owned: owned), StringComparison.Ordinal);
    }

    [Fact]
    public void AStreamWithNoReportedStateIsNudgedAsBefore()
    {
        var rig = RigWithMemberUp(true);
        var owned = ArrangeSubTeam(rig, parent: "top", state: null);

        Assert.Contains(WrittenNotSentLine, Stop(rig, ReportState.WrittenNotSent, owned: owned), StringComparison.Ordinal);
    }

    private const string CharterId = "20260101-000100-cc33";

    private static string Lane(HooksHostTests.Rig rig, string streamState)
    {
        var pool = rig.Sandbox.Root + "/pool";
        _ = Directory.CreateDirectory(pool + "/teams");
        _ = Directory.CreateDirectory(pool + "/msgs");
        File.WriteAllText(
            pool + "/teams/mo.json",
            "{\"members\":[{\"name\":\"mo-so-ws1\",\"role\":\"sub-orchestrator\"}],\"streams\":{\"ws1\":{\"state\":\"" + streamState + "\",\"sub_orchestrator\":\"mo-so-ws1\",\"opened\":\"2026-01-01T00:00:00Z\"}}}");
        var charter = pool + "/msgs/" + CharterId + "--sub-orchestrator--ws1-charter--request.md";
        File.WriteAllText(charter, "---\nid: " + CharterId + "\ntype: request\nto: sub-orchestrator\nfrom: orchestrator\nslug: ws1-charter\nteam: mo\ncreated: 2026-01-01T00:01:00Z\n---\n");
        return charter;
    }

    private static string OtherBrief(HooksHostTests.Rig rig)
    {
        var path = rig.Sandbox.Root + "/pool/msgs/20260101-000200-dd44--sub-orchestrator--other--request.md";
        File.WriteAllText(path, "---\nid: 20260101-000200-dd44\ntype: request\nto: sub-orchestrator\nfrom: orchestrator\nslug: other\nteam: mo\ncreated: 2026-01-01T00:02:00Z\n---\n");
        return path;
    }

    [Fact]
    public void AnOpenLanesCharterIsNotOwedOnTheMailPath()
    {
        var rig = new HooksHostTests.Rig();
        var charter = Lane(rig, "open");

        Assert.Equal(string.Empty, Stop(rig, ReportState.NotWritten, briefPath: charter));
    }

    [Fact]
    public void AnOpenLanesCharterIsNotOwedOnThePeerRowPath()
    {
        var rig = new HooksHostTests.Rig();
        var charter = Lane(rig, "open");
        OwedBySendMessage(rig, charter);

        Assert.Equal(string.Empty, Stop(rig, ReportState.NotWritten, mailed: false, briefPath: charter));
    }

    [Fact]
    public void NegativeTheCharterOfALaneThatIsDoneIsStillOwed()
    {
        var rig = new HooksHostTests.Rig();
        var charter = Lane(rig, "done");

        Assert.Contains(WrittenNotSentLine, Stop(rig, ReportState.WrittenNotSent, briefPath: charter), StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeTheCharterOfALaneThatIsDoneIsStillOwedOnThePeerRowPath()
    {
        var rig = new HooksHostTests.Rig();
        var charter = Lane(rig, "done");
        OwedBySendMessage(rig, charter);

        Assert.Contains("has not been sent yet", Stop(rig, ReportState.NotWritten, mailed: false, briefPath: charter), StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeANonCharterBriefOfTheSameSubOrchestratorIsStillNudgedWhileTheLaneIsOpen()
    {
        var rig = new HooksHostTests.Rig();
        _ = Lane(rig, "open");
        var other = OtherBrief(rig);

        Assert.Contains(WrittenNotSentLine, Stop(rig, ReportState.WrittenNotSent, briefPath: other), StringComparison.Ordinal);
    }

    // ---- fewer-turns S7.3 (rows B-12, B-13, B-13a): an Orchestrator's unwritten brief goes to its digest, not a Stop block.

    private const string Advisory = "report-owed-advisory";

    /// <summary>Registers the session as the Orchestrator of team tm with its <c>orchestrator@tm</c> box, through the mailbox the hook gets.</summary>
    private static IMailbox OrchestratorWithBox(HooksHostTests.Rig rig)
    {
        HoldFixture.Orchestrates(rig, Session, "tm");
        var team = rig.Services.Roster.TeamPath(rig.Services.Config.HierarchyDir(rig.Sandbox.Root + "/proj"), "tm");
        var mailbox = BindingAttestationTests.MailboxFor(rig);

        // The rig's state writer only records calls, so the registration is written where the mailbox reads it.
        var registrations = rig.Services.Environment.AhHome + "/registrations";
        _ = Directory.CreateDirectory(registrations);
        var registration = new JsonObject
        {
            ["session_id"] = Session,
            ["address"] = "orchestrator@tm",
            ["addresses"] = new JsonArray { "orchestrator@tm" },
            ["boxes"] = new JsonObject { ["orchestrator@tm"] = mailbox.BoxDirFor(team, "orchestrator") },
            ["team_file"] = team,
            ["harness"] = "claude",
            ["pid"] = (double)Environment.ProcessId,
        };
        File.WriteAllBytes(registrations + "/" + Session + ".json", JsonText.ToUtf8(registration, 0));
        Assert.NotNull(mailbox.ReadRegistration(Session));
        return mailbox;
    }

    private static int AdvisoryWrites(HooksHostTests.Rig rig) => rig.Writer.Arguments.Count(a => a.Contains(Advisory, StringComparison.Ordinal));

    private static void AdvisedAt(HooksHostTests.Rig rig, DateTimeOffset at)
    {
        var pending = rig.Services.Hierarchy.Peers.PeerPendingPath();
        _ = Directory.CreateDirectory(Path.GetDirectoryName(pending)!);
        var row = new JsonObject { ["type"] = Advisory, ["session_id"] = Session, ["task"] = "e1", ["ts"] = Ah.Core.Time.Timestamps.ToIsoString(at) };
        File.AppendAllText(pending, Encoding.UTF8.GetString(JsonText.ToUtf8(row, 0)) + "\n");
    }

    /// <summary>A db-mode peer log that answers every read but the advisory one, which fails as a database that cannot be read.</summary>
    private sealed class FailsOnTheAdvisoryRead : IPeerRecordSource
    {
        public IReadOnlyList<JsonValue> ReadAll() => [];

        public IReadOnlyList<JsonValue> Rows(PeerQuery query) =>
            query.Types?.Contains(Advisory) == true ? throw new InvalidOperationException("the state database cannot be read") : [];

        public IReadOnlyList<JsonValue> LatestByKeyFor(JsonValue? sessionId) => [];

        public IReadOnlyList<PeerRowAt> Since(long cursor) => [];

        public long Version() => 0;
    }

    [Fact]
    public void B13AStoreThatFailsOnTheAdvisoryReadHoldsTheStopInsteadOfLettingItGoUnadvised()
    {
        var rig = new HooksHostTests.Rig(peerRoute: new PeerRecordsRoute(new FailsOnTheAdvisoryRead(), new StopPeerNudgeUnreadableStoreTests.DbMode()));
        var mailbox = OrchestratorWithBox(rig);

        Assert.Contains("cannot be read", Stop(rig, ReportState.NotWritten, mailbox: mailbox), StringComparison.Ordinal);
        Assert.Equal(0, AdvisoryWrites(rig));
    }

    [Fact]
    public void B13AnOrchestratorsUnwrittenBriefDoesNotBlockAndQueuesAnAdvisoryToItsOwnBox()
    {
        var rig = new HooksHostTests.Rig();
        var mailbox = OrchestratorWithBox(rig);

        Assert.Equal(string.Empty, Stop(rig, ReportState.NotWritten, mailbox: mailbox));
        Assert.Contains(rig.Writer.Arguments, a => a.Contains(Advisory, StringComparison.Ordinal) && a.Contains("orchestrator@tm", StringComparison.Ordinal));
        Assert.Contains(rig.Writer.Arguments, a => a.Contains("\"type\":\"" + Advisory + "\"", StringComparison.Ordinal) && a.Contains("\"task\":\"e1\"", StringComparison.Ordinal));
    }

    [Fact]
    public void B13NegativeAnAdvisoryForTheSameBriefWithinTheHourIsNotRepeated()
    {
        var rig = new HooksHostTests.Rig();
        var mailbox = OrchestratorWithBox(rig);
        AdvisedAt(rig, rig.Services.Clock.UtcNow.AddMinutes(-59));

        Assert.Equal(string.Empty, Stop(rig, ReportState.NotWritten, mailbox: mailbox));
        Assert.Equal(0, AdvisoryWrites(rig));
    }

    [Fact]
    public void B13AnAdvisoryForTheSameBriefIsRepeatedAfterAnHour()
    {
        var rig = new HooksHostTests.Rig();
        var mailbox = OrchestratorWithBox(rig);
        AdvisedAt(rig, rig.Services.Clock.UtcNow.AddMinutes(-61));

        Assert.Equal(string.Empty, Stop(rig, ReportState.NotWritten, mailbox: mailbox));
        Assert.NotEqual(0, AdvisoryWrites(rig));
    }

    [Fact]
    public void B13ASubOrchestratorsUnwrittenBriefDoesNotBlock()
    {
        var rig = new HooksHostTests.Rig();
        var mailbox = OrchestratorWithBox(rig);

        Assert.Equal(string.Empty, Stop(rig, ReportState.NotWritten, agentType: "wrangl:sub-orchestrator", mailbox: mailbox));
    }

    [Fact]
    public void B13NegativeARoleLessSessionWithNoOrchestratorBoxStillBlocksOnAnUnwrittenBrief()
    {
        var rig = new HooksHostTests.Rig();

        var output = Stop(rig, ReportState.NotWritten);

        Assert.Contains("\"decision\":\"block\"", output, StringComparison.Ordinal);
        Assert.Contains("its report is not written yet", output, StringComparison.Ordinal);
        Assert.Equal(0, AdvisoryWrites(rig));
    }

    [Fact]
    public void B13aNegativeAMembersUnwrittenBriefStillBlocksAndQueuesNoAdvisory()
    {
        var rig = new HooksHostTests.Rig();

        var output = Stop(rig, ReportState.NotWritten, agentType: "wrangl:implementor");

        Assert.Contains("\"decision\":\"block\"", output, StringComparison.Ordinal);
        Assert.Contains("its report is not written yet", output, StringComparison.Ordinal);
        Assert.Equal(0, AdvisoryWrites(rig));
    }

    [Fact]
    public void B13bNegativeAnOrchestratorsWrittenUnsentBriefStillBlocks()
    {
        var rig = new HooksHostTests.Rig();
        var mailbox = OrchestratorWithBox(rig);

        Assert.Contains(WrittenNotSentLine, Stop(rig, ReportState.WrittenNotSent, mailbox: mailbox), StringComparison.Ordinal);
        Assert.Equal(0, AdvisoryWrites(rig));
    }

    [Fact]
    public void B12APendingRelayRowStillBlocksAsToday()
    {
        var rig = new HooksHostTests.Rig();
        OwesARelay(rig, "pending");

        Assert.Contains("\"decision\":\"block\"", Stop(rig, ReportState.Sent, mailed: false), StringComparison.Ordinal);
    }
}
