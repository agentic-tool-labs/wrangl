using Ah.Commands;
using Ah.Core.Json;
using Ah.Mesh;
using Ah.TestKit;

namespace Ah.Cli.Tests;

/// <summary><c>resume --hold</c> brings back the whole tree: it seals what a killed quit left, releases nested lanes, and relaunches parents first.</summary>
public sealed partial class RosterTeamVerbTests
{
    private string _treeHome = string.Empty;

    /// <summary>A team of one implementor with its own snapshot and, when given a hold kind, its own hold pinned to a record. Panes are gone, so the member is missing.</summary>
    private string TreeTeam(string team, string member, string? parent = null, string? pid = null, string? pidStart = null, string? kind = "quit", string phase = "held", string? cascade = null)
    {
        var tree = FamilyWorkTree("rc2");
        var row = MemberRow(tree).Replace("proj-implementor", member, StringComparison.Ordinal);
        var json = LiveTeamJson("tmux", row, pid, pidStart: pidStart);
        json = parent is null ? json : json.Replace("\"version\":1,", "\"version\":1,\"parent\":{\"team\":\"" + parent + "\",\"stream\":\"ws1\"},", StringComparison.Ordinal);
        _sandbox.PlantTeam(Path.Combine(_treeHome, "teams", team + ".json"), json);
        var (code, stdout, stderr) = Roster("snapshot", "--team", team, "--cwd", Project);
        Assert.True(code == 0, stdout + stderr);
        var snapshot = JsonText.Parse(stdout).Member("snapshot")!.Value.AsString();
        var record = WriteRecord(snapshot, session: null, team: team);
        var entry = JsonText.Parse(File.ReadAllText(record));
        var rewritten = JsonText.Stringify(entry, 2).Replace(Impl, member, StringComparison.Ordinal);
        File.WriteAllText(record, rewritten);
        if (kind is not null)
        {
            PutHold(TreeHold(team), JsonText.Stringify(new JsonObject
            {
                ["v"] = 1,
                ["team"] = team,
                ["kind"] = kind,
                ["phase"] = phase,
                ["since"] = "2026-11-04T00:00:00.000Z",
                ["checkpoint"] = record,
                ["cascade_from"] = cascade is null ? JsonValue.Null : cascade,
            }, 2));
        }

        return record;
    }

    private void ArrangeTree()
    {
        _treeHome = ResumeHome();
        _withPanes = true;
        _registrations = [];
        _panes.Default = PaneKnowledge.Absent;
    }

    private string TreeHold(string team) => Path.Combine(_treeHome, "holds", team, "hold.json");

    private (int Code, string Stdout, string Stderr) ResumeTree(string team = "proj", bool yes = true) => yes
        ? Roster("resume", "--hold", "--team", team, "--yes", "--cwd", Project, "--orchestrator-pid", Self)
        : Roster("resume", "--hold", "--team", team, "--cwd", Project, "--orchestrator-pid", Self);

    private int LaunchIndexOf(string member) =>
        _runner.Calls.Select((argv, i) => (argv, i)).First(c => string.Join(' ', c.argv).Contains(member, StringComparison.Ordinal)).i;

    [Fact]
    public void ATopAndANestedLaneAreAllReleasedAndRelaunchedParentsFirst()
    {
        ArrangeTree();
        TreeTeam("proj", "proj-implementor");
        // A-2: the lane's record names its dead lead, and the root's owner still launches into it
        TreeTeam("lane", "lane-implementor", parent: "proj", pid: DeadPid().ToString(System.Globalization.CultureInfo.InvariantCulture), pidStart: GoneStart, cascade: "proj");
        TreeTeam("deep", "deep-implementor", parent: "lane", pid: DeadPid().ToString(System.Globalization.CultureInfo.InvariantCulture), pidStart: GoneStart, cascade: "lane");
        var ownerBefore = JsonText.Parse(_sandbox.TeamText(Path.Combine(_treeHome, "teams", "proj.json"))).Member("orchestrator");

        var (code, stdout, stderr) = ResumeTree();

        Assert.True(code == 0, stdout + stderr);
        Assert.False(Held(TreeHold("lane")), "the lane was kept");
        Assert.False(Held(TreeHold("deep")), "the nested lane was kept");
        Assert.False(Held(TreeHold("proj")));
        Assert.Equal(2, JsonText.Parse(stdout).Member("lanes")!.Value.AsArray().Count);
        Assert.True(LaunchIndexOf("proj-implementor") < LaunchIndexOf("lane-implementor") && LaunchIndexOf("lane-implementor") < LaunchIndexOf("deep-implementor"));
        Assert.Equal(JsonText.Stringify(ownerBefore!.Value, 0), JsonText.Stringify(JsonText.Parse(_sandbox.TeamText(Path.Combine(_treeHome, "teams", "proj.json"))).Member("orchestrator")!.Value, 0));
    }

    [Fact]
    public void NegativeThePlanOfAHeldTreeWritesNothingAndSaysWhatItWouldSeal()
    {
        ArrangeTree();
        TreeTeam("proj", "proj-implementor", phase: "winding-down");
        var before = HoldBody(TreeHold("proj"));

        var (code, stdout, stderr) = ResumeTree(yes: false);

        Assert.True(code == 0, stdout + stderr);
        Assert.Equal("proj", JsonText.Parse(stdout).Member("would_seal")!.Value.AsArray().Single().AsString());
        Assert.Equal(before, HoldBody(TreeHold("proj")));
        Assert.DoesNotContain(_runner.Calls, argv => argv.Contains("new-window"));
    }

    [Fact]
    public void AQuitKilledMidWaitIsSealedWithStubsAndResumed()
    {
        ArrangeTree();
        TreeTeam("proj", "proj-implementor", phase: "winding-down");

        var (code, stdout, stderr) = ResumeTree();

        Assert.True(code == 0, stdout + stderr);
        Assert.False(Held(TreeHold("proj")));
        Assert.Contains(_runner.Calls, argv => argv.Contains("new-window"));
    }

    [Fact]
    public void NegativeALaneWithItsOwnStopIsKeptAndItsMembersAreNotRelaunched()
    {
        ArrangeTree();
        TreeTeam("proj", "proj-implementor");
        TreeTeam("lane", "lane-implementor", parent: "proj", kind: "stop", phase: "stopped");

        var (code, stdout, stderr) = ResumeTree();

        Assert.True(code == 0, stdout + stderr);
        Assert.True(Held(TreeHold("lane")));
        Assert.Contains("lane lane keeps", JsonText.Parse(stdout).Member("lanes_kept")!.Value.AsArray().Single().AsString(), StringComparison.Ordinal);
        Assert.DoesNotContain(_runner.Calls, argv => argv.Contains("new-window") && string.Join(' ', argv).Contains("lane-implementor", StringComparison.Ordinal));
    }

    [Fact]
    public void NegativeALaneWithAHeldAncestorRefusesAndNamesTheAncestor()
    {
        ArrangeTree();
        TreeTeam("proj", "proj-implementor");
        TreeTeam("lane", "lane-implementor", parent: "proj", cascade: "proj");

        var (code, stdout, stderr) = ResumeTree("lane");

        Assert.True(code != 0, stdout + stderr);
        Assert.Contains("resume_not_top", stdout, StringComparison.Ordinal);
        Assert.Contains("--team proj", stdout + stderr, StringComparison.Ordinal);
        Assert.True(Held(TreeHold("lane")));
    }

    [Fact]
    public void AKeptLaneIsReleasedByItsRootsOwnerAndRefusedToAnyoneElse()
    {
        ArrangeTree();
        TreeTeam("proj", "proj-implementor", kind: null, pid: DeadPid().ToString(System.Globalization.CultureInfo.InvariantCulture), pidStart: GoneStart);
        TreeTeam("lane", "lane-implementor", parent: "proj");

        var (refusedCode, refusedOut, refusedErr) = ResumeTree("lane");

        Assert.True(refusedCode != 0, refusedOut + refusedErr);
        Assert.Contains("resume_not_top", refusedOut, StringComparison.Ordinal);
        Assert.True(Held(TreeHold("lane")));

        TreeTeam("proj", "proj-implementor", kind: null);
        var (code, stdout, stderr) = ResumeTree("lane");

        Assert.True(code == 0, stdout + stderr);
        Assert.False(Held(TreeHold("lane")));
        Assert.True(LaunchIndexOf("lane-implementor") >= 0);
    }

    [Fact]
    public void AHeldTeamWhoseOrchestratorIsGoneIsTakenOverByTheResumingSession()
    {
        ArrangeTree();
        ObserverSeesAParent();
        TreeTeam("proj", "proj-implementor", pid: DeadPid().ToString(System.Globalization.CultureInfo.InvariantCulture), pidStart: GoneStart);

        var (code, stdout, stderr) = ResumeTree();

        Assert.True(code == 0, stdout + stderr);
        Assert.False(Held(TreeHold("proj")));
        Assert.NotEqual(DeadPid(), JsonText.Parse(_sandbox.TeamText(Path.Combine(_treeHome, "teams", "proj.json"))).Member("orchestrator")!.Value.Member("pid")!.Value.AsNumber());
    }

    [Fact]
    public void NegativeAHeldTeamWhoseOrchestratorCannotBeProvenGoneNeedsForce()
    {
        ArrangeTree();
        TreeTeam("proj", "proj-implementor", pid: LivePidOfAnotherProcess().ToString(System.Globalization.CultureInfo.InvariantCulture));
        var team = _sandbox.TeamText(Path.Combine(_treeHome, "teams", "proj.json"));

        var (code, stdout, stderr) = ResumeTree();

        Assert.True(code != 0, stdout + stderr);
        Assert.Contains("--force", stdout + stderr, StringComparison.Ordinal);
        Assert.True(Held(TreeHold("proj")));
        Assert.Equal(team, _sandbox.TeamText(Path.Combine(_treeHome, "teams", "proj.json")));
    }

    [Fact]
    public void NegativeAResumeFromAMemberShellIsRefused()
    {
        ArrangeTree();
        TreeTeam("proj", "proj-implementor");
        _nearest = new NearestSession(NearestKind.Member, "proj", "proj-implementor", Environment.ProcessId);

        var (code, stdout, stderr) = ResumeTree();

        Assert.True(code != 0, stdout + stderr);
        Assert.Contains("member-cannot-resume", stdout + stderr, StringComparison.Ordinal);
        Assert.True(Held(TreeHold("proj")));
    }

    [Fact]
    public void TeamResumedGoesOnlyToMembersWithNoBrief()
    {
        ArrangeTree();
        var mail = new FullMail();
        _eventMail = mail;
        TreeTeam("proj", "proj-implementor");

        var (code, stdout, stderr) = ResumeTree();

        Assert.True(code == 0, stdout + stderr);
        Assert.Contains("proj-implementor@proj", mail.Sent.Single(s => s.Event == "team-resumed").To);
    }

    [Fact]
    public void NegativeAMemberThatGotABriefIsNotAlsoToldTheTeamResumed()
    {
        var (request, _) = AnsweredCheckpointRequest();
        _withPanes = true;
        _callers ??= new RecordingCallers { StartText = SelfStart };
        var mail = new FullMail();
        _eventMail = mail;
        Registered(ImplSession, live: true);
        _ = HoldOn(Hierarchy, WriteRecord(null, request: request, open: ["open-1"]));

        var (code, stdout, stderr) = ResumeHeld();

        Assert.True(code == 0, stdout + stderr);
        Assert.Equal(Impl, Assert.Single(_sent).Member);
        Assert.DoesNotContain(mail.Sent, s => s.Event == "team-resumed");
    }

    [Fact]
    public void ABareMemberWithRecoveryOffExitsThreeAndNamesTheLineThatTurnsItOn()
    {
        _callers = new RecordingCallers { StartText = SelfStart };
        _withPanes = true;
        WriteTeam("proj", LiveTeamJson("tmux", OneImplementor.TrimEnd('}') + ",\"bare\":" + MarkOf(Environment.ProcessId, SelfStart) + "}"));
        ScriptCheckpointTrees();
        _panes.Default = PaneKnowledge.Present;
        Registered(ImplSession, live: true);
        _runner.Ps = argv => SelfStartOnly(argv) ?? (argv.Contains("command=") && argv.Contains(Self) ? "claude --resume s1\n" : null);
        _ = HoldOn(Hierarchy, WriteRecord(null));
        TestHomeDocs.Put(_sandbox.AhHome, Ah.Watch.WatchModule.RecoverySwitchKey, "{\"on\":false}");

        var (code, stdout, stderr) = ResumeHeld();

        Assert.True(code == 3, stdout + stderr);
        var result = JsonText.Parse(stdout);
        Assert.Equal(Impl, result.Member("bare_waiting")!.Value.AsArray().Single().AsString());
        Assert.Equal("ah roster recover enable", result.Member("next")!.Value.AsString());
        Assert.Empty(_sent);
    }

    [Fact]
    public void TheDaemonsReBriefSendsThatMemberItsBriefAndAFailedSendSaysWhy()
    {
        ArrangeCheckpoint();
        _panes.Default = PaneKnowledge.Present;
        Activity("sess-impl.json", "working", DateTimeOffset.UtcNow);
        var taken = Take();
        _ = CheckpointAnswer(taken, $"Checkpoint: ready branch=ah/feature sha={Sha} inflight=none");
        _sent.Clear();
        var path = taken.Member("record")!.Value.AsString();
        TestCheckpoints.Write(_sandbox.AhHome, path, System.Text.RegularExpressions.Regex.Replace(TestCheckpoints.Read(_sandbox.AhHome, path),"\"open_requests\":\\s*\\[\\s*\\]", "\"open_requests\":[\"open-1\"]"));
        var relaunch = CommandsModule.CreateMemberRelaunch(ComposeServices());

        Assert.Null(relaunch.ReBrief(Hierarchy, Project, "proj", Impl));
        Assert.Equal(Impl, Assert.Single(_sent).Member);

        _briefFails = true;
        Assert.Contains("mail_error", relaunch.ReBrief(Hierarchy, Project, "proj", Impl), StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeARecoverLeavesARowWithAMatchedBareMarkAlone()
    {
        _callers = new RecordingCallers { StartText = SelfStart };
        var live = LivePidOfAnotherProcess();
        var row = MemberRow("bot", "implementor", DeadPid(), "p1", "old-session").TrimEnd('}')
            + ",\"bare\":{\"state\":\"bare\",\"pid\":" + live.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"session_id\":\"new-session\",\"since\":\"2026-11-04T00:00:00.000Z\"}}";
        RecoverTeam(Environment.ProcessId, row);
        _liveSessions = [new LiveSession("new-session", live, RecoverStart, "p1", [])];

        var (code, stdout, stderr) = Recover();

        Assert.True(code == 0, stdout + stderr);
        Assert.Equal("old-session", RowOf("bot").Member("session_id")!.Value.AsString());
    }
}
