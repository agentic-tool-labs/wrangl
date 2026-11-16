using Ah.Core.Json;
using Ah.Core.Time;
using Ah.Hierarchy;

namespace Ah.Watch.Model;

/// <summary>
/// The serve-time half of the ETA read model. A slice carries file facts only; the state of a dispatch at the instant a response is built
/// comes from <see cref="HierarchyModule.ComputeEtaState"/> over those facts and that instant, so the slice never depends on the clock.
/// </summary>
internal static class EtaProjection
{
    /// <summary>How many thresholds after the clock base a working addressee still defers the check-in.</summary>
    private const double BusyHoldThresholds = 3;

    /// <summary>How long after its last working write an addressee still counts as working.</summary>
    private const double WorkingFreshMs = 120_000;

    /// <summary>The watch period the check-in episodes are grouped by.</summary>
    internal static double DefaultPollMs => Liveness.DefaultPollMs;

    private static EtaStateResult? Compute(JsonObject body, double nowMs, double pollMs)
    {
        if (body.Get("eta_facts") is not { IsMap: true } map || body.Str("id") is not { } id)
        {
            return null;
        }

        var facts = map.AsObject();
        var threshold = Number(facts, "threshold_s");
        var busyAt = Number(facts, "busy_at_ms");
        var startMs = Number(facts, "start_ms");
        var heardMs = Number(facts, "heard_ms");
        return HierarchyModule.ComputeEtaState(
            new EtaFacts(
                id,
                body.Str("eta") ?? "medium",
                threshold,
                body.Str("created"),
                startMs,
                heardMs,
                Numbers(facts, "nudge_ms"),
                Numbers(facts, "checked_ms"),
                false,
                busyAt > 0 ? Math.Min(busyAt + WorkingFreshMs, Math.Max(startMs, heardMs) + (BusyHoldThresholds * threshold * 1000)) : null),
            nowMs,
            pollMs);
    }

    /// <summary><c>state</c>, <c>due_at</c>, <c>overdue_by</c> and <c>next_nudge</c> of the dispatch a slice body describes at <paramref name="nowMs"/>; null for a body with no eta facts (an answered request, or one not sent by the Orchestrator).</summary>
    internal static JsonObject? Of(JsonObject body, double nowMs, double pollMs)
    {
        if (Compute(body, nowMs, pollMs) is not { } state)
        {
            return null;
        }

        return new JsonObject
        {
            ["state"] = state.State,
            ["due_at"] = Timestamps.ToIsoString(DateTimeOffset.FromUnixTimeMilliseconds((long)state.DueMs)),
            ["overdue_by"] = state.OverdueBySeconds is { } over ? JsonValue.FromNumber(Math.Floor(over)) : JsonValue.Null,
            ["next_nudge"] = state.Next is { } next
                ? new JsonObject { ["step"] = next.Step, ["at"] = Timestamps.ToIsoString(DateTimeOffset.FromUnixTimeMilliseconds((long)next.AtMs)) }
                : JsonValue.Null,
        };
    }

    /// <summary>The first instant after <paramref name="nowMs"/> at which the dispatch a slice body describes changes by time alone; null when it never does (no eta facts, or nothing pending).</summary>
    internal static double? NextChange(JsonObject body, double nowMs, double pollMs) => Compute(body, nowMs, pollMs)?.NextChangeMs;

    /// <summary>How many of <paramref name="bodies"/> (exchange slice bodies of one team) are overdue at <paramref name="nowMs"/>.</summary>
    internal static int OverdueCount(IEnumerable<JsonObject> bodies, double nowMs, double pollMs) =>
        bodies.Count(b => Of(b, nowMs, pollMs)?.Str("state") == "overdue");

    private static double Number(JsonObject o, string key) => o.TryGetValue(key, out var v) && v.Kind == JsonKind.Number ? v.AsNumber() : 0;

    private static List<double> Numbers(JsonObject o, string key)
    {
        var list = new List<double>();
        if (o.TryGetValue(key, out var v) && v.Kind == JsonKind.Array)
        {
            foreach (var item in v.AsArray())
            {
                if (item.Kind == JsonKind.Number)
                {
                    list.Add(item.AsNumber());
                }
            }
        }

        return list;
    }
}
