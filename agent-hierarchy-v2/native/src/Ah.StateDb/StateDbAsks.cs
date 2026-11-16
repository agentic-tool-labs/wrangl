using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.StateDb;

/// <summary>The <c>asks</c> table (schema 33, per-dir-files-to-db S10) over the state database of <paramref name="ahHome"/>.</summary>
internal sealed class StateDbAsks(string ahHome) : IAskSource
{
    /// <summary>The logs the table holds, by file name. The question log (<c>qa-log.jsonl</c>) is its own domain's (S6).</summary>
    internal static readonly IReadOnlySet<string> Logs = new HashSet<string>(["asks.jsonl", "asks-gate.jsonl", "parked-questions.jsonl"], StringComparer.Ordinal);

    /// <summary>The meta key recording that this home took <paramref name="file"/>'s rows into the table.</summary>
    internal static string TakenKey(string file) => "asks.taken:" + StateDbTeamKey.RealPath(file);

    public bool Holds(string path) => Logs.Contains(Path.GetFileName(path)) && Path.IsPathRooted(path);

    public bool Taken(string path)
    {
        if (!Holds(path) || StateDbAuthority.NeverCreated(ahHome))
        {
            return false;
        }

        try
        {
            return StateDbStoreReads.Reading(ahHome, c =>
                (StateDbRows.Run(c, "SELECT value FROM meta WHERE key = 'mode.asks'", []) is not [[string mode]] || mode == StateDbMessageMode.Db)
                && StateDbRows.Run(c, "SELECT 1 FROM meta WHERE key = ?1", [TakenKey(path)]).Count > 0);
        }
        catch (Exception e) when (e is StoreUnavailableException or InvalidOperationException)
        {
            return true;
        }
    }

    public AskLog? Read(string path)
    {
        if (!Holds(path) || StateDbAuthority.NeverCreated(ahHome))
        {
            return null;
        }

        var hierDir = HierDirKey(path);
        var log = Path.GetFileName(path);
        try
        {
            // rows only (files-off-disk §4): the file is taken by the import pass, never here
            var rows = StateDbStoreReads.Reading(ahHome, c =>
                // no mode row is the database's, as the engine reads it; only an explicit files row sends the log back to its file
                StateDbRows.Run(c, "SELECT value FROM meta WHERE key = 'mode.asks'", []) is [[string mode]] && mode != StateDbMessageMode.Db
                    ? null
                    : StateDbRows.Run(c, "SELECT body FROM asks WHERE hier_dir = ?1 AND log = ?2 ORDER BY seq", [hierDir, log]).Select(static r => (string)r[0]!).ToList());
            return rows is null ? null : AskLog.Of(rows);
        }
        catch (Exception e) when (e is StoreUnavailableException or InvalidOperationException)
        {
            // busy past the read budget, newer or unreadable: no lines at all (§6c G5), and nothing written
            return AskLog.Unreadable;
        }
    }

    /// <summary>The import pass's take of <paramref name="path"/> (files-off-disk §3.2): false when the write did not land.</summary>
    internal bool TakeFile(string path) =>
        new StateDbDirect(ahHome, DirectWriteOptions.Default).Write(tx =>
        {
            Take(tx, HierDirKey(path), Path.GetFileName(path), path);
            return 0;
        }).Ok;

    /// <summary>The key of a log's hierarchy dir, on the write side as on the read side: the dir's real path, as the take key resolves it.</summary>
    internal static string HierDirKey(string path) => StateDbTeamKey.RealPath(Path.GetDirectoryName(path)!);

    /// <summary>
    /// The one take of an asks log: each non-blank line byte for byte (its <c>at</c> and <c>writer</c> as written, never the take's time)
    /// stored before any row the log already holds, and the take recorded. The file stays where it is. A file that cannot be read is
    /// left untaken.
    /// </summary>
    private static void Take(IStateDbTransaction tx, string hierDir, string log, string file)
    {
        if (tx.Query("SELECT 1 FROM meta WHERE key = ?1", TakenKey(file)).Count > 0 || !File.Exists(file))
        {
            return;
        }

        List<string> lines;
        try
        {
            lines = [.. File.ReadAllLines(file).Where(static l => l.Trim().Length > 0)];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return;
        }

        // before the first row stored since the upgrade, so a row written after the file's lines stays the later one
        var first = tx.Query("SELECT MIN(seq) FROM asks WHERE hier_dir = ?1 AND log = ?2", hierDir, log) is [[long min]] ? min : 1;
        for (var i = 0; i < lines.Count; i++)
        {
            tx.Execute("INSERT INTO asks(hier_dir, log, seq, body) VALUES (?1, ?2, ?3, ?4)", hierDir, log, first - lines.Count + i, lines[i]);
        }

        tx.Execute("INSERT INTO meta(key, value) VALUES (?1, ?2)", TakenKey(file), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}
