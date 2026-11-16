using Ah.Core.Json;

namespace Ah.Cli.Tests;

public sealed partial class RosterTeamVerbTests
{
    private string RowWith(string settings) =>
        "{\"role\":\"implementor\",\"name\":\"proj-implementor\",\"route\":\"peer\",\"transport_id\":\"%1\"" + settings + ",\"expected_root\":\"" + Project + "\"}";

    /// <summary>
    /// A snapshot taken of a member whose row records no model or effort; the team file then holds the row with <paramref name="settings"/>
    /// when the resume runs. Returns the launch line the resume sent to the new pane.
    /// </summary>
    private string LaunchLineOfAResumeWhoseSnapshotLacksTheSettings(string settings, bool noMultiplexer = false)
    {
        var home = ResumeHome();
        _sandbox.PlantTeam(Path.Combine(home, "teams", "proj.json"), LiveTeamJson("tmux", RowWith(string.Empty)));
        WriteHomeGrants(home, "log", "{\"proj-implementor\":{\"work_root\":\"" + FamilyWorkTree("rf") + "\",\"stream\":null,\"role\":\"implementor\",\"extra\":[]}}");
        var (snapshotCode, snapshotOut, snapshotErr) = Roster("snapshot", "--team", "proj", "--cwd", Project);
        Assert.True(snapshotCode == 0, snapshotOut + snapshotErr);
        var snapshot = JsonText.Parse(snapshotOut).Member("snapshot")!.Value.AsString();
        DismissEverything(home);
        _sandbox.PlantTeam(Path.Combine(home, "teams", "proj.json"), LiveTeamJson("tmux", RowWith(settings)));

        if (noMultiplexer)
        {
            _runner.Exit = argv => argv.Length > 0 && argv[0] == "tmux" ? 1 : 0;
        }

        var (code, _, text) = ResumeYes(snapshot);

        Assert.True(code == 0, text);
        return string.Join(" | ", _runner.Calls.Select(argv => string.Join(' ', argv)));
    }

    [Fact]
    public void AnInteractiveResumeInsideTmuxStillGetsTheTmuxShape()
    {
        var line = LaunchLineOfAResumeWhoseSnapshotLacksTheSettings(",\"model\":\"opus\"");

        Assert.Contains("new-window", line, StringComparison.Ordinal);
        Assert.DoesNotContain(" --bg", line, StringComparison.Ordinal);
    }

    [Fact]
    public void AnInteractiveResumeWithNoHerdrAndNoTmuxStillGetsTheTerminalShape()
    {
        var line = LaunchLineOfAResumeWhoseSnapshotLacksTheSettings(",\"model\":\"opus\"", noMultiplexer: true);

        Assert.Contains(" --bg", line, StringComparison.Ordinal);
        Assert.DoesNotContain("new-window", line, StringComparison.Ordinal);
    }

    [Fact]
    public void AResumeWhoseSnapshotLacksModelAndEffortTakesThemFromTheMembersTeamFileRow()
    {
        var line = LaunchLineOfAResumeWhoseSnapshotLacksTheSettings(",\"model\":\"opus\",\"effort\":\"medium\"");

        Assert.Contains("--model opus", line, StringComparison.Ordinal);
        Assert.Contains("--effort medium", line, StringComparison.Ordinal);
    }

    [Fact]
    public void AResumeWhoseTeamFileRowHasNoEffortOmitsOnlyTheEffortFlag()
    {
        var line = LaunchLineOfAResumeWhoseSnapshotLacksTheSettings(",\"model\":\"opus\"");

        Assert.Contains("--model opus", line, StringComparison.Ordinal);
        Assert.DoesNotContain("--effort", line, StringComparison.Ordinal);
    }
}
