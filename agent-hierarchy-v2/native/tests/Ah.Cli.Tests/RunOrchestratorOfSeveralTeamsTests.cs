using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Core.Json;
using Ah.Mesh;

namespace Ah.Cli.Tests;

/// <summary>b467: a restarted Orchestrator holding an older team as well as the run's is judged on the run's team.</summary>
public sealed class RunOrchestratorOfSeveralTeamsTests
{
    private sealed class Facts(Dictionary<string, int> orchestratorPidOf) : ICutoverFacts
    {
        public string? TeamFileEntry(string sessionId, string address) => "/h/.claude/ah2/teams/" + address["orchestrator@".Length..] + ".json";

        public JsonValue ReadTeamFile(string path) =>
            JsonText.Parse("{\"orchestrator\":{\"pid\":" + orchestratorPidOf[Path.GetFileNameWithoutExtension(path)] + ",\"pid_start\":\"start-10\"},\"members\":[]}");

        public bool ProcessIs(int pid, string pidStart) => pidStart == "start-" + pid;

        public bool IsMemberProcess(int pid) => false;

        public bool ManagerNamesLane(string hierDir, string team) => false;
    }

    // The older team comes first, as the restarted session's registration listed it.
    private static string? Verdict(int rc4Orchestrator, params string[] addresses) =>
        DecisionProof.Judge(
            CutoverCallers.Classify(
                new CallerPrincipal(PrincipalKind.Session, [new SessionRegistration("s2", 10, "start-10", addresses)]),
                new Facts(new() { ["rc3"] = 10, ["rc4"] = rc4Orchestrator }),
                "rc4"),
            "rc4",
            memberOfAnchorTeam: false).Code;

    [Fact]
    public void TheRunsOrchestratorAlsoHoldingAnOlderTeamMayAddDecisions() =>
        Assert.Null(Verdict(10, "orchestrator@rc3", "orchestrator@rc4"));

    [Fact]
    public void NegativeAnOrchestratorProvenOnlyForAnotherTeamIsStillRefused() =>
        Assert.Equal(DecisionProof.NotTheRunOrchestrator, Verdict(99, "orchestrator@rc3", "orchestrator@rc4"));

    [Fact]
    public void NegativeAMemberSessionIsStillRefused() =>
        Assert.Equal("member", Verdict(10, "rc4-imp@rc4"));
}
