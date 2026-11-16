using System.Globalization;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Time;
using Ah.Hierarchy;

namespace Ah.Watch;

/// <summary>The daemon's watch period and the readers of rows both the Stop hook and the daemon use for "past its eta".</summary>
internal static class Liveness
{
    /// <summary>How often the daemon reads the store, before any override.</summary>
    internal const double DefaultPollMs = 15_000;

    /// <summary>
    /// The poll period: <c>AH_WATCH_POLL_MS</c>, then <c>AHD_WATCH_POLL_MS</c> (the daemon's environment is scrubbed, so a test
    /// lowers it with the second), the first that is a number above 0; else 15 s.
    /// </summary>
    internal static double PollMs(IAhEnvironment env)
    {
        ArgumentNullException.ThrowIfNull(env);
        foreach (var name in new[] { "AH_WATCH_POLL_MS", "AHD_WATCH_POLL_MS" })
        {
            var raw = env.GetVariable(name);
            if (raw is null)
            {
                continue;
            }

            var text = raw.Trim();
            var n = text.Length == 0
                ? 0
                : double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : double.NaN;
            if (n > 0)
            {
                return n;
            }
        }

        return DefaultPollMs;
    }

    /// <summary>
    /// Distinct episodes among ascending timestamps (ms): rows closer than <paramref name="gapMs"/> to the previous one belong to
    /// the same episode. Returns each episode's first timestamp.
    /// </summary>
    internal static List<double> Episodes(IReadOnlyList<double> sortedTimes, double gapMs) => HierarchyModule.EtaEpisodes(sortedTimes, gapMs);

    /// <summary><c>Date.parse(ts) || 0</c> as the oracle writes it for a row's time.</summary>
    internal static double TimeOrZero(JsonObject row, string key) =>
        row.Str(key) is { } text && Timestamps.TryParseMs(text, out var ms) && ms != 0 ? ms : 0;

    /// <summary>The row's time, or NaN when it is absent or does not parse.</summary>
    internal static double TimeOrNaN(JsonObject row, string key) =>
        row.Str(key) is { } text && Timestamps.TryParseMs(text, out var ms) ? ms : double.NaN;

    /// <summary>
    /// One dispatch's liveness clock: <c>Base</c> is the later of the dispatch <c>start</c> (ms; 0 if unknown) and the latest
    /// <c>heard</c> row from <paramref name="toAddr"/> for this session, and <c>Nudges</c> are the <c>liveness-nudge</c> gate times
    /// (ms, ascending) after <c>Base</c>. Whatever the peer said restarts the clock, and nudges from before it no longer count.
    /// </summary>
    internal static (double Base, List<double> Nudges) Clock(
        IReadOnlyList<JsonObject> records,
        IReadOnlyList<JsonObject> gates,
        string sessionId,
        string requestId,
        string? toAddr,
        double start)
    {
        var baseMs = HierarchyModule.EtaBase(start, Heard(records, sessionId, toAddr));
        return (baseMs, GateTimes(gates, "liveness-nudge", sessionId, requestId).Where(t => t > baseMs).ToList());
    }

    /// <summary>The peer row ahd writes once a plain-send request's member has worked on it (<see cref="HierarchyModule.PlainSendArmedRow"/>).</summary>
    internal const string PlainSendArmedRow = HierarchyModule.PlainSendArmedRow;

    /// <summary>The peer row the SendMessage hook writes for a follow-up to an open request: <c>{type, session_id, request_id, at}</c>.</summary>
    internal const string FollowupRow = "followup";

    /// <summary>How long of work after a plain send arms its request (board-derive call 6).</summary>
    internal const double PlainSendWorkMs = 60_000;

    /// <summary>
    /// The one reading of whether a plain-send request's clock runs, shared by the dispatch clock and the overdue timer so they cannot disagree:
    /// when its member began the work that armed it (ms), or null while it is unarmed.
    /// </summary>
    internal static double? PlainSendArmedAt(IReadOnlyList<JsonObject> records, string sessionId, string requestId) =>
        LatestRow(records, PlainSendArmedRow, sessionId, requestId);

    /// <summary>Whether <paramref name="c"/> is a plain send not yet armed: no reader may count it as holding its member busy (board-derive § C3 r3).</summary>
    internal static bool UnarmedPlainSend(DispatchCandidate c, IReadOnlyList<JsonObject> records, string sessionId) =>
        c.PlainSend && PlainSendArmedAt(records, sessionId, c.RequestId) is null;

    /// <summary>The latest follow-up to <paramref name="requestId"/> this session sent (ms), or null.</summary>
    internal static double? LatestFollowup(IReadOnlyList<JsonObject> records, string sessionId, string requestId) =>
        LatestRow(records, FollowupRow, sessionId, requestId);

    private static double? LatestRow(IReadOnlyList<JsonObject> records, string type, string sessionId, string requestId)
    {
        ArgumentNullException.ThrowIfNull(records);
        double? latest = null;
        foreach (var r in records)
        {
            if (r.Str("type") == type && r.Str("session_id") == sessionId && r.Str("request_id") == requestId && TimeOrZero(r, "at") is var at and > 0)
            {
                latest = Math.Max(latest ?? 0, at);
            }
        }

        return latest;
    }

    /// <summary>The latest <c>heard</c> row from <paramref name="toAddr"/> for this session, as a time (ms); 0 when there is none.</summary>
    internal static double Heard(IReadOnlyList<JsonObject> records, string sessionId, string? toAddr)
    {
        ArgumentNullException.ThrowIfNull(records);
        double heard = 0;
        foreach (var r in records)
        {
            if (!string.IsNullOrEmpty(toAddr) && r.Str("type") == "heard" && r.Str("session_id") == sessionId && r.Str("from") == toAddr)
            {
                heard = Math.Max(heard, TimeOrZero(r, "ts"));
            }
        }

        return heard;
    }

    /// <summary>The times (ms, ascending) of this session's gate rows of <paramref name="type"/> for the request; a row whose time does not parse is left out.</summary>
    internal static List<double> GateTimes(IReadOnlyList<JsonObject> gates, string type, string sessionId, string requestId)
    {
        ArgumentNullException.ThrowIfNull(gates);
        var times = new List<double>();
        foreach (var g in gates)
        {
            if (g.Str("type") == type && g.Str("session_id") == sessionId && g.Str("request_id") == requestId && TimeOrNaN(g, "ts") is var t && !double.IsNaN(t))
            {
                times.Add(t);
            }
        }

        times.Sort();
        return times;
    }

    /// <summary>
    /// The latest <c>liveness-checked</c> time (ms) after <paramref name="since"/> for this session's request, or 0 when there is
    /// none. A checked row records that the Orchestrator looked and saw the peer busy: it postpones the next prompt by the current
    /// gap without counting as a nudge, so it never advances a dispatch toward silence.
    /// </summary>
    internal static double LastChecked(IReadOnlyList<JsonObject> gates, string sessionId, string requestId, double since) =>
        GateTimes(gates, "liveness-checked", sessionId, requestId).Where(t => t > since).DefaultIfEmpty(0).Max();

    internal static double SilentDueAt(double checkinMs, double lookMs, double baseMs, double thresholdMs, double gapFactor) =>
        HierarchyModule.EtaSilentDueAt(checkinMs, lookMs, baseMs, thresholdMs, gapFactor);

    /// <summary>
    /// A dispatch is due for a check-in when it has never been nudged, or when the gap the cadence gives for its next check-in has
    /// passed since its last nudge or busy look (<see cref="LastChecked"/>), whichever is later: half a threshold before the second
    /// nudge, a full one before each later one. After a first nudge a busy look follows <see cref="SilentDueAt"/> instead. Only
    /// nudges are counted. An unparseable or absent <c>ts</c> on a nudge counts as due; nudging one extra time is the harmless
    /// direction.
    /// </summary>
    internal static bool DueForNudge(
        IReadOnlyList<JsonObject> gates,
        string sessionId,
        string itemId,
        double thresholdSeconds,
        IReadOnlyList<double> cadence,
        double now,
        double since = 0)
    {
        ArgumentNullException.ThrowIfNull(gates);
        ArgumentNullException.ThrowIfNull(cadence);
        double lastTs = 0;
        var count = 0;
        foreach (var r in gates)
        {
            if (r.Str("type") != "liveness-nudge" || r.Str("session_id") != sessionId || r.Str("request_id") != itemId)
            {
                continue;
            }

            var t = TimeOrNaN(r, "ts");
            if (double.IsNaN(t))
            {
                return true;
            }

            if (t <= since)
            {
                continue;
            }

            count++;
            if (t > lastTs)
            {
                lastTs = t;
            }
        }

        var look = LastChecked(gates, sessionId, itemId, since);
        if (count == 1 && look > lastTs)
        {
            return now >= SilentDueAt(lastTs, look, since, thresholdSeconds * 1000, cadence[Math.Min(count, cadence.Count - 1)]);
        }

        var reference = Math.Max(lastTs, look);
        if (reference == 0)
        {
            return true;
        }

        var gap = cadence[Math.Min(count, cadence.Count - 1)];
        return now - reference >= thresholdSeconds * gap * 1000;
    }
}

/// <summary>Small readers over <see cref="JsonObject"/> for the watch logic's records.</summary>
internal static class WatchJson
{
    internal static string? Str(this JsonObject o, string key) =>
        o.TryGetValue(key, out var v) && v.Kind == JsonKind.Text ? v.AsString() : null;

    internal static JsonObject? Obj(this JsonObject o, string key) =>
        o.TryGetValue(key, out var v) && v.Kind == JsonKind.Map ? v.AsObject() : null;

    /// <summary>A whole number member (<c>Number.isInteger</c>), else null.</summary>
    internal static long? Int(this JsonObject o, string key) =>
        o.TryGetValue(key, out var v) && v.Kind == JsonKind.Number && Math.Floor(v.AsNumber()) == v.AsNumber() && !double.IsInfinity(v.AsNumber())
            ? (long)v.AsNumber()
            : null;

    internal static JsonValue Get(this JsonObject o, string key) => o.TryGetValue(key, out var v) ? v : JsonValue.Null;

    internal static IReadOnlyList<JsonObject> Objects(JsonArray array)
    {
        var list = new List<JsonObject>();
        foreach (var item in array)
        {
            if (item.Kind == JsonKind.Map)
            {
                list.Add(item.AsObject());
            }
        }

        return list;
    }
}
