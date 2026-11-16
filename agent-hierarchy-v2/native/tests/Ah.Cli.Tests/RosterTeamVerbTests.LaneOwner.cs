using System.Globalization;

namespace Ah.Cli.Tests;

/// <summary>
/// A lane child's rules hold whichever way its owner was proved. The caller is an unmarked shell whose process is, as each case says,
/// the registered lane lead that owns the child, the owner of the manager, or neither.
/// </summary>
public sealed partial class RosterTeamVerbTests
{
    private const int DeadOwnerPid = 2147483000;

    private void ArrangeLane(
        bool valid = true,
        bool registered = true,
        bool ownsManager = false,
        bool childOrphaned = false,
        int? cap = null,
        bool managerExists = true,
        bool withMember = true,
        string? marker = null)
    {
        var p = ParentPid();
        var start = StartOfPid(p);
        var mo = ownsManager ? p : LivePidOfAnotherProcess();
        var moStart = ownsManager ? start : StartOfPid(mo);
        var lane = cap is { } n ? ",\"lane\":{\"max_members\":" + n.ToString(CultureInfo.InvariantCulture) + ",\"max_streams\":8}" : string.Empty;
        if (managerExists)
        {
            WriteTeam(
                "mo",
                "{\"version\":1,\"team_id\":\"t-mo\",\"transport\":\"terminal\",\"orchestrator\":" + OwnerJson(mo, moStart, "sess-mo") + ",\"members\":["
                + "{\"name\":\"" + WalkSo + "\",\"role\":\"sub-orchestrator\",\"route\":\"peer\"},{\"name\":\"mo-so-ws2\",\"role\":\"sub-orchestrator\",\"route\":\"peer\"}],"
                + "\"streams\":{\"ws1\":{\"state\":\"open\",\"sub_orchestrator\":\"" + WalkSo + "\"" + lane + "},\"ws2\":{\"state\":\"open\",\"sub_orchestrator\":\"mo-so-ws2\"}}}");
        }

        var owner = childOrphaned ? OwnerJson(DeadOwnerPid, "gone", "sess-gone") : OwnerJson(p, start, "sess-so");
        var link = "\"parent\":{\"team\":\"mo\",\"stream\":\"ws1\"" + (valid ? ",\"team_id\":\"t-mo\"" : string.Empty) + "},";
        var child = LiveTeamJson("tmux", withMember ? OneImplementor : string.Empty, p.ToString(CultureInfo.InvariantCulture))
            .Replace("\"orchestrator\":{\"session_id\":null,\"pid\":" + p.ToString(CultureInfo.InvariantCulture) + "}", "\"orchestrator\":" + owner, StringComparison.Ordinal)
            .Replace("\"version\":1,", "\"version\":1," + link, StringComparison.Ordinal);
        child = child.TrimEnd('}') + ",\"workstream\":{\"state\":\"working\"},\"streams\":{\"ws1\":{\"state\":\"open\",\"worktree\":null}}}";
        WriteTeam(ChildName, child);
        if (registered)
        {
            WalkRegister(p, start, "sess-so", "orchestrator@mo-ws1", WalkSo + "@mo");
        }
        else
        {
            WalkRegister(p, start, "sess-so", "orchestrator@mo-ws1");
        }

        ScriptGit();
        B1Env(marker);
    }

    private (int Code, string Out) LaneRun(params string[] args)
    {
        var (code, stdout, stderr) = Roster(args);
        return (code, stdout + stderr);
    }

    private static readonly string[] LaneSpawnReviewer = ["spawn-ad-hoc", "reviewer", "--kind", "claude", "--model", "sonnet"];

    [Fact]
    public void LaneLeadRow1AnUnmarkedRegisteredLeadAtTheCapIsRefusedLaneMembersFull()
    {
        ArrangeLane(cap: 1);

        var (code, text) = LaneRun([.. LaneSpawnReviewer, "--team", ChildName]);

        Assert.NotEqual(0, code);
        Assert.Contains("lane-members-full", text, StringComparison.Ordinal);
    }

    [Fact]
    public void LaneLeadRow2BelowTheCapTheSameSpawnIsNotRefusedByTheLane()
    {
        ArrangeLane(cap: 3);

        var (_, text) = LaneRun([.. LaneSpawnReviewer, "--team", ChildName, "--dry-run"]);

        Assert.DoesNotContain("lane-members-full", text, StringComparison.Ordinal);
        Assert.DoesNotContain("lane-owner-unproven", text, StringComparison.Ordinal);
        Assert.DoesNotContain("lane-lead-scope", text, StringComparison.Ordinal);
    }

    [Fact]
    public void LaneLeadRow3ASpawnWithNoTeamWhoseOwnedTeamIsTheChildAsksForTheChildTeam()
    {
        ArrangeLane(cap: 3);

        var (code, text) = LaneRun(LaneSpawnReviewer);

        Assert.NotEqual(0, code);
        Assert.Contains("child-team-unnamed", text, StringComparison.Ordinal);
    }

    [Fact]
    public void LaneLeadRow4AnUltraAdvisorIsNotForLanes()
    {
        ArrangeLane(cap: 3);

        var (code, text) = LaneRun("spawn-ad-hoc", "ultra-advisor", "--kind", "claude", "--model", "opus", "--team", ChildName);

        Assert.NotEqual(0, code);
        Assert.Contains("role-not-for-lanes", text, StringComparison.Ordinal);
    }

    [Fact]
    public void LaneLeadRow5TheCustomKindIsRefused()
    {
        ArrangeLane(cap: 3);

        var (code, text) = LaneRun("spawn-ad-hoc", "reviewer", "--kind", "custom", "--harness", "mytool", "--stream", "ws1", "--team", ChildName);

        Assert.NotEqual(0, code);
        Assert.Contains("custom", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("lane-caps")]
    [InlineData("transfer")]
    [InlineData("checkpoint")]
    public void LaneLeadRow6AVerbThatIsNotCarvedIsLaneLeadScope(string verb)
    {
        ArrangeLane(cap: 3);

        var (code, text) = LaneRun(verb, "--team", ChildName);

        Assert.NotEqual(0, code);
        Assert.Contains("lane-lead-scope", text, StringComparison.Ordinal);
        Assert.Contains("its lead may run only", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("disband")]
    [InlineData("untrack")]
    public void LaneLeadRow6bTheCarvedDisbandAndUntrackOfItsOwnChildAreNotRefusedByTheLane(string verb)
    {
        ArrangeLane(cap: 3);

        var (_, text) = LaneRun(verb, "--team", ChildName);

        Assert.DoesNotContain("lane-lead-scope", text, StringComparison.Ordinal);
        Assert.DoesNotContain("lane-owner-unproven", text, StringComparison.Ordinal);
    }

    [Fact]
    public void LaneLeadRow7MoveReplaceLiveIsRefused()
    {
        ArrangeLane(cap: 3);

        var (code, text) = LaneRun("move", Impl, "--stream", "ws1", "--team", ChildName, "--replace-live");

        Assert.NotEqual(0, code);
        Assert.Contains("lane-replace-live", text, StringComparison.Ordinal);
    }

    [Fact]
    public void LaneLeadRow8AnOwnerWithNoLeadRegistrationIsRefusedAndToldToRespawnTheLead()
    {
        ArrangeLane(registered: false, cap: 3);

        var (code, text) = LaneRun([.. LaneSpawnReviewer, "--team", ChildName]);

        Assert.NotEqual(0, code);
        Assert.Contains("lane-owner-unproven", text, StringComparison.Ordinal);
        Assert.Contains("dismiss and respawn " + WalkSo + "@mo", text, StringComparison.Ordinal);
    }

    [Fact]
    public void LaneLeadRow9TheOwnerOfATeamWithNoParentOnItsOwnTeamIsUnchanged()
    {
        WriteTeam("proj", SoTeam(OpenStreamJson("s1")));

        var (_, text) = LaneRun("spawn-one", "reviewer", "--dry-run");

        Assert.DoesNotContain("lane-owner-unproven", text, StringComparison.Ordinal);
        Assert.DoesNotContain("lane-lead-scope", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData(WalkSo)]
    public void LaneLeadRow10TheMarkedLeadGivesTheSameOutcomeAsTheUnmarkedOne(string marker)
    {
        ArrangeLane(cap: 1, marker: marker);

        var (atCap, atCapText) = LaneRun([.. LaneSpawnReviewer, "--team", ChildName]);
        var (scope, scopeText) = LaneRun("lane-caps", "--team", ChildName);

        Assert.NotEqual(0, atCap);
        Assert.Contains("lane-members-full", atCapText, StringComparison.Ordinal);
        Assert.NotEqual(0, scope);
        Assert.Contains("ah roster lane-caps:", scopeText, StringComparison.Ordinal);
    }

    [Fact]
    public void LaneLeadRow11ACallerThatOwnsTheChildAndTheManagerIsTheManagersOwner()
    {
        ArrangeLane(ownsManager: true, cap: 3);

        var (_, spawn) = LaneRun([.. LaneSpawnReviewer, "--team", ChildName, "--dry-run"]);
        var (laneCode, laneCaps) = LaneRun("lane-caps", "--team", ChildName);

        Assert.DoesNotContain("lane-owner-unproven", spawn, StringComparison.Ordinal);
        Assert.NotEqual(0, laneCode);
        Assert.Contains("not-team-owner", laneCaps, StringComparison.Ordinal);
        Assert.Contains("its parent's owner may run only", laneCaps, StringComparison.Ordinal);
    }

    [Fact]
    public void LaneLeadRow12ANonSpawnVerbWithNoTeamIsJudgedAsALaneChildWhoseTeamFileIsTheResolvedOne()
    {
        ArrangeLane(cap: 3);

        var (code, text) = LaneRun("untrack", Impl, "--commit");

        Assert.NotEqual(0, code);
        Assert.Contains("child-team-unnamed", text, StringComparison.Ordinal);
        Assert.True(_sandbox.TeamExists(TeamPath(ChildName)));
    }

    [Fact]
    public void LaneLeadRow12bAnUntrackThatNamesTheChildTeamIsAllowedAndKeepsTheFile()
    {
        ArrangeLane(cap: 3);

        var (code, text) = LaneRun("untrack", Impl, "--team", ChildName, "--commit");

        Assert.Equal(0, code);
        Assert.DoesNotContain("child-team-unnamed", text, StringComparison.Ordinal);
        Assert.DoesNotContain("lane-lead-scope", text, StringComparison.Ordinal);
        Assert.DoesNotContain("lane-owner-unproven", text, StringComparison.Ordinal);
        using var doc = System.Text.Json.JsonDocument.Parse(_sandbox.TeamText(TeamPath(ChildName)));
        Assert.Equal(0, doc.RootElement.GetProperty("members").GetArrayLength());
        Assert.True(doc.RootElement.TryGetProperty("parent", out _));
        Assert.True(doc.RootElement.TryGetProperty("orchestrator", out _));
    }

    [Fact]
    public void LaneLeadRow13AStaleLinkChildIsRefusedForSpawnAndStillTornDown()
    {
        ArrangeLane(valid: false, cap: 3);

        var (spawnCode, spawn) = LaneRun([.. LaneSpawnReviewer, "--team", ChildName]);
        var (_, disband) = LaneRun("disband", "--team", ChildName);

        Assert.NotEqual(0, spawnCode);
        Assert.Contains("lane-owner-unproven", spawn, StringComparison.Ordinal);
        Assert.Contains("no longer exists", spawn, StringComparison.Ordinal);
        Assert.DoesNotContain("lane-owner-unproven", disband, StringComparison.Ordinal);
    }

    [Fact]
    public void LaneLeadRow13bTheOrphanedChildOfAGoneManagerIsDisbandedByAnyoneAndReaped()
    {
        ArrangeLane(valid: false, childOrphaned: true, managerExists: false);

        var (disbandCode, disband) = LaneRun("disband", "--team", ChildName);
        var (reapCode, reap) = LaneRun("reap");

        Assert.True(disbandCode == 0, disband);
        Assert.True(reapCode == 0, reap);
        Assert.DoesNotContain("lane-child:", reap, StringComparison.Ordinal);
    }

    [Fact]
    public void ManagerOwnerAdoptOfAValidLaneChildGetsTheLaneText()
    {
        ArrangeLane(ownsManager: true, childOrphaned: true);

        var (code, text) = LaneRun("adopt", "--team", ChildName);

        Assert.NotEqual(0, code);
        Assert.Contains("its parent's owner may run only", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnotherTeamsOwnerAdoptOfAValidLaneChildGetsTheLaneText()
    {
        ArrangeLane(childOrphaned: true);

        var (code, text) = LaneRun("adopt", "--team", ChildName);

        Assert.NotEqual(0, code);
        Assert.Contains("its parent's owner may run only", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ManagerOwnerDisbandOfALiveValidChildGetsTheLaneTextAndOfAnOrphanedOneIsAllowed()
    {
        ArrangeLane(ownsManager: true);
        var (liveCode, live) = LaneRun("disband", "--team", ChildName);

        ArrangeLane(ownsManager: true, childOrphaned: true);
        var (orphanCode, orphan) = LaneRun("disband", "--team", ChildName);

        Assert.NotEqual(0, liveCode);
        Assert.Contains("its parent's owner may run only", live, StringComparison.Ordinal);
        Assert.True(orphanCode == 0, orphan);
    }

    [Fact]
    public void AnotherTeamsOwnerDisbandOfAnOrphanedValidChildGetsTheLaneText()
    {
        ArrangeLane(childOrphaned: true);

        var (code, text) = LaneRun("disband", "--team", ChildName);

        Assert.NotEqual(0, code);
        Assert.Contains("its parent's owner may run only", text, StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeAnyonesDisbandOfAnOrphanedTeamThatIsNotALaneChildIsAllowed()
    {
        WriteTeam(
            "plain",
            "{\"version\":1,\"team_id\":\"t-plain\",\"transport\":\"terminal\",\"orchestrator\":" + OwnerJson(DeadOwnerPid, "gone", "sess-gone") + ",\"members\":[]}");
        B1Env(null);

        var (code, text) = LaneRun("disband", "--team", "plain");

        Assert.True(code == 0, text);
    }

    [Fact]
    public void CreateWithAParentIsRefusedToASessionThatDoesNotOwnTheParent()
    {
        ArrangeLane(cap: 3);

        var (code, text) = LaneRun("create", "--parent", "mo", "--stream", "ws2", "--team", "squat-ws2");

        Assert.NotEqual(0, code);
        Assert.Contains("only the owner of mo may create its lane teams", text, StringComparison.Ordinal);
        Assert.False(_sandbox.TeamExists(TeamPath("squat-ws2")));
    }

    [Fact]
    public void NegativeCreateWithAParentByTheParentsOwnerIsNotRefusedAsNotTheOwner()
    {
        ArrangeLane(ownsManager: true, cap: 3);

        var (_, text) = LaneRun("create", "--parent", "mo", "--stream", "ws2", "--team", "mo-ws2");

        Assert.DoesNotContain("may create its lane teams", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ReapSkipsAnOrphanedValidLaneChildForANonOwnerAndReapsItForTheManagersOwner()
    {
        ArrangeLane(childOrphaned: true);
        var (_, skippedRun) = LaneRun("reap", "--commit");
        var skipped = _sandbox.TeamExists(TeamPath(ChildName));

        ArrangeLane(ownsManager: true, childOrphaned: true);
        var (_, ownerRun) = LaneRun("reap", "--commit");

        Assert.True(skipped, skippedRun);
        Assert.Contains("lane-child: disband from mo owner", skippedRun, StringComparison.Ordinal);
        Assert.False(_sandbox.TeamExists(TeamPath(ChildName)), ownerRun);
    }

    [Fact]
    public void ReapReapsAnOrphanedStaleLinkChild()
    {
        ArrangeLane(valid: false, childOrphaned: true);

        var (_, text) = LaneRun("reap", "--commit");

        Assert.False(_sandbox.TeamExists(TeamPath(ChildName)), text);
        Assert.DoesNotContain("lane-child:", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TeamsListsALaneChildsParentLinkAndNullForATeamWithNone()
    {
        ArrangeLane(cap: 3);

        var (code, text) = LaneRun("teams");

        Assert.Equal(0, code);
        using var doc = System.Text.Json.JsonDocument.Parse(text);
        var rows = doc.RootElement.GetProperty("teams").EnumerateArray().ToDictionary(r => r.GetProperty("name").GetString()!);
        var parent = rows[ChildName].GetProperty("parent");
        Assert.Equal("mo", parent.GetProperty("team").GetString());
        Assert.Equal("ws1", parent.GetProperty("stream").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, rows["mo"].GetProperty("parent").ValueKind);
    }
}
