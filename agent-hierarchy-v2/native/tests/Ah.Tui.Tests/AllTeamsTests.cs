using Ah.Core.Json;
using Ah.Tui.Core;
using static Ah.Tui.Tests.TreeFixtures;

namespace Ah.Tui.Tests;

internal static class TeamsFixtures
{
    internal static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-23T12:04:00Z", System.Globalization.CultureInfo.InvariantCulture);

    private static string Ago(int minutes) => Now.AddMinutes(-minutes).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture);

    internal static Dictionary<string, JsonValue> Input() => ReadModel(
        new Spec("parity", [new("implementor", "working", 14), new("reviewer", "working", 3), new("qa", "working", 22), new("architect", "idle", 40)],
            [new("implementor", "writer-split-0103", 14), new("reviewer", "review-398", 3), new("qa", "full-test-suite", 22)]),
        new Spec("lane-ux", [new("implementor", "working", 41, Ctx: 62, Reported: 58, Stream: "lanes"), new("ux-engineer", "working", 6, Ctx: 31, Reported: 60, Stream: "lanes"), new("reviewer", "idle", 22, Ctx: 12, Reported: 22), new("architect", "idle", 180, Live: false)],
            [new("implementor", "lane-badge", 41), new("ux-engineer", "lanes-view-polish", 6)]),
        new Spec("docs", [new("architect", "blocked", 12), new("docs-writer", "blocked", 3)], [new("docs-writer", "msg-close-docs", 9)]),
        new Spec("default", [new("task-runner", "idle", 50, Live: false)], []));

    internal const string Dir = "d1";

    internal static string TeamId(string team) => "team:" + Dir + "/" + team;

    internal static string MemberId(string team, string name) => "member:" + Dir + "/" + team + "/" + name;

    /// <summary>
    /// One agent of a read-model fixture team, named team-role unless <c>Name</c> says otherwise. <c>State</c> and <c>Liveness</c>
    /// are what a served team summary says of it, when they differ from what its activity reads as; <c>Unserved</c> leaves it out.
    /// </summary>
    internal sealed record Agent(string Role, string Activity, int Ago, bool Live = true, int? Ctx = null, int? Reported = null, string? Stream = null, string? Name = null, int Alerts = 1, string? State = null, string? Liveness = null, bool Unserved = false, string? Audience = null);

    internal sealed record Ask(string Role, string Slug, int Ago, string? Title = null);

    /// <summary>A fixture team. <c>Served</c> has its summary serve each member's state and liveness, their counts and the team's
    /// state word (<c>State</c> when given); without it the summary is an old daemon's.</summary>
    internal sealed record Spec(string Name, Agent[] Agents, Ask[] Asks, string? Parent = null, string? Orchestrator = null, bool Served = false, string? State = null, string OrchLiveness = "live");

    private static string Q(string? s) => s is null ? "null" : "\"" + s + "\"";

    private static string Slice(string id, string body) => "{\"id\":\"" + id + "\",\"as_of\":1,\"body\":" + body + "}";

    /// <summary>
    /// The slices a set of teams makes, as the daemon serves them: the team list, each team's summary and every other slice. A dead
    /// agent has no activity record, and a task to it is not in flight.
    /// </summary>
    internal static Dictionary<string, JsonValue> ReadModel(params Spec[] teams)
    {
        var all = new List<string> { Slice("list:teams", "{\"teams\":[" + string.Join(',', teams.Select(t => "{\"id\":\"" + TeamId(t.Name) + "\",\"name\":\"" + t.Name + "\",\"dir_id\":\"" + Dir + "\"}")) + "]}") };
        foreach (var t in teams)
        {
            string NameOf(Agent a) => a.Name ?? t.Name + "-" + a.Role;
            var ids = new List<string>();
            var orch = t.Orchestrator is null ? null : MemberId(t.Name, t.Orchestrator);
            if (orch is not null)
            {
                ids.Add(orch);
                all.Add(Slice(orch, "{\"name\":\"" + t.Orchestrator + "\",\"role\":\"orchestrator\",\"activity\":null,\"placement\":{},\"last_exchanges\":[]}"));
            }

            foreach (var a in t.Agents)
            {
                var name = NameOf(a);
                var id = MemberId(t.Name, name);
                ids.Add(id);
                var activity = a.Live ? "activity:" + Dir + "/s-" + name : null;
                var report = a.Reported is null ? null : "exchange:" + Dir + "/r-" + name;
                all.Add(Slice(id, "{\"name\":\"" + name + "\",\"role\":\"" + a.Role + "\",\"activity\":" + Q(activity) + ",\"placement\":{\"stream\":" + Q(a.Stream) + "},\"context_text\":" + Q(a.Live && a.Ctx is { } shownCtx ? shownCtx.ToString(System.Globalization.CultureInfo.InvariantCulture) + "k" : "—") + ",\"last_report_at\":" + Q(a.Reported is { } reported ? Ago(reported) : null) + ",\"last_exchanges\":[" + (report is null ? string.Empty : Q(report)) + "]}"));
                if (activity is not null)
                {
                    all.Add(Slice(activity, "{\"activity\":\"" + a.Activity + "\",\"turn_at\":\"" + Ago(a.Ago) + "\",\"context\":" + (a.Ctx is null ? "{}" : "{\"used\":" + (a.Ctx.Value * 1000).ToString(System.Globalization.CultureInfo.InvariantCulture) + "}") + "}"));
                }

                if (report is not null)
                {
                    all.Add(Slice(report, "{\"slug\":\"report\",\"created\":\"" + Ago(a.Reported!.Value + 5) + "\",\"response_created\":\"" + Ago(a.Reported.Value) + "\",\"has_response\":true}"));
                }
            }

            var live = t.Agents.Where(static a => a.Live).ToList();
            var open = new List<string>();
            for (var i = 0; i < t.Asks.Length; i++)
            {
                var ask = t.Asks[i];
                var to = t.Agents.FirstOrDefault(a => a.Role == ask.Role);
                if (to is null || !to.Live)
                {
                    continue;
                }

                var x = "exchange:" + Dir + "/x-" + t.Name + "-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                open.Add("{\"id\":\"" + x + "\",\"to_member\":\"" + MemberId(t.Name, NameOf(to)) + "\",\"from_member\":" + Q(orch) + "}");
                all.Add(Slice(x, "{\"slug\":\"" + ask.Slug + "\",\"title\":" + Q(ask.Title) + ",\"created\":\"" + Ago(ask.Ago) + "\",\"open\":true}"));
            }

            all.Add(Slice("list:open:" + TeamId(t.Name), "{\"team\":\"" + TeamId(t.Name) + "\",\"open\":[" + string.Join(',', open) + "]}"));
            all.Add(Slice(TeamId(t.Name), "{\"name\":\"" + t.Name + "\",\"dir_id\":\"" + Dir + "\",\"orchestrator\":" + Q(orch) + ",\"members\":[" + string.Join(',', ids.Where(i => i != orch).Select(Q)) + "]"
                + (t.Parent is null ? string.Empty : ",\"lane\":{\"stream\":\"" + t.Name + "\",\"parent_team\":\"" + t.Parent + "\"}") + "}"));
            var blocked = live.Where(static a => a.Activity == "blocked").ToList();
            var alerts = blocked.SelectMany(a => Enumerable.Repeat(a, a.Alerts)).Select(a => "{\"kind\":\"blocked\",\"member\":\"" + MemberId(t.Name, NameOf(a)) + "\",\"since\":\"" + Ago(a.Ago) + "\",\"text\":\"Waiting on a permission\",\"title\":\"" + NameOf(a) + " is blocked\",\"detail\":\"Waiting on a permission\"" + (a.Audience is null ? string.Empty : ",\"audience\":\"" + a.Audience + "\"") + "}");
            var state = t.State ?? (blocked.Count > 0 ? "needs_you" : live.Count == 0 ? "ended" : live.Any(static a => a.Activity == "working") ? "working" : "idle");
            var ended = state == "ended" || (state == "needs_you" && live.Count == 0);
            var updated = live.Count == 0 ? "null" : Q(Ago(live.Min(static a => a.Ago)));
            var served = string.Empty;
            if (t.Served)
            {
                var entries = new List<(string Id, string State, string Liveness, int Ago)>();
                if (orch is not null)
                {
                    entries.Add((orch, "working", t.OrchLiveness, 1));
                }

                entries.AddRange(t.Agents.Where(static a => !a.Unserved).Select(a => (MemberId(t.Name, NameOf(a)), a.State ?? (!a.Live ? "gone" : a.Activity == "failed" ? "blocked" : a.Activity), a.Liveness ?? (a.Live ? "live" : "proven_dead"), a.Ago)));
                string Count(string word) => entries.Count(e => e.Liveness == word).ToString(System.Globalization.CultureInfo.InvariantCulture);
                served = ",\"member_states\":{" + string.Join(',', entries.Select(e => Q(e.Id) + ":{" + (e.State.Length == 0 ? string.Empty : "\"state\":\"" + e.State + "\",") + "\"liveness\":\"" + e.Liveness + "\",\"state_at\":\"" + Ago(e.Ago) + "\"}"))
                    + "},\"liveness_counts\":{\"live\":" + Count("live") + ",\"unknown\":" + Count("unknown") + ",\"not_seen\":" + Count("not_seen") + ",\"proven_dead\":" + Count("proven_dead") + "}";
            }

            all.Add(Slice("team-summary:" + Dir + "/" + t.Name, "{\"name\":\"" + t.Name + "\",\"running\":" + (live.Count > 0 ? "true" : "false") + ",\"members\":" + live.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ",\"working\":" + live.Count(static a => a.Activity == "working").ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"blocked\":" + blocked.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ",\"needs_you\":" + blocked.Count(static a => a.Audience != "orchestrator").ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"in_flight\":" + open.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ",\"state\":\"" + state + "\",\"ended\":" + (ended ? "true" : "false") + (ended ? ",\"quiet_at\":\"" + Ago(1) + "\"" : string.Empty)
                + ",\"updated_at\":" + updated + ",\"alerts\":[" + string.Join(',', alerts) + "]" + served + "}"));
        }

        return ReadModelPaths.All([Parse("{\"slices\":[" + string.Join(',', all) + "]}")]);
    }

    /// <summary>A release team of fifteen with three lanes, each lane a sub-orchestrator's team of one.</summary>
    internal static Dictionary<string, JsonValue> LanesInput()
    {
        var roles = new[] { "architect", "reviewer", "reviewer", "implementor", "ultra-advisor", "implementor", "implementor", "implementor", "implementor", "implementor", "ux-engineer", "sub-orchestrator", "sub-orchestrator", "sub-orchestrator" };
        var names = new[] { "rel-team-architect", "rel-team-reviewer", "rel-team-reviewer-4", "rel-team-implementor", "rel-team-ultra-advisor", "rel-team-implementor-43", "rel-team-implementor-44", "rel-team-implementor-45", "rel-team-implementor-46", "rel-team-implementor-47", "rel-team-ux-engineer-3", "rel-team-so-lane-ui", "rel-team-so-lane-db", "rel-team-so-lane-ops" };
        var agents = roles.Select((r, i) => new Agent(r, i == 3 ? "working" : "idle", 2 + i, Name: names[i])).ToArray();
        var asks = Enumerable.Range(0, 20).Select(i => new Ask(roles[(i % 10) + 1], "step-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), 5 + (i * 3))).ToArray();
        return ReadModel(
            new("rel-team", agents, asks, Orchestrator: "rel-team-orchestrator"),
            new("rel-team-lane-db", [new("sub-orchestrator", "idle", 4, Name: "rel-team-lane-db-lead")], [], Parent: "rel-team"),
            new("rel-team-lane-ops", [new("sub-orchestrator", "idle", 7, Name: "rel-team-lane-ops-lead")], [], Parent: "rel-team"),
            new("rel-team-lane-ui", [new("sub-orchestrator", "idle", 9, Name: "rel-team-lane-ui-lead")], [], Parent: "rel-team"),
            new("default", [new("task-runner", "idle", 50, Live: false)], []));
    }
}

/// <summary>The rows of the Teams and Team screens as the slices give them.</summary>
public sealed class AllTeamsTests
{
    private static TeamSlices.Tree Tree(Dictionary<string, JsonValue> slices) => TeamSlices.Build(slices, TeamsFixtures.Now)!;

    private static AllTeams.TeamDetail Detail(Dictionary<string, JsonValue> slices, string team) =>
        TeamSlices.Team(Tree(slices), slices, TeamsFixtures.TeamId(team), TeamsFixtures.Now)!.Value.Detail;

    [Fact]
    public void AnAgentRowSaysWhatItIsDoingInPlainWordsAndDashesWhatTheDaemonDoesNotServe()
    {
        var d = Detail(TeamsFixtures.Input(), "lane-ux");
        var impl = d.Agents[0];
        Assert.Equal(("implementor", "working", "lane badge"), (impl.Name, impl.Status.Word, impl.Working));
        var gone = d.Agents[3];
        Assert.Equal(("unknown", "–"), (gone.Status.Word, gone.Working));
    }

    [Fact]
    public void TeamsSortTroubleFirstAndTakeTheirCountsFromTheTeamSummary()
    {
        var m = Tree(TeamsFixtures.Input()).Model;
        Assert.Equal(["docs", "lane-ux", "parity", "default"], m.Teams.Select(t => t.Name));
        Assert.Equal(("needs you", 2, 2, 2), (m.Teams[0].Status.Word, m.Teams[0].NeedsYou, m.Teams[0].Up, m.Teams[0].Agents));
        Assert.Equal(("working", 3, 4, 2), (m.Teams[1].Status.Word, m.Teams[1].Up, m.Teams[1].Agents, m.Teams[1].InFlight));
        Assert.Equal("ended", m.Teams[3].Status.Word);
        Assert.Equal([("docs", "architect", "Waiting on a permission"), ("docs", "docs-writer", "Waiting on a permission")], m.Needs.Select(n => (n.Team, n.Agent, n.What)));
    }

    [Fact]
    public void ALaneIsListedUnderTheTeamItsRecordNamesAndItsParentSumsThemAll()
    {
        var tree = Tree(TeamsFixtures.LanesInput());
        Assert.Equal(["rel-team", "default"], tree.Model.Teams.Select(t => t.Name));
        Assert.Equal(3, tree.LaneCount);
        var rel = tree.Model.Teams[0];
        Assert.Equal((17, 18, 20), (rel.Up, rel.Agents, rel.InFlight));
        Assert.Equal(["rel-team-lane-db", "rel-team-lane-ops", "rel-team-lane-ui"], tree.Lanes[rel.Path!].Select(l => l.Name));

        // A name that only looks like a lane is a team of its own: the link is the served parent, never the name.
        var lookalike = TeamsFixtures.ReadModel(new TeamsFixtures.Spec("rel", [new("implementor", "idle", 3)], []), new TeamsFixtures.Spec("rel-lane", [new("implementor", "idle", 3)], []));
        Assert.Equal(2, TeamSlices.Build(lookalike, TeamsFixtures.Now)!.Model.Teams.Count);
    }

    [Fact]
    public void ARepeatedRoleIsNamedWithItsMemberName()
    {
        var pair = TeamsFixtures.ReadModel(new TeamsFixtures.Spec("pair", [new("reviewer", "idle", 5), new("reviewer", "idle", 5, Name: "pair-reviewer-2"), new("implementor", "idle", 5)], []));
        Assert.Equal(["reviewer (pair-reviewer)", "reviewer (pair-reviewer-2)", "implementor"], Detail(pair, "pair").Agents.Select(x => x.Name));
    }

    [Fact]
    public void AnAgentsTaskIsItsOldestOpenOne()
    {
        var solo = TeamsFixtures.ReadModel(new TeamsFixtures.Spec("solo", [new("implementor", "working", 2)], [new("implementor", "newer", 10), new("implementor", "older", 50)]));
        Assert.Equal("older", Detail(solo, "solo").Agents[0].Working);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(3, 8)]
    [InlineData(6, 60)]
    [InlineData(40, 60)]
    public void AHeldPathThatKeepsFailingWaitsTwiceAsLongEachTimeUpToAMinute(int failures, int seconds) =>
        Assert.Equal(seconds, TuiClientRules.Backoff(failures).TotalSeconds);

    [Fact]
    public void ATaskNameIsPlainWordsAndNeverAnId()
    {
        Assert.Equal("lane badge", AllTeams.Title(null, "lane-badge"));
        Assert.Equal("reviewing #398", AllTeams.Title("reviewing #398", "20261022-101500-1ig1"));
        Assert.Equal("a task", AllTeams.Title(null, "20261022-101500-1ig1"));
        Assert.Equal("a task", AllTeams.Title(null, "9f3a2c81d0b4e6aa"));
        Assert.Equal("a task", AllTeams.Title(" ", null));
    }

    [Fact]
    public void AnAgentShowsTheServedStateNotWhatItsActivityReadsAs()
    {
        var team = new TeamsFixtures.Spec("solo", [new("implementor", "working", 2, State: "idle"), new("reviewer", "failed", 3, State: "blocked")], [], Served: true);
        var agents = Detail(TeamsFixtures.ReadModel(team), "solo").Agents;
        Assert.Equal("idle", agents[0].Status.Key);
        Assert.Equal(StatusLanguage.Of("blocked"), agents[1].Status);
    }

    [Fact]
    public void AnAgentsLivenessWordIsTheServedOne()
    {
        var team = new TeamsFixtures.Spec("solo", [
            new("architect", "idle", 2, Liveness: "unknown"),
            new("reviewer", "idle", 2, Live: false, Liveness: "not_seen"),
            new("implementor", "idle", 2, Liveness: "proven_dead"),
            new("ux-engineer", "idle", 2, Live: false, Liveness: "live")], [], Served: true);
        Assert.Equal(["unknown", "not seen", "gone", null], Detail(TeamsFixtures.ReadModel(team), "solo").Agents.Select(static a => a.Liveness));
        Assert.All(Detail(TeamsFixtures.ReadModel(team with { Served = false }), "solo").Agents, static a => Assert.Null(a.Liveness));
    }

    [Fact]
    public void AMemberTheServedStatesLeaveOutIsUnknownNotReadFromItsActivity()
    {
        var team = new TeamsFixtures.Spec("solo", [new("implementor", "working", 2), new("reviewer", "working", 2, Unserved: true)], [], Served: true);
        var agents = Detail(TeamsFixtures.ReadModel(team), "solo").Agents;
        Assert.Equal(("working", null), (agents[0].Status.Key, agents[0].Liveness));
        Assert.Equal(("unknown", "unknown"), (agents[1].Status.Key, agents[1].Liveness));
    }

    [Fact]
    public void AServedEntryWithNoStateIsUnknownAsAMissingMemberIs()
    {
        var team = new TeamsFixtures.Spec("solo", [new("implementor", "working", 2, State: "")], [], Served: true);
        var agent = Detail(TeamsFixtures.ReadModel(team), "solo").Agents.Single();
        Assert.Equal(("unknown", null, "unknown"), (agent.Status.Key, agent.Since, agent.Liveness));
    }

    [Fact]
    public void AnOrchestratorAudienceAlertStaysOutOfNeedsYouAndTheListMatchesTheCount()
    {
        var team = new TeamsFixtures.Spec(
            "solo",
            [new("architect", "blocked", 12, Audience: "orchestrator"), new("implementor", "blocked", 9, Audience: "user"), new("reviewer", "blocked", 3)],
            []);
        var model = Tree(TeamsFixtures.ReadModel(team)).Model;

        Assert.Equal(["implementor", "reviewer"], model.Needs.Select(static n => n.Agent).Order(StringComparer.Ordinal));
        Assert.Equal(model.Needs.Count, model.Teams.Single().NeedsYou);
    }

    [Fact]
    public void TheTeamWordIsTheServedOne()
    {
        var team = new TeamsFixtures.Spec("solo", [new("implementor", "idle", 2, Live: false)], [], Served: true, State: "needs_you");
        Assert.Equal("needs you", Tree(TeamsFixtures.ReadModel(team)).Model.Teams.Single().Status.Word);
    }

    [Fact]
    public void TheUpCountIsTheServedLiveCountOfEveryMemberWithTheUnknownCounted()
    {
        string? liveness(int i) => i < 9 ? null : i < 13 ? "unknown" : "not_seen";
        var team = new TeamsFixtures.Spec("big", [.. Enumerable.Range(0, 15).Select(i => new TeamsFixtures.Agent("implementor", "idle", 2, Name: "big-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), Liveness: liveness(i)))], [], Served: true);
        var row = Tree(TeamsFixtures.ReadModel(team)).Model.Teams.Single();
        Assert.Equal((9, 15, 6), (row.Up, row.Agents, row.Unknown));
        var old = Tree(TeamsFixtures.ReadModel(team with { Served = false })).Model.Teams.Single();
        Assert.Equal((15, 15, 0), (old.Up, old.Agents, old.Unknown));
    }

    [Fact]
    public void ALaneRollupSumsTheServedValuesAndTakesTheWorstServedWord()
    {
        var tree = Tree(TeamsFixtures.ReadModel(
            new TeamsFixtures.Spec("rel", [new("implementor", "idle", 2), new("reviewer", "idle", 2, Liveness: "unknown")], [], Served: true),
            new TeamsFixtures.Spec("rel-a", [new("sub-orchestrator", "idle", 2, Live: false, Liveness: "not_seen")], [], Parent: "rel", Served: true, State: "needs_you"),
            new TeamsFixtures.Spec("rel-b", [new("sub-orchestrator", "working", 2), new("implementor", "idle", 2)], [], Parent: "rel", Served: true)));
        var own = tree.Own.Values.ToList();
        var top = tree.Model.Teams.Single();
        Assert.Equal(("needs you", own.Sum(static r => r.Up), own.Sum(static r => r.Agents), own.Sum(static r => r.Unknown)), (top.Status.Word, top.Up, top.Agents, top.Unknown));
        Assert.Equal((3, 5, 2), (top.Up, top.Agents, top.Unknown));
    }

    [Fact]
    public void AnUnconfirmedLaneClaimAndAnOrphanLaneAreTeamsOfTheirOwn()
    {
        var slices = TeamsFixtures.ReadModel(
            new TeamsFixtures.Spec("top", [new("implementor", "idle", 3)], []),
            new TeamsFixtures.Spec("top-claimed", [new("implementor", "idle", 3)], []),
            new TeamsFixtures.Spec("top-orphan", [new("implementor", "idle", 3)], [], Parent: "gone"));
        slices[TeamsFixtures.TeamId("top-claimed")] = Parse("{\"lane_claim\":{\"parent_team\":\"top\"},\"name\":\"top-claimed\",\"dir_id\":\"d1\"}");
        var tree = Tree(slices);
        Assert.Equal(["top", "top-claimed", "top-orphan"], tree.Model.Teams.Select(t => t.Name));
        Assert.Equal(0, tree.LaneCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DeputyOnDutyItSitsUnderTheChiefInTheLeadSlotOnceAndTheTallyIsShown(bool onDuty)
    {
        var slices = TeamsFixtures.ReadModel(new TeamsFixtures.Spec(
            "tm",
            [new("orchestrator", "working", 1, Name: "tm-orch"), new("implementor", "idle", 2, Name: "tm-impl"), new("deputy-chief", "working", 3, Name: "tm-dep")],
            [],
            Orchestrator: "tm-orch"));
        var deputy = TeamsFixtures.MemberId("tm", "tm-dep");
        if (onDuty)
        {
            slices[TeamsFixtures.TeamId("tm")].AsObject()["duty"] = JsonText.Parse(
                "{\"deputy\":\"tm-dep\",\"member\":\"" + deputy + "\",\"since\":\"2026-11-08T20:00:00.000Z\",\"tally\":{\"handled\":14,\"escalated\":2,\"waiting\":1}}");
        }

        var agents = Detail(slices, "tm").Agents;
        var line = TeamSlices.DutyLine(slices, TeamsFixtures.TeamId("tm"));

        Assert.Single(agents, a => a.Id == deputy);
        if (onDuty)
        {
            Assert.Equal(deputy, agents[1].Id);
            Assert.EndsWith("· on duty", agents[1].Name, StringComparison.Ordinal);
            Assert.Equal("Chief Orchestrator: with user", agents[0].Name);
            Assert.EndsWith("14 handled, 2 escalated, 1 waiting on you", line, StringComparison.Ordinal);
        }
        else
        {
            Assert.Null(line);
            Assert.DoesNotContain(agents, a => a.Name.Contains("on duty", StringComparison.Ordinal));
        }
    }
}
