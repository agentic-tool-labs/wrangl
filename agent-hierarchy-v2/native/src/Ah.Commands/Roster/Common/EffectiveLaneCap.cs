using Ah.Core.Json;
using Ah.Roster;

namespace Ah.Commands;

/// <summary>The cap on live sub-orchestrators that holds for the team the command acts on, read live from its record and the roster block it was built from.</summary>
internal static class EffectiveLaneCap
{
    internal static (int Value, string Source, string? RejectedTeam, string? RejectedRoster) Of(RosterContext c, JsonValue team) =>
        RosterModule.EffectiveMaxLanes(team, RosterBlock(c));

    /// <summary>The roster block the team was built from, as the roster resolution returns it; null when none is found.</summary>
    internal static JsonValue RosterBlock(RosterContext c) =>
        c.Services.Config.ResolveRoster(c.Cwd, c.Services.Roster.TeamRosterKey(c.Dir, c.TeamFile), c.RepoBasename, c.Registry);

    /// <summary>The cap as a refusal or a listing names it: <c>max_lanes 4 (team)</c>, <c>max_lanes 8 from roster ah2</c> or <c>max_lanes 6, the default</c>.</summary>
    internal static string Describe(int value, string source) => source switch
    {
        "team" => $"max_lanes {value} (team)",
        "default" => $"max_lanes {value}, the default",
        _ => $"max_lanes {value} from {source}",
    };
}
