using Ah.Core.Json;
using Ah.Hooks.Activity;
using Ah.Hooks.Gates;
using Ah.Hooks.Plumbing;

namespace Ah.Hooks.Lifecycle;

/// <summary>
/// The session-start side of the chain-down rule: a fresh session whose chain of command is proven Down is told to wind down, and the
/// <c>active</c> row is written at once (there is no earlier observation to wait on, so no grace and no <c>suspect</c> row). A lane lead is
/// judged from the hook's own session, pid and start rather than the registry, which may not hold it yet. It sends no mail and
/// touches no team record.
/// </summary>
internal static class SessionStartChainDown
{
    /// <summary>The wind-down text for a session that is Down (its row written unless already active), or null when it is Up, Unknown or not a member.</summary>
    public static string? Notice(GateContext c, AdvisoryServices advisory, JsonObject? registry, string role, string member, string team, string home, string sessionId)
    {
        var s = c.Services;
        if (sessionId.Length == 0 || member.Length == 0 || s.Sessions is not { } sessions)
        {
            return null;
        }

        try
        {
            var who = new JobTrack.Who(member, team, sessionId, home, role);
            var now = s.Clock.UtcNow;
            var ppid = s.Process.ParentPid;
            (string, long, string?)? own = ppid > 1 ? (sessionId, ppid, sessions.Table.StartTime(ppid)) : null;
            if (StopChainDown.DownLink(s, sessions, who, sessionId, now, own) is not { } chain)
            {
                return null;
            }

            var latest = StopChainDown.LatestRow(s.Hierarchy.Core.PeerRecordsShared(), sessionId);
            if (!(latest?.Member("state") is { IsText: true } st && st.AsString() == "active"))
            {
                StopChainDown.AppendRow(s, "active", sessionId, now, who, chain);
            }

            var form = s.Config.RoleClass(role, registry) is "review" or "design" ? "read-only" : "branch";
            return advisory.Directive.WindDown(chain.Link ?? team, chain.Why, "orchestrator@" + team, form);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            c.LogError(e);
            return null;
        }
    }
}
