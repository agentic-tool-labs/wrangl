using Ah.Abstractions;
using Ah.Core.Text;

namespace Ah.Commands;

/// <summary>
/// The highest member-name suffix a team has ever handed out for a role. A dismissed member leaves no row in the team file, so the
/// team's mailbox box directories (made when a member registers, never removed at dismiss) and the team-history entries stand
/// in for it. A name is <c>&lt;base&gt;</c> (suffix 1) or <c>&lt;base&gt;-&lt;n&gt;</c>.
/// </summary>
internal static class UsedSuffix
{
    /// <summary>The suffix <paramref name="name"/> carries as a <paramref name="role"/> member under <paramref name="prefix"/>, or 0 when it is not one of its names.</summary>
    internal static int Of(string name, string prefix, string role) => MemberNames.Ordinal(name, prefix, role);

    /// <summary>
    /// The maximum suffix over the team's current members and the names they were renamed from (the old session may still answer to
    /// one), its box directories and its history entries. A history entry keeps
    /// roles without names, so its <paramref name="role"/> members count as the suffixes 1 to their number.
    /// </summary>
    internal static int Highest(RosterContext c, string dir, string role, string prefix)
    {
        var highest = 0;
        var services = c.Services;
        var team = services.Roster.ReadTeamRecord(dir, c.TeamFile);
        if (team.Member("members") is { IsArray: true } members)
        {
            foreach (var m in members.AsArray())
            {
                highest = Math.Max(highest, Of(MemberPlanning.Text(m.Member("name")) ?? string.Empty, prefix, role));
                highest = Math.Max(highest, Of(MemberPlanning.Text(m.Member("renamed_from")) ?? string.Empty, prefix, role));
            }
        }

        if (services.MemberBoxRoot(services.Roster.TeamPath(dir, c.TeamFile)) is { } root && services.Files.Exists(root))
        {
            foreach (var entry in services.Files.List(root))
            {
                if (services.Files.Stat(Path.Combine(root, entry)) is { Kind: FileKind.Directory })
                {
                    highest = Math.Max(highest, Of(entry, prefix, role));
                }
            }
        }

        if (services.Roster.ReadHistory(dir).Get("teams") is { IsArray: true } teams)
        {
            foreach (var entry in teams.AsArray())
            {
                if (MemberPlanning.Text(entry.Member("alias")) != c.TeamFile || entry.Member("members") is not { IsArray: true } snapshot)
                {
                    continue;
                }

                highest = Math.Max(highest, snapshot.AsArray().Count(m => MemberPlanning.Text(m.Member("role")) == role));
            }
        }

        return highest;
    }
}
