using Ah.Core.Json;

namespace Ah.Cli.Tests;

/// <summary>A lane lead's dismiss of a released member of its own child team, which needs no green light from the manager.</summary>
public sealed partial class RosterTeamVerbTests
{
    private void ArrangeLaneMember(bool answered = true)
    {
        ArrangeManaged();
        PeerUp(Impl, "%1");
        AskRelease("20261016-010000-aaaa");
        if (answered)
        {
            Answer("20261016-010000-aaaa", StatusSection(ReadyLine()));
        }
    }

    private (int Code, string Stdout, string Stderr) LaneLeadDismissesTheMember()
    {
        var plan = Roster("dismiss", Impl, "--plan", "--team", ChildName);
        var token = JsonText.Parse(plan.Stdout).Member("close_token") is { IsText: true } t ? t.AsString() : "none";
        AsSubOrchestrator();
        return Roster("dismiss", Impl, "--close", "--confirm", "--plan-token", token, "--team", ChildName);
    }

    [Fact]
    public void ALaneLeadClosesAnIdleMemberWithCommitsAheadWithoutAGreenLightAndTheWarningNamesThem()
    {
        ArrangeLaneMember();
        var scripted = _runner.Output;
        _runner.Output = argv => argv.Contains("rev-list") ? "2\n" : scripted!(argv);

        var (code, stdout, stderr) = LaneLeadDismissesTheMember();

        Assert.True(code == 0, stdout + stderr);
        Assert.True(Killed("%1"));
        Assert.Contains("2 commit(s) in", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void ALaneLeadsDismissOfAWorkingMemberStillHolds()
    {
        ArrangeLaneMember();
        Activity("sess-impl.json", "working", DateTimeOffset.UtcNow);

        var (code, _, _) = LaneLeadDismissesTheMember();

        Assert.NotEqual(0, code);
        Assert.False(Killed("%1"));
    }

    [Fact]
    public void ALaneLeadClosesAReleasedMemberOfItsChildTeamWithoutAGreenLight()
    {
        ArrangeLaneMember();

        var (code, _, stderr) = LaneLeadDismissesTheMember();

        Assert.True(code == 0, stderr);
        Assert.True(Killed("%1"));
    }

    [Fact]
    public void ALaneLeadClosesAnIdleMemberWithAnOpenRequestAndTheWarningNamesIt()
    {
        ArrangeLaneMember(answered: false);

        var (code, stdout, stderr) = LaneLeadDismissesTheMember();

        Assert.True(code == 0, stdout + stderr);
        Assert.True(Killed("%1"));
        Assert.Contains("20261016-010000-aaaa has no response yet", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void ALaneLeadClosesAnIdleMemberWithUncommittedChangesAndTheWarningNamesThem()
    {
        ArrangeLaneMember();
        _porcelain = " M src/file.cs\n";

        var (code, stdout, stderr) = LaneLeadDismissesTheMember();

        Assert.True(code == 0, stdout + stderr);
        Assert.True(Killed("%1"));
        Assert.Contains("uncommitted work in", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void ALaneLeadsBareDismissOfAMemberThatIsNotCleanIsStillRefusedAndClosesNothing()
    {
        ArrangeLaneMember();
        _porcelain = " M src/file.cs\n";
        AsSubOrchestrator();

        var (code, stdout, stderr) = Roster("dismiss", Impl, "--team", ChildName);

        Assert.NotEqual(0, code);
        Assert.Contains("not-clean", stdout + stderr, StringComparison.Ordinal);
        Assert.False(Killed("%1"));
    }

    [Fact]
    public void ALaneLeadsDisbandOfTheChildTeamStillNeedsTheManagersGreenLight()
    {
        ArrangeLaneMember();
        var token = PlanToken();
        AsSubOrchestrator();

        Assert.Contains("close-no-green-light", SoDisband(token).Stdout, StringComparison.Ordinal);
        Assert.False(Killed("%1"));
    }
}
