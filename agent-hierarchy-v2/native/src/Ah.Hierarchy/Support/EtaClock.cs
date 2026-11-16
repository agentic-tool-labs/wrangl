namespace Ah.Hierarchy;

/// <summary>The instant ahd raises its next step for a dispatch: <c>check-in</c> or <c>silent</c>.</summary>
/// <param name="Step">The step's name.</param>
/// <param name="AtMs">When it is raised, in epoch milliseconds.</param>
public sealed record NextNudge(string Step, double AtMs);

/// <summary>When a dispatch is due and what state it is in; <see cref="EtaClock.Compute"/> is the one place that says.</summary>
/// <param name="RequestId">The dispatch's request id.</param>
/// <param name="Eta">The effective eta word.</param>
/// <param name="SentAt">The request's <c>created</c> instant.</param>
/// <param name="DueMs">When the check-in is due, in epoch milliseconds, busy hold not included.</param>
/// <param name="SilentDueMs">When the silent step is due, or null before the check-in has been raised.</param>
/// <param name="State"><c>answered</c>, <c>overdue</c>, <c>due-soon</c> or <c>on-time</c>.</param>
/// <param name="OverdueBySeconds">Seconds past <paramref name="DueMs"/> while overdue, else null.</param>
/// <param name="Next">The next ahd step, busy hold included; null when none remains or the dispatch is answered.</param>
/// <param name="Checkins">How many check-in episodes ahd has already raised for it.</param>
/// <param name="NextChangeMs">The first instant after the computation's <c>now</c> at which the state or the next step changes by time alone; null when nothing is pending.</param>
public sealed record EtaStateResult(string RequestId, string Eta, string? SentAt, double DueMs, double? SilentDueMs, string State, double? OverdueBySeconds, NextNudge? Next, int Checkins, double? NextChangeMs = null);

/// <summary>What the files say about one dispatch, as plain values.</summary>
/// <param name="RequestId">The dispatch's request id.</param>
/// <param name="Eta">The effective eta word.</param>
/// <param name="ThresholdSeconds">The check-in threshold of that eta.</param>
/// <param name="SentAt">The request's <c>created</c> text.</param>
/// <param name="StartMs">When the dispatch was sent, in epoch milliseconds; 0 when unknown.</param>
/// <param name="HeardMs">The latest time the addressee was heard from for this session, or 0.</param>
/// <param name="NudgeMs">Every liveness-nudge time for the request.</param>
/// <param name="CheckedMs">Every liveness-checked (busy look) time for the request.</param>
/// <param name="Answered">Whether a response landed, or the request was closed or withdrawn.</param>
/// <param name="HeldUntilMs">The end of the addressee's busy hold, or null.</param>
public sealed record EtaFacts(
    string RequestId,
    string Eta,
    double ThresholdSeconds,
    string? SentAt,
    double StartMs,
    double HeardMs,
    IReadOnlyList<double> NudgeMs,
    IReadOnlyList<double> CheckedMs,
    bool Answered,
    double? HeldUntilMs);

/// <summary>
/// The one pure answer to "when is this dispatch due, and what state is it in". The dispatch clock raises its check-in and silent steps
/// from it, the overdue timer arms its deadlines from it, the status views read their state from it, and the read model projects it at
/// serve time, so none of them can disagree. It reads only what it is handed.
/// </summary>
internal static class EtaClock
{
    /// <summary>
    /// Distinct episodes among ascending timestamps (ms): rows closer than <paramref name="gapMs"/> to the previous one belong to
    /// the same episode. Returns each episode's first timestamp.
    /// </summary>
    internal static List<double> Episodes(IReadOnlyList<double> sortedTimes, double gapMs)
    {
        ArgumentNullException.ThrowIfNull(sortedTimes);
        var output = new List<double>();
        var previous = double.NegativeInfinity;
        foreach (var t in sortedTimes)
        {
            if (t - previous > gapMs)
            {
                output.Add(t);
            }

            previous = t;
        }

        return output;
    }

    /// <summary>
    /// When the second nudge, the silence warning, is due after a first nudge at <paramref name="checkinMs"/>. A busy look
    /// (<paramref name="lookMs"/>) after that nudge is evidence the peer is working: it restarts the wait as a full threshold from
    /// the look, but never past <see cref="HierarchyModule.BusyCeilingSeconds"/> from <paramref name="baseMs"/> and never before the wait
    /// without the look. A look from before the nudge changes nothing. A <paramref name="baseMs"/> of 0 (unknown) is taken as one
    /// threshold before the first nudge, since that nudge is only issued a threshold after the send; <paramref name="checkinMs"/>
    /// is the first nudge, so the estimate is fixed and later looks never move it.
    /// </summary>
    internal static double SilentDueAt(double checkinMs, double lookMs, double baseMs, double thresholdMs, double gapFactor)
    {
        var idleDue = checkinMs + (gapFactor * thresholdMs);
        if (lookMs <= checkinMs)
        {
            return idleDue;
        }

        var ceiling = (baseMs > 0 ? baseMs : checkinMs - thresholdMs) + (HierarchyModule.BusyCeilingSeconds * 1000);
        return Math.Min(lookMs + thresholdMs, Math.Max(ceiling, idleDue));
    }

    /// <summary>The clock base: the later of the send and the latest word from the addressee. Whatever the peer said restarts the clock.</summary>
    internal static double BaseOf(double startMs, double heardMs) => Math.Max(startMs, heardMs);

    /// <summary>The state of one dispatch at <paramref name="nowMs"/>.</summary>
    internal static EtaStateResult Compute(EtaFacts facts, double nowMs, double pollMs)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var t = facts.ThresholdSeconds * 1000;
        var baseMs = BaseOf(facts.StartMs, facts.HeardMs);
        var nudges = facts.NudgeMs.Where(n => n > baseMs).Order().ToList();
        var checkedAt = facts.CheckedMs.Where(c => c > baseMs).DefaultIfEmpty(0).Max();
        var checkins = Episodes(nudges, 2 * pollMs);
        var dueMs = checkins.Count > 0 ? checkins[0] : Math.Max(baseMs, checkedAt) + t;
        double? silentDue = checkins.Count > 0 ? SilentDueAt(checkins[0], checkedAt, baseMs, t, HierarchyModule.CheckinCadence[1]) : null;
        var next = facts.Answered || checkins.Count > 1
            ? null
            : checkins.Count == 0
                ? new NextNudge("check-in", Held(dueMs, facts.HeldUntilMs))
                : new NextNudge("silent", silentDue!.Value);
        var soonBy = Math.Max(60_000, 0.2 * t);
        var state = facts.Answered ? "answered" : nowMs >= dueMs ? "overdue" : nowMs >= dueMs - soonBy ? "due-soon" : "on-time";
        double? nextChange = facts.Answered
            ? null
            : new[] { dueMs - soonBy, dueMs, next?.AtMs ?? double.NaN }.Where(c => !double.IsNaN(c) && c > nowMs).Select(static c => (double?)c).Min();
        return new EtaStateResult(
            facts.RequestId, facts.Eta, facts.SentAt, dueMs, silentDue, state, state == "overdue" ? Math.Max(0, (nowMs - dueMs) / 1000) : null, next, checkins.Count, nextChange);
    }

    private static double Held(double at, double? heldUntilMs) => heldUntilMs is { } h ? Math.Max(at, h) : at;
}
