using System.Text;
using Ah.Core.Json;

namespace Ah.StateDb;

internal sealed record TeamRow(string HierDir, string Name, string File, string Doc, string? TeamId, string? Orchestrator, string? GateMode, long At, IReadOnlyList<object?[]> Members);

/// <summary>Reads team files into rows and writes them to the <c>teams</c> and <c>members</c> tables.</summary>
internal static class StateDbTeams
{
    private static readonly string[] States = ["active", "dismissed", "untracked"];

    internal sealed record Listing(IReadOnlyList<TeamRow> Rows, IReadOnlyList<string> Names, string? Problem, IReadOnlyList<(string HierDir, string Name)> NotTeams, bool HasFolder, IReadOnlyList<string>? Skipped = null);

    internal static string TeamsDirectory(string hierDir) => Path.Join(hierDir, "teams");

    /// <summary>The team file names of a directory in ordinal order, or null when it cannot be listed; a directory with no <c>teams</c> folder has none.</summary>
    internal static List<string>? Names(string hierDir)
    {
        try
        {
            return ListNames(hierDir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static List<string> ListNames(string hierDir)
    {
        var dir = TeamsDirectory(hierDir);
        return Directory.Exists(dir) ? [.. Directory.EnumerateFiles(dir, "*.json").Select(Path.GetFileName).Select(n => n!).Order(StringComparer.Ordinal)] : [];
    }

    internal static Listing Read(string hierDir)
    {
        List<string> names;
        try
        {
            names = ListNames(hierDir);
        }
        catch (UnauthorizedAccessException)
        {
            return new Listing([], [], null, [], true, [TeamsDirectory(hierDir)]);
        }
        catch (IOException)
        {
            return new Listing([], [], $"the team directory of {hierDir} could not be listed", [], false);
        }

        List<string> skipped = [];
        List<TeamRow> rows = [];
        List<(string HierDir, string Name)> notTeams = [];
        string? problem = null;
        HashSet<(string, string)> keys = [];
        foreach (var name in names)
        {
            var file = Path.Join(TeamsDirectory(hierDir), name);
            try
            {
                var bytes = StateDbBoundedFile.Read(file);
                var at = new DateTimeOffset(File.GetLastWriteTimeUtc(file)).ToUnixTimeMilliseconds();
                var made = RowOf(file, bytes, at, keys);
                if (made.NotTeam is { } notTeam)
                {
                    notTeams.Add(notTeam);
                }
                else if (made.Problem is not null)
                {
                    problem ??= made.Problem;
                }
                else
                {
                    rows.Add(made.Row!);
                }
            }
            catch (UnauthorizedAccessException)
            {
                skipped.Add(file);
            }
            catch (Exception e) when (e is IOException or JsonParseException)
            {
                problem ??= $"{file} could not be read: {e.Message}";
            }
        }

        return new Listing(rows, names, problem, notTeams, Directory.Exists(TeamsDirectory(hierDir)), skipped);
    }

    /// <summary>What one team file's bytes make: a row, or the key of a file that is no team, or the problem that kept it out.</summary>
    internal sealed record RowResult(TeamRow? Row, (string HierDir, string Name)? NotTeam, string? Problem);

    /// <summary>The row of one team document. <paramref name="keys"/>, when given, collects the keys already taken so a second file with one key is a problem.</summary>
    internal static RowResult RowOf(string file, byte[] bytes, long at, HashSet<(string, string)>? keys)
    {
        var parsed = JsonText.Parse(bytes);
        if (StateDbTeamKey.Of(file) is not { } key)
        {
            return new RowResult(null, null, $"{file} has no usable key");
        }

        if (!parsed.IsMap || parsed.AsObject().Get("members") is not { IsArray: true })
        {
            return new RowResult(null, key, null);
        }

        if (keys is not null && !keys.Add(key))
        {
            return new RowResult(null, null, $"{file} has the same key as another team file");
        }

        var members = Members(parsed, at, out var bad);
        if (bad is not null)
        {
            return new RowResult(null, null, $"{file}: {bad}");
        }

        return new RowResult(new TeamRow(key.HierDir, key.Name, file, Encoding.UTF8.GetString(bytes), Text(parsed, "team_id"), parsed.Member("orchestrator") is { IsMap: true } o ? Text(o, "name") : null, Text(parsed, "gate_mode"), at, members), null, null);
    }

    private static List<object?[]> Members(JsonValue team, long at, out string? bad)
    {
        bad = null;
        List<object?[]> result = [];
        HashSet<string> memberNames = new(StringComparer.Ordinal);
        foreach (var m in team.Member("members")!.Value.AsArray())
        {
            if (m.Member("name") is not { IsText: true } name || name.AsString().Length == 0)
            {
                bad = "a member has no name";
                return [];
            }

            if (!memberNames.Add(name.AsString()))
            {
                bad = $"two members are named {name.AsString()}";
                return [];
            }

            var state = Text(m, "state") ?? "active";
            if (!States.Contains(state, StringComparer.Ordinal))
            {
                bad = $"member {name.AsString()} has the unknown state '{state}'";
                return [];
            }

            result.Add(
            [
                name.AsString(), Text(m, "role"), Text(m, "kind"), Text(m, "route"), Text(m, "model"), Text(m, "effort"), Text(m, "auto_mode"),
                m.Member("args") is { IsNull: false } args ? JsonText.Stringify(args, 0) : null,
                Text(m, "stream"), Text(m, "expected_root"), Text(m, "pane_id"), Text(m, "session_id"), Text(m, "transcript_path"), Text(m, "cwd"),
                m.Member("pid") is { IsNumber: true } pid ? (long)pid.AsNumber() : null, Text(m, "pid_start"), state,
                m.Member("last_seen_at") is { IsNumber: true } seen ? (long)seen.AsNumber() : null, at,
            ]);
        }

        return result;
    }

    private static string? Text(JsonValue record, string name) => record.Member(name) is { IsText: true } v && v.AsString().Length > 0 ? v.AsString() : null;

    /// <summary>Writes the rows of the scanned directories in one transaction (the caller's, with <paramref name="inTransaction"/>); a team whose file is gone is removed only when <paramref name="mayRemove"/>. Null when the database was busy.</summary>
    internal static (int Changed, int Removed)? Apply(SqliteConnection connection, IReadOnlyList<string> scanned, IReadOnlyList<TeamRow> rows, bool mayRemove, bool inTransaction = false)
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
            var removed = 0;
            foreach (var r in rows)
            {
                _ = StateDbRows.Run(
                    connection,
                    "INSERT INTO teams(hier_dir, team, team_id, orchestrator_name, gate_mode, doc, created_at, updated_at) VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?7) " +
                    "ON CONFLICT(hier_dir, team) DO UPDATE SET team_id = ?3, orchestrator_name = ?4, gate_mode = ?5, doc = ?6, updated_at = ?7 WHERE doc IS NOT ?6",
                    [r.HierDir, r.Name, r.TeamId, r.Orchestrator, r.GateMode, r.Doc, r.At]);
                if (connection.Changes == 0)
                {
                    continue;
                }

                changed++;
                _ = StateDbRows.Run(connection, "DELETE FROM members WHERE hier_dir = ?1 AND team = ?2", [r.HierDir, r.Name]);
                foreach (var m in r.Members)
                {
                    _ = StateDbRows.Run(
                        connection,
                        "INSERT INTO members(hier_dir, team, name, role, kind, route, model, effort, auto_mode, args, stream, expected_root, pane_id, session_id, transcript_path, cwd, pid, pid_start, state, last_seen_at, updated_at) " +
                        "VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12, ?13, ?14, ?15, ?16, ?17, ?18, ?19, ?20, ?21)",
                        [r.HierDir, r.Name, .. m]);
                }
            }

            if (mayRemove)
            {
                var kept = rows.Select(r => (r.HierDir, r.Name)).ToHashSet();
                foreach (var dir in scanned)
                {
                    foreach (var row in StateDbRows.Run(connection, "SELECT team FROM teams WHERE hier_dir = ?1", [dir]))
                    {
                        var name = (string)row[0]!;
                        if (kept.Contains((dir, name)))
                        {
                            continue;
                        }

                        _ = StateDbRows.Run(connection, "DELETE FROM members WHERE hier_dir = ?1 AND team = ?2", [dir, name]);
                        _ = StateDbRows.Run(connection, "DELETE FROM teams WHERE hier_dir = ?1 AND team = ?2", [dir, name]);
                        removed++;
                    }
                }
            }

            if (!inTransaction)
            {
                connection.Exec("COMMIT");
            }

            return (changed, removed);
        }
        catch (SqliteException)
        {
            if (!inTransaction)
            {
                try
                {
                    connection.Exec("ROLLBACK");
                }
                catch (SqliteException)
                {
                    // the transaction is already gone
                }
            }

            throw;
        }
    }
}
