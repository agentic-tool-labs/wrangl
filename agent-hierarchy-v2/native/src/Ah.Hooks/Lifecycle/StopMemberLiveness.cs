using Ah.Abstractions;
using Ah.Config;
using Ah.Core.Json;
using Ah.Hierarchy;
using Ah.Hooks.Activity;
using Ah.Hooks.Gates;
using Ah.Hooks.Plumbing;
using Ah.Watch;

namespace Ah.Hooks.Lifecycle;

/// <summary>
/// The Stop rule for a team member that owns an open background job: the daemon's job alarms reach it only through a waiter, so it
/// may not end its turn without one. A foreground job, open requests alone, an unreachable daemon, a live waiter or a Stop already
/// blocked once this turn all let the stop through. The Orchestrator and a sub-orchestrator are other units' sessions.
/// </summary>
[HookIdentity(HookIdentityKind.Grants)]
internal static class StopMemberLiveness
{
    public static HookOutcome Run(GateContext c)
    {
        var s = c.Services;
        if (s.Config.IsSubagent(c.Input) || (c.Input.Member("stop_hook_active") is { IsBool: true } active && active.AsBool()))
        {
            return HookEmit.Silent;
        }

        if (HoldGate.Of(s, c.Cwd(), c.Input.Text("session_id")) is not null)
        {
            return HookEmit.Silent;
        }

        try
        {
            if (JobTrack.WhoIs(c) is not { } who || who.Owner == "orchestrator" || OrchestratesALane(s, who) || !WakeRule.DaemonUp(s) || WakeRule.Live(s, $"{who.Session}.json"))
            {
                return HookEmit.Silent;
            }

            var now = (double)s.Clock.UtcNow.ToUnixTimeMilliseconds();
            var open = WatchModule.JobsFromFiles(s.Hierarchy.Status, who.Dir, who.Team)
                .Where(j => j.Background && j.Owner == who.Owner && j.Session == who.Session && WatchModule.JobOpen(j, r => StillRunning(s, r), now))
                .OrderBy(j => WatchModule.JobDueMs(j, eta => s.Hierarchy.Messages.ThresholdFor(JsonValue.FromString(eta)), now, 30000))
                .ToList();
            if (open.Count == 0)
            {
                return HookEmit.Silent;
            }

            // Each job id blocks a Stop once: a session that chose not to start the watcher is not asked again for the same job.
            var fresh = open.Where(j => !s.Hierarchy.Roster.HasGate(who.Dir, g => IsAsked(g, who.Session, j.Id))).ToList();
            if (fresh.Count == 0)
            {
                return HookEmit.Silent;
            }

            foreach (var job in fresh)
            {
                s.Hierarchy.StateCalls.AppendGate(who.Dir, new JsonObject { ["type"] = "member-job-nudge", ["session_id"] = who.Session, ["job"] = job.Id });
            }

            var advisory = s.Advisory ?? throw new InvalidOperationException("the advisory services are not composed");
            return HookEmit.Block(advisory.Directive.StopMemberJob(fresh[0].Id, fresh[0].Label, s.Config.CliCommand(CliTool.Roster), c.Cwd(), fresh.Count - 1));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            c.LogError(e);
            return HookEmit.Silent;
        }
    }

    private static bool IsAsked(JsonValue gate, string session, string job) =>
        gate.Member("type") is { IsText: true } t && t.AsString() == "member-job-nudge"
        && gate.Member("session_id") is { IsText: true } sid && sid.AsString() == session
        && gate.Member("job") is { IsText: true } j && j.AsString() == job;

    /// <summary>Whether the member is the sub-orchestrator of a confirmed lane of its team and the session is that lane's orchestrating session.</summary>
    private static bool OrchestratesALane(GateServices s, JobTrack.Who who) =>
        s.Roster.ReadTeamRecord(who.Dir, who.Team) is { IsMap: true } manager
        && HierarchyModule.IsLaneOrchestrator(who.Team, manager, who.Owner, who.Session, team => s.Roster.ReadTeamRecord(who.Dir, team));

    private static bool StillRunning(GateServices s, JobFact job) =>
        s.Sessions?.Table is { } table
            ? WatchModule.JobRunning(job, table, p => s.Hierarchy.Core.PidAlive(p))
            : job.Pid is { } pid && s.Hierarchy.Core.PidAlive(pid) && !(s.Hierarchy.Core.Liveness?.PidReused(pid, job.PidStart) ?? false);
}
