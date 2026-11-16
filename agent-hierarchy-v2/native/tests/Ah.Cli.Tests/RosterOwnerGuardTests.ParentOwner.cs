using System.Globalization;
using Ah.Core.Json;
using Ah.Mesh;

namespace Ah.Cli.Tests;

/// <summary>The owner of a lane's manager team staffing the lane's child team: what the guard lets through, the lane rules, and what it refuses.</summary>
public sealed partial class RosterOwnerGuardTests
{
    private const string TodaysRefusal = "this session does not own";

    private static readonly string[] SpawnAdHocReviewer = ["spawn-ad-hoc", "reviewer", "--kind", "claude", "--model", "sonnet", "--team", "mo-ws1", "--dry-run"];

    /// <summary>An open lane whose child is led by another live process, run by the unmarked owner of its manager team.</summary>
    private void ParentOwner(string? childOwner = null, string members = "", string? laneRecord = null, string? parentTeamId = "t-mo")
    {
        OpenLane(managerOwner: Self, childOwner: childOwner ?? LivePidOfAnotherProcess().ToString(CultureInfo.InvariantCulture), members: members, laneRecord: laneRecord, parentTeamId: parentTeamId);
        _nearest = new NearestSession(NearestKind.Orchestrator, null, null, Environment.ProcessId);
    }

    private void StampCreated(string team, string iso)
    {
        var path = Path.Combine(Hierarchy, "teams", team + ".json");
        _sandbox.PlantTeam(path, _sandbox.TeamText(path).Replace("{\"version\":1,", "{\"version\":1,\"created\":\"" + iso + "\",", StringComparison.Ordinal));
    }

    [Fact]
    public void TheOwnerOfAManagerTeamSpawnsIntoItsLanesChild()
    {
        ParentOwner();

        var (code, stdout, stderr) = Run(SpawnAdHocReviewer);

        Assert.True(code == 0, stdout + stderr);
    }

    [Theory]
    [InlineData("disband")]
    [InlineData("untrack")]
    [InlineData("stream-open")]
    [InlineData("progress")]
    public void NegativeTheOwnerOfAManagerTeamRunningAnotherOwnerVerbOnTheChildIsRefusedWithTheLaneText(string verb)
    {
        ParentOwner();

        var (_, stdout, stderr) = Run(verb, "--team", "mo-ws1");

        Assert.Equal("not-team-owner", Refusal(stdout).Refused);
        Assert.Contains("may run only spawn-one, spawn-ad-hoc, dismiss and move there", stdout + stderr, StringComparison.Ordinal);
        Assert.Contains("mo-so-ws1", stdout + stderr, StringComparison.Ordinal);
        Assert.Equal("verb-not-parent-owned", ProofFailed(stdout));
    }

    private static string? ProofFailed(string stdout) =>
        stdout.StartsWith('{') && JsonText.Parse(stdout).AsObject().Get("proof_failed") is { IsText: true } failed ? failed.AsString() : null;

    [Fact]
    public void NegativeASessionOrLegacyCallerOwningTheManagerIsToldToRegisterBeforeASpawnOnTheChild()
    {
        ParentOwner();
        _nearest = new NearestSession(NearestKind.Session, null, null, Environment.ProcessId);

        var (_, stdout, stderr) = Run(SpawnAdHocReviewer);

        Assert.Equal("not-team-owner", Refusal(stdout).Refused);
        Assert.Equal("caller-not-orchestrator-registration", ProofFailed(stdout));
        Assert.Contains("ah roster checkin", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeAChildMadeForAnEarlierManagerIncarnationNamesTheBrokenLinkAndTheAdoptRemedy()
    {
        ParentOwner(parentTeamId: "t-mo-before-it-was-recreated");

        var (_, stdout, stderr) = Run(SpawnAdHocReviewer);

        Assert.Equal("child-not-valid-lane", ProofFailed(stdout));
        Assert.Contains("earlier incarnation", stderr, StringComparison.Ordinal);
        Assert.Contains("ah roster adopt --team mo-ws1", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeTheOwnerOfAnotherTeamGetsTodaysRefusalOnTheChild()
    {
        OpenLane(managerOwner: LivePidOfAnotherProcess().ToString(CultureInfo.InvariantCulture), childOwner: LivePidOfAnotherProcess().ToString(CultureInfo.InvariantCulture));
        _nearest = new NearestSession(NearestKind.Orchestrator, null, null, Environment.ProcessId);

        var (_, stdout, stderr) = Run(SpawnAdHocReviewer);

        Assert.Equal("not-team-owner", Refusal(stdout).Refused);
        Assert.Contains(TodaysRefusal, stdout + stderr, StringComparison.Ordinal);
        Assert.Equal("owns-neither", ProofFailed(stdout));
    }

    [Fact]
    public void NegativeAForgedChildWithAParentFieldButNoStreamOnTheManagerIsRefusedWithTodaysText()
    {
        ParentOwner();
        WriteTeam(
            "mo-fake",
            "{\"version\":1,\"team_id\":\"t-fake\",\"transport\":\"terminal\",\"orchestrator\":{\"session_id\":null,\"pid\":" + GonePid + GoneStartPart
            + "},\"members\":[],\"parent\":{\"team\":\"mo\",\"stream\":\"fake\",\"team_id\":\"t-mo\"}}");

        var (_, stdout, stderr) = Run("spawn-ad-hoc", "reviewer", "--kind", "claude", "--model", "sonnet", "--team", "mo-fake", "--dry-run");

        Assert.Equal("not-team-owner", Refusal(stdout).Refused);
        Assert.Contains(TodaysRefusal, stdout + stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeAChildMadeForAnEarlierIncarnationOfTheManagerIsRefusedWithTodaysText()
    {
        ParentOwner(parentTeamId: "t-mo-before-it-was-recreated");

        var (_, stdout, stderr) = Run(SpawnAdHocReviewer);

        Assert.Equal("not-team-owner", Refusal(stdout).Refused);
        Assert.Contains(TodaysRefusal, stdout + stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeAChildWithNoRecordedIdentityAndNoCreatedTimeIsRefusedWithTodaysText()
    {
        ParentOwner(parentTeamId: null);

        var (_, stdout, stderr) = Run(SpawnAdHocReviewer);

        Assert.Equal("not-team-owner", Refusal(stdout).Refused);
        Assert.Contains(TodaysRefusal, stdout + stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AChildWithNoRecordedIdentityIsAcceptedByItsCreatedTimeOnlyWhenItIsNotOlderThanTheManager(bool childLater)
    {
        ParentOwner(parentTeamId: null);
        StampCreated("mo", "2026-01-02T00:00:00Z");
        StampCreated("mo-ws1", childLater ? "2026-01-03T00:00:00Z" : "2026-01-01T00:00:00Z");

        var (_, stdout, stderr) = Run(SpawnAdHocReviewer);

        Assert.Equal(!childLater, Refusal(stdout).Refused == "not-team-owner");
        Assert.True(!childLater || Refusal(stdout).Refused is null, stdout + stderr);
    }

    [Fact]
    public void ASpawnByTheParentOwnerAtTheLanesMemberCapIsRefused()
    {
        ParentOwner(members: LaneRow("mo-ws1-m0"), laneRecord: "{\"max_members\":1,\"max_streams\":8}");

        var (code, stdout, stderr) = Run(SpawnAdHocReviewer);

        Assert.NotEqual(0, code);
        Assert.Contains("lane-members-full", stdout + stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeASpawnByTheParentOwnerBelowTheLanesMemberCapIsNotRefusedAsFull()
    {
        ParentOwner(members: LaneRow("mo-ws1-m0"), laneRecord: "{\"max_members\":2,\"max_streams\":8}");

        var (code, stdout, stderr) = Run(SpawnAdHocReviewer);

        Assert.True(code == 0, stdout + stderr);
    }

    [Theory]
    [InlineData("ultra-advisor")]
    [InlineData("sub-orchestrator")]
    public void AParentOwnerMayNotSpawnTheRolesNoLaneMayHave(string role)
    {
        ParentOwner();

        var (code, stdout, stderr) = Run("spawn-ad-hoc", role, "--kind", "claude", "--model", "opus", "--team", "mo-ws1", "--dry-run");

        Assert.NotEqual(0, code);
        Assert.True((stdout + stderr).Contains("role-not-for-lanes", StringComparison.Ordinal) || (stdout + stderr).Contains("under-specified", StringComparison.Ordinal), stdout + stderr);
    }

    [Fact]
    public void ASpawnByTheParentOwnerIntoAClosedLaneIsRefused()
    {
        OpenLane(managerStreamState: "done", managerOwner: Self, childOwner: LivePidOfAnotherProcess().ToString(CultureInfo.InvariantCulture));
        _nearest = new NearestSession(NearestKind.Orchestrator, null, null, Environment.ProcessId);

        var (code, stdout, stderr) = Run(SpawnAdHocReviewer);

        Assert.NotEqual(0, code);
        Assert.Contains("lane-closed", stdout + stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void ASpawnByTheParentOwnerUnderALeadWhoseProcessIsGoneIsRefusedAndADismissIsNot()
    {
        ParentOwner(childOwner: GonePid.ToString(CultureInfo.InvariantCulture), members: LaneRow("mo-ws1-m0"));

        var (spawn, spawnOut, spawnErr) = Run(SpawnAdHocReviewer);
        var (_, dismissOut, dismissErr) = Run("dismiss", "mo-ws1-m0", "--team", "mo-ws1", "--plan");

        Assert.NotEqual(0, spawn);
        Assert.Contains("lane-lead-gone", spawnOut + spawnErr, StringComparison.Ordinal);
        Assert.DoesNotContain("lane-lead-gone", dismissOut + dismissErr, StringComparison.Ordinal);
        Assert.DoesNotContain("not-team-owner", dismissOut + dismissErr, StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeASpawnByTheParentOwnerUnderALeadWhoseLifeCannotBeJudgedIsNotRefusedAsGone()
    {
        ParentOwner(childOwner: "999998");

        var (_, stdout, stderr) = Run(SpawnAdHocReviewer);

        Assert.DoesNotContain("lane-lead-gone", stdout + stderr, StringComparison.Ordinal);
    }
}
