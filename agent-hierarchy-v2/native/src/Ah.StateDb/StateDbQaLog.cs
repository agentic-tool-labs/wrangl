using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.StateDb;

/// <summary>The <c>qa_log</c> table (schema 25, per-dir-files-to-db S6) over the state database of <paramref name="ahHome"/>.</summary>
internal sealed class StateDbQaLog(string ahHome) : IQaLogSource
{
    private const int ImportBusyMs = 250;

    /// <summary>
    /// The rows only (files-off-disk §4: the import pass takes the file). A database that cannot be read (busy, newer, missing table)
    /// reads as no rows and is never written.
    /// </summary>
    public IReadOnlyList<string> Bodies(string hierDir)
    {
        if (StateDbAuthority.NeverCreated(ahHome))
        {
            return [];
        }

        try
        {
            return StateDbStoreReads.Reading(ahHome, connection =>
                StateDbRows.Run(connection, "SELECT body FROM qa_log WHERE hier_dir = ?1 ORDER BY seq", [MsgRows.PoolKey(hierDir)]).Select(static r => (string)r[0]!).ToList());
        }
        catch (Exception e) when (e is StoreUnavailableException or InvalidOperationException)
        {
            return [];
        }
    }

    /// <summary>The meta key recording that this home took <paramref name="file"/>'s rows into the table.</summary>
    internal static string TakenKey(string file) => "qa-log.taken:" + StateDbTeamKey.RealPath(file);

    /// <summary>The import pass's take of <paramref name="hierDir"/>'s <c>qa-log.jsonl</c> (files-off-disk §3.2); false when nothing was taken.</summary>
    internal bool Take(string hierDir) => Import(MsgRows.PoolKey(hierDir), Path.Join(hierDir, "qa-log.jsonl"));

    /// <summary>
    /// The upgrade's one take of a dir's <c>qa-log.jsonl</c>: its object rows in file order, a bad line skipped and a row whose <c>id</c>
    /// the table holds already ignored, stored before any row the table holds for the dir, and the take recorded, in one write
    /// transaction. The file stays where it is. False when nothing was taken (taken already, busy, unreadable).
    /// </summary>
    private bool Import(string hierDir, string file)
    {
        List<(string Body, string? Id, long? At)> rows;
        try
        {
            rows = [.. File.ReadAllLines(file).Select(static l => l.Trim()).Where(static l => l.Length > 0).Select(Parsed).OfType<(string, string?, long?)>()];
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
                var first = StateDbRows.Run(connection, "SELECT MIN(seq) FROM qa_log WHERE hier_dir = ?1", [hierDir]) is [[long min]] ? min : 1;
                for (var i = 0; i < rows.Count; i++)
                {
                    _ = StateDbRows.Run(
                        connection,
                        "INSERT OR IGNORE INTO qa_log(hier_dir, seq, id, at, body) VALUES (?1, ?2, ?3, ?4, ?5)",
                        [hierDir, first - rows.Count + i, rows[i].Id, rows[i].At, rows[i].Body]);
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

    private static (string Body, string? Id, long? At)? Parsed(string line)
    {
        try
        {
            var row = JsonText.Parse(line);
            return row.IsMap
                ? (line, row.Member("id") is { IsText: true } id ? id.AsString() : null, row.Member("at") is { IsNumber: true } at ? (long)at.AsNumber() : null)
                : null;
        }
        catch (JsonParseException)
        {
            return null;
        }
    }
}
