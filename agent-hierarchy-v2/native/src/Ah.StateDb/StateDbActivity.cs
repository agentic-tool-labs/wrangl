using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.StateDb;

/// <summary>The <c>activity</c> table (schema 22) over the state database of <paramref name="ahHome"/>.</summary>
internal sealed class StateDbActivity(string ahHome) : IActivitySource
{
    private const string Columns = "session_id, body, updated_at";

    public IReadOnlyList<ActivityEntry> Rows(string hierDir) =>
        Query("SELECT " + Columns + " FROM activity WHERE hier_dir = ?1 ORDER BY session_id", MsgRows.PoolKey(hierDir));

    public ActivityEntry? Row(string hierDir, string subject) =>
        Query("SELECT " + Columns + " FROM activity WHERE hier_dir = ?1 AND session_id = ?2", MsgRows.PoolKey(hierDir), subject) is [var row] ? row : null;

    // a busy database is tried once more; one that still cannot be read is no record, like an activity file that cannot be read
    private List<ActivityEntry> Query(string sql, params object?[] args) =>
        StateDbStoreReads.ReadOrEmpty(ahHome, connection => StateDbRows.Run(connection, sql, args)
            .Select(static r => new ActivityEntry((string)r[0]!, (string)r[1]!, (long)r[2]!)).ToList());
}
