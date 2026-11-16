using Ah.Config;
using Ah.Core.Json;
using Ah.Hierarchy;
using Ah.Mesh;
using Ah.Watch;

namespace Ah.Hooks.Lifecycle;

/// <summary>
/// The Stop rule for the Orchestrator's checklist: it may not end its turn while a queued item has an idle member of its role and no
/// dispatch (the queue-stall condition), or while any item waits on it. An item waiting on the user never blocks. It reads files only.
/// </summary>
internal static class QueueRule
{
    /// <summary>The block text, starting with the line that says to read the checklist, or null when nothing needs the Orchestrator.</summary>
    internal static string? Notice(GateServices s, AdvisoryServices advisory, string dir, string pool, string team, double nowMs, string cwd, string sessionId)
    {
        if (Ah.Roster.RosterModule.ChainDownActive(s.Hierarchy.Core.PeerRecordsShared(), sessionId))
        {
            return null;
        }

        var entries = HierarchyModule.ChecklistEntries(s.Hierarchy.Progress, pool, HierarchyModule.ProgressTeamOf(team));
        if (entries.Count == 0)
        {
            return null;
        }

        var dispatches = new List<string>();
        if (HierarchyModule.ChecklistAnyQueuedLongEnough(entries, nowMs) && s.Roster.ReadTeamRecord(dir, team) is { IsMap: true } record)
        {
            if (s.Sessions?.Table is { } table)
            {
                try
                {
                    var slots = WatchModule.SlotRecords(s.Files, s.Environment, table, pid => MeshModule.CallerSessionIds(table, pid, advisory.Mailbox));
                    var scan = WatchModule.QueueStallsFromFiles(s.Hierarchy, record, entries, dir, team, nowMs, session => WatchModule.SlotOf(slots, session) is not null);
                    dispatches.AddRange(scan.Stalled.Select(static q => HierarchyModule.ChecklistDispatchLine(HierarchyModule.ChecklistKey(q.Entry), q.Eligible[0])));
                }
#pragma warning disable CA1031 // a failed scan must not fail the Stop hook; the items waiting on the Orchestrator are still listed
                catch (Exception)
#pragma warning restore CA1031
                {
                }
            }
        }

        var lines = HierarchyModule.ChecklistQueueLines(entries, dispatches, nowMs);
        if (lines.Count == 0)
        {
            return null;
        }

        // An item blocks a Stop once per state: the same item in the same state is not asked about again, and a new state is.
        var keys = dispatches.Select(static d => "dispatch:" + d)
            .Concat(HierarchyModule.ChecklistWaitingOnOrchestrator(entries, nowMs, 0).Select(static w => FormattableString.Invariant($"answer:{HierarchyModule.ChecklistKey(w)}:{w.Item.State}:{HierarchyModule.ChecklistStateSinceMs(w.Item):F0}:{w.Item.Todo}")))
            .ToList();
        var fresh = new List<(string Line, string Key)>();
        for (var i = 0; i < lines.Count; i++)
        {
            var key = i < keys.Count ? keys[i] : "line:" + lines[i];
            if (!s.Hierarchy.Roster.HasGate(dir, g => IsAsked(g, sessionId, key)))
            {
                fresh.Add((lines[i], key));
            }
        }

        if (fresh.Count == 0)
        {
            return null;
        }

        foreach (var (_, key) in fresh)
        {
            s.Hierarchy.StateCalls.AppendGate(dir, new JsonObject { ["type"] = "queue-nudge", ["session_id"] = sessionId, ["key"] = key });
        }

        return advisory.Directive.StopChecklist(s.Config.CliCommand(CliTool.Roster), cwd, [.. fresh.Select(static f => f.Line)]);
    }

    private static bool IsAsked(JsonValue gate, string session, string key) =>
        gate.Member("type") is { IsText: true } t && t.AsString() == "queue-nudge"
        && gate.Member("session_id") is { IsText: true } sid && sid.AsString() == session
        && gate.Member("key") is { IsText: true } k && k.AsString() == key;
}
