using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.StateDb;

/// <summary>The <c>status_entries</c> table (schema 12) over the state database of <paramref name="ahHome"/>.</summary>
internal sealed class StateDbStatusEntries(string ahHome, StateDbReadPool? pool = null) : IStatusEntryStore
{
    private const string Columns = "pool_dir, kind, key, json";

    // own rows of S, every row of the dirs S's view rows key, and the global rows S reads: origins of their exchanges, registrations
    private static string ReadOf(string columns) => ViewKeys + RowsOf(columns);

    private const string ViewKeys = "WITH v AS (SELECT key FROM status_entries WHERE pool_dir = ?1 AND kind = 'view') ";

    private static string RowsOf(string columns) =>
        "SELECT " + columns + " FROM status_entries WHERE pool_dir = ?1 OR pool_dir IN (SELECT key FROM v) "
        + "UNION ALL SELECT " + columns + " FROM status_entries WHERE pool_dir = '' AND kind = 'registration' "
        + "UNION ALL SELECT " + columns + " FROM status_entries WHERE pool_dir = '' AND kind = 'origin' "
        + "AND key IN (SELECT key FROM status_entries WHERE kind = 'exchange' AND pool_dir IN (SELECT key FROM v))";

    internal static readonly string ReadSql = ReadOf(Columns);

    private static readonly string ReadNewerSql = ReadOf("pool_dir, kind, key, changed_ms, CASE WHEN changed_ms > ?2 THEN json END");

    // b459 step 27: the rows of the read written after ?2, then one row with the read's row count and the sum of its write times, in one
    // statement (one snapshot)
    private static readonly string ChangedSinceSql = ViewKeys
        + "SELECT pool_dir, kind, key, json, changed_ms FROM (" + RowsOf("pool_dir, kind, key, json, changed_ms") + ") WHERE changed_ms > ?2 "
        + "UNION ALL SELECT NULL, NULL, NULL, count(*), coalesce(sum(changed_ms), 0) FROM (" + RowsOf("changed_ms") + ")";

    // the write counters of every dir the read spans (its own, its view keys, the global rows), as written: a counter only rises, so a
    // write at the same or an earlier ms, or one that keeps the count, still moves the revision
    private static readonly string RevisionSql = ViewKeys + "SELECT MAX(changed_ms), count(*), "
        + "(SELECT group_concat(k, ',') FROM (SELECT substr(key, length(?2) + 1) || '=' || value AS k FROM meta "
        + "WHERE key IN (SELECT ?2 || ?1 UNION SELECT ?2 || key FROM v UNION SELECT ?2) ORDER BY key)) "
        + "FROM (" + RowsOf("changed_ms") + ")";

    public IReadOnlyList<StatusEntryRow> Read(string statusDir) => Rows(Query(ReadSql, statusDir));

    public string? Revision(string statusDir) =>
        Query(RevisionSql, statusDir, SeqPrefix) is [[long max, long count, var seqs]] && count > 0
            ? max.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + count.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + (seqs as string ?? string.Empty)
            : string.Empty;

    public StatusEntriesDelta? ReadChangedSince(string statusDir, long sinceMs)
    {
        var rows = Query(ChangedSinceSql, statusDir, sinceMs);
        if (rows.Count == 0)
        {
            return null;
        }

        // the summary row is the one with no pool_dir: a stored row always has one
        var summary = rows.Single(static r => r[0] is null);
        return new StatusEntriesDelta(
            (long)summary[3]!,
            (long)summary[4]!,
            [.. rows.Where(static r => r[0] is not null).Select(static r => new StatusEntryStamped((string)r[0]!, (string)r[1]!, (string)r[2]!, (long)r[4]!, (string)r[3]!))]);
    }

    public IReadOnlyList<StatusEntryStamped>? ReadNewerThan(string statusDir, long sinceMs) =>
        [.. Query(ReadNewerSql, statusDir, sinceMs).Select(static r => new StatusEntryStamped((string)r[0]!, (string)r[1]!, (string)r[2]!, (long)r[3]!, r[4] as string))];

    public IReadOnlyList<string> StatusDirs() =>
        [.. Query("SELECT pool_dir FROM status_entries WHERE kind = 'meta' ORDER BY pool_dir").Select(static r => (string)r[0]!)];

    public IReadOnlyList<StatusEntryRow> ReadKind(string poolDir, string kind, string? key = null) =>
        Rows(key is null
            ? Query("SELECT " + Columns + " FROM status_entries WHERE pool_dir = ?1 AND kind = ?2 ORDER BY key", poolDir, kind)
            : Query("SELECT " + Columns + " FROM status_entries WHERE pool_dir = ?1 AND kind = ?2 AND key = ?3", poolDir, kind, key));

    public IReadOnlyList<string> Listing(string poolDir) =>
        [.. Query("SELECT pool_dir FROM status_entries WHERE kind = 'view' AND key = ?1 ORDER BY pool_dir", poolDir).Select(static r => (string)r[0]!)];

    public IReadOnlyList<string> PoolsWith(string kind, string key) =>
        [.. Query("SELECT pool_dir FROM status_entries WHERE kind = ?1 AND key = ?2 ORDER BY pool_dir", kind, key).Select(static r => (string)r[0]!)];

    public (bool Ok, string Message) Replace(
        IReadOnlyList<(string PoolDir, string Kind)> scopes, IReadOnlyList<(string PoolDir, string Kind, string Key)> deletes, IReadOnlyList<StatusEntryRow> rows, long changedMs) =>
        Replace(scopes, deletes, rows, changedMs, [], null);

    public IReadOnlyDictionary<string, long> Sequences() =>
        Query("SELECT key, value FROM meta WHERE substr(key, 1, ?1) = ?2", SeqPrefix.Length, SeqPrefix)
            .ToDictionary(static r => ((string)r[0]!)[SeqPrefix.Length..], static r => SeqValue(r[1]), StringComparer.Ordinal);

    public (bool Ok, string Message) Replace(
        IReadOnlyList<(string PoolDir, string Kind)> scopes, IReadOnlyList<(string PoolDir, string Kind, string Key)> deletes, IReadOnlyList<StatusEntryRow> rows, long changedMs,
        IReadOnlyList<string> touched, IReadOnlyDictionary<string, long>? expected)
    {
        var result = new StateDbDirect(ahHome, DirectWriteOptions.Default).Write(tx =>
        {
            // F1: a rebuild commits only when no write reached its dirs since its snapshot began
            foreach (var (dir, seq) in expected ?? new Dictionary<string, long>())
            {
                if (SeqValue(tx.Query("SELECT value FROM meta WHERE key = ?1", SeqPrefix + dir) is [var held] ? held[0] : null) != seq)
                {
                    return false;
                }
            }

            foreach (var dir in StatusEntryRow.WrittenDirs(scopes, deletes, rows, touched))
            {
                tx.Execute("INSERT INTO meta(key, value) VALUES (?1, '1') ON CONFLICT(key) DO UPDATE SET value = CAST(CAST(value AS INTEGER) + 1 AS TEXT)", SeqPrefix + dir);
            }

            // b459 step 21b: a scope keeps the rows it is given again; only its keys absent from the new rows are deleted
            foreach (var (pool, kind) in scopes)
            {
                var kept = rows.Where(r => r.PoolDir == pool && r.Kind == kind).Select(static r => r.Key).ToHashSet(StringComparer.Ordinal);
                foreach (var held in tx.Query("SELECT key FROM status_entries WHERE pool_dir = ?1 AND kind = ?2", pool, kind))
                {
                    if (!kept.Contains((string)held[0]!))
                    {
                        tx.Execute("DELETE FROM status_entries WHERE pool_dir = ?1 AND kind = ?2 AND key = ?3", pool, kind, held[0]);
                    }
                }
            }

            foreach (var (pool, kind, key) in deletes)
            {
                tx.Execute("DELETE FROM status_entries WHERE pool_dir = ?1 AND kind = ?2 AND key = ?3", pool, kind, key);
            }

            // an unchanged row is not written: it keeps its changed_ms, so a delta reader takes it from what it already parsed
            foreach (var row in rows)
            {
                tx.Execute(
                    "INSERT INTO status_entries(pool_dir, kind, key, json, changed_ms) VALUES (?1, ?2, ?3, ?4, ?5) "
                    + "ON CONFLICT(pool_dir, kind, key) DO UPDATE SET json = excluded.json, changed_ms = excluded.changed_ms WHERE json <> excluded.json",
                    row.PoolDir, row.Kind, row.Key, row.Json, changedMs);
            }

            return true;
        });
        return result is { Ok: true, Value: false } ? (false, "a write reached these status entries since the rebuild began") : (result.Ok, result.Message);
    }

    /// <summary>The <c>meta</c> key prefix of a dir's write sequence: bumped by every write of its entries, read by a rebuild.</summary>
    private const string SeqPrefix = "status.seq:";

    private static long SeqValue(object? value) => value switch
    {
        long n => n,
        string s when long.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n) => n,
        _ => 0,
    };

    // a value is "<ms>\t<pool dir>"; one with no ms (written before the ms was kept) counts as old
    public IReadOnlyList<(string Key, string PoolDir)> DirtyMarks(long olderThanMs = 0) =>
        [.. Query($"SELECT key, value, {StatusDirty.NowMsSql} FROM meta WHERE substr(key, 1, ?1) = ?2 ORDER BY key", StatusDirty.Prefix.Length, StatusDirty.Prefix)
            .Select(static r => (Key: (string)r[0]!, Value: r[1] as string ?? string.Empty, Now: SeqValue(r[2])))
            .Select(static m => (m.Key, m.Value, m.Now, Tab: m.Value.IndexOf('\t', StringComparison.Ordinal)))
            .Select(static m => (m.Key, Pool: m.Tab < 0 ? m.Value : m.Value[(m.Tab + 1)..], Age: m.Tab < 0 ? long.MaxValue : m.Now - SeqValue(m.Value[..m.Tab])))
            .Where(m => m.Age >= olderThanMs)
            .Select(static m => (m.Key, m.Pool))];

    public void MarkDirty(string key, string poolDir) => _ = new StateDbDirect(ahHome, DirectWriteOptions.Default).Write(tx =>
    {
        tx.Execute(StatusDirty.MarkSql, key, poolDir);
        return true;
    });

    public void ClearDirty(IReadOnlyList<string> keys)
    {
        // read first: a files-mode write has no mark, and its AfterWrite must not pay for a write transaction
        var held = keys.Where(k => Query("SELECT 1 FROM meta WHERE key = ?1", k).Count > 0).ToList();
        if (held.Count == 0)
        {
            return;
        }

        _ = new StateDbDirect(ahHome, DirectWriteOptions.Default).Write(tx =>
        {
            foreach (var key in held)
            {
                tx.Execute("DELETE FROM meta WHERE key = ?1", key);
            }

            return true;
        });
    }

    private static List<StatusEntryRow> Rows(IReadOnlyList<object?[]> rows) =>
        [.. rows.Select(static r => new StatusEntryRow((string)r[0]!, (string)r[1]!, (string)r[2]!, (string)r[3]!))];

    private IReadOnlyList<object?[]> Query(string sql, params object?[] args)
    {
        var opened = StateDbReader.Open(ahHome, pool);
        if (opened.Reader is not { } reader)
        {
            // a home with no state database yet holds no entries: status falls back to the files and the write that asked goes on.
            // A database lost after a cut-over (StoreProblem.Missing) still throws: that is an alert, not an empty store.
            if (!File.Exists(StateDbBackups.DatabasePath(ahHome)) && StateDbAuthority.Check(ahHome) is not { Problem: StoreProblem.Missing })
            {
                return [];
            }

            throw new InvalidOperationException($"the state database cannot be read: {opened.Message}");
        }

        using (reader)
        {
            return reader.Query(sql, args);
        }
    }
}
