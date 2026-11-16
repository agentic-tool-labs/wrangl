using Ah.Core.Json;

namespace Ah.Hierarchy;

/// <summary>
/// Which rows of the peer log no reader can still need. A request's dispatch, seen, surfaced and report-acked rows are read together
/// (the readers ask whether a landed report was shown), so they leave together or not at all; the rows that say "latest per key"
/// keep their newest row.
/// </summary>
internal static class PeerLogRetention
{
    private const double DayMs = 86_400_000;

    /// <summary>The days a settled row is kept when the config names none.</summary>
    public const int DefaultDays = 14;

    /// <summary>A session whose newest row of any type is older than this and that is gone (no registration, or one proven dead) loses the rows its session alone reads. A quiet session that is still running keeps them.</summary>
    public const int IdleSessionDays = 3;

    private static readonly HashSet<string> OutageTypes = new(StringComparer.Ordinal) { "outage", "outage-ack", "outage-probe-failed" };

    /// <summary>
    /// Row types that no age rule drops, because a reader folds or counts the whole history of them with no time limit: the latest
    /// row per key (heard, idle-seen, watch-consumed), a count against the nudge budget (mail-nudge, file-nudge), an answer to "was this
    /// response sent" (sent), and the outage state machine and its acks (outage, outage-ack, outage-probe-failed).
    /// </summary>
    internal static readonly IReadOnlySet<string> KeptForever = new HashSet<string>(StringComparer.Ordinal)
    {
        "heard", "idle-seen", "watch-consumed", "mail-nudge", "file-nudge", "sent", "outage", "outage-ack", "outage-probe-failed",
    };

    private static readonly HashSet<string> ShownTypes = new(StringComparer.Ordinal) { "seen", "surfaced", "report-acked" };

    private sealed class Request
    {
        public bool Shown;
        public bool Unreadable;
        public double Newest = double.NegativeInfinity;
    }

    /// <summary>
    /// The log without the dropped rows, or null when nothing is dropped. The rows that carry a request id are read together, so they
    /// leave together or not at all: when the request is not in <paramref name="open"/>, was never reopened and its newest row is
    /// older than <paramref name="retentionDays"/> when the request is in <paramref name="closed"/> and shown, or older than twice
    /// that when it is in neither set (its pool is no longer listed). A closed request that was never shown stays: its report is still
    /// owed. Every other row goes when its own time is more than <paramref name="retentionDays"/> old, unless its type is in
    /// <see cref="KeptForever"/>. A turn, cli-root or subagent row goes when a later row of its key exists and it is more than a day
    /// old. A row of a gone session (no registration, or one proven dead; the idle rule is off unless the caller says which sessions run) with no row for <see cref="IdleSessionDays"/> days goes whatever its type, unless it carries a request id,
    /// names an open message, or is an outage row. A later row repeating an earlier <c>op_id</c> goes. A row that cannot be parsed
    /// or dated stays, and so does the order of the rest.
    /// </summary>
    public static string? Compact(string text, IReadOnlySet<string> closed, IReadOnlySet<string> open, double nowMs, int retentionDays, out int dropped) =>
        Compact(text, closed, open, nowMs, retentionDays, null, out dropped);

    /// <summary>
    /// <see cref="Compact(string, IReadOnlySet{string}, IReadOnlySet{string}, double, int, out int)"/> with the sessions that are not gone
    /// (a registration that is not proven dead): the idle-session rule drops rows only of a session outside that set, and null switches it off.
    /// </summary>
    public static string? Compact(string text, IReadOnlySet<string> closed, IReadOnlySet<string> open, double nowMs, int retentionDays, IReadOnlySet<string>? liveSessions, out int dropped)
    {
        var lines = text.Split('\n');
        var drop = DroppedLines(lines, closed, open, nowMs, retentionDays, liveSessions);
        dropped = drop.Count;
        return dropped == 0 ? null : string.Join('\n', lines.Where((_, i) => !drop.Contains(i)));
    }

    /// <summary>The retention days a compaction's arguments carry; <see cref="DefaultDays"/> when absent or not a positive whole number.</summary>
    public static int RetentionOf(JsonValue args) =>
        args.Member("retention_days") is { IsNumber: true } n && n.AsNumber() >= 1 && n.AsNumber() == Math.Floor(n.AsNumber()) && n.AsNumber() <= 3650 ? (int)n.AsNumber() : DefaultDays;

    /// <summary>The text members of the array <paramref name="name"/> of a compaction's arguments; an absent or non-array member is an empty set.</summary>
    public static HashSet<string> IdSet(JsonValue args, string name)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (args.Member(name) is { IsArray: true } listed)
        {
            foreach (var id in listed.AsArray())
            {
                if (id.IsText)
                {
                    _ = ids.Add(id.AsString());
                }
            }
        }

        return ids;
    }

    /// <summary>The sessions named by the <c>live_sessions</c> array of a compaction's arguments; null when the argument is absent, which leaves the idle-session rule off.</summary>
    public static HashSet<string>? LiveSessionsOf(JsonValue args) => args.Member("live_sessions") is { IsArray: true } ? IdSet(args, "live_sessions") : null;

    /// <summary>The indexes of the lines <see cref="Compact(string, IReadOnlySet{string}, IReadOnlySet{string}, double, int, IReadOnlySet{string}?, out int)"/> drops.</summary>
    public static HashSet<int> DroppedLines(IReadOnlyList<string> lines, IReadOnlySet<string> closed, IReadOnlySet<string> open, double nowMs, int retentionDays, IReadOnlySet<string>? liveSessions = null)
    {
        var rows = new JsonValue?[lines.Count];
        var requests = new Dictionary<string, Request>(StringComparer.Ordinal);
        var reopened = new HashSet<string>(StringComparer.Ordinal);
        var lastOfKey = new Dictionary<string, int>(StringComparer.Ordinal);
        var lastSeen = new Dictionary<string, double>(StringComparer.Ordinal);
        var repeats = new HashSet<int>();
        var operations = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < lines.Count; i++)
        {
            JsonValue row;
            try
            {
                row = JsonText.Parse(lines[i]);
            }
            catch (JsonParseException)
            {
                continue;
            }

            if (!row.IsMap)
            {
                continue;
            }

            rows[i] = row;
            if (row.Member("op_id")?.NonEmptyText is { } operation && !operations.Add(operation))
            {
                _ = repeats.Add(i);
            }

            if (row.Member("session_id")?.NonEmptyText is { } sessionId && TimeOf(row) is { } seenAt)
            {
                lastSeen[sessionId] = lastSeen.TryGetValue(sessionId, out var before) ? Math.Max(before, seenAt) : seenAt;
            }

            var type = row.Member("type")?.NonEmptyText;
            var requestId = row.Member("request_id")?.NonEmptyText;
            if (type is null)
            {
                continue;
            }

            if (type != "reopened" && KeyOf(row, type) is null && requestId is not null)
            {
                if (!requests.TryGetValue(requestId, out var request))
                {
                    request = new Request();
                    requests[requestId] = request;
                }

                if (TimeOf(row) is { } at)
                {
                    request.Newest = Math.Max(request.Newest, at);
                }
                else
                {
                    request.Unreadable = true;
                }

                request.Shown |= ShownTypes.Contains(type);
            }
            else if (type == "reopened" && requestId is not null)
            {
                _ = reopened.Add(requestId);
            }
            else if (KeyOf(row, type) is { } key)
            {
                lastOfKey[key] = i;
            }
        }

        var drop = new HashSet<int>();
        for (var i = 0; i < lines.Count; i++)
        {
            if (rows[i] is { } row && (repeats.Contains(i) || IdleSessionRow(row, open, lastSeen, liveSessions, nowMs) || Drops(row, i, closed, open, requests, reopened, lastOfKey, nowMs, retentionDays)))
            {
                _ = drop.Add(i);
            }
        }

        return drop;
    }

    private static bool Drops(JsonValue row, int index, IReadOnlySet<string> closed, IReadOnlySet<string> open, Dictionary<string, Request> requests, HashSet<string> reopened, Dictionary<string, int> lastOfKey, double nowMs, int retentionDays)
    {
        var type = row.Member("type")?.NonEmptyText;
        if (type is null || type == "reopened")
        {
            return false;
        }

        if (KeyOf(row, type) is { } key)
        {
            return lastOfKey.TryGetValue(key, out var last) && last != index && TimeOf(row) is { } stamp && stamp < nowMs - DayMs;
        }

        if (row.Member("request_id")?.NonEmptyText is { } requestId)
        {
            if (!requests.TryGetValue(requestId, out var request) || request.Unreadable || open.Contains(requestId) || reopened.Contains(requestId))
            {
                return false;
            }

            var settled = closed.Contains(requestId);
            if (settled && !request.Shown)
            {
                return false;
            }

            return request.Newest < nowMs - ((settled ? retentionDays : 2 * retentionDays) * DayMs);
        }

        return !KeptForever.Contains(type) && TimeOf(row) is { } at && at < nowMs - (retentionDays * DayMs);
    }

    private static bool IdleSessionRow(JsonValue row, IReadOnlySet<string> open, Dictionary<string, double> lastSeen, IReadOnlySet<string>? liveSessions, double nowMs) =>
        liveSessions is not null
        && row.Member("session_id")?.NonEmptyText is { } session
        && !liveSessions.Contains(session)
        && lastSeen.TryGetValue(session, out var last)
        && last < nowMs - (IdleSessionDays * DayMs)
        && row.Member("request_id")?.NonEmptyText is null
        && !(row.Member("msg_id")?.NonEmptyText is { } message && open.Contains(message))
        && !(row.Member("type")?.NonEmptyText is { } type && OutageTypes.Contains(type));

    private static string? KeyOf(JsonValue row, string type)
    {
        var session = row.Member("session_id")?.NonEmptyText;
        return type switch
        {
            "turn" or "cli-root" when session is not null => type + "\0" + session,
            "subagent" when session is not null => type + "\0" + session + "\0" + (row.Member("agent_id")?.NonEmptyText ?? "undefined"),
            _ => null,
        };
    }

    private static double? TimeOf(JsonValue row) => PeerRows.TimeMs(row);
}
