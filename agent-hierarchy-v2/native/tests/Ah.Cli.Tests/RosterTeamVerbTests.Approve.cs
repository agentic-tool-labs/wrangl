namespace Ah.Cli.Tests;

/// <summary><c>roster approve &lt;member&gt; &lt;rule&gt;</c>: one soft-gate approve row for a member of a team, nothing for a stranger.</summary>
public sealed partial class RosterTeamVerbTests
{
    [Fact]
    public void ApproveAppendsOneApproveRowForATeamMember()
    {
        WriteTeam("proj", LiveTeamJson("tmux", OneImplementor));

        var (code, stdout, _) = Roster("approve", Impl, "worktree");

        Assert.Equal(0, code);
        Assert.Contains("\"approved\"", stdout, StringComparison.Ordinal);
        var row = Assert.Single(Ah.TestKit.TestGates.Text(_sandbox.AhHome, Hierarchy).Split('\n'), l => l.Contains("\"outcome\":\"approve\"", StringComparison.Ordinal));
        Assert.Contains($"\"member\":\"{Impl}\"", row, StringComparison.Ordinal);
        Assert.Contains("\"rule\":\"worktree\"", row, StringComparison.Ordinal);
    }

    [Fact]
    public void ApproveRefusesAMemberOfNoTeamAndWritesNothing()
    {
        WriteTeam("proj", LiveTeamJson("tmux", OneImplementor));

        var (code, _, _) = Roster("approve", "nobody", "worktree");

        Assert.Equal(2, code);
        Assert.DoesNotContain("approve", Ah.TestKit.TestGates.Text(_sandbox.AhHome, Hierarchy), StringComparison.Ordinal);
    }
}
