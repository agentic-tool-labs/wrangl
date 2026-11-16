using Ah.Core.Json;

namespace Ah.Watch.Tests;

/// <summary>D9: the tree document serves <c>live_path</c> on a member whose open lane's child team is held by its own session, and on no one else.</summary>
public sealed class LivePathFieldTests
{
    private const string Own = "sess-so";

    private static JsonObject Row(ModelSource s, string name) =>
        s.Build(s.Paths().Team)["children"].AsArray().Select(static c => c.AsObject()).Single(c => c["id"].AsString() == name);

    private static JsonObject Identity(string? session, long? pid = null, string? start = null)
    {
        var who = new JsonObject();
        if (session is not null)
        {
            who["session_id"] = session;
        }

        if (pid is not null)
        {
            who["pid"] = pid.Value;
        }

        if (start is not null)
        {
            who["pid_start"] = start;
        }

        return who;
    }

    /// <summary>A team whose member <c>so</c> is in stream <c>lane</c>, with the stream's state, who it names, and who holds its child team.</summary>
    private static ModelSource Lane(JsonObject so, JsonObject? childHolder, string state = "open", string named = "so")
    {
        var s = new ModelSource { TeamName = "t1" };
        _ = s.Member("so", "sub-orchestrator").Member("plain");
        foreach (var key in so.Keys)
        {
            s.TeamRows["so"][key] = so[key];
        }

        s.Status["so"]["stream"] = "lane";
        s.Status["plain"]["stream"] = "lane";
        s.LaneStreams = new JsonObject { ["lane"] = new JsonObject { ["state"] = state, ["sub_orchestrator"] = named } };
        if (childHolder is not null)
        {
            s.LaneChildren["t1-lane"] = childHolder;
        }

        return s;
    }

    [Fact]
    public void ASubOrchestratorHoldingItsChildTeamIsServedThatTeamsOrchestrator()
    {
        var s = Lane(Identity(Own), Identity(Own));
        var path = Row(s, "so")["live_path"].AsString();
        Assert.StartsWith("/" + Model.TreeIds.NodePathRoot + "/", path, StringComparison.Ordinal);
        Assert.Contains("/t1-lane/m/", path, StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeAChildTeamHeldByAnotherSessionIsServedNone()
    {
        Assert.False(Row(Lane(Identity(Own), Identity("sess-manager")), "so").ContainsKey("live_path"));
    }

    [Fact]
    public void NegativeAClosedLaneIsServedNone()
    {
        Assert.False(Row(Lane(Identity(Own), Identity(Own), state: "done"), "so").ContainsKey("live_path"));
    }

    [Fact]
    public void NegativeALaneWithNoChildTeamIsServedNone()
    {
        Assert.False(Row(Lane(Identity(Own), null), "so").ContainsKey("live_path"));
    }

    [Fact]
    public void NegativeAMemberTheStreamDoesNotNameIsServedNone()
    {
        var s = Lane(Identity(Own), Identity(Own));
        s.TeamRows["plain"]["session_id"] = Own;
        Assert.False(Row(s, "plain").ContainsKey("live_path"));
    }

    [Fact]
    public void NegativeASubOrchestratorNotNamedByTheStreamIsServedNone()
    {
        Assert.False(Row(Lane(Identity(Own), Identity(Own), named: "plain"), "so").ContainsKey("live_path"));
    }

    [Theory]
    [InlineData(null, 7L, "s", null, 7L, "s", true)]
    [InlineData(null, 7L, "s", null, 7L, "other", false)]
    [InlineData(null, 7L, null, null, 7L, "s", false)]
    [InlineData(null, 7L, "s", null, 8L, "s", false)]
    [InlineData(null, null, null, null, null, null, false)]
    [InlineData("a", 7L, "s", "b", 7L, "s", false)]
    [InlineData("a", 7L, "s", "a", 8L, "other", true)]
    public void SessionsDecideWhenBothHaveOneElseTheProcessMustBeProved(string? session, long? pid, string? start, string? childSession, long? childPid, string? childStart, bool served)
    {
        var s = Lane(Identity(session, pid, start), Identity(childSession, childPid, childStart));
        Assert.Equal(served, Row(s, "so").ContainsKey("live_path"));
    }
}
