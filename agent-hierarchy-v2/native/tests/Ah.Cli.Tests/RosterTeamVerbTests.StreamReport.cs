using Ah.Core.Json;

namespace Ah.Cli.Tests;

/// <summary><c>stream-report</c>: a sub-orchestrator's status record.</summary>
public sealed partial class RosterTeamVerbTests
{
    private const string ParentMark = ",\"parent\":{\"team\":\"top\",\"stream\":\"s1\"}";

    private void ArrangeChildTeam(string? pid = null) =>
        WriteTeam("proj", LiveTeamJson("terminal", TwoPeers, pid).TrimEnd('}') + ParentMark + ",\"streams\":{" + ChildStream + "}}");

    private (int Code, string Stdout, string Stderr) Report(params string[] args) =>
        Roster(["stream-report", "--team", "proj", .. args]);

    // A row and, once the verb runs (Mirrored deletes every message file first), no file.
    private string StoredResponse() => Ah.TestKit.TestMsgStore.Plant(
        _sandbox.AhHome,
        Path.Combine(ProjectHierarchyDir, "msgs", "20261016-000000-abcd--orchestrator--ws-s1--response.md"),
        "---\nid: 20261016-000000-abcd\ntype: response\nto: orchestrator\nfrom: implementor\nslug: ws-s1\n---\n- DONE\n");

    private JsonValue Workstream() => JsonText.Parse(_sandbox.TeamText(TeamPath("proj"))).Member("workstream")!.Value;

    [Theory]
    [InlineData("working")]
    [InlineData("blocked")]
    [InlineData("review")]
    [InlineData("done")]
    [InlineData("paused")]
    public void AReportWritesTheStateTheSummaryAndTheTimeAndKeepsTheRestOfTheTeamFile(string state)
    {
        ArrangeChildTeam();

        var (code, _, stderr) = Report("--state", state, "--summary", "halfway through the parser", "--note", "wound down");

        Assert.True(code == 0, stderr);
        var workstream = Workstream();
        Assert.Equal(state, workstream.Member("state")!.Value.AsString());
        Assert.Equal("halfway through the parser", workstream.Member("summary")!.Value.AsString());
        Assert.False(workstream.Member("updated")!.Value.AsString().Length == 0);
        Assert.Null(workstream.Member("result"));
        var team = JsonText.Parse(_sandbox.TeamText(TeamPath("proj")));
        Assert.Equal(2, team.Member("members")!.Value.AsArray().Count);
        Assert.Equal("top", team.Member("parent")!.Value.Member("team")!.Value.AsString());
    }

    [Theory]
    [InlineData("finished")]
    [InlineData("")]
    [InlineData("Working")]
    public void AStateOutsideTheFiveIsRefused(string state)
    {
        ArrangeChildTeam();

        var (code, _, stderr) = Report("--state", state, "--summary", "x");

        Assert.NotEqual(0, code);
        Assert.Contains("--state must be one of", stderr, StringComparison.Ordinal);
        Assert.Contains("paused", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void PausedNeedsANoteAndOtherStatesNeedNone()
    {
        ArrangeChildTeam();

        var (code, _, stderr) = Report("--state", "paused", "--summary", "s");
        var (emptyCode, _, _) = Report("--state", "paused", "--summary", "s", "--note", "");
        var (workingCode, _, workingErr) = Report("--state", "working", "--summary", "s");
        var noPauser = Workstream().Member("paused_by");
        var (okCode, _, okErr) = Report("--state", "paused", "--summary", "s", "--note", "lane wound down");

        Assert.NotEqual(0, code);
        Assert.Contains("paused-needs-note", stderr, StringComparison.Ordinal);
        Assert.NotEqual(0, emptyCode);
        Assert.True(workingCode == 0, workingErr);
        Assert.Null(noPauser);
        Assert.True(okCode == 0, okErr);
        Assert.Equal("lane wound down", Workstream().Member("note")!.Value.AsString());
        Assert.Equal("orchestrator@proj", Workstream().Member("paused_by")!.Value.AsString());
    }

    [Fact]
    public void TheManagersOrchestratorMayPauseAndResumeALiveSubOrchestratorsLaneButDoNothingElseThere()
    {
        WriteTeam("top", LiveTeamJson("terminal", TwoPeers));
        ArrangeChildTeam(pid: LivePidOfAnotherProcess().ToString(System.Globalization.CultureInfo.InvariantCulture));

        var (workingCode, _, workingErr) = Report("--state", "working", "--summary", "s");
        var (pausedCode, _, pausedErr) = Report("--state", "paused", "--summary", "s", "--note", "wound down");
        var by = Workstream().Member("paused_by")!.Value.AsString();
        var (resumeCode, _, resumeErr) = Report("--state", "working", "--summary", "back");
        var (afterCode, _, afterErr) = Report("--state", "review", "--summary", "s");

        Assert.NotEqual(0, workingCode);
        Assert.Contains("does not own", workingErr, StringComparison.Ordinal);
        Assert.Contains("the manager team top", workingErr, StringComparison.Ordinal);
        Assert.Contains("--state paused", workingErr, StringComparison.Ordinal);
        Assert.True(pausedCode == 0, pausedErr);
        Assert.Equal("orchestrator@top", by);
        Assert.True(resumeCode == 0, resumeErr);
        Assert.NotEqual(0, afterCode);
        Assert.Contains("does not own", afterErr, StringComparison.Ordinal);
    }

    [Fact]
    public void DoctorShowsOneInfoRowForAPausedLaneThatIsNotAWarningAndNoneForAWorkingOne()
    {
        ArrangeChildTeam();
        Assert.True(Report("--state", "working", "--summary", "s").Code == 0);
        var none = RowNamed(DoctorRows(), "paused-lane");
        Assert.True(Report("--state", "paused", "--summary", "s", "--note", "wound down").Code == 0);

        var row = RowNamed(DoctorRows(), "paused-lane")!;

        Assert.Null(none);
        Assert.Equal("ok", row["status"].AsString());
        Assert.Contains("info: lane proj paused", row["detail"].AsString(), StringComparison.Ordinal);
        Assert.Contains("wound down", row["detail"].AsString(), StringComparison.Ordinal);
    }

    [Fact]
    public void AnOrchestratorOfAnotherManagerCannotPauseTheLane()
    {
        WriteTeam("other", LiveTeamJson("terminal", TwoPeers));
        ArrangeChildTeam(pid: LivePidOfAnotherProcess().ToString(System.Globalization.CultureInfo.InvariantCulture));

        var (code, _, stderr) = Report("--state", "paused", "--summary", "s", "--note", "x");

        Assert.NotEqual(0, code);
        Assert.Contains("does not own", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("manager team", stderr, StringComparison.Ordinal);
        Assert.Null(JsonText.Parse(_sandbox.TeamText(TeamPath("proj"))).Member("workstream"));
    }

    private void ArrangeManagerWithLane(string streams = ChildStream)
    {
        WriteTeam("top", LiveTeamJson("terminal", TwoPeers).TrimEnd('}') + ",\"streams\":{" + streams + "}}");
        ArrangeChildTeam(pid: LivePidOfAnotherProcess().ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private (int Code, string Stdout, string Stderr) ReportAsTop(params string[] args) =>
        Roster(["stream-report", "--state", "paused", "--summary", "s", "--note", "wound down", .. args]);

    [Fact]
    public void AManagerWithOneOpenLaneNeedNotNameIt()
    {
        ArrangeManagerWithLane();

        var (code, _, stderr) = ReportAsTop();

        Assert.True(code == 0, stderr);
        Assert.Equal("paused", Workstream().Member("state")!.Value.AsString());
        Assert.Null(JsonText.Parse(_sandbox.TeamText(TeamPath("top"))).Member("workstream"));
    }

    [Fact]
    public void NamingTheManagerTeamItselfIsRefusedAndTheRefusalNamesTheLane()
    {
        ArrangeManagerWithLane();

        var (code, _, stderr) = ReportAsTop("--team", "top");

        Assert.NotEqual(0, code);
        Assert.Contains("--team proj (lane s1)", stderr, StringComparison.Ordinal);
        Assert.Null(JsonText.Parse(_sandbox.TeamText(TeamPath("proj"))).Member("workstream"));
    }

    [Fact]
    public void AnUnknownLaneIsNotReplacedByTheOneOpenLane()
    {
        ArrangeManagerWithLane();

        var (code, _, stderr) = ReportAsTop("no-such-lane");

        Assert.NotEqual(0, code);
        Assert.Contains("--team proj (lane s1)", stderr, StringComparison.Ordinal);
        Assert.Null(JsonText.Parse(_sandbox.TeamText(TeamPath("proj"))).Member("workstream"));
    }

    [Fact]
    public void TwoOpenLanesAreNotGuessedBetween()
    {
        ArrangeManagerWithLane(ChildStream + "," + ChildStream.Replace("s1", "s2", StringComparison.Ordinal));
        WriteTeam("other", LiveTeamJson("terminal", TwoPeers, LivePidOfAnotherProcess().ToString(System.Globalization.CultureInfo.InvariantCulture)).TrimEnd('}') + ",\"parent\":{\"team\":\"top\",\"stream\":\"s2\"}}");

        var (code, _, stderr) = ReportAsTop();

        Assert.NotEqual(0, code);
        Assert.Contains("--team proj (lane s1)", stderr, StringComparison.Ordinal);
        Assert.Contains("--team other (lane s2)", stderr, StringComparison.Ordinal);
        Assert.Null(JsonText.Parse(_sandbox.TeamText(TeamPath("proj"))).Member("workstream"));
        Assert.Null(JsonText.Parse(_sandbox.TeamText(TeamPath("other"))).Member("workstream"));
    }

    [Fact]
    public void ADoneStreamIsNotAnOpenLane()
    {
        ArrangeManagerWithLane(ChildStream.Replace("\"open\"", "\"done\"", StringComparison.Ordinal));

        var (code, _, stderr) = ReportAsTop();

        Assert.NotEqual(0, code);
        Assert.Contains("no open lane", stderr, StringComparison.Ordinal);
        Assert.Null(JsonText.Parse(_sandbox.TeamText(TeamPath("proj"))).Member("workstream"));
    }

    [Fact]
    public void ASummaryOfOneHundredSixtyCharactersIsKeptAndOneOfOneHundredSixtyOneIsRefused()
    {
        ArrangeChildTeam();

        var (okCode, _, okErr) = Report("--state", "working", "--summary", new string('a', 160));
        var (code, _, stderr) = Report("--state", "working", "--summary", new string('a', 161));

        Assert.True(okCode == 0, okErr);
        Assert.NotEqual(0, code);
        Assert.Contains("161 characters", stderr, StringComparison.Ordinal);
        Assert.Equal(160, Workstream().Member("summary")!.Value.AsString().Length);
    }

    [Theory]
    [InlineData("two\nlines", "one line")]
    [InlineData("a `quoted` word", "backtick")]
    public void ASummaryWithALineBreakOrABacktickIsRefused(string summary, string reason)
    {
        ArrangeChildTeam();

        var (code, _, stderr) = Report("--state", "working", "--summary", summary);

        Assert.NotEqual(0, code);
        Assert.Contains(reason, stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void AResultIsAcceptedOnlyWithDoneAndOnlyForAStoredResponseByAbsolutePath()
    {
        ArrangeChildTeam();
        var good = StoredResponse();
        var notResponse = Path.Combine(Project, "notes.md");
        File.WriteAllText(notResponse, "x");
        var fileOnly = Path.Combine(Project, "20261016-000000-ffff--orchestrator--ws-s1--response.md");
        File.WriteAllText(fileOnly, "x");

        var (workingCode, _, workingErr) = Report("--state", "working", "--summary", "s", "--result", good);
        var (relativeCode, _, relativeErr) = Report("--state", "done", "--summary", "s", "--result", "x--response.md");
        var (plainCode, _, plainErr) = Report("--state", "done", "--summary", "s", "--result", notResponse);
        var (missingCode, _, missingErr) = Report("--state", "done", "--summary", "s", "--result", Path.Combine(Project, "gone--response.md"));
        var (fileOnlyCode, _, fileOnlyErr) = Report("--state", "done", "--summary", "s", "--result", fileOnly);
        var (code, _, stderr) = Report("--state", "done", "--summary", "s", "--result", good);

        Assert.NotEqual(0, fileOnlyCode);
        Assert.Contains("does not exist", fileOnlyErr, StringComparison.Ordinal);
        Assert.NotEqual(0, workingCode);
        Assert.Contains("only with --state done", workingErr, StringComparison.Ordinal);
        Assert.NotEqual(0, relativeCode);
        Assert.Contains("absolute", relativeErr, StringComparison.Ordinal);
        Assert.NotEqual(0, plainCode);
        Assert.Contains("--response.md", plainErr, StringComparison.Ordinal);
        Assert.NotEqual(0, missingCode);
        Assert.Contains("does not exist", missingErr, StringComparison.Ordinal);
        Assert.True(code == 0, stderr);
        Assert.Equal(good, Workstream().Member("result")!.Value.AsString());
    }

    [Fact]
    public void AnEarlierResultIsNotKeptByALaterReport()
    {
        ArrangeChildTeam();
        var good = StoredResponse();
        Assert.True(Report("--state", "done", "--summary", "s", "--result", good).Code == 0);

        Assert.True(Report("--state", "working", "--summary", "again").Code == 0);

        Assert.Null(Workstream().Member("result"));
    }

    [Fact]
    public void ATeamWithNoParentIsRefusedAndNothingIsWritten()
    {
        WriteTeam("proj", LiveTeamJson("terminal", TwoPeers));

        var (code, _, stderr) = Report("--state", "working", "--summary", "s");

        Assert.NotEqual(0, code);
        Assert.Contains("not a sub-orchestrator's team", stderr, StringComparison.Ordinal);
        Assert.Null(JsonText.Parse(_sandbox.TeamText(TeamPath("proj"))).Member("workstream"));
    }

    private void ArrangeLaneUnderTop()
    {
        WriteTeam("top", LiveTeamJson("terminal", TwoPeers).TrimEnd('}') + ",\"streams\":{" + ChildStream + "}}");
        ArrangeChildTeam(pid: LivePidOfAnotherProcess().ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private (int Code, string Stdout, string Stderr) ReportFromTop(params string[] args) =>
        Roster(["stream-report", .. args, "--team", "top", "--state", "paused", "--summary", "s", "--note", "held"]);

    [Fact]
    public void TheManagerNamesItsOwnTeamAndALaneToReachTheLanesChildTeam()
    {
        ArrangeLaneUnderTop();

        var (code, _, stderr) = ReportFromTop("s1");

        Assert.True(code == 0, stderr);
        Assert.Equal("paused", Workstream().Member("state")!.Value.AsString());
        Assert.Null(JsonText.Parse(_sandbox.TeamText(TeamPath("top"))).Member("workstream"));
    }

    [Theory]
    [InlineData]
    [InlineData("no-such-lane")]
    public void WithoutALaneOfItsOwnTheManagerIsToldTheTeamToNameAndNothingIsWritten(params string[] lane)
    {
        ArrangeLaneUnderTop();

        var (code, _, stderr) = ReportFromTop(lane);

        Assert.NotEqual(0, code);
        Assert.Contains("--team proj (lane s1)", stderr, StringComparison.Ordinal);
        Assert.Null(JsonText.Parse(_sandbox.TeamText(TeamPath("proj"))).Member("workstream"));
    }

    [Fact]
    public void NamingALaneDoesNotLetTheManagerReportAnythingButAPause()
    {
        ArrangeLaneUnderTop();

        var (code, _, stderr) = Roster(["stream-report", "s1", "--team", "top", "--state", "working", "--summary", "s"]);

        Assert.NotEqual(0, code);
        Assert.Contains("does not own", stderr, StringComparison.Ordinal);
        Assert.Null(JsonText.Parse(_sandbox.TeamText(TeamPath("proj"))).Member("workstream"));
    }

    [Fact]
    public void ASessionThatDoesNotOwnTheChildTeamIsRefused()
    {
        ArrangeChildTeam(pid: LivePidOfAnotherProcess().ToString(System.Globalization.CultureInfo.InvariantCulture));

        var (code, _, stderr) = Report("--state", "working", "--summary", "s");

        Assert.NotEqual(0, code);
        Assert.Contains("does not own", stderr, StringComparison.Ordinal);
        Assert.Null(JsonText.Parse(_sandbox.TeamText(TeamPath("proj"))).Member("workstream"));
    }
}
