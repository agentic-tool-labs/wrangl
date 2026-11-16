using Ah.Abstractions;
using Ah.Config;
using Ah.Core.Json;
using Ah.Hierarchy;
using Ah.Hooks.Gates;
using Ah.Hooks.Plumbing;

namespace Ah.Hooks.Lifecycle;

/// <summary>
/// The rules of engagement a session gets at start and after compaction, in a hook entry of its own: the directive is over the harness's cap, so anything appended to it would sit
/// behind the preview. Injected text only: nothing reads whether a rule was followed.
/// </summary>
[HookIdentity(HookIdentityKind.None)]
internal static class SessionRules
{
    /// <summary>Who a session counts as for its rules.</summary>
    private sealed record Who(string? Team, IReadOnlySet<string> Names, bool Orchestrator, bool SubOrchestrator = false, string? TeamDir = null, string? Parent = null);

    public static HookOutcome Run(GateContext c)
    {
        if (c.Services.Config.IsSubagent(c.Input))
        {
            return HookEmit.Silent;
        }

        // SessionStart runs in parallel and moves the flag to the new id on /clear; the move is idempotent, so whichever hook comes first does it.
        var source = c.Input.Text("source");
        if (source == "clear")
        {
            SessionStart.CarryOrchestratorFlag(c, source, c.Cwd());
        }

        if (!OrchestratorIdentity.In(c))
        {
            return HookEmit.Silent;
        }

        return SessionText(c, c.Cwd()) is { } text ? HookEmit.AdditionalContext("SessionStart", text) : HookEmit.Silent;
    }

    /// <summary>What the session of this payload gets: the block, and for an Orchestrator its text, which also says how to add a rule and so comes even with no rules. Null for a bare or disabled session.</summary>
    internal static string? SessionText(GateContext c, string cwd)
    {
        if (WhoIs(c, cwd) is not { } who)
        {
            return null;
        }

        var block = BlockOf(c, cwd, who);
        if (!who.Orchestrator)
        {
            return block;
        }

        var text = who.SubOrchestrator ? RulesOfEngagement.SubOrchestratorText : RulesOfEngagement.OrchestratorText;
        if (!who.SubOrchestrator && c.Services.AdrWaiting?.Invoke(cwd) is { } waiting)
        {
            text += "\n\n" + waiting;
        }

        return block is null ? text : block + "\n\n" + text;
    }

    /// <summary>The block a subagent gets: it resolves as the session that spawned it, whose id its <c>session_id</c> carries. Never the Orchestrator's text.</summary>
    internal static string? SubagentBlock(GateContext c, string cwd) => WhoIs(c, cwd) is { } who ? BlockOf(c, cwd, who) : null;

    private static string? BlockOf(GateContext c, string cwd, Who who) => c.Services.RulesBlock?.Invoke(cwd, new RulesSubject(who.Team, who.Names, who.Orchestrator, who.TeamDir, who.Parent));

    private static Who? WhoIs(GateContext c, string cwd)
    {
        var s = c.Services;
        var sessionId = c.Input.Text("session_id") ?? string.Empty;
        var registry = s.Config.ResolveConfig(cwd, new ResolveOptions(SessionId: sessionId.Length > 0 ? sessionId : null));
        var configured = registry.Get("configured") is { IsTruthy: true };
        if (configured && registry.Get("enabled") is not { IsTruthy: true })
        {
            return null;
        }

        var declared = IsLaneSession(c, cwd, registry) || DeclaredSubOrchestrator.Is(c);
        if (!declared && RoleOf(c, cwd, registry) is { } role)
        {
            return MemberOf(c, cwd, role, sessionId);
        }

        if (!configured || !OrchestratorIdentity.In(c))
        {
            return null;
        }

        var none = new HashSet<string>(StringComparer.Ordinal);
        if (declared && LaneChildOf(c) is { } lane)
        {
            return new Who(lane.Child, none, true, true, lane.Dir, lane.Manager);
        }

        var owned = OrchestratorIdentity.OwnedTeam(c, cwd);
        return new Who(owned?.Team, none, true, declared, owned?.Dir);
    }

    /// <summary>
    /// A lane session's team: the child of the one stream of the manager team (its launcher's team file) that names its member as sub-orchestrator.
    /// SessionStart writes the child in parallel, so this names it from the manager rather than reading it.
    /// </summary>
    private static (string Child, string Manager, string? Dir)? LaneChildOf(GateContext c)
    {
        var s = c.Services;
        if (s.Environment.GetVariable("WRANGL_TEAM_FILE") is not { Length: > 0 } file
            || Path.GetFileNameWithoutExtension(file) is not { Length: > 0 } manager
            || s.Environment.GetVariable(Ah.Mesh.MeshModule.MemberMarkerEnv) is not { Length: > 0 } member
            || s.Roster.ReadTeamFileAt(file).Member("streams") is not { IsMap: true } streams)
        {
            return null;
        }

        var map = streams.AsObject();
        var led = map.Keys.Where(k => map[k].Member("sub_orchestrator") is { IsText: true } so && so.AsString() == member).ToList();
        return led.Count == 1 ? (HierarchyModule.LaneChildName(manager, led[0]), manager, OrchestratorIdentity.DirOfTeamFile(file)) : null;
    }

    /// <summary>A top-level session of a lane role, decided from the registry: the session role that says the same is written by SessionStart, which runs in parallel.</summary>
    private static bool IsLaneSession(GateContext c, string cwd, JsonObject registry)
    {
        var s = c.Services;
        return !s.Config.IsSubagent(c.Input)
            && s.Config.IsTopLevelAgentSession(c.Input)
            && !(s.Config.HierarchyRoleOf(c.Input.Member("agent_type"), registry, cwd) is { IsText: true } role && role.AsString().Length > 0)
            && c.Input.Text("agent_type") is { Length: > 0 } agentType
            && SessionStart.LaneRoleOf(s, registry, agentType) is not null;
    }

    /// <summary>The peer role of the session, or of the session that spawned a subagent.</summary>
    private static string? RoleOf(GateContext c, string cwd, JsonObject registry)
    {
        var s = c.Services;
        if (s.Config.IsSubagent(c.Input))
        {
            return s.Config.ReadSessionRole(c.Input.Member("session_id")) is { IsText: true } parent && parent.AsString().Length > 0 && parent.AsString() != "orchestrator" ? parent.AsString() : null;
        }

        if (!s.Config.IsTopLevelAgentSession(c.Input))
        {
            return null;
        }

        return s.Config.HierarchyRoleOf(c.Input.Member("agent_type"), registry, cwd) is { IsText: true } role && role.AsString().Length > 0 ? role.AsString() : null;
    }

    private static Who MemberOf(GateContext c, string cwd, string role, string sessionId)
    {
        var s = c.Services;
        var names = new HashSet<string>(StringComparer.Ordinal) { role };
        var dir = s.Config.HierarchyDir(cwd);
        var lookup = SessionStart.ResolveMemberRow(s, dir, cwd, role, SessionStart.PaneOf(s), s.Process.ParentPid, sessionId, logUnclaimable: false);
        if (lookup.Member.Member("name") is { IsText: true } name)
        {
            _ = names.Add(name.AsString());
        }

        if (s.Environment.GetVariable("AH_MEMBER_NAME") is { Length: > 0 } marker)
        {
            _ = names.Add(marker);
        }

        return new Who(lookup.Attributed.Member("teamName") is { IsText: true } team ? team.AsString() : null, names, false);
    }
}
