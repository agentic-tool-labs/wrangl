using System.Text;
using Ah.Commands;
using Ah.Core.Json;

namespace Ah.Cli.Tests;

public sealed partial class RosterTeamVerbTests
{
    private Action? _scriptPane;

    private JsonObject LaunchedOutsideRow(string memberFields)
    {
        var home = SpawnTeamHome();
        _scriptPane?.Invoke();
        _sandbox.PlantTeam(
            Path.Combine(home, "teams", "proj.json"),
            LiveTeamJson("herdr", "{\"role\":\"implementor\",\"name\":\"proj-implementor\",\"route\":\"peer\",\"transport_id\":\"w1:p1\",\"model\":\"sonnet\"" + memberFields + "}"));
        var (code, stdout, stderr) = Roster("doctor", "--team", "proj");
        Assert.True(code is 0 or 1, stdout + stderr);
        return JsonText.Parse(Encoding.UTF8.GetBytes(stdout)).AsObject()["rows"].AsArray()
            .Single(r => r.Member("name")!.Value.AsString() == "launched-outside-ah").AsObject();
    }

    [Fact]
    public void DoctorWarnsOfAMemberWhoseRowHoldsAMatchedBareMarkAndNamesTheRelaunch()
    {
        var row = LaunchedOutsideRow(",\"bare\":{\"state\":\"bare\",\"pid\":100,\"pid_start\":\"t0\",\"session_id\":\"s1\",\"since\":\"2026-10-21T20:00:00.0000000+00:00\",\"failed\":[]}");

        Assert.Equal("warn", row["status"].AsString());
        Assert.Contains("launched outside ah: model/effort/hooks not applied; the daemon re-adopts it once it is idle; to ask now: `ah roster recover now --team proj proj-implementor`", row["detail"].AsString(), StringComparison.Ordinal);
        Assert.DoesNotContain("--relaunch", row["detail"].AsString(), StringComparison.Ordinal);
    }

    [Fact]
    public void DoctorSaysRecoveryIsOffAndOnlyTheUserCanSwitchItOnWhenTheSwitchIsOff()
    {
        Ah.TestKit.TestHomeDocs.Put(_sandbox.AhHome, Ah.Watch.WatchModule.RecoverySwitchKey, "{\"on\":false}");

        var row = LaunchedOutsideRow(",\"bare\":{\"state\":\"bare\",\"pid\":100,\"pid_start\":\"t0\",\"session_id\":\"s1\",\"since\":\"2026-10-21T20:00:00.0000000+00:00\",\"failed\":[]}");

        Assert.Contains("recovery is off, so nothing re-adopts it; only the user can switch it on (`ah roster recover enable`)", row["detail"].AsString(), StringComparison.Ordinal);
        Assert.DoesNotContain("--relaunch", row["detail"].AsString(), StringComparison.Ordinal);
        Assert.DoesNotContain("recover now", row["detail"].AsString(), StringComparison.Ordinal);
    }

    [Fact]
    public void DoctorNamesAnUnmatchedClaudeWithItsFailedChecksAndSaysItIsLeftAlone()
    {
        var row = LaunchedOutsideRow(",\"bare\":{\"state\":\"bare-unmatched\",\"pid\":100,\"pid_start\":\"t0\",\"session_id\":\"s1\",\"since\":\"2026-10-21T20:00:00.0000000+00:00\",\"failed\":[\"M3\",\"M4\"]}");

        Assert.Equal("warn", row["status"].AsString());
        Assert.Contains("proj-implementor (M3, M4)", row["detail"].AsString(), StringComparison.Ordinal);
        Assert.Contains("not acted on", row["detail"].AsString(), StringComparison.Ordinal);
        Assert.DoesNotContain("ah roster resume", row["detail"].AsString(), StringComparison.Ordinal);
    }

    private const string PaneAnswerHead = "{\"result\":{\"process_info\":{\"foreground_processes\":[";

    private void PaneRuns(string argvJson, string cwd = "/work")
    {
        _scriptPane = () =>
        {
            PutHerdrOnPath();
            _runner.Output = argv => argv.Contains("process-info") ? PaneAnswerHead + "{\"pid\":77,\"argv\":" + argvJson + ",\"cwd\":\"" + cwd + "\"}]}}}" : string.Empty;
        };
    }

    private string SettingsFor(string member) =>
        "{\\\"env\\\":{\\\"WRANGL_TEAM_FILE\\\":\\\"" + Path.Combine(Project, ".claude", "ah2", "teams", "proj.json") + "\\\",\\\"AH_MEMBER_NAME\\\":\\\"" + member + "\\\"}}";

    [Fact]
    public void DoctorReadsTheBarePaneArgvItselfAndWarnsWithNoMarkAtAll()
    {
        PaneRuns("[\"claude\",\"--resume\",\"s1\"]");

        var row = LaunchedOutsideRow(string.Empty);

        Assert.Equal("warn", row["status"].AsString());
        Assert.True(row["detail"].AsString().Contains("launched outside ah", StringComparison.Ordinal), row["detail"].AsString() + " || " + string.Join(" ;; ", _runner.Calls.Select(a => string.Join(' ', a))));
        Assert.Contains("`ah roster recover now --team proj proj-implementor`", row["detail"].AsString(), StringComparison.Ordinal);
    }

    [Fact]
    public void DoctorReportsDriftForALaunchByAhWhoseAgentDiffersFromTheRowAndNeverCallsItOutsideAh()
    {
        PaneRuns("[\"claude\",\"--agent\",\"some:other-agent\",\"--model\",\"sonnet\",\"--settings\",\"" + SettingsFor("proj-implementor") + "\"]");

        var row = LaunchedOutsideRow(string.Empty);

        Assert.Equal("warn", row["status"].AsString());
        Assert.Contains("runs agent some:other-agent", row["detail"].AsString(), StringComparison.Ordinal);
        Assert.Contains("relaunch to apply", row["detail"].AsString(), StringComparison.Ordinal);
        Assert.DoesNotContain("launched outside ah", row["detail"].AsString(), StringComparison.Ordinal);
    }

    [Fact]
    public void DoctorNeverSaysOkForAPaneItCouldNotReadAndTheDaemonHasNotLookedAt()
    {
        var row = LaunchedOutsideRow(string.Empty);

        Assert.Equal("warn", row["status"].AsString());
        Assert.Contains("not checked", row["detail"].AsString(), StringComparison.Ordinal);
    }

    [Fact]
    public void DoctorIsOkWhenThePaneHoldsNoClaudeAtAll()
    {
        PaneRuns("[\"-zsh\"]");

        var row = LaunchedOutsideRow(string.Empty);

        Assert.Equal("ok", row["status"].AsString());
    }

    private string CheckpointStatusWithMark(string? mark)
    {
        var row = OneImplementor.TrimEnd('}') + (mark is null ? string.Empty : ",\"bare\":" + mark) + "}";
        WriteTeam("proj", LiveTeamJson("tmux", row));
        ScriptCheckpointTrees();
        _panes.Default = PaneKnowledge.Present;
        Registered(ImplSession, live: true);
        _runner.Ps = argv => SelfStartOnly(argv) ?? (argv.Contains("command=") && argv.Contains(Self) ? "claude --resume s1\n" : null);

        var (code, result, text) = ResumeCheckpointOf(WriteRecord(null));

        Assert.True(code == 0, text);
        return StatusOfMember(result);
    }

    [Fact]
    public void AResumeCheckpointDoesNotAdoptALiveMemberWhoseRowHoldsAMatchedBareMark()
    {
        var status = CheckpointStatusWithMark(MarkOf(Environment.ProcessId, SelfStart));

        Assert.Equal("bare", status);
    }

    private static string MarkOf(int pid, string start) =>
        "{\"state\":\"bare\",\"pid\":" + pid + ",\"pid_start\":\"" + start + "\",\"session_id\":\"s1\",\"since\":\"2026-10-21T20:00:00.0000000+00:00\",\"failed\":[]}";

    private static string MarkSince(DateTimeOffset since, int pid = 999_999) =>
        MarkOf(pid, SelfStart).Replace("2026-10-21T20:00:00.0000000+00:00", since.ToString("o", System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);

    [Fact]
    public void L2bABarePidTheHostCannotObserveWithEvidenceAMinuteOldIsNotAdopted()
    {
        Ah.TestKit.PeerRowJudge.CannotObserve = true;
        try
        {
            Assert.Equal("bare", CheckpointStatusWithMark(MarkSince(DateTimeOffset.UtcNow.AddSeconds(-60))));
        }
        finally
        {
            Ah.TestKit.PeerRowJudge.CannotObserve = false;
        }
    }

    [Fact]
    public void NegativeL2bABarePidTheHostCannotObserveWithOldEvidenceIsAdopted()
    {
        Ah.TestKit.PeerRowJudge.CannotObserve = true;
        try
        {
            Assert.Equal("live", CheckpointStatusWithMark(MarkSince(DateTimeOffset.UtcNow.AddHours(-2))));
        }
        finally
        {
            Ah.TestKit.PeerRowJudge.CannotObserve = false;
        }
    }

    [Fact]
    public void AResumeCheckpointIgnoresAMatchedMarkWhoseProcessIsGone()
    {
        Assert.Equal("live", CheckpointStatusWithMark(MarkOf(999_999, SelfStart)));
    }

    [Fact]
    public void AResumeCheckpointStillAdoptsALiveMemberWithNoMarkOrOnlyAnUnmatchedOne()
    {
        Assert.Equal("live", CheckpointStatusWithMark(null));
        Assert.Equal("live", CheckpointStatusWithMark("{\"state\":\"bare-unmatched\",\"pid\":100,\"since\":\"2026-10-21T20:00:00.0000000+00:00\",\"failed\":[\"M3\"]}"));
    }

    [Fact]
    public void DoctorReadsAFieldThatIsNotAMarkAsNoMark()
    {
        var row = LaunchedOutsideRow(",\"bare\":\"yes\"");

        Assert.Contains("not checked", row["detail"].AsString(), StringComparison.Ordinal);
    }
}
