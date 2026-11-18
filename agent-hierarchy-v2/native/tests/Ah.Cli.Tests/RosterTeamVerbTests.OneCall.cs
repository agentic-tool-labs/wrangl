using Ah.Core.Json;

namespace Ah.Cli.Tests;

/// <summary>b298: a spawn places its pane in one call, create plans and commits in one call, and a clean dismiss closes in one call.</summary>
public sealed partial class RosterTeamVerbTests
{
    [Fact]
    public void SpawnHereSplitsTheCallersPaneAndSaysHowToBriefTheMember()
    {
        var herdr = OnHerdr(string.Empty);

        var (code, spawned) = SpawnReviewer("--here");

        Assert.Equal(0, code);
        Assert.True(Called("herdr", "pane", "split", "--pane", "pOrch", "--direction", "right"));
        Assert.False(Called("herdr", "tab", "create"));
        var next = spawned["next"].AsObject();
        Assert.Equal("tCaller", next["tab"].AsString());
        Assert.Equal(spawned["member"].AsObject()["transport_id"].AsString(), next["terminal"].AsString());
        Assert.Contains(herdr.Panes, p => p.Pane == next["terminal"].AsString() && p.Tab == "tCaller");
        Assert.Contains("msg new --to reviewer --from orchestrator --to-name " + next["name"].AsString(), next["brief"].AsString(), StringComparison.Ordinal);
    }

    [Fact]
    public void SpawnWithBothHereAndTabIsRefusedBeforeAnyPaneIsMade()
    {
        _ = OnHerdr(string.Empty);

        var (code, _) = SpawnReviewer("--here", "--tab", "tCaller");

        Assert.NotEqual(0, code);
        Assert.False(Called("herdr", "pane", "split"));
    }

    [Fact]
    public void CreateYesPlansLaunchesAndRecordsTheTeamInOneCall()
    {
        WriteGlobalRosterMembers("""[{"role":"implementor","model":"opus"}]""");
        _sandbox.Executable("bin/hc");
        _extraEnv["PATH"] = _sandbox.Bin;
        _runner.Exit = argv => argv.Contains("herdr-app") ? 1 : 0;
        _runner.Output = argv => argv.Contains("get") ? """{"error":{"code":"agent_not_found"}}""" : "{}";

        var (code, stdout, stderr) = Roster("create", "--yes", "--transport", "terminal", "--roster-level", "global", "--orchestrator-pid", Self);

        Assert.True(code == 0, stdout + stderr);
        var output = JsonText.Parse(stdout);
        Assert.True(output.Member("plan") is { IsMap: true }, stdout);
        Assert.True(output.Member("commit") is { IsMap: true }, stdout);
        var members = JsonText.Parse(_sandbox.TeamText(TeamPath("proj"))).Member("members")!.Value.AsArray();
        Assert.Contains(members, m => m.Member("role")?.AsString() == "implementor");
    }

    [Fact]
    public void CreateYesWithPlanIsRefused()
    {
        var (code, _, stderr) = Roster("create", "--yes", "--plan");

        Assert.NotEqual(0, code);
        Assert.Contains("mutually exclusive", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void ABareDismissClosesACleanMemberInOneCall()
    {
        ArrangeCleanImplementor();

        var (code, stdout, stderr) = Roster(["dismiss", Impl, "--team", "proj"]);

        Assert.True(code == 0, stdout + stderr);
        Assert.True(Killed("%1"));
    }

    [Fact]
    public void ABareDismissOfAMemberThatIsNotCleanRefusesWithItsReasonsAndClosesNothing()
    {
        ArrangeCleanImplementor(streamState: "open");

        var (code, stdout, stderr) = Roster(["dismiss", Impl, "--team", "proj"]);

        Assert.NotEqual(0, code);
        Assert.Contains("stream s1 is not done", stdout + stderr, StringComparison.Ordinal);
        Assert.Contains("has no uncommitted or unmerged work", stdout + stderr, StringComparison.Ordinal);
        Assert.False(Killed("%1"));
    }

    /// <summary>
    /// An implementor whose only dirt is the untracked file <c>old.txt</c>, written now and with its mtime set a day back; the row's
    /// launch times are <paramref name="launchTimes"/> (minutes from now, negative is the future, as <see cref="StampNow"/> takes them).
    /// </summary>
    private string ReleaseWithUntrackedFile(string launchTimes)
    {
        ArrangeImplementor();
        Directory.CreateDirectory(Project);
        var file = Path.Combine(Project, "old.txt");
        File.WriteAllText(file, "x");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-1));
        WriteTeam("proj", LiveTeamJson("tmux", Member("implementor", Impl, "%1").TrimEnd('}') + "," + launchTimes + "}"));
        _porcelain = "?? old.txt\n";
        return PlanOfImplementor().Status;
    }

    [Fact]
    public void AnUntrackedFileFromBeforeTheMemberWasLaunchedDoesNotHoldIt() =>
        Assert.Equal("ready", ReleaseWithUntrackedFile($"\"spawned_at\":\"{StampNow(-1)}\""));

    [Fact]
    public void AnUntrackedFileWhoseMtimeWasSetBackButWasMadeAfterTheLaunchStillHoldsIt() =>
        Assert.Equal("dirty", ReleaseWithUntrackedFile($"\"spawned_at\":\"{StampNow(60)}\""));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ARelaunchOfTheSameMemberKeepsItsFirstLaunchTimeOnlyInTheSameTree(bool sameTree)
    {
        var root = Path.GetFullPath(Project);
        Directory.CreateDirectory(root);
        var priorRoot = sameTree ? root : Path.Combine(_sandbox.Root, "elsewhere");
        Directory.CreateDirectory(priorRoot);
        var first = StampNow(60);
        _ = OnHerdr($"{{\"role\":\"implementor\",\"name\":\"proj-implementor\",\"route\":\"peer\",\"transport_id\":\"pGone\",\"model\":\"sonnet\",\"expected_root\":\"{priorRoot}\",\"spawned_at\":\"{first}\"}}");
        WriteGlobalRosterMembers("""[{"role":"implementor","model":"sonnet"}]""");

        var (code, stdout, stderr) = Roster("spawn-one", "implementor", "--team", "proj", "--orchestrator-pid", Self, "--allow-main", "--cwd", root);

        Assert.True(code == 0, stdout + stderr);
        var row = JsonText.Parse(_sandbox.TeamText(TeamPath("proj"))).Member("members")!.Value.AsArray().Single(m => m.Member("name")?.AsString() == "proj-implementor");
        Assert.True((sameTree ? first : null) == row.Member("first_spawned_at")?.AsString(), JsonText.Stringify(row, 0));
        Assert.NotEqual(first, row.Member("spawned_at")?.AsString());
    }

    [Fact]
    public void AFileWrittenAfterTheFirstLaunchStillHoldsAMemberThatWasResumedSince() =>
        Assert.Equal("dirty", ReleaseWithUntrackedFile($"\"first_spawned_at\":\"{StampNow(60)}\",\"spawned_at\":\"{StampNow(-1)}\""));
}
