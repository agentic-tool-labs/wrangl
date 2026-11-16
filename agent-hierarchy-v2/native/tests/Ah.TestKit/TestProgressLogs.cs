using Ah.StateDb;

namespace Ah.TestKit;

/// <summary>A team's progress logs (per-dir-files-to-db S11c: <c>ledger.jsonl</c> and <c>&lt;stream&gt;.plan.jsonl</c> are <c>progress_logs</c> rows), read and planted the way a test once read and wrote the files.</summary>
public static class TestProgressLogs
{
    /// <summary>The rows of the log, one line each as the file held them; null when it has none.</summary>
    public static string? Text(string ahHome, string dir, string team, string log)
    {
        var rows = StateDbModule.CreateProgressLogs(ahHome).Rows(dir, team, log);
        return rows.Count == 0 ? null : string.Concat(rows.Select(static r => r.Body + "\n"));
    }

    /// <summary>The rows of the log as lines; none when it has none.</summary>
    public static string[] Lines(string ahHome, string dir, string team, string log) =>
        [.. StateDbModule.CreateProgressLogs(ahHome).Rows(dir, team, log).Select(static r => r.Body)];

    /// <summary>Appends each non-blank line of <paramref name="text"/> as a row, as appending to the file did; the line is stored as is, JSON or not.</summary>
    public static void Append(string ahHome, string dir, string team, string log, string text)
    {
        // the old file, if any, is taken first (by the import pass: a read no longer takes it), so the new rows land after its lines
        TestImport.Planted(ahHome, [Path.Join(dir, "progress", team, log)]);
        dir = StateDbModule.CreateProgressLogs(ahHome).Key(dir);
        Write(ahHome, tx =>
        {
            foreach (var line in text.Split('\n').Where(static l => l.Trim().Length > 0))
            {
                tx.Execute(
                    "INSERT INTO progress_logs(hier_dir, team, log, seq, body) VALUES (?1, ?2, ?3, (SELECT COALESCE(MAX(seq), 0) + 1 FROM progress_logs WHERE hier_dir = ?1 AND team = ?2 AND log = ?3), ?4)",
                    dir, team, log, line);
            }
        });
    }

    /// <summary>Removes every row of the log, as deleting the file did.</summary>
    public static void Drop(string ahHome, string dir, string team, string log)
    {
        // the old file, if any, is taken first (by the import pass), so it cannot come back after the drop
        TestImport.Planted(ahHome, [Path.Join(dir, "progress", team, log)]);
        dir = StateDbModule.CreateProgressLogs(ahHome).Key(dir);
        Write(ahHome, tx => tx.Execute("DELETE FROM progress_logs WHERE hier_dir = ?1 AND team = ?2 AND log = ?3", dir, team, log));
    }

    /// <summary>The (dir, team, log) an old log file path <c>&lt;dir&gt;/progress/&lt;team&gt;/&lt;log&gt;</c> names.</summary>
    public static (string Dir, string Team, string Log) Of(string path)
    {
        var folder = Path.GetDirectoryName(path)!;
        return (Path.GetDirectoryName(Path.GetDirectoryName(folder)!)!, Path.GetFileName(folder), Path.GetFileName(path));
    }

    /// <summary>The rows of the log the old file <paramref name="path"/> held, one line each ("" when none), as reading the file did.</summary>
    public static string ReadFile(string ahHome, string path)
    {
        var (dir, team, log) = Of(path);
        return Text(ahHome, dir, team, log) ?? string.Empty;
    }

    /// <summary>Whether the log the old file <paramref name="path"/> held has a row.</summary>
    public static bool Exists(string ahHome, string path) => ReadFile(ahHome, path).Length > 0;

    /// <summary>Appends <paramref name="text"/>'s lines to the log the old file <paramref name="path"/> held.</summary>
    public static void AppendFile(string ahHome, string path, string text)
    {
        var (dir, team, log) = Of(path);
        Append(ahHome, dir, team, log, text);
    }

    /// <summary>Replaces the log the old file <paramref name="path"/> held with <paramref name="text"/>'s lines, as writing the file did.</summary>
    public static void WriteFile(string ahHome, string path, string text)
    {
        var (dir, team, log) = Of(path);
        Drop(ahHome, dir, team, log);
        Append(ahHome, dir, team, log, text);
    }

    /// <summary>The plan logs (<c>&lt;stream&gt;.plan.jsonl</c>) of the team that hold a row.</summary>
    public static string[] PlanLogs(string ahHome, string dir, string team) =>
        [.. StateDbModule.CreateProgressLogs(ahHome).Logs(dir, team).Where(static l => l.EndsWith(".plan.jsonl", StringComparison.Ordinal))];

    private static void Write(string ahHome, Action<IStateDbTransaction> body)
    {
        var written = StateDbModule.OpenDirect(ahHome).Write(tx =>
        {
            body(tx);
            return 0;
        });
        if (!written.Ok)
        {
            throw new InvalidOperationException($"progress_logs: {written.Message}");
        }
    }
}
