using Ah.Hierarchy;
using Ah.Watch;

namespace Ah.Cli.Composition;

/// <summary>The queued items of a named team as the web view's queue slice lists them: the checklist, the team record and the slot files, read for one refresh.</summary>
internal static class QueueRows
{
    internal const string DefaultFolder = "_default";

    // entriesOf: the checklist entries of (directory, progress team) when the caller holds them; null folds them here
    internal static IReadOnlyList<QueueRow> Of(ServiceGraph g, string hierarchyDir, string team, double nowMs, Func<string, string, IReadOnlyList<ChecklistEntry>>? entriesOf = null)
    {
        if (team == DefaultFolder || g.Roster.ReadTeamRecord(hierarchyDir, team) is not { IsMap: true } record)
        {
            return [];
        }

        var progressTeam = HierarchyModule.ProgressTeamOf(team);
        var entries = entriesOf?.Invoke(hierarchyDir, progressTeam) ?? HierarchyModule.ChecklistEntries(g.Hierarchy.Progress, hierarchyDir, progressTeam);
        // the facts below read the dir's whole message pool; a team with no queued item needs none of them
        if (!HierarchyModule.QueueMayHaveItems(entries))
        {
            return [];
        }

        var slots = new SlotFiles(g, null).Read();
        var (members, dispatches) = WatchModule.QueueFactsFromFiles(g.Hierarchy, record, hierarchyDir, team, session => WatchModule.SlotOf(slots, session) is not null);
        return [.. HierarchyModule.QueueAll(entries, members, dispatches, nowMs).Select(static q => new QueueRow(
            HierarchyModule.ChecklistKey(q.Entry), q.Role, q.QueuedSinceMs, q.QueuedSinceMs + HierarchyModule.QueueStallMs, q.Eligible, q.LastDispatchMs) { Title = q.Entry.Item.Title })];
    }
}
