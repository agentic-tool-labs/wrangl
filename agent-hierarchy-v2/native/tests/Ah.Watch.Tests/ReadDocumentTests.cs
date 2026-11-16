using Ah.Core.Json;

namespace Ah.Watch.Tests;

/// <summary>The status, stream-status and inbox documents: what the verbs of the same names show, served as data and never run.</summary>
public sealed class ReadDocumentTests
{
    private static ModelSource Source()
    {
        var src = new ModelSource { TeamName = "alpha" }.Member("impl").Member("rev", role: "reviewer", activity: "idle");
        src.StreamPeers = ["impl"];
        src.Boxes["impl"] =
        [
            new JsonObject { ["id"] = "m1", ["kind"] = "request", ["from"] = "orchestrator@alpha", ["to"] = "impl@alpha", ["created"] = "2027-01-15T07:58:00.000Z", ["acked"] = false, ["changed"] = false },
        ];
        src.Boxes["orchestrator"] =
        [
            new JsonObject { ["id"] = "m2", ["kind"] = "report", ["from"] = "impl@alpha", ["to"] = "orchestrator@alpha", ["created"] = "2027-01-15T07:59:00.000Z", ["acked"] = true, ["changed"] = false },
        ];
        return src;
    }

    private static string StreamPath(ModelSource src) => src.Paths().Team + "/s/tui";

    [Fact]
    public void TheStatusOfATeamIsItsObjectInTheStatusDocument()
    {
        var src = Source();

        var body = src.Build("/status" + src.Paths().Team).Obj("body")!;

        Assert.Equal("alpha", body.Str("team"));
        Assert.Equal(["impl", "rev"], body.Obj("status")!["members"].AsArray().Select(static m => m.AsObject().Str("name")).Order());
    }

    [Fact]
    public void TheStatusOfAnOrchestratorListsItsTeams()
    {
        var src = Source();

        var teams = src.Build("/status" + src.Paths().Orch).Obj("body")!["teams"].AsArray();

        Assert.Equal("alpha", Assert.Single(teams).AsObject().Str("team"));
    }

    [Fact]
    public void NegativeAMemberHasNoStatusAndReportsItsTeamAsNearest()
    {
        var src = Source();
        var paths = src.Paths();

        var answer = ViewBuilder.Build(src, "/status" + paths.Member, ModelSource.Now);

        Assert.Equal(404, answer.Status);
        Assert.Equal(paths.Team, answer.Body.Str("nearest"));
    }

    [Fact]
    public void TheStreamStatusOfATeamHasTheVerbsKeysAndEveryStream()
    {
        var src = Source();

        var body = src.Build("/stream-status" + src.Paths().Team).Obj("body")!;

        Assert.Equal(["team", "transport", "streams"], body.Keys);
        var stream = Assert.Single(body["streams"].AsArray()).AsObject();
        Assert.Equal("tui", stream.Str("name"));
        Assert.Equal("ah/tui", stream.Str("branch"));
        Assert.Equal("impl", stream["peers"].AsArray().Single().AsObject().Str("name"));
    }

    [Fact]
    public void TheStreamStatusOfAStreamNodeIsThatStreamAloneWhileTheTeamsHasBoth()
    {
        var src = Source();
        src.SecondStream = true;

        var alone = src.Build("/stream-status" + StreamPath(src)).Obj("body")!;
        var team = src.Build("/stream-status" + src.Paths().Team).Obj("body")!;

        Assert.Equal("tui", Assert.Single(alone["streams"].AsArray()).AsObject().Str("name"));
        Assert.Equal(["ops", "tui"], team["streams"].AsArray().Select(static s => s.AsObject().Str("name")).Order());
    }

    [Fact]
    public void NegativeANodeWithNoStreamsHasAnEmptyStreamListAndNoDegradedKey()
    {
        var src = Source();
        src.StreamPeers = null;

        var body = src.Build("/stream-status" + src.Paths().Team).Obj("body")!;

        Assert.Empty(body["streams"].AsArray());
        Assert.False(body.ContainsKey("degraded"));
    }

    [Fact]
    public void ARealStreamNodeViewStillCarriesTheBodyTheSharedBuilderMakes()
    {
        var src = Source();

        var body = src.Build(StreamPath(src)).Obj("body")!;

        Assert.Equal("ah/tui", body.Str("branch"));
        Assert.Equal("ok", body.Str("glyph_state"));
    }

    [Fact]
    public void TheInboxOfAMemberIsItsBoxUnderItsAddress()
    {
        var src = Source();

        var box = Assert.Single(src.Build("/inbox" + src.Paths().Member).Obj("body")!["boxes"].AsArray()).AsObject();

        Assert.Equal("impl@alpha", box.Str("address"));
        Assert.Equal("m1", Assert.Single(box["entries"].AsArray()).AsObject().Str("id"));
    }

    [Fact]
    public void TheInboxListsEntriesFromTheIndexAloneWithNoChangedOrSpecAndNeverReadsARequestFile()
    {
        var src = Source();

        var entry = Assert.Single(Assert.Single(src.Build("/inbox" + src.Paths().Member).Obj("body")!["boxes"].AsArray()).AsObject()["entries"].AsArray()).AsObject();

        Assert.Equal(["id", "kind", "from", "to", "created", "acked", "ref"], entry.Keys);
        Assert.Equal(0, src.FullBoxReads);
    }

    [Fact]
    public void TheInboxOfAnOrchestratorIsItsOwnBoxOfEachTeam()
    {
        var src = Source();

        var box = Assert.Single(src.Build("/inbox" + src.Paths().Orch).Obj("body")!["boxes"].AsArray()).AsObject();

        Assert.Equal("orchestrator@alpha", box.Str("address"));
        Assert.Equal("m2", Assert.Single(box["entries"].AsArray()).AsObject().Str("id"));
    }

    [Fact]
    public void AMemberSlicesNodePathOpensTheNodeTheTreeServesAndTheOrchestratorsOpensItsNode()
    {
        var src = new ModelSource().Member("impl");
        var dir = Model.ActivityLane.DirId(ModelSource.Dir);

        var member = src.Build(Model.TreeIds.NodePath(dir, "default", "impl"));
        Assert.Equal("member", member.Str("kind"));
        Assert.Equal(src.Paths().Member, member.Str("path"));

        var orchestrator = src.Build(Model.TreeIds.NodePath(dir, "default", Model.TreeIds.Orchestrator));
        Assert.Equal("orchestrator", orchestrator.Str("kind"));
        Assert.Equal(src.Paths().Orch, orchestrator.Str("path"));
    }

    [Theory]
    [InlineData("nosuchdir", "default", "impl")]
    [InlineData(null, "nosuchteam", "impl")]
    [InlineData(null, "default", "nosuchmember")]
    public void NegativeANodePathOfNothingServedIsNotFound(string? dirId, string team, string member)
    {
        var src = new ModelSource().Member("impl");

        var view = src.Build(Model.TreeIds.NodePath(dirId ?? Model.ActivityLane.DirId(ModelSource.Dir), team, member));

        Assert.Equal("not_found", view.Str("error"));
    }

    [Fact]
    public void NegativeTheDefaultTeamsAddressesHaveNoTeamPart()
    {
        var src = new ModelSource().Member("impl");

        var box = Assert.Single(src.Build("/inbox" + src.Paths().Member).Obj("body")!["boxes"].AsArray()).AsObject();

        Assert.Equal("impl", box.Str("address"));
    }

    [Fact]
    public void NegativeATeamHasNoInboxAndAMemberHasNoStreamStatus()
    {
        var src = Source();
        var paths = src.Paths();

        Assert.Equal(404, ViewBuilder.Build(src, "/inbox" + paths.Team, ModelSource.Now).Status);
        Assert.Equal(404, ViewBuilder.Build(src, "/stream-status" + paths.Member, ModelSource.Now).Status);
    }

    [Fact]
    public void TheReadDocumentsMatchTheirGoldens()
    {
        var src = Source();
        var paths = src.Paths();

        ModelGolden.Check(src.Build("/status" + paths.Team), "status-team");
        ModelGolden.Check(src.Build("/stream-status" + paths.Team), "stream-status-team");
        ModelGolden.Check(src.Build("/inbox" + paths.Member), "inbox-member");
    }
}
