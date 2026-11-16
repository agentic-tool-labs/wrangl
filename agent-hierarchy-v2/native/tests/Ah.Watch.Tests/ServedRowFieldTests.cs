using Ah.Core.Json;

namespace Ah.Watch.Tests;

/// <summary>The model of a member row, the work tree of a stream row and the mail record a mail edge or a detail mail item stands for.</summary>
public sealed class ServedRowFieldTests
{
    private static ModelSource Source() => new ModelSource { TeamName = "alpha" }.Member("impl").Member("rev", role: "reviewer", activity: "idle");

    private static JsonObject Summary(ModelSource src, string id) =>
        src.Build(src.Paths().Team)["children"].AsArray().Select(static c => c.AsObject()).Single(c => c.Str("id") == id).Obj("summary")!;

    private static string Ago(TimeSpan by) => DateTimeOffset.FromUnixTimeMilliseconds((long)ModelSource.Now).Subtract(by).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);

    private static JsonObject Mail(string id, string from, string created, bool acked = false) =>
        new() { ["id"] = id, ["kind"] = "request", ["from"] = from, ["to"] = "impl@alpha", ["created"] = created, ["acked"] = acked };

    private static List<JsonObject> MailEdges(ModelSource src) =>
        [.. src.Build("/graph" + src.Paths().Orch).Obj("body")!["edges"].AsArray().Select(static e => e.AsObject()).Where(static e => e.Str("kind") == "mail")];

    [Fact]
    public void AMemberRowServesItsModel()
    {
        var src = Source();

        Assert.Equal("sonnet", Summary(src, "impl").Str("model"));
        Assert.Equal("sonnet", Summary(src, "rev").Str("model"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NegativeAMemberWhoseRowNamesNoModelServesNullNotAMissingKey(bool empty)
    {
        var src = Source();
        src.TeamRows["impl"] = new JsonObject { ["name"] = "impl", ["role"] = "implementor", ["model"] = empty ? string.Empty : JsonValue.Null };

        var summary = Summary(src, "impl");

        Assert.True(summary.TryGetValue("model", out var model));
        Assert.True(model.IsNull);
        Assert.Equal("sonnet", Summary(src, "rev").Str("model"));
    }

    [Fact]
    public void AStreamRowServesTheNameOfItsWorkTree()
    {
        var src = Source();
        src.StreamPeers = ["impl"];
        src.StreamWorkTree = "/m/repo/.claude/worktrees/rc3-tui";

        Assert.Equal("rc3-tui", Summary(src, "tui").Str("work_tree"));
    }

    [Fact]
    public void AWorkTreePathWithATrailingSlashServesTheSameName()
    {
        var src = Source();
        src.StreamPeers = ["impl"];
        src.StreamWorkTree = "/m/repo/.claude/worktrees/rc3-tui/";

        Assert.Equal("rc3-tui", Summary(src, "tui").Str("work_tree"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/")]
    public void NegativeAStreamWithNoWorkTreeServesNullNotAMissingKey(string? path)
    {
        var src = Source();
        src.StreamPeers = ["impl"];
        src.StreamWorkTree = path;

        var summary = Summary(src, "tui");

        Assert.True(summary.TryGetValue("work_tree", out var tree));
        Assert.True(tree.IsNull);
    }

    [Fact]
    public void AMailEdgeNamesTheNewestRecordOfItsPairAndThePathOpensIt()
    {
        var src = Source();
        src.Boxes["impl"] = [Mail("m1", "orchestrator@alpha", Ago(TimeSpan.FromMinutes(20))), Mail("m2", "orchestrator@alpha", Ago(TimeSpan.FromMinutes(2)))];

        var edge = Assert.Single(MailEdges(src));
        var record = src.Paths().Member + "/mail/m2";

        Assert.Equal("m2", edge.Str("mail_id"));
        Assert.Equal(record, edge.Str("mail_path"));
        Assert.Equal(200, ViewBuilder.Build(src, "/detail" + record, ModelSource.Now).Status);
    }

    [Fact]
    public void AMailEdgeToTheOrchestratorNamesARecordOfTheTeamsOrchestratorBoxAndThePathOpensIt()
    {
        var src = Source();
        src.Boxes["orchestrator"] = [Mail("r1", "impl@alpha", Ago(TimeSpan.FromMinutes(3)))];

        var edge = Assert.Single(MailEdges(src));
        var record = src.Paths().Team + "/mail/r1";

        Assert.Equal("r1", edge.Str("mail_id"));
        Assert.Equal(record, edge.Str("mail_path"));
        Assert.Equal(200, ViewBuilder.Build(src, "/detail" + record, ModelSource.Now).Status);
    }

    [Fact]
    public void NegativeAPathOfARecordTheBoxDoesNotHoldDoesNotOpen()
    {
        var src = Source();
        src.Boxes["impl"] = [Mail("m1", "orchestrator@alpha", Ago(TimeSpan.FromMinutes(2)))];

        Assert.Equal(404, ViewBuilder.Build(src, "/detail" + src.Paths().Member + "/mail/m9", ModelSource.Now).Status);
    }

    [Fact]
    public void NegativeAnEdgeThatIsNotMailCarriesNoRecord()
    {
        var src = Source();
        src.StreamPeers = ["impl"];
        src.Boxes["impl"] = [Mail("m1", "orchestrator@alpha", Ago(TimeSpan.FromMinutes(2)))];

        var others = src.Build("/graph" + src.Paths().Orch).Obj("body")!["edges"].AsArray().Select(static e => e.AsObject()).Where(static e => e.Str("kind") != "mail").ToList();

        Assert.NotEmpty(others);
        Assert.All(others, static e =>
        {
            Assert.False(e.TryGetValue("mail_id", out _));
            Assert.False(e.TryGetValue("mail_path", out _));
        });
    }

    [Fact]
    public void ADetailMailItemNamesItsRecordByIdAndByAPathThatOpensIt()
    {
        var src = Source();
        src.Boxes["impl"] = [Mail("m1", "orchestrator@alpha", Ago(TimeSpan.FromMinutes(2)))];

        var fields = src.Build("/detail" + src.Paths().Member).Obj("body")!["fields"].AsArray().Select(static f => f.AsObject());
        var item = Assert.Single(fields.Single(static f => f.Str("key") == "mail")["value"].AsArray()).AsObject();

        Assert.Equal("m1", item.Str("id"));
        Assert.Equal(src.Paths().Member + "/mail/m1", item.Str("path"));
        Assert.Equal(200, ViewBuilder.Build(src, "/detail" + item.Str("path"), ModelSource.Now).Status);
    }

    private static List<JsonObject> GraphNodes(ModelSource src) =>
        [.. src.Build("/graph" + src.Paths().Orch).Obj("body")!["nodes"].AsArray().Select(static n => n.AsObject())];

    [Fact]
    public void AGraphMemberNodeServesItsModelAndNegativeTheOtherKindsDoNot()
    {
        var src = Source();
        src.StreamPeers = ["impl"];

        var nodes = GraphNodes(src);

        Assert.Equal(["sonnet", "sonnet"], nodes.Where(static n => n.Str("kind") == "member").Select(static n => n.Str("model")));
        Assert.All(nodes.Where(static n => n.Str("kind") != "member"), static n => Assert.False(n.TryGetValue("model", out _)));
    }

    [Fact]
    public void NegativeAGraphMemberWhoseRowNamesNoModelServesNullNotAMissingKey()
    {
        var src = Source();
        src.TeamRows["impl"] = new JsonObject { ["name"] = "impl", ["role"] = "implementor" };

        var member = GraphNodes(src).Single(static n => n.Str("label") == "impl");

        Assert.True(member.TryGetValue("model", out var model));
        Assert.True(model.IsNull);
    }

    [Fact]
    public void AGraphMemberNodeServesTheShortFormOfItsModelAndNullWhenItHasNone()
    {
        var src = Source();
        src.TeamRows["impl"] = new JsonObject { ["name"] = "impl", ["role"] = "implementor", ["model"] = "claude-sonnet-4-5-20250929" };
        src.TeamRows["rev"] = new JsonObject { ["name"] = "rev", ["role"] = "reviewer" };

        var members = GraphNodes(src).Where(static n => n.Str("kind") == "member").ToDictionary(static n => n.Str("label")!);

        Assert.Equal("claude-sonnet-4-5-20250929", members["impl"].Str("model"));
        Assert.Equal("sonnet", members["impl"].Str("model_short"));
        Assert.True(members["rev"].TryGetValue("model_short", out var none));
        Assert.True(none.IsNull);
    }

    [Fact]
    public void NegativeNoNodeOtherThanAMemberServesAShortModel()
    {
        var src = Source();
        src.Runs.Add(new SubagentRun("sess-impl", "a2", "started", "2026-06-02T09:10:00.000Z", null, "task-gopher:task-gopher"));

        Assert.All(GraphNodes(src).Where(static n => n.Str("kind") != "member"), static n => Assert.False(n.TryGetValue("model_short", out _)));
    }

    [Fact]
    public void TwoVersionsOfOneFamilyInOneGraphAreToldApartByTheirVersion()
    {
        var src = Source();
        src.TeamRows["impl"] = new JsonObject { ["name"] = "impl", ["role"] = "implementor", ["model"] = "claude-opus-4-1-20250805" };
        src.TeamRows["rev"] = new JsonObject { ["name"] = "rev", ["role"] = "reviewer", ["model"] = "claude-opus-4-6" };

        var members = GraphNodes(src).Where(static n => n.Str("kind") == "member").ToDictionary(static n => n.Str("label")!);

        Assert.Equal("opus 4.1", members["impl"].Str("model_short"));
        Assert.Equal("opus 4.6", members["rev"].Str("model_short"));
    }

    [Fact]
    public void NegativeTwoMembersOnTheSameIdKeepTheBareFamily()
    {
        var src = Source();
        src.TeamRows["impl"] = new JsonObject { ["name"] = "impl", ["role"] = "implementor", ["model"] = "claude-opus-4-1-20250805" };
        src.TeamRows["rev"] = new JsonObject { ["name"] = "rev", ["role"] = "reviewer", ["model"] = "claude-opus-4-1-20250805" };

        Assert.All(GraphNodes(src).Where(static n => n.Str("kind") == "member"), static n => Assert.Equal("opus", n.Str("model_short")));
    }

    [Theory]
    [InlineData("claude-sonnet-4-5-20250929", "sonnet")]
    [InlineData("claude-opus-4-1-20250805", "opus")]
    [InlineData("claude-sonnet-4-5", "sonnet")]
    [InlineData("claude-3-5-sonnet-20241022", "sonnet")]
    [InlineData("claude-3-opus", "opus")]
    [InlineData("opus", "opus")]
    [InlineData("inherit", "inherit")]
    [InlineData("opus[1m]", "opus[1m]")]
    [InlineData("claude-opus-4-1[1m]", "opus")]
    [InlineData("claude-sonnet-4-5-20250929[1m]", "sonnet")]
    [InlineData("gpt-5-codex", "gpt-5-codex")]
    [InlineData("gemini-2.5-pro-20250601", "gemini-2.5-pro")]
    [InlineData("o4-mini-2025-04-16", "o4-mini")]
    [InlineData("vendor-model-7-with-a-long-name", "vendor-model-7-…")]
    public void TheShortFormFollowsTheRuleForEachShapeOfId(string model, string expected)
    {
        Assert.Equal(expected, ModelShort.Of(model).Short);
    }

    [Fact]
    public void NegativeAnIdOfExactlySixteenCellsIsShownWhole()
    {
        Assert.Equal("abcdefgh-ijklm-7", ModelShort.Of("abcdefgh-ijklm-7").Short);
        Assert.Equal("abcdefgh-ijklm-…", ModelShort.Of("abcdefgh-ijklm-78").Short);
    }

    [Fact]
    public void ADocumentWithOneIdTwiceDisambiguatesNothingAndOneWithTwoVersionsDoes()
    {
        Assert.Equal(["opus", "opus"], ModelShort.Document(["claude-opus-4-1-20250805", "claude-opus-4-1-20250805"]));
        Assert.Equal(["opus 4.1", "opus 4.6", "sonnet"], ModelShort.Document(["claude-opus-4-1-20250805", "claude-opus-4-6", "claude-sonnet-4-5"]));
        Assert.Equal(["opus", "opus 4"], ModelShort.Document(["opus", "claude-opus-4-20250514"]));
    }

    [Fact]
    public void EveryMemberHasATeamEdgeFromTheOrchestratorAndNothingElseDoes()
    {
        var src = Source();
        src.Runs.Add(new SubagentRun("sess-impl", "a2", "started", "2026-06-02T09:10:00.000Z", null, "task-gopher:task-gopher"));

        var body = src.Build("/graph" + src.Paths().Orch).Obj("body")!;
        var nodes = body["nodes"].AsArray().Select(static n => n.AsObject()).ToList();
        var edges = body["edges"].AsArray().Select(static e => e.AsObject()).Where(static e => e.Str("kind") == "team").ToList();
        var members = nodes.Where(static n => n.Str("kind") == "member").Select(static n => n.Str("id")!).Order(StringComparer.Ordinal).ToList();

        Assert.Equal(members, edges.Select(static e => e.Str("to")!).Order(StringComparer.Ordinal));
        Assert.All(edges, e => Assert.Equal((src.Paths().Orch, "team:" + e.Str("to")), (e.Str("from"), e.Str("id"))));
        Assert.DoesNotContain(edges, e => nodes.Single(n => n.Str("id") == e.Str("to")).Str("kind") != "member");
    }

    [Fact]
    public void ASubagentNodeServesTheAgentTypeItsRunWasStartedAsVerbatim()
    {
        var src = Source();
        src.Runs.Add(new SubagentRun("sess-impl", "a2", "started", "2026-06-02T09:10:00.000Z", null, "task-gopher:task-gopher"));

        var sub = Assert.Single(GraphNodes(src), static n => n.Str("kind") == "subagent");

        Assert.Equal("task-gopher:task-gopher", sub.Str("agent_type"));
        Assert.Equal("a2", sub.Str("label"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NegativeASubagentRunWithNoAgentTypeServesNullNotAMissingKey(string? type)
    {
        var src = Source();
        src.Runs.Add(new SubagentRun("sess-impl", "a2", "started", "2026-06-02T09:10:00.000Z", null, type));

        var sub = Assert.Single(GraphNodes(src), static n => n.Str("kind") == "subagent");

        Assert.True(sub.TryGetValue("agent_type", out var served));
        Assert.True(served.IsNull);
    }

    [Fact]
    public void NegativeNoNodeOtherThanASubagentServesAnAgentType()
    {
        var src = Source();
        src.Runs.Add(new SubagentRun("sess-impl", "a2", "started", "2026-06-02T09:10:00.000Z", null, "task-gopher:task-gopher"));

        Assert.All(GraphNodes(src).Where(static n => n.Str("kind") != "subagent"), static n => Assert.False(n.TryGetValue("agent_type", out _)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TwoEntriesOfOneSecondGiveTheEdgeTheWaitingOnesRecordWhicheverComesFirstInTheBox(bool swap)
    {
        var src = Source();
        var same = Ago(TimeSpan.FromMinutes(2));
        var waiting = Mail("m1", "orchestrator@alpha", same);
        var acked = Mail("m2", "orchestrator@alpha", same, acked: true);
        src.Boxes["impl"] = swap ? [acked, waiting] : [waiting, acked];

        var edge = Assert.Single(MailEdges(src));

        Assert.Equal("m1", edge.Str("mail_id"));
        Assert.Equal(src.Paths().Member + "/mail/m1", edge.Str("mail_path"));
        Assert.True(edge["fresh"].AsBool());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TwoWaitingEntriesOfOneSecondBreakTheTieByIdWhicheverComesFirstInTheBox(bool swap)
    {
        var src = Source();
        var same = Ago(TimeSpan.FromMinutes(2));
        var first = Mail("m1", "orchestrator@alpha", same);
        var second = Mail("m2", "orchestrator@alpha", same);
        src.Boxes["impl"] = swap ? [second, first] : [first, second];

        var edge = Assert.Single(MailEdges(src));

        Assert.Equal("m2", edge.Str("mail_id"));
        Assert.True(edge["fresh"].AsBool());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NegativeTwoAckedEntriesOfOneSecondBreakTheTieByIdAndTheEdgeIsNotFresh(bool swap)
    {
        var src = Source();
        var same = Ago(TimeSpan.FromMinutes(2));
        var first = Mail("m1", "orchestrator@alpha", same, acked: true);
        var second = Mail("m2", "orchestrator@alpha", same, acked: true);
        src.Boxes["impl"] = swap ? [second, first] : [first, second];

        var edge = Assert.Single(MailEdges(src));

        Assert.Equal("m2", edge.Str("mail_id"));
        Assert.False(edge["fresh"].AsBool());
    }
}
