using Ah.Commands;
using Ah.Core.Json;

namespace Ah.Cli.Tests;

/// <summary>A member with no recorded pane: absence is proved from the registry and herdr's agent list, or the refusal says which check failed.</summary>
public sealed partial class RosterTeamVerbTests
{
    private string _noPaneRecord = string.Empty;

    /// <summary>A herdr team whose member's row records no pane. The registry lists this session, and herdr lists no agent.</summary>
    private void ArrangeNoPane(string transport = "herdr")
    {
        _noPaneRecord = PendingMemberWithASnapshot(PaneKnowledge.Absent);
        var team = _sandbox.TeamText(HomeTeam).Replace("\"transport\":\"tmux\"", "\"transport\":\"" + transport + "\"", StringComparison.Ordinal);
        _sandbox.PlantTeam(HomeTeam, System.Text.RegularExpressions.Regex.Replace(team, "\"transport_id\":\"[^\"]*\",?", string.Empty));
        if (transport == "herdr")
        {
            _ = OnHerdr(string.Empty);
        }

        _liveSessions = [new LiveSession("sess-me", Environment.ProcessId, SelfStart, null, [])];
        _panes.AgentRows = [];
        _runner.Calls.Clear();
    }

    private JsonValue RelaunchNoPane()
    {
        var (code, result, text) = ResumeCheckpointOf(_noPaneRecord, "--yes", "--relaunch", Impl);
        Assert.True(code == 0, text);
        return result.Member("members")!.Value.AsArray().Single();
    }

    private void AssertNoPaneRefused(string check)
    {
        var row = RelaunchNoPane();
        Assert.Equal("relaunch-refused", row.Member("status")!.Value.AsString());
        Assert.Contains(check, row.Member("why")!.Value.AsString(), StringComparison.Ordinal);
        Assert.Empty(LaunchLines());
    }

    [Fact]
    public void AMemberWithNoRecordedPaneWhoseAbsenceIsProvedLaunchesInANewPane()
    {
        ArrangeNoPane();

        var row = RelaunchNoPane();

        Assert.Equal("relaunched", row.Member("status")!.Value.AsString());
        Assert.Single(LaunchLines());
    }

    [Fact]
    public void ARelaunchOnAHomeWithNoRunDirectoryDoesNotThrow()
    {
        ArrangeNoPane();
        var run = Path.Combine(_sandbox.AhHome, "run");
        if (Directory.Exists(run))
        {
            Directory.Delete(run, recursive: true);
        }

        var row = RelaunchNoPane();

        Assert.Equal("relaunched", row.Member("status")!.Value.AsString());
    }

    [Fact]
    public void NegativeNoRecordedPaneWithTheMembersSessionLiveAnywhereIsRefused()
    {
        ArrangeNoPane();
        _liveSessions = [.. _liveSessions, new LiveSession(ImplSession, LivePidOfAnotherProcess(), RecoverStart, "p9", [])];

        AssertNoPaneRefused("a live session is this member");
    }

    [Fact]
    public void NegativeNoRecordedPaneWithAnUnreadableRegistryIsRefused()
    {
        ArrangeNoPane();
        _liveSessions = [];

        AssertNoPaneRefused("the session registry cannot be read");
    }

    [Fact]
    public void NegativeNoRecordedPaneWithAnUnreadableHerdrListIsRefused()
    {
        ArrangeNoPane();
        _panes.AgentRows = null;

        AssertNoPaneRefused("the herdr agent list cannot be read");
    }

    [Fact]
    public void NegativeNoRecordedPaneOnATmuxTeamIsRefused()
    {
        ArrangeNoPane("tmux");

        AssertNoPaneRefused("this team's transport is tmux");
    }
}
