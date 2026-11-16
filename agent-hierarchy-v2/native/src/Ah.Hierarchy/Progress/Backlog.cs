using System.Globalization;
using Ah.Core.Json;

namespace Ah.Hierarchy;

/// <summary>One backlog item as <c>plan_items</c> holds it: the folded item and when its row last changed.</summary>
public sealed record BacklogRow(PlanItem Item, long UpdatedMs);

/// <summary>
/// The backlog's groom health: item counts by wave then state, open untriaged and stale items, the last groom (ms and its summary), whether
/// a groom is due, since when and why, the backlog-watch job's last stamp, and whether the loop looks stalled. Times are Unix milliseconds.
/// </summary>
public sealed record BacklogHealth(
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> Items,
    int Untriaged,
    int Stale,
    long? LastGroomMs,
    JsonValue? LastGroomSummary,
    bool Due,
    long? DueSinceMs,
    string Reason,
    long? JobStampMs,
    bool Stalled);

/// <summary>
/// The backlog: the one plan stream kept in the state database, scoped to a hierarchy directory and to no team. Writes go through
/// the <c>progress.backlog-write</c> op, which appends the events and refolds the item in one transaction; the reads here are indexed
/// queries on <c>plan_items</c> through the caller's read-only <c>query</c>.
/// </summary>
internal static class Backlog
{
    /// <summary>The reserved stream name.</summary>
    public const string Stream = "backlog";

    /// <summary>The op that writes the backlog.</summary>
    public const string WriteOp = "backlog-write";

    /// <summary>The error code of a refusal the caller answers with exit 3: the holder or the ext belongs to someone else.</summary>
    public const string ConflictCode = "backlog-conflict";

    /// <summary>The waves, in list order.</summary>
    public static IReadOnlyList<string> Waves => ProgressFormat.Waves;

    /// <summary>The sizes an item may have.</summary>
    public static IReadOnlyList<string> Sizes => ProgressFormat.Sizes;

    /// <summary>The plan states.</summary>
    public static IReadOnlyList<string> States { get; } =
        [PlanStates.NotStarted, PlanStates.Next, PlanStates.InProgress, PlanStates.Reported, PlanStates.InReview, PlanStates.Testing, PlanStates.Blocked, PlanStates.WaitingUser, PlanStates.WaitingOrch, PlanStates.Done, PlanStates.Dropped];

    /// <summary>
    /// The state a row reads as, from the checklist items in <paramref name="items"/> that name it as parent. Only a row with no state of its own moves: not started,
    /// next or in progress. Items only queued leave it as stored; items all waiting on review or ship make it in review; any other started work makes it in progress.
    /// </summary>
    public static string StateOf(PlanItem row, IEnumerable<PlanItem> items) => ReadState(row.Item, row.State, ChildrenOf(items));

    /// <summary>What the read state needs of a checklist item that names a backlog row as parent. Recorded in the health facts, so a status can replay it.</summary>
    internal sealed record Child(string Parent, string State, long StartedMs);

    /// <summary>The items in <paramref name="items"/> that count toward a row's read state: those with a parent, not dropped.</summary>
    internal static IReadOnlyList<Child> ChildrenOf(IEnumerable<PlanItem>? items) =>
        items is null
            ? []
            : [.. items.Where(static i => i.Parent is not null && i.State != PlanStates.Dropped).Select(static i => new Child(i.Parent!, i.State, i.StartedAt is { } at ? Ms(at) : 0))];

    private static string ReadState(string item, string state, IReadOnlyList<Child> children)
    {
        if (state is not (PlanStates.NotStarted or PlanStates.Next or PlanStates.InProgress))
        {
            return state;
        }

        var own = children.Where(c => c.Parent == item).ToList();
        if (own.All(static c => c.State is PlanStates.NotStarted or PlanStates.Next))
        {
            return state;
        }

        var open = own.Where(static c => c.State != PlanStates.Done).ToList();
        return open.Count > 0 && open.All(static c => c.State is PlanStates.Reported or PlanStates.InReview or PlanStates.Approved) ? PlanStates.InReview : PlanStates.InProgress;
    }

    private const string Columns = "doc, updated_ms";

    private const string ListOrder =
        "ORDER BY CASE wave WHEN 'now' THEN 0 WHEN 'next' THEN 1 WHEN 'later' THEN 2 ELSE 3 END, rank IS NULL, rank, CAST(substr(item, 2) AS INTEGER), item";

    /// <summary>The key a hierarchy directory is stored under.</summary>
    public static string DirKey(string dir) => MsgRows.PoolKey(dir);

    /// <summary>The items, in list order: wave (now, next, later, untriaged), then rank, then N. Each filter is skipped when null.</summary>
    public static IReadOnlyList<BacklogRow> List(
        Func<string, object?[], IReadOnlyList<object?[]>> query, string dir, string? wave = null, string? state = null, string? theme = null, string? holder = null)
    {
        var rows = query(
            $"SELECT {Columns} FROM plan_items WHERE hier_dir = ?1 AND stream = ?2 AND (?3 IS NULL OR wave = ?3) AND (?4 IS NULL OR state = ?4) AND (?5 IS NULL OR holder = ?5) {ListOrder}",
            [DirKey(dir), Stream, wave, state, holder]);
        var items = rows.Select(Row).ToList();
        return theme is null ? items : [.. items.Where(r => r.Item.Area == theme)];
    }

    /// <summary>One item, or null.</summary>
    public static BacklogRow? Get(Func<string, object?[], IReadOnlyList<object?[]>> query, string dir, string id) =>
        query($"SELECT {Columns} FROM plan_items WHERE hier_dir = ?1 AND stream = ?2 AND item = ?3", [DirKey(dir), Stream, id]) is { Count: > 0 } rows ? Row(rows[0]) : null;

    /// <summary>
    /// The first item in list order an agent may take: not started or next, no holder, every dependency done or dropped, a wave other
    /// than untriaged, and not a project or goal. A row whose checklist <paramref name="items"/> have started it is taken. Null when there is none.
    /// </summary>
    public static BacklogRow? Next(Func<string, object?[], IReadOnlyList<object?[]>> query, string dir, string? theme = null, IEnumerable<PlanItem>? items = null)
    {
        var key = DirKey(dir);
        var children = ChildrenOf(items);
        var candidates = query(
            $"SELECT {Columns} FROM plan_items WHERE hier_dir = ?1 AND stream = ?2 AND state IN ('not_started', 'next') AND wave IN ('now', 'next', 'later') AND (holder IS NULL OR holder = '') {ListOrder}",
            [key, Stream]).Select(Row)
            .Where(r => (theme is null || r.Item.Area == theme) && !ProgressFormat.IsContainerKind(r.Item.Kind) && ReadState(r.Item.Item, r.Item.State, children) == r.Item.State).ToList();
        if (candidates.Count == 0)
        {
            return null;
        }

        var closed = query("SELECT item FROM plan_items WHERE hier_dir = ?1 AND stream = ?2 AND state IN ('done', 'dropped')", [key, Stream])
            .Select(static r => (string)r[0]!).ToHashSet(StringComparer.Ordinal);
        return candidates.FirstOrDefault(r => (r.Item.Depends ?? []).All(closed.Contains));
    }

    /// <summary>Every event row of one item, oldest first; this folds nothing and is the one read that scans the stream.</summary>
    public static IReadOnlyList<JsonValue> History(Func<string, object?[], IReadOnlyList<object?[]>> query, string dir, string id) =>
        ItemRows(query, DirKey(dir), id);

    /// <summary>Every item whose row changed after <paramref name="sinceMs"/> (all of them when null), oldest change first.</summary>
    public static IReadOnlyList<BacklogRow> Export(Func<string, object?[], IReadOnlyList<object?[]>> query, string dir, long? sinceMs) =>
        [.. query($"SELECT {Columns} FROM plan_items WHERE hier_dir = ?1 AND stream = ?2 AND updated_ms > ?3 ORDER BY updated_ms, item", [DirKey(dir), Stream, sinceMs ?? long.MinValue]).Select(Row)];


    internal const long GroomEveryMs = 24 * 3600 * 1000L;
    internal const long StaleWaitingMs = 14 * 24 * 3600 * 1000L;
    internal const long StaleInProgressMs = 3 * 24 * 3600 * 1000L;
    internal const long StampStaleMs = 30 * 60 * 1000L;

    /// <summary>The <c>meta</c> key of the last groom of hierarchy dir key <paramref name="dirKey"/>: <c>{ms, summary}</c>, written by each groom.</summary>
    internal static string GroomMetaKey(string dirKey) => "backlog.groom:" + dirKey;

    /// <summary>
    /// The groom health of the backlog at <paramref name="nowMs"/>: counts by wave and state, open untriaged items, stale items (not started
    /// or next in now/next untouched 14 days, in progress untouched 3 days), the last groom, and whether a groom is due (24 h since the last,
    /// or anything untriaged or stale) since when. Stalled: the job's stamp (<paramref name="stampMs"/>, null when it never ran) is older than
    /// 30 minutes, or the groom has been due for more than 24 hours. Counts and staleness use the read state from <paramref name="children"/>;
    /// the latest start among a row's children counts as touching it.
    /// </summary>
    /// <remarks>
    /// <paramref name="flip"/> (b459 C1) is told every later instant at which this answer changes with the clock alone: an item going stale,
    /// the groom falling due. It is told <paramref name="nowMs"/> itself where no instant can be named (the never-groomed backlog with no
    /// events, whose <c>due_since</c> is the clock; a job stamp), which means "changes continuously". A new clock rule here must tell it too.
    /// </remarks>
    internal static BacklogHealth Health(Func<string, object?[], IReadOnlyList<object?[]>> query, string dir, long nowMs, long? stampMs, IReadOnlyList<Child>? children = null, Action<double>? flip = null)
    {
        var key = DirKey(dir);
        children ??= [];
        var counts = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        foreach (var group in query("SELECT COALESCE(wave, 'untriaged'), state, COUNT(*) FROM plan_items WHERE hier_dir = ?1 AND stream = ?2 GROUP BY 1, 2", [key, Stream])
            .GroupBy(static r => (string)r[0]!))
        {
            counts[group.Key] = group.ToDictionary(static r => (string)r[1]!, static r => (int)(long)r[2]!, StringComparer.Ordinal);
        }

        var open = query(
            "SELECT COALESCE(wave, 'untriaged'), state, updated_ms, CASE WHEN wave IS NULL OR wave = 'untriaged' THEN doc END, item FROM plan_items WHERE hier_dir = ?1 AND stream = ?2 AND state NOT IN ('done', 'dropped')",
            [key, Stream]);
        long? lastGroom = null;
        JsonValue? summary = null;
        if (query("SELECT value FROM meta WHERE key = ?1", [GroomMetaKey(key)]) is { Count: > 0 } g && JsonText.Parse((string)g[0][0]!) is var groom)
        {
            lastGroom = (long)groom.Member("ms")!.Value.AsNumber();
            summary = groom.Member("summary");
        }

        // when each reason began; due_since is the earliest, never before the last groom, so it is stable for one due episode
        var starts = new List<long>();
        var untriaged = 0;
        var stale = 0;
        foreach (var r in open)
        {
            var (wave, stored, updated) = ((string)r[0]!, (string)r[1]!, (long)r[2]!);
            var item = (string)r[4]!;
            var state = ReadState(item, stored, children);
            if (state != stored && counts.TryGetValue(wave, out var byState))
            {
                if (byState.GetValueOrDefault(stored) <= 1)
                {
                    byState.Remove(stored);
                }
                else
                {
                    byState[stored]--;
                }

                byState[state] = byState.GetValueOrDefault(state) + 1;
            }

            var touched = children.Where(c => c.Parent == item).Select(static c => c.StartedMs).Append(updated).Max();
            if (wave == "untriaged")
            {
                // an untriaged item's reason starts when it was added; an edit does not move it
                untriaged++;
                starts.Add(r[3] is string doc && FromDoc(doc)?.AddedAt is { } added && Ms(added) is > 0 and var addedMs ? addedMs : updated);
            }
            else if (state == PlanStates.InProgress ? touched + StaleInProgressMs <= nowMs : wave is "now" or "next" && state is PlanStates.NotStarted or PlanStates.Next && touched + StaleWaitingMs <= nowMs)
            {
                stale++;
                starts.Add(touched + (state == PlanStates.InProgress ? StaleInProgressMs : StaleWaitingMs));
            }
            else if (state == PlanStates.InProgress)
            {
                flip?.Invoke(touched + StaleInProgressMs);
            }
            else if (wave is "now" or "next" && state is PlanStates.NotStarted or PlanStates.Next)
            {
                flip?.Invoke(touched + StaleWaitingMs);
            }
        }

        var groomDue = lastGroom is not { } at || nowMs - at >= GroomEveryMs;
        var noEvent = false;
        if (groomDue)
        {
            // never groomed: the episode starts with the backlog's first event, not at the epoch
            if (lastGroom is { } last)
            {
                starts.Add(last + GroomEveryMs);
            }
            else if (query("SELECT ts_ms FROM plan_events WHERE hier_dir = ?1 AND stream = ?2 ORDER BY id LIMIT 1", [key, Stream]) is { Count: > 0 } first)
            {
                starts.Add((long)first[0][0]!);
            }
            else
            {
                starts.Add(nowMs);
                noEvent = true;
            }
        }
        else
        {
            flip?.Invoke(lastGroom!.Value + GroomEveryMs);
        }

        var due = groomDue || untriaged > 0 || stale > 0;
        long? dueSince = due ? Math.Max(lastGroom ?? 0, starts.Min()) : null;
        if (noEvent && dueSince == nowMs)
        {
            flip?.Invoke(nowMs);  // due_since is the clock itself: no earlier reason (an untriaged or stale item) fixes it
        }
        var reasons = new List<string>();
        if (untriaged > 0)
        {
            reasons.Add($"{untriaged} untriaged");
        }

        if (stale > 0)
        {
            reasons.Add($"{stale} stale");
        }

        if (groomDue)
        {
            reasons.Add(lastGroom is null ? "never groomed" : "last groom over 24 h ago");
        }

        var stalled = stampMs is not { } stamp || nowMs - stamp > StampStaleMs || (dueSince is { } since && nowMs - since > GroomEveryMs);
        if (stampMs is not null)
        {
            flip?.Invoke(nowMs);  // a job stamp's age rules are not enumerated; no status reader passes a stamp
        }
        return new BacklogHealth(counts.ToDictionary(static c => c.Key, static c => (IReadOnlyDictionary<string, int>)c.Value, StringComparer.Ordinal), untriaged, stale, lastGroom, summary, due, dueSince, string.Join(", ", reasons), stampMs, stalled);
    }

    /// <summary>
    /// The health facts of hierarchy dir <paramref name="dir"/> (ah-msgs-fast-2 §3.8, ruling B1): the rows of every query <see cref="Health"/>
    /// runs, by SQL text. Recorded at the far future so the groom-due branch runs its query too; nothing in a row depends on the time.
    /// </summary>
    internal static JsonObject HealthFacts(Func<string, object?[], IReadOnlyList<object?[]>> query, string dir, IEnumerable<PlanItem>? items = null)
    {
        var facts = new JsonObject();
        var children = ChildrenOf(items);
        var recordedChildren = new JsonArray();
        foreach (var c in children)
        {
            recordedChildren.Add(new JsonArray { JsonValue.FromString(c.Parent), JsonValue.FromString(c.State), c.StartedMs });
        }

        facts[ChildrenFact] = recordedChildren;
        _ = Health(
            (sql, args) =>
            {
                var rows = query(sql, args);
                var recorded = new JsonArray();
                foreach (var r in rows)
                {
                    var row = new JsonArray();
                    foreach (var c in r)
                    {
                        row.Add(c switch
                        {
                            null => JsonValue.Null,
                            string s => JsonValue.FromString(s),
                            long n => n,
                            _ => JsonValue.FromString(Convert.ToString(c, CultureInfo.InvariantCulture) ?? string.Empty),
                        });
                    }

                    recorded.Add(row);
                }

                facts[sql] = recorded;
                return rows;
            },
            dir,
            long.MaxValue / 2,
            null,
            children);
        return facts;
    }

    /// <summary>The facts key of the checklist items that move a row's read state; every other key is SQL text.</summary>
    private const string ChildrenFact = "children";

    /// <summary>The health at <paramref name="nowMs"/> from recorded <paramref name="facts"/>: the same <see cref="Health"/> over the recorded rows.</summary>
    internal static BacklogHealth HealthFromFacts(JsonValue facts, string dir, long nowMs, long? stampMs, Action<double>? flip = null) =>
        Health(
            (sql, _) => facts.Member(sql) is { IsArray: true } rows
                ? [.. rows.AsArray().Select(static r => r.AsArray().Select(static c => c.IsNull ? null : c.IsText ? c.AsString() : (object)(long)c.AsNumber()).ToArray())]
                : [],
            dir,
            nowMs,
            stampMs,
            facts.Member(ChildrenFact) is { IsArray: true } children
                ? [.. children.AsArray().Select(static c => c.AsArray()).Select(static c => new Child(c[0].AsString(), c[1].AsString(), (long)c[2].AsNumber()))]
                : null,
            flip);

    /// <summary>The item a <c>doc</c> column holds, or null when it does not parse.</summary>
    public static PlanItem? FromDoc(string doc)
    {
        JsonValue v;
        try
        {
            v = JsonText.Parse(doc);
        }
        catch (JsonParseException)
        {
            return null;
        }

        string? T(string k) => v.Member(k) is { IsText: true } t ? t.AsString() : null;
        int? N(string k) => v.Member(k) is { IsNumber: true } n ? (int)n.AsNumber() : null;
        IReadOnlyList<string>? L(string k) => v.Member(k) is { IsArray: true } a ? [.. a.AsArray().Where(static x => x.IsText).Select(static x => x.AsString())] : null;
        return T("item") is not { } item ? null : new PlanItem(
            item, T("title") ?? item, T("state") ?? PlanStates.NotStarted, T("added_at") ?? string.Empty, T("started_at"), T("done_at"), L("commits") ?? [], T("branch"), N("issue"), T("note"),
            N("rank"), T("area"), T("holder"), T("role"), T("todo"), L("refs"), T("updated_at"), T("state_since"), T("outcome"), T("size"), T("wave"), L("depends"), L("sources"), T("ext"))
        {
            Kind = T("kind"),
            Parent = T("parent"),
        };
    }

    internal static string ToDoc(PlanItem i)
    {
        var o = new JsonObject();
        void S(string k, string? v)
        {
            if (v is not null)
            {
                o[k] = v;
            }
        }

        void L(string k, IReadOnlyList<string>? v)
        {
            if (v is not null)
            {
                o[k] = Strs(v);
            }
        }

        S("item", i.Item);
        S("title", i.Title);
        S("state", i.State);
        S("added_at", i.AddedAt);
        S("started_at", i.StartedAt);
        S("done_at", i.DoneAt);
        L("commits", i.Commits);
        S("branch", i.Branch);
        if (i.Issue is { } issue)
        {
            o["issue"] = issue;
        }

        S("note", i.Note);
        if (i.Rank is { } rank)
        {
            o["rank"] = rank;
        }

        S("area", i.Area);
        S("holder", i.Holder);
        S("role", i.Role);
        S("todo", i.Todo);
        L("refs", i.Refs);
        S("updated_at", i.UpdatedAt);
        S("state_since", i.StateSince);
        S("outcome", i.Outcome);
        S("size", i.Size);
        S("wave", i.Wave);
        L("depends", i.Depends);
        L("sources", i.Sources);
        S("ext", i.Ext);
        S("kind", i.Kind);
        S("parent", i.Parent);
        return JsonText.Stringify(o, 0);
    }

    private static JsonArray Strs(IEnumerable<string> items)
    {
        var list = new JsonArray();
        foreach (var item in items)
        {
            list.Add(item);
        }

        return list;
    }

    private static BacklogRow Row(object?[] r) => new(FromDoc((string)r[0]!) ?? throw new InvalidOperationException("a backlog row holds a doc that does not parse"), (long)r[1]!);

    /// <summary>The event rows of one item, oldest first. The text filter narrows the scan; the parse keeps only the item's own rows.</summary>
    internal static IReadOnlyList<JsonValue> ItemRows(Func<string, object?[], IReadOnlyList<object?[]>> query, string dirKey, string id)
    {
        // ponytail: a LIKE scan of the stream's events per item write; add an item column to plan_events when the backlog outgrows it
        var rows = new List<JsonValue>();
        foreach (var r in query("SELECT row FROM plan_events WHERE hier_dir = ?1 AND stream = ?2 AND row LIKE ?3 ORDER BY id", [dirKey, Stream, "%\"item\":\"" + id + "\"%"]))
        {
            var row = JsonText.Parse((string)r[0]!);
            if (ProgressFormat.PlanText(row, "item") == id)
            {
                rows.Add(row);
            }
        }

        return rows;
    }

    internal static PlanItem? Fold(IReadOnlyList<JsonValue> rows)
    {
        var fold = new PlanFold();
        foreach (var row in rows)
        {
            fold.Apply(row);
        }

        return First(fold.State);
    }

    /// <summary>The first item of a fold, or null: the fold of one item's rows holds at most that item.</summary>
    internal static PlanItem? First(PlanState state) => state.Items.Count > 0 ? state.Items[0] : null;

    internal static long Ms(string ts) =>
        DateTimeOffset.TryParse(ts, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at.ToUnixTimeMilliseconds() : 0;
}
