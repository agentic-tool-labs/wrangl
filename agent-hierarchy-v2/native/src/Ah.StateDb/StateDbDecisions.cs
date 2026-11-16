using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.StateDb;

/// <summary>The <c>decisions</c> table (schema 28, per-dir-files-to-db S9) over the state database of <paramref name="ahHome"/>.</summary>
internal sealed class StateDbDecisions(string ahHome) : IDecisionSource
{
    private sealed class Tx(IStateDbTransaction tx) : IDbOpTransaction
    {
        public void Execute(string sql, params object?[] args) => tx.Execute(sql, args);

        public IReadOnlyList<object?[]> Query(string sql, params object?[] args) => tx.Query(sql, args);
    }

    private static string LogOf(string hierDir, string run) => Path.Join(hierDir, "pipeline", run, "decisions.jsonl");

    /// <summary>The meta key recording that this home took <paramref name="file"/>'s rows into the table.</summary>
    internal static string TakenKey(string file) => "decisions.taken:" + StateDbTeamKey.RealPath(file);

    public IReadOnlyList<string>? Bodies(string hierDir, string run) =>
        Read(c => StateDbRows.Run(c, "SELECT body FROM decisions WHERE hier_dir = ?1 AND run = ?2 ORDER BY seq", [MsgRows.PoolKey(hierDir), run]).Select(static r => (string)r[0]!).ToList());

    public IReadOnlyList<string>? Runs(string hierDir) =>
        Read(c => StateDbRows.Run(c, "SELECT DISTINCT run FROM decisions WHERE hier_dir = ?1 ORDER BY run", [MsgRows.PoolKey(hierDir)]).Select(static r => (string)r[0]!).ToList());

    /// <summary>Reads with <paramref name="query"/>, rows only (files-off-disk §4): the run files are taken by the import pass, never here.</summary>
    private List<string>? Read(Func<SqliteConnection, List<string>> query)
    {
        if (StateDbAuthority.NeverCreated(ahHome))
        {
            return null;
        }

        try
        {
            return StateDbStoreReads.Reading(ahHome, c =>
                // no mode row is the database's, as the engine reads it; only an explicit files row sends the log back to its file
                StateDbRows.Run(c, "SELECT value FROM meta WHERE key = 'mode.decisions'", []) is not [[string mode]] || mode == StateDbMessageMode.Db ? query(c) : null);
        }
        catch (Exception e) when (e is StoreUnavailableException or InvalidOperationException)
        {
            // busy past the read budget, newer or unreadable: no rows, and nothing written
            return [];
        }
    }

    /// <summary>The import pass's take of every run file of <paramref name="hierDir"/> (files-off-disk §3.2), in one write: false when it did not land.</summary>
    internal bool TakeRuns(string hierDir)
    {
        var pipeline = Path.Join(hierDir, "pipeline");
        List<string> runs = Directory.Exists(pipeline) ? [.. Directory.EnumerateDirectories(pipeline).Select(Path.GetFileName).OfType<string>()] : [];
        return new StateDbDirect(ahHome, DirectWriteOptions.Default).Write(tx =>
        {
            foreach (var run in runs)
            {
                Take(new Tx(tx), MsgRows.PoolKey(hierDir), run);
            }

            return 0;
        }).Ok;
    }

    /// <summary>
    /// The one take of a run's <c>decisions.jsonl</c>: each non-blank line as written stored before any row the run already holds, and the
    /// take recorded. A line that is no JSON object is kept, so the log's reader still counts it as skipped, as it did reading the file.
    /// The file stays where it is. A file that cannot be read is left untaken.
    /// </summary>
    private static void Take(IDbOpTransaction tx, string hierDir, string run)
    {
        ArgumentNullException.ThrowIfNull(tx);
        var file = LogOf(hierDir, run);
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
        var first = tx.Query("SELECT MIN(seq) FROM decisions WHERE hier_dir = ?1 AND run = ?2", hierDir, run) is [[long min]] ? min : 1;
        for (var i = 0; i < lines.Count; i++)
        {
            tx.Execute("INSERT INTO decisions(hier_dir, run, seq, body) VALUES (?1, ?2, ?3, ?4)", hierDir, run, first - lines.Count + i, lines[i]);
        }

        tx.Execute("INSERT INTO meta(key, value) VALUES (?1, ?2)", TakenKey(file), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}
