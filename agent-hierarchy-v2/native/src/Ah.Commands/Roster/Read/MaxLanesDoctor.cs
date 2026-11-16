namespace Ah.Commands;

/// <summary>
/// The <c>max-lanes</c> doctor rows: a <c>max_lanes</c> recorded on the team, or set in the roster block it was built from, that is not a whole
/// number from 1 to 16. That value is skipped and the next level applies. No row when both are fine.
/// </summary>
internal static class MaxLanesDoctor
{
    internal static (string Status, string Detail)? Row(RosterContext c)
    {
        var team = c.Services.Roster.ReadTeamRecord(c.Dir, c.TeamFile);
        if (team.IsNull)
        {
            return null;
        }

        var roster = EffectiveLaneCap.RosterBlock(c);
        var (value, source, rejectedTeam, rejectedRoster) = EffectiveLaneCap.Of(c, team);
        var range = $"{Ah.Roster.RosterModule.LaneCapMin}..{Ah.Roster.RosterModule.LaneCapMax}";
        var details = new List<string>();
        if (rejectedTeam is not null)
        {
            var name = Path.GetFileNameWithoutExtension(c.Services.Roster.TeamPath(c.Dir, c.TeamFile));
            details.Add($"team {name}: max_lanes {rejectedTeam} is not {range}; using {Describe(value, source)}");
        }

        if (rejectedRoster is not null)
        {
            var block = roster.Member("teamKey") is { IsText: true } key ? key.AsString() : "(default)";
            var file = roster.Member("path") is { IsText: true } path ? path.AsString() : "?";
            details.Add($"roster {block} ({file}): max_lanes {rejectedRoster} is not {range}; using {Describe(value, source)}");
        }

        return details.Count == 0 ? null : ("warn", string.Join("; ", details));
    }

    private static string Describe(int value, string source) => source == "default" ? value.ToString(System.Globalization.CultureInfo.InvariantCulture) : EffectiveLaneCap.Describe(value, source);
}
