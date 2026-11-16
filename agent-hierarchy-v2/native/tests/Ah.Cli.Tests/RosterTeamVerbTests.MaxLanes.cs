using Ah.Core.Json;

namespace Ah.Cli.Tests;

/// <summary>The team's cap on live sub-orchestrators: <c>max_lanes</c>, its default, who sets it and how a bad value reads.</summary>
public sealed partial class RosterTeamVerbTests
{
    private void TeamWithLiveLanes(int live, string extra = "")
    {
        var names = Enumerable.Range(1, live).Select(i => "l" + i).ToArray();
        var streams = string.Join(",", names.Select(w => OpenStreamJson(w, "proj-so-" + w)).Append(OpenStreamJson("s1")));
        WriteTeam("proj", SoTeam(streams, string.Join(",", names.Select(w => SoMember(w, live: true))), extra));
    }

    private (int Code, string Output) LaneCaps(params string[] args)
    {
        var (code, stdout, stderr) = Roster(["lane-caps", .. args, "--team", "proj", "--orchestrator-pid", Self]);
        return (code, stdout + stderr);
    }

    [Fact]
    public void WithoutAMaxLanesTheSixthLiveSubOrchestratorIsAllowedAndTheSeventhIsRefusedNamingTheDefault()
    {
        TeamWithLiveLanes(5);
        var (sixthCode, _) = SpawnSo("--stream", "s1");
        TeamWithLiveLanes(6);
        var (seventhCode, seventh) = SpawnSo("--stream", "s1");

        Assert.Equal(0, sixthCode);
        Assert.Equal(2, seventhCode);
        Assert.Equal("sub-orchestrator-breadth", seventh["refused"].AsString());
        Assert.Contains("already has 6 live sub-orchestrators (max_lanes 6, the default); raise it with ah roster lane-caps --max-lanes <N>; nothing was launched", seventh["message"].AsString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ARaisedMaxLanesAllowsTheEighthAndRefusesTheNinthNamingTheValueWithoutTheDefault()
    {
        TeamWithLiveLanes(7);
        var (setCode, setOut) = LaneCaps("--max-lanes", "8");
        var (eighthCode, _) = SpawnSo("--stream", "s1");
        var recorded = (int)JsonText.Parse(_sandbox.TeamText(TeamPath("proj"))).Member("max_lanes")!.Value.AsNumber();
        TeamWithLiveLanes(8, ",\"max_lanes\":8");
        var (ninthCode, ninth) = SpawnSo("--stream", "s1");

        Assert.True(setCode == 0, setOut);
        Assert.Equal(8, recorded);
        Assert.Equal(0, eighthCode);
        Assert.Equal(2, ninthCode);
        Assert.Contains("(max_lanes 8 (team)); raise it",ninth["message"].AsString(), StringComparison.Ordinal);
        Assert.DoesNotContain("the default", ninth["message"].AsString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheVerbWritesMaxLanesOnTheTeamAndTheSpawnReadsIt()
    {
        TeamWithLiveLanes(3);
        var (setCode, setOut) = LaneCaps("--max-lanes", "3");
        var (code, refused) = SpawnSo("--stream", "s1");

        Assert.True(setCode == 0, setOut);
        Assert.Equal(3, (int)JsonText.Parse(_sandbox.TeamText(TeamPath("proj"))).Member("max_lanes")!.Value.AsNumber());
        Assert.Equal(2, code);
        Assert.Contains("(max_lanes 3 (team));",refused["message"].AsString(), StringComparison.Ordinal);
    }

    [Fact]
    public void LoweringMaxLanesBelowTheLiveCountDismissesNoOneRefusesTheNextSpawnAndLaneCapsShowsIt()
    {
        TeamWithLiveLanes(3);
        var (setCode, _) = LaneCaps("--max-lanes", "2");
        var (code, _) = SpawnSo("--stream", "s1");
        var (showCode, shown) = LaneCaps();

        Assert.Equal(0, setCode);
        Assert.Equal(2, code);
        Assert.Equal(3, JsonText.Parse(_sandbox.TeamText(TeamPath("proj"))).Member("members")!.Value.AsArray().Count);
        Assert.Equal(0, showCode);
        Assert.Equal(2, (int)JsonText.Parse(shown).Member("max_lanes")!.Value.AsNumber());
        Assert.False(JsonText.Parse(shown).Member("max_lanes_default")!.Value.AsBool());
    }

    [Fact]
    public void LaneCapsWithNoFlagsShowsTheDefaultMaxLanesAndEachLanesCaps()
    {
        WriteTeam("proj", SoTeam(OpenStreamJson("s1")));

        var (code, shown) = LaneCaps();

        Assert.Equal(0, code);
        var parsed = JsonText.Parse(shown);
        Assert.Equal(6, (int)parsed.Member("max_lanes")!.Value.AsNumber());
        Assert.True(parsed.Member("max_lanes_default")!.Value.AsBool());
        Assert.Equal(4, (int)parsed.Member("lanes")!.Value.Member("s1")!.Value.Member("members")!.Value.AsNumber());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("17")]
    [InlineData("abc")]
    [InlineData("2.5")]
    public void NegativeAMaxLanesOutsideOneToSixteenIsRefusedAndWritesNothing(string value)
    {
        WriteTeam("proj", SoTeam(OpenStreamJson("s1")));
        var before = _sandbox.TeamText(TeamPath("proj"));

        var (code, output) = LaneCaps("--max-lanes", value);

        Assert.Equal(2, code);
        Assert.Contains("max-lanes-range", output, StringComparison.Ordinal);
        Assert.Contains("ah: --max-lanes must be an integer from 1 to 16", output, StringComparison.Ordinal);
        Assert.Equal(before, _sandbox.TeamText(TeamPath("proj")));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("16")]
    public void TheEdgesOfTheMaxLanesRangeAreAccepted(string value)
    {
        WriteTeam("proj", SoTeam(OpenStreamJson("s1")));

        var (code, output) = LaneCaps("--max-lanes", value);

        Assert.True(code == 0, output);
        Assert.Equal(int.Parse(value, System.Globalization.CultureInfo.InvariantCulture), (int)JsonText.Parse(_sandbox.TeamText(TeamPath("proj"))).Member("max_lanes")!.Value.AsNumber());
    }

    [Theory]
    [InlineData("99")]
    [InlineData("0")]
    [InlineData("2.5")]
    [InlineData("\"x\"")]
    public void AHandEditedMaxLanesOutOfRangeReadsAsTheDefaultAndDoctorWarnsOnce(string recorded)
    {
        TeamWithLiveLanes(6, ",\"max_lanes\":" + recorded);

        var (code, refused) = SpawnSo("--stream", "s1");
        var (_, doctorOut, doctorErr) = Roster("doctor", "--team", "proj", "--orchestrator-pid", Self);

        Assert.Equal(2, code);
        Assert.Contains("(max_lanes 6, the default)", refused["message"].AsString(), StringComparison.Ordinal);
        var all = doctorOut + doctorErr;
        var inJson = recorded.Replace("\"", "\\\"", StringComparison.Ordinal);
        Assert.Contains($"team proj: max_lanes {inJson} is not 1..16; using 6", all, StringComparison.Ordinal);
        Assert.Equal(1, all.Split("max_lanes " + inJson + " is not 1..16", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void NegativeAValidOrAbsentMaxLanesRaisesNoDoctorRow()
    {
        TeamWithLiveLanes(2, ",\"max_lanes\":9");

        var (_, doctorOut, doctorErr) = Roster("doctor", "--team", "proj", "--orchestrator-pid", Self);
        WriteTeam("proj", SoTeam(OpenStreamJson("s1")));
        var (_, absentOut, absentErr) = Roster("doctor", "--team", "proj", "--orchestrator-pid", Self);

        Assert.DoesNotContain("max_lanes", doctorOut + doctorErr, StringComparison.Ordinal);
        Assert.DoesNotContain("max_lanes", absentOut + absentErr, StringComparison.Ordinal);
    }

    [Fact]
    public void MaxLanesWithAStreamIsRefusedAsTeamWideAndWritesNothing()
    {
        WriteTeam("proj", SoTeam(OpenStreamJson("s1")));
        var before = _sandbox.TeamText(TeamPath("proj"));

        var (code, output) = LaneCaps("--max-lanes", "4", "--stream", "s1");

        Assert.Equal(2, code);
        Assert.Contains("max-lanes-is-team-wide", output, StringComparison.Ordinal);
        Assert.Equal(before, _sandbox.TeamText(TeamPath("proj")));
    }

    private void RosterConfig(string rosterJson)
    {
        var file = Path.Combine(_sandbox.Home, ".claude", "ah2", "agent-hierarchy.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "{\"version\":1,\"enabled\":true," + rosterJson + "}");
    }

    private static string Block(string maxLanes) =>
        "{\"route\":\"peer\",\"members\":[{\"role\":\"implementor\"}]" + (maxLanes.Length == 0 ? string.Empty : ",\"max_lanes\":" + maxLanes) + "}";

    [Fact]
    public void ADryRunSpawnOverTheCapIsRefusedWithTheSameTextAndLaunchesNothing()
    {
        TeamWithLiveLanes(6);
        var before = _sandbox.TeamText(TeamPath("proj"));

        var (code, stdout, stderr) = Roster(["spawn-one", "sub-orchestrator", "--team", "proj", "--orchestrator-pid", Self, "--dry-run", "--stream", "s1"]);

        Assert.Equal(2, code);
        Assert.Contains("(max_lanes 6, the default); raise it with ah roster lane-caps --max-lanes <N>; nothing was launched", stdout + stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("\"planned\"", stdout, StringComparison.Ordinal);
        Assert.Equal(before, _sandbox.TeamText(TeamPath("proj")));
    }

    [Fact]
    public void ARosterBlocksMaxLanesIsTheCapWhenTheTeamRecordHasNoneAndTheRefusalNamesTheBlock()
    {
        RosterConfig("\"roster\":" + Block("8"));
        TeamWithLiveLanes(7);
        var (belowCode, _) = SpawnSo("--stream", "s1");
        TeamWithLiveLanes(8);

        var (code, refused) = SpawnSo("--stream", "s1");

        Assert.Equal(0, belowCode);
        Assert.Equal(2, code);
        Assert.Contains("(max_lanes 8 from roster (default))", refused["message"].AsString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheTeamRecordsMaxLanesBeatsTheRosterBlocks()
    {
        RosterConfig("\"roster\":" + Block("8"));
        TeamWithLiveLanes(4, ",\"max_lanes\":4");

        var (code, refused) = SpawnSo("--stream", "s1");

        Assert.Equal(2, code);
        Assert.Contains("(max_lanes 4 (team))", refused["message"].AsString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ANamedRostersMaxLanesAppliesToATeamBuiltFromItAndADefaultBlockTeamStaysAtSix()
    {
        RosterConfig("\"roster\":" + Block(string.Empty) + ",\"rosters\":{\"x\":" + Block("10") + "}");
        TeamWithLiveLanes(10, ",\"roster\":\"x\"");
        var (namedCode, named) = SpawnSo("--stream", "s1");
        TeamWithLiveLanes(6);

        var (code, refused) = SpawnSo("--stream", "s1");

        Assert.Equal(2, namedCode);
        Assert.Contains("(max_lanes 10 from roster x)", named["message"].AsString(), StringComparison.Ordinal);
        Assert.Equal(2, code);
        Assert.Contains("(max_lanes 6, the default)", refused["message"].AsString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("17")]
    [InlineData("\"abc\"")]
    public void AnInvalidRosterMaxLanesReadsAsSixAndDoctorWarnsAndATeamValueStillWins(string recorded)
    {
        RosterConfig("\"roster\":" + Block(recorded));
        TeamWithLiveLanes(6);

        var (code, refused) = SpawnSo("--stream", "s1");
        var (_, doctorOut, doctorErr) = Roster("doctor", "--team", "proj", "--orchestrator-pid", Self);
        TeamWithLiveLanes(6, ",\"max_lanes\":9");
        var (teamWinsCode, _) = SpawnSo("--stream", "s1");

        Assert.Equal(2, code);
        Assert.Contains("(max_lanes 6, the default)", refused["message"].AsString(), StringComparison.Ordinal);
        var all = doctorOut + doctorErr;
        var inJson = recorded.Replace("\"", "\\\"", StringComparison.Ordinal);
        Assert.Contains($"roster (default) (", all, StringComparison.Ordinal);
        Assert.Contains($"max_lanes {inJson} is not 1..16; using 6", all, StringComparison.Ordinal);
        Assert.Equal(0, teamWinsCode);
    }

    [Fact]
    public void LoweringTheRostersMaxLanesRefusesTheNextSpawnWithoutDismissingAnyone()
    {
        RosterConfig("\"roster\":" + Block("8"));
        TeamWithLiveLanes(6);
        var (beforeCode, _) = SpawnSo("--stream", "s1");
        var before = _sandbox.TeamText(TeamPath("proj"));
        RosterConfig("\"roster\":" + Block("5"));

        var (code, refused) = SpawnSo("--stream", "s1");

        Assert.Equal(0, beforeCode);
        Assert.Equal(2, code);
        Assert.Contains("(max_lanes 5 from roster (default))", refused["message"].AsString(), StringComparison.Ordinal);
        Assert.Equal(before, _sandbox.TeamText(TeamPath("proj")));
    }

    [Fact]
    public void CreateWritesNoMaxLanesIntoTheTeamRecordSoTheRosterIsReadLive()
    {
        RosterConfig("\"roster\":" + Block("8"));
        var verified = "[{\"role\":\"implementor\",\"name\":\"proj-implementor\",\"route\":\"peer\",\"expected_root\":\"" + Project + "\"}]";

        var (code, _, stderr) = Roster("create", "--commit", "--verified", verified, "--transport", "terminal", "--roster-level", "repo", "--orchestrator-pid", Self);

        Assert.True(code == 0, stderr);
        Assert.Null(JsonText.Parse(_sandbox.TeamText(TeamPath("proj"))).Member("max_lanes"));
    }

    [Fact]
    public void ASpawnOverTheCapThatIsNotADryRunIsRefusedWithTheSameTextAndAddsNoRow()
    {
        TeamWithLiveLanes(6);
        var before = _sandbox.TeamText(TeamPath("proj"));

        var (code, stdout, stderr) = Roster("spawn-ad-hoc", "sub-orchestrator", "--model", "inherit", "--team", "proj", "--orchestrator-pid", Self, "--stream", "s1");

        Assert.Equal(2, code);
        Assert.Contains("(max_lanes 6, the default); raise it with ah roster lane-caps --max-lanes <N>; nothing was launched", stdout + stderr, StringComparison.Ordinal);
        Assert.Equal(before, _sandbox.TeamText(TeamPath("proj")));
    }
}
