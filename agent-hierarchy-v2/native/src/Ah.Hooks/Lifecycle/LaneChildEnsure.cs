using Ah.Abstractions;
using Ah.Config;
using Ah.Core.Json;
using Ah.Hierarchy;
using Ah.Hooks.Gates;
using Ah.Hooks.Plumbing;
using Ah.Mesh;

namespace Ah.Hooks.Lifecycle;

/// <summary>A sub-orchestrator's session start makes sure the child team of its lane exists, empty and owned by this session.</summary>
internal static class LaneChildEnsure
{
    /// <summary>Whether the manager's member row <paramref name="member"/> has an orchestrate-class role.</summary>
    private static bool IsOrchestrateMember(GateServices s, JsonValue team, string member, string cwd, string sessionId)
    {
        if (team.Member("members") is not { IsArray: true } rows
            || rows.AsArray().FirstOrDefault(m => m.Member("name") is { IsText: true } n && n.AsString() == member).Member("role") is not { IsText: true } role)
        {
            return false;
        }

        return s.Config.RoleClass(role.AsString(), s.Config.ResolveConfig(cwd, new ResolveOptions(SessionId: sessionId.Length > 0 ? sessionId : null))) == "orchestrate";
    }

    /// <summary>The outcome for the lane this session runs; a refusal coded <c>no-lane-row</c> when no stream names this session's member. It never throws.</summary>
    internal static LaneChildResult? Run(GateContext c, IReadOnlyList<CallerBox> boxes, string cwd, string dir, string sessionId, int ppid)
    {
        try
        {
            var s = c.Services;
            foreach (var box in boxes.Where(b => b.Team is not null && b.Name is not null && b.Name != "orchestrator" && b.TeamFile is not null))
            {
                var team = s.Roster.ReadTeamFileAt(box.TeamFile!);
                if (!team.IsMap || team.Member("streams") is not { IsMap: true } streams)
                {
                    continue;
                }

                var map = streams.AsObject();
                var owned = map.Keys.Where(k => map[k].Member("sub_orchestrator") is { IsText: true } so && so.AsString() == box.Name).ToList();
                if (owned.Count == 0)
                {
                    continue;
                }

                var start = ppid > 1 ? s.Sessions?.Table.StartTime(ppid) : null;
                var adoptOrphan = IsOrchestrateMember(s, team, box.Name!, cwd, sessionId);
                var results = owned.Select(stream => HierarchyModule.EnsureLaneChild(
                    s.Hierarchy.StateCalls,
                    s.Roster,
                    team,
                    s.Hierarchy.Progress,
                    dir,
                    ProgressPool.Of(s, cwd),
                    box.Team!,
                    stream,
                    new LaneChildOwner(sessionId.Length > 0 ? sessionId : null, ppid, start),
                    s.Hierarchy.Core.NewId(),
                    s.Hierarchy.Messages.LocalIso(),
                    new StateCallOptions(Hook: true),
                    adoptOrphan)).ToList();
                foreach (var adopted in results.Where(r => r.Adopted))
                {
                    s.ErrorLog.Log(new HookErrorRecord("sessionstart", $"info: {box.Name} took over lane child team {adopted.Team} from {adopted.AdoptedFrom} (its process is gone)"));
                }

                var refusals = results.Where(r => r.Refusal is not null).Select(r => r.Refusal!).ToList();
                return refusals.Count > 0 ? new LaneChildResult(null, false, string.Join("; ", refusals)) : results.FirstOrDefault(r => r.Created) ?? results.FirstOrDefault(r => r.Adopted) ?? results[0];
            }

            var missing = new LaneChildResult(null, false, "no-lane-row: no open stream of the manager team names this session's member as its sub_orchestrator");
            c.LogError(new InvalidOperationException(missing.Refusal));
            return missing;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            c.LogError(e);
            return new LaneChildResult(null, false, e.Message);
        }
    }
}
