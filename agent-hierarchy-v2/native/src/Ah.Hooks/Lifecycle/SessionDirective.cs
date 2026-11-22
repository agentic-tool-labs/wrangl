using Ah.Abstractions;
using Ah.Config;
using Ah.Core.Json;
using Ah.Hooks.Gates;
using Ah.Hooks.Plumbing;
using Ah.Mesh;

namespace Ah.Hooks.Lifecycle;

/// <summary>
/// The top-level Orchestrator's directive at SessionStart, in a hook entry of its own: the resident core and the index of the sections
/// <c>ah rules show &lt;key&gt;</c> prints. Static text plus resolved config; the dynamic blocks stay in <c>sessionstart</c>, which runs in
/// parallel. It emits exactly when <c>sessionstart</c> would have put this directive in its output, decided from what exists before that unit runs.
/// </summary>
[HookIdentity(HookIdentityKind.None)]
internal static class SessionDirective
{
    public static HookOutcome Run(GateContext c)
    {
        if (c.Services.Config.IsSubagent(c.Input))
        {
            return HookEmit.Silent;
        }

        // As in session-rules: on /clear the flag moves to the new id, idempotently, whichever unit comes first.
        var source = c.Input.Text("source");
        if (source == "clear")
        {
            SessionStart.CarryOrchestratorFlag(c, source, c.Cwd());
        }

        // As sessionstart does for a --agent Orchestrator whose start it does not refuse, idempotently whichever unit comes first: the role
        // lines resolve from the rebound team records.
        if (OrchestratorIdentity.LaunchedAsAgent(c) && !SessionStart.StartRefused(c))
        {
            TeamRebind.Run(c, "session-directive", c.Cwd(), c.Input.Text("session_id") ?? string.Empty, RebindModes.Both);
        }

        return Text(c, c.Cwd()) is { } text ? HookEmit.AdditionalContext("SessionStart", SessionStart.WithLeadLine(text)) : HookEmit.Silent;
    }

    /// <summary>The resident directive of a top-level Orchestrator in a configured, enabled repo; null for any other session.</summary>
    internal static string? Text(GateContext c, string cwd)
    {
        if (Resolved(c, cwd) is not { } resolved)
        {
            return null;
        }

        var s = c.Services;
        var advisory = s.Advisory ?? throw new InvalidOperationException("the advisory services are not composed");
        var dir = s.Config.HierarchyDir(cwd);
        string? model = null;
        try
        {
            model = s.Hierarchy.Roster.SessionModel(c.Input, dir) is { IsText: true } m ? m.AsString() : null;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // as in sessionstart: the tier line falls back to reading the model from the environment line
        }

        return advisory.Directive.BuildOrchestratorDirective(resolved, dir, model);
    }

    /// <summary>
    /// The emit predicate of this unit and of <c>session-index</c>: the session's resolved config when sessionstart would have given it the
    /// top-level Orchestrator's directive, decided from what exists before sessionstart runs; null for any other session.
    /// </summary>
    internal static JsonObject? Resolved(GateContext c, string cwd)
    {
        var s = c.Services;
        if (s.Config.IsSubagent(c.Input) || !SessionStart.TopOrchestratorDirective(c))
        {
            return null;
        }

        var sessionId = c.Input.Text("session_id") ?? string.Empty;
        if (SessionStart.StartRefused(c))
        {
            return null;
        }

        if (!OrchestratorIdentity.In(c))
        {
            return null;
        }

        var resolved = s.Config.ResolveConfig(cwd, new ResolveOptions(SessionId: sessionId.Length > 0 ? sessionId : null));

        // A top-level --agent session of a hierarchy role gets its role notice from sessionstart, not the directive.
        if (s.Config.IsTopLevelAgentSession(c.Input) && s.Config.HierarchyRoleOf(c.Input.Member("agent_type"), resolved, cwd) is { IsText: true } role && role.AsString().Length > 0)
        {
            return null;
        }

        if (resolved.Get("configured") is not { IsTruthy: true } || resolved.Get("enabled") is not { IsTruthy: true })
        {
            return null;
        }

        // sessionstart records a lane session's role in this same event before it picks the directive (DeclaredSubOrchestrator.Is); the same
        // pick, from the registry instead of that record.
        if (s.Config.IsTopLevelAgentSession(c.Input) && c.Input.Text("agent_type") is { Length: > 0 } agentType && SessionStart.LaneRoleOf(s, resolved, agentType) is { } lane
            && s.Config.RoleClass(lane, null) == "orchestrate")
        {
            return null;
        }

        return resolved;
    }
}
