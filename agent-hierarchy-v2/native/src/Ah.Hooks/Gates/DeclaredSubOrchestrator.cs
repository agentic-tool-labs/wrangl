using Ah.Hooks.Plumbing;

namespace Ah.Hooks.Gates;

/// <summary>
/// Whether the session declares itself a sub-orchestrator: its payload names the sub-orchestrator agent, or the role persisted for
/// its session has the orchestrate class. A declaration only ever restricts; nothing is granted by it, so a session that claims
/// it gains nothing and one that hides it is still caught by the proof the owner verbs ask for.
/// </summary>
internal static class DeclaredSubOrchestrator
{
    internal static bool Is(GateContext c)
    {
        var s = c.Services;
        if (s.Environment.LocalName(c.Input.Text("agent_type")) == "sub-orchestrator")
        {
            return true;
        }

        return c.Input.NonEmpty("session_id") is { } sessionId
            && s.Config.ReadSessionRole(sessionId) is { IsText: true } role
            && role.AsString() != "orchestrator"
            && s.Config.RoleClass(role.AsString(), null) == "orchestrate";
    }

    /// <summary>Whether the session declares itself the Deputy-Chief: its payload names the deputy-chief agent, or that role is persisted for its session. It only picks the directive's audience.</summary>
    internal static bool IsDeputy(GateContext c)
    {
        var s = c.Services;
        return s.Environment.LocalName(c.Input.Text("agent_type")) == "deputy-chief"
            || (c.Input.NonEmpty("session_id") is { } sessionId && s.Config.ReadSessionRole(sessionId) is { IsText: true } role && role.AsString() == "deputy-chief");
    }
}
