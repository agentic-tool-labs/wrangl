using Ah.Core.Json;
using Ah.Hooks.Gates;
using Ah.Hooks.Plumbing;

namespace Ah.Hooks.Tests;

/// <summary>ah2-216: the Orchestrator's first team call in a span gets one note naming the team commands; nothing is ever held.</summary>
public sealed partial class HooksHostTests
{
    private const string Orchestrator = "wrangl:orchestrator";

    private static string AgentAs(string agentType, string subagentType) =>
        Event("Agent", agentType, new JsonObject { ["subagent_type"] = subagentType, ["prompt"] = "x", ["description"] = "x" });

    private static Rig DispatchRig(string? dispatch)
    {
        var rig = SoftRig();
        if (dispatch is not null)
        {
            Directory.CreateDirectory(rig.Sandbox.Root + "/proj/.claude/ah2");
            File.WriteAllText(rig.Sandbox.Root + "/proj/.claude/ah2/agent-hierarchy.json", $"{{\"version\":1,\"dispatch\":\"{dispatch}\"}}");
        }

        return rig;
    }

    [Fact]
    public void TheFirstChainRoleAgentCallUnderPeersGetsTheNoteOnceAndIsNotHeld()
    {
        var rig = DispatchRig("peers");
        var first = rig.Run("pretooluse-chain", AgentAs(Orchestrator, "wrangl:architect"));
        Assert.DoesNotContain("permissionDecision", Text(first), StringComparison.Ordinal);
        Assert.Contains("ah: architect runs as a peer session here (dispatch: peers)", Text(first), StringComparison.Ordinal);
        Assert.Contains("spawn-ad-hoc architect", Text(first), StringComparison.Ordinal);
        Assert.Contains("`ah rules show peer-roster`", Text(first), StringComparison.Ordinal);

        Assert.Equal(string.Empty, Chain(rig, AgentAs(Orchestrator, "wrangl:reviewer")));
    }

    [Fact]
    public void NegativeAfterTheRuleWasShownTheFirstCallGetsNoNote()
    {
        var rig = DispatchRig("peers");
        rig.Services.Hierarchy.Roster.AppendGateRouted(HierDir(rig), new JsonObject { ["type"] = "soft-gate", ["rule"] = "team-commands", ["outcome"] = "read", ["session_id"] = "sess-1" });
        Assert.Equal(string.Empty, Chain(rig, AgentAs(Orchestrator, "wrangl:architect")));
    }

    [Fact]
    public void NegativeAReadMarkBeforeTheLatestResetCountsForNothing()
    {
        var rig = DispatchRig("peers");
        rig.Services.Hierarchy.Roster.AppendGateRouted(HierDir(rig), new JsonObject { ["type"] = "soft-gate", ["rule"] = "team-commands", ["outcome"] = "read", ["session_id"] = "sess-1" });
        rig.Services.Hierarchy.Roster.AppendGateRouted(HierDir(rig), new JsonObject { ["type"] = "soft-gate", ["outcome"] = "reset", ["session_id"] = "sess-1" });
        Assert.Contains("runs as a peer session here", Chain(rig, AgentAs(Orchestrator, "wrangl:architect")), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("wrangl:task-runner")]
    [InlineData("task-gopher:task-gopher")]
    [InlineData("wrangl:backlog-groomer")]
    [InlineData("general-purpose")]
    [InlineData("wrangl:sub-orchestrator")]
    public void NegativeALegworkOrNonRoleAgentCallGetsNoNote(string subagentType)
    {
        var rig = DispatchRig("peers");
        Assert.Equal(string.Empty, Chain(rig, AgentAs(Orchestrator, subagentType)));
        Assert.Empty(SoftRows(rig));
    }

    [Theory]
    [InlineData("subagents")]
    [InlineData("prefer-subagents")]
    public void NegativeUnderASubagentModeTheAgentCallGetsNoNote(string dispatch) =>
        Assert.Equal(string.Empty, Chain(DispatchRig(dispatch), AgentAs(Orchestrator, "wrangl:architect")));

    [Fact]
    public void UnderPreferPeersTheNoteLeavesTheSubagentToTheOrchestrator()
    {
        var text = Chain(DispatchRig("prefer-peers"), AgentAs(Orchestrator, "wrangl:implementor"));
        Assert.Contains("peer sessions are the default here (dispatch: prefer-peers); a subagent for implementor is your call", text, StringComparison.Ordinal);
        Assert.Contains("This note does not hold the call.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("went through", text, StringComparison.Ordinal);
    }

    /// <summary>A rig whose session <c>sess-1</c> is flagged as the Orchestrator by command and owns no team (Amendment 3).</summary>
    private static Rig FlaggedRig(Dictionary<string, string>? env = null)
    {
        var rig = SoftRig(env);
        Directory.CreateDirectory(rig.Sandbox.Root + "/proj/.claude/ah2");
        File.WriteAllText(rig.Sandbox.Root + "/proj/.claude/ah2/agent-hierarchy.json", "{\"version\":1,\"dispatch\":\"peers\"}");
        Ah.TestKit.TestHomeDocs.Put(rig.Sandbox.Root + "/ah", Ah.TestKit.TestHomeDocs.SessionRoles, "{\"version\":1,\"sessions\":{\"sess-1\":{\"orchestrator_by_command\":true}}}");
        return rig;
    }

    [Fact]
    public void AFlaggedOrchestratorWithNoTeamGetsTheNoteOnItsFirstChainRoleAgentCall() =>
        Assert.Contains("ah: architect runs as a peer session here", Chain(FlaggedRig(), AgentAs(string.Empty, "wrangl:architect")), StringComparison.Ordinal);

    [Fact]
    public void NegativeAFlaggedMemberSessionGetsNoNote()
    {
        var rig = FlaggedRig(new() { ["AH_MEMBER_NAME"] = "proj-implementor" });
        Assert.Equal(string.Empty, Chain(rig, AgentAs(string.Empty, "wrangl:architect")));
        Assert.Empty(SoftRows(rig, "nudge"));
    }

    private static string? PromptNoteFor(Rig rig, string prompt) =>
        TeamCommands.PromptNote(
            new GateContext(rig.Services, JsonText.Parse(JsonText.Stringify(new JsonObject { ["hook_event_name"] = "UserPromptSubmit", ["session_id"] = "sess-1", ["agent_type"] = Orchestrator, ["prompt"] = prompt }, 0)), "x"),
            prompt);

    [Fact]
    public void UnderPreferSubagentsThePromptNoteGivesTheAgentCallFirstAndThePeerAsTheAlternative()
    {
        var note = PromptNoteFor(DispatchRig("prefer-subagents"), "spawn two reviewers")!;
        Assert.StartsWith(
            "To start a subagent for reviewer (dispatch: prefer-subagents, the default): `Agent(subagent_type:\"wrangl:reviewer\")`, with the brief in the Agent prompt; a peer for it instead: `",
            note,
            StringComparison.Ordinal);
        Assert.Contains("spawn-ad-hoc reviewer", note, StringComparison.Ordinal);
        Assert.DoesNotContain("Not the Agent tool", note, StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeUnderPeersThePromptNoteStillRulesOutTheAgentTool() =>
        Assert.StartsWith("To start a peer for reviewer: `", PromptNoteFor(DispatchRig("peers"), "spawn two reviewers")!, StringComparison.Ordinal);

    [Fact]
    public void NegativeAPromptWithNoStartVerbGetsNoNote() =>
        Assert.Null(PromptNoteFor(DispatchRig("peers"), "the reviewers look busy"));

    [Fact]
    public void NegativeAMemberOrPlainSessionGetsNoNote()
    {
        var rig = DispatchRig("peers");
        Assert.Equal(string.Empty, Chain(rig, AgentAs(string.Empty, "wrangl:architect")));
        Assert.Equal(string.Empty, Chain(rig, AsSubagent(AgentAs(Orchestrator, "wrangl:architect"))));
        Assert.Empty(SoftRows(rig));
    }

    [Fact]
    public void ARosterVerbThatStartsAMemberGetsTheNoteOnceAndAReadVerbNone()
    {
        var rig = DispatchRig(null);
        Assert.Equal(string.Empty, Chain(rig, BashAs(Orchestrator, "ah roster status")));
        var first = Chain(rig, BashAs(Orchestrator, "ah roster spawn-ad-hoc reviewer --cwd /w"));
        Assert.Contains("ah: before starting, reshaping or closing members, read the team commands: `ah rules show peer-roster`.", first, StringComparison.Ordinal);
        Assert.DoesNotContain("permissionDecision", first, StringComparison.Ordinal);
        Assert.Equal(string.Empty, Chain(rig, BashAs(Orchestrator, "ah roster spawn-ad-hoc architect --cwd /w")));
    }

    [Fact]
    public void NegativeACallThatShowsTheRuleGetsNoNote() =>
        Assert.Equal(string.Empty, Chain(DispatchRig(null), BashAs(Orchestrator, "ah rules show peer-roster && ah roster spawn-ad-hoc reviewer --cwd /w")));

    [Theory]
    [InlineData("herdr pane split --direction right", true)]
    [InlineData("herdr --session s agent start rev-1", true)]
    [InlineData("herdr tab new", true)]
    [InlineData("hc run --bg -- claude --agent wrangl:reviewer", true)]
    [InlineData("tmux new-window 'claude --agent wrangl:reviewer'", true)]
    [InlineData("tmux split-window -h claude", true)]
    [InlineData("herdr pane list", false)] // negative: a read
    [InlineData("hc run -- npm test", false)] // negative: hc runs something that is not claude
    [InlineData("tmux new-window htop", false)] // negative: a window that starts no claude
    [InlineData("tmux list-windows claude", false)] // negative: no new window
    public void AMultiplexerStartOfAClaudeSessionIsFound(string command, bool starts) =>
        Assert.Equal(starts, ShellScan.Commands(command)!.Any(cmd => TeamCommands.StartsSession(cmd, command)));

    [Fact]
    public void AMultiplexerStartGetsTheNoteNamingSpawnAdHoc()
    {
        var text = Chain(DispatchRig(null), BashAs(Orchestrator, "herdr pane split --direction right"));
        Assert.Contains("ah: members are started with `", text, StringComparison.Ordinal);
        Assert.Contains("spawn-ad-hoc <role>", text, StringComparison.Ordinal);
    }

    private static readonly string[] Roles = ["architect", "reviewer", "implementor", "ultra-advisor", "my-critic"];

    [Theory]
    [InlineData("spawn two peer reviewers", "reviewer")]
    [InlineData("please spin up three more implementors", "implementor")]
    [InlineData("Start an architect for this", "architect")]
    [InlineData("bring up the ultra-advisor", "ultra-advisor")]
    [InlineData("add a my-critic peer", "my-critic")]
    [InlineData("launch a couple of agents", "<role>")]
    [InlineData("open a new teammate", "<role>")]
    [InlineData("the reviewer said to add tests later", null)] // negative: the role comes before the verb
    [InlineData("add a test for the reviewer", null)] // negative: the role is past the reach
    [InlineData("start the build", null)] // negative: a start verb with no member word
    [InlineData("spinning reviewers", null)] // negative: "spin" without "up"
    public void APromptAskingToStartAMemberNamesItsRole(string prompt, string? role) =>
        Assert.Equal(role, TeamCommands.AskedRole(prompt, Roles));
}
