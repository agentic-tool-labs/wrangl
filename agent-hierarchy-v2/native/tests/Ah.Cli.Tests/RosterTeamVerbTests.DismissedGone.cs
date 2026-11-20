using Ah.Core.Json;
using Ah.Hierarchy;

namespace Ah.Cli.Tests;

/// <summary>ah2-313: a dismissed member leaves the roster and the queue alarms; a close that leaves the session says so and fails.</summary>
public sealed partial class RosterTeamVerbTests
{
    /// <summary>Two implementors on tmux, both idle an hour, with an implementor item queued; <paramref name="listed"/> is what tmux lists, before and after any close.</summary>
    private void ArrangeIdlePair(string listed)
    {
        // a codex member is a pane member: the queue reads its liveness and idleness from its pane activity record
        static string Codex(string row) => row.Replace("\"route\":\"peer\"", "\"route\":\"pane\",\"kind\":\"codex\"", StringComparison.Ordinal);
        WriteTeam("proj", LiveTeamJson("tmux", string.Join(",", Codex(Member("implementor", "proj-a", "%1")), Codex(Member("implementor", "proj-b", "%2")))));
        ScriptGit();
        _runner.Output = argv => argv.Contains("list-panes") ? listed : string.Empty;
        var hourAgo = DateTimeOffset.UtcNow.AddHours(-1).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);
        foreach (var name in new[] { "proj-a", "proj-b" })
        {
            _sandbox.PlantActivity(Hierarchy, "pane-" + name, "{\"activity\":\"idle\",\"at\":\"" + hourAgo + "\"}\n");
        }

        var queued = JobGraph().Hierarchy.Progress.RecordChecklist(Hierarchy, "proj", "_run", "orchestrator@proj", [new PlanInput("stab", "add", "Do stab"), new PlanInput("stab", "next"), new PlanInput("stab", "role", Role: "implementor")]);
        Assert.Null(queued.Refused);
    }

    /// <summary>Who a queue-stall evaluation of team proj would dispatch the queued item to, an hour from now.</summary>
    private IReadOnlyList<string> QueueStallTargets()
    {
        var graph = JobGraph();
        var record = graph.Roster.ReadTeamRecord(Hierarchy, "proj");
        var (members, dispatches) = Ah.Watch.WatchModule.QueueFactsFromFiles(graph.Hierarchy, record, Hierarchy, "proj", _ => false);
        var entries = HierarchyModule.ChecklistEntries(graph.Hierarchy.Progress, Hierarchy, "proj");
        return Assert.Single(HierarchyModule.QueueAll(entries, members, dispatches, DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds())).Eligible;
    }

    private (int Code, string Stdout, string Stderr) DismissProjA() =>
        Roster("dismiss", "proj-a", "--close", "--confirm", "--plan-token", TokenOf(PlanMany("proj-a").Out), "--ignore-release", "proj-a", "--team", "proj");

    [Fact]
    public void ADismissedMemberHasNoTeamsRowAndNoQueueStallNamesItWhileTheLiveMemberIsStillNamed()
    {
        ArrangeIdlePair("%2\n");
        Assert.Equal(["proj-a", "proj-b"], QueueStallTargets().Order(StringComparer.Ordinal));

        var (code, _, stderr) = DismissProjA();

        Assert.True(code == 0, stderr);
        var (teamsCode, teams, teamsErr) = Roster("teams");
        Assert.True(teamsCode == 0, teamsErr);
        Assert.DoesNotContain("proj-a", teams, StringComparison.Ordinal);
        var row = Assert.Single(JsonText.Parse(teams).Member("teams")!.Value.AsArray(), t => t.Member("name")?.AsString() == "proj");
        Assert.Equal(1, row.Member("member_rows")!.Value.AsNumber());
        Assert.Equal(["proj-b"], QueueStallTargets());
    }

    [Fact]
    public void ADismissWhosePaneIsStillListedSaysWhichPaneItLeftExitsOneAndKeepsTheRow()
    {
        ArrangeIdlePair("%1\n%2\n");

        var (code, stdout, stderr) = DismissProjA();

        Assert.Equal(1, code);
        Assert.Contains("proj-a was not closed — it left pane %1 (still listed after the close command)", stderr, StringComparison.Ordinal);
        Assert.False(JsonText.Parse(stdout).Member("closed")!.Value.AsBool());
        Assert.Contains(_sandbox.ReadTeam(TeamPath("proj")).Member("members")!.Value.AsArray(), m => m.Member("name")!.Value.AsString() == "proj-a");
    }
}
