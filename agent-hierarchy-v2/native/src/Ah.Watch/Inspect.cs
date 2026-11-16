using Ah.Core.Json;

namespace Ah.Watch;

/// <summary>
/// The read-only views of the dispatch clock the daemon's <c>/v1/inspect/timers</c> and <c>/v1/inspect/events</c> routes answer.
/// Nothing here writes: a dry evaluation, so two calls a moment apart return the same object.
/// </summary>
internal static class Inspect
{
    /// <summary>
    /// For every live Orchestrator registration (or only <paramref name="session"/>): its dispatches with their check-in and
    /// silence deadlines, and the events the dispatch clock would fire now.
    /// </summary>
    internal static JsonObject Timers(IWatchHost host, double nowMs, double pollMs, string? session)
    {
        var timers = new List<(string Session, JsonObject Row)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reg in host.LiveRegistrations())
        {
            var sid = reg.SessionId;
            if (seen.Contains(sid) || (!string.IsNullOrEmpty(session) && sid != session))
            {
                continue;
            }

            var boxes = reg.Boxes.Where(static b => b.Address.StartsWith("orchestrator@", StringComparison.Ordinal)).ToList();
            if (boxes.Count == 0)
            {
                continue;
            }

            _ = seen.Add(sid);
            var outcome = DispatchClock.Evaluate(sid, nowMs, pollMs, host.Dispatches(sid, nowMs), host.PeerRecords(), host.Gates, host.ThresholdSeconds);
            var dispatches = new JsonArray();
            foreach (var r in outcome.Report)
            {
                dispatches.Add(JsonValue.FromObject(new JsonObject
                {
                    ["request_id"] = r.RequestId,
                    ["to"] = r.To,
                    ["member"] = Nullable(r.Member),
                    ["eta"] = r.Eta,
                    ["created"] = Nullable(r.Created),
                    ["state"] = Nullable(r.State),
                    ["check_in_at"] = Nullable(r.CheckInAt),
                    ["silent_at"] = Nullable(r.SilentAt),
                    ["queued_after"] = Nullable(r.QueuedAfter),
                }));
            }

            var due = new JsonArray();
            foreach (var ev in outcome.Events)
            {
                var member = ev.Data.Str("member");
                due.Add(JsonValue.FromObject(new JsonObject
                {
                    ["request_id"] = Nullable(ev.RequestId),
                    ["event"] = DispatchClock.EventName.GetValueOrDefault(ev.Kind) is { } name ? JsonValue.FromString(name) : JsonValue.Null,
                    ["member"] = string.IsNullOrEmpty(member) ? JsonValue.Null : JsonValue.FromString(member),
                }));
            }

            var addresses = new JsonArray();
            foreach (var b in boxes)
            {
                addresses.Add(JsonValue.FromString(b.Address));
            }

            timers.Add((sid, new JsonObject
            {
                ["session"] = sid,
                ["addresses"] = JsonValue.FromArray(addresses),
                ["owned"] = boxes.Any(b => host.PullFresh(b.Dir, sid, nowMs)),
                ["dispatches"] = JsonValue.FromArray(dispatches),
                ["due"] = JsonValue.FromArray(due),
            }));
        }

        var sorted = new JsonArray();
        foreach (var (_, row) in timers.OrderBy(static t => t.Session, StringComparer.Ordinal))
        {
            sorted.Add(JsonValue.FromObject(row));
        }

        return new JsonObject { ["timers"] = JsonValue.FromArray(sorted) };
    }

    /// <summary>The unacked event entries the daemon mailed, oldest first, optionally of one box.</summary>
    internal static JsonObject Events(
        IEnumerable<WatchBox> boxes,
        Func<string, IReadOnlyList<JsonObject>> entriesOf,
        Func<string, string, bool> isAcked,
        string? address)
    {
        var events = new List<(string Key, JsonObject Row)>();
        foreach (var box in boxes)
        {
            if (!string.IsNullOrEmpty(address) && box.Address != address)
            {
                continue;
            }

            foreach (var e in entriesOf(box.Dir))
            {
                var id = e.Str("id");
                if (e.Str("kind") != "event" || e.Str("from") != "ahd" || (id is not null && isAcked(box.Dir, id)))
                {
                    continue;
                }

                var row = new JsonObject { ["address"] = box.Address };
                if (id is not null)
                {
                    row["id"] = id;
                }

                row["event"] = Nullable(e.Str("event"));
                row["to_session"] = Nullable(e.Str("to_session"));
                var requestId = e.Obj("data") is { } data && data.Str("request_id") is { Length: > 0 } rid ? rid : null;
                row["request_id"] = Nullable(requestId);
                var created = e.Str("created");
                if (created is not null)
                {
                    row["created"] = created;
                }

                events.Add(($"{created ?? "undefined"}\0{id ?? "undefined"}", row));
            }
        }

        var sorted = new JsonArray();
        foreach (var (_, row) in events.OrderBy(static e => e.Key, StringComparer.Ordinal))
        {
            sorted.Add(JsonValue.FromObject(row));
        }

        return new JsonObject { ["events"] = JsonValue.FromArray(sorted) };
    }

    private static JsonValue Nullable(string? value) => value is null ? JsonValue.Null : JsonValue.FromString(value);
}
