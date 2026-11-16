using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.StateDb;

namespace Ah.TestKit;

/// <summary>
/// Grants documents for a test (per-dir-files-to-db S11e: once a database exists the grants tables are their home). There is no seed op
/// (§6c G4): a document is planted as its file and taken into the table by the import pass, exactly as an upgrade takes it.
/// </summary>
public static class TestGrants
{
    private static IGrantSource Source(string ahHome, string home) => StateDbModule.CreateGrants(ahHome, AhPaths.UserEntry(home, AhPaths.WorktreeAuditFile));

    private static bool HasDatabase(string ahHome) => StateDbModule.DatabaseCreated(ahHome);

    /// <summary>
    /// Puts <paramref name="json"/> down as <paramref name="team"/>'s document in <paramref name="hier"/>: the file, and with a database its
    /// row (a row already there is removed first, so the plant replaces it, as rewriting the file did).
    /// </summary>
    public static void Plant(string ahHome, string home, string hier, string team, string json)
    {
        var path = Path.Combine(hier, "grants", team + ".json");
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
        if (HasDatabase(ahHome))
        {
            Execute(ahHome, "DELETE FROM worktree_grants WHERE hier_dir = ?1 AND team = ?2", Key(hier), team);
            Untake(ahHome, hier);

            // the import pass is the only take (files-off-disk §4)
            _ = StateDbModule.ImportHierDirFiles(ahHome, hier, force: true);
            if (!Source(ahHome, home).Read(hier)!.TryDocuments(out _))
            {
                throw new InvalidOperationException($"the plant of {team}'s grants was not taken");
            }
        }
    }

    /// <summary><paramref name="team"/>'s document text in <paramref name="hier"/>, from the store when there is a database, else the file; null when there is none.</summary>
    public static string? Text(string ahHome, string home, string hier, string team)
    {
        var path = Path.Combine(hier, "grants", team + ".json");
        if (!HasDatabase(ahHome))
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }

        if (!Source(ahHome, home).Read(hier)!.TryDocuments(out var documents))
        {
            throw new InvalidOperationException("the grants store could not be read");
        }

        return documents.FirstOrDefault(d => d.Team == team)?.Body;
    }

    /// <summary><paramref name="team"/>'s document parsed; throws when there is none.</summary>
    public static JsonValue Document(string ahHome, string home, string hier, string team) =>
        JsonText.Parse(Text(ahHome, home, hier, team) ?? throw new InvalidOperationException($"no grants document for {team}"));

    /// <summary>Removes <paramref name="team"/>'s document: its file, and with a database its row (no op removes one; the test fixture does).</summary>
    public static void Delete(string ahHome, string hier, string team)
    {
        File.Delete(Path.Combine(hier, "grants", team + ".json"));
        if (HasDatabase(ahHome))
        {
            Execute(ahHome, "DELETE FROM worktree_grants WHERE hier_dir = ?1 AND team = ?2", Key(hier), team);
        }
    }

    /// <summary>Every worktree audit row, from the store (table, then the old file) when there is a database, else the file.</summary>
    public static IReadOnlyList<string> AuditRows(string ahHome, string home)
    {
        var file = AhPaths.UserEntry(home, AhPaths.WorktreeAuditFile);
        return HasDatabase(ahHome) ? Source(ahHome, home).AuditRows() : File.Exists(file) ? File.ReadAllLines(file) : [];
    }

    /// <summary>Forgets that <paramref name="hier"/> was taken, so the next read takes its files again (a plant made after a take).</summary>
    private static void Untake(string ahHome, string hier) =>
        Execute(ahHome, "DELETE FROM meta WHERE key = ?1", "grants.taken:" + Key(hier));

    private static string Key(string hier) => PathGuard.RealPath(hier, strict: false)!;

    private static void Execute(string ahHome, string sql, params object?[] args)
    {
        if (!StateDbModule.OpenDirect(ahHome).Write(tx =>
        {
            tx.Execute(sql, args);
            return 0;
        }).Ok)
        {
            throw new InvalidOperationException("the grants fixture write failed: " + sql);
        }
    }
}
