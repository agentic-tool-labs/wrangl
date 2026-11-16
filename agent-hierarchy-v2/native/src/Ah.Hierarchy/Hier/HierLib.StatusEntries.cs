using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.Hierarchy;

/// <summary>One persisted status input (ah-msgs-fast-2 §3.8): the dir its input lives in, its kind, its key within the kind, and the input itself.</summary>
internal sealed record StatusEntry(string Pool, string Kind, string Key, JsonValue Json);

/// <summary>The kinds of <see cref="StatusEntry"/>.</summary>
/// <remarks>
/// Orchestrator ruling B3 (delta-status S8b), deviating from r3's kind names under the same rule: entries are input facts, one kind per input
/// file, and team and roster rows are keyed under each status dir S that lists them (not under the pool the file lives in).
/// </remarks>
internal static class StatusKind
{
    /// <summary>Pool <c>S</c>, key <c>X</c>: status dir S lists exchanges of pool X; <c>{ord}</c> is X's place in S's pool list.</summary>
    internal const string View = "view";

    /// <summary>Pool <c>S</c>, key "": <c>{names}</c>, S's team names in listing order.</summary>
    internal const string Teams = "teams";

    /// <summary>Pool <c>S</c>, key = team name ("" = the default team): the team record S reads.</summary>
    internal const string Team = "team";

    /// <summary>Pool <c>X</c>, key = file name: a team file of pool X that can declare a lane, <c>{ord, doc}</c>.</summary>
    internal const string TeamDoc = "team-doc";

    /// <summary>Pool <c>S</c>, key = the latest-roster key: <c>{ord, row}</c>, the newest peer row of a member or session.</summary>
    internal const string Roster = "roster";

    /// <summary>Pool <c>S</c>, key = session id: the session's newest <c>session_pids</c> row, <c>{ord, ts, session_id, pid}</c>.</summary>
    internal const string Session = "session";

    /// <summary>Pool <c>S</c>, key = request id: <c>{count}</c>, the liveness nudges S's gates log holds for it.</summary>
    internal const string Gate = "gate";

    /// <summary>Pool <c>S</c>, key = the activity subject (session id or <c>pane-&lt;name&gt;</c>): the record <c>ReadActivity</c> gives.</summary>
    internal const string Activity = "activity";

    /// <summary>Pool <c>S</c>, key = run anchor id: the run's decisions as <c>ReadDecisions</c> gives them.</summary>
    internal const string Decisions = "decisions";

    /// <summary>Pool <c>X</c>, key = exchange id: the pool's request and response rows with what status reads of each.</summary>
    internal const string Exchange = "exchange";

    /// <summary>Pool "", key = request id: <c>{rows, origin}</c>, its dispatch records in the per-home peer store.</summary>
    internal const string Origin = "origin";

    /// <summary>Pool <c>S</c>, key "": the backlog health facts of S (the rows of the queries <c>Backlog.Health</c> runs), or null.</summary>
    internal const string Backlog = "backlog";

    /// <summary>Pool "", key = session id: a mailbox registration record as the mailbox wrote it.</summary>
    internal const string Registration = "registration";

    /// <summary>Pool <c>P</c>, key team + "\n" + member name: the queue's newest dispatch to that member from P (§3.8 item 7).</summary>
    internal const string LastDispatch = "last-dispatch";

    /// <summary>Pool <c>S</c>, key <c>version</c>: <c>{version}</c>, the entry shape S's rows were built with.</summary>
    internal const string Meta = "meta";
}

internal sealed partial class HierLib
{
    /// <summary>The shape version of the entries; a status dir whose <c>meta</c> row holds another is rebuilt. Bump on any shape or meaning change.</summary>
    internal const int StatusEntriesVersion = 5;

    /// <summary>
    /// The status entries of status dir <paramref name="dir"/>, read from the stores: its pool list, teams, roster, sessions, nudges,
    /// activity, run decisions, and per pool its exchanges, their dispatch records and lane files. Recovery and the parity oracle only;
    /// never per write.
    /// </summary>
    internal List<StatusEntry> RebuildStatusEntries(string dir)
    {
        dir = MsgRows.PoolKey(dir);

        // every message read of the rebuild goes over one connection (b139)
        using var sharedReads = _core.ShareMsgReads();
        var entries = TeamEntries(dir);
        var pools = entries.Where(static e => e.Kind == StatusKind.View).Select(static e => e.Key).ToList();
        entries.Add(new(dir, StatusKind.Backlog, string.Empty, BacklogFactsOf(dir)));

        entries.AddRange(pools.SelectMany(TeamDocEntries));
        entries.AddRange(pools.SelectMany(LastDispatchEntries));
        entries.AddRange(RosterEntries(dir));
        entries.AddRange(SessionEntries(dir));
        entries.AddRange(GateEntries(dir));
        entries.AddRange(ActivityEntries(dir));

        var exchanges = RebuildExchangeEntries(pools);
        entries.AddRange(exchanges);
        foreach (var (id, facts) in DispatchFacts([.. exchanges.Select(static e => e.Key).Distinct(StringComparer.Ordinal)]))
        {
            entries.Add(new(string.Empty, StatusKind.Origin, id, new JsonObject { ["rows"] = facts.Rows, ["origin"] = facts.Origin }));
        }

        foreach (var anchor in exchanges.Where(static e => e.Json.Member("request")?.Member("meta")?.Member("slug") is { IsText: true } s && s.AsString() == HierarchyModule.PipelineAnchorSlug)
            .Select(static e => e.Key).Distinct(StringComparer.Ordinal))
        {
            entries.Add(new(dir, StatusKind.Decisions, anchor, ReadDecisions(DecisionLogPath(dir, anchor))));
        }

        entries.AddRange(RegistrationEntries());
        return entries;
    }

    /// <summary>One global row per mailbox registration record, keyed by its session id (H3).</summary>
    private List<StatusEntry> RegistrationEntries() =>
        [.. (_core.Liveness?.Registrations() ?? []).Select(static r => r.Get("session_id") is { IsText: true } id ? new StatusEntry(string.Empty, StatusKind.Registration, id.AsString(), r) : null)
            .OfType<StatusEntry>()];

    /// <summary>The rows of <see cref="LatestRoster"/> with the key each is kept under, in its order.</summary>
    private List<(string Key, JsonValue Row)> LatestRosterKeyed(string dir)
    {
        var order = new List<string>();
        var byKey = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
        var unique = 0;
        foreach (var rec in ReadRoster(dir))
        {
            var key = Or(rec.Member("name"), Or(rec.Member("session_id"), string.Empty));
            if (!key.IsTruthy)
            {
                continue;
            }

            var k = JsOps.SetKey(key) ?? "ref:" + unique++;
            if (!byKey.ContainsKey(k))
            {
                order.Add(k);
            }

            byKey[k] = rec;
        }

        return [.. order.Select(k => (k, byKey[k]))];
    }

    /// <summary>Per request id, the liveness nudges among <paramref name="gates"/>, the only gate rows status counts.</summary>
    private static Dictionary<string, int> NudgeCounts(JsonArray gates)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var g in gates)
        {
            if (Member(g, "type") is { IsText: true } gt && gt.AsString() == "liveness-nudge" && Member(g, "request_id") is { IsText: true } rid)
            {
                counts[rid.AsString()] = counts.GetValueOrDefault(rid.AsString()) + 1;
            }
        }

        return counts;
    }

    /// <summary>The <see cref="StatusKind.Exchange"/> entries of <paramref name="pools"/>.</summary>
    // every pair of the pool, as the files listing status reads today holds every exchange: a pool's rows then never depend on which
    // status dir rebuilt them, and msgs archive/prune bound them as they bound the files
    internal List<StatusEntry> RebuildExchangeEntries(IReadOnlyList<string> pools)
    {
        var entries = new List<StatusEntry>();
        foreach (var pool in pools)
        {
            // texts, times and team tags are read a chunk at a time over one connection each: per message they were nearly all of a rebuild's
            // time (x53: ~178 s of text reads and ~80 s of team queries over 128k pairs)
            foreach (var chunk in _core.PoolPairs(pool).Chunk(RebuildChunk))
            {
                var paths = chunk.SelectMany(static p => ExchangeParts.Select(part => p[part]).Where(static r => r.IsMap).Select(static r => r.AsObject()["path"].AsString())).ToList();
                var read = _core.ReadMsgTextsAndTimes(paths);
                var texts = paths.Zip(read, static (path, r) => (path, r.Text)).ToDictionary(static e => e.path, static e => e.Text, StringComparer.Ordinal);
                var times = paths.Zip(read, static (path, r) => (path, r.TimeMs)).ToDictionary(static e => e.path, static e => e.TimeMs, StringComparer.Ordinal);
                var teams = _core.MsgTeams([.. chunk.Where(static p => p["request"].IsMap).Select(static p => p["request"].AsObject()["path"].AsString())]);
                foreach (var pair in chunk)
                {
                    entries.Add(new(pool, StatusKind.Exchange, pair["id"].AsString(), ExchangeEntry(pair, texts, teams, times)));
                }
            }
        }

        return entries;
    }

    // ponytail: a rebuild still reads every message of the pool once; a background recovery job, so its ceiling (minutes at x53) is accepted
    private const int RebuildChunk = 2000;

    /// <summary>One exchange's entry, its texts read here (a delta's handful of pairs).</summary>
    private JsonObject ExchangeEntry(JsonObject pair) =>
        ExchangeEntry(pair, ExchangeParts.Select(p => pair[p]).Where(static r => r.IsMap).Select(static r => r.AsObject()["path"].AsString())
            .Distinct(StringComparer.Ordinal).ToDictionary(static p => p, MessageTextAt, StringComparer.Ordinal), null);

    /// <summary>One pool's request and response rows of an exchange, with every fact status reads of each, from texts and team tags read ahead.</summary>
    private JsonObject ExchangeEntry(JsonObject pair, IReadOnlyDictionary<string, string?> texts, IReadOnlyDictionary<string, string?>? teams, IReadOnlyDictionary<string, double?>? times = null)
    {
        var entry = new JsonObject { ["request"] = JsonValue.Null, ["response"] = JsonValue.Null };
        foreach (var part in ExchangeParts)
        {
            if (pair[part] is { IsMap: true } row)
            {
                var path = row.AsObject()["path"].AsString();
                SetExchangePart(entry, part, row.AsObject(), texts.GetValueOrDefault(path), pair["id"].AsString(), teams, times);
            }
        }

        return entry;
    }

    /// <summary>The stored text of the message at <paramref name="path"/> (db bytes or the file), or null when it cannot be read.</summary>
    private string? MessageTextAt(string path) => _core.ReadMsgFile(path).Member("text") is { IsText: true } t ? t.AsString() : null;

    /// <summary>
    /// Sets one part of an exchange entry from the message's file row and text, with every fact status reads of it: the request's front
    /// matter, team tag and title; the response's fields, no-report flag, mtime and report status. One implementation for the rebuild (text
    /// read from the store) and the delta (text the write carried, or the one file it wrote).
    /// </summary>
    private void SetExchangePart(JsonObject entry, string part, JsonObject row, string? text, string id, IReadOnlyDictionary<string, string?>? teams = null, IReadOnlyDictionary<string, double?>? times = null)
    {
        var path = row["path"].AsString();
        entry[part] = row;
        var fields = text is null ? null : MessageText.ParseFrontmatter(text)?.Fields;
        if (part == "request")
        {
            entry["fm"] = fields is { } f ? f : new JsonObject();
            entry["fields"] = fields is { } g ? g : JsonValue.Null;
            entry["tag"] = (teams ?? _core.MsgTeams([path])) is { } stored && stored.TryGetValue(path, out var team)
                ? (team is null ? JsonValue.Null : JsonValue.FromString(team))
                : Or(fields?.Get("team"), JsonValue.Null);
            var (title, source) = text is null ? (null, null) : TitleOfText(text);
            entry["title"] = new JsonArray { StrOrNull(title), StrOrNull(source) };
            return;
        }

        entry["rfields"] = fields is { } h ? h : JsonValue.Null;
        entry["no_report"] = text is not null && System.Text.Encoding.UTF8.GetByteCount(text) <= MsgRows.ReportBytes && MsgRows.ReportStatusOf(text, id) == "no-report"
            || (text is not null && MsgRows.IsPartial(text));
        entry["mtime"] = (times is null ? MtimeMs(path) : times.GetValueOrDefault(path)) is { } ms ? ms : JsonValue.Null;
        entry["status"] = StrOrNull(text is null ? null : ProgressFormat.ExtractReport(text).Status);
    }

    /// <summary>The two parts of an exchange row.</summary>
    private static readonly string[] ExchangeParts = ["request", "response"];

    /// <summary>The kinds whose entries live under the status dir itself; a rebuild of the dir replaces each.</summary>
    private static readonly string[] OwnKinds =
        [StatusKind.View, StatusKind.Teams, StatusKind.Team, StatusKind.Roster, StatusKind.Session, StatusKind.Gate, StatusKind.Activity, StatusKind.Decisions, StatusKind.Backlog, StatusKind.Meta];

    /// <summary>The kinds whose entries live under a pool; a rebuild of any status dir that lists the pool replaces each.</summary>
    private static readonly string[] PoolKinds = [StatusKind.Exchange, StatusKind.TeamDoc, StatusKind.LastDispatch];

    /// <summary>
    /// Recovery: rebuilds the persisted entries of status dir <paramref name="dir"/> from the stores, its own and those of every pool it
    /// lists (idempotent for a pool another status dir shares), then its <c>meta</c> row, in one transaction. It commits only when no write
    /// reached the dir, one of its pools or a global kind since its reads began (F1): a delta that landed meanwhile is never overwritten.
    /// </summary>
    public (bool Ok, string Message) RebuildStatusEntriesOf(string dir)
    {
        if (_statusEntries is not { } store)
        {
            return (false, "no state database");
        }

        // b442: every key this rebuild writes (pool_dir, view keys, the seq suffixes, the dirty mark) comes from this one form
        dir = MsgRows.PoolKey(dir);

        // the daemon (R2-1 ruling P): the deltas this process runs while the rebuild reads are recorded with the sequence bumps they make;
        // the rebuild lands whatever they did, then re-runs them on the rebuilt rows. Elsewhere (no background runner) nothing is recorded
        // and any write since the snapshot discards the rebuild
        var log = RunInBackground is null ? null : new RebuildLog();
        if (log is not null)
        {
            lock (_rebuildLogs)
            {
                _rebuildLogs.Add(log);
            }
        }

        try
        {
            var before = store.Sequences();
            var entries = RebuildStatusEntries(dir);
            entries.Add(new(dir, StatusKind.Meta, "version", new JsonObject { ["version"] = StatusEntriesVersion }));
            var pools = entries.Where(e => e.Pool == dir && e.Kind == StatusKind.View).Select(static e => e.Key).ToList();
            var scopes = OwnKinds.Select(k => (dir, k)).Append((string.Empty, StatusKind.Registration)).Concat(pools.SelectMany(p => PoolKinds.Select(k => (p, k)))).ToList();

            // a crash between the commit and the replay leaves this mark: the graced heal rebuilds the dir
            var mark = StatusDirty.Prefix + "rebuild:" + dir;
            if (log is not null)
            {
                store.MarkDirty(mark, dir);
            }

            List<StatusEntryRow> rows = [.. entries.Select(static e => new StatusEntryRow(e.Pool, e.Kind, e.Key, JsonText.Stringify(e.Json, 0)))];

            // R5: the bumps are read last, just before the Replace's sequence check, so a delta this process ran up to here is counted
            Dictionary<string, long> expected;
            lock (_rebuildLogs)
            {
                if (log is { Overflow: true })
                {
                    store.ClearDirty([mark]);
                    return (false, $"more than {ReplayCap} writes landed during the rebuild");
                }

                // only a write this process did not see (another process's in-process write) moves a sequence past the bumps counted here
                expected = pools.Append(dir).Append(string.Empty).Distinct(StringComparer.Ordinal)
                    .ToDictionary(static d => d, d => before.GetValueOrDefault(d) + (log?.Bumps.GetValueOrDefault(d) ?? 0), StringComparer.Ordinal);
            }

            if (log is null)
            {
                return store.Replace(scopes, [], rows, (long)NowMs, [], expected);
            }

            // R4-1 ruling: the commit holds no lock (a delta during it is recorded and re-run below), and the replay runs one delta at a
            // time through StatusDelta, which holds the status-writer lock (R3-2), so a live write waits at most one replayed delta. The
            // log stays open until drained: a delta that runs meanwhile is recorded too and re-run after the older ones, never before
            var landed = store.Replace(scopes, [], rows, (long)NowMs, [], expected);
            if (landed.Ok)
            {
                log.Committed = true;
                for (var i = 0; NextToReplay(log, i) is { } d; i++)
                {
                    StatusDelta(d.Domain, d.Op, d.Args, d.Result, log);
                }
            }

            store.ClearDirty([mark]);
            return landed;
        }
        finally
        {
            if (log is not null)
            {
                lock (_rebuildLogs)
                {
                    _ = _rebuildLogs.Remove(log);
                }
            }
        }
    }

    /// <summary>How many deltas a rebuild records before it gives up and is rescheduled.</summary>
    // ponytail: past 10k writes during one rebuild it is discarded and run again; record entry keys instead of whole deltas if a busy team hits it
    private const int ReplayCap = 10_000;

    private readonly record struct RecordedDelta(string Domain, string Op, JsonValue Args, JsonValue Result);

    /// <summary>What one in-flight rebuild saw this process write: the deltas to re-run and the sequence bumps they made, per dir.</summary>
    private sealed class RebuildLog
    {
        internal List<RecordedDelta> Deltas { get; } = [];

        internal Dictionary<string, long> Bumps { get; } = new(StringComparer.Ordinal);

        internal bool Overflow { get; set; }

        /// <summary>Whether the rebuild has landed: its log is being replayed and takes every delta, past the cap too.</summary>
        internal bool Committed { get; set; }

    }

    private readonly List<RebuildLog> _rebuildLogs = [];

    /// <summary>The <paramref name="i"/>th recorded delta of <paramref name="log"/>, or null (and the log closed) when it is drained.</summary>
    private RecordedDelta? NextToReplay(RebuildLog log, int i)
    {
        lock (_rebuildLogs)
        {
            if (i < log.Deltas.Count)
            {
                return log.Deltas[i];
            }

            _ = _rebuildLogs.Remove(log);
            return null;
        }
    }

    /// <summary>Records one delta in every in-flight rebuild's log (the daemon's only) but <paramref name="replayOf"/>, whose own re-run it is.</summary>
    private void RecordDelta(string domain, string op, JsonValue args, JsonValue result, RebuildLog? replayOf)
    {
        lock (_rebuildLogs)
        {
            foreach (var log in _rebuildLogs)
            {
                if (ReferenceEquals(log, replayOf))
                {
                    continue;
                }

                if (log.Deltas.Count >= ReplayCap && !log.Committed)
                {
                    log.Overflow = true;
                }
                else
                {
                    log.Deltas.Add(new(domain, op, args, result));
                }
            }
        }
    }

    /// <summary>Counts, in every in-flight rebuild's log, the sequence bumps one store write of this process made.</summary>
    private void CountBumps(IReadOnlyList<string> dirs)
    {
        lock (_rebuildLogs)
        {
            foreach (var log in _rebuildLogs)
            {
                foreach (var d in dirs)
                {
                    log.Bumps[d] = log.Bumps.GetValueOrDefault(d) + 1;
                }
            }
        }
    }

    /// <summary>How long after a row's write its text is still fetched: a write stamped earlier that commits late is still read.</summary>
    private const long ParsedRowTrustMs = 5000;

    /// <summary>
    /// The entries of <paramref name="dir"/>, held per dir: a later read takes only the rows written since the last one (from
    /// <see cref="ParsedRowTrustMs"/> before it) and the read's row count. Null when the store cannot (the caller then reads every row).
    /// </summary>
    private List<StatusEntry>? ReusedEntries(IStatusEntryStore store, string dir)
    {
        var readMs = (long)NowMs;

        // b459 step 27: a dir whose entries are held reads only the rows written since (with the read's row count and write-time sum);
        // a moved count (a row deleted or added), a sum the changed rows do not account for (a row written with an earlier stamp), or a
        // changed row the hold does not have reads the dir in full
        HeldEntries? held;
        lock (_heldEntries)
        {
            _ = _heldEntries.TryGetValue(dir, out held);
        }

        if (held is not null && store.ReadChangedSince(dir, held.ReadMs - ParsedRowTrustMs) is { } delta && delta.Count == held.Entries.Count)
        {
            var entries = new List<StatusEntry>(held.Entries);
            var stamps = (long[])held.Stamps.Clone();
            var sum = held.SumMs;
            var kept = true;
            foreach (var r in delta.Rows)
            {
                if (!held.Index.TryGetValue((r.PoolDir, r.Kind, r.Key), out var at))
                {
                    kept = false;
                    break;
                }

                entries[at] = new StatusEntry(r.PoolDir, r.Kind, r.Key, ParsedRow(r, readMs));
                sum += r.ChangedMs - stamps[at];
                stamps[at] = r.ChangedMs;
            }

            if (kept && sum == delta.SumMs)
            {
                _ = Interlocked.Increment(ref _entryDeltaReads);
                Hold(dir, new HeldEntries(entries, stamps, sum, held.Index, readMs));

                // the caller gets its own list: the held one is never handed out
                return [.. entries];
            }
        }

        // ah2-1: the text comes back only for the rows written within the trust window; an older row is taken from the rows already
        // parsed for another dir, or fetched by its pool and kind. A row that cannot be resolved that way reads the dir with every text.
        if (store.ReadNewerThan(dir, readMs - ParsedRowTrustMs) is not { } rows)
        {
            return null;
        }

        var values = ResolvedRows(store, rows, readMs);
        if (values is null)
        {
            if (store.ReadNewerThan(dir, long.MinValue) is not { } full)
            {
                return null;
            }

            rows = full;
            values = [.. rows.Select(r => ParsedRow(r, readMs))];
        }

        _ = Interlocked.Increment(ref _entryFullReads);
        var all = new List<StatusEntry>(rows.Count);
        for (var i = 0; i < rows.Count; i++)
        {
            all.Add(new StatusEntry(rows[i].PoolDir, rows[i].Kind, rows[i].Key, values[i]));
        }

        var index = new Dictionary<(string, string, string), int>(all.Count);
        for (var i = 0; i < all.Count; i++)
        {
            // a key the read returns twice cannot be swapped in place: such a dir is not held
            if (!index.TryAdd((all[i].Pool, all[i].Kind, all[i].Key), i))
            {
                return all;
            }
        }

        Hold(dir, new HeldEntries(all, [.. rows.Select(static r => r.ChangedMs)], rows.Sum(static r => r.ChangedMs), index, readMs));
        return [.. all];
    }

    /// <summary>
    /// One row's parsed value, shared by every status dir that reads the row (ah2-1): a dir reads its own pool, the pools it views and
    /// every registration, so one row is read by many dirs, and at daemon start each of them fetched and parsed it again. With its text
    /// in hand, a row is the same value while its write time and text are (a text is told apart by its length and hash, not kept).
    /// Without the text, it is the same value while its write time is and it was read at least <see cref="ParsedRowTrustMs"/> after
    /// that time, the trust the delta read gives a row. Shared like a held entry: never mutate one.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string, string, string), (long Ms, int Length, int Hash, long ReadMs, JsonValue Json)> _parsedRows = new();

    private long _rowParses;

    /// <summary>How many status rows were parsed rather than shared, for the tests of the sharing.</summary>
    internal long RowParses => Interlocked.Read(ref _rowParses);

    private JsonValue ParsedRow(StatusEntryStamped r, long readMs) => ParsedRow((r.PoolDir, r.Kind, r.Key), r.ChangedMs, r.Json!, readMs);

    private JsonValue ParsedRow((string, string, string) key, long changedMs, string json, long readMs)
    {
        var hash = json.GetHashCode();
        if (_parsedRows.TryGetValue(key, out var held) && held.Ms == changedMs && held.Length == json.Length && held.Hash == hash)
        {
            return held.Json;
        }

        var parsed = JsonText.Parse(json);
        _ = Interlocked.Increment(ref _rowParses);

        // ponytail: rows that were deleted stay until the table is emptied at this size; evict by dir if a home ever holds this many live rows
        if (_parsedRows.Count >= 500_000)
        {
            _parsedRows.Clear();
        }

        _parsedRows[key] = (changedMs, json.Length, hash, readMs, parsed);
        return parsed;
    }

    /// <summary>A pool and kind with more rows to fetch than this are fetched in one read, not row by row.</summary>
    private const int RowFetchBatch = 8;

    /// <summary>The value of each of <paramref name="rows"/>, in order; null when a row without its text is neither shared nor found in the store.</summary>
    private JsonValue[]? ResolvedRows(IStatusEntryStore store, IReadOnlyList<StatusEntryStamped> rows, long readMs)
    {
        var values = new JsonValue[rows.Count];
        Dictionary<(string Pool, string Kind), List<int>>? missing = null;
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            if (r.Json is not null)
            {
                values[i] = ParsedRow(r, readMs);
            }
            else if (_parsedRows.TryGetValue((r.PoolDir, r.Kind, r.Key), out var held) && held.Ms == r.ChangedMs && held.ReadMs - held.Ms >= ParsedRowTrustMs)
            {
                values[i] = held.Json;
            }
            else
            {
                missing ??= [];
                if (!missing.TryGetValue((r.PoolDir, r.Kind), out var at))
                {
                    missing[(r.PoolDir, r.Kind)] = at = [];
                }

                at.Add(i);
            }
        }

        if (missing is null)
        {
            return values;
        }

        foreach (var ((pool, kind), at) in missing)
        {
            Dictionary<string, string>? texts = null;
            // a dir reads every row of a pool and kind except the origins, where it reads those of its own exchanges: they are fetched one by one
            if (at.Count > RowFetchBatch && kind != StatusKind.Origin)
            {
                texts = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var t in store.ReadKind(pool, kind))
                {
                    texts[t.Key] = t.Json;
                }
            }

            foreach (var i in at)
            {
                var r = rows[i];
                var json = texts is not null
                    ? texts.GetValueOrDefault(r.Key)
                    : store.ReadKind(pool, kind, r.Key) is [var one] ? one.Json : null;
                if (json is null)
                {
                    return null;
                }

                values[i] = ParsedRow((r.PoolDir, r.Kind, r.Key), r.ChangedMs, json, readMs);
            }
        }

        return values;
    }

    private void Hold(string dir, HeldEntries entries)
    {
        lock (_heldEntries)
        {
            _heldEntries[dir] = entries;
        }
    }

    /// <summary>A status dir's exchange listing with the pools and the exchange and team-doc entry values it was built from (b459 step 27b). Shared by later sources: never mutate one.</summary>
    internal sealed record HeldExchangeListing(List<string> Pools, List<JsonValue> Basis, JsonArray Listing);

    private readonly Dictionary<string, HeldExchangeListing> _heldListings = new(StringComparer.Ordinal);

    internal long _listingBuilds;

    /// <summary>How many status exchange listings were built rather than shared, for the tests of the hold.</summary>
    internal long ListingBuilds => Interlocked.Read(ref _listingBuilds);

    private HeldExchangeListing? HeldListing(string dir)
    {
        lock (_heldListings)
        {
            return _heldListings.GetValueOrDefault(dir);
        }
    }

    private void HoldListing(string dir, HeldExchangeListing listing)
    {
        lock (_heldListings)
        {
            _heldListings[dir] = listing;
        }
    }

    /// <summary>A dir's entries as its last read assembled them, with each row's write time, their sum, each key's place and when they were read. Shared by later reads: never mutate one.</summary>
    private sealed record HeldEntries(List<StatusEntry> Entries, long[] Stamps, long SumMs, Dictionary<(string, string, string), int> Index, long ReadMs);

    private readonly Dictionary<string, HeldEntries> _heldEntries = new(StringComparer.Ordinal);

    private long _entryDeltaReads;
    private long _entryFullReads;

    /// <summary>How many status entry reads took only the changed rows, and how many read the dir in full, for the tests of the hold.</summary>
    internal (long Delta, long Full) EntryReads => (Interlocked.Read(ref _entryDeltaReads), Interlocked.Read(ref _entryFullReads));

    /// <summary>The persisted entries of <paramref name="dir"/>, or null when there is no store or its <c>meta</c> is missing or another version.</summary>
    internal List<StatusEntry>? StoredStatusEntries(string dir)
    {
        if (_statusEntries is not { } store)
        {
            return null;
        }

        dir = MsgRows.PoolKey(dir);
        var entries = ReusedEntries(store, dir) ?? store.Read(dir).Select(static r => new StatusEntry(r.PoolDir, r.Kind, r.Key, JsonText.Parse(r.Json))).ToList();
        return entries.Any(e => e.Pool == dir && e.Kind == StatusKind.Meta && e.Json.Member("version") is { IsNumber: true } v && v.AsNumber() == StatusEntriesVersion)
            ? entries
            : null;
    }

    public IReadOnlyList<string> StatusEntryDirs() => _statusEntries?.StatusDirs() ?? [];

    /// <summary>Rebuilds <paramref name="dir"/>'s entries when its <c>meta</c> is missing or stale (daemon start, a first-seen status dir); true when it rebuilt.</summary>
    public bool EnsureStatusEntries(string dir) => _statusEntries is not null && StoredStatusEntries(dir) is null && RebuildStatusEntriesOf(dir).Ok;

    public bool ScheduleStatusEntries(string dir)
    {
        if (_statusEntries is null || RunInBackground is null || StoredStatusEntries(dir) is not null)
        {
            return false;
        }

        ScheduleRebuild(dir);
        return true;
    }

    /// <summary>The status a reader sees (§3.8 item 5): assembled from the stored entries when their <c>meta</c> is current, else computed from the stores.</summary>
    public JsonObject ReadStatus(string? cwd, double? nowMs = null, string? dir = null, bool? enabledOverride = null)
    {
        dir ??= _config.HierarchyDir(cwd);
        if (string.IsNullOrEmpty(dir) || HeldStatusSource(dir) is not { } source)
        {
            return ComputeStatus(cwd, nowMs, dir, enabledOverride);
        }

        // the held source fills its derived caches on first use; one compute at a time per dir
        lock (source)
        {
            return ComputeStatusFrom(source, cwd, nowMs ?? NowMs, dir, enabledOverride);
        }
    }

    private readonly Dictionary<string, (string Stamp, EntryStatusSource Source)> _statusHolds = new(StringComparer.Ordinal);

    private long _statusPatches;

    /// <summary>How many moved stamps patched a held source rather than building one, for the tests of the patch.</summary>
    internal long StatusPatches => Interlocked.Read(ref _statusPatches);

    /// <summary>
    /// The status source of <paramref name="dir"/>, held while its stamp (<c>maxChangedMs:rowCount</c> of its entries) is unchanged (b459
    /// step 14): the entries are not read again and the source's indexes, exchange listing, gates and roster are kept. Liveness, the
    /// config and the clock are read again by every compute. Null when the entries are not current.
    /// </summary>
    private EntryStatusSource? HeldStatusSource(string dir) => HeldStatusSource(dir, out _);

    /// <summary><see cref="HeldStatusSource(string)"/>, with the stamp the source is held under in <paramref name="stamp"/>: null when it is not held (no store revision).</summary>
    private EntryStatusSource? HeldStatusSource(string dir, out string? stamp)
    {
        var key = MsgRows.PoolKey(dir);
        // the stamp is read before the entries: a write between them leaves the older stamp held, so the next read builds again
        stamp = _statusEntries?.Revision(key);
        (string Stamp, EntryStatusSource Source) prior = default;
        lock (_statusHolds)
        {
            EvictUnpolled(key);
            if (stamp is not null && _statusHolds.TryGetValue(key, out prior) && prior.Stamp == stamp)
            {
                return prior.Source;
            }
        }

        if (StoredStatusEntries(dir) is not { } entries)
        {
            return null;
        }

        // b459 step 16: a moved stamp patches the held source with the keys that changed; a full build only when the rows read are not
        // the count the stamp names (a write between the two reads) or the entries cannot be patched in
        if (stamp is not null && prior.Source is { } patchable && StampCount(stamp) == entries.Count)
        {
            lock (patchable)
            {
                if (patchable.Patch(entries) is not null)
                {
                    _ = Interlocked.Increment(ref _statusPatches);
                    lock (_statusHolds)
                    {
                        _statusHolds[key] = (stamp, patchable);
                    }

                    return patchable;
                }
            }
        }

        var source = new EntryStatusSource(this, dir, entries);
        if (stamp is not null)
        {
            lock (_statusHolds)
            {
                _statusHolds[key] = (stamp, source);
            }
        }

        return source;
    }

    /// <summary>How long a dir's held source outlives its last poll (b459 step 18): one is held per status dir ever read, a few MB each.</summary>
    internal const long StatusHoldIdleMs = 10 * 60 * 1000;

    /// <summary>When each dir in <see cref="_statusHolds"/> was last polled. Guarded by <see cref="_statusHolds"/>.</summary>
    private readonly Dictionary<string, long> _statusPolledMs = new(StringComparer.Ordinal);

    /// <summary>Marks <paramref name="key"/> polled now and drops the held source of every dir not polled for <see cref="StatusHoldIdleMs"/>: checked on a poll, no timer. Call under the <see cref="_statusHolds"/> lock.</summary>
    private void EvictUnpolled(string key)
    {
        var now = (long)NowMs;
        _statusPolledMs[key] = now;
        foreach (var idle in _statusPolledMs.Where(p => now - p.Value > StatusHoldIdleMs).Select(static p => p.Key).ToList())
        {
            _ = _statusPolledMs.Remove(idle);
            _ = _statusHolds.Remove(idle);
        }
    }

    /// <summary>How many dirs hold a status source, for the tests of the eviction.</summary>
    internal int StatusHoldCount
    {
        get
        {
            lock (_statusHolds)
            {
                return _statusHolds.Count;
            }
        }
    }

    /// <summary>The row count a status stamp (<c>maxChangedMs:rowCount:counters</c>) names, or -1 when it names none.</summary>
    private static long StampCount(string stamp) =>
        stamp.Split(':') is [_, var count, ..] && long.TryParse(count, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : -1;

    /// <summary>Drops every dir's held status source, so the next read builds it from the entries (the on-demand rebuild).</summary>
    public void ForgetStatusHolds()
    {
        lock (_statusHolds)
        {
            _statusHolds.Clear();
            _statusPolledMs.Clear();
        }

        lock (_heldEntries)
        {
            _heldEntries.Clear();
        }

        lock (_heldListings)
        {
            _heldListings.Clear();
        }
    }

    /// <summary>
    /// The status a publish writes: assembled from the stored entries when their <c>meta</c> is current. A dir with a store but no current
    /// <c>meta</c> (never built, or dropped) is computed from the stores and gets one background rebuild, the recovery path, so later
    /// publishes assemble; the publish never waits on that rebuild.
    /// </summary>
    internal JsonObject StatusDocument(string dir, double nowMs, bool enabled)
    {
        if (StoredStatusEntries(dir) is { } entries)
        {
            return AssembleStatus(dir, entries, nowMs, enabled);
        }

        if (_statusEntries is not null)
        {
            ScheduleRebuild(dir);
        }

        return ComputeStatus(dir, nowMs, dir, enabled);
    }

    /// <summary>The dirs queued or running, each with who waits to hear whether its rebuild landed.</summary>
    private readonly Dictionary<string, List<Action<bool>>> _rebuilding = new(StringComparer.Ordinal);

    /// <summary>
    /// Runs a rebuild off the caller's thread. Null (no background rebuild; the dir stays computed) except in the daemon, which sets it with
    /// its status coalescer: a short-lived process would exit before the rebuild ends, or race its own clean-up. Tests set their own.
    /// </summary>
    internal Action<Action>? RunInBackground { get; set; }

    /// <summary>The background rebuilds waiting their turn, each dir at most once (queued or running: <see cref="_rebuilding"/>).</summary>
    private readonly Queue<string> _rebuildQueue = new();

    /// <summary>Whether a drain of <see cref="_rebuildQueue"/> is running.</summary>
    private bool _draining;

    /// <summary>
    /// Background rebuilds run one at a time, in one queue (R3-1): every rebuild writes the global registration rows, so two at once would
    /// bump each other's sequence and neither would land. A dir already queued or running is not queued again: <paramref name="done"/> waits on that
    /// rebuild. Answering it false would fail every dirty mark but one on the same pool, and each failed mark queues the dirs again, so the queue never empties.
    /// A rebuild a write overtook (F1) is discarded and run once more; one that still does not land is retried by the next publish. <paramref name="done"/>
    /// gets whether it landed; false at once when none could be scheduled.
    /// </summary>
    private void ScheduleRebuild(string dir, Action<bool>? done = null)
    {
        if (RunInBackground is not { } run)
        {
            done?.Invoke(false);
            return;
        }

        dir = MsgRows.PoolKey(dir);
        var start = false;
        lock (_rebuilding)
        {
            if (_rebuilding.TryGetValue(dir, out var waiting))
            {
                if (done is not null)
                {
                    waiting.Add(done);
                }

                return;
            }

            _rebuilding[dir] = done is null ? [] : [done];
            _rebuildQueue.Enqueue(dir);
            start = !_draining;
            _draining = true;
        }

        if (start)
        {
            run(DrainRebuilds);
        }
    }

    private void DrainRebuilds()
    {
        while (true)
        {
            string? next;
            lock (_rebuilding)
            {
                if (!_rebuildQueue.TryDequeue(out next))
                {
                    _draining = false;
                    return;
                }
            }

            var ok = false;
            List<Action<bool>> waiting = [];
            try
            {
                ok = RebuildStatusEntriesOf(next).Ok || RebuildStatusEntriesOf(next).Ok;
            }
            catch (Exception)
            {
                // the dir keeps being computed until a rebuild lands
            }
            finally
            {
                lock (_rebuilding)
                {
                    _ = _rebuilding.Remove(next, out waiting!);
                }
            }

            foreach (var done in waiting)
            {
                try
                {
                    done(ok);
                }
                catch (Exception)
                {
                    // a caller's callback never stops the queue
                }
            }
        }
    }

    /// <summary>The status of <paramref name="dir"/> assembled from <paramref name="entries"/> at <paramref name="nowMs"/>: the same rules as <see cref="ComputeStatus"/>, no store read but pid probes and <c>.git/HEAD</c>.</summary>
    internal JsonObject AssembleStatus(string dir, IReadOnlyList<StatusEntry> entries, double nowMs, bool enabled) =>
        ComputeStatusFrom(new EntryStatusSource(this, dir, entries), dir, nowMs, dir, enabled);

    /// <summary>The status inputs as persisted entries give them.</summary>
    internal sealed class EntryStatusSource : IStatusSource
    {
        private readonly HierLib _lib;
        private List<string> _pools;
        private readonly Dictionary<(string Pool, string Kind, string Key), JsonValue> _by = [];
        private readonly Dictionary<string, JsonObject> _requestFacts = new(StringComparer.Ordinal);
        private readonly Dictionary<string, JsonObject> _responseFacts = new(StringComparer.Ordinal);
        private readonly string _dir;
        private IReadOnlyList<StatusEntry> _entries;
        private JsonArray? _exchanges;

        internal EntryStatusSource(HierLib lib, string dir, IReadOnlyList<StatusEntry> entries)
        {
            _lib = lib;
            _entries = entries;
            foreach (var e in entries)
            {
                _by[(e.Pool, e.Kind, e.Key)] = e.Json;
            }

            _dir = MsgRows.PoolKey(dir);
            _pools = PoolsOf(_dir, entries);
            IndexFacts(entries);
        }

        private static List<string> PoolsOf(string dir, IReadOnlyList<StatusEntry> entries) =>
            [.. entries.Where(e => e.Pool == dir && e.Kind == StatusKind.View).OrderBy(static e => e.Json.Member("ord")!.Value.AsNumber()).Select(static e => e.Key)];

        // the exchange rows by request and by response path, the first entry of a path winning
        private void IndexFacts(IReadOnlyList<StatusEntry> entries)
        {
            _requestFacts.Clear();
            _responseFacts.Clear();
            foreach (var e in entries.Where(static e => e.Kind == StatusKind.Exchange))
            {
                var o = e.Json.AsObject();
                if (o["request"].IsMap)
                {
                    _requestFacts.TryAdd(o["request"].AsObject()["path"].AsString(), o);
                }

                if (o["response"].IsMap)
                {
                    _responseFacts.TryAdd(o["response"].AsObject()["path"].AsString(), o);
                }
            }
        }

        private JsonValue Get(string pool, string kind, string key) => _by.TryGetValue((pool, kind, key), out var v) ? v : JsonValue.Null;

        // a held row comes back as the same parsed value (ReusedEntries), so identity says unchanged; a scalar is compared by its text
        private static bool Same(JsonValue a, JsonValue b) => a.Kind == b.Kind && a.Kind switch
        {
            JsonKind.Map => ReferenceEquals(a.AsObject(), b.AsObject()),
            JsonKind.Array => ReferenceEquals(a.AsArray(), b.AsArray()),
            _ => JsonText.Stringify(a, 0) == JsonText.Stringify(b, 0),
        };

        /// <summary>
        /// Brings the source to <paramref name="entries"/> (b459 step 16): the keys whose value is new, changed or gone are replaced or removed,
        /// and only the derived indexes their kinds feed are rebuilt. Returns the names of those indexes, or null when the entries cannot be
        /// patched in (a key listed twice), and the caller builds the source afresh.
        /// </summary>
        internal IReadOnlyList<string>? Patch(IReadOnlyList<StatusEntry> entries)
        {
            var seen = new HashSet<(string Pool, string Kind, string Key)>();
            var changed = new HashSet<(string Pool, string Kind)>();
            foreach (var e in entries)
            {
                var key = (e.Pool, e.Kind, e.Key);
                if (!seen.Add(key))
                {
                    return null;
                }

                if (!_by.TryGetValue(key, out var held) || !Same(held, e.Json))
                {
                    _ = changed.Add((e.Pool, e.Kind));
                }
            }

            var gone = _by.Keys.Where(k => !seen.Contains(k)).ToList();
            foreach (var key in gone)
            {
                _ = changed.Add((key.Pool, key.Kind));
                _ = _by.Remove(key);
            }

            _entries = entries;
            if (changed.Count == 0)
            {
                return [];
            }

            var regrouped = new Dictionary<(string Pool, string Kind), List<StatusEntry>>();
            foreach (var e in entries)
            {
                if (changed.Contains((e.Pool, e.Kind)))
                {
                    _by[(e.Pool, e.Kind, e.Key)] = e.Json;
                    if (_groups is not null)
                    {
                        if (!regrouped.TryGetValue((e.Pool, e.Kind), out var list))
                        {
                            regrouped[(e.Pool, e.Kind)] = list = [];
                        }

                        list.Add(e);
                    }
                }
            }

            var rebuilt = new List<string> { "by", "groups" };
            foreach (var group in changed)
            {
                _ = _sorted.Remove(group);
                if (_groups is not null)
                {
                    if (regrouped.TryGetValue(group, out var list))
                    {
                        _groups[group] = list;
                    }
                    else
                    {
                        _ = _groups.Remove(group);
                    }
                }
            }

            var kinds = changed.Select(static g => g.Kind).ToHashSet(StringComparer.Ordinal);
            if (kinds.Contains(StatusKind.Exchange))
            {
                IndexFacts(entries);
                rebuilt.Add("facts");
            }

            if (changed.Contains((_dir, StatusKind.View)))
            {
                _pools = PoolsOf(_dir, entries);
                rebuilt.Add("pools");
            }

            if (kinds.Contains(StatusKind.Exchange) || kinds.Contains(StatusKind.TeamDoc) || changed.Contains((_dir, StatusKind.View)))
            {
                _exchanges = null;
                rebuilt.Add("exchanges");
            }

            if (kinds.Contains(StatusKind.Registration))
            {
                _registrations = null;
                rebuilt.Add("registrations");
            }

            if (kinds.Contains(StatusKind.Roster) || kinds.Contains(StatusKind.Teams) || kinds.Contains(StatusKind.Team))
            {
                _rosters.Clear();
                rebuilt.Add("rosters");
            }

            if (kinds.Contains(StatusKind.Gate))
            {
                _gates.Clear();
                rebuilt.Add("gates");
            }

            return rebuilt;
        }

        private Dictionary<(string Pool, string Kind), List<StatusEntry>>? _groups;

        private readonly Dictionary<(string Pool, string Kind), List<StatusEntry>> _sorted = [];

        // the entries grouped by (pool, kind) in one pass, each group sorted once on first use: a lookup, not a scan of every entry per call
        private List<StatusEntry> OfKind(string pool, string kind)
        {
            if (_sorted.TryGetValue((pool, kind), out var done))
            {
                return done;
            }

            _groups ??= _entries.GroupBy(static e => (e.Pool, e.Kind)).ToDictionary(static g => g.Key, static g => g.ToList());
            var sorted = _groups.TryGetValue((pool, kind), out var group)
                ? [.. group.OrderBy(static e => e.Json.Member("ord")?.AsNumber() ?? 0).ThenBy(static e => e.Key, StringComparer.Ordinal)]
                : new List<StatusEntry>();
            _sorted[(pool, kind)] = sorted;
            return sorted;
        }

        // b442: a dir lookup answers whichever spelling the caller passes; the pools this holds are keys already
        public IReadOnlyList<string> TeamNames(string dir) =>
            Get(MsgRows.PoolKey(dir), StatusKind.Teams, string.Empty).Member("names") is { IsArray: true } names ? [.. names.AsArray().Select(static n => n.AsString())] : [];

        public JsonValue TeamRecord(string dir, string? team) => Get(MsgRows.PoolKey(dir), StatusKind.Team, team ?? string.Empty);

        public bool Orphaned(JsonValue team) =>
            (_lib._core.Liveness?.TeamLifeOf(team, Registrations) ?? _lib._roster.TeamLifeOf(team)) == Ah.Roster.TeamLife.Orphaned;

        private List<JsonObject>? _registrations;

        // the mailbox registration records, from their entries: no registrations listing at assembly, only the pid probes
        private List<JsonObject> Registrations => _registrations ??=
            [.. _entries.Where(static e => e.Kind == StatusKind.Registration && e.Json.IsMap).Select(static e => e.Json.AsObject())];

        private readonly Dictionary<string, JsonArray> _rosters = new(StringComparer.Ordinal);
        private readonly Dictionary<string, JsonArray> _gates = new(StringComparer.Ordinal);

        // held with the source (b459 step 14): built from entries only, and read, never changed, downstream
        public JsonArray Roster(string dir)
        {
            dir = MsgRows.PoolKey(dir);
            if (!_rosters.TryGetValue(dir, out var built))
            {
                _rosters[dir] = built = BuildRoster(dir);
            }

            return built;
        }

        private JsonArray BuildRoster(string dir)
        {
            var recs = new JsonArray();
            foreach (var e in OfKind(dir, StatusKind.Roster))
            {
                recs.Add(e.Json.Member("row")!.Value);
            }

            var teams = new List<(string? Name, JsonValue Team)>();
            foreach (var name in new string?[] { null }.Concat(TeamNames(dir)))
            {
                teams.Add((name, TeamRecord(dir, name)));
            }

            return AttributedRoster(recs, Ah.Roster.RosterModule.PaneResolverOver(teams));
        }

        public IReadOnlyList<JsonValue> SessionRows(string dir) => [.. OfKind(MsgRows.PoolKey(dir), StatusKind.Session).Select(static e => e.Json)];

        public JsonArray Exchanges(string dir)
        {
            if (_exchanges is not null)
            {
                return _exchanges;
            }

            // b459 step 27b: the listing is built from the pools' exchange and team-doc entries alone (the facts are those exchange rows),
            // so a source whose pools and those entries are the ones a held listing was built from shares that listing
            List<JsonValue> basis = [.. _pools.SelectMany(p => OfKind(p, StatusKind.Exchange).Concat(OfKind(p, StatusKind.TeamDoc))).Select(static e => e.Json)];
            if (_lib.HeldListing(_dir) is { } held && held.Pools.SequenceEqual(_pools, StringComparer.Ordinal) && held.Basis.Count == basis.Count
                && held.Basis.Zip(basis).All(static p => Same(p.First, p.Second)))
            {
                _exchanges = held.Listing;
                return _exchanges;
            }

            _exchanges = BuildExchanges();
            _lib.HoldListing(_dir, new HeldExchangeListing([.. _pools], basis, _exchanges));
            return _exchanges;
        }

        private JsonArray BuildExchanges()
        {
            var byId = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            foreach (var pool in _pools)
            {
                foreach (var e in OfKind(pool, StatusKind.Exchange))
                {
                    if (!byId.TryGetValue(e.Key, out var pair))
                    {
                        pair = new JsonObject { ["id"] = e.Key, ["request"] = JsonValue.Null, ["response"] = JsonValue.Null };
                        byId[e.Key] = pair;
                    }

                    foreach (var part in ExchangeParts)
                    {
                        if (!pair[part].IsMap && e.Json.Member(part) is { IsMap: true } row)
                        {
                            pair[part] = row;
                        }
                    }
                }
            }

            var withRequest = HierarchyCore.WithRequestNewestFirst(byId.Values);
            var lanes = LaneCharters.FromDocs([.. _pools.SelectMany(p => OfKind(p, StatusKind.TeamDoc).Select(e => (e.Key, e.Json.Member("doc")!.Value)))]);
            // AnyAnsweredSubOrchestrator per pool, over the pool's own rows (a request and its response both filed there)
            var mayHaveCharter = HierarchyCore.AnswersASubOrchestrator(withRequest) || _pools.Any(p => OfKind(p, StatusKind.Exchange).Any(static e =>
                e.Json.Member("request") is { IsMap: true } q && e.Json.Member("response") is { IsMap: true } && q.Member("meta")?.Member("to") is { IsText: true } to && to.AsString() == "sub-orchestrator"));
            IReadOnlySet<string> charters = mayHaveCharter && !lanes.IsEmpty
                ? lanes.OpenCharterIds(HierarchyCore.CharterFacts(withRequest, lanes, Fields))
                : new HashSet<string>(StringComparer.Ordinal);
            _ = Interlocked.Increment(ref _lib._listingBuilds);
            return HierarchyCore.Listed(withRequest, charters, e => ResponseFacts(e["response"].AsObject()["path"].AsString())?["no_report"].IsTruthy == true);
        }

        private JsonValue? Fields(string path)
        {
            JsonValue? found = _requestFacts.TryGetValue(path, out var r) ? r.Get("fields") : _responseFacts.TryGetValue(path, out var s) ? s.Get("rfields") : null;
            return found is { IsNull: false } f ? (JsonValue?)f : null;
        }

        private JsonObject? RequestFacts(JsonValue exchange) =>
            Member(Member(exchange, "request"), "path") is { IsText: true } p && _requestFacts.TryGetValue(p.AsString(), out var f) ? f : null;

        private JsonObject? ResponseFacts(string path) => _responseFacts.TryGetValue(path, out var f) ? f : null;

        public JsonArray Gates(string dir)
        {
            dir = MsgRows.PoolKey(dir);
            if (!_gates.TryGetValue(dir, out var built))
            {
                _gates[dir] = built = BuildGates(dir);
            }

            return built;
        }

        private JsonArray BuildGates(string dir)
        {
            var rows = new JsonArray();
            foreach (var e in OfKind(dir, StatusKind.Gate))
            {
                for (var i = 0; i < e.Json.Member("count")!.Value.AsNumber(); i++)
                {
                    rows.Add(new JsonObject { ["type"] = "liveness-nudge", ["request_id"] = e.Key });
                }
            }

            return rows;
        }

        public JsonValue RequestFm(JsonValue exchange) => RequestFacts(exchange)?.Get("fm") is { IsMap: true } fm ? fm : new JsonObject();

        public (string? Title, string? Source) Title(JsonValue exchange) =>
            RequestFacts(exchange)?.Get("title") is { IsArray: true } t
                ? (t.AsArray()[0].IsText ? t.AsArray()[0].AsString() : null, t.AsArray()[1].IsText ? t.AsArray()[1].AsString() : null)
                : (null, null);

        public double? ResponseMtime(string path) => ResponseFacts(path)?.Get("mtime") is { IsNumber: true } ms ? ms.AsNumber() : null;

        public string? ResponseStatus(string path) => ResponseFacts(path)?.Get("status") is { IsText: true } s ? s.AsString() : null;

        public Dictionary<string, JsonValue> Origins(JsonArray requests) =>
            OriginsOf(requests, ids => ids.ToDictionary(
                static id => id,
                id => Get(string.Empty, StatusKind.Origin, id) is { IsMap: true } o ? ((int)o.Member("rows")!.Value.AsNumber(), o.Member("origin")!.Value) : (0, JsonValue.Null),
                StringComparer.Ordinal));

        public JsonValue Activity(string dir, string file) => ActivitySubjectOf(file) is { } subject ? Get(MsgRows.PoolKey(dir), StatusKind.Activity, subject) : JsonValue.Null;

        public MemberRegistrationLiveness? Registration(string name, string team) => _lib._core.Liveness?.ForMember(name, team, Registrations);

        public JsonArray RunExchanges(string dir, string? team, long sinceMs) => Exchanges(dir);

        public JsonObject Decisions(string dir, string anchorId) =>
            Get(MsgRows.PoolKey(dir), StatusKind.Decisions, anchorId) is { IsMap: true } d ? d.AsObject() : _lib.ReadDecisions(string.Empty);

        public BacklogHealth? Backlog(string dir, double nowMs, Action<double>? flip = null) =>
            BacklogFromFacts(Get(MsgRows.PoolKey(dir), StatusKind.Backlog, string.Empty), dir, nowMs, flip);

        // the four below and MemberLife read outside the entries: the config, the disk, the process table and liveness' own clock
        public bool Enabled(string? cwd) => _lib._config.ConfigEnabled(cwd);

        public bool HasDir(string dir) => _lib.Exists(dir);

        public string? Branch(string workTree) => HierarchyModule.BranchOfWorkTree(workTree, _lib.ReadTextOrNull);

        public string? Checkout(string cwd) => HierarchyModule.CheckoutOfCwd(cwd, p => _lib._files.Stat(p) is not null);

        public (bool Live, string? SessionId) MemberLife(JsonArray roster, JsonValue name, string? team) => _lib.MemberLifeOf(roster, name, team, Registration);

        public IReadOnlyList<JsonValue> TeamTags(IReadOnlyList<JsonValue> exchanges) =>
            [.. exchanges.Select(e => RequestFacts(e)?.Get("tag") is { } tag ? tag : JsonValue.Null)];
    }
}
