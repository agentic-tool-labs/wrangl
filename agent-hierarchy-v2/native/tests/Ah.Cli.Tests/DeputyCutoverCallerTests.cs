using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Core.Json;
using Ah.Mesh;

namespace Ah.Cli.Tests;

/// <summary>C1: the live deputy on duty is classified from the record's duty and its member row, and may decide only as surrogate.</summary>
public sealed class DeputyCutoverCallerTests
{
    private const string TeamFile = "/h/.claude/agent-hierarchy/teams/tm.json";

    private sealed class Facts(JsonValue record) : ICutoverFacts
    {
        public string? TeamFileEntry(string sessionId, string address) => TeamFile;

        public JsonValue ReadTeamFile(string path) => record;

        public bool ProcessIs(int pid, string pidStart) => pidStart == "start-" + pid;

        public bool IsMemberProcess(int pid) => pid == 20;

        public bool ManagerNamesLane(string hierDir, string team) => false;
    }

    private static JsonValue Team(string deputy) => JsonText.Parse(
        "{\"orchestrator\":{\"pid\":10,\"pid_start\":\"start-10\"},\"duty\":{\"deputy\":\"" + deputy + "\"},"
        + "\"members\":[{\"name\":\"tm-dep\",\"pid\":20,\"pid_start\":\"start-20\"},{\"name\":\"tm-imp\",\"pid\":30,\"pid_start\":\"start-30\"}]}");

    private static CutoverCaller Classify(int pid, string deputy = "tm-dep") =>
        CutoverCallers.Classify(new CallerPrincipal(PrincipalKind.Session, [new SessionRegistration("s", pid, "start-" + pid, ["orchestrator@tm"])]), new Facts(Team(deputy)));

    [Fact]
    public void TheLiveDeputyIsTheDeputyChiefOfItsTeamAndDecidesThere()
    {
        var caller = Classify(20);

        Assert.Equal(CutoverCallerKind.DeputyChief, caller.Kind);
        Assert.False(caller.MayChangeAuthority);
        Assert.Null(DecisionProof.Judge(caller, "tm", memberOfAnchorTeam: true).Code);
        Assert.Equal(DecisionProof.NotTheRunOrchestrator, DecisionProof.Judge(caller, "other", memberOfAnchorTeam: false).Code);
    }

    [Fact]
    public void NegativeAnotherMemberOrADutyNamingAnotherRowIsAMember()
    {
        Assert.Equal(CutoverCallerKind.Member, Classify(30).Kind);
        Assert.Equal(CutoverCallerKind.Member, Classify(20, deputy: "tm-imp").Kind);
    }

    [Fact]
    public void ASessionWithNoAddressThatIsNoMemberProcessIsTheTopOrchestrator()
    {
        CutoverCaller Of(int pid, params string[] addresses) =>
            CutoverCallers.Classify(new CallerPrincipal(PrincipalKind.Session, [new SessionRegistration("s", pid, "start-" + pid, addresses)]), new Facts(Team("tm-dep")));

        Assert.True(Of(99).MayChangeAuthority);
        Assert.Equal(CutoverCallerKind.TopOrchestrator, Of(99).Kind);
        Assert.Equal(CutoverCallerKind.Member, Of(20).Kind); // a member process with no address stays a member
        Assert.Equal(CutoverCallerKind.Member, Of(99, "tm-imp@tm").Kind); // a member address stays a member
    }
}
