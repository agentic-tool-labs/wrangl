using Ah.Core.Json;
using Ah.Core.Time;

namespace Ah.Watch.Model;

/// <summary>
/// The serve-time half of the jobs slice. The slice holds file facts; the state of each job at the instant a response is built comes
/// from the one eta function (<see cref="EtaState"/>) over those facts and that instant, as for a dispatch.
/// </summary>
internal static class JobProjection
{
    private static Ah.Hierarchy.EtaStateResult? Compute(JsonObject row, double nowMs, double pollMs)
    {
        if (row.Str("id") is not { } id || row.Str("eta") is not { } eta || !Timestamps.TryParseMs(row.Str("sent_at") ?? string.Empty, out var sent))
        {
            return null;
        }

        var extended = row.Str("extended_at") is { } ext && Timestamps.TryParseMs(ext, out var ms) ? ms : 0;
        var threshold = row.TryGetValue("threshold_s", out var t) && t.Kind == JsonKind.Number ? t.AsNumber() : 0;
        var ended = row.Str("ended_at") is { Length: > 0 };
        var slotSince = row.Str("slot_since") is { } slot && Timestamps.TryParseMs(slot, out var slotMs) ? (double?)slotMs : null;
        return JobEvents.StateOf(id, eta, threshold, sent, extended > 0 ? extended : (double?)null, row.Str("slot_phase"), slotSince, ended, nowMs, pollMs);
    }

    /// <summary>The slice body with <c>state</c>, <c>due_at</c> and <c>overdue_by</c> added to every row, as of <paramref name="nowMs"/>; the body is changed in place.</summary>
    internal static void Project(JsonObject body, double nowMs, double pollMs)
    {
        if (body.Get("rows") is not { IsArray: true } rows)
        {
            return;
        }

        foreach (var value in rows.AsArray())
        {
            if (!value.IsMap || Compute(value.AsObject(), nowMs, pollMs) is not { } state)
            {
                continue;
            }

            var row = value.AsObject();
            row["state"] = state.State;
            row["due_at"] = double.IsFinite(state.DueMs) ? Timestamps.ToIsoString(DateTimeOffset.FromUnixTimeMilliseconds((long)state.DueMs)) : JsonValue.Null;
            row["overdue_by"] = state.OverdueBySeconds is { } over ? JsonValue.FromNumber(Math.Floor(over)) : JsonValue.Null;
        }
    }

    /// <summary>How many rows of the slice body are overdue at <paramref name="nowMs"/>, by the same state function <see cref="Project"/> serves.</summary>
    internal static int OverdueCount(JsonObject body, double nowMs, double pollMs) =>
        body.Get("rows") is { IsArray: true } rows
            ? rows.AsArray().Count(value => value.IsMap && Compute(value.AsObject(), nowMs, pollMs)?.State == "overdue")
            : 0;

    /// <summary>The owners of the rows of the slice body that are overdue at <paramref name="nowMs"/>, by the same state function <see cref="Project"/> serves.</summary>
    internal static HashSet<string> OverdueOwners(JsonObject body, double nowMs, double pollMs)
    {
        var owners = new HashSet<string>(StringComparer.Ordinal);
        if (body.Get("rows") is { IsArray: true } rows)
        {
            foreach (var value in rows.AsArray())
            {
                if (value.IsMap && value.AsObject().Str("owner") is { } owner && Compute(value.AsObject(), nowMs, pollMs)?.State == "overdue")
                {
                    _ = owners.Add(owner);
                }
            }
        }

        return owners;
    }

    /// <summary>The first instant after <paramref name="nowMs"/> at which a row of the slice body changes by time alone; null when none does.</summary>
    internal static double? NextChange(JsonObject body, double nowMs, double pollMs)
    {
        double? earliest = null;
        if (body.Get("rows") is { IsArray: true } rows)
        {
            foreach (var value in rows.AsArray())
            {
                if (value.IsMap && Compute(value.AsObject(), nowMs, pollMs)?.NextChangeMs is { } next && (earliest is null || next < earliest))
                {
                    earliest = next;
                }
            }
        }

        return earliest;
    }
}
