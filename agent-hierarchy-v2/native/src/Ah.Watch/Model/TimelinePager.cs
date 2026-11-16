using System.Globalization;
using Ah.Core.Json;

namespace Ah.Watch.Model;

/// <summary>
/// The pager of <c>list:timeline:&lt;dirId&gt;</c>. Rows are a directory's state transitions, newest first by <c>seq</c>, and the cursor is the
/// opaque seq. The window is <c>from_ms</c> to <c>to_ms</c>, integer epoch milliseconds and half-open, defaulting to the retention up to now.
/// The first page of a window (no cursor) also carries <c>seed</c>: for each subject with a row older than <c>from_ms</c>, that row's state and time.
/// Pages are read on request threads through <see cref="ITimelinePages"/> and are never published.
/// </summary>
/// <param name="pages">The paged reads of the timeline file.</param>
/// <param name="retentionDays">The configured retention, in days.</param>
/// <param name="nowMs">Epoch milliseconds now.</param>
/// <param name="version">The publish counter of the published state.</param>
internal sealed class TimelinePager(ITimelinePages pages, Func<int> retentionDays, Func<long> nowMs, Func<long> version) : IListPager
{
    public const string ListPrefix = "list:timeline:";

    private const long DayMs = 86_400_000;

    public string Prefix => ListPrefix;

    public IReadOnlyCollection<string> Filters { get; } = ["from_ms", "to_ms"];

    public ValueTask<ListResult> PageAsync(ListRequest request, CancellationToken cancellation)
    {
        var dirId = request.SliceId[Prefix.Length..];
        var now = nowMs();
        var from = now - (TimelineLane.ClampDays(retentionDays()) * DayMs);
        var to = now;
        if (!TryMs(request, "from_ms", ref from))
        {
            return ValueTask.FromResult<ListResult>(new ListResult.Invalid("from_ms"));
        }

        if (!TryMs(request, "to_ms", ref to))
        {
            return ValueTask.FromResult<ListResult>(new ListResult.Invalid("to_ms"));
        }

        if (!TrySeq(request.After, out var after) || !TrySeq(request.Before, out var before))
        {
            return ValueTask.FromResult<ListResult>(new ListResult.Invalid("bad_cursor"));
        }

        cancellation.ThrowIfCancellationRequested();
        var queries = 0;
        var open = from < to;
        IReadOnlyList<TimelineRow> rows = [];
        if (open)
        {
            queries++;
            rows = pages.Page(dirId, from, to, after ?? before ?? 0, older: before is null, request.Limit);
        }

        TimelineRow? head = rows.Count > 0 ? rows[0] : null;
        TimelineRow? tail = rows.Count > 0 ? rows[^1] : null;
        var hasOlder = false;
        if (tail is { } oldest)
        {
            queries++;
            hasOlder = pages.Page(dirId, from, to, oldest.Seq, older: true, 1).Count > 0;
        }

        long? total = null;
        if (open)
        {
            queries++;
            total = pages.CountIn(dirId, from, to);
        }

        var page = new ListPage(
            version(),
            total ?? 0,
            head is { } h ? Cursor(h.Seq) : null,
            tail is { } l ? Cursor(l.Seq) : null,
            [.. rows.Select(RowJson)],
            tail is { } last ? (hasOlder ? Cursor(last.Seq) : null) : (after is { } a ? Cursor(a) : null),
            head is { } first ? Cursor(first.Seq) : (before is { } b ? Cursor(b) : null),
            queries);
        if (after is null && before is null)
        {
            page = page with { Extra = [("seed", SeedJson(pages.Seeds(dirId, from)))], ColdReads = queries + 1 };
        }

        return ValueTask.FromResult<ListResult>(new ListResult.Found(page));
    }

    private static string Cursor(long seq) => ListCursor.Encode(seq.ToString(CultureInfo.InvariantCulture));

    private static bool TrySeq(string? cursor, out long? seq)
    {
        seq = null;
        if (cursor is null)
        {
            return true;
        }

        if (ListCursor.Decode(cursor, 1) is { } parts && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0)
        {
            seq = value;
            return true;
        }

        return false;
    }

    internal static bool TryMs(ListRequest request, string name, ref long value)
    {
        if (request.Param(name) is not { } text)
        {
            return true;
        }

        if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
        {
            return false;
        }

        value = parsed;
        return true;
    }

    private static JsonValue RowJson(TimelineRow row) => JsonValue.FromObject(new JsonObject
    {
        ["seq"] = row.Seq,
        ["subject"] = row.Subject,
        ["from_state"] = row.From is null ? JsonValue.Null : row.From,
        ["to_state"] = row.To,
        ["at"] = row.AtMs,
    });

    private static JsonValue SeedJson(IReadOnlyList<TimelineSeed> seeds)
    {
        var array = new JsonArray();
        foreach (var seed in seeds)
        {
            array.Add(JsonValue.FromObject(new JsonObject { ["subject"] = seed.Subject, ["state"] = seed.State, ["at"] = seed.AtMs }));
        }

        return JsonValue.FromArray(array);
    }
}
