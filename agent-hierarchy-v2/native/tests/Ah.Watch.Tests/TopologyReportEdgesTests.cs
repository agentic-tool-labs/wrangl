using Ah.Core.Json;
using Ah.Watch.Model;

namespace Ah.Watch.Tests;

/// <summary>ah2-268 P2: the topology slice's <c>edges.report</c>, the exchanges whose report waits on the reader.</summary>
public sealed class TopologyReportEdgesTests
{
    private const string Dir = "/canon/hier";
    private const string Name = "ship";

    private sealed class Records : IRecordView
    {
        public Dictionary<(string Lane, string Key), LaneRecord> Rows { get; } = [];

        public LaneRecord? Get(string lane, string key) => Rows.GetValueOrDefault((lane, key));

        public IReadOnlyList<string> Keys(string lane) => [.. Rows.Keys.Where(k => k.Lane == lane).Select(static k => k.Key).Order(StringComparer.Ordinal)];
    }

    private static string D => ActivityLane.DirId(Dir);

    private static string Id => "topology:" + D + "/" + Name;

    private static Records With(string state, string lastRef = "r1", bool responded = true, string toName = "dev")
    {
        var records = new Records();
        records.Rows[(TeamLane.LaneName, TeamLane.KeyOf(Dir, Dir + "/teams/" + Name + ".json"))] = new LaneRecord(JsonText.Parse(
            """{"team_id":"t1","orchestrator":{"session_id":"so"},"members":[{"name":"dev","role":"implementor"},{"name":"rev","role":"reviewer"}]}"""), 1);
        records.Rows[(MessagesLane.LaneName, MessagesLane.KeyOf(Dir, "r1"))] = new LaneRecord(JsonText.Parse(
            $$"""{"team":"{{Name}}","to":"implementor","from":"orchestrator","to_name":"{{toName}}","from_name":null,"open":false,"response_created":{{(responded ? "\"2026-11-19T01:00:00Z\"" : "null")}}}"""), 2);
        var lane = new ProgressLane(new PlanReader(state, lastRef), TestFolds.New, static () => [Dir]);
        records.Rows[(ProgressLane.LaneName, ProgressLane.KeyOf(Dir, Name))] = lane.Reread(ProgressLane.KeyOf(Dir, Name), 1_780_000_000_000).Record!;
        return records;
    }

    /// <summary>A plan whose one row is linked to r0 then <paramref name="lastRef"/>, started, reported, and done when <paramref name="state"/> says so.</summary>
    private sealed class PlanReader(string state, string lastRef) : Ah.Watch.IViewStateReader
    {
        public JsonValue Read(string domain, string op, string hierarchyDir, JsonObject args)
        {
            if (domain == "team")
            {
                return JsonValue.Null;
            }

            if (op == "streams")
            {
                return JsonText.Parse("""{"streams":[]}""");
            }

            string[] events = ["\"event\":\"add\",\"title\":\"A\"", "\"event\":\"ref\",\"ref\":\"r0\"", $"\"event\":\"ref\",\"ref\":\"{lastRef}\"", "\"event\":\"start\"", "\"event\":\"report\""];
            if (state == "done")
            {
                events = [.. events, "\"event\":\"done\""];
            }

            var rows = events.Select((e, i) => $"{{\"ts\":\"2026-11-19T01:0{i}:00Z\",\"stream\":\"s\",\"item\":\"a\",{e},\"by\":\"o@t\"}}");
            return JsonText.Parse($$"""{"streams":[{"stream":"s","offset":{{events.Length}},"length":{{events.Length}},"rows":[{{string.Join(',', rows)}}]}]}""");
        }
    }

    private static JsonArray Reports(Records records) =>
        JsonText.Parse(new TopologyComposer().Compose(Id, records).Body!).Member("edges")!.Value.Member("report")!.Value.AsArray();

    [Fact]
    public void AReportedRowWithAFiledResponseIsListedWithItsBriefsParties()
    {
        var row = Assert.Single(Reports(With("reported")));
        Assert.Equal(MessagesLane.SliceIdOfDirId(D, "r1"), row.Member("id")!.Value.AsString());
        Assert.Equal(TreeIds.Member(Dir, Name, TreeIds.Orchestrator), row.Member("from_member")!.Value.AsString());
        Assert.Equal(TreeIds.Member(Dir, Name, "dev"), row.Member("to_member")!.Value.AsString());
    }

    [Fact]
    public void NegativeOnceTheRowIsDoneItIsNotListed() => Assert.Empty(Reports(With("done")));

    [Fact]
    public void NegativeAReportedRowWhoseResponseIsNotFiledIsNotListed() => Assert.Empty(Reports(With("reported", responded: false)));

    [Fact]
    public void NegativeAReportedRowWhoseLastRefNamesAnotherExchangeIsNotListed() => Assert.Empty(Reports(With("reported", lastRef: "r9")));

    [Fact]
    public void APartyThatResolvesToNobodyIsNull()
    {
        var row = Assert.Single(Reports(With("reported", toName: "nobody")));
        Assert.True(row.Member("to_member")!.Value.IsNull);
        Assert.False(row.Member("from_member")!.Value.IsNull);
    }

    [Fact]
    public void AProgressChangeRecomposesTheTeamsTopologyAndItsStampCarriesThePlansAsOf()
    {
        var records = With("reported");
        Assert.Equal([Id], new TopologyComposer().Affected(new LaneKey(ProgressLane.LaneName, ProgressLane.KeyOf(Dir, Name)), null, null, records));
        var plan = records.Get(ProgressLane.LaneName, ProgressLane.KeyOf(Dir, Name))!;
        Assert.True(plan.AsOfMs > 2);
        Assert.Equal(plan.AsOfMs, new TopologyComposer().Compose(Id, records).AsOfMs);
    }

    // ah2-268 A3: the lane carries last_ref on a reported row only, and work_items never lists a reported row.
    [Fact]
    public void TheLaneCarriesLastRefOnAReportedItemAndNegativeNotOnceDone()
    {
        JsonValue ItemOf(Records r) => Assert.Single(r.Get(ProgressLane.LaneName, ProgressLane.KeyOf(Dir, Name))!.Value.Member("items")!.Value.AsArray());
        var reported = With("reported");
        Assert.Equal("r1", ItemOf(reported).Member("last_ref")!.Value.AsString());
        Assert.Empty(reported.Get(ProgressLane.LaneName, ProgressLane.KeyOf(Dir, Name))!.Value.Member("work_items")!.Value.AsArray());
        Assert.Null(ItemOf(With("done")).Member("last_ref"));
    }
}
