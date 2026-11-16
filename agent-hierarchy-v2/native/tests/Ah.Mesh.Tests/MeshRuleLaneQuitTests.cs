using System.Runtime.Versioning;
using Ah.Core.Json;
using Ah.TestKit;

namespace Ah.Mesh.Tests;

/// <summary>Rule 7's quit exception: a hold-exempt send from a team's Orchestrator reaches a member of an open, confirmed lane of that team, and nothing else.</summary>
[UnsupportedOSPlatform("windows")]
public sealed class MeshRuleLaneQuitTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-03-01T12:00:00.000Z", System.Globalization.CultureInfo.InvariantCulture);

    private static readonly Dictionary<string, string> Teams = new(StringComparer.Ordinal)
    {
        ["tm"] = """{"streams":{"ws1":{"sub_orchestrator":"so1","state":"open"},"ws2":{"sub_orchestrator":"so2","state":"done"}},"members":[{"name":"so1","role":"sub-orchestrator"},{"name":"so2","role":"sub-orchestrator"}]}""",
        ["tm-ws1"] = """{"parent":{"team":"tm","stream":"ws1"},"streams":{"ws9":{"sub_orchestrator":"so9","state":"open"}},"members":[{"name":"l1","role":"implementor"},{"name":"so9","role":"sub-orchestrator"}]}""",
        ["tm-ws2"] = """{"parent":{"team":"tm","stream":"ws2"},"members":[{"name":"l2","role":"implementor"}]}""",
        ["tm-ws1-ws9"] = """{"parent":{"team":"tm-ws1","stream":"ws9"},"members":[{"name":"g1","role":"implementor"}]}""",
        ["other"] = """{"members":[{"name":"x1","role":"implementor"}]}""",
    };

    private sealed class NoFacts : IMeshFacts
    {
        public bool RelayLive(string entryId, string address) => false;

        public bool RepliesToBriefer(MeshParty sender, MeshParty recipient, string? msgKind) => false;

        public bool ChainReachesTask(string messagePath, string taskId, string dir, string team) => false;
    }

    [Theory]
    [InlineData("l1", "tm-ws1", true, true)] // the quit's brief to a member of an open lane of the sender's team
    [InlineData("l1", "tm-ws1", false, false)] // (d) the same send outside a quit
    [InlineData("x1", "other", true, false)] // (a) a team that is no lane of the sender's
    [InlineData("l2", "tm-ws2", true, false)] // (b) a lane whose stream is done
    [InlineData("g1", "tm-ws1-ws9", true, false)] // (c) a lane of a lane: its manager is tm-ws1, not the sender's team
    public void AHoldExemptSendFromTheTopReachesOnlyAnOpenLaneOfItsOwnTeam(string member, string team, bool holdExempt, bool allowed)
    {
        var sandbox = UnitSandbox.Create();
        var fx = MeshFixture.Create(new Dictionary<string, string> { ["HOME"] = sandbox.Home, ["AH_HOME"] = sandbox.Root + "/ah" }, Now, sandbox.Root);
        var sender = new MeshParty("orchestrator", "orchestrator@tm", "orchestrator", "tm");
        var recipient = new MeshParty("member", $"{member}@{team}", member, team, "implementor");

        var verdict = fx.Rule.Decide(new MeshDecideRequest(
            Now.ToUnixTimeMilliseconds(), sender, recipient, name => Teams.TryGetValue(name, out var data) ? new MeshTeam(JsonText.Parse(data), null) : null, null, "brief", new NoFacts(), HoldExempt: holdExempt));

        Assert.Equal(allowed, verdict.Allow);
        Assert.Equal("7", verdict.Rule);
        Assert.Equal(allowed ? null : "outside_team", verdict.Code);
    }
}
