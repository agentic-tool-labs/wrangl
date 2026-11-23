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

    /// <summary>Every row of the <c>resources</c> table (schema 46), dead or alive, oldest first; none when the database cannot be read.</summary>
    internal static IReadOnlyList<Ah.Abstractions.ResourceRow> Resources(string ahHome) => Read(ahHome, connection =>
        StateDbRows.Run(connection, "SELECT id, name, kind, pid, pid_start, who, session, note, since_ms FROM resources ORDER BY since_ms, id", [])
            .Select(static r => new Ah.Abstractions.ResourceRow((long)r[0]!, (string)r[1]!, (string)r[2]!, (long)r[3]!, r[4] as string, (string)r[5]!, r[6] as string, r[7] as string, (long)r[8]!)).ToList());

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
