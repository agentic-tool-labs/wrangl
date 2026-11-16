using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Core.Time;

namespace Ah.Hierarchy;

/// <summary>
/// The user's index of hierarchy directories that hold an ah2-enabled session: one <c>meta</c> row per dir, its last-seen time (b397).
/// The daemon learns a directory from a registration's team file only, so a session with no Team is found through this index.
/// </summary>
internal sealed partial class HierLib
{
    internal const int KnownDirsCap = 256;

    /// <summary>How stale a directory's last-seen time may be before a hook notes it again, so the daemon can tell a directory in use from one long dormant.</summary>
    private const double KnownDirsRefreshMs = 10 * 60 * 1000;

    internal string KnownDirsStateDirectory => AhPaths.UserEntry(_environment.HomeDirectory, AhPaths.KnownDirsStateDir);

    public IReadOnlyList<string> KnownHierarchyDirs() => [.. ReadKnownDirs().Keys];

    /// <summary>The sessions recorded in <c>session_pids</c> under <paramref name="dir"/> as session id and pid, the latest row of each, oldest first.</summary>
    public IReadOnlyList<KeyValuePair<string, long>> SessionPidsOf(string dir)
    {
        var latest = new List<KeyValuePair<string, long>>();
        foreach (var row in SessionPidRows(dir))
        {
            if (row.SessionId.Length > 0 && row.Pid > 0)
            {
                latest.RemoveAll(e => e.Key == row.SessionId);
                latest.Add(new KeyValuePair<string, long>(row.SessionId, row.Pid));
            }
        }

        return latest;
    }

    /// <summary>The index's rows (b397: <c>known-dir:</c> rows of <c>meta</c>), dir to last-seen time, oldest first; empty without a state database.</summary>
    private JsonObject ReadKnownDirs()
    {
        var dirs = new JsonObject();
        foreach (var (dir, at) in _knownDirRows?.Invoke() ?? [])
        {
            dirs[dir] = at;
        }

        return dirs;
    }

    /// <summary>Records <c>hierarchy_dir</c> (by its real path) as seen now; past <see cref="KnownDirsCap"/> the longest-unseen entries go.</summary>
    internal OpResult KnownDirsNoteDb(OpCall call, IDbOpTransaction tx) => OpArgs.Run(call, args =>
    {
        var dir = TextOf(args.Member("hierarchy_dir"));
        if (dir is null || !NodePath.IsAbsolute(dir))
        {
            throw new InvalidOperationException("hierarchy_dir must be an absolute path");
        }

        string real;
        try
        {
            real = _files.RealPath(dir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            real = dir;
        }

        const string Ours = "substr(key, 1, ?1) = ?2";
        var prefix = AhPaths.KnownDirKeyPrefix;
        tx.Execute("INSERT INTO meta(key, value) VALUES (?1, ?2) ON CONFLICT(key) DO UPDATE SET value = excluded.value", prefix + real, IsoNowString());
        tx.Execute(
            $"DELETE FROM meta WHERE key IN (SELECT key FROM meta WHERE {Ours} ORDER BY value, key LIMIT max(0, (SELECT COUNT(*) FROM meta WHERE {Ours}) - ?3))",
            (long)prefix.Length, prefix, (long)KnownDirsCap);
        return new JsonObject { ["noted"] = true, ["count"] = tx.Query($"SELECT COUNT(*) FROM meta WHERE {Ours}", (long)prefix.Length, prefix) is [[long n]] ? n : 0 };
    });

    /// <summary>
    /// Whether the repository <paramref name="dir"/> belongs to has ah2 enabled in its config. A repository nobody configured is
    /// not enabled, whatever the default of <c>enabled</c> is; a directory no repository root can be derived from is not either.
    /// </summary>
    public bool DirEnabled(string dir) => _config.DirEnabled(dir);

    /// <summary>
    /// Notes <paramref name="dir"/> when it is enabled and the index does not hold it or last saw it over ten minutes ago. The index is
    /// read without a lock, so a directory seen recently costs no state call; a stale read at worst repeats one idempotent note.
    /// </summary>
    private void NoteKnownDirIfAbsent(string dir)
    {
        string real;
        try
        {
            real = _files.RealPath(dir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            real = dir;
        }

        if (ReadKnownDirs().Get(real) is not { IsText: true } at
            || !Timestamps.TryParseMs(at.AsString(), out var seenMs)
            || _clock.UtcNow.ToUnixTimeMilliseconds() - seenMs > KnownDirsRefreshMs)
        {
            NoteKnownDir(dir);
        }
    }

    private void NoteKnownDir(string dir)
    {
        if (DirEnabled(dir))
        {
            Call(PoolContext(dir), "known-dirs", "note", new JsonObject { ["hierarchy_dir"] = dir }, hook: true);
        }
    }
}
