using System.Text;
using Ah.Core.Json;
using Ah.StateDb;

namespace Ah.Cli.Composition;

/// <summary>Facts read from the state database's team records, which only the daemon's own writers change, never from a team file in a repository.</summary>
internal static class TeamRecords
{
    /// <summary>
    /// The team the record of <paramref name="team"/> in <paramref name="hierDir"/> names as its parent, as text (a bare name or a <c>team</c>
    /// member). Null when the teams domain is not held by the database, the record is missing, or it names no parent: a file is never read.
    /// </summary>
    internal static string? ParentOf(string ahHome, string hierDir, string team)
    {
        try
        {
            var opened = StateDbModule.OpenReader(ahHome);
            if (opened.Reader is not { } reader)
            {
                return null;
            }

            using (reader)
            {
                if (reader.Query("SELECT value FROM meta WHERE key = 'authority.teams'").Count == 0)
                {
                    return null;
                }

                var rows = reader.Query("SELECT doc FROM teams WHERE hier_dir = ?1 AND team = ?2", hierDir, team);
                if (rows.Count == 0 || rows[0][0] is not string doc || JsonText.Parse(Encoding.UTF8.GetBytes(doc)).Member("parent") is not { } parent)
                {
                    return null;
                }

                return parent.IsText ? parent.AsString() : parent.Member("team") is { IsText: true } t ? t.AsString() : null;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonParseException or InvalidOperationException)
        {
            return null;
        }
    }
}
