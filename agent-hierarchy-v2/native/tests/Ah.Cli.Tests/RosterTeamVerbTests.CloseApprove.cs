using Ah.Core.Json;
using Ah.Mesh;

namespace Ah.Cli.Tests;

/// <summary>The managing orchestrator's close-approve and the green light a sub-orchestrator's close needs.</summary>
public sealed partial class RosterTeamVerbTests
{
    private const string SoName = "mo-so-ws1";

    private const string ChildName = "mo-ws1";

    private string ManagerPath => TeamPath("mo");

    private void ArrangeManaged(string? grants = null, string members = "", string moPid = "", string? childPid = null)
    {
        var pid = moPid.Length > 0 ? moPid : Self;
        var streams = "{\"ws1\":{\"state\":\"open\",\"sub_orchestrator\":\"" + SoName + "\""
            + (grants is null ? string.Empty : ",\"close_grants\":" + grants) + "}}";
        WriteTeam(
            "mo",
            "{\"version\":1,\"team_id\":\"t-mo\",\"transport\":\"terminal\",\"orchestrator\":{\"session_id\":\"sess-mo\",\"pid\":" + pid
            + ",\"pid_start\":\"" + SelfStart + "\"},\"members\":[{\"name\":\"" + SoName + "\",\"role\":\"sub-orchestrator\",\"route\":\"peer\"}],\"streams\":" + streams + "}");
        WriteTeam(ChildName, LiveTeamJson("tmux", members.Length > 0 ? members : OneImplementor, childPid).Replace("\"version\":1,", "\"version\":1,\"parent\":{\"team\":\"mo\",\"stream\":\"ws1\"},", StringComparison.Ordinal));
        ScriptGit();
    }

    private void AsSubOrchestrator()
    {
        _extraEnv[MeshModule.MemberMarkerEnv] = SoName;
        _extraEnv["WRANGL_TEAM_FILE"] = ManagerPath;
        _nearest = new NearestSession(NearestKind.Member, "mo", SoName, Environment.ProcessId, false);
    }

    private string PlanToken(string verb = "disband", params string[] extra)
    {
        var (code, stdout, stderr) = Roster(["close-approve", "--team", ChildName, "--verb", verb, .. extra]);
        Assert.True(code == 0, stderr);
        return TokenOf(stdout);
    }

    private static string Grant(string token, string verb = "disband", string expires = "2999-01-01T00:00:00.000Z", string session = "sess-mo") =>
        "{\"verb\":\"" + verb + "\",\"team\":\"" + ChildName + "\",\"plan_token\":\"" + token + "\",\"targets\":[],\"granted_at\":\"2026-10-16T01:00:00.000-04:00\",\"expires_at\":\""
        + expires + "\",\"by\":{\"session_id\":\"" + session + "\",\"pid\":" + Self + ",\"pid_start\":\"" + SelfStart + "\"}}";

    private (int Code, string Stdout, string Stderr) SoDisband(string token) =>
        Roster("disband", "--close", "--confirm", "--plan-token", token, "--team", ChildName);

    [Fact]
    public void CloseApproveWithoutATokenPrintsThePlanAndWritesNothing()
    {
        ArrangeManaged();
        var before = _sandbox.TeamText(ManagerPath);

        var (code, stdout, stderr) = Roster("close-approve", "--team", ChildName, "--verb", "disband");

        Assert.True(code == 0, stderr);
        var plan = JsonText.Parse(stdout);
        Assert.False(plan.Member("approve")!.Value.AsBool());
        Assert.Equal(Impl, plan.Member("targets")!.Value.AsArray().Single().AsString());
        Assert.Equal(before, _sandbox.TeamText(ManagerPath));
    }

    [Fact]
    public void CloseApproveWithTheCurrentTokenRecordsAGrantBoundToTheManagingOrchestrator()
    {
        ArrangeManaged();
        var token = PlanToken();

        var (code, stdout, stderr) = Roster("close-approve", "--team", ChildName, "--verb", "disband", "--plan-token", token);

        Assert.True(code == 0, stderr);
        Assert.True(JsonText.Parse(stdout).Member("approve")!.Value.AsBool());
        var grant = JsonText.Parse(_sandbox.TeamText(ManagerPath)).Member("streams")!.Value.Member("ws1")!.Value.Member("close_grants")!.Value.AsArray().Single();
        Assert.Equal(token, grant.Member("plan_token")!.Value.AsString());
        Assert.Equal("disband", grant.Member("verb")!.Value.AsString());
        Assert.Equal(ChildName, grant.Member("team")!.Value.AsString());
        Assert.Equal("sess-mo", grant.Member("by")!.Value.Member("session_id")!.Value.AsString());
        Assert.Equal(SelfStart, grant.Member("by")!.Value.Member("pid_start")!.Value.AsString());
    }

    [Fact]
    public void CloseApproveWithAStaleTokenWritesNothing()
    {
        ArrangeManaged();
        var before = _sandbox.TeamText(ManagerPath);

        var (code, _, stderr) = Roster("close-approve", "--team", ChildName, "--verb", "disband", "--plan-token", "0000000000000000");

        Assert.NotEqual(0, code);
        Assert.Contains("close plan changed", stderr, StringComparison.Ordinal);
        Assert.Equal(before, _sandbox.TeamText(ManagerPath));
    }

    [Fact]
    public void CloseApproveRefusesAnEmptyCloseSetEvenWithTheTokenItWouldHave()
    {
        ArrangeManaged(members: " ");
        WriteTeam(ChildName, LiveTeamJson("tmux", string.Empty).Replace("\"version\":1,", "\"version\":1,\"parent\":{\"team\":\"mo\",\"stream\":\"ws1\"},", StringComparison.Ordinal));
        var before = _sandbox.TeamText(ManagerPath);
        var token = JsonText.Parse(Roster("close-approve", "--team", ChildName, "--verb", "disband").Stdout).Member("close_token")!.Value.AsString();

        var (code, _, stderr) = Roster("close-approve", "--team", ChildName, "--verb", "disband", "--plan-token", token);

        Assert.NotEqual(0, code);
        Assert.Contains("nothing to close", stderr, StringComparison.Ordinal);
        Assert.Equal(before, _sandbox.TeamText(ManagerPath));
    }

    [Fact]
    public void CloseApproveKeepsTheNewestTwentyGrants()
    {
        var old = string.Join(',', Enumerable.Range(0, 20).Select(i => Grant("old" + i.ToString("D13", System.Globalization.CultureInfo.InvariantCulture))));
        ArrangeManaged("[" + old + "]");
        var token = PlanToken();

        Assert.Equal(0, Roster("close-approve", "--team", ChildName, "--verb", "disband", "--plan-token", token).Code);

        var grants = JsonText.Parse(_sandbox.TeamText(ManagerPath)).Member("streams")!.Value.Member("ws1")!.Value.Member("close_grants")!.Value.AsArray();
        Assert.Equal(20, grants.Count);
        Assert.Equal(token, grants[^1].Member("plan_token")!.Value.AsString());
        Assert.DoesNotContain(grants, g => g.Member("plan_token")!.Value.AsString() == "old" + 0.ToString("D13", System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void CloseApproveByAnotherSessionOrBySubOrchestratorWritesNothing()
    {
        ArrangeManaged(moPid: LivePidOfAnotherProcess().ToString(System.Globalization.CultureInfo.InvariantCulture));
        var before = _sandbox.TeamText(ManagerPath);

        var (code, stdout, _) = Roster("close-approve", "--team", ChildName, "--verb", "disband");
        Assert.Equal(2, code);
        Assert.Contains("lane-owner-unproven", stdout, StringComparison.Ordinal);

        ArrangeManaged();
        AsSubOrchestrator();
        var (soCode, soOut, _) = Roster("close-approve", "--team", ChildName, "--verb", "disband");
        Assert.Equal(2, soCode);
        Assert.Contains("member-marker", soOut, StringComparison.Ordinal);
        Assert.DoesNotContain("close_grants", _sandbox.TeamText(ManagerPath), StringComparison.Ordinal);
        Assert.DoesNotContain("close_grants", before, StringComparison.Ordinal);
    }

    [Fact]
    public void CloseApproveOnATeamWithoutAParentIsRefused()
    {
        ArrangeManaged();

        var (code, _, stderr) = Roster("close-approve", "--team", "mo", "--verb", "disband");

        Assert.NotEqual(0, code);
        Assert.Contains("no parent", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void ASubOrchestratorClosesWithTheManagersGrant()
    {
        ArrangeManaged();
        var token = PlanToken();
        WriteTeam("mo", _sandbox.TeamText(ManagerPath).Replace("\"state\":\"open\",\"sub_orchestrator\":\"" + SoName + "\"", "\"state\":\"open\",\"sub_orchestrator\":\"" + SoName + "\",\"close_grants\":[" + Grant(token) + "]", StringComparison.Ordinal));
        AsSubOrchestrator();

        var (code, _, stderr) = SoDisband(token);

        Assert.True(code == 0, stderr);
    }

    [Fact]
    public void ASubOrchestratorWithoutAGrantACloseWithAnotherVerbOrTeamOrTokenIsRefused()
    {
        ArrangeManaged();
        var token = PlanToken();
        AsSubOrchestrator();
        Assert.Contains("close-no-green-light", SoDisband(token).Stdout, StringComparison.Ordinal);

        WriteTeam("mo", _sandbox.TeamText(ManagerPath).Replace("\"state\":\"open\",\"sub_orchestrator\":\"" + SoName + "\"", "\"state\":\"open\",\"sub_orchestrator\":\"" + SoName + "\",\"close_grants\":[" + Grant(token, verb: "dismiss") + "]", StringComparison.Ordinal));
        Assert.Contains("close-no-green-light", SoDisband(token).Stdout, StringComparison.Ordinal);

        WriteTeam("mo", _sandbox.TeamText(ManagerPath).Replace("\"close_grants\":[" + Grant(token, verb: "dismiss") + "]", "\"close_grants\":[" + Grant("ffffffffffffffff" + token) + "]", StringComparison.Ordinal));
        Assert.Contains("close-no-green-light", SoDisband(token).Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void AnExpiredOrForeignGrantDoesNotLetASubOrchestratorClose()
    {
        ArrangeManaged();
        var token = PlanToken();
        var open = "\"state\":\"open\",\"sub_orchestrator\":\"" + SoName + "\"";
        AsSubOrchestrator();

        WriteTeam("mo", _sandbox.TeamText(ManagerPath).Replace(open, open + ",\"close_grants\":[" + Grant(token, expires: "2020-01-01T00:00:00.000Z") + "]", StringComparison.Ordinal));
        var (_, expiredOut, expiredErr) = SoDisband(token);
        Assert.Contains("close-green-light-expired", expiredOut, StringComparison.Ordinal);
        Assert.Contains("expired", expiredErr, StringComparison.Ordinal);

        ArrangeManaged("[" + Grant(token, session: "sess-previous") + "]");
        var (_, foreignOut, _) = SoDisband(token);
        Assert.Contains("close-green-light-replaced", foreignOut, StringComparison.Ordinal);
    }

    [Fact]
    public void ASubOrchestratorCannotCloseAnEmptyChildAndCannotUseIgnoreRelease()
    {
        ArrangeManaged();
        WriteTeam(ChildName, LiveTeamJson("tmux", string.Empty).Replace("\"version\":1,", "\"version\":1,\"parent\":{\"team\":\"mo\",\"stream\":\"ws1\"},", StringComparison.Ordinal));
        AsSubOrchestrator();
        var token = JsonText.Parse(Roster("disband", "--team", ChildName).Stdout).Member("close_token")!.Value.AsString();

        Assert.Contains("close-empty-set", SoDisband(token).Stdout, StringComparison.Ordinal);

        var (_, flagOut, _) = Roster("disband", "--close", "--confirm", "--ignore-release", "--plan-token", token, "--team", ChildName);
        Assert.Contains("close-flag-refused", flagOut, StringComparison.Ordinal);
    }

    [Fact]
    public void TheManagingOrchestratorClosesTheChildWithoutAGrant()
    {
        ArrangeManaged();
        var token = PlanToken();

        var (code, _, stderr) = SoDisband(token);

        Assert.True(code == 0, stderr);
    }

    private string ChildWorkstream() => JsonText.Parse(_sandbox.TeamText(TeamPath(ChildName))).Member("workstream")?.Member("state") is { IsText: true } t ? t.AsString() : "none";

    [Fact]
    public void ASubOrchestratorReportsOnItsOwnChildWithoutNamingIt()
    {
        ArrangeManaged(moPid: LivePidOfAnotherProcess().ToString(System.Globalization.CultureInfo.InvariantCulture));
        AsSubOrchestrator();

        var (code, _, stderr) = Roster("stream-report", "--state", "working", "--summary", "going");

        Assert.True(code == 0, stderr);
        Assert.Equal("working", ChildWorkstream());
    }

    [Fact]
    public void ASubOrchestratorCannotReportOnAnotherTeam()
    {
        ArrangeManaged(moPid: LivePidOfAnotherProcess().ToString(System.Globalization.CultureInfo.InvariantCulture));
        AsSubOrchestrator();

        var (code, stdout, _) = Roster("stream-report", "--state", "working", "--summary", "going", "--team", "mo");

        Assert.Equal(2, code);
        Assert.Contains("not-child-team", stdout, StringComparison.Ordinal);
        Assert.Equal("none", ChildWorkstream());
    }

    [Fact]
    public void TheManagingOrchestratorReportsForAGoneSubOrchestratorOnly()
    {
        ArrangeManaged(childPid: LivePidOfAnotherProcess().ToString(System.Globalization.CultureInfo.InvariantCulture));
        _callers = new RecordingCallers { OwnerGone = false };
        var live = Roster("stream-report", "--state", "blocked", "--summary", "stuck", "--team", ChildName);
        Assert.NotEqual(0, live.Code);
        Assert.Equal("none", ChildWorkstream());

        _callers = new RecordingCallers { OwnerGone = true };
        var gone = Roster("stream-report", "--state", "blocked", "--summary", "stuck", "--team", ChildName);

        Assert.True(gone.Code == 0, gone.Stderr);
        Assert.Equal("blocked", ChildWorkstream());
    }
}
