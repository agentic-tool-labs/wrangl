using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.StateDb;

/// <summary>The <c>roster_records</c> table (schema 5, read since schema 26) over the state database of <paramref name="ahHome"/>.</summary>
internal sealed class StateDbRosterRecords(string ahHome) : IRosterRecordSource
{
    private const int ImportBusyMs = 250;

    /// <summary>
    /// The rows only (files-off-disk §4: the import pass takes the file). A database that cannot be read (busy, newer, missing table)
    /// reads as no rows and is never written.
    /// </summary>
    public IReadOnlyList<string> Bodies(string dir)
    {
        if (StateDbAuthority.NeverCreated(ahHome))
        {
            return [];
        }

        // the key is the dir's real path, as peers.append stores it: any spelling of one dir reads one roster
        var hierDir = StateDbTeamKey.RealPath(dir);
        try
        {
            return StateDbStoreReads.Reading(ahHome, connection =>
                StateDbRows.Run(connection, "SELECT CAST(raw AS TEXT) FROM roster_records WHERE pool_dir = ?1 ORDER BY seq", [hierDir]).Select(static r => (string)r[0]!).ToList());
        }
        catch (Exception e) when (e is StoreUnavailableException or InvalidOperationException)
        {
            return [];
        }
    }

    public string? Stamp(string dir)
    {
        if (StateDbAuthority.NeverCreated(ahHome))
        {
            return null;
        }

        var hierDir = StateDbTeamKey.RealPath(dir);
        try
        {
            return StateDbStoreReads.Reading(ahHome, connection =>
                StateDbRows.Run(connection, "SELECT (SELECT seq FROM domain_seq WHERE domain = 'peers'), (SELECT count(*) FROM roster_records WHERE pool_dir = ?1)", [hierDir]) is [var r]
                    ? $"{r[0]}:{r[1]}"
                    : null);
        }
        catch (Exception e) when (e is StoreUnavailableException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The meta key recording that this home took <paramref name="file"/>'s rows into the table.</summary>
    internal static string TakenKey(string file) => "peers.taken:" + StateDbTeamKey.RealPath(file);

    /// <summary>The import pass's take of <paramref name="dir"/>'s <c>peers.jsonl</c> (files-off-disk §3.2), keyed by real path; false when nothing was taken.</summary>
    internal bool Take(string dir)
    {
        var hierDir = StateDbTeamKey.RealPath(dir);
        return Import(hierDir, Path.Join(hierDir, "peers.jsonl"));
    }

    /// <summary>
    /// The upgrade's one take of a dir's <c>peers.jsonl</c> (per-dir-files-to-db S7): its rows, each JSON line as written and a bad line
    /// skipped, stored before every row the table already holds, and the take recorded, in one write transaction. The file stays where
    /// it is. False when nothing was taken (taken already, busy, unreadable): the read goes on with what the table holds.
    /// </summary>
    private bool Import(string hierDir, string file)
    {
        List<string> lines;
        try
        {
            lines = [.. File.ReadAllLines(file).Select(static l => l.Trim()).Where(static l => l.Length > 0 && IsJsonMap(l))];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        try
        {
            using var connection = SqliteConnection.Open(StateDbBackups.DatabasePath(ahHome), SqliteOpenMode.ReadWrite, ImportBusyMs);
            connection.Exec("BEGIN IMMEDIATE");
            try
            {
                if (StateDbRows.Run(connection, "SELECT 1 FROM meta WHERE key = ?1", [TakenKey(file)]).Count > 0)
                {
                    connection.Exec("ROLLBACK");
                    return false;
                }

                // seq is table-wide: below the lowest one, so a row written after the file's lines stays the later one
                var first = StateDbRows.Run(connection, "SELECT MIN(seq) FROM roster_records", []) is [[long min]] ? min : 1;
                for (var i = 0; i < lines.Count; i++)
                {
                    _ = StateDbRows.Run(
                        connection,
                        "INSERT OR IGNORE INTO roster_records(seq, pool_dir, session_id, at_ms, raw, op_id) VALUES (?1, ?2, json_extract(?3, '$.session_id'), NULL, ?3, json_extract(?3, '$.op_id'))",
                        [first - lines.Count + i, hierDir, lines[i]]);
                }

                _ = StateDbRows.Run(connection, "INSERT INTO meta(key, value) VALUES (?1, ?2)", [TakenKey(file), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture)]);
                connection.Exec("COMMIT");
                return true;
            }
            catch
            {
                try
                {
                    connection.Exec("ROLLBACK");
                }
                catch (SqliteException)
                {
                    // the transaction is already gone
                }

                throw;
            }
        }
        catch (SqliteException)
        {
            // busy or not writable: a later read takes the file
            return false;
        }
    }

    private static bool IsJsonMap(string line)
    {
        try
        {
            return JsonText.Parse(line).IsMap;
        }
        catch (JsonParseException)
        {
            return false;
        }
    }
}
