using System.Globalization;
using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.StateDb;

/// <summary>
/// The <c>holds</c> table (schema 35) over the state database of <paramref name="ahHome"/>. Fail closed: a read that cannot answer
/// is <see cref="HoldRead.Unreadable"/>, never no row. Rows only once the database exists: the import pass takes a team home's old hold files.
/// </summary>
internal sealed class StateDbHolds(string ahHome) : IHoldSource
{
    private const string RowSql = "SELECT body FROM holds WHERE team_home = ?1 AND team = ?2";

    private readonly IDbOpRunner _runner = StateDbModule.CreateOpRunner(ahHome);

    public HoldRead Read(string teamHome, string team)
    {
        // no database yet: the hold is still only its file
        if (StateDbAuthority.NeverCreated(ahHome))
        {
            return FromFile(teamHome, team);
        }

        var home = StateDbTeamKey.RealPath(teamHome);
        try
        {
            // rows only (files-off-disk §4): the import pass takes the team home's files
            if (StateDbStoreReads.Reading(ahHome, c => StateDbRows.Run(c, RowSql, [home, team])) is [var row])
            {
                return new HoldRead((string)row[0]!, null);
            }

            // a hold file written after the take (an older binary still writes files) is never read as released
            return WrittenAfterTake(home, LegacyPath(teamHome, team))
                ? new HoldRead(null, "a hold file written after the move to the database: " + LegacyPath(teamHome, team))
                : new HoldRead(null, null);
        }
        catch (Exception e) when (e is StoreUnavailableException or SqliteException or InvalidOperationException)
        {
            return new HoldRead(null, $"the state database cannot be read: {e.Message}");
        }
    }

    public void Change(string teamHome, string team, Func<string?, HoldWrite?> decide)
    {
        var home = StateDbTeamKey.RealPath(teamHome);
        Exception? refused = null;
        var outcome = _runner.Run(new DbOpRequest("holds", null, LegacyPath(teamHome, team), tx =>
        {
            try
            {
                // ponytail: the write's take stays (files-off-disk §4 gap, atp-1518): decide builds on the current hold, so an untaken file would come back after a release
                TakeIn((sql, args) => tx.Query(sql, args), (sql, args) => tx.Execute(sql, args), teamHome);
                var current = tx.Query(RowSql, home, team) is [var row] ? (string)row[0]! : null;
                var write = decide(current);
                if (write is { ArchiveStamp: { } stamp, ArchiveBody: { } archived })
                {
                    tx.Execute(
                        "INSERT OR REPLACE INTO hold_archive(team_home, team, stamp, body, created_at) VALUES (?1, ?2, ?3, ?4, ?5)",
                        home, team, stamp, archived, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                }

                switch (write)
                {
                    case { Body: { } body }:
                        tx.Execute(
                            "INSERT INTO holds(team_home, team, body, updated_at) VALUES (?1, ?2, ?3, ?4) ON CONFLICT(team_home, team) DO UPDATE SET body = ?3, updated_at = ?4",
                            home, team, body, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                        break;
                    case { Body: null }:
                        tx.Execute("DELETE FROM holds WHERE team_home = ?1 AND team = ?2", home, team);
                        break;
                }

                return new OpResult(true, "null"u8.ToArray());
            }
            catch (Exception e)
            {
                refused = e;
                return new OpResult(false, default, e.Message);
            }
        }, LeavesSeq: true, DbOnly: true));
        if (refused is not null)
        {
            throw refused;
        }

        if (outcome.Status != DbOpStatus.Done)
        {
            throw new InvalidOperationException($"the hold of team {team} was not written: {outcome.Error ?? outcome.Status.ToString()}");
        }
    }

    public MemberPause? PauseOf(string teamHome, string team, string member)
    {
        if (StateDbAuthority.NeverCreated(ahHome))
        {
            return null;
        }

        try
        {
            var found = StateDbStoreReads.Reading(ahHome, c => StateDbRows.Run(c, "SELECT value FROM meta WHERE key = ?1", [PauseKey(teamHome, team, member)]));
            return found is [[string text]] && text.Split(' ') is [var word, var at] && long.TryParse(at, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms)
                ? new MemberPause(word == "paused", ms)
                : null;
        }
        catch (Exception e) when (e is StoreUnavailableException or SqliteException or InvalidOperationException)
        {
            return null;
        }
    }

    public void SetPause(string teamHome, string team, string member, bool paused, long atMs)
    {
        var key = PauseKey(teamHome, team, member);
        var value = (paused ? "paused " : "resumed ") + atMs.ToString(CultureInfo.InvariantCulture);
        var outcome = _runner.Run(new DbOpRequest("holds", null, LegacyPath(teamHome, team), tx =>
        {
            tx.Execute("INSERT INTO meta(key, value) VALUES (?1, ?2) ON CONFLICT(key) DO UPDATE SET value = excluded.value", key, value);
            return new OpResult(true, "null"u8.ToArray());
        }, LeavesSeq: true, DbOnly: true));
        if (outcome.Status != DbOpStatus.Done)
        {
            throw new InvalidOperationException($"the pause of {member} was not written: {outcome.Error ?? outcome.Status.ToString()}");
        }
    }

    private static string PauseKey(string teamHome, string team, string member) => "member.pause:" + StateDbTeamKey.RealPath(teamHome) + "|" + team + "|" + member;

    /// <summary>The meta key recording that this home took <paramref name="teamHome"/>'s hold files into the table.</summary>
    internal static string TakenKey(string teamHome) => "holds.taken:" + StateDbTeamKey.RealPath(teamHome);

    /// <summary>The import pass's take of <paramref name="teamHome"/>'s hold files (files-off-disk §3.2); false when the write did not land.</summary>
    internal bool Take(string teamHome) =>
        Directory.Exists(Path.Join(teamHome, "holds")) && StateDbTake.Takeable(ahHome, TakenKey(teamHome))
        && StateDbTake.InTransaction(ahHome, (query, execute) => TakeIn(query, execute, teamHome));

    private bool WrittenAfterTake(string home, string file)
    {
        if (!File.Exists(file))
        {
            return false;
        }

        var takenAt = StateDbStoreReads.Reading(ahHome, c => StateDbRows.Run(c, "SELECT value FROM meta WHERE key = ?1", [TakenKey(home)]));
        return takenAt is [var row] && long.TryParse(row[0] as string, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms)
            && new DateTimeOffset(File.GetLastWriteTimeUtc(file)).ToUnixTimeMilliseconds() > ms;
    }

    private static string LegacyPath(string teamHome, string team) => Path.Join(teamHome, "holds", team, "hold.json");

    private static HoldRead FromFile(string teamHome, string team)
    {
        var path = LegacyPath(teamHome, team);
        try
        {
            return File.Exists(path) ? new HoldRead(File.ReadAllText(path), null) : new HoldRead(null, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return File.Exists(path) ? new HoldRead(null, e.Message) : new HoldRead(null, null);
        }
    }

    /// <summary>
    /// The one take of a team home's hold files (per-dir-files-to-db S11a-holds): each <c>holds/&lt;team&gt;/hold.json</c> becomes the
    /// team's row, its body verbatim, a corrupt one included (it still reads as unreadable, so held). Recorded once per team home; the
    /// files stay.
    /// </summary>
    private static void TakeIn(Func<string, object?[], IReadOnlyList<object?[]>> query, Action<string, object?[]> execute, string teamHome)
    {
        var key = TakenKey(teamHome);
        if (query("SELECT 1 FROM meta WHERE key = ?1", [key]).Count > 0)
        {
            return;
        }

        var home = StateDbTeamKey.RealPath(teamHome);
        var root = Path.Join(teamHome, "holds");
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (Directory.Exists(root))
        {
            foreach (var folder in Directory.EnumerateDirectories(root))
            {
                var file = Path.Join(folder, "hold.json");
                if (File.Exists(file))
                {
                    execute("INSERT OR IGNORE INTO holds(team_home, team, body, updated_at) VALUES (?1, ?2, ?3, ?4)", [home, Path.GetFileName(folder), File.ReadAllText(file), now]);
                }
            }
        }

        execute("INSERT INTO meta(key, value) VALUES (?1, ?2)", [key, now.ToString(CultureInfo.InvariantCulture)]);
    }
}
