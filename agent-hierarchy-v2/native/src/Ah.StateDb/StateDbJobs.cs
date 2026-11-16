using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.StateDb;

/// <summary>The <c>jobs</c> table (schema 27) over the state database of <paramref name="ahHome"/>.</summary>
internal sealed class StateDbJobs(string ahHome) : IJobSource
{
    private const int ImportBusyMs = 250;

    /// <summary>The meta key recording that this home took the job records under <paramref name="root"/> into the table.</summary>
    internal static string TakenKey(string root) => "jobs.taken:" + StateDbTeamKey.RealPath(root);

    /// <summary>
    /// The rows only (files-off-disk §4: the import pass takes the records). A database that cannot be read (busy, newer, missing table)
    /// reads as no rows and is never written; with <paramref name="mustAnswer"/> it throws instead (b416: <c>job start</c> must not make a
    /// second record of an open job).
    /// </summary>
    public IReadOnlyList<JobEntry> Rows(string hierDir, bool mustAnswer = false)
    {
        if (StateDbAuthority.NeverCreated(ahHome))
        {
            return [];
        }

        try
        {
            return StateDbStoreReads.Reading(ahHome, connection =>
                StateDbRows.Run(connection, "SELECT team, id, body FROM jobs WHERE team_home = ?1 ORDER BY team, id", [MsgRows.PoolKey(hierDir)])
                    .Select(static r => new JobEntry((string)r[0]!, (string)r[1]!, (string)r[2]!)).ToList());
        }
        catch (Exception e) when (e is StoreUnavailableException or InvalidOperationException)
        {
            if (mustAnswer)
            {
                throw new InvalidOperationException($"the state database is busy; try again ({e.Message})", e);
            }

            return [];
        }
    }

    /// <summary>The import pass's take of <paramref name="hierDir"/>'s <c>jobs/</c> records (files-off-disk §3.2); false when nothing was taken.</summary>
    internal bool Take(string hierDir) => Directory.Exists(Path.Join(hierDir, "jobs")) && Import(MsgRows.PoolKey(hierDir), Path.Join(hierDir, "jobs"));

    /// <summary>
    /// The upgrade's one take of a dir's <c>jobs/&lt;team&gt;/&lt;id&gt;.json</c> records (per-dir-files-to-db S8): only those not yet ended
    /// (no <c>ended_at</c>), a file that does not parse as an object skipped, a row the table already holds kept, and the take recorded, in
    /// one write transaction. The files stay where they are. False when nothing was taken (taken already, busy, unreadable).
    /// </summary>
    private bool Import(string hierDir, string root)
    {
        var open = new List<JobEntry>();
        try
        {
            foreach (var teamDir in Directory.GetDirectories(root))
            {
                foreach (var file in Directory.GetFiles(teamDir, "*.json"))
                {
                    if (OpenRecord(File.ReadAllText(file)) is { } body)
                    {
                        open.Add(new JobEntry(Path.GetFileName(teamDir), Path.GetFileNameWithoutExtension(file), body));
                    }
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        try
        {
            using var connection = SqliteConnection.Open(StateDbBackups.DatabasePath(ahHome), SqliteOpenMode.ReadWrite, ImportBusyMs);
            connection.Exec("BEGIN IMMEDIATE");
            try
            {
                if (StateDbRows.Run(connection, "SELECT 1 FROM meta WHERE key = ?1", [TakenKey(root)]).Count > 0)
                {
                    connection.Exec("ROLLBACK");
                    return false;
                }

                foreach (var job in open)
                {
                    _ = StateDbRows.Run(connection, "INSERT OR IGNORE INTO jobs(team_home, team, id, body) VALUES (?1, ?2, ?3, ?4)", [hierDir, job.Team, job.Id, job.Body]);
                }

                var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                _ = StateDbRows.Run(connection, "INSERT INTO meta(key, value) VALUES (?1, ?2)", [TakenKey(root), nowMs.ToString(System.Globalization.CultureInfo.InvariantCulture)]);

                // b459 step 11: the import writes outside the op runner, so it moves the jobs counter itself, or a reader keyed on it keeps the rows from before
                _ = StateDbRows.Run(connection, "INSERT INTO domain_seq(domain, seq, changed_ms) VALUES ('jobs', 1, ?1) ON CONFLICT(domain) DO UPDATE SET seq = seq + 1, changed_ms = ?1", [nowMs]);
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
        catch (SqliteException)
        {
            // busy or not writable: a later read takes the files
            return false;
        }
    }

    /// <summary>The record's JSON text when it is an object that has not ended; else null.</summary>
    private static string? OpenRecord(string text)
    {
        try
        {
            return JsonText.Parse(text) is { IsMap: true } record && record.Member("ended_at") is not { IsText: true } ? JsonText.Stringify(record, 0) : null;
        }
        catch (JsonParseException)
        {
            return null;
        }
    }
}
