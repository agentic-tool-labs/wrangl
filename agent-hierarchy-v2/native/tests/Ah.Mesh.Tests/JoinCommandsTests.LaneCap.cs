using Ah.Core.Json;

namespace Ah.Mesh.Tests;

/// <summary>An accept into a team that is a lane's child is refused at the lane's member cap, and the request stays open.</summary>
public sealed partial class JoinCommandsTests
{
    private static readonly string[] LaneDeciders = ["orchestrator@tm"];

    private static async Task<string> OpenLaneJoinAsync(Rig rig, IJoinCommands joins)
    {
        rig.Fx.Clock.UtcNow = T0;
        var opened = await joins.OpenAsync(
            rig.Context,
            new JsonObject { ["team"] = "tm", ["harness"] = "my-harness", ["session_id"] = "s1", ["why"] = "w" },
            CancellationToken.None,
            T0.ToUnixTimeMilliseconds());
        return opened.Value!["join_id"].AsString();
    }

    private static Task<JoinCommandOutcome>AcceptLaneJoinAsync(Rig rig, IJoinCommands joins, string id)
    {
        var at = T0.AddSeconds(10);
        rig.Fx.Clock.UtcNow = at;
        var options = new Dictionary<string, string> { ["basis"] = "user-approved", ["why"] = "ok", ["name"] = "helper2", ["role"] = "reviewer" };
        var deciders = LaneDeciders.Select(a => new JoinDecider(a, "sess1")).ToList();
        return joins.CommandAsync(new JoinCommandRequest(rig.Context, ["accept", id], options, deciders, at.ToUnixTimeMilliseconds()), CancellationToken.None);
    }

    private static int MemberRows(Rig rig) => JsonText.Parse(File.ReadAllText(rig.TeamFile)).AsObject()["members"].AsArray().Count;

    [Fact]
    public async Task AnAcceptIntoALaneAtItsCapIsRefusedWritesNoRowAndLeavesTheRequestOpenForALaterAccept()
    {
        var rig = new Rig();
        var joins = rig.Joins(new Probe(false, null));
        var id = await OpenLaneJoinAsync(rig, joins);
        var before = MemberRows(rig);
        rig.Recorder.LaneRefusal = "lane-members-full: 4/4; dismiss one or ask the top Orchestrator to raise the cap";

        var refused = await AcceptLaneJoinAsync(rig, joins, id);

        Assert.False(refused.Ok);
        Assert.Equal("lane-members-full", refused.Failure!.Code);
        Assert.Contains("4/4", refused.Failure.Message, StringComparison.Ordinal);
        Assert.Equal(before, MemberRows(rig));
        Assert.DoesNotContain(rig.Recorder.Created, c => c["kind"].AsString() == "member");

        rig.Recorder.LaneRefusal = null;
        var later = await AcceptLaneJoinAsync(rig, joins, id);

        Assert.True(later.Ok, later.Failure?.Message);
        Assert.Equal(before + 1, MemberRows(rig));
    }

    [Fact]
    public async Task AnAcceptIntoATeamThatIsNotAtALaneCapAddsTheRow()
    {
        var rig = new Rig();
        var joins = rig.Joins(new Probe(false, null));
        var id = await OpenLaneJoinAsync(rig, joins);
        var before = MemberRows(rig);

        var accepted = await AcceptLaneJoinAsync(rig, joins, id);

        Assert.True(accepted.Ok, accepted.Failure?.Message);
        Assert.Equal(before + 1, MemberRows(rig));
    }
}
