using Ah.Core.Json;

namespace Ah.Watch;

/// <summary>An implement-class member's DONE report on a request an owned team's Orchestrator sent (b487).</summary>
/// <param name="RequestId">The request the report answers.</param>
/// <param name="Title">The request's <c>title</c>, or null.</param>
/// <param name="Member">The reporting member's address, or its name when it has none.</param>
/// <param name="Team">The request's team: its Orchestrator box gets the alarm.</param>
/// <param name="ReportedMs">When the report was filed, in epoch milliseconds.</param>
/// <param name="Reviewed">Whether a review exchange exists for it now: a link to a reviewer-class member on the request or a child, or a reviewer-class request whose parent is the request.</param>
public sealed record DoneReport(string RequestId, string? Title, string Member, string? Team, double ReportedMs, bool Reviewed);

public static partial class WatchModule
{
    /// <summary>How old a DONE report may be and still raise <c>unreviewed-done</c>; the host reads no older one.</summary>
    public const double UnreviewedDoneWindowMs = UnreviewedDone.WindowMs;

    /// <summary>The peer row type that records an <c>unreviewed-done</c> alarm was sent.</summary>
    public const string UnreviewedDoneRowType = UnreviewedDone.RowType;
}

/// <summary>The <c>unreviewed-done</c> alarm: a DONE build with no review exchange 15 minutes on. Alarm only; it changes nothing else.</summary>
internal static class UnreviewedDone
{
    internal const string Kind = "UNREVIEWED-DONE";

    /// <summary>The peer row that records the alarm was sent, one per request id.</summary>
    internal const string RowType = "unreviewed-done";

    /// <summary>How long a DONE report may go without a review exchange before the alarm.</summary>
    internal const double AfterMs = 15 * 60_000;

    /// <summary>A report older than this never alarms, so a restarted daemon does not raise the back catalogue.</summary>
    internal const double WindowMs = 60 * 60_000;

    /// <summary>The alarms due now, and the peer rows that keep each one from being sent again. Ages are wall-clock.</summary>
    internal static (IReadOnlyList<WatchEvent> Events, IReadOnlyList<PeerAppend> Rows) Evaluate(
        string sessionId, double now, double startedMs, IReadOnlyList<DoneReport> reports, IReadOnlyList<JsonObject> peerRecords)
    {
        ArgumentNullException.ThrowIfNull(reports);
        ArgumentNullException.ThrowIfNull(peerRecords);
        var told = peerRecords.Where(static r => r.Str("type") == RowType).Select(static r => r.Str("request_id")).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var events = new List<WatchEvent>();
        var rows = new List<PeerAppend>();
        foreach (var r in reports)
        {
            var age = now - r.ReportedMs;
            if (r.Reviewed || r.ReportedMs < startedMs || age < AfterMs || age > WindowMs || !told.Add(r.RequestId))
            {
                continue;
            }

            rows.Add(new PeerAppend(new JsonObject
            {
                ["type"] = RowType,
                ["session_id"] = sessionId,
                ["request_id"] = r.RequestId,
                ["ts"] = Ah.Core.Time.Timestamps.ToIsoString(DateTimeOffset.FromUnixTimeMilliseconds((long)now)),
            }));
            var title = r.Title is { Length: > 0 } t ? $" \"{t}\"" : string.Empty;
            events.Add(new WatchEvent(
                r.RequestId,
                Kind,
                $"ah: {r.Member} reported DONE on {r.RequestId}{title} {DispatchClock.FmtAge(age / 1000)} ago. No review exchange found for this DONE report. If it was reviewed outside a link, ignore this; otherwise send it to the item's reviewer.",
                new JsonObject { ["request_id"] = r.RequestId, ["member"] = r.Member, ["step"] = "unreviewed-done" })
            {
                Team = r.Team,
            });
        }

        return (events, rows);
    }
}
