using System.Globalization;
using Ah.Commands;
using Ah.Core.Json;
using Ah.Mesh;

namespace Ah.Cli.Tests;

/// <summary><c>roster recover --team</c>: the team's owner re-stamps the member rows whose process is gone from the one live session in the row's pane.</summary>
public sealed partial class RosterTeamVerbTests
{
    private const string RecoverStart = "2026-10-25T21:00:00.000Z";

    private static string MemberRow(string name, string role, double pid, string pane, string session) =>
        $"{{\"name\":\"{name}\",\"role\":\"{role}\",\"transport_id\":\"{pane}\",\"pid\":{pid.ToString(CultureInfo.InvariantCulture)},\"pid_start\":\"2026-01-01T00:00:00.000Z\",\"session_id\":\"{session}\"}}";

    private void RecoverTeam(double ownerPid, params string[] rows) =>
        WriteTeam("proj", $"{{\"version\":1,\"team_id\":\"t-1\",\"transport\":\"terminal\",\"roster_level\":\"repo\",\"orchestrator\":{{\"pid\":{ownerPid.ToString(CultureInfo.InvariantCulture)},\"pid_start\":\"{SelfStart}\"}},\"members\":[{string.Join(',', rows)}]}}");

    private JsonValue RowOf(string name) =>
        JsonText.Parse(_sandbox.TeamText(TeamPath("proj"))).Member("members")!.Value.AsArray().Single(r => r.Member("name")!.Value.AsString() == name);

    private (int Code, string Stdout, string Stderr) Recover() => Roster("recover", "--team", "proj", "--orchestrator-pid", Self);

    [Fact]
    public void RecoverReStampsARowWhoseProcessIsGoneFromTheLiveSessionInItsPane()
    {
        RecoverTeam(Environment.ProcessId, MemberRow("imp", "implementor", GonePid(), "p1", "old-session"));
        var live = LivePidOfAnotherProcess();
        _liveSessions = [new LiveSession("new-session", live, RecoverStart, "p1", [])];

        var (code, stdout, stderr) = Recover();

        Assert.True(code == 0, stderr);
        Assert.Contains("re-stamped 1, down 0, untouched 0", stdout, StringComparison.Ordinal);
        var row = RowOf("imp");
        Assert.Equal(live, (int)row.Member("pid")!.Value.AsNumber());
        Assert.Equal(RecoverStart, row.Member("pid_start")!.Value.AsString());
        Assert.Equal("new-session", row.Member("session_id")!.Value.AsString());
    }

    [Fact]
    public void ARowWhosePaneHoldsASessionRegisteredAsAnotherMemberIsNotReStamped()
    {
        var botPid = LivePidOfAnotherProcess();
        RecoverTeam(
            Environment.ProcessId,
            MemberRow("imp", "implementor", GonePid(), "p1", "old-session"),
            MemberRow("bot", "reviewer", botPid, "p2", "bot-session"));
        _liveSessions = [new LiveSession("bot-new", LivePidOfAnotherProcess(), RecoverStart, "p1", ["bot@proj"])];

        var (code, stdout, stderr) = Recover();

        Assert.True(code == 0, stderr);
        Assert.Contains("re-stamped 0, down 1, untouched 1", stdout, StringComparison.Ordinal);
        Assert.Contains("imp: down; spawn-one implementor --resume old-session --team proj", stdout, StringComparison.Ordinal);
        Assert.Equal("old-session", RowOf("imp").Member("session_id")!.Value.AsString());
    }

    [Fact]
    public void ARowWithNoLiveSessionInItsPaneIsDownWithTheResumeRemedy()
    {
        RecoverTeam(Environment.ProcessId, MemberRow("imp", "implementor", GonePid(), "p1", "old-session"));
        _liveSessions = [new LiveSession("elsewhere", LivePidOfAnotherProcess(), RecoverStart, "p9", [])];

        var (code, stdout, _) = Recover();

        Assert.Equal(0, code);
        Assert.Contains("re-stamped 0, down 1, untouched 0", stdout, StringComparison.Ordinal);
        Assert.Contains("--resume old-session", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoLiveSessionsInOnePaneAreAmbiguousAndTheRowIsNotReStamped()
    {
        RecoverTeam(Environment.ProcessId, MemberRow("imp", "implementor", GonePid(), "p1", "old-session"));
        _liveSessions =
        [
            new LiveSession("a", LivePidOfAnotherProcess(), RecoverStart, "p1", []),
            new LiveSession("b", LivePidOfAnotherProcess(), RecoverStart, "p1", []),
        ];

        var (_, stdout, _) = Recover();

        Assert.Contains("re-stamped 0, down 1", stdout, StringComparison.Ordinal);
        Assert.Equal("old-session", RowOf("imp").Member("session_id")!.Value.AsString());
    }

    [Fact]
    public void OneLiveSessionTakesAtMostOneRowSoTwoDeadRowsInItsPaneAreBothDown()
    {
        RecoverTeam(
            Environment.ProcessId,
            MemberRow("imp", "implementor", GonePid(), "p1", "imp-session"),
            MemberRow("rev", "reviewer", GonePid(), "p1", "rev-session"));
        _liveSessions = [new LiveSession("only", LivePidOfAnotherProcess(), RecoverStart, "p1", [])];

        var (code, stdout, stderr) = Recover();

        Assert.True(code == 0, stderr);
        Assert.Contains("re-stamped 0, down 2, untouched 0", stdout, StringComparison.Ordinal);
        Assert.Equal("imp-session", RowOf("imp").Member("session_id")!.Value.AsString());
        Assert.Equal("rev-session", RowOf("rev").Member("session_id")!.Value.AsString());
    }

    [Fact]
    public void ARowWhoseProcessIsAliveIsNeverDisplacedEvenWhenAnotherSessionRunsInItsPane()
    {
        var holder = LivePidOfAnotherProcess();
        RecoverTeam(Environment.ProcessId, MemberRow("imp", "implementor", holder, "p1", "held-session"));
        _liveSessions = [new LiveSession("usurper", LivePidOfAnotherProcess(), RecoverStart, "p1", [])];

        var (code, stdout, _) = Recover();

        Assert.Equal(0, code);
        Assert.Contains("re-stamped 0, down 0, untouched 1", stdout, StringComparison.Ordinal);
        Assert.Equal(holder, (int)RowOf("imp").Member("pid")!.Value.AsNumber());
    }

    [Fact]
    public void ASessionThatDoesNotOwnTheTeamIsRefusedAndNothingIsChanged()
    {
        RecoverTeam(LivePidOfAnotherProcess(), MemberRow("imp", "implementor", GonePid(), "p1", "old-session"));
        _liveSessions = [new LiveSession("new-session", LivePidOfAnotherProcess(), RecoverStart, "p1", [])];

        var (code, _, stderr) = Recover();

        Assert.NotEqual(0, code);
        Assert.Contains("does not own the team", stderr, StringComparison.Ordinal);
        Assert.Equal("old-session", RowOf("imp").Member("session_id")!.Value.AsString());
    }

    [Fact]
    public void ACallerWhoseAncestryProvesNoSessionIsRefusedAndNothingIsChanged()
    {
        RecoverTeam(Environment.ProcessId, MemberRow("imp", "implementor", GonePid(), "p1", "old-session"));
        _liveSessions = [new LiveSession("new-session", LivePidOfAnotherProcess(), RecoverStart, "p1", [])];
        _nearest = new NearestSession(NearestKind.None, null, null, 0);

        var (code, _, stderr) = Recover();

        Assert.NotEqual(0, code);
        Assert.Contains("only the team's owner", stderr, StringComparison.Ordinal);
        Assert.Equal("old-session", RowOf("imp").Member("session_id")!.Value.AsString());
    }

    private const string RelinkChild = "proj-lane";

    private void ArrangeLaneLink(string linkTeamId = "", bool leadRow = true, string linkTeam = "proj")
    {
        const string childCreated = "2026-10-22T09:05:35-04:00";
        const string childSession = "s-lead";
        var owner = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        var lead = leadRow ? $"{{\"name\":\"proj-so-ws1\",\"role\":\"sub-orchestrator\",\"route\":\"peer\",\"pid\":{owner},\"pid_start\":\"{SelfStart}\",\"session_id\":\"s-lead\"}}" : string.Empty;
        var streams = leadRow ? "{\"ws1\":{\"state\":\"open\",\"sub_orchestrator\":\"proj-so-ws1\"}}" : "{\"ws1\":{\"state\":\"open\"}}";
        WriteTeam("proj", $"{{\"version\":1,\"team_id\":\"t-1\",\"created\":\"2026-10-25T10:21:47-04:00\",\"transport\":\"terminal\",\"roster_level\":\"repo\",\"orchestrator\":{{\"pid\":{owner},\"pid_start\":\"{SelfStart}\"}},\"members\":[{lead}],\"streams\":{streams}}}");
        WriteTeam(RelinkChild, $"{{\"version\":1,\"team_id\":\"t-lane\",\"created\":\"{childCreated}\",\"transport\":\"terminal\",\"orchestrator\":{{\"session_id\":\"{childSession}\"}},\"parent\":{{\"team\":\"{linkTeam}\",\"stream\":\"ws1\"{linkTeamId}}},\"members\":[],\"workstream\":{{\"state\":\"working\"}}}}");
    }

    private JsonValue ChildRecord() => JsonText.Parse(_sandbox.TeamText(TeamPath(RelinkChild)));

    private bool ChildStillProvesAsTheLane() =>
        ChildRecord().Member("parent")!.Value.Member("team_id") is { IsText: true } id
        && id.AsString() == JsonText.Parse(_sandbox.TeamText(TeamPath("proj"))).Member("team_id")!.Value.AsString();

    [Fact]
    public void PlainRecoverNeverTouchesALaneChildsLink()
    {
        ArrangeLaneLink();

        var (code, stdout, stderr) = Recover();

        Assert.True(code == 0, stderr);
        Assert.DoesNotContain("lane child", stdout, StringComparison.Ordinal);
        Assert.False(ChildStillProvesAsTheLane());
    }

    private (int Code, string Stdout, string Stderr) RecoverRelink(params string[] children) =>
        Roster([.. new[] { "recover", "--team", "proj", "--orchestrator-pid", Self }, .. children.SelectMany(c => new[] { "--relink-lane", c })]);

    [Fact]
    public void RelinkLaneStampsTheManagersCurrentTeamIdIntoTheNamedChildsLink()
    {
        ArrangeLaneLink();
        Assert.False(ChildStillProvesAsTheLane());

        var (code, stdout, stderr) = RecoverRelink(RelinkChild);

        Assert.True(code == 0, stderr);
        Assert.Contains($"lane child {RelinkChild} re-linked to this team's current incarnation (by owner)", stdout, StringComparison.Ordinal);
        Assert.Equal("t-1", ChildRecord().Member("parent")!.Value.Member("team_id")!.Value.AsString());
        Assert.True(ChildStillProvesAsTheLane());
    }

    [Fact]
    public void RelinkLaneOverwritesALinkThatNamesAnotherIncarnationAndSaysSo()
    {
        ArrangeLaneLink(linkTeamId: ",\"team_id\":\"t-old\"");

        var (code, stdout, stderr) = RecoverRelink(RelinkChild);

        Assert.True(code == 0, stderr);
        Assert.Contains("its link named team_id t-old, now overwritten", stdout, StringComparison.Ordinal);
        Assert.True(ChildStillProvesAsTheLane());
    }

    [Fact]
    public void RelinkLaneRefusesAChildWhoseLinkNamesAnotherParentTeamAndChangesNothing()
    {
        ArrangeLaneLink(linkTeam: "other");

        var (code, _, stderr) = RecoverRelink(RelinkChild);

        Assert.NotEqual(0, code);
        Assert.Contains("is not a lane child of proj", stderr, StringComparison.Ordinal);
        Assert.False(ChildStillProvesAsTheLane());
    }

    [Fact]
    public void RelinkLaneRefusesATeamThatIsNotATeamAtAll()
    {
        ArrangeLaneLink();

        var (code, _, stderr) = RecoverRelink("no-such-team");

        Assert.NotEqual(0, code);
        Assert.Contains("is not a lane child of proj", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void RelinkLaneIsRefusedToASessionThatDoesNotOwnTheTeam()
    {
        ArrangeLaneLink();
        WriteTeam("proj", _sandbox.TeamText(TeamPath("proj")).Replace($"\"pid\":{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)},\"pid_start\"", $"\"pid\":{LivePidOfAnotherProcess().ToString(CultureInfo.InvariantCulture)},\"pid_start\"", StringComparison.Ordinal));

        var (code, _, stderr) = RecoverRelink(RelinkChild);

        Assert.NotEqual(0, code);
        Assert.Contains("does not own the team", stderr, StringComparison.Ordinal);
        Assert.False(ChildStillProvesAsTheLane());
    }

    [Fact]
    public void RelinkLaneRefusesAChildWhenTheManagersStreamNamesNoLead()
    {
        ArrangeLaneLink(leadRow: false);

        var (code, _, stderr) = RecoverRelink(RelinkChild);

        Assert.NotEqual(0, code);
        Assert.Contains("records no lane lead for stream ws1", stderr, StringComparison.Ordinal);
        Assert.True(ChildRecord().Member("parent")!.Value.Member("team_id") is not { IsText: true });
    }
}
