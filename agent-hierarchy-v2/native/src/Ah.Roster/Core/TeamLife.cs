using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Time;

namespace Ah.Roster;

/// <summary>What a team record's Orchestrator is now, as far as the process table can prove.</summary>
public enum TeamLife
{
    /// <summary>The Orchestrator is shown to be running: the live registration's process, or the recorded pid with its start, is the session.</summary>
    OwnedLive,

    /// <summary>The Orchestrator is proven gone: its process is not running, or its pid now belongs to another process.</summary>
    Orphaned,

    /// <summary>
    /// Nothing proves either: no record, no process table, or evidence too weak to decide (a bare pid with no recorded start, a start
    /// that cannot be compared, an observer that cannot see other processes). Whatever acts on orphanhood does nothing, a display
    /// shows <c>unknown</c>, and ownership is not proven.
    /// </summary>
    Indeterminate,
}

internal static class TeamLives
{
    internal static TeamLife Of(JsonValue team, Func<string, (long Pid, string Start)?>? liveRegistration, IProcessTable? table, DateTimeOffset now, int observerPid, IEnumerable<(long Pid, string? Start)>? references = null) =>
        Judge(team, liveRegistration, table, now, observerPid, references).Life;

    /// <summary>
    /// Whether a team record's <c>orchestrator</c> carries any claim on the team. Unclaimed is an absent or null orchestrator, or an
    /// object with no non-empty <c>session_id</c> and a <c>pid</c> that is absent, null or an integer of 0 or less (it names no process);
    /// anything else is claimed, a malformed session or a non-integer pid included, so a record that cannot be read is judged by its
    /// verdict (indeterminate) and not taken unchecked.
    /// </summary>
    internal static bool IsClaimed(JsonValue? orchestrator)
    {
        if (orchestrator is not { } o || o.IsNull)
        {
            return false;
        }

        if (!o.IsMap)
        {
            return true;
        }

        var sessionUnclaimed = o.Member("session_id") is not { } s || s.IsNull || (s.IsText && s.AsString().Length == 0);
        var pidUnclaimed = o.Member("pid") is not { } p || p.IsNull || (p.IsNumber && p.AsNumber() == Math.Floor(p.AsNumber()) && p.AsNumber() <= 0);
        return !(sessionUnclaimed && pidUnclaimed);
    }

    /// <summary>
    /// Whether the Orchestrator's <c>released</c> marker names the incarnation that holds the team now: the same pid and start as the
    /// session's live registration, or as the record's own pid and start. A marker without both, or one that names any other process, is
    /// stale and applies to nothing.
    /// </summary>
    internal static bool IsReleased(JsonValue orchestrator, Func<string, (long Pid, string Start)?>? liveRegistration)
    {
        if (orchestrator.Member("released") is not { IsMap: true } marker
            || JsConvert.AsInteger(marker.Member("pid")) is not { } markedPid
            || marker.Member("pid_start") is not { IsText: true } markedStart || markedStart.AsString().Length == 0)
        {
            return false;
        }

        var start = markedStart.AsString();
        if (orchestrator.Member("session_id") is { IsText: true } session && session.AsString().Length > 0
            && liveRegistration?.Invoke(session.AsString()) is { } registration
            && registration.Pid == markedPid && registration.Start == start)
        {
            return true;
        }

        return JsConvert.AsInteger(orchestrator.Member("pid")) is { } recorded && recorded == markedPid
            && orchestrator.Member("pid_start") is { IsText: true } recordedStart && recordedStart.AsString() == start;
    }

    /// <summary>The team's life and the sentence that says why, for a refusal to print.</summary>
    internal static (TeamLife Life, string Why) Judge(JsonValue team, Func<string, (long Pid, string Start)?>? liveRegistration, IProcessTable? table, DateTimeOffset now, int observerPid, IEnumerable<(long Pid, string? Start)>? references = null)
    {
        if (team.IsNull || table is null)
        {
            return (TeamLife.Indeterminate, "there is no team record or no process table to check it against");
        }

        var orch = team.Member("orchestrator") is { IsTruthy: true } o ? o : (JsonValue)new JsonObject();
        if (IsReleased(orch, liveRegistration))
        {
            return (TeamLife.Orphaned, "released");
        }

        long pid;
        string? start;
        var registered = false;
        if (orch.Member("session_id") is { IsText: true } session && session.AsString().Length > 0
            && liveRegistration?.Invoke(session.AsString()) is { } registration)
        {
            (pid, start, registered) = (registration.Pid, registration.Start, true);
        }
        else if (JsConvert.AsInteger(orch.Member("pid")) is { } recorded && recorded > 1 && recorded <= int.MaxValue)
        {
            pid = recorded;
            start = orch.Member("pid_start") is { IsText: true } text && text.AsString().Length > 0 ? text.AsString() : null;
        }
        else
        {
            return IsClaimed(team.Member("orchestrator"))
                ? (TeamLife.Indeterminate, "the record names no Orchestrator process")
                : (TeamLife.Orphaned, "the record names no Orchestrator: nobody has claimed the team");
        }

        // The reference is read before the subject, and a subject the table does not show counts as unobserved unless the table showed
        // another process in the same pass.
        var canSee = LivenessRules.ObserverSeesAReference(table, observerPid, references ?? [], pid);
        var current = table.StartTime(pid);
        var evidence = new LivenessEvidence
        {
            Now = now,
            MaxObservationAge = TimeSpan.FromSeconds(LivenessRules.ObservationAgeSeconds),
            HasRegistration = registered,
            RegistrationPid = registered ? pid : null,
            RegistrationStart = registered ? start : null,
            PeerRow = registered ? PeerRowKind.None : PeerRowKind.Up,
            PeerPid = registered ? null : pid,
            PeerStart = registered ? null : start,
            Observation = current is not null ? ProcessObservation.Present : canSee ? ProcessObservation.Absent : ProcessObservation.None,
            ObservedStartUtc = current,
            Cmdline = HarnessCmdline.Unreadable,
            ObservedAt = now,
            ObserverCanSeeReference = canSee,
        };
        var (verdict, why) = LivenessRules.Judge(evidence);
        return (verdict switch
        {
            LivenessVerdict.Live => TeamLife.OwnedLive,
            LivenessVerdict.ProvenDead => TeamLife.Orphaned,
            _ => TeamLife.Indeterminate,
        }, why);
    }
}
