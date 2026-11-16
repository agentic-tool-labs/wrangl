using Ah.Core.Json;
using Ah.Hierarchy;
using Ah.Watch.Model;

namespace Ah.Cli.Composition;

/// <summary>
/// The answers about lanes the team view serves, from the code the routing runs: the same confirmation predicate, the same child names, and the
/// same resolver and liveness check for where a member's messages go, over the team files as they are now.
/// </summary>
internal sealed class LaneFactsHost(ServiceGraph g) : ILaneFacts
{
    public string? ConfirmedStream(string managerName, JsonValue managerRecord, string childName, JsonValue childRecord) =>
        HierarchyModule.ConfirmedLaneStream(managerName, managerRecord, childName, childRecord);

    public string ChildName(string manager, string stream) => HierarchyModule.LaneChildName(manager, stream);

    public LaneRoute? RouteOf(string dir, string team, string member)
    {
        JsonValue Record(string name) => g.Roster.ReadTeamRecord(dir, name == "default" ? null : name);
        var row = Record(team).Member("members") is { IsArray: true } rows
            ? rows.AsArray().FirstOrDefault(r => r.IsMap && r.Member("name") is { IsText: true } n && n.AsString() == member)
            : default;
        return row.IsMap && HierarchyModule.LaneRouteOf(team, row, Record, g.Hierarchy.Core.IdentityLive) is var (to, name, via) ? new LaneRoute(to, name, via) : null;
    }
}
