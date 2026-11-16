using System.Text;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Hierarchy;
using Ah.Hooks.Gates;
using Ah.Hooks.Plumbing;
using Ah.Mesh;

namespace Ah.Hooks.Tests;

/// <summary>
/// A bare Orchestrator↔member send is tracked by the hook: the request (an existing one is reused) or response file, a dispatch row, a
/// mailbox entry filed through the mesh sender, and the entry's claim taken for the call, with the message rewritten (rewrite on) or left
/// alone and settled by PostToolUse (rewrite off).
/// </summary>
public sealed class AutoTrackTrackingTests
{
    private const string Id = "20261011-110000-aa01";
    private const string Original = "Please look at the reviewer's second finding on the stream branch and rework the lock so that two parallel spawns cannot both create the tab, then run the Roster tests and report back. xxxxxxxxxxxxxxxxxxxx";

    internal sealed class RecHooks : IMailboxHooks
    {
        public List<(string? Recipient, string? Message, string? ToolUseId)> Claims { get; } = [];

        public List<(string? Recipient, string? Message, RelayResult Result, string? MsgId)> Confirms { get; } = [];

        public bool DenyClaim { get; set; }

        public Func<OpenBrief, ReportState>? OfBrief { get; set; }

        public RelayClaimDecision ClaimRelay(string? sessionId, string? recipient, string? message, RequestContext context, string? toolUseId = null)
        {
            Claims.Add((recipient, message, toolUseId));
            OnClaim?.Invoke();
            return DenyClaim ? new RelayClaimDecision(true, "claim denied", []) : new RelayClaimDecision(false, null, []);
        }

        public IReadOnlyList<string> ConfirmRelay(string? sessionId, string? recipient, string? message, RelayResult result, string? reason, RequestContext context, string? msgId = null)
        {
            Confirms.Add((recipient, message, result, msgId));
            return [];
        }

        public HookAckResult AckFromPrompt(string? sessionId, string? prompt) => throw new NotSupportedException();

        public IReadOnlyList<string> AckFromSendMessage(string? sessionId, string? recipient, string? message, RequestContext context) => throw new NotSupportedException();

        public string? PointerDuplicate(string? message, RequestContext context) => throw new NotSupportedException();

        public string? SendDenial(string? message, string? sessionId, RequestContext context, out string? warning) => throw new NotSupportedException();

        public List<(string? Recipient, string? ToolUseId)> Withdraws { get; } = [];

        public Action? OnClaim { get; set; }

        public IReadOnlyList<string> WithdrawRelay(string? sessionId, string? recipient, string? message, string? toolUseId, RequestContext context)
        {
            Withdraws.Add((recipient, toolUseId));
            return [];
        }

        public IReadOnlyList<string> AckFromTranscript(string? sessionId, string? transcriptPath) => throw new NotSupportedException();

        public FullMail FullMailText(string? sessionId, CompactMail compact) => throw new NotSupportedException();

        public IReadOnlyList<OpenBrief> OpenMailBriefs(string? sessionId) => throw new NotSupportedException();

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
            responsePath = null;
            return OfBrief is { } of ? of(brief) : ReportState.NotWritten;
        }
    }

    /// <summary>Forwards every IMailbox call to the real mailbox and records the acks.</summary>
#pragma warning disable CA1852 // DispatchProxy generates a subclass of the proxy type
    internal class MailSpy : System.Reflection.DispatchProxy
#pragma warning restore CA1852
    {
        public static List<(string[] Ids, string By)> Acks { get; } = [];

        public IMailbox? Inner { get; set; }

        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(IMailbox.AckIds))
            {
                Acks.Add((((IReadOnlyList<string>)args![1]!).ToArray(), (string)args[2]!));
            }

            try
            {
                return targetMethod.Invoke(Inner, args);
            }
            catch (System.Reflection.TargetInvocationException e) when (e.InnerException is not null)
            {
                throw e.InnerException;
            }
        }
    }

    internal sealed class FakeSender(bool throws = false) : IMeshSender
    {
        public List<JsonObject> Requests { get; } = [];

        public List<TimeSpan?> LockWaits { get; } = [];

        public MailboxOutcome<MeshSender> SenderOf(JsonObject request, Func<string, MeshTeam?> lookup, string? toTeam) => throw new NotSupportedException();

        public MailboxOutcome<JsonObject> Send(JsonObject request, DateTimeOffset? now = null, TimeSpan? lockWait = null)
        {
            Requests.Add(request);
            LockWaits.Add(lockWait);
            if (throws)
            {
                throw new InvalidOperationException("the sender is down");
            }

            return new MailboxOutcome<JsonObject>(
                new JsonObject { ["entry"] = new JsonObject { ["id"] = "e1" }, ["address"] = request.Get("to") is { } to ? to : JsonValue.Null },
                null);
        }
    }

    internal sealed class Context(IMailCallers inner, string cwd, string hierarchy) : IMailCallers
    {
        public bool OrchestratorProcessIsGone(double pid, string? recordedStart) => inner.OrchestratorProcessIsGone(pid, recordedStart);

        public RequestContext RequestContext(string c) => new(cwd, hierarchy, string.Empty, string.Empty);

        public IReadOnlyList<CallerBox> ResolveCaller(CallerRequest request) => inner.ResolveCaller(request);

        public IReadOnlyList<CallerBox> RegisterCaller(CallerRequest request) => inner.RegisterCaller(request);

        public IReadOnlyList<CallerBox> EnsureRegistered(CallerRequest request) => inner.EnsureRegistered(request);

        public IReadOnlyList<CallerBox> CallerBoxes(CallerRequest request) => inner.CallerBoxes(request);

        public IReadOnlyList<DeciderBox> JoinDeciderBoxes(string c, string? sessionId = null) => inner.JoinDeciderBoxes(c, sessionId);

        public AckIdsResult AckRouted(string dir, IReadOnlyList<string> ids, string by, string? sessionId = null) => inner.AckRouted(dir, ids, by, sessionId);

        public string? ProcessCommand(int pid) => inner.ProcessCommand(pid);

        public RebindResult RebindOwnedTeams(RebindRequest request) => inner.RebindOwnedTeams(request);
    }

    private sealed record Arranged(bool Member, HooksHostTests.Rig Rig, GateServices Services, RecHooks Hooks, FakeSender Sender, string Request, string Msgs);

    private static string? T(JsonObject o, string key) => o.Get(key) is { IsText: true } v ? v.AsString() : null;

    private static Arranged Arrange(bool rewrite = true, bool live = true, bool senderThrows = false, bool member = false)
    {
        var rig = member ? AutoTrackTests.MemberRig("p1") : AutoTrackTests.OwnerRig(live);
        var project = rig.Sandbox.Root + "/proj";
        File.WriteAllText(project + "/.claude/ah2/agent-hierarchy.json", $"{{\"version\":1,\"auto_track\":{{\"rewrite\":{(rewrite ? "true" : "false")}}}}}");
        var hooks = new RecHooks();
        var sender = new FakeSender(senderThrows);
        if (!member)
        {
            var teamPath = rig.Services.Roster.TeamPath(rig.Services.Config.HierarchyDir(project), "tm");
            File.WriteAllText(teamPath, File.ReadAllText(teamPath).Replace("\"members\":[]", "\"members\":[{\"name\":\"tm-implementor\",\"role\":\"implementor\"}]", StringComparison.Ordinal));
        }

        MailSpy.Acks.Clear();
        var spy = System.Reflection.DispatchProxy.Create<IMailbox, MailSpy>();
        ((MailSpy)(object)spy).Inner = BindingAttestationTests.MailboxFor(rig);
        var advisory = new AdvisoryServices(
            spy, hooks, new Context(new BindingAttestationTests.Callers([]), project, rig.Services.Config.HierarchyDir(project)), null!, null!, null!, null!, null!, new BindingAttestationTests.Daemon(), null!, null!, null, sender);
        var services = rig.Services with { Advisory = advisory };
        var msgs = rig.Services.Config.HierarchyDir(project) + "/msgs";
        _ = Directory.CreateDirectory(msgs);
        var request = $"{msgs}/{Id}--implementor--lock--request.md";
        File.WriteAllText(
            request,
            $"---\nid: {Id}\ntype: request\nto: implementor\nfrom: orchestrator\nslug: lock\nparent: null\nreason: null\neta: null\nto_name: {(member ? "implementor-1" : "tm-implementor")}\nfrom_name: null\nteam: tm\ncreated: 2026-10-11T11:00:00+00:00\n---\n\n## [0] tldr\n- do the thing\n");
        return new Arranged(member, rig, services, hooks, sender, request, msgs);
    }

    private static GateContext Ctx(Arranged a, string message, string to = "tm-implementor", string tool = "PreToolUse", string toolUseId = "tu1", JsonObject? extra = null, JsonObject? response = null, bool subagent = false)
    {
        var input = new JsonObject { ["to"] = to, ["message"] = message };
        if (extra is not null)
        {
            foreach (var key in extra.Keys)
            {
                input[key] = extra[key];
            }
        }

        var evt = new JsonObject
        {
            ["hook_event_name"] = tool,
            ["session_id"] = "s-a",
            ["tool_use_id"] = toolUseId,
            ["cwd"] = a.Rig.Sandbox.Root + "/proj",
            ["tool_name"] = "SendMessage",
            ["tool_input"] = input,
        };
        if (response is not null)
        {
            evt["tool_response"] = response;
        }

        if (subagent)
        {
            evt["agent_id"] = "sub-1";
        }

        if (a.Member)
        {
            evt["agent_type"] = "wrangl:implementor";
        }
        return new GateContext(a.Services, JsonText.Parse(Encoding.UTF8.GetBytes(JsonText.Stringify(evt, 0))), "x");
    }

    private static string Written(Arranged a) => string.Join("\n", a.Rig.Writer.Arguments);

    private static JsonValue Updated(HookOutcome outcome) => JsonText.Parse(outcome.Stdout.Span).Member("hookSpecificOutput")!.Value.Member("updatedInput")!.Value;

    private const string MsgGateName = "pretooluse-msg-gate";

    private static string ChainEvent(Arranged a) =>
        JsonText.Stringify(
            new JsonObject
            {
                ["hook_event_name"] = "PreToolUse",
                ["session_id"] = "s-a",
                ["tool_use_id"] = "tu1",
                ["cwd"] = a.Rig.Sandbox.Root + "/proj",
                ["tool_name"] = "SendMessage",
                ["tool_input"] = new JsonObject { ["to"] = "tm-implementor", ["message"] = "Brief: " + a.Request },
            },
            0);

    private static Ah.Abstractions.HookOutcome RunUnits(Arranged a, string name, IReadOnlyList<Ah.Hooks.Plumbing.GateUnit> units) =>
        Ah.Hooks.Plumbing.HookHost.Run(name, true, Encoding.UTF8.GetBytes(ChainEvent(a)), a.Rig.Errors, () => a.Services, units);

    private static Ah.Hooks.Plumbing.GateUnit MsgGateUnit => Ah.Hooks.Gates.GateUnits.All.Single(u => u.Name == MsgGateName);

    private static Ah.Hooks.Plumbing.GateUnit Stub(string name, Ah.Hooks.Plumbing.GateBody body) => new(name, body, Ah.Hooks.Plumbing.GateClass.BlockA, Matcher: "SendMessage");

    private static string Neutral(string text, Arranged a) => text.Replace(a.Rig.Sandbox.Root, "<root>", StringComparison.Ordinal).Replace(a.Rig.Sandbox.Root.Replace("/", "\\/", StringComparison.Ordinal), "<root>", StringComparison.Ordinal);

    [Fact]
    public void TheChainCarriesTheMsgGatesRewriteExactlyAsTheGateAloneGivesIt()
    {
        var alone = Arrange();
        var chained = Arrange();

        var lone = RunUnits(alone, MsgGateName, [MsgGateUnit]);
        var viaChain = RunUnits(chained, Ah.Hooks.Plumbing.HookHost.ChainName, [MsgGateUnit]);

        var expected = Updated(lone);
        Assert.StartsWith("[hierarchy-msg ", expected.Text("message")!, StringComparison.Ordinal);
        Assert.Equal(Neutral(JsonText.Stringify(expected, 0), alone), Neutral(JsonText.Stringify(Updated(viaChain), 0), chained));
    }

    [Fact]
    public void ADenyFromAnotherGateDropsTheRewrite()
    {
        var a = Arrange();

        var o = RunUnits(a, Ah.Hooks.Plumbing.HookHost.ChainName, [MsgGateUnit, Stub("pretooluse-sendmessage-response", static _ => Ah.Hooks.Plumbing.HookEmit.Deny("route says no"))]);

        var text = Encoding.UTF8.GetString(o.Stdout.Span);
        Assert.Contains("\"permissionDecision\":\"deny\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("updatedInput", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAskFromAnotherGateDropsTheRewrite()
    {
        var a = Arrange();

        var o = RunUnits(a, Ah.Hooks.Plumbing.HookHost.ChainName, [MsgGateUnit, Stub("pretooluse-sendmessage-response", static _ => Ah.Hooks.Plumbing.HookEmit.Decision("ask", "confirm"))]);

        var text = Encoding.UTF8.GetString(o.Stdout.Span);
        Assert.Contains("\"permissionDecision\":\"ask\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("updatedInput", text, StringComparison.Ordinal);
        Assert.Contains(a.Rig.Errors.Records, r => r.Message.Contains("rewrite of the call's input was dropped", StringComparison.Ordinal));
    }

    [Fact]
    public void ADenyThatDropsTheRewriteLeavesNoDroppedRewriteRow()
    {
        var a = Arrange();

        _ = RunUnits(a, Ah.Hooks.Plumbing.HookHost.ChainName, [MsgGateUnit, Stub("pretooluse-sendmessage-response", static _ => Ah.Hooks.Plumbing.HookEmit.Deny("route says no"))]);

        Assert.DoesNotContain(a.Rig.Errors.Records, r => r.Message.Contains("rewrite of the call's input was dropped", StringComparison.Ordinal));
    }

    [Fact]
    public void ACleanGateAlongsideTheRewritingOneKeepsTheRewrite()
    {
        var a = Arrange();

        var o = RunUnits(a, Ah.Hooks.Plumbing.HookHost.ChainName, [MsgGateUnit, Stub("pretooluse-sendmessage-response", static _ => Ah.Hooks.Plumbing.HookEmit.Silent)]);

        Assert.StartsWith("[hierarchy-msg ", Updated(o).Text("message")!, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoGatesThatEachRewriteTheInputHoldTheCall()
    {
        var a = Arrange();
        var rewrite = new JsonObject { ["to"] = "x", ["message"] = "y" };

        var o = RunUnits(
            a,
            Ah.Hooks.Plumbing.HookHost.ChainName,
            [Stub("pretooluse-msg-gate", _ => Ah.Hooks.Plumbing.HookEmit.UpdatedInput(rewrite)), Stub("pretooluse-sendmessage-response", _ => Ah.Hooks.Plumbing.HookEmit.UpdatedInput(rewrite))]);

        var text = Encoding.UTF8.GetString(o.Stdout.Span);
        Assert.Contains("\"permissionDecision\":\"deny\"", text, StringComparison.Ordinal);
        Assert.Contains(Ah.Hooks.Plumbing.HookHost.TwoRewritesReason, text, StringComparison.Ordinal);
        Assert.DoesNotContain("updatedInput", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnExistingRequestIsReusedAndTheRewriteCarriesItsPointerAndTheEntryMarker()
    {
        var a = Arrange();

        var tracking = AutoTrack.PreSend(Ctx(a, "Brief: " + a.Request), true, out var outcome);

        Assert.Equal(AutoTrack.Tracking.Done, tracking);
        var message = Updated(outcome!).Text("message")!;
        Assert.StartsWith(Ah.Core.Json.MsgPointer.ShowLine(a.Request) + "\n", message, StringComparison.Ordinal);
        Assert.EndsWith("\n[ah-entry e1 to tm-implementor@tm]", message, StringComparison.Ordinal);
        Assert.Single(Directory.GetFiles(a.Msgs));
        Assert.DoesNotContain("\"content\"", Written(a), StringComparison.Ordinal);
        Assert.Contains("\"type\":\"dispatch\"", Written(a), StringComparison.Ordinal);
        Assert.Contains(Id, Written(a), StringComparison.Ordinal);
        var sent = Assert.Single(a.Sender.Requests);
        Assert.Equal("brief", T(sent, "kind"));
        Assert.Equal(a.Request, T(sent, "ref"));
        Assert.Equal("tm-implementor@tm", T(sent, "to"));
    }

    [Fact]
    public void TheMeshSenderIsCalledOnceWithNoFromAndTheClaimAndTheRowCarryTheCall()
    {
        var a = Arrange();

        _ = AutoTrack.PreSend(Ctx(a, "Brief: " + a.Request), true, out _);

        Assert.False(Assert.Single(a.Sender.Requests).ContainsKey("from"));
        var claim = Assert.Single(a.Hooks.Claims);
        Assert.Equal("tu1", claim.ToolUseId);
        Assert.Contains("[ah-entry e1 to tm-implementor@tm]", claim.Message, StringComparison.Ordinal);
        Assert.Contains("\"tool_use_id\":\"tu1\"", Written(a), StringComparison.Ordinal);
        Assert.Contains("\"entry\":\"e1\"", Written(a), StringComparison.Ordinal);
    }

    [Fact]
    public void TheEntryIsFiledWithABoundedLockWait()
    {
        var a = Arrange();

        _ = AutoTrack.PreSend(Ctx(a, "Brief: " + a.Request), true, out _);

        var wait = Assert.Single(a.Sender.LockWaits);
        Assert.True(wait is { } w && w > TimeSpan.Zero && w <= TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void ARowThatCannotBeWrittenAfterTheClaimWithdrawsTheClaimAndFailsTheTracking()
    {
        var a = Arrange();
        a.Hooks.OnClaim = () => a.Rig.Writer.Failing.Add("peer-records.append");

        var tracking = AutoTrack.PreSend(Ctx(a, "Brief: " + a.Request), true, out var outcome);

        Assert.Equal(AutoTrack.Tracking.Failed, tracking);
        Assert.Null(outcome);
        var withdrawn = Assert.Single(a.Hooks.Withdraws);
        Assert.Equal("tu1", withdrawn.ToolUseId);
    }

    [Fact]
    public void ARetryAfterAFailedEntryIsTrackedAgainNotLetThroughBare()
    {
        var a = Arrange(senderThrows: true);
        var first = AutoTrack.PreSend(Ctx(a, "Brief: " + a.Request), true, out _);

        var second = AutoTrack.PreSend(Ctx(a, "Brief: " + a.Request, toolUseId: "tu2"), true, out _);

        Assert.Equal(AutoTrack.Tracking.Failed, first);
        Assert.DoesNotContain("\"type\":\"auto-track\"", Written(a), StringComparison.Ordinal);
        Assert.Equal(AutoTrack.Tracking.Failed, second);
        Assert.Equal(2, a.Sender.Requests.Count);
    }

    [Fact]
    public void APlainTaskSizedTextGoesThroughUndeniedAndTouchesNothingEvenWhenTheSenderWouldThrow()
    {
        var a = Arrange(senderThrows: true);
        File.Delete(a.Request);

        var outcome = AutoTrack.Run(Ctx(a, Original));

        Assert.DoesNotContain("deny", Denied(outcome), StringComparison.Ordinal);
        Assert.Empty(a.Sender.Requests);
        Assert.DoesNotContain("\"type\":\"dispatch\"", Written(a), StringComparison.Ordinal);
    }

    [Fact]
    public void AMembersBareReportInHookModeFilesAReportEntryAndTakesItsClaimWithTheMarkerAlone()
    {
        var a = Arrange(rewrite: false, member: true);
        var response = AnsweredResponse(a);

        var tracking = AutoTrack.PreSend(Ctx(a, "DONE lock", to: "orchestrator"), false, out var outcome);

        Assert.Equal(AutoTrack.Tracking.Done, tracking);
        Assert.Null(outcome);
        var sent = Assert.Single(a.Sender.Requests);
        Assert.Equal("report", T(sent, "kind"));
        Assert.Equal(response, T(sent, "ref"));
        Assert.Equal("[ah-entry e1 to orchestrator@tm]", Assert.Single(a.Hooks.Claims).Message);
    }

    [Fact]
    public void ATaskSizedTextWithNoPathIsMailAndFilesNoRequestNoRowAndNoEntry()
    {
        var a = Arrange();
        File.Delete(a.Request);

        var tracking = AutoTrack.PreSend(Ctx(a, Original), true, out var outcome);

        Assert.Equal(AutoTrack.Tracking.NotApplicable, tracking);
        Assert.Null(outcome);
        Assert.DoesNotContain("\"content\"", Written(a), StringComparison.Ordinal);
        Assert.DoesNotContain("\"type\":\"dispatch\"", Written(a), StringComparison.Ordinal);
        Assert.Empty(a.Sender.Requests);
        Assert.Empty(a.Hooks.Claims);
    }

    [Fact]
    public void WithTheRewriteOffTheSendIsTrackedWithoutAnUpdatedInputAndTheClaimIsTheMarkerAlone()
    {
        var a = Arrange(rewrite: false);
        var resolved = a.Rig.Services.Config.ResolveConfig(a.Rig.Sandbox.Root + "/proj");

        Assert.False(AutoTrack.RewriteOn(resolved));
        var tracking = AutoTrack.PreSend(Ctx(a, "Brief: " + a.Request), AutoTrack.RewriteOn(resolved), out var outcome);

        Assert.Equal(AutoTrack.Tracking.Done, tracking);
        Assert.Null(outcome);
        Assert.Equal("[ah-entry e1 to tm-implementor@tm]", Assert.Single(a.Hooks.Claims).Message);
        Assert.Single(a.Sender.Requests);
    }

    [Fact]
    public void TheRewriteIsOnByDefault() =>
        Assert.True(AutoTrack.RewriteOn(AutoTrackTests.OwnerRig().Services.Config.ResolveConfig(AutoTrackTests.OwnerRig().Sandbox.Root + "/proj")));

    [Fact]
    public void ADeadMemberIsStillFiledAndTimedButGetsNoNotifyWhenIdle()
    {
        var a = Arrange(live: false);

        var tracking = AutoTrack.PreSend(Ctx(a, "Brief: " + a.Request), true, out var outcome);

        Assert.Equal(AutoTrack.Tracking.Done, tracking);
        Assert.Contains("\"type\":\"dispatch\"", Written(a), StringComparison.Ordinal);
        Assert.Null(Updated(outcome!).Member("notify_when_idle"));
    }

    [Fact]
    public void ALiveMemberGetsNotifyWhenIdle()
    {
        var a = Arrange();

        _ = AutoTrack.PreSend(Ctx(a, "Brief: " + a.Request), true, out var outcome);

        Assert.True(Updated(outcome!).Member("notify_when_idle")!.Value.AsBool());
    }

    [Fact]
    public void TheRewrittenInputIsTheOriginalExceptMessageAndNotifyWhenIdle()
    {
        var a = Arrange();
        var extra = new JsonObject { ["summary"] = "lock", ["type"] = "message", ["recipient"] = "tm-implementor", ["recipient_kind"] = "name", ["content"] = "Brief: " + a.Request };

        _ = AutoTrack.PreSend(Ctx(a, "Brief: " + a.Request, extra: extra), true, out var outcome);

        var updated = Updated(outcome!);
        foreach (var key in extra.Keys)
        {
            Assert.Equal(JsonText.Stringify(extra[key], 0), JsonText.Stringify(updated.Member(key)!.Value, 0));
        }

        Assert.Equal("tm-implementor", updated.Text("to"));
        Assert.NotEqual("Brief: " + a.Request, updated.Text("message"));
    }

    [Fact]
    public void AMemberReplyWhoseResponseIsAlreadyWrittenIsAReportSendAndNoSecondResponseIsMade()
    {
        var a = Arrange(member: true);
        var response = $"{a.Msgs}/{Id}--orchestrator--lock--response.md";
        File.WriteAllText(response, $"---\nid: {Id}\ntype: response\n---\n\n## [0] tldr\n- done\n");
        File.AppendAllText(
            a.Rig.Services.Hierarchy.Peers.PeerPendingPath(),
            Encoding.UTF8.GetString(JsonText.ToUtf8(new JsonObject { ["type"] = "turn", ["session_id"] = "s-a", ["status"] = "armed", ["ts"] = Ah.Core.Time.Timestamps.ToIsoString(DateTimeOffset.UtcNow.AddMinutes(-5)) }, 0)) + "\n");

        var tracking = AutoTrack.PreSend(Ctx(a, "DONE lock", to: "orchestrator"), true, out var outcome);

        Assert.Equal(AutoTrack.Tracking.Done, tracking);
        var sent = Assert.Single(a.Sender.Requests);
        Assert.Equal("report", T(sent, "kind"));
        Assert.Equal(response, T(sent, "ref"));
        Assert.Equal("orchestrator@tm", T(sent, "to"));
        Assert.DoesNotContain("\"content\"", Written(a), StringComparison.Ordinal);
        Assert.EndsWith("\n[ah-entry e1 to orchestrator@tm]", Updated(outcome!).Text("message")!, StringComparison.Ordinal);
    }

    [Fact]
    public void AReportAlreadyInTheOrchestratorsBoxMakesALaterPlainMessageOfTheSameTurnUntracked()
    {
        var a = Arrange(member: true);
        _ = AnsweredResponse(a);
        a.Hooks.OfBrief = brief => brief.From is { IsText: true } f && f.AsString() == "orchestrator" ? ReportState.Sent : ReportState.WrittenNotSent;

        var plan = AutoTrack.Classify(Ctx(a, "note: one more thing I noticed while testing the lock", to: "orchestrator"), out _);

        Assert.Null(plan);
    }

    [Fact]
    public void AReportWrittenButNotYetInTheBoxStillPairsTheReply()
    {
        var a = Arrange(member: true);
        _ = AnsweredResponse(a);
        a.Hooks.OfBrief = brief => brief.From is { IsText: true } f && f.AsString() == "orchestrator" ? ReportState.WrittenNotSent : ReportState.Sent;

        Assert.NotNull(AutoTrack.Classify(Ctx(a, "DONE lock", to: "orchestrator"), out _));
    }

    [Fact]
    public void ASenderThatThrowsLeavesTheInputUnchangedWithAnErrorRowAndFailedTracking()
    {
        var a = Arrange(senderThrows: true);

        var tracking = AutoTrack.PreSend(Ctx(a, "Brief: " + a.Request), true, out var outcome);

        Assert.Equal(AutoTrack.Tracking.Failed, tracking);
        Assert.Null(outcome);
        Assert.NotEmpty(a.Rig.Errors.Records);
        Assert.Empty(a.Hooks.Claims);
    }

    [Fact]
    public void AClaimTheMailboxRefusesDeniesTheCall()
    {
        var a = Arrange();
        a.Hooks.DenyClaim = true;

        _ = AutoTrack.PreSend(Ctx(a, "Brief: " + a.Request), true, out var outcome);

        Assert.Contains("deny", Encoding.UTF8.GetString(outcome!.Stdout.Span), StringComparison.Ordinal);
    }

    [Fact]
    public void ThePostToolUseOfAHonouredRewriteConfirmsNothingMore()
    {
        var a = Arrange();
        Seed(a, "rewrite");
        var message = "[hierarchy-msg " + a.Request + "]\n" + Original + "\n[ah-entry e1 to tm-implementor@tm]";

        _ = AutoTrack.AfterSend(Ctx(a, message, tool: "PostToolUse", response: new JsonObject { ["msg_id"] = "m1", ["success"] = true }));

        Assert.Empty(a.Hooks.Confirms);
        Assert.Empty(a.Rig.Errors.Records);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void APlainSendThatWentOutIsAFollowUpOfTheOpenRequestOrElseAPlainSendRequestAndItsEntryIsSettledAsDelivered(bool openRequest)
    {
        var a = Arrange();
        if (!openRequest)
        {
            File.Delete(a.Request);
        }

        _ = AutoTrack.AfterSend(Ctx(a, Original, tool: "PostToolUse", response: new JsonObject { ["msg_id"] = "m1", ["success"] = true }));

        // one brief entry, claimed, confirmed and acked by this call, so the mailbox never delivers it again
        var entry = Assert.Single(a.Sender.Requests);
        Assert.Equal("brief", T(entry, "kind"));
        Assert.Equal("tu1", Assert.Single(a.Hooks.Claims).ToolUseId);
        _ = Assert.Single(a.Hooks.Confirms);
        Assert.Contains(MailSpy.Acks, static x => x.Ids.SequenceEqual(["e1"]) && x.By == "sendmessage");
        if (openRequest)
        {
            Assert.Equal(a.Request, T(entry, "ref"));
            Assert.Contains("\"type\":\"followup\"", Written(a), StringComparison.Ordinal);
            Assert.Equal([a.Request], Directory.GetFiles(a.Msgs));
        }
        else
        {
            Assert.Contains("\\nreason: plain-send\\n", Written(a), StringComparison.Ordinal);
            Assert.Contains("\"type\":\"dispatch\"", Written(a), StringComparison.Ordinal);
            Assert.DoesNotContain("\"type\":\"followup\"", Written(a), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ASecondPlainSendToAnUnarmedPlainSendHolderIsAFollowUpAndFilesNoSecondRequest()
    {
        var a = Arrange();
        File.WriteAllText(a.Request, File.ReadAllText(a.Request).Replace("reason: null", "reason: plain-send", StringComparison.Ordinal));

        _ = AutoTrack.AfterSend(Ctx(a, Original, tool: "PostToolUse", response: new JsonObject { ["msg_id"] = "m2", ["success"] = true }));

        Assert.Equal(a.Request, T(Assert.Single(a.Sender.Requests), "ref"));
        Assert.DoesNotContain("reason: plain-send", Written(a), StringComparison.Ordinal);
        Assert.Contains("\"type\":\"followup\"", Written(a), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void APlainSendsEntryIsAckedEvenWhenItsClaimIsRefusedAndConfirmedOnlyWhenClaimed(bool deny)
    {
        var a = Arrange();
        a.Hooks.DenyClaim = deny;

        _ = AutoTrack.AfterSend(Ctx(a, Original, tool: "PostToolUse", response: new JsonObject { ["msg_id"] = "m1", ["success"] = true }));

        Assert.Contains(MailSpy.Acks, static x => x.Ids.SequenceEqual(["e1"]) && x.By == "sendmessage");
        Assert.Equal(deny ? 0 : 1, a.Hooks.Confirms.Count);
    }

    [Fact]
    public void AFollowUpToAReportedExchangeReArmsItWithADispatchRecordAndMovesItsItemToDispatched()
    {
        var a = Arrange();
        a.Rig.Writer.Executing = a.Services.Hierarchy.StateDomains.Where(d => d.Name == "progress").ToList();
        var pool = Ah.Hooks.Lifecycle.ProgressPool.Of(a.Services, a.Rig.Sandbox.Root + "/proj");
        var p = a.Services.Hierarchy.Progress;
        Assert.Null(p.RecordChecklist(pool, "tm", "_run", "o@tm", [new PlanInput("a", "add", "Do a"), new PlanInput("a", "next")]).Refused);
        Assert.Null(HierarchyModule.ChecklistOnDispatch(p, pool, "tm", "a", "tm-implementor", Id, "o@tm", null));
        Assert.Null(HierarchyModule.ChecklistOnReply(p, pool, "tm", Id, "a", "tm-implementor", "DONE lock @abc1234", "tm-implementor@tm", null));
        Assert.NotEqual("in_progress", p.ReadPlan(pool, "tm", "_run")!.Items.Single(i => i.Item == "a").State);

        _ = AutoTrack.AfterSend(Ctx(a, Original, tool: "PostToolUse", response: new JsonObject { ["msg_id"] = "m1", ["success"] = true }));

        Assert.Contains("\"type\":\"dispatch\"", Written(a), StringComparison.Ordinal);
        var item = p.ReadPlan(pool, "tm", "_run")!.Items.Single(i => i.Item == "a");
        Assert.Equal("in_progress", item.State);
    }

    [Fact]
    public void NegativeAPlainSendThatFailedRecordsNothing()
    {
        var a = Arrange();

        _ = AutoTrack.AfterSend(Ctx(a, Original, tool: "PostToolUse", response: new JsonObject { ["success"] = false }));

        Assert.Empty(a.Sender.Requests);
        Assert.Empty(a.Hooks.Claims);
        Assert.DoesNotContain("\"type\":\"followup\"", Written(a), StringComparison.Ordinal);
    }

    [Fact]
    public void ASendThatNamesAnExistingRequestIsStillFiledAfterwards()
    {
        var a = Arrange();

        _ = AutoTrack.AfterSend(Ctx(a, "Brief: " + a.Request, tool: "PostToolUse", response: new JsonObject { ["msg_id"] = "m1", ["success"] = true }));

        Assert.Single(Directory.GetFiles(a.Msgs));
        Assert.Contains("\"type\":\"auto-track\"", Written(a), StringComparison.Ordinal);
    }

    [Fact]
    public void ADroppedRewriteFallsBackToTheSyntheticMarkerConfirmAndLogsOnce()
    {
        var a = Arrange();
        Seed(a, "rewrite");

        _ = AutoTrack.AfterSend(Ctx(a, Original, tool: "PostToolUse", response: new JsonObject { ["msg_id"] = "m1", ["success"] = true }));

        var confirm = Assert.Single(a.Hooks.Confirms);
        Assert.Equal("[ah-entry e1 to tm-implementor@tm]", confirm.Message);
        Assert.Equal("m1", confirm.MsgId);
        Assert.Equal("sendmessage", Assert.Single(MailSpy.Acks).By);
        Assert.Single(a.Rig.Errors.Records);
        Assert.Contains("not honoured", a.Rig.Errors.Records[0].ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ThePostToolUseOfAHookTrackedSendConfirmsByTheRebuiltMarkerOnlyForItsOwnToolUseId()
    {
        var a = Arrange(rewrite: false);
        Seed(a, "hook");

        _ = AutoTrack.AfterSend(Ctx(a, "Brief: " + a.Request, tool: "PostToolUse", toolUseId: "other", response: new JsonObject { ["msg_id"] = "m1", ["success"] = true }));
        Assert.Empty(a.Hooks.Confirms);
        Assert.Empty(MailSpy.Acks);

        _ = AutoTrack.AfterSend(Ctx(a, "Brief: " + a.Request, tool: "PostToolUse", response: new JsonObject { ["msg_id"] = "m1", ["success"] = true }));
        var confirm = Assert.Single(a.Hooks.Confirms);
        Assert.Equal("[ah-entry e1 to tm-implementor@tm]", confirm.Message);
        Assert.Equal(RelayResult.Sent, confirm.Result);
        Assert.Empty(a.Rig.Errors.Records);
        var ack = Assert.Single(MailSpy.Acks);
        Assert.Equal("e1", Assert.Single(ack.Ids));
        Assert.Equal("sendmessage", ack.By);
    }

    [Fact]
    public void AnErrorResultOfAHookTrackedSendConfirmsFailedAndAcksNothing()
    {
        var a = Arrange(rewrite: false);
        Seed(a, "hook");

        _ = AutoTrack.AfterSend(Ctx(a, "Brief: " + a.Request, tool: "PostToolUse", response: new JsonObject { ["success"] = false, ["error"] = "nope" }));

        Assert.Equal(RelayResult.Failed, Assert.Single(a.Hooks.Confirms).Result);
        Assert.Empty(MailSpy.Acks);
    }

    [Fact]
    public void APostToolUseOfATrackedSendDoesNotFileASecondRequest()
    {
        var a = Arrange(rewrite: false);
        Seed(a, "hook");

        _ = AutoTrack.AfterSend(Ctx(a, "Brief: " + a.Request, tool: "PostToolUse", response: new JsonObject { ["msg_id"] = "m1", ["success"] = true }));

        Assert.DoesNotContain("\"content\"", Written(a), StringComparison.Ordinal);
        Assert.Equal(string.Empty, Written(a));
        Assert.Empty(a.Sender.Requests);
    }

    [Fact]
    public void TheSameTextWithinTheDedupWindowIsNotTrackedTwice()
    {
        var a = Arrange();
        File.AppendAllText(
            a.Rig.Services.Hierarchy.Peers.PeerPendingPath(),
            Encoding.UTF8.GetString(JsonText.ToUtf8(
                new JsonObject
                {
                    ["type"] = "auto-track", ["session_id"] = "s-a", ["to"] = "tm-implementor", ["hash"] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(("Brief: " + a.Request).Trim()))).ToLowerInvariant(),
                    ["path"] = a.Request, ["ts"] = Ah.Core.Time.Timestamps.ToIsoString(DateTimeOffset.UtcNow),
                },
                0)) + "\n");

        var tracking = AutoTrack.PreSend(Ctx(a, "Brief: " + a.Request), true, out var outcome);

        Assert.Equal(AutoTrack.Tracking.NotApplicable, tracking);
        Assert.Null(outcome);
        Assert.Empty(a.Sender.Requests);
    }

    [Fact]
    public void AResponseInTheStoreWithNoFileIsThisTurnsReportAndIsReusedNotCreated()
    {
        var a = Arrange(member: true);
        var response = AnsweredResponse(a);
        Ah.TestKit.TestMsgStore.DropFiles(a.Rig.Sandbox.Root + "/ah", a.Rig.Sandbox.Root);

        var tracking = AutoTrack.PreSend(Ctx(a, "DONE lock", to: "orchestrator"), true, out _);

        Assert.Equal(AutoTrack.Tracking.Done, tracking);
        var sent = Assert.Single(a.Sender.Requests);
        Assert.Equal(Ah.Core.Json.MsgRows.PathKey(response), Ah.Core.Json.MsgRows.PathKey(T(sent, "ref")!));
        Assert.DoesNotContain("\"content\"", Written(a), StringComparison.Ordinal);
    }

    private static string AnsweredResponse(Arranged a)
    {
        var response = $"{a.Msgs}/{Id}--orchestrator--lock--response.md";
        File.WriteAllText(response, $"---\nid: {Id}\ntype: response\n---\n\n## [0] tldr\n- done\n");
        File.AppendAllText(
            a.Rig.Services.Hierarchy.Peers.PeerPendingPath(),
            Encoding.UTF8.GetString(JsonText.ToUtf8(new JsonObject { ["type"] = "turn", ["session_id"] = "s-a", ["status"] = "armed", ["ts"] = Ah.Core.Time.Timestamps.ToIsoString(DateTimeOffset.UtcNow.AddMinutes(-5)) }, 0)) + "\n");
        return response;
    }

    private static string Denied(HookOutcome outcome) => Encoding.UTF8.GetString(outcome.Stdout.Span);

    [Fact]
    public void ABareBriefWhoseTrackingFailedGoesThroughWithItsFilingStepsAsContext()
    {
        var a = Arrange(senderThrows: true);

        var outcome = Denied(AutoTrack.Run(Ctx(a, "Take " + a.Request + " now")));

        Assert.DoesNotContain("permissionDecision", outcome, StringComparison.Ordinal);
        Assert.Contains("additionalContext", outcome, StringComparison.Ordinal);
        Assert.Contains("--kind brief --cwd " + a.Rig.Sandbox.Root + "/proj", outcome, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void TheRelayLineAnAhMsgSendPrintsIsNeverTrackedOrRewritten(bool rewrite, bool member)
    {
        var a = Arrange(rewrite: rewrite, member: member);
        var line = member
            ? "ah mail from implementor-1@tm to orchestrator@tm (report): [hierarchy-msg " + AnsweredResponse(a) + "] [ah-entry E1 to orchestrator@tm]"
            : "ah mail from orchestrator@tm to tm-implementor@tm (brief): [hierarchy-msg " + a.Request + "] [ah-entry E1 to tm-implementor@tm]";

        var outcome = AutoTrack.Run(Ctx(a, line, to: member ? "orchestrator" : "tm-implementor", extra: new JsonObject { ["notify_when_idle"] = true }));

        Assert.DoesNotContain("updatedInput", Denied(outcome), StringComparison.Ordinal);
        Assert.DoesNotContain("deny", Denied(outcome), StringComparison.Ordinal);
        Assert.Empty(a.Sender.Requests);
        Assert.Empty(a.Hooks.Claims);
        Assert.DoesNotContain("\"type\":\"dispatch\"", Written(a), StringComparison.Ordinal);
    }

    [Fact]
    public void ABareBriefWhoseTrackingSucceededIsNotDenied()
    {
        var a = Arrange();

        var outcome = AutoTrack.Run(Ctx(a, "Take " + a.Request + " now"));

        Assert.DoesNotContain("bare-brief", Denied(outcome), StringComparison.Ordinal);
        Assert.Single(a.Sender.Requests);
    }

    [Fact]
    public void ABareBriefNamingAFileThatIsNotAnOpenRequestToTheRecipientIsNotDenied()
    {
        var a = Arrange(senderThrows: true);
        var closed = $"{a.Msgs}/20261011-110001-bb02--implementor--other--request.md";
        File.WriteAllText(closed, File.ReadAllText(a.Request).Replace(Id, "20261011-110001-bb02", StringComparison.Ordinal).Replace("to_name: tm-implementor", "to_name: tm-reviewer", StringComparison.Ordinal));
        var done = $"{a.Msgs}/20261011-110003-ee04--implementor--fourth--request.md";
        File.WriteAllText(done, File.ReadAllText(a.Request).Replace(Id, "20261011-110003-ee04", StringComparison.Ordinal));
        File.WriteAllText($"{a.Msgs}/20261011-110003-ee04--orchestrator--fourth--response.md", "---\nid: 20261011-110003-ee04\ntype: response\n---\n\n## [0] tldr\n- finished\n");
        var otherTeam = $"{a.Msgs}/20261011-110002-cc03--implementor--third--request.md";
        File.WriteAllText(otherTeam, File.ReadAllText(a.Request).Replace(Id, "20261011-110002-cc03", StringComparison.Ordinal).Replace("team: tm", "team: zz", StringComparison.Ordinal));

        foreach (var text in new[] { "Take " + closed + " now", "Take " + done + " now", "Take " + otherTeam + " now", "Take " + a.Msgs + "/20990101-000000-zz99--implementor--none--request.md now", "Take " + a.Request.Replace("--request.md", "--response.md", StringComparison.Ordinal) })
        {
            Assert.DoesNotContain("bare-brief", Denied(AutoTrack.Run(Ctx(a, text))), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ABareBriefToAnotherRecipientOrWithAPointerOrAMarkerIsNotDenied()
    {
        var a = Arrange(senderThrows: true);

        foreach (var text in new[] { "[hierarchy-msg " + a.Request + "]\nTake it", "Take " + a.Request + " [ah-entry e1 to tm-implementor@tm]" })
        {
            Assert.DoesNotContain("bare-brief", Denied(AutoTrack.Run(Ctx(a, text))), StringComparison.Ordinal);
        }

        Assert.DoesNotContain("bare-brief", Denied(AutoTrack.Run(Ctx(a, "Take " + a.Request + " now", to: "someone-else"))), StringComparison.Ordinal);
    }

    [Fact]
    public void ABareReportNamingAResponseTheSessionDoesNotOweIsNotDenied()
    {
        var a = Arrange(senderThrows: true, member: true);
        var other = $"{a.Msgs}/20261011-110009-dd09--orchestrator--other--response.md";
        File.WriteAllText(other, "---\nid: 20261011-110009-dd09\ntype: response\n---\n");

        Assert.DoesNotContain("bare-report", Denied(AutoTrack.Run(Ctx(a, "DONE, see " + other, to: "orchestrator"))), StringComparison.Ordinal);
    }

    private static void Seed(Arranged a, string mode) =>
        File.AppendAllText(
            a.Rig.Services.Hierarchy.Peers.PeerPendingPath(),
            Encoding.UTF8.GetString(JsonText.ToUtf8(
                new JsonObject
                {
                    ["type"] = "auto-track", ["session_id"] = "s-a", ["to"] = "tm-implementor", ["hash"] = "h", ["path"] = a.Request, ["ts"] = Ah.Core.Time.Timestamps.ToIsoString(DateTimeOffset.UtcNow),
                    ["tool_use_id"] = "tu1", ["entry"] = "e1", ["address"] = "tm-implementor@tm", ["mode"] = mode,
                },
                0)) + "\n");
}
