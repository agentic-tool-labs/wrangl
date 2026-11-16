using Ah.Core.Json;
using Ah.Hierarchy;

namespace Ah.Watch.Tests;

/// <summary>The fields of a member's detail document the member page needs: who sent the brief, the latest report and the items held.</summary>
public sealed class MemberDetailFieldsTests
{
    private sealed class OnePlan(PlanState? plan) : IViewProgress
    {
        public TeamProgress Read(string pool, string team) => new(false, true, [new StreamProgress("s1", true, true, 0, 1, 0, "open", DateTimeOffset.UnixEpoch, null, false, false, [], plan is not null, "t")], []);

        public PlanState? ReadPlan(string pool, string team, string stream) => plan;

        public ProgressStreamInfo Describe(string pool, string team, string stream) => new("—", "—", "—", null);
    }

    private static PlanItem Item(string key, string? holder, string added) => new(key, "title " + key, "in_progress", added, null, null, [], null, null, null, Holder: holder);

    private static JsonObject Field(ModelSource s, string key) =>
        s.Build("/detail" + s.Paths().Member)["body"].AsObject()["fields"].AsArray().Select(static f => f.AsObject()).Single(f => f["key"].AsString() == key);

    private static ModelSource Source(PlanState? plan = null, bool reader = true)
    {
        var s = new ModelSource { TeamName = "t1", ProgressReader = reader ? new OnePlan(plan) : null };
        _ = s.Member("impl").Member("other");
        return s;
    }

    private static JsonObject Dispatch(string id, string member, string? reported) => new()
    {
        ["id"] = id,
        ["slug"] = "slug-" + id,
        ["member"] = member,
        ["sent_at"] = "2026-06-02T10:00:00.000Z",
        ["reported_at"] = reported is null ? JsonValue.Null : reported,
    };

    [Fact]
    public void TheDocumentOfASliceNodePathIsTheDocumentOfTheNodeItNames()
    {
        var s = Source();
        var doc = s.Build("/detail/" + Model.TreeIds.NodePathRoot + "/" + DirId + "/t1/m/impl");
        Assert.Equal("member", doc["body"].AsObject()["node"].AsObject()["kind"].AsString());
        Assert.Equal(s.Paths().Member, doc["body"].AsObject()["node"].AsObject()["path"].AsString());
    }

    [Fact]
    public void TheOrchestratorSegmentOfASliceNodePathIsTheOrchestrator()
    {
        var s = Source();
        var doc = s.Build("/detail/" + Model.TreeIds.NodePathRoot + "/" + DirId + "/t1/m/" + Model.TreeIds.Orchestrator);
        Assert.Equal("orchestrator", doc["body"].AsObject()["node"].AsObject()["kind"].AsString());
    }

    [Fact]
    public void NegativeASliceNodePathOfNoTeamIsNotFound()
    {
        var s = Source();
        var doc = s.Build("/detail/" + Model.TreeIds.NodePathRoot + "/" + DirId + "/nobody/m/impl");
        Assert.NotEqual("detail", doc.Str("kind"));
    }

    private static string DirId => Model.ActivityLane.DirId(ModelSource.Dir);

    [Fact]
    public void TheLatestReportIsTheMembersNewestReportedDispatch()
    {
        var s = Source();
        s.Dispatches.Add(Dispatch("old", "impl", "2026-06-02T10:30:00.000Z"));
        s.Dispatches.Add(Dispatch("new", "impl", "2026-06-02T11:30:00.000Z"));

        var report = Field(s, "report")["value"].AsObject();
        Assert.Equal("slug-new", report["slug"].AsString());
        Assert.Equal("2026-06-02T11:30:00.000Z", report["reported_at"].AsString());
    }

    [Fact]
    public void NegativeAnOpenDispatchAndAnotherMembersReportAreNotItsReport()
    {
        var s = Source();
        s.Dispatches.Add(Dispatch("open", "impl", null));
        s.Dispatches.Add(Dispatch("theirs", "other", "2026-06-02T11:30:00.000Z"));

        var report = Field(s, "report");
        Assert.False(report["present"].AsBool());
        Assert.Equal("no report yet", report["reason"].AsString());
    }

    [Fact]
    public void TheItemsHeldAreTheMembersNewestFirst()
    {
        var s = Source(new PlanState([Item("a", "impl", "2026-06-02T09:00:00Z"), Item("b", "other", "2026-06-02T10:00:00Z"), Item("c", "impl", "2026-06-02T11:00:00Z"), Item("d", null, "2026-06-02T12:00:00Z")], 0));

        var held = Field(s, "held")["value"].AsArray().Select(static i => i.AsObject()).ToList();
        Assert.Equal(["c", "a"], held.Select(static i => i["key"].AsString()));
        Assert.Equal("s1", held[0]["stream"].AsString());
        Assert.Equal("in_progress", held[0]["state"].AsString());
    }

    [Fact]
    public void NegativeNoProgressReaderIsAbsentAndNoPlanIsAnEmptyList()
    {
        Assert.False(Field(Source(reader: false), "held")["present"].AsBool());

        var noPlan = Field(Source(), "held");
        Assert.True(noPlan["present"].AsBool());
        Assert.Empty(noPlan["value"].AsArray());
    }

    [Fact]
    public void TheBriefSaysWhoSentIt()
    {
        var s = Source();
        s.Dispatches.Add(Dispatch("b1", "impl", null));
        s.Boxes["impl"] = [new JsonObject { ["id"] = "b1", ["from"] = "orchestrator@t1", ["kind"] = "brief" }];

        Assert.Equal("orchestrator@t1", Field(s, "brief")["value"].AsObject()["from"].AsString());
    }

    [Fact]
    public void NegativeABriefNotAmongTheRecentEntriesHasNoSender()
    {
        var s = Source();
        s.Dispatches.Add(Dispatch("b1", "impl", null));

        Assert.True(Field(s, "brief")["value"].AsObject()["from"].IsNull);
    }
}
