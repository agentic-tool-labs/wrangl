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

    // ah2-367 L7; the negative: off duty, the deputy's session is no owner session
    [Theory]
    [InlineData(",\"duty\":{\"deputy\":\"dep\"}", true)]
    [InlineData("", false)]
    public void TheDeputyOnDutyIsAnOwnerSessionOfItsTeam(string duty, bool listed)
    {
        var src = new Source(new()
        {
            ["top"] = """{"orchestrator":{"pid":1,"session_id":"o-top"},"members":[{"name":"dep","role":"deputy-chief"}]""" + duty + "}",
        });

        var status = HierLib.ComputeStatusFrom(src, "/d", 1_780_000_000_000, "/d");
        var sessions = status["owners"].AsArray().Single().Member("sessions")!.Value.AsArray().Select(static s => s.AsString()).ToList();

        Assert.Contains("o-top", sessions);
        Assert.Equal(listed, sessions.Contains("s-dep"));
    }

    internal sealed class Source(Dictionary<string, string> teams, Func<JsonArray>? resources = null) : IStatusSource
    {
        public IReadOnlyList<string> TeamNames(string dir) => [.. teams.Keys];
        public JsonValue TeamRecord(string dir, string? team) => team is not null && teams.TryGetValue(team, out var doc) ? JsonText.Parse(doc) : JsonValue.Null;
        public bool Orphaned(JsonValue team) => false;
        public JsonArray Roster(string dir) => [];
        public IReadOnlyList<JsonValue> SessionRows(string dir) => [];
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
    }
}
