using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Hierarchy.StateCalls;
using Ah.Hooks.Gates;
using Ah.Hooks.Plumbing;

namespace Ah.Hooks.Lifecycle;

/// <summary>
/// The team of an Orchestrator that has created none (ah2-34): one empty team named after the project folder, owned by the session and
/// marked <c>implicit</c>, so the session registers a mailbox and the team-scoped views show it. <c>roster create</c> writes over it.
/// </summary>
internal static class ImplicitTeam
{
    public const string Field = "implicit";

    /// <summary>Whether <paramref name="team"/> is an implicit team that no member has joined.</summary>
    public static bool IsEmptyImplicit(JsonValue team) =>
        team.IsMap && team.Member(Field) is { IsBool: true } flag && flag.AsBool() && team.Member("members") is { IsArray: true } members && members.AsArray().Count == 0;

    /// <summary>
    /// Creates the session's implicit team when it may have one; true when this call wrote it. Nothing is written for a member, a session
    /// that turned ah off, a folder name that is no team name, a name already taken, or a process whose start cannot be read. It never throws.
    /// </summary>
    internal static bool Ensure(GateContext c, string cwd, string dir, string sessionId, int ppid)
    {
        try
        {
            var s = c.Services;
            if (s.Environment.GetVariable(Ah.Mesh.MeshModule.MemberMarkerEnv) is { Length: > 0 }
                || s.Environment.GetVariable("WRANGL_TEAM_FILE") is { Length: > 0 }
                || OrchestratorIdentity.Declined(c)
                || sessionId.Length == 0
                || ppid <= 1
                || s.Sessions?.Table.StartTime(ppid) is not { Length: > 0 } start)
            {
                return false;
            }

            var name = s.Config.TeamPrefix(cwd, null);
            var home = s.Config.MainHierarchyDir(cwd) is { Length: > 0 } main ? main : dir;
            if (name.Length == 0
                || s.Config.ValidateTeamAlias(name).Get("ok") is not { IsBool: true } ok
                || !ok.AsBool()
                || !s.Roster.ReadTeamRecord(home, name).IsNull)
            {
                return false;
            }

            var now = s.Hierarchy.Messages.LocalIso();
            var team = new JsonObject
            {
                ["version"] = 1,
                ["team_id"] = s.Hierarchy.Core.NewId(),
                ["created"] = now,
                ["roster_level"] = JsonValue.Null,
                ["transport"] = JsonValue.Null,
                ["orchestrator"] = new JsonObject { ["session_id"] = sessionId, ["pid"] = ppid, ["pid_start"] = start },
                ["members"] = new JsonArray(),
                ["expected_root"] = s.Hierarchy.Messages.RealCwd(cwd),
                ["roster"] = JsonValue.Null,
                ["layout"] = "auto",
                [Field] = true,
            };
            var wrote = false;
            _ = s.Hierarchy.StateCalls.UpdateTeam(
                home,
                name,
                doc =>
                {
                    wrote = !doc.IsMap;
                    return wrote ? TeamFileOutcome.Write(team) : null;
                },
                new StateCallOptions(Hook: true));
            return wrote;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            c.LogError(e);
            return false;
        }
    }

    /// <summary>
    /// Late registration (ah2-32): an Orchestrator session that still resolves no mailbox, because its SessionStart never ran or ran before
    /// ahd was built, gets its implicit team and registers. The boxes it now holds; empty when it may have no team. It never throws.
    /// </summary>
    internal static IReadOnlyList<Ah.Mesh.CallerBox> RegisterLate(GateContext c, AdvisoryServices advisory, Ah.Mesh.CallerRequest request)
    {
        try
        {
            var s = c.Services;
            if (request.SessionId is not { Length: > 0 } sessionId
                || request.Pid is not { } pid
                || s.Config.IsSubagent(c.Input)
                || s.Config.ResolveHierarchyRole(c.Input).Role is not null
                || !OrchestratorIdentity.In(c)
                || s.Config.ResolveConfig(request.Cwd).Get("enabled") is not { IsTruthy: true })
            {
                return [];
            }

            return Ensure(c, request.Cwd, s.Config.HierarchyDir(request.Cwd), sessionId, pid) ? advisory.Callers.EnsureRegistered(request) : [];
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            c.LogError(e);
            return [];
        }
    }

    /// <summary>Removes the implicit team this session owns, if it is still empty: a session that turns ah off keeps no team. It never throws.</summary>
    internal static void Drop(GateContext c, string cwd)
    {
        try
        {
            var s = c.Services;
            var parent = s.Process.ParentPid;
            var invoker = new TeamInvoker(parent > 1 ? parent : null, c.Input.NonEmpty("session_id"));
            var name = s.Config.TeamPrefix(cwd, null);
            var pool = s.Config.HierarchyDir(cwd);
            var home = s.Config.MainHierarchyDir(cwd) is { Length: > 0 } main ? main : pool;
            if (name.Length == 0 || !IsEmptyImplicit(s.Roster.ReadTeamRecord(home, name)))
            {
                return;
            }

            _ = s.Hierarchy.StateCalls.UpdateTeam(
                home,
                name,
                doc => IsEmptyImplicit(doc) && s.Roster.IsTeamOwnedBy(doc, invoker) ? TeamFileOutcome.Clear() : null,
                new StateCallOptions(Hook: true));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            c.LogError(e);
        }
    }
}
