using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.StateDb;

/// <summary>The read-side take of old files into the database (the db-s1 import shape), shared by the progress logs and the checkpoints.</summary>
internal static class StateDbTake
{
    private const int ImportBusyMs = 250;

    /// <summary>
    /// Whether a take may run: the database exists, reads at this binary's schema or older, and holds no take <paramref name="takenKey"/>.
    /// A database that cannot be read (busy, newer, missing table) is never written. Answered before any write lock.
    /// </summary>
    internal static bool Takeable(string ahHome, string takenKey)
    {
        if (StateDbAuthority.NeverCreated(ahHome))
        {
            return false;
        }

        try
        {
            return StateDbStoreReads.Reading(ahHome, connection => StateDbRows.Run(connection, "SELECT 1 FROM meta WHERE key = ?1", [takenKey]).Count == 0);
        }
        catch (Exception e) when (e is StoreUnavailableException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Runs <paramref name="body"/> in one write transaction of its own: false when it could not (busy, not writable, a file unreadable), with nothing written.</summary>
    internal static bool InTransaction(string ahHome, Action<Func<string, object?[], IReadOnlyList<object?[]>>, Action<string, object?[]>> body)
    {
        try
        {
            using var connection = SqliteConnection.Open(StateDbBackups.DatabasePath(ahHome), SqliteOpenMode.ReadWrite, ImportBusyMs);
            connection.Exec("BEGIN IMMEDIATE");
            try
            {
                body((sql, args) => StateDbRows.Run(connection, sql, args), (sql, args) => StateDbRows.Run(connection, sql, args));
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
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException)
        {
            // busy, not writable or a file unreadable: a later read takes it, after the backoff
            return false;
        }
    }
}
