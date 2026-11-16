using Ah.Core.Json;

namespace Ah.Mesh.Tests;

public sealed partial class JoinCommandsTests
{
    private static string PoolOf(string tree) => Path.Combine(tree, ".claude", "ah2");

    private static string Tree(Rig rig, string name)
    {
        var tree = Path.Combine(Path.GetDirectoryName(rig.Project)!, name);
        Directory.CreateDirectory(Path.Combine(tree, ".git"));
        Directory.CreateDirectory(Path.Combine(PoolOf(tree), "msgs"));
        return tree;
    }

    private static void GiveMemberTree(Rig rig, string tree)
    {
        var team = JsonText.Parse(File.ReadAllText(rig.TeamFile)).AsObject();
        team["members"].AsArray()[0].AsObject()["expected_root"] = tree;
        File.WriteAllText(rig.TeamFile, JsonText.Stringify(team, 0));
    }

    /// <summary>Moves the join request the rig's hierarchy holds into the pool of <paramref name="tree"/>.</summary>
    private static void MoveJoinRequest(Rig rig, string tree)
    {
        var request = Directory.GetFiles(Path.Combine(rig.Hierarchy, "msgs"), "*--request.md").Single(f => File.ReadAllText(f).Contains("subject: join", StringComparison.Ordinal));
        File.Move(request, Path.Combine(PoolOf(tree), "msgs", Path.GetFileName(request)));
    }

    private static JsonObject JoinBody(string session) =>
        new() { ["team"] = "tm", ["harness"] = "my-harness", ["session_id"] = session, ["why"] = "because" };

    [Fact]
    public async Task AJoinRequestFiledInAMembersWorkTreePoolIsPending()
    {
        var rig = new Rig();
        var joins = rig.Joins(new Probe(false, null));
        var tree = Tree(rig, "wt1");
        GiveMemberTree(rig, tree);
        rig.Fx.Clock.UtcNow = T0;
        _ = await joins.OpenAsync(rig.Context, JoinBody("s1"), CancellationToken.None, T0.ToUnixTimeMilliseconds());
        MoveJoinRequest(rig, tree);

        Assert.Single(joins.Pending(rig.Hierarchy, "tm", T0.AddSeconds(60).ToUnixTimeMilliseconds()));
    }

    [Fact]
    public async Task AJoinRequestFiledInATreeOfNoTeamMemberIsNotPending()
    {
        var rig = new Rig();
        var joins = rig.Joins(new Probe(false, null));
        GiveMemberTree(rig, Tree(rig, "wt1"));
        rig.Fx.Clock.UtcNow = T0;
        _ = await joins.OpenAsync(rig.Context, JoinBody("s1"), CancellationToken.None, T0.ToUnixTimeMilliseconds());
        MoveJoinRequest(rig, Tree(rig, "stranger"));

        Assert.Empty(joins.Pending(rig.Hierarchy, "tm", T0.AddSeconds(60).ToUnixTimeMilliseconds()));
    }

    [Fact]
    public async Task ASecondOpenFromTheSameSessionFindsTheRequestFiledInAMembersWorkTreePool()
    {
        var rig = new Rig();
        var joins = rig.Joins(new Probe(false, null));
        var tree = Tree(rig, "wt1");
        GiveMemberTree(rig, tree);
        rig.Fx.Clock.UtcNow = T0;
        var first = await joins.OpenAsync(rig.Context, JoinBody("s1"), CancellationToken.None, T0.ToUnixTimeMilliseconds());
        MoveJoinRequest(rig, tree);
        rig.Fx.Clock.UtcNow = T0.AddSeconds(5);

        var second = await joins.OpenAsync(rig.Context, JoinBody("s1"), CancellationToken.None, T0.AddSeconds(5).ToUnixTimeMilliseconds());

        Assert.Equal(first.Value!["join_id"].AsString(), second.Value!["join_id"].AsString());
        Assert.True(second.Value["existing"].AsBool());
    }

    [Fact]
    public async Task ASecondOpenFromTheSameSessionDoesNotFindARequestFiledInATreeOfNoTeamMember()
    {
        var rig = new Rig();
        var joins = rig.Joins(new Probe(false, null));
        GiveMemberTree(rig, Tree(rig, "wt1"));
        rig.Fx.Clock.UtcNow = T0;
        var first = await joins.OpenAsync(rig.Context, JoinBody("s1"), CancellationToken.None, T0.ToUnixTimeMilliseconds());
        MoveJoinRequest(rig, Tree(rig, "stranger"));
        rig.Fx.Clock.UtcNow = T0.AddSeconds(5);

        var second = await joins.OpenAsync(rig.Context, JoinBody("s1"), CancellationToken.None, T0.AddSeconds(5).ToUnixTimeMilliseconds());

        Assert.NotEqual(first.Value!["join_id"].AsString(), second.Value!["join_id"].AsString());
    }
}
