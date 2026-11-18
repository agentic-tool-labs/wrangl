using Ah.Core.Json;

namespace Ah.Watch.Tests;

/// <summary>A subagent run is a node under its member, so the id a graph names resolves to a detail and a menu.</summary>
public sealed class SubagentNodeDocumentTests
{
    private static ModelSource Source()
    {
        var src = new ModelSource { TeamName = "alpha" }.Member("impl").Member("rev");
        src.Runs.Add(new SubagentRun("sess-impl", "a1", "stopped", "2026-06-02T09:00:00.000Z", "2026-06-02T09:05:00.000Z"));
        src.Runs.Add(new SubagentRun("sess-impl", "a2", "started", "2026-06-02T09:10:00.000Z", null));
        src.Runs.Add(new SubagentRun("sess-rev", "r1", "started", "2026-06-02T09:11:00.000Z", null));
        return src;
    }

    private static JsonObject Field(JsonObject doc, string key) =>
        doc.Obj("body")!["fields"].AsArray().Select(static f => f.AsObject()).Single(f => f.Str("key") == key);

    [Fact]
    public void TheNodeTheGraphNamesServesItsDetailWithItsRunAndItsParent()
    {
        var src = Source();
        var member = src.Paths().Member;
        var graphId = src.Build("/graph" + src.Paths().Orch).Obj("body")!["nodes"].AsArray().Select(static n => n.AsObject()).Single(static n => n.Str("kind") == "subagent" && n.Str("label") == "a2").Str("id")!;

        var doc = src.Build("/detail" + graphId);

        Assert.Equal(member + "/sub/a2", graphId);
        Assert.Equal("detail", doc.Str("kind"));
        Assert.Equal(["id", "status", "started_at", "ended_at", "parent"], doc.Obj("body")!["fields"].AsArray().Select(static f => f.AsObject().Str("key")));
        Assert.Equal("started", Field(doc, "status")["value"].AsString());
        Assert.Equal("2026-06-02T09:10:00.000Z", Field(doc, "started_at")["value"].AsString());
        Assert.False(Field(doc, "ended_at")["present"].AsBool());
        Assert.Equal(member, Field(doc, "parent")["value"].AsString());
        Assert.Equal("inflight records", Field(doc, "status").Str("source"));
    }

    [Fact]
    public void AStoppedRunStillServesAndSaysWhenItEnded()
    {
        var src = Source();

        var doc = src.Build("/detail" + src.Paths().Member + "/sub/a1");

        Assert.Equal("stopped", Field(doc, "status")["value"].AsString());
        Assert.Equal("2026-06-02T09:05:00.000Z", Field(doc, "ended_at")["value"].AsString());
    }

    [Fact]
    public void TheMenuOfASubagentCopiesItsIdAndHighlightsItsEdges()
    {
        var src = Source();
        var path = src.Paths().Member + "/sub/a2";

        var actions = src.Build("/actions" + path).Obj("body")!["actions"].AsArray().Select(static a => a.AsObject()).ToList();

        Assert.Equal(["subagent.copy_id", "node.highlight_edges"], actions.Select(static a => a.Str("id")));
        Assert.Equal("a2", actions[0].Obj("invoke")!.Str("copy"));
        Assert.Equal(path, actions[1].Obj("invoke")!.Str("target"));
        Assert.All(actions, static a => Assert.False(string.IsNullOrWhiteSpace(a.Str("help"))));
    }

    [Fact]
    public void TheRunsAreReadOncePerBuildWhateverTheNumberOfMembersAndNodes()
    {
        var src = Source();
        var member = src.Paths().Member;
        var before = src.RunReads;

        _ = src.Build("/graph" + src.Paths().Orch);
        Assert.Equal(1, src.RunReads - before);

        before = src.RunReads;
        _ = src.Build("/detail" + member + "/sub/a2");
        Assert.Equal(1, src.RunReads - before);

        before = src.RunReads;
        _ = src.Build("/detail" + member);
        Assert.Equal(1, src.RunReads - before);
    }

    [Fact]
    public void NegativeAnUnknownRunAnotherMembersRunAndTheOtherDocumentsAreNotFoundWithTheMemberAsNearest()
    {
        var src = Source();
        var member = src.Paths().Member;

        var unknown = ViewBuilder.Build(src, "/detail" + member + "/sub/nope", ModelSource.Now);
        var other = ViewBuilder.Build(src, "/actions" + member + "/sub/r1", ModelSource.Now);
        var graph = ViewBuilder.Build(src, "/graph" + member + "/sub/a2", ModelSource.Now);
        var inbox = ViewBuilder.Build(src, "/inbox" + member + "/sub/a2", ModelSource.Now);

        Assert.All(new[] { unknown, other, graph, inbox }, static a => Assert.Equal(404, a.Status));
        Assert.Equal(member, unknown.Body.Str("nearest"));
        Assert.Equal(member, other.Body.Str("nearest"));
    }

    [Fact]
    public void AStartedRunOfTheOrchestratorsSessionIsASubagentNodeUnderTheOrchestrator()
    {
        var src = Source();
        src.Runs.Add(new SubagentRun("sess-orch", "o1", "started", "2026-06-02T09:12:00.000Z", null, "wrangl:architect"));
        src.Runs.Add(new SubagentRun("sess-orch", "o2", "stopped", "2026-06-02T09:00:00.000Z", "2026-06-02T09:01:00.000Z"));
        var orch = src.Paths().Orch;

        var body = src.Build("/graph" + orch).Obj("body")!;
        var nodes = body["nodes"].AsArray().Select(static n => n.AsObject()).Where(n => n.Str("id")!.StartsWith(orch + "/sub/", StringComparison.Ordinal)).ToList();
        var node = Assert.Single(nodes);
        var edge = body["edges"].AsArray().Select(static e => e.AsObject()).Single(e => e.Str("id") == "subagent:" + node.Str("id"));
        var detail = src.Build("/detail" + orch + "/sub/o1");

        Assert.Equal((orch + "/sub/o1", "subagent", "busy", "wrangl:architect"), (node.Str("id"), node.Str("kind"), node.Str("state"), node.Str("agent_type")));
        Assert.Equal(orch, edge.Str("from"));
        Assert.Equal(orch, Field(detail, "parent")["value"].AsString());
        Assert.Equal(404, ViewBuilder.Build(src, "/detail" + orch + "/sub/a2", ModelSource.Now).Status);
    }

    [Fact]
    public void TheSubagentDocumentsMatchTheirGoldens()
    {
        var src = Source();
        var path = src.Paths().Member + "/sub/a2";

        ModelGolden.Check(src.Build("/detail" + path), "detail-subagent");
        ModelGolden.Check(src.Build("/actions" + path), "actions-subagent");
    }
}
