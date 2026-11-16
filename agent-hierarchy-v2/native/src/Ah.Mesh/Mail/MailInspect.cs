using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Text;

namespace Ah.Mesh;

/// <summary>
/// Read-only views of the mailbox state, shared by the daemon's <c>/v1/inspect/*</c> endpoints and by the CLI when it answers from the
/// files: both call these, so a daemon that is up and one that is down give the same JSON. Nothing here writes; every time a view carries
/// is a stored timestamp, so two calls a moment apart return the same object.
/// </summary>
public interface IMailInspect
{
    /// <summary>The entry cap of an entries view.</summary>
    const int MaxEntries = 200;

    /// <summary>The filters an entries view takes.</summary>
    static IReadOnlyList<string> EntryFilters { get; } = ["pending", "acked", "all"];

    /// <summary><c>{boxes}</c>: per mailbox its directory, entry counts, and the newest heartbeat's time and adapter.</summary>
    JsonObject Boxes(string? address = null, double? nowMs = null);

    /// <summary>
    /// <c>{address, filter, entries, total}</c>: the entries of box <paramref name="dir"/> (the newest <paramref name="n"/> of those passing
    /// <paramref name="filter"/>), each with its route, ack state and latest routing attempt.
    /// </summary>
    JsonObject Entries(string dir, string? address, string filter = "pending", int n = MaxEntries);

    /// <summary><c>{registrations}</c>: every registration, live or stale, with the adapter freshness of its first box.</summary>
    JsonObject Registrations(double? nowMs = null);

    /// <summary><c>{events}</c>: unacked event entries the daemon mailed, oldest first.</summary>
    JsonObject Events(string? address = null);
}

internal sealed class MailInspect(Mailbox mailbox, IFileReader files, IAhEnvironment environment, IClock clock) : IMailInspect
{
    private double NowMs => clock.UtcNow.ToUnixTimeMilliseconds();

    private static string Js(JsonValue? v) => JsConvert.ToJsString(v);

    private string AdapterState(string dir, double now)
    {
        var hb = mailbox.ReadHeartbeat(dir, null, now);
        return hb is null ? "none" : mailbox.HeartbeatFresh(hb, now) ? "fresh" : "stale";
    }

    private static JsonValue OrNull(JsonObject o, string key) => o.Get(key) is { IsNull: false } v ? v : JsonValue.Null;

    public JsonObject Boxes(string? address = null, double? nowMs = null)
    {
        var now = nowMs ?? NowMs;
        var rows = new List<(string Key, JsonObject Row)>();
        foreach (var b in mailbox.ListBoxes())
        {
            if (!string.IsNullOrEmpty(address) && b.Address != address)
            {
                continue;
            }

            var hb = mailbox.ReadHeartbeat(b.Dir, null, now);
            var row = new JsonObject
            {
                ["address"] = b.Address,
                ["dir"] = b.Dir,
                ["entries"] = mailbox.ReadEntries(b.Dir).Entries.Count,
                ["unacked"] = mailbox.Inbox(b.Dir, new InboxOptions(NowMs: now)).Entries.Count,
                ["overdue"] = mailbox.Inbox(b.Dir, new InboxOptions(Overdue: true, NowMs: now)).Entries.Count,
                ["heartbeat_at"] = hb?.Get("at") is { } at ? at : JsonValue.Null,
                ["adapter"] = hb?.Get("adapter") is { } adapter ? adapter : JsonValue.Null,
                ["adapter_state"] = AdapterState(b.Dir, now),
            };
            rows.Add(($"{b.Address}\0{b.Dir}", row));
        }

        var boxes = new JsonArray();
        foreach (var (_, row) in rows.OrderBy(r => r.Key, StringComparer.Ordinal))
        {
            boxes.Add(row);
        }

        return new JsonObject { ["boxes"] = boxes };
    }

    /// <summary>The latest routing attempt per entry id, from the box's attempt log.</summary>
    private Dictionary<string, JsonObject> LatestAttempts(string dir)
    {
        var latest = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var o in mailbox.ReadAttempts(dir))
        {
            if (o.Get("id") is { IsText: true } id && !(o.Get("ladder") is { IsBool: true } l && l.AsBool()))
            {
                latest[id.AsString()] = new JsonObject
                {
                    ["state"] = OrNull(o, "state"),
                    ["rung"] = OrNull(o, "rung"),
                    ["adapter"] = OrNull(o, "adapter"),
                    ["at"] = OrNull(o, "at"),
                    ["reason"] = OrNull(o, "reason"),
                };
            }
        }

        return latest;
    }

    public JsonObject Entries(string dir, string? address, string filter = "pending", int n = IMailInspect.MaxEntries)
    {
        var attempts = LatestAttempts(dir);
        var rows = new List<JsonObject>();
        foreach (var v in mailbox.ReadEntries(dir).Entries)
        {
            var e = v.AsObject();
            var id = e.Get("id")!.Value.AsString();
            var acked = mailbox.IsAcked(dir, id);
            if (!(filter == "all" || (filter == "acked") == acked))
            {
                continue;
            }

            var row = new JsonObject();
            foreach (var key in new[] { "id", "kind", "from", "to", "created" })
            {
                if (e.Get(key) is { } value)
                {
                    row[key] = value;
                }
            }

            row["route"] = OrNull(e, "route");
            row["ref"] = OrNull(e, "ref");
            row["event"] = OrNull(e, "event");
            row["acked"] = acked;
            row["attempt"] = attempts.TryGetValue(id, out var attempt) ? attempt : JsonValue.Null;
            rows.Add(row);
        }

        // `rows.slice(-n)`: a count of zero reads as the whole list, and a negative count skips that many from the front.
        var start = n == 0 ? 0 : -n;
        start = start < 0 ? Math.Max(rows.Count + start, 0) : Math.Min(start, rows.Count);
        var kept = new JsonArray();
        foreach (var row in rows.Skip(start))
        {
            kept.Add(row);
        }

        var result = new JsonObject();
        if (address is not null)
        {
            result["address"] = address;
        }

        result["filter"] = filter;
        result["entries"] = kept;
        result["total"] = rows.Count;
        return result;
    }

    public JsonObject Registrations(double? nowMs = null)
    {
        var now = nowMs ?? NowMs;
        var directory = NodePath.Join(environment.AhHome, "registrations");
        var stored = mailbox.StoredSessionIds();
        List<string> names;
        try
        {
            names = stored is not null ? stored.Select(static id => id + ".json").ToList() : files.List(directory).Where(f => f.EndsWith(".json", StringComparison.Ordinal)).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            names = [];
        }

        var rows = new List<(string Key, JsonObject Row)>();
        foreach (var name in names)
        {
            JsonObject? r;
            try
            {
                r = stored is not null ? mailbox.ReadRegistration(name[..^".json".Length]) : JsonText.Parse(files.ReadText(NodePath.Join(directory, name))) is { IsMap: true } v ? v.AsObject() : null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonParseException)
            {
                continue;
            }

            if (r is null || !(r.Get("address") is { IsText: true }))
            {
                continue;
            }

            var boxes = mailbox.RegistrationBoxes(r);
            var sessionId = r.Get("session_id") is { IsNull: false } sid ? sid : JsonValue.FromString(name[..^".json".Length]);
            var addresses = r.Get("addresses") is { IsArray: true } list ? list : new JsonArray { r.Get("address")!.Value };
            rows.Add((Js(sessionId), new JsonObject
            {
                ["session_id"] = sessionId,
                ["address"] = r.Get("address")!.Value,
                ["addresses"] = addresses,
                ["harness"] = r.Get("harness") is { IsTruthy: true } harness ? harness : JsonValue.Null,
                ["pid"] = OrNull(r, "pid"),
                ["live"] = mailbox.RegistrationLive(r),
                ["pane_id"] = OrNull(r, "pane_id"),
                ["team_file"] = OrNull(r, "team_file"),
                ["at"] = OrNull(r, "at"),
                ["adapter"] = boxes.Count == 0 ? "none" : AdapterState(boxes[0].Dir, now),
            }));
        }

        var registrations = new JsonArray();
        foreach (var (_, row) in rows.OrderBy(r => r.Key, StringComparer.Ordinal))
        {
            registrations.Add(row);
        }

        return new JsonObject { ["registrations"] = registrations };
    }

    public JsonObject Events(string? address = null)
    {
        var rows = new List<(string Key, JsonObject Row)>();
        foreach (var b in mailbox.ListBoxes())
        {
            if (!string.IsNullOrEmpty(address) && b.Address != address)
            {
                continue;
            }

            foreach (var v in mailbox.ReadEntries(b.Dir).Entries)
            {
                var e = v.AsObject();
                var id = e.Get("id")!.Value.AsString();
                if (!(e.Get("kind") is { IsText: true } kind && kind.AsString() == "event")
                    || !(e.Get("from") is { IsText: true } from && from.AsString() == "ahd")
                    || mailbox.IsAcked(b.Dir, id))
                {
                    continue;
                }

                var request = e.Get("data") is { IsMap: true } data && data.AsObject().Get("request_id") is { IsTruthy: true } rid ? rid : JsonValue.Null;
                var row = new JsonObject
                {
                    ["address"] = b.Address,
                    ["id"] = id,
                    ["event"] = OrNull(e, "event"),
                    ["to_session"] = OrNull(e, "to_session"),
                    ["request_id"] = request,
                };
                if (e.Get("created") is { } created)
                {
                    row["created"] = created;
                }

                rows.Add(($"{Js(e.Get("created"))}\0{id}", row));
            }
        }

        var events = new JsonArray();
        foreach (var (_, row) in rows.OrderBy(r => r.Key, StringComparer.Ordinal))
        {
            events.Add(row);
        }

        return new JsonObject { ["events"] = events };
    }
}
