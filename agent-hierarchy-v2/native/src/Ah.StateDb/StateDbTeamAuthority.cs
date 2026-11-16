using System.Text;
using Ah.Abstractions;
using Ah.Core.Digests;

namespace Ah.StateDb;

/// <summary>
/// The team domain's way into the database. A write or clear is one transaction over the <c>teams</c> and <c>members</c> rows, and the
/// writer's digest is compared with the stored document inside it, so the database alone decides whether a write is stale. No file is written.
/// </summary>
internal sealed class StateDbTeamAuthority(string ahHome, IStateDbDirect direct, StateDbReadPool? reads = null) : ITeamAuthority
{
    public bool IsDatabase => !StateDbAuthority.NeverCreated(ahHome);

    public TeamAuthorityResult Write(string teamFile, ReadOnlyMemory<byte> document, string? expectedDigest = null)
    {
        if (StateDbTeamKey.Of(teamFile) is not { } key)
        {
            return new TeamAuthorityResult(false, "invalid-team", $"{teamFile} has no usable team key");
        }

        if (StateDbAuthority.EnsureForWrite(ahHome) is { } why)
        {
            return new TeamAuthorityResult(false, "failed", why);
        }

        var at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var bytes = document.ToArray();
        var written = direct.Write(tx =>
        {
            var connection = ((StateDbTx)tx).Connection;
            if (Mismatch(connection, key, expectedDigest))
            {
                return new TeamAuthorityResult(false, "digest-mismatch", "the team changed since it was read");
            }

            StateDbTeams.RowResult made;
            try
            {
                made = StateDbTeams.RowOf(teamFile, bytes, at, null);
            }
            catch (Ah.Core.Json.JsonParseException e)
            {
                return new TeamAuthorityResult(false, "invalid-team", $"the team document is not JSON: {e.Message}");
            }

            if (made.Row is not { } row)
            {
                return new TeamAuthorityResult(false, "invalid-team", made.Problem ?? "the document has no members array");
            }

            var changed = StateDbTeams.Apply(connection, [], [row], false, inTransaction: true)!.Value.Changed > 0;
            return new TeamAuthorityResult(true, "ok", string.Empty, changed);
        });
        if (!written.Ok)
        {
            return new TeamAuthorityResult(false, "failed", written.Message);
        }

        return written.Value!;
    }

    public TeamAuthorityResult Clear(string teamFile, string? expectedDigest = null)
    {
        if (StateDbTeamKey.Of(teamFile) is not { } key)
        {
            return new TeamAuthorityResult(false, "invalid-team", $"{teamFile} has no usable team key");
        }

        if (StateDbAuthority.EnsureForWrite(ahHome) is { } why)
        {
            return new TeamAuthorityResult(false, "failed", why);
        }

        var cleared = direct.Write(tx =>
        {
            var connection = ((StateDbTx)tx).Connection;
            if (Mismatch(connection, key, expectedDigest))
            {
                return new TeamAuthorityResult(false, "digest-mismatch", "the team changed since it was read");
            }

            _ = StateDbRows.Run(connection, "DELETE FROM members WHERE hier_dir = ?1 AND team = ?2", [key.HierDir, key.Name]);
            _ = StateDbRows.Run(connection, "DELETE FROM teams WHERE hier_dir = ?1 AND team = ?2", [key.HierDir, key.Name]);
            return new TeamAuthorityResult(true, "ok", string.Empty, connection.Changes > 0);
        });
        if (!cleared.Ok)
        {
            return new TeamAuthorityResult(false, "failed", cleared.Message);
        }

        return cleared.Value!;
    }

    public ReadOnlyMemory<byte>? Read(string teamFile)
    {
        if (StateDbTeamKey.Of(teamFile) is not { } key || !IsDatabase)
        {
            return null;
        }

        return ReadStored(teamFile);
    }

    public ReadOnlyMemory<byte>? ReadStored(string teamFile)
    {
        if (StateDbTeamKey.Of(teamFile) is not { } key || !IsDatabase)
        {
            return null;
        }

        if (ReadDoc(key) is { } doc)
        {
            return Encoding.UTF8.GetBytes(doc);
        }

        // ponytail: the miss's take stays (files-off-disk §4 gap, atp-1518): every team write builds on this read, so a file read as absent would be overwritten by a document without its members
        return File.Exists(teamFile) && ImportMissed(teamFile) is { } imported ? imported : (ReadOnlyMemory<byte>?)null;
    }

    private const int ReadBusyMs = 500;

    // a repo this home never recorded: only a miss pays for the stat, and the file stays where it is
    private byte[]? ImportMissed(string teamFile)
    {
        try
        {
            using var connection = SqliteConnection.Open(StateDbBackups.DatabasePath(ahHome), SqliteOpenMode.ReadWrite, ReadBusyMs);
            return StateDbStoreImport.ImportMissedTeam(connection, teamFile, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) is { } doc ? Encoding.UTF8.GetBytes(doc) : null;
        }
        catch (SqliteException e)
        {
            throw new Ah.Core.Json.StoreUnavailableException(e.Message);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Ah.Core.Json.JsonParseException)
        {
            return null;
        }
    }

    public IReadOnlyList<string> ListTeams(string hierDir)
    {
        if (!IsDatabase)
        {
            return [];
        }

        var key = StateDbTeamKey.RealPath(hierDir);
        return Reading(connection =>
            (IReadOnlyList<string>)[.. StateDbRows.Run(connection, "SELECT team FROM teams WHERE hier_dir = ?1 ORDER BY team", [key]).Select(static r => (string)r[0]!).Where(static t => t != StateDbTeamKey.LegacyName)]);
    }

    public string? ReadHistory(string teamHome)
    {
        if (!IsDatabase)
        {
            return null;
        }

        try
        {
            var key = StateDbTeamKey.RealPath(teamHome);
            return Reading(connection => StateDbRows.Run(connection, "SELECT body FROM team_history WHERE home = ?1", [key]) is [var row] ? (string?)row[0] : null);
        }
        catch (SqliteException e) when (e.Message.Contains("no such table", StringComparison.Ordinal))
        {
            // a database not yet migrated to V39 holds no history row; the caller reads the file
            return null;
        }
    }

    public TeamAuthorityResult WriteHistory(string teamHome, string document)
    {
        if (StateDbAuthority.EnsureForWrite(ahHome) is { } why)
        {
            return new TeamAuthorityResult(false, "failed", why);
        }

        var key = StateDbTeamKey.RealPath(teamHome);
        var at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var written = direct.Write(tx =>
        {
            _ = StateDbRows.Run(((StateDbTx)tx).Connection, "INSERT OR REPLACE INTO team_history(home, body, updated_ms) VALUES (?1, ?2, ?3)", [key, document, at]);
            return new TeamAuthorityResult(true, "ok", string.Empty, true);
        });
        return written.Ok ? written.Value! : new TeamAuthorityResult(false, "failed", written.Message);
    }

    public IReadOnlyList<string> SnapshotFiles(string teamHome, string team) =>
        Snapshots(teamHome, (connection, home) =>
            (IReadOnlyList<string>)[.. StateDbRows.Run(connection, "SELECT file FROM team_snapshots WHERE home = ?1 AND team = ?2", [home, team]).Select(static r => (string)r[0]!)]) ?? [];

    public string? ReadSnapshot(string teamHome, string file) =>
        Snapshots<string>(teamHome, (connection, home) =>
            StateDbRows.Run(connection, "SELECT body FROM team_snapshots WHERE home = ?1 AND file = ?2", [home, file]) is [var row] ? (string?)row[0] : null);

    public SnapshotWriteResult WriteSnapshot(string teamHome, string team, string ts, string document, int? keep, string? pinned)
    {
        if (StateDbAuthority.EnsureForWrite(ahHome) is { } why)
        {
            return new SnapshotWriteResult(false, why, 0);
        }

        var home = StateDbTeamKey.RealPath(teamHome);
        var file = $"{team}-{ts}.json";
        var at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var written = direct.Write(tx =>
        {
            var connection = ((StateDbTx)tx).Connection;
            _ = StateDbRows.Run(connection, "INSERT OR REPLACE INTO team_snapshots(home, file, team, ts, body, created_ms) VALUES (?1, ?2, ?3, ?4, ?5, ?6)", [home, file, team, ts, document, at]);
            if (keep is not { } k)
            {
                return 0;
            }

            _ = StateDbRows.Run(
                connection,
                "DELETE FROM team_snapshots WHERE home = ?1 AND team = ?2 AND file IS NOT ?3 AND file NOT IN (SELECT file FROM team_snapshots WHERE home = ?1 AND team = ?2 ORDER BY ts DESC LIMIT ?4)",
                [home, team, pinned, k]);
            return connection.Changes;
        });
        return written.Ok ? new SnapshotWriteResult(true, string.Empty, (int)written.Value) : new SnapshotWriteResult(false, written.Message, 0);
    }

    // a snapshot read: null with no database or on one not yet migrated to V39 (no rows there); busy throws, so a read never looks empty
    private T? Snapshots<T>(string teamHome, Func<SqliteConnection, string, T?> read)
        where T : class
    {
        if (!IsDatabase)
        {
            return null;
        }

        try
        {
            var home = StateDbTeamKey.RealPath(teamHome);
            return Reading(connection => read(connection, home));
        }
        catch (SqliteException e) when (e.Message.Contains("no such table", StringComparison.Ordinal))
        {
            return null;
        }
    }

    public long ChangeToken() =>
        Reading(static connection => StateDbRows.Run(connection, "SELECT seq FROM domain_seq WHERE domain = 'teams'", []) is { Count: > 0 } rows ? (long)rows[0][0]! : 0);

    private T Reading<T>(Func<SqliteConnection, T> read) => StateDbStoreReads.Reading(ahHome, read, reads);

    /// <summary>Whether the digest a writer presented is not the digest of the stored document (or <c>absent</c> when there is none).</summary>
    private static bool Mismatch(SqliteConnection connection, (string HierDir, string Name) key, string? expectedDigest)
    {
        if (!ContentDigest.IsValidExpect(expectedDigest))
        {
            return false;
        }

        var stored = StateDbRows.Run(connection, "SELECT doc FROM teams WHERE hier_dir = ?1 AND team = ?2", [key.HierDir, key.Name]);
        var current = stored.Count == 0 ? "absent" : ContentDigest.OfBytes(Encoding.UTF8.GetBytes((string)stored[0][0]!));
        return !string.Equals(current, expectedDigest, StringComparison.Ordinal);
    }

    private string? ReadDoc((string HierDir, string Name) key) => Reading(connection =>
        StateDbRows.Run(connection, "SELECT doc FROM teams WHERE hier_dir = ?1 AND team = ?2", [key.HierDir, key.Name]) is [var stored] ? (string?)stored[0] : null);
}
