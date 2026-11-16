using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.StateDb;

/// <summary>
/// The <c>worktree_grants</c> and <c>worktree_grant_audit</c> tables (schema 38, per-dir-files-to-db S11e) over the state database of
/// <paramref name="ahHome"/>. <paramref name="oldAuditFile"/> is the pre-upgrade worktree audit log: its rows are never imported, and
/// it is read only for a team the table holds no document audit row for.
/// </summary>
internal sealed class StateDbGrants(string ahHome, string oldAuditFile, Func<long>? nowMs = null) : IGrantSource
{
    private const int ImportBusyMs = 250;

    /// <summary>The meta key recording that this home took <paramref name="hierDir"/>'s grants documents into the table.</summary>
    internal static string TakenKey(string hierDir) => "grants.taken:" + StateDbTeamKey.RealPath(hierDir);

    private sealed record Row(object Stored, string Digest, long UpdatedAt);

    public GrantsLog? Read(string hierDir)
    {
        if (StateDbAuthority.NeverCreated(ahHome))
        {
            return null;
        }

        var key = StateDbTeamKey.RealPath(hierDir);
        (Dictionary<string, Row> Rows, Dictionary<string, string> Audit, long? TakenAt) Once() => StateDbStoreReads.Reading(ahHome, c =>
        {
            var rows = StateDbRows.Run(c, "SELECT team, body, digest, updated_at FROM worktree_grants WHERE hier_dir = ?1", [key])
                .ToDictionary(static r => (string)r[0]!, static r => new Row(r[1]!, (string)r[2]!, (long)r[3]!), StringComparer.Ordinal);
            var audit = LastDocumentRows(StateDbRows.Run(c, "SELECT team, body FROM worktree_grant_audit WHERE hier_dir = ?1 ORDER BY seq", [key])
                .Select(static r => ((string?)r[0], (string)r[1]!)));
            return (rows, audit, TakenAt((sql, args) => StateDbRows.Run(c, sql, args), hierDir));
        });

        // rows only (files-off-disk §4): the import pass takes the dir's files
        (Dictionary<string, Row> Rows, Dictionary<string, string> Audit, long? TakenAt) read;
        try
        {
            read = Once();
        }
        catch (Exception e) when (e is StoreUnavailableException or InvalidOperationException)
        {
            // busy past the read budget, newer or unreadable: no documents at all (§6c G5), and nothing written
            return GrantsLog.Unreadable;
        }

        var documents = read.Rows.ToDictionary(static r => r.Key, static r => Body(r.Value.Stored), StringComparer.Ordinal);
        try
        {
            if (read.TakenAt is { } takenAt && Skewed(hierDir, takenAt, read.Rows))
            {
                return GrantsLog.Unreadable;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return GrantsLog.Unreadable;
        }

        IReadOnlyDictionary<string, string>? old = null;
        return GrantsLog.Of([.. documents.OrderBy(static d => d.Key, StringComparer.Ordinal).Select(d =>
        {
            // a row edited past its digest carries no audit row, so it reads as inconsistent (enforce, no extras)
            if (read.Rows.TryGetValue(d.Key, out var row) && Digest(row.Stored) != row.Digest)
            {
                return new GrantDocument(d.Key, d.Value, null);
            }

            return new GrantDocument(d.Key, d.Value, read.Audit.TryGetValue(d.Key, out var audit) ? audit : (old ??= OldAuditRows()).GetValueOrDefault(d.Key));
        })]);
    }

    public string Take(IDbOpTransaction tx, string hierDir)
    {
        ArgumentNullException.ThrowIfNull(tx);
        Func<string, object?[], IReadOnlyList<object?[]>> query = (sql, args) => tx.Query(sql, args);
        TakeIn(query, (sql, args) => tx.Execute(sql, args), hierDir);
        var key = StateDbTeamKey.RealPath(hierDir);

        // the write sees what the read sees: a current-build write never re-grants what an older build revoked in a file
        var rows = query("SELECT team, body, digest, updated_at FROM worktree_grants WHERE hier_dir = ?1", [key])
            .ToDictionary(static r => (string)r[0]!, static r => new Row(r[1]!, (string)r[2]!, (long)r[3]!), StringComparer.Ordinal);
        if (Skewed(hierDir, TakenAt(query, hierDir) ?? long.MaxValue, rows))
        {
            throw new InvalidOperationException("the grants changed outside this build; left as it is");
        }

        return key;
    }

    /// <summary>
    /// Whether a grants file was written after the take and after its row (an older binary still writes files, a revoke among them)
    /// with bytes that are not the row's; one whose bytes are the row's (a touch, a checkout, a restore) changes nothing. Throws when a
    /// file cannot be read.
    /// </summary>
    private static bool Skewed(string hierDir, long takenAt, Dictionary<string, Row> rows) =>
        FilesOf(hierDir).Any(file =>
        {
            var row = rows.GetValueOrDefault(TeamOf(file));
            return MtimeMs(file) > Math.Max(takenAt, row?.UpdatedAt ?? 0) && Digest(Stored(File.ReadAllBytes(file))) != row?.Digest;
        });

    public IReadOnlyList<string> AuditRows()
    {
        List<string> rows = [];
        try
        {
            if (!StateDbAuthority.NeverCreated(ahHome))
            {
                rows.AddRange(StateDbStoreReads.Reading(ahHome, c => StateDbRows.Run(c, "SELECT body FROM worktree_grant_audit ORDER BY seq", [])).Select(static r => (string)r[0]!));
            }

            if (File.Exists(oldAuditFile))
            {
                rows.AddRange(File.ReadLines(oldAuditFile).Where(static l => l.Length > 0));
            }
        }
        catch (Exception e) when (e is StoreUnavailableException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return [];
        }

        return rows;
    }

    /// <summary>The import pass's take (files-off-disk §3.2), on its own connection: false when it failed (a file unreadable, the database busy or not writable), and nothing is written.</summary>
    internal bool Take(string hierDir)
    {
        try
        {
            using var connection = SqliteConnection.Open(StateDbBackups.DatabasePath(ahHome), SqliteOpenMode.ReadWrite, ImportBusyMs);
            connection.Exec("BEGIN IMMEDIATE");
            try
            {
                TakeIn((sql, args) => StateDbRows.Run(connection, sql, args), (sql, args) => StateDbRows.Run(connection, sql, args), hierDir);
                connection.Exec("COMMIT");
                return true;
            }
            catch
            {
                try
                {
                    connection.Exec("ROLLBACK");
                }
                catch (SqliteException)
                {
                    // the transaction is already gone
                }

                throw;
            }
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// The one take of a hierarchy dir's grants, inside the caller's transaction: each <c>grants/&lt;team&gt;.json</c> stored as one row,
    /// its bytes as they are, unless the team already has a row (written since the upgrade), and the take recorded, even with no grants
    /// dir. The take's time is stamped before the files are read, so a file written during the take is later than the stamp. The files
    /// stay where they are. Throws when a file cannot be read.
    /// </summary>
    private void TakeIn(Func<string, object?[], IReadOnlyList<object?[]>> query, Action<string, object?[]> execute, string hierDir)
    {
        if (TakenAt(query, hierDir) is not null)
        {
            return;
        }

        var stamp = (nowMs ?? (static () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))();
        var key = StateDbTeamKey.RealPath(hierDir);
        foreach (var (team, stored, updatedAt) in FileDocuments(hierDir))
        {
            execute(
                "INSERT INTO worktree_grants(hier_dir, team, body, digest, updated_at) VALUES (?1, ?2, ?3, ?4, ?5) ON CONFLICT(hier_dir, team) DO NOTHING",
                [key, team, stored, Digest(stored), updatedAt]);
        }

        execute("INSERT INTO meta(key, value) VALUES (?1, ?2)", [TakenKey(hierDir), stamp.ToString(CultureInfo.InvariantCulture)]);
    }

    /// <summary>When the dir was taken (unix ms), or null when it is untaken; a stamp that does not parse is never earlier than a file.</summary>
    private static long? TakenAt(Func<string, object?[], IReadOnlyList<object?[]>> query, string hierDir) =>
        query("SELECT value FROM meta WHERE key = ?1", [TakenKey(hierDir)]) is [var row]
            ? long.TryParse(row[0] as string, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms) ? ms : long.MaxValue
            : null;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>The SHA-256 of a document's stored bytes, the row's stale-write check.</summary>
    internal static string Digest(object stored) => Convert.ToHexStringLower(SHA256.HashData(stored as byte[] ?? Encoding.UTF8.GetBytes((string)stored)));

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

    /// <summary>A stored body as text: text as it is, bytes (a file that was not valid UTF-8) decoded leniently.</summary>
    private static string Body(object? stored) => stored is byte[] bytes ? Encoding.UTF8.GetString(bytes) : (string)stored!;

    private static IEnumerable<string> FilesOf(string hierDir)
    {
        var dir = Path.Combine(hierDir, "grants");
        return Directory.Exists(dir) ? Directory.EnumerateFiles(dir).Where(static f => f.EndsWith(".json", StringComparison.Ordinal)) : [];
    }

    private static string TeamOf(string file) => Path.GetFileName(file)[..^".json".Length];

    private static long MtimeMs(string file) => new DateTimeOffset(File.GetLastWriteTimeUtc(file)).ToUnixTimeMilliseconds();

    /// <summary>The dir's grants documents on disk, by team; none when there is no grants dir. Throws when one cannot be read.</summary>
    private static List<(string Team, object Stored, long UpdatedAt)> FileDocuments(string hierDir) =>
        [.. FilesOf(hierDir).Select(static f => (TeamOf(f), Stored(File.ReadAllBytes(f)), MtimeMs(f)))];

    /// <summary>One old audit log's doc rows as last loaded, with the length and mtime they were loaded at; <c>-1</c> for an absent file.</summary>
    private sealed record OldAudit(long Length, long MtimeTicks, IReadOnlyDictionary<string, string> Rows, int Loads);

    /// <summary>Process-wide, by log path: the log is re-read only when its length or mtime changes (b459).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, OldAudit> OldAuditCache = new(StringComparer.Ordinal);

    /// <summary>Test seam: how many times <paramref name="path"/> was loaded into the cache; 0 when never.</summary>
    internal static int OldAuditLoads(string path) => OldAuditCache.TryGetValue(path, out var hit) ? hit.Loads : 0;

    /// <summary>Each team's latest document row in the old audit log; none for a log that is missing or cannot be read.</summary>
    private IReadOnlyDictionary<string, string> OldAuditRows()
    {
        if (string.IsNullOrEmpty(oldAuditFile))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        try
        {
            var info = new FileInfo(oldAuditFile);
            var (length, mtime) = info.Exists ? (info.Length, info.LastWriteTimeUtc.Ticks) : (-1L, -1L);
            var hit = OldAuditCache.GetValueOrDefault(oldAuditFile);
            if (hit is not null && hit.Length == length && hit.MtimeTicks == mtime)
            {
                return hit.Rows;
            }

            var rows = info.Exists ? OldDocumentRows(File.ReadLines(oldAuditFile)) : new Dictionary<string, string>(StringComparer.Ordinal);
            OldAuditCache[oldAuditFile] = new OldAudit(length, mtime, rows, (hit?.Loads ?? 0) + 1);
            return rows;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    /// <summary>By team, the last of <paramref name="lines"/> that changed a document (<c>"doc": true</c>), each line parsed once.</summary>
    private static Dictionary<string, string> OldDocumentRows(IEnumerable<string> lines)
    {
        var last = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            try
            {
                if (JsonText.Parse(line) is { IsMap: true } row && row.AsObject().Get("team") is { IsText: true } team
                    && row.AsObject().Get("doc") is { Kind: JsonKind.Bool, IsTruthy: true })
                {
                    last[team.AsString()] = line;
                }
            }
            catch (JsonParseException)
            {
                // a torn or foreign line carries no hash
            }
        }

        return last;
    }

    /// <summary>By team, the last of <paramref name="rows"/> that changed a document (<c>"doc": true</c>), as written.</summary>
    private static Dictionary<string, string> LastDocumentRows(IEnumerable<(string? Team, string Row)> rows)
    {
        var last = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (team, row) in rows)
        {
            if (team is not null && IsDocumentRow(row))
            {
                last[team] = row;
            }
        }

        return last;
    }

    private static bool IsDocumentRow(string line)
    {
        try
        {
            return JsonText.Parse(line) is { IsMap: true } row && row.AsObject().Get("doc") is { Kind: JsonKind.Bool, IsTruthy: true };
        }
        catch (JsonParseException)
        {
            // a torn or foreign line carries no hash
            return false;
        }
    }
}
