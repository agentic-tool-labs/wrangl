using System.Globalization;
using Ah.Abstractions;
using Ah.Hooks.Plumbing;
using Ah.Mesh;

namespace Ah.Hooks.Lifecycle;

/// <summary>
/// Repairs the Orchestrator record of the teams a session's process owns, from the pair a hook proves: the input's session id
/// and the hook's parent process, which is the harness's own identity. The parent counts only when it is the harness process
/// the environment names, so a hook run through a wrapper shell never stamps a transient pid.
/// </summary>
internal static class TeamRebind
{
    public static void Run(GateContext c, string unit, string cwd, string sessionId, RebindModes modes)
    {
        var s = c.Services;
        var parent = s.Process.ParentPid;
        if (s.Advisory is not { } advisory || sessionId.Length == 0 || parent <= 1)
        {
            return;
        }

        var parentText = parent.ToString(CultureInfo.InvariantCulture);
        if (s.Environment.GetVariable("CLAUDE_PID") is { Length: > 0 } claude && claude != parentText)
        {
            s.ErrorLog.Log(new HookErrorRecord(unit, $"rebind skipped: parent {parentText} is not CLAUDE_PID {claude}"));
            return;
        }

        try
        {
            var result = advisory.Callers.RebindOwnedTeams(new RebindRequest(cwd, sessionId, parent, modes, Hook: true));
            foreach (var note in result.Notes)
            {
                s.ErrorLog.Log(new HookErrorRecord(unit, note));
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            s.ErrorLog.Log(new HookErrorRecord(unit, e.Message));
        }
    }
}
