using System.Globalization;
using System.Text;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Hierarchy;
using Ah.Mesh;
using Ah.TestKit;

namespace Ah.Hooks.Tests;

/// <summary>The Stop hook's own check-in clock: where a request's liveness rows live, and what counts toward its interval.</summary>
public sealed class StopOrchestratorLivenessTests
{
    private const string Session = "sess-1";
    private const string RequestId = "20260101-000000-t1aa";

    /// <summary>The fake clock the rig starts at.</summary>
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    private sealed class Scenario
    {
        public required HooksHostTests.Rig Rig { get; init; }

        /// <summary>The hierarchy dir the hook resolves for its own cwd.</summary>
        public required string CwdDir { get; init; }

        /// <summary>The hierarchy dir holding the request file.</summary>
        public required string PoolDir { get; init; }

        public HarnessSessions? Sessions { get; init; }

        public string RequestPath => $"{PoolDir}/msgs/{RequestId}--implementor--t1--request.md";

        public List<JsonValue> GateRecordsIn(string dir) =>
            Rig.Writer.Calls.Zip(Rig.Writer.Arguments)
                .Where(p => p.First == "gates.append")
                .Select(p => JsonText.Parse(Encoding.UTF8.GetBytes(p.Second)))
                .Where(a => a.Member("dir") is { IsText: true } d && d.AsString() == dir)
                .Select(a => a.Member("rec")!.Value)
                .ToList();

        public List<string> GateAppendsTo(string dir) => GateRecordsIn(dir).Select(r => r.Member("type")!.Value.AsString()).ToList();
    }

    /// <summary>
    /// A dispatcher session at <c>proj</c> (hierarchy dir <c>cwd-hier</c>) with one old open request whose file is in another
    /// pool; <paramref name="withPath"/> false leaves the dispatch row without a <c>path</c>, so the request is the cwd's own.
    /// </summary>
    private static Scenario Arrange(bool withPath = true, string to = "implementor", string? toAddr = null, string created = "2020-01-01T00:00:00-00:00", string eta = "small", string? toName = "peer-name", string? rowCreated = null, bool doorbell = false, PeerRecordsRoute? peerRoute = null, Dictionary<string, string>? env = null)
    {
        // the older tests exercise the event-body delivery the kill switch keeps; the doorbell tests turn it on
        var environment = env ?? [];
        if (!doorbell)
        {
            environment["AH_MAIL_DOORBELL"] = "0";
        }

        var rig = new HooksHostTests.Rig(environment.Count == 0 ? null : environment, processTable: new HarnessSessions().Table, peerRoute: peerRoute);
        var root = rig.Sandbox.Root;
        var cwdDir = rig.Services.Config.HierarchyDir(root + "/proj");
        var poolDir = withPath ? root + "/pool-hier" : cwdDir;
        _ = Directory.CreateDirectory(cwdDir);
        _ = Directory.CreateDirectory(poolDir + "/msgs");
        var s = new Scenario { Rig = rig, CwdDir = cwdDir, PoolDir = poolDir };
        if (doorbell)
        {
            // the doorbell keeps its state in a mailbox note, so the note op has to reach the files
            rig.Writer.Executing = [BindingAttestationTests.MailboxFor(rig).StateDomain()];
        }

        File.WriteAllText(
            s.RequestPath,
            $"---\nid: {RequestId}\ntype: request\nto: implementor\nfrom: orchestrator\nslug: t1\nparent: null\nreason: null\neta: {eta}\nto_name: {toName ?? "null"}\nfrom_name: null\nteam: null\ncreated: {created}\n---\n\n## [0] tldr\n- none\n");
        var pending = rig.Services.Hierarchy.Peers.PeerPendingPath();
        _ = Directory.CreateDirectory(Path.GetDirectoryName(pending)!);
        var dispatch = new JsonObject
        {
            ["type"] = "dispatch",
            ["session_id"] = Session,
            ["request_id"] = RequestId,
            ["to"] = to,
            ["created"] = rowCreated ?? (withPath ? created : At(3600)),
        };
        if (toAddr is not null)
        {
            dispatch["to_addr"] = toAddr;
        }

        if (withPath)
        {
            dispatch["path"] = s.RequestPath;
        }

        File.AppendAllText(pending, Encoding.UTF8.GetString(JsonText.ToUtf8(dispatch, 0)) + "\n");
        return s;
    }

    /// <summary>A <c>liveness-nudge</c> row written <paramref name="secondsAgo"/> before the rig's clock, into <paramref name="dir"/>.</summary>
    private static void Nudge(Scenario s, string dir, int secondsAgo)
    {
        var row = new JsonObject
        {
            ["type"] = "liveness-nudge",
            ["session_id"] = Session,
            ["request_id"] = RequestId,
            ["ts"] = Now.AddSeconds(-secondsAgo).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
        };
        s.Rig.PutGates(dir, Encoding.UTF8.GetString(JsonText.ToUtf8(row, 0)));
    }

    /// <summary>The caller helper of an orchestrator: registers into the orchestrator box and records the entry ids it is asked to ack.</summary>
    private sealed class AckingCallers(List<string> acked, bool firstToAck = true, string? deciderDir = null) : IMailCallers
    {
        public RequestContext RequestContext(string cwd) => throw new NotSupportedException();

        public IReadOnlyList<CallerBox> ResolveCaller(CallerRequest request) => throw new NotSupportedException();

        public IReadOnlyList<CallerBox> RegisterCaller(CallerRequest request) => BindingAttestationTests.Orchestrator;

        public IReadOnlyList<CallerBox> EnsureRegistered(CallerRequest request) => RegisterCaller(request);

        public IReadOnlyList<CallerBox> CallerBoxes(CallerRequest request) => throw new NotSupportedException();

        public IReadOnlyList<DeciderBox> JoinDeciderBoxes(string cwd, string? sessionId = null) =>
            deciderDir is null ? throw new NotSupportedException() : [new DeciderBox("orchestrator@tm", "tm", sessionId) { Dir = deciderDir }];

        public AckIdsResult AckRouted(string dir, IReadOnlyList<string> ids, string by, string? sessionId = null)
        {
            acked.AddRange(ids);
            return new AckIdsResult(ids, [], firstToAck ? ids : []);
        }

        public string? ProcessCommand(int pid) => null;
    }

    internal static AdvisoryServices Advisory(HooksHostTests.Rig rig, List<string> acked, bool firstToAck = true, BindingAttestationTests.Daemon? daemon = null, string? deciderDir = null)
    {
        var mailbox = BindingAttestationTests.MailboxFor(rig);
        var hooks = MeshModule.CreateMailboxHooks(mailbox, MeshModule.CreateRelayObligations(mailbox, rig.Services.Hierarchy), rig.Services.Hierarchy, rig.Services.Clock);
        return new(
            mailbox, hooks, new AckingCallers(acked, firstToAck, deciderDir), MeshModule.CreateMailCore(rig.Services.Clock), null!, StopDirective.Instance, null!, null!,
            daemon ?? new BindingAttestationTests.Daemon(), null!, null!);
    }

    /// <summary>
    /// Registers the session with one orchestrator box and writes one daemon event entry per item: (id, event name, request id the
    /// event is about or null, text). Every entry is old enough to be due for a Stop to deliver.
    /// </summary>
    private static void Mailed(Scenario s, params (string Id, string Event, string? Request, string Text)[] entries)
    {
        var box = s.Rig.Sandbox.Root + "/box";
        _ = Directory.CreateDirectory(box);
        var registration = BindingAttestationTests.MailboxFor(s.Rig).RegistrationFile(Session);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(registration)!);
        var record = new JsonObject
        {
            ["session_id"] = Session,
            ["address"] = "orchestrator@tm",
            ["pid"] = Environment.ProcessId,
            ["boxes"] = new JsonObject { ["orchestrator@tm"] = box },
        };
        File.WriteAllBytes(registration, JsonText.ToUtf8(record, 0));
        var lines = entries.Select(e => Encoding.UTF8.GetString(JsonText.ToUtf8(
            new JsonObject
            {
                ["id"] = e.Id,
                ["kind"] = "event",
                ["from"] = "ahd",
                ["to"] = "orchestrator@tm",
                ["event"] = e.Event,
                ["text"] = e.Text,
                ["data"] = new JsonObject { ["request_id"] = e.Request is null ? JsonValue.Null : e.Request, ["member"] = JsonValue.Null, ["response_path"] = ResponsePathOf(s) },
                ["created"] = "2020-01-01T00:00:00.000Z",
            },
            0)));
        File.WriteAllText(box + "/entries.jsonl", string.Join('\n', lines) + "\n");
    }

    /// <summary>A ListAgents PostToolUse for <paramref name="session"/> (or a subagent of it) whose listing is <paramref name="lines"/>.</summary>
    private static void ListAgents(Scenario s, string[] lines, string session = Session, bool subagent = false)
    {
        var e = new JsonObject
        {
            ["hook_event_name"] = "PostToolUse",
            ["session_id"] = session,
            ["cwd"] = s.Rig.Sandbox.Root + "/proj",
            ["tool_name"] = "ListAgents",
            ["tool_input"] = new JsonObject(),
            ["tool_response"] = "Peer sessions\n" + string.Join('\n', lines),
        };
        if (subagent)
        {
            e["agent_id"] = "sub-1";
        }

        _ = s.Rig.Run("posttooluse-roster", Encoding.UTF8.GetString(JsonText.ToUtf8(e, 0)));
    }

    private static string Stop(Scenario s) => Stop(s, []);

    /// <summary>How many times a Stop registers a session with no registration, as one whose SessionStart fell in the import window has.</summary>
    private static int RegistrationsAtStop(string? agentType, string? agentId = null)
    {
        var s = Arrange();
        var registered = 0;
        var callers = new BindingAttestationTests.Callers(BindingAttestationTests.Orchestrator, onRegister: _ => registered++);
        var services = s.Rig.Services with { Advisory = Advisory(s.Rig, []) with { Callers = callers } };
        var e = new JsonObject { ["hook_event_name"] = "Stop", ["session_id"] = Session, ["cwd"] = s.Rig.Sandbox.Root + "/proj" };
        if (agentType is not null)
        {
            e["agent_type"] = agentType;
        }

        if (agentId is not null)
        {
            e["agent_id"] = agentId;
        }

        _ = s.Rig.Run("stop-orchestrator-liveness", Encoding.UTF8.GetString(JsonText.ToUtf8(e, 0)), services: () => services);
        return registered;
    }

    [Fact]
    public void ARoleSessionMissedInTheImportWindowRegistersAtItsNextStop() => Assert.Equal(1, RegistrationsAtStop("wrangl:implementor"));

    [Fact]
    public void AnOrchestratorMissedInTheImportWindowRegistersAtItsNextStop() => Assert.True(RegistrationsAtStop(null) >= 1);

    [Fact]
    public void NegativeASubagentStopRegistersNothing() => Assert.Equal(0, RegistrationsAtStop("wrangl:implementor", agentId: "a1"));

    [Theory]
    [InlineData("HC_TERMINAL_ID", "t7", "core")]
    [InlineData("TMUX_PANE", "%1", null)] // negative: a tmux pane is on no herdr server
    public void AStopRegistrationNamesTheHerdrFlavourOfTheSessionsEnv(string variable, string value, string? flavour)
    {
        var s = Arrange(env: new Dictionary<string, string> { [variable] = value });
        CallerRequest? seen = null;
        var callers = new BindingAttestationTests.Callers(BindingAttestationTests.Orchestrator, onRegister: r => seen = r);
        var services = s.Rig.Services with { Advisory = Advisory(s.Rig, []) with { Callers = callers } };
        var e = new JsonObject { ["hook_event_name"] = "Stop", ["session_id"] = Session, ["cwd"] = s.Rig.Sandbox.Root + "/proj", ["agent_type"] = "wrangl:implementor" };

        _ = s.Rig.Run("stop-orchestrator-liveness", Encoding.UTF8.GetString(JsonText.ToUtf8(e, 0)), services: () => services);

        Assert.NotNull(seen);
        Assert.Equal(flavour, seen!.HerdrFlavour);
    }

    [Fact]
    public void AnUnreadablePeerLogHoldsTheStopOnceAndTheSecondStopLeaves()
    {
        var s = Arrange(peerRoute: new PeerRecordsRoute(new StopPeerNudgeUnreadableStoreTests.UnreadablePeerLog(), new StopPeerNudgeUnreadableStoreTests.DbMode()));
        var services = s.Rig.Services with { Advisory = Advisory(s.Rig, [], true, null) };
        string Run(bool again)
        {
            var e = new JsonObject { ["hook_event_name"] = "Stop", ["session_id"] = Session, ["cwd"] = s.Rig.Sandbox.Root + "/proj", ["stop_hook_active"] = again };
            return Encoding.UTF8.GetString(s.Rig.Run("stop-orchestrator-liveness", Encoding.UTF8.GetString(JsonText.ToUtf8(e, 0)), services: () => services).Stdout.Span);
        }

        Assert.Contains("cannot be read", Run(again: false), StringComparison.Ordinal);
        Assert.DoesNotContain("cannot be read", Run(again: true), StringComparison.Ordinal);
    }

    private static string Stop(Scenario s, List<string> acked, bool firstToAck = true, BindingAttestationTests.Daemon? daemon = null)
    {
        var services = s.Rig.Services with { Advisory = Advisory(s.Rig, acked, firstToAck, daemon) };
        if (s.Sessions is not null)
        {
            services = services with { Sessions = s.Sessions };
        }

        var e = new JsonObject { ["hook_event_name"] = "Stop", ["session_id"] = Session, ["cwd"] = s.Rig.Sandbox.Root + "/proj" };
        var outcome = s.Rig.Run("stop-orchestrator-liveness", Encoding.UTF8.GetString(JsonText.ToUtf8(e, 0)), services: () => services);
        return Encoding.UTF8.GetString(outcome.Stdout.Span);
    }

    private static string ResponsePathOf(Scenario s) => $"{s.PoolDir}/msgs/{RequestId}--orchestrator--t1--response.md";

    /// <summary>A peer record of the given type, as the peer hooks write them into the shared peer file.</summary>
    private static void PeerRow(Scenario s, JsonObject row)
    {
        var pending = s.Rig.Services.Hierarchy.Peers.PeerPendingPath();
        File.AppendAllText(pending, Encoding.UTF8.GetString(JsonText.ToUtf8(row, 0)) + "\n");
    }

    /// <summary>The row a SendMessage that carried the response's token leaves at send time, written by another session.</summary>
    private static JsonObject SentRow(string responsePath) =>
        new() { ["type"] = "sent", ["session_id"] = "member-sess", ["msg_id"] = "m-1", ["to"] = "uds:/tmp/orch.sock", ["response"] = responsePath, ["resolved"] = new JsonArray { new JsonObject { ["task"] = RequestId, ["status"] = "pending" } }, ["ts"] = "2026-10-11T11:40:00.000Z" };

    /// <summary>A closed exchange whose response file holds a report older than the quiet period; <paramref name="mailed"/> puts a report entry for it into the session's box.</summary>
    private static Scenario ReportLanded(bool mailed, string? toAddr = null, string? author = null, bool doorbell = false)
    {
        var s = Arrange(toAddr: toAddr, doorbell: doorbell);
        var response = $"{s.PoolDir}/msgs/{RequestId}--orchestrator--t1--response.md";
        File.WriteAllText(response, "---\nid: " + RequestId + "\ntype: response\n" + (author is null ? string.Empty : "from: " + author + "\n") + "---\n\n## [0] tldr\n- [1] status: DONE\n");
        File.SetLastWriteTimeUtc(response, new DateTime(2026, 10, 11, 11, 0, 0, DateTimeKind.Utc));
        var box = s.Rig.Sandbox.Root + "/box";
        _ = Directory.CreateDirectory(box);
        var registration = BindingAttestationTests.MailboxFor(s.Rig).RegistrationFile(Session);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(registration)!);
        File.WriteAllBytes(
            registration,
            JsonText.ToUtf8(
                new JsonObject { ["session_id"] = Session, ["address"] = "orchestrator@tm", ["pid"] = Environment.ProcessId, ["boxes"] = new JsonObject { ["orchestrator@tm"] = box } },
                0));
        if (mailed)
        {
            File.WriteAllText(
                box + "/entries.jsonl",
                Encoding.UTF8.GetString(JsonText.ToUtf8(
                    new JsonObject { ["id"] = "e-rep", ["kind"] = "report", ["from"] = "implementor@tm", ["to"] = "orchestrator@tm", ["ref"] = response, ["created"] = "2026-10-11T11:30:00.000Z" },
                    0)) + "\n");
        }

        return s;
    }

    private sealed class NoTeamIntent : Ah.Config.ITeamIntent
    {
        public IReadOnlyList<string> Phrases() => [];

        public string? MatchedPhrase(string prompt) => null;
    }

    /// <summary>The prompt hook run for an idle notice about <c>tm-impl</c>, with the Stop scenario's services; returns what it injects.</summary>
    private static string IdleNotice(Scenario s, string? finishedAt = null)
    {
        var services = s.Rig.Services with { Advisory = Advisory(s.Rig, []) with { TeamIntent = new NoTeamIntent() } };
        var e = new JsonObject
        {
            ["hook_event_name"] = "UserPromptSubmit",
            ["session_id"] = Session,
            ["cwd"] = s.Rig.Sandbox.Root + "/proj",
            ["prompt"] = "[Cross-session idle notice] \"tm-impl\" " + (finishedAt is null ? "is idle now." : $"finished a turn at {finishedAt} and is idle now."),
        };
        var outcome = s.Rig.Run("userpromptsubmit-peer-tracking", Encoding.UTF8.GetString(JsonText.ToUtf8(e, 0)), services: () => services);
        return Encoding.UTF8.GetString(outcome.Stdout.Span);
    }

    private static int DaemonStartsOfAPrompt(Scenario s)
    {
        var daemon = new BindingAttestationTests.Daemon();
        var services = s.Rig.Services with { Advisory = Advisory(s.Rig, [], daemon: daemon) with { TeamIntent = new NoTeamIntent() } };
        var e = new JsonObject { ["hook_event_name"] = "UserPromptSubmit", ["session_id"] = Session, ["cwd"] = s.Rig.Sandbox.Root + "/proj", ["prompt"] = "carry on" };
        _ = s.Rig.Run("userpromptsubmit-peer-tracking", Encoding.UTF8.GetString(JsonText.ToUtf8(e, 0)), services: () => services);
        return daemon.EnsureCalls;
    }

    private static int TurnEndRows(Scenario s, string? deciderDir, Action<Scenario>? arrange = null)
    {
        File.Delete(s.RequestPath);
        arrange?.Invoke(s);
        var services = s.Rig.Services with { Advisory = Advisory(s.Rig, [], deciderDir: deciderDir) };
        var e = new JsonObject { ["hook_event_name"] = "Stop", ["session_id"] = Session, ["cwd"] = s.Rig.Sandbox.Root + "/proj" };
        _ = s.Rig.Run("stop-orchestrator-liveness", Encoding.UTF8.GetString(JsonText.ToUtf8(e, 0)), services: () => services);
        return s.Rig.Writer.Calls.Select((c, i) => (c, i)).Count(x => x.c == "peer-records.append"
            && JsonText.Parse(s.Rig.Writer.Arguments[x.i]).Member("rec")!.Value.Member("type") is { IsText: true } t && t.AsString() == "turn-end");
    }

    [Fact]
    public void AnOrchestratorStopThatIsNotBlockedLeavesOneTurnEndRow()
    {
        var s = Arrange();

        Assert.Equal(1, TurnEndRows(s, s.CwdDir));
    }

    [Fact]
    public void AStopThatIsBlockedLeavesNoTurnEndRow()
    {
        var s = Arrange();

        var rows = TurnEndRows(s, s.CwdDir, scene =>
        {
            var question = new JsonObject { ["question"] = "Alarm?", ["header"] = "Alarm", ["options"] = new JsonArray { new JsonObject { ["label"] = "yes" } } };
            File.WriteAllText(
                ParkedQuestions.PathIn(scene.CwdDir),
                Encoding.UTF8.GetString(JsonText.ToUtf8(ParkedQuestions.ParkedLine("q1", "2026-10-11T11:00:00.000Z", "tm", Session, "toolu_0", 0, question, "ah:R8", "reserved"), 0)) + "\n");
        });

        Assert.Equal(0, rows);
    }

    [Fact]
    public void ASubordinateRoleSessionWithDeciderBoxesLeavesNoTurnEndRow()
    {
        var s = Arrange();
        File.Delete(s.RequestPath);
        var advisory = Advisory(s.Rig, [], deciderDir: s.CwdDir) with { Callers = new BindingAttestationTests.Callers(BindingAttestationTests.Member, _ => { }) };
        var services = s.Rig.Services with { Advisory = advisory };
        var e = new JsonObject { ["hook_event_name"] = "Stop", ["session_id"] = Session, ["cwd"] = s.Rig.Sandbox.Root + "/proj", ["agent_type"] = "ah:implementor" };

        _ = s.Rig.Run("stop-orchestrator-liveness", Encoding.UTF8.GetString(JsonText.ToUtf8(e, 0)), services: () => services);

        Assert.DoesNotContain(s.Rig.Writer.Calls, c => c == "peer-records.append");
    }

    [Fact]
    public void ASessionWithoutDeciderBoxesLeavesNoTurnEndRow()
    {
        var s = Arrange();

        Assert.Equal(0, TurnEndRows(s, null));
    }

    [Fact]
    public void APromptOfARegisteredSessionStartsTheDaemon()
    {
        var s = Arrange();
        Mailed(s);

        Assert.Equal(1, DaemonStartsOfAPrompt(s));
    }

    [Fact]
    public void APromptOfARegisteredSessionWhoseTeamIsHeldNeverStartsTheDaemon()
    {
        var s = Arrange();
        Mailed(s);
        HoldFixture.Hold(s.Rig, "tm");

        Assert.Equal(0, DaemonStartsOfAPrompt(s));
    }

    /// <summary>The scenario's request tagged with team <c>team</c>, a member tree with a pool, and the team file beside the request.</summary>
    private static string TeamWithMemberTree(Scenario s)
    {
        var tree = MemberTree(s);
        File.WriteAllText(s.RequestPath, File.ReadAllText(s.RequestPath).Replace("team: null", "team: team", StringComparison.Ordinal));
        _ = Directory.CreateDirectory(s.PoolDir + "/teams");
        File.Copy(s.CwdDir + "/teams/team.json", s.PoolDir + "/teams/team.json", overwrite: true);
        return tree;
    }

    private static void FileResponseInPoolOf(string tree)
    {
        var response = $"{tree}/.claude/ah2/msgs/{RequestId}--orchestrator--t1--response.md";
        File.WriteAllText(response, "---\nid: " + RequestId + "\ntype: response\n---\n\n## [0] tldr\n- [1] status: DONE\n" + new string('x', 5000));
        File.SetLastWriteTimeUtc(response, new DateTime(2026, 10, 11, 11, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void AnIdleNoticeSeesAResponseFiledInAMembersWorkTreePool()
    {
        var s = Arrange(toAddr: "tm-impl");
        FileResponseInPoolOf(TeamWithMemberTree(s));

        var output = IdleNotice(s);

        Assert.Contains("landed but was never sent to you", output, StringComparison.Ordinal);
        Assert.DoesNotContain("with no report for", output, StringComparison.Ordinal);
    }

    [Fact]
    public void AnIdleNoticeStillCallsTheRequestOpenWhenNoPoolHoldsAResponse()
    {
        var s = Arrange(toAddr: "tm-impl");
        _ = TeamWithMemberTree(s);

        var output = IdleNotice(s);

        Assert.Contains("with no report for", output, StringComparison.Ordinal);
    }

    // ---- fewer-turns S7.5 (rows B-15, B-15a): the idle-with-no-report line is shown at most once before its check-in and once after.

    private const string NoReport = "with no report for";

    /// <summary>A scenario whose peer rows reach the file, so the throttle sees what it recorded; <paramref name="dueLater"/> puts the check-in after the rig's clock.</summary>
    private static Scenario IdleScenario(bool dueLater)
    {
        var s = dueLater ? Arrange(toAddr: "tm-impl", created: "2026-10-11T11:59:00+00:00", eta: "large") : Arrange(toAddr: "tm-impl");
        _ = TeamWithMemberTree(s);
        s.Rig.Writer.Executing = [.. s.Rig.Services.Hierarchy.StateDomains.Where(d => d.Name == "peer-records")];
        return s;
    }

    [Fact]
    public void B15TheIdleNoReportLineIsNotShownAgainBeforeItsCheckIn()
    {
        var s = IdleScenario(dueLater: true);

        Assert.Contains(NoReport, IdleNotice(s, "11:58"), StringComparison.Ordinal);
        Assert.DoesNotContain(NoReport, IdleNotice(s, "11:59"), StringComparison.Ordinal);
    }

    [Fact]
    public void B15aTheIdleNoReportLineIsShownOnceMoreAfterItsCheckInPassedThenNever()
    {
        var s = IdleScenario(dueLater: false);
        PeerRow(s, new JsonObject { ["type"] = "idle-noreport-shown", ["session_id"] = Session, ["name"] = "tm-impl", ["request"] = RequestId, ["phase"] = "before", ["ts"] = "2020-01-01T00:00:00.000Z" });

        Assert.Contains(NoReport, IdleNotice(s, "11:58"), StringComparison.Ordinal);
        Assert.DoesNotContain(NoReport, IdleNotice(s, "11:59"), StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeAShownLineForAnotherRequestDoesNotThrottleThisOne()
    {
        var s = IdleScenario(dueLater: false);
        PeerRow(s, new JsonObject { ["type"] = "idle-noreport-shown", ["session_id"] = Session, ["name"] = "tm-impl", ["request"] = "20260101-000000-zz99", ["phase"] = "after", ["ts"] = "2020-01-01T00:00:00.000Z" });

        Assert.Contains(NoReport, IdleNotice(s, "11:58"), StringComparison.Ordinal);
    }

    [Fact]
    public void WithAnOrchestratorBoxTheIdleNoReportLineGoesIntoTheDigest()
    {
        var s = IdleScenario(dueLater: false);
        var box = s.Rig.Sandbox.Root + "/box";
        _ = Directory.CreateDirectory(box);
        var registration = BindingAttestationTests.MailboxFor(s.Rig).RegistrationFile(Session);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(registration)!);
        File.WriteAllBytes(registration, JsonText.ToUtf8(new JsonObject { ["session_id"] = Session, ["address"] = "orchestrator@tm", ["pid"] = Environment.ProcessId, ["boxes"] = new JsonObject { ["orchestrator@tm"] = box } }, 0));

        var output = IdleNotice(s);

        Assert.Contains("ah digest (1):", output, StringComparison.Ordinal);
        Assert.Contains("- tm-impl is idle " + NoReport, output, StringComparison.Ordinal);
    }

    [Fact]
    public void AMembersPlainNoteIsShownInItsDigestAtItsNextPrompt()
    {
        var s = Arrange(doorbell: true);
        var box = s.Rig.Sandbox.Root + "/box";
        _ = Directory.CreateDirectory(box);
        File.WriteAllText(box + "/entries.jsonl", "{\"id\":\"n1\",\"kind\":\"note\",\"from\":\"orchestrator@tm\",\"created\":\"2026-10-12T00:00:00.000Z\",\"text\":\"news\",\"route\":\"pull\"}\n");
        var registration = BindingAttestationTests.MailboxFor(s.Rig).RegistrationFile(Session);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(registration)!);
        File.WriteAllBytes(registration, JsonText.ToUtf8(new JsonObject { ["session_id"] = Session, ["address"] = "imp@tm", ["pid"] = Environment.ProcessId, ["boxes"] = new JsonObject { ["imp@tm"] = box } }, 0));

        var output = IdleNotice(s);

        Assert.Contains("ah digest (1):", output, StringComparison.Ordinal);
        Assert.Contains("news", output, StringComparison.Ordinal);
    }

    private static IReadOnlyList<OpenBrief> OpenBriefs(Scenario s)
    {
        var box = s.Rig.Sandbox.Root + "/box";
        _ = Directory.CreateDirectory(box + "/acks");
        var registration = BindingAttestationTests.MailboxFor(s.Rig).RegistrationFile(Session);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(registration)!);
        File.WriteAllBytes(
            registration,
            JsonText.ToUtf8(
                new JsonObject { ["session_id"] = Session, ["address"] = "implementor@tm", ["pid"] = Environment.ProcessId, ["boxes"] = new JsonObject { ["implementor@tm"] = box } },
                0));
        File.WriteAllText(
            box + "/entries.jsonl",
            Encoding.UTF8.GetString(JsonText.ToUtf8(
                new JsonObject { ["id"] = "e-brief", ["kind"] = "brief", ["from"] = "orchestrator@tm", ["to"] = "implementor@tm", ["ref"] = s.RequestPath, ["created"] = "2026-10-11T11:30:00.000Z" },
                0)) + "\n");
        File.WriteAllText(box + "/acks/e-brief", Encoding.UTF8.GetString(JsonText.ToUtf8(new JsonObject { ["by"] = "prompt-hook", ["session_id"] = Session }, 0)));
        return Advisory(s.Rig, []).MailboxHooks.OpenMailBriefs(Session);
    }

    [Fact]
    public void AMailedBriefWhoseResponseIsFiledInAMembersWorkTreePoolIsNotOpen()
    {
        var s = Arrange();
        FileResponseInPoolOf(TeamWithMemberTree(s));

        Assert.Empty(OpenBriefs(s));
    }

    [Fact]
    public void AMailedBriefNobodyAnsweredIsStillOpen()
    {
        var s = Arrange();
        _ = TeamWithMemberTree(s);

        Assert.Equal(s.RequestPath, Assert.Single(OpenBriefs(s)).Ref);
    }

    [Fact]
    public void AnIdleNoticeSaysTheReportWasNeverSentWhenNobodyMailedIt()
    {
        var s = ReportLanded(mailed: false, toAddr: "tm-impl");

        var output = IdleNotice(s);

        Assert.Contains("landed but was never sent to you", output, StringComparison.Ordinal);
    }

    [Fact]
    public void AnIdleNoticeDoesNotSayTheReportWasNeverSentWhenItWasMailed()
    {
        var s = ReportLanded(mailed: true, toAddr: "tm-impl");

        var output = IdleNotice(s);

        Assert.DoesNotContain("never sent to you", output, StringComparison.Ordinal);
    }

    [Fact]
    public void AReportThatLandedAndWasNeverMailedIsSurfacedAsNeverSent()
    {
        var s = ReportLanded(mailed: false);

        var output = Stop(s);

        Assert.Contains("a report is ready for you to read", output, StringComparison.Ordinal);
    }

    [Fact]
    public void AReportThatLandedAndWasMailedToTheSessionIsNotSaidToBeUnsent()
    {
        var s = ReportLanded(mailed: true);

        var output = Stop(s);

        Assert.DoesNotContain("a report is ready for you to read", output, StringComparison.Ordinal);
    }

    private static string Prompt(Scenario s, string prompt)
    {
        var services = s.Rig.Services with { Advisory = Advisory(s.Rig, []) with { TeamIntent = new NoTeamIntent() } };
        var e = new JsonObject { ["hook_event_name"] = "UserPromptSubmit", ["session_id"] = Session, ["cwd"] = s.Rig.Sandbox.Root + "/proj", ["prompt"] = prompt };
        return Encoding.UTF8.GetString(s.Rig.Run("userpromptsubmit-peer-tracking", Encoding.UTF8.GetString(JsonText.ToUtf8(e, 0)), services: () => services).Stdout.Span);
    }

    private static void AckRow(Scenario s, string session, string requestId) =>
        File.AppendAllText(
            s.Rig.Services.Hierarchy.Peers.PeerPendingPath(),
            "{\"type\":\"report-acked\",\"session_id\":\"" + session + "\",\"request_id\":\"" + requestId + "\"}\n");

    private static void AddFiledExchange(Scenario s, string id)
    {
        File.WriteAllText(
            $"{s.PoolDir}/msgs/{id}--implementor--t1--request.md",
            $"---\nid: {id}\ntype: request\nto: implementor\nfrom: orchestrator\nslug: t1\nparent: null\nreason: null\neta: small\nto_name: peer-name\nfrom_name: null\nteam: null\ncreated: 2020-01-01T00:00:00-00:00\n---\n\n## [0] tldr\n- none\n");
        var response = $"{s.PoolDir}/msgs/{id}--orchestrator--t1--response.md";
        File.WriteAllText(response, $"---\nid: {id}\ntype: response\nfrom: implementor\n---\n\n## [0] tldr\n- [1] status: DONE\n");
        File.SetLastWriteTimeUtc(response, new DateTime(2026, 10, 11, 11, 0, 0, DateTimeKind.Utc));
        File.AppendAllText(
            s.Rig.Services.Hierarchy.Peers.PeerPendingPath(),
            "{\"type\":\"dispatch\",\"session_id\":\"" + Session + "\",\"request_id\":\"" + id + "\",\"to\":\"implementor\",\"created\":\"2020-01-01T00:00:00-00:00\",\"path\":\"" + $"{s.PoolDir}/msgs/{id}--implementor--t1--request.md" + "\"}\n");
    }

    [Fact]
    public void APromptListsAFiledResponseThatWasMailedButNeverAcknowledged()
    {
        var s = ReportLanded(mailed: true);

        var output = Prompt(s, "carry on");

        Assert.Contains("responses filed to you, not yet acknowledged (1)", output, StringComparison.Ordinal);
        Assert.Contains(RequestId, output, StringComparison.Ordinal);
        Assert.Contains("status: DONE", output, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAcknowledgedResponseIsNotListed()
    {
        var s = ReportLanded(mailed: true);
        AckRow(s, Session, RequestId);

        Assert.DoesNotContain("not yet acknowledged", Prompt(s, "carry on"), StringComparison.Ordinal);
        Assert.DoesNotContain("not yet acknowledged", Stop(s), StringComparison.Ordinal);
    }

    [Fact]
    public void AnAckFromAnotherSessionDoesNotClearTheResponse()
    {
        var s = ReportLanded(mailed: true);
        AckRow(s, "sess-other", RequestId);

        Assert.Contains("not yet acknowledged (1)", Prompt(s, "carry on"), StringComparison.Ordinal);
    }

    private static void OwnTeam(Scenario s)
    {
        _ = Directory.CreateDirectory(s.CwdDir + "/teams");
        File.WriteAllText(
            s.CwdDir + "/teams/tm.json",
            $"{{\"team\":\"tm\",\"orchestrator\":{{\"session_id\":\"{Session}\",\"pid\":{HarnessSessions.Pid},\"pid_start\":\"{HarnessSessions.Start}\"}},\"members\":[],\"streams\":{{}}}}");
    }

    private static void OrchestratorAckRow(Scenario s, string session, string team, string requestId) =>
        File.AppendAllText(
            s.Rig.Services.Hierarchy.Peers.PeerPendingPath(),
            "{\"type\":\"report-acked\",\"session_id\":\"" + session + "\",\"role\":\"orchestrator\",\"team\":\"" + team + "\",\"request_id\":\"" + requestId + "\"}\n");

    [Fact]
    public void AnOrchestratorAckOfTheSessionsTeamFromAnotherSessionClearsTheResponseAndAnotherTeamsDoesNot()
    {
        var cleared = ReportLanded(mailed: true);
        OwnTeam(cleared);
        OrchestratorAckRow(cleared, "sess-old", "tm", RequestId);
        var kept = ReportLanded(mailed: true);
        OwnTeam(kept);
        OrchestratorAckRow(kept, "sess-old", "other", RequestId);

        Assert.DoesNotContain("not yet acknowledged", Prompt(cleared, "carry on"), StringComparison.Ordinal);
        Assert.Contains("not yet acknowledged (1)", Prompt(kept, "carry on"), StringComparison.Ordinal);
    }

    [Fact]
    public void AnAckFromAnEarlierSessionOfTheSameOrchestratorClearsTheListingAndOneFromAMemberDoesNot()
    {
        var earlier = ReportLanded(mailed: true);
        RegisterOther(earlier, "sess-earlier", "orchestrator@tm");
        AckRow(earlier, "sess-earlier", RequestId);
        var member = ReportLanded(mailed: true);
        RegisterOther(member, "sess-member", "implementor@tm");
        AckRow(member, "sess-member", RequestId);

        Assert.DoesNotContain("not yet acknowledged", Prompt(earlier, "carry on"), StringComparison.Ordinal);
        Assert.Contains("not yet acknowledged (1)", Prompt(member, "carry on"), StringComparison.Ordinal);
    }

    private static JsonObject StampedAck(string session, string role, string team) =>
        new() { ["type"] = "report-acked", ["session_id"] = session, ["request_id"] = RequestId, ["role"] = role, ["team"] = team, ["ts"] = "2026-10-11T11:40:00.000Z" };

    [Theory]
    [InlineData("orchestrator", "tm", true)]
    [InlineData("implementor", "tm", false)]
    [InlineData("orchestrator", "other", false)]
    public void AnAckStampedWithTheOrchestratorRoleOfOurTeamClearsTheListingFromAnySessionAndNoOtherStampDoes(string role, string team, bool clears)
    {
        var s = ReportLanded(mailed: true);
        PeerRow(s, StampedAck("sess-unregistered", role, team));

        var output = Prompt(s, "carry on");

        Assert.Equal(!clears, output.Contains("not yet acknowledged (1)", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("orchestrator", "tm", false)]
    [InlineData("implementor", "tm", true)]
    [InlineData("orchestrator", "other", true)]
    public void AnAckStampedWithTheOrchestratorRoleOfOurTeamMeansTheReportReachedItAndNoOtherStampDoes(string role, string team, bool unsent)
    {
        var s = ReportLanded(mailed: false, toAddr: "tm-impl");
        PeerRow(s, StampedAck("sess-unregistered", role, team));

        Assert.Equal(unsent, Stop(s).Contains("a report is ready for you to read", StringComparison.Ordinal));
    }

    [Fact]
    public void AnotherSessionsDispatchIsNotListed()
    {
        var s = ReportLanded(mailed: true);
        var pending = s.Rig.Services.Hierarchy.Peers.PeerPendingPath();
        File.WriteAllText(pending, File.ReadAllText(pending).Replace("\"session_id\":\"" + Session + "\"", "\"session_id\":\"sess-other\"", StringComparison.Ordinal));

        Assert.DoesNotContain("not yet acknowledged", Prompt(s, "carry on"), StringComparison.Ordinal);
    }

    [Fact]
    public void TheListingNamesAtMostFiveAndCountsTheRest()
    {
        var s = ReportLanded(mailed: true);
        foreach (var n in new[] { "20260101-000000-t2aa", "20260101-000000-t3aa", "20260101-000000-t4aa", "20260101-000000-t5aa", "20260101-000000-t6aa", "20260101-000000-t7aa" })
        {
            AddFiledExchange(s, n);
        }

        var output = Prompt(s, "carry on");

        Assert.Contains("not yet acknowledged (7)", output, StringComparison.Ordinal);
        Assert.Contains("+2 more", output, StringComparison.Ordinal);
        Assert.Equal(5, output.Split("\\n- ").Length - 1);
    }

    private static void Filed(Scenario s, string id, DateTime at)
    {
        AddFiledExchange(s, id);
        File.SetLastWriteTimeUtc($"{s.PoolDir}/msgs/{id}--orchestrator--t1--response.md", at);
    }

    private static void Stamp(Scenario s, string text)
    {
        var home = s.Rig.Services.Environment.AhHome;
        _ = Directory.CreateDirectory(home);
        File.WriteAllText(home + "/install.json", text);
    }

    [Fact]
    public void ResponsesFiledBeforeTheInstallAreCountedNotListedAndOneAtTheInstallIsListed()
    {
        var s = ReportLanded(mailed: true);
        Filed(s, "20260101-000000-t2aa", new DateTime(2026, 10, 11, 11, 29, 59, DateTimeKind.Utc));
        Filed(s, "20260101-000000-t3aa", new DateTime(2026, 10, 11, 11, 30, 0, DateTimeKind.Utc));
        Filed(s, "20260101-000000-t4aa", new DateTime(2026, 10, 11, 11, 45, 0, DateTimeKind.Utc));
        Stamp(s, "{\"built_at\":\"2026-10-11T11:30:00Z\"}");

        var output = Prompt(s, "carry on");

        var listed = output[..output.IndexOf("ah: 2 older responses", StringComparison.Ordinal)];
        Assert.Contains("not yet acknowledged (2)", output, StringComparison.Ordinal);
        Assert.Contains("20260101-000000-t3aa", listed, StringComparison.Ordinal);
        Assert.Contains("20260101-000000-t4aa", listed, StringComparison.Ordinal);
        Assert.DoesNotContain(RequestId, listed, StringComparison.Ordinal);
        Assert.DoesNotContain("20260101-000000-t2aa", listed, StringComparison.Ordinal);
        Assert.Contains("ah: 2 older responses not listed; clear them with `ah msg ack <request id>` (" + RequestId + ", 20260101-000000-t2aa)", output, StringComparison.Ordinal);
    }

    [Fact]
    public void WhenEveryUnackedResponseIsOlderThanTheInstallOnlyTheCountLineIsGiven()
    {
        var s = ReportLanded(mailed: true);
        Stamp(s, "{\"built_at\":\"2026-10-11T11:30:00Z\"}");

        var output = Prompt(s, "carry on");

        Assert.Contains("1 older responses not listed", output, StringComparison.Ordinal);
        Assert.DoesNotContain("not yet acknowledged", output, StringComparison.Ordinal);
    }

    [Fact]
    public void TheOlderResponsesLineNamesAtMostTenIdsAndIsSaidOnce()
    {
        var s = ReportLanded(mailed: true);
        Stamp(s, "{\"built_at\":\"2026-10-11T11:30:00Z\"}");
        for (var i = 0; i < 11; i++)
        {
            Filed(s, $"20260101-000000-o{i:000}", new DateTime(2026, 10, 11, 11, 0, 0, DateTimeKind.Utc));
        }

        var first = Prompt(s, "carry on");
        var surfaced = new List<JsonValue>();
        foreach (var (call, args) in s.Rig.Writer.Calls.Zip(s.Rig.Writer.Arguments))
        {
            if (call != "peer-records.append")
            {
                continue;
            }

            var rec = JsonText.Parse(Encoding.UTF8.GetBytes(args)).Member("rec")!.Value;
            IEnumerable<JsonValue> recs = rec.IsArray ? rec.AsArray() : [rec];
            surfaced.AddRange(recs.Where(r => r.Member("type") is { IsText: true } t && t.AsString() == "surfaced"));
        }

        var pending = s.Rig.Services.Hierarchy.Peers.PeerPendingPath();
        foreach (var row in surfaced)
        {
            File.AppendAllText(pending, Encoding.UTF8.GetString(JsonText.ToUtf8(row, 0)) + "\n");
        }

        var second = Prompt(s, "carry on");

        Assert.True(surfaced.Count >= 12, $"expected a surfaced row per older response, saw {surfaced.Count}");

        Assert.Contains("12 older responses not listed", first, StringComparison.Ordinal);
        Assert.Contains("+2 more", first, StringComparison.Ordinal);
        Assert.Contains(RequestId, first, StringComparison.Ordinal);
        Assert.Equal(9, first.Split("20260101-000000-o").Length - 1);
        Assert.DoesNotContain("older responses", second, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not json")]
    [InlineData("{\"version\":\"2.5.0\"}")]
    public void WithoutAUsableInstallStampTheLastDayIsListedAndOlderIsCounted(string? stamp)
    {
        var s = ReportLanded(mailed: true);
        Filed(s, "20260101-000000-t2aa", new DateTime(2026, 10, 10, 11, 0, 0, DateTimeKind.Utc));
        if (stamp is not null)
        {
            Stamp(s, stamp);
        }

        var output = Prompt(s, "carry on");

        var listed = output[..output.IndexOf("ah: 1 older responses", StringComparison.Ordinal)];
        Assert.Contains("not yet acknowledged (1)", output, StringComparison.Ordinal);
        Assert.Contains(RequestId, listed, StringComparison.Ordinal);
        Assert.DoesNotContain("20260101-000000-t2aa", listed, StringComparison.Ordinal);
        Assert.Contains("1 older responses not listed", output, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnacknowledgedResponseIsSaidAtThePromptAndNeverAtStop()
    {
        var s = ReportLanded(mailed: true);

        Assert.Equal(string.Empty, Stop(s));
        Assert.Contains("not yet acknowledged (1)", Prompt(s, "carry on"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("orchestrator", false)]
    [InlineData("implementor", true)]
    public void AResponseTheOrchestratorWroteItselfIsNotSaidToBeAnUnsentPeerReport(string author, bool surfaced)
    {
        var s = ReportLanded(mailed: false, author: author);

        var output = Stop(s);

        Assert.Equal(surfaced, output.Contains("a report is ready for you to read", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("tm-impl@tm")]
    [InlineData("tm-impl")]
    public void AnOverdueDispatchWithNoToNameIsNamedByTheMemberItWasSentTo(string toAddr)
    {
        var s = Arrange(toAddr: toAddr, toName: null);

        var output = Stop(s);

        Assert.Contains(OverdueLine("tm-impl"), output, StringComparison.Ordinal);
        Assert.DoesNotContain("(unnamed)", output, StringComparison.Ordinal);
    }

    [Fact]
    public void APathBearingDispatchIsDueByItsOwnRowsClockNotByWhenItsRequestWasCreated()
    {
        var s = Arrange(created: At(60), rowCreated: At(3600));

        var output = Stop(s);

        Assert.Contains(OverdueLine("peer-name"), output, StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeAPathBearingDispatchWhoseRowIsRecentIsNotDueWhateverTheAgeOfItsRequest()
    {
        var s = Arrange(created: At(3600), rowCreated: At(60));

        var output = Stop(s);

        Assert.DoesNotContain(RequestId, output, StringComparison.Ordinal);
    }

    [Fact]
    public void APathBearingDispatchToAPaneMemberIsCheckedThroughDeliver()
    {
        var s = Arrange();
        File.WriteAllText(s.PoolDir + "/team.json", "{\"members\":[{\"role\":\"implementor\",\"name\":\"peer-name\",\"route\":\"pane\",\"kind\":\"codex\",\"transport_id\":\"w1:p1\"}]}\n");

        var output = Stop(s);

        Assert.Contains("a pane member: check it with", output, StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeAPathBearingDispatchToAMemberOnThePeerRouteGetsNoPaneWording()
    {
        var s = Arrange();
        File.WriteAllText(s.PoolDir + "/team.json", "{\"members\":[{\"role\":\"implementor\",\"name\":\"peer-name\",\"route\":\"peer\",\"transport_id\":\"w1:p1\"}]}\n");

        var output = Stop(s);

        Assert.Contains(OverdueLine("peer-name"), output, StringComparison.Ordinal);
        Assert.DoesNotContain("a pane member", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A running lane (<c>tm</c> team, sub-orchestrator <c>so1</c>) whose only request to the sub-orchestrator is old; the index keeps that request
    /// open as the lane's charter. <paramref name="reported"/> files an authored report beside it, as the live <c>release</c> requests have.
    /// </summary>
    private static Scenario LaneCharter(bool reported)
    {
        var s = Arrange(to: "sub-orchestrator", toName: "so1");
        File.Delete(s.RequestPath);
        var request = $"{s.PoolDir}/msgs/{RequestId}--sub-orchestrator--t1--request.md";
        File.WriteAllText(
            request,
            $"---\nid: {RequestId}\ntype: request\nto: sub-orchestrator\nfrom: orchestrator\nslug: t1\nparent: null\nreason: null\neta: small\nto_name: so1\nfrom_name: null\nteam: tm\ncreated: 2020-01-01T00:00:00-00:00\n---\n\n## [0] tldr\n- none\n");
        _ = Directory.CreateDirectory(s.PoolDir + "/teams");
        File.WriteAllText(s.PoolDir + "/teams/tm.json", $"{{\"orchestrator\":{{\"session_id\":\"{Session}\",\"pid\":{HarnessSessions.Pid},\"pid_start\":\"{HarnessSessions.Start}\"}}," + "\"streams\":{\"lane\":{\"state\":\"open\",\"sub_orchestrator\":\"so1\",\"opened\":\"2019-01-01T00:00:00-00:00\"}},\"members\":[]}");
        if (reported)
        {
            var response = $"{s.PoolDir}/msgs/{RequestId}--orchestrator--t1--response.md";
            File.WriteAllText(response, $"---\nid: {RequestId}\ntype: response\nfrom: sub-orchestrator\n---\n\n## [0] tldr\n- [1] status: Release: ready\n");
            File.SetLastWriteTimeUtc(response, new DateTime(2026, 10, 11, 11, 0, 0, DateTimeKind.Utc));
        }

        return s;
    }

    [Fact]
    public void ARunningLanesCharterThatHoldsALandedReportIsNotListedAsWaitingOnThePeer()
    {
        var output = Stop(LaneCharter(reported: true));

        Assert.Contains("a report is ready for you to read", output, StringComparison.Ordinal);
        Assert.DoesNotContain("has not answered", output, StringComparison.Ordinal);
    }

    [Fact]
    public void ARunningLanesCharterWithNoReportIsStillListedAsWaitingOnThePeer()
    {
        var output = Stop(LaneCharter(reported: false));

        Assert.Contains($"request {RequestId}", output, StringComparison.Ordinal);
        Assert.DoesNotContain("a report is ready for you to read", output, StringComparison.Ordinal);
    }

    private static string CharterLine => $"- sub-orchestrator \\\"so1\\\", request {RequestId}";

    private static Scenario PathlessLaneCharter(bool reported)
    {
        var s = Arrange(withPath: false, to: "sub-orchestrator", toName: "so1");
        File.Delete(s.RequestPath);
        File.WriteAllText(
            $"{s.PoolDir}/msgs/{RequestId}--sub-orchestrator--t1--request.md",
            $"---\nid: {RequestId}\ntype: request\nto: sub-orchestrator\nfrom: orchestrator\nslug: t1\nparent: null\nreason: null\neta: small\nto_name: so1\nfrom_name: null\nteam: team\ncreated: 2020-01-01T00:00:00-00:00\n---\n\n## [0] tldr\n- none\n");
        _ = Directory.CreateDirectory(s.PoolDir + "/teams");
        File.WriteAllText(
            s.PoolDir + "/teams/team.json",
            $"{{\"team\":\"team\",\"orchestrator\":{{\"session_id\":\"{Session}\",\"pid\":{HarnessSessions.Pid},\"pid_start\":\"{HarnessSessions.Start}\"}},\"members\":[],\"streams\":{{\"lane\":{{\"state\":\"open\",\"sub_orchestrator\":\"so1\",\"opened\":\"2019-01-01T00:00:00-00:00\"}}}}}}");
        if (reported)
        {
            var response = $"{s.PoolDir}/msgs/{RequestId}--orchestrator--t1--response.md";
            File.WriteAllText(response, $"---\nid: {RequestId}\ntype: response\nfrom: sub-orchestrator\n---\n\n## [0] tldr\n- [1] status: Release: ready\n");
            File.SetLastWriteTimeUtc(response, new DateTime(2026, 10, 11, 11, 0, 0, DateTimeKind.Utc));
        }

        return s;
    }

    [Fact]
    public void APathlessDispatchOfARunningLanesCharterThatHoldsALandedReportIsNotListedAsWaitingOnThePeer()
    {
        var output = Stop(PathlessLaneCharter(reported: true));

        Assert.Contains("a report is ready for you to read", output, StringComparison.Ordinal);
        Assert.Contains(CharterLine + ": ", output, StringComparison.Ordinal);
        Assert.Equal(2, output.Split(CharterLine).Length);
    }

    [Fact]
    public void NegativeAPathlessDispatchOfARunningLanesCharterWithNoReportIsStillListedAsWaitingOnThePeer()
    {
        var output = Stop(PathlessLaneCharter(reported: false));

        Assert.Contains(CharterLine, output, StringComparison.Ordinal);
        Assert.DoesNotContain("a report is ready for you to read", output, StringComparison.Ordinal);
    }

    /// <summary>The overdue list's line for the scenario's request, as it appears inside the hook's JSON reason.</summary>
    private static string OverdueLine(string name) => $"- implementor \\\"{name}\\\", request {RequestId}";

    [Theory]
    [InlineData("sub-orchestrator", true, true)]
    [InlineData("architect", true, false)]
    [InlineData("sub-orchestrator", false, true)]
    [InlineData("architect", false, false)]
    public void ALaneLeadsOverdueDispatchIsListedAndNegativeAMembersIsNot(string from, bool withPath, bool listed)
    {
        var s = withPath ? Arrange() : Arrange(withPath: false, rowCreated: At(2 * 3600));
        File.WriteAllText(s.RequestPath, File.ReadAllText(s.RequestPath).Replace("from: orchestrator", "from: " + from, StringComparison.Ordinal));

        Assert.Equal(listed, Stop(s).Contains(OverdueLine("peer-name"), StringComparison.Ordinal));
    }

    [Fact]
    public void AnOverdueDispatchWithNeitherAToNameNorAnAddressIsStillUnnamed()
    {
        var s = Arrange(toAddr: null, toName: null);

        var output = Stop(s);

        Assert.Contains(OverdueLine("(unnamed)"), output, StringComparison.Ordinal);
    }

    [Fact]
    public void AToNameOnTheRequestIsKeptWhenTheDispatchAlsoHasAnAddress()
    {
        var s = Arrange(toAddr: "tm-impl@tm", toName: "peer-name");

        var output = Stop(s);

        Assert.Contains(OverdueLine("peer-name"), output, StringComparison.Ordinal);
    }

    [Fact]
    public void ADaemonNudgeInTheRequestsPoolSatisfiesTheStopHookFromAnotherCwd()
    {
        var s = Arrange();
        Nudge(s, s.PoolDir, secondsAgo: 60);

        var output = Stop(s);

        Assert.Equal(string.Empty, output);
    }

    [Fact]
    public void ANudgeOnlyInTheCwdDirDoesNotSatisfyARequestInAnotherPool()
    {
        var s = Arrange();
        Nudge(s, s.CwdDir, secondsAgo: 60);

        var output = Stop(s);

        Assert.Contains("\"decision\":\"block\"", output, StringComparison.Ordinal);
        Assert.Contains(RequestId, output, StringComparison.Ordinal);
    }

    [Fact]
    public void AStopHookNudgeIsWrittenIntoTheRequestsPool()
    {
        var s = Arrange();

        var output = Stop(s);

        Assert.Contains("\"decision\":\"block\"", output, StringComparison.Ordinal);
        Assert.Equal(["liveness-nudge"], s.GateAppendsTo(s.PoolDir));
        Assert.DoesNotContain("liveness-nudge", s.GateAppendsTo(s.CwdDir));
    }

    [Fact]
    public void ADispatchWithoutAPathStillUsesTheCwdDir()
    {
        var s = Arrange(withPath: false);
        Nudge(s, s.CwdDir, secondsAgo: 60);

        var output = Stop(s);

        Assert.Equal(string.Empty, output);
    }

    /// <summary>A member work tree with its own pool, listed by a team file in the hook's own hierarchy dir.</summary>
    private static string MemberTree(Scenario s)
    {
        var tree = s.Rig.Sandbox.Root + "/wt1";
        _ = Directory.CreateDirectory(tree + "/.git");
        _ = Directory.CreateDirectory(tree + "/.claude/ah2/msgs");
        _ = Directory.CreateDirectory(s.CwdDir + "/teams");
        File.WriteAllText(
            s.CwdDir + "/teams/team.json",
            $"{{\"team\":\"team\",\"orchestrator\":{{\"session_id\":\"{Session}\",\"pid\":{HarnessSessions.Pid},\"pid_start\":\"{HarnessSessions.Start}\"}},\"members\":[{{\"name\":\"tm-impl\",\"role\":\"implementor\",\"expected_root\":\"{tree}\"}}],\"streams\":{{}}}}");
        return tree;
    }

    /// <summary>Adds <paramref name="count"/> more open requests, each with a dispatch row carrying its path, to the scenario's pool.</summary>
    private static void MoreDispatches(Scenario s, int count)
    {
        var pending = s.Rig.Services.Hierarchy.Peers.PeerPendingPath();
        for (var i = 0; i < count; i++)
        {
            var id = $"20260101-000000-m{i:D3}";
            var path = $"{s.PoolDir}/msgs/{id}--implementor--t1--request.md";
            File.WriteAllText(
                path,
                $"---\nid: {id}\ntype: request\nto: implementor\nfrom: orchestrator\nslug: t1\nparent: null\nreason: null\neta: small\nto_name: peer-name\nfrom_name: null\nteam: null\ncreated: 2020-01-01T00:00:00-00:00\n---\n\n## [0] tldr\n- none\n");
            var row = new JsonObject { ["type"] = "dispatch", ["session_id"] = Session, ["request_id"] = id, ["to"] = "implementor", ["path"] = path, ["created"] = "2020-01-01T00:00:00-00:00" };
            File.AppendAllText(pending, Encoding.UTF8.GetString(JsonText.ToUtf8(row, 0)) + "\n");
        }
    }

    private static int ListingsDuringStop(int extraDispatches)
    {
        var s = Arrange();
        _ = MemberTree(s);
        MoreDispatches(s, extraDispatches);
        var counted = CallCounting.Wrap(s.Rig.Services.Hierarchy.Core, out var counter);
        var services = s.Rig.Services with { Advisory = Advisory(s.Rig, []), Hierarchy = s.Rig.Services.Hierarchy with { Core = counted } };
        var e = new JsonObject { ["hook_event_name"] = "Stop", ["session_id"] = Session, ["cwd"] = s.Rig.Sandbox.Root + "/proj" };
        _ = s.Rig.Run("stop-orchestrator-liveness", Encoding.UTF8.GetString(JsonText.ToUtf8(e, 0)), services: () => services);
        return counter.Count(nameof(IHierarchyCore.ListExchanges));
    }

    [Fact]
    public void ThePoolsExchangesAreListedTheSameNumberOfTimesWhateverTheNumberOfDispatches()
    {
        var one = ListingsDuringStop(0);
        var four = ListingsDuringStop(3);

        Assert.True(one > 0);
        Assert.Equal(one, four);
    }

    [Fact]
    public void AResponseFiledInAMembersWorkTreePoolClosesARequestSittingInAnotherPool()
    {
        var s = Arrange();
        var tree = MemberTree(s);
        _ = Ah.TestKit.TestMsgStore.Plant(
            $"{tree}/.claude/ah2/msgs/{RequestId}--orchestrator--t1--response.md",
            "---\nid: " + RequestId + "\ntype: response\n---\n\n## [0] tldr\n- [1] status: DONE\n" + new string('x', 5000));

        var output = Stop(s);

        Assert.DoesNotContain("still waiting on these", output, StringComparison.Ordinal);
        Assert.Contains("a report is ready for you to read", output, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOverdueRequestNobodyAnsweredInAnyPoolIsStillListedWhenAMemberHasATree()
    {
        var s = Arrange();
        _ = MemberTree(s);

        var output = Stop(s);

        Assert.Contains(OverdueLine("peer-name"), output, StringComparison.Ordinal);
    }

    [Fact]
    public void ADispatchWithoutAPathSeesARequestFiledInAMembersWorkTreePool()
    {
        var s = Arrange(withPath: false);
        var tree = MemberTree(s);
        var tagged = File.ReadAllText(s.RequestPath).Replace("team: null", "team: team", StringComparison.Ordinal);
        File.Delete(s.RequestPath);
        File.WriteAllText($"{tree}/.claude/ah2/msgs/{RequestId}--implementor--t1--request.md", tagged);

        var output = Stop(s);

        Assert.Contains(OverdueLine("peer-name"), output, StringComparison.Ordinal);
    }

    [Fact]
    public void ARequestTrackedAsANoteIsNeverListedAsOutstandingAndAnOrdinaryOneIs()
    {
        var note = Arrange();
        File.WriteAllText(note.RequestPath, File.ReadAllText(note.RequestPath).Replace("reason: null", "reason: auto-tracked-note", StringComparison.Ordinal));
        var ordinary = Arrange();

        Assert.DoesNotContain(OverdueLine("peer-name"), Stop(note), StringComparison.Ordinal);
        Assert.Contains(OverdueLine("peer-name"), Stop(ordinary), StringComparison.Ordinal);
    }

    [Fact]
    public void ADispatchWithoutAPathOlderThanADayIsNotListed()
    {
        var s = Arrange(withPath: false, rowCreated: At(30 * 3600));
        var tree = MemberTree(s);
        var tagged = File.ReadAllText(s.RequestPath).Replace("team: null", "team: team", StringComparison.Ordinal);
        File.Delete(s.RequestPath);
        File.WriteAllText($"{tree}/.claude/ah2/msgs/{RequestId}--implementor--t1--request.md", tagged);

        var output = Stop(s);

        Assert.DoesNotContain(OverdueLine("peer-name"), output, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnackedCheckInMailAndADueClockGiveOnePromptForTheRequest()
    {
        var s = Arrange();
        Mailed(s, ("e-checkin", "check-in-due", RequestId, "MAILED-CHECK-IN-TEXT"));
        var acked = new List<string>();

        var output = Stop(s, acked);

        Assert.Contains("MAILED-CHECK-IN-TEXT", output, StringComparison.Ordinal);
        Assert.DoesNotContain("still waiting on these", output, StringComparison.Ordinal);
        Assert.Equal(["e-checkin"], acked);
        Assert.Equal(["liveness-nudge"], s.GateAppendsTo(s.PoolDir));
    }

    [Fact]
    public void ARunningTeamsOrchestratorStopStartsTheDaemonAndGetsItsDueMail()
    {
        var s = Arrange();
        Mailed(s, ("e-checkin", "check-in-due", RequestId, "MAILED-CHECK-IN-TEXT"));
        var daemon = new BindingAttestationTests.Daemon();

        var output = Stop(s, [], daemon: daemon);

        Assert.Contains("MAILED-CHECK-IN-TEXT", output, StringComparison.Ordinal);
        Assert.Equal(1, daemon.EnsureCalls);
    }

    [Fact]
    public void AHeldTeamsOrchestratorStopIsSilentAcksNothingAndNeverStartsTheDaemon()
    {
        var s = Arrange();
        Mailed(s, ("e-checkin", "check-in-due", RequestId, "MAILED-CHECK-IN-TEXT"));
        HoldFixture.Hold(s.Rig, "tm");
        var acked = new List<string>();
        var daemon = new BindingAttestationTests.Daemon();

        var output = Stop(s, acked, daemon: daemon);

        Assert.Equal(string.Empty, output);
        Assert.Empty(acked);
        Assert.Equal(0, daemon.EnsureCalls);
        Assert.Empty(s.GateAppendsTo(s.PoolDir));
    }

    [Fact]
    public void AMailedSilentEventAndADueClockGiveOnePromptForTheRequest()
    {
        var s = Arrange();
        Mailed(s, ("e-silent", "peer-silent", RequestId, "MAILED-SILENT-TEXT"));

        var output = Stop(s);

        Assert.Contains("MAILED-SILENT-TEXT", output, StringComparison.Ordinal);
        Assert.DoesNotContain("still waiting on these", output, StringComparison.Ordinal);
    }

    [Fact]
    public void OtherMailedEventsAreStillDelivered()
    {
        var s = Arrange();
        Mailed(
            s,
            ("e-checkin", "check-in-due", RequestId, "MAILED-CHECK-IN-TEXT"),
            ("e-blocked", "member-blocked", null, "MAILED-BLOCKED-TEXT"));
        var acked = new List<string>();

        var output = Stop(s, acked);

        Assert.Contains("MAILED-CHECK-IN-TEXT", output, StringComparison.Ordinal);
        Assert.Contains("MAILED-BLOCKED-TEXT", output, StringComparison.Ordinal);
        Assert.Equal(["e-blocked", "e-checkin"], acked.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void AnEventTheOtherCarrierAckedFirstIsNotDeliveredAgain()
    {
        var s = Arrange();
        Mailed(s, ("e-checkin", "check-in-due", RequestId, "MAILED-CHECK-IN-TEXT"));

        var output = Stop(s, [], firstToAck: false);

        Assert.DoesNotContain("MAILED-CHECK-IN-TEXT", output, StringComparison.Ordinal);
    }

    /// <summary>The real mailbox hooks, except that asking whether a report was delivered fails: a step that runs after the due event mail was claimed.</summary>
    public class FailingReportCheck : System.Reflection.DispatchProxy
    {
        public IMailboxHooks Inner { get; set; } = null!;

        public int Failures { get; private set; }

        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == "ReportReached")
            {
                Failures++;
                throw new InvalidOperationException("composing failed");
            }

            return targetMethod.Invoke(Inner, args);
        }
    }

    [Fact]
    public void AFailureAfterTheEventsWereClaimedStillHandsThemOver()
    {
        var s = ReportLanded(mailed: false);
        Mailed(s, ("e-checkin", "lane-escalated", RequestId, "MAILED-CHECK-IN-TEXT"));
        var acked = new List<string>();
        var advisory = Advisory(s.Rig, acked);
        var failing = System.Reflection.DispatchProxy.Create<IMailboxHooks, FailingReportCheck>();
        ((FailingReportCheck)(object)failing).Inner = advisory.MailboxHooks;
        var services = s.Rig.Services with { Advisory = advisory with { MailboxHooks = failing } };
        var e = new JsonObject { ["hook_event_name"] = "Stop", ["session_id"] = Session, ["cwd"] = s.Rig.Sandbox.Root + "/proj" };

        var outcome = s.Rig.Run("stop-orchestrator-liveness", Encoding.UTF8.GetString(JsonText.ToUtf8(e, 0)), services: () => services);

        Assert.True(((FailingReportCheck)(object)failing).Failures > 0, "the failing step was never reached");
        Assert.Contains("MAILED-CHECK-IN-TEXT", Encoding.UTF8.GetString(outcome.Stdout.Span), StringComparison.Ordinal);
        Assert.Equal(["e-checkin"], acked);
    }

    [Fact]
    public void AMailedCheckInForAnotherRequestDoesNotReplaceThisRequestsPrompt()
    {
        var s = Arrange();
        Mailed(s, ("e-checkin", "check-in-due", "20260101-000000-other", "MAILED-CHECK-IN-TEXT"));

        var output = Stop(s);

        Assert.Contains("MAILED-CHECK-IN-TEXT", output, StringComparison.Ordinal);
        Assert.Contains("still waiting on these", output, StringComparison.Ordinal);
        Assert.Contains(RequestId, output, StringComparison.Ordinal);
    }

    [Fact]
    public void AMailedBlockedEventForTheRequestDoesNotReplaceItsCheckIn()
    {
        var s = Arrange();
        Mailed(s, ("e-blocked", "member-blocked", RequestId, "MAILED-BLOCKED-TEXT"));

        var output = Stop(s);

        Assert.Contains("MAILED-BLOCKED-TEXT", output, StringComparison.Ordinal);
        Assert.Contains("still waiting on these", output, StringComparison.Ordinal);
    }

    private const string BusyReviewer = "  tm-reviewer [a1b2c3] · reviewer · busy · 3m";
    private const string IdleReviewer = "  tm-reviewer [a1b2c3] · reviewer · idle · 3m";

    private static System.Text.RegularExpressions.Match ListedLine(string line)
    {
        var method = typeof(Ah.Hooks.Activity.PostToolUseRoster).GetMethod("LineRegex", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        return ((System.Text.RegularExpressions.Regex)method.Invoke(null, null)!).Match(line);
    }

    [Theory]
    [InlineData("busy")]
    [InlineData("idle")]
    [InlineData("shell")]
    public void AListAgentsLineWithTheTagIsParsed(string tag)
    {
        var m = ListedLine($"  proj-reviewer [a1b2c3] · reviewer · {tag} · 3m");

        Assert.True(m.Success);
        Assert.Equal(tag, m.Groups[4].Value);
    }

    [Theory]
    [InlineData("shell", true, true)]
    [InlineData("busy", true, false)]
    [InlineData("idle", false, false)]
    public void ASeenRowSaysBusyForBusyAndShellAndMarksShellOnly(string tag, bool busy, bool shell)
    {
        var row = Ah.Hooks.Activity.PostToolUseRoster.SeenRow("proj-reviewer", "a1b2c3", "reviewer", tag, JsonValue.Null);

        Assert.Equal(busy, row.Get("busy")!.Value.AsBool());
        Assert.Equal(shell, row.Get("shell") is { IsBool: true } marked && marked.AsBool());
    }

    [Theory]
    [InlineData("gone")]
    [InlineData("shells")]
    public void AListAgentsLineWithAnyOtherTagIsNotParsed(string tag)
    {
        Assert.False(ListedLine($"  proj-reviewer [a1b2c3] · reviewer · {tag} · 3m").Success);
    }

    [Fact]
    public void ABusyListAgentsRecordsACheckedRowForTheOpenDispatch()
    {
        var s = Arrange(toAddr: "tm-reviewer@tm");

        ListAgents(s, [BusyReviewer]);

        var row = Assert.Single(s.GateRecordsIn(s.PoolDir));
        Assert.Equal("liveness-checked", row.Member("type")!.Value.AsString());
        Assert.Equal(Session, row.Member("session_id")!.Value.AsString());
        Assert.Equal(RequestId, row.Member("request_id")!.Value.AsString());
        Assert.Equal("tm-reviewer", row.Member("peer")!.Value.AsString());
        Assert.True(row.Member("busy")!.Value.AsBool());
        Assert.Empty(s.GateRecordsIn(s.CwdDir));
    }

    [Fact]
    public void ABusyListAgentsMatchesByRecipientNameWhenTheDispatchHasNoAddress()
    {
        var s = Arrange(to: "tm-reviewer");

        ListAgents(s, [BusyReviewer]);

        Assert.Equal(["liveness-checked"], s.GateAppendsTo(s.PoolDir));
    }

    [Fact]
    public void AnIdlePeerRecordsNone()
    {
        var s = Arrange(toAddr: "tm-reviewer@tm");

        ListAgents(s, [IdleReviewer]);

        Assert.Empty(s.GateRecordsIn(s.PoolDir));
    }

    [Fact]
    public void APeerWithNoOpenDispatchRecordsNone()
    {
        var s = Arrange(toAddr: "tm-reviewer@tm");

        ListAgents(s, ["  tm-someone-else [d4e5f6] · reviewer · busy · 3m"]);

        Assert.Empty(s.GateRecordsIn(s.PoolDir));
    }

    [Fact]
    public void AMembersListAgentsRecordsNone()
    {
        var s = Arrange(toAddr: "tm-reviewer@tm");

        ListAgents(s, [BusyReviewer], session: "sess-member");

        Assert.Empty(s.GateRecordsIn(s.PoolDir));
    }

    [Fact]
    public void ASubagentListAgentsRecordsNone()
    {
        var s = Arrange(toAddr: "tm-reviewer@tm");

        ListAgents(s, [BusyReviewer], subagent: true);

        Assert.Empty(s.GateRecordsIn(s.PoolDir));
    }

    [Fact]
    public void AShellLookAtADispatchNotYetDueAndNamedOnlyByToNameIsRecorded()
    {
        // b541: no due filter, shell counts as busy, and the peer is matched by to_name as the watch lists it
        var s = Arrange(toName: "tm-reviewer", created: Now.AddSeconds(-10).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));

        ListAgents(s, ["  tm-reviewer [a1b2c3] · reviewer · shell · 3m"]);

        Assert.Equal(["liveness-checked"], s.GateAppendsTo(s.PoolDir));
    }

    [Fact]
    public void ABusyListAgentsPostponesTheStopHooksCheckIn()
    {
        var s = Arrange(toAddr: "tm-reviewer@tm");
        s.Rig.PutGates(
            s.PoolDir,
            Encoding.UTF8.GetString(JsonText.ToUtf8(
                new JsonObject
                {
                    ["type"] = "liveness-checked",
                    ["session_id"] = Session,
                    ["request_id"] = RequestId,
                    ["peer"] = "tm-reviewer",
                    ["busy"] = true,
                    ["ts"] = Now.AddSeconds(-60).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
                },
                0)));

        Assert.Equal(string.Empty, Stop(s));
    }

    [Fact]
    public void AnIdleLookDoesNotPostponeTheStopHooksCheckIn()
    {
        var s = Arrange(toAddr: "tm-reviewer@tm");

        ListAgents(s, [IdleReviewer]);

        Assert.Contains("still waiting on these", Stop(s), StringComparison.Ordinal);
    }

    private static string At(int secondsAgo) => Now.AddSeconds(-secondsAgo).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static void BusyLook(Scenario s, string dir, int secondsAgo) =>
        s.Rig.PutGates(
            dir,
            Encoding.UTF8.GetString(JsonText.ToUtf8(
                new JsonObject { ["type"] = "liveness-checked", ["session_id"] = Session, ["request_id"] = RequestId, ["peer"] = "tm-reviewer", ["busy"] = true, ["ts"] = At(secondsAgo) },
                0)));

    [Fact]
    public void ABusyLookAfterTheCheckInHoldsTheSilenceWarningAFullThresholdFromTheLook()
    {
        var s = Arrange(toAddr: "tm-reviewer@tm", created: At(600));
        Nudge(s, s.PoolDir, secondsAgo: 300);
        BusyLook(s, s.PoolDir, secondsAgo: 200);

        Assert.Equal(string.Empty, Stop(s));
    }

    [Fact]
    public void AnIdleCheckInStillGetsTheSilenceWarningHalfAThresholdAfterIt()
    {
        var s = Arrange(toAddr: "tm-reviewer@tm", created: At(600));
        Nudge(s, s.PoolDir, secondsAgo: 300);

        Assert.Contains("still waiting on these", Stop(s), StringComparison.Ordinal);
    }

    [Fact]
    public void BusyLooksStopHoldingTheSilenceWarningAtTheBusyCeilingAfterTheBrief()
    {
        var s = Arrange(toAddr: "tm-reviewer@tm", created: At(7260));
        Nudge(s, s.PoolDir, secondsAgo: 480);
        BusyLook(s, s.PoolDir, secondsAgo: 60);

        Assert.Contains("still waiting on these", Stop(s), StringComparison.Ordinal);
    }

    [Fact]
    public void ABriefWithNoEtaHasTheSameCeilingAsASmallOne()
    {
        var s = Arrange(toAddr: "tm-reviewer@tm", created: At(7260), eta: "null");
        Nudge(s, s.PoolDir, secondsAgo: 480);
        BusyLook(s, s.PoolDir, secondsAgo: 60);

        Assert.Contains("still waiting on these", Stop(s), StringComparison.Ordinal);
    }

    [Fact]
    public void ABriefWithAMediumEtaIsStillHeldAtTheSameAge()
    {
        var s = Arrange(toAddr: "tm-reviewer@tm", created: At(960), eta: "medium");
        Nudge(s, s.PoolDir, secondsAgo: 480);
        BusyLook(s, s.PoolDir, secondsAgo: 400);

        Assert.Equal(string.Empty, Stop(s));
    }

    // ---------------------------------------------------------------- the progress nudge

    private const string ReportId = "20261016-120000-abcd";

    private static string Checklist(string updated) =>
        "<!-- ah-progress v1.0 kind=stream -->\n# main — g\nOwner: o   State: open\nUpdated: " + updated + "\n\n## Checklist\n### Done\n### In progress\n### Not started\n";

    private static string Journal(string at, string report) =>
        "<!-- ah-progress v1.0 kind=journal -->\n\n## " + at + " · m · slice " + ReportId + "\n- Status: s\n- Report: " + report + "\n<!-- ah-entry id=" + ReportId + " sha=x -->\n";

    private const string PlanAddI1 = """{"v":1,"ts":"2026-10-16T09:00:00Z","team":"tm","stream":"main","item":"i1","event":"add","title":"T1","by":"o","session":null}""" + "\n";

    /// <summary>The <c>main</c> stream's records in <paramref name="folder"/>: a checklist updated at <paramref name="updated"/>, a journal entry at
    /// 12:00:00Z linking a report from <paramref name="from"/>, and a plan holding <paramref name="plan"/>.</summary>
    private static void WriteStream(string folder, string updated, string from = "implementor", string plan = PlanAddI1)
    {
        _ = Directory.CreateDirectory(folder);
        File.WriteAllText(folder + "/main.md", Checklist(updated));
        File.SetLastWriteTimeUtc(folder + "/main.md", new DateTime(2026, 10, 16, 10, 0, 0, DateTimeKind.Utc));
        var pool = Path.GetDirectoryName(Path.GetDirectoryName(folder))!;
        TestMsgStore.Plant(pool + "/msgs/" + ReportId + "--orchestrator--slice--response.md", "---\nid: " + ReportId + "\ntype: response\nfrom: " + from + "\n---\n\n## [0] tldr\n- [1] status: DONE\n");
        File.WriteAllText(folder + "/main.journal.md", Journal("2026-10-16T12:00:00+00:00", ReportId + " (ah msg show " + ReportId + " response)"));
        File.WriteAllText(folder + "/main.plan.jsonl", plan);
    }

    /// <summary>A team <c>tm</c> whose <c>main</c> stream got a report at 12:00:00Z; the checklist says it was updated at <paramref name="updated"/>.</summary>
    private static Scenario Behind(string updated = "2026-10-16T11:00:00+00:00", string from = "implementor", string plan = PlanAddI1)
    {
        var rig = new HooksHostTests.Rig();
        var dir = rig.Services.Config.HierarchyDir(rig.Sandbox.Root + "/proj");
        WriteStream(dir + "/progress/tm", updated, from, plan);
        return new Scenario { Rig = rig, CwdDir = dir, PoolDir = dir, Sessions = new HarnessSessions() };
    }

    private static List<string> NudgedAppends(Scenario s) =>
        s.Rig.Writer.Calls.Zip(s.Rig.Writer.Arguments).Where(p => p.First == "progress.nudged-append").Select(p => p.Second).ToList();

    /// <summary>Gives the scenario team <c>tm</c> a record, so mail to its Orchestrator can be sent.</summary>
    private static void BehindTeam(Scenario s)
    {
        _ = Directory.CreateDirectory(s.CwdDir + "/teams");
        File.WriteAllText(s.CwdDir + "/teams/tm.json", "{\"team_id\":\"t-tm\",\"members\":[]}");
    }

    [Fact]
    public void ABehindNoteThatCannotBeSentEmitsNothingAndStillRecordsTheNudge()
    {
        var s = Behind();

        Assert.Equal(string.Empty, Stop(s));
        _ = Assert.Single(NudgedAppends(s));
    }

    [Fact]
    public void AReportNewerThanItsChecklistIsANoteToTheOwnBoxAndRecordsTheNudge()
    {
        var s = Behind();
        BehindTeam(s);

        var output = Stop(s);

        Assert.Equal(string.Empty, output);
        var note = Assert.Single(s.Rig.Writer.Arguments, static a => a.Contains("Stream main got", StringComparison.Ordinal));
        Assert.Contains("\"kind\":\"note\"", note, StringComparison.Ordinal);
        Assert.Contains("\"to\":\"orchestrator@tm\"", note, StringComparison.Ordinal);
        Assert.Contains($"Stream main got implementor report {ReportId}. If it finished an item, record it: `ah roster progress item done main <item> --commit <sha>` (open: i1).", note, StringComparison.Ordinal);
        var recorded = Assert.Single(NudgedAppends(s));
        Assert.Contains("\"stream\":\"main\"", recorded, StringComparison.Ordinal);
        Assert.Contains($"\"report_id\":\"{ReportId}\"", recorded, StringComparison.Ordinal);
    }

    /// <summary>The hook's cwd <c>proj</c> made a linked work tree of <c>main</c>; returns the scenario and the two progress folders (main pool, tree's own pool).</summary>
    private static (Scenario Scenario, string MainFolder, string TreeFolder) InLinkedWorkTree()
    {
        var rig = new HooksHostTests.Rig();
        var root = rig.Sandbox.Root;
        var tree = root + "/proj";
        _ = Directory.CreateDirectory(root + "/main/.git/worktrees/proj");
        _ = Directory.CreateDirectory(tree);
        File.WriteAllText(root + "/main/.git/worktrees/proj/gitdir", tree + "/.git\n");
        File.WriteAllText(tree + "/.git", "gitdir: " + root + "/main/.git/worktrees/proj\n");
        var mainPool = rig.Services.Config.MainHierarchyDir(tree)!;
        var treePool = rig.Services.Config.HierarchyDir(tree);
        Assert.NotEqual(mainPool, treePool);
        return (new Scenario { Rig = rig, CwdDir = treePool, PoolDir = mainPool }, mainPool + "/progress/tm", treePool + "/progress/tm");
    }

    private static void WriteBehind(string folder) => WriteStream(folder, "2026-10-16T11:00:00+00:00");

    [Fact]
    public void AnOrchestratorInALinkedWorkTreeIsNudgedAboutTheMainCheckoutsRecords()
    {
        var (s, mainFolder, _) = InLinkedWorkTree();
        WriteBehind(mainFolder);

        Assert.Equal(string.Empty, Stop(s));
        Assert.Contains("\"dir\":\"" + s.PoolDir + "\"", Assert.Single(NudgedAppends(s)), StringComparison.Ordinal);
    }

    [Fact]
    public void RecordsOnlyInTheWorkTreesOwnPoolAreNotTheTeamsRecords()
    {
        var (s, _, treeFolder) = InLinkedWorkTree();
        WriteBehind(treeFolder);

        var output = Stop(s);

        Assert.Equal(string.Empty, output);
        Assert.Empty(NudgedAppends(s));
    }

    [Fact]
    public void AReportAlreadyNudgedAboutIsNotNudgedAgain()
    {
        var s = Behind();
        File.WriteAllText(s.CwdDir + "/progress/tm/.nudged", "# ah-progress v1.0 kind=nudged\nmain " + ReportId + "\n");

        var output = Stop(s);

        Assert.Equal(string.Empty, output);
        Assert.Empty(NudgedAppends(s));
    }

    [Fact]
    public void ANewReportForTheSameStreamIsNudgedAboutEvenAfterAnEarlierOne()
    {
        var s = Behind();
        File.WriteAllText(s.CwdDir + "/progress/tm/.nudged", "# ah-progress v1.0 kind=nudged\nmain 20261016-110000-zzzz\n");

        Assert.Equal(string.Empty, Stop(s));
        Assert.Contains(ReportId, Assert.Single(NudgedAppends(s)), StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeAReportFromANonBuildingRoleIsNotNudgedAbout()
    {
        var s = Behind(from: "reviewer");

        Assert.Equal(string.Empty, Stop(s));
        Assert.Empty(NudgedAppends(s));
    }

    [Fact]
    public void NegativeAStreamWithNoOpenPlanItemIsNotNudgedAbout()
    {
        var s = Behind(plan: string.Empty);

        Assert.Equal(string.Empty, Stop(s));
        Assert.Empty(NudgedAppends(s));
    }

    [Theory]
    [InlineData("2026-10-16T12:00:00+00:00")]
    [InlineData("2026-10-16T12:30:00+00:00")]
    public void AChecklistUpdatedAtOrAfterTheReportIsNotNudgedAbout(string updated)
    {
        var s = Behind(updated);

        Assert.Equal(string.Empty, Stop(s));
        Assert.Empty(NudgedAppends(s));
    }

    [Fact]
    public void ATeamWithProgressOffIsNeverNudged()
    {
        var s = Behind();
        Ah.TestKit.TestProgressDocs.Off(s.Rig.Services.Environment.AhHome, s.CwdDir, "tm");

        Assert.Equal(string.Empty, Stop(s));
        Assert.Empty(NudgedAppends(s));
    }

    [Fact]
    public void AMemberSessionIsNeverNudged()
    {
        var s = Behind();
        var advisory = new AdvisoryServices(
            BindingAttestationTests.MailboxFor(s.Rig), null!, new BindingAttestationTests.Callers(BindingAttestationTests.Member, _ => { }), null!, null!, null!, null!, null!,
            new BindingAttestationTests.Daemon(), null!, null!);
        var services = s.Rig.Services with { Advisory = advisory };
        var e = new JsonObject
        {
            ["hook_event_name"] = "Stop",
            ["session_id"] = Session,
            ["cwd"] = s.Rig.Sandbox.Root + "/proj",
            ["agent_type"] = "ah:implementor",
        };

        var outcome = s.Rig.Run("stop-orchestrator-liveness", Encoding.UTF8.GetString(JsonText.ToUtf8(e, 0)), services: () => services);

        Assert.True(outcome.Stdout.IsEmpty);
        Assert.Empty(NudgedAppends(s));
    }

    [Fact]
    public void AJournalFromANewerFormatIsNeverNudgedAbout()
    {
        var s = Behind();
        File.WriteAllText(s.CwdDir + "/progress/tm/main.journal.md", Journal("2026-10-16T12:00:00+00:00", s.CwdDir + "/progress/tm/report.md").Replace("v1.0", "v2.0", StringComparison.Ordinal));

        Assert.Equal(string.Empty, Stop(s));
    }

    /// <summary>
    /// A brief for the scenario's request in the box of <c>tm-impl@tm</c>, and the ladder row that ends it in <paramref name="state"/>
    /// (none when null): what <c>ah msg withdraw</c> leaves behind.
    /// </summary>
    private static void Briefed(Scenario s, string id, string? state)
    {
        var box = s.Rig.Services.Environment.AhHome + "/mailbox/tm/tm-impl";
        _ = Directory.CreateDirectory(box);
        var entry = new JsonObject { ["v"] = 1, ["id"] = id, ["from"] = "orchestrator@tm", ["to"] = "tm-impl@tm", ["kind"] = "brief", ["route"] = "pull", ["created"] = "2026-10-11T11:00:00.000Z", ["ref"] = s.RequestPath };
        File.AppendAllText(box + "/entries.jsonl", Encoding.UTF8.GetString(JsonText.ToUtf8(entry, 0)) + "\n");
        if (state is not null)
        {
            var row = new JsonObject { ["id"] = id, ["rung"] = 2, ["state"] = state, ["by"] = "orchestrator@tm", ["reason"] = "r", ["seq"] = 1, ["ladder"] = true };
            File.AppendAllText(box + "/attempts.jsonl", Encoding.UTF8.GetString(JsonText.ToUtf8(row, 0)) + "\n");
        }
    }

    [Fact]
    public void AWithdrawnRequestNoLongerBlocksTheStopHookAndOneThatIsNotStillDoes()
    {
        var live = Arrange(toAddr: "tm-impl");
        Briefed(live, "b1", null);
        var withdrawn = Arrange(toAddr: "tm-impl");
        Briefed(withdrawn, "b1", "withdrawn");

        Assert.Contains("\"decision\":\"block\"", Stop(live), StringComparison.Ordinal);
        Assert.DoesNotContain("\"decision\":\"block\"", Stop(withdrawn), StringComparison.Ordinal);
        Assert.Empty(withdrawn.GateAppendsTo(withdrawn.PoolDir));
    }

    [Fact]
    public void ARequestWhoseRowHasNoPathIsDroppedToo()
    {
        var live = Arrange(withPath: false, toAddr: "tm-impl");
        Briefed(live, "b1", null);
        var withdrawn = Arrange(withPath: false, toAddr: "tm-impl");
        Briefed(withdrawn, "b1", "withdrawn");

        Assert.Contains("\"decision\":\"block\"", Stop(live), StringComparison.Ordinal);
        Assert.DoesNotContain("\"decision\":\"block\"", Stop(withdrawn), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("superseded")]
    [InlineData("declined")]
    public void ASupersededOrDeclinedBriefIsNotAWithdrawalAndStillBlocks(string state)
    {
        var s = Arrange(toAddr: "tm-impl");
        Briefed(s, "b1", state);

        Assert.Contains("\"decision\":\"block\"", Stop(s), StringComparison.Ordinal);
    }

    [Fact]
    public void ARequestWithAWithdrawnBriefAndALiveOneStillBlocks()
    {
        var s = Arrange(toAddr: "tm-impl");
        Briefed(s, "b1", "withdrawn");
        Briefed(s, "b2", null);

        Assert.Contains("\"decision\":\"block\"", Stop(s), StringComparison.Ordinal);
    }

    [Fact]
    public void AnIdleNoticeNamesAnOpenRequestUntilItIsWithdrawn()
    {
        var live = Arrange(toAddr: "tm-impl");
        Briefed(live, "b1", null);
        var withdrawn = Arrange(toAddr: "tm-impl");
        Briefed(withdrawn, "b1", "withdrawn");

        Assert.Contains("with no report for", IdleNotice(live), StringComparison.Ordinal);
        Assert.DoesNotContain("with no report for", IdleNotice(withdrawn), StringComparison.Ordinal);
    }

    /// <summary>The request's response filed beside it, last written 30 minutes before the rig's clock.</summary>
    private static void ResponseBesideRequest(Scenario s)
    {
        var response = $"{s.PoolDir}/msgs/{RequestId}--orchestrator--t1--response.md";
        File.WriteAllText(response, "---\nid: " + RequestId + "\ntype: response\n---\n\n## [0] tldr\n- [1] status: DONE\n");
        File.SetLastWriteTimeUtc(response, Now.AddMinutes(-30).UtcDateTime);
    }

    /// <summary>The local H:MM of the rig's clock an hour back, the time a notice names for the turn it reports.</summary>
    private static string TurnFinishedAnHourAgo(Scenario s)
    {
        var then = Now.AddHours(-1);
        var local = then.ToOffset(s.Rig.Services.Clock.LocalOffset(then));
        return string.Create(CultureInfo.InvariantCulture, $"{local.Hour}:{local.Minute:00}");
    }

    [Fact]
    public void AnIdleNoticeTakesTheReportTimeFromTheStoreWithNoFile()
    {
        var live = Arrange(toAddr: "tm-impl");
        Briefed(live, "b1", null);
        ResponseBesideRequest(live);
        Ah.TestKit.TestMsgStore.DropFiles(live.Rig.Sandbox.Root + "/ah", live.Rig.Sandbox.Root);

        Assert.Contains("reported at", IdleNotice(live, TurnFinishedAnHourAgo(live)), StringComparison.Ordinal);
    }

    [Fact]
    public void AnIdleNoticeIsStaleForAReportThatLandedAfterItAndNotForAWithdrawnRequestWhoseReportDid()
    {
        var live = Arrange(toAddr: "tm-impl");
        Briefed(live, "b1", null);
        ResponseBesideRequest(live);
        var withdrawn = Arrange(toAddr: "tm-impl");
        Briefed(withdrawn, "b1", "withdrawn");
        ResponseBesideRequest(withdrawn);

        Assert.Contains("reported at", IdleNotice(live, TurnFinishedAnHourAgo(live)), StringComparison.Ordinal);
        Assert.DoesNotContain("reported at", IdleNotice(withdrawn, TurnFinishedAnHourAgo(withdrawn)), StringComparison.Ordinal);
    }

    /// <summary>An Orchestrator with one fresh open request of its own team, so no check-in is due and only the wake rule can block.</summary>
    private static Scenario WorkInFlight(bool withRequest = true, bool doorbell = false)
    {
        var s = Arrange(withPath: false, created: Now.AddSeconds(-10).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture), eta: "large", doorbell: doorbell);
        File.WriteAllText(s.RequestPath, File.ReadAllText(s.RequestPath).Replace("team: null", "team: tm", StringComparison.Ordinal));
        if (!withRequest)
        {
            File.Delete(s.RequestPath);
        }

        return s;
    }

    private static string StopWithARequestFrom(string from)
    {
        var s = WorkInFlight();
        DaemonRunning(s);
        File.WriteAllText(s.RequestPath, File.ReadAllText(s.RequestPath).Replace("from: orchestrator", "from: " + from, StringComparison.Ordinal));
        return StopAgain(s, stopHookActive: false);
    }

    [Fact]
    public void ALaneLeadsOwnDispatchAsksForTheWakeSources() =>
        Assert.Contains("ah: work is running; start the background watcher", StopWithARequestFrom("sub-orchestrator"), StringComparison.Ordinal);

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void APlainSendIsWorkInFlightOnlyOnceArmed(bool armed, bool asked)
    {
        var s = WorkInFlight();
        DaemonRunning(s);
        File.WriteAllText(s.RequestPath, File.ReadAllText(s.RequestPath).Replace("reason: null", "reason: plain-send", StringComparison.Ordinal));
        if (armed)
        {
            PeerRow(s, new JsonObject { ["type"] = "plain-send-armed", ["session_id"] = Session, ["request_id"] = RequestId, ["at"] = "2026-10-11T11:40:00.000Z" });
        }

        Assert.Equal(asked, StopAgain(s, stopHookActive: false).Contains("start the background watcher", StringComparison.Ordinal));
    }

    [Fact]
    public void NegativeAMembersRequestAsksForNoWakeSource() =>
        Assert.DoesNotContain("start the background watcher", StopWithARequestFrom("architect"), StringComparison.Ordinal);

    private static void WakeFile(Scenario s, string name, int pid)
    {
        var dir = Path.Combine(s.Rig.Services.Environment.AhHome, "wake");
        _ = Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name), $"{{\"pid\":{pid.ToString(CultureInfo.InvariantCulture)},\"pid_start\":null,\"team\":\"tm\",\"started\":\"2026-10-11T12:00:00.000Z\"}}");
    }

    private static void DaemonRunning(Scenario s)
    {
        var dir = Path.Combine(s.Rig.Services.Environment.AhHome, "run");
        _ = Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "ahd.json"), $"{{\"pid\":{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}}}");
    }

    private static string StopAgain(Scenario s, bool stopHookActive)
    {
        var services = s.Rig.Services with { Advisory = Advisory(s.Rig, []) };
        var e = new JsonObject { ["hook_event_name"] = "Stop", ["session_id"] = Session, ["cwd"] = s.Rig.Sandbox.Root + "/proj", ["stop_hook_active"] = stopHookActive };
        return Encoding.UTF8.GetString(s.Rig.Run("stop-orchestrator-liveness", Encoding.UTF8.GetString(JsonText.ToUtf8(e, 0)), services: () => services).Stdout.Span);
    }

    private static readonly int Alive = Environment.ProcessId;
    private const int Dead = 2_000_000_000;

    [Fact]
    public void WorkInFlightWithNoWakeSourceBlocksOnceNamingBothCommands()
    {
        var s = WorkInFlight();
        DaemonRunning(s);

        var first = StopAgain(s, stopHookActive: false);
        var second = StopAgain(s, stopHookActive: true);

        Assert.Contains("\"decision\":\"block\"", first, StringComparison.Ordinal);
        Assert.Contains("Not running: the overdue timer and the ahd waiter.", first, StringComparison.Ordinal);
        Assert.Contains(" overdue --cwd ", first, StringComparison.Ordinal);
        Assert.Contains(" wake --cwd ", first, StringComparison.Ordinal);
        Assert.DoesNotContain("block", second, StringComparison.Ordinal);
    }

    [Fact]
    public void BothWakeSourcesLiveMeansTheStopIsAllowed()
    {
        var s = WorkInFlight();
        DaemonRunning(s);
        WakeFile(s, Session + ".json", Alive);
        WakeFile(s, Session + ".overdue.json", Alive);

        Assert.DoesNotContain("block", StopAgain(s, stopHookActive: false), StringComparison.Ordinal);
    }

    // fewer-turns S4.1: W-1 (the doorbell serves the session: no wake source is demanded) and W-3 (a held team); W-2 is the test above.
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, true)]
    public void TheDoorbellOrAHeldTeamSilencesTheWakeRule(bool served, bool held, bool stalePull)
    {
        var s = WorkInFlight(doorbell: served);
        DaemonRunning(s);
        if (served)
        {
            Mailed(s);
        }

        if (served && !stalePull)
        {
            FreshPull(s);
        }

        if (held)
        {
            HoldFixture.Hold(s.Rig, "tm");
        }

        // the negative row: a registered box whose pull is not fresh does not serve the session, so the rule still fires
        Assert.Equal(stalePull, StopAgain(s, stopHookActive: false).Contains("Not running: the overdue timer", StringComparison.Ordinal));
    }

    [Fact]
    public void AnActiveChainDownRowSilencesTheWakeRule()
    {
        var s = WorkInFlight();
        DaemonRunning(s);
        StopPeerNudgeReportTests.ChainDownRow(s.Rig, Session);

        Assert.DoesNotContain("block", StopAgain(s, stopHookActive: false), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("sess-other", "active")]
    [InlineData(Session, "cleared")]
    public void NegativeAChainDownRowThatIsNotThisSessionsActiveRowLeavesTheWakeRuleFiring(string session, string state)
    {
        var s = WorkInFlight();
        DaemonRunning(s);
        StopPeerNudgeReportTests.ChainDownRow(s.Rig, session, state);

        Assert.Contains("Not running: the overdue timer and the ahd waiter.", StopAgain(s, stopHookActive: false), StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyTheWaiterLiveBlocksNamingTheOverdueTimerAlone()
    {
        var s = WorkInFlight();
        DaemonRunning(s);
        WakeFile(s, Session + ".json", Alive);

        var text = StopAgain(s, stopHookActive: false);

        Assert.Contains("Not running: the overdue timer.", text, StringComparison.Ordinal);
        Assert.Contains(" overdue --cwd ", text, StringComparison.Ordinal);
        Assert.DoesNotContain(" wake --cwd ", text, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyTheOverdueTimerLiveWithTheDaemonUpBlocksNamingTheWaiterAlone()
    {
        var s = WorkInFlight();
        DaemonRunning(s);
        WakeFile(s, Session + ".overdue.json", Alive);

        var text = StopAgain(s, stopHookActive: false);

        Assert.Contains("Not running: the ahd waiter.", text, StringComparison.Ordinal);
        Assert.DoesNotContain(" overdue --cwd ", text, StringComparison.Ordinal);
    }

    [Fact]
    public void WithTheDaemonDownOnlyTheOverdueTimerIsRequired()
    {
        var withTimer = WorkInFlight();
        WakeFile(withTimer, Session + ".overdue.json", Alive);
        var without = WorkInFlight();

        Assert.DoesNotContain("block", StopAgain(withTimer, stopHookActive: false), StringComparison.Ordinal);
        var text = StopAgain(without, stopHookActive: false);
        Assert.Contains("Not running: the overdue timer.", text, StringComparison.Ordinal);
        Assert.DoesNotContain(" wake --cwd ", text, StringComparison.Ordinal);
    }

    [Fact]
    public void NoWorkInFlightMeansTheStopIsAllowedWithNoWakeSource()
    {
        var s = WorkInFlight(withRequest: false);
        DaemonRunning(s);

        Assert.DoesNotContain("wake source", StopAgain(s, stopHookActive: false), StringComparison.Ordinal);
    }

    [Fact]
    public void AWakeFileNamingADeadPidIsNoWakeSource()
    {
        var s = WorkInFlight();
        DaemonRunning(s);
        WakeFile(s, Session + ".json", Dead);
        WakeFile(s, Session + ".overdue.json", Dead);

        Assert.Contains("Not running: the overdue timer and the ahd waiter.", StopAgain(s, stopHookActive: false), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- the checklist rule

    private static string PlanRow(string at, string item, string ev, string? extra = null) =>
        $"{{\"v\":1,\"ts\":\"{at}\",\"team\":\"tm\",\"stream\":\"_run\",\"item\":\"{item}\",\"event\":\"{ev}\"{extra},\"by\":\"o\",\"session\":null}}";

    /// <summary>A team <c>tm</c> with the given plan rows and one pane member <c>impl-1</c> (role implementor) that has been in <paramref name="activity"/> since 11:00Z.</summary>
    private static Scenario Queue(IEnumerable<string> rows, string activity = "idle", bool subOrchestrator = false)
    {
        var rig = new HooksHostTests.Rig();
        var dir = rig.Services.Config.HierarchyDir(rig.Sandbox.Root + "/proj");
        _ = Directory.CreateDirectory(dir + "/progress/tm");
        File.WriteAllText(dir + "/progress/tm/_run.plan.jsonl", string.Join('\n', rows) + "\n");
        _ = Directory.CreateDirectory(dir + "/teams");
        File.WriteAllText(
            dir + "/teams/tm.json",
            "{\"team_id\":\"t-tm\"," + (subOrchestrator ? "\"parent\":{\"team\":\"top\"}," : string.Empty) + "\"members\":[{\"name\":\"impl-1\",\"role\":\"implementor\",\"route\":\"pane\",\"kind\":\"codex\"}]}\n");
        rig.PutActivity(dir, "pane-impl-1", "{\"activity\":\"" + activity + "\",\"at\":\"2026-10-11T11:00:00Z\"}\n");
        return new Scenario { Rig = rig, CwdDir = dir, PoolDir = dir, Sessions = new HarnessSessions() };
    }

    private static string[] Queued(string at = "2026-10-11T11:00:00Z") =>
        [PlanRow(at, "stab", "add", ",\"title\":\"Stabilise\""), PlanRow(at, "stab", "next"), PlanRow(at, "stab", "role", ",\"role\":\"implementor\"")];

    private static string[] Waiting(string event_ = "wait-orch") =>
        [
            PlanRow("2026-10-11T10:00:00Z", "ans", "add", ",\"title\":\"Answer\""),
            PlanRow("2026-10-11T10:00:00Z", "ans", "assign", ",\"holder\":\"impl-1\""),
            PlanRow("2026-10-11T10:00:00Z", "ans", "todo", ",\"todo\":\"answer impl-1: STATUS\""),
            PlanRow("2026-10-11T10:01:00Z", "ans", event_, ",\"note\":\"waiting\""),
        ];

    [Fact]
    public void AQueuedItemWithAnIdleMemberOfItsRoleBlocksOnceWithTheCommandFirstAndTheDispatchNamed()
    {
        var s = Queue(Queued());

        var output = Stop(s);

        Assert.Contains("\"decision\":\"block\"", output, StringComparison.Ordinal);
        Assert.Contains("\"reason\":\"ah: your checklist has items only you can move:", output, StringComparison.Ordinal);
        Assert.Contains("checklist show --cwd", output, StringComparison.Ordinal);
        Assert.Contains("- dispatch _run/stab to impl-1", output, StringComparison.Ordinal);
        Assert.Equal(string.Empty, StopAgain(s, stopHookActive: true));
    }

    [Theory]
    [InlineData("working")]
    [InlineData("queued-short")]
    [InlineData("blocked-item")]
    public void NegativeTheNearestQueueThatMustNotBlockPasses(string kind)
    {
        var rows = kind == "queued-short" ? Queued("2026-10-11T11:55:00Z")
            : kind == "blocked-item" ? [.. Queued(), PlanRow("2026-10-11T11:01:00Z", "stab", "block", ",\"note\":\"waiting on a fix\"")]
            : Queued();

        Assert.Equal(string.Empty, Stop(Queue(rows, activity: kind == "working" ? "working" : "idle")));
    }

    [Fact]
    public void AnActiveChainDownRowSilencesTheQueueRule()
    {
        var s = Queue(Queued());
        StopPeerNudgeReportTests.ChainDownRow(s.Rig, Session);

        Assert.Equal(string.Empty, Stop(s));
    }

    [Theory]
    [InlineData("sess-other", "active")]
    [InlineData(Session, "cleared")]
    public void NegativeAChainDownRowThatIsNotThisSessionsActiveRowLeavesTheQueueRuleFiring(string session, string state)
    {
        var s = Queue(Queued());
        StopPeerNudgeReportTests.ChainDownRow(s.Rig, session, state);

        Assert.Contains("- dispatch _run/stab to impl-1", Stop(s), StringComparison.Ordinal);
    }

    [Fact]
    public void AnItemWaitingOnTheOrchestratorBlocksOnceWithTheAnswerNamedAndAWaitingUserItemDoesNot()
    {
        var waiting = Queue(Waiting());
        var user = Queue(Waiting("wait-user"));

        var output = Stop(waiting);

        Assert.Contains("\"reason\":\"ah: your checklist has items only you can move:", output, StringComparison.Ordinal);
        Assert.Contains("- answer impl-1 (STATUS, ", output, StringComparison.Ordinal);
        Assert.Equal(string.Empty, StopAgain(waiting, stopHookActive: true));
        Assert.Equal(string.Empty, Stop(user));
    }

    [Fact]
    public void ASubOrchestratorIsHeldByTheSameRule()
    {
        Assert.Contains("- answer impl-1 (STATUS, ", Stop(Queue(Waiting(), subOrchestrator: true)), StringComparison.Ordinal);
    }

    [Fact]
    public void WithNoProcessTableTheStopRuleCannotTellWhoIsOnAJobSoItNamesNoDispatchAndWaitingItemsStillBlock()
    {
        var queued = Queue(Queued());
        var noTable = new Scenario { Rig = queued.Rig, CwdDir = queued.CwdDir, PoolDir = queued.PoolDir };
        var waiting = Queue(Waiting());
        var waitingNoTable = new Scenario { Rig = waiting.Rig, CwdDir = waiting.CwdDir, PoolDir = waiting.PoolDir };

        Assert.Contains("- dispatch _run/stab to impl-1", Stop(queued), StringComparison.Ordinal);
        Assert.Equal(string.Empty, Stop(noTable));
        Assert.Contains("- answer impl-1 (STATUS, ", Stop(waitingNoTable), StringComparison.Ordinal);
    }

    /// <summary>Copies the gate rows the hook wrote in <paramref name="from"/> into the gate file of <paramref name="to"/>: the rig's writer is a recorder, so a second Stop sees them only when a test persists them.</summary>
    private static void PersistGates(Scenario from, Scenario to, params string[] types)
    {
        foreach (var rec in from.GateRecordsIn(from.CwdDir).Where(r => r.Member("type") is { IsText: true } t && types.Contains(t.AsString())))
        {
            to.Rig.PutGates(to.CwdDir, Encoding.UTF8.GetString(JsonText.ToUtf8(rec, 0)));
        }
    }

    [Fact]
    public void ACheckInEventForAnExchangeThatClosedBeforeTheStopIsAckedResolvedAndNotShown()
    {
        var s = ReportLanded(mailed: false);
        Mailed(s, ("e-checkin", "check-in-due", RequestId, "MAILED-CHECK-IN-TEXT"));
        var acked = new List<string>();

        var output = Stop(s, acked);

        Assert.DoesNotContain("MAILED-CHECK-IN-TEXT", output, StringComparison.Ordinal);
        Assert.Empty(acked);
        var ack = s.Rig.Writer.Arguments.Select(static a => JsonText.Parse(Encoding.UTF8.GetBytes(a))).Single(a => a.Member("id") is { IsText: true } id && id.AsString() == "e-checkin");
        Assert.Equal("resolved", ack.Member("reason")!.Value.AsString());
    }

    [Fact]
    public void ACheckInEventForAnExchangeStillOpenIsShownUnderThePlainFirstLine()
    {
        var s = Arrange();
        Mailed(s, ("e-checkin", "check-in-due", RequestId, "MAILED-CHECK-IN-TEXT"));

        var output = Stop(s);

        Assert.Contains("ah: new team mail:", output, StringComparison.Ordinal);
        Assert.Contains("MAILED-CHECK-IN-TEXT", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    public void ACheckInOnTheSameOpenRequestBlocksThreeTimesThenIsSilent(int earlier, bool blocks)
    {
        var s = Arrange();
        for (var i = 0; i < earlier; i++)
        {
            Nudge(s, s.PoolDir, 40000 + (i * 1000));
        }

        var output = Stop(s);

        Assert.Equal(blocks, output.Contains("still waiting on these", StringComparison.Ordinal));
        Assert.Equal(blocks, output.Contains("\"decision\":\"block\"", StringComparison.Ordinal));
        Assert.Equal(blocks, output.Length > 0);

        // b13 fault 2: a request at the cap gets no more rows, so the gates file stops growing on every Stop
        Assert.Equal(blocks ? 1 : 0, NudgeRows(s));
    }

    private static int NudgeRows(Scenario s) =>
        s.Rig.Writer.Calls.Zip(s.Rig.Writer.Arguments).Count(static p => p.First == "gates.append" && p.Second.Contains("liveness-nudge", StringComparison.Ordinal));

    [Theory]
    [InlineData("gone-team", false)]
    [InlineData("team", true)]
    public void ARequestOfATeamWhoseFileIsGoneIsNoLongerListedButALiveTeamsStillIs(string team, bool listed)
    {
        var s = Arrange();
        File.WriteAllText(s.RequestPath, File.ReadAllText(s.RequestPath).Replace("team: null", "team: " + team, StringComparison.Ordinal));
        _ = Directory.CreateDirectory(s.PoolDir + "/teams");
        File.WriteAllText(
            s.PoolDir + "/teams/team.json",
            $"{{\"orchestrator\":{{\"session_id\":\"{Session}\",\"pid\":{HarnessSessions.Pid},\"pid_start\":\"{HarnessSessions.Start}\"}},\"members\":[]}}");
        for (var i = 0; i < 2; i++)
        {
            Nudge(s, s.PoolDir, 40000 + (i * 1000));
        }

        // The request is still watched, and so is checked in on and gets its row, only while its team lives.
        Assert.Equal(listed, Stop(s).Length > 0);
        Assert.Equal(listed ? 1 : 0, NudgeRows(s));
    }

    [Fact]
    public void AnItemInTheSameStateBlocksOnceAndOneInAnotherStateBlocksAgain()
    {
        var first = Queue(Waiting());
        var sameState = Queue(Waiting());
        var newState = Queue([.. Waiting(), PlanRow("2026-10-11T10:30:00Z", "ans", "todo", ",\"todo\":\"answer impl-1: QUESTION\"")]);

        var blocked = Stop(first);
        PersistGates(first, sameState, "queue-nudge");
        PersistGates(first, newState, "queue-nudge");

        Assert.Contains("ah: your checklist has items only you can move:", blocked, StringComparison.Ordinal);
        Assert.Equal(string.Empty, Stop(sameState));
        Assert.Contains("ah: your checklist has items only you can move:", Stop(newState), StringComparison.Ordinal);
    }

    [Fact]
    public void ARunningWatcherMakesTheStopQuietAndAJobSetBlocksOnceUntilItChanges()
    {
        var first = WorkInFlight();
        DaemonRunning(first);
        var again = WorkInFlight();
        DaemonRunning(again);
        var live = WorkInFlight();
        DaemonRunning(live);
        WakeFile(live, Session + ".json", Alive);
        WakeFile(live, Session + ".overdue.json", Alive);

        var blocked = StopAgain(first, stopHookActive: false);
        PersistGates(first, again, "wake-nudge", "wake-live");
        Assert.Contains("wake-nudge", again.Rig.GatesText(again.CwdDir), StringComparison.Ordinal);

        Assert.Contains("ah: work is running; start the background watcher (run_in_background: true):", blocked, StringComparison.Ordinal);
        Assert.Equal(string.Empty, StopAgain(again, stopHookActive: false));
        Assert.Equal(string.Empty, StopAgain(live, stopHookActive: false));
    }

    private static Scenario LandedReachedBy(string route)
    {
        var s = ReportLanded(mailed: route == "mailed", toAddr: "tm-impl", author: route == "self-written" ? "orchestrator" : null);
        switch (route)
        {
            case "sent":
                PeerRow(s, SentRow(ResponsePathOf(s)));
                break;
            case "seen" or "report-acked":
                PeerRow(s, new JsonObject { ["type"] = route, ["session_id"] = Session, ["request_id"] = RequestId, ["ts"] = "2026-10-11T11:40:00.000Z" });
                break;
        }

        return s;
    }

    [Theory]
    [InlineData("none", true)]
    [InlineData("mailed", false)]
    [InlineData("sent", false)]
    [InlineData("seen", false)]
    [InlineData("report-acked", false)]
    [InlineData("self-written", false)]
    public void TheStopHookAndThePromptHookAnswerAlikeWhetherAReportReachedTheOrchestrator(string route, bool unsent)
    {
        var stopped = Stop(LandedReachedBy(route));
        var idle = IdleNotice(LandedReachedBy(route));

        Assert.Equal(unsent, stopped.Contains("a report is ready for you to read", StringComparison.Ordinal));
        Assert.Equal(unsent, idle.Contains("landed but was never sent to you", StringComparison.Ordinal));
    }

    private static void RegisterOther(Scenario s, string session, string address, params string[] secondary)
    {
        var file = BindingAttestationTests.MailboxFor(s.Rig).RegistrationFile(session);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var all = new JsonArray();
        foreach (var a in new[] { address }.Concat(secondary))
        {
            all.Add(a);
        }

        File.WriteAllBytes(file, JsonText.ToUtf8(new JsonObject { ["session_id"] = session, ["address"] = address, ["addresses"] = all, ["pid"] = Environment.ProcessId, ["boxes"] = new JsonObject() }, 0));
    }

    [Fact]
    public void AnAckFromASessionThatHoldsTheOrchestratorAddressOnlyAsASecondaryOneDoesNotCount()
    {
        var s = ReportLanded(mailed: false, toAddr: "tm-impl");
        RegisterOther(s, "sess-lane", "orchestrator@child", "orchestrator@tm");
        PeerRow(s, new JsonObject { ["type"] = "report-acked", ["session_id"] = "sess-lane", ["request_id"] = RequestId, ["ts"] = "2026-10-11T11:40:00.000Z" });

        Assert.Contains("a report is ready for you to read", Stop(s), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("orchestrator@tm", false)]
    [InlineData("implementor@tm", true)]
    [InlineData("orchestrator@other", true)]
    [InlineData("", true)]
    public void AnAckFromAnEarlierSessionCountsOnlyWhenThatSessionWasThisTeamsOrchestrator(string ackerAddress, bool unsent)
    {
        var s = ReportLanded(mailed: false, toAddr: "tm-impl");
        if (ackerAddress.Length > 0)
        {
            RegisterOther(s, "sess-earlier", ackerAddress);
        }

        PeerRow(s, new JsonObject { ["type"] = "report-acked", ["session_id"] = "sess-earlier", ["request_id"] = RequestId, ["ts"] = "2026-10-11T11:40:00.000Z" });

        Assert.Equal(unsent, Stop(s).Contains("a report is ready for you to read", StringComparison.Ordinal));
    }

    private static Scenario Sent(Action<JsonObject> shape, params JsonObject[] after)
    {
        var s = ReportLanded(mailed: false, toAddr: "tm-impl");
        var row = SentRow(ResponsePathOf(s));
        shape(row);
        PeerRow(s, row);
        foreach (var more in after)
        {
            PeerRow(s, more);
        }

        return s;
    }

    private static bool NeverSent(Scenario s) => Stop(s).Contains("a report is ready for you to read", StringComparison.Ordinal);

    private static JsonObject Resolved(string task) => new() { ["task"] = task, ["status"] = "pending" };

    [Fact]
    public void ASentRowToAnotherMemberWithNothingResolvedDoesNotCount()
    {
        Assert.True(NeverSent(Sent(static r => { r["to"] = "implementor-2@tm"; r["resolved"] = new JsonArray(); })));
        Assert.False(NeverSent(Sent(static r => { r["to"] = "orchestrator@tm"; r["resolved"] = new JsonArray(); })));
    }

    [Fact]
    public void ASentRowThatResolvedThisRequestsObligationCountsWhateverItWentTo()
    {
        Assert.False(NeverSent(Sent(static r => { r["to"] = "uds:/tmp/earlier.sock"; r["resolved"] = new JsonArray { Resolved(RequestId) }; })));
        Assert.True(NeverSent(Sent(static r => { r["to"] = "uds:/tmp/earlier.sock"; r["resolved"] = new JsonArray { Resolved("20990101-000000-other") }; })));
    }

    [Fact]
    public void ASentRowWhoseDeliveryWasCutDoesNotCountAndACutOfAnotherSendDoes()
    {
        JsonObject Reopen(string msgId) => new() { ["type"] = "reopened", ["session_id"] = "member-sess", ["request_id"] = RequestId, ["msg_id"] = msgId, ["ts"] = "2026-10-11T11:41:00.000Z" };

        Assert.True(NeverSent(Sent(static _ => { }, Reopen("m-1"))));
        Assert.False(NeverSent(Sent(static _ => { }, Reopen("m-2"))));
    }

    [Fact]
    public void ASendAfterACutDeliveryCountsAgain()
    {
        var s = Sent(static _ => { }, new JsonObject { ["type"] = "reopened", ["session_id"] = "member-sess", ["request_id"] = RequestId, ["msg_id"] = "m-1", ["ts"] = "2026-10-11T11:41:00.000Z" });
        var again = SentRow(ResponsePathOf(s));
        again["msg_id"] = "m-3";
        PeerRow(s, again);

        Assert.False(NeverSent(s));
    }

    [Fact]
    public void ASentRowForAnotherResponseDoesNotCountAndOneForTheSameFileByAnotherPathDoes()
    {
        var other = ReportLanded(mailed: false);
        PeerRow(other, SentRow(other.PoolDir + "/msgs/20260101-000000-zzzz--orchestrator--t1--response.md"));
        var linked = ReportLanded(mailed: false);
        var link = linked.Rig.Sandbox.Root + "/linked-msgs";
        _ = Directory.CreateSymbolicLink(link, linked.PoolDir + "/msgs");
        PeerRow(linked, SentRow($"{link}/{RequestId}--orchestrator--t1--response.md"));

        Assert.Contains("a report is ready for you to read", Stop(other), StringComparison.Ordinal);
        Assert.DoesNotContain("a report is ready for you to read", Stop(linked), StringComparison.Ordinal);
    }

    [Fact]
    public void AReportLandedEventQueuedBeforeTheReportWasSentIsAckedResolvedAndNotShown()
    {
        var s = ReportLanded(mailed: false);
        Mailed(s, ("e-landed", "report-landed", RequestId, "LANDED-EVENT-TEXT"));
        PeerRow(s, SentRow(ResponsePathOf(s)));
        var acked = new List<string>();

        var output = Stop(s, acked);

        Assert.DoesNotContain("LANDED-EVENT-TEXT", output, StringComparison.Ordinal);
        Assert.Empty(acked);
        var ack = s.Rig.Writer.Arguments.Select(static a => JsonText.Parse(Encoding.UTF8.GetBytes(a)))
            .Single(a => a.Member("id") is { IsText: true } id && id.AsString() == "e-landed");
        Assert.Equal("resolved", ack.Member("reason")!.Value.AsString());
        Assert.Equal("stop-hook", ack.Member("by")!.Value.AsString());
    }

    [Fact]
    public void AReportLandedEventForAReportStillUnsentIsDeliveredAsBefore()
    {
        var s = ReportLanded(mailed: false);
        Mailed(s, ("e-landed", "report-landed", RequestId, "LANDED-EVENT-TEXT"));
        var acked = new List<string>();

        var output = Stop(s, acked);

        Assert.Contains("LANDED-EVENT-TEXT", output, StringComparison.Ordinal);
        Assert.Equal(["e-landed"], acked);
    }

    [Fact]
    public void AnEventWithNoRevalidatorIsDeliveredWhateverTheReportRowsSay()
    {
        var s = ReportLanded(mailed: false);
        Mailed(s, ("e-lane", "lane-escalated", RequestId, "LANE-EVENT-TEXT"));
        PeerRow(s, SentRow(ResponsePathOf(s)));
        var acked = new List<string>();

        var output = Stop(s, acked);

        Assert.Contains("LANE-EVENT-TEXT", output, StringComparison.Ordinal);
        Assert.Equal(["e-lane"], acked);
    }

    private const string BellLine = "ah: You have 2 messages (2 alarms). Run `ah msg next`.";

    private static IMailDoorbell Bell(Scenario s)
    {
        var advisory = Advisory(s.Rig, []);
        return MeshModule.CreateMailDoorbell(advisory.Mailbox, advisory.MailCore, s.Rig.Services.Files, s.Rig.Services.Environment, advisory.MailboxHooks);
    }

    [Fact]
    public void AStopWithQueuedEventMailGivesOneDoorbellLineInsteadOfTheEventBullets()
    {
        var s = Arrange(doorbell: true);
        Mailed(s, ("e-1", "check-in-due", "other-request", "MAILED-CHECK-IN-TEXT"), ("e-2", "member-bare", null, "MAILED-BARE-TEXT"));
        var acked = new List<string>();

        var output = Stop(s, acked);

        Assert.True(output.Contains(BellLine, StringComparison.Ordinal), output);
        Assert.DoesNotContain("MAILED-CHECK-IN-TEXT", output, StringComparison.Ordinal);
        Assert.DoesNotContain("MAILED-BARE-TEXT", output, StringComparison.Ordinal);
        Assert.Empty(acked);
    }

    [Fact]
    public void AStopAfterTheModRangThisTurnAddsNoDoorbellLine()
    {
        var s = Arrange(doorbell: true);
        Mailed(s, ("e-1", "check-in-due", "other-request", "MAILED-CHECK-IN-TEXT"), ("e-2", "member-bare", null, "MAILED-BARE-TEXT"));
        var nowMs = (double)Now.ToUnixTimeMilliseconds();
        var bell = Bell(s);
        Assert.NotNull(bell.Decide(new DoorbellRequest(Session, nowMs - 100_000, null, new Dictionary<string, HandedState>(), DoorbellCarrier.Prompt)).Ring);
        _ = bell.Decide(new DoorbellRequest(Session, nowMs - 90_000, nowMs - 95_000, new Dictionary<string, HandedState>(), DoorbellCarrier.Prompt));

        var output = Stop(s);

        Assert.DoesNotContain("ah: You have", output, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnackedRingIsShownByTheStopHookOnceAndNotAgainOnTheNextStop()
    {
        var s = Arrange(doorbell: true);
        Mailed(s, ("e-1", "check-in-due", "other-request", "MAILED-CHECK-IN-TEXT"), ("e-2", "member-bare", null, "MAILED-BARE-TEXT"));

        var first = Stop(s);
        var second = Stop(s);

        Assert.Contains(BellLine, first, StringComparison.Ordinal);
        Assert.DoesNotContain("ah: You have", second, StringComparison.Ordinal);
    }

    [Fact]
    public void AStopWithNoQueuedMailAddsNoDoorbellLine()
    {
        var s = Arrange(doorbell: true);
        Mailed(s);

        Assert.DoesNotContain("ah: You have", Stop(s), StringComparison.Ordinal);
    }

    /// <summary>The mod has pulled the session's box just now: the doorbell serves the session (a registered orchestrator box and a fresh pull).</summary>
    private static void FreshPull(Scenario s) =>
        BindingAttestationTests.MailboxFor(s.Rig).WriteHeartbeat(s.Rig.Sandbox.Root + "/box", "adapter", Session, 5000);

    // S-2: a check-in that is due while the doorbell serves the session reaches it as mail from the ahd clock, so Stop says nothing
    [Fact]
    public void ADueCheckInIsSilentWhileTheDoorbellServesTheSession()
    {
        var s = Arrange(doorbell: true);
        Mailed(s);
        FreshPull(s);

        Assert.Equal(string.Empty, Stop(s));
    }

    private static void UrgentBrief(Scenario s) =>
        File.AppendAllText(
            s.Rig.Sandbox.Root + "/box/entries.jsonl",
            Encoding.UTF8.GetString(JsonText.ToUtf8(new JsonObject { ["id"] = "e-urgent", ["kind"] = "brief", ["from"] = "implementor@tm", ["to"] = "orchestrator@tm", ["ref"] = s.RequestPath, ["created"] = "2026-10-11T11:30:00.000Z", ["route"] = "pull" }, 0)) + "\n");

    [Fact]
    public void AnUrgentItemAtAStopWithAFreshModIsLeftForTheMod()
    {
        var s = Arrange(doorbell: true);
        Mailed(s);
        FreshPull(s);
        UrgentBrief(s);

        Assert.Equal(string.Empty, Stop(s));
    }

    [Fact]
    public void AnUrgentItemAtAStopWithAStalePullBlocksWithTheRing()
    {
        var s = Arrange(doorbell: true);
        Mailed(s);
        UrgentBrief(s);

        var output = Stop(s);

        Assert.Contains("\"decision\":\"block\"", output, StringComparison.Ordinal);
        Assert.Contains("Run `ah msg next`", output, StringComparison.Ordinal);
    }

    [Fact]
    public void NoStopOutcomeSpeaksThroughASystemMessage()
    {
        var checkIn = Arrange(doorbell: true);
        Mailed(checkIn);
        FreshPull(checkIn);
        var stillOpen = Arrange();
        for (var i = 0; i < 3; i++)
        {
            Nudge(stillOpen, stillOpen.PoolDir, 40000 + (i * 1000));
        }

        var landed = ReportLanded(mailed: false, doorbell: true);
        FreshPull(landed);
        var scenarios = new[] { checkIn, stillOpen, landed, Arrange(), Behind(), ReportLanded(mailed: true) };

        Assert.All(scenarios, s => Assert.DoesNotContain("systemMessage", Stop(s), StringComparison.Ordinal));
    }

    // S-7: a queued item with an idle member blocks; the due check-in stays out of it, since the ahd clock mails it
    [Fact]
    public void AQueueBlockLeavesOutTheDueCheckInWhileTheDoorbellServesTheSession()
    {
        var arranged = Arrange(doorbell: true);
        Mailed(arranged);
        FreshPull(arranged);
        var dir = arranged.CwdDir;
        _ = Directory.CreateDirectory(dir + "/progress/tm");
        File.WriteAllText(dir + "/progress/tm/_run.plan.jsonl", string.Join('\n', Queued()) + "\n");
        _ = Directory.CreateDirectory(dir + "/teams");
        File.WriteAllText(dir + "/teams/tm.json", "{\"team_id\":\"t-tm\",\"members\":[{\"name\":\"impl-1\",\"role\":\"implementor\",\"route\":\"pane\",\"kind\":\"codex\"}]}\n");
        arranged.Rig.PutActivity(dir, "pane-impl-1", "{\"activity\":\"idle\",\"at\":\"2026-10-11T11:00:00Z\"}\n");
        var s = new Scenario { Rig = arranged.Rig, CwdDir = dir, PoolDir = arranged.PoolDir, Sessions = new HarnessSessions() };

        var output = Stop(s);

        Assert.Contains("\"decision\":\"block\"", output, StringComparison.Ordinal);
        Assert.Contains("your checklist has items only you can move", output, StringComparison.Ordinal);
        Assert.DoesNotContain("still waiting on these", output, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- b39: a working peer is not silent

    /// <summary>A check-in <paramref name="thresholds"/> eta thresholds old, for team member <c>tm-impl</c> whose activity record is <paramref name="activity"/>, last working <paramref name="workedSecondsAgo"/> ago.</summary>
    private static Scenario DueFor(double thresholds, string activity, int workedSecondsAgo)
    {
        var probe = new HooksHostTests.Rig();
        var threshold = probe.Services.Hierarchy.Messages.ThresholdFor("small");
        var created = Now.AddSeconds(-thresholds * threshold).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        var s = Arrange(withPath: false, toAddr: "tm-impl@tm", created: created, rowCreated: created, toName: "tm-impl");
        _ = Directory.CreateDirectory(s.CwdDir + "/teams");
        File.WriteAllBytes(s.CwdDir + "/teams/tm.json", JsonText.ToUtf8(new JsonObject { ["members"] = new JsonArray { new JsonObject { ["name"] = "tm-impl", ["session_id"] = "s-impl" } } }, 0));
        var worked = Now.AddSeconds(-workedSecondsAgo).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        s.Rig.PutActivity(s.CwdDir, "s-impl", JsonText.Stringify(new JsonObject { ["activity"] = activity, ["at"] = worked, ["state_since"] = worked, ["working_at"] = worked }, 0));
        return s;
    }

    [Fact]
    public void ADueCheckInIsHeldWhileThePeerIsFreshlyWorking()
    {
        var s = DueFor(thresholds: 1.5, "working", workedSecondsAgo: 60);
        Assert.DoesNotContain("still waiting on these", Stop(s), StringComparison.Ordinal);
        Assert.DoesNotContain("liveness-nudge", s.GateAppendsTo(s.CwdDir));
    }

    [Fact]
    public void ADueCheckInStillBlocksWhenThePeersWorkingWriteIsStale()
    {
        Assert.Contains("still waiting on these", Stop(DueFor(thresholds: 1.5, "working", workedSecondsAgo: 300)), StringComparison.Ordinal);
    }

    [Fact]
    public void ADueCheckInStillBlocksPastTheBusyCeilingEvenWhileThePeerIsWorking()
    {
        Assert.Contains("still waiting on these", Stop(DueFor(thresholds: 25, "working", workedSecondsAgo: 60)), StringComparison.Ordinal);
    }

    [Fact]
    public void ADueCheckInStillBlocksForAnIdlePeerWithAFreshWorkingWrite()
    {
        Assert.Contains("still waiting on these", Stop(DueFor(thresholds: 1.5, "idle", workedSecondsAgo: 60)), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- b67: a report already read ends the check-in

    /// <summary>A due check-in over the dispatch row's path, or over its request id when the row has none.</summary>
    private static Scenario DueOver(bool withPath) => withPath ? Arrange(toAddr: "tm-impl@tm") : DueFor(thresholds: 1.5, "idle", workedSecondsAgo: 60);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ADueCheckInIsDroppedOnceTheRequestsReportWasRead(bool withPath)
    {
        var s = DueOver(withPath);
        AckRow(s, Session, RequestId);

        Assert.DoesNotContain("still waiting on these", Stop(s), StringComparison.Ordinal);
        Assert.DoesNotContain("liveness-nudge", s.GateAppendsTo(s.CwdDir));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ADueCheckInStillBlocksWhenOnlyAnotherRequestsReportWasRead(bool withPath)
    {
        var s = DueOver(withPath);
        AckRow(s, Session, "20260101-000000-othr");

        Assert.Contains("still waiting on these", Stop(s), StringComparison.Ordinal);
    }

    [Fact]
    public void ADueCheckInStillBlocksWhenTheAckIsFromASessionThatWasNeverThisTeamsOrchestrator()
    {
        var s = DueOver(withPath: true);
        AckRow(s, "sess-unrelated", RequestId);

        Assert.Contains("still waiting on these", Stop(s), StringComparison.Ordinal);
    }

    /// <summary>Notes, for each live exchange listing, whether a listing scope was open around it.</summary>
    public class ScopeSpy : System.Reflection.DispatchProxy
    {
        internal IHierarchyCore Inner { get; set; } = null!;

        internal List<bool> LiveListings { get; } = [];

        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(IHierarchyCore.ListExchanges) && args is [_, true])
            {
                LiveListings.Add(Ah.Core.Files.ListingScope.IsOpen);
            }

            try
            {
                return targetMethod.Invoke(Inner, args);
            }
            catch (System.Reflection.TargetInvocationException e) when (e.InnerException is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();
                throw;
            }
        }
    }

    [Fact]
    public void TheStopsExchangeListingsAreReadInsideOneListingScope()
    {
        var s = Arrange(withPath: false);
        var core = System.Reflection.DispatchProxy.Create<IHierarchyCore, ScopeSpy>();
        var spy = (ScopeSpy)(object)core;
        spy.Inner = s.Rig.Services.Hierarchy.Core;
        var services = s.Rig.Services with { Advisory = Advisory(s.Rig, []), Hierarchy = s.Rig.Services.Hierarchy with { Core = core } };
        var e = new JsonObject { ["hook_event_name"] = "Stop", ["session_id"] = Session, ["cwd"] = s.Rig.Sandbox.Root + "/proj" };

        _ = s.Rig.Run("stop-orchestrator-liveness", Encoding.UTF8.GetString(JsonText.ToUtf8(e, 0)), services: () => services);

        Assert.NotEmpty(spy.LiveListings);
        Assert.All(spy.LiveListings, Assert.True);
    }

    // S-3: the nearest non-match, the same check-in with no fresh pull, still blocks
    [Fact]
    public void ADueCheckInStillBlocksWhenTheModHasNotPulled()
    {
        var s = Arrange(doorbell: true);
        Mailed(s);

        var output = Stop(s);

        Assert.Contains("\"decision\":\"block\"", output, StringComparison.Ordinal);
        Assert.Contains(RequestId, output, StringComparison.Ordinal);
    }

    // S-4 (D18): a landed report (filled, never sent) has no box entry, so the Stop block is its only carrier even when the doorbell serves the session
    [Fact]
    public void ALandedReportStillBlocksWhileTheDoorbellServesTheSessionAndNothingIsUrgent()
    {
        var s = ReportLanded(mailed: false, doorbell: true);
        FreshPull(s);

        var output = Stop(s);

        Assert.Contains("\"decision\":\"block\"", output, StringComparison.Ordinal);
        Assert.Contains("a report is ready for you to read", output, StringComparison.Ordinal);
        Assert.DoesNotContain("ah: You have", output, StringComparison.Ordinal);
    }

    // S-4a: a report sent and still unacked in the box is not landed; nothing blocks for it, the doorbell rings it
    [Fact]
    public void AReportSentAndUnackedInTheBoxDoesNotBlockWhileTheDoorbellServesTheSession()
    {
        var s = ReportLanded(mailed: true, doorbell: true);
        FreshPull(s);

        var output = Stop(s);

        Assert.DoesNotContain("a report is ready for you to read", output, StringComparison.Ordinal);
        Assert.DoesNotContain("ah: You have", output, StringComparison.Ordinal);
        Assert.DoesNotContain("\"decision\":\"block\"", output, StringComparison.Ordinal);
    }

    // S-4b: the same landed report with no fresh pull blocks too (unchanged)
    [Fact]
    public void ALandedReportBlocksWhenTheDoorbellDoesNotServeTheSession()
    {
        var s = ReportLanded(mailed: false, doorbell: true);

        var output = Stop(s);

        Assert.Contains("\"decision\":\"block\"", output, StringComparison.Ordinal);
        Assert.Contains("a report is ready for you to read", output, StringComparison.Ordinal);
    }

    // S-5: a landed report blocks whatever is in the box, and an urgent item there is left for the fresh mod to ring
    [Fact]
    public void ALandedReportBlocksAndCarriesNoDoorbellLineForAnUrgentItem()
    {
        var s = ReportLanded(mailed: false, doorbell: true);
        FreshPull(s);
        UrgentBrief(s);

        var output = Stop(s);

        Assert.Contains("\"decision\":\"block\"", output, StringComparison.Ordinal);
        Assert.Contains("a report is ready for you to read", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Run `ah msg next`", output, StringComparison.Ordinal);
    }
}
