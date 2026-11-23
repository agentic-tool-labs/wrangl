using System.Globalization;
using Ah.Core.Json;

namespace Ah.Watch.Model;

/// <summary>
/// The status lane: one key per canonical hierarchy directory, one slice per key, <c>status:&lt;dirId&gt;</c>. A key is re-read with one
/// <c>status.read</c>, the stored <c>status.json</c> that every status-producing write already refreshed, so nothing is recomputed here.
/// The slice carries the whitelisted fields of that document, with the same shape; <c>as_of</c> is the latest of the document's own
/// timestamps, never the clock. The key set is the canonical directories themselves.
/// </summary>
internal sealed class StatusLane(IViewStateReader reader, Func<IReadOnlyList<string>> canonicalDirs, Action<string>? reread = null) : IModelLane, IQuietKeys
{
    public const string LaneName = "status";

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _readAt = new(StringComparer.Ordinal);

    /// <summary>The fields of a document node: a leaf field is copied whole, an object or array-of-objects field is filtered by its own node.</summary>
    private sealed class Node(params (string Name, Node? Child)[] fields)
    {
        public IReadOnlyList<(string Name, Node? Child)> Fields { get; } = fields;
    }

    private static readonly Node TimelineEntry = new(
        ("at", null), ("visible", null), ("tone", null), ("live", null), ("out", null), ("blocked", null), ("overdue", null), ("stalled", null), ("text", null), ("short", null));

    private static readonly Node Member = new(
        ("name", null), ("role", null), ("label", null), ("kind", null), ("route", null), ("session_id", null), ("live", null), ("activity", null), ("activity_at", null),
        ("blocked_by", null), ("focusable", null), ("stream", null), ("last_tool", null), ("last_tool_at", null), ("blocked_note", null), ("context_used", null),
        ("work_tree", null), ("branch", null), ("last_report_line", null), ("last_report_at", null));

    private static readonly Node DispatchState = new(("at", null), ("state", null), ("reason", null));

    private static readonly Node Dispatch = new(
        ("id", null), ("slug", null), ("to", null), ("to_name", null), ("member", null), ("label", null), ("eta", null), ("eta_ms", null), ("created", null),
        ("sent_at", null), ("checkins", null), ("reported_at", null), ("states", DispatchState),
        ("title", null), ("title_source", null), ("item", null), ("report_line", null));

    private static readonly Node PipelineItem = new(("slug", null), ("rounds", null), ("open", null), ("to", null));

    private static readonly Node Pipeline = new(("anchor_id", null), ("started", null), ("round_cap", null), ("waiting_on_user", null), ("items", PipelineItem));

    private static readonly Node Resource = new(("name", null), ("role", null), ("who", null), ("since", null), ("note", null), ("outside", null));

    private static readonly Node Team = new(("team", null), ("members", Member), ("dispatches", Dispatch), ("dispatches_truncated", null), ("pipeline", Pipeline), ("resources", Resource));

    private static readonly Node Owner = new(("sessions", null), ("teams", null), ("timeline", TimelineEntry));

    private static readonly Node Document = new(
        ("schema", null), ("written_at", null), ("expires_at", null), ("enabled", null), ("member_sessions", null), ("teams", Team), ("timeline", TimelineEntry), ("owners", Owner));

    /// <summary>The timestamp fields that are instants already past when written; a future instant (<c>expires_at</c>, a check-in time) never moves <c>as_of</c>.</summary>
    private static readonly HashSet<string> OwnTimestamps = new(["written_at", "at", "activity_at", "last_tool_at", "reported_at", "sent_at", "created", "started"], StringComparer.Ordinal);

    public string Name => LaneName;

    public static string SliceIdOf(string dir) => "status:" + ActivityLane.DirId(dir);

    public IReadOnlyList<string> SliceIds(string key) => [SliceIdOf(key)];

    public IReadOnlyList<string> Keys() => [.. canonicalDirs().Distinct(StringComparer.Ordinal)];

    /// <summary>The directories whose <c>status.json</c> has the stamp it had when last read: the slice is a function of that file alone.</summary>
    public IReadOnlySet<string> QuietKeys() => _readAt.Where(r => StampOf(r.Key) == r.Value).Select(static r => r.Key).ToHashSet(StringComparer.Ordinal);

    private string? StampOf(string dir)
    {
        try
        {
            return reader.Read("status", "stamp", dir, new JsonObject { ["dir"] = dir }).Member("stamp") is { IsText: true } stamp ? stamp.AsString() : null;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return null;
        }
    }

    public LaneResult Reread(string key, long nowMs)
    {
        var id = SliceIdOf(key);
        if (!canonicalDirs().Contains(key, StringComparer.Ordinal))
        {
            _ = _readAt.TryRemove(key, out _);
            return new LaneResult([new SliceChange(id, null)]) { Record = LaneRecord.Absent };
        }

        // taken before the read, and kept only after it parsed: a write in between costs one more read, never a stale slice
        var stamp = StampOf(key);
        var document = reader.Read("status", "read", key, new JsonObject { ["dir"] = key });
        if (stamp is null)
        {
            _ = _readAt.TryRemove(key, out _);
        }
        else
        {
            _readAt[key] = stamp;
        }

        if (!document.IsMap)
        {
            return new LaneResult([new SliceChange(id, null)]) { Record = LaneRecord.Absent };
        }

        var body = Filter(document, Document);
        reread?.Invoke(key);
        return new LaneResult([new SliceChange(id, JsonText.ToUtf8(body, 0), AsOf(body))]) { Record = new LaneRecord(body, AsOf(body)) };
    }

    /// <summary>The latest instant among the document's own past timestamps in epoch ms; 0 when it has none.</summary>
    internal static long AsOf(JsonValue body)
    {
        var latest = 0L;
        Walk(body);
        return latest;

        void Walk(JsonValue value)
        {
            if (value.IsMap)
            {
                var map = value.AsObject();
                foreach (var name in map.Keys)
                {
                    var child = map[name];
                    if (child.IsText && OwnTimestamps.Contains(name))
                    {
                        if (DateTimeOffset.TryParse(child.AsString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var instant))
                        {
                            latest = Math.Max(latest, instant.ToUnixTimeMilliseconds());
                        }
                    }
                    else if (name != "states")
                    {
                        Walk(child);
                    }
                }
            }
            else if (value.IsArray)
            {
                foreach (var item in value.AsArray())
                {
                    Walk(item);
                }
            }
        }
    }

    private static JsonValue Filter(JsonValue value, Node node)
    {
        var result = new JsonObject();
        foreach (var (name, child) in node.Fields)
        {
            if (value.Member(name) is not { } field)
            {
                continue;
            }

            result[name] = child is null ? field : FilterChild(field, child);
        }

        return JsonValue.FromObject(result);
    }

    private static JsonValue FilterChild(JsonValue value, Node node)
    {
        if (value.IsMap)
        {
            return Filter(value, node);
        }

        if (value.IsArray)
        {
            var array = new JsonArray();
            foreach (var item in value.AsArray())
            {
                array.Add(item.IsMap ? Filter(item, node) : item);
            }

            return JsonValue.FromArray(array);
        }

        return value;
    }
}
