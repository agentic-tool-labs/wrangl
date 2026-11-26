using Ah.Core.Json;

namespace Ah.Hierarchy.Tests;

/// <summary>ah2-367 L3: the status the pane reads lists an open lane with its parent team's owner, so the parent's Orchestrator's scope holds its dispatches.</summary>
public sealed class StatusLaneOwnersTests
{
    [Fact]
    public void AnOpenLaneIsListedWithItsParentsOwnerAndADoneOneIsNot()
    {
        var src = new Source(new()
        {
            ["top"] = """{"orchestrator":{"pid":1,"session_id":"o-top"},"members":[],"streams":{"s1":{"state":"open","sub_orchestrator":"so-1"},"s2":{"state":"done","sub_orchestrator":"so-2"}}}""",
            ["lane"] = """{"orchestrator":{"pid":2,"session_id":"o-so1"},"members":[],"parent":{"team":"top","stream":"s1"}}""",
            ["gone"] = """{"orchestrator":{"pid":3,"session_id":"o-so2"},"members":[],"parent":{"team":"top","stream":"s2"}}""",
        });

        var status = HierLib.ComputeStatusFrom(src, "/d", 1_780_000_000_000, "/d");
        var owners = status["owners"].AsArray().Select(static o => (Sessions: o.Member("sessions")!.Value.AsArray().Select(static s => s.AsString()).ToList(), Teams: o.Member("teams")!.Value.AsArray().Select(static t => t.AsString()).ToList())).ToList();

        Assert.Equal(["top", "lane"], owners.Single(o => o.Sessions.Contains("o-top")).Teams);
        Assert.Equal(["lane"], owners.Single(o => o.Sessions.Contains("o-so1")).Teams);
        Assert.Equal(["gone"], owners.Single(o => o.Sessions.Contains("o-so2")).Teams);
    }

    // ah2-367 L7, deputy-pane D1: on duty, the deputy owns an entry of its own listing its team and that team's lane, not the chief's
    // other team, and a chief group past the sessions cap does not cut it; the chief's entry keeps the whole group.
    // The negative: off duty, the deputy's session is in no owner entry.
    [Theory]
    [InlineData(",\"duty\":{\"deputy\":\"dep\"}", true)]
    [InlineData("", false)]
    public void TheDeputyOnDutyOwnsOnlyItsTeamAndItsLanes(string duty, bool listed)
    {
        var rows = Enumerable.Range(0, 40).Select(static i => (JsonValue)new JsonObject { ["pid"] = 1, ["session_id"] = "o-row" + i }).ToList();
        var src = new Source(
            new()
            {
                ["top"] = """{"orchestrator":{"pid":1,"session_id":"o-top"},"members":[{"name":"dep","role":"deputy-chief"}],"streams":{"s1":{"state":"open"}}""" + duty + "}",
                ["other"] = """{"orchestrator":{"pid":1,"session_id":"o-top"},"members":[]}""",
                ["lane"] = """{"orchestrator":{"pid":2,"session_id":"o-so1"},"members":[],"parent":{"team":"top","stream":"s1"}}""",
            },
            sessionRows: rows);

        var status = HierLib.ComputeStatusFrom(src, "/d", 1_780_000_000_000, "/d");
        var owners = status["owners"].AsArray().Select(static o => (Sessions: o.Member("sessions")!.Value.AsArray().Select(static s => s.AsString()).ToList(), Teams: o.Member("teams")!.Value.AsArray().Select(static t => t.AsString()).ToList())).ToList();

        var chief = owners.Single(o => o.Sessions.Contains("o-row39"));
        Assert.Equal(["top", "other", "lane"], chief.Teams);
        Assert.DoesNotContain("s-dep", chief.Sessions);
        var deputy = owners.Where(o => o.Sessions.Contains("s-dep")).ToList();
        Assert.Equal(listed ? 1 : 0, deputy.Count);
        if (listed)
        {
            Assert.Equal(["top", "lane"], deputy[0].Teams);
        }
    }

    internal sealed class Source(Dictionary<string, string> teams, Func<JsonArray>? resources = null, IReadOnlyList<JsonValue>? sessionRows = null) : IStatusSource
    {
        public IReadOnlyList<string> TeamNames(string dir) => [.. teams.Keys];
        public JsonValue TeamRecord(string dir, string? team) => team is not null && teams.TryGetValue(team, out var doc) ? JsonText.Parse(doc) : JsonValue.Null;
        public bool Orphaned(JsonValue team) => false;
        public JsonArray Roster(string dir) => [];
        public IReadOnlyList<JsonValue> SessionRows(string dir) => sessionRows ?? [];
        public JsonArray Exchanges(string dir) => [];
        public JsonArray Gates(string dir) => [];
        public JsonValue RequestFm(JsonValue exchange) => new JsonObject();
        public (string? Title, string? Source) Title(JsonValue exchange) => (null, null);
        public double? ResponseMtime(string path) => null;
        public string? ResponseStatus(string path) => null;
        public Dictionary<string, JsonValue> Origins(JsonArray requests) => [];
        public JsonValue Activity(string dir, string file) => JsonValue.Null;
        public MemberRegistrationLiveness? Registration(string name, string team) => null;
        public JsonArray Subagents(string sessionId) => [];
        public JsonArray RunExchanges(string dir, string? team, long sinceMs) => [];
        public JsonObject Decisions(string dir, string anchorId) => new();
        public IReadOnlyList<JsonValue> TeamTags(IReadOnlyList<JsonValue> exchanges) => [.. exchanges.Select(static _ => JsonValue.Null)];
        public BacklogHealth? Backlog(string dir, double nowMs, Action<double>? flip = null) => null;
        public bool Enabled(string? cwd) => true;
        public bool HasDir(string dir) => true;
        public string? Branch(string workTree) => null;
        public string? Checkout(string cwd) => null;
        public (bool Live, string? SessionId) MemberLife(JsonArray roster, JsonValue name, string? team) => (true, "s-" + name.AsString());

        public JsonArray Resources() => resources?.Invoke() ?? [];

        public JsonArray Spend(string dir, string? team) => [];
    }
}
