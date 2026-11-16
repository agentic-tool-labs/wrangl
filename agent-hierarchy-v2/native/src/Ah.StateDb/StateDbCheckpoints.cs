using System.Globalization;
using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.StateDb;

/// <summary>The <c>checkpoints</c> table (schema 29) over the state database of <paramref name="ahHome"/>.</summary>
internal sealed class StateDbCheckpoints(string ahHome) : ICheckpointSource
{
    private const string InsertRow = "INSERT OR IGNORE INTO checkpoints(key, hier_dir, team, ts, body, created_at) VALUES (?1, ?2, ?3, ?4, ?5, ?6)";

    private const string ByKeySql = "SELECT key, ts, body FROM checkpoints WHERE key = ?1";

    public string Key(string path) => StateDbTeamKey.RealPath(path);

    public CheckpointRow? Latest(string hierDir, string team, string? ts = null)
    {
        if (StateDbAuthority.NeverCreated(ahHome))
        {
            return null;
        }

        return ts is null
            ? One("SELECT key, ts, body FROM checkpoints WHERE hier_dir = ?1 AND team = ?2 ORDER BY ts DESC LIMIT 1", [MsgRows.PoolKey(hierDir), team])
            : One("SELECT key, ts, body FROM checkpoints WHERE hier_dir = ?1 AND team = ?2 AND ts = ?3", [MsgRows.PoolKey(hierDir), team, ts]);
    }

    public CheckpointRow? ByKey(string key)
    {
        var normal = Key(key);

        // no database yet: the record is still only its file
        if (StateDbAuthority.NeverCreated(ahHome))
        {
            return File.Exists(normal) ? new CheckpointRow(normal, Path.GetFileNameWithoutExtension(normal), File.ReadAllText(normal)) : null;
        }

        if (One(ByKeySql, [normal]) is { } found)
        {
            return found;
        }

        // ponytail: the by-key take of one file stays (files-off-disk §4 gap): `resume --checkpoint <path>` names a record file the H's take never reaches (it lies elsewhere, or was unreadable then); dropping it would refuse that resume
        // (Takeable is asked with the key itself, which no meta row holds, only for the database check)
        var folder = Path.GetDirectoryName(normal) ?? string.Empty;
        var hierDir = Path.GetDirectoryName(Path.GetDirectoryName(folder)) ?? string.Empty;
        if (File.Exists(normal) && !_failed.HoldsBack(normal) && StateDbTake.Takeable(ahHome, normal)
            && !StateDbTake.InTransaction(ahHome, (_, execute) => execute(
                InsertRow, [normal, hierDir, Path.GetFileName(folder), Path.GetFileNameWithoutExtension(normal), File.ReadAllText(normal), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()])))
        {
            _failed.Failed(normal);
        }

        return One(ByKeySql, [normal]);
    }

    private readonly StateDbTakeBackoff _failed = new();

    /// <summary>The import pass's take of <paramref name="hierDir"/>'s checkpoint files (files-off-disk §3.2); false when the write did not land.</summary>
    internal bool Take(string hierDir) =>
        Directory.Exists(Path.Join(hierDir, "checkpoints")) && StateDbTake.Takeable(ahHome, TakenKey(hierDir))
        && StateDbTake.InTransaction(ahHome, (query, execute) => TakeIn(query, execute, hierDir));

    /// <summary>The meta key recording that this home took <paramref name="hierDir"/>'s checkpoint files into the table.</summary>
    internal static string TakenKey(string hierDir) => "checkpoints.taken:" + StateDbTeamKey.RealPath(hierDir);

    /// <summary>The one row <paramref name="sql"/> finds, or null. A read that cannot answer (busy, newer) throws, never reads as no record: a held team's pinned record must not look missing.</summary>
    private CheckpointRow? One(string sql, object?[] args)
    {
        try
        {
            return StateDbStoreReads.Reading(ahHome, connection => StateDbRows.Run(connection, sql, args)
                .Select(static r => new CheckpointRow((string)r[0]!, (string)r[1]!, (string)r[2]!)).ToList()) is [var row] ? row : null;
        }
        catch (Exception e) when (e is StoreUnavailableException or SqliteException)
        {
            throw new InvalidOperationException($"the state database is busy; try again ({e.Message})", e);
        }
    }

    /// <summary>
    /// The one take of an H's checkpoint files (per-dir-files-to-db S11a-ckpt): each <c>checkpoints/&lt;team&gt;/&lt;ts&gt;.json</c> becomes a row
    /// keyed by its own path (normalized), its body verbatim, so a hold that names the old path finds it. Recorded once per H; the files stay.
    /// </summary>
    private static void TakeIn(Func<string, object?[], IReadOnlyList<object?[]>> query, Action<string, object?[]> execute, string hierDir)
    {
        var key = TakenKey(hierDir);
        if (query("SELECT 1 FROM meta WHERE key = ?1", [key]).Count > 0)
        {
            return;
        }

        var root = Path.Join(hierDir, "checkpoints");
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var skipped = 0;
        if (Directory.Exists(root))
        {
            foreach (var folder in Directory.EnumerateDirectories(root))
            {
                foreach (var file in Directory.EnumerateFiles(folder, "*.json"))
                {
                    string body;
                    try
                    {
                        body = File.ReadAllText(file);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        // b419: one unreadable file never holds back the H's take; ByKey still reads it by its own key once it can be read
                        skipped++;
                        continue;
                    }

                    execute(InsertRow, [StateDbTeamKey.RealPath(file), MsgRows.PoolKey(hierDir), Path.GetFileName(folder), Path.GetFileNameWithoutExtension(file), body, now]);
                }
            }
        }

        var value = now.ToString(CultureInfo.InvariantCulture) + (skipped > 0 ? $" skipped {skipped.ToString(CultureInfo.InvariantCulture)} unreadable" : string.Empty);
        execute("INSERT INTO meta(key, value) VALUES (?1, ?2)", [key, value]);
    }
}
