using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Text;

namespace Ah.Hierarchy;

/// <summary>
/// Status deltas (ah-msgs-fast-2 §3.8 item 3, S8b). After a write, each delta recomputes only the entries the write's own input determines
/// (the text the write carried, or the one file it wrote), stores them, then publishes every status dir whose <c>view</c> rows list the dir
/// the input lives in (fan-out, item 4). A delta never lists or reads messages, team files or registrations beyond that input; a delta the
/// database refuses drops the <c>meta</c> row of every status dir it reaches, so the next start or first sight rebuilds once.
/// </summary>
internal sealed partial class HierLib
{
    /// <summary>Stores one delta and publishes every status dir that lists one of <paramref name="pools"/>.</summary>
    private void ApplyStatusDelta(
        IReadOnlyList<(string Pool, string Kind)> scopes, IReadOnlyList<(string Pool, string Kind, string Key)> deletes, IReadOnlyList<StatusEntry> rows, IReadOnlyList<string> pools)
    {
        if (_statusEntries is not { } store)
        {
            return;
        }

        var dirs = pools.SelectMany(store.Listing).Distinct(StringComparer.Ordinal).ToList();
        var touched = _touched.Value ?? [];
        _touched.Value = null;
        List<StatusEntryRow> written = [.. rows.Select(static e => new StatusEntryRow(e.Pool, e.Kind, e.Key, JsonText.Stringify(e.Json, 0)))];
        if (store.Replace(scopes, deletes, written, (long)NowMs, touched, null) is { Ok: false })
        {
            InvalidateStatusEntries(dirs);
        }
        else
        {
            CountBumps(StatusEntryRow.WrittenDirs(scopes, deletes, written, touched));
        }

        foreach (var dir in dirs)
        {
            RefreshStatus(dir);
        }
    }

    /// <summary>Drops the <c>meta</c> row of each of <paramref name="dirs"/>: their status is computed from the stores until a rebuild.</summary>
    private void InvalidateStatusEntries(IEnumerable<string> dirs)
    {
        try
        {
            _ = _statusEntries?.Replace([], [.. dirs.Select(static d => (d, StatusKind.Meta, "version"))], [], (long)NowMs);
        }
        catch (Exception)
        {
            // nothing more can be done here; the stale meta is caught by the next rebuild check
        }
    }

    /// <summary>Whether any status dir lists <paramref name="dir"/>: a dir no status reads keeps no entries.</summary>
    private bool Listed(string dir) => _statusEntries is { } store && store.Listing(dir).Count > 0;

    /// <summary>
    /// The status side of a write: its delta, which publishes every status dir it reaches; a <paramref name="refreshDir"/> no status dir with
    /// entries lists yet (before its first-seen rebuild) gets today's full refresh instead.
    /// </summary>
    internal void AfterStatusWrite(string domain, string op, ReadOnlyMemory<byte> argsJson, ReadOnlyMemory<byte> resultJson, string? refreshDir)
    {
        try
        {
            if (!string.IsNullOrEmpty(refreshDir) && !Listed(MsgRows.PoolKey(refreshDir)))
            {
                RefreshStatus(refreshDir);
            }

            if (JsonText.TryParse(argsJson.Span, out var args))
            {
                StatusDelta(domain, op, args, JsonText.TryParse(resultJson.Span, out var result) ? result : JsonValue.Null);
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // the write has landed: a database that cannot be read costs a rebuild, as a delta it refuses does, and the write's dirty mark stays for it
            return;
        }

        // H1 ruling B: the delta ran, so the mark the write's database body left (if it ran in db mode) is cleared
        if (DirtyMarked.Contains((domain, op)))
        {
            try
            {
                _statusEntries?.ClearDirty([StatusDirty.KeyOf(domain, op, argsJson)]);
            }
            catch (Exception)
            {
                // a mark left behind only costs one rebuild at the next start or status-dir refresh
            }
        }
    }

    /// <summary>The database op bodies <see cref="StatusDirtyMark"/> wraps.</summary>
    private static readonly HashSet<(string Domain, string Op)> DirtyMarked =
        [("msgs", "create"), ("msgs", "settle"), ("msgs", "fill"), ("msgs", "retire"), ("msgs", "archive"), ("msgs", "seal"), ("peer-records", "append"), ("peer-records", "compact"), ("progress", Backlog.WriteOp)];

    /// <summary>
    /// How old a dirty mark must be before it is healed: far beyond a write's commit-to-clear window, so a write still in its POST is
    /// never taken for a crash (F2 ruling).
    /// </summary>
    internal long DirtyGraceMs { get; set; } = DefaultDirtyGraceMs;

    private const long DefaultDirtyGraceMs = 30_000;

    /// <summary>The marks whose heal is running: a later tick does not schedule them again.</summary>
    private readonly HashSet<string> _healing = new(StringComparer.Ordinal);

    /// <summary>
    /// Recovery for H1 ruling B: a dirty mark older than <see cref="DirtyGraceMs"/>, which a crash left between a database write's commit
    /// and its delta, gets a background rebuild of the status dirs that list its pool (every status dir when it names none); each dir that
    /// lands is republished, and the mark is cleared only when every dir it reached landed (F5). Never rebuilds on the caller's thread.
    /// </summary>
    public void HealDirtyStatus()
    {
        if (_statusEntries is not { } store || store.DirtyMarks(DirtyGraceMs) is not { Count: > 0 } marks)
        {
            return;
        }

        foreach (var (key, pool) in marks)
        {
            lock (_healing)
            {
                if (!_healing.Add(key))
                {
                    continue;
                }
            }

            // b442: a mark written before the keys were canonical still names its raw pool
            var dirs = pool.Length == 0 ? store.StatusDirs() : store.Listing(MsgRows.PoolKey(pool));
            var left = dirs.Count;
            var allOk = true;
            void Done(string dir, bool ok)
            {
                if (ok)
                {
                    RefreshStatus(dir);
                }

                bool last;
                lock (_healing)
                {
                    allOk &= ok;
                    last = --left == 0;
                }

                if (last)
                {
                    Healed(key, allOk);
                }
            }

            if (dirs.Count == 0)
            {
                Healed(key, true);
                continue;
            }

            foreach (var dir in dirs)
            {
                ScheduleRebuild(dir, ok => Done(dir, ok));
            }
        }
    }

    private void Healed(string key, bool clear)
    {
        try
        {
            if (clear)
            {
                _statusEntries?.ClearDirty([key]);
            }
        }
        catch (Exception)
        {
            // the mark stays and a later tick heals it again
        }
        finally
        {
            lock (_healing)
            {
                _ = _healing.Remove(key);
            }
        }
    }

    /// <summary>
    /// The dirs a running delta's write names (empty for a global kind), whose write sequence the delta's store write bumps (F1); a delta
    /// that writes no entry (no status dir lists the dir yet) bumps them on its own, so a rebuild whose reads began before it never lands.
    /// </summary>
    private readonly AsyncLocal<List<string>?> _touched = new();

    private static List<string> NamedDirs(string domain, string op, JsonValue args) => domain switch
    {
        "msgs" or "peers" or "gates" or "session-pids" or "team-state" => [.. new[] { DirArg(args, "dir") }.OfType<string>()],
        "activity" => [.. new[] { DirArg(args, "dir"), DirArg(args, "mirror_dir") }.OfType<string>()],
        "decisions" => TextOf(args.Member("path")) is { } path && PoolOfLog(path) is { } pool ? [MsgRows.PoolKey(pool)] : [],
        "peer-records" or "mailbox" => [string.Empty],
        "progress" when op == Backlog.WriteOp => [string.Empty],
        _ => [],
    };

    /// <summary>A write's dir argument as the status entries key it (b442: <see cref="MsgRows.PoolKey"/>); null when absent.</summary>
    private static string? DirArg(JsonValue args, string name) => TextOf(args.Member(name)) is { } dir ? MsgRows.PoolKey(dir) : null;

    /// <summary>
    /// The status-writer lock (R3-2): one delta at a time, a rebuild's replayed ones included, so a replayed delta never read-modify-writes
    /// alongside a live one. Reentrant: a delta's own publish may run a rebuild inline (tests' runner).
    /// </summary>
    private readonly Lock _statusWrite = new();

    /// <summary>Runs one delta; an exception drops the reached status dirs' <c>meta</c> instead of failing the write.</summary>
    internal void StatusDelta(string domain, string op, JsonValue args, JsonValue result) => StatusDelta(domain, op, args, result, null);

    /// <summary>Runs one delta, or (<paramref name="replayOf"/> set) a rebuild's re-run of one, which that rebuild's log does not record again.</summary>
    private void StatusDelta(string domain, string op, JsonValue args, JsonValue result, RebuildLog? replayOf)
    {
        if (_statusEntries is null)
        {
            return;
        }

        lock (_statusWrite)
        {
            StatusDeltaLocked(domain, op, args, result, replayOf);
        }
    }

    private void StatusDeltaLocked(string domain, string op, JsonValue args, JsonValue result, RebuildLog? replayOf)
    {
        RecordDelta(domain, op, args, result, replayOf);
        _touched.Value = NamedDirs(domain, op, args);
        try
        {
            StatusDeltaOf(domain, op, args, result);
        }
        finally
        {
            if (_touched.Value is { Count: > 0 } left)
            {
                try
                {
                    if (_statusEntries!.Replace([], [], [], (long)NowMs, left, null).Ok)
                    {
                        CountBumps(left);
                    }
                }
                catch (Exception)
                {
                    // no readable database: no rebuild can be in flight against it
                }
            }

            _touched.Value = null;
        }
    }

    private void StatusDeltaOf(string domain, string op, JsonValue args, JsonValue result)
    {
        try
        {
            switch (domain)
            {
                case "msgs":
                    MsgsDelta(op, args, result);
                    break;
                case "peers":
                    OwnDelta(DirArg(args, "dir"), StatusKind.Roster, RosterEntries);
                    break;
                case "gates":
                    OwnDelta(DirArg(args, "dir"), StatusKind.Gate, GateEntries);
                    break;
                case "session-pids":
                    OwnDelta(DirArg(args, "dir"), StatusKind.Session, SessionEntries);
                    break;
                case "decisions":
                    DecisionsDelta(TextOf(args.Member("path")));
                    break;
                case "activity":
                    ActivityDelta(op, args);
                    break;
                case "peer-records":
                    OriginsDelta(op, args);
                    break;
                case "team-state":
                    TeamDelta(DirArg(args, "dir"));
                    break;
                case "progress" when op == Backlog.WriteOp:
                    BacklogDelta();
                    break;
                case "mailbox":
                    RegistrationDelta(op, args, result);
                    break;
            }
        }
        catch (Exception)
        {
            try
            {
                InvalidateStatusEntries(_statusEntries!.StatusDirs());
            }
            catch (Exception)
            {
                // no readable database: no stored entries to go stale, and a status write never fails the write that triggered it
            }
        }
    }

    /// <summary>Replaces every row of one own kind of status dir <paramref name="dir"/> from the one file its write wrote.</summary>
    private void OwnDelta(string? dir, string kind, Func<string, List<StatusEntry>> rowsOf)
    {
        if (!string.IsNullOrEmpty(dir) && Listed(dir))
        {
            ApplyStatusDelta([(dir, kind)], [], rowsOf(dir), [dir]);
        }
    }

    // one reader per own kind, shared by the rebuild and the delta: each re-reads the one file its write wrote

    private List<StatusEntry> SessionEntries(string dir) =>
        [.. SessionStatusRows(dir).Select(r => new StatusEntry(dir, StatusKind.Session, r["session_id"]!.AsString(), r))];

    private List<StatusEntry> RosterEntries(string dir) =>
        [.. LatestRosterKeyed(dir).Select((r, i) => new StatusEntry(dir, StatusKind.Roster, r.Key, new JsonObject { ["ord"] = i, ["row"] = r.Row }))];

    private List<StatusEntry> GateEntries(string dir) =>
        [.. NudgeCounts(ReadGates(dir)).Select(g => new StatusEntry(dir, StatusKind.Gate, g.Key, new JsonObject { ["count"] = g.Value }))];

    private List<StatusEntry> TeamDocEntries(string pool) =>
        [.. LaneCharters.ReadDocs(_files, [pool], _roster).Select((d, i) => new StatusEntry(pool, StatusKind.TeamDoc, d.Name, new JsonObject { ["ord"] = i, ["doc"] = d.Doc }))];

    /// <summary>
    /// A message write in pool <c>dir</c>: create/settle carry the text they wrote; fill/retire read the one file they wrote. Archive, seal and
    /// prune remove messages: the pool's name listing (no message is read) says which rows go (orchestrator ruling B2).
    /// </summary>
    private void MsgsDelta(string op, JsonValue args, JsonValue result)
    {
        var dir = DirArg(args, "dir");
        if (string.IsNullOrEmpty(dir) || !Listed(dir))
        {
            return;
        }

        if (op is "archive" or "seal")
        {
            RemovedMessagesDelta(dir);
            return;
        }

        if (result.Member("existing") is { IsTruthy: true } || TextOf(args.Member("path")) is not { } path || _core.FileRowOf(path) is not { } row)
        {
            return;
        }

        var text = op is "create" or "settle" ? TextOf(args.Member("content")) : MessageTextAt(path);
        var id = row["meta"].AsObject()["id"].AsString();
        var part = row["meta"].AsObject()["type"].AsString();
        var entry = StoredExchange(dir, id) ?? new JsonObject { ["request"] = JsonValue.Null, ["response"] = JsonValue.Null };
        var beforeFm = entry.Get("fm");
        SetExchangePart(entry, part, row, text, id);
        List<StatusEntry> rows = [new(dir, StatusKind.Exchange, id, entry)];
        List<(string, string, string)> deletes = [];
        if (part == "request" && op == "create")
        {
            // a new request raises its member's last dispatch from the front matter just parsed (§3.8 item 7)
            rows.AddRange(LastDispatchRaised(dir, entry["fm"]));
        }
        else if (part == "request" && op == "settle")
        {
            // a settle can rewrite what a request says: its member's last dispatch moves from what it said to what it says (F4); a fill
            // or retire changes no created
            var addr = FmText(beforeFm, "to_name") is { Length: > 0 } && FmText(entry["fm"], "to_name") is { Length: > 0 }
                ? null
                : PeerRecordsOf([id]).TryGetValue(id, out var recs) ? FmText(recs[0], "to_addr") : null;
            var (moved, gone) = LastDispatchMoved(dir, [new DispatchChange(id, beforeFm, addr, entry["fm"], addr)]);
            rows.AddRange(moved);
            deletes.AddRange(gone);
        }

        ApplyStatusDelta([], deletes, rows, [dir]);
    }

    private JsonObject? StoredExchange(string dir, string id) =>
        _statusEntries!.ReadKind(dir, StatusKind.Exchange, id) is [var stored] ? JsonText.Parse(stored.Json).AsObject() : null;

    /// <summary>The rows of messages gone from pool <paramref name="dir"/> are deleted; a pair that lost one part is rebuilt from what is left.</summary>
    private void RemovedMessagesDelta(string dir)
    {
        var listed = _core.PoolPairs(dir).ToDictionary(static p => p["id"].AsString(), StringComparer.Ordinal);
        var deletes = new List<(string, string, string)>();
        var rows = new List<StatusEntry>();
        var changed = new List<(string Id, JsonValue? Before, JsonValue? After)>();
        foreach (var stored in _statusEntries!.ReadKind(dir, StatusKind.Exchange))
        {
            var entry = JsonText.Parse(stored.Json);
            if (!listed.TryGetValue(stored.Key, out var pair))
            {
                deletes.Add((dir, StatusKind.Exchange, stored.Key));
                changed.Add((stored.Key, entry.Member("fm"), null));
                continue;
            }

            if (ExchangeParts.Any(p => TextOf(Member(Member(entry, p), "path")) != TextOf(Member(pair[p], "path"))))
            {
                var rebuilt = ExchangeEntry(pair);
                rows.Add(new(dir, StatusKind.Exchange, stored.Key, rebuilt));
                changed.Add((stored.Key, entry.Member("fm"), rebuilt.Get("fm")));
            }
        }

        // a removal can take a member's newest request: only the members of the removed requests move (§3.8 item 7, F4)
        var unnamed = changed.Where(static c => FmText(c.Before, "to_name") is not { Length: > 0 } || FmText(c.After, "to_name") is not { Length: > 0 }).Select(static c => c.Id).ToList();
        var peers = PeerRecordsOf(unnamed);
        var (moved, gone) = LastDispatchMoved(dir, [.. changed.Select(c =>
        {
            var addr = peers.TryGetValue(c.Id, out var recs) ? FmText(recs[0], "to_addr") : null;
            return new DispatchChange(c.Id, c.Before, addr, c.After, addr);
        })]);
        rows.AddRange(moved);
        deletes.AddRange(gone);
        ApplyStatusDelta([], deletes, rows, [dir]);
    }

    /// <summary>A decision write: that run's decisions, from the one log it wrote.</summary>
    private void DecisionsDelta(string? path)
    {
        if (path is null || IsAsksLog(path) || NodePath.Basename(path) != "decisions.jsonl" || PoolOfLog(path) is not { } raw)
        {
            return;
        }

        var dir = MsgRows.PoolKey(raw);
        if (!Listed(dir))
        {
            return;
        }

        var runId = NodePath.Basename(NodePath.Dirname(path));
        ApplyStatusDelta([], [], [new(dir, StatusKind.Decisions, runId, ReadDecisions(path))], [dir]);
    }

    /// <summary>An activity write: record/clear touch one file in the dir and its mirror; a sweep re-reads the dir it swept.</summary>
    private void ActivityDelta(string op, JsonValue args)
    {
        var dirs = new[] { DirArg(args, "dir"), DirArg(args, "mirror_dir") }.Where(d => !string.IsNullOrEmpty(d) && Listed(d!)).Select(static d => d!).Distinct(StringComparer.Ordinal).ToList();
        foreach (var dir in dirs)
        {
            if (op == "sweep")
            {
                ApplyStatusDelta([(dir, StatusKind.Activity)], [], ActivityEntries(dir), [dir]);
            }
            else if (ActivityFile(args.Member("subject")) is not null)
            {
                var subject = args.Member("subject")!.Value.AsString();
                var exists = _activity?.Row(dir, subject) is not null;
                ApplyStatusDelta([], exists ? [] : [(dir, StatusKind.Activity, subject)], exists ? [new(dir, StatusKind.Activity, subject, ReadActivityRecord(dir, args.Member("subject")))] : [], [dir]);
            }
        }
    }

    private List<StatusEntry> ActivityEntries(string dir) =>
        [.. (_activity?.Rows(dir) ?? []).OrderBy(static r => r.Subject, StringComparer.Ordinal).Select(r => new StatusEntry(dir, StatusKind.Activity, r.Subject, ParseActivity(r.Body)))];

    /// <summary>A dispatch record append: the origin facts of the request ids it names; a compaction re-derives every stored origin.</summary>
    private void OriginsDelta(string op, JsonValue args)
    {
        var ids = op == "compact"
            ? [.. _statusEntries!.ReadKind(string.Empty, StatusKind.Origin).Select(static r => r.Key)]
            : (args.Member("rec") is { IsArray: true } many ? many.AsArray() : new JsonArray { args.Member("rec") ?? JsonValue.Null })
                .Where(static r => Member(r, "type") is { IsText: true } t && t.AsString() == "dispatch" && Member(r, "request_id").IsText)
                .Select(static r => Member(r, "request_id").AsString()).Distinct(StringComparer.Ordinal).ToList();
        if (ids.Count == 0)
        {
            return;
        }

        var rows = DispatchFacts(ids).Select(f => new StatusEntry(string.Empty, StatusKind.Origin, f.Key, new JsonObject { ["rows"] = f.Value.Rows, ["origin"] = f.Value.Origin })).ToList();
        var pools = ids.SelectMany(id => _statusEntries!.PoolsWith(StatusKind.Exchange, id).Select(p => (Pool: p, Id: id))).ToList();

        // a request with no to_name is listed under its first peer record's to_addr: an append that gives it its first record (or a compact
        // that drops it) renames its last dispatch; the member moves from that one request's records (F3); one that names its member is untouched
        var unnamed = pools.Select(p => (p.Pool, p.Id, Fm: StoredExchange(p.Pool, p.Id)?.Get("fm"))).Where(static p => p.Fm is { IsMap: true } && FmText(p.Fm, "to_name") is not { Length: > 0 }).ToList();
        var deletes = new List<(string, string, string)>();
        var scopes = new List<(string, string)>();
        if (op == "compact")
        {
            // a compact's earlier first records are gone: the pools of its unnamed requests recompute every member from stored rows (a rare maintenance op)
            foreach (var pool in unnamed.Select(static p => p.Pool).Distinct(StringComparer.Ordinal))
            {
                scopes.Add((pool, StatusKind.LastDispatch));
                rows.AddRange(NewestStored(pool, null, []).Values.Select(f => LastDispatchEntry(pool, f)));
            }
        }
        else if (unnamed.Count > 0)
        {
            var peers = PeerRecordsOf([.. unnamed.Select(static p => p.Id)]);
            foreach (var group in unnamed.GroupBy(static p => p.Pool, StringComparer.Ordinal))
            {
                var changes = group.Select(p =>
                {
                    var recs = peers.TryGetValue(p.Id, out var r) ? r : [];
                    var after = recs.Count > 0 ? FmText(recs[0], "to_addr") : null;

                    // the records before an append are those it did not add: its own come last
                    return new DispatchChange(p.Id, p.Fm, recs.Count > Appended(args, p.Id) ? after : null, p.Fm, after);
                }).ToList();
                var (moved, gone) = LastDispatchMoved(group.Key, changes);
                rows.AddRange(moved);
                deletes.AddRange(gone);            }
        }

        ApplyStatusDelta(scopes, deletes, rows, [.. pools.Select(static p => p.Pool).Distinct(StringComparer.Ordinal)]);
    }

    /// <summary>How many of the records an append carries name <paramref name="id"/>.</summary>
    private static int Appended(JsonValue args, string id) =>
        (args.Member("rec") is { IsArray: true } many ? many.AsArray() : new JsonArray { args.Member("rec") ?? JsonValue.Null })
            .Count(r => Member(r, "request_id") is { IsText: true } t && t.AsString() == id);

    /// <summary>
    /// A team file write in <paramref name="dir"/>: every status dir that lists the dir re-derives its team entries and pool list from its
    /// team files (no message is read); a pool it newly lists gets its exchange rows, as a first sight of that pool.
    /// </summary>
    private void TeamDelta(string? dir)
    {
        if (string.IsNullOrEmpty(dir) || _statusEntries is not { } store)
        {
            return;
        }

        foreach (var status in store.Listing(dir))
        {
            var before = store.ReadKind(status, StatusKind.View).Select(static r => r.Key).ToHashSet(StringComparer.Ordinal);
            var rows = TeamEntries(status);
            var pools = rows.Where(e => e.Kind == StatusKind.View).Select(static e => e.Key).ToList();
            rows.AddRange(pools.SelectMany(TeamDocEntries));

            rows.AddRange(RebuildExchangeEntries([.. pools.Where(p => !before.Contains(p) && store.Listing(p).Count == 0)]));
            ApplyStatusDelta(
                [(status, StatusKind.View), (status, StatusKind.Teams), (status, StatusKind.Team), .. pools.Select(static p => (p, StatusKind.TeamDoc))], [], rows, [status]);
        }
    }

    /// <summary>The <c>view</c>, <c>teams</c> and <c>team</c> entries of status dir <paramref name="dir"/>, from its team files.</summary>
    private List<StatusEntry> TeamEntries(string dir)
    {
        dir = MsgRows.PoolKey(dir);
        var names = _roster.ListTeamNames(dir);
        var pools = PoolDirs(dir, names).Select(MsgRows.PoolKey).Distinct(StringComparer.Ordinal).ToList();
        var entries = pools.Select((p, i) => new StatusEntry(dir, StatusKind.View, p, new JsonObject { ["ord"] = i })).ToList();
        entries.Add(new(dir, StatusKind.Teams, string.Empty, new JsonObject { ["names"] = ToArray(names) }));
        entries.AddRange(new string?[] { null }.Concat(names).Select(key => new StatusEntry(dir, StatusKind.Team, key ?? string.Empty, _roster.ReadTeamRecord(dir, key))));
        return entries;
    }

    /// <summary>A backlog write: the health facts of every status dir (each keys its backlog by its main checkout).</summary>
    /// <summary>
    /// H3: a register that wrote re-reads the session's one registration record into its global row; an unregister that removed deletes it.
    /// Every status dir is republished.
    /// </summary>
    // ponytail: republishes every status dir, as the backlog delta does; narrow to the dirs whose teams name the record's addresses if it shows in the perf table
    private void RegistrationDelta(string op, JsonValue args, JsonValue result)
    {
        if (TextOf(args.Member("session_id")) is not { Length: > 0 } session)
        {
            return;
        }

        var changed = op == "register" ? result.Member("registered") is { IsTruthy: true } : result.Member("removed") is { IsTruthy: true };
        var dirs = _statusEntries!.StatusDirs();
        if (!changed || dirs.Count == 0)
        {
            return;
        }

        var record = op == "register" ? _core.Liveness?.RegistrationOf(session) : null;
        ApplyStatusDelta(
            [], [(string.Empty, StatusKind.Registration, session)], record is null ? [] : [new StatusEntry(string.Empty, StatusKind.Registration, session, record)], dirs);
    }

    private void BacklogDelta()
    {
        var dirs = _statusEntries!.StatusDirs();
        ApplyStatusDelta([.. dirs.Select(static d => (d, StatusKind.Backlog))], [], [.. dirs.Select(d => new StatusEntry(d, StatusKind.Backlog, string.Empty, BacklogFactsOf(d)))], dirs);
    }
}
