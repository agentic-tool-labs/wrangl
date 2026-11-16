using Ah.Core.Json;

namespace Ah.Mesh;

/// <summary>
/// Read-only views of the mailbox: what <c>/v1/inspect/boxes|entries|registrations|events</c> answer and what the CLI answers from
/// the files when no daemon is up, so a daemon that is up and one that is down give the same JSON. Nothing here writes anything.
/// Ages are left to the renderer: every time a view carries is a stored timestamp.
/// </summary>
public interface IMailboxInspect
{
    /// <summary>Per mailbox: its directory, entry counts, and the newest heartbeat's time and adapter, ordered by address then directory.</summary>
    JsonObject Boxes(string? address, double nowMs);

    /// <summary>The entries of box <paramref name="dir"/> (the newest <paramref name="n"/> of those passing <paramref name="filter"/>: pending, acked or all), each with its route, ack state and latest routing attempt.</summary>
    JsonObject Entries(string dir, string address, string filter, int n);

    /// <summary>Every registration, live or stale, with the adapter freshness of its first box, ordered by session id.</summary>
    JsonObject Registrations(double nowMs);

    /// <summary>The unacked event entries the daemon mailed, oldest first, for one box or all.</summary>
    JsonObject Events(string? address);
}

internal sealed class MailboxInspect(IMailbox mailbox) : IMailboxInspect
{
    private static JsonValue OrNull(JsonValue? v) => v is { IsNull: false } x ? x : JsonValue.Null;

    private string AdapterState(string dir, double now)
    {
        var hb = mailbox.ReadHeartbeat(dir, null, now);
        return hb is null ? "none" : mailbox.HeartbeatFresh(hb, now) ? "fresh" : "stale";
    }

    private static JsonArray Sorted(IEnumerable<(string Key, JsonObject Row)> rows)
    {
        var sorted = new JsonArray();
        foreach (var (_, row) in rows.OrderBy(r => r.Key, StringComparer.Ordinal))
        {
            sorted.Add(row);
        }

        return sorted;
    }

    public JsonObject Boxes(string? address, double nowMs)
    {
        var rows = new List<(string Key, JsonObject Row)>();
        foreach (var box in mailbox.ListBoxes().Where(b => string.IsNullOrEmpty(address) || b.Address == address))
        {
            var hb = mailbox.ReadHeartbeat(box.Dir, null, nowMs);
            rows.Add((
                $"{box.Address}\0{box.Dir}",
                new JsonObject
                {
                    ["address"] = box.Address,
                    ["dir"] = box.Dir,
                    ["entries"] = mailbox.ReadEntries(box.Dir).Entries.Count,
                    ["unacked"] = mailbox.Inbox(box.Dir, new InboxOptions(NowMs: nowMs)).Entries.Count,
                    ["overdue"] = mailbox.Inbox(box.Dir, new InboxOptions(Overdue: true, NowMs: nowMs)).Entries.Count,
                    ["heartbeat_at"] = hb is null ? JsonValue.Null : OrNull(hb.Get("at")),
                    ["adapter"] = hb is null ? JsonValue.Null : OrNull(hb.Get("adapter")),
                    ["adapter_state"] = AdapterState(box.Dir, nowMs),
                }));
        }

        return new JsonObject { ["boxes"] = Sorted(rows) };
    }

    public JsonObject Entries(string dir, string address, string filter, int n)
    {
        var latest = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var row in mailbox.ReadAttempts(dir))
        {
            if (row.Get("id") is { IsText: true } id && !(row.Get("ladder") is { Kind: JsonKind.Bool } l && l.AsBool()))
            {
                latest[id.AsString()] = new JsonObject
                {
                    ["state"] = OrNull(row.Get("state")),
                    ["rung"] = OrNull(row.Get("rung")),
                    ["adapter"] = OrNull(row.Get("adapter")),
                    ["at"] = OrNull(row.Get("at")),
                    ["reason"] = OrNull(row.Get("reason")),
                };
            }
        }

        var rows = new List<JsonObject>();
        foreach (var e in mailbox.ReadEntries(dir).Entries)
        {
            var id = e.Member("id") is { IsText: true } t ? t.AsString() : string.Empty;
            var acked = mailbox.IsAcked(dir, id);
            if (!(filter == "all" || (filter == "acked") == acked))
            {
                continue;
            }

            rows.Add(new JsonObject
            {
                ["id"] = OrNull(e.Member("id")),
                ["kind"] = OrNull(e.Member("kind")),
                ["from"] = OrNull(e.Member("from")),
                ["to"] = OrNull(e.Member("to")),
                ["created"] = OrNull(e.Member("created")),
                ["route"] = OrNull(e.Member("route")),
                ["ref"] = OrNull(e.Member("ref")),
                ["event"] = OrNull(e.Member("event")),
                ["acked"] = acked,
                ["attempt"] = latest.TryGetValue(id, out var attempt) ? attempt : JsonValue.Null,
            });
        }

        var shown = new JsonArray();
        foreach (var row in rows.Skip(Math.Max(0, rows.Count - n)))
        {
            shown.Add(row);
        }

        return new JsonObject { ["address"] = address, ["filter"] = filter, ["entries"] = shown, ["total"] = rows.Count };
    }

    public JsonObject Registrations(double nowMs)
    {
        var rows = new List<(string Key, JsonObject Row)>();
        foreach (var r in mailbox.AllRegistrations())
        {
            if (!(r.Get("address") is { IsText: true } address))
            {
                continue;
            }

            var registered = mailbox.RegistrationBoxes(r);
            var box = registered.Count > 0 ? registered[0] : null;
            var live = mailbox.RegistrationLive(r);
            var sessionId = r.Get("session_id") is { IsNull: false } sid ? sid : JsonValue.FromString(string.Empty);
            JsonValue addresses = r.Get("addresses") is { IsArray: true } list ? list : new JsonArray { address };
            rows.Add((
                JsConvert.ToJsString(sessionId),
                new JsonObject
                {
                    ["session_id"] = sessionId,
                    ["address"] = address,
                    ["addresses"] = addresses,
                    ["harness"] = r.Get("harness") is { IsTruthy: true } h ? h : JsonValue.Null,
                    ["pid"] = OrNull(r.Get("pid")),
                    ["live"] = live,
                    ["pane_id"] = OrNull(r.Get("pane_id")),
                    ["team_file"] = OrNull(r.Get("team_file")),
                    ["team_files"] = OrNull(r.Get("team_files")),
                    ["at"] = OrNull(r.Get("at")),

                    // Each session's mod asks for every row every few seconds and rows outlive their sessions, so a dead one costs no store read.
                    ["adapter"] = box is null || !live ? "none" : AdapterState(box.Dir, nowMs),
                }));
        }

        return new JsonObject { ["registrations"] = Sorted(rows) };
    }

    public JsonObject Events(string? address)
    {
        var rows = new List<(string Key, JsonObject Row)>();
        foreach (var box in mailbox.ListBoxes())
        {
            if (!string.IsNullOrEmpty(address) && box.Address != address)
            {
                continue;
            }

            foreach (var e in mailbox.ReadEntries(box.Dir).Entries)
            {
                var from = e.Member("from") is { IsText: true } f ? f.AsString() : null;
                var kind = e.Member("kind") is { IsText: true } k ? k.AsString() : null;
                var id = e.Member("id") is { IsText: true } i ? i.AsString() : string.Empty;
                if (kind != "event" || from != "ahd" || mailbox.IsAcked(box.Dir, id))
                {
                    continue;
                }

                var requestId = e.Member("data") is { IsMap: true } data && data.AsObject().Get("request_id") is { IsTruthy: true } rid ? rid : JsonValue.Null;
                var created = e.Member("created");
                rows.Add((
                    $"{JsConvert.ToJsString(created)}\0{id}",
                    new JsonObject
                    {
                        ["address"] = box.Address,
                        ["id"] = OrNull(e.Member("id")),
                        ["event"] = OrNull(e.Member("event")),
                        ["to_session"] = OrNull(e.Member("to_session")),
                        ["request_id"] = requestId,
                        ["created"] = OrNull(created),
                    }));
            }
        }

        return new JsonObject { ["events"] = Sorted(rows) };
    }
}
