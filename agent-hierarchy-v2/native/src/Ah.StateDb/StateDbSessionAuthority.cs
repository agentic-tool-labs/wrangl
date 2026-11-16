using System.Text;
using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.StateDb;

/// <summary>
/// The session registrations' way into the database. A registration or removal is one transaction over the <c>sessions</c> and
/// <c>sessions_unplaced</c> rows. No file is written.
/// </summary>
internal sealed class StateDbSessionAuthority(string ahHome, IStateDbDirect direct, StateDbReadPool? reads = null) : ISessionAuthority
{
    public bool IsDatabase => !StateDbAuthority.NeverCreated(ahHome);

    public ReadOnlyMemory<byte>? ReadStored(string sessionId) =>
        IsDatabase ? StateDbStoreReads.Reading(ahHome, connection => LiveDocument(connection, sessionId), reads) : null;

    public IReadOnlyList<KeyValuePair<string, ReadOnlyMemory<byte>>> ListStored() =>
        IsDatabase
            ? StateDbStoreReads.Reading<IReadOnlyList<KeyValuePair<string, ReadOnlyMemory<byte>>>>(ahHome, static connection =>
                [.. StateDbRows.Run(connection, LiveDocsSql, [])
                    .Select(static r => (Id: (string)r[0]!, Doc: (string)r[1]!))
                    .OrderBy(static row => row.Id, StringComparer.Ordinal)
                    .Select(static row => KeyValuePair.Create(row.Id, new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(row.Doc))))], reads)
            : [];

    public IReadOnlyList<KeyValuePair<string, ReadOnlyMemory<byte>>> ListEnded() =>
        IsDatabase
            ? StateDbStoreReads.Reading<IReadOnlyList<KeyValuePair<string, ReadOnlyMemory<byte>>>>(ahHome, static connection =>
                [.. StateDbRows.Run(connection, EndedDocsSql, [])
                    .Select(static r => KeyValuePair.Create((string)r[0]!, new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes((string)r[1]!))))], reads)
            : [];

    public IReadOnlyList<SessionHistoryRow> ListHistory(string hierDir) =>
        IsDatabase
            ? StateDbStoreReads.Reading<IReadOnlyList<SessionHistoryRow>>(ahHome, connection =>
                [.. StateDbRows.Run(connection, "SELECT session_id, team, member, started_at, ended_at FROM sessions WHERE hier_dir = ?1", [hierDir])
                    .Select(static r => new SessionHistoryRow((string)r[0]!, r[1] as string, r[2] as string, (long)r[3]!, r[4] as long?))], reads)
            : [];

    // the ended sessions with no live row of the same id in either table
    private const string EndedDocsSql =
        "SELECT session_id, doc FROM sessions s WHERE ended_at IS NOT NULL AND doc IS NOT NULL " +
        "AND NOT EXISTS (SELECT 1 FROM sessions_unplaced u WHERE u.session_id = s.session_id AND u.ended_at IS NULL AND u.doc IS NOT NULL) " +
        "UNION ALL SELECT session_id, doc FROM sessions_unplaced u WHERE ended_at IS NOT NULL AND doc IS NOT NULL " +
        "AND NOT EXISTS (SELECT 1 FROM sessions s WHERE s.session_id = u.session_id AND (s.ended_at IS NULL OR s.doc IS NOT NULL))";

    // one document per live session: an id can sit in both tables after an upgrade, and the placed row wins
    private const string LiveDocsSql =
        "SELECT session_id, doc FROM sessions WHERE ended_at IS NULL AND doc IS NOT NULL " +
        "UNION ALL SELECT session_id, doc FROM sessions_unplaced u WHERE ended_at IS NULL AND doc IS NOT NULL " +
        "AND NOT EXISTS (SELECT 1 FROM sessions s WHERE s.session_id = u.session_id AND s.ended_at IS NULL AND s.doc IS NOT NULL)";

    public long ChangeToken() =>
        StateDbStoreReads.Reading(ahHome, static connection => StateDbRows.Run(connection, "SELECT seq FROM domain_seq WHERE domain = 'sessions'", []) is { Count: > 0 } rows ? (long)rows[0][0]! : 0, reads);

    public SessionAuthorityResult Register(string registrationFile, string sessionId, Func<ReadOnlyMemory<byte>?, SessionDecision> decide)
    {
        if (!IsOwnFile(registrationFile, sessionId))
        {
            return OutsideHome(registrationFile);
        }

        if (StateDbAuthority.EnsureForWrite(ahHome) is { } why)
        {
            return new SessionAuthorityResult(false, "failed", why);
        }

        var written = direct.Write(tx =>
        {
            var connection = ((StateDbTx)tx).Connection;
            var decision = decide(LiveDocument(connection, sessionId));
            switch (decision.Verdict)
            {
                case SessionVerdict.Refused:
                    return new SessionAuthorityResult(false, "refused", decision.Why ?? string.Empty);
                case SessionVerdict.Unchanged:
                    return new SessionAuthorityResult(true, "ok", string.Empty);
            }

            var bytes = decision.Document.ToArray();
            SessionRow? row;
            try
            {
                row = StateDbSessions.Row(JsonText.Parse(bytes), DateTime.UtcNow, sessionId + ".json", Encoding.UTF8.GetString(bytes));
            }
            catch (JsonParseException e)
            {
                return new SessionAuthorityResult(false, "failed", $"the registration is not JSON: {e.Message}");
            }

            if (row is null || row.SessionId != sessionId)
            {
                return new SessionAuthorityResult(false, "failed", "the registration does not hold the session id it is written for");
            }

            _ = StateDbSessions.Upsert(connection, row);
            return new SessionAuthorityResult(true, "ok", string.Empty, true, bytes);
        });
        return written.Ok ? written.Value! : new SessionAuthorityResult(false, "failed", written.Message);
    }

    public SessionAuthorityResult Unregister(string registrationFile, string sessionId)
    {
        if (!IsOwnFile(registrationFile, sessionId))
        {
            return OutsideHome(registrationFile);
        }

        if (StateDbAuthority.EnsureForWrite(ahHome) is { } why)
        {
            return new SessionAuthorityResult(false, "failed", why);
        }

        var at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var ended = direct.Write(tx =>
        {
            var connection = ((StateDbTx)tx).Connection;
            _ = StateDbRows.Run(connection, StateDbSessions.EndSql("sessions"), [at, sessionId]);
            var changed = connection.Changes;
            _ = StateDbRows.Run(connection, StateDbSessions.EndSql("sessions_unplaced"), [at, sessionId]);
            return new SessionAuthorityResult(true, "ok", string.Empty, changed + connection.Changes > 0);
        });
        return ended.Ok ? ended.Value! : new SessionAuthorityResult(false, "failed", ended.Message);
    }

    public bool EndGone(string sessionId, long pid, string pidStart)
    {
        if (!IsDatabase)
        {
            return false;
        }

        var at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var ended = direct.Write(tx =>
        {
            var connection = ((StateDbTx)tx).Connection;
            _ = StateDbRows.Run(connection, StateDbSessions.EndSql("sessions") + " AND pid = ?3 AND pid_start = ?4", [at, sessionId, pid, pidStart]);
            var changed = connection.Changes;
            _ = StateDbRows.Run(connection, StateDbSessions.EndSql("sessions_unplaced") + " AND pid = ?3 AND pid_start = ?4", [at, sessionId, pid, pidStart]);
            return changed + connection.Changes > 0;
        });
        return ended.Ok && ended.Value;
    }

    public int PruneEnded(long endedBeforeMs, bool countOnly)
    {
        if (!IsDatabase)
        {
            return 0;
        }

        const string Where = " WHERE ended_at IS NOT NULL AND ended_at < ?1";
        if (countOnly)
        {
            return StateDbStoreReads.Reading(ahHome, connection =>
                (int)(long)StateDbRows.Run(connection, "SELECT (SELECT count(*) FROM sessions" + Where + ") + (SELECT count(*) FROM sessions_unplaced" + Where + ")", [endedBeforeMs])[0][0]!, reads);
        }

        var pruned = direct.Write(tx =>
        {
            var connection = ((StateDbTx)tx).Connection;
            _ = StateDbRows.Run(connection, "DELETE FROM sessions" + Where, [endedBeforeMs]);
            var changed = connection.Changes;
            _ = StateDbRows.Run(connection, "DELETE FROM sessions_unplaced" + Where, [endedBeforeMs]);
            return changed + connection.Changes;
        });
        return pruned.Ok ? pruned.Value : throw new InvalidOperationException(pruned.Message);
    }

    /// <summary>The document of the live registration of the session, from <c>sessions</c> or else <c>sessions_unplaced</c>; null when it has none, or none with a document.</summary>
    private static ReadOnlyMemory<byte>? LiveDocument(SqliteConnection connection, string sessionId)
    {
        foreach (var table in new[] { "sessions", "sessions_unplaced" })
        {
            var rows = StateDbRows.Run(connection, StateDbSessions.LiveDocumentSql(table), [sessionId]);
            if (rows.Count > 0)
            {
                return Encoding.UTF8.GetBytes((string)rows[0][0]!);
            }
        }

        return null;
    }

    /// <summary>Whether <paramref name="registrationFile"/> is the file of <paramref name="sessionId"/> in this authority's own ah home, and nowhere else.</summary>
    private bool IsOwnFile(string registrationFile, string sessionId) =>
        StateDbSessions.OwnFile(ahHome, sessionId) is { } own
        && string.Equals(Path.GetFullPath(registrationFile), Path.GetFullPath(own), StringComparison.Ordinal);

    private SessionAuthorityResult OutsideHome(string file) => new(false, "outside-home", $"{file} is not a registration file of {ahHome}; nothing was changed");
}
