using System.Globalization;
using System.Text;
using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.StateDb;

/// <summary>The <c>progress_logs</c> table (schema 31) over the state database of <paramref name="ahHome"/>.</summary>
internal sealed class StateDbProgressLogs(string ahHome) : IProgressLogSource
{
    internal const string Ledger = "ledger.jsonl";

    internal const string PlanSuffix = ".plan.jsonl";

    public string Key(string hierDir) => StateDbTeamKey.RealPath(hierDir);

    public IReadOnlyList<ProgressLogRow> Rows(string hierDir, string team, string log, long after = 0)
    {
        return StateDbStoreReads.ReadOrEmpty(ahHome, connection => StateDbRows.Run(
                connection, "SELECT seq, body FROM progress_logs WHERE hier_dir = ?1 AND team = ?2 AND log = ?3 AND seq > ?4 ORDER BY seq", [Key(hierDir), team, log, after])
            .Select(static r => new ProgressLogRow((long)r[0]!, (string)r[1]!)).ToList());
    }

    public IReadOnlyList<string> Logs(string hierDir, string team)
    {
        return StateDbStoreReads.ReadOrEmpty(ahHome, connection => StateDbRows.Run(
                connection, "SELECT DISTINCT log FROM progress_logs WHERE hier_dir = ?1 AND team = ?2 ORDER BY log", [Key(hierDir), team])
            .Select(static r => (string)r[0]!).ToList());
    }

    public IReadOnlyList<string> Teams(string hierDir, string? log)
    {
        return StateDbStoreReads.ReadOrEmpty(ahHome, connection => StateDbRows.Run(
                connection, "SELECT DISTINCT team FROM progress_logs WHERE hier_dir = ?1 AND (?2 IS NULL OR log = ?2) ORDER BY team", [Key(hierDir), log])
            .Select(static r => (string)r[0]!).ToList());
    }

    public LedgerSince? LedgerAfterCursor(string hierDir, string team)
    {
        // b435: no take here: the memory forwarder's every-tick read touches no file
        if (StateDbAuthority.NeverCreated(ahHome))
        {
            return null;
        }

        try
        {
            return StateDbStoreReads.Reading(ahHome, connection =>
            {
                var cursor = StateDbRows.Run(connection, "SELECT value FROM meta WHERE key = ?1", [CursorKey(hierDir, team)]) is [[string text]]
                    && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seq) ? seq : 0;
                var rows = StateDbRows.Run(connection, "SELECT seq, body FROM progress_logs WHERE hier_dir = ?1 AND team = ?2 AND log = ?3 AND seq > ?4 ORDER BY seq", [Key(hierDir), team, Ledger, cursor])
                    .Select(static r => new ProgressLogRow((long)r[0]!, (string)r[1]!)).ToList();
                return new LedgerSince(cursor, rows);
            });
        }
        catch (Exception e) when (e is StoreUnavailableException or InvalidOperationException)
        {
            return null;
        }
    }

    public long LedgerCursor(string hierDir, string team)
    {
        var found = StateDbStoreReads.ReadOrEmpty(ahHome, connection => StateDbRows.Run(connection, "SELECT value FROM meta WHERE key = ?1", [CursorKey(hierDir, team)])
            .Select(static r => (string)r[0]!).ToList());
        return found is [var text] && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seq) ? seq : 0;
    }

    // ponytail: the write's take stays (files-off-disk §4 gap, atp-1518): a plan fold, checklist rewrite or ledger append builds on the log's rows
    public void Take(IDbOpTransaction tx, string hierDir, string team, string log) => TakeIn(tx.Query, (sql, args) => tx.Execute(sql, args), hierDir, team, log);

    public void SetLedgerCursor(IDbOpTransaction tx, string hierDir, string team, long seq) => tx.Execute(
        "INSERT INTO meta(key, value) VALUES (?1, ?2) ON CONFLICT(key) DO UPDATE SET value = excluded.value", CursorKey(hierDir, team), seq.ToString(CultureInfo.InvariantCulture));

    public string? ReleaseSeen(string hierDir) =>
        StateDbAuthority.NeverCreated(ahHome)
            ? null
            : StateDbStoreReads.Reading(ahHome, connection => StateDbRows.Run(connection, "SELECT value FROM meta WHERE key = ?1", [ReleaseSeenKey(hierDir)])
                .Select(static r => (string)r[0]!).ToList()) is [var text] ? text : null;

    public void SetReleaseSeen(IDbOpTransaction tx, string hierDir, string text) => tx.Execute(
        "INSERT INTO meta(key, value) VALUES (?1, ?2) ON CONFLICT(key) DO UPDATE SET value = excluded.value", ReleaseSeenKey(hierDir), text);

    private static string ReleaseSeenKey(string hierDir) => "release-watch.seen:" + StateDbTeamKey.RealPath(hierDir);

    internal static bool IsLog(string name) => name == Ledger || (name.EndsWith(PlanSuffix, StringComparison.Ordinal) && name.Length > PlanSuffix.Length);

    /// <summary>The meta key recording that this home took <paramref name="file"/>'s rows into the table.</summary>
    internal static string TakenKey(string file) => "progress.taken:" + StateDbTeamKey.RealPath(file);

    private static string CursorKey(string hierDir, string team) => "progress.ledger-cursor:" + StateDbTeamKey.RealPath(hierDir) + "|" + team;

    private static string Folder(string hierDir, string team) => Path.Join(hierDir, "progress", team);

    /// <summary>The import pass's take of the team's old log files (files-off-disk §3.2), each in its own write transaction; a busy or unwritable database leaves it untaken.</summary>
    internal void Take(string hierDir, string team)
    {
        var folder = Folder(hierDir, team);
        if (!Directory.Exists(folder))
        {
            return;
        }

        foreach (var log in Directory.EnumerateFiles(folder).Select(Path.GetFileName).OfType<string>().Where(IsLog))
        {
            if (StateDbTake.Takeable(ahHome, TakenKey(Path.Join(folder, log))))
            {
                _ = StateDbTake.InTransaction(ahHome, (query, execute) =>
                {
                    TakeIn(query, execute, hierDir, team, log);

                    // b459 step 11: outside the op runner, so it moves the plan counter (the progress ops' "backlog" domain) itself
                    execute("INSERT INTO domain_seq(domain, seq, changed_ms) VALUES ('backlog', 1, ?1) ON CONFLICT(domain) DO UPDATE SET seq = seq + 1, changed_ms = ?1", [DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()]);
                });
            }
        }
    }

    /// <summary>
    /// The one take of a log's old file (per-dir-files-to-db S11c): each line that is a JSON object becomes the next row, a bad line is
    /// skipped, and the take is recorded, so a file is taken once. The file stays where it is. For the ledger, the old byte cursor
    /// (<c>ledger.cursor</c>) becomes the seq of the last whole row it covered, unless a cursor is already stored.
    /// </summary>
    private static void TakeIn(Func<string, object?[], IReadOnlyList<object?[]>> query, Action<string, object?[]> execute, string hierDir, string team, string log)
    {
        var folder = Folder(hierDir, team);
        var file = Path.Join(folder, log);
        var key = TakenKey(file);
        if (query("SELECT 1 FROM meta WHERE key = ?1", [key]).Count > 0)
        {
            return;
        }

        var bytes = File.Exists(file) ? File.ReadAllBytes(file) : [];
        var cursorBytes = log == Ledger ? OldCursor(Path.Join(folder, "ledger.cursor")) : -1;
        var dirKey = StateDbTeamKey.RealPath(hierDir);
        var seq = query("SELECT COALESCE(MAX(seq), 0) FROM progress_logs WHERE hier_dir = ?1 AND team = ?2 AND log = ?3", [dirKey, team, log]) is [[long max]] ? max : 0;
        var cursorSeq = 0L;
        for (var start = 0; start < bytes.Length;)
        {
            var stop = Array.IndexOf(bytes, (byte)'\n', start);
            var end = stop < 0 ? bytes.Length : stop + 1;
            var line = Encoding.UTF8.GetString(bytes, start, end - start).Trim();
            // a last line with no newline is taken when it is a whole object: the file is never read again, so skipping it would lose it
            if (line.Length > 0 && IsJsonMap(line))
            {
                seq++;
                execute("INSERT INTO progress_logs(hier_dir, team, log, seq, body) VALUES (?1, ?2, ?3, ?4, ?5)", [dirKey, team, log, seq, line]);
                if (end <= cursorBytes)
                {
                    cursorSeq = seq;
                }
            }

            start = end;
        }

        if (cursorSeq > 0)
        {
            execute("INSERT OR IGNORE INTO meta(key, value) VALUES (?1, ?2)", [CursorKey(hierDir, team), cursorSeq.ToString(CultureInfo.InvariantCulture)]);
        }

        execute("INSERT INTO meta(key, value) VALUES (?1, ?2)", [key, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)]);
    }

    private static long OldCursor(string path)
    {
        try
        {
            return File.Exists(path) && long.TryParse(File.ReadAllText(path).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var offset) ? offset : -1;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return -1;
        }
    }

    private static bool IsJsonMap(string line)
    {
        try
        {
            return JsonText.Parse(line).IsMap;
        }
        catch (JsonParseException)
        {
            return false;
        }
    }
}
