using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.StateDb;

/// <summary>
/// The <c>links</c> table (schema 30, per-dir-files-to-db S11b) over the state database of <paramref name="ahHome"/>: the whole <c>{v, links, denied}</c>
/// document of each team home and team. The import pass, or a team's first write, takes the team's old <c>links/&lt;team&gt;.json</c> into the
/// table once, verbatim (a taken meta key); a corrupt file is not imported and stays on disk. After the take the file is never read again,
/// and it is never written.
/// </summary>
internal sealed class StateDbLinks(string ahHome) : ILinkStore
{
    private const int ImportBusyMs = 250;

    private sealed class On(SqliteConnection connection) : IDbOpTransaction
    {
        public void Execute(string sql, params object?[] args) => _ = StateDbRows.Run(connection, sql, args);

        public IReadOnlyList<object?[]> Query(string sql, params object?[] args) => StateDbRows.Run(connection, sql, args);
    }

    // rows only (files-off-disk §4): the import pass takes the file
    public string? Body(string hierDir, string team) =>
        StateDbStoreReads.ReadOrEmpty(ahHome, connection => new List<string?> { Row(new On(connection), hierDir, team) }).FirstOrDefault();

    /// <summary>The import pass's take of <paramref name="team"/>'s <c>links/&lt;team&gt;.json</c> (files-off-disk §3.2); false when nothing was done.</summary>
    internal bool Take(string hierDir, string team) => File.Exists(FileOf(hierDir, team)) && TakeOnce(FileOf(hierDir, team), hierDir, team);

    // ponytail: the write's take stays (files-off-disk §4 gap): a write stores the whole document, so one landing before the pass would shadow the file's links
    public string? Body(IDbOpTransaction tx, string hierDir, string team)
    {
        var file = FileOf(hierDir, team);
        if (File.Exists(file) && !Taken(tx, file))
        {
            Take(tx, file, hierDir, team);
        }

        return Row(tx, hierDir, team);
    }

    public void Put(IDbOpTransaction tx, string hierDir, string team, string body) => tx.Execute(
        "INSERT INTO links(team_home, team, body, updated_at) VALUES (?1, ?2, ?3, ?4) ON CONFLICT(team_home, team) DO UPDATE SET body = excluded.body, updated_at = excluded.updated_at",
        MsgRows.PoolKey(hierDir), team, body, Now());

    /// <summary>The meta key recording that this home took <paramref name="file"/> into the table.</summary>
    internal static string TakenKey(string file) => "links.taken:" + StateDbTeamKey.RealPath(file);

    private static string FileOf(string hierDir, string team) => Path.Join(hierDir, "links", team + ".json");

    private static string? Row(IDbOpTransaction tx, string hierDir, string team) =>
        tx.Query("SELECT body FROM links WHERE team_home = ?1 AND team = ?2", MsgRows.PoolKey(hierDir), team) is [[string body]] ? body : null;

    private static bool Taken(IDbOpTransaction tx, string file) => tx.Query("SELECT 1 FROM meta WHERE key = ?1", TakenKey(file)).Count > 0;

    /// <summary>
    /// The one take of a team's file: its text verbatim when it is a JSON object, stored unless the table already holds the team (a row
    /// stored since is the newer one), and the take recorded. A corrupt file is recorded as taken with no row, so the team reads empty and
    /// the file stays on disk. An unreadable file is not taken: a later read or write tries again.
    /// </summary>
    private static void Take(IDbOpTransaction tx, string file, string hierDir, string team)
    {
        string text;
        try
        {
            text = File.ReadAllText(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return;
        }

        if (IsJsonMap(text))
        {
            tx.Execute("INSERT OR IGNORE INTO links(team_home, team, body, updated_at) VALUES (?1, ?2, ?3, '')", MsgRows.PoolKey(hierDir), team, text);
        }

        tx.Execute("INSERT OR IGNORE INTO meta(key, value) VALUES (?1, ?2)", TakenKey(file), Now());
    }

    /// <summary>The read path's take, in its own write transaction; false when nothing was done (taken already, busy, not writable).</summary>
    private bool TakeOnce(string file, string hierDir, string team)
    {
        try
        {
            using var connection = SqliteConnection.Open(StateDbBackups.DatabasePath(ahHome), SqliteOpenMode.ReadWrite, ImportBusyMs);
            connection.Exec("BEGIN IMMEDIATE");
            try
            {
                var tx = new On(connection);
                if (Taken(tx, file))
                {
                    connection.Exec("ROLLBACK");
                    return false;
                }

                Take(tx, file, hierDir, team);
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
            // busy or not writable: a later read takes the file
            return false;
        }
    }

    private static string Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static bool IsJsonMap(string text)
    {
        try
        {
            return JsonText.Parse(text).IsMap;
        }
        catch (JsonParseException)
        {
            return false;
        }
    }
}
