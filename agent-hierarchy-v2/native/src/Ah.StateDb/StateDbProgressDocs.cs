using System.Globalization;
using System.Text;
using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.StateDb;

/// <summary>The <c>progress_docs</c> table (schema 37) over the state database of <paramref name="ahHome"/>.</summary>
internal sealed class StateDbProgressDocs(string ahHome) : IProgressDocSource
{
    private const string Columns = "pool, team, name, kind, body, updated_ms";

    public IReadOnlyList<ProgressDoc> Docs(string pool, string team)
    {
        if (StateDbAuthority.NeverCreated(ahHome))
        {
            // no database yet: the documents are still only their files
            return FileDocs(pool, team);
        }

        // rows only (files-off-disk §4): the import pass takes the team's files
        try
        {
            return StateDbStoreReads.Reading(ahHome, connection =>
                StateDbRows.Run(connection, $"SELECT {Columns} FROM progress_docs WHERE pool = ?1 AND team = ?2 AND name <> ?3 ORDER BY name", [MsgRows.PoolKey(pool), team, OffName]).Select(Row).ToList());
        }
        catch (Exception e) when (e is StoreUnavailableException or InvalidOperationException)
        {
            // a read that cannot answer says so: the files may be older than the rows, so they are never the answer here
            throw new StoreUnavailableException($"the state database is busy; try again ({e.Message})", e);
        }
    }

    public ProgressDoc? Doc(string pool, string team, string name) => Docs(pool, team).FirstOrDefault(d => d.Name == name);

    public string? Stamp(string pool, string? team = null)
    {
        if (StateDbAuthority.NeverCreated(ahHome))
        {
            return null;
        }

        try
        {
            return StateDbStoreReads.Reading(ahHome, connection =>
                StateDbRows.Run(connection, "SELECT count(*) FROM progress_docs WHERE pool = ?1 AND (?2 IS NULL OR team = ?2)", [MsgRows.PoolKey(pool), team]) is [[long n]]
                    ? n.ToString(CultureInfo.InvariantCulture)
                    : null);
        }
        catch (Exception e) when (e is StoreUnavailableException or InvalidOperationException)
        {
            return null;
        }
    }

    public IReadOnlyList<string> Teams(string pool)
    {
        if (StateDbAuthority.NeverCreated(ahHome))
        {
            var root = Path.Join(pool, "progress");
            var folders = Directory.Exists(root) ? Directory.EnumerateDirectories(root).Select(Path.GetFileName).OfType<string>().Where(static n => !n.StartsWith('.')).ToList() : [];
            return [.. folders.Where(t => FileDocs(pool, t).Count > 0).Order(StringComparer.Ordinal)];
        }

        try
        {
            return StateDbStoreReads.Reading(ahHome, connection => StateDbRows.Run(
                    connection, "SELECT DISTINCT team FROM progress_docs WHERE pool = ?1 ORDER BY team", [MsgRows.PoolKey(pool)])
                .Select(static r => (string)r[0]!).ToList());
        }
        catch (Exception e) when (e is StoreUnavailableException or InvalidOperationException)
        {
            // busy: no team, never the stale files
            return [];
        }
    }

    public ProgressDoc? ByKey(string key)
    {
        // a key is the path the document's file had: <pool>/progress/<team>/<name>
        var folder = Path.GetDirectoryName(key);
        var progress = folder is null ? null : Path.GetDirectoryName(folder);
        if (folder is null || progress is null || Path.GetFileName(progress) != "progress" || Path.GetDirectoryName(progress) is not { Length: > 0 } pool)
        {
            return null;
        }

        return Doc(pool, Path.GetFileName(folder), Path.GetFileName(key));
    }

    // ponytail: the write's take stays (files-off-disk §4 gap): an append or create-only write landing before the pass would shadow the file's document
    public string? Body(IDbOpTransaction tx, string pool, string team, string name)
    {
        TakeIn(tx.Query, (sql, args) => tx.Execute(sql, args), pool, team, TakenKey(pool, team));
        return tx.Query("SELECT body FROM progress_docs WHERE pool = ?1 AND team = ?2 AND name = ?3", MsgRows.PoolKey(pool), team, name) is [[var body]] ? Text(body).Body : null;
    }

    public void Append(IDbOpTransaction tx, string pool, string team, string name, string text)
    {
        // at the byte level, as the file writer appended: a body kept as bytes (not valid UTF-8) stays exactly those bytes plus the text's
        _ = Body(tx, pool, team, name);
        var stored = tx.Query("SELECT body FROM progress_docs WHERE pool = ?1 AND team = ?2 AND name = ?3", MsgRows.PoolKey(pool), team, name) is [[var b]] ? b : null;
        if (stored is not byte[] bytes)
        {
            _ = Put(tx, pool, team, name, (stored as string ?? string.Empty) + text);
            return;
        }

        tx.Execute(
            "UPDATE progress_docs SET body = ?4, updated_ms = ?5 WHERE pool = ?1 AND team = ?2 AND name = ?3",
            MsgRows.PoolKey(pool), team, name, Stored([.. bytes, .. Encoding.UTF8.GetBytes(text)]), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    public bool Put(IDbOpTransaction tx, string pool, string team, string name, string body, bool createOnly = false)
    {
        var kind = KindOf(name) ?? throw new InvalidOperationException($"\"{name}\" is not a progress document");
        if (createOnly && Body(tx, pool, team, name) is not null)
        {
            return false;
        }

        // the take ran in Body, or runs here, so an old file never lands over a newer row
        TakeIn(tx.Query, (sql, args) => tx.Execute(sql, args), pool, team, TakenKey(pool, team));
        tx.Execute(
            $"INSERT INTO progress_docs({Columns}) VALUES (?1, ?2, ?3, ?4, ?5, ?6) ON CONFLICT(pool, team, name) DO UPDATE SET kind = excluded.kind, body = excluded.body, updated_ms = excluded.updated_ms",
            MsgRows.PoolKey(pool), team, name, kind, body, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        return true;
    }

    /// <summary>The name of the team's off switch row (files-off-disk §6): present = off. It is no document, so <see cref="Docs"/> leaves it out.</summary>
    internal const string OffName = ".off";

    internal const string OffMarker = "# ah-progress v1.0 kind=off";

    public bool IsOff(string pool, string team)
    {
        if (StateDbAuthority.NeverCreated(ahHome))
        {
            return false;
        }

        try
        {
            return StateDbStoreReads.Reading(ahHome, c => StateDbRows.Run(c, "SELECT 1 FROM progress_docs WHERE pool = ?1 AND team = ?2 AND name = ?3", [MsgRows.PoolKey(pool), team, OffName]).Count > 0);
        }
        catch (Exception e) when (e is StoreUnavailableException or InvalidOperationException)
        {
            // ponytail: a busy read says on, as a missing file did; the write ops then journal as usual
            return false;
        }
    }

    public bool IsOff(IDbOpTransaction tx, string pool, string team) =>
        tx.Query("SELECT 1 FROM progress_docs WHERE pool = ?1 AND team = ?2 AND name = ?3", MsgRows.PoolKey(pool), team, OffName).Count > 0;

    public void SetOff(IDbOpTransaction tx, string pool, string team, bool off)
    {
        if (off)
        {
            tx.Execute($"INSERT OR IGNORE INTO progress_docs({Columns}) VALUES (?1, ?2, ?3, 'off', ?4, ?5)", MsgRows.PoolKey(pool), team, OffName, OffMarker, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }
        else
        {
            tx.Execute("DELETE FROM progress_docs WHERE pool = ?1 AND team = ?2 AND name = ?3", MsgRows.PoolKey(pool), team, OffName);
        }
    }

    /// <summary>The meta key recording that this home took the team's old progress documents into the table.</summary>
    internal static string TakenKey(string pool, string team) => "progress.taken:" + StateDbTeamKey.RealPath(pool) + "/" + team;

    /// <summary>The kind of the document an old file named <paramref name="name"/> held, or null when that file is not a progress document (the logs, <c>.off</c>, a temp file).</summary>
    internal static string? KindOf(string name) => name switch
    {
        "rollup.md" => "rollup",
        "closing.md" => "closing",
        ".nudged" => "nudged",
        "sessions.jsonl" => "sessions",
        _ when name.StartsWith('.') => null,
        _ when name.EndsWith(".journal.md", StringComparison.Ordinal) => "journal",
        _ when name.EndsWith(".closing.md", StringComparison.Ordinal) => "closing",
        _ when name.EndsWith(".md", StringComparison.Ordinal) && name.Length > ".md".Length => "checklist",
        _ => null,
    };

    private static ProgressDoc Row(object?[] r)
    {
        var (body, raw) = Text(r[4]);
        return new((string)r[0]!, (string)r[1]!, (string)r[2]!, (string)r[3]!, body, (long)r[5]!, raw);
    }

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>A stored body: text as it is, bytes (a file that was not valid UTF-8) decoded leniently with the bytes kept.</summary>
    private static (string Body, byte[]? Raw) Text(object? stored) => stored is byte[] bytes ? (Encoding.UTF8.GetString(bytes), bytes) : ((string)stored!, null);

    /// <summary>What a file's bytes are stored as: the text when they are valid UTF-8, else the bytes themselves, so the take loses nothing.</summary>
    private static object Stored(byte[] bytes)
    {
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return bytes;
        }
    }

    private static string Folder(string pool, string team) => Path.Join(pool, "progress", team);

    /// <summary>The team's old document files, each verbatim, in name order; a file that cannot be read is left out.</summary>
    private static List<ProgressDoc> FileDocs(string pool, string team)
    {
        var folder = Folder(pool, team);
        var docs = new List<ProgressDoc>();
        if (!Directory.Exists(folder))
        {
            return docs;
        }

        foreach (var path in Directory.EnumerateFiles(folder).Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(path);
            if (KindOf(name) is not { } kind || new FileInfo(path).LinkTarget is not null)
            {
                continue;
            }

            try
            {
                var (body, raw) = Text(Stored(File.ReadAllBytes(path)));
                docs.Add(new ProgressDoc(pool, team, name, kind, body, new DateTimeOffset(File.GetLastWriteTimeUtc(path)).ToUnixTimeMilliseconds(), raw));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // an unreadable file is not a document this read can give
            }
        }

        return docs;
    }

    /// <summary>The import pass's take of the team's old documents (files-off-disk §3.2) in one write transaction; a busy or unwritable database leaves them untaken.</summary>
    internal void Take(string pool, string team)
    {
        var key = TakenKey(pool, team);
        if (Directory.Exists(Folder(pool, team)) && StateDbTake.Takeable(ahHome, key))
        {
            _ = StateDbTake.InTransaction(ahHome, (query, execute) => TakeIn(query, execute, pool, team, key));
        }
    }

    /// <summary>
    /// The one take of a team's old documents (per-dir-files-to-db S11d): each file becomes its row, its body byte for byte and its
    /// modification time as <c>updated_ms</c>, and the take is recorded, so a team is taken once. The files stay where they are.
    /// </summary>
    private static void TakeIn(Func<string, object?[], IReadOnlyList<object?[]>> query, Action<string, object?[]> execute, string pool, string team, string key)
    {
        if (query("SELECT 1 FROM meta WHERE key = ?1", [key]).Count > 0)
        {
            return;
        }

        foreach (var doc in FileDocs(pool, team))
        {
            execute($"INSERT OR IGNORE INTO progress_docs({Columns}) VALUES (?1, ?2, ?3, ?4, ?5, ?6)", [MsgRows.PoolKey(doc.Pool), doc.Team, doc.Name, doc.Kind, doc.Raw ?? (object)doc.Body, doc.UpdatedMs]);
        }

        execute("INSERT INTO meta(key, value) VALUES (?1, ?2)", [key, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)]);
    }
}
