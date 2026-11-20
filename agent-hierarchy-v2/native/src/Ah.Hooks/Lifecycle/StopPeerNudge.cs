using Ah.Abstractions;
using Ah.Config;
using Ah.Core.Json;
using Ah.Core.Time;
using Ah.Hierarchy;
using Ah.Hooks.Activity;
using Ah.Hooks.Gates;
using Ah.Hooks.Plumbing;
using Ah.Mesh;

namespace Ah.Hooks.Lifecycle;

/// <summary>
/// Stop hook for the peer report-back contract: a session with unresolved peer obligations, or briefs mailed to it that have no
/// response yet, has its stop blocked with what is owed. Bounded: an armed turn spends at most <c>MaxNudges</c> blocks per
/// obligation then waives it; an unarmed turn blocks once; in-flight background work, subagents and any failure allow.
/// </summary>
[HookIdentity(HookIdentityKind.Grants)]
internal static partial class StopPeerNudge
{
    /// <summary>
    /// A relay claim this session holds that no PostToolUse settled is settled from the session's own transcript, by the tool call
    /// that took it: an error result (a denied call is one) fails it, a result that reached the inbox confirms it, and no result
    /// moves nothing.
    /// </summary>
    private static void ResolveFromTranscript(GateContext c, AdvisoryServices advisory, string sessionId, string dir, string entry)
    {
        try
        {
            if (advisory.Mailbox.ReadClaim(dir, entry) is not { } claim
                || claim.Get("state") is not { IsText: true } state
                || state.AsString() is not (Ah.Mesh.ClaimStates.RelayClaimed or Ah.Mesh.ClaimStates.RelayUnconfirmed)
                || claim.Get("holder") is not { IsMap: true } holder
                || holder.AsObject().Get("session_id") is not { IsText: true } holderSession || holderSession.AsString() != sessionId
                || holder.AsObject().Get("tool_use_id") is not { IsText: true } call
                || c.Input.Text("transcript_path") is not { Length: > 0 } path)
            {
                return;
            }

            var result = RelayTranscript.ResultOf(c.Services.Files, path, call.AsString(), out var reason);
            if (result == RelayResult.Unknown)
            {
                return;
            }

            var args = new JsonObject { ["session_id"] = sessionId };
            if (result == RelayResult.Failed && reason is not null)
            {
                args["reason"] = reason;
            }

            _ = advisory.Mailbox.Claim(dir, entry, result == RelayResult.Sent ? Ah.Mesh.ClaimActions.Confirm : Ah.Mesh.ClaimActions.Fail, args);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StoreUnavailableException)
        {
            c.LogError(ex);
        }
    }

    /// <summary>The first time the obligation was recorded: its row is rewritten with a new stamp at every nudge, its earliest row keeps the first.</summary>
    private static double? ObligationStartMs(GateContext c, JsonValue rec)
    {
        if (rec.Member("session_id") is not { IsText: true } session || rec.Member("task") is not { IsText: true } task)
        {
            return null;
        }

        double? first = null;
        foreach (var r in c.Services.Hierarchy.Core.SessionRows(session.AsString()))
        {
            if (r.IsTruthy && r.Member("session_id") is { IsText: true } s && s.AsString() == session.AsString()
                && r.Member("task") is { IsText: true } t && t.AsString() == task.AsString()
                && r.Member("ts") is { IsText: true } ts && Timestamps.TryParseMs(ts.AsString(), out var ms) && (first is null || ms < first))
            {
                first = ms;
            }
        }

        return first;
    }

    private static bool SentByTheCarrier(GateContext c, JsonValue rec, JsonValue relay, string entry)
    {
        try
        {
            return relay.Member("to") is { IsText: true } to
                && c.Input.Text("transcript_path") is { Length: > 0 } path
                && RelayTranscript.SentTo(c.Services.Files, path, to.AsString(), entry, ObligationStartMs(c, rec));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StoreUnavailableException)
        {
            c.LogError(ex);
            return false;
        }
    }

    /// <summary>
    /// What a session owes is read from the peer log, and an empty log is the answer "owes nothing". A log that cannot be read says
    /// nothing, so the Stop is held once with that reason; a second Stop (<c>stop_hook_active</c>) is let through so a broken store cannot trap a session.
    /// </summary>
    public static HookOutcome Run(GateContext c)
    {
        if (!OrchestratorIdentity.In(c))
        {
            return HookEmit.Silent;
        }

        using (c.Services.Hierarchy.Core.DecisionReads())
        {
            try
            {
                return RunOwed(c);
            }
            catch (StoreUnavailableException error) when (c.Services.Advisory is not null)
            {
                c.LogError(error);
                var again = c.Input.Member("stop_hook_active") is { IsBool: true } active && active.AsBool();
                return again ? HookEmit.Silent : HookEmit.Block(c.Services.Advisory.Directive.StopStateUnreadable(error.Message));
            }
        }
    }

    private static HookOutcome RunOwed(GateContext c)
    {
        var s = c.Services;
        if (s.Config.IsSubagent(c.Input))
        {
            return HookEmit.Silent;
        }

        var sessionId = c.Input.Text("session_id") ?? string.Empty;
        if (sessionId.Length == 0)
        {
            return HookEmit.Silent;
        }

        if (HoldGate.Of(s, c.Cwd(), sessionId) is not null || Ah.Roster.RosterModule.ChainDownActive(s.Hierarchy.Core.PeerRecordsShared(), sessionId))
        {
            return HookEmit.Silent;
        }

        var advisory = s.Advisory ?? throw new InvalidOperationException("the advisory services are not composed");
        var peers = s.Hierarchy.Peers;
        var session = JsonValue.FromString(sessionId);

        // A relayed message that drained while this session was busy never reached a prompt hook; its transcript is where it is seen.
        try
        {
            _ = advisory.MailboxHooks.AckFromTranscript(sessionId, c.Input.Text("transcript_path"));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StoreUnavailableException)
        {
            c.LogError(ex);
        }

        JsonValue NowText() => JsonValue.FromString(Timestamps.ToIsoString(s.Clock.UtcNow));

        // A relay obligation is resolved by the entry's ack, whoever wrote it; check before anything is nudged.
        foreach (var rec in PeerLatestFor.Of(c).PendingFor(session))
        {
            if (rec.Member("relay") is { IsTruthy: true } relay)
            {
                var dir = relay.Member("dir");
                var entry = relay.Member("entry");
                if (dir is not { IsText: true } d || entry is not { IsText: true } e)
                {
                    throw new InvalidOperationException("the relay obligation names no mailbox entry");
                }

                ResolveFromTranscript(c, advisory, sessionId, d.AsString(), e.AsString());

                // The carrier's own successful SendMessage of the entry to its recipient is the relay, whatever the claim says.
                if (SentByTheCarrier(c, rec, relay, e.AsString()))
                {
                    peers.RecordPeerRow(RecordShapes.With(rec, ("ts", NowText()), ("status", "resolved"), ("void", "relayed")));
                    continue;
                }

                // Nothing is owed once the entry is acked, delivered, superseded, declined, or held by the sender's relay.
                if (advisory.Mailbox.RelayVoidNote(d.AsString(), e.AsString()) is { } voidNote)
                {
                    // A declined relay needs nothing from the session: the resolved row is the record and the Stop stays quiet.
                    peers.RecordPeerRow(RecordShapes.With(rec, ("ts", NowText()), ("status", "resolved"), ("void", voidNote)));
                }
            }
        }

        // A report already in the sender's box satisfies an owed report, whichever path the brief took.
        foreach (var rec in PeerLatestFor.Of(c).PendingFor(session))
        {
            if (rec.Member("relay") is not { IsTruthy: true }
                && rec.Member("msg") is { IsText: true } msg && rec.Member("task") is { IsText: true } task
                && advisory.MailboxHooks.ReportStateOf(sessionId, new OpenBrief(task.AsString(), msg.AsString(), rec.Member("from")), out _) == ReportState.Sent)
            {
                peers.RecordPeerRow(RecordShapes.With(rec, ("ts", NowText()), ("status", "resolved")));
            }
        }

        var charters = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
        var owed = PeerLatestFor.Of(c).PendingFor(session).Where(rec => !(rec.Member("msg") is { IsText: true } owedMsg && IsOpenLaneCharter(s, charters, owedMsg.AsString()))).ToList();
        var mail = MailOwed(s, advisory, sessionId, charters);
        var alreadyOwed = owed.Select(static r => r.Member("task") is { IsText: true } t ? t.AsString() : string.Empty)
            .Concat(mail.Select(static m => m.Brief.Id)).ToHashSet(StringComparer.Ordinal);
        var fileOwed = FileOwed(c, advisory, sessionId, charters).Where(o => !alreadyOwed.Contains(o.RequestId)).ToList();
        if (IsOrchestrateSession(c, sessionId) && UserPromptSubmitPeerTracking.OwnsOrchestratorBox(advisory, sessionId))
        {
            // An Orchestrator owing a response it has not written is doing long work: the digest says so instead of a block. A
            // response written and not sent is one command away and still blocks. Without an orchestrator@ box there is no
            // digest to carry it (a role-less member also lands in the orchestrate class), so the block stays.
            AdviseReportsOwed(c, advisory, sessionId, [.. mail.Where(static m => m.Written is null).Select(static m => m.Brief)]);
            mail = [.. mail.Where(static m => m.Written is not null)];
        }
        var qaOwed = QaOwed(c, sessionId);
        var reStop = c.Input.Member("stop_hook_active") is { IsBool: true } stopActive && stopActive.AsBool();
        if (!reStop && SubOrchestratorStop.WakeNotice(c, advisory, sessionId) is { } wakeNotice)
        {
            return HookEmit.Block(wakeNotice);
        }

        if (owed.Count == 0 && mail.Count == 0 && fileOwed.Count == 0 && qaOwed.Count == 0)
        {
            return HookEmit.Silent;
        }

        if (peers.HasInflightSubagent(session))
        {
            return HookEmit.Silent;
        }

        if (SubOrchestratorStop.WaitsOnItsSubTeam(c, advisory, sessionId))
        {
            return HookEmit.Silent;
        }

        var role = s.Config.ResolveHierarchyRole(c.Input).Role;

        if (mail.Count > 0)
        {
            var now = NowText();
            var rows = new JsonArray();
            foreach (var (brief, _, _) in mail)
            {
                rows.Add(new JsonObject { ["type"] = "mail-nudge", ["session_id"] = sessionId, ["task"] = brief.Id, ["ts"] = now });
            }

            peers.RecordPeerRow(rows);
            var briefs = advisory.Directive.StopBriefsOwed([.. mail.Select(m => MailLine(s, advisory, m.Brief, m.Nudges, role, m.Written))]);
            return HookEmit.Block(briefs);
        }

        var marker = peers.LatestTurnMarker(session);
        var armed = marker.IsTruthy && marker.Member("status") is { IsText: true } st && st.AsString() == "armed";

        // A user-driven turn lets the re-stop through; every block, armed or not, spends the same per-obligation budget.
        if (!armed && c.Input.Member("stop_hook_active") is { IsBool: true } active && active.AsBool())
        {
            return HookEmit.Silent;
        }

        if (owed.Count == 0)
        {
            return BlockOnFileOwed(c, advisory, sessionId, fileOwed, qaOwed);
        }

        var toNudge = new List<JsonObject>();
        var stamp = NowText();
        foreach (var rec in owed)
        {
            var nudges = rec.Member("nudges") is { IsNumber: true } n ? n.AsNumber() : 0;
            if (nudges >= HierarchyModule.MaxNudges)
            {
                peers.RecordPeerRow(RecordShapes.With(rec, ("ts", stamp), ("status", "waived")));
            }
            else
            {
                var updated = RecordShapes.With(rec, ("ts", stamp), ("status", "pending"), ("nudges", nudges + 1));
                peers.RecordPeerRow(updated);
                toNudge.Add(updated);
            }
        }

        var qaDue = QaDue(c, sessionId, qaOwed);

        // The marker is released only once nothing is owed on it any more: blocking does not spend it.
        if (toNudge.Count == 0 && qaDue.Count > 0)
        {
            return HookEmit.Block(advisory.Directive.StopReportsOwed(null, QaBlockLines(c, sessionId, qaDue)));
        }

        if (toNudge.Count == 0)
        {
            if (peers.LatestTurnMarker(session) is { IsTruthy: true } latest && latest.Member("status") is { IsText: true } status && status.AsString() == "armed")
            {
                peers.AppendTurnMarker(session, "disarmed");
            }

            return HookEmit.Silent;
        }

        static bool Final(JsonObject rec) => rec.Get("nudges") is { IsNumber: true } n && n.AsNumber() >= HierarchyModule.MaxNudges;
        var lines = toNudge.Select(rec => OwedLine(s, rec, Final(rec), role)).ToList();
        lines.AddRange(QaBlockLines(c, sessionId, qaDue));
        var to = toNudge.Count == 1 && qaDue.Count == 0 && toNudge[0].Get("relay") is not { IsTruthy: true } ? RecordShapes.Js(toNudge[0].Get("from_name")) : null;
        return HookEmit.Block(advisory.Directive.StopReportsOwed(to, lines));
    }

    internal static string OwedLine(GateServices s, JsonValue rec, bool isFinal, string? role)
    {
        if (rec.Member("relay") is { IsTruthy: true } relay)
        {
            return MeshModule.RelayInstructionText(
                RecordShapes.Js(relay.Member("entry")),
                RecordShapes.Js(relay.Member("address")),
                RecordShapes.Js(relay.Member("to")),
                RecordShapes.Js(relay.Member("line")));
        }

        string? message = null;
        if (rec.Member("msg") is { IsTruthy: true } msg)
        {
            var meta = msg.IsText ? s.Hierarchy.Core.ParseMsgFilename(msg.AsString()) : null;
            var id = meta is null ? "<id>" : meta.Id;
            message = $" The message you send must carry [hierarchy-msg {id} response].";
            var fill = $"`ah msg fill --id {id} --from {role ?? "<your role>"} --body-file -`";
            var plan = s.Hierarchy.Messages.ResponsePlan(msg);
            if (plan.IsTruthy && plan.Member("path") is { IsText: true } filePath && s.Hierarchy.Core.MsgExists(filePath.AsString())
                && s.Hierarchy.Messages.ReportStatus(filePath.AsString(), id) == "no-report")
            {
                return OwedText(
                    s,
                    RecordShapes.Js(rec.Member("task")),
                    RecordShapes.Js(rec.Member("from_name")),
                    RecordShapes.Js(rec.Member("from")),
                    $"{message} ah: response {id} is still the template — fill it with {fill}, then send again.",
                    isFinal);
            }

            message += $" File it with {fill} — then SEND it.";
        }

        return OwedText(s, RecordShapes.Js(rec.Member("task")), RecordShapes.Js(rec.Member("from_name")), RecordShapes.Js(rec.Member("from")), message, isFinal);
    }

    private static string OwedText(GateServices s, string task, string fromName, string from, string? message, bool isFinal) =>
        (s.Advisory ?? throw new InvalidOperationException("the advisory services are not composed")).Directive.StopReportOwedLine(task, fromName, from, message, isFinal);

    /// <summary>
    /// Whether the request at <paramref name="requestPath"/> is the charter of a lane that is still running. Its response cannot be written
    /// until the lane is done, so the charter is not owed while the lane is open.
    /// </summary>
    private static bool IsOpenLaneCharter(GateServices s, Dictionary<string, IReadOnlySet<string>> charters, string requestPath)
    {
        var meta = s.Hierarchy.Core.ParseMsgFilename(Path.GetFileName(requestPath));
        var pool = Path.GetDirectoryName(Path.GetDirectoryName(requestPath));
        if (meta is null || string.IsNullOrEmpty(pool))
        {
            return false;
        }

        if (!charters.TryGetValue(pool, out var ids))
        {
            ids = s.Hierarchy.Core.OpenLaneCharterIds(pool);
            charters[pool] = ids;
        }

        return ids.Contains(meta.Id);
    }

    private sealed record FileOwedItem(string RequestId, string ResponsePath, string? Team);

    /// <summary>
    /// The responses this member wrote since its turn began for requests addressed to it, whose report has reached no Orchestrator box
    /// and was not sent by a tokened SendMessage. Found from the message files, so a brief that travelled untokened is covered.
    /// </summary>
    private static List<FileOwedItem> FileOwed(GateContext c, AdvisoryServices advisory, string sessionId, Dictionary<string, IReadOnlySet<string>> charters)
    {
        var s = c.Services;
        var found = new List<FileOwedItem>();
        if (s.Config.ResolveHierarchyRole(c.Input).Role is not { } role || AutoTrack.SelfOf(c, role) is not { } self
            || s.Hierarchy.Peers.LatestTurnMarker(JsonValue.FromString(sessionId)) is not { IsTruthy: true } marker
            || marker.Member("ts") is not { IsText: true } ts || !Timestamps.TryParseMs(ts.AsString(), out var turnMs))
        {
            return found;
        }

        var sentResponses = s.Hierarchy.Core.SessionRows(sessionId, "sent")
            .Where(r => r.IsTruthy && r.Member("type") is { IsText: true } t && t.AsString() == "sent" && r.Member("session_id") is { IsText: true } sid && sid.AsString() == sessionId)
            .Select(static r => r.Member("response") is { IsText: true } p ? MsgRows.PathKey(p.AsString()) : string.Empty)
            .ToHashSet(StringComparer.Ordinal);
        // Every exchange, not only the open ones: a response that carries its report no longer counts as open.
        foreach (var exchange in s.Hierarchy.Core.ListExchanges(s.Config.HierarchyDir(c.Cwd())))
        {
            if (exchange.Member("request")?.Member("path") is not { IsText: true } requestPath
                || exchange.Member("response")?.Member("path") is not { IsText: true } responsePath
                || (s.Hierarchy.Core.MsgTimeMs(responsePath.AsString()) is { } written ? written < turnMs : !s.Hierarchy.Core.MsgExists(responsePath.AsString()))
                || s.Hierarchy.Core.ReadMsgFile(requestPath.AsString()).Member("fm") is not { IsMap: true } fm
                || fm.Text("type") != "request" || fm.Text("to") != role
                || (fm.Member("to_name") is { IsText: true } toName && toName.AsString() != self.Name)
                || (fm.Text("team") is { Length: > 0 } team && team != "null" ? team : string.Empty) != (self.Team ?? string.Empty)
                || exchange.Member("id") is not { IsText: true } id
                || IsOpenLaneCharter(s, charters, requestPath.AsString())
                || s.Hierarchy.Messages.ReportStatus(responsePath.AsString(), id.AsString()) != "reported"
                || sentResponses.Contains(MsgRows.PathKey(responsePath.AsString()))
                || advisory.MailboxHooks.ReportStateOf(sessionId, new OpenBrief(id.AsString(), requestPath.AsString(), fm.Member("from")), out _) == ReportState.Sent)
            {
                continue;
            }

            found.Add(new FileOwedItem(id.AsString(), responsePath.AsString(), self.Team));
        }

        return found;
    }

    /// <summary>Blocks the stop for written-but-unsent responses within the per-obligation budget; silent once every one has spent it.</summary>
    private static HookOutcome BlockOnFileOwed(GateContext c, AdvisoryServices advisory, string sessionId, List<FileOwedItem> owed, List<QaOwedItem> qaOwed)
    {
        var s = c.Services;
        var spent = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var r in s.Hierarchy.Core.SessionRows(sessionId, "file-nudge"))
        {
            if (r.IsTruthy && r.Member("type") is { IsText: true } t && t.AsString() == "file-nudge"
                && r.Member("session_id") is { IsText: true } sid && sid.AsString() == sessionId && r.Member("task") is { IsText: true } task)
            {
                spent[task.AsString()] = spent.GetValueOrDefault(task.AsString()) + 1;
            }
        }

        var due = owed.Where(o => spent.GetValueOrDefault("report:" + o.RequestId) < HierarchyModule.MaxNudges).ToList();
        var qaDue = QaDue(c, sessionId, qaOwed);
        if (due.Count == 0 && qaDue.Count == 0)
        {
            return HookEmit.Silent;
        }

        var now = JsonValue.FromString(Timestamps.ToIsoString(s.Clock.UtcNow));
        var rows = new JsonArray();
        foreach (var o in due)
        {
            rows.Add(new JsonObject { ["type"] = "file-nudge", ["session_id"] = sessionId, ["task"] = "report:" + o.RequestId, ["ts"] = now });
        }

        if (rows.Count > 0)
        {
            s.Hierarchy.Peers.RecordPeerRow(rows);
        }

        var msg = s.Config.CliCommand(CliTool.Msg, alwaysQuote: true);
        var lines = due.Select(o =>
            $"ah: you filed response {o.RequestId} but did not send it. Run: {msg} send orchestrator{(o.Team is null ? string.Empty : "@" + o.Team)} --kind report --ref {o.RequestId} --cwd {c.Cwd()}, then do what its delivery line says.").ToList();
        lines.AddRange(QaBlockLines(c, sessionId, qaDue));
        return HookEmit.Block(advisory.Directive.StopReportsOwed(null, lines));
    }

    /// <summary>Records one block against each due QA item and returns the lines that name what to run.</summary>
    private static List<string> QaBlockLines(GateContext c, string sessionId, List<QaOwedItem> qaDue)
    {
        var now = JsonValue.FromString(Timestamps.ToIsoString(c.Services.Clock.UtcNow));
        var rows = new JsonArray();
        foreach (var q in qaDue)
        {
            rows.Add(new JsonObject { ["type"] = QaNudgeType, ["session_id"] = sessionId, ["task"] = QaTask(q), ["ts"] = now });
        }

        // ah2-312 M4: the cadence question is asked on the session's first DONE only
        var askCadence = qaDue.Any(static q => q.AskOnce) && !CadenceAsked(c, sessionId);
        if (askCadence)
        {
            rows.Add(new JsonObject { ["type"] = QaNudgeType, ["session_id"] = sessionId, ["task"] = QaCadenceTask, ["ts"] = now });
        }

        if (rows.Count > 0)
        {
            c.Services.Hierarchy.Peers.RecordPeerRow(rows);
        }

        return QaLines(c, qaDue, askCadence);
    }

    /// <summary>The <c>qa-nudge</c> task that records this session was asked when QA runs.</summary>
    private const string QaCadenceTask = "qa-cadence";

    private static bool CadenceAsked(GateContext c, string sessionId) =>
        c.Services.Hierarchy.Core.SessionRows(sessionId, QaNudgeType).Any(r => r.IsTruthy && r.Member("session_id") is { IsText: true } sid && sid.AsString() == sessionId
            && r.Member("task") is { IsText: true } task && task.AsString() == QaCadenceTask);

    private static List<string> QaLines(GateContext c, List<QaOwedItem> qaDue, bool askCadence)
    {
        var msg = c.Services.Config.CliCommand(CliTool.Msg, alwaysQuote: true);
        List<string> lines = [.. qaDue.Select(q =>
            $"ah: {q.Member} reported DONE on {q.Slug} ({q.ResponsePath}) and no QA request for {q.Slug} exists. Dispatch QA: {ConfigModule.QaDispatchStep(q.Member, q.Slug, msg, c.Cwd())} A docs-only item opts out with `qa: none` in its original request's frontmatter.")];
        if (askCadence)
        {
            lines.Add(QaChooseLine);
        }

        return lines;
    }

    /// <summary>
    /// Whether the session is an Orchestrator or Sub-Orchestrator: its role is of the orchestrate class. A top-level Orchestrator session
    /// carries no agent type, so an unresolved role is the Orchestrator too.
    /// </summary>
    private static bool IsOrchestrateSession(GateContext c, string sessionId)
    {
        var s = c.Services;
        var role = s.Config.ResolveHierarchyRole(c.Input).Role;
        return role is null || s.Config.RoleClass(role, s.Config.ResolveConfig(c.Cwd(), new ResolveOptions(SessionId: sessionId))) == "orchestrate";
    }

    private const string AdvisoryEvent = "report-owed-advisory";

    private static readonly TimeSpan AdvisoryEvery = TimeSpan.FromHours(1);

    /// <summary>
    /// Appends one <c>report-owed-advisory</c> event per owed brief, at most once an hour each, to the session's own <c>orchestrator@</c>
    /// boxes, where the digest reads it at the next prompt. A session with no such box gets nothing. A failure is logged, never blocks.
    /// </summary>
    private static void AdviseReportsOwed(GateContext c, AdvisoryServices advisory, string sessionId, IReadOnlyList<OpenBrief> briefs)
    {
        var s = c.Services;
        try
        {
            if (briefs.Count == 0 || advisory.Mailbox.ReadRegistration(sessionId) is not { } registration)
            {
                return;
            }

            var boxes = advisory.Mailbox.RegistrationBoxes(registration).Where(static b => b.Address.StartsWith("orchestrator@", StringComparison.Ordinal)).ToList();
            if (boxes.Count == 0)
            {
                return;
            }

            var now = s.Clock.UtcNow;
            var last = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var r in s.Hierarchy.Core.SessionRows(sessionId, AdvisoryEvent))
            {
                if (r.Member("type") is { IsText: true } t && t.AsString() == AdvisoryEvent && r.Member("session_id") is { IsText: true } sid && sid.AsString() == sessionId
                    && r.Member("task") is { IsText: true } task && r.Member("ts") is { IsText: true } ts && Timestamps.TryParseMs(ts.AsString(), out var ms))
                {
                    last[task.AsString()] = Math.Max(last.GetValueOrDefault(task.AsString()), ms);
                }
            }

            var dir = s.Config.HierarchyDir(c.Cwd());
            var rows = new JsonArray();
            foreach (var brief in briefs.Where(b => !last.TryGetValue(b.Id, out var at) || now.ToUnixTimeMilliseconds() - at >= AdvisoryEvery.TotalMilliseconds))
            {
                foreach (var box in boxes)
                {
                    _ = advisory.Mailbox.Send(new JsonObject
                    {
                        ["to"] = box.Address,
                        ["from"] = "ahd",
                        ["cwd"] = dir,
                        ["hierarchy_dir"] = dir,
                        ["event"] = AdvisoryEvent,
                        ["text"] = IDirectiveText.DigestReportOwed(brief.Id, RecordShapes.Js(brief.From)),
                        ["data"] = new JsonObject { ["request_id"] = brief.Id },
                    });
                }

                rows.Add(new JsonObject { ["type"] = AdvisoryEvent, ["session_id"] = sessionId, ["task"] = brief.Id, ["ts"] = Timestamps.ToIsoString(now) });
            }

            if (rows.Count > 0)
            {
                s.Hierarchy.Peers.RecordPeerRow(rows);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException and not StoreUnavailableException)
        {
            c.LogError(e);
        }
    }

    /// <summary>The one test of whether a role is the QA role; both the exclusion and the satisfaction side of the owed check use it.</summary>
    private static bool IsQaRole(string role) => role == "qa";

    private const string QaNudgeType = "qa-nudge";

    private const string QaWaivedType = "qa-waived";

    /// <summary>One owed QA dispatch. <paramref name="AskOnce"/>: the repo has not chosen when QA runs (config <c>qa.cadence</c> unset), so it is raised once, not repeated.</summary>
    private sealed record QaOwedItem(string RequestId, string Slug, string Member, string ResponsePath, bool AskOnce = false);

    /// <summary>The line an unconfigured repo's QA prompt ends with (ah2-154 D5).</summary>
    private const string QaChooseLine = "ah: when QA runs is not configured, so this is asked once per DONE report. Choose: /wrangl:hierarchy qa item (QA at each item's merge) or /wrangl:hierarchy qa batch (QA once on a release batch).";

    private static string QaTask(QaOwedItem q) => "qa:" + q.RequestId;

    private static double? CreatedMs(JsonValue msgFile) =>
        msgFile.Member("fm")?.Member("created") is { IsText: true } created && Timestamps.TryParseMs(created.AsString(), out var ms) ? ms : null;

    /// <summary>
    /// The implement-class DONE responses that arrived during this orchestrator session and have no QA request filed after them.
    /// Only an orchestrate-class session owes them; an item whose request carries <c>qa: none</c> is exempt.
    /// </summary>
    private static List<QaOwedItem> QaOwed(GateContext c, string sessionId)
    {
        var found = new List<QaOwedItem>();
        try
        {
            var s = c.Services;
            var resolved = s.Config.ResolveConfig(c.Cwd(), new ResolveOptions(SessionId: sessionId));
            if (!IsOrchestrateSession(c, sessionId))
            {
                return found;
            }

            // ah2-154: "batch" raises no per-item QA prompt; unset (null) raises it once.
            var qa = resolved.Get("qaCadence") is { IsText: true } qaMode ? qaMode.AsString() : null;
            if (qa == "batch")
            {
                return found;
            }

            var role = s.Config.ResolveHierarchyRole(c.Input).Role ?? "orchestrator";

            double? startMs = null;
            foreach (var r in s.Hierarchy.Core.SessionRows(sessionId))
            {
                if (r.IsTruthy && r.Member("session_id") is { IsText: true } sid && sid.AsString() == sessionId
                    && r.Member("ts") is { IsText: true } ts && Timestamps.TryParseMs(ts.AsString(), out var ms) && (startMs is null || ms < startMs))
                {
                    startMs = ms;
                }
            }

            if (startMs is null)
            {
                return found;
            }

            var self = AutoTrack.SelfOf(c, role);
            var exchanges = s.Hierarchy.Core.ListExchanges(s.Config.HierarchyDir(c.Cwd())).Cast<JsonValue>().ToList();
            static string? ItemOf(JsonValue fm) => fm.Text("item") is { Length: > 0 } i && i != "null" ? i : null;
            var qaRequests = new List<(string Slug, string? Item, string Text, double? Ms)>();
            foreach (var e in exchanges)
            {
                if (e.Member("request")?.Member("path") is { IsText: true } rp && e.Member("to") is { IsText: true } to && IsQaRole(to.AsString())
                    && s.Hierarchy.Core.ReadMsgFile(rp.AsString()) is { IsMap: true } file && file.Member("text") is { IsText: true } text)
                {
                    qaRequests.Add((e.Member("slug") is { IsText: true } sl ? sl.AsString() : string.Empty, file.Member("fm") is { IsMap: true } qfm ? ItemOf(qfm) : null, text.AsString(), CreatedMs(file)));
                }
            }

            var atBaseByTeam = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var e in exchanges)
            {
                if (e.Member("request")?.Member("path") is not { IsText: true } requestPath
                    || e.Member("response")?.Member("path") is not { IsText: true } responsePath
                    || e.Member("id") is not { IsText: true } id || e.Member("to") is not { IsText: true } to
                    || e.Member("slug") is not { IsText: true } slugText
                    || IsQaRole(to.AsString()) || s.Config.RoleClass(to.AsString(), resolved) != "implement"
                    || s.Hierarchy.Core.ReadMsgFile(requestPath.AsString()) is not { IsMap: true } request || request.Member("fm") is not { IsMap: true } rfm
                    || (self is { } me && (rfm.Text("team") is { Length: > 0 } t && t != "null" ? t : string.Empty) != (me.Team ?? string.Empty))
                    || rfm.Text("qa") == "none"
                    || s.Hierarchy.Core.ReadMsgFile(responsePath.AsString()) is not { IsMap: true } response || response.Member("text") is not { IsText: true } responseText
                    || CreatedMs(response) is not { } responseMs || responseMs < startMs
                    || !HierarchyModule.IsDoneReport(responseText.AsString()))
                {
                    continue;
                }

                // ah2-312 M3'': the checklist's report row says the DONE's sha is the item's base: nothing committed, nothing to QA
                var team = HierarchyModule.ProgressTeamOf(rfm.Text("team"));
                if (!atBaseByTeam.TryGetValue(team, out var atBase))
                {
                    atBase = atBaseByTeam[team] = HierarchyModule.ChecklistEntries(s.Hierarchy.Progress, ProgressPool.Of(s, c.Cwd()), team)
                        .SelectMany(static e => e.Item.AtBaseRefs ?? []).ToHashSet(StringComparer.Ordinal);
                }

                if (atBase.Contains(id.AsString()))
                {
                    continue;
                }

                var slug = slugText.AsString();
                var item = ItemOf(rfm);
                // ah2-312 M1: the same checklist item counts whatever the QA request's slug; slug and ship branch stay as fallbacks
                var filed = qaRequests.Any(q => ((item is not null && q.Item == item) || q.Slug == slug || q.Text.Contains("ah/ship/" + slug, StringComparison.Ordinal)) && (q.Ms is null || q.Ms >= responseMs));
                if (!filed)
                {
                    found.Add(new QaOwedItem(id.AsString(), slug, rfm.Member("to_name") is { IsText: true } name && name.AsString() != "null" ? name.AsString() : to.AsString(), responsePath.AsString(), qa is null));
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StoreUnavailableException)
        {
            c.LogError(ex);
            found.Clear();
        }

        return found;
    }

    /// <summary>
    /// The QA items still within their block budget. An item that has spent it is waived with one journal row and is silent from then on.
    /// </summary>
    private static List<QaOwedItem> QaDue(GateContext c, string sessionId, List<QaOwedItem> owed)
    {
        var peers = c.Services.Hierarchy.Peers;
        var spent = new Dictionary<string, int>(StringComparer.Ordinal);
        var waived = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in c.Services.Hierarchy.Core.SessionRows(sessionId, QaNudgeType, QaWaivedType))
        {
            if (r.IsTruthy && r.Member("session_id") is { IsText: true } sid && sid.AsString() == sessionId
                && r.Member("type") is { IsText: true } type && r.Member("task") is { IsText: true } task)
            {
                if (type.AsString() == QaNudgeType)
                {
                    spent[task.AsString()] = spent.GetValueOrDefault(task.AsString()) + 1;
                }
                else if (type.AsString() == QaWaivedType)
                {
                    _ = waived.Add(task.AsString());
                }
            }
        }

        var due = new List<QaOwedItem>();
        foreach (var q in owed)
        {
            if (spent.GetValueOrDefault(QaTask(q)) < (q.AskOnce ? 1 : HierarchyModule.MaxNudges))
            {
                due.Add(q);
            }
            else if (!waived.Contains(QaTask(q)))
            {
                peers.RecordPeerRow(new JsonObject
                {
                    ["type"] = QaWaivedType,
                    ["session_id"] = sessionId,
                    ["task"] = QaTask(q),
                    ["slug"] = q.Slug,
                    ["note"] = $"QA dispatch for {q.Slug} was owed after {q.Member}'s DONE and never filed; the Stop hook stopped asking",
                    ["ts"] = JsonValue.FromString(Timestamps.ToIsoString(c.Services.Clock.UtcNow)),
                });
            }
        }

        return due;
    }

    /// <summary>Briefs mailed to this session that entered a turn and have no response, each with the nudges already spent on it; those at the ceiling drop out.</summary>
    private static List<(OpenBrief Brief, int Nudges, string? Written)> MailOwed(GateServices s, AdvisoryServices advisory, string sessionId, Dictionary<string, IReadOnlySet<string>> charters)
    {
        var nudged = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var r in s.Hierarchy.Core.SessionRows(sessionId, "mail-nudge"))
        {
            if (r.IsTruthy && r.Member("type") is { IsText: true } t && t.AsString() == "mail-nudge"
                && r.Member("session_id") is { IsText: true } sid && sid.AsString() == sessionId
                && r.Member("task") is { IsText: true } task)
            {
                nudged[task.AsString()] = nudged.GetValueOrDefault(task.AsString()) + 1;
            }
        }

        var owed = new List<(OpenBrief Brief, int Nudges, string? Written)>();
        foreach (var b in advisory.MailboxHooks.OpenMailBriefs(sessionId))
        {
            if (IsOpenLaneCharter(s, charters, b.Ref))
            {
                continue;
            }

            var state = advisory.MailboxHooks.ReportStateOf(sessionId, b, out var written);
            var spent = nudged.GetValueOrDefault(b.Id);
            if (state != ReportState.Sent && spent < HierarchyModule.MaxNudges)
            {
                owed.Add((b, spent, state == ReportState.WrittenNotSent ? written : null));
            }
        }

        return owed;
    }

    private static string MailLine(GateServices s, AdvisoryServices advisory, OpenBrief b, int nudges, string? role, string? written)
    {
        if (written is not null)
        {
            return advisory.Directive.StopBriefWrittenLine(RecordShapes.Js(b.From), written);
        }

        return advisory.Directive.StopBriefLine(b.Id, RecordShapes.Js(b.From), b.Ref, role, nudges + 1 >= HierarchyModule.MaxNudges);
    }
}
