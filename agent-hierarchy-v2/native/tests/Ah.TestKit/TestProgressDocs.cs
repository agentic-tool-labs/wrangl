using Ah.StateDb;

namespace Ah.TestKit;

/// <summary>A team's progress documents (per-dir-files-to-db S11d: rollup, checklists, journals, <c>.nudged</c>, <c>sessions.jsonl</c> are <c>progress_docs</c> rows), read and planted by the path their file had.</summary>
public static class TestProgressDocs
{
    /// <summary>The body of the document the old file <paramref name="path"/> held, or null when there is none.</summary>
    public static string? ReadFile(string ahHome, string path) => StateDbModule.CreateProgressDocs(ahHome).ByKey(path)?.Body;

    /// <summary>The document's text; throws <see cref="FileNotFoundException"/> when there is none, as reading a missing file did.</summary>
    public static string Read(string ahHome, string path) => ReadFile(ahHome, path) ?? throw new FileNotFoundException("no progress document", path);

    /// <summary>The document's body split into lines, as <c>File.ReadAllLines</c> split the file.</summary>
    public static string[] ReadLines(string ahHome, string path) =>
        ReadFile(ahHome, path) is { Length: > 0 } body ? (body.EndsWith('\n') ? body[..^1] : body).Split('\n') : [];

    /// <summary>Whether the document the old file <paramref name="path"/> held exists.</summary>
    public static bool Exists(string ahHome, string path) => ReadFile(ahHome, path) is not null;

    /// <summary>Sets the document the old file <paramref name="path"/> held to <paramref name="text"/>, as writing the file did (the team's files are taken first).</summary>
    public static void WriteFile(string ahHome, string path, string text)
    {
        var (pool, team, name) = Take(ahHome, path);
        Write(ahHome, tx => tx.Execute(
            "INSERT INTO progress_docs(pool, team, name, kind, body, updated_ms) VALUES (?1, ?2, ?3, ?4, ?5, ?6) ON CONFLICT(pool, team, name) DO UPDATE SET body = excluded.body, updated_ms = excluded.updated_ms",
            pool, team, name, KindOf(name), text, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
    }

    /// <summary>Removes the document, as deleting the file did.</summary>
    public static void Delete(string ahHome, string path)
    {
        var (pool, team, name) = Take(ahHome, path);
        Write(ahHome, tx => tx.Execute("DELETE FROM progress_docs WHERE pool = ?1 AND team = ?2 AND name = ?3", pool, team, name));
    }

    /// <summary>Turns the team's progress off, as <c>ah progress off</c> does (files-off-disk §6: the <c>.off</c> row, which replaced the file).</summary>
    public static void Off(string ahHome, string pool, string team) =>
        Write(ahHome, tx => tx.Execute(
            "INSERT OR IGNORE INTO progress_docs(pool, team, name, kind, body, updated_ms) VALUES (?1, ?2, '.off', 'off', '# ah-progress v1.0 kind=off', 0)", Ah.Core.Json.MsgRows.PoolKey(pool), team));

    /// <summary>Sets the document's update time, as <c>File.SetLastWriteTimeUtc</c> set the file's.</summary>
    public static void Touch(string ahHome, string path, DateTimeOffset at)
    {
        var (pool, team, name) = Take(ahHome, path);
        Write(ahHome, tx => tx.Execute("UPDATE progress_docs SET updated_ms = ?4 WHERE pool = ?1 AND team = ?2 AND name = ?3", pool, team, name, at.ToUnixTimeMilliseconds()));
    }

    /// <summary>The (pool, team, name) the path names, the database created and the team's old files taken into it, so a later read never takes a file over the row.</summary>
    private static (string Pool, string Team, string Name) Take(string ahHome, string path)
    {
        var folder = Path.GetDirectoryName(path)!;
        var (pool, team) = (Path.GetDirectoryName(Path.GetDirectoryName(folder)!)!, Path.GetFileName(folder));
        Write(ahHome, static _ => { });
        TestImport.Planted(ahHome, [path]);
        return (Ah.Core.Json.MsgRows.PoolKey(pool), team, Path.GetFileName(path));
    }

    private static string KindOf(string name) => name switch
    {
        "rollup.md" => "rollup",
        ".nudged" => "nudged",
        "sessions.jsonl" => "sessions",
        _ when name.EndsWith(".journal.md", StringComparison.Ordinal) => "journal",
        _ => "checklist",
    };

    private static void Write(string ahHome, Action<IStateDbTransaction> body)
    {
        var written = StateDbModule.OpenDirect(ahHome).Write(tx =>
        {
            body(tx);
            return 0;
        });
        if (!written.Ok)
        {
            throw new InvalidOperationException($"progress_docs: {written.Message}");
        }
    }
}
