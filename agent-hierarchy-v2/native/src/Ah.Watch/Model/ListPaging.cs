using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Ah.Core.Json;

namespace Ah.Watch.Model;

/// <summary>
/// One <c>GET /v1/list/&lt;slice-id&gt;</c> request, parsed. Every list is newest first. <see cref="After"/> is the cursor of a row to page past
/// toward older rows; <see cref="Before"/> toward newer rows (the newer rows nearest the cursor, still returned newest first). At most one is set;
/// neither means the newest page. <see cref="Query"/> holds every other query parameter by name, so a pager reads the filters
/// it owns (<c>expand</c>, <c>member</c>, <c>item</c>, and any a list kind adds) without the route knowing them.
/// </summary>
internal sealed record ListRequest(string SliceId, string? After, string? Before, int Limit, IReadOnlyDictionary<string, string> Query)
{
    public const int MaxLimit = 200;

    /// <summary>The page size when the request names none.</summary>
    public const int DefaultLimit = 50;

    public string? Param(string name) => Query.TryGetValue(name, out var v) ? v : null;

    /// <summary>
    /// Reads the query of a list request: <c>limit</c> is an integer in 1..<see cref="MaxLimit"/> (<see cref="DefaultLimit"/> when absent), and
    /// <c>after</c> and <c>before</c> are not both set. The route and every pager share this check; the cursors are decoded by the pager that owns them.
    /// </summary>
    public static bool TryParse(string sliceId, IReadOnlyDictionary<string, string> query, [NotNullWhen(true)] out ListRequest? request, [NotNullWhen(false)] out ListResult.Invalid? invalid)
    {
        request = null;
        invalid = null;
        var limit = DefaultLimit;
        if (query.TryGetValue("limit", out var raw)
            && (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit is < 1 or > MaxLimit))
        {
            invalid = new ListResult.Invalid("limit");
            return false;
        }

        query.TryGetValue("after", out var after);
        query.TryGetValue("before", out var before);
        if (after is not null && before is not null)
        {
            invalid = new ListResult.Invalid("bad_cursor");
            return false;
        }

        request = new ListRequest(sliceId, after, before, limit, query);
        return true;
    }
}

/// <summary>
/// One page in the <c>/list</c> envelope: <c>{version, total, head_cursor, tail_cursor, rows, next, prev}</c>. Cursors are opaque to the client.
/// <see cref="HeadCursor"/> is the newest row's cursor and <see cref="TailCursor"/> the oldest's. <see cref="Next"/> is the cursor to pass as
/// <c>after=</c> (the last row's, null when nothing older exists); <see cref="Prev"/> the cursor to pass as <c>before=</c> (the first row's, never
/// null on a non-empty page). An empty page echoes the request's cursor on the side asked: <c>before=c</c> gives prev c, <c>after=c</c> gives next c. <see cref="ColdReads"/> counts the reads this page made
/// through its lane's read op or the file system, so a gate can assert reads per page; it is not part of the envelope.
/// </summary>
internal sealed record ListPage(
    long Version,
    long? Total,
    string? HeadCursor,
    string? TailCursor,
    IReadOnlyList<JsonValue> Rows,
    string? Next,
    string? Prev,
    int ColdReads = 0)
{
    private static readonly string[] Reserved = ["version", "total", "head_cursor", "tail_cursor", "rows", "next", "prev"];

    private readonly IReadOnlyList<(string Name, JsonValue Value)>? _extra;

    /// <summary>Extra top-level members a list kind adds to the envelope (for example a seed); null for none. A name of the envelope itself is refused.</summary>
    public IReadOnlyList<(string Name, JsonValue Value)>? Extra
    {
        get => _extra;
        init => _extra = value is not null && value.Any(e => Reserved.Contains(e.Name, StringComparer.Ordinal))
            ? throw new ArgumentException("an envelope member cannot be redefined", nameof(value))
            : value;
    }

    public JsonValue ToJson()
    {
        var rows = new JsonArray();
        foreach (var row in Rows)
        {
            rows.Add(row);
        }

        var o = new JsonObject
        {
            ["version"] = Version,
            ["total"] = Total is { } t ? t : JsonValue.Null,
            ["head_cursor"] = HeadCursor is null ? JsonValue.Null : HeadCursor,
            ["tail_cursor"] = TailCursor is null ? JsonValue.Null : TailCursor,
            ["rows"] = JsonValue.FromArray(rows),
            ["next"] = Next is null ? JsonValue.Null : Next,
            ["prev"] = Prev is null ? JsonValue.Null : Prev,
        };
        if (Extra is not null)
        {
            foreach (var (name, value) in Extra)
            {
                o[name] = value;
            }
        }

        return JsonValue.FromObject(o);
    }
}

/// <summary>What a pager answered: a page, or why there is none.</summary>
internal abstract record ListResult
{
    private ListResult()
    {
    }

    /// <summary>The page, 200.</summary>
    internal sealed record Found(ListPage Page) : ListResult;

    /// <summary>A body that is not a page (one expanded entry), 200.</summary>
    internal sealed record Document(JsonValue Body) : ListResult;

    /// <summary>The prefix is served but this id names nothing (404).</summary>
    internal sealed record NotFound : ListResult;

    /// <summary>The request is malformed for this list kind: a cursor that does not decode, a filter value out of range (400).</summary>
    internal sealed record Invalid(string Reason, string? Key = null) : ListResult;
}

/// <summary>
/// The pager of one list kind, registered by the lane that owns it. It serves a page from what its lane has published into concurrent
/// structures, or cold through the lane's own read op. It runs on a request thread: it never reads the projector's record store, and a cold page
/// is never published as a slice.
/// </summary>
internal interface IListPager
{
    /// <summary>The slice-id prefix this pager owns, ending in <c>:</c> (for example <c>list:transcript:</c>). The rest of the id is the list's key.</summary>
    string Prefix { get; }

    /// <summary>The query keys this kind accepts besides <c>after</c>, <c>before</c> and <c>limit</c>; any other key is refused before a read.</summary>
    IReadOnlyCollection<string> Filters { get; }

    /// <summary>
    /// Serves the page <paramref name="request"/> names (its limit and cursor pairing already checked by <see cref="ListRequest.TryParse"/>).
    /// Throws only for faults; a bad request is <see cref="ListResult.Invalid"/>. A pager that does file or lane reads awaits them, so a slow
    /// cold read holds no thread.
    /// </summary>
    ValueTask<ListResult> PageAsync(ListRequest request, CancellationToken cancellation);
}

/// <summary>The pagers of the lanes, looked up by slice-id prefix. Registration happens at start; lookups are lock-free afterwards.</summary>
internal sealed class ListPagerRegistry
{
    private readonly Lock _gate = new();
    private IReadOnlyList<IListPager> _pagers = [];

    /// <summary>
    /// Adds a pager. A prefix that is empty, does not end in <c>:</c>, or is already registered (or nests one that is) throws. A prefix names
    /// one list kind: <c>list:exchanges:dir:</c> and <c>list:exchanges:team:</c> are separate pagers, never one <c>list:exchanges:</c>, which
    /// would nest both.
    /// </summary>
    public void Register(IListPager pager)
    {
        var prefix = pager.Prefix;
        if (prefix.Length < 2 || prefix[^1] != ':')
        {
            throw new ArgumentException("a pager prefix ends in ':'", nameof(pager));
        }

        lock (_gate)
        {
            if (_pagers.Any(p => p.Prefix.StartsWith(prefix, StringComparison.Ordinal) || prefix.StartsWith(p.Prefix, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException("list prefix already served: " + prefix);
            }

            Volatile.Write(ref _pagers, [.. _pagers, pager]);
        }
    }

    /// <summary>The pager whose prefix <paramref name="sliceId"/> starts with and which leaves a non-empty key, else null (an unknown prefix is a 404).</summary>
    public IListPager? Find(string sliceId)
    {
        foreach (var pager in Volatile.Read(ref _pagers))
        {
            if (sliceId.Length > pager.Prefix.Length && sliceId.StartsWith(pager.Prefix, StringComparison.Ordinal))
            {
                return pager;
            }
        }

        return null;
    }
}
