using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Mesh;
using Ah.TestKit;

namespace Ah.Cli.Tests;

/// <summary><c>roster quit</c>: one call waits at most 100 s, one pass covers the lanes, and <c>--all</c> takes every top team this session owns.</summary>
public sealed partial class RosterTeamVerbTests
{
    private sealed class StepClock(DateTimeOffset start) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = start;

        public TimeSpan LocalOffset(DateTimeOffset instant) => TimeSpan.Zero;
    }

    private StepClock? _stepClock;

    private Action? _onQuitSleep;

    private void ArrangeQuit(string team = "proj", string member = Impl, string session = "sess-impl", string? parent = null, bool live = true)
    {
        _withPanes = true;
        _stepClock ??= new StepClock(DateTimeOffset.UtcNow);
        var json = LiveTeamJson("tmux", Member("implementor", member, "%1"));
        WriteTeam(team, parent is null ? json : json.Replace("\"version\":1,", "\"version\":1,\"parent\":{\"team\":\"" + parent + "\",\"stream\":\"ws1\"},", StringComparison.Ordinal));
        if (live)
        {
            PeerUp(member, "%1", session: session);
            Activity(session + ".json", "working", DateTimeOffset.UtcNow);
        }

        ScriptCheckpointTrees();
    }

    private string QuitHoldPath(string team) => Path.Combine(Hierarchy, "holds", team, "hold.json");

    private JsonValue QuitHold(string team) => JsonText.Parse(HoldBody(QuitHoldPath(team)) ?? throw new InvalidOperationException($"team {team} has no hold"));

    private void AnswerOnFirstSleep(string team)
    {
        _onQuitSleep = () =>
        {
            _onQuitSleep = null;
            var checkpoint = QuitHold(team).Member("checkpoint")!.Value.AsString();
            _ = CheckpointAnswer(JsonText.Parse(TestCheckpoints.Read(_sandbox.AhHome, checkpoint)), "Checkpoint: ready branch=ah/feature sha=" + Sha[..7] + " inflight=none");
        };
    }

    [Fact]
    public void TheUserAtAPlainTerminalQuitsTheFoldersOneTeamWithNoTeamFlagAndASessionlessAgentIsStillRefused()
    {
        ArrangeQuit();
        AnswerOnFirstSleep("proj");
        _nearest = new NearestSession(NearestKind.None);

        var (refusedCode, refusedOut, _) = Roster(["quit", "--team", "proj"]);
        Assert.Equal(2, refusedCode);
        Assert.Equal("owner-unproven", JsonText.Parse(refusedOut).Member("refused")!.Value.AsString());

        _plainUser = true;
        var (code, stdout, stderr) = Roster(["quit"]);

        Assert.True(code == 0, stdout + stderr);
        Assert.Equal("held", QuitHold("proj").Member("phase")!.Value.AsString());
    }

    [Fact]
    public void TheUserAtAPlainTerminalGetsTheStopPlanOfTheFoldersOneTeam()
    {
        ArrangeQuit();
        _callers = new RecordingCallers { ProcessTable = new NoAncestry() };
        _nearest = new NearestSession(NearestKind.None);
        _plainUser = true;

        var (code, stdout, stderr) = Roster(["stop", "--now"]);

        Assert.True(code == 0, stdout + stderr);
        Assert.DoesNotContain("owner-unproven", stdout + stderr, StringComparison.Ordinal);
        Assert.Contains("proj", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void TheUserWithSeveralTeamsInTheFolderIsToldWhichExist()
    {
        ArrangeQuit();
        WriteTeam("other", LiveTeamJson("tmux", Member("implementor", "other-implementor", "%2")));
        _nearest = new NearestSession(NearestKind.None);
        _plainUser = true;

        var (code, stdout, stderr) = Roster(["quit"]);

        Assert.NotEqual(0, code);
        Assert.Contains("pass --team <name>", stdout + stderr, StringComparison.Ordinal);
        Assert.Contains("other", stdout + stderr, StringComparison.Ordinal);
        Assert.Contains("proj", stdout + stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void AQuitWhoseOneMemberAnswersSealsInOneCall()
    {
        ArrangeQuit();
        AnswerOnFirstSleep("proj");

        var (code, stdout, stderr) = Roster(["quit", "--team", "proj"]);

        Assert.True(code == 0, stdout + stderr);
        Assert.Equal("held", QuitHold("proj").Member("phase")!.Value.AsString());
        Assert.Equal("held", JsonText.Parse(stdout).Member("phase")!.Value.AsString());
    }

    [Fact]
    public void AQuitWithdrawsTheMembersPendingRewakeAndNegativeLeavesItsPlainMailToTheDrain()
    {
        ArrangeQuit();
        AnswerOnFirstSleep("proj");
        _exposeMailbox = true;
        string? box = null;
        Ah.Mesh.IMailbox? mailbox = null;
        _seedGraph = graph =>
        {
            box = graph.Mailbox.BoxDirFor(graph.Roster.TeamPath(Hierarchy, "proj"), Impl);
            mailbox = graph.Mailbox;
            foreach (var (id, kind) in new[] { ("rewake-0123456789abcdef", "event"), ("m1", "note") })
            {
                Ah.TestKit.TestPlant.Mail(graph.Writer, box, JsonText.Parse("{\"id\":\"" + id + "\",\"kind\":\"" + kind + "\",\"event\":\"resume\",\"from\":\"ahd\",\"to\":\"" + Impl + "@proj\",\"to_session\":\"sess-impl\",\"text\":\"t\",\"created\":\"2026-11-04T00:00:00.000Z\",\"route\":\"pull\"}"), Project);
            }
        };

        var (code, stdout, stderr) = Roster(["quit", "--team", "proj"]);

        Assert.True(code == 0, stdout + stderr);
        Assert.Equal("withdrawn", mailbox!.LadderRowsOf(box!, "rewake-0123456789abcdef")[^1].Get("state")!.Value.AsString());
        Assert.NotEqual("withdrawn", mailbox.LadderRowsOf(box!, "m1") is { Count: > 0 } rows ? rows[^1].Get("state")!.Value.AsString() : "none");
    }

    [Fact]
    public void AQuitSealAbandonsThePlainBriefOfAMemberAddedAfterItsCheckpoint()
    {
        ArrangeQuit();
        AnswerOnFirstSleep("proj");
        var answer = _onQuitSleep!;
        _onQuitSleep = () =>
        {
            answer();
            WriteTeam("proj", LiveTeamJson("tmux", Member("implementor", Impl, "%1") + "," + Member("reviewer", "proj-late", "%2")));
            _ = AskRelease("20261016-010000-bbbb", member: "proj-late", slug: "late");
        };

        var (_, stdout, stderr) = Roster(["quit", "--team", "proj"]);

        Assert.Equal("held", QuitHold("proj").Member("phase")!.Value.AsString());
        var closed = JsonText.Parse(stdout).Member("briefs_abandoned")?.AsArray().Select(static id => id.AsString());
        Assert.True(closed is not null, stdout + stderr);
        Assert.Equal(["20261016-010000-bbbb"], closed);
    }

    [Fact]
    public void AQuitSealNegativeKeepsThePlainBriefOfAMemberResumeWillRelaunch()
    {
        ArrangeQuit();
        _ = AskRelease("20261016-010000-aaaa", slug: "work");

        var (_, stdout, stderr) = Roster(["quit", "--team", "proj", "--wait", "0"]);

        Assert.Equal("held", QuitHold("proj").Member("phase")!.Value.AsString());
        var closed = JsonText.Parse(stdout).Member("briefs_abandoned")?.AsArray().Select(static id => id.AsString()) ?? [];
        Assert.DoesNotContain("20261016-010000-aaaa", closed);
        Assert.True(stdout.Length > 0, stderr);
    }

    [Fact]
    public void AQuitWhoseBriefIsRefusedPrintsTheRefusal()
    {
        ArrangeQuit();
        _briefFails = true;

        var (code, stdout, _) = Roster(["quit", "--team", "proj", "--wait", "0"]);

        Assert.Equal(1, code);
        var failure = JsonText.Parse(stdout).Member("mail_errors")!.Value.AsArray().Single();
        Assert.Equal("proj", failure.Member("team")!.Value.AsString());
        Assert.False(string.IsNullOrEmpty(failure.Member("error")!.Value.AsString()));
    }

    [Fact]
    public void AQuitWhoseBriefsGoOutListsNoMailErrors()
    {
        ArrangeQuit();

        var (_, stdout, _) = Roster(["quit", "--team", "proj", "--wait", "0"]);

        Assert.Null(JsonText.Parse(stdout).Member("mail_errors"));
    }

    [Fact]
    public void AQuitWithWaitZeroSealsInOneCallWithStubs()
    {
        ArrangeQuit();

        var (code, stdout, stderr) = Roster(["quit", "--team", "proj", "--wait", "0"]);

        Assert.True(code == 3, stdout + stderr);
        Assert.Contains("STUB", File.ReadAllText(JsonText.Parse(stdout).Member("plan")!.Value.AsString()), StringComparison.Ordinal);
    }

    [Fact]
    public void AQuitUnansweredAtTheBudgetExitsFourAndARerunAfterTheWindowSeals()
    {
        ArrangeQuit();

        var (code, stdout, stderr) = Roster(["quit", "--team", "proj"]);

        Assert.True(code == 4, stdout + stderr);
        var first = JsonText.Parse(stdout);
        Assert.Equal("winding-down", first.Member("phase")!.Value.AsString());
        Assert.Equal(Impl, first.Member("waiting_on")!.Value.AsArray().Single().AsString());
        Assert.Equal("ah roster quit --team proj", first.Member("next")!.Value.AsString());
        Assert.Equal("winding-down", QuitHold("proj").Member("phase")!.Value.AsString());
        var checkpoint = first.Member("checkpoint")!.Value.AsString();

        _stepClock!.UtcNow = DateTimeOffset.Parse(QuitHold("proj").Member("since")!.Value.AsString(), System.Globalization.CultureInfo.InvariantCulture).AddSeconds(430);
        (code, stdout, stderr) = Roster(["quit", "--team", "proj"]);

        Assert.True(code == 3, stdout + stderr);
        Assert.Equal("held", QuitHold("proj").Member("phase")!.Value.AsString());
        Assert.Equal(checkpoint, JsonText.Parse(stdout).Member("checkpoint")!.Value.AsString());
    }

    [Fact]
    public void NegativeAStopThatLandsDuringAQuitIsLeftAsItIs()
    {
        ArrangeQuit();
        _onQuitSleep = () => PutHold(QuitHoldPath("proj"),"{\"v\":1,\"team\":\"proj\",\"kind\":\"stop\",\"phase\":\"stopped\",\"since\":\"2026-11-04T00:00:00.000Z\"}");

        var (code, stdout, stderr) = Roster(["quit", "--team", "proj", "--wait", "10"]);

        Assert.True(code != 0, stdout + stderr);
        Assert.Contains("team_stopped", stdout + stderr, StringComparison.Ordinal);
        Assert.Equal("stop", QuitHold("proj").Member("kind")!.Value.AsString());
    }

    [Fact]
    public void NegativeALaneLeadsQuitDuringTheTopsQuitIsRefusedAndTakesNoSecondCheckpoint()
    {
        ArrangeLane();
        _withPanes = true;
        var record = Path.Combine(Hierarchy, "lane-checkpoint.json");
        File.WriteAllText(record, "{\"members\":[]}");
        var hold = QuitHoldPath(ChildName);
        Directory.CreateDirectory(Path.GetDirectoryName(hold)!);
        File.WriteAllText(hold, JsonText.Stringify(new JsonObject { ["v"] = 1, ["team"] = ChildName, ["kind"] = "quit", ["phase"] = "winding-down", ["since"] = "2026-11-04T00:00:00.000Z", ["checkpoint"] = record }, 2));
        var before = CheckpointFiles();

        var (code, stdout) = LaneRun("quit", "--team", ChildName, "--wait", "0");

        Assert.True(code != 0, stdout);
        Assert.Contains("lane-lead-scope", stdout, StringComparison.Ordinal);
        Assert.Equal(before, CheckpointFiles());
        Assert.Equal("winding-down", QuitHold(ChildName).Member("phase")!.Value.AsString());
    }

    [Fact]
    public void NegativeALaneMembersQuitIsRefused()
    {
        ArrangeQuit("proj", live: false);
        _nearest = new NearestSession(NearestKind.Member, "proj", Impl, Environment.ProcessId);

        var (code, stdout, stderr) = Roster(["quit", "--team", "proj"]);

        Assert.True(code != 0, stdout + stderr);
        Assert.Contains("member-cannot-quit", stdout + stderr, StringComparison.Ordinal);
        Assert.False(Held(QuitHoldPath("proj")));
    }

    [Fact]
    public void OneCallSealsATreeWithADeadLeadAndTheTopsPlanEmbedsEveryLane()
    {
        ArrangeQuit("proj", live: false);
        ArrangeQuit("lane", "lane-impl", "sess-lane", parent: "proj");
        ArrangeQuit("deep", "deep-impl", "sess-deep", parent: "lane", live: false);

        var (code, stdout, stderr) = Roster(["quit", "--team", "proj", "--wait", "0"]);

        Assert.True(code == 3, stdout + stderr);
        Assert.Contains(_sent, s => s.Member == "lane-impl" && s.Team == "lane");
        Assert.Equal("held", QuitHold("lane").Member("phase")!.Value.AsString());
        Assert.Equal("held", QuitHold("deep").Member("phase")!.Value.AsString());
        Assert.Equal("lane", QuitHold("deep").Member("cascade_from")!.Value.AsString());
        Assert.Equal(QuitHold("proj").Member("since")!.Value.AsString(), QuitHold("deep").Member("since")!.Value.AsString());
        Assert.True(TestCheckpoints.Exists(_sandbox.AhHome, QuitHold("lane").Member("checkpoint")!.Value.AsString()));
        var plan = File.ReadAllText(JsonText.Parse(stdout).Member("plan")!.Value.AsString());
        Assert.Contains("lane-impl", plan, StringComparison.Ordinal);
        Assert.Contains("deep-impl", plan, StringComparison.Ordinal);
    }

    [Fact]
    public void AHeldTeamDoesNotReadClosedInTeamsSoTheSweepKeepsItsTrees()
    {
        ArrangeQuit("proj", live: false);
        var (code, stdout, stderr) = Roster(["quit", "--team", "proj", "--wait", "0"]);
        Assert.True(code == 0, stdout + stderr);
        Assert.Equal("held", QuitHold("proj").Member("phase")!.Value.AsString());

        var (teamsCode, teamsOut, teamsErr) = Roster(["teams"]);

        Assert.True(teamsCode == 0, teamsOut + teamsErr);
        var row = JsonText.Parse(teamsOut).Member("teams")!.Value.AsArray().Single(t => t.Member("name")!.Value.AsString() == "proj");
        Assert.NotEqual("closed", row.Member("life")!.Value.AsString());
    }

    [Fact]
    public void QuitAllOverTwoTeamsWithOneStillWindingDownExitsFour()
    {
        ArrangeQuit("one", "one-impl", "sess-one");
        ArrangeQuit("two", "two-impl", "sess-two", live: false);

        var (code, stdout, stderr) = Roster(["quit", "--all"]);

        Assert.True(code == 4, stdout + stderr);
        var teams = JsonText.Parse(stdout).Member("teams")!.Value.AsArray();
        Assert.Equal("winding-down", teams.Single(t => t.Member("team")!.Value.AsString() == "one").Member("phase")!.Value.AsString());
        Assert.Equal("held", teams.Single(t => t.Member("team")!.Value.AsString() == "two").Member("phase")!.Value.AsString());
    }

    [Fact]
    public void NegativeQuitAllWithATeamIsABadFlagAndABareQuitOverTwoTeamsNamesAll()
    {
        ArrangeQuit("one", "one-impl", "sess-one", live: false);
        ArrangeQuit("two", "two-impl", "sess-two", live: false);

        var (badCode, badOut, badErr) = Roster(["quit", "--all", "--team", "one"]);
        var (bareCode, bareOut, bareErr) = Roster(["quit"]);

        Assert.True(badCode != 0 && (badOut + badErr).Contains("bad_flags", StringComparison.Ordinal), badOut + badErr);
        Assert.True(bareCode != 0 && (bareOut + bareErr).Contains("--all", StringComparison.Ordinal), bareOut + bareErr);
        Assert.False(Held(QuitHoldPath("one")));
    }
}
