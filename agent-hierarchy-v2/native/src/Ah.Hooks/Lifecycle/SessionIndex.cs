using Ah.Abstractions;
using Ah.Hooks.Gates;
using Ah.Hooks.Plumbing;

namespace Ah.Hooks.Lifecycle;

/// <summary>
/// The top-level Orchestrator's index at SessionStart, in a hook entry of its own after <c>session-directive</c>: the look-up rule, the index
/// of the sections <c>ah rules show &lt;key&gt;</c> prints, and the lifted never rules. Static text; it emits exactly when
/// <c>session-directive</c> does.
/// </summary>
[HookIdentity(HookIdentityKind.None)]
internal static class SessionIndex
{
    public static HookOutcome Run(GateContext c)
    {
        if (c.Services.Config.IsSubagent(c.Input))
        {
            return HookEmit.Silent;
        }

        // As in session-directive: on /clear the flag moves to the new id, idempotently, whichever unit comes first.
        var source = c.Input.Text("source");
        if (source == "clear")
        {
            SessionStart.CarryOrchestratorFlag(c, source, c.Cwd());
        }

        return Text(c, c.Cwd()) is { } text ? HookEmit.AdditionalContext("SessionStart", SessionStart.WithLeadLine(text)) : HookEmit.Silent;
    }

    /// <summary>The index of a session that gets the top-level Orchestrator's directive; null for any other session.</summary>
    internal static string? Text(GateContext c, string cwd) =>
        SessionDirective.Resolved(c, cwd) is { } resolved
            ? (c.Services.Advisory ?? throw new InvalidOperationException("the advisory services are not composed")).Directive.BuildOrchestratorIndex(resolved)
            : null;
}
