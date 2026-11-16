using Ah.Core.Json;
using Ah.Hierarchy;

namespace Ah.Cli.Composition;

/// <summary>A confirmed child lane of a team, with what its team file says of its sub-orchestrator and its status record.</summary>
/// <param name="Child">The child team's name.</param>
/// <param name="Stream">The lane's workstream name in the parent team.</param>
/// <param name="Record">The child team's record.</param>
/// <param name="SubOrchestrator">The sub-orchestrator's name.</param>
/// <param name="StreamState">The lane's <c>stream-report</c> state, or null.</param>
/// <param name="UpdatedMs">When the status record was last written, or null.</param>
internal sealed record ChildLane(string Child, string Stream, JsonValue Record, string SubOrchestrator, string? StreamState, double? UpdatedMs);

/// <summary>The lanes of a team as the files show them: the one reading both the lane escalation and the top's failsafe use.</summary>
internal static class LaneStreams
{
    internal static IReadOnlyList<ChildLane> Of(ServiceGraph graph, string home, string parentTeam)
    {
        if (graph.Roster.ReadTeamRecord(home, parentTeam) is not { IsMap: true } manager || manager.Member("streams") is not { IsMap: true } streams)
        {
            return [];
        }

        var mailed = LastMailMs(graph, home, parentTeam, manager);
        var lanes = new List<ChildLane>();
        foreach (var stream in streams.AsObject().Keys)
        {
            var child = HierarchyModule.LaneChildName(parentTeam, stream);
            var record = graph.Roster.ReadTeamRecord(home, child);
            if (HierarchyModule.ConfirmedLaneStream(parentTeam, manager, child, record) != stream)
            {
                continue;
            }

            var name = streams.AsObject().Get(stream)?.Member("sub_orchestrator") is { IsText: true } so ? so.AsString() : HierarchyModule.OrchestratorAddress(child);
            var workstream = record.Member("workstream");
            var state = workstream?.Member("state") is { IsText: true } text ? text.AsString() : null;
            var updated = workstream?.Member("updated") is { IsText: true } at && Ah.Core.Time.Timestamps.TryParseMs(at.AsString(), out var updatedMs) ? (double?)updatedMs : null;
            // a sub-orchestrator that mails its parent is alive: its latest mail counts as a write of its status record
            foreach (var sender in new[] { name.Contains('@', StringComparison.Ordinal) ? name : name + "@" + parentTeam, HierarchyModule.OrchestratorAddress(child) })
            {
                if (mailed.TryGetValue(sender, out var sent) && (updated is null || sent > updated))
                {
                    updated = sent;
                }
            }

            lanes.Add(new ChildLane(child, stream, record, name, state, updated));
        }

        return lanes;
    }

    /// <summary>When each sender last put an entry in the Orchestrator box of <paramref name="parentTeam"/>, by the sender's full address: the team part tells two members of the same name apart.</summary>
    private static Dictionary<string, double> LastMailMs(ServiceGraph graph, string home, string parentTeam, JsonValue manager)
    {
        var last = new Dictionary<string, double>(StringComparer.Ordinal);
        if (new WakeComposition(graph, graph.Environment).OrchestratorBoxOfTeam(home, parentTeam, manager) is not { } box)
        {
            return last;
        }

        foreach (var e in graph.Mailbox.ReadEntries(box.Dir).Entries)
        {
            if (e.IsMap && e.Member("from") is { IsText: true } from && e.Member("created") is { IsText: true } created
                && Ah.Core.Time.Timestamps.TryParseMs(created.AsString(), out var ms) && from.AsString() is { Length: > 0 } sender
                && (!last.TryGetValue(sender, out var seen) || ms > seen))
            {
                last[sender] = ms;
            }
        }

        return last;
    }
}
