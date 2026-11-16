using Ah.Hooks.Gates;
using Ah.Hooks.Plumbing;
using Ah.Mesh;

namespace Ah.Hooks.Lifecycle;

/// <summary>
/// A sub-orchestrator whose workstream is active and whose sub-team still has a live member is waiting on that work, so its Stop
/// is let through without a nudge charged or an obligation waived.
/// </summary>
internal static class SubOrchestratorStop
{
    private static readonly string[] ActiveStates = ["working", "blocked", "review"];

    /// <summary>The managing team when the calling session is a sub-orchestrator (it owns a team that has a parent), else null.</summary>
    internal static string? ManagerOf(GateContext c)
    {
        var s = c.Services;
        if (s.Advisory is not { } advisory)
        {
            return null;
        }

        var pane = Ah.Core.Mux.HerdrFlavour.SessionPaneId(s.Environment.GetVariable) is { Length: > 0 } herdr ? herdr
            : s.Environment.GetVariable("TMUX_PANE") is { Length: > 0 } tmux ? tmux : null;
        var boxes = advisory.Callers.ResolveCaller(new CallerRequest(c.Cwd(), c.Input.NonEmpty("session_id"), s.Process.ParentPid, pane));
        foreach (var box in boxes.Where(b => b.Name == "orchestrator" && b.TeamFile is not null))
        {
            var team = s.Roster.ReadTeamFileAt(box.TeamFile!);
            if (team.IsMap && team.Member("parent")?.Member("team") is { IsText: true } manager)
            {
                return manager.AsString();
            }
        }

        return null;
    }

    /// <summary>Whether the session owns a team with a parent whose workstream is active and that has a member whose process is up.</summary>
    internal static bool WaitsOnItsSubTeam(GateContext c, AdvisoryServices advisory, string sessionId) =>
        WaitedTeam(c, advisory, sessionId) is not null;

    /// <summary>
    /// What a Stop of a sub-orchestrator that waits on its sub-team owes: the wake-source notice of <see cref="WakeRule"/> (the same rule an
    /// Orchestrator is held to, blocking once) while its dispatches are open and the timer or the waiter is not running, else null.
    /// </summary>
    internal static string? WakeNotice(GateContext c, AdvisoryServices advisory, string sessionId) =>
        WaitedTeam(c, advisory, sessionId) is { } team
            ? WakeRule.Notice(c.Services, c.Services.Config.HierarchyDir(c.Cwd()), team, sessionId, c.Cwd())
            : null;

    /// <summary>The name of the team the session owns that has a parent, an active workstream and a member whose process is up, or null.</summary>
    private static string? WaitedTeam(GateContext c, AdvisoryServices advisory, string sessionId)
    {
        var s = c.Services;
        var pane = Ah.Core.Mux.HerdrFlavour.SessionPaneId(s.Environment.GetVariable) is { Length: > 0 } herdr ? herdr
            : s.Environment.GetVariable("TMUX_PANE") is { Length: > 0 } tmux ? tmux : null;
        var boxes = advisory.Callers.ResolveCaller(new CallerRequest(c.Cwd(), sessionId, s.Process.ParentPid, pane));
        foreach (var box in boxes.Where(b => b.Name == "orchestrator" && b.TeamFile is not null && b.Team is not null))
        {
            var team = s.Roster.ReadTeamFileAt(box.TeamFile!);
            if (!team.IsMap || team.Member("parent") is not { IsTruthy: true })
            {
                continue;
            }

            var state = team.Member("workstream")?.Member("state") is { IsText: true } text ? text.AsString() : null;
            if (state is null || !ActiveStates.Contains(state))
            {
                continue;
            }

            if (s.MemberUp is { } up && team.Member("members") is { IsArray: true } members
                && members.AsArray().Any(m => up(s.Config.HierarchyDir(c.Cwd()), m)))
            {
                return box.Team;
            }
        }

        return null;
    }
}
