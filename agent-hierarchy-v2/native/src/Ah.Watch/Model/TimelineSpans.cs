using Ah.Core.Json;

namespace Ah.Watch.Model;

/// <summary>
/// The pager of <c>list:timeline-spans:&lt;dirId&gt;/&lt;team&gt;</c>: one row per member of a team, for the window <c>from_ms</c> to <c>to_ms</c> (as
/// <see cref="TimelinePager"/> reads them), with the member's working, idle and blocked spans and its message marks. The rows are in roster order and the
/// cursor is a member name. The envelope also carries the window it answered, <c>archived</c> when the window starts before retention, and <c>more</c> when
/// the transitions run past what one read loads. A window that ends before now holds no member at its current activity, so a range ending now leaves out <c>to_ms</c>.
/// </summary>
/// <param name="pages">The paged reads of the timeline file.</param>
/// <param name="retentionDays">The configured retention, in days.</param>
/// <param name="nowMs">Epoch milliseconds now.</param>
/// <param name="slices">The published state; a snapshot read, safe from any thread.</param>
/// <param name="readCold">The exchange body of (directory id, exchange id) read through <c>msgs.read</c>, or null when it is gone.</param>
internal sealed class TimelineSpansPager(ITimelinePages pages, Func<int> retentionDays, Func<long> nowMs, Func<ModelState> slices, Func<string, string, JsonValue?> readCold) : IListPager
{
    public const string ListPrefix = "list:timeline-spans:";

    /// <summary>The state of a stretch before the oldest row one read loads, when older rows are left.</summary>
    internal const string Loading = "loading";

    /// <summary>The most transition pages one read loads.</summary>
    internal const int TransitionPages = 3;

    /// <summary>The most exchanges one read looks at for marks, newest first. Past it the lanes carry a <c>band</c>: the time before which marks are not shown.</summary>
    internal const int MaxExchanges = 600;

    private const long DayMs = 86_400_000;

    private const long MinuteMs = 60_000;

    public string Prefix => ListPrefix;

    public IReadOnlyCollection<string> Filters { get; } = ["from_ms", "to_ms"];

    /// <summary>The state a span is drawn in, from a served activity word.</summary>
    internal static string StateOf(string? activity) => activity switch
    {
        Loading => Loading,
        null or "" or "unknown" => "unknown",
        "idle" => "idle",
        "blocked" or "failed" => "blocked",
        _ => "working",
    };

    /// <summary>
    /// One subject's spans over the window. <paramref name="rows"/> are its transitions, oldest first. Before the first row the lane holds the seed (its newest
    /// row before the window), else that row's from state. A subject with no row holds the seed, else <paramref name="tail"/>, else unknown. While
    /// <paramref name="loadedFrom"/> is after the window's start, the stretch before it is <see cref="Loading"/>, and the seed does not apply: it describes the state before the window, not there.
    /// </summary>
    internal static List<(string State, long From, long To)> Spans(IReadOnlyList<TimelineRow> rows, long from, long to, string? seed, string? tail, long? loadedFrom)
    {
        var spans = new List<(string, long, long)>();
        void Push(string? state, long a, long b)
        {
            var (start, end) = (Math.Max(a, from), Math.Min(b, to));
            if (end > start)
            {
                spans.Add((StateOf(state), start, end));
            }
        }

        var partial = loadedFrom is { } loaded && loaded > from;
        if (partial)
        {
            Push(Loading, from, loadedFrom!.Value);
        }

        var at = partial ? loadedFrom!.Value : from;
        var known = partial ? null : seed;
        if (rows.Count == 0)
        {
            Push(known ?? tail, at, to);
            return spans;
        }

        var current = known ?? rows[0].From;
        foreach (var row in rows)
        {
            Push(current, at, row.AtMs);
            current = row.To;
            at = row.AtMs;
        }

        Push(current, at, to);
        return spans;
    }

    public ValueTask<ListResult> PageAsync(ListRequest request, CancellationToken cancellation) => ValueTask.FromResult(Page(request, cancellation));

    private ListResult Page(ListRequest request, CancellationToken cancellation)
    {
        var state = slices();
        var key = request.SliceId[Prefix.Length..];
        var slash = key.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0 || state.Rosters.GetValueOrDefault((key[..slash], key[(slash + 1)..])) is not { } roster)
        {
            return new ListResult.NotFound();
        }

        var dirId = key[..slash];
        var now = nowMs();
        var retained = now - (TimelineLane.ClampDays(retentionDays()) * DayMs);
        var from = retained;
        var to = now;
        if (!TimelinePager.TryMs(request, "from_ms", ref from))
        {
            return new ListResult.Invalid("from_ms");
        }

        if (!TimelinePager.TryMs(request, "to_ms", ref to))
        {
            return new ListResult.Invalid("to_ms");
        }

        var names = roster.MemberNames;
        var (start, count) = Window(names, request);
        if (start < 0)
        {
            return new ListResult.Invalid("bad_cursor");
        }

        cancellation.ThrowIfCancellationRequested();
        var (rows, more, reads) = Transitions(dirId, from, to);
        var seeds = pages.Seeds(dirId, from).ToDictionary(static s => s.Subject, static s => s.State, StringComparer.Ordinal);
        var loadedFrom = more && rows.Count > 0 ? rows.Min(static r => r.AtMs) : (long?)null;
        var bySubject = rows.GroupBy(static r => r.Subject, StringComparer.Ordinal)
            .ToDictionary(static g => g.Key, static g => (IReadOnlyList<TimelineRow>)[.. g.OrderBy(static r => r.AtMs).ThenBy(static r => r.Seq)], StringComparer.Ordinal);
        var (marks, band, cold) = Marks(state, roster, dirId, request.SliceId[Prefix.Length..], from, to, cancellation);
        var page = names.Skip(start).Take(count).ToList();
        var lanes = new List<JsonValue>(page.Count);
        foreach (var name in page)
        {
            var body = BodyOf(state, TreeIds.Member(roster.Dir, roster.Name, name));
            var session = body?.Member("session")?.NonEmptyText;
            var spans = Spans(
                session is not null && bySubject.TryGetValue(session, out var own) ? own : [],
                from,
                to,
                session is not null ? seeds.GetValueOrDefault(session) : null,
                to >= now ? TailOf(state, body) : null,
                loadedFrom);
            lanes.Add(Lane(TreeIds.Member(roster.Dir, roster.Name, name), body?.Member("name")?.NonEmptyText ?? name, spans, marks.GetValueOrDefault(name) ?? [], band));
        }

        var older = start + count < names.Count;
        return new ListResult.Found(new ListPage(
            state.Publish,
            names.Count,
            page.Count > 0 ? ListCursor.Encode(page[0]) : null,
            page.Count > 0 ? ListCursor.Encode(page[^1]) : null,
            lanes,
            older && page.Count > 0 ? ListCursor.Encode(page[^1]) : null,
            start > 0 && page.Count > 0 ? ListCursor.Encode(page[0]) : null,
            reads + cold)
        {
            Extra =
            [
                ("from_ms", JsonValue.FromNumber(from)),
                ("to_ms", JsonValue.FromNumber(to)),
                // A front end floors its range to the minute, so a range of exactly the retention starts just before it and is not archived.
                ("archived", from < retained - MinuteMs),
                ("more", more),
            ],
        });
    }

    /// <summary>The members of the page, as a start and a count; a start below 0 for a cursor that names no member.</summary>
    private static (int Start, int Count) Window(IReadOnlyList<string> names, ListRequest request)
    {
        int At(string cursor) => ListCursor.Decode(cursor, 1)?[0] is { } name ? names.ToList().IndexOf(name) : -1;
        if (request.After is { } after)
        {
            var at = At(after);
            return at < 0 ? (-1, 0) : (at + 1, request.Limit);
        }

        if (request.Before is { } before)
        {
            var at = At(before);
            return at < 0 ? (-1, 0) : (Math.Max(0, at - request.Limit), Math.Min(at, request.Limit));
        }

        return (0, request.Limit);
    }

    /// <summary>The window's transitions, newest first, up to <see cref="TransitionPages"/> pages, whether older ones are left, and the reads spent.</summary>
    private (List<TimelineRow> Rows, bool More, int Reads) Transitions(string dirId, long from, long to)
    {
        var rows = new List<TimelineRow>();
        var reads = 0;
        if (from >= to)
        {
            return (rows, false, reads);
        }

        var cursor = 0L;
        for (var i = 0; i < TransitionPages; i++)
        {
            var page = pages.Page(dirId, from, to, cursor, older: true, ListRequest.MaxLimit);
            reads++;
            rows.AddRange(page);
            if (page.Count < ListRequest.MaxLimit)
            {
                return (rows, false, reads);
            }

            cursor = page[^1].Seq;
        }

        reads++;
        return (rows, pages.Page(dirId, from, to, cursor, older: true, 1).Count > 0, reads);
    }

    /// <summary>
    /// Each member's marks, oldest first: a brief where its exchange was created, a response where it was answered, on the lanes of both parties. The exchanges
    /// are read newest first until one was created before the window, so an exchange created earlier and answered in the window is not seen.
    /// </summary>
    private (Dictionary<string, List<JsonValue>> Marks, long? Band, int Cold) Marks(ModelState state, TeamEntry roster, string dirId, string teamKey, long from, long to, CancellationToken cancellation)
    {
        var found = new Dictionary<string, List<(long At, JsonValue Mark)>>(StringComparer.Ordinal);
        var ids = state.Slices.TryGetValue(TeamExchangeListComposer.ExchangesPrefix + ":" + teamKey, out var list) ? ExchangeListPager.IdsOf(list) ?? [] : [];
        var cold = 0;
        var seen = 0;
        var oldest = long.MaxValue;
        var ended = true;
        foreach (var id in ids)
        {
            if (seen == MaxExchanges)
            {
                ended = false;
                break;
            }

            cancellation.ThrowIfCancellationRequested();
            JsonValue? read;
            if (state.Slices.TryGetValue("exchange:" + dirId + "/" + id, out var held))
            {
                read = JsonText.Parse(held.Body);
            }
            else
            {
                cold++;
                read = readCold(dirId, id);
            }

            seen++;
            if (read is not { } body)
            {
                continue;
            }

            var created = LaneParts.Ms(LaneParts.Text(body, "created"));
            oldest = Math.Min(oldest, created);
            if (created < from)
            {
                break;
            }

            var parties = ExchangeFiling.PartiesOf(new LaneRecord(body, 0), roster);
            void Add(long at, string kind)
            {
                foreach (var party in parties)
                {
                    if (!found.TryGetValue(party, out var marks))
                    {
                        found[party] = marks = [];
                    }

                    marks.Add((at, Mark(id, at, kind, body)));
                }
            }

            if (created <= to)
            {
                Add(created, "brief");
            }

            var responded = body.Member("has_response") is { IsBool: true } has && has.AsBool() ? LaneParts.Ms(LaneParts.Text(body, "response_created")) : 0;
            if (responded >= from && responded <= to && responded > 0)
            {
                Add(responded, "response");
            }
        }

        var sorted = found.ToDictionary(static p => p.Key, static p => p.Value.OrderBy(static m => m.At).Select(static m => m.Mark).ToList(), StringComparer.Ordinal);
        return (sorted, !ended && from < oldest ? oldest : null, cold);
    }

    private static JsonValue Mark(string id, long at, string kind, JsonValue body) => JsonValue.FromObject(new JsonObject
    {
        ["at"] = at,
        ["kind"] = kind,
        ["slug"] = LaneParts.Text(body, "title") is { Length: > 0 } title ? title : LaneParts.TextOrNull(body, "slug"),
        ["class"] = LaneParts.TextOrNull(body, "class"),
        ["key"] = kind + ":" + id,
    });

    private static JsonValue Lane(string id, string name, List<(string State, long From, long To)> spans, List<JsonValue> marks, long? band)
    {
        var spanRows = new JsonArray();
        foreach (var (state, from, to) in spans)
        {
            spanRows.Add(JsonValue.FromObject(new JsonObject { ["state"] = state, ["from"] = from, ["to"] = to }));
        }

        var markRows = new JsonArray();
        foreach (var mark in marks)
        {
            markRows.Add(mark);
        }

        var lane = new JsonObject { ["id"] = id, ["name"] = name, ["spans"] = JsonValue.FromArray(spanRows), ["marks"] = JsonValue.FromArray(markRows) };
        if (band is { } at)
        {
            lane["band"] = at;
        }

        return JsonValue.FromObject(lane);
    }

    private static JsonValue? BodyOf(ModelState state, string id) =>
        state.Slices.TryGetValue(id, out var slice) && JsonText.TryParse(slice.Body, out var body) ? body : (JsonValue?)null;

    /// <summary>The member's current activity as a state word, when its activity slice is held.</summary>
    private static string? TailOf(ModelState state, JsonValue? member) =>
        member?.Member("activity")?.NonEmptyText is { } id && BodyOf(state, id) is { } activity ? StateOf(activity.Member("activity")?.NonEmptyText) : null;
}
