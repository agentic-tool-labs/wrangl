using Ah.Commands;
using Ah.Core.Json;
using Ah.Hierarchy;

namespace Ah.Cli.Tests;

/// <summary><c>resume --all</c>: each kind of team takes its own row, and a member left in a shell pane comes back after one settle.</summary>
public sealed partial class RosterTeamVerbTests
{
    private (int Code, string Stdout, string Stderr) ResumeEverything(params string[] extra) =>
        Roster(["resume", "--all", "--cwd", Project, "--orchestrator-pid", Self, .. extra]);

    private static JsonValue RowOfTeam(string stdout, string list, string team) =>
        JsonText.Parse(stdout).Member(list)!.Value.AsArray().Single(t => t.Member("team")!.Value.AsString() == team);

    [Fact]
    public void QuitStopUnheldAndNotOwnedTeamsEachTakeTheirRow()
    {
        ArrangeTree();
        ObserverSeesAParent();
        TreeTeam("one", "one-implementor");
        TreeTeam("two", "two-implementor", kind: "stop", phase: "stopped");
        TreeTeam("three", "three-implementor", kind: null);
        TreeTeam("four", "four-implementor", pid: DeadPid().ToString(System.Globalization.CultureInfo.InvariantCulture), pidStart: GoneStart);

        var (code, stdout, stderr) = ResumeEverything("--yes");

        Assert.True(code == 0, stdout + stderr);
        Assert.False(Held(TreeHold("one")));
        Assert.Equal("relaunched", RowOfTeam(stdout, "teams", "one").Member("members")!.Value.AsArray().Single().Member("status")!.Value.AsString());
        Assert.True(Held(TreeHold("two")), "a stop needs its assessment read");
        Assert.Equal("ah roster resume --hold --team two", RowOfTeam(stdout, "teams", "two").Member("next")!.Value.AsString());
        Assert.Equal("relaunched", RowOfTeam(stdout, "teams", "three").Member("members")!.Value.AsArray().Single().Member("status")!.Value.AsString());
        Assert.True(Held(TreeHold("four")), "a team this session does not own was touched");
        Assert.Equal("orphaned", RowOfTeam(stdout, "not_owned", "four").Member("life")!.Value.AsString());
        Assert.Equal("ah roster resume --hold --team four --yes", RowOfTeam(stdout, "not_owned", "four").Member("next")!.Value.AsString());
        Assert.False(LaunchedAny("four-implementor") || LaunchedAny("two-implementor"));
    }

    private bool LaunchedAny(string member) => _runner.Calls.Any(argv => string.Join(' ', argv).Contains(member, StringComparison.Ordinal));

    [Fact]
    public void NegativeAnUnheldTeamAllLiveGetsNoLaunchNoOldBriefAndNoEvent()
    {
        var (request, _) = AnsweredCheckpointRequest();
        _callers ??= new RecordingCallers { StartText = SelfStart };
        _withPanes = true;
        var mail = new FullMail();
        _eventMail = mail;
        Registered(ImplSession, live: true);
        _runner.Calls.Clear();
        Assert.False(string.IsNullOrEmpty(request));

        var (code, stdout, stderr) = ResumeEverything("--yes");

        Assert.True(code == 0, stdout + stderr);
        Assert.Empty(_sent);
        Assert.Empty(mail.Sent);
        Assert.DoesNotContain(_runner.Calls, argv => argv.Contains("new-window"));
    }

    [Fact]
    public void ARerunAfterAKilledResumeLaunchesNothingTwice()
    {
        ArrangeTree();
        TreeTeam("proj", "proj-implementor");
        var (firstCode, firstOut, firstErr) = ResumeEverything("--yes");
        Assert.True(firstCode == 0, firstOut + firstErr);
        Assert.False(Held(TreeHold("proj")));
        var launched = _runner.Calls.Count(argv => argv.Contains("new-window"));
        _registrations = new() { ["proj-implementor@proj"] = new MemberRegistrationLiveness("sess-new", true, "registration live") };

        var (code, stdout, stderr) = ResumeEverything("--yes");

        Assert.True(code == 0, stdout + stderr);
        Assert.Equal(launched, _runner.Calls.Count(argv => argv.Contains("new-window")));
    }

    /// <summary>
    /// The member left in shell pane pA after a restart, its row recording session <see cref="RelaunchSession"/> and spawn nonce
    /// <paramref name="nonce"/>. On herdr TUI no terminal label can tie pA to the member.
    /// </summary>
    private void DeadMemberWithARowSession(string nonce = "0123456789abcdef")
    {
        _stepClock = new StepClock(DateTimeOffset.UtcNow);
        _ = RestoredHerdrMember();
        _sandbox.PlantTeam(ProjectTeamFile, _sandbox.TeamText(ProjectTeamFile).Replace(
            "\"effort\":\"low\"",
            "\"effort\":\"low\",\"autoMode\":\"auto\",\"session_id\":\"" + RelaunchSession + "\",\"spawn_nonce\":\"" + nonce + "\"",
            StringComparison.Ordinal));
    }

    [Fact]
    public void NegativeAShellPaneNoLabelTiesToTheMemberIsNeverSplitOrClosedAndTheMemberResumesByNormalPlacement()
    {
        DeadMemberWithARowSession();

        var (code, stdout, stderr) = ResumeEverything("--yes");

        Assert.True(code == 0, stdout + stderr);
        Assert.DoesNotContain(_runner.Calls, argv => argv.Contains("split") && argv.Contains("pA"));
        Assert.DoesNotContain(_runner.Calls, argv => argv.Contains("close") && argv.Contains("pA"));
        var launch = Assert.Single(LaunchLines());
        Assert.DoesNotContain("--pane pA", launch, StringComparison.Ordinal);
        Assert.Contains("--resume " + RelaunchSession, launch, StringComparison.Ordinal);
        Assert.Contains("--model sonnet", launch, StringComparison.Ordinal);
        Assert.Contains("--effort low", launch, StringComparison.Ordinal);
        Assert.Contains("--permission-mode auto", launch, StringComparison.Ordinal);
        var row = RowOfTeam(stdout, "teams", "proj").Member("members")!.Value.AsArray().Single();
        Assert.Equal("resumed:" + RelaunchSession, row.Member("session")!.Value.AsString());
        Assert.Equal("untied, left open", row.Member("old_pane")!.Value.AsString());
    }

    [Fact]
    public void NegativeAMemberWhoseHarnessAppearsDuringTheSettleIsNotLaunched()
    {
        _stepClock = new StepClock(DateTimeOffset.UtcNow);
        _ = RestoredHerdrMember();
        _onQuitSleep = () => _panes.Harness = true;

        var (code, stdout, stderr) = ResumeEverything("--yes");

        Assert.True(code == 0, stdout + stderr);
        Assert.Empty(LaunchLines());
        Assert.Equal("pending", RowOfTeam(stdout, "teams", "proj").Member("members")!.Value.AsArray().Single().Member("status")!.Value.AsString());
    }

    [Fact]
    public void NegativeAnUnknownPaneReadingNeverLaunchesAndShowsItsReason()
    {
        _stepClock = new StepClock(DateTimeOffset.UtcNow);
        _ = RestoredHerdrMember(PaneKnowledge.Unknown);
        _panes.Reason = "herdr exited 1: socket not found";

        var (code, stdout, stderr) = ResumeEverything("--yes");

        Assert.True(code == 0, stdout + stderr);
        Assert.Empty(LaunchLines());
        Assert.Contains("socket not found", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeResumeAllWithATeamIsABadFlag()
    {
        ArrangeTree();
        TreeTeam("proj", "proj-implementor");

        var (code, stdout, stderr) = ResumeEverything("--team", "proj", "--yes");

        Assert.True(code != 0, stdout + stderr);
        Assert.Contains("bad_flags", stdout + stderr, StringComparison.Ordinal);
        Assert.True(Held(TreeHold("proj")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)] // the lane's sub-orchestrator is already back
    public void AnUnheldTopTeamAndItsUnheldLanesAreAllRelaunchedParentsFirstAfterARestart(bool liveLead)
    {
        ArrangeTree();
        var gone = DeadPid().ToString(System.Globalization.CultureInfo.InvariantCulture);
        TreeTeam("proj", "proj-implementor", kind: null);
        TreeTeam("lane", "lane-implementor", parent: "proj", pid: liveLead ? LivePidOfAnotherProcess().ToString(System.Globalization.CultureInfo.InvariantCulture) : gone, pidStart: liveLead ? null : GoneStart, kind: null);
        TreeTeam("deep", "deep-implementor", parent: "lane", pid: gone, pidStart: GoneStart, kind: null);

        var (code, stdout, stderr) = ResumeEverything("--yes");

        Assert.True(code == 0, stdout + stderr);
        var lanes = RowOfTeam(stdout, "teams", "proj").Member("lanes")!.Value.AsArray();
        Assert.Equal(["relaunched", "relaunched"], lanes.Select(l => l.Member("members")!.Value.AsArray().Single().Member("status")!.Value.AsString()));
        Assert.True(LaunchIndexOf("proj-implementor") < LaunchIndexOf("lane-implementor") && LaunchIndexOf("lane-implementor") < LaunchIndexOf("deep-implementor"));
    }

    [Fact]
    public void ANotOwnedTeamBehindAWindDownListsThePlainCheckpointCommand()
    {
        ArrangeTree();
        ObserverSeesAParent();
        var gone = DeadPid().ToString(System.Globalization.CultureInfo.InvariantCulture);
        TreeTeam("four", "four-implementor", pid: gone, pidStart: GoneStart, kind: null);
        TreeTeam("five", "five-implementor", pid: gone, pidStart: GoneStart, kind: null);
        ChainDownRow("four", "active", AfterAnyRecord);

        var (_, stdout, _) = ResumeEverything();

        Assert.Equal("ah roster resume --checkpoint --team four --yes", RowOfTeam(stdout, "not_owned", "four").Member("next")!.Value.AsString());
        Assert.Equal("ah roster resume --checkpoint --team five --relaunch-missing --yes", RowOfTeam(stdout, "not_owned", "five").Member("next")!.Value.AsString());
    }
}
