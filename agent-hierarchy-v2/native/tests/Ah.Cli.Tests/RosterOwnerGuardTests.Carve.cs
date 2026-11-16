using Ah.Core.Json;
using Ah.Mesh;

namespace Ah.Cli.Tests;

/// <summary>The sub-orchestrator carve-out: a marked shell passes only as the registered orchestrate member, on its own child team.</summary>
public sealed partial class RosterOwnerGuardTests
{
    private const int GonePid = 2147483000;

    /// <summary>The start recorded beside <see cref="GonePid"/>: with it the exited pid proves the owner gone, without it nothing does.</summary>
    private const string GoneStartPart = ",\"pid_start\":\"Mon Jan  1 00:00:00 2001\"";

    private string ManagerFile => Path.Combine(Hierarchy, "teams", "mo.json");

    private void WriteTeam(string team, string json)
    {
        _sandbox.PlantTeam(Path.Combine(Hierarchy, "teams", team + ".json"), json);
    }

    private void Manager(string soRole = "sub-orchestrator")
    {
        WriteTeam(
            "mo",
            "{\"version\":1,\"team_id\":\"t-mo\",\"transport\":\"terminal\",\"orchestrator\":{\"session_id\":null,\"pid\":" + GonePid + GoneStartPart
            + "},\"members\":[{\"name\":\"mo-so-ws1\",\"role\":\"" + soRole + "\",\"route\":\"peer\"},{\"name\":\"mo-implementor\",\"role\":\"implementor\",\"route\":\"peer\"}],"
            + "\"streams\":{\"ws1\":{\"state\":\"open\",\"sub_orchestrator\":\"mo-so-ws1\"},\"ws2\":{\"state\":\"open\",\"sub_orchestrator\":\"mo-so-ws2\"}}}");
    }

    private void Child(string team, string parent, string stream, string ownerPid)
    {
        WriteTeam(
            team,
            "{\"version\":1,\"team_id\":\"t-" + team + "\",\"transport\":\"terminal\",\"orchestrator\":{\"session_id\":null,\"pid\":" + ownerPid + StartPart(ownerPid)
            + "},\"members\":[],\"parent\":{\"team\":\"" + parent + "\",\"stream\":\"" + stream + "\"}}");
    }

    private void SubOrchestrator(string member = "mo-so-ws1", string team = "mo", bool legacy = false)
    {
        _env[MeshModule.MemberMarkerEnv] = member;
        _env["WRANGL_TEAM_FILE"] = ManagerFile;
        _nearest = new NearestSession(NearestKind.Member, team, member, Environment.ProcessId, legacy);
    }

    private void UnmarkedMember(string member, string team = "mo") =>
        _nearest = new NearestSession(NearestKind.Member, team, member, Environment.ProcessId, false);

    public static TheoryData<string> CarvedOnTheChild() => ["spawn-one", "spawn-ad-hoc", "untrack", "dismiss", "disband", "progress", "move", "stream-open", "stream-done"];

    [Theory]
    [MemberData(nameof(CarvedOnTheChild))]
    public void ASubOrchestratorPassesTheGuardOnItsOwnChildTeam(string verb)
    {
        Manager();
        Child("mo-ws1", "mo", "ws1", Self);
        SubOrchestrator();

        var (_, stdout, _) = Run(verb, "--team", "mo-ws1");

        Assert.Null(Refusal(stdout).Refused);
    }

    [Fact]
    public void ASubOrchestratorMayCreateItsChildForItsOwnStream()
    {
        Manager();
        SubOrchestrator();

        var (_, stdout, _) = Run("create", "--parent", "mo", "--stream", "ws1");

        Assert.Null(Refusal(stdout).Refused);
    }

    [Fact]
    public void B429LaneChildExistsNamesAdoptOnlyWhenTheChildIsOrphaned()
    {
        Manager();
        SubOrchestrator();
        Child("mo-ws1", "mo", "ws1", GonePid.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var (_, _, orphaned) = Run("create", "--parent", "mo", "--stream", "ws1");
        Child("mo-ws1", "mo", "ws1", LivePidOfAnotherProcess().ToString(System.Globalization.CultureInfo.InvariantCulture));
        var (_, _, live) = Run("create", "--parent", "mo", "--stream", "ws1");

        Assert.Contains("lane-child-exists", orphaned, StringComparison.Ordinal);
        Assert.Contains("ah roster adopt --team mo-ws1", orphaned, StringComparison.Ordinal);
        Assert.Contains("lane-child-exists", live, StringComparison.Ordinal);
        Assert.DoesNotContain("adopt", live, StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeASubOrchestratorCannotCreateTheChildOfAStreamThatIsDone()
    {
        WriteTeam(
            "mo",
            "{\"version\":1,\"team_id\":\"t-mo\",\"transport\":\"terminal\",\"orchestrator\":{\"session_id\":null,\"pid\":" + GonePid + GoneStartPart
            + "},\"members\":[{\"name\":\"mo-so-ws1\",\"role\":\"sub-orchestrator\",\"route\":\"peer\"}],"
            + "\"streams\":{\"ws1\":{\"state\":\"done\",\"sub_orchestrator\":\"mo-so-ws1\"}}}");
        UnmarkedMember("mo-so-ws1");

        var (code, stdout, _) = Run("create", "--parent", "mo", "--stream", "ws1");

        Assert.Equal(2, code);
        Assert.Equal(("not-child-team", "member-marker"), Refusal(stdout));
    }

    [Fact]
    public void ASubOrchestratorWithNoMemberMarkerMayCreateItsChildWhenItsAncestryProvesIt()
    {
        Manager();
        UnmarkedMember("mo-so-ws1");

        var (_, stdout, _) = Run("create", "--parent", "mo", "--stream", "ws1");

        Assert.Null(Refusal(stdout).Refused);
    }

    [Fact]
    public void AnUnregisteredChildOfTheSubOrchestratorHoldsItsCarveUntilItRegistersAsAnotherMember()
    {
        Manager();
        UnmarkedMember("mo-so-ws1");
        var (_, inherited, _) = Run("create", "--parent", "mo", "--stream", "ws1");

        UnmarkedMember("mo-implementor");
        var (_, registered, _) = Run("create", "--parent", "mo", "--stream", "ws1");

        Assert.Null(Refusal(inherited).Refused);
        Assert.Equal(("member-cannot-create", "member-ancestor"), Refusal(registered));
    }

    [Fact]
    public void AnotherMemberWithNoMarkerStaysRefusedForCreateAndForACarvedVerb()
    {
        Manager();
        Child("mo-ws1", "mo", "ws1", Self);
        UnmarkedMember("mo-implementor");

        var (_, create, _) = Run("create", "--parent", "mo", "--stream", "ws1");
        var (_, spawn, _) = Run("spawn-one", "--team", "mo-ws1");

        Assert.Equal(("member-cannot-create", "member-ancestor"), Refusal(create));
        Assert.Equal(("member-cannot-spawn-one", "member-ancestor"), Refusal(spawn));
    }

    [Theory]
    [InlineData("mo", "ws2")]
    [InlineData("other", "ws1")]
    [InlineData("mo", "nope")]
    public void ASubOrchestratorWithNoMarkerCannotCreateForAStreamItDoesNotRun(string parent, string stream)
    {
        Manager();
        UnmarkedMember("mo-so-ws1");

        var (code, stdout, _) = Run("create", "--parent", parent, "--stream", stream);

        Assert.Equal(2, code);
        Assert.Equal(("not-child-team", "member-marker"), Refusal(stdout));
    }

    [Theory]
    [InlineData("reap")]
    [InlineData("transfer")]
    public void ASubOrchestratorWithNoMarkerStillCannotRunAVerbThatIsNotCarved(string verb)
    {
        Manager();
        UnmarkedMember("mo-so-ws1");

        var (_, stdout, _) = Run(verb, "--team", "mo");

        Assert.Equal(($"member-cannot-{verb}", "member-ancestor"), Refusal(stdout));
    }

    [Fact]
    public void AnUnmarkedMemberWhoseRowIsNotTheOrchestrateClassGetsNoCarveEvenForItsOwnStream()
    {
        Manager(soRole: "implementor");
        UnmarkedMember("mo-so-ws1");

        var (_, stdout, _) = Run("create", "--parent", "mo", "--stream", "ws1");

        Assert.Equal(("member-cannot-create", "member-ancestor"), Refusal(stdout));
    }

    [Theory]
    [InlineData("mo", "ws2")]
    [InlineData("other", "ws1")]
    [InlineData("mo", "nope")]
    public void ACreateForAnotherStreamOrAnotherParentIsAScopeMiss(string parent, string stream)
    {
        Manager();
        SubOrchestrator();

        var (code, stdout, stderr) = Run("create", "--parent", parent, "--stream", stream);

        Assert.Equal(2, code);
        Assert.Equal(("not-child-team", "member-marker"), Refusal(stdout));
        Assert.Contains("only on its own child team", stderr, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(CarvedOnTheChild))]
    public void ACarvedVerbOnTheManagersTeamASiblingsChildOrAnUnrelatedTeamIsAScopeMiss(string verb)
    {
        Manager();
        Child("mo-ws1", "mo", "ws1", Self);
        Child("mo-ws2", "mo", "ws2", Self);
        WriteTeam("lone", "{\"version\":1,\"team_id\":\"t-lone\",\"transport\":\"terminal\",\"orchestrator\":{\"session_id\":null,\"pid\":" + Self + ",\"pid_start\":\"" + SelfStart + "\"},\"members\":[]}");
        SubOrchestrator();

        foreach (var team in new[] { "mo", "mo-ws2", "lone", "absent" })
        {
            var (code, stdout, _) = Run(verb, "--team", team);

            Assert.Equal(2, code);
            Assert.Equal(("not-child-team", "member-marker"), Refusal(stdout));
        }
    }

    [Fact]
    public void AChildWhoseParentTeamIsNotTheRegisteredMembersTeamIsAScopeMiss()
    {
        Manager();
        Child("mo-ws1", "someone-else", "ws1", Self);
        SubOrchestrator();

        var (_, stdout, _) = Run("spawn-one", "--team", "mo-ws1");

        Assert.Equal(("not-child-team", "member-marker"), Refusal(stdout));
    }

    [Fact]
    public void AChildOfAStreamAnotherSubOrchestratorRunsIsAScopeMiss()
    {
        Manager();
        Child("mo-ws1", "mo", "ws2", Self);
        SubOrchestrator();

        var (_, stdout, _) = Run("spawn-one", "--team", "mo-ws1");

        Assert.Equal(("not-child-team", "member-marker"), Refusal(stdout));
    }

    [Fact]
    public void AChildOwnedByAnotherProcessIsAScopeMiss()
    {
        Manager();
        Child("mo-ws1", "mo", "ws1", LivePidOfAnotherProcess().ToString(System.Globalization.CultureInfo.InvariantCulture));
        SubOrchestrator();

        var (_, stdout, _) = Run("spawn-one", "--team", "mo-ws1");

        Assert.Equal(("not-child-team", "member-marker"), Refusal(stdout));
    }

    [Fact]
    public void AdoptNeedsTheChildsOwnerToBeGone()
    {
        Manager();
        SubOrchestrator();
        Child("mo-ws1", "mo", "ws1", GonePid.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var (_, gone, _) = Run("adopt", "--team", "mo-ws1");
        Child("mo-ws1", "mo", "ws1", LivePidOfAnotherProcess().ToString(System.Globalization.CultureInfo.InvariantCulture));
        var (_, alive, _) = Run("adopt", "--team", "mo-ws1");

        Assert.Null(Refusal(gone).Refused);
        Assert.Equal(("not-child-team", "member-marker"), Refusal(alive));
    }

    [Theory]
    [InlineData("spawn-one")]
    [InlineData("spawn-ad-hoc")]
    [InlineData("untrack")]
    [InlineData("dismiss")]
    [InlineData("disband")]
    [InlineData("adopt")]
    [InlineData("progress")]
    [InlineData("move")]
    [InlineData("stream-open")]
    [InlineData("stream-done")]
    public void ACarvedVerbWithoutATeamFlagAsksForTheChildTeam(string verb)
    {
        Manager();
        Child("mo-ws1", "mo", "ws1", Self);
        SubOrchestrator();

        var (code, stdout, stderr) = Run(verb);

        Assert.Equal(2, code);
        Assert.Equal(("child-team-unnamed", "member-marker"), Refusal(stdout));
        Assert.Contains($"ah roster {verb}: name the child team with --team; nothing was changed.", stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("reap")]
    [InlineData("transfer")]
    public void AVerbThatIsNotCarvedKeepsTodaysMarkerRefusal(string verb)
    {
        Manager();
        Child("mo-ws1", "mo", "ws1", Self);
        SubOrchestrator();

        var (code, stdout, stderr) = Run(verb, "--team", "mo-ws1");

        Assert.Equal(2, code);
        Assert.Equal(($"member-cannot-{verb}", "member-marker"), Refusal(stdout));
        Assert.StartsWith($"ah roster {verb}: this shell carries a member marker", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void AMoveWithReplaceLiveIsRefusedForASubOrchestratorButNotWithoutIt()
    {
        Manager();
        Child("mo-ws1", "mo", "ws1", Self);
        SubOrchestrator();

        var (code, stdout, stderr) = Run("move", "--team", "mo-ws1", "--stream", "x", "--replace-live");
        var (_, plain, _) = Run("move", "--team", "mo-ws1", "--stream", "x");

        Assert.Equal(2, code);
        Assert.Equal(("lane-replace-live", "member-marker"), Refusal(stdout));
        Assert.Contains("--replace-live", stderr, StringComparison.Ordinal);
        Assert.Null(Refusal(plain).Refused);
    }

    [Theory]
    [InlineData("--tab", "t1")]
    [InlineData("--workspace", "w1")]
    [InlineData("--new-workspace", null)]
    [InlineData("--allow-global", null)]
    public void AMoveWithAPaneDestinationFlagIsRefusedForASubOrchestrator(string flag, string? value)
    {
        Manager();
        Child("mo-ws1", "mo", "ws1", Self);
        SubOrchestrator();
        var args = new List<string> { "--team", "mo-ws1", flag };
        if (value is not null)
        {
            args.Add(value);
        }

        var (code, stdout, _) = Run(["move", .. args]);

        Assert.Equal(2, code);
        Assert.Equal(("lane-pane-destination", "member-marker"), Refusal(stdout));
    }

    [Theory]
    [InlineData("--tab", "t1")]
    [InlineData("--workspace", "w1")]
    [InlineData("--new-workspace", null)]
    [InlineData("--allow-global", null)]
    public void TheOwnerMayStillPassAPaneDestinationFlagToMove(string flag, string? value)
    {
        TeamOwnedBy(Self);
        var args = new List<string> { "--team", "tm", flag };
        if (value is not null)
        {
            args.Add(value);
        }

        var (_, stdout, _) = Run(["move", .. args]);

        Assert.Null(Refusal(stdout).Refused);
    }

    private void LaneManager(string? laneBase, string? lane = null, string? laneBranch = null)
    {
        var baseJson = laneBase is null ? "null" : "\"" + laneBase + "\"";
        var laneJson = (lane is null ? string.Empty : ",\"lane\":" + lane) + (laneBranch is null ? string.Empty : ",\"branch\":\"" + laneBranch + "\"");
        WriteTeam(
            "mo",
            "{\"version\":1,\"team_id\":\"t-mo\",\"transport\":\"terminal\",\"orchestrator\":{\"session_id\":null,\"pid\":" + GonePid + GoneStartPart
            + "},\"members\":[{\"name\":\"mo-so-ws1\",\"role\":\"sub-orchestrator\",\"route\":\"peer\"}],"
            + "\"streams\":{\"ws1\":{\"state\":\"open\",\"sub_orchestrator\":\"mo-so-ws1\",\"base\":" + baseJson + laneJson + "}}}");
    }

    private void LaneChild(int open, int done = 0)
    {
        var streams = string.Join(
            ",",
            Enumerable.Range(0, open).Select(i => "\"ws1-o" + i + "\":{\"state\":\"open\"}")
                .Concat(Enumerable.Range(0, done).Select(i => "\"ws1-d" + i + "\":{\"state\":\"done\"}")));
        WriteTeam(
            "mo-ws1",
            "{\"version\":1,\"team_id\":\"t-mo-ws1\",\"transport\":\"terminal\",\"orchestrator\":{\"session_id\":null,\"pid\":" + Self + StartPart(Self)
            + "},\"members\":[],\"parent\":{\"team\":\"mo\",\"stream\":\"ws1\"},\"streams\":{" + streams + "}}");
    }

    private static string? RefusedCode(string stdout) =>
        stdout.TrimStart().StartsWith('{') && JsonText.Parse(stdout).AsObject().Get("refused") is { IsText: true } code ? code.AsString() : null;

    [Fact]
    public void ALaneStreamOutsideTheLanePrefixIsRefusedAndOneInsideIsNot()
    {
        LaneManager("origin/train");
        LaneChild(0);
        SubOrchestrator();

        var (_, outside, _) = Run("stream-open", "other-x", "--team", "mo-ws1", "--dry-run");
        var (_, inside, _) = Run("stream-open", "ws1-a", "--team", "mo-ws1", "--dry-run");

        Assert.Equal("lane-name", RefusedCode(outside));
        Assert.NotEqual("lane-name", RefusedCode(inside));
    }

    [Fact]
    public void ALaneBranchOutsideTheLanePrefixIsRefused()
    {
        LaneManager("origin/train");
        LaneChild(0);
        SubOrchestrator();

        var (_, outside, _) = Run("stream-open", "ws1-a", "--team", "mo-ws1", "--branch", "feature/x", "--dry-run");
        var (_, inside, _) = Run("stream-open", "ws1-a", "--team", "mo-ws1", "--branch", "ws1-a", "--dry-run");

        Assert.Equal("lane-name", RefusedCode(outside));
        Assert.NotEqual("lane-name", RefusedCode(inside));
    }

    [Fact]
    public void ABaseOtherThanTheLaneBaseIsRefusedAndTheLaneBaseItselfIsNot()
    {
        LaneManager("origin/train");
        LaneChild(0);
        SubOrchestrator();

        var (_, other, _) = Run("stream-open", "ws1-a", "--team", "mo-ws1", "--base", "origin/main", "--dry-run");
        var (_, same, _) = Run("stream-open", "ws1-a", "--team", "mo-ws1", "--base", "origin/train", "--dry-run");

        Assert.Equal("lane-base-fixed", RefusedCode(other));
        Assert.NotEqual("lane-base-fixed", RefusedCode(same));
    }

    [Fact]
    public void ALaneChildMayBranchFromTheLanesOwnBranchButNotFromAnotherBranch()
    {
        LaneManager("origin/train", laneBranch: "ws1");
        LaneChild(0);
        SubOrchestrator();

        var (_, lane, _) = Run("stream-open", "ws1-a", "--team", "mo-ws1", "--base", "ws1", "--dry-run");
        var (_, sibling, _) = Run("stream-open", "ws1-a", "--team", "mo-ws1", "--base", "ws1-other", "--dry-run");

        Assert.NotEqual("lane-base-fixed", RefusedCode(lane));
        Assert.Equal("lane-base-fixed", RefusedCode(sibling));
    }

    [Theory]
    [InlineData("ws1", "ws1")]
    [InlineData(null, "origin/train")]
    public void B358ANewLaneStreamBranchesFromTheLaneBranchAndFromTheLaneBaseOnlyWhenTheLaneHasNoBranch(string? laneBranch, string expected)
    {
        LaneManager("origin/train", laneBranch: laneBranch);
        LaneChild(0);
        SubOrchestrator();
        Directory.CreateDirectory(Path.Combine(Project, ".git"));
        _runner.Output = argv => argv.Contains("--verify") ? "abc1234abc1234abc1234abc1234abc1234abc12\n" : string.Empty;

        var (_, stdout, _) = Run("stream-open", "ws1-a", "--team", "mo-ws1", "--dry-run");

        Assert.Contains("\"base: " + expected + " ", stdout, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(8, 0, null, "lane-streams-full")]
    [InlineData(7, 0, null, null)]
    [InlineData(2, 0, "{\"max_members\":4,\"max_streams\":2}", "lane-streams-full")]
    [InlineData(1, 5, "{\"max_members\":4,\"max_streams\":2}", null)]
    public void TheNinthOpenLaneStreamIsRefusedAndClosedOnesDoNotCount(int open, int done, string? lane, string? expected)
    {
        LaneManager("origin/train", lane);
        LaneChild(open, done);
        SubOrchestrator();

        var (_, stdout, _) = Run("stream-open", "ws1-new", "--team", "mo-ws1", "--dry-run");

        Assert.Equal(expected, RefusedCode(stdout) is { } code && code.StartsWith("lane-", StringComparison.Ordinal) ? code : null);
    }

    [Fact]
    public void AnOpenLaneStreamTouchedAgainIsNotCountedAgainstTheCap()
    {
        LaneManager("origin/train", "{\"max_members\":4,\"max_streams\":1}");
        LaneChild(1);
        SubOrchestrator();

        var (_, stdout, _) = Run("stream-open", "ws1-o0", "--team", "mo-ws1", "--dry-run");

        Assert.NotEqual("lane-streams-full", RefusedCode(stdout));
    }

    [Fact]
    public void ReopeningADoneLaneStreamIsRefusedWhenTheLaneIsFull()
    {
        LaneManager("origin/train", "{\"max_members\":4,\"max_streams\":2}");
        LaneChild(2, done: 1);
        SubOrchestrator();

        var (_, stdout, _) = Run("stream-open", "ws1-d0", "--team", "mo-ws1", "--dry-run");

        Assert.Equal("lane-streams-full", RefusedCode(stdout));
    }

    [Fact]
    public void ReopeningADoneLaneStreamIsAllowedWhenTheLaneHasRoom()
    {
        LaneManager("origin/train", "{\"max_members\":4,\"max_streams\":2}");
        LaneChild(1, done: 1);
        SubOrchestrator();

        var (_, stdout, _) = Run("stream-open", "ws1-d0", "--team", "mo-ws1", "--dry-run");

        Assert.NotEqual("lane-streams-full", RefusedCode(stdout));
    }

    [Fact]
    public void ALaneStreamNamedUnderAnotherLanesNameIsRefusedAndOneUnderItsOwnIsNot()
    {
        WriteTeam(
            "mo",
            "{\"version\":1,\"team_id\":\"t-mo\",\"transport\":\"terminal\",\"orchestrator\":{\"session_id\":null,\"pid\":" + GonePid + GoneStartPart
            + "},\"members\":[{\"name\":\"mo-so-ui\",\"role\":\"sub-orchestrator\",\"route\":\"peer\"},{\"name\":\"mo-so-ui-polish\",\"role\":\"sub-orchestrator\",\"route\":\"peer\"}],\"streams\":{\"ui\":{\"state\":\"open\",\"sub_orchestrator\":\"mo-so-ui\",\"base\":\"origin/train\"},"
            + "\"ui-polish\":{\"state\":\"open\",\"sub_orchestrator\":\"mo-so-ui-polish\",\"base\":\"origin/train\"}}}");
        WriteTeam(
            "mo-ui",
            "{\"version\":1,\"team_id\":\"t-mo-ui\",\"transport\":\"terminal\",\"orchestrator\":{\"session_id\":null,\"pid\":" + Self + StartPart(Self)
            + "},\"members\":[],\"parent\":{\"team\":\"mo\",\"stream\":\"ui\"},\"streams\":{}}");
        SubOrchestrator("mo-so-ui");

        var (_, other, _) = Run("stream-open", "ui-polish-x", "--team", "mo-ui", "--dry-run");
        var (_, own, _) = Run("stream-open", "ui-x", "--team", "mo-ui", "--dry-run");

        Assert.Equal("lane-name", RefusedCode(other));
        Assert.NotEqual("lane-name", RefusedCode(own));
    }

    [Theory]
    [InlineData("ui-polish", null)]
    [InlineData("ui-a", "ui-polish-x")]
    [InlineData("ui-a", "ui-polish")]
    public void ALaneStreamOrBranchFallingUnderAnotherStreamOfTheManagerIsRefused(string name, string? branch)
    {
        WriteTeam(
            "mo",
            "{\"version\":1,\"team_id\":\"t-mo\",\"transport\":\"terminal\",\"orchestrator\":{\"session_id\":null,\"pid\":" + GonePid + GoneStartPart
            + "},\"members\":[{\"name\":\"mo-so-ui\",\"role\":\"sub-orchestrator\",\"route\":\"peer\"},{\"name\":\"mo-so-ui-polish\",\"role\":\"sub-orchestrator\",\"route\":\"peer\"}],\"streams\":{\"ui\":{\"state\":\"open\",\"sub_orchestrator\":\"mo-so-ui\",\"base\":\"origin/train\"},"
            + "\"ui-polish\":{\"state\":\"open\",\"base\":\"origin/train\"}}}");
        WriteTeam(
            "mo-ui",
            "{\"version\":1,\"team_id\":\"t-mo-ui\",\"transport\":\"terminal\",\"orchestrator\":{\"session_id\":null,\"pid\":" + Self + StartPart(Self)
            + "},\"members\":[],\"parent\":{\"team\":\"mo\",\"stream\":\"ui\"},\"streams\":{}}");
        SubOrchestrator("mo-so-ui");

        var args = new List<string> { "stream-open", name, "--team", "mo-ui", "--dry-run" };
        if (branch is not null)
        {
            args.AddRange(["--branch", branch]);
        }

        var (_, stdout, _) = Run([.. args]);
        var (_, fine, _) = Run("stream-open", "ui-a", "--team", "mo-ui", "--branch", "ui-a-x", "--dry-run");

        Assert.Equal("lane-name", RefusedCode(stdout));
        Assert.NotEqual("lane-name", RefusedCode(fine));
    }

    private void UiManagerWithChild(string lane, string polishBranch)
    {
        WriteTeam(
            "mo",
            "{\"version\":1,\"team_id\":\"t-mo\",\"transport\":\"terminal\",\"orchestrator\":{\"session_id\":null,\"pid\":" + GonePid + GoneStartPart
            + "},\"members\":[{\"name\":\"mo-so-ui\",\"role\":\"sub-orchestrator\",\"route\":\"peer\"},{\"name\":\"mo-so-ui-polish\",\"role\":\"sub-orchestrator\",\"route\":\"peer\"}],\"streams\":{\"ui\":{\"state\":\"open\",\"sub_orchestrator\":\"mo-so-ui\",\"base\":\"origin/train\"},"
            + "\"ui-polish\":{\"state\":\"open\",\"sub_orchestrator\":\"mo-so-ui-polish\",\"base\":\"origin/train\",\"branch\":\"" + polishBranch + "\"}}}");
        WriteTeam(
            "mo-" + lane,
            "{\"version\":1,\"team_id\":\"t-mo-" + lane + "\",\"transport\":\"terminal\",\"orchestrator\":{\"session_id\":null,\"pid\":" + Self + StartPart(Self)
            + "},\"members\":[],\"parent\":{\"team\":\"mo\",\"stream\":\"" + lane + "\"},\"streams\":{}}");
        SubOrchestrator("mo-so-" + lane);
    }

    [Fact]
    public void TheMoreSpecificLaneMayOpenUnderItsOwnNameWhileTheBroaderOneMayNot()
    {
        UiManagerWithChild("ui-polish", "ui-polish");
        var (_, own, _) = Run("stream-open", "ui-polish-x", "--team", "mo-ui-polish", "--dry-run");

        UiManagerWithChild("ui", "ui-polish");
        var (_, broader, _) = Run("stream-open", "ui-polish-x", "--team", "mo-ui", "--dry-run");

        Assert.NotEqual("lane-name", RefusedCode(own));
        Assert.Equal("lane-name", RefusedCode(broader));
    }

    [Fact]
    public void ALaneStreamUnderAnotherStreamsRecordedBranchIsRefused()
    {
        UiManagerWithChild("ui", "ui-polish");
        var (_, fine, _) = Run("stream-open", "ui-a", "--team", "mo-ui", "--branch", "ui-a", "--dry-run");

        WriteTeam(
            "mo",
            "{\"version\":1,\"team_id\":\"t-mo\",\"transport\":\"terminal\",\"orchestrator\":{\"session_id\":null,\"pid\":" + GonePid + GoneStartPart
            + "},\"members\":[{\"name\":\"mo-so-ui\",\"role\":\"sub-orchestrator\",\"route\":\"peer\"},{\"name\":\"mo-so-ui-polish\",\"role\":\"sub-orchestrator\",\"route\":\"peer\"}],\"streams\":{\"ui\":{\"state\":\"open\",\"sub_orchestrator\":\"mo-so-ui\",\"base\":\"origin/train\"},"
            + "\"polish\":{\"state\":\"open\",\"base\":\"origin/train\",\"branch\":\"ui-polish\"}}}");
        var (_, viaBranch, _) = Run("stream-open", "ui-polish-x", "--team", "mo-ui", "--dry-run");

        Assert.NotEqual("lane-name", RefusedCode(fine));
        Assert.Equal("lane-name", RefusedCode(viaBranch));
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(1, false)]
    public void AMemberRowCountsTowardTheLaneCapWhetherOrNotItsPaneIsUp(int rows, bool refused)
    {
        LaneManager("origin/train", "{\"max_members\":2,\"max_streams\":8}");
        var members = string.Join(",", Enumerable.Range(0, rows).Select(i => "{\"name\":\"mo-ws1-m" + i + "\",\"role\":\"reviewer\",\"route\":\"peer\"}"));
        WriteTeam(
            "mo-ws1",
            "{\"version\":1,\"team_id\":\"t-mo-ws1\",\"transport\":\"terminal\",\"orchestrator\":{\"session_id\":null,\"pid\":" + Self + StartPart(Self)
            + "},\"members\":[" + members + "],\"parent\":{\"team\":\"mo\",\"stream\":\"ws1\"},\"streams\":{\"ws1\":{\"state\":\"open\"}}}");
        SubOrchestrator();

        var (_, stdout, stderr) = Run("spawn-ad-hoc", "reviewer", "--kind", "claude", "--team", "mo-ws1", "--dry-run");

        Assert.Equal(refused, (stdout + stderr).Contains("lane-members-full", StringComparison.Ordinal));
    }

    private string TmuxLaneChild(int rows)
    {
        var members = string.Join(",", Enumerable.Range(0, rows).Select(i => "{\"name\":\"mo-ws1-m" + i + "\",\"role\":\"reviewer\",\"route\":\"peer\"}"));
        return "{\"version\":1,\"team_id\":\"t-mo-ws1\",\"transport\":\"tmux\",\"orchestrator\":{\"session_id\":null,\"pid\":" + Self + StartPart(Self)
            + "},\"members\":[" + members + "],\"parent\":{\"team\":\"mo\",\"stream\":\"ws1\"},\"streams\":{\"ws1\":{\"state\":\"open\"}}}";
    }

    private (int Code, string Output) SpawnReviewerWhileTheLaneFills(int rowsAtLaunch, int rowsBeforeTheWrite)
    {
        LaneManager("origin/train", "{\"max_members\":2,\"max_streams\":8}");
        WriteTeam("mo-ws1", TmuxLaneChild(rowsAtLaunch));
        SubOrchestrator();
        _env["TMUX"] = "/tmp/tmux-1/default,1,0";
        _env["TMUX_PANE"] = "%0";
        _runner.Output = argv =>
        {
            if (!argv.Contains("new-window"))
            {
                return string.Empty;
            }

            if (rowsBeforeTheWrite >= 0)
            {
                WriteTeam("mo-ws1", TmuxLaneChild(rowsBeforeTheWrite));
            }

            return "%7\n";
        };

        _withScratch = true;
        var (code, stdout, stderr) = Run("spawn-ad-hoc", "reviewer", "--kind", "claude", "--model", "sonnet", "--team", "mo-ws1");
        return (code, stdout + stderr);
    }

    private bool PaneWasClosed() => _runner.Calls.Any(c => c.SequenceEqual(["tmux", "kill-pane", "-t", "%7"]));

    [Fact]
    public void ASpawnIntoAFullLaneIsRefusedBeforeAnyPaneOpens()
    {
        var (code, output) = SpawnReviewerWhileTheLaneFills(rowsAtLaunch: 2, rowsBeforeTheWrite: -1);

        Assert.NotEqual(0, code);
        Assert.Contains("lane-members-full", output, StringComparison.Ordinal);
        Assert.DoesNotContain(_runner.Calls, argv => argv.Contains("new-window"));
        Assert.Equal(2, JsonText.Parse(StoredTeam(Path.Combine(Hierarchy, "teams", "mo-ws1.json"))).Member("members")!.Value.AsArray().Count);
    }

    [Fact]
    public void ASpawnThatLeavesTheLaneBelowItsCapRecordsTheMemberAndClosesNoPane()
    {
        var (code, output) = SpawnReviewerWhileTheLaneFills(rowsAtLaunch: 1, rowsBeforeTheWrite: -1);

        Assert.True(code == 0, output);
        Assert.False(PaneWasClosed());
        var rows = JsonText.Parse(StoredTeam(Path.Combine(Hierarchy, "teams", "mo-ws1.json"))).Member("members")!.Value.AsArray();
        Assert.Equal(2, rows.Count);
        Assert.Equal("%7", rows[1].AsObject()["transport_id"].AsString());
        Assert.True(rows[1].AsObject().ContainsKey("spawned_at"));
    }

    [Fact]
    public void ASpawnWritesItsRowWithSpawnedAtAndNoPaneBeforeThePaneOpens()
    {
        LaneManager("origin/train", "{\"max_members\":3,\"max_streams\":8}");
        WriteTeam("mo-ws1", TmuxLaneChild(0));
        SubOrchestrator();
        _env["TMUX"] = "/tmp/tmux-1/default,1,0";
        _env["TMUX_PANE"] = "%0";
        var atLaunch = string.Empty;
        _runner.Output = argv =>
        {
            if (!argv.Contains("new-window"))
            {
                return string.Empty;
            }

            atLaunch = StoredTeam(Path.Combine(Hierarchy, "teams", "mo-ws1.json"));
            return "%7\n";
        };

        _withScratch = true;
        var (code, stdout, stderr) = Run("spawn-ad-hoc", "reviewer", "--kind", "claude", "--model", "sonnet", "--team", "mo-ws1");

        Assert.True(code == 0, stdout + stderr);
        var row = JsonText.Parse(atLaunch).Member("members")!.Value.AsArray().Single().AsObject();
        Assert.StartsWith("mo-ws1-", row["name"].AsString(), StringComparison.Ordinal);
        Assert.True(row.ContainsKey("spawned_at"));
        Assert.False(row.ContainsKey("transport_id"));
    }

    [Fact]
    public void ASpawnWhoseLaunchFailsLeavesTheTeamFileAsItWasBeforeTheCall()
    {
        LaneManager("origin/train", "{\"max_members\":3,\"max_streams\":8}");
        WriteTeam("mo-ws1", TmuxLaneChild(1));
        SubOrchestrator();
        _env["TMUX"] = "/tmp/tmux-1/default,1,0";
        _env["TMUX_PANE"] = "%0";
        var before = StoredTeam(Path.Combine(Hierarchy, "teams", "mo-ws1.json"));
        _runner.Output = argv => argv.Contains("new-window") ? throw new InvalidOperationException("tmux refused the new window") : string.Empty;

        _withScratch = true;
        var (code, stdout, stderr) = Run("spawn-ad-hoc", "reviewer", "--kind", "claude", "--model", "sonnet", "--team", "mo-ws1");

        Assert.NotEqual(0, code);
        Assert.Contains("new-window", _runner.Calls.SelectMany(a => a), StringComparer.Ordinal);
        Assert.Equal(JsonText.Parse(before).Member("members")!.Value.AsArray().Count, JsonText.Parse(StoredTeam(Path.Combine(Hierarchy, "teams", "mo-ws1.json"))).Member("members")!.Value.AsArray().Count);
        Assert.DoesNotContain("spawned_at", StoredTeam(Path.Combine(Hierarchy, "teams", "mo-ws1.json")), StringComparison.Ordinal);
        _ = stdout + stderr;
    }

    [Fact]
    public void NegativeASpawnWhoseRowWasReplacedDuringItsLaunchFailsWithSpawnRowReplacedAndWritesNothing()
    {
        var (code, output) = SpawnReviewerWhileTheLaneFills(rowsAtLaunch: 0, rowsBeforeTheWrite: 1);

        Assert.NotEqual(0, code);
        Assert.Contains("spawn-row-replaced", output, StringComparison.Ordinal);
        var rows = JsonText.Parse(StoredTeam(Path.Combine(Hierarchy, "teams", "mo-ws1.json"))).Member("members")!.Value.AsArray();
        Assert.Single(rows);
        Assert.False(rows[0].AsObject().ContainsKey("transport_id"));
    }

    [Theory]
    [InlineData("custom", true)]
    [InlineData("claude", false)]
    public void ASubOrchestratorMayNotSpawnACustomKindMember(string kind, bool refused)
    {
        LaneManager("origin/train");
        LaneChild(0);
        SubOrchestrator();

        var (_, stdout, stderr) = Run("spawn-ad-hoc", "reviewer", "--kind", kind, "--harness", "mytool", "--team", "mo-ws1", "--dry-run");

        Assert.Equal(refused, (stdout + stderr).Contains("lane-custom-kind", StringComparison.Ordinal));
    }

    [Fact]
    public void ASubOrchestratorCannotCreateASecondChildTeamForItsStream()
    {
        Manager();
        Child("mo-ws1", "mo", "ws1", Self);
        SubOrchestrator();

        var (code, stdout, stderr) = Run("create", "--parent", "mo", "--stream", "ws1", "--team", "mo-ws1-again");

        Assert.NotEqual(0, code);
        Assert.Contains("not-child-team", stdout + stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void ALaneWhoseStreamRecordsNoBaseRefusesStreamOpen()
    {
        LaneManager(null);
        LaneChild(0);
        SubOrchestrator();

        var (_, stdout, _) = Run("stream-open", "ws1-a", "--team", "mo-ws1", "--dry-run");

        Assert.Equal("lane-base-missing", RefusedCode(stdout));
    }

    [Fact]
    public void TheOwnerOfAChildTeamIsNotHeldToTheLaneRules()
    {
        TeamOwnedBy(Self);

        var (_, stdout, _) = Run("stream-open", "other-x", "--team", "tm", "--base", "origin/main", "--dry-run");

        Assert.False(RefusedCode(stdout)?.StartsWith("lane-", StringComparison.Ordinal) ?? false);
    }

    [Fact]
    public void AMoveOnTheManagersTeamIsAScopeMissForASubOrchestrator()
    {
        Manager();
        Child("mo-ws1", "mo", "ws1", Self);
        SubOrchestrator();

        var (code, stdout, _) = Run("move", "--team", "mo", "--stream", "x");

        Assert.Equal(2, code);
        Assert.Equal(("not-child-team", "member-marker"), Refusal(stdout));
    }

    [Fact]
    public void ACreateWithoutParentKeepsTodaysMarkerRefusal()
    {
        Manager();
        SubOrchestrator();

        var (code, stdout, _) = Run("create", "--stream", "ws1");

        Assert.Equal(2, code);
        Assert.Equal(("member-cannot-create", "member-marker"), Refusal(stdout));
    }

    private void ChildWithStreams()
    {
        WriteTeam(
            "mo-ws1",
            "{\"version\":1,\"team_id\":\"t-mo-ws1\",\"transport\":\"terminal\",\"orchestrator\":{\"session_id\":null,\"pid\":" + Self + ",\"pid_start\":\"" + SelfStart + "\""
            + "},\"members\":[],\"parent\":{\"team\":\"mo\",\"stream\":\"ws1\"},\"streams\":{\"ws1\":{\"state\":\"open\"}}}");
    }

    private string ChildPlanLog => Path.Combine(Hierarchy, "progress", "mo-ws1", "ws1.plan.jsonl");

    [Fact]
    public void ASubOrchestratorRecordsAPlanEventOnItsChildAsTheChildsOrchestrator()
    {
        Manager();
        ChildWithStreams();
        SubOrchestrator();

        var (code, stdout, stderr) = Run("progress", "item", "add", "ws1", "a", "--title", "Do a", "--team", "mo-ws1");

        Assert.True(code == 0, stderr);
        Assert.Equal("progress: ws1/a → not_started\n", stdout);
        Assert.Contains("orchestrator@mo-ws1", ReadLog(ChildPlanLog), StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeASubOrchestratorsPlanEventForAStreamTheChildDoesNotListIsRefusedAndNothingIsWritten()
    {
        Manager();
        ChildWithStreams();
        SubOrchestrator();

        var (code, _, stderr) = Run("progress", "item", "add", "ws2", "a", "--title", "Do a", "--team", "mo-ws1");

        Assert.Equal(2, code);
        Assert.Contains("stream ws2 is not one of this team's streams (ws1)", stderr, StringComparison.Ordinal);
        Assert.False(LogExists(ChildPlanLog));
    }

    [Fact]
    public void NegativeAProgressWriteWhoseMarkerNamesTheSubOrchestratorButWhoseWalkReachesAnotherMemberIsRefused()
    {
        Manager();
        ChildWithStreams();
        SubOrchestrator();
        _nearest = new NearestSession(NearestKind.Member, "mo-ws1", "mo-ws1-implementor", Environment.ProcessId);

        var (code, stdout, _) = Run("progress", "item", "add", "ws1", "a", "--title", "Do a", "--team", "mo-ws1");

        Assert.Equal(2, code);
        Assert.Equal(("member-cannot-progress", "member-marker"), Refusal(stdout));
        Assert.False(LogExists(ChildPlanLog));
    }

    [Fact]
    public void NegativeAProgressWriteByAMemberWhoseRowIsNotTheOrchestrateClassKeepsTodaysMarkerRefusal()
    {
        Manager(soRole: "implementor");
        ChildWithStreams();
        SubOrchestrator();

        var (code, stdout, _) = Run("progress", "item", "add", "ws1", "a", "--title", "Do a", "--team", "mo-ws1");

        Assert.Equal(2, code);
        Assert.Equal(("member-cannot-progress", "member-marker"), Refusal(stdout));
        Assert.False(LogExists(ChildPlanLog));
    }

    [Fact]
    public void AMemberWhoseRowIsNotTheOrchestrateClassIsRefusedEvenWithTheSubOrchestratorsName()
    {
        Manager(soRole: "implementor");
        Child("mo-ws1", "mo", "ws1", Self);
        SubOrchestrator();

        var (code, stdout, _) = Run("spawn-one", "--team", "mo-ws1");

        Assert.Equal(2, code);
        Assert.Equal(("member-cannot-spawn-one", "member-marker"), Refusal(stdout));
    }

    [Fact]
    public void AShellWhoseNearestSessionIsAnotherMemberIsRefused()
    {
        Manager();
        Child("mo-ws1", "mo", "ws1", Self);
        SubOrchestrator();
        _nearest = new NearestSession(NearestKind.Member, "mo-ws1", "mo-ws1-implementor", Environment.ProcessId);

        var (code, stdout, _) = Run("spawn-one", "--team", "mo-ws1");

        Assert.Equal(2, code);
        Assert.Equal(("member-cannot-spawn-one", "member-marker"), Refusal(stdout));
    }

    [Fact]
    public void ARegistrationWithoutAVerifiableStartIsRefused()
    {
        Manager();
        Child("mo-ws1", "mo", "ws1", Self);
        SubOrchestrator(legacy: true);

        var (code, stdout, _) = Run("spawn-one", "--team", "mo-ws1");

        Assert.Equal(2, code);
        Assert.Equal(("member-cannot-spawn-one", "member-marker"), Refusal(stdout));
    }

    [Fact]
    public void AMarkerThatNamesAnotherMemberThanTheRegistrationIsRefusedAsDisagreeing()
    {
        Manager();
        Child("mo-ws1", "mo", "ws1", Self);
        SubOrchestrator();
        _env[MeshModule.MemberMarkerEnv] = "mo-implementor";

        var (code, stdout, stderr) = Run("spawn-one", "--team", "mo-ws1");

        Assert.Equal(2, code);
        Assert.Equal(("member-marker-disagrees", "member-marker"), Refusal(stdout));
        Assert.Contains("the member marker disagrees with the registered session", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void ATeamFileThatNamesAnotherTeamThanTheRegistrationIsRefusedAsDisagreeing()
    {
        Manager();
        Child("mo-ws1", "mo", "ws1", Self);
        SubOrchestrator();
        _env["WRANGL_TEAM_FILE"] = Path.Combine(Hierarchy, "teams", "mo-ws1.json");

        var (code, stdout, stderr) = Run("spawn-one", "--team", "mo-ws1");

        Assert.Equal(2, code);
        Assert.Equal(("member-marker-disagrees", "member-marker"), Refusal(stdout));
        Assert.Contains("the member marker disagrees with the registered session; nothing was changed.", stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(NearestKind.None, "no-orchestrator")]
    [InlineData(NearestKind.Unavailable, "unproven")]
    public void AMarkerNamingAnOrchestrateMemberIsUnprovenWhenNoWalkResultBacksIt(NearestKind kind, string proof)
    {
        Manager();
        Child("mo-ws1", "mo", "ws1", Self);
        SubOrchestrator();
        _nearest = new NearestSession(kind);

        var (code, stdout, _) = Run("spawn-one", "--team", "mo-ws1");

        Assert.Equal(2, code);
        Assert.Equal(("owner-unproven", proof), Refusal(stdout));
    }

    [Fact]
    public void AMarkerNamingAnOrdinaryMemberKeepsTodaysRefusalWhenNoWalkResultBacksIt()
    {
        Manager();
        _env[MeshModule.MemberMarkerEnv] = "mo-implementor";
        _env["WRANGL_TEAM_FILE"] = ManagerFile;
        _nearest = new NearestSession(NearestKind.None);

        var (code, stdout, _) = Run("spawn-one", "--team", "mo");

        Assert.Equal(2, code);
        Assert.Equal(("member-cannot-spawn-one", "member-marker"), Refusal(stdout));
    }

    [Fact]
    public void ARunWithNoOwnerProofServiceNeverTrustsTheMarker()
    {
        Manager();
        Child("mo-ws1", "mo", "ws1", Self);
        SubOrchestrator();

        var (code, stdout, _) = Roster(false, "spawn-one", "--team", "mo-ws1");

        Assert.Equal(2, code);
        Assert.Equal(("owner-unproven", "unproven"), Refusal(stdout));
    }
    [Fact]
    public void AnUntrackOfAMemberWhoseProcessIsGoneIsAllowed()
    {
        Manager();
        WriteTeam(
            "mo-ws1",
            "{\"version\":1,\"team_id\":\"t-mo-ws1\",\"transport\":\"terminal\",\"orchestrator\":{\"session_id\":null,\"pid\":" + Self + ",\"pid_start\":\"" + SelfStart + "\""
            + "},\"members\":[{\"name\":\"mo-ws1-implementor\",\"role\":\"implementor\",\"route\":\"peer\",\"transport_id\":\"%7\"}],"
            + "\"parent\":{\"team\":\"mo\",\"stream\":\"ws1\"}}");
        SubOrchestrator();
        using var gone = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("true") { UseShellExecute = false })!;
        gone.WaitForExit();
        Directory.CreateDirectory(Hierarchy);
        _sandbox.PlantPeers(
            Hierarchy,
            $"{{\"type\":\"peer\",\"status\":\"down\",\"name\":\"mo-ws1-implementor\",\"role\":\"implementor\",\"session_id\":\"gone\",\"pid\":{gone.Id},\"pid_start\":\"2026-01-01T00:00:00.000Z\",\"team\":\"mo-ws1\",\"ts\":\"2026-01-01T00:00:00.000Z\"}}\n");

        var (code, _, stderr) = Run("untrack", "mo-ws1-implementor", "--team", "mo-ws1", "--commit");

        Assert.True(code == 0, stderr);
    }
}
