using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.StateDb;

/// <summary>The <c>gates</c> table (schema 23) over the state database of <paramref name="ahHome"/>.</summary>
internal sealed class StateDbGates(string ahHome) : IGateSource
{
    private const int ImportBusyMs = 250;

    public IReadOnlyList<string> Bodies(string hierDir) => Bodies(hierDir, strict: false);

    public IReadOnlyList<string>? BodiesOrNull(string hierDir, out string? why)
    {
        why = null;
        try
        {
            return Bodies(hierDir, strict: true);
        }
        catch (Exception e) when (e is StoreUnavailableException or InvalidOperationException)
        {
            why = e.Message;
            return null;
        }
    }

    public string? Stamp(string hierDir)
    {
        if (StateDbAuthority.NeverCreated(ahHome))
        {
            return "0:0";
        }

        try
        {
            return StateDbStoreReads.Reading(ahHome, connection =>
                StateDbRows.Run(connection, "SELECT count(*), COALESCE(MAX(seq), 0) FROM gates WHERE hier_dir = ?1", [MsgRows.PoolKey(hierDir)]) is [[long count, long last]]
                    ? count.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + last.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : null);
        }
        catch (Exception e) when (e is StoreUnavailableException or InvalidOperationException)
        {
            return null;
        }
    }

    public GateRowsAfter? After(string hierDir, long seq)
    {
        if (StateDbAuthority.NeverCreated(ahHome))
        {
            return new GateRowsAfter(0, []);
        }

        // one statement, so the count and the rows come from one snapshot; the count row sorts first whatever the seqs (an import writes seq <= 0)
        var read = StateDbStoreReads.Reading(ahHome, connection => StateDbRows.Run(
            connection,
            "SELECT 0, NULL, NULL, count(*) FROM gates WHERE hier_dir = ?1 UNION ALL SELECT 1, seq, body, 0 FROM gates WHERE hier_dir = ?1 AND seq > ?2 ORDER BY 1, 2",
            [MsgRows.PoolKey(hierDir), seq]));
        return new GateRowsAfter(read.Count > 0 ? (long)read[0][3]! : 0, [.. read.Skip(1).Select(static r => new GateRowAt((long)r[1]!, (string)r[2]!))]);
    }

    /// <summary>The meta key recording that this home took <paramref name="file"/>'s rows into the table.</summary>
    internal static string TakenKey(string file) => "gates.taken:" + StateDbTeamKey.RealPath(file);

    /// <summary>
    /// The rows only (files-off-disk §4: the import pass takes the file). A database that cannot be read (busy, newer, missing table) reads
    /// as no rows and is never written; <paramref name="strict"/> throws on it instead.
    /// </summary>
    private List<string> Bodies(string hierDir, bool strict)
    {
        if (StateDbAuthority.NeverCreated(ahHome))
        {
            return [];
        }

        try
        {
            return StateDbStoreReads.Reading(ahHome, connection =>
                StateDbRows.Run(connection, "SELECT body FROM gates WHERE hier_dir = ?1 ORDER BY seq", [MsgRows.PoolKey(hierDir)]).Select(static r => (string)r[0]!).ToList());
        }
        catch (Exception e) when (!strict && e is StoreUnavailableException or InvalidOperationException)
        {
            return [];
        }
    }

    /// <summary>The import pass's take of <paramref name="hierDir"/>'s <c>gates.jsonl</c> (files-off-disk §3.2); false when nothing was taken.</summary>
    internal bool Take(string hierDir) => Import(MsgRows.PoolKey(hierDir), Path.Join(hierDir, "gates.jsonl"));

    /// <summary>
    /// The upgrade's one take of a dir's <c>gates.jsonl</c> (per-dir-files-to-db S3): its rows, each JSON line as written and a bad line
    /// skipped, stored before any row the table already holds for the dir, and the take recorded, in one write transaction. The file
    /// stays where it is. False when nothing was taken (taken already, busy, unreadable): the read goes on with what the table holds.
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

                // before the first row stored since the upgrade, so a row written after the file's lines stays the later one
                var first = StateDbRows.Run(connection, "SELECT MIN(seq) FROM gates WHERE hier_dir = ?1", [hierDir]) is [[long min]] ? min : 1;
                for (var i = 0; i < lines.Count; i++)
                {
                    _ = StateDbRows.Run(connection, "INSERT INTO gates(hier_dir, seq, body, at) VALUES (?1, ?2, ?3, '')", [hierDir, first - lines.Count + i, lines[i]]);
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
