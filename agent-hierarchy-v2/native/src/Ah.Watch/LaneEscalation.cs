using System.Globalization;
using Ah.Core.Json;

namespace Ah.Watch;

/// <summary>An open dispatch of a lane, as the parent is told of it.</summary>
/// <param name="Id">The request id.</param>
/// <param name="Member">The member it was sent to.</param>
/// <param name="State">The eta state (<c>on-track</c>, <c>due</c>, <c>overdue</c> and the like).</param>
public sealed record LaneDispatch(string Id, string Member, string State);

/// <summary>What the files show of one child lane (a team whose file names the evaluated session's team as its parent).</summary>
/// <param name="Lane">The child team's name.</param>
/// <param name="SubOrchestrator">The sub-orchestrator's name.</param>
/// <param name="Dead">Whether the sub-orchestrator's Claude session is not running (its pid fails the identity test).</param>
/// <param name="BlockedOnJob">Whether the sub-orchestrator owns an overdue job.</param>
/// <param name="OldestUnackedMs">When the oldest of the sub-orchestrator's own unacknowledged events was written, or null when none is pending.</param>
/// <param name="StreamState">The lane's <c>stream-report</c> state, or null.</param>
/// <param name="StreamUpdatedMs">When the lane's status record was last written, or null.</param>
/// <param name="Open">The lane's open dispatches.</param>
/// <param name="ParentTeam">The team the lane's team file names as its parent; the events about it are that team's.</param>
/// <param name="Stream">The lane's workstream name in the parent team, or null when it is the team name.</param>
/// <param name="DeadAnchor">What identifies the dead session of E-a (its pid and start, else its name and session), or null.</param>
/// <param name="JobAnchor">The id of the overdue job of E-b, or null.</param>
/// <param name="UnackedAnchor">The id of the oldest unacknowledged event of E-c, or null.</param>
/// <param name="DeadSinceMs">When the sub-orchestrator's session last did anything, or null when that is not recorded.</param>
/// <param name="JobDueMs">When the overdue job of E-b fell due: its send or its last extension, whichever is later.</param>
/// <param name="PausedBy">Who paused the lane, when it is paused.</param>
/// <param name="Note">The note the lane's last report carried.</param>
/// <param name="WaitingOnSlot">Whether the sub-orchestrator owns a job queued for the test slot, which is waiting and not blocked.</param>
/// <param name="ChildActiveMs">When the lane's child team last worked for the sub-orchestrator (an open request on time, a commit), or null; it counts as a write of the status record for E-d only.</param>
public sealed record LaneFact(
    string Lane,
    string SubOrchestrator,
    bool Dead,
    bool BlockedOnJob,
    double? OldestUnackedMs,
    string? StreamState,
    double? StreamUpdatedMs,
    IReadOnlyList<LaneDispatch> Open,
    string? ParentTeam = null,
    string? Stream = null,
    string? DeadAnchor = null,
    string? JobAnchor = null,
    string? UnackedAnchor = null,
    double? DeadSinceMs = null,
    double? JobDueMs = null,
    string? PausedBy = null,
    string? Note = null,
    bool WaitingOnSlot = false,
    double? ChildActiveMs = null);

/// <summary>
/// Escalation up the chain: a lane whose sub-orchestrator is dead for the grace, blocked on a job, not acknowledging its events or silent
/// in its status record is reported once to the parent as <c>lane-escalated</c>, with the lane's open dispatches, and once as
/// <c>lane-recovered</c> when none of those holds any more.
/// </summary>
internal static class LaneEscalation
{
    internal const string EscalatedName = "lane-escalated";
    internal const string RecoveredName = "lane-recovered";

    /// <summary>How long a sub-orchestrator's own event may stay unacknowledged.</summary>
    internal const double UnackedMs = 5 * 60 * 1000;

    /// <summary>How long a lane in an active state may go without writing its status record, before the margin.</summary>
    internal const double StaleMs = 30 * 60 * 1000;

    internal const string PausedState = "paused";

    internal const string DoneState = "done";

    private static readonly string[] ActiveStates = ["working", "blocked", "review"];

    /// <summary>An open escalation: its reason, the <c>due_slot</c> its event carried, and the parent team and sub-orchestrator its events are copied to and named for.</summary>
    internal sealed record Episode(string Reason, string Slot, string? ParentTeam = null, string? SubOrchestrator = null);

    /// <summary>What was already reported, per lane: the open escalation, and since when each lane's sub-orchestrator has been dead.</summary>
    internal sealed class State
    {
        public Dictionary<string, Episode> Escalated { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, double> DeadSince { get; } = new(StringComparer.Ordinal);

        /// <summary>The lanes this process has evaluated before: for those, the time it first saw the sub-orchestrator dead is the whole truth.</summary>
        public HashSet<string> Known { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>The reason a lane is escalated at <paramref name="now"/> (E-a to E-d, first that holds), or null. A paused lane has none.</summary>
    internal static string? ReasonOf(LaneFact lane, double now, double deadSince, double deadGraceMs)
    {
        if (lane.StreamState == PausedState)
        {
            return null;
        }

        // b377: a lead that stopped after its lane reported done with nothing open is finished, not dead
        if (lane.Dead && now - deadSince >= deadGraceMs && !(lane.StreamState == DoneState && lane.Open.Count == 0))
        {
            return "E-a";
        }

        if (lane.BlockedOnJob)
        {
            return "E-b";
        }

        // a lane that wrote its status record or mailed its parent after the event arrived is working, so the event's age counts from then
        if (lane.OldestUnackedMs is { } unacked && now - Math.Max(unacked, lane.StreamUpdatedMs ?? 0) >= UnackedMs)
        {
            return "E-c";
        }

        // b376: the child team working for the lead is the lead speaking
        var spoke = lane.ChildActiveMs is { } active && (lane.StreamUpdatedMs is null || active > lane.StreamUpdatedMs) ? active : lane.StreamUpdatedMs;
        if (SilentSinceMs(lane.StreamState, spoke) is { } silent && now > silent)
        {
            return "E-d";
        }

        return null;
    }

    /// <summary>
    /// The instant after which a lane's status record counts as stale (E-d): the record's last write plus the staleness limit and the margin. Null for a lane
    /// that is not in an active state (done, paused) or has never written one.
    /// </summary>
    internal static double? SilentSinceMs(string? streamState, double? updatedMs) =>
        streamState is { } state && ActiveStates.Contains(state, StringComparer.Ordinal) && updatedMs is { } updated ? updated + StaleMs + JobEvents.MarginMs : null;

    /// <summary>The <c>request_id</c> every escalation event of the lane in <paramref name="stream"/> of <paramref name="parentTeam"/> carries.</summary>
    internal static string RequestIdOf(string? parentTeam, string stream) => "lane:" + parentTeam + "/" + stream;

    /// <summary>
    /// The events due for <paramref name="lanes"/>. An escalation or recovery is recorded in <paramref name="state"/> only when its entry in <paramref name="delivered"/>
    /// is run, after the event was sent; one that was not sent is due again on the next call. An escalation that <paramref name="alreadyMailed"/>
    /// finds already in the box (told before an ahd restart) is recorded as open without being sent again.
    /// </summary>
    internal static IReadOnlyList<WatchEvent> Evaluate(string session, double now, double deadGraceMs, IReadOnlyList<LaneFact> lanes, State state, out IReadOnlyDictionary<WatchEvent, Action> delivered, Func<WatchEvent, bool>? alreadyMailed = null)
    {
        var commits = new Dictionary<WatchEvent, Action>();
        delivered = commits;
        var events = new List<WatchEvent>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var lane in lanes)
        {
            var key = session + "|" + lane.Lane;
            _ = seen.Add(key);
            if (lane.Dead)
            {
                _ = state.DeadSince.TryAdd(key, now);
            }
            else
            {
                _ = state.DeadSince.Remove(key);
            }

            var deadSince = state.DeadSince.GetValueOrDefault(key, now);
            var firstSight = state.Known.Add(key);
            if (firstSight && lane.Dead && lane.DeadSinceMs is { } seenAlive)
            {
                deadSince = Math.Min(deadSince, seenAlive);
            }

            if (lane.StreamUpdatedMs is { } epoch)
            {
                deadSince = Math.Max(deadSince, epoch);
            }

            var reason = ReasonOf(lane, now, deadSince, deadGraceMs);
            if (reason is not null && !state.Escalated.ContainsKey(key))
            {
                var slot = SlotOf(lane, reason);
                var escalated = Escalated(lane, reason, slot);
                if (firstSight && alreadyMailed?.Invoke(escalated) == true)
                {
                    state.Escalated[key] = new Episode(reason, slot, lane.ParentTeam, lane.SubOrchestrator);
                }
                else
                {
                    events.Add(escalated);
                    commits[escalated] = () => state.Escalated[key] = new Episode(reason, slot, lane.ParentTeam, lane.SubOrchestrator);
                }
            }
            else if (reason is null && state.Escalated.TryGetValue(key, out var open))
            {
                var recovered = Recovered(lane, open.Slot);
                events.Add(recovered);
                commits[recovered] = () => _ = state.Escalated.Remove(key);
            }
        }

        foreach (var gone in state.Escalated.Keys.Concat(state.DeadSince.Keys).Concat(state.Known).Distinct().Where(k => k.StartsWith(session + "|", StringComparison.Ordinal) && !seen.Contains(k)).ToList())
        {
            _ = state.Escalated.Remove(gone);
            _ = state.DeadSince.Remove(gone);
            _ = state.Known.Remove(gone);
        }

        return events;
    }

    /// <summary>The eta state of <paramref name="dispatch"/> at <paramref name="nowMs"/>.</summary>
    internal static string StateOf(OverdueDispatch dispatch, double nowMs, double pollMs) =>
        EtaState.Compute(dispatch.RequestId, dispatch.Eta, dispatch.ThresholdSeconds, dispatch.Created, dispatch.ToAddr, dispatch.FloorMs, dispatch.Session, dispatch.PeerRecords, dispatch.Gates, false, dispatch.HeldUntilMs, nowMs, pollMs).State;

    /// <summary>The <c>due_slot</c> of one episode of <paramref name="reason"/>: the reason and what defines that episode, which a restart of ahd does not change.</summary>
    private static string SlotOf(LaneFact lane, string reason)
    {
        var epoch = (lane.StreamUpdatedMs ?? 0).ToString("F0", CultureInfo.InvariantCulture);
        return reason + ":" + reason switch
        {
            "E-a" => $"{lane.DeadAnchor}:{epoch}",
            "E-b" => $"{lane.JobAnchor}:{(lane.JobDueMs ?? 0).ToString("F0", CultureInfo.InvariantCulture)}:{epoch}",
            "E-c" => $"{lane.UnackedAnchor}:{epoch}",
            _ => epoch,
        };
    }

    private static string RequestOf(LaneFact lane) => RequestIdOf(lane.ParentTeam, lane.Stream ?? lane.Lane);

    private static WatchEvent Escalated(LaneFact lane, string reason, string slot)
    {
        var open = new JsonArray();
        foreach (var d in lane.Open)
        {
            open.Add(new JsonObject { ["id"] = d.Id, ["member"] = d.Member, ["state"] = d.State });
        }

        var data = new JsonObject
        {
            ["lane"] = lane.Lane,
            ["sub_orchestrator"] = lane.SubOrchestrator,
            ["reason"] = reason,
            ["open_dispatches"] = JsonValue.FromArray(open),
            ["waiting_on_slot"] = lane.WaitingOnSlot,
            ["request_id"] = RequestOf(lane),
            ["due_slot"] = slot,
        };
        var listed = lane.Open.Count == 0 ? "no open dispatches" : "open dispatches: " + string.Join(", ", lane.Open.Select(static d => $"{d.Id} {d.Member} {d.State}"));
        var text = $"lane {lane.Lane} escalated ({reason}: {Describe(reason)}); sub-orchestrator {lane.SubOrchestrator}"
            + (lane.WaitingOnSlot ? " (waiting-on-slot: a job is queued for the test slot)" : string.Empty) + $"; {listed}"
            + (reason == "E-a" ? ". Ask the user whether to respawn the sub-orchestrator, finish the lane yourself, or wind it down; a sub-orchestrator reports this to its parent instead" : string.Empty);
        return new WatchEvent(lane.Lane, "LANE-ESCALATED", text, data) { Name = EscalatedName, Team = lane.ParentTeam };
    }

    private static WatchEvent Recovered(LaneFact lane, string escalatedSlot)
    {
        var data = new JsonObject { ["lane"] = lane.Lane, ["sub_orchestrator"] = lane.SubOrchestrator, ["request_id"] = RequestOf(lane), ["due_slot"] = "recovered:" + escalatedSlot };
        var text = lane.StreamState == PausedState
            ? $"lane {lane.Lane} paused by {lane.PausedBy ?? "?"}: {lane.Note ?? "no note"}"
            : $"lane {lane.Lane} recovered; sub-orchestrator {lane.SubOrchestrator} is live and acknowledging again";
        return new WatchEvent(lane.Lane, "LANE-RECOVERED", text, data) { Name = RecoveredName, Team = lane.ParentTeam };
    }

    private static string Describe(string reason) => reason switch
    {
        "E-a" => "the sub-orchestrator is dead",
        "E-b" => "the sub-orchestrator is blocked on a job",
        "E-c" => "its own event is unacknowledged for " + (UnackedMs / 60000).ToString("0", CultureInfo.InvariantCulture) + " min",
        _ => "the lane's status record is stale",
    };
}
