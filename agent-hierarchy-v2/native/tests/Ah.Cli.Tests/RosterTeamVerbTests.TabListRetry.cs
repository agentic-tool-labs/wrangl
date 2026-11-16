namespace Ah.Cli.Tests;

/// <summary>b472: a slow herdr tab list is tried three times before a stream spawn is refused, and a recorded stream tab stands in for it.</summary>
public sealed partial class RosterTeamVerbTests
{
    private static bool IsTabList(string[] argv) => argv.Length > 2 && argv[0] == "herdr" && argv[1] == "tab" && argv[2] == "list";

    /// <summary>Stream s1 recorded in tab tS, which holds one shell pane; the first <paramref name="timeouts"/> tab lists time out.</summary>
    private HerdrFake WithSlowTabList(int timeouts, bool recorded)
    {
        _stepClock ??= new StepClock(DateTimeOffset.UtcNow);
        var herdr = WithStreamTab("open");
        herdr.Panes.RemoveAll(p => p.Pane == "pOrch");
        herdr.Panes.Add(("pShell", "tS"));
        if (!recorded)
        {
            WriteTeam("proj", _sandbox.TeamText(TeamPath("proj")).Replace("\"tab_id\":\"tS\",\"opened\"", "\"tab_id\":null,\"opened\"", StringComparison.Ordinal));
        }

        var seen = 0;
        _runner.Truncated = argv => IsTabList(argv) && ++seen <= timeouts;
        return herdr;
    }

    /// <summary>The tab lists made while placing the pane: those before the launch (a tab list after it is not the anchor's).</summary>
    private int TabListsBeforeLaunch() => _runner.Calls.TakeWhile(argv => argv[0] != "/bin/sh").Count(IsTabList);

    private string LaunchLine() => _runner.Calls.Where(argv => argv[0] == "/bin/sh").Select(argv => argv[^1]).LastOrDefault() ?? string.Empty;

    [Fact]
    public void ATabListThatTimesOutTwiceThenAnswersStillPlacesTheSpawnInTheListedTab()
    {
        WithSlowTabList(timeouts: 2, recorded: true);

        var (code, stdout, err) = Roster("spawn-ad-hoc", "implementor", "--model", "sonnet", "--team", "proj", "--orchestrator-pid", Self, "--stream", "s1", "--cwd", Project);

        Assert.True(code == 0, err + stdout);
        Assert.Equal(3, TabListsBeforeLaunch());
        Assert.Contains("herdr tab list slow, try 1 of 3", err, StringComparison.Ordinal);
        Assert.Contains("herdr tab list slow, try 2 of 3", err, StringComparison.Ordinal);
        Assert.DoesNotContain("try 3 of 3", err, StringComparison.Ordinal);
        Assert.Contains("--pane pShell", LaunchLine(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ATabListThatAlwaysTimesOutUsesTheRecordedStreamTabElseSpawnsAnyway(bool recorded)
    {
        WithSlowTabList(timeouts: int.MaxValue, recorded);

        var (code, stdout, err) = Roster("spawn-ad-hoc", "implementor", "--model", "sonnet", "--team", "proj", "--orchestrator-pid", Self, "--stream", "s1", "--cwd", Project);

        Assert.Equal(3, TabListsBeforeLaunch());
        if (recorded)
        {
            Assert.True(code == 0, err + stdout);
            Assert.Contains("--pane pShell", LaunchLine(), StringComparison.Ordinal);
            Assert.DoesNotContain(_runner.Calls, argv => argv.Length > 2 && argv[1] == "tab" && argv[2] == "create");
        }
        else
        {
            // b536: a tab list that never answers no longer refuses the spawn when no tab is recorded.
            Assert.True(code == 0, err + stdout);
        }
    }
}
