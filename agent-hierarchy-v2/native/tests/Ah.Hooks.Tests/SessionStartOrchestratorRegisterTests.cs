using Ah.Core.Json;
using Ah.Mesh;

namespace Ah.Hooks.Tests;

/// <summary>b457 R1: an Orchestrator launched with <c>--agent</c> registers its caller at session start, so a restart is never "no signal".</summary>
public sealed class SessionStartOrchestratorRegisterTests
{
    private const string OldSession = "s1";
    private const string NewSession = "s2";

    private static List<CallerRequest> Start(IReadOnlyList<CallerBox> boxes, string agentType, string? agentId = null)
    {
        var f = new OrchestrateCommandTests.Fixture();
        HoldFixture.Orchestrates(f.Rig, OldSession, "tm");
        var registered = new List<CallerRequest>();
        var plain = f.Services();
        var services = plain with { Advisory = plain.Advisory! with { Callers = new BindingAttestationTests.Callers(boxes, onRegister: registered.Add) } };
        var e = new JsonObject
        {
            ["hook_event_name"] = "SessionStart", ["session_id"] = NewSession, ["cwd"] = f.Rig.Sandbox.Root + "/proj", ["source"] = "startup", ["agent_type"] = agentType,
        };
        if (agentId is not null)
        {
            e["agent_id"] = agentId;
        }

        _ = f.Rig.Run("sessionstart", JsonText.Stringify(e, 0), services: () => services);
        return registered;
    }

    [Fact]
    public void ARestartedAgentOrchestratorRegistersItsNewSessionAtStart()
    {
        var registered = Assert.Single(Start(BindingAttestationTests.Orchestrator, "wrangl:orchestrator"));
        Assert.Equal(NewSession, registered.SessionId);
    }

    [Fact]
    public void NegativeTheOldSessionIsNeverRegisteredAgainSoAGoneOneStaysGone() =>
        Assert.DoesNotContain(Start(BindingAttestationTests.Orchestrator, "wrangl:orchestrator"), r => r.SessionId == OldSession);

    [Fact]
    public void NegativeAnAgentOrchestratorWithNoTeamBoundRegistersNothing() =>
        Assert.Empty(Start([], "wrangl:orchestrator"));

    [Fact]
    public void NegativeASubagentOfTheOrchestratorNeverRegistersAsIt() =>
        Assert.Empty(Start(BindingAttestationTests.Orchestrator, "wrangl:orchestrator", agentId: "sub-1"));

    [Fact]
    public void NegativeANonOrchestratorAgentRoleIsUnaffected() =>
        Assert.Empty(Start(BindingAttestationTests.Orchestrator, "wrangl:implementor"));
}
