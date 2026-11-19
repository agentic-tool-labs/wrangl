using Ah.Core.Json;
using Ah.Watch.Model;

namespace Ah.Watch.Tests;

/// <summary>What the Flow reads: a member's column, the topology's lane bands and its briefs.</summary>
public sealed partial class TreeLanesTests
{
    private const string FlowDoc =
        """
        {"team_id":"t1","created":"2026-06-01T10:00:00Z","orchestrator":{"session_id":"so","pid":1,"pid_start":"o1"},
         "streams":{"tui":{"state":"open","sub_orchestrator":"so-a"},"old":{"state":"closed","sub_orchestrator":"so-b"}},
         "members":[
          {"name":"so-a","role":"sub-orchestrator"},
          {"name":"so-b","role":"sub-orchestrator"},
          {"name":"dev","role":"implementor","lead":{"team":"default","name":"so-a"}},
          {"name":"rev","role":"reviewer"},
          {"name":"far","role":"implementor","lead":{"team":"another","name":"so-a"}}]}
        """;

    private static JsonValue FlowTopology(ReadModel model) => JsonText.Parse(Body(model, $"topology:{D}/default"));

    private static JsonValue BandOf(ReadModel model, string member) => FlowTopology(model).Member("nodes")!.Value.Member("bands")!.Value.Member($"member:{D}/default/{member}") ?? JsonValue.Null;

    private static (ReadModel Model, Clock Clock) FlowModel(TreeReader reader)
    {
        reader.Teams[TeamPath] = FlowDoc;
        return Seeded(reader);
    }

    [Theory]
    [InlineData("orchestrator", 0)]
    [InlineData("sub-orchestrator", 0)]
    [InlineData("ultra-advisor", 1)]
    [InlineData("architect", 1)]
    [InlineData("docs-writer", 1)]
    [InlineData("implementor", 2)]
    [InlineData("ux-engineer", 2)]
    [InlineData("reviewer", 3)]
    [InlineData("qa", 3)]
    [InlineData("task-runner", 2)]
    [InlineData("", 2)]
    [InlineData(null, 2)]
    public void ARoleHasItsColumnAndNegativeOneTheTableDoesNotKnowGoesToImplement(string? role, int column) => Assert.Equal(column, TreeIds.FlowColumn(role));

    [Fact]
    public void AMemberSliceServesItsColumnAndTheOrchestratorLeads()
    {
        var (model, _) = FlowModel(Truth());
        using var owner = model;
        Assert.Contains("\"column\":3", Body(model, $"member:{D}/default/rev"), StringComparison.Ordinal);
        Assert.Contains("\"column\":0", Body(model, $"member:{D}/default/@orchestrator"), StringComparison.Ordinal);
    }

    // ah2-268 P1: the summary's member_states carries the column the member slice serves; negative: the implementor is not in Review's column.
    [Fact]
    public void EachMemberStateServesTheMemberSlicesColumnAndNegativeAnImplementorIsNotInReview()
    {
        var (model, _) = FlowModel(Truth());
        using var owner = model;
        var states = Summary(model).Member("member_states")!.Value;
        long ColumnOf(string member) => Num(states.Member($"member:{D}/default/{member}")!.Value, "column");
        Assert.Equal(3, ColumnOf("rev"));
        Assert.Equal(2, ColumnOf("dev"));
        Assert.NotEqual(3, ColumnOf("dev"));
        Assert.Equal(0, ColumnOf("@orchestrator"));
        foreach (var member in new[] { "rev", "dev", "so-a", "@orchestrator" })
        {
            Assert.Contains($"\"column\":{ColumnOf(member)}", Body(model, $"member:{D}/default/{member}"), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AnOpenStreamsSubOrchestratorLeadsABandWithTheMembersItLeadsAndNegativeAClosedStreamOrAnotherTeamsLeadDoesNot()
    {
        var (model, _) = FlowModel(Truth());
        using var owner = model;
        Assert.Equal($"member:{D}/default/so-a", BandOf(model, "so-a").AsString());
        Assert.Equal($"member:{D}/default/so-a", BandOf(model, "dev").AsString());
        Assert.True(BandOf(model, "so-b").IsNull, "its stream is closed");
        Assert.True(BandOf(model, "far").IsNull, "its lead is of a team that is no child of a stream here");
        Assert.True(BandOf(model, "rev").IsNull);
        Assert.True(BandOf(model, "@orchestrator").IsNull);
    }

    [Theory]
    [InlineData("open", true)]
    [InlineData("done", false)]
    public void AnOpenLanesChildTeamMembersSitInItsBandAndSayTheirTeamAndNegativeNotAClosedLanesOrItsOrchestrator(string state, bool served)
    {
        var reader = new TreeReader();
        reader.Teams[MgrPath] = MgrDoc(Streams(("s1", state)), "{\"name\":\"so-s1\",\"role\":\"sub-orchestrator\"}");
        reader.Teams[ChildPath] = ChildDoc("mgr", "s1").Replace("\"members\":[]", "\"members\":[{\"name\":\"a-impl\",\"role\":\"implementor\"}]", StringComparison.Ordinal);
        reader.KnownDirs.Add(Canon);
        var (model, _) = MakeLanes(reader);
        using var owner = model;

        var nodes = JsonText.Parse(Body(model, $"topology:{D}/mgr")).Member("nodes")!.Value;
        var kid = $"member:{D}/mgr-s1/a-impl";
        Assert.Equal(served ? $"member:{D}/mgr/so-s1" : null, nodes.Member("bands")!.Value.Member(kid)?.AsString());
        Assert.Equal(served ? $"team:{D}/mgr-s1" : null, nodes.Member("band_teams")!.Value.Member(kid)?.AsString());
        Assert.Null(nodes.Member("bands")!.Value.Member($"member:{D}/mgr-s1/@orchestrator"));
    }

    [Fact]
    public void AnOpenBriefIsServedSentForReviewOnlyFromAnImplementorToAReviewerAndNegativeNotBackOrWhenAnswered()
    {
        var reader = Truth();
        var (model, clock) = FlowModel(reader);
        using var owner = model;
        Send(model, clock, reader, "x1", Msg("reviewer", "implementor", "rev", "dev", "default"));
        Send(model, clock, reader, "x2", Msg("implementor", "reviewer", "dev", "rev", "default"));
        Send(model, clock, reader, "x3", Msg("reviewer", "implementor", "rev", "dev", "default", open: false));

        var briefs = FlowTopology(model).Member("edges")!.Value.Member("brief")!.Value.AsArray();
        Assert.Equal(2, briefs.Count);
        Assert.Equal("review", briefs.Single(static b => b.Member("id")!.Value.AsString().EndsWith("/x1", StringComparison.Ordinal)).Member("brief")!.Value.AsString());
        Assert.Equal("in_flight", briefs.Single(static b => b.Member("id")!.Value.AsString().EndsWith("/x2", StringComparison.Ordinal)).Member("brief")!.Value.AsString());
        AssertEqualsRebuild(model, reader);
    }

    [Fact]
    public async Task TheSpansListMarksTheExchangesTheLaneComposedAndNegativeNotOnALaneThatIsNoParty()
    {
        var reader = Truth();
        reader.Exchanges["a"] = Msg("developer", "tester", "dev", "qa", "default");
        var (model, _) = Seeded(reader);
        using var owner = model;
        using var store = TimelineDb.Open(TimelineDb.NewPath());
        var pager = new TimelineSpansPager(store, static () => 7, static () => 4_000_000_000_000, () => model.Store.Current, static (_, _) => null);
        var query = new Dictionary<string, string> { ["from_ms"] = "1", ["to_ms"] = "4000000000000" };
        var page = Assert.IsType<ListResult.Found>(await pager.PageAsync(new ListRequest($"list:timeline-spans:{D}/default", null, null, 50, query), CancellationToken.None)).Page.ToJson();

        JsonArray MarksOf(string name) => page.Member("rows")!.Value.AsArray().Single(r => r.Member("name")!.Value.AsString() == name).Member("marks")!.Value.AsArray();
        Assert.Equal("brief", Assert.Single(MarksOf("dev")).Member("kind")!.Value.AsString());
        Assert.Single(MarksOf("qa"));
        Assert.Empty(MarksOf(TreeIds.ReservedName));
    }

    [Fact]
    public void NegativeABriefWithAPartyThatIsNoMemberIsNotServed()
    {
        var reader = Truth();
        var (model, clock) = FlowModel(reader);
        using var owner = model;
        Send(model, clock, reader, "x1", Msg("reviewer", "implementor", "rev", "nobody", "default"));
        Assert.Empty(FlowTopology(model).Member("edges")!.Value.Member("brief")!.Value.AsArray());
    }
}
