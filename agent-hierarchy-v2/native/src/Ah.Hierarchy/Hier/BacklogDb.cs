using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.Hierarchy;

/// <summary>
/// <c>progress.backlog-write</c>: one write to the backlog in one transaction. The events are built from the args, checked against the
/// item's fold as any plan row is, appended to <c>plan_events</c>, and the item's <c>plan_items</c> row is refolded from its events.
/// </summary>
internal sealed partial class HierLib
{
    private const int RankStep = 1024;

    /// <summary>
    /// Args: <c>dir</c>, <c>by</c>, and one of <c>groom</c> (a summary object: a stream-level event), <c>add</c> (a title: allocates
    /// <c>b&lt;N&gt;</c>, or with <c>ext</c> held by an item upserts it, or with <c>once</c> held as a source skips), or <c>item</c>.
    /// Then the fields: title, outcome, theme, size, wave, kind, parent (empty clears; checked by <see cref="RefuseParent"/>), after, depends, sources, ext (+ ext_issue), note, claim, start, done
    /// (+ commits), drop. Result: <c>{item, existing?, skipped?, written}</c>.
    /// </summary>
    internal JsonValue BacklogWriteDb(OpCall call, IDbOpTransaction tx, JsonValue args)
    {
        var dir = Backlog.DirKey(ArgText(args, "dir"));
        var by = OpArgs.Text(args, "by") ?? "user";
        var now = _clock.UtcNow;
        var ts = ProgressFormat.PlanTime(now);
        var ms = now.ToUnixTimeMilliseconds();
        IReadOnlyList<object?[]> Q(string sql, object?[] a) => tx.Query(sql, a);

        if (args.Member("groom") is { IsMap: true } summary)
        {
            var groom = new JsonObject { ["v"] = ProgressFormat.PlanVersion, ["ts"] = ts, ["team"] = string.Empty, ["stream"] = Backlog.Stream, ["event"] = "groom", ["summary"] = summary, ["by"] = by };
            tx.Execute("INSERT INTO plan_events(hier_dir, stream, ts_ms, by, row) VALUES (?1, ?2, ?3, ?4, ?5)", dir, Backlog.Stream, ms, by, JsonText.Stringify(groom, 0));

            // the last groom is kept, not looked up: health reads it on every status save and Orchestrator prompt
            tx.Execute("INSERT OR REPLACE INTO meta(key, value) VALUES (?1, ?2)", Backlog.GroomMetaKey(dir), JsonText.Stringify(new JsonObject { ["ms"] = ms, ["summary"] = summary }, 0));
            return new JsonObject { ["groom"] = true };
        }

        var ext = OpArgs.Text(args, "ext");
        string item;
        var existing = false;
        var upsert = false;
        var rows = new List<JsonObject>();
        if (OpArgs.Text(args, "add") is { } addTitle)
        {
            if (OpArgs.Text(args, "once") is { } once && HolderOfSource(tx, dir, once) is { } seen)
            {
                return new JsonObject { ["item"] = seen, ["skipped"] = true, ["written"] = 0 };
            }

            if (ext is not null && Q("SELECT item FROM plan_items WHERE hier_dir = ?1 AND stream = ?2 AND ext = ?3", [dir, Backlog.Stream, ext]) is { Count: > 0 } held)
            {
                item = (string)held[0][0]!;
                existing = upsert = true;
                rows.Add(BacklogEvent("retitle", ("title", addTitle)));
            }
            else
            {
                tx.Execute("INSERT INTO domain_seq(domain, seq, changed_ms) VALUES ('backlog-id', 1, ?1) ON CONFLICT(domain) DO UPDATE SET seq = seq + 1, changed_ms = ?1", ms);
                item = "b" + (long)tx.Query("SELECT seq FROM domain_seq WHERE domain = 'backlog-id'")[0][0]!;
                rows.Add(BacklogEvent("add", ("title", addTitle)));
                if (args.Member("wave") is null)
                {
                    // creating a project or goal must not make the groomer due
                    rows.Add(BacklogEvent("wave", ("wave", ProgressFormat.IsContainerKind(OpArgs.Text(args, "kind")) ? "later" : "untriaged")));
                }
            }
        }
        else
        {
            item = ArgText(args, "item");
            existing = true;
        }

        var events = existing ? Backlog.ItemRows(Q, dir, item) : [];
        var current = Backlog.Fold(events);
        if (existing && current is null)
        {
            throw new InvalidOperationException($"no backlog item {item}");
        }

        if (!upsert && OpArgs.Text(args, "title") is { } title)
        {
            rows.Add(BacklogEvent("retitle", ("title", title)));
        }

        Field("outcome", "outcome");
        if (OpArgs.Text(args, "theme") is { } theme)
        {
            rows.Add(BacklogEvent("area", ("area", theme)));
        }

        Field("size", "size");
        Field("wave", "wave");
        Field("kind", "kind");
        Field("parent", "parent");
        if (args.Member("depends") is { IsArray: true } depends)
        {
            rows.Add(new JsonObject { ["event"] = "depend", ["depends"] = depends });
        }

        foreach (var source in args.Member("sources") is { IsArray: true } list ? list.AsArray().Where(static s => s.IsText).Select(static s => s.AsString()) : [])
        {
            rows.Add(BacklogEvent("source", ("source", source)));
        }

        var clearExt = args.Member("ext_clear") is { IsTruthy: true };
        if (clearExt)
        {
            rows.Add(new JsonObject { ["event"] = "ext", ["clear"] = true });
        }

        if (ext is not null)
        {
            if (Q("SELECT item FROM plan_items WHERE hier_dir = ?1 AND stream = ?2 AND ext = ?3 AND item <> ?4", [dir, Backlog.Stream, ext, item]) is { Count: > 0 } other)
            {
                throw new CodedIoException(Backlog.ConflictCode, $"ext held by {(string)other[0][0]!}");
            }

            var extRow = BacklogEvent("ext", ("ext", ext));
            if (args.Member("ext_issue") is { IsNumber: true } issue)
            {
                extRow["issue"] = issue;
            }

            rows.Add(extRow);
        }

        Field("note", "note");
        Field("ref", "ref");
        Field("unref", "unref");
        if (OpArgs.Text(args, "claim") is { } holder)
        {
            if (current?.Holder is { Length: > 0 } was && was != holder)
            {
                throw new CodedIoException(Backlog.ConflictCode, $"held by {was}");
            }

            rows.Add(BacklogEvent("assign", ("holder", holder)));
            if (current?.State != PlanStates.InProgress)
            {
                rows.Add(BacklogEvent("start"));
            }
        }

        if (args.Member("done") is { IsTruthy: true })
        {
            var done = BacklogEvent("done");
            if (args.Member("commits") is { IsArray: true } commits && commits.AsArray().Count > 0)
            {
                done["commits"] = commits;
            }

            if (OpArgs.Text(args, "done_note") is { } doneNote)
            {
                done["note"] = doneNote;
            }

            rows.Add(done);
        }

        if (OpArgs.Text(args, "drop") is { } dropNote)
        {
            rows.Add(BacklogEvent("drop", ("note", dropNote)));
        }

        if (OpArgs.Text(args, "block") is { } blockNote)
        {
            rows.Add(BacklogEvent("block", ("note", blockNote)));
        }

        if (OpArgs.Text(args, "testing") is { } batch)
        {
            if (current?.State is PlanStates.Done or PlanStates.Dropped)
            {
                throw new CodedIoException(Backlog.ConflictCode, $"already {current.State}");
            }

            rows.Add(BacklogEvent("testing", ("note", "testing " + batch)));
        }

        if (args.Member("testing_clear") is { IsTruthy: true })
        {
            if (current?.State != PlanStates.Testing)
            {
                throw new CodedIoException(Backlog.ConflictCode, "not marked as testing");
            }

            rows.Add(BacklogEvent("reopen"));
        }

        // Fold the new rows over the item's events: an upsert keeps only the rows that change the item; every row is checked as any plan row is.
        var fold = new PlanFold();
        foreach (var e in events)
        {
            fold.Apply(e);
        }

        var written = new List<JsonObject>();
        foreach (var row in rows)
        {
            var stamped = Stamp(row);
            // b496: work done outside the repo has no commit to name; a done with a note and no commits is taken as is
            var noteOnlyDone = OpArgs.Text(stamped, "event") == "done"
                && OpArgs.Text(stamped, "note") is { Length: > 0 }
                && !stamped.ContainsKey("commits");
            if (ProgressFormat.RefusePlanRow(stamped, fold.State, import: noteOnlyDone) is { } refused)
            {
                throw new InvalidOperationException(refused);
            }

            var before = Backlog.First(fold.State);
            fold.Apply(stamped);
            if (upsert && SameBacklogItem(before, Backlog.First(fold.State)))
            {
                continue;
            }

            written.Add(stamped);
        }

        // The rank: after the item named, else last in the item's wave when the item is new or changed wave.
        var folded = Backlog.First(fold.State) ?? throw new InvalidOperationException($"no backlog item {item}");
        if ((args.Member("kind") is not null || args.Member("parent") is not null) && folded.Parent is { } parentId)
        {
            RefuseParent(tx, dir, item, folded.Kind, parentId);
        }

        var after = OpArgs.Text(args, "after");
        if (after is not null || !existing || folded.Wave != current?.Wave)
        {
            foreach (var row in RankRows(tx, dir, item, folded, after))
            {
                if (row.Item == item)
                {
                    var stamped = Stamp(row.Row);
                    fold.Apply(stamped);
                    written.Add(stamped);
                }
                else
                {
                    AppendRows(tx, dir, row.Item, [row.Row], ms, by, ts);
                }
            }
        }

        foreach (var row in written)
        {
            tx.Execute("INSERT INTO plan_events(hier_dir, stream, ts_ms, by, row) VALUES (?1, ?2, ?3, ?4, ?5)", dir, Backlog.Stream, ms, by, JsonText.Stringify(row, 0));
        }

        if (written.Count > 0)
        {
            RefreshBacklogItem(tx, dir, Backlog.First(fold.State)!, ms);
        }

        if (!existing)
        {
            RegisterWork(tx, dir, string.Empty, Backlog.Stream, item, folded.AddedAt, ms);
        }

        // the backlog row's id stays in both stores, in this one transaction; the URL lives only in the registry
        if (ext is not null || clearExt || OpArgs.Text(args, "ext_url") is not null)
        {
            SetWorkExt(tx, dir, RegisterWork(tx, dir, string.Empty, Backlog.Stream, item, null, ms), clearExt ? string.Empty : ext, OpArgs.Text(args, "ext_url"), ms);
        }

        if (args.Member("depends") is { IsArray: true } replaced)
        {
            ReplaceBlockers(tx, dir, item, replaced.AsArray().Where(static d => d.IsText).Select(static d => d.AsString()), by, ms);
        }

        var result = new JsonObject { ["item"] = item, ["written"] = written.Count };
        if (existing && OpArgs.Text(args, "add") is not null)
        {
            result["existing"] = true;
        }

        return result;

        void Field(string arg, string eventName)
        {
            if (OpArgs.Text(args, arg) is { } value)
            {
                rows.Add(BacklogEvent(eventName, (eventName, value)));
            }
        }

        JsonObject Stamp(JsonObject row) => StampRow(row, ts, item, by);
    }

    /// <summary>
    /// Throws unless <paramref name="parentId"/> may be the parent of <paramref name="item"/> of kind <paramref name="kind"/>: it is another
    /// backlog item and its kind outranks the item's (project 5 > goal 4 > epic 3 > deliverable 2 > task 1), so no chain of parents can loop.
    /// </summary>
    private static void RefuseParent(IDbOpTransaction tx, string dir, string item, string? kind, string parentId)
    {
        if (parentId == item)
        {
            throw new InvalidOperationException($"--parent {parentId}: an item cannot be its own parent");
        }

        var parent = tx.Query("SELECT doc FROM plan_items WHERE hier_dir = ?1 AND stream = ?2 AND item = ?3", dir, Backlog.Stream, parentId) is { Count: > 0 } p
            ? Backlog.FromDoc((string)p[0][0]!)
            : null;
        if (parent is null)
        {
            throw new InvalidOperationException($"--parent {parentId}: no backlog item {parentId}");
        }

        if (ProgressFormat.KindRank(parent.Kind) <= ProgressFormat.KindRank(kind))
        {
            throw new InvalidOperationException($"--parent {parentId}: a {parent.Kind ?? "task"} cannot be the parent of a {kind ?? "task"}");
        }
    }

    /// <summary>An event as a plan row: the version, time, stream and item first, then what the event carries, then who wrote it.</summary>
    private static JsonObject StampRow(JsonObject row, string ts, string item, string by)
    {
        var stamped = new JsonObject { ["v"] = ProgressFormat.PlanVersion, ["ts"] = ts, ["team"] = string.Empty, ["stream"] = Backlog.Stream, ["item"] = item };
        foreach (var k in row.Keys)
        {
            stamped[k] = row[k];
        }

        stamped["by"] = by;
        return stamped;
    }

    private static JsonObject BacklogEvent(string name, params (string Key, string Value)[] fields)
    {
        var row = new JsonObject { ["event"] = name };
        foreach (var (key, value) in fields)
        {
            row[key] = value;
        }

        return row;
    }

    /// <summary>Whether two folds of an item are the same apart from their change times.</summary>
    private static bool SameBacklogItem(PlanItem? a, PlanItem? b) =>
        a is not null && b is not null && Backlog.ToDoc(a with { UpdatedAt = null, StateSince = null }) == Backlog.ToDoc(b with { UpdatedAt = null, StateSince = null });

    /// <summary>The item that holds <paramref name="source"/> among its sources, or null.</summary>
    private static string? HolderOfSource(IDbOpTransaction tx, string dir, string source)
    {
        foreach (var r in tx.Query("SELECT doc FROM plan_items WHERE hier_dir = ?1 AND stream = ?2 AND doc LIKE ?3", dir, Backlog.Stream, "%" + JsonText.Quote(source) + "%"))
        {
            if (Backlog.FromDoc((string)r[0]!) is { } i && (i.Sources ?? []).Contains(source, StringComparer.Ordinal))
            {
                return i.Item;
            }
        }

        return null;
    }

    /// <summary>
    /// The rank rows that place <paramref name="item"/>: after <paramref name="after"/> (taking its wave), else last in its wave. With no
    /// gap after <paramref name="after"/> the items behind it move down one step each.
    /// </summary>
    private static List<(string Item, JsonObject Row)> RankRows(IDbOpTransaction tx, string dir, string item, PlanItem folded, string? after)
    {
        var rows = new List<(string, JsonObject)>();
        var wave = folded.Wave ?? "untriaged";
        if (after is null)
        {
            var max = tx.Query("SELECT MAX(rank) FROM plan_items WHERE hier_dir = ?1 AND stream = ?2 AND wave = ?3 AND item <> ?4", dir, Backlog.Stream, wave, item);
            var rank = max.Count > 0 && max[0][0] is long m ? m + RankStep : RankStep;
            rows.Add((item, RankRow(rank)));
            return rows;
        }

        var anchor = tx.Query("SELECT wave, rank FROM plan_items WHERE hier_dir = ?1 AND stream = ?2 AND item = ?3", dir, Backlog.Stream, after) is { Count: > 0 } a
            ? a[0]
            : throw new InvalidOperationException($"--after {after}: no backlog item {after}");
        var anchorWave = anchor[0] as string ?? "untriaged";
        var anchorRank = anchor[1] as long? ?? 0;
        if (anchorWave != wave)
        {
            rows.Add((item, new JsonObject { ["event"] = "wave", ["wave"] = anchorWave }));
        }

        var behind = tx.Query(
            "SELECT item, rank FROM plan_items WHERE hier_dir = ?1 AND stream = ?2 AND wave = ?3 AND rank > ?4 AND item <> ?5 ORDER BY rank",
            dir, Backlog.Stream, anchorWave, anchorRank, item);
        if (behind.Count == 0)
        {
            rows.Add((item, RankRow(anchorRank + RankStep)));
        }
        else if ((long)behind[0][1]! - anchorRank > 1)
        {
            rows.Add((item, RankRow(anchorRank + (((long)behind[0][1]! - anchorRank) / 2))));
        }
        else
        {
            rows.Add((item, RankRow(anchorRank + 1)));
            foreach (var b in behind)
            {
                rows.Add(((string)b[0]!, RankRow((long)b[1]! + RankStep)));
            }
        }

        return rows;
    }

    private static JsonObject RankRow(long rank) => new() { ["event"] = "rank", ["rank"] = rank };

    /// <summary>Appends <paramref name="rows"/> to another item and refolds it.</summary>
    private static void AppendRows(IDbOpTransaction tx, string dir, string item, IReadOnlyList<JsonObject> rows, long ms, string by, string ts)
    {
        var events = Backlog.ItemRows((s, a) => tx.Query(s, a), dir, item).ToList();
        foreach (var row in rows)
        {
            var stamped = StampRow(row, ts, item, by);
            tx.Execute("INSERT INTO plan_events(hier_dir, stream, ts_ms, by, row) VALUES (?1, ?2, ?3, ?4, ?5)", dir, Backlog.Stream, ms, by, JsonText.Stringify(stamped, 0));
            events.Add(stamped);
        }

        // a move made for another item's write keeps this item's change time, so it does not hide staleness
        if (Backlog.Fold(events) is { } folded)
        {
            var kept = tx.Query("SELECT updated_ms FROM plan_items WHERE hier_dir = ?1 AND stream = ?2 AND item = ?3", dir, Backlog.Stream, item) is { Count: > 0 } u && u[0][0] is long was ? was : ms;
            RefreshBacklogItem(tx, dir, folded, kept);
        }
    }

    private static void RefreshBacklogItem(IDbOpTransaction tx, string dir, PlanItem i, long ms) =>
        tx.Execute(
            "INSERT OR REPLACE INTO plan_items(hier_dir, stream, item, state, wave, rank, holder, ext, updated_ms, doc) VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10)",
            dir, Backlog.Stream, i.Item, i.State, i.Wave, i.Rank is { } r ? (long)r : null, i.Holder, i.Ext, ms, Backlog.ToDoc(i));
}
