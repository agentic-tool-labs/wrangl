using System.Globalization;
using Ah.Core.Json;
using Ah.Core.Time;

namespace Ah.Watch;

/// <summary>A session whose context fill is watched: a member of an owned team.</summary>
/// <param name="SessionId">The subject's session id; none means it is skipped.</param>
/// <param name="Label">The member's name; an alert names the member by its address when this is empty.</param>
/// <param name="Member">The <c>name@team</c> address the event carries.</param>
/// <param name="Context">The latest reading, <c>{used, window_source, at}</c> plus <c>pct</c> when its window is known, or null.</param>
/// <param name="Role">The member's role, named beside its name in an alert; null or empty when unknown.</param>
public sealed record ContextSubject(string? SessionId, string Label, string Member, JsonObject? Context, string? Role = null);

/// <summary>The context-high and context-critical alerts, once per (session, threshold) until the fill falls well below it.</summary>
internal static class ContextAlerts
{
    /// <summary>The fills at which <c>context-high</c> and <c>context-critical</c> fire, in percent.</summary>
    internal const int Warn = 70;

    internal const int Act = 85;

    /// <summary>A fill reading older than this is not acted on.</summary>
    internal const double FreshMs = 2 * 3_600_000;

    /// <summary>A threshold re-arms once the fill is this far below it.</summary>
    internal const int RearmGap = 10;

    /// <summary>The context level of a percentage: 2 at the act threshold, 1 at the warn threshold, else 0.</summary>
    internal static int Level(JsonValue pct) =>
        pct.Kind != JsonKind.Number || Math.Floor(pct.AsNumber()) != pct.AsNumber() ? 0 : pct.AsNumber() >= Act ? 2 : pct.AsNumber() >= Warn ? 1 : 0;

    /// <summary>
    /// The alerts due now. <paramref name="gates"/> is the team's gate rows as read; the episode and re-arm rows this call decides
    /// are returned (to <paramref name="home"/>), and count for the later subjects of the same call.
    /// </summary>
    internal static (IReadOnlyList<WatchEvent> Events, IReadOnlyList<GateAppend> Gates) Evaluate(
        string home, IReadOnlyList<JsonObject> gates, IReadOnlyList<ContextSubject> subjects, double now)
    {
        ArgumentNullException.ThrowIfNull(gates);
        ArgumentNullException.ThrowIfNull(subjects);
        var rows = new List<JsonObject>(gates);
        var appends = new List<GateAppend>();
        var events = new List<WatchEvent>();

        IEnumerable<JsonObject> KeyRows(string sid, int threshold) =>
            rows.Where(g => g.Str("type") is "context-episode" or "context-rearm" && g.Str("session_id") == sid && g.Int("threshold") == threshold);

        bool Armed(string sid, int threshold)
        {
            var last = KeyRows(sid, threshold).LastOrDefault();
            return last is null || last.Str("type") == "context-rearm";
        }

        void Record(string type, string sid, int threshold)
        {
            rows.Add(new JsonObject { ["type"] = type, ["session_id"] = sid, ["threshold"] = threshold, ["ts"] = Timestamps.ToIsoString(DateTimeOffset.FromUnixTimeMilliseconds((long)now)) });
            appends.Add(new GateAppend(home, new JsonObject { ["type"] = type, ["session_id"] = sid, ["threshold"] = threshold }));
        }

        foreach (var s in subjects)
        {
            var c = s.Context;
            var at = c is null ? double.NaN : Liveness.TimeOrNaN(c, "at");
            if (string.IsNullOrEmpty(s.SessionId) || c is null || double.IsNaN(at) || now - at > FreshMs)
            {
                continue;
            }

            if (!c.TryGetValue("pct", out var pctValue) || pctValue.Kind != JsonKind.Number)
            {
                continue;
            }

            var pct = pctValue.AsNumber();
            var sid = s.SessionId;
            var crossed = new[] { Warn, Act }.Where(t => pct >= t && Armed(sid, t)).ToList();
            foreach (var t in crossed)
            {
                Record("context-episode", sid, t);
            }

            foreach (var t in new[] { Warn, Act })
            {
                if (pct < t - RearmGap && !Armed(sid, t))
                {
                    Record("context-rearm", sid, t);
                }
            }

            if (crossed.Count == 0)
            {
                continue;
            }

            var top = crossed.Max();
            var critical = top == Act;
            var pctText = pct.ToString("0.##", CultureInfo.InvariantCulture);
            var role = string.IsNullOrEmpty(s.Role) ? null : s.Role;
            var who = s.Label.Length == 0 ? s.Member : role is null ? s.Label : $"{s.Label} ({role})";
            events.Add(new WatchEvent(
                $"context-{sid}-{top}",
                critical ? "CONTEXT-CRITICAL" : "CONTEXT-HIGH",
                critical
                    ? $"ah: {who} is at {pctText}% of its context window. Ask it to report now; a compaction may follow."
                    : $"ah: {who} is at {pctText}% of its context window. Consider asking it to finish and report soon.",
                new JsonObject
                {
                    ["member"] = s.Member,
                    ["session_id"] = sid,
                    ["pct"] = pct,
                    ["threshold"] = top,
                    ["role"] = role is null ? JsonValue.Null : role,
                }));
        }

        return (events, appends);
    }
}
