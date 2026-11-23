using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.Hierarchy;

/// <summary><c>progress.work-write</c>: the registry's one write, in one transaction, so numbers are per repo, never reused and without gaps.</summary>
internal sealed partial class HierLib
{
    /// <summary>
    /// Args: <c>dir</c>, and <c>prefix</c> (changes the repo's prefix), <c>record</c> (a history entry), <c>link</c> (blocker, blocked, by, remove), <c>attach</c> (n, ref, kind, title, sha, by, remove, need),
    /// <c>ext_of</c> (a number, with ext and url) or <c>register</c> (rows of team, stream, item, added_at). A registration
    /// without <c>backfill</c> writes nothing and answers <c>{needs_backfill}</c> until the repo holds a team item, so older items keep their
    /// <c>AddedAt</c> order: the caller then reconciles. Result: <c>{prefix}</c>; a write's result is capped, so it never lists the numbers.
    /// </summary>
    internal JsonValue WorkWriteDb(IDbOpTransaction tx, JsonValue args)
    {
        var dir = Backlog.DirKey(ArgText(args, "dir"));
        var ms = _clock.UtcNow.ToUnixTimeMilliseconds();
        if (OpArgs.Text(args, "prefix") is { } prefix)
        {
            if (!Work.ValidPrefix(prefix))
            {
                throw new InvalidOperationException($"prefix {prefix}: must be a lower-case letter then up to 7 lower-case letters or digits");
            }

            EnsureWorkRepo(tx, dir, ms);
            tx.Execute("UPDATE work_repos SET prefix = ?2, changed_ms = ?3 WHERE hier_dir = ?1", dir, prefix, ms);
            tx.Execute("INSERT OR IGNORE INTO work_prefixes(hier_dir, prefix, set_ms) VALUES (?1, ?2, ?3)", dir, prefix, ms);
            return new JsonObject { ["prefix"] = prefix };
        }

        if (args.Member("record") is { IsMap: true } entry)
        {
            return RecordWork(tx, dir, entry, ms);
        }

        if (args.Member("link") is { IsMap: true } link)
        {
            return LinkWork(tx, dir, link, ms);
        }

        if (args.Member("adr") is { IsMap: true } adr)
        {
            return AdrWrite(tx, dir, adr, ms);
        }

        if (args.Member("attach") is { IsMap: true } attach)
        {
            return AttachWork(tx, dir, attach, ms);
        }

        if (args.Member("ext_of") is { IsNumber: true } extOf)
        {
            SetWorkExt(tx, dir, (long)extOf.AsNumber(), OpArgs.Text(args, "ext"), OpArgs.Text(args, "url"), ms);
            return new JsonObject { ["ext"] = true };
        }

        var backfill = args.Member("backfill") is { IsTruthy: true };
        if (!backfill && tx.Query("SELECT 1 FROM work_items WHERE hier_dir = ?1 AND team <> '' LIMIT 1", dir).Count == 0)
        {
            return new JsonObject { ["needs_backfill"] = true };
        }

        EnsureWorkRepo(tx, dir, ms);
        if (backfill)
        {
            var unregistered = tx.Query(
                "SELECT doc FROM plan_items WHERE hier_dir = ?1 AND stream = ?2 AND item NOT IN (SELECT item FROM work_items WHERE hier_dir = ?1 AND team = '' AND stream = ?2)", dir, Backlog.Stream)
                .Select(static r => Backlog.FromDoc((string)r[0]!)).OfType<PlanItem>().OrderBy(static i => i.AddedAt, StringComparer.Ordinal);
            foreach (var item in unregistered)
            {
                var n = RegisterWork(tx, dir, string.Empty, Backlog.Stream, item.Item, item.AddedAt, ms);
                tx.Execute("UPDATE OR IGNORE work_items SET ext_id = ?3 WHERE hier_dir = ?1 AND n = ?2", dir, n, item.Ext);
            }
        }

        foreach (var row in args.Member("register") is { IsArray: true } rows ? rows.AsArray() : [])
        {
            _ = RegisterWork(tx, dir, ArgText(row, "team"), ArgText(row, "stream"), ArgText(row, "item"), OpArgs.Text(row, "added_at"), ms);
        }

        return new JsonObject { ["prefix"] = Work.Prefix((s, a) => tx.Query(s, a), dir)! };
    }

    /// <summary>
    /// An agent's history entry (§6.5): any member, any item, any state. It changes no state, holder or <c>plan_items.updated_ms</c>, so it cannot hide
    /// staleness; only <c>work_items.updated_ms</c> moves. A refusal says why and nothing is written.
    /// </summary>
    private static JsonValue RecordWork(IDbOpTransaction tx, string dir, JsonValue entry, long ms)
    {
        var type = ArgText(entry, "type");
        if (!Work.AgentTypes.Contains(type, StringComparer.Ordinal))
        {
            throw new InvalidOperationException($"--type {type}: must be one of {Work.AgentTypeList}");
        }

        var title = ArgText(entry, "title");
        if (title.Contains('\n', StringComparison.Ordinal))
        {
            throw new InvalidOperationException("--title must be one line");
        }

        title = Work.Clean(title);
        if (title.Length is 0 or > Work.MaxTitle)
        {
            throw new InvalidOperationException($"--title must be 1 to {Work.MaxTitle} characters");
        }

        var body = Work.Clean(OpArgs.Text(entry, "body") ?? string.Empty);
        if (Work.BodyProblem(body) is { } problem)
        {
            throw new InvalidOperationException(problem);
        }

        var n = (long)(entry.Member("n") ?? throw new InvalidOperationException("record needs n")).AsNumber();
        if (tx.Query("SELECT 1 FROM work_items WHERE hier_dir = ?1 AND n = ?2", dir, n).Count == 0)
        {
            throw new InvalidOperationException($"no work item {n}");
        }

        tx.Execute(
            "INSERT INTO work_log(hier_dir, n, ts_ms, type, title, body, by, session) VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8)",
            dir, n, ms, type, title, body.Length == 0 ? null : body, OpArgs.Text(entry, "by"), OpArgs.Text(entry, "session"));
        tx.Execute("UPDATE work_items SET updated_ms = ?3 WHERE hier_dir = ?1 AND n = ?2", dir, n, ms);
        return new JsonObject { ["recorded"] = true };
    }

    /// <summary>
    /// One blocked-by link, or its removal (§7). A link to itself or one that closes a cycle is refused naming the path, walked here inside the write.
    /// Links change no state and gate nothing. Between two backlog rows the row's <c>depends</c> follows in the same transaction, so <c>ah backlog next</c> stays right.
    /// </summary>
    private static JsonObject LinkWork(IDbOpTransaction tx, string dir, JsonValue link, long ms)
    {
        long N(string key) => (long)(link.Member(key) ?? throw new InvalidOperationException("link needs " + key)).AsNumber();
        var blocker = N("blocker");
        var blocked = N("blocked");
        foreach (var n in new[] { blocker, blocked })
        {
            if (tx.Query("SELECT 1 FROM work_items WHERE hier_dir = ?1 AND n = ?2", dir, n).Count == 0)
            {
                throw new InvalidOperationException($"no work item {n}");
            }
        }

        string Id(long n) => WorkIdIn(tx, dir, n);
        var by = OpArgs.Text(link, "by") ?? "user";
        var held = tx.Query("SELECT 1 FROM work_links WHERE hier_dir = ?1 AND blocker_n = ?2 AND blocked_n = ?3", dir, blocker, blocked).Count > 0;
        if (link.Member("remove") is { IsTruthy: true })
        {
            if (held)
            {
                tx.Execute("DELETE FROM work_links WHERE hier_dir = ?1 AND blocker_n = ?2 AND blocked_n = ?3", dir, blocker, blocked);
                LogLink(tx, dir, blocker, blocked, by, ms, removed: true);
            }
        }
        else if (!held)
        {
            if (BlockerPath(tx, dir, from: blocker, to: blocked) is { } path)
            {
                throw new InvalidOperationException($"that would close a cycle: {string.Join(" blocked by ", path.Prepend(blocked).Select(Id))}");
            }

            tx.Execute("INSERT OR IGNORE INTO work_links(hier_dir, blocker_n, blocked_n, by, ts_ms) VALUES (?1, ?2, ?3, ?4, ?5)", dir, blocker, blocked, by, ms);
            LogLink(tx, dir, blocker, blocked, by, ms, removed: false);
        }

        tx.Execute("UPDATE work_items SET updated_ms = ?3 WHERE hier_dir = ?1 AND n IN (?2, ?4)", dir, blocker, ms, blocked);
        SyncDepends(tx, dir, blocked, by, ms);
        return new JsonObject { ["linked"] = true };
    }

    private static string WorkIdIn(IDbOpTransaction tx, string dir, long n) => Work.Id(Work.Prefix((s, a) => tx.Query(s, a), dir) ?? "w", n);

    /// <summary>A link that moved, as an entry on both items: the log is what a successor reads. No record can write this type.</summary>
    private static void LogLink(IDbOpTransaction tx, string dir, long blocker, long blocked, string by, long ms, bool removed)
    {
        var lead = removed ? "no longer " : string.Empty;
        foreach (var (n, title) in new[] { (blocked, $"{lead}blocked by {WorkIdIn(tx, dir, blocker)}"), (blocker, $"{lead}blocks {WorkIdIn(tx, dir, blocked)}") })
        {
            tx.Execute("INSERT INTO work_log(hier_dir, n, ts_ms, type, title, by) VALUES (?1, ?2, ?3, 'link', ?4, ?5)", dir, n, ms, title, by);
        }
    }

    /// <summary>An item's external id and URL (§9): <paramref name="ext"/> empty clears both, <paramref name="url"/> empty clears the URL, null leaves either. A new id drops a URL that labelled the old one.</summary>
    private static void SetWorkExt(IDbOpTransaction tx, string dir, long n, string? ext, string? url, long ms)
    {
        var current = tx.Query("SELECT ext_id, ext_url FROM work_items WHERE hier_dir = ?1 AND n = ?2", dir, n) is { Count: > 0 } row ? row[0] : throw new InvalidOperationException($"no work item {n}");
        var id = ext is null ? current[0] as string : ext.Length == 0 ? null : ext;
        if (url is { Length: > 0 })
        {
            if (id is null)
            {
                throw new InvalidOperationException("--url needs an external id: a URL labels one");
            }

            if (!Work.ValidUrl(url))
            {
                throw new InvalidOperationException($"--url must be an absolute http or https URL of at most {Work.MaxUrl} characters");
            }
        }

        if (id is not null && tx.Query("SELECT n FROM work_items WHERE hier_dir = ?1 AND ext_id = ?2 AND n <> ?3", dir, id, n) is { Count: > 0 } other)
        {
            throw new CodedIoException(Backlog.ConflictCode, $"ext held by {WorkIdIn(tx, dir, (long)other[0][0]!)}");
        }

        var link = id is null ? null : url is null ? (id == current[0] as string ? current[1] as string : null) : url.Length == 0 ? null : url;
        tx.Execute("UPDATE work_items SET ext_id = ?3, ext_url = ?4, updated_ms = ?5 WHERE hier_dir = ?1 AND n = ?2", dir, n, id, link, ms);
    }

    /// <summary>
    /// A reference on an item (§8), never its content. The same ref again updates its title and sha; <c>remove</c> deletes it. A URL must be http or https.
    /// </summary>
    private static JsonObject AttachWork(IDbOpTransaction tx, string dir, JsonValue attach, long ms)
    {
        var n = (long)(attach.Member("n") ?? throw new InvalidOperationException("attach needs n")).AsNumber();
        var reference = ArgText(attach, "ref");
        if (tx.Query("SELECT 1 FROM work_items WHERE hier_dir = ?1 AND n = ?2", dir, n).Count == 0)
        {
            throw new InvalidOperationException($"no work item {n}");
        }

        var kind = OpArgs.Text(attach, "kind");
        var need = OpArgs.Text(attach, "need");

        // a fill names a need its item declares (adr-38 G4)
        if (need is not null && !Work.IsNeedKind(kind ?? string.Empty) && attach.Member("remove") is not { IsTruthy: true }
            && tx.Query("SELECT 1 FROM work_attachments WHERE hier_dir = ?1 AND n = ?2 AND kind IN (?3, ?4) AND ref = ?5", dir, n, Work.NeedKind, Work.ReviewNeedKind, Work.NeedRefPrefix + need).Count == 0)
        {
            throw new InvalidOperationException($"--as {need}: {WorkIdIn(tx, dir, n)} declares no need {need}; declare it: ah work gate {WorkIdIn(tx, dir, n)} --need {need}");
        }

        if (attach.Member("remove") is { IsTruthy: true })
        {
            tx.Execute("DELETE FROM work_attachments WHERE hier_dir = ?1 AND n = ?2 AND ref = ?3", dir, n, reference);

            // a removed need's fills stay as plain attachments
            if (Work.IsNeedKind(kind ?? string.Empty) && need is not null)
            {
                tx.Execute("UPDATE work_attachments SET need = NULL WHERE hier_dir = ?1 AND n = ?2 AND need = ?3", dir, n, need);
            }
        }
        else
        {
            kind = ArgText(attach, "kind");
            if (kind == "url" && !Work.ValidUrl(reference))
            {
                throw new InvalidOperationException($"{reference}: must be an absolute http or https URL of at most {Work.MaxUrl} characters");
            }

            var title = OpArgs.Text(attach, "title") is { } text ? Work.Clean(text) : null;
            if (title is { Length: 0 or > Work.MaxTitle })
            {
                throw new InvalidOperationException($"--title must be 1 to {Work.MaxTitle} characters");
            }

            tx.Execute(
                "INSERT INTO work_attachments(hier_dir, n, ref, kind, title, sha, by, ts_ms, need) VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9) ON CONFLICT(hier_dir, n, ref) DO UPDATE SET kind = excluded.kind, title = COALESCE(excluded.title, title), sha = excluded.sha, by = excluded.by, ts_ms = excluded.ts_ms, need = COALESCE(excluded.need, need)",
                dir, n, reference, kind, title, OpArgs.Text(attach, "sha"), OpArgs.Text(attach, "by"), ms, need);
        }

        tx.Execute("UPDATE work_items SET updated_ms = ?3 WHERE hier_dir = ?1 AND n = ?2", dir, n, ms);
        return new JsonObject { ["attached"] = true };
    }

    /// <summary>The blocked-by path from <paramref name="from"/> down to <paramref name="to"/>, each step included; null when there is none. A loop already in the data ends the walk.</summary>
    private static List<long>? BlockerPath(IDbOpTransaction tx, string dir, long from, long to)
    {
        var seen = new HashSet<long>();
        return Walk(from);

        List<long>? Walk(long n)
        {
            if (!seen.Add(n))
            {
                return null;
            }

            if (n == to)
            {
                return [n];
            }

            foreach (var row in tx.Query("SELECT blocker_n FROM work_links WHERE hier_dir = ?1 AND blocked_n = ?2 ORDER BY blocker_n", dir, n))
            {
                if (Walk((long)row[0]!) is { } rest)
                {
                    rest.Insert(0, n);
                    return rest;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// A backlog row's <c>depends</c> after its links moved: its backlog blockers, after any entry that names no backlog row, which <c>ah backlog next</c> reads as before.
    /// A team item's blocker is not listed: <c>ah backlog next</c> does not see it. Nothing is written when the list is unchanged.
    /// </summary>
    private static void SyncDepends(IDbOpTransaction tx, string dir, long blocked, string by, long ms)
    {
        if (tx.Query("SELECT item FROM work_items WHERE hier_dir = ?1 AND n = ?2 AND team = '' AND stream = ?3", dir, blocked, Backlog.Stream) is not { Count: > 0 } row
            || tx.Query("SELECT doc FROM plan_items WHERE hier_dir = ?1 AND stream = ?2 AND item = ?3", dir, Backlog.Stream, (string)row[0][0]!) is not { Count: > 0 } doc
            || Backlog.FromDoc((string)doc[0][0]!) is not { } current)
        {
            return;
        }

        var blockers = tx.Query(
            "SELECT i.item FROM work_links l JOIN work_items i ON i.hier_dir = l.hier_dir AND i.n = l.blocker_n WHERE l.hier_dir = ?1 AND l.blocked_n = ?2 AND i.team = '' AND i.stream = ?3 ORDER BY l.blocker_n",
            dir, blocked, Backlog.Stream).Select(static r => (string)r[0]!);
        var kept = (current.Depends ?? []).Where(d => tx.Query("SELECT 1 FROM plan_items WHERE hier_dir = ?1 AND stream = ?2 AND item = ?3", dir, Backlog.Stream, d).Count == 0);
        var depends = new JsonArray();
        foreach (var d in kept.Concat(blockers))
        {
            depends.Add(d);
        }

        if (!depends.Select(static d => d.AsString()).SequenceEqual(current.Depends ?? [], StringComparer.Ordinal))
        {
            AppendRows(tx, dir, current.Item, [new JsonObject { ["event"] = "depend", ["depends"] = depends }], ms, by, ProgressFormat.PlanTime(DateTimeOffset.FromUnixTimeMilliseconds(ms)));
        }
    }

    /// <summary><c>ah backlog set --depends</c> (§7): the row's blocked-by links become the rows named, team blockers included in what goes. An entry that names no row, or the row itself, makes no link.</summary>
    private static void ReplaceBlockers(IDbOpTransaction tx, string dir, string item, IEnumerable<string> depends, string by, long ms)
    {
        var blocked = RegisterWork(tx, dir, string.Empty, Backlog.Stream, item, null, ms);
        var before = tx.Query("SELECT blocker_n FROM work_links WHERE hier_dir = ?1 AND blocked_n = ?2", dir, blocked).Select(static r => (long)r[0]!).ToHashSet();
        var after = depends.Where(d => d != item)
            .SelectMany(d => tx.Query("SELECT n FROM work_items WHERE hier_dir = ?1 AND team = '' AND stream = ?2 AND item = ?3", dir, Backlog.Stream, d))
            .Select(static r => (long)r[0]!).ToHashSet();
        foreach (var gone in before.Except(after))
        {
            tx.Execute("DELETE FROM work_links WHERE hier_dir = ?1 AND blocker_n = ?2 AND blocked_n = ?3", dir, gone, blocked);
            LogLink(tx, dir, gone, blocked, by, ms, removed: true);
        }

        // a depends cycle stays in the plan as it is valid today, and no cycle enters the links
        foreach (var added in after.Except(before).Where(a => BlockerPath(tx, dir, from: a, to: blocked) is null))
        {
            tx.Execute("INSERT INTO work_links(hier_dir, blocker_n, blocked_n, by, ts_ms) VALUES (?1, ?2, ?3, ?4, ?5)", dir, added, blocked, by, ms);
            LogLink(tx, dir, added, blocked, by, ms, removed: false);
        }

        tx.Execute("UPDATE work_items SET updated_ms = ?3 WHERE hier_dir = ?1 AND n = ?2", dir, blocked, ms);
    }

    /// <summary>The repo's first use: its row, its default prefix, every backlog row at its own number with the counter at the highest, and the links its <c>depends</c> name.</summary>
    private static void EnsureWorkRepo(IDbOpTransaction tx, string dir, long ms)
    {
        if (tx.Query("SELECT 1 FROM work_repos WHERE hier_dir = ?1", dir).Count > 0)
        {
            return;
        }

        var backlog = tx.Query("SELECT doc FROM plan_items WHERE hier_dir = ?1 AND stream = ?2", dir, Backlog.Stream)
            .Select(static r => Backlog.FromDoc((string)r[0]!)).OfType<PlanItem>().Where(static i => ProgressFormat.IsBacklogId(i.Item)).ToList();
        var prefix = Work.DefaultPrefix(dir);
        var seq = 0L;
        foreach (var item in backlog)
        {
            var n = long.Parse(item.Item[1..], System.Globalization.CultureInfo.InvariantCulture);
            seq = Math.Max(seq, n);
            tx.Execute("INSERT INTO work_items(hier_dir, n, team, stream, item, added_at, ext_id, updated_ms) VALUES (?1, ?2, '', ?3, ?4, ?5, ?6, ?7)", dir, n, Backlog.Stream, item.Item, item.AddedAt, item.Ext, ms);
        }

        var numbered = backlog.Select(static i => i.Item).ToHashSet(StringComparer.Ordinal);
        foreach (var item in backlog)
        {
            var blocked = long.Parse(item.Item[1..], System.Globalization.CultureInfo.InvariantCulture);
            foreach (var dep in (item.Depends ?? []).Where(d => d != item.Item && numbered.Contains(d) && BlockerPath(tx, dir, from: long.Parse(d[1..], System.Globalization.CultureInfo.InvariantCulture), to: blocked) is null))
            {
                tx.Execute(
                    "INSERT OR IGNORE INTO work_links(hier_dir, blocker_n, blocked_n, by, ts_ms) VALUES (?1, ?2, ?3, 'backfill', ?4)",
                    dir, long.Parse(dep[1..], System.Globalization.CultureInfo.InvariantCulture), blocked, ms);
            }
        }

        tx.Execute("INSERT INTO work_repos(hier_dir, prefix, seq, changed_ms) VALUES (?1, ?2, ?3, ?4)", dir, prefix, seq, ms);
        tx.Execute("INSERT OR IGNORE INTO work_prefixes(hier_dir, prefix, set_ms) VALUES (?1, ?2, ?3)", dir, prefix, ms);
    }

    /// <summary>
    /// The number of a placement, allocating one when it is new. A placement registered with another <c>added_at</c> is a slug a later team reused:
    /// the old row moves to <c>slug#n</c>, where it reads as gone and keeps its log, and the new item gets a new number.
    /// </summary>
    private static long RegisterWork(IDbOpTransaction tx, string dir, string team, string stream, string item, string? addedAt, long ms)
    {
        EnsureWorkRepo(tx, dir, ms);
        if (tx.Query("SELECT n, added_at FROM work_items WHERE hier_dir = ?1 AND team = ?2 AND stream = ?3 AND item = ?4", dir, team, stream, item) is { Count: > 0 } held)
        {
            var heldN = (long)held[0][0]!;
            if (addedAt is null || held[0][1] is not string was || was == addedAt)
            {
                return heldN;
            }

            tx.Execute("UPDATE work_items SET item = ?2, updated_ms = ?3 WHERE hier_dir = ?1 AND n = ?4", dir, item + "#" + heldN.ToString(System.Globalization.CultureInfo.InvariantCulture), ms, heldN);
        }

        tx.Execute("UPDATE work_repos SET seq = seq + 1, changed_ms = ?2 WHERE hier_dir = ?1", dir, ms);
        var n = (long)tx.Query("SELECT seq FROM work_repos WHERE hier_dir = ?1", dir)[0][0]!;
        tx.Execute("INSERT INTO work_items(hier_dir, n, team, stream, item, added_at, updated_ms) VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7)", dir, n, team, stream, item, addedAt, ms);
        return n;
    }
}
