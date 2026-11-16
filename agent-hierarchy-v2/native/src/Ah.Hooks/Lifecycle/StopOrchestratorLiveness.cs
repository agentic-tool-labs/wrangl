using System.Globalization;
using System.Text.RegularExpressions;
using Ah.Abstractions;
using Ah.Config;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Core.Time;
using Ah.Hierarchy;
using Ah.Hooks.Gates;
using Ah.Hooks.Plumbing;
using Ah.Mesh;
using Ah.Watch;

namespace Ah.Hooks.Lifecycle;

/// <summary>
/// Stop hook: the orchestrator-side liveness check-in. It refuses to let the orchestrator go quiet while it is still owed a
/// report from a peer it dispatched, hands over event mail the daemon fired and nobody delivered, and says once per session
/// when the mod is not pulling. A caller positively attributed as a subordinate role is skipped; a session that itself owes a
/// peer report takes precedence over one that is owed one.
/// </summary>
[HookIdentity(HookIdentityKind.Grants)]
internal static partial class StopOrchestratorLiveness
{
    private static readonly TimeSpan DaemonBudget = TimeSpan.FromMilliseconds(1500);

    // the README states the floor as "Claude Code <version> or later"; the words after it have changed between releases
    [GeneratedRegex("Claude Code ([0-9]+\\.[0-9]+\\.[0-9]+) or later", RegexOptions.CultureInvariant)]
    private static partial Regex FloorLine();

    // Dir is the hierarchy dir holding this request's liveness rows: the pool its request file is in. ToAddr and To are the
    // dispatch row's address and recipient name.
    internal sealed record Outstanding(
        string Id, string Role, string ToName, string Path, double AgeSec, string Eta, double Base, string? Pane, string Dir, string? ToAddr, string? To);

    private sealed record Gathered(List<Outstanding> Outstanding, List<Landed> Landed, JsonArray CwdGates, Func<string, JsonArray> GatesOf, IExchangeIndex Exchanges);

    private sealed record Landed(string Id, string Role, string ToName, string ResponsePath);

    private static string? PaneOf(GateServices s) =>
        Ah.Core.Mux.HerdrFlavour.SessionPaneId(s.Environment.GetVariable) is { Length: > 0 } herdr ? herdr
        : s.Environment.GetVariable("TMUX_PANE") is { Length: > 0 } tmux ? tmux : null;

    /// <summary>
    /// A member that started before its team file named it (or before its pane id was recorded) has no registration yet; its Stop
    /// is the next chance to make one. A session that already has a live registration is left alone.
    /// </summary>
    private static void RegisterUnregisteredMember(GateContext c)
    {
        var s = c.Services;
        var sessionId = c.Input.Text("session_id") ?? string.Empty;
        if (sessionId.Length == 0 || s.Advisory is not { } advisory)
        {
            return;
        }

        try
        {
            var cwd = c.Cwd();
            var resolved = s.Config.ResolveConfig(cwd, new ResolveOptions(SessionId: sessionId));
            if (resolved.Get("enabled") is not { IsTruthy: true })
            {
                return;
            }

            if (advisory.Mailbox.ReadRegistration(sessionId) is { } held && advisory.Mailbox.RegistrationLive(held))
            {
                return;
            }

            _ = advisory.Callers.RegisterCaller(
                new CallerRequest(cwd, sessionId, s.Process.ParentPid, PaneOf(s), HerdrFlavour: Ah.Core.Mux.HerdrFlavour.SessionFlavour(s.Environment.GetVariable)));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            c.LogError(e);
        }
    }

    /// <summary>The streams whose newest journaled report is newer than their checklist and not yet nudged; none when the team is unknown or the records cannot be read.</summary>
    private static IReadOnlyList<Ah.Hierarchy.ProgressNudge> BehindChecklists(GateContext c, GateServices s, string dir, string? team)
    {
        if (team is null)
        {
            return [];
        }

        try
        {
            return s.Hierarchy.Progress.PendingNudges(dir, team);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            c.LogError(e);
            return [];
        }
    }

    public static HookOutcome Run(GateContext c)
    {
        // A held team's Orchestrator talks to the user: nothing here blocks, warns, acks, hands over mail or starts the daemon.
        // The question log still records the reply: a held team's Orchestrator is the one talking to the user.
        if (HoldGate.Of(c.Services, c.Cwd(), c.Input.Text("session_id")) is not null)
        {
            RecordTurnEnd(c, turnEnd: false);
            return HookEmit.Silent;
        }

        var outcome = CheckInOrHold(c);
        if (HookHost.BlockReason(outcome) is null)
        {
            RecordTurnEnd(c, turnEnd: true);

            // a nudge only, on a Stop that says nothing else (atp-1703 part 4)
            if (outcome.Stdout.IsEmpty && !c.Services.Config.IsSubagent(c.Input) && c.Services.Config.ResolveHierarchyRole(c.Input) is not { Direct: true, Role: not null }
                && OrchestratorIdentity.In(c) && NeedsYouNudge.Notice(c) is { } nudge)
            {
                outcome = HookEmit.AdditionalContext("Stop", nudge);
            }
        }

        return outcome;
    }

    /// <summary>
    /// The outstanding dispatches come from the peer log, and an empty log reads as "nothing outstanding". A log that cannot be read
    /// holds the Stop once with that reason; a second Stop (<c>stop_hook_active</c>) leaves so a broken store cannot trap the session.
    /// </summary>
    private static HookOutcome CheckInOrHold(GateContext c)
    {
        using (c.Services.Hierarchy.Core.DecisionReads())
        {
            try
            {
                return RunCheckIn(c);
            }
            catch (StoreUnavailableException error) when (c.Services.Advisory is not null)
            {
                c.LogError(error);
                var again = c.Input.Member("stop_hook_active") is { IsBool: true } active && active.AsBool();
                return again ? HookEmit.Silent : HookEmit.Block(c.Services.Advisory.Directive.StopStateUnreadable(error.Message));
            }
        }
    }

    /// <summary>
    /// The question-log gate, shared by the Stop step and the PostToolUse direct reply: the session is the top Orchestrator's own (not a
    /// subagent, a sub-orchestrator, a member or a role session) and decides for at least one team.
    /// </summary>
    internal static bool QaLogSession(GateContext c, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out AdvisoryServices? advisory,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? sessionId)
    {
        var s = c.Services;
        advisory = null;
        sessionId = null;
        if (s.Config.IsSubagent(c.Input) || DeclaredSubOrchestrator.Is(c) || s.Config.IsMemberSession || s.Config.ResolveHierarchyRole(c.Input) is { Direct: true, Role: not null }
            || s.Advisory is not { } found || c.Input.NonEmpty("session_id") is not { } id || found.Callers.SessionDeciderBoxes(c.Cwd(), id).Count == 0)
        {
            return false;
        }

        (advisory, sessionId) = (found, id);
        return true;
    }

    /// <summary>
    /// Notes that the top Orchestrator's turn ended: the user is away once a later turn starts without a typed prompt. A failed write
    /// is logged and changes nothing. It also records the turn's reply in the user's question log; <paramref name="turnEnd"/> false (a
    /// held team) records the reply only.
    /// </summary>
    private static void RecordTurnEnd(GateContext c, bool turnEnd)
    {
        var s = c.Services;
        try
        {
            if (!QaLogSession(c, out var advisory, out var sessionId))
            {
                return;
            }

            Ah.Hooks.Activity.QaLogCapture.Reply(c, advisory, sessionId);
            if (turnEnd)
            {
                s.Hierarchy.Peers.AppendTurnEnd(sessionId);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            c.LogError(e);
        }
    }

    private static HookOutcome RunCheckIn(GateContext c)
    {
        var s = c.Services;
        if (s.Config.IsSubagent(c.Input))
        {
            return HookEmit.Silent;
        }

        var caller = s.Config.ResolveHierarchyRole(c.Input);
        if (caller.Direct && caller.Role is not null)
        {
            RegisterUnregisteredMember(c);
            return HookEmit.Silent;
        }

        if (!OrchestratorIdentity.In(c))
        {
            return HookEmit.Silent;
        }

        var advisory = s.Advisory ?? throw new InvalidOperationException("the advisory services are not composed");
        var sessionId = c.Input.Text("session_id") ?? string.Empty;
        var cwd = c.Cwd();
        var resolved = s.Config.ResolveConfig(cwd, new ResolveOptions(SessionId: sessionId.Length > 0 ? sessionId : null));
        if (resolved.Get("enabled") is not { IsTruthy: true })
        {
            return HookEmit.Silent;
        }

        // An orchestrator is registered once it owns a named team, by whatever verb or file write gave it one. This runs first,
        // so the daemon check below sees a new registration in the same Stop.
        var regBefore = sessionId.Length > 0 ? advisory.Mailbox.ReadRegistration(sessionId) : null;
        string? ownTeam = null;
        if (sessionId.Length > 0)
        {
            TeamRebind.Run(c, "stop-orchestrator-liveness", cwd, sessionId, RebindModes.Session);
            try
            {
                var boxes = advisory.Callers.EnsureRegistered(
                    new CallerRequest(cwd, sessionId, s.Process.ParentPid, PaneOf(s), HerdrFlavour: Ah.Core.Mux.HerdrFlavour.SessionFlavour(s.Environment.GetVariable)));
                ownTeam = boxes.Select(b => b.Address).FirstOrDefault(a => a.StartsWith("orchestrator@", StringComparison.Ordinal))?["orchestrator@".Length..];
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                c.LogError(e);
            }
        }

        if (c.Input.Member("stop_hook_active") is { IsBool: true } active && active.AsBool())
        {
            return HookEmit.Silent;
        }

        if (sessionId.Length > 0 && ParkedPushNudge.Notice(c, advisory, sessionId) is { } parkedNotice)
        {
            return HookEmit.Block(parkedNotice);
        }

        var peers = s.Hierarchy.Peers;
        if (sessionId.Length > 0
            && PeerLatestFor.Of(c).PendingFor(sessionId).Any(r => !(r.Member("armed_by") is { IsText: true } by && by.AsString() == "msg-token")))
        {
            return HookEmit.Silent;
        }

        // The daemon keeps this session's clock and carries its mail; a hook is what brings it back mid-session.
        if (sessionId.Length > 0 && advisory.Mailbox.ReadRegistration(sessionId) is not null)
        {
            try
            {
                advisory.Daemon.EnsureDaemonForHookAsync(DaemonBudget).GetAwaiter().GetResult();
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                c.LogError(e);
            }
        }

        var dir = s.Config.HierarchyDir(cwd);
        var now = (double)s.Clock.UtcNow.ToUnixTimeMilliseconds();
        var (rings, doorbellLine, doorbellFresh) = sessionId.Length > 0 ? Doorbell(c, advisory, sessionId, now) : (false, null, false);
        // A session the doorbell serves is told by one counts line, never by event bodies, so the event mail stays queued for `ah msg next`.
        var drained = sessionId.Length > 0 && !rings ? ClaimDue(c, advisory, EventMail.Due(advisory, sessionId, now), sessionId) : [];
        // The due event mail is already acked, so a failure while composing must still hand it over rather than lose it.
        HookOutcome Compose()
        {
            // Everything below asks for the same pools and exchanges: each is read once.
            using var listings = Ah.Core.Files.ListingScope.Begin();

            // A landed report reached the session by no route and has no box entry, so this block is its only carrier.
            var (outstanding, landed, gates, gatesOf, exchanges) = Gather(s, dir, resolved, sessionId, now);

            var notice = ModNotice(s, advisory, regBefore, sessionId, now, gates, exchanges);
            var behind = BehindChecklists(c, s, ProgressPool.Of(s, cwd), ownTeam);
            var wake = ownTeam is null ? null : WakeRule.Notice(s, dir, ownTeam, sessionId, cwd, doorbellFresh);
            var queue = ownTeam is null ? null : QueueNotice(c, advisory, dir, ownTeam, now, cwd, sessionId);
            if (outstanding.Count == 0 && landed.Count == 0 && drained.Count == 0 && notice is null && behind.Count == 0 && wake is null && queue is null && doorbellLine is null)
            {
                return HookEmit.Silent;
            }

            var cadence = HierarchyModule.CheckinCadence;
            var toBlockOn = new List<Outstanding>();
            foreach (var item in outstanding)
            {
                var threshold = s.Hierarchy.Messages.ThresholdFor(item.Eta);
                if (!WatchModule.DueForNudge(gatesOf(item.Dir), sessionId, item.Id, threshold, cadence, now, item.Base))
                {
                    continue;
                }

                // A peer that is working is not silent; nothing is recorded, so the next Stop looks again.
                if (WorkingPeer(s, dir, item, now))
                {
                    continue;
                }

                // A request that has been checked in on as often as a Stop asks gets no block and no more rows; ahd keeps watching it.
                if (CheckInBlocks(gatesOf(item.Dir), sessionId, item.Id) >= CheckInBlockCap)
                {
                    continue;
                }

                s.Hierarchy.Roster.AppendGateRouted(item.Dir, new JsonObject { ["type"] = "liveness-nudge", ["session_id"] = sessionId, ["request_id"] = item.Id });

                // The delivered mail is this request's check-in prompt; the clock's own would ask the same thing twice.
                if (!drained.Any(d => d.RequestId == item.Id && (d.Event is "check-in-due" or "peer-silent")))
                {
                    toBlockOn.Add(item);
                }
            }

            // A check-in says nothing new while something else will wake this session for the request: the doorbell serves it
            // (Serves and a fresh pull) or its ahd waiter runs, and the ahd clock mails the check-in to the box. It blocks only when neither does.
            if (toBlockOn.Count > 0 && (doorbellFresh || WakeRule.Live(s, $"{sessionId}.json")))
            {
                toBlockOn.Clear();
            }

            // A behind checklist goes to the session's own box as a note, read at its next prompt; it is still marked nudged.
            foreach (var nudge in behind)
            {
                SendBehindNote(c, advisory, cwd, ownTeam!, advisory.Directive.StopBehindChecklist(nudge.Stream, nudge.ReportId, ownTeam!, nudge.Role, nudge.Open ?? []));
                try
                {
                    s.Hierarchy.Progress.MarkNudged(ProgressPool.Of(s, cwd), ownTeam!, nudge);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    c.LogError(e);
                }
            }

            if (toBlockOn.Count == 0 && landed.Count == 0 && drained.Count == 0 && notice is null && wake is null && queue is null && doorbellLine is null)
            {
                return HookEmit.Silent;
            }

            foreach (var item in landed)
            {
                peers.AppendReportRecord("surfaced", sessionId, item.Id);
            }

            var parts = new List<string>();
            if (queue is not null)
            {
                parts.Add(queue);
            }

            if (drained.Count > 0)
            {
                parts.Add(EventMailReason(advisory, drained));
            }

            if (doorbellLine is not null)
            {
                parts.Add(doorbellLine);
            }

            if (landed.Count > 0)
            {
                parts.Add(LandedReason(s, advisory, landed));
            }

            if (toBlockOn.Count > 0)
            {
                parts.Add(CheckInReason(s, advisory, toBlockOn, cwd));
            }

            if (notice is not null)
            {
                s.Hierarchy.Roster.AppendGateRouted(dir, new JsonObject { ["type"] = "mod-notice", ["session_id"] = sessionId });
                parts.Add(notice);
            }

            if (wake is not null)
            {
                parts.Add(wake);
            }

            return HookEmit.Block(string.Join("\n\n", parts));
        }

        try
        {
            return Compose();
        }
        catch (Exception e) when (drained.Count > 0 && e is not OperationCanceledException)
        {
            c.LogError(e);
            return HookEmit.Block(EventMailReason(advisory, drained));
        }
    }

    /// <summary>Sends a behind-checklist line to the session's own Orchestrator box as a note; a failed send is logged and nothing else happens.</summary>
    private static void SendBehindNote(GateContext c, AdvisoryServices advisory, string cwd, string team, string text)
    {
        try
        {
            var request = new JsonObject
            {
                ["to"] = "orchestrator@" + team,
                ["from"] = "ahd",
                ["cwd"] = cwd,
                ["hierarchy_dir"] = c.Services.Config.HierarchyDir(cwd),
                ["text"] = text,
            };
            if (c.Services.Config.MainHierarchyDir(cwd) is { } main)
            {
                request["main_hierarchy_dir"] = main;
            }

            if (advisory.Mailbox.Send(request).Failure is { } failure)
            {
                c.LogError(new InvalidOperationException("behind-checklist note not sent: " + failure));
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            c.LogError(e);
        }
    }

    /// <summary>
    /// Whether the doorbell serves the session and, when it rings at this turn end, its one line. The ring is claimed with the same
    /// state the mod's poll uses, so a turn that was already rung for gets nothing here.
    /// </summary>
    private static (bool Serves, string? Line, bool Fresh) Doorbell(GateContext c, AdvisoryServices advisory, string sessionId, double now)
    {
        try
        {
            var bell = MeshModule.CreateMailDoorbell(advisory.Mailbox, advisory.MailCore, c.Services.Files, c.Services.Environment, advisory.MailboxHooks);
            if (!bell.Serves(sessionId))
            {
                return (false, null, false);
            }

            // The doorbell serves the session when it serves it AND the mod's pull is fresh: only then does its ring reach the session.
            var fresh = advisory.Mailbox.OrchestratorPullFresh(sessionId, now);
            if (fresh)
            {
                // The mod rings an idle session within one poll, urgent mail at once: no ring is claimed at this turn end.
                return (true, null, true);
            }

            var decision = bell.Decide(new DoorbellRequest(sessionId, now, null, new Dictionary<string, HandedState>(), DoorbellCarrier.Stop));
            return (true, decision.Ring?.Text, fresh);
        }
        catch (ArgumentException)
        {
            return (false, null, false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            c.LogError(e);
            return (false, null, false);
        }
    }

    /// <summary>
    /// Acks the due event mail before anything else is decided, so the other carrier cannot deliver the same entry while this hook
    /// works; an entry that carrier acked first is dropped, since it already reached the conversation. If the ack itself fails the
    /// entries are delivered unacked, which can repeat them but never loses one.
    /// </summary>
    private static List<DueEventMail> ClaimDue(GateContext c, AdvisoryServices advisory, List<DueEventMail> due, string sessionId)
    {
        if (due.Count == 0)
        {
            return due;
        }

        try
        {
            var first = EventMail.Ack(advisory, due, "stop-hook", sessionId);
            return [.. due.Where(d => first.Contains(d.Id))];
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            c.LogError(e);
            return due;
        }
    }

    /// <summary>
    /// Every open dispatch of this session still waiting on its peer at <paramref name="now"/>, due for a check-in or not (b541), with
    /// the liveness rows' pool dir and the dispatch's address.
    /// </summary>
    internal static List<Outstanding> OpenOf(GateServices s, string cwd, JsonObject resolved, string sessionId, double now) =>
        Gather(s, s.Config.HierarchyDir(cwd), resolved, sessionId, now, dueOnly: false).Outstanding;

    /// <summary>Whether a report reached the session, by any route (<see cref="Ah.Mesh.IMailboxHooks.ReportReached(string?)"/>); false for every report when the hook runs without the mail services.</summary>
    internal static Func<string, string, bool> ReachedOf(GateServices s, string sessionId) =>
        s.Advisory is { } advisory ? advisory.MailboxHooks.ReportReached(sessionId) : static (_, _) => false;

    /// <summary>Whether a request's report was read by this session or by an orchestrator of its team.</summary>
    private static Func<JsonValue?, bool> AckedOf(GateServices s, string sessionId) =>
        s.Advisory?.MailboxHooks is { } hooks
            ? s.Hierarchy.Peers.ReportAckedBy(hooks.SameOrchestrator(sessionId), hooks.OrchestratedTeams(sessionId))
            : s.Hierarchy.Peers.ReportAckedFor(sessionId);

    private static Gathered Gather(GateServices s, string dir, JsonObject resolved, string sessionId, double now, bool dueOnly = true)
    {
        var gates = s.Hierarchy.Roster.ReadGates(dir);
        JsonArray records = [.. s.Hierarchy.Core.SessionRows(sessionId, "heard")];
        var poolGates = new Dictionary<string, JsonArray>(StringComparer.Ordinal) { [dir] = gates };
        JsonArray GatesOf(string poolDir)
        {
            if (!poolGates.TryGetValue(poolDir, out var rows))
            {
                rows = s.Hierarchy.Roster.ReadGates(poolDir);
                poolGates[poolDir] = rows;
            }

            return rows;
        }

        var pools = s.Hierarchy.Messages.PoolDirs(dir, TeamNames(resolved));
        var exchanges = HierarchyModule.CreateExchangeIndex(s.Hierarchy.Core, pools);
        var reached = ReachedOf(s, sessionId);
        var (pathOutstanding, landed) = PathDispatches(s, sessionId, now, records, GatesOf, exchanges, reached, dueOnly);
        var outstanding = OutstandingDispatches(s, dir, resolved, sessionId, now, records, gates, pools, landed, reached, dueOnly);
        outstanding.AddRange(pathOutstanding);
        // A report the orchestrator has read leaves nothing to wait for, even when no response file closes the exchange.
        if (outstanding.Count > 0)
        {
            var acked = AckedOf(s, sessionId);
            _ = outstanding.RemoveAll(o => acked(JsonValue.FromString(o.Id)));
        }

        return new Gathered(outstanding, landed, gates, GatesOf, exchanges);
    }

    /// <summary>The names of the teams this session works on: its owned teams when it has several, else its one team.</summary>
    private static List<string> TeamNames(JsonObject resolved)
    {
        var names = new List<string>();
        if (resolved.Get("ownedTeams") is { IsArray: true } owned && owned.AsArray().Count > 1)
        {
            names.AddRange(owned.AsArray().Where(static t => t.IsText).Select(static t => t.AsString()));
        }
        else if (resolved.Get("team") is { IsText: true } team)
        {
            names.Add(team.AsString());
        }

        return names;
    }

    /// <summary>The exchange of <paramref name="requestId"/> over the index's pools and the pool the file at <paramref name="path"/> sits in.</summary>
    private static JsonValue ExchangeAt(IExchangeIndex exchanges, string path, string requestId) =>
        exchanges.Find(requestId, NodePath.Dirname(NodePath.Dirname(path)));

    /// <summary>How old a dispatch row without a <c>path</c> may be and still be looked up by its request id.</summary>
    private const double PathlessRowMaxAgeMs = 24 * 3600 * 1000;

    /// <summary>Whether an exchange is still waiting on the peer: it is open and holds no landed report. <paramref name="reported"/> says which of the two ended the wait.</summary>
    private static bool StillWaiting(GateServices s, JsonValue exchange, double now, out bool reported)
    {
        reported = s.Hierarchy.Messages.ResponseLanded(StringOf(exchange.Member("response")?.Member("path")), now);
        return !reported && exchange.Member("open") is { IsTruthy: true };
    }

    private static string StringOf(JsonValue? value) => value is { IsText: true } t ? t.AsString() : string.Empty;

    /// <summary>Whether the request only records a chat message to a member: it has no deadline, so nobody is waited on for it.</summary>
    private static bool IsNote(JsonValue fm) => StringOf(fm.Member("reason")) == Ah.Hierarchy.HierarchyModule.AutoTrackedNoteReason;

    /// <summary>Whether nobody is waited on for the request: a note, or a plain send whose clock ahd has not armed (board-derive § C3, the same armed row the daemon reads).</summary>
    private static bool Untimed(GateServices s, string sessionId, JsonValue fm, string requestId) =>
        IsNote(fm) || (StringOf(fm.Member("reason")) == Ah.Hierarchy.HierarchyModule.PlainSendReason
            && !s.Hierarchy.Core.SessionRows(sessionId, "plain-send-armed").Any(r => StringOf(r.Member("request_id")) == requestId));

    private static string ToName(JsonValue fm, string? toAddr) =>
        WatchModule.DispatchName(fm.Member("to_name") is { IsText: true } n ? n.AsString() : null, toAddr);

    /// <summary>
    /// Open exchanges this session dispatched on the peer route, past their eta threshold: a dispatch record for the exchange's
    /// id exists among this session's own. A subagent dispatch never goes through SendMessage, so it has no record, which alone
    /// excludes it.
    /// </summary>
    private static List<Outstanding> OutstandingDispatches(
        GateServices s, string dir, JsonObject resolved, string sessionId, double now, JsonArray records, JsonArray gates, IReadOnlyList<string> pools, List<Landed> landed, Func<string, string, bool> reached, bool dueOnly)
    {
        var peers = s.Hierarchy.Peers;
        // A row without a `path` is looked up by its request id over the pools, as the watch host does, and only while it is at most a day old.
        var mine = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
        foreach (var row in DispatchObligations.Live(s, sessionId))
        {
            if (row.Member("path") is { IsTruthy: true } || row.Member("request_id") is not { IsText: true } rid
                || row.Member("created") is not { IsText: true } created || !Timestamps.TryParseMs(created.AsString(), out var createdMs)
                || now - createdMs > PathlessRowMaxAgeMs)
            {
                continue;
            }

            mine[rid.AsString()] = row;
        }

        var teams = new List<JsonValue>();
        if (resolved.Get("ownedTeams") is { IsArray: true } owned && owned.AsArray().Count > 1)
        {
            teams.AddRange(owned.AsArray());
        }
        else
        {
            teams.Add(resolved.Get("team") ?? JsonValue.Null);
        }

        var candidates = new List<(JsonValue Exchange, string Id, JsonValue Fm)>();
        var exchanges = s.Hierarchy.Core.ListExchanges(pools, live: true);
        foreach (var team in teams)
        {
            foreach (var e in s.Hierarchy.Messages.OpenExchanges(dir, team, exchanges))
            {
                if (e.Member("id") is not { IsText: true } id || !mine.ContainsKey(id.AsString()))
                {
                    continue;
                }

                if (!StillWaiting(s, e, now, out var reported))
                {
                    var responsePath = StringOf(e.Member("response")?.Member("path"));
                    if (reported && !reached(id.AsString(), responsePath)
                        && s.Hierarchy.Core.ReadMsgFile(StringOf(e.Member("request")?.Member("path"))).Member("fm") is { } landedFm)
                    {
                        var toAddr = mine[id.AsString()].Member("to_addr") is { IsText: true } ta ? ta.AsString() : null;
                        landed.Add(new Landed(id.AsString(), StringOf(e.Member("to")), ToName(landedFm, toAddr), responsePath));
                    }

                    continue;
                }

                var requestPath = StringOf(e.Member("request")?.Member("path"));
                var fm = s.Hierarchy.Core.ReadMsgFile(requestPath).Member("fm");
                if (fm is { } f && f.Member("from") is { IsText: true } from && from.AsString() is "orchestrator" or "sub-orchestrator" && !Untimed(s, sessionId, f, id.AsString()))
                {
                    candidates.Add((e, id.AsString(), f));
                }
            }
        }

        var requests = new JsonArray();
        foreach (var (_, id, fm) in candidates)
        {
            requests.Add(new JsonObject { ["id"] = id, ["created"] = fm.Member("created") ?? JsonValue.Null });
        }

        var origins = peers.DispatchOrigin(requests);
        var output = new List<Outstanding>();
        foreach (var (e, id, fm) in candidates)
        {
            if (!origins.TryGetValue(id, out var originValue) || originValue is not { IsText: true } ot
                || !Timestamps.TryParseMs(ot.AsString(), out var origin))
            {
                continue;
            }

            if (OutstandingIfDue(s, dir, sessionId, now, records, gates, id, StringOf(e.Member("to")), fm, StringOf(e.Member("request")?.Member("path")), origin, mine[id], dueOnly) is { } due)
            {
                output.Add(due);
            }
        }

        return output;
    }

    /// <summary>
    /// The first check-in a dispatch owes, or null before it is due (unless <paramref name="dueOnly"/> is false). A dispatch's clock starts at <paramref name="originMs"/>, its own
    /// earliest dispatch row, and a request addressed to a pane-driven member names that member, whichever way the dispatch row found
    /// its request (by its path or by its id).
    /// </summary>
    private static Outstanding? OutstandingIfDue(
        GateServices s, string dir, string sessionId, double now, JsonArray records, JsonArray gates, string id, string role, JsonValue fm, string requestPath, double originMs, JsonValue row, bool dueOnly)
    {
        if (GoneTeamRequest(s, dir, fm))
        {
            return null;
        }

        var eta = s.Hierarchy.Messages.EtaOf(fm.Member("eta") ?? JsonValue.Null);
        var toAddr = row.Member("to_addr") is { IsText: true } ta ? ta.AsString() : null;
        var (baseMs, _) = WatchModule.LivenessClock(records, gates, sessionId, id, toAddr, originMs);
        if (dueOnly && now - baseMs < s.Hierarchy.Messages.ThresholdFor(eta) * HierarchyModule.CheckinCadence[0] * 1000)
        {
            return null;
        }

        return new Outstanding(
            id, role, ToName(fm, toAddr), requestPath, Math.Max(0, (now - originMs) / 1000), eta, baseMs, PaneMemberName(s, dir, fm, role), dir, toAddr,
            row.Member("to") is { IsText: true } to ? to.AsString() : null);
    }

    /// <summary>
    /// A request whose team is gone (no team file) or orphaned with its recipient no longer in the team file: nobody can answer it, so a
    /// Stop does not list it again; <c>ah msg close</c> retires it.
    /// </summary>
    private static bool GoneTeamRequest(GateServices s, string dir, JsonValue fm)
    {
        if (fm.Member("team") is not { IsText: true } t || t.AsString() is not { Length: > 0 } team || team == "null")
        {
            return false;
        }

        var record = s.Roster.ReadTeamRecord(dir, team);
        return record.IsNull || (s.Roster.TeamIsOrphaned(record) && !s.Roster.TeamMemberByName(dir, StringOf(fm.Member("to_name")), team).IsTruthy);
    }

    /// <summary>The name of the pane-driven member a request is addressed to (by to_name, then by role), else null.</summary>
    private static string? PaneMemberName(GateServices s, string dir, JsonValue fm, string role)
    {
        var team = fm.Member("team") is { IsText: true } t && t.AsString().Length > 0 ? t.AsString() : null;
        var name = fm.Member("to_name") is { IsText: true } n ? n.AsString() : null;
        var member = s.Roster.TeamMemberByName(dir, name, team);
        if (!member.IsTruthy)
        {
            var doc = s.Roster.ReadTeamRecord(dir, team);
            member = JsonValue.Null;
            if (doc.Member("members") is { IsArray: true } members)
            {
                foreach (var m in members.AsArray())
                {
                    if (m.Member("role") is { IsText: true } r && r.AsString() == role)
                    {
                        member = m;
                        break;
                    }
                }
            }
        }

        return s.Config.IsPaneMember(member) && member.Member("name") is { IsText: true } mn ? mn.AsString() : null;
    }

    /// <summary>
    /// Dispatches that record where their request lives, looked up in that request's own msgs dir so a request written to another
    /// checkout's pool is still seen: the open ones past their eta threshold, and the exchanges whose response file exists but
    /// was never shown to this session. Liveness rows for such a request are read from and written to the pool its request file is
    /// in, the dir the daemon uses, whichever checkout this session's cwd is in.
    /// </summary>
    private static (List<Outstanding> Outstanding, List<Landed> Landed) PathDispatches(
        GateServices s, string sessionId, double now, JsonArray records, Func<string, JsonArray> gatesOf, IExchangeIndex exchanges, Func<string, string, bool> reached, bool dueOnly)
    {
        var outstanding = new List<Outstanding>();
        var landed = new List<Landed>();
        var peers = s.Hierarchy.Peers;
        foreach (var row in DispatchObligations.Live(s, sessionId))
        {
            if (row.Member("path") is not { IsText: true } path || path.AsString().Length == 0
                || row.Member("request_id") is not { IsText: true } rid)
            {
                continue;
            }

            var poolDir = NodePath.Dirname(NodePath.Dirname(path.AsString()));
            var exchange = ExchangeAt(exchanges, path.AsString(), rid.AsString());
            if (exchange.IsNull)
            {
                continue;
            }

            var requestPath = StringOf(exchange.Member("request")?.Member("path"));
            var fm = s.Hierarchy.Core.ReadMsgFile(requestPath).Member("fm");
            if (fm is not { } f || f.Member("from") is not { IsText: true } from || from.AsString() is not ("orchestrator" or "sub-orchestrator") || Untimed(s, sessionId, f, rid.AsString()))
            {
                continue;
            }

            var role = StringOf(exchange.Member("to"));
            var toAddr = row.Member("to_addr") is { IsText: true } ta ? ta.AsString() : null;
            var responsePath = StringOf(exchange.Member("response")?.Member("path"));
            // An exchange that holds a landed report is never waiting on the peer, even when the index keeps it open (a running lane's
            // charter): `msg close` refuses it as already reported, so listing it as outstanding asks for an action the verb forbids.
            if (!StillWaiting(s, exchange, now, out var reported))
            {
                if (reported && !reached(rid.AsString(), responsePath))
                {
                    landed.Add(new Landed(rid.AsString(), role, ToName(f, toAddr), responsePath));
                }

                continue;
            }

            var asked = new JsonArray();
            asked.Add(new JsonObject { ["id"] = rid.AsString(), ["created"] = f.Member("created") ?? JsonValue.Null });
            var originMs = peers.DispatchOrigin(asked).TryGetValue(rid.AsString(), out var originValue) && originValue is { IsText: true } ot && Timestamps.TryParseMs(ot.AsString(), out var origin)
                ? origin
                : now - s.Hierarchy.Messages.ExchangeAgeSec(exchange, now) * 1000;
            if (OutstandingIfDue(s, poolDir, sessionId, now, records, gatesOf(poolDir), rid.AsString(), role, f, requestPath, originMs, row, dueOnly) is { } due)
            {
                outstanding.Add(due);
            }
        }

        return (outstanding, landed);
    }

    private static string FormatAge(double sec)
    {
        var (value, unit) = sec < 3600 ? (Math.Floor(sec / 60), "m") : sec < 86400 ? (Math.Floor(sec / 3600), "h") : (Math.Floor(sec / 86400), "d");
        return string.Create(CultureInfo.InvariantCulture, $"{value}{unit}");
    }

    private static string? QueueNotice(GateContext c, AdvisoryServices advisory, string dir, string ownTeam, double now, string cwd, string sessionId)
    {
        try
        {
            return QueueRule.Notice(c.Services, advisory, dir, ProgressPool.Of(c.Services, cwd), ownTeam, now, cwd, sessionId);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonParseException or InvalidOperationException)
        {
            c.LogError(e);
            return null;
        }
    }

    /// <summary>Whether the request's member has a fresh working record, under the same rule the daemon's check-in holds back by. A member with no record is not held.</summary>
    private static bool WorkingPeer(GateServices s, string dir, Outstanding item, double now)
    {
        var team = item.ToAddr is { } address && address.Contains('@', StringComparison.Ordinal) ? address[(address.IndexOf('@', StringComparison.Ordinal) + 1)..] : null;
        return s.Roster.TeamMemberByName(dir, item.ToName, team).Member("session_id") is { IsText: true } session
            && s.Hierarchy.Status.ReadActivityRecord(dir, session) is { IsMap: true } record
            && HierarchyModule.RecordVerdict(record, now, dispatchOpen: true) is "working" or "waiting"
            && record.Member("working_at") is { IsText: true } at && Timestamps.TryParseMs(at.AsString(), out var workedAt)
            && WatchModule.WorkingHoldsCheckIn(now, workedAt, item.Base);
    }

    /// <summary>How many blocking check-ins one request gets; after that it is only listed, without a block.</summary>
    internal const int CheckInBlockCap = 3;

    private static StopItem StopItemOf(Outstanding it) => new(it.Role, it.ToName, it.Id, it.Path, FormatAge(it.AgeSec), it.Pane);

    private static string CheckInReason(GateServices s, AdvisoryServices advisory, List<Outstanding> items, string cwd) =>
        advisory.Directive.StopStillWaiting([.. items.Select(StopItemOf)], s.Config.CliCommand(CliTool.Roster, alwaysQuote: true), cwd);

    /// <summary>How many times the gates say this session was blocked on a check-in for the request.</summary>
    private static int CheckInBlocks(JsonArray gates, string sessionId, string requestId) =>
        gates.Count(g => g.Member("type") is { IsText: true } t && t.AsString() == "liveness-nudge"
            && g.Member("session_id") is { IsText: true } sid && sid.AsString() == sessionId
            && g.Member("request_id") is { IsText: true } rid && rid.AsString() == requestId);

    private static string EventMailReason(AdvisoryServices advisory, List<DueEventMail> events) =>
        advisory.Directive.StopNewTeamMail([.. events.Select(e => e.Text)]);

    private static string LandedReason(GateServices s, AdvisoryServices advisory, List<Landed> items) =>
        advisory.Directive.StopReportReady([.. items.Select(it =>
        {
            var followUp = s.Hierarchy.Messages.ReportFollowUp(it.ResponsePath);
            return new StopItem(it.Role, it.ToName, it.Id, it.ResponsePath, Extra: followUp is { IsText: true } f && f.AsString().Length > 0 ? f.AsString() : null);
        })]);

    /// <summary>True when this session has a dispatch whose exchange is still open and unreported.</summary>
    private static bool HasOpenDispatch(GateServices s, string sessionId, IExchangeIndex exchanges)
    {
        foreach (var r in DispatchObligations.Live(s, sessionId))
        {
            if (r.Member("path") is not { IsText: true } path || path.AsString().Length == 0
                || r.Member("request_id") is not { IsText: true } rid || s.Hierarchy.Peers.ReportShown(sessionId, rid))
            {
                continue;
            }

            var exchange = ExchangeAt(exchanges, path.AsString(), rid.AsString());
            if (!exchange.IsNull && exchange.Member("open") is { IsTruthy: true })
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The Claude Code version the README's Prerequisites name as the mod floor, or null.</summary>
    private static string? ModFloor(GateServices s)
    {
        var root = s.Environment.Plugin?.Root;
        if (root is null)
        {
            return null;
        }

        try
        {
            var readme = s.Files.ReadText(NodePath.Join(root, "README.md"));
            var m = FloorLine().Match(readme);
            return m.Success ? m.Groups[1].Value : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The one-line notice that ah's mod is not pulling for this session, once per session. <paramref name="regBefore"/> is the
    /// registration as it stood before this Stop registered anything: a session registered at this Stop has had no time to pull.
    /// </summary>
    private static string? ModNotice(GateServices s, AdvisoryServices advisory, JsonObject? regBefore, string sessionId, double now, JsonArray gates, IExchangeIndex exchanges)
    {
        if (regBefore is null || !advisory.Mailbox.RegistrationLive(regBefore))
        {
            return null;
        }

        var boxes = advisory.Mailbox.RegistrationBoxes(regBefore).Where(b => b.Address.StartsWith("orchestrator@", StringComparison.Ordinal)).ToList();
        if (boxes.Count == 0)
        {
            return null;
        }

        var limit = boxes.Max(b =>
        {
            var heartbeat = advisory.Mailbox.ReadHeartbeat(b.Dir, sessionId) ?? advisory.Mailbox.ReadHeartbeat(b.Dir);
            return advisory.MailCore.PullTimeoutMs(heartbeat is null ? JsonValue.Null : heartbeat);
        });
        if (regBefore.Get("at") is not { IsText: true } at || !Timestamps.TryParseMs(at.AsString(), out var atMs) || now - atMs <= limit)
        {
            return null;
        }

        if (boxes.Any(b => advisory.Mailbox.PullFresh(b.Dir, sessionId, now)))
        {
            return null;
        }

        if (!HasOpenDispatch(s, sessionId, exchanges))
        {
            return null;
        }

        if (gates.Any(g => g.Member("type") is { IsText: true } t && t.AsString() == "mod-notice"
                && g.Member("session_id") is { IsText: true } sid && sid.AsString() == sessionId))
        {
            return null;
        }

        var floor = ModFloor(s);
        return advisory.Directive.StopModNotice(floor);
    }
}
