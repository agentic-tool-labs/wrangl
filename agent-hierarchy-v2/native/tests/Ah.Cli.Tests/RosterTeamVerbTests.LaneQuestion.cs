namespace Ah.Cli.Tests;

/// <summary>ah2-510: a lane lead parks a pick on an item of its own lane with checklist waiting-user, and the top Orchestrator is told.</summary>
public sealed partial class RosterTeamVerbTests
{
    [Fact]
    public void TheLaneLeadParksAQuestionOnItsOwnLaneItemAndTheTopIsToldOnceNamingTheLaneAndItem()
    {
        ParentLane(string.Empty, leadPid: Self);
        AsTheLaneLead();
        var mail = LeadMailOf();
        Assert.Equal(0, Roster("checklist", "add", "pick-candidate", "--stream", "s1", "--team", "proj-s1").Code);

        var (code, stdout, stderr) = Roster("checklist", "set", "s1/pick-candidate", "--state", "waiting-user", "--note", "Which candidate?", "--option", "A", "--option", "B", "--team", "proj-s1");

        Assert.True(code == 0, stdout + stderr);
        Assert.Contains("\"state\": \"waiting-user\"", stdout, StringComparison.Ordinal);
        var told = Assert.Single(mail.Sent);
        Assert.Equal(["orchestrator@proj"], told.Recipients);
        Assert.Contains("lane s1", told.Text, StringComparison.Ordinal);
        Assert.Contains("s1/pick-candidate", told.Text, StringComparison.Ordinal);
        Assert.Contains("Which candidate?", told.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeTheLaneLeadSettingAnotherStateTellsTheTopNothing()
    {
        ParentLane(string.Empty, leadPid: Self);
        AsTheLaneLead();
        var mail = LeadMailOf();
        Assert.Equal(0, Roster("checklist", "add", "pick-candidate", "--stream", "s1", "--team", "proj-s1").Code);

        var (code, stdout, stderr) = Roster("checklist", "set", "s1/pick-candidate", "--state", "blocked", "--note", "Which candidate?", "--team", "proj-s1");

        Assert.True(code == 0, stdout + stderr);
        Assert.Empty(mail.Sent);
    }

    [Fact]
    public void NegativeTheTopOrchestratorParkingOnALaneItemSendsNoParkEvent()
    {
        ParentLane(string.Empty, leadPid: Self);
        var mail = LeadMailOf();
        Assert.Equal(0, Roster("checklist", "add", "pick-candidate", "--stream", "s1", "--team", "proj-s1").Code);

        var (code, stdout, stderr) = Roster("checklist", "set", "s1/pick-candidate", "--state", "waiting-user", "--note", "Which candidate?", "--team", "proj-s1");

        Assert.True(code == 0, stdout + stderr);
        Assert.DoesNotContain(mail.Sent, s => s.Event == "lane-question-parked");
    }
}
