using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.StateDb;

/// <summary>The <c>session_pids</c> table (schema 21) over the state database of <paramref name="ahHome"/>.</summary>
internal sealed class StateDbSessionPids(string ahHome) : ISessionPidSource
{
    public IReadOnlyList<SessionPidEntry> Rows(string hierDir) =>
        StateDbStoreReads.ReadOrEmpty(ahHome, connection => StateDbRows.Run(connection, "SELECT session_id, pid, at FROM session_pids WHERE hier_dir = ?1 ORDER BY at, rowid", [MsgRows.PoolKey(hierDir)])
            .Select(static r => new SessionPidEntry((string)r[0]!, (long)r[1]!, (string)r[2]!)).ToList());
}
