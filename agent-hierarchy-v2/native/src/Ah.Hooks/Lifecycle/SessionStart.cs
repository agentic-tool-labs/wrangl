using System.Globalization;
using Ah.Abstractions;
using Ah.Config;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Core.Time;
using Ah.DaemonClient;
using Ah.Hierarchy;
using Ah.Hierarchy.StateCalls;
using Ah.Hooks.Gates;
using Ah.Hooks.Plumbing;
using Ah.Mesh;
using Ah.Roster;

namespace Ah.Hooks.Lifecycle;

/// <summary>
/// SessionStart context injection: the resolved role-to-model table and the orchestration protocol for a top-level session,
/// the role notice for a top-level <c>--agent &lt;hierarchy role&gt;</c> session (which also records its roster row), a setup
/// nudge when there is no usable config, and nothing for a subagent or a session that opted out. A session with a mailbox
/// address (a participant) additionally starts the daemon and hears the install-stamp and spawn-failed lines; any other
/// session leaves no such state behind. There is exactly one write to stdout, at the very end.
/// </summary>
[HookIdentity(HookIdentityKind.Grants)]
internal static class SessionStart
{
    /// <summary>The source of a mid-session delivery: it matches none of the harness's, so nothing keyed to one of those fires.</summary>
    internal const string OptInSource = "opt-in";

    /// <summary>
    /// The first line of a directive delivery: the harness caps <c>additionalContext</c> at 10,000 characters and hands the model a
    /// path and a 2,000-character preview of the rest.
    /// </summary>
    private const string LeadLine = "Wrangl Orchestrator directive. If this is cut to a preview, Read the saved file it names, in full, before anything else.";

    private const string CompactStateLine =
        "After compaction this HIERARCHY STATE block, not memory, is the truth about peers and dispatches: trust the HIERARCHY STATE block over memory.";

    private static readonly TimeSpan DaemonBudget = TimeSpan.FromMilliseconds(1500);

    public static HookOutcome Run(GateContext c)
    {
        var s = c.Services;
        if (s.Config.IsSubagent(c.Input))
        {
            return HookEmit.Silent;
        }

        var advisory = s.Advisory ?? throw new InvalidOperationException("the advisory services are not composed");
        var cwd = c.Cwd();
        var sessionIdValue = c.Input.Member("session_id") is { IsTruthy: true } sv ? sv : JsonValue.Null;
        var sessionId = c.Input.Text("session_id") ?? string.Empty;
        var source = c.Input.Text("source");
        var topLevel = s.Config.IsTopLevelAgentSession(c.Input);
        var ppid = s.Process.ParentPid;
        var span = PhaseTrace.Begin();
        var refusal = s.Sessions is { } sessions
            ? MeshModule.SessionStartRefusal(sessions.Table, ppid, sessionId, sessions.Sessions())
            : "no process table";
        PhaseTrace.End("sessionstart.refusal", span);
        if (refusal is not null)
        {
            return HookHost.Refused(c.Name, $"session start refused for {SafeText.Render(sessionId)} from pid {ppid}: {refusal}", s.ErrorLog);
        }

        var paneId = PaneOf(s);
        CarryOrchestratorFlag(c, source, cwd);

        // A bare session gets no text and leaves no record. A hand-launched role session is a member only if a team claims it, which only this unit can find out.
        if (!OrchestratorIdentity.In(c) && !AttributedRole(c, cwd))
        {
            return HookEmit.Silent;
        }

        // A subagent's payload names only its own agent, so what says its parent is the Orchestrator before it owns a team is this record.
        if (OrchestratorIdentity.LaunchedAsAgent(c) && !OrchestratorIdentity.Flagged(c))
        {
            try
            {
                _ = OrchestratorIdentity.Write(c, "on", cwd);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                c.LogError(e);
            }
        }

        string? context = null;
        var registered = false;

        JsonObject? registry = null;
        if (topLevel)
        {
            try
            {
                span = PhaseTrace.Begin();
                registry = s.Config.ResolveConfig(cwd, new ResolveOptions(SessionId: sessionId.Length > 0 ? sessionId : null));
                PhaseTrace.End("sessionstart.config", span);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                registry = null;
            }
        }

        var agentType = c.Input.Text("agent_type");
        string? role = topLevel && s.Config.HierarchyRoleOf(c.Input.Member("agent_type"), registry, cwd) is { IsText: true } rv && rv.AsString().Length > 0
            ? rv.AsString()
            : null;

        string? laneRole = null;
        if (role is null && topLevel && registry is not null && agentType is { Length: > 0 }
            && LaneRoleOf(s, registry, agentType) is { } foundLaneRole)
        {
            laneRole = foundLaneRole;
            RecordSessionRole(c, advisory, cwd, sessionIdValue, laneRole);
        }

        // Compaction drops the skill body out of context: a soft-gate reset row starts this session's bounce counts and its
        // skill-loaded record afresh.
        if (source == "compact" && sessionId.Length > 0)
        {
            try
            {
                s.Hierarchy.StateCalls.AppendGate(
                    s.Bootstrap.EnsureHierarchyDir(s.Config.HierarchyDir(cwd)),
                    new JsonObject { ["type"] = "soft-gate", ["outcome"] = "reset", ["session_id"] = sessionId });
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // the gate stays one-shot; never let this cost the injection
            }
        }

        if (role is not null)
        {
            context = advisory.Directive.BuildRoleSessionNotice(role, agentType, registry);
            RecordSessionRole(c, advisory, cwd, sessionIdValue, role);
            var misplaced = false;
            string? treeWarning = null;
            string? chainDownText = null;
            string? expectedRoot = null;
            JsonValue teamId = JsonValue.Null;
            try
            {
                var dir = s.Bootstrap.EnsureHierarchyDir(s.Config.HierarchyDir(cwd));
                // This hook fires while the orchestrator is still launching, before its team file lands, so the launcher's
                // WRANGL_TEAM_FILE answers from the start; the pane step and the role scan are the fallbacks for a session it did
                // not launch. A session that resolves to no team is a legitimate non-peer session, not a mismatch.
                span = PhaseTrace.Begin();
                var lookup = ResolveMemberRow(s, dir, cwd, role, paneId, ppid, sessionId);
                PhaseTrace.End("sessionstart.member-row", span);
                var attributed = lookup.Attributed;
                var team = lookup.Team;
                var viaEnv = lookup.ViaEnv;
                var marker = lookup.Marker;
                var member = lookup.Member;
                var expected = s.Roster.ExpectedRootFor(team, member, viaEnv ? s.Environment.GetVariable("WRANGL_EXPECTED_ROOT") : null);
                if (expected is { IsText: true } er && er.AsString().Length > 0)
                {
                    expectedRoot = er.AsString();
                }

                teamId = team.Member("team_id") ?? JsonValue.Null;
                misplaced = expectedRoot is not null && !s.Config.IsPlaced(s.Hierarchy.Messages.RealCwd(cwd), expectedRoot);
                var record = new JsonObject
                {
                    ["status"] = "up",
                    ["role"] = role,
                    ["session_id"] = sessionIdValue,
                    ["pid"] = ppid,
                    ["ppid"] = ppid,
                    ["cwd"] = cwd,
                    ["pane_id"] = PaneIdEnv(s),
                    ["tab_id"] = Env(s, "HERDR_TAB_ID"),
                    ["workspace_id"] = Env(s, "HERDR_WORKSPACE_ID"),
                };
                if (ppid > 1 && s.Sessions?.Table.StartTime(ppid) is { Length: > 0 } pidStart)
                {
                    record["pid_start"] = pidStart;
                }

                // The row gains `team` when a team resolved and never `name`: the roster is keyed by name or session id.
                if (attributed.IsTruthy)
                {
                    record["team"] = attributed.Member("teamName") ?? JsonValue.Null;
                }

                if (expectedRoot is not null)
                {
                    record["expected_root"] = expectedRoot;
                    record["misplaced"] = misplaced;
                    if (attributed.Member("teamName") is { IsText: true } treeTeam && member.Member("name") is { IsText: true } treeMember
                        && s.Hierarchy.Roster.TreeFactsOfTeam(dir, treeTeam.AsString()).FirstOrDefault(f => f.Name == treeMember.AsString()) is { } facts)
                    {
                        var block = HierarchyModule.TreeJson(facts);
                        record["co_tenants"] = block["co_tenants"];
                        record["cross_stream"] = block["cross_stream"];
                        treeWarning = HierarchyModule.TreeWarning(facts);
                    }
                }

                span = PhaseTrace.Begin();
                s.Hierarchy.StateCalls.AppendRosterRecord(dir, record, new StateCallOptions(Hook: true));
                PhaseTrace.End("sessionstart.roster-record", span);
                span = PhaseTrace.Begin();
                var boxes = advisory.Callers.RegisterCaller(
                    new CallerRequest(cwd, sessionId.Length > 0 ? sessionId : null, ppid, paneId, HerdrFlavour: Ah.Core.Mux.HerdrFlavour.SessionFlavour(s.Environment.GetVariable)));
                PhaseTrace.End("sessionstart.register", span);
                registered = boxes.Count > 0;
                if (registered)
                {
                    span = PhaseTrace.Begin();
                    StampMemberRow(s, advisory, dir, lookup, paneId, ppid, record.Get("pid_start") is { IsText: true } started ? started.AsString() : null, sessionId);
                    PhaseTrace.End("sessionstart.stamp", span);
                }

                if (registered && attributed.Member("teamName") is { IsText: true } sessionTeam && sessionTeam.AsString().Length > 0)
                {
                    var row = member.IsMap ? member : (JsonValue?)null;
                    span = PhaseTrace.Begin();
                    RecordSession(
                        c,
                        cwd,
                        sessionTeam.AsString(),
                        row?.Member("name") is { IsText: true } memberName ? memberName.AsString() : role,
                        role,
                        row?.Member("kind") is { IsText: true } memberKind ? memberKind.AsString() : "claude",
                        row?.Member("model") is { IsText: true } memberModel ? memberModel.AsString() : null,
                        sessionId,
                        "register");
                    PhaseTrace.End("sessionstart.record-session", span);
                }

                if (attributed.Member("teamName") is { IsText: true } downTeam && member.Member("name") is { IsText: true } downMember)
                {
                    span = PhaseTrace.Begin();
                    chainDownText = SessionStartChainDown.Notice(c, advisory, registry, role, downMember.AsString(), downTeam.AsString(), s.Config.MainHierarchyDir(cwd) is { Length: > 0 } mainHome ? mainHome : dir, sessionId);
                    PhaseTrace.End("sessionstart.chain-down", span);
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // The roster is best-effort and the notice still goes out. A refusal by the fence is a team the cut-over left to
                // another project, which the doctor must show.
                if (e.Message.StartsWith("fenced:", StringComparison.Ordinal))
                {
                    s.ErrorLog.Log(new HookErrorRecord("sessionstart", e.Message));
                }
            }

            // A nudge, not a gate: the peer is registered either way, and this is advisory text appended to the notice.
            if (misplaced)
            {
                var team = teamId is { IsText: true } t ? t.AsString() : JsConvert.ToJsString(teamId);
                context +=
                    "\n\n"
                    + $"Misplaced: this session is at `{cwd}`; team `{team}` expects `{expectedRoot}`.\n"
                    + $"Work there by absolute path: `cd {expectedRoot} || exit 1; <cmd>` inside each Bash call, "
                    + $"`git -C {expectedRoot} …` for git, and absolute paths for Read, Edit and Write. "
                    + "Switching your tree or branch is your Orchestrator's call: report BLOCKED or put it in open_questions; "
                    + "the call goes through once it approves. If you need to be respawned in the stream, "
                    + "report to the orchestrator.";
            }

            if (treeWarning is not null)
            {
                context += "\n\n" + treeWarning;
            }

            if (chainDownText is not null)
            {
                context += "\n\n" + chainDownText;
            }
        }
        else if (OrchestratorIdentity.In(c))
        {
            span = PhaseTrace.Begin();
            (context, registered) = PlainSession(c, advisory, cwd, sessionIdValue, sessionId, source, topLevel, ppid, paneId, laneRole);
            PhaseTrace.End("sessionstart.plain-session", span);
        }

        var text = Finish(c, advisory, cwd, sessionIdValue, sessionId, source, ppid, context, registered);
        if (text is not null && role is null && TopOrchestratorDirective(c))
        {
            text = WithLeadLine(text);
        }

        return text is not null ? HookEmit.AdditionalContext("SessionStart", text) : HookEmit.Silent;
    }

    /// <summary>The longest SessionStart output this plugin aims for; the harness cuts one over 10,000 characters to a preview.</summary>
    internal const int OutputCap = 9500;

    /// <summary>The output as sent: with <see cref="LeadLine"/> first only when it is over <see cref="OutputCap"/>.</summary>
    internal static string WithLeadLine(string text) => text.Length > OutputCap ? LeadLine + "\n\n" + text : text;

    /// <summary>Whether this unit refuses the payload's start (no process table, or the session does not belong to the hook's harness): the units beside it then inject nothing it would not.</summary>
    internal static bool StartRefused(GateContext c) =>
        c.Services.Sessions is not { } sessions
        || MeshModule.SessionStartRefusal(sessions.Table, c.Services.Process.ParentPid, c.Input.Text("session_id") ?? string.Empty, sessions.Sessions()) is not null;

    /// <summary>Whether the session's directive is the top-level Orchestrator's resident core and index: not a Sub-Orchestrator's or a Deputy's.</summary>
    internal static bool TopOrchestratorDirective(GateContext c) => !DeclaredSubOrchestrator.Is(c) && !DeclaredSubOrchestrator.IsDeputy(c);

    /// <summary>
    /// What a session that has just opted in gets, in the turn it opted in: the same text a flagged session gets at its next start, or
    /// null when there is none. The caller has written the flag.
    /// </summary>
    internal static string? OptInPayload(GateContext c, string cwd)
    {
        var s = c.Services;
        var advisory = s.Advisory ?? throw new InvalidOperationException("the advisory services are not composed");
        var sessionId = c.Input.Text("session_id") ?? string.Empty;
        var sessionIdValue = c.Input.Member("session_id") is { IsTruthy: true } sv ? sv : JsonValue.Null;
        var ppid = s.Process.ParentPid;
        var (context, registered) = PlainSession(c, advisory, cwd, sessionIdValue, sessionId, OptInSource, s.Config.IsTopLevelAgentSession(c.Input), ppid, PaneOf(s), null);
        var text = Finish(c, advisory, cwd, sessionIdValue, sessionId, OptInSource, ppid, context, registered);

        // No SessionStart fires for an opt-in, so the directive, index and rules hooks will not bring theirs: the four outputs, in their order.
        var parts = new[]
        {
            text,
            text is null ? null : SessionDirective.Text(c, cwd),
            text is null ? null : SessionIndex.Text(c, cwd),
            SessionRules.StartText(c, cwd, OptInSource, text is not null),
        }.OfType<string>().ToList();
        if (parts.Count == 0)
        {
            return null;
        }

        var payload = string.Join("\n\n", parts);
        return TopOrchestratorDirective(c) ? WithLeadLine(payload) : payload;
    }

    /// <summary>The non-role branch: the setup nudge of an unconfigured repo, the directive and state of an enabled one, nothing in a disabled one.</summary>
    private static (string? Context, bool Registered) PlainSession(
        GateContext c, AdvisoryServices advisory, string cwd, JsonValue sessionIdValue, string sessionId, string? source, bool topLevel, int ppid, string? paneId, string? laneRole)
    {
        var resolved = c.Services.Config.ResolveConfig(cwd, new ResolveOptions(SessionId: sessionId.Length > 0 ? sessionId : null));
        if (resolved.Get("configured") is not { IsTruthy: true })
        {
            return (advisory.Directive.BuildNudge(resolved), false);
        }

        return resolved.Get("enabled") is { IsTruthy: true }
            ? EnabledSession(c, advisory, resolved, cwd, sessionIdValue, sessionId, source, topLevel, ppid, paneId, laneRole)
            : (null, false);
    }

    /// <summary>The tail of every delivery: the participant's lines and daemon, the status records, the identity, the root and error lines. The text to inject, or null.</summary>
    private static string? Finish(
        GateContext c, AdvisoryServices advisory, string cwd, JsonValue sessionIdValue, string sessionId, string? source, int ppid, string? context, bool registered)
    {
        var s = c.Services;

        // An empty context is the top-level Orchestrator's, whose directive is another unit's: it still gets the lines below.
        static string With(string? context, string line) => string.IsNullOrEmpty(context) ? line : $"{context}\n\n{line}";

        // Only a participant (a non-subagent session with a mailbox address) touches the daemon or the install stamp.
        if (registered)
        {
            var stampSpan = PhaseTrace.Begin();
            try
            {
                foreach (var line in InstallStampNotice.Lines(s, advisory.InstallStamp, JsConvert.ToJsString(s.Config.PluginVersion())))
                {
                    context = With(context, line);
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                c.LogError(e);
            }

            foreach (var line in HerdrModNotice.Lines(s, JsConvert.ToJsString(s.Config.PluginVersion())))
            {
                context = With(context, line);
            }

            try
            {
                if (DaemonClientModule.ReadSpawnFailed(advisory.Daemon) is { } failed)
                {
                    var line = $"ahd failed to start ({failed.Reason}); retrying after {failed.RetryAt}";
                    context = With(context, line);
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                c.LogError(e);
            }

            PhaseTrace.End("sessionstart.install-stamp", stampSpan);
        }

        // Every session start refreshes the status file, role or not, enabled or not, so a reboot or a crash heals at the next
        // session in the repo. The session record goes first so the published document already maps this session to its process.
        var span = PhaseTrace.Begin();
        var poolDir = s.Config.HierarchyDir(cwd);
        s.Hierarchy.Status.AppendSessionPid(poolDir, sessionIdValue, ppid);
        PhaseTrace.End("sessionstart.session-pid", span);
        // In a linked worktree the main checkout's document is the one the Pane reads first, so it lists this session too.
        var mainDir = s.Config.MainHierarchyDir(cwd);
        if (!string.IsNullOrEmpty(mainDir) && mainDir != poolDir)
        {
            span = PhaseTrace.Begin();
            s.Hierarchy.Status.AppendSessionPid(mainDir, sessionIdValue, ppid);
            s.Hierarchy.Status.RefreshStatus(mainDir, new JsonObject { ["hook"] = true });
            PhaseTrace.End("sessionstart.refresh-main", span);
        }

        span = PhaseTrace.Begin();
        s.Hierarchy.Status.RefreshStatus(poolDir, new JsonObject { ["cwd"] = cwd, ["hook"] = true });
        PhaseTrace.End("sessionstart.refresh-pool", span);
        span = PhaseTrace.Begin();
        var heldBy = registered ? HoldGate.Of(s, cwd, sessionId) : null;
        PhaseTrace.End("sessionstart.hold", span);
        if (registered && heldBy is null)
        {
            try
            {
                span = PhaseTrace.Begin();
                advisory.Daemon.EnsureDaemonForHookAsync(DaemonBudget).GetAwaiter().GetResult();
                PhaseTrace.End("sessionstart.ensure-daemon", span);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // the files work without it
            }
        }

        if (heldBy is not null)
        {
            var holdLine = HoldGate.Line(s, cwd, sessionId, heldBy);
            context = With(context, holdLine);
        }

        // The Orchestrator's identity is the session-rules unit's (SessionRules.Identity).
        if (context is null)
        {
            return null;
        }

        // Hooks own the path to the ah CLIs: every session this hook injects into learns the absolute root here.
        span = PhaseTrace.Begin();
        var rootLine = s.Config.CliRootLine();
        context = With(context, rootLine);
        s.Hierarchy.Peers.RecordCliRootEmitted(sessionIdValue, rootLine);
        PhaseTrace.End("sessionstart.cli-root", span);
        try
        {
            span = PhaseTrace.Begin();
            var errors = s.Config.RecentHookErrors(s.Clock.UtcNow, 24);
            PhaseTrace.End("sessionstart.hook-errors", span);
            if (errors.Count > 0)
            {
                context +=
                    $"\n\nah: {errors.Count.ToString(CultureInfo.InvariantCulture)} agent-hierarchy hook error(s) logged in the last 24 h — `{s.Config.CliCommand(CliTool.Roster)} doctor --cwd <abs cwd>` for the last few, full log at {s.Config.HookErrorLogPath}.";
            }

            // A soft rule that throws lets its call through (SoftRules.Run), so say when one did.
            if (errors.AsEnumerable().Any(e => e.Member("hook")?.NonEmptyText == "pretooluse-soft-rules"))
            {
                context += $"\nah: a soft rule failed in the last 24 h, so its check did not run for that call; `{s.Config.CliCommand(CliTool.Roster)} doctor --cwd <abs cwd>` shows the error.";
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // the log is a diagnostic; never let reading it break the injection it rides on
        }

        return context;
    }

    internal static string? PaneOf(GateServices s) =>
        Ah.Core.Mux.HerdrFlavour.SessionPaneId(s.Environment.GetVariable) is { } herdr ? herdr
        : s.Environment.GetVariable("TMUX_PANE") is { Length: > 0 } tmux ? tmux : null;

    /// <summary>The rebind of the session's team and the record of the Orchestrator's start, for a session that has just become the Orchestrator.</summary>
    internal static void StartAsOrchestrator(GateContext c, string cwd)
    {
        var s = c.Services;
        var advisory = s.Advisory ?? throw new InvalidOperationException("the advisory services are not composed");
        var sessionId = c.Input.Text("session_id") ?? string.Empty;
        TeamRebind.Run(c, "sessionstart", cwd, sessionId, RebindModes.Both);
        RecordOrchestratorStart(c, advisory, cwd, sessionId, s.Process.ParentPid, PaneOf(s));
    }

    /// <summary>
    /// A <c>/clear</c> moves the Orchestrator-by-command flag from the session the process had to the new one; a new process that
    /// resumes or starts a flagged conversation takes over its ownership. Another source moves nothing. A failure leaves the
    /// session unflagged.
    /// </summary>
    internal static void CarryOrchestratorFlag(GateContext c, string? source, string cwd)
    {
        if (c.Input.Member("agent_type") is { IsTruthy: true } || (c.Input.Text("session_id") ?? string.Empty).Length == 0)
        {
            return;
        }

        try
        {
            if (source == "clear")
            {
                _ = OrchestratorIdentity.Write(c, "migrate", cwd);
            }
            else if (source is "startup" or "resume" && OrchestratorIdentity.Flagged(c))
            {
                _ = OrchestratorIdentity.Write(c, "restamp", cwd);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            c.LogError(e);
        }
    }

    private static JsonValue Env(GateServices s, string name) =>
        s.Environment.GetVariable(name) is { Length: > 0 } value ? value : JsonValue.Null;

    /// <summary>The session's pane id: the terminal address <c>HC_TERMINAL_ID</c> under herdr core, else <c>HERDR_PANE_ID</c>.</summary>
    internal static JsonValue PaneIdEnv(GateServices s) =>
        Ah.Core.Mux.HerdrFlavour.SessionPaneId(s.Environment.GetVariable) is { } pane ? pane : JsonValue.Null;

    /// <summary>Persists the role this session runs as, best-effort: the role notice goes out either way.</summary>
    /// <summary>The session-roles <c>set</c> arguments: the role and the harness process (the hook's parent) that holds it.</summary>
    internal static JsonObject SessionRoleArgs(GateServices s, JsonValue sessionId, string role)
    {
        var args = new JsonObject { ["session_id"] = sessionId, ["role"] = role };
        var pid = s.Process.ParentPid;
        if (pid > 1 && s.Sessions?.Table.StartTime(pid) is { Length: > 0 } start)
        {
            args["pid"] = pid;
            args["pid_start"] = start;
        }

        return args;
    }

    /// <summary>The team a session belongs to and its member row there, as the start hook finds them: by pane first, then by the member marker.</summary>
    internal sealed record MemberRowLookup(JsonValue Attributed, JsonValue Team, bool ViaEnv, MemberMarker? Marker, JsonValue Member);

    /// <summary>Whether a top-level session launched as a hierarchy role resolves to a team. Anything that cannot be read means no.</summary>
    private static bool AttributedRole(GateContext c, string cwd)
    {
        var s = c.Services;
        try
        {
            if (!s.Config.IsTopLevelAgentSession(c.Input))
            {
                return false;
            }

            var registry = s.Config.ResolveConfig(cwd);
            var agentType = c.Input.Text("agent_type") ?? string.Empty;
            var role = s.Config.HierarchyRoleOf(c.Input.Member("agent_type"), registry, cwd) is { IsText: true } r && r.AsString().Length > 0 ? r.AsString() : LaneRoleOf(s, registry, agentType);
            var dir = s.Config.HierarchyDir(cwd);
            return role is not null && s.Roster.AttributeSessionTeam(dir, role, null, PaneOf(s), [dir, s.Config.MainHierarchyDir(cwd) ?? string.Empty]).IsTruthy;
        }
        catch (Exception e) when (e is not (OperationCanceledException or StoreUnavailableException))
        {
            c.LogError(e);
            return false;
        }
    }

    /// <summary>
    /// The one member-row lookup of a session start, for a role session and for a lane leader alike: the team the session is
    /// attributed to, and its row by pane and then by the member marker (inside the starting window).
    /// </summary>
    internal static MemberRowLookup ResolveMemberRow(GateServices s, string dir, string cwd, string role, string? paneId, int ppid, string sessionId, bool logUnclaimable = true)
    {
        var attributed = s.Roster.AttributeSessionTeam(dir, role, null, paneId, [dir, s.Config.MainHierarchyDir(cwd) ?? string.Empty]);
        var team = attributed.Member("team") ?? JsonValue.Null;
        var viaEnv = attributed.Member("via") is { IsText: true } via && via.AsString() == "env";
        var startedAt = ppid > 1 ? s.Sessions?.Table.StartTime(ppid) : null;
        var attributedTeam = attributed.Member("teamName") is { IsText: true } at ? at.AsString() : null;
        var marker = s.Environment.GetVariable("AH_MEMBER_NAME") is { Length: > 0 } markerName
            ? new MemberMarker(markerName, ppid, startedAt, s.Clock.UtcNow, (t, n) => s.Hierarchy.Core.Liveness?.ForMember(n, t) is { Live: true } held && !(sessionId.Length > 0 && held.SessionId == sessionId))
            : null;
        var member = s.Roster.SessionMemberRow(team, paneId, marker, attributedTeam);
        if (logUnclaimable && marker is not null && !member.IsMap && team.Member("members") is { IsArray: true } named
            && named.AsArray().Any(m => m.Member("name") is { IsText: true } n && n.AsString() == marker.Name))
        {
            s.ErrorLog.Log(new HookErrorRecord("sessionstart", $"member row {marker.Name} is not claimable by the member marker (another pane or process holds it, it has registered, or it is no longer starting); this session is not registered as it"));
        }

        return new MemberRowLookup(attributed, team, viaEnv, marker, member);
    }

    /// <summary>Records this session's process on the member row <paramref name="row"/> found, when it found one.</summary>
    private static void StampMemberRow(GateServices s, AdvisoryServices advisory, string dir, MemberRowLookup row, string? paneId, int ppid, string? pidStart, string? sessionId)
    {
        if (row.Attributed.Member("teamName") is { IsText: true } rowTeam && row.Member.IsMap)
        {
            RecordMemberProcess(s, advisory, dir, row.Attributed.Member("home") is { IsText: true } home ? home.AsString() : dir, rowTeam.AsString(), paneId, row.Marker, ppid, pidStart, sessionId);
        }
    }

    /// <summary>The orchestrate-class role other than the reserved `orchestrator` whose agent is <paramref name="agentType"/>, or null.</summary>
    internal static string? LaneRoleOf(GateServices s, JsonObject registry, string agentType)
    {
        var roles = registry.Get("roles") is { IsMap: true } map ? map.AsObject() : null;
        return s.Config.RegistryRoles(registry).FirstOrDefault(r => r != "orchestrator" && s.Config.RoleClass(r, registry) == "orchestrate"
            && s.Config.RoleAgent(r, roles?.Get(r)) == agentType);
    }

    /// <summary>
    /// Records the process this session runs as (the harness's own pid, and its start) on its member row, so the mesh can tell the
    /// session that holds a member's name from any other process of the user that registers under it. The first process to record
    /// holds the row while it lives: a later start on the same pane is recorded only when the row has no process, is the same process,
    /// or names one that is gone (a resume); otherwise the write is refused and logged, and the session carries on.
    /// </summary>
    private static void RecordMemberProcess(GateServices s, AdvisoryServices advisory, string dir, string home, string team, string? paneId, MemberMarker? marker, int pid, string? pidStart, string? sessionId)
    {
        if (pid <= 1 || string.IsNullOrEmpty(pidStart))
        {
            return;
        }

        string? refused = null;
        s.Hierarchy.StateCalls.UpdateTeamFile(
            s.Roster.TeamPath(home, team),
            dir,
            doc =>
            {
                refused = null;
                if (!doc.IsMap || doc.AsObject().Get("members") is not { IsArray: true } rows)
                {
                    return null;
                }

                var members = new JsonArray();
                var changed = false;
                var mine = s.Roster.SessionMemberRow(doc, paneId, marker, team);
                foreach (var row in rows.AsArray())
                {
                    if (!(row.IsMap && mine.IsMap && row.Member("name") is { IsText: true } rowName && mine.Member("name") is { IsText: true } mineName && rowName.AsString() == mineName.AsString()))
                    {
                        members.Add(row);
                        continue;
                    }

                    var next = row.AsObject().Clone();
                    var heldPid = next.Get("pid") is { IsNumber: true } hp ? hp.AsNumber() : double.NaN;
                    var heldStart = next.Get("pid_start") is { IsText: true } hs ? hs.AsString() : null;
                    var sameProcess = heldPid == pid && heldStart == pidStart;
                    if (!double.IsNaN(heldPid) && !sameProcess && heldPid != pid && !advisory.Callers.OrchestratorProcessIsGone(heldPid, heldStart))
                    {
                        refused = $"member row {(mine.Member("name") is { IsText: true } heldName ? heldName.AsString() : "?")} is held by live process {heldPid}; pid {pid} not recorded";
                        members.Add(row);
                        continue;
                    }

                    changed = changed || !sameProcess;
                    next["pid"] = (double)pid;
                    next["pid_start"] = pidStart;
                    if (!string.IsNullOrEmpty(sessionId) && !(next.Get("session_id") is { IsText: true } heldSession && heldSession.AsString() == sessionId))
                    {
                        next["session_id"] = sessionId;
                        changed = true;
                    }

                    members.Add(next);
                }

                if (!changed)
                {
                    return null;
                }

                var copy = doc.AsObject().Clone();
                copy["members"] = members;
                return TeamFileOutcome.Write(copy);
            },
            new StateCallOptions(Hook: true));
        if (refused is not null)
        {
            s.ErrorLog.Log(new HookErrorRecord("sessionstart", refused));
        }
    }

    private static void RecordSessionRole(GateContext c, AdvisoryServices advisory, string cwd, JsonValue sessionId, string role)
    {
        var s = c.Services;
        try
        {
            // The daemon validates the request context, which names a hierarchy dir that must exist.
            try
            {
                s.Bootstrap.EnsureHierarchyDir(s.Config.HierarchyDir(cwd));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // the helper then writes directly, as it does for any context the daemon refuses
            }

            s.State.Invoke(
                "session-roles",
                "set",
                JsonText.ToUtf8(SessionRoleArgs(s, sessionId, role), 0),
                advisory.Callers.RequestContext(cwd),
                new StateCallOptions(Hook: true));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // best-effort
        }
    }

    /// <summary>
    /// Adds one line to the team's session inventory for a session that just registered. The inventory is a record, not a gate: a
    /// failed append is logged and the hook goes on.
    /// </summary>
    private static void RecordSession(
        GateContext c, string cwd, string team, string? member, string? role, string kind, string? model, string sessionId, string eventName)
    {
        try
        {
            var s = c.Services;
            var pool = ProgressPool.Of(s, cwd);
            s.Hierarchy.Progress.AppendSession(
                pool,
                team,
                new JsonObject
                {
                    ["at"] = Ah.Core.Time.Timestamps.ToIsoString(s.Clock.UtcNow),
                    ["member"] = member is null ? JsonValue.Null : member,
                    ["role"] = role is null ? JsonValue.Null : role,
                    ["kind"] = kind,
                    ["model"] = model is null ? JsonValue.Null : model,
                    ["session_id"] = sessionId.Length > 0 ? sessionId : JsonValue.Null,
                    ["transcript_path"] = c.Input.Text("transcript_path") is { Length: > 0 } transcript ? transcript : JsonValue.Null,
                    ["parent_session_id"] = JsonValue.Null,
                    ["event"] = eventName,
                });
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            c.LogError(e);
        }
    }

    /// <summary>
    /// The start of an Orchestrator launched with <c>--agent</c>: its team is read from the team files, which <see cref="TeamRebind"/>
    /// has just bound to this session. When that yields an <c>orchestrator@&lt;team&gt;</c> box the session registers its caller here,
    /// so a restarted Orchestrator shows its real state from its first turn (b457); the Stop hook's registration stays the fallback.
    /// A session that resolves no such box registers nothing.
    /// </summary>
    private static void RecordOrchestratorStart(GateContext c, AdvisoryServices advisory, string cwd, string sessionId, int ppid, string? paneId)
    {
        try
        {
            var session = sessionId.Length > 0 ? sessionId : null;
            var boxes = advisory.Callers.ResolveCaller(new CallerRequest(cwd, session, ppid, paneId));
            if (boxes.Count == 0 && ImplicitTeam.Ensure(c, cwd, c.Services.Config.HierarchyDir(cwd), sessionId, ppid))
            {
                boxes = advisory.Callers.ResolveCaller(new CallerRequest(cwd, session, ppid, paneId));
            }

            if (boxes.Any(b => b.Address.StartsWith("orchestrator@", StringComparison.Ordinal)))
            {
                boxes = advisory.Callers.RegisterCaller(
                    new CallerRequest(cwd, session, ppid, paneId, HerdrFlavour: Ah.Core.Mux.HerdrFlavour.SessionFlavour(c.Services.Environment.GetVariable)));
            }

            RecordOrchestratorStart(c, cwd, boxes, sessionId);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            c.LogError(e);
        }
    }

    private static void RecordOrchestratorStart(GateContext c, string cwd, IReadOnlyList<CallerBox> boxes, string sessionId)
    {
        if (boxes.Select(b => b.Address).FirstOrDefault(a => a.StartsWith("orchestrator@", StringComparison.Ordinal)) is { } address)
        {
            RecordSession(c, cwd, address["orchestrator@".Length..], "orchestrator", "orchestrator", "claude", null, sessionId, "orchestrator-start");
        }
    }

    /// <summary>The directive and the HIERARCHY STATE block of a configured, enabled top-level session, and whether it registered a caller.</summary>
    private static (string Context, bool Registered) EnabledSession(
        GateContext c,
        AdvisoryServices advisory,
        JsonObject resolved,
        string cwd,
        JsonValue sessionIdValue,
        string sessionId,
        string? source,
        bool topLevel,
        int ppid,
        string? paneId,
        string? laneRole)
    {
        var s = c.Services;
        string? dir = null;
        JsonValue model = JsonValue.Null;
        string? state = null;
        string? teamSweepNote = null;
        string? soBlock = null;
        string? laneChildRefusal = null;
        string? laneChildAdoptedFrom = null;
        string? chainDownText = null;
        string? downNote = null;
        var subOrchestrator = laneRole is not null;
        var registered = false;
        try
        {
            dir = s.Bootstrap.EnsureHierarchyDir(s.Config.HierarchyDir(cwd));
            model = s.Hierarchy.Roster.SessionModel(c.Input, dir);
            if (c.Input.Member("model") is { IsTruthy: true } inputModel && sessionId.Length > 0)
            {
                s.Hierarchy.Roster.CacheSessionModel(dir, sessionId, inputModel);
            }

            if (source == "startup")
            {
                s.Hierarchy.Messages.Sweep(dir, HierarchyModule.SweepDays, (double)s.Clock.UtcNow.ToUnixTimeMilliseconds(), hook: true);
            }

            // Stale-team safety net: only a plain top-level session can legitimately observe a different session's abandoned
            // team, never the orchestrator's own session before it has written the registry and never a `--agent` member.
            // An Orchestrator launched with `--agent <plugin>:orchestrator` is a top-level session with no registry role, so it is
            // not the plain-session branch below; it is the one agent launch whose team is rebound.
            if (OrchestratorIdentity.LaunchedAsAgent(c))
            {
                StartAsOrchestrator(c, cwd);
            }

            if (topLevel && subOrchestrator)
            {
                // Registered before anything slower, so a hook cut off at its time bound still leaves the lead's mail deliverable (b340).
                var soRequest = new CallerRequest(cwd, sessionId.Length > 0 ? sessionId : null, ppid, paneId);
                var soBoxes = advisory.Callers.RegisterCaller(soRequest);
                try
                {
                    var leadRow = ResolveMemberRow(s, dir, cwd, laneRole!, paneId, ppid, sessionId, logUnclaimable: false);
                    chainDownText = SessionStartChainDown.Notice(
                        c,
                        advisory,
                        resolved,
                        laneRole!,
                        leadRow.Member.Member("name") is { IsText: true } leadName ? leadName.AsString() : laneRole!,
                        leadRow.Attributed.Member("teamName") is { IsText: true } leadTeam ? leadTeam.AsString() : string.Empty,
                        s.Config.MainHierarchyDir(cwd) is { Length: > 0 } leadHome ? leadHome : dir,
                        sessionId);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    c.LogError(e);
                }

                TeamRebind.Run(c, "sessionstart", cwd, sessionId, RebindModes.Both);
                try
                {
                    StampMemberRow(s, advisory, dir, ResolveMemberRow(s, dir, cwd, laneRole!, paneId, ppid, sessionId), paneId, ppid, ppid > 1 ? s.Sessions?.Table.StartTime(ppid) : null, sessionId);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    c.LogError(e);
                }

                soBoxes = advisory.Callers.RegisterCaller(soRequest);
                var laneChild = LaneChildEnsure.Run(c, soBoxes, cwd, dir, sessionId, ppid);
                laneChildRefusal = laneChild?.Refusal;
                laneChildAdoptedFrom = laneChild?.AdoptedFrom;
                if (laneChild is { Created: true })
                {
                    soBoxes = advisory.Callers.RegisterCaller(soRequest);
                }

                registered = soBoxes.Count > 0;
            }

            if (!topLevel)
            {
                TeamRebind.Run(c, "sessionstart", cwd, sessionId, RebindModes.Both);
                teamSweepNote = SweepStaleTeam(s, dir, resolved, new TeamInvoker(ppid, sessionId.Length > 0 ? sessionId : null));
                if (advisory.Callers.ResolveCaller(new CallerRequest(cwd, sessionId.Length > 0 ? sessionId : null, ppid, paneId)).Count == 0)
                {
                    _ = ImplicitTeam.Ensure(c, cwd, dir, sessionId, ppid);
                }

                var ownBoxes = advisory.Callers.RegisterCaller(
                    new CallerRequest(cwd, sessionId.Length > 0 ? sessionId : null, ppid, paneId, HerdrFlavour: Ah.Core.Mux.HerdrFlavour.SessionFlavour(s.Environment.GetVariable)));
                if (ownBoxes.Count > 0)
                {
                    registered = true;
                }

                RecordOrchestratorStart(c, cwd, ownBoxes, sessionId);
            }

            var route = s.Hierarchy.Roster.EffectiveRoute(dir, resolved, sessionIdValue);
            var repo = s.Config.TeamPrefix(
                resolved.Get("cwd") is { IsText: true } rc ? rc.AsString() : null,
                resolved.Get("team") is { IsText: true } rt ? rt.AsString() : null);
            state = s.Hierarchy.Roster.BuildStateBlock(dir, resolved, repo, model, sessionIdValue, route);
            soBlock = SubOrchestratorStart.Block(c, advisory, resolved, cwd, sessionId, ppid, paneId, source, laneChildRefusal, laneChildAdoptedFrom);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // the state block is best-effort; the directive still goes out
        }

        // after the rebind above: an Orchestrator resumed after a restart hears which of its teams have members down
        if (dir is not null && source == "resume" && !subOrchestrator && (!topLevel || OrchestratorIdentity.LaunchedAsAgent(c)))
        {
            try
            {
                downNote = DownAfterRestart(s, advisory, dir, new TeamInvoker(ppid, sessionId.Length > 0 ? sessionId : null));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                c.LogError(e);
            }
        }

        // The top-level Orchestrator's directive is the session-directive unit's output; every other variant stays here, as it was.
        var modelText = model is { IsText: true } m ? m.AsString() : null;
        var context = TopOrchestratorDirective(c)
            ? string.Empty
            : LeadLine + "\n\n" + advisory.Directive.BuildDirective(resolved, sessionId.Length > 0 ? sessionId : null, dir, modelText, DeclaredSubOrchestrator.Is(c), DeclaredSubOrchestrator.IsDeputy(c));
        void Append(string text) => context = context.Length == 0 ? text : context + "\n\n" + text;
        if (teamSweepNote is not null)
        {
            Append(teamSweepNote);
        }

        if (state is not null)
        {
            Append(state + (source == "compact" ? $"\n{CompactStateLine}" : string.Empty));
        }

        if (MemoryRecall.Digest(c, source) is { } memory)
        {
            Append(memory);
        }

        if (soBlock is { } block)
        {
            Append(block);
        }

        if (sessionId.Length > 0 && HoldGate.Of(c.Services, cwd, sessionId) is null && Checklist(c, advisory, sessionId, cwd) is { } checklist)
        {
            Append(checklist);
        }

        if (s.Environment.GetVariable("HERDR_ENV") == "1" && !HerdrOnPath(s))
        {
            Append(
                "ah: HERDR_ENV=1 but no `herdr` binary was found on PATH. Roster spawning "
                + "(/wrangl:agent-team create, ah roster spawn-one) will fail when it tries to place "
                + "panes. Install herdr, or unset HERDR_ENV to fall back to tmux/terminal.");
        }

        if (chainDownText is not null)
        {
            Append(chainDownText);
        }

        if (downNote is not null)
        {
            Append(downNote);
        }

        return (context, registered);
    }

    /// <summary>
    /// After a restart (b238 S2.b): one advisory line for each unheld top team <paramref name="invoker"/> owns whose member rows record a
    /// process that is gone, or null when there is none. It only tells; nothing is launched.
    /// </summary>
    private static string? DownAfterRestart(GateServices s, AdvisoryServices advisory, string dir, TeamInvoker invoker)
    {
        var lines = new List<string>();
        foreach (var team in new string?[] { null }.Concat(s.Roster.ListTeamNames(dir)))
        {
            var record = s.Roster.ReadTeamRecord(dir, team);
            // the line only tells, so the team's recorded session being this one is owner enough
            var mine = invoker.SessionId is { Length: > 0 } own && record.Member("orchestrator")?.Member("session_id") is { IsText: true } recorded && recorded.AsString() == own;
            if (record.IsNull || record.Member("parent") is { IsMap: true } || !(mine || s.Roster.IsTeamOwnedBy(record, invoker)) || s.Roster.HoldOf(dir, team) is not null)
            {
                continue;
            }

            var down = (record.Member("members") is { IsArray: true } rows ? rows.AsArray() : [])
                .Count(r => r.Member("pid") is { IsNumber: true } pid
                    && advisory.Callers.OrchestratorProcessIsGone(pid.AsNumber(), r.Member("pid_start") is { IsText: true } start ? start.AsString() : null));
            if (down > 0)
            {
                lines.Add($"{down} members of {team ?? "_default"} are down after a restart; run ah roster resume --all");
            }
        }

        return lines.Count == 0 ? null : string.Join("\n", lines);
    }

    private static string? Checklist(GateContext c, AdvisoryServices advisory, string sessionId, string cwd)
    {
        try
        {
            return ChecklistBlock.Of(c.Services, advisory, sessionId, cwd);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonParseException or InvalidOperationException)
        {
            c.LogError(e);
            return null;
        }
    }

    private static bool HerdrOnPath(GateServices s)
    {
        if (s.Environment.GetVariable("PATH") is not { Length: > 0 } path)
        {
            return false;
        }

        foreach (var entry in path.Split(':'))
        {
            if (entry.Length > 0 && s.Files.IsExecutable(NodePath.Join(entry, "herdr")))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether the team names the starting session as its Orchestrator. A resumed or restarted session keeps its id but not its pid
    /// or its registration yet, so the ownership proof cannot hold here; keeping the file is the only safe reading.
    /// </summary>
    internal static bool RecordsThisSession(JsonValue team, TeamInvoker invoker) =>
        !string.IsNullOrEmpty(invoker.SessionId)
        && team.Member("orchestrator") is { IsTruthy: true } o
        && o.Member("session_id") is { IsText: true } recorded
        && recorded.AsString() == invoker.SessionId;

    /// <summary>
    /// Whether a team record may be cleared: its Orchestrator is proven gone (never its age, and never a pid nothing proves), and the
    /// team is not this session's own.
    /// </summary>
    private static bool ProvenAbandoned(GateServices s, JsonValue record, TeamInvoker invoker) =>
        record.IsTruthy && s.Roster.TeamLifeOf(record) == TeamLife.Orphaned && !s.Roster.IsTeamOwnedBy(record, invoker) && !RecordsThisSession(record, invoker);

    /// <summary>
    /// Clears an abandoned team file (the resolved team's): its orchestrator proven gone. A team this session owns
    /// is never abandoned; a team it was only attributed to (its launch env, or a pane match) is someone else's to clear.
    /// </summary>
    private static string? SweepStaleTeam(GateServices s, string dir, JsonObject resolved, TeamInvoker invoker)
    {
        if (resolved.Get("teamVia") is { IsText: true } via && via.AsString() is "env" or "pane")
        {
            return null;
        }

        var team = resolved.Get("team") is { IsText: true } t ? t.AsString() : null;
        var record = s.Roster.ReadTeamRecord(dir, team);
        if (!ProvenAbandoned(s, record, invoker))
        {
            return null;
        }

        // A team revived since the read above is judged again on the document the clear is checked against, and kept.
        try
        {
            var result = s.Hierarchy.StateCalls.UpdateTeam(
                dir,
                team,
                doc =>
                {
                    // no copy kept: the team is rows now, and a raw record in team_snapshots would read as a resume snapshot
                    return ProvenAbandoned(s, doc, invoker) ? TeamFileOutcome.Clear() : null;
                },
                new StateCallOptions(Hook: true));
            if (!result.Changed)
            {
                return null;
            }

            var teamId = record.Member("team_id") is { } id ? JsConvert.ToJsString(id) : "undefined";
            return $"cleared stale team {teamId}";
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            s.ErrorLog.Log(new HookErrorRecord("sessionstart", e.Message));
            return null;
        }
    }
}
