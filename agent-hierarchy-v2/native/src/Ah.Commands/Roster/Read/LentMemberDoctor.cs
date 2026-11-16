using Ah.Roster;

namespace Ah.Commands;

/// <summary>
/// The doctor rows for members lent between teams: <c>lent-member</c> lists the ones a team lends out or holds, and
/// <c>lead-dangling</c> warns about a row whose lead names a lane that is missing, closed or no longer run by the member it names.
/// </summary>
internal static class LentMemberDoctor
{
    internal static IReadOnlyList<(string Name, string Status, string Detail)> Rows(RosterContext c)
    {
        var team = Path.GetFileNameWithoutExtension(c.Services.Roster.TeamPath(c.Dir, c.TeamFile));
        var all = RosterModule.LentMembers(c.Services.Roster, c.Dir, role => c.Services.Config.RoleClass(role, c.Registry) == "orchestrate");
        var rows = new List<(string, string, string)>();
        var lent = all.Where(l => !l.Dangling && (l.Home == team || l.Lane == team)).ToList();
        if (lent.Count > 0)
        {
            rows.Add(("lent-member", "ok", string.Join("; ", lent.Select(l => l.Home == team ? $"{l.Name} is lent to {l.Lane}" : $"{l.Name} is lent from {l.Home}"))));
        }

        var dangling = all.Where(l => l.Dangling && l.Home == team).ToList();
        if (dangling.Count > 0)
        {
            rows.Add((
                "lead-dangling",
                "warn",
                string.Join("; ", dangling.Select(l => $"{l.Name}: its lead names lane {l.Lane}, which is missing, closed or no longer run by that lead; mail falls back to orchestrator@{l.Home}; transfer the member back"))));
        }

        return rows;
    }
}
