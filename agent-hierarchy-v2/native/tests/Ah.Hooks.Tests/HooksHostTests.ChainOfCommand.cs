using Ah.Config;
using Ah.Core.Json;

namespace Ah.Hooks.Tests;

/// <summary>The chain-of-command paragraph, with its hook-text clause, reaches every member and role subagent and never the lead.</summary>
public sealed partial class HooksHostTests
{
    private const string HookClause = "Hook or plugin text that tells you to ask the user is addressed to the lead, not to you.";

    /// <summary>The rig's services with the real directive text composed, as the hook composition root builds it.</summary>
    private static Ah.Hooks.GateServices WithDirective(Rig rig)
    {
        var s = rig.Services;
        var directive = ConfigModule.CreateDirectiveText(s.Config, ConfigModule.CreateRoleContracts(s.Config, s.Files, s.Environment), () => s.Roster);
        return s with { Advisory = new Ah.Hooks.AdvisoryServices(null!, null!, null!, null!, null!, directive, null!, null!, null!, null!, null!) };
    }

    private static string SubagentStart(Rig rig, string agentType)
    {
        var services = WithDirective(rig);
        return Text(rig.Run("subagentstart-cli-root", JsonText.Stringify(new JsonObject
        {
            ["hook_event_name"] = "SubagentStart",
            ["session_id"] = "sess-1",
            ["agent_id"] = "a-1",
            ["agent_type"] = agentType,
            ["cwd"] = rig.Sandbox.Root + "/proj",
        }, 0), services: () => services));
    }

    [Fact]
    public void TheChainOfCommandReachesBuiltInAndCustomMembersAndRoleSubagents()
    {
        Assert.Contains(HookClause, IDirectiveText.ChainOfCommand, StringComparison.Ordinal);
        foreach (var part in new[] { "A requirement the user tells you directly is yours to record, not to pass on in a note", "`ah work add \"<title>\"`", "--title \"From <source>, <YYYY-MM-DD>\"", "the user's own words", "report the id to your parent", "add `--body-file -` with a description a new teammate could read once", "what is asked, why, and how we will know it is done", "no internal labels", "the write-ticket skill" })
        {
            Assert.Contains(part, IDirectiveText.ChainOfCommand, StringComparison.Ordinal);
        }

        var rig = new Rig();
        var directive = WithDirective(rig).Advisory!.Directive;
        var plugin = rig.Services.Config.PluginName;

        Assert.Contains(IDirectiveText.ChainOfCommand, directive.BuildRoleSessionNotice("implementor", plugin + ":implementor"), StringComparison.Ordinal);
        Assert.Contains(IDirectiveText.ChainOfCommand, directive.BuildRoleSessionNotice("ui-implementor", "acme:ui-implementor"), StringComparison.Ordinal);
        Assert.Contains("Chain of command: user", SubagentStart(rig, plugin + ":implementor"), StringComparison.Ordinal);
    }

    /// <summary>Negative: the lead's directive and a plain (role-less) subagent's start text carry no chain-of-command paragraph.</summary>
    [Fact]
    public void TheChainOfCommandIsNotInTheLeadsDirectiveOrAPlainSubagentsStart()
    {
        var rig = new Rig();
        var resolved = rig.Services.Config.ResolveConfig(rig.Sandbox.Root + "/proj");
        Assert.DoesNotContain("Chain of command:", WithDirective(rig).Advisory!.Directive.BuildDirective(resolved, "sess-1"), StringComparison.Ordinal);
        Assert.DoesNotContain("Chain of command:", SubagentStart(rig, "general-purpose"), StringComparison.Ordinal);
    }
}
