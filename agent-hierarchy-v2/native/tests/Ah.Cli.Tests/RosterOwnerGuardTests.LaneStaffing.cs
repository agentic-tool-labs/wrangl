using Ah.Core.Json;
using Ah.Mesh;

namespace Ah.Cli.Tests;

public sealed partial class RosterOwnerGuardTests
{
    private void OpenLane(string managerStreamState = "open", string childWorkstream = "working", string members = "", string? worktree = null, string? managerOwner = null, string? childOwner = null, string? laneRecord = null, string managerExtra = "", string? managerWorktree = null, string? parentTeamId = "t-mo", string childStreams = "")
    {
        var parentId = parentTeamId is null ? string.Empty : ",\"team_id\":\"" + parentTeamId + "\"";
        var tree = worktree is null ? "null" : "\"" + worktree + "\"";
        var managerTree = managerWorktree is null ? string.Empty : ",\"worktree\":\"" + managerWorktree + "\"";
        var managerPid = managerOwner is null ? GonePid + GoneStartPart + "}" : managerOwner + StartPart(managerOwner) + "}";
        var childPid = childOwner ?? Self;
        var lane = laneRecord is null ? string.Empty : ",\"lane\":" + laneRecord;
        WriteTeam(
            "mo",
            "{\"version\":1,\"team_id\":\"t-mo\",\"transport\":\"terminal\",\"orchestrator\":{\"session_id\":null,\"pid\":" + managerPid
            + ",\"members\":[{\"name\":\"mo-so-ws1\",\"role\":\"sub-orchestrator\",\"route\":\"peer\"}" + managerExtra + "],"
            + "\"streams\":{\"ws1\":{\"state\":\"" + managerStreamState + "\",\"sub_orchestrator\":\"mo-so-ws1\",\"base\":\"origin/train\"" + lane + managerTree + "},\"ws2\":{\"state\":\"open\",\"sub_orchestrator\":\"mo-so-ws2\"}}}");
        WriteTeam(
            "mo-ws1",
            "{\"version\":1,\"team_id\":\"t-mo-ws1\",\"transport\":\"terminal\",\"orchestrator\":{\"session_id\":null,\"pid\":" + childPid + StartPart(childPid)
            + "},\"members\":[" + members + "],\"parent\":{\"team\":\"mo\",\"stream\":\"ws1\"" + parentId + "},\"workstream\":{\"state\":\"" + childWorkstream
            + "\"},\"streams\":{\"ws1\":{\"state\":\"open\",\"worktree\":" + tree + "}" + childStreams + "}}");
    }

    private static string LaneRow(string name, string role = "reviewer", string extra = "") =>
        "{\"name\":\"" + name + "\",\"role\":\"" + role + "\",\"route\":\"peer\"" + extra + "}";

    [Fact]
    public void ALaneLeaderSpawnsIntoItsOwnOpenLaneAndTheOtherTeamsAreRefused()
    {
        OpenLane();
        SubOrchestrator();

        var (own, ownOut, ownErr) = Run("spawn-ad-hoc", "reviewer", "--kind", "claude", "--model", "sonnet", "--team", "mo-ws1", "--dry-run");
        var (_, managerOut, managerErr) = Run("spawn-ad-hoc", "reviewer", "--kind", "claude", "--model", "sonnet", "--team", "mo", "--dry-run");
        var (_, otherStream, otherErr) = Run("spawn-ad-hoc", "reviewer", "--kind", "claude", "--model", "sonnet", "--team", "mo-ws1", "--stream", "ws2", "--dry-run");

        Assert.True(own == 0, ownOut + ownErr);
        Assert.Contains("not-child-team", managerOut + managerErr, StringComparison.Ordinal);
        Assert.Contains("not-child-team", otherStream + otherErr, StringComparison.Ordinal);
    }

    [Fact]
    public void ALaneLeaderStaffsAStreamItOpenedUnderItsOwnTeam()
    {
        OpenLane(childStreams: ",\"ws1-x\":{\"state\":\"open\",\"worktree\":null}");
        SubOrchestrator();

        var (code, stdout, stderr) = Run("spawn-ad-hoc", "reviewer", "--kind", "claude", "--model", "sonnet", "--team", "mo-ws1", "--stream", "ws1-x", "--dry-run");

        Assert.True(code == 0, stdout + stderr);
    }

    [Theory]
    [InlineData(",\"ws1-x\":{\"state\":\"done\",\"worktree\":null}", "ws1-x")]
    [InlineData(",\"ws1-x\":{\"state\":\"open\",\"worktree\":null}", "ws1-y")]
    [InlineData(",\"ws1-x\":{\"state\":\"open\",\"worktree\":null}", "ws2")]
    public void NegativeALaneLeaderMayNotStaffAClosedAnUnknownOrAnotherLanesStream(string childStreams, string stream)
    {
        OpenLane(childStreams: childStreams);
        SubOrchestrator();

        var (code, stdout, stderr) = Run("spawn-ad-hoc", "reviewer", "--kind", "claude", "--model", "sonnet", "--team", "mo-ws1", "--stream", stream, "--dry-run");

        Assert.NotEqual(0, code);
        Assert.Contains("not-child-team", stdout + stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ultra-advisor")]
    [InlineData("orchestrator")]
    [InlineData("sub-orchestrator")]
    public void ALaneLeaderMayNotSpawnTheRolesNoLaneMayHave(string role)
    {
        OpenLane();
        SubOrchestrator();

        var (code, stdout, stderr) = Run("spawn-ad-hoc", role, "--kind", "claude", "--model", "opus", "--team", "mo-ws1", "--dry-run");

        Assert.NotEqual(0, code);
        Assert.True((stdout + stderr).Contains("role-not-for-lanes", StringComparison.Ordinal) || (stdout + stderr).Contains("under-specified", StringComparison.Ordinal), stdout + stderr);
    }

    [Theory]
    [InlineData("advise", true)]
    [InlineData("orchestrate", true)]
    [InlineData("review", false)]
    public void ACustomRoleIsRefusedToALaneLeaderByItsClassAdviseAndOrchestrateButNotReview(string roleClass, bool refused)
    {
        var file = Path.Combine(_sandbox.Home, ".claude", "ah2", "agent-hierarchy.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "{\"version\":1,\"enabled\":true,\"roles\":{\"counsel\":{\"class\":\"" + roleClass + "\",\"routes\":\"x\",\"model\":\"opus\"}}}");
        OpenLane();
        SubOrchestrator();

        var (_, stdout, stderr) = Run("spawn-ad-hoc", "counsel", "--kind", "claude", "--model", "opus", "--team", "mo-ws1", "--dry-run");

        Assert.Equal(refused, (stdout + stderr).Contains("role-not-for-lanes", StringComparison.Ordinal));
    }

    [Fact]
    public void ALaneLeaderSpawnsAdHocABuiltInRoleTheManagersRosterLacksButNotAnUnknownRole()
    {
        var file = Path.Combine(_sandbox.Home, ".claude", "ah2", "agent-hierarchy.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "{\"version\":1,\"enabled\":true,\"roster\":{\"route\":\"peer\",\"members\":[{\"role\":\"implementor\"}]}}");
        OpenLane();
        SubOrchestrator();

        var (adHoc, adHocOut, adHocErr) = Run("spawn-ad-hoc", "ux-engineer", "--kind", "claude", "--team", "mo-ws1", "--dry-run");
        var (unknown, unknownOut, unknownErr) = Run("spawn-ad-hoc", "ux-designer", "--kind", "claude", "--team", "mo-ws1", "--dry-run");

        Assert.True(adHoc == 0, adHocOut + adHocErr);
        Assert.True(unknown != 0, unknownOut + unknownErr);
    }

    [Fact]
    public void ASpawnIntoALaneWhoseStreamIsDoneIsRefusedAndADismissOfItsMemberIsNot()
    {
        OpenLane(managerStreamState: "done", members: LaneRow("mo-ws1-m0"));
        SubOrchestrator();

        var (spawn, spawnOut, spawnErr) = Run("spawn-ad-hoc", "reviewer", "--kind", "claude", "--model", "sonnet", "--team", "mo-ws1", "--dry-run");
        var (dismiss, dismissOut, dismissErr) = Run("dismiss", "mo-ws1-m0", "--team", "mo-ws1", "--plan");

        Assert.NotEqual(0, spawn);
        Assert.Contains("lane-closed", spawnOut + spawnErr, StringComparison.Ordinal);
        Assert.DoesNotContain("lane-closed", dismissOut + dismissErr, StringComparison.Ordinal);
        Assert.DoesNotContain("member-cannot", dismissOut + dismissErr, StringComparison.Ordinal);
    }

    [Fact]
    public void ASpawnIntoALaneWhoseWorkstreamIsDoneIsRefused()
    {
        OpenLane(childWorkstream: "done");
        SubOrchestrator();

        var (code, stdout, stderr) = Run("spawn-ad-hoc", "reviewer", "--kind", "claude", "--model", "sonnet", "--team", "mo-ws1", "--dry-run");

        Assert.NotEqual(0, code);
        Assert.Contains("lane-closed", stdout + stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void ACwdOutsideTheLanesWorkTreeIsRefusedAndOneInsideIsNot()
    {
        var tree = Path.Combine(Hierarchy, "lane-tree");
        Directory.CreateDirectory(Path.Combine(tree, "sub"));
        var outside = Path.Combine(Hierarchy, "lane-tree-sibling");
        Directory.CreateDirectory(outside);
        OpenLane(worktree: tree);
        SubOrchestrator();

        var (_, outsideOut, outsideErr) = Run("spawn-ad-hoc", "reviewer", "--kind", "claude", "--model", "sonnet", "--team", "mo-ws1", "--cwd", outside, "--dry-run");
        var (_, insideOut, insideErr) = Run("spawn-ad-hoc", "reviewer", "--kind", "claude", "--model", "sonnet", "--team", "mo-ws1", "--cwd", Path.Combine(tree, "sub"), "--dry-run");

        Assert.Contains("cwd-outside-lane", outsideOut + outsideErr, StringComparison.Ordinal);
        Assert.DoesNotContain("cwd-outside-lane", insideOut + insideErr, StringComparison.Ordinal);
    }

    [Fact]
    public void ACwdIsCheckedAgainstTheManagersRecordedWorkTreeWhenTheChildsOwnStreamRecordHasNone()
    {
        var tree = Path.Combine(Hierarchy, "lane-tree");
        Directory.CreateDirectory(Path.Combine(tree, "sub"));
        var outside = Path.Combine(Hierarchy, "elsewhere");
        Directory.CreateDirectory(outside);
        OpenLane(managerWorktree: tree);
        SubOrchestrator();

        var (_, outsideOut, outsideErr) = Run("spawn-ad-hoc", "reviewer", "--kind", "claude", "--model", "sonnet", "--team", "mo-ws1", "--cwd", outside, "--dry-run");
        var (_, insideOut, insideErr) = Run("spawn-ad-hoc", "reviewer", "--kind", "claude", "--model", "sonnet", "--team", "mo-ws1", "--cwd", Path.Combine(tree, "sub"), "--dry-run");

        Assert.Contains("cwd-outside-lane", outsideOut + outsideErr, StringComparison.Ordinal);
        Assert.DoesNotContain("cwd-outside-lane", insideOut + insideErr, StringComparison.Ordinal);
    }

    [Fact]
    public void ACwdIsRefusedWhenNoWorkTreeIsKnownForTheLane()
    {
        OpenLane();
        SubOrchestrator();

        var (code, stdout, stderr) = Run("spawn-ad-hoc", "reviewer", "--kind", "claude", "--model", "sonnet", "--team", "mo-ws1", "--cwd", Hierarchy, "--dry-run");

        Assert.NotEqual(0, code);
        Assert.Contains("cwd-outside-lane", stdout + stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void ALentMemberIsNotCountedAndADismissOfItIsRefusedAsLent()
    {
        OpenLane(members: LaneRow("mo-ws1-m0"));
        WriteTeam(
            "home",
            "{\"version\":1,\"team_id\":\"t-home\",\"transport\":\"terminal\",\"orchestrator\":{\"session_id\":null,\"pid\":" + GonePid + GoneStartPart
            + "},\"members\":[" + LaneRow("home-arch", "architect", ",\"lead\":{\"team\":\"mo-ws1\",\"name\":\"mo-so-ws1\"}") + "]}");
        SubOrchestrator();

        var (code, stdout, stderr) = Run("dismiss", "home-arch", "--team", "mo-ws1", "--plan");
        var (otherCode, otherOut, otherErr) = Run("dismiss", "nobody", "--team", "mo-ws1", "--plan");

        Assert.NotEqual(0, code);
        Assert.Contains("lent-member", stdout + stderr, StringComparison.Ordinal);
        Assert.Contains("orchestrator@home", stdout + stderr, StringComparison.Ordinal);
        Assert.NotEqual(0, otherCode);
        Assert.DoesNotContain("lent-member", otherOut + otherErr, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTopOwnerRaisesALanesMemberCapAndALaneSpawnBeyondTheOldCapThenSucceedsWhileOnePastTheNewCapIsRefused()
    {
        var rows = string.Join(",", LaneRow("mo-ws1-m0"), LaneRow("mo-ws1-m1"));
        OpenLane(members: rows, managerOwner: Self, laneRecord: "{\"max_members\":2,\"max_streams\":8}");

        var (code, stdout, stderr) = Run("lane-caps", "--stream", "ws1", "--members", "3", "--team", "mo");
        var lane = JsonText.Parse(StoredTeam(Path.Combine(Hierarchy, "teams", "mo.json"))).Member("streams")!.Value.Member("ws1")!.Value.Member("lane")!.Value;
        SubOrchestrator();
        var (beyondOld, beyondOldOut, beyondOldErr) = Run("spawn-ad-hoc", "reviewer", "--kind", "claude", "--model", "sonnet", "--team", "mo-ws1", "--dry-run");

        Assert.True(code == 0, stdout + stderr);
        Assert.Equal(3, (int)lane.Member("max_members")!.Value.AsNumber());
        Assert.Equal(8, (int)lane.Member("max_streams")!.Value.AsNumber());
        Assert.True(beyondOld == 0, beyondOldOut + beyondOldErr);

        OpenLane(members: string.Join(",", rows, LaneRow("mo-ws1-m2")), laneRecord: "{\"max_members\":3,\"max_streams\":8}");
        var (pastNew, pastNewOut, pastNewErr) = Run("spawn-ad-hoc", "reviewer", "--kind", "claude", "--model", "sonnet", "--team", "mo-ws1", "--dry-run");

        Assert.NotEqual(0, pastNew);
        Assert.Contains("lane-members-full", pastNewOut + pastNewErr, StringComparison.Ordinal);
    }

    [Fact]
    public void ALaneCapOutsideOneToSixteenIsRefusedAndWritesNothing()
    {
        OpenLane(managerOwner: Self, laneRecord: "{\"max_members\":2,\"max_streams\":8}");

        var (code, stdout, stderr) = Run("lane-caps", "--stream", "ws1", "--members", "17", "--team", "mo");

        Assert.NotEqual(0, code);
        Assert.Contains("lane-cap-invalid", stdout + stderr, StringComparison.Ordinal);
        Assert.Contains("\"max_members\": 2", StoredTeam(Path.Combine(Hierarchy, "teams", "mo.json")).Replace("\"max_members\":2", "\"max_members\": 2", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnmarkedLaneLeaderWhoseMemberRowIsTheLanesSubOrchestratorSpawnsIntoItsOwnChild()
    {
        OpenLane();
        UnmarkedMember("mo-so-ws1");

        var (code, stdout, stderr) = Run("spawn-ad-hoc", "reviewer", "--kind", "claude", "--model", "sonnet", "--team", "mo-ws1", "--dry-run");

        Assert.True(code == 0, stdout + stderr);
    }

    [Fact]
    public void AnUnmarkedMemberThatIsAnotherStreamsSubOrchestratorIsRefusedOnThisLanesChild()
    {
        OpenLane();
        UnmarkedMember("mo-so-ws2");

        var (code, stdout, stderr) = Run("spawn-ad-hoc", "reviewer", "--kind", "claude", "--model", "sonnet", "--team", "mo-ws1", "--dry-run");

        Assert.NotEqual(0, code);
        Assert.Contains("member-cannot-spawn-ad-hoc", stdout + stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnmarkedLaneLeaderWhoseProcessDoesNotOwnTheChildIsRefused()
    {
        OpenLane(childOwner: $"{GonePid}");
        UnmarkedMember("mo-so-ws1");

        var (code, stdout, stderr) = Run("spawn-ad-hoc", "reviewer", "--kind", "claude", "--model", "sonnet", "--team", "mo-ws1", "--dry-run");

        Assert.NotEqual(0, code);
        Assert.DoesNotContain("\"ok\": true", stdout + stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnmarkedLaneLeaderRunningANonCarvedVerbOnItsChildIsRefused()
    {
        OpenLane();
        UnmarkedMember("mo-so-ws1");

        var (code, stdout, stderr) = Run("transfer", "mo-ws1-m0", "--to", "mo-ws2", "--team", "mo-ws1");

        Assert.NotEqual(0, code);
        Assert.Contains("member-cannot-transfer", stdout + stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void AMemberRefusalSaysWhatTheWalkFound()
    {
        OpenLane();
        UnmarkedMember("mo-implementor");

        var (code, stdout, stderr) = Run("spawn-ad-hoc", "reviewer", "--kind", "claude", "--model", "sonnet", "--team", "mo-ws1", "--dry-run");

        Assert.NotEqual(0, code);
        Assert.Contains("walk: nearest=Member legacy-start=no team=mo member=mo-implementor.", stdout + stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void ALaneLeaderWhoseStartMatchedOnlyInTheLegacyFormIsRefusedAndTheRefusalSaysSo()
    {
        OpenLane();
        _nearest = new NearestSession(NearestKind.Member, "mo", "mo-so-ws1", Environment.ProcessId, true);

        var (code, stdout, stderr) = Run("spawn-ad-hoc", "reviewer", "--kind", "claude", "--model", "sonnet", "--team", "mo-ws1", "--dry-run");

        Assert.NotEqual(0, code);
        Assert.Contains("walk: nearest=Member legacy-start=yes team=mo member=mo-so-ws1.", stdout + stderr, StringComparison.Ordinal);
    }

    private static string LentArchitect(string lane = "mo-ws1", string leadName = "mo-so-ws1") =>
        "," + LaneRow("mo-arch", "architect", ",\"lead\":{\"team\":\"" + lane + "\",\"name\":\"" + leadName + "\"}");

    private static JsonValue TeamRow(string stdout, string team) =>
        JsonText.Parse(stdout).Member("teams")!.Value.AsArray().Single(t => t.Member("name") is { IsText: true } n && n.AsString() == team);

    [Fact]
    public void ALentMemberIsShownUnderItsLaneAndMarkedUnderItsHomeAndTheLanesOwnListIsUnchanged()
    {
        OpenLane(members: LaneRow("mo-ws1-m0"), managerOwner: Self, managerExtra: LentArchitect());

        var (code, stdout, stderr) = Run("teams");

        Assert.True(code == 0, stdout + stderr);
        var lane = TeamRow(stdout, "mo-ws1");
        var home = TeamRow(stdout, "mo");
        Assert.Equal("mo-arch", lane.Member("lent_from")!.Value.AsArray().Single().Member("name")!.Value.AsString());
        Assert.Equal("mo", lane.Member("lent_from")!.Value.AsArray().Single().Member("home")!.Value.AsString());
        Assert.Equal("mo-ws1", home.Member("lent_to")!.Value.AsArray().Single().Member("lane")!.Value.AsString());
        Assert.Equal(1, (int)lane.Member("member_rows")!.Value.AsNumber());
        Assert.Null(home.Member("lent_from"));
        Assert.Null(lane.Member("lent_to"));
    }

    [Theory]
    [InlineData("mo-gone", "mo-so-ws1")]
    [InlineData("mo-ws1", "someone-else")]
    public void ALeadThatNamesAMissingLaneOrAnotherLeaderIsNotShownAsLentAndDoctorWarnsLeadDangling(string lane, string leadName)
    {
        OpenLane(managerOwner: Self, managerExtra: LentArchitect(lane, leadName));

        var (teamsCode, teamsOut, teamsErr) = Run("teams");
        var (doctorCode, doctorOut, doctorErr) = Run("doctor", "--team", "mo");

        Assert.True(teamsCode == 0, teamsOut + teamsErr);
        Assert.Null(TeamRow(teamsOut, "mo").Member("lent_to"));
        Assert.True(doctorCode == 0 || doctorOut.Length > 0, doctorErr);
        Assert.Contains("lead-dangling", doctorOut + doctorErr, StringComparison.Ordinal);
        Assert.DoesNotContain("lent-member", doctorOut + doctorErr, StringComparison.Ordinal);
    }

    [Fact]
    public void DoctorListsALentMemberOnBothTeamsAndAClosedLaneMakesItDangle()
    {
        OpenLane(managerOwner: Self, managerExtra: LentArchitect());

        var (_, homeOut, homeErr) = Run("doctor", "--team", "mo");
        var (_, laneOut, laneErr) = Run("doctor", "--team", "mo-ws1");
        OpenLane(managerStreamState: "done", managerOwner: Self, managerExtra: LentArchitect());
        var (_, closedOut, closedErr) = Run("doctor", "--team", "mo");

        Assert.Contains("mo-arch is lent to mo-ws1", homeOut + homeErr, StringComparison.Ordinal);
        Assert.Contains("mo-arch is lent from mo", laneOut + laneErr, StringComparison.Ordinal);
        Assert.Contains("lead-dangling", closedOut + closedErr, StringComparison.Ordinal);
        Assert.DoesNotContain("lent-member", closedOut + closedErr, StringComparison.Ordinal);
    }

    [Fact]
    public void ALaneLeaderCannotRunLaneCaps()
    {
        OpenLane();
        SubOrchestrator();

        var (code, stdout, stderr) = Run("lane-caps", "--stream", "ws1", "--members", "16", "--team", "mo-ws1");
        var (managerCode, managerOut, managerErr) = Run("lane-caps", "--stream", "ws1", "--members", "16", "--team", "mo");

        Assert.NotEqual(0, code);
        Assert.NotEqual(0, managerCode);
        Assert.Contains("member-cannot-lane-caps", stdout + stderr, StringComparison.Ordinal);
        Assert.Contains("member-cannot-lane-caps", managerOut + managerErr, StringComparison.Ordinal);
    }

    [Fact]
    public void ALaneLeaderCannotRaiseMaxLanesOnItsOwnTeamOrItsManagers()
    {
        OpenLane();
        SubOrchestrator();

        var (code, stdout, stderr) = Run("lane-caps", "--max-lanes", "9", "--team", "mo-ws1");
        var (managerCode, managerOut, managerErr) = Run("lane-caps", "--max-lanes", "9", "--team", "mo");

        Assert.NotEqual(0, code);
        Assert.NotEqual(0, managerCode);
        Assert.Contains("member-cannot-lane-caps", stdout + stderr, StringComparison.Ordinal);
        Assert.Contains("member-cannot-lane-caps", managerOut + managerErr, StringComparison.Ordinal);
        Assert.DoesNotContain("max_lanes", StoredTeam(Path.Combine(Hierarchy, "teams", "mo.json")), StringComparison.Ordinal);
    }
}
