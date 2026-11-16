using Ah.Core.Json;

namespace Ah.Cli.Tests;

/// <summary>untrack --commit refuses only a live member; one whose liveness is unknown is forgotten with a note saying so.</summary>
public sealed partial class RosterTeamVerbTests
{
    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 2)]
    public void UntrackCommitForgetsAnUndecidedMemberAndNegativeRefusesALiveOne(bool live, int expected)
    {
        WriteTeam("proj", LiveTeamJson("terminal", TwoPeers));
        _registrations = new() { ["proj-implementor@proj"] = new Ah.Hierarchy.MemberRegistrationLiveness("sess-impl", live, live ? "registration live" : "no usable observation of its process") };

        var (code, stdout, stderr) = Roster("untrack", "proj-implementor", "--commit", "--team", "proj");

        Assert.True(code == expected, stdout + stderr);
        var members = JsonText.Parse(_sandbox.TeamText(TeamPath("proj"))).Member("members")!.Value.AsArray();
        Assert.Equal(live ? 2 : 1, members.Count);
        Assert.Contains(live ? "still live" : "liveness unknown", stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UntrackOfALiveMemberPointsAtDismissCloseOnlyWhenItHasAPane(bool pane)
    {
        var members = pane ? TwoPeers : TwoPeers.Replace("\"transport_id\":\"%1\"", "\"transport_id\":null", StringComparison.Ordinal);
        WriteTeam("proj", LiveTeamJson("tmux", members));
        _registrations = new() { ["proj-implementor@proj"] = new Ah.Hierarchy.MemberRegistrationLiveness("sess-impl", true, "registration live") };

        var (code, _, stderr) = Roster("untrack", "proj-implementor", "--commit", "--team", "proj");

        Assert.NotEqual(0, code);
        Assert.Equal(pane, stderr.Contains("Use `dismiss proj-implementor --close`", StringComparison.Ordinal));
        Assert.Contains(pane ? "or re-run with --keep-sessions" : "Re-run with --keep-sessions", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void DismissCloseOfAMemberWithNoPanePointsAtUntrackCommit()
    {
        WriteTeam("proj", LiveTeamJson("tmux", TwoPeers.Replace("\"transport_id\":\"%1\"", "\"transport_id\":null", StringComparison.Ordinal)));

        var (code, _, stderr) = Roster("dismiss", "proj-implementor", "--close", "--ignore-release", "--team", "proj");

        Assert.NotEqual(0, code);
        Assert.Contains("use `untrack proj-implementor --commit`", stderr, StringComparison.Ordinal);
    }
}
