using System.Globalization;
using Ah.Core.Json;
using Ah.Core.Time;
using Ah.Hierarchy;

namespace Ah.Watch;

/// <summary>One watched dispatch of a session, resolved from its request file and exchange (the caller reads them).</summary>
/// <param name="RequestId">The exchange id.</param>
/// <param name="To">The addressee role of the exchange.</param>
/// <param name="Slug">The exchange's slug.</param>
/// <param name="ToAddr">The dispatch row's <c>to_addr</c>, or null.</param>
/// <param name="ToName">The request's <c>to_name</c>, or null.</param>
/// <param name="Eta">The request's <c>eta</c>.</param>
/// <param name="Created">The request's <c>created</c> frontmatter text, or null.</param>
/// <param name="Open">Whether the exchange has no response yet.</param>
/// <param name="ResponsePath">Where the response is, or would be.</param>
/// <param name="ResponseLanded">For a closed exchange: whether the response counts as landed at <c>now</c>.</param>
/// <param name="LandedFollowUp">For a landed response: the follow-up rule text, without its leading space, or empty.</param>
/// <param name="HierarchyDir">The hierarchy dir holding the request file (the dedupe rows go there).</param>
public sealed record DispatchCandidate(
    string RequestId,
    string To,
    string Slug,
    string? ToAddr,
    string? ToName,
    string Eta,
    string? Created,
    bool Open,
    string ResponsePath,
    bool ResponseLanded,
    string LandedFollowUp,
    string HierarchyDir)
{
    /// <summary>The request's <c>team</c> frontmatter: the team whose orchestrator box its events are mailed to when <see cref="ToAddr"/> names none.</summary>
    public string? Team { get; init; }

    /// <summary>The request's <c>item:</c> frontmatter: the checklist item it was dispatched for, or null when it names none.</summary>
    public string? Item { get; init; }

    /// <summary>The request's <c>title</c> frontmatter, or null.</summary>
    public string? Title { get; init; }

    /// <summary>For a request to a sub-orchestrator: when its workstream last reported, in epoch milliseconds. The clock never starts earlier than this.</summary>
    public double? StreamActiveMs { get; init; }

    /// <summary>Whether this is the open charter of a lane that is paused: the clock holds, so no check-in or silence alarm is raised for it.</summary>
    public bool PausedLane { get; init; }

    /// <summary>Whether this is the open charter of a lane whose workstream is working, in review or blocked: a standing brief of a lane that is being worked is not silent, so no check-in or silence alarm is raised for it.</summary>
    public bool ActiveLane { get; init; }

    /// <summary>Whether this is the open charter of a running lane, in any workstream state: the lane keeps working after its report is read, so that report does not end its clock.</summary>
    public bool LaneCharter { get; init; }

    /// <summary>Whether the assignee leads a stream whose child team holds an open request it sent: an idle lane lead waiting on its own team is not idle without a report (b341).</summary>
    public bool LaneDelegating { get; init; }

    /// <summary>For a request an orchestrator made inside a sub-team: the managing team. Its session cannot ask the user, so a silent peer is reported to that team's orchestrator.</summary>
    public string? ManagerTeam { get; init; }

    /// <summary>For a request sent <c>--after</c> another that exists: that request's slug. Null for any other request, including one that names an unknown id.</summary>
    public string? AfterSlug { get; init; }

    /// <summary>Whether the request was found from its file alone: no dispatch row and no brief entry record that it was ever sent.</summary>
    public bool Undelivered { get; init; }

    /// <summary>Whether the brief sits unread in the member's box: sent, and not yet in the member's hands.</summary>
    public bool BriefUnread { get; init; }

    /// <summary>The request file itself, when the host knows it; null leaves the path to be derived from the response's directory.</summary>
    public string? RequestPath { get; init; }

    /// <summary>Whether the request only records a chat message to a member: it has no deadline, so the clock raises nothing for it.</summary>
    public bool Note { get; init; }

    /// <summary>Whether the request was filed for a plain Orchestrator send (<see cref="HierarchyModule.PlainSendReason"/>): its clock waits until the member has worked on it.</summary>
    public bool PlainSend { get; init; }

    /// <summary>Whether the assignee is paused (b456, <c>roster pause-member</c>): the clock raises nothing for it and resumes nobody.</summary>
    public bool MemberPaused { get; init; }

    /// <summary>When the assignee was last resumed from a pause, in epoch milliseconds: the clock never starts earlier than this.</summary>
    public double? MemberResumedMs { get; init; }

    /// <summary>When the brief was first sent (its earliest dispatch row), in epoch milliseconds: the clock never starts earlier than this (b475). Null when no dispatch row records it.</summary>
    public double? SentMs { get; init; }

    /// <summary>The predecessor's eta, which bounds how long its silence can hold this request's clock.</summary>
    public string? AfterEta { get; init; }

    /// <summary>When the predecessor's response was filed, in epoch milliseconds; null while it is open.</summary>
    public double? AfterClosedMs { get; init; }
}

/// <summary>What a member's slot records say of it: holding a slot, or waiting for one.</summary>
/// <param name="Pool">The pool's name.</param>
/// <param name="Waiting">True for a waiter, false for a holder.</param>
/// <param name="SinceMs">When the holder took its slot, or the waiter began to wait, in epoch milliseconds.</param>
/// <param name="WaitedMs">For a holder: how long it waited for the slot first.</param>
/// <param name="Tokens">The tokens the holder holds, or the waiter asks for; a record written before tokens existed is one.</param>
/// <param name="Free">The tokens free in the pool; 0 when the pool has no capacity record.</param>
/// <param name="Capacity">The pool's capacity in tokens; 0 when the pool has no capacity record.</param>
/// <param name="Ahead">For a waiter: the live tickets ahead of it; 0 for a holder.</param>
public sealed record SlotState(string Pool, bool Waiting, double SinceMs, double WaitedMs, int Tokens = 1, int Free = 0, int Capacity = 0, int Ahead = 0);

/// <summary>An event the watch decided is due: what the daemon mails.</summary>
/// <param name="RequestId">The request the event is about, or null.</param>
/// <param name="Kind">The event kind, e.g. <c>LANDED</c>.</param>
/// <param name="Text">The mail text.</param>
/// <param name="Data">The event's data object.</param>
public sealed record WatchEvent(string? RequestId, string Kind, string Text, JsonObject Data)
{
    /// <summary>Mailbox event name when it differs from the kind's (a member's resume, a blocked member), else null.</summary>
    public string? Name { get; init; }

    /// <summary>The same text without a screen excerpt, for a mail that cannot hold the whole.</summary>
    public string? Compact { get; init; }

    /// <summary>The team whose orchestrator box the mail goes to when <c>Data</c>'s member names no team, else null.</summary>
    public string? Team { get; init; }

    /// <summary>Extra fields the watch log row carries.</summary>
    public JsonObject? Extra { get; init; }

    /// <summary>The box the mail goes to when it is not the team's own (the chief's <c>chief@T</c>), else null.</summary>
    public string? To { get; init; }
}

/// <summary>One gate row to append, to the hierarchy dir named by <see cref="Dir"/>.</summary>
/// <param name="Dir">The hierarchy dir.</param>
/// <param name="Row">The row, without its time.</param>
public sealed record GateAppend(string Dir, JsonObject Row);

/// <summary>One line of the timers report: a dispatch the evaluation looked at.</summary>
internal sealed record DispatchReport(
    string RequestId, string To, string? Member, string Eta, string? Created, string? State, string? CheckInAt, string? SilentAt, string? QueuedAfter = null);

/// <summary>What one evaluation decided. Nothing is written by the evaluation; the caller applies <see cref="Gates"/> and <see cref="Surfaced"/>.</summary>
internal sealed record DispatchOutcome(
    IReadOnlyList<WatchEvent> Events,
    IReadOnlyList<GateAppend> Gates,
    IReadOnlyList<string> Surfaced,
    IReadOnlyList<DispatchReport> Report)
{
    /// <summary>The resumes due for members whose brief is overdue, each tied to the event that reports it.</summary>
    internal IReadOnlyList<OverdueResume> Resumes { get; init; } = [];

    /// <summary>The <c>overdue-resume</c> peer rows to record for those resumes.</summary>
    internal IReadOnlyList<PeerAppend> PeerRows { get; init; } = [];

}

/// <summary>The member a dispatch is for, as far as the session's owned teams say.</summary>
/// <param name="Address">The member's own box (<c>name@team</c>), or null when it has none.</param>
/// <param name="Session">The member's session id.</param>
/// <param name="Idle">Whether the member is neither working, blocked nor failed.</param>
/// <param name="Known">Whether the member has an activity record at all. Without one nothing is known about what it is doing.</param>
/// <param name="Working">Whether the member's activity is <c>working</c> (not blocked, failed or idle).</param>
/// <param name="StateSinceMs">When the member entered its current activity, in epoch milliseconds, or null when the record does not say.</param>
/// <param name="Blocked">Whether the member is blocked or failed: it is never held back, whatever else the roster says of it (a peer waiting on an approval can be listed as busy).</param>
/// <param name="Busy">Whether the attributed roster lists the member as busy, whatever its activity.</param>
/// <param name="Stalled">Whether the member's verdict is <c>stalled-after-compact</c>: it compacted, then stopped without working again.</param>
/// <param name="CompactedAt">The record's <c>compacted_at</c>, the key of the stall episode, when <paramref name="Stalled"/>.</param>
/// <param name="Slot">The slot the member holds or waits for, from the slot records of a process of its own; null when none.</param>
/// <param name="WorkingSinceMs">When the current stretch of working began, in epoch milliseconds, or null when the record does not say.</param>
/// <param name="WorkingAtMs">The member's last working write, in epoch milliseconds, or null when the record does not say.</param>
/// <param name="ChainDown">Whether the member's session has an active <c>chain-down</c> row: it is winding down, so no resume is sent to it.</param>
/// <param name="BgWaiting">Whether the member is idle on its own background work launched within 2 x the eta threshold: no check-in or resume is due.</param>
/// <param name="BusySeenMs">When the roster last saw the member busy, in epoch milliseconds, or null when it never has.</param>
internal sealed record OverdueTarget(string? Address, string Session, bool Idle, bool Known = true, bool Working = false, double? StateSinceMs = null, bool Busy = false, bool Blocked = false, bool Stalled = false, string? CompactedAt = null, SlotState? Slot = null, double? WorkingSinceMs = null, double? WorkingAtMs = null, bool ChainDown = false, double? BusySeenMs = null, bool BgWaiting = false);

/// <summary>A resume entry for a member whose brief is overdue, and the words its event carries when the entry is or is not written.</summary>
/// <param name="EventIndex">Where the CHECK-IN or SILENT event it is reported in sits in <see cref="DispatchOutcome.Events"/>.</param>
/// <param name="Address">The member's own box.</param>
/// <param name="Session">The member's session id.</param>
/// <param name="Body">The entry's text.</param>
/// <param name="Data">The entry's data.</param>
/// <param name="Written">The suffix already on the event, saying the member was resumed.</param>
/// <param name="NotWritten">The suffix to put in its place, given the reason, when the entry could not be written.</param>
internal sealed record OverdueResume(int EventIndex, string Address, string Session, string Body, JsonObject Data, string Written, Func<string, string> NotWritten);

/// <summary>
/// The time the machine was awake, read from the gaps between the daemon's polls: a poll that comes more than <paramref name="gapMs"/>
/// after the last one means nothing ran in between.
/// </summary>
// ponytail: held in memory, so a daemon or failsafe timer started after the wake does not know of that sleep; persist the gaps if that shows up as alarms.
internal sealed class AwakeClock(double gapMs)
{
    private readonly List<(double From, double To)> _gaps = [];
    private double? _lastPoll;
    private int _counted;

    /// <summary>Records a poll at <paramref name="now"/>; true when it ended a gap.</summary>
    internal bool Poll(double now)
    {
        if (Interlocked.Exchange(ref _counted, 1) == 0)
        {
            Ah.Core.CoreModule.CensusAdd(this, "watch.awake_gaps", static c => c._gaps.Count);
        }

        var gap = _lastPoll is { } last && now - last > gapMs;
        if (gap)
        {
            _gaps.Add((_lastPoll!.Value, now));
        }

        _lastPoll = now;
        return gap;
    }

    /// <summary>The instant <paramref name="ms"/> on a timeline with the gaps cut out; only differences of two such instants mean anything.</summary>
    internal double Of(double ms) => ms - _gaps.Sum(g => Math.Max(0, Math.Min(ms, g.To) - g.From));
}

/// <summary>The dispatch clock: LANDED, CHECK-IN, SILENT and STALLED-AFTER-COMPACT for one Orchestrator session.</summary>
internal static class DispatchClock
{
    /// <summary>How long an assignee must have been idle, with its request still unanswered, before the Orchestrator is told.</summary>
    private const double IdleWithoutReportMs = 60_000;

    /// <summary>Mailbox event name for each event kind.</summary>
    internal static readonly IReadOnlyDictionary<string, string> EventName = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["LANDED"] = "report-landed",
        ["CHECK-IN"] = "check-in-due",
        ["SILENT"] = "peer-silent",
        ["IDLE-WITHOUT-REPORT"] = "idle-without-report",
        ["CONTEXT-HIGH"] = "context-high",
        ["CONTEXT-CRITICAL"] = "context-critical",
        ["BLOCKED"] = "member-blocked",
        ["RESUME"] = "resume",
        ["API-FAILED"] = "api-failed",
        ["CLASSIFIER-DENIED"] = "step-needs-user",
        ["OUTAGE"] = "outage",
        ["STALLED-AFTER-COMPACT"] = "stalled-after-compact",
        ["QUEUE-STALL"] = "queue-stall",
        ["MEMBER-WAITING"] = "member-waiting",
        ["UNLINKED-DISPATCH"] = "unlinked-dispatch",
        ["JOB-OVERDUE"] = "job-overdue",
        ["LANE-ESCALATED"] = "lane-escalated",
        ["LANE-RECOVERED"] = "lane-recovered",
        [UnreviewedDone.Kind] = UnreviewedDone.RowType,
    };

    /// <summary>
    /// The name a dispatch is listed under: the request's <c>to_name</c>, else the member part of the address it was sent to (a
    /// request made for a role and then sent to one member has no <c>to_name</c>), else <c>(unnamed)</c>.
    /// </summary>
    internal static string NameOf(string? toName, string? toAddr) => HierarchyModule.DispatchName(toName, toAddr);

    /// <summary><c>fmtAge</c>: whole minutes under an hour, else whole hours.</summary>
    internal static string FmtAge(double seconds) =>
        seconds < 3600
            ? $"{Math.Floor(seconds / 60).ToString("0", CultureInfo.InvariantCulture)}m"
            : $"{Math.Floor(seconds / 3600).ToString("0", CultureInfo.InvariantCulture)}h";

    /// <summary>
    /// The events due now across a session's watchable dispatches. <paramref name="thresholdSeconds"/> maps an eta to its
    /// check-in threshold in seconds. Gate rows and <c>surfaced</c> records are returned, not written, so a dry run is the same
    /// call with the writes dropped.
    /// </summary>
    internal static DispatchOutcome Evaluate(
        string sessionId,
        double now,
        double pollMs,
        IReadOnlyList<DispatchCandidate> candidates,
        IReadOnlyList<JsonObject> peerRecords,
        Func<string, IReadOnlyList<JsonObject>> gatesOf,
        Func<string, double> thresholdSeconds,
        Func<DispatchCandidate, OverdueTarget?>? targetOf = null,
        Func<double, double>? awake = null)
    {
        // Every age below is measured on the awake timeline, so time the machine slept does not count.
        double Awake(double ms) => awake?.Invoke(ms) ?? ms;
        var awakeNow = Awake(now);
        double AwakeSince(double ms) => awakeNow - Awake(ms);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(peerRecords);
        ArgumentNullException.ThrowIfNull(gatesOf);
        ArgumentNullException.ThrowIfNull(thresholdSeconds);
        var events = new List<WatchEvent>();
        var gates = new List<GateAppend>();
        var surfaced = new List<string>();
        var report = new List<DispatchReport>();
        var resumes = new List<OverdueResume>();
        var peerRows = new List<PeerAppend>();

        // Evaluate writes nothing, so a hierarchy dir's gate rows cannot change between two candidates of one call.
        var gatesByDir = new Dictionary<string, IReadOnlyList<JsonObject>>(StringComparer.Ordinal);
        IReadOnlyList<JsonObject> GatesOnce(string dir)
        {
            if (!gatesByDir.TryGetValue(dir, out var held))
            {
                held = gatesOf(dir);
                gatesByDir[dir] = held;
            }

            return held;
        }

        // Every record check below also requires this session's id, so the other sessions' records are never scanned.
        var mine = peerRecords.Where(r => r.Str("session_id") == sessionId).ToList();

        // The event just added for c is the CHECK-IN or SILENT of an overdue brief: an idle member also gets a resume entry in its own
        // box, at most MaxOverdueResumes times for the request, and the event says so.
        void ResumeOverdue(DispatchCandidate c, string? stalledAt = null)
        {
            if (targetOf?.Invoke(c) is not { } target)
            {
                return;
            }

            var member = NameOf(c.ToName, c.ToAddr);
            var last = events.Count - 1;
            bool IsResumeOfThisRequest(JsonObject r) => r.Str("type") == "overdue-resume" && r.Str("session_id") == sessionId && r.Str("request_id") == c.RequestId;
            var done = mine.Count(IsResumeOfThisRequest) + peerRows.Count(p => IsResumeOfThisRequest(p.Row));
            var queuedThisPoll = peerRows.Any(p => IsResumeOfThisRequest(p.Row));
            if ((target.Known && !target.Idle && !target.Stalled) || queuedThisPoll)
            {
                return;
            }

            if (target.ChainDown)
            {
                events[last] = events[last] with { Text = $"{events[last].Text}; not resumed: chain-down" };
                return;
            }

            if (done >= MaxOverdueResumes)
            {
                if (stalledAt is not null)
                {
                    events[last] = events[last] with { Text = $"{events[last].Text}; not resumed: the request already had its {MaxOverdueResumes.ToString(CultureInfo.InvariantCulture)} resumes" };
                }

                return;
            }

            if (!target.Known)
            {
                events[last] = events[last] with { Text = $"{events[last].Text}; could not resume {member} directly: activity unknown" };
                return;
            }

            if (target.Address is null)
            {
                events[last] = events[last] with { Text = $"{events[last].Text}; could not resume {member} directly: no box" };
                return;
            }

            var n = done + 1;
            var written = $"; resumed {member} by mail (pasted if unread) ({n.ToString(CultureInfo.InvariantCulture)}/{MaxOverdueResumes.ToString(CultureInfo.InvariantCulture)})";
            events[last] = events[last] with { Text = events[last].Text + written };
            peerRows.Add(new PeerAppend(new JsonObject
            {
                ["type"] = "overdue-resume",
                ["session_id"] = sessionId,
                ["request_id"] = c.RequestId,
                ["n"] = n,
                ["ts"] = Timestamps.ToIsoString(DateTimeOffset.FromUnixTimeMilliseconds((long)now)),
            }));
            var send = $"ah msg send {(c.Team is { Length: > 0 } team ? $"orchestrator@{team}" : "<your orchestrator>")} --kind report --ref {c.RequestId}";
            var body = stalledAt is null
                ? $"ah: your brief {c.RequestId} ({c.Slug}) is overdue (eta {c.Eta}, sent {ClockOf(c.Created)}). Continue it, or report its status with `{send}`. Do not redo side effects."
                : $"ah: you compacted your context at {ClockOf(stalledAt)} and stopped. Continue your brief {c.RequestId} ({c.Slug}), or report its status with `{send}`. Do not redo side effects.";
            resumes.Add(new OverdueResume(
                last,
                target.Address,
                target.Session,
                body,
                new JsonObject { ["request_id"] = c.RequestId, ["member"] = target.Address, ["n"] = n },
                written,
                reason => $"; could not resume {member} directly: {reason}"));
        }

        // A peer that is working or busy is not silent: the check-in is held back while it is, up to the busy ceiling whatever the eta,
        // so a wedged busy peer stays visible to the orchestrator. The silent alarm, which goes on to the user, has no cap while the peer
        // itself shows work: a fresh working write, or a held slot (the test slot kills its run at three etas). Otherwise it fires at the
        // busy ceiling after the clock base (the dispatch or the latest word), as the check-in does. A lead's ListAgents look renews nothing past the ceiling, so a wedged peer alarms
        // there however often it is looked at; a peer in one long tool call with no writes alarms too, which is the accepted cost. Nothing is recorded here, so it
        // is looked at again next poll. A peer whose record says neither when it began nor when it last wrote is aged from the dispatch itself.
        bool HeldBack(DispatchCandidate c, double thresholdMs, double dueMs, double baseMs, bool checkIn, out string busyNote, double? checkInMs = null)
        {
            busyNote = string.Empty;
            if (c.PausedLane || c.ActiveLane)
            {
                return true;
            }

            // A brief nobody was sent: a member who could get it earns one check-in saying to send it, and nobody has gone silent about it.
            if (c.Undelivered && (!checkIn || targetOf?.Invoke(c) is not { Session.Length: > 0 }))
            {
                return true;
            }

            // fewer-turns S3.1: a member waiting on its own background work is working, not late.
            if (targetOf?.Invoke(c) is { Blocked: false, BgWaiting: true })
            {
                return true;
            }

            var slot = targetOf?.Invoke(c) is { Blocked: false, Slot: { } s } ? s : null;

            // A member waiting for a slot is paused, not slow: the wait ends in the slot's own timeout, so no cap is needed here.
            if (slot is { Waiting: true })
            {
                return true;
            }

            if (checkIn && slot is null && targetOf?.Invoke(c) is { Known: true, Working: true, Blocked: false, Busy: false, WorkingAtMs: { } workedAt } fresh)
            {
                // The base of the clock is the dispatch or the latest word or check-in: the due instant less one threshold.
                if (WorkingHoldsCheckIn(awakeNow, Awake(workedAt), Awake(dueMs - thresholdMs)))
                {
                    return true;
                }

                // working_since is kept past its stint: for a waiting member the later state_since (the idle start) is the base
                double?[] stintFrom = [fresh.WorkingSinceMs, fresh.StateSinceMs];
                var workingFor = AwakeSince(stintFrom.Max() ?? workedAt);
                busyNote = $" (working for {Math.Round(workingFor / 60000).ToString(CultureInfo.InvariantCulture)} min)";
                return false;
            }

            if (targetOf?.Invoke(c) is not { } target || target.Blocked || !((target.Known && (target.Working || target.Busy)) || slot is not null))
            {
                return false;
            }

            if (!checkIn && (slot is not null || (target.WorkingAtMs is { } lastWrite && AwakeSince(lastWrite) <= WorkingFreshMs)))
            {
                return true;
            }

            // A busy look at or after the check-in answers it (the check-in says to send a busy peer nothing): held while the member
            // has worked less than the busy ceiling, so a wedged one is still called silent at the ceiling (b278).
            if (!checkIn && checkInMs is { } askedAt && target.Busy && target.BusySeenMs >= askedAt
                && (target.WorkingSinceMs ?? target.StateSinceMs ?? target.WorkingAtMs) is { } workingFrom
                && AwakeSince(workingFrom) < HierarchyModule.BusyCeilingSeconds * 1000)
            {
                return true;
            }

            double?[] ownEvidence = [target.WorkingAtMs, target.StateSinceMs];
            var since = (checkIn ? null : ownEvidence.Max()) ?? slot?.SinceMs ?? target.StateSinceMs ?? (c.Created is not null && Timestamps.TryParseMs(c.Created, out var createdMs) ? createdMs : now);

            var busyMs = AwakeSince(since);
            if (AwakeSince(baseMs) <= HierarchyModule.BusyCeilingSeconds * 1000)
            {
                return true;
            }

            busyNote = $" (busy for {Math.Round(busyMs / 60000).ToString(CultureInfo.InvariantCulture)} min)";
            if (slot is { WaitedMs: > 0 })
            {
                busyNote += $" (waited {Math.Round(slot.WaitedMs / 60000).ToString(CultureInfo.InvariantCulture)} min on the {slot.Pool} slot)";
            }

            return false;
        }

        // One stall event per (Orchestrator session, member session, compaction): the episode key.
        bool StallReported(OverdueTarget target, string compactedAt)
        {
            bool Same(JsonObject r) =>
                r.Str("type") == "stalled-after-compact" && r.Str("session_id") == sessionId && r.Str("member_session") == target.Session && r.Str("compacted_at") == compactedAt;
            return mine.Any(Same) || peerRows.Any(p => Same(p.Row));
        }

        // Only a report since the latest follow-up counts: the follow-up gave the member something new to report on.
        bool IdleReported(string requestId, double? since)
        {
            bool Same(JsonObject r) => r.Str("type") == "idle-without-report" && r.Str("session_id") == sessionId && r.Str("request_id") == requestId
                && (since is not { } after || Liveness.TimeOrZero(r, "ts") > after);
            return mine.Any(Same) || peerRows.Any(p => Same(p.Row));
        }

        bool ReportAcked(string requestId)
        {
            bool Same(JsonObject r) => r.Str("type") == "report-acked" && r.Str("request_id") == requestId;
            return mine.Any(Same) || peerRows.Any(p => Same(p.Row));
        }

        foreach (var c in candidates)
        {
            if (c.Note)
            {
                continue;
            }

            // A report the orchestrator has read leaves the member nothing owed, even when no response file closes the exchange.
            // A running lane still owes its charter: it keeps working after a report, so its stall and check-in clocks stay on.
            if (c.Open && !c.LaneCharter && ReportAcked(c.RequestId))
            {
                continue;
            }

            var who =$"{c.To} \"{NameOf(c.ToName, c.ToAddr)}\"";
            JsonObject Data(string step) => new() { ["request_id"] = c.RequestId, ["member"] = c.ToAddr is null ? JsonValue.Null : c.ToAddr, ["step"] = step };
            if (!c.Open)
            {
                if (!c.ResponseLanded)
                {
                    continue;
                }

                report.Add(new DispatchReport(c.RequestId, c.To, c.ToAddr, c.Eta, c.Created, "LANDED", null, null));
                surfaced.Add(c.RequestId);
                var follow = c.LandedFollowUp.Length > 0 ? $" {c.LandedFollowUp}" : string.Empty;
                var landedData = Data("landed");
                landedData["response_path"] = c.ResponsePath;
                events.Add(new WatchEvent(
                    c.RequestId,
                    "LANDED",
                    $"ah: {who} wrote its report for {c.RequestId} but never sent it: {c.ResponsePath}. Read it now.{follow}",
                    landedData)
                {
                    Team = c.Team,
                });
                continue;
            }

            // board-derive § C3 (r3): a plain send's clock is unarmed until the activity record shows a working stint of a minute after its
            // anchor (max(created, latest follow-up)); the arm is sticky and the clock runs from the stint's start. The watch never closes it.
            double? armedAt = null;
            if (c.PlainSend)
            {
                armedAt = Liveness.PlainSendArmedAt(mine, sessionId, c.RequestId);
                if (armedAt is null)
                {
                    var anchorMs = Math.Max(c.Created is not null && Timestamps.TryParseMs(c.Created, out var sent) ? sent : now, Liveness.LatestFollowup(mine, sessionId, c.RequestId) ?? 0);
                    var member = targetOf?.Invoke(c);
                    // The stint ends at the last working write while working, or at the idle transition once it left working.
                    var stintEnd = member is { Known: true, Working: true } ? member.WorkingAtMs : member is { Known: true, Idle: true } ? member.StateSinceMs : null;
                    if (member?.WorkingSinceMs is { } workingSince && stintEnd is { } end && end - Math.Max(anchorMs, workingSince) >= Liveness.PlainSendWorkMs)
                    {
                        var workStart = Math.Max(anchorMs, workingSince);
                        armedAt = workStart;
                        peerRows.Add(new PeerAppend(new JsonObject
                        {
                            ["type"] = Liveness.PlainSendArmedRow,
                            ["session_id"] = sessionId,
                            ["request_id"] = c.RequestId,
                            ["at"] = Timestamps.ToIsoString(DateTimeOffset.FromUnixTimeMilliseconds((long)workStart)),
                        }));
                    }
                    else
                    {
                        continue;
                    }
                }
            }

            if (!c.MemberPaused && targetOf?.Invoke(c) is { Known: true, Stalled: true, CompactedAt: { } compactedAt } stalled && !StallReported(stalled, compactedAt))
            {
                var stallRow = new JsonObject
                {
                    ["type"] = "stalled-after-compact",
                    ["session_id"] = sessionId,
                    ["member_session"] = stalled.Session,
                    ["compacted_at"] = compactedAt,
                    ["request_id"] = c.RequestId,
                    ["ts"] = Timestamps.ToIsoString(DateTimeOffset.FromUnixTimeMilliseconds((long)now)),
                };
                peerRows.Add(new PeerAppend(stallRow));
                events.Add(new WatchEvent(
                    c.RequestId,
                    "STALLED-AFTER-COMPACT",
                    $"ah: {who}, request {c.RequestId}: it compacted its context at {ClockOf(compactedAt)} and stopped without working again, so its brief is not being worked.",
                    Data("stalled"))
                {
                    Team = c.Team,
                });
                ResumeOverdue(c, compactedAt);
            }

            // The assignee went idle without sending its report: told once per request, and only when the request's own clock
            // (check-in, silent, stall) has nothing to say this pass, since those events already carry that news.
            var eventsBefore = events.Count;
            void IdleWithoutReport()
            {
                if (events.Count != eventsBefore || c.BriefUnread || c.LaneDelegating
                    || targetOf?.Invoke(c) is not { Known: true, Idle: true, Busy: false, Working: false, StateSinceMs: { } idleStart })
                {
                    return;
                }

                // A follow-up restarts the count, so a member that finishes it and goes idle is caught again, and never behind an old idle.
                var followupAt = Liveness.LatestFollowup(mine, sessionId, c.RequestId);
                var idleSince = Math.Max(Math.Max(idleStart, c.Created is not null && Timestamps.TryParseMs(c.Created, out var briefedAt) ? briefedAt : 0), Math.Max(Math.Max(followupAt ?? 0, armedAt ?? 0), c.MemberResumedMs ?? 0));
                if (AwakeSince(idleSince) < IdleWithoutReportMs || IdleReported(c.RequestId, followupAt) || ReportAcked(c.RequestId))
                {
                    return;
                }

                peerRows.Add(new PeerAppend(new JsonObject
                {
                    ["type"] = "idle-without-report",
                    ["session_id"] = sessionId,
                    ["request_id"] = c.RequestId,
                    ["ts"] = Timestamps.ToIsoString(DateTimeOffset.FromUnixTimeMilliseconds((long)now)),
                }));
                events.Add(new WatchEvent(
                    c.RequestId,
                    "IDLE-WITHOUT-REPORT",
                    $"ah: {who} has been idle for {FmtAge(AwakeSince(idleSince) / 1000)} and request {c.RequestId} has no report. Ask it for its report (SendMessage [hierarchy-msg {c.RequestId} response]) or tell the user if it is gone.",
                    Data("idle"))
                {
                    Team = c.Team,
                });
            }

            var t =thresholdSeconds(c.Eta) * 1000;
            var silentGap = t * HierarchyModule.CheckinCadence[1];
            var created = c.Created is not null && Timestamps.TryParseMs(c.Created, out var ms) ? ms : double.NaN;
            var rows = GatesOnce(c.HierarchyDir);
            var startMs = c.AfterSlug is null || double.IsNaN(created)
                ? 0
                : Math.Max(created, Math.Min(c.AfterClosedMs ?? double.PositiveInfinity, created + 2 * thresholdSeconds(c.AfterEta ?? c.Eta) * 1000));
            // b475: a request made early and sent later is waited on from the send, as the Stop hook's clock does
            var floorMs = Math.Max(Math.Max(Math.Max(Math.Max(Math.Max(double.IsNaN(created) ? 0 : created, c.SentMs ?? 0), c.StreamActiveMs ?? 0), startMs), armedAt ?? 0), c.MemberResumedMs ?? 0);
            var eta = EtaState.Compute(
                c.RequestId, c.Eta, thresholdSeconds(c.Eta), c.Created, c.ToAddr,
                floorMs,
                sessionId, mine, rows, false, null, now, pollMs);
            var baseMs = Math.Max(floorMs, Liveness.Heard(mine, sessionId, c.ToAddr));
            var ageSec = Math.Max(0, (double.IsNaN(created) ? 0 : AwakeSince(created)) / 1000);
            var state = eta.Checkins == 0 ? "CHECK-IN" : eta.Checkins == 1 ? "SILENT" : null;
            report.Add(new DispatchReport(
                c.RequestId,
                c.To,
                c.ToAddr,
                c.Eta,
                c.Created,
                state,
                Iso(eta.DueMs),
                eta.Checkins == 1 && eta.SilentDueMs is { } silentDueMs ? Iso(silentDueMs) : null,
                now < startMs ? c.AfterSlug : null));
            // b456: a paused member is told nothing and alarmed about nothing: no check-in, silence, resume or idle-without-report
            if (c.MemberPaused)
            {
                continue;
            }

            if (eta.Checkins == 0 && awakeNow >= Awake(eta.DueMs - t) + t)
            {
                if (HeldBack(c, t, eta.DueMs, baseMs, true, out var checkInNote))
                {
                    continue;
                }

                gates.Add(Nudge(c, sessionId));
                events.Add(new WatchEvent(
                    c.RequestId,
                    "CHECK-IN",
                    $"ah: {who}, request {c.RequestId}, {FmtAge(ageSec)} old (eta {c.Eta}), no report{checkInNote}.{UndeliveredClause(c)} Call ListAgents. If it shows the peer busy, send nothing: it is working. If it is idle, SendMessage it once, with notify_when_idle:true and the message \"Status of request {c.RequestId}? If finished, SendMessage me [hierarchy-msg {c.RequestId} response]; if not, one line on where you are.\" If it is gone, tell the user.",
                    WithDueSlot(Data("check-in"), eta.DueMs, t))
                {
                    Team = c.Team,
                });
                ResumeOverdue(c);
            }
            else if (eta.Checkins == 1 && awakeNow >= Awake(eta.SilentDueMs!.Value - silentGap) + silentGap)
            {
                // b504: any busy look after this check-in answers it (the check-in said to send a busy peer nothing), up to the busy
                // ceiling from the clock base, so a peer stuck busy is still called silent there (b278).
                if (HeldBack(c, t, eta.SilentDueMs!.Value, baseMs, false, out var busyNote, eta.DueMs)
                    || (AwakeSince(baseMs) <= HierarchyModule.BusyCeilingSeconds * 1000
                        && Liveness.GateTimes(rows, "liveness-checked", sessionId, c.RequestId).Any(at => at > eta.DueMs)))
                {
                    // The failsafe timer reads this as ahd alive and choosing to wait; it has no other way to tell that from a missed step.
                    if (!c.PausedLane && !Liveness.GateTimes(rows, SilentHeldRow, sessionId, c.RequestId).Any(at => now - at < SilentHeldEveryMs))
                    {
                        gates.Add(new GateAppend(c.HierarchyDir, new JsonObject { ["type"] = SilentHeldRow, ["session_id"] = sessionId, ["request_id"] = c.RequestId }));
                    }

                    continue;
                }

                gates.Add(Nudge(c, sessionId));
                events.Add(new WatchEvent(
                    c.RequestId,
                    "SILENT",
                    $"ah: {who}, request {c.RequestId}: no report and no reply {FmtAge(t / 2000)} after the check-in{busyNote}. {(c.ManagerTeam is null ? $"Tell the user now (name, request, age {FmtAge(ageSec)}). Do not take over its work or re-dispatch it elsewhere without the user's go-ahead; on a go-ahead, follow agent-team 'When a role can't take the work' and leave its pane running." : $"Report `blocked` with stream-report and notify the orchestrator of {c.ManagerTeam} now (name, request, age {FmtAge(ageSec)}). Do not take over its work or re-dispatch it elsewhere without that orchestrator's go-ahead, and leave its pane running.")} You will not be woken for it again unless the peer speaks.",
                    WithDueSlot(Data("silent"), eta.SilentDueMs!.Value, t))
                {
                    Team = c.Team,
                });
                ResumeOverdue(c);
            }

            IdleWithoutReport();
        }

        return new DispatchOutcome(events, gates, surfaced, report) { Resumes = resumes, PeerRows = peerRows };
    }

    /// <summary>
    /// A copy of an event's data that names which interval of the request's clock the event is for, so the mailer can tell a repeat of
    /// one event from the same event due again at the next interval.
    /// </summary>
    private static JsonObject WithDueSlot(JsonObject data, double dueMs, double intervalMs)
    {
        var slotted = new JsonObject();
        foreach (var key in data.Keys)
        {
            slotted[key] = data[key];
        }

        slotted["due_slot"] = Math.Floor(dueMs / intervalMs);
        return slotted;
    }

    private static string UndeliveredClause(DispatchCandidate c) =>
        c.Undelivered
            ? $" No delivery recorded for {c.RequestId}; send it with ah msg send {c.ToName ?? c.To}{(c.Team is null ? string.Empty : "@" + c.Team)} --ref {c.RequestPath ?? Path.Combine(Path.GetDirectoryName(c.ResponsePath) ?? string.Empty, $"{c.RequestId}--{c.To}--{c.Slug}--request.md")}."
            : string.Empty;

    /// <summary>How many resume entries one overdue brief gets in all.</summary>
    internal const int MaxOverdueResumes = 2;

    /// <summary>The gate row that says ahd held a due silent step back.</summary>
    internal const string SilentHeldRow = "silent-held";

    /// <summary>How often a held silent step writes its row: well under the failsafe's two minute margin.</summary>
    private const double SilentHeldEveryMs = 60_000;

    /// <summary>How long after its last working write a member still counts as working when the check-in is decided.</summary>
    private const double WorkingFreshMs = 120_000;

    /// <summary>Whether a member's last working write at <paramref name="workedAtMs"/> holds its check-in back: the write is fresh and the clock is under the busy ceiling past <paramref name="baseMs"/>.</summary>
    internal static bool WorkingHoldsCheckIn(double now, double workedAtMs, double baseMs) =>
        now - workedAtMs <= WorkingFreshMs && now < baseMs + (HierarchyModule.BusyCeilingSeconds * 1000);

    /// <summary>The local wall-clock time of an ISO time, as the other events print it.</summary>
    private static string ClockOf(string? iso) =>
        iso is not null && Timestamps.TryParseMs(iso, out var ms)
            ? DateTimeOffset.FromUnixTimeMilliseconds((long)ms).ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)
            : "an unknown time";

    private static GateAppend Nudge(DispatchCandidate c, string sessionId) =>
        new(c.HierarchyDir, new JsonObject { ["type"] = "liveness-nudge", ["session_id"] = sessionId, ["request_id"] = c.RequestId });

    private static string? Iso(double ms) => double.IsNaN(ms) || double.IsInfinity(ms) ? null : Timestamps.ToIsoString(DateTimeOffset.FromUnixTimeMilliseconds((long)ms));
}
