using System.Globalization;
using System.Text;
using Ah.Core.Json;

namespace Ah.StateDb;

/// <summary>One registration file as the sessions table holds it.</summary>
internal sealed record SessionRow(string File, string SessionId, string HierDir, string? Team, string? Member, long? Pid, string? PidStart, string? Cwd, string? PaneId, string? AhVersion, long At, IReadOnlyList<string>? TeamFiles = null, string? Doc = null);

/// <summary>Reads session registration files into rows and stores them in <c>sessions</c> or <c>sessions_unplaced</c>.</summary>
internal static class StateDbSessions
{
    /// <summary>
    /// Why a stored session sits in <c>sessions_unplaced</c> after the migration: its registration names no team file, or no registration
    /// of it exists. After an upgrade the importer may also hold the same session in <c>sessions</c>.
    /// </summary>
    internal const string NoTeamFile = "no team file";

    internal const string NoRegistration = "no registration";

    internal static string RegistrationsDirectory(string ahHome) => Ah.Core.Text.AhPaths.HomeEntry(ahHome, "registrations");

    /// <summary>
    /// The registration file of <paramref name="sessionId"/> under <paramref name="ahHome"/>, or null when the id could name another
    /// place or the path leads, through a link, outside the home.
    /// </summary>
    internal static string? OwnFile(string ahHome, string sessionId)
    {
        if (sessionId.Length == 0 || sessionId.IndexOfAny(['/', '\\']) >= 0 || sessionId.Contains("..", StringComparison.Ordinal))
        {
            return null;
        }

        var file = Path.Join(RegistrationsDirectory(ahHome), sessionId + ".json");
        return Ah.Core.Text.PathGuard.Within(ahHome, file) ? file : null;
    }

    /// <summary>The registration files with the rows they describe. <c>Problem</c> is set when a file cannot be used, which makes the pass incomplete.</summary>
    internal sealed record Listing(List<SessionRow> Rows, int Files, string[] Names, string? Problem, IReadOnlyList<string>? Skipped = null);

    /// <summary>The registration file names only, in ordinal order; null when the directory cannot be listed.</summary>
    internal static string[]? Names(string ahHome)
    {
        try
        {
            var directory = RegistrationsDirectory(ahHome);
            return Directory.Exists(directory) ? [.. Directory.GetFiles(directory, "*.json").Select(Path.GetFileName).Order(StringComparer.Ordinal)!] : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static Listing Read(string ahHome)
    {
        List<SessionRow> rows = [];
        string[] names;
        string? problem = null;
        try
        {
            var directory = RegistrationsDirectory(ahHome);
            names = Directory.Exists(directory) ? [.. Directory.GetFiles(directory, "*.json").Select(f => Path.GetFileName(f)!).Order(StringComparer.Ordinal)] : [];
        }
        catch (UnauthorizedAccessException)
        {
            return new Listing(rows, 0, [], null, [RegistrationsDirectory(ahHome)]);
        }
        catch (IOException)
        {
            return new Listing(rows, 0, [], "the registrations directory could not be listed");
        }

        List<string> skipped = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (var name in names)
        {
            var file = Path.Join(RegistrationsDirectory(ahHome), name);
            try
            {
                var bytes = File.ReadAllBytes(file);
                var row = Row(JsonText.Parse(bytes), File.GetLastWriteTimeUtc(file), name, Encoding.UTF8.GetString(bytes));
                if (row is null)
                {
                    problem ??= $"{name} holds no session id";
                }
                else if (!seen.Add(row.SessionId))
                {
                    problem ??= $"{name} repeats session {row.SessionId}";
                }
                else
                {
                    rows.Add(row);
                }
            }
            catch (UnauthorizedAccessException)
            {
                skipped.Add(file);
            }
            catch (Exception e) when (e is IOException or FormatException or JsonParseException)
            {
                problem ??= $"{name} could not be read";
            }
        }

        return new Listing(rows, names.Length, names, problem, skipped);
    }

    /// <summary>The rows of the named files; a file that cannot be used is left out.</summary>
    internal static List<SessionRow> ReadNames(string ahHome, IReadOnlyList<string> names)
    {
        List<SessionRow> rows = [];
        foreach (var name in names)
        {
            var file = Path.Join(RegistrationsDirectory(ahHome), name);
            try
            {
                var bytes = File.ReadAllBytes(file);
                if (Row(JsonText.Parse(bytes), File.GetLastWriteTimeUtc(file), name, Encoding.UTF8.GetString(bytes)) is { } row)
                {
                    rows.Add(row);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException or JsonParseException)
            {
                // the next pass reads it again
            }
        }

        return rows;
    }

    /// <summary>The row a registration document describes; <paramref name="file"/> names its file, and null comes back when it holds no session id.</summary>
    internal static SessionRow? Row(JsonValue record, DateTime modified, string file, string doc)
    {
        if (record.Member("session_id") is not { IsText: true } id || id.AsString().Length == 0)
        {
            return null;
        }

        var address = PrimaryAddress(record);
        var at = record.Member("at") is { IsText: true } stamp && DateTimeOffset.TryParse(stamp.AsString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.ToUnixTimeMilliseconds()
            : new DateTimeOffset(modified, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var split = address?.IndexOf('@', StringComparison.Ordinal) ?? -1;
        return new SessionRow(
            file,
            id.AsString(),
            HierDirOf(record, address),
            split > 0 ? address![(split + 1)..] : null,
            split > 0 ? address![..split] : null,
            record.Member("pid") is { IsNumber: true } pid ? (long)pid.AsNumber() : null,
            Text(record, "pid_start"),
            Text(record, "cwd"),
            Text(record, "pane_id"),
            Text(record, "ah_version"),
            at,
            TeamFilesOf(record),
            doc);
    }

    /// <summary>Every team file a registration names: its <c>team_file</c> and each <c>team_files</c> entry.</summary>
    private static List<string> TeamFilesOf(JsonValue record)
    {
        List<string> files = [];
        if (Text(record, "team_file") is { Length: > 0 } one)
        {
            files.Add(one);
        }

        if (record.Member("team_files") is { IsMap: true } byAddress)
        {
            foreach (var key in byAddress.AsObject().Keys)
            {
                if (byAddress.Member(key) is { IsText: true } file && file.AsString().Length > 0)
                {
                    files.Add(file.AsString());
                }
            }
        }

        return files;
    }

    /// <summary>
    /// The team file of the team the primary address belongs to: the <c>team_files</c> entry for that address, else <c>team_file</c> when
    /// the registration names a single team. A registration of several teams with no entry for its primary address gets none rather than
    /// another team's file, which differs on purpose from the mailbox lookup that falls back to <c>team_file</c>.
    /// </summary>
    internal static string? TeamFileOf(JsonValue record, string? primaryAddress)
    {
        if (record.Member("team_files") is { IsMap: true } byAddress && byAddress.AsObject().Keys.Any())
        {
            return primaryAddress is not null && byAddress.Member(primaryAddress) is { IsText: true } file && file.AsString().Length > 0 ? file.AsString() : null;
        }

        return Text(record, "team_file");
    }

    private static string HierDirOf(JsonValue record, string? primaryAddress) => StateDbTeamKey.Of(TeamFileOf(record, primaryAddress))?.HierDir ?? string.Empty;

    /// <summary>The first address in ordinal order, so the team and member do not change when a registration lists its boxes in another order.</summary>
    private static string? PrimaryAddress(JsonValue record)
    {
        List<string> all = [];
        if (record.Member("addresses") is { IsArray: true } list)
        {
            foreach (var item in list.AsArray())
            {
                if (item is { IsText: true })
                {
                    all.Add(item.AsString());
                }
            }
        }

        if (record.Member("address") is { IsText: true } single)
        {
            all.Add(single.AsString());
        }

        return all.Count == 0 ? null : all.Order(StringComparer.Ordinal).First();
    }

    private static string? Text(JsonValue record, string name) => record.Member(name) is { IsText: true } v && v.AsString().Length > 0 ? v.AsString() : null;

    internal const string DeleteSessionSql = "DELETE FROM sessions WHERE session_id = ?1";

    internal const string DeleteUnplacedSql = "DELETE FROM sessions_unplaced WHERE session_id = ?1";

    /// <summary>Ends a pass's session whose registration file is gone, unless it was seen since the pass started.</summary>
    internal static string EndGoneSql(string table) =>
        $"UPDATE {table} SET ended_at = ?1 WHERE session_id = ?2 AND ended_at IS NULL AND last_seen_at < ?1" + (table == "sessions_unplaced" ? " AND doc IS NOT NULL" : string.Empty);

    /// <summary>Ends a session that unregistered.</summary>
    internal static string EndSql(string table) => $"UPDATE {table} SET ended_at = ?1 WHERE session_id = ?2 AND ended_at IS NULL";

    /// <summary>The document of the live registration of one session in <paramref name="table"/>.</summary>
    internal static string LiveDocumentSql(string table) => $"SELECT doc FROM {table} WHERE session_id = ?1 AND ended_at IS NULL AND doc IS NOT NULL";

    /// <summary>
    /// Stores one registration in the table that holds it: <c>sessions</c> when it names a team file, else <c>sessions_unplaced</c> with the
    /// reason <see cref="NoTeamFile"/>. A row never moves <c>last_seen_at</c> backwards, a session that is seen again is live again, and a
    /// session that changes table is removed from the other one in the same transaction, keeping its first start. Returns how many rows
    /// changed. The caller's transaction is the one in use.
    /// </summary>
    internal const string UpsertPlacedSql =
        "INSERT INTO sessions(session_id, team, member, pid, pid_start, cwd, pane_id, ah_version, started_at, last_seen_at, hier_dir, doc) " +
        "VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, COALESCE((SELECT started_at FROM sessions_unplaced WHERE session_id = ?1), ?9), ?9, ?10, ?11) " +
        "ON CONFLICT(session_id) DO UPDATE SET team = ?2, member = ?3, pid = ?4, pid_start = ?5, cwd = ?6, pane_id = ?7, ah_version = ?8, hier_dir = ?10, doc = ?11, ended_at = NULL, last_seen_at = max(last_seen_at, ?9) " +
        "WHERE hier_dir IS NOT ?10 OR team IS NOT ?2 OR member IS NOT ?3 OR pid IS NOT ?4 OR pid_start IS NOT ?5 OR cwd IS NOT ?6 OR pane_id IS NOT ?7 OR ah_version IS NOT ?8 OR doc IS NOT ?11 OR last_seen_at < ?9 OR ended_at IS NOT NULL";

    internal const string UpsertUnplacedSql =
        "INSERT INTO sessions_unplaced(session_id, reason, team, member, pid, pid_start, cwd, pane_id, ah_version, started_at, last_seen_at, doc) " +
        "VALUES (?1, ?10, ?2, ?3, ?4, ?5, ?6, ?7, ?8, COALESCE((SELECT started_at FROM sessions WHERE session_id = ?1), ?9), ?9, ?11) " +
        "ON CONFLICT(session_id) DO UPDATE SET reason = ?10, team = ?2, member = ?3, pid = ?4, pid_start = ?5, cwd = ?6, pane_id = ?7, ah_version = ?8, doc = ?11, ended_at = NULL, last_seen_at = max(last_seen_at, ?9) " +
        "WHERE reason IS NOT ?10 OR team IS NOT ?2 OR member IS NOT ?3 OR pid IS NOT ?4 OR pid_start IS NOT ?5 OR cwd IS NOT ?6 OR pane_id IS NOT ?7 OR ah_version IS NOT ?8 OR doc IS NOT ?11 OR last_seen_at < ?9 OR ended_at IS NOT NULL";

    internal static int Upsert(SqliteConnection connection, SessionRow r)
    {
        if (r.HierDir.Length > 0)
        {
            _ = StateDbRows.Run(
                connection,
                UpsertPlacedSql,
                [r.SessionId, r.Team, r.Member, r.Pid, r.PidStart, r.Cwd, r.PaneId, r.AhVersion, r.At, r.HierDir, r.Doc]);
            var stored = connection.Changes;
            _ = StateDbRows.Run(connection, DeleteUnplacedSql, [r.SessionId]);
            return stored + connection.Changes;
        }

        _ = StateDbRows.Run(
            connection,
            UpsertUnplacedSql,
            [r.SessionId, r.Team, r.Member, r.Pid, r.PidStart, r.Cwd, r.PaneId, r.AhVersion, r.At, NoTeamFile, r.Doc]);
        var kept = connection.Changes;
        _ = StateDbRows.Run(connection, DeleteSessionSql, [r.SessionId]);
        return kept + connection.Changes;
    }

    /// <summary>
    /// Writes the rows and ends the listed sessions in one transaction (the caller's, with <paramref name="inTransaction"/>); returns (changed, ended), or null when the database was busy. A row
    /// never moves <c>last_seen_at</c> backwards, and a session that is seen again is live again.
    /// </summary>
    internal static (int Changed, int Ended)? Apply(SqliteConnection connection, IReadOnlyList<SessionRow> rows, IReadOnlyList<string> toEnd, long passStart, bool inTransaction = false)
    {
        if (!inTransaction)
        {
            try
            {
                connection.Exec("BEGIN IMMEDIATE");
            }
            catch (SqliteException)
            {
                return null;
            }
        }

        try
        {
            var changed = 0;
            foreach (var r in rows)
            {
                changed += Upsert(connection, r);
            }

            var ended = 0;
            foreach (var id in toEnd)
            {
                _ = StateDbRows.Run(connection, EndGoneSql("sessions"), [passStart, id]);
                ended += connection.Changes;
                _ = StateDbRows.Run(connection, EndGoneSql("sessions_unplaced"), [passStart, id]);
                ended += connection.Changes;
            }

            if (!inTransaction)
            {
                connection.Exec("COMMIT");
            }

            return (changed, ended);
        }
        catch
        {
            if (!inTransaction)
            {
                try
                {
                    connection.Exec("ROLLBACK");
                }
                catch (SqliteException)
                {
                    // nothing is open to roll back
                }
            }

            throw;
        }
    }
}
