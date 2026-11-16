using Ah.Core.Json;

namespace Ah.Launch.Tests;

/// <summary>M1-M6: a bare Claude in a member's pane is matched only when every check holds, and any miss is unmatched.</summary>
public sealed class BareDetectorTests
{
    private const string Session = "8c1d2f6e-0b7a-4c55-9d3e-5a1b2c3d4e5f";
    private const string Root = "/work/alpha";
    private const string TeamFile = "/work/alpha/.claude/ah2/teams/alpha.json";

    private static readonly BareMember Member = new("alpha", "alpha-implementor", "w1:p1", Root, new AhLaunchExpectation("wrangl:implementor", "alpha-implementor", TeamFile, "sonnet", null));

    private sealed class Facts : IBareFacts
    {
        public List<PaneProcess>? Processes { get; set; } = [];

        public string? Last { get; set; } = Session;

        public bool Claimed { get; set; }

        public string? Start { get; set; } = "Wed Oct 21 16:00:00 2026";

        public IReadOnlyList<PaneProcess>? PaneProcesses(string paneId) => Processes;

        public string? LastSessionId(string name, string team) => Last;

        public bool ClaimedElsewhere(string? sessionId, int pid, string? pidStart, string team, string member) => Claimed;

        public string? StartOf(int pid) => Start;

        public string RealPath(string path) => path.Replace("/private", string.Empty, StringComparison.Ordinal);
    }

    private static PaneProcess Bare(string session = Session, string? cwd = Root, int pid = 100) =>
        new(pid, ["claude", "--resume", session], cwd);

    private static PaneProcess AhBuilt() =>
        new(100, ["claude", "--resume", Session, "--agent", "wrangl:implementor", "--name", "alpha-implementor", "--model", "sonnet", "--settings",
            "{\"env\":{\"WRANGL_TEAM_FILE\":\"" + TeamFile + "\",\"AH_MEMBER_NAME\":\"alpha-implementor\"}}"], Root);

    private static readonly BareSighting Earlier = new(100, "Wed Oct 21 16:00:00 2026");

    [Fact]
    public void ABareResumeThatMatchesOnEveryCheckAndWasSeenTwiceIsBare()
    {
        var facts = new Facts { Processes = [Bare()] };

        var verdict = LaunchModule.CheckBare(facts, Member, Earlier);

        Assert.Equal(BareKind.Bare, verdict.Kind);
        Assert.Equal(100, verdict.Pid);
        Assert.Equal(Session, verdict.SessionId);
        Assert.Empty(verdict.Failed);
    }

    [Fact]
    public void TheFirstSightingIsOnlyACandidate()
    {
        var verdict = LaunchModule.CheckBare(new Facts { Processes = [Bare()] }, Member, null);

        Assert.Equal(BareKind.Candidate, verdict.Kind);
    }

    [Fact]
    public void AClaudeTheBuilderMadeIsLaunchedAndNeverBare()
    {
        var verdict = LaunchModule.CheckBare(new Facts { Processes = [AhBuilt()] }, Member, Earlier);

        Assert.Equal(BareKind.Launched, verdict.Kind);
    }

    [Fact]
    public void AShellOnlyPaneHasNoClaudeAndAnUnreadablePaneConcludesNothing()
    {
        Assert.Equal(BareKind.NoClaude, LaunchModule.CheckBare(new Facts { Processes = [new PaneProcess(7, ["-zsh"], Root)] }, Member, Earlier).Kind);
        Assert.Equal(BareKind.Unreadable, LaunchModule.CheckBare(new Facts { Processes = null }, Member, Earlier).Kind);
    }

    [Fact]
    public void ARunnerThatIsNotTheHarnessIsNotClaude()
    {
        var mcp = new PaneProcess(8, ["node", "/home/u/mcp/index.js"], Root);

        Assert.Equal(BareKind.Bare, LaunchModule.CheckBare(new Facts { Processes = [mcp, Bare()] }, Member, Earlier).Kind);
    }

    [Fact]
    public void ASessionThatIsNotTheMembersLastFailsM3()
    {
        var older = LaunchModule.CheckBare(new Facts { Processes = [Bare("11111111-1111-4111-8111-111111111111")] }, Member, Earlier);
        var none = LaunchModule.CheckBare(new Facts { Processes = [new PaneProcess(100, ["claude"], Root)] }, Member, Earlier);
        var unknown = LaunchModule.CheckBare(new Facts { Processes = [Bare()], Last = null }, Member, Earlier);

        Assert.Equal((BareKind.Unmatched, "M3"), (older.Kind, Assert.Single(older.Failed)));
        Assert.Equal((BareKind.Unmatched, "M3"), (none.Kind, Assert.Single(none.Failed)));
        Assert.Equal((BareKind.Unmatched, "M3"), (unknown.Kind, Assert.Single(unknown.Failed)));
    }

    private const string Project = "/Users/someone/git/repos/agent-tools";

    [Theory]
    [InlineData(Project + "/.claude/worktrees/feat-p1", Project + "/.claude/worktrees/api-no-giveup")]
    [InlineData(Project, Project + "/.claude/worktrees/fix-link-grant")]
    [InlineData(Project + "/.claude/worktrees/lane-db", Project + "/.claude/worktrees/lane-db")]
    [InlineData(Project + "/agent-hierarchy-v2", Project + "/.claude/worktrees/lane-db")]
    [InlineData(Project + "/.claude/worktrees/stall-p2/agent-hierarchy-v2/native", Project)]
    public void AClaudeStartedElsewhereInTheMembersProjectStillMatchesM4(string cwd, string expectedRoot)
    {
        var member = Member with { ExpectedRoot = expectedRoot };

        var verdict = LaunchModule.CheckBare(new Facts { Processes = [Bare(cwd: cwd)] }, member, Earlier);

        Assert.Equal(BareKind.Bare, verdict.Kind);
    }

    [Theory]
    [InlineData("/Users/someone/git/repos/agent-tools-copy/.claude/worktrees/lane-db", Project + "/.claude/worktrees/lane-db")]
    [InlineData("/Users/someone/git/repos/other", Project + "/.claude/worktrees/lane-db")]
    [InlineData("/Users/someone", Project)]
    [InlineData(Project + "/.claude/worktrees/lane-db", "/Users/someone/git/repos/other")]
    public void AClaudeOutsideTheMembersProjectFailsM4(string cwd, string expectedRoot)
    {
        var member = Member with { ExpectedRoot = expectedRoot };

        var verdict = LaunchModule.CheckBare(new Facts { Processes = [Bare(cwd: cwd)] }, member, Earlier);

        Assert.Equal((BareKind.Unmatched, "M4"), (verdict.Kind, Assert.Single(verdict.Failed)));
    }

    [Fact]
    public void ADifferentWorkingDirectoryFailsM4ButASymlinkedSpellingOfTheSameOneDoesNot()
    {
        var elsewhere = LaunchModule.CheckBare(new Facts { Processes = [Bare(cwd: "/work/other")] }, Member, Earlier);
        var noCwd = LaunchModule.CheckBare(new Facts { Processes = [Bare(cwd: null)] }, Member, Earlier);
        var sameByRealPath = LaunchModule.CheckBare(new Facts { Processes = [Bare(cwd: "/private" + Root)] }, Member, Earlier);

        Assert.Equal((BareKind.Unmatched, "M4"), (elsewhere.Kind, Assert.Single(elsewhere.Failed)));
        Assert.Equal((BareKind.Unmatched, "M4"), (noCwd.Kind, Assert.Single(noCwd.Failed)));
        Assert.Equal(BareKind.Bare, sameByRealPath.Kind);
    }

    [Fact]
    public void ASessionAnotherLiveMemberClaimsFailsM5()
    {
        var verdict = LaunchModule.CheckBare(new Facts { Processes = [Bare()], Claimed = true }, Member, Earlier);

        Assert.Equal((BareKind.Unmatched, "M5"), (verdict.Kind, Assert.Single(verdict.Failed)));
    }

    [Fact]
    public void AProcessWhoseStartTimeCannotBeReadFailsM6ButANewProcessIsOnlyACandidate()
    {
        var newPid = LaunchModule.CheckBare(new Facts { Processes = [Bare(pid: 200)] }, Member, Earlier);
        var reusedPid = LaunchModule.CheckBare(new Facts { Processes = [Bare()], Start = "Wed Oct 21 16:05:00 2026" }, Member, Earlier);
        var gone = LaunchModule.CheckBare(new Facts { Processes = [Bare()], Start = null }, Member, Earlier);

        Assert.Equal(BareKind.Candidate, newPid.Kind);
        Assert.Equal(BareKind.Candidate, reusedPid.Kind);
        Assert.Equal((BareKind.Unmatched, "M6"), (gone.Kind, Assert.Single(gone.Failed)));
    }

    [Fact]
    public void ALaunchByAhWhoseModelDiffersFromTheRowIsDriftAndNeitherBareNorUnmatched()
    {
        var drifted = new PaneProcess(100, ["claude", "--resume", Session, "--agent", "wrangl:implementor", "--name", "alpha-implementor", "--model", "haiku", "--settings",
            "{\"env\":{\"WRANGL_TEAM_FILE\":\"" + TeamFile + "\",\"AH_MEMBER_NAME\":\"alpha-implementor\"}}"], Root);

        var verdict = LaunchModule.CheckBare(new Facts { Processes = [drifted] }, Member, Earlier);

        Assert.Equal(BareKind.Drift, verdict.Kind);
        Assert.Contains("runs model haiku, row says sonnet", verdict.Why, StringComparison.Ordinal);
        Assert.Empty(verdict.Failed);
    }

    [Fact]
    public void ALaunchNamingAnotherMemberInItsSettingsIsBareEvenWithTheRightAgentAndName()
    {
        var other = new PaneProcess(100, ["claude", "--resume", Session, "--agent", "wrangl:implementor", "--name", "alpha-implementor", "--model", "sonnet", "--settings",
            "{\"env\":{\"WRANGL_TEAM_FILE\":\"" + TeamFile + "\",\"AH_MEMBER_NAME\":\"alpha-someone-else\"}}"], Root);

        Assert.Equal(BareKind.Bare, LaunchModule.CheckBare(new Facts { Processes = [other] }, Member, Earlier).Kind);
    }

    [Fact]
    public void TwoClaudesInOnePaneFailM1AndNothingIsPicked()
    {
        var verdict = LaunchModule.CheckBare(new Facts { Processes = [Bare(pid: 100), Bare(pid: 101)] }, Member, Earlier);

        Assert.Equal(BareKind.Unmatched, verdict.Kind);
        Assert.Equal(["M1"], verdict.Failed);
        Assert.Null(verdict.Pid);
    }

    [Fact]
    public void AnAhBuiltClaudeBesideABareOneIsStillLaunched()
    {
        var verdict = LaunchModule.CheckBare(new Facts { Processes = [AhBuilt(), Bare(pid: 101)] }, Member, Earlier);

        Assert.Equal(BareKind.Launched, verdict.Kind);
    }

    [Fact]
    public void ProcessInfoJsonIsReadIntoProcessesWithTheirArgvAndCwd()
    {
        var answer = JsonText.Parse(
            "{\"result\":{\"process_info\":{\"foreground_processes\":[{\"argv\":[\"claude\",\"--resume\",\"" + Session + "\"],\"cwd\":\"" + Root + "\",\"pid\":76645},{\"argv\":[\"-zsh\"],\"pid\":3}]}}}");

        var processes = LaunchModule.PaneProcessesOf(answer)!;

        Assert.Equal(2, processes.Count);
        Assert.Equal((76645, Root), (processes[0].Pid, processes[0].Cwd));
        Assert.Equal(["claude", "--resume", Session], processes[0].Argv);
        Assert.Null(processes[1].Cwd);
        Assert.Null(LaunchModule.PaneProcessesOf(JsonText.Parse("{\"error\":{\"code\":\"pane_not_found\"}}")));
    }
}
