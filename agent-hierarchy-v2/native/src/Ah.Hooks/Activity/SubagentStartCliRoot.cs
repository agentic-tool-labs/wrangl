using Ah.Abstractions;
using Ah.Hooks.Gates;
using Ah.Hooks.Plumbing;

namespace Ah.Hooks.Activity;

/// <summary>
/// SubagentStart: gives a subagent the absolute CLI root, since neither SessionStart nor UserPromptSubmit fires inside it. One
/// line for every subagent; a subagent running a custom or overridden chain role also gets the hierarchy contract, and a design
/// role gets the routing block. Output is only valid inside the <c>hookSpecificOutput</c> envelope.
/// </summary>
[HookIdentity(HookIdentityKind.None)]
internal static class SubagentStartCliRoot
{
    public static HookOutcome Run(GateContext c)
    {
        var s = c.Services;

        // An explicit plugin subagent needs the CLI root whatever its parent is; anything else follows its parent.
        if (c.Input.Text("agent_type")?.StartsWith(s.Environment.PluginName + ":", StringComparison.Ordinal) != true
            && !Ah.Hooks.Lifecycle.OrchestratorIdentity.In(c))
        {
            return HookEmit.Silent;
        }

        var advisory = s.Advisory ?? throw new InvalidOperationException("the advisory services are not composed");
        var resolved = s.Config.ResolveConfig(c.Cwd());
        var configured = resolved.Get("configured") is { IsTruthy: true };
        var enabled = resolved.Get("enabled") is { IsTruthy: true };
        if (configured && !enabled)
        {
            return HookEmit.Silent;
        }

        var blocks = new List<string> { s.Config.CliRootLine() };
        var agentType = c.Input.Member("agent_type");
        var role = s.Config.HierarchyRoleOf(agentType, resolved) is { IsText: true } r ? r.AsString() : null;
        if (role is not null)
        {
            blocks.Add(Ah.Config.IDirectiveText.ChainOfCommand);
            var entry = resolved.Get("roles") is { IsMap: true } roles ? roles.AsObject().Get(role) : null;
            var type = agentType is { IsText: true } t ? t.AsString() : string.Empty;
            if (!type.StartsWith(s.Config.PluginName + ":", StringComparison.Ordinal)
                && type == s.Config.RoleAgent(role, entry)
                && (!s.Config.IsBuiltinRole(role) || s.Config.IsOverride(role, entry)))
            {
                var contract = advisory.Directive.ContractBlock(role, resolved);
                if (!string.IsNullOrEmpty(contract))
                {
                    blocks.Add(contract);
                }
            }

            if (s.Config.RoleClass(role, resolved) == "design")
            {
                var routing = advisory.Directive.DesignRoutingBlock(resolved);
                if (!string.IsNullOrEmpty(routing))
                {
                    blocks.Add(routing);
                }
            }
        }

        if (Ah.Hooks.Lifecycle.SessionRules.SubagentBlock(c, c.Cwd()) is { } rules)
        {
            blocks.Add(rules);
        }

        return HookEmit.AdditionalContext("SubagentStart", string.Join("\n\n", blocks));
    }
}
