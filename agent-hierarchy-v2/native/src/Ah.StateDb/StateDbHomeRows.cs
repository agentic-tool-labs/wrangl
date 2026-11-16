using Ah.Core.Json;
using Ah.Core.Text;

namespace Ah.StateDb;

/// <summary>The home-scoped rows read outside a write: the QA job queue (<c>qa_jobs</c>, schema 40) and the known-dirs index (<c>meta</c> rows).</summary>
internal static class StateDbHomeRows
{
    /// <summary>Each QA job's JSON text (the queue-epoch row included), in the order the jobs were first written; none when the database cannot be read.</summary>
    internal static IReadOnlyList<string> QaJobBodies(string ahHome) => Read(ahHome, connection =>
        StateDbRows.Run(connection, "SELECT body FROM qa_jobs ORDER BY seq", []).Select(static r => (string)r[0]!).ToList());

    /// <summary>The known dirs with their last-seen time, oldest first; none when the database cannot be read.</summary>
    internal static IReadOnlyList<KeyValuePair<string, string>> KnownDirs(string ahHome) => Read(ahHome, connection =>
        StateDbRows.Run(connection, "SELECT key, value FROM meta WHERE substr(key, 1, ?1) = ?2 ORDER BY value, key", [(long)AhPaths.KnownDirKeyPrefix.Length, AhPaths.KnownDirKeyPrefix])
            .Select(static r => new KeyValuePair<string, string>(((string)r[0]!)[AhPaths.KnownDirKeyPrefix.Length..], (string)r[1]!)).ToList());

    private static List<T> Read<T>(string ahHome, Func<SqliteConnection, List<T>> read)
    {
        if (StateDbAuthority.NeverCreated(ahHome))
        {
            return [];
        }

        try
        {
            return StateDbStoreReads.Reading(ahHome, read);
        }
        catch (Exception e) when (e is StoreUnavailableException or InvalidOperationException)
        {
            return [];
        }
    }
}
