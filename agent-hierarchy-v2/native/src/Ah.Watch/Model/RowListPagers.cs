using Ah.Core.Json;

namespace Ah.Watch.Model;

/// <summary>A row of a list whose rows all sit in one published slice body, with the key its cursor is made of.</summary>
internal readonly record struct KeyedRow(string[] Key, JsonValue Row);

/// <summary>
/// Pages rows that are already in list order (newest first) by keyset: the cursor is the row's key, and a page is the rows on one side of it, so
/// an append at the head moves no page. The one windowing rule for the lists whose rows the published slice holds.
/// </summary>
internal static class KeysetRows
{
    public static ListResult Page(ListRequest request, long version, int parts, IReadOnlyList<KeyedRow> rows, Comparison<string[]> compare)
    {
        if (!TryKey(request.After, parts, out var after) || !TryKey(request.Before, parts, out var before))
        {
            return new ListResult.Invalid("bad_cursor");
        }

        List<KeyedRow> page;
        var hasMore = false;
        if (before is not null)
        {
            var newer = rows.Where(r => compare(r.Key, before) > 0).ToList();
            page = newer.Count > request.Limit ? newer.GetRange(newer.Count - request.Limit, request.Limit) : newer;
        }
        else
        {
            var older = after is null ? rows : [.. rows.Where(r => compare(r.Key, after) < 0)];
            page = [.. older.Take(request.Limit)];
            hasMore = older.Count > request.Limit;
        }

        var head = page.Count > 0 ? ListCursor.Encode(page[0].Key) : null;
        var tail = page.Count > 0 ? ListCursor.Encode(page[^1].Key) : null;
        var next = before is not null ? tail
            : hasMore ? tail
            : page.Count == 0 && after is not null ? request.After
            : null;
        var prev = head ?? (before is not null ? request.Before : null);
        return new ListResult.Found(new ListPage(version, rows.Count, head, tail, [.. page.Select(static r => r.Row)], next, prev));
    }

    private static bool TryKey(string? cursor, int parts, out string[]? key)
    {
        key = null;
        if (cursor is null)
        {
            return true;
        }

        key = ListCursor.Decode(cursor, parts);
        return key is not null;
    }
}

/// <summary>
/// The pager of <c>list:open:team:&lt;dirId&gt;/&lt;team&gt;</c>: the team's open exchanges, each row <c>{id, to_member, from_member}</c> as the
/// published list holds it, newest first. The cursor is the exchange id. <c>member=</c> keeps the rows whose either end is that member; the ends were
/// resolved by the same party resolver when the list was composed, so no second resolution happens here and nothing is read cold.
/// </summary>
/// <param name="slices">The published state; a snapshot read, safe from any thread.</param>
/// <param name="readCold">The exchange body of (directory id, exchange id) read through <c>msgs.read</c>, or null when it is gone.</param>
internal sealed class OpenListPager(Func<ModelState> slices, Func<string, string, JsonValue?> readCold) : IListPager
{
    public string Prefix => OpenExchangeListComposer.Prefix + ":";

    public IReadOnlyCollection<string> Filters { get; } = ["member"];

    public ValueTask<ListResult> PageAsync(ListRequest request, CancellationToken cancellation)
    {
        var state = slices();
        if (!state.Slices.TryGetValue(request.SliceId, out var list) || OpenOf(list) is not { } open)
        {
            return ValueTask.FromResult<ListResult>(new ListResult.NotFound());
        }

        var key = request.SliceId[Prefix.Length..];
        var slash = key.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0)
        {
            return ValueTask.FromResult<ListResult>(new ListResult.NotFound());
        }

        if (ListMember.Parse(request, key[..slash], key[(slash + 1)..], out var member) is { } bad)
        {
            return ValueTask.FromResult<ListResult>(bad);
        }

        var wanted = member is null ? null : request.Param("member");
        var dirId = key[..slash];
        var rows = new List<KeyedRow>(open.Count);
        foreach (var row in open)
        {
            if (row.Member("id") is { IsText: true } id
                && ExchangeIdOf(id.AsString()) is { } exchange
                && (wanted is null || EndIs(row, "to_member", wanted) || EndIs(row, "from_member", wanted)))
            {
                rows.Add(new KeyedRow([exchange], row));
            }
        }

        var result = KeysetRows.Page(request, state.Publish, 1, rows, static (a, b) => string.CompareOrdinal(a[0], b[0]));
        return ValueTask.FromResult(result is ListResult.Found found ? Materialize(found, state, dirId, cancellation) : result);
    }

    /// <summary>Replaces each open entry of the page by the exchange item slice it names, from the published map or else read cold.</summary>
    private ListResult.Found Materialize(ListResult.Found found, ModelState state, string dirId, CancellationToken cancellation)
    {
        var rows = new List<JsonValue>(found.Page.Rows.Count);
        var cold = 0;
        foreach (var entry in found.Page.Rows)
        {
            cancellation.ThrowIfCancellationRequested();
            var id = ExchangeIdOf(entry.Member("id")!.Value.AsString())!;
            JsonValue? body;
            if (state.Slices.TryGetValue("exchange:" + dirId + "/" + id, out var held))
            {
                body = JsonText.Parse(held.Body);
            }
            else
            {
                cold++;
                body = readCold(dirId, id);
            }

            if (body is { } b)
            {
                rows.Add(ExchangeListPager.RowOf(dirId, id, b));
            }
        }

        return new ListResult.Found(found.Page with { Rows = rows, ColdReads = cold });
    }

    private static bool EndIs(JsonValue row, string end, string wanted) => row.Member(end) is { IsText: true } id && id.AsString() == wanted;

    /// <summary>The exchange id of an <c>exchange:&lt;dirId&gt;/&lt;id&gt;</c> slice id.</summary>
    private static string? ExchangeIdOf(string sliceId)
    {
        var slash = sliceId.LastIndexOf('/');
        return slash >= 0 && slash < sliceId.Length - 1 ? sliceId[(slash + 1)..] : null;
    }

    private static List<JsonValue>? OpenOf(Slice list)
    {
        try
        {
            return JsonText.Parse(list.Body).Member("open") is { IsArray: true } open ? [.. open.AsArray()] : null;
        }
        catch (JsonParseException)
        {
            return null;
        }
    }
}

/// <summary>
/// The pager of <c>list:plan-changes:&lt;dirId&gt;/&lt;team&gt;</c>: the changes the plan events made, each row <c>{at, seq, stream, item, kind, from, to,
/// by}</c> as the published list holds it, newest first. The cursor is <c>(at, stream, seq)</c>: <c>at</c> and <c>stream</c> compare as text, <c>seq</c>
/// as a number, newest first, because seq is only unique within a stream's log. <c>item=&lt;item id&gt;</c> keeps the changes of that item.
/// </summary>
/// <param name="slices">The published state; a snapshot read, safe from any thread.</param>
/// <param name="held">The progress lane's complete, newest-first changes of a slice suffix (<c>&lt;dirId&gt;/&lt;team&gt;</c>), so rows past the slice's cap are reachable.</param>
internal sealed class PlanChangesPager(Func<ModelState> slices, Func<string, JsonArray?> held) : IListPager
{
    public const string ListPrefix = "list:plan-changes:";

    public string Prefix => ListPrefix;

    public IReadOnlyCollection<string> Filters { get; } = ["item"];

    public ValueTask<ListResult> PageAsync(ListRequest request, CancellationToken cancellation)
    {
        var state = slices();
        if (!state.Slices.ContainsKey(request.SliceId) || held(request.SliceId[ListPrefix.Length..]) is not { } changes)
        {
            return ValueTask.FromResult<ListResult>(new ListResult.NotFound());
        }

        var item = request.Param("item");
        if (item is { Length: 0 })
        {
            return ValueTask.FromResult<ListResult>(new ListResult.Invalid("item"));
        }

        var rows = new List<KeyedRow>(changes.Count);
        foreach (var change in changes)
        {
            if (KeyOf(change) is { } key && (item is null || change.Member("item") is { IsText: true } id && id.AsString() == item))
            {
                rows.Add(new KeyedRow(key, change));
            }
        }

        return ValueTask.FromResult(KeysetRows.Page(request, state.Publish, 3, rows, Compare));
    }

    private static int Compare(string[] a, string[] b)
    {
        var byAt = string.CompareOrdinal(a[0], b[0]);
        if (byAt != 0)
        {
            return byAt;
        }

        var byStream = string.CompareOrdinal(a[1], b[1]);
        if (byStream != 0)
        {
            return byStream;
        }

        return long.TryParse(a[2], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var x)
            && long.TryParse(b[2], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var y)
            ? x.CompareTo(y)
            : string.CompareOrdinal(a[2], b[2]);
    }

    internal static string[]? KeyOf(JsonValue change) =>
        change.Member("at") is { IsText: true } at && change.Member("stream") is { IsText: true } stream && change.Member("seq") is { IsNumber: true } seq
            ? [at.AsString(), stream.AsString(), ((long)seq.AsNumber()).ToString(System.Globalization.CultureInfo.InvariantCulture)]
            : null;
}
