namespace Ah.StateDb;

/// <summary>The one take of a team file, by the import pass (files-off-disk §3.2) and by a by-path read that misses: a team file with no row is stored.</summary>
internal static class StateDbStoreImport
{
    private static string StampOf(string file) => file + ".stamp";

    /// <summary>The meta key recording that this home took <paramref name="file"/> on a read that missed it.</summary>
    internal static string TakenKey(string file) => "teams.taken:" + StateDbTeamKey.RealPath(file);

    private static bool IsTaken(SqliteConnection connection, string file) => StateDbRows.Run(connection, "SELECT 1 FROM meta WHERE key = ?1", [TakenKey(file)]).Count > 0;

    /// <summary>The team a read found no row for, taken from its unstamped file; the file stays where it is. Null when there is no such file or it is no team.</summary>
    internal static string? ImportMissedTeam(SqliteConnection connection, string file, long at)
    {
        // a file already taken answers with one meta read and takes no write lock; the take is checked again inside the transaction
        if (!File.Exists(file) || File.Exists(StampOf(file)) || IsTaken(connection, file) || StateDbTeams.RowOf(file, File.ReadAllBytes(file), at, null).Row is not { } row)
        {
            return null;
        }

        // one write transaction: the take is checked, the row stored and the take recorded together, so neither a writer racing the
        // caller's read nor a crash between the row and the take lets a later read import a disbanded team again
        connection.Exec("BEGIN IMMEDIATE");
        try
        {
            if (IsTaken(connection, file)
                || StateDbRows.Run(connection, "SELECT 1 FROM teams WHERE hier_dir = ?1 AND team = ?2", [row.HierDir, row.Name]).Count > 0)
            {
                connection.Exec("ROLLBACK");
                return null;
            }

            _ = StateDbTeams.Apply(connection, [row.HierDir], [row], mayRemove: false, inTransaction: true) ?? throw new Ah.Core.Json.StoreUnavailableException("the state database is busy");

            // the take is this home's own record: untaken, the file would import the team again after a disband. Any home may read a repo's
            // team file, so none writes beside it here; a sandbox home that stamped the repo would hide the team from the real one
            _ = StateDbRows.Run(connection, "INSERT OR IGNORE INTO meta(key, value) VALUES (?1, ?2)", [TakenKey(file), at.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
            connection.Exec("COMMIT");
            return row.Doc;
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
}
