using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Hooks.Gates;
using Ah.Hooks.Plumbing;
using Ah.Launch;

namespace Ah.Hooks.Lifecycle;

/// <summary>
/// SessionEnd roster writer. A top-level <c>--agent &lt;hierarchy role&gt;</c> session recorded <c>up</c> at SessionStart; this
/// appends the matching <c>down</c> when it ends. The payload carries no <c>agent_type</c>, so the branch is: <c>agent_type</c>
/// names a role, or the roster's latest record for this session id is <c>up</c>. An orchestrator ending with an owned team that
/// still has members leaves a snapshot of it. Anything else writes nothing; every failure is silent.
/// </summary>
[HookIdentity(HookIdentityKind.Grants)]
internal static class SessionEndRoster
{
    public static HookOutcome Run(GateContext c)
    {
        var s = c.Services;
        if (s.Config.IsSubagent(c.Input))
        {
            return HookEmit.Silent;
        }

        var advisory = s.Advisory ?? throw new InvalidOperationException("the advisory services are not composed");
        var cwd = c.Cwd();
        var dir = s.Config.HierarchyDir(cwd);
        var sessionId = c.Input.Text("session_id") ?? string.Empty;
        string? role = null;
        if (s.Config.IsTopLevelAgentSession(c.Input))
        {
            role = s.Config.LookupRole(c.Input.Member("agent_type"), cwd).Get("role") is { IsText: true } lr && lr.AsString().Length > 0 ? lr.AsString() : null;
        }

        var hook = new StateCallOptions(Hook: true);
        var ppid = s.Process.ParentPid;

        // Leaving a snapshot is apart from the roster write below: its failure does not stop the member going down.
        try
        {
            var rules = new SnapshotRules(s.Files, s.Config.KindDefault);
            var builder = new SnapshotBuilder(
                s.Files, advisory.Runner, s.Clock, s.Config, s.Roster, s.Hierarchy.Roster, s.Hierarchy.Core, advisory.Mailbox, s.Hierarchy.StateCalls, s.ErrorLog);
            foreach (var name in s.Roster.OwnedTeams(dir, new TeamInvoker(ppid, sessionId.Length > 0 ? sessionId : null)))
            {
                var doc = builder.BuildSnapshot(dir, name, s.Clock.UtcNow);
                if (doc is not null && doc.Get("members") is { IsArray: true } members && members.AsArray().Count > 0)
                {
                    s.Hierarchy.StateCalls.Snapshot(
                        dir,
                        new JsonObject
                        {
                            ["team_name"] = rules.SnapshotTeamName(name),
                            ["ts"] = rules.SnapshotStamp(s.Clock.UtcNow),
                            ["doc"] = doc,
                        },
                        hook);
                }
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            c.LogError(e);
        }

        var up = sessionId.Length > 0 ? s.Hierarchy.Roster.UpRecordFor(dir, sessionId) : JsonValue.Null;
        if (role is null && up.Member("role") is { IsText: true } ur && ur.AsString().Length > 0)
        {
            role = ur.AsString();
        }

        // A session id can be resumed by a second process; only the registered process, or any process once the
        // registered one is proven dead, may mark the member down. A registered process that is undecided counts as running.
        var upPid = up.Member("pid");
        var foreignEnd = up.IsTruthy && !(upPid is { IsNumber: true } p && p.AsNumber() == ppid) && RegisteredProcessRuns(s, upPid, up.Member("pid_start"));
        if (!foreignEnd && sessionId.Length > 0)
        {
            s.Hierarchy.Status.ClearActivityRouted(dir, sessionId, hook: true, mirrorDir: Ah.Hooks.Activity.ActivityRecorder.MirrorDir(s, cwd, dir));
        }

        // A clear ends the session id but not the process, so the member is not marked down.
        var cleared = string.Equals(c.Input.Text("reason"), "clear", StringComparison.Ordinal);
        if (role is not null && !foreignEnd && !cleared)
        {
            JsonValue pane = up.Member("pane_id") is { IsTruthy: true } pp ? pp
                : Ah.Core.Mux.HerdrFlavour.SessionPaneId(s.Environment.GetVariable) is { } herdr ? herdr
                : JsonValue.Null;
            s.Hierarchy.StateCalls.AppendRosterRecord(
                dir,
                new JsonObject
                {
                    ["status"] = "down",
                    ["role"] = role,
                    ["session_id"] = sessionId.Length > 0 ? sessionId : JsonValue.Null,
                    ["pid"] = ppid,
                    ["pane_id"] = pane,
                    ["cwd"] = cwd,
                },
                hook);
        }

        return HookEmit.Silent;
    }

    /// <summary>Whether the process an up row names is not proven gone: false for a row without a valid pid, true for a live or an undecided one.</summary>
    private static bool RegisteredProcessRuns(GateServices s, JsonValue? pid, JsonValue? start)
    {
        if (pid is not { IsNumber: true } number || number.AsNumber() is not (> 0 and <= int.MaxValue) || number.AsNumber() != Math.Floor(number.AsNumber()))
        {
            return false;
        }

        var recorded = start is { IsText: true } text && text.AsString().Length > 0 ? text.AsString() : null;
        return s.Hierarchy.Core.Liveness is { } liveness
            ? liveness.PidVerdict((int)number.AsNumber(), recorded) != Ah.Core.Time.LivenessVerdict.ProvenDead
            : s.Hierarchy.Core.IdentityLive(number, start ?? JsonValue.Null);
    }
}
