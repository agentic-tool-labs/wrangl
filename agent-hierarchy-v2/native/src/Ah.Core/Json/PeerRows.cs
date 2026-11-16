using System.Globalization;

namespace Ah.Core.Json;

/// <summary>What the database keeps beside the verbatim text of one peer-log row.</summary>
/// <param name="Type"><c>type</c> when it is text.</param>
/// <param name="SessionId"><c>session_id</c> when it is text.</param>
/// <param name="RequestId"><c>request_id</c> when it is text.</param>
/// <param name="Task"><c>task</c> when it is text.</param>
/// <param name="From"><c>from</c> when it is text.</param>
/// <param name="AgentId"><c>agent_id</c> when it is text.</param>
/// <param name="OpId"><c>op_id</c> when it is non-empty text: the key the log deduplicates on.</param>
/// <param name="TsMs">The row's time in Unix milliseconds; null for an undated row.</param>
/// <param name="TypeTruthy">Whether the row is truthy and its <c>type</c> is truthy, the rows latest-by-key skips.</param>
/// <param name="PeerKey">The latest-by-key key: the JS strings of <c>session_id</c>, <c>task</c> and <c>from</c> joined by spaces.</param>
/// <param name="FromName">The JS string of <c>from_name</c> when the row has the member, else null.</param>
public readonly record struct PeerColumns(
    string? Type,
    string? SessionId,
    string? RequestId,
    string? Task,
    string? From,
    string? AgentId,
    string? OpId,
    long? TsMs,
    bool TypeTruthy,
    string PeerKey,
    string? FromName);

/// <summary>
/// A filter over the peer records. <paramref name="Field"/> is <c>session_id</c>, <c>subject</c>, <c>from_name</c> or null for none, and a
/// row passes when the JS string of that member is <paramref name="Value"/>; <paramref name="Types"/> null admits any type, and
/// otherwise the row's <c>type</c> must be text in the set; <paramref name="TaskPrefix"/> null admits any task. <paramref name="Values"/>
/// is the many-valued <paramref name="Value"/>: when it is set, the JS string of the member must be one of them. A query sets one of
/// the two.
/// </summary>
public sealed record PeerQuery(string? Field = null, string? Value = null, IReadOnlySet<string>? Types = null, string? TaskPrefix = null, IReadOnlySet<string>? Values = null);

/// <summary>A peer record with its place in the log.</summary>
public readonly record struct PeerRowAt(long Seq, JsonValue Row);

/// <summary>
/// Reads of the peer log that a backing other than the file answers. Every row is a JSON object, parsed from the verbatim text of its
/// log line, in log order.
/// </summary>
public interface IPeerRecordSource
{
    /// <summary>Every record, each distinct <c>op_id</c> once.</summary>
    IReadOnlyList<JsonValue> ReadAll();

    /// <summary>The records that pass <see cref="PeerRows.Matches"/> for <paramref name="query"/>.</summary>
    IReadOnlyList<JsonValue> Rows(PeerQuery query);

    /// <summary><see cref="PeerRows.LatestByKey"/> over the records whose key can belong to <paramref name="sessionId"/>.</summary>
    IReadOnlyList<JsonValue> LatestByKeyFor(JsonValue? sessionId);

    /// <summary>The records after position <paramref name="cursor"/>, in log order; the position of the last one is the next cursor.</summary>
    IReadOnlyList<PeerRowAt> Since(long cursor);

    /// <summary>The change counter of the peer log: it differs after any write.</summary>
    long Version();

    /// <summary>How many records the log holds, or -1 when this source cannot say.</summary>
    long Count() => -1;

    /// <summary>Drops the rows this source holds parsed, if any (the on-demand rebuild).</summary>
    void ForgetHeld()
    {
    }
}

/// <summary>The rules of the peer log that every backing of it shares, so the file and the database cannot disagree on them.</summary>
public static class PeerRows
{
    /// <summary>The statement that stores one record; <see cref="InsertArgs"/> gives its arguments. A row whose <c>op_id</c> is already stored is left out.</summary>
    public static string InsertSql =>
        "INSERT OR IGNORE INTO peer_records(type, session_id, request_id, task, \"from\", agent_id, op_id, ts_ms, raw, type_truthy, peer_key, from_name) VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12)";

    /// <summary>The arguments of <see cref="InsertSql"/> for a record with these columns and this verbatim text.</summary>
    public static object?[] InsertArgs(PeerColumns c, byte[] raw) =>
        [c.Type, c.SessionId, c.RequestId, c.Task, c.From, c.AgentId, c.OpId, c.TsMs, raw, c.TypeTruthy ? 1L : 0L, c.PeerKey, c.FromName];

    /// <summary>Whether a parsed log line is a record: only an object is. A null, a scalar, an array or a line that is not JSON is not kept.</summary>
    public static bool IsRecord(JsonValue row) => row.IsMap;

    /// <summary>The latest-by-key key of a row: <c>String(session_id) String(task) String(from)</c>, compared ordinally.</summary>
    public static string KeyOf(JsonValue row) =>
        $"{JsConvert.ToJsString(row.Member("session_id"))} {JsConvert.ToJsString(row.Member("task"))} {JsConvert.ToJsString(row.Member("from"))}";

    /// <summary>Whether latest-by-key skips the row: the row is truthy and so is its <c>type</c>.</summary>
    public static bool SkippedByType(JsonValue row) => row.IsTruthy && (row.Member("type")?.IsTruthy ?? false);

    /// <summary>The time of a row from <c>ts</c>, else <c>created</c>, in Unix milliseconds; null when neither is a parseable text.</summary>
    public static long? TimeMs(JsonValue row)
    {
        var text = row.Member("ts")?.NonEmptyText ?? row.Member("created")?.NonEmptyText;
        return text is not null && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
            ? at.ToUnixTimeMilliseconds()
            : null;
    }

    public static PeerColumns ColumnsOf(JsonValue row) => new(
        TextOf(row, "type"),
        TextOf(row, "session_id"),
        TextOf(row, "request_id"),
        TextOf(row, "task"),
        TextOf(row, "from"),
        TextOf(row, "agent_id"),
        row.Member("op_id")?.NonEmptyText,
        TimeMs(row),
        SkippedByType(row),
        KeyOf(row),
        row.Member("from_name") is { } fromName ? JsConvert.ToJsString(fromName) : null);

    /// <summary>
    /// The first-seen order of the keys, each with the last row that has it. A row that is truthy with a truthy <c>type</c> is skipped.
    /// A JSON <c>null</c> row throws, as reading <c>session_id</c> of it does in JavaScript; the log never holds one.
    /// </summary>
    public static List<JsonValue> LatestByKey(IEnumerable<JsonValue> rows)
    {
        var order = new List<string>();
        var byKey = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (SkippedByType(row))
            {
                continue;
            }

            if (row.IsNull)
            {
                throw new InvalidOperationException("Cannot read properties of null (reading 'session_id')");
            }

            var key = KeyOf(row);
            if (!byKey.ContainsKey(key))
            {
                order.Add(key);
            }

            byKey[key] = row;
        }

        return [.. order.Select(k => byKey[k])];
    }

    /// <summary>
    /// The rows of <see cref="LatestByKey"/> that are pending for <paramref name="sessionId"/>: a <c>session_id</c> member that strictly
    /// equals it (a null argument matches nothing) and a <c>status</c> that is the text <c>pending</c>.
    /// </summary>
    public static List<JsonValue> PendingOf(IEnumerable<JsonValue> candidateRows, JsonValue? sessionId) =>
        [.. LatestByKey(candidateRows).Where(r =>
            r.Member("session_id") is { } rs && sessionId is { } sid && StrictEquals(rs, sid)
            && r.Member("status") is { IsText: true } status && status.AsString() == "pending")];

    /// <summary>JavaScript <c>===</c> over two JSON values: the same kind and the same text, number (NaN never equal) or bool; null equals null; arrays and objects never.</summary>
    public static bool StrictEquals(JsonValue a, JsonValue b) =>
        a.Kind == b.Kind && (a.IsText ? a.AsString() == b.AsString() : a.IsNumber ? a.AsNumber() == b.AsNumber() : a.IsBool ? a.AsBool() == b.AsBool() : a.IsNull);

    /// <summary>
    /// The keys that can belong to <paramref name="sessionId"/>: every <see cref="KeyOf"/> that starts with its JS string and a space is
    /// at or above <c>Low</c> and below <c>High</c>, the byte after the space, so a byte-ordered range is exact for prefix membership.
    /// </summary>
    public static (string Low, string High) KeyRangeOf(JsonValue? sessionId)
    {
        var text = JsConvert.ToJsString(sessionId);
        return (text + " ", text + "!");
    }

    /// <summary>Whether the row passes the superset filter of <paramref name="query"/>; each reader's own test then runs over what passes.</summary>
    public static bool Matches(JsonValue row, PeerQuery query) =>
        row.IsMap
        && (query.Field is null || (query.Values is { } values ? values.Contains(JsConvert.ToJsString(row.Member(query.Field))) : JsConvert.ToJsString(row.Member(query.Field)) == query.Value))
        && (query.Types is null || (row.Member("type") is { IsText: true } type && query.Types.Contains(type.AsString())))
        && (query.TaskPrefix is null || JsConvert.ToJsString(row.Member("task")).StartsWith(query.TaskPrefix, StringComparison.Ordinal));

    /// <summary>
    /// Whether rows whose <c>request_id</c> reads as <paramref name="jsString"/> are exactly the rows whose stored <c>request_id</c> column
    /// equals it. The column holds the member only when it is text, so a string that a missing member, a null, a bool, a number or an
    /// array also reads as (<c>undefined</c>, <c>null</c>, <c>true</c>, <c>7</c>, <c>1,2</c>, the empty string) is not one the column can answer.
    /// A one-element array of text reads as its element, which this cannot tell from the text; request ids are written as text.
    /// </summary>
    public static bool RequestIdColumnAnswers(string jsString) =>
        jsString.Length > 0
        && jsString is not ("undefined" or "null" or "true" or "false")
        && !jsString.Contains(',', StringComparison.Ordinal)
        && !jsString.Contains('[', StringComparison.Ordinal)
        && !double.TryParse(jsString, NumberStyles.Float, CultureInfo.InvariantCulture, out _);

    private static string? TextOf(JsonValue row, string name) => row.Member(name) is { IsText: true } text ? text.AsString() : null;
}
