using Ah.Core.Json;

namespace Ah.Watch.Model;

/// <summary>
/// The pager of the team exchange lists (<c>list:exchanges:team:&lt;dirId&gt;/&lt;team&gt;</c> and <c>list:feed:team:…</c>). It pages the ids of the
/// published list slice, newest first, by keyset: the cursor is an exchange id, and ids sort in time order, so a page is the ids on one side of
/// the cursor and an append at the head moves no page. A row is the published <c>exchange:</c> slice body when the store holds it, else the
/// same body read cold through the lane's own read op; a cold row is never published.
/// <para>
/// <c>member=&lt;member id&gt;</c> keeps the exchanges that name the member, decided by <see cref="ExchangeFiling.PartiesOf"/> (the one party
/// resolver) over the team roster of the same published state the page reads; the record store is never touched. <c>with=&lt;member id&gt;</c>,
/// valid only beside <c>member</c> and naming a member of the same team, keeps the exchanges that name both, in either direction.
/// </para>
/// </summary>
/// <param name="prefix">The list prefix this pager owns, ending in <c>:</c>.</param>
/// <param name="slices">The published state; a snapshot read, safe from any thread.</param>
/// <param name="readCold">The exchange body of (directory id, exchange id) read through <c>msgs.read</c>, or null when it is gone.</param>
/// <param name="teamScoped">Whether the list ids end in <c>&lt;dirId&gt;/&lt;team&gt;</c> (a team's list) rather than a bare <c>&lt;dirId&gt;</c> (a directory's list).</param>
internal sealed class ExchangeListPager(string prefix, Func<ModelState> slices, Func<string, string, JsonValue?> readCold, bool teamScoped = true) : IListPager
{
    /// <summary>The most cold reads one page spends deciding which ids match a filter; past it the page ends early and its cursor says where to continue.</summary>
    internal const int MaxColdScansPerPage = 64;

    public string Prefix { get; } = prefix;

    public IReadOnlyCollection<string> Filters { get; } = ["member", "with"];

    public ValueTask<ListResult> PageAsync(ListRequest request, CancellationToken cancellation) => ValueTask.FromResult(Page(request, cancellation));

    private ListResult Page(ListRequest request, CancellationToken cancellation)
    {
        var state = slices();
        if (!state.Slices.TryGetValue(request.SliceId, out var list) || IdsOf(list) is not { } ids)
        {
            return new ListResult.NotFound();
        }

        var key = request.SliceId[Prefix.Length..];
        var slash = key.IndexOf('/', StringComparison.Ordinal);
        if (teamScoped ? slash <= 0 : key.Length == 0 || slash >= 0)
        {
            return new ListResult.NotFound();
        }

        var dirId = teamScoped ? key[..slash] : key;
        if (!TryCursor(request.After, out var after) || !TryCursor(request.Before, out var before))
        {
            return new ListResult.Invalid("bad_cursor");
        }

        if (ListMember.Parse(request, dirId, teamScoped ? key[(slash + 1)..] : null, out var member) is { } badMember)
        {
            return badMember;
        }

        (string DirId, string Team, string Member)? with = null;
        if (request.Param("with") is not null
            && (member is not { } paired || ListMember.Parse(request, dirId, teamScoped ? key[(slash + 1)..] : paired.Team, out with, "with") is not null))
        {
            return new ListResult.Invalid(ListMember.BadMember);
        }

        var roster = member is { } m ? state.Rosters.GetValueOrDefault((dirId, m.Team)) : null;
        var unknownMember = member is { } wanted
            && (roster is null || !roster.MemberNames.Contains(wanted.Member, StringComparer.Ordinal)
                || (with is { } other && !roster.MemberNames.Contains(other.Member, StringComparer.Ordinal)));
        var bodies = new Dictionary<string, JsonValue?>(StringComparer.Ordinal);
        var cold = 0;
        var scanCold = 0;
        var stopped = false;

        JsonValue? BodyOf(string id, bool scanning)
        {
            if (!bodies.TryGetValue(id, out var body))
            {
                cancellation.ThrowIfCancellationRequested();
                if (state.Slices.TryGetValue("exchange:" + dirId + "/" + id, out var held))
                {
                    body = JsonText.Parse(held.Body);
                }
                else if (scanning && scanCold >= MaxColdScansPerPage)
                {
                    stopped = true;
                    return null;
                }
                else
                {
                    cold++;
                    scanCold += scanning ? 1 : 0;
                    body = readCold(dirId, id);
                }

                bodies[id] = body;
            }

            return body;
        }

        bool Matches(string id) => member is null || (!unknownMember && BodyOf(id, scanning: true) is { } body && Names(body, roster!, member.Value.Member) && (with is null || Names(body, roster!, with.Value.Member)));

        var towardNewer = before is not null;
        var cursor = after ?? before;
        var ordered = unknownMember
            ? []
            : towardNewer
            ? ids.Where(id => string.CompareOrdinal(id, cursor) > 0).Reverse()
            : ids.Where(id => cursor is null || string.CompareOrdinal(id, cursor) < 0);
        var scan = Scan(ordered, request.Limit, Matches, () => stopped);
        var page = scan.Page;
        if (towardNewer)
        {
            page.Reverse();
        }

        var rows = new List<JsonValue>(page.Count);
        foreach (var id in page)
        {
            if (BodyOf(id, scanning: false) is { } body)
            {
                rows.Add(RowOf(dirId, id, body));
            }
        }

        string? head = page.Count > 0 ? page[0] : null;
        string? tail = page.Count > 0 ? page[^1] : null;
        var next = towardNewer ? (tail is null ? null : ListCursor.Encode(tail))
            : scan.HasMore && tail is not null ? ListCursor.Encode(tail)
            : scan.Stopped && scan.LastScanned is not null ? ListCursor.Encode(scan.LastScanned)
            : tail is null && after is not null ? ListCursor.Encode(after)
            : null;
        var prev = scan.Stopped && towardNewer && scan.LastScanned is not null ? ListCursor.Encode(scan.LastScanned)
            : head is not null ? ListCursor.Encode(head)
            : before is not null ? ListCursor.Encode(before)
            : null;
        return new ListResult.Found(new ListPage(
            state.Publish,
            unknownMember ? 0 : TotalOf(ids, member is null, state, dirId, Matches),
            head is null ? null : ListCursor.Encode(head),
            tail is null ? null : ListCursor.Encode(tail),
            rows,
            next,
            prev,
            cold));
    }

    /// <summary>
    /// The number of rows the list and filter match, when it is known without a read: the list's size, or with a filter the matches among ids
    /// that are all held; otherwise null.
    /// </summary>
    private static long? TotalOf(List<string> ids, bool unfiltered, ModelState state, string dirId, Func<string, bool> matches)
    {
        if (unfiltered)
        {
            return ids.Count;
        }

        return ids.All(id => state.Slices.ContainsKey("exchange:" + dirId + "/" + id)) ? ids.Count(matches) : null;
    }

    /// <summary>Whether the exchange <paramref name="body"/> names the member with id <paramref name="member"/> in <paramref name="roster"/>.</summary>
    private static bool Names(JsonValue body, TeamEntry roster, string member) =>
        ExchangeFiling.PartiesOf(new LaneRecord(body, 0), roster).Any(party => party == member);

    /// <summary>A page row: the exchange item slice, <c>{id, body}</c>, with the full slice id and the body the exchange composer builds.</summary>
    internal static JsonValue RowOf(string dirId, string id, JsonValue body) =>
        JsonValue.FromObject(new JsonObject { ["id"] = "exchange:" + dirId + "/" + id, ["body"] = body });

    /// <summary>The outcome of <see cref="Scan"/>: the matching ids taken, whether one more match follows, the last id decided, and whether the cold-read budget ended the scan early.</summary>
    private readonly record struct Scanned(List<string> Page, bool HasMore, string? LastScanned, bool Stopped);

    /// <summary>
    /// Takes the first <paramref name="limit"/> ids of <paramref name="ordered"/> that match, then keeps going until it finds one more match, reaches
    /// the end, or <paramref name="stopped"/> reports that the budget for cold reads is spent.
    /// </summary>
    private static Scanned Scan(IEnumerable<string> ordered, int limit, Func<string, bool> matches, Func<bool> stopped)
    {
        var page = new List<string>();
        string? last = null;
        foreach (var id in ordered)
        {
            var match = matches(id);
            if (stopped())
            {
                return new Scanned(page, false, last, true);
            }

            last = id;
            if (!match)
            {
                continue;
            }

            if (page.Count == limit)
            {
                return new Scanned(page, true, last, false);
            }

            page.Add(id);
        }

        return new Scanned(page, false, last, false);
    }

    private static bool TryCursor(string? cursor, out string? id)
    {
        id = null;
        if (cursor is null)
        {
            return true;
        }

        id = ListCursor.Decode(cursor, 1)?[0];
        return id is not null;
    }

    internal static List<string>? IdsOf(Slice list)
    {
        try
        {
            return JsonText.Parse(list.Body).Member("ids") is { IsArray: true } ids
                ? [.. ids.AsArray().Where(static i => i.IsText).Select(static i => i.AsString())]
                : null;
        }
        catch (JsonParseException)
        {
            return null;
        }
    }
}
