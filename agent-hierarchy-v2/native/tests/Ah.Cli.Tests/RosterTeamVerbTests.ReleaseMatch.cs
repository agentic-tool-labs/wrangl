using Ah.Core.Json;

namespace Ah.Cli.Tests;

/// <summary>The disband close summary with held members, and how a delivered release request is matched to a member.</summary>
public sealed partial class RosterTeamVerbTests
{
    private const string ProjReviewer = "proj-reviewer";

    private void ArrangeDeadImplementorAndLiveReviewer()
    {
        WriteTeam("proj", LiveTeamJson("tmux", OneImplementor + "," + Member("reviewer", ProjReviewer, "%2")));
        AppendGoneRow(Impl, "proj");
        PeerUp(ProjReviewer, "%2", session: "sess-rev");
        ScriptGit();
        Activity("sess-rev.json", "working", DateTimeOffset.UtcNow);
    }

    [Fact]
    public void DisbandCloseCountsAHeldLiveMemberAsExpectedAndStillLive()
    {
        ArrangeDeadImplementorAndLiveReviewer();

        var token = TokenOf(Roster("disband", "--team", "proj").Stdout);
        var (code, stdout, _) = Roster("disband", "--close", "--confirm", "--plan-token", token, "--team", "proj");

        var result = JsonText.Parse(stdout);
        Assert.Equal(3, code);
        Assert.True(Killed("%1"));
        Assert.False(Killed("%2"));
        Assert.Equal(2, (int)result.Member("expected")!.Value.AsNumber());
        Assert.Equal(1, (int)result.Member("closed_count")!.Value.AsNumber());
        Assert.Equal(ProjReviewer,Assert.Single(result.Member("still_live")!.Value.AsArray()).AsString());
        Assert.Equal("busy", Assert.Single(result.Member("held")!.Value.AsArray()).Member("status")!.Value.AsString());
        Assert.Contains(ProjReviewer +" busy", result.Member("reason")!.Value.AsString(), StringComparison.Ordinal);
        Assert.False(result.Member("closed")!.Value.AsBool());
    }

    [Fact]
    public void DisbandCloseOfPeersWithNoTeamRecordCountsAHeldLiveMemberAsExpectedAndStillLive()
    {
        PeerUp("solo-implementor", "%5");
        PeerUp("solo-reviewer", "%6", session: "sess-rev");
        ScriptGit();
        Activity("sess-rev.json", "working", DateTimeOffset.UtcNow);

        var token = TokenOf(Roster("disband").Stdout);
        var (code, stdout, _) = Roster("disband", "--close", "--confirm", "--plan-token", token);

        var result = JsonText.Parse(stdout);
        Assert.Equal(3, code);
        Assert.False(Killed("%6"));
        Assert.Equal(2, (int)result.Member("expected")!.Value.AsNumber());
        Assert.Contains(result.Member("still_live")!.Value.AsArray(), m => m.AsString() == "solo-reviewer");
        Assert.Contains("solo-reviewer busy",result.Member("reason")!.Value.AsString(), StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeDisbandCloseWithEveryMemberReleasedHasNoHeldNoStillLiveAndNoReason()
    {
        var members = OneImplementor + "," + Member("reviewer", ProjReviewer, "%2");
        WriteTeam("proj", LiveTeamJson("tmux", members));
        PeerUp(Impl, "%1");
        PeerUp(ProjReviewer, "%2", session: "sess-rev");
        ScriptGit();
        AskRelease("20261016-010000-aaaa");
        Answer("20261016-010000-aaaa", StatusSection(ReadyLine()));
        AskRelease("20261016-010000-bbbb", member: ProjReviewer);
        Answer("20261016-010000-bbbb", StatusSection(ReadyLine()));

        var token = TokenOf(Roster("disband", "--team", "proj").Stdout);
        var (code, stdout, _) = Roster("disband", "--close", "--confirm", "--plan-token", token, "--team", "proj");

        var result = JsonText.Parse(stdout);
        Assert.Equal(0, code);
        Assert.Equal(2, (int)result.Member("expected")!.Value.AsNumber());
        Assert.Empty(result.Member("still_live")!.Value.AsArray());
        Assert.Null(result.Member("reason"));
    }

}
