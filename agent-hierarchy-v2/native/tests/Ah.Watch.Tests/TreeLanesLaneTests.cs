using System.Text;
using Ah.Core.Json;
using Ah.Hierarchy;
using Ah.Watch.Model;

namespace Ah.Watch.Tests;

/// <summary>Lanes in the team slices: a lane is served only when the manager's file confirms it, the streams come from the manager's file, and what a sub-orchestrator reports about itself is bounded and named as its own.</summary>
public sealed partial class TreeLanesTests
{
    private const string MgrPath = Canon + "/teams/mgr.json";
    private const string ChildPath = Canon + "/teams/mgr-s1.json";

    private static string Streams(params (string Name, string State)[] streams) =>
        "{" + string.Join(",", streams.Select(s => "\"" + s.Name + "\":{\"state\":\"" + s.State + "\",\"branch\":\"b-" + s.Name + "\",\"base\":\"main\",\"sub_orchestrator\":\"so-" + s.Name + "\",\"lane\":{\"max_members\":4,\"max_streams\":2}}")) + "}";

    private static string MgrDoc(string streams, string members = "") =>
        "{\"team_id\":\"tm\",\"created\":\"2026-06-01T10:00:00Z\",\"orchestrator\":{\"session_id\":\"mo\",\"pid\":1,\"pid_start\":\"o1\"},\"members\":[" + members + "],\"streams\":" + streams + "}";

    private static string ChildDoc(string parent, string stream, string summary = "working on it", string state = "working", string? result = null) =>
        "{\"team_id\":\"tc\",\"created\":\"2026-06-01T11:00:00Z\",\"orchestrator\":{\"session_id\":\"so\",\"pid\":2,\"pid_start\":\"o2\"},\"members\":[],"
        + "\"parent\":{\"team\":\"" + parent + "\",\"stream\":\"" + stream + "\"},\"streams\":{\"" + stream + "\":{\"state\":\"open\",\"branch\":\"b\"}},"
        + "\"workstream\":{\"state\":\"" + state + "\",\"summary\":\"" + summary + "\",\"updated\":\"2026-06-01T11:30:00Z\"" + (result is null ? "" : ",\"result\":\"" + result + "\"") + "}}";

    /// <summary>The host's answers with the product's own confirmation predicate and child naming; routes come from the test.</summary>
    private sealed class Facts(Func<string, string, string, LaneRoute?>? routes) : ILaneFacts
    {
        public string? ConfirmedStream(string managerName, JsonValue managerRecord, string childName, JsonValue childRecord) =>
            HierarchyModule.ConfirmedLaneStream(managerName, managerRecord, childName, childRecord);

        public string ChildName(string manager, string stream) => HierarchyModule.LaneChildName(manager, stream);

        public LaneRoute? RouteOf(string dir, string team, string member) => routes?.Invoke(dir, team, member);
    }

    private static readonly ILaneFacts ProductFacts = new Facts(null);

    private static (ReadModel Model, Clock Clock) MakeLanes(TreeReader reader, Func<string, string, string, LaneRoute?>? routes = null, ILaneFacts? facts = null)
    {
        var clock = new Clock();
        string[] canonical = [Canon];
        var lanes = TreeModel.Lanes(reader, () => canonical, Home).Append(new MessagesLane(reader, () => canonical)).Append(new StatusLane(reader, () => canonical)).ToList();
        var model = new ReadModel(lanes, new ModelOptions { SeedAtStart = false }, clock.Read, null, "e1", new PathLaneTable(() => canonical, home: Home), TreeModel.Composers(static () => 5_000_000, facts ?? (routes is null ? ProductFacts : new Facts(routes))));
        foreach (var lane in new[] { "teams", "worktrees", "decisions", "registrations", "known-dirs", "messages" })
        {
            model.EmitKeySet(lane);
        }

        Drain(model, clock);
        return (model, clock);
    }

    private static TreeReader LaneTruth(string streams, string child)
    {
        var reader = new TreeReader();
        reader.Teams[MgrPath] = MgrDoc(streams);
        reader.Teams[ChildPath] = child;
        reader.KnownDirs.Add(Canon);
        return reader;
    }

    private static JsonValue Slice(ReadModel model, string id) => JsonText.Parse(Body(model, id));

    private static string TeamId(string name) => $"team:{D}/{name}";

    [Fact]
    public void AConfirmedLaneIsServedWithItsStreamTheManagersStateAndTheSubOrchestratorsOwnReport()
    {
        var (model, _) = MakeLanes(LaneTruth(Streams(("s1", "open")), ChildDoc("mgr", "s1", result: "merged")));
        using var owner = model;

        var child = Slice(model, TeamId("mgr-s1"));
        var lane = child.Member("lane")!.Value;

        Assert.Equal("s1", lane.Member("stream")!.Value.AsString());
        Assert.Equal("mgr", lane.Member("parent_team")!.Value.AsString());
        Assert.Equal("open", lane.Member("state")!.Value.AsString());
        var reported = lane.Member("reported")!.Value;
        Assert.Equal("working", reported.Member("state")!.Value.AsString());
        Assert.Equal("working on it", reported.Member("summary")!.Value.AsString());
        Assert.Equal("merged", reported.Member("result")!.Value.AsString());
        Assert.Null(child.Member("lane_claim"));
        Assert.Null(child.Member("streams"));
    }

    [Fact]
    public void AManagerListsItsStreamsFromItsOwnFileWithTheConfirmedChildAndOnlyThat()
    {
        var (model, _) = MakeLanes(LaneTruth(Streams(("s2", "done"), ("s1", "open")), ChildDoc("mgr", "s1")));
        using var owner = model;

        var body = Slice(model, TeamId("mgr"));
        var rows = body.Member("streams")!.Value.AsArray();

        Assert.Equal(["s1", "s2"], rows.Select(r => r.Member("name")!.Value.AsString()));
        Assert.Equal("open", rows[0].Member("state")!.Value.AsString());
        Assert.Equal("done", rows[1].Member("state")!.Value.AsString());
        Assert.Equal("so-s1", rows[0].Member("sub_orchestrator")!.Value.AsString());
        Assert.Equal("mgr-s1", rows[0].Member("child_team")!.Value.AsString());
        Assert.Null(rows[1].Member("child_team"));
        Assert.Null(body.Member("streams_total"));
        Assert.Null(body.Member("lane"));
    }

    [Theory]
    [InlineData("s1", "mgr-x", "mgr")]
    [InlineData("s9", "mgr-s9", "mgr")]
    [InlineData("s1", "mgr-s1", "ghost")]
    public void AClaimTheManagerDoesNotConfirmGetsOnlyALaneClaimAndNoLink(string stream, string name, string parent)
    {
        var reader = LaneTruth(Streams(("s1", "open")), ChildDoc(parent, stream));
        reader.Teams.Remove(ChildPath);
        reader.Teams[Canon + "/teams/" + name + ".json"] = ChildDoc(parent, stream);
        var (model, _) = MakeLanes(reader);
        using var owner = model;

        var child = Slice(model, TeamId(name));

        Assert.Null(child.Member("lane"));
        Assert.Equal(parent, child.Member("lane_claim")!.Value.Member("parent_team")!.Value.AsString());
        Assert.Null(Slice(model, TeamId("mgr")).Member("streams")!.Value.AsArray()[0].Member("child_team"));
    }

    private static TeamEntry TeamEntryOf(string dir, string name, string doc)
    {
        var path = dir + "/teams/" + name + ".json";
        return new TeamEntry(dir, path, name, JsonValue.FromObject(TeamLane.ValueOf(dir, path, name, JsonText.Parse(doc))), 0);
    }

    [Fact]
    public void AClaimedParentInAnotherDirectoryIsUnconfirmed()
    {
        var child = TeamEntryOf("/other/.claude/ah2", "mgr-s1", ChildDoc("mgr", "s1"));
        var manager = TeamEntryOf(Canon, "mgr", MgrDoc(Streams(("s1", "open"))));

        var childBody = new JsonObject();
        LaneView.Apply(childBody, child, [child], ProductFacts);
        var managerBody = new JsonObject();
        LaneView.Apply(managerBody, manager, [manager], ProductFacts);

        Assert.Null(childBody.Get("lane"));
        Assert.NotNull(childBody.Get("lane_claim"));
        Assert.Null(managerBody.Get("streams")!.Value.AsArray()[0].Member("child_team"));
    }

    [Fact]
    public void WithNoHostFactsNothingIsConfirmedAndNoLinkIsServed()
    {
        var reader = LaneTruth(Streams(("s1", "open")), ChildDoc("mgr", "s1"));
        string[] canonical = [Canon];
        var clock = new Clock();
        var lanes = TreeModel.Lanes(reader, () => canonical, Home).Append(new MessagesLane(reader, () => canonical)).Append(new StatusLane(reader, () => canonical)).ToList();
        using var model = new ReadModel(lanes, new ModelOptions { SeedAtStart = false }, clock.Read, null, "e1", new PathLaneTable(() => canonical, home: Home), TreeModel.Composers(static () => 5_000_000));
        foreach (var lane in new[] { "teams", "worktrees", "decisions", "registrations", "known-dirs", "messages" })
        {
            model.EmitKeySet(lane);
        }

        Drain(model, clock);

        Assert.Null(Slice(model, TeamId("mgr-s1")).Member("lane"));
        Assert.NotNull(Slice(model, TeamId("mgr-s1")).Member("lane_claim"));
        Assert.Null(Slice(model, TeamId("mgr")).Member("streams")!.Value.AsArray()[0].Member("child_team"));
    }

    [Fact]
    public void ATopLevelTeamCarriesNeitherLaneNorLaneClaimNorStreams()
    {
        var (model, _) = MakeLanes(Truth());
        using var owner = model;

        var body = Slice(model, TeamId("default"));

        Assert.Null(body.Member("lane"));
        Assert.Null(body.Member("lane_claim"));
        Assert.Null(body.Member("streams"));
        Assert.Null(body.Member("streams_total"));
    }

    [Fact]
    public void AnOverlongReportIsCutAtTwoHundredAndEightyCharactersEndingInAnEllipsis()
    {
        var long10k = new string('x', 10_000);
        var (model, _) = MakeLanes(LaneTruth(Streams(("s1", "open")), ChildDoc("mgr", "s1", summary: long10k, result: long10k)));
        using var owner = model;

        var reported = Slice(model, TeamId("mgr-s1")).Member("lane")!.Value.Member("reported")!.Value;

        Assert.Equal(280, reported.Member("summary")!.Value.AsString().Length);
        Assert.EndsWith("…", reported.Member("summary")!.Value.AsString(), StringComparison.Ordinal);
        Assert.Equal(280, reported.Member("result")!.Value.AsString().Length);
    }

    [Fact]
    public void AReportOfExactlyTwoHundredAndEightyCharactersIsServedWhole()
    {
        var exact = new string('y', 280);
        var (model, _) = MakeLanes(LaneTruth(Streams(("s1", "open")), ChildDoc("mgr", "s1", summary: exact)));
        using var owner = model;

        Assert.Equal(exact, Slice(model, TeamId("mgr-s1")).Member("lane")!.Value.Member("reported")!.Value.Member("summary")!.Value.AsString());
    }

    [Fact]
    public void AManagerWithAHundredAndFiftyStreamsServesAHundredRowsAndTheTotal()
    {
        var reader = LaneTruth(Streams(Enumerable.Range(0, 150).Select(i => ($"s{i:D3}", "open")).ToArray()), ChildDoc("mgr", "s000"));
        reader.Teams.Remove(ChildPath);
        var (model, _) = MakeLanes(reader);
        using var owner = model;

        var body = Slice(model, TeamId("mgr"));

        Assert.Equal(100, body.Member("streams")!.Value.AsArray().Count);
        Assert.Equal(150, (int)body.Member("streams_total")!.Value.AsNumber());
        Assert.Equal("s099", body.Member("streams")!.Value.AsArray()[99].Member("name")!.Value.AsString());
    }

    [Fact]
    public void AMembersLeadIsServedRawAndItsRouteIsWhatTheHostResolves()
    {
        var reader = new TreeReader();
        var members = "{\"name\":\"a\",\"role\":\"implementor\",\"lead\":{\"team\":\"ghost\",\"name\":\"so\"}},{\"name\":\"b\",\"role\":\"implementor\"},{\"name\":\"c\",\"role\":\"implementor\"}";
        reader.Teams[MgrPath] = MgrDoc("{}", members);
        reader.KnownDirs.Add(Canon);
        var asked = new List<string>();
        var (model, _) = MakeLanes(reader, (dir, team, member) =>
        {
            asked.Add(dir + "|" + team + "|" + member);
            return member switch
            {
                "a" => new LaneRoute("mgr", "orchestrator", "none"),
                "c" => new LaneRoute("mgr", "orchestrator", "dissolved-lane"),
                _ => null,
            };
        });
        using var owner = model;

        var a = Slice(model, $"member:{D}/mgr/a");
        var b = Slice(model, $"member:{D}/mgr/b");
        var c = Slice(model, $"member:{D}/mgr/c");

        Assert.Equal("ghost", a.Member("lead")!.Value.Member("team")!.Value.AsString());
        Assert.Equal("none", a.Member("routes_to")!.Value.Member("via")!.Value.AsString());
        Assert.Equal("mgr", a.Member("routes_to")!.Value.Member("team")!.Value.AsString());
        Assert.Null(b.Member("lead"));
        Assert.Null(b.Member("routes_to"));
        Assert.Null(c.Member("lead"));
        Assert.Equal("dissolved-lane", c.Member("routes_to")!.Value.Member("via")!.Value.AsString());
        Assert.Contains(Canon + "|mgr|a", asked);
        Assert.DoesNotContain(asked, static x => x.EndsWith("|@orchestrator", StringComparison.Ordinal));
    }

    [Fact]
    public void WithNoHostResolverNoRouteIsServed()
    {
        var reader = new TreeReader();
        reader.Teams[MgrPath] = MgrDoc("{}", "{\"name\":\"a\",\"role\":\"implementor\",\"lead\":{\"team\":\"ghost\",\"name\":\"so\"}}");
        reader.KnownDirs.Add(Canon);
        var (model, _) = MakeLanes(reader);
        using var owner = model;

        var a = Slice(model, $"member:{D}/mgr/a");

        Assert.NotNull(a.Member("lead"));
        Assert.Null(a.Member("routes_to"));
    }

    [Fact]
    public void OpeningOrClosingAStreamWithTheChildUnchangedRefreshesTheChildAndTheManager()
    {
        var reader = LaneTruth(Streams(("s1", "open")), ChildDoc("mgr", "s1"));
        var (model, clock) = MakeLanes(reader);
        using var owner = model;
        long Version(string id) => model.Store.Current.Slices[id].Version;
        var (childBefore, managerBefore) = (Version(TeamId("mgr-s1")), Version(TeamId("mgr")));

        reader.Teams[MgrPath] = MgrDoc(Streams(("s1", "open"), ("s2", "open")));
        model.Emit("teams", TeamKey(MgrPath));
        Drain(model, clock);
        var (childOpened, managerOpened) = (Version(TeamId("mgr-s1")), Version(TeamId("mgr")));

        reader.Teams[MgrPath] = MgrDoc(Streams(("s1", "done"), ("s2", "open")));
        model.Emit("teams", TeamKey(MgrPath));
        Drain(model, clock);

        Assert.True(childOpened > childBefore && managerOpened > managerBefore);
        Assert.True(Version(TeamId("mgr-s1")) > childOpened && Version(TeamId("mgr")) > managerOpened);
        Assert.Equal("done", Slice(model, TeamId("mgr-s1")).Member("lane")!.Value.Member("state")!.Value.AsString());
    }

    [Fact]
    public void AChildChangeRefreshesTheManagersRowsAndProjectionEqualsARebuild()
    {
        var reader = LaneTruth(Streams(("s1", "open")), ChildDoc("mgr", "s1"));
        var (model, clock) = MakeLanes(reader);
        using var owner = model;

        reader.Teams[ChildPath] = ChildDoc("mgr", "s1", summary: "second", state: "reviewing");
        model.Emit("teams", TeamKey(ChildPath));
        Drain(model, clock);
        reader.Teams.Remove(ChildPath);
        model.Emit("teams", TeamKey(ChildPath));
        Drain(model, clock);

        Assert.Null(Slice(model, TeamId("mgr")).Member("streams")!.Value.AsArray()[0].Member("child_team"));
        AssertEqualsRebuild(model, reader);
    }

    [Fact]
    public void AnOpenStreamWithNoSubOrchestratorIsNoLaneSoASquattingTeamGetsOnlyAClaim()
    {
        var reader = LaneTruth("{\"s1\":{\"state\":\"open\",\"branch\":\"b\"}}", ChildDoc("mgr", "s1"));
        var (model, _) = MakeLanes(reader);
        using var owner = model;

        var child = Slice(model, TeamId("mgr-s1"));

        Assert.Null(child.Member("lane"));
        Assert.NotNull(child.Member("lane_claim"));
        Assert.Null(Slice(model, TeamId("mgr")).Member("streams")!.Value.AsArray()[0].Member("child_team"));
    }

    [Fact]
    public void EveryTextTakenFromAChildsFileIsServedBounded()
    {
        var big = new string('z', 10_000);
        var child = "{\"team_id\":\"tc\",\"orchestrator\":{\"session_id\":\"so\",\"pid\":2,\"pid_start\":\"o2\"},\"members\":[{\"name\":\"a\",\"role\":\"implementor\",\"lead\":{\"team\":\"" + big + "\",\"name\":\"" + big + "\"}}],"
            + "\"parent\":{\"team\":\"" + big + "\",\"stream\":\"" + big + "\"},\"workstream\":{\"state\":\"" + big + "\",\"summary\":\"x\",\"updated\":\"" + big + "\"}}";
        var reader = LaneTruth(Streams(("s1", "open")), child);
        reader.Teams[MgrPath] = MgrDoc(Streams(("s1", "open"), (big, "open")));
        var (model, _) = MakeLanes(reader);
        using var owner = model;

        var childBody = Slice(model, TeamId("mgr-s1"));
        var claim = childBody.Member("lane_claim")!.Value.Member("parent_team")!.Value.AsString();
        var member = Slice(model, $"member:{D}/mgr-s1/a").Member("lead")!.Value;

        Assert.Equal(280, claim.Length);
        Assert.Equal(280, member.Member("team")!.Value.AsString().Length);
        Assert.Equal(280, member.Member("name")!.Value.AsString().Length);
        Assert.DoesNotContain(big[..300], Body(model, TeamId("mgr-s1")), StringComparison.Ordinal);
        Assert.DoesNotContain(big[..300], Body(model, TeamId("mgr")), StringComparison.Ordinal);
        Assert.Equal(["s1"], Slice(model, TeamId("mgr")).Member("streams")!.Value.AsArray().Select(r => r.Member("name")!.Value.AsString()));
    }

    [Fact]
    public void AConfirmedChildsReportedStateAndUpdatedAreBounded()
    {
        var big = new string('q', 10_000);
        var child = ChildDoc("mgr", "s1", state: big).Replace("2026-06-01T11:30:00Z", big);
        var (model, _) = MakeLanes(LaneTruth(Streams(("s1", "open")), child));
        using var owner = model;

        var reported = Slice(model, TeamId("mgr-s1")).Member("lane")!.Value.Member("reported")!.Value;

        Assert.Equal(280, reported.Member("state")!.Value.AsString().Length);
        Assert.Equal(280, reported.Member("updated")!.Value.AsString().Length);
    }

    [Fact]
    public void OnlyTheManagersOtherStreamChangingStillMovesTheChildsVersion()
    {
        var reader = LaneTruth(Streams(("s1", "open"), ("s2", "open")), ChildDoc("mgr", "s1"));
        var (model, clock) = MakeLanes(reader);
        using var owner = model;
        var before = model.Store.Current.Slices[TeamId("mgr-s1")];

        reader.Teams[MgrPath] = MgrDoc(Streams(("s1", "open"), ("s2", "done")));
        model.Emit("teams", TeamKey(MgrPath));
        Drain(model, clock);
        var after = model.Store.Current.Slices[TeamId("mgr-s1")];

        Assert.True(after.Version > before.Version);
        Assert.NotEqual(Body(model, TeamId("mgr-s1")), Encoding.UTF8.GetString(before.Body));
    }

    private sealed class Records(Dictionary<(string Lane, string Key), LaneRecord> all) : IRecordView
    {
        public LaneRecord? Get(string lane, string key) => all.GetValueOrDefault((lane, key));

        public IReadOnlyList<string> Keys(string lane) => [.. all.Keys.Where(k => k.Lane == lane).Select(static k => k.Key).Order(StringComparer.Ordinal)];
    }

    [Fact]
    public void AProcessTableChangeRecomposesOnlyTheMembersThatNameALeadInThatDirectory()
    {
        var members = "{\"name\":\"a\",\"role\":\"implementor\",\"lead\":{\"team\":\"mgr-s1\",\"name\":\"so\"}},{\"name\":\"b\",\"role\":\"implementor\"}";
        var key = TeamLane.KeyOf(Canon, MgrPath);
        var records = new Records(new() { [(TeamLane.LaneName, key)] = new LaneRecord(JsonValue.FromObject(TeamLane.ValueOf(Canon, MgrPath, "mgr", JsonText.Parse(MgrDoc("{}", members)))), 0) });
        var composer = new MemberComposer();

        var hit = composer.Affected(new LaneKey(ProcessesLane.LaneName, Canon), null, new LaneRecord(JsonValue.Null, 1), records);
        var elsewhere = composer.Affected(new LaneKey(ProcessesLane.LaneName, "/other/.claude/ah2"), null, new LaneRecord(JsonValue.Null, 1), records);

        Assert.Equal([$"member:{D}/mgr/a"], hit);
        Assert.Empty(elsewhere);
    }

    [Fact]
    public void TheLaneSlicesAreTheCommittedGoldenTheWebClientReads()
    {
        var reader = LaneTruth(Streams(("s1", "open"), ("s2", "done")), ChildDoc("mgr", "s1", result: "merged"));
        reader.Teams[MgrPath] = MgrDoc(Streams(("s1", "open"), ("s2", "done")), "{\"name\":\"dev\",\"role\":\"implementor\",\"lead\":{\"team\":\"mgr-s1\",\"name\":\"so-s1\"}},{\"name\":\"gone\",\"role\":\"implementor\"}");
        var (model, _) = MakeLanes(reader, (_, _, member) => member switch
        {
            "dev" => new LaneRoute("mgr-s1", "orchestrator", "lead"),
            "gone" => new LaneRoute("mgr", "orchestrator", "dissolved-lane"),
            _ => null,
        });
        using var owner = model;
        var slices = new JsonArray();
        foreach (var id in new[] { TeamId("mgr"), TeamId("mgr-s1"), $"member:{D}/mgr/dev", $"member:{D}/mgr/gone" })
        {
            slices.Add(JsonValue.FromObject(new JsonObject { ["id"] = id, ["body"] = Slice(model, id) }));
        }

        ModelGolden.Check(new JsonObject { ["slices"] = JsonValue.FromArray(slices) }, "lane-slices");
    }
}
