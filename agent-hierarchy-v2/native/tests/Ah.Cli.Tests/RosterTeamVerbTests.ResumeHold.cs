using Ah.Commands;
using Ah.Core.Json;

namespace Ah.Cli.Tests;

/// <summary><c>roster resume --hold</c> after a reboot: the hold goes only when the relaunch can start, and a new session is told how to take the team over.</summary>
public sealed partial class RosterTeamVerbTests
{
    /// <summary>Holds the team in the holds table, or, with <paramref name="asFile"/>, plants the hold file a home from before the table had; returns the hold's key (its old path).</summary>
    private string HoldOn(string home, string record, string team = "proj", bool asFile = false)
    {
        var path = Path.Combine(home, "holds", team, "hold.json");
        var body = JsonText.Stringify(new JsonObject
        {
            ["v"] = 1,
            ["team"] = team,
            ["kind"] = "quit",
            ["phase"] = "held",
            ["since"] = "2026-11-04T00:00:00.000Z",
            ["checkpoint"] = record,
        }, 2);
        if (asFile)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, body);

            // the upgrade's import pass takes the old file (files-off-disk §4: a read no longer does); an earlier compose's pass took the home with no file yet
            _ = Ah.StateDb.StateDbModule.OpenDirect(_sandbox.AhHome).Write(tx =>
            {
                tx.Execute("DELETE FROM meta WHERE key = ?1", "holds.taken:" + Ah.Core.Text.PathGuard.RealPath(home, strict: false));
                return 0;
            });
            _ = Ah.StateDb.StateDbModule.ImportHierDirFiles(_sandbox.AhHome, home, force: true);
        }
        else
        {
            PutHold(path, body);
        }

        return path;
    }

    // per-dir-files-to-db S11a-holds: a hold is a row of the holds table; a test names it by the path its file had
    private Ah.Abstractions.IHoldSource HoldRows => Ah.StateDb.StateDbModule.CreateHolds(_sandbox.AhHome);

    private static (string Home, string Team) HoldKey(string path) =>
        (Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(path)))!, Path.GetFileName(Path.GetDirectoryName(path))!);

    private string? HoldBody(string path) => HoldKey(path) is var (home, team) ? HoldRows.Read(home, team).Body : null;

    private bool Held(string path) => HoldBody(path) is not null;

    private void PutHold(string path, string body)
    {
        var (home, team) = HoldKey(path);
        HoldRows.Change(home, team, _ => new Ah.Abstractions.HoldWrite(body));
    }

    private (int Code, string Stdout, string Stderr) ResumeHeld() =>
        Roster("resume", "--hold", "--team", "proj", "--yes", "--cwd", Project, "--orchestrator-pid", Self);

    /// <summary>The real processes, with this test host shown as another user's: alive to kill-0 (EPERM), never owned.</summary>
    private sealed class SelfNotOwned(Ah.Abstractions.IProcessInspector real) : Ah.Abstractions.IProcessInspector
    {
        public bool PidAlive(int pid) => real.PidAlive(pid);

        public bool PidOwned(int pid) => pid != Environment.ProcessId && real.PidOwned(pid);

        public string? CommandLine(int pid) => real.CommandLine(pid);
    }

    /// <summary>The real start times with no ancestry, so no caller walk reaches past the sandbox to this machine's real sessions.</summary>
    private sealed class StartsOnly(Ah.Abstractions.IProcessTable real) : Ah.Abstractions.IProcessTable
    {
        public IReadOnlyList<int>? Ancestors(int pid) => null;

        public bool? HasAncestor(int pid, int candidate) => null;

        public string? StartTime(long pid) => real.StartTime(pid);
    }

    // one-liveness.md site 3: a process left running at the stop that is now another user's is gone at the assessment; the table's
    // start read it alive before. Owned, with its recorded start, it is still running.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void AStopAssessmentReportsAnotherUsersProcessAsGone(bool foreign)
    {
        ArrangeTree();
        _registrations = null;
        TreeTeam("proj", Impl, kind: null);
        var home = _treeHome;
        var table = new StartsOnly(Ah.Platform.PlatformModule.CreateProcessTable(Ah.Platform.PlatformModule.CreateProcessRunner()));
        _callers = new RecordingCallers { StartText = SelfStart, ProcessTable = table };
        _realPidRunning = true;
        var processes = Ah.Platform.PlatformModule.CreateIo(null).Processes;
        _seedGraph = graph => graph.Hierarchy.Core.Liveness = Ah.Mesh.MeshModule.CreateMemberLiveness(graph.Mailbox, table, foreign ? new SelfNotOwned(processes) : processes);
        var pid = Environment.ProcessId;
        var evidence = Path.Combine(home, "stop-evidence.json");
        File.WriteAllText(evidence, JsonText.Stringify(new JsonObject
        {
            ["members"] = new JsonArray { new JsonObject { ["name"] = Impl, ["team"] = "proj" } },
            ["still_running"] = new JsonArray { new JsonObject { ["team"] = "proj", ["member"] = Impl, ["pid"] = pid, ["pid_start"] = table.StartTime(pid)!, ["command"] = "x" } },
        }, 2));
        var hold = Path.Combine(home, "holds", "proj", "hold.json");
        PutHold(hold, JsonText.Stringify(new JsonObject { ["v"] = 1, ["team"] = "proj", ["kind"] = "stop", ["phase"] = "stopped", ["since"] = "2026-11-04T00:00:00.000Z", ["evidence"] = evidence }, 2));

        var (code, stdout, stderr) = Roster("resume", "--hold", "--team", "proj", "--cwd", Project, "--orchestrator-pid", Self);

        Assert.True(code == 0, stdout + stderr);
        var assessment = File.ReadAllText(JsonText.Parse(stdout).Member("assessment")!.Value.AsString());
        Assert.Contains($"(pid {pid}), now {(foreign ? "gone" : "alive")}", assessment, StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeAHeldResumeWhoseRecordNamesNoSnapshotRefusesAndTheTeamStaysHeld()
    {
        var home = ResumeHome();
        _withPanes = true;
        _sandbox.PlantTeam(Path.Combine(home, "teams","proj.json"), LiveTeamJson("tmux", OneImplementor));
        ScriptCheckpointTrees();
        _panes.Default = PaneKnowledge.Absent;
        Registered(ImplSession, live: false);
        var hold = HoldOn(home, WriteRecord(null));

        var (code, stdout, stderr) = ResumeHeld();

        Assert.True(code != 0, stdout + stderr);
        Assert.Contains("names no snapshot", stdout + stderr, StringComparison.Ordinal);
        Assert.True(Held(hold), "the hold was released although nothing could be relaunched");
        Assert.DoesNotContain(_runner.Calls, argv => argv.Contains("new-window"));
    }

    [Fact]
    public void AHeldResumeWithAUsableSnapshotReleasesTheHoldAndRelaunches()
    {
        var home = ResumeHome();
        _withPanes = true;
        var snapshot = TakeSnapshot(home, FamilyWorkTree("rc2"), "log");
        _registrations = [];
        _panes.Default = PaneKnowledge.Absent;
        var hold = HoldOn(home, WriteRecord(snapshot));

        var (code, stdout, stderr) = ResumeHeld();

        Assert.True(code == 0, stdout + stderr);
        Assert.False(Held(hold));
        var result = JsonText.Parse(stdout);
        Assert.Single(result.Member("hold_archived")!.Value.AsArray());
        Assert.Equal("relaunched", StatusOfMember(result));
        Assert.Null(result.Member("members")!.Value.AsArray().Single().Member("next"));
        Assert.Contains(_runner.Calls, argv => argv.Contains("new-window"));
    }

    // per-dir-files-to-db S11a-ckpt: a hold taken before the upgrade names its record's file path; after it the record is the row with that key
    [Fact]
    public void AHoldFromBeforeTheUpgradeStaysHeldAndResumesFromItsPinnedRecord()
    {
        var home = ResumeHome();
        _withPanes = true;
        var snapshot = TakeSnapshot(home, FamilyWorkTree("rc2"), "log");
        _registrations = [];
        _panes.Default = PaneKnowledge.Absent;
        var old = Path.Combine(home, "checkpoints", "proj", "20261104T000000000Z.json");
        Directory.CreateDirectory(Path.GetDirectoryName(old)!);
        File.Move(WriteRecord(snapshot), old);
        var hold = HoldOn(home, old, asFile: true);

        var (held, heldOut, heldErr) = Roster("resume", "--cwd", Project, "--team", "proj", "--orchestrator-pid", Self);
        Assert.True(held != 0 && Held(hold), "the team was not held after the upgrade");
        Assert.Contains("held", heldOut + heldErr, StringComparison.Ordinal);

        var (code, stdout, stderr) = ResumeHeld();

        Assert.True(code == 0, stdout + stderr);
        Assert.False(Held(hold));
        Assert.Equal("relaunched", StatusOfMember(JsonText.Parse(stdout)));
    }

    [Fact]
    public void AResumeFromAQuitQueuesAgainTheMailTheHoldDrained()
    {
        var home = ResumeHome();
        _withPanes = true;
        _exposeMailbox = true;
        var snapshot = TakeSnapshot(home, FamilyWorkTree("rc2"), "log");
        _registrations = [];
        _panes.Default = PaneKnowledge.Absent;
        _ = HoldOn(home, WriteRecord(snapshot));
        string? box = null;
        Ah.Mesh.IMailbox? mailbox = null;
        _seedGraph = graph =>
        {
            box = graph.Mailbox.BoxDirFor(graph.Roster.TeamPath(home, "proj"), Impl);
            mailbox = graph.Mailbox;
            Ah.TestKit.TestPlant.Mail(graph.Writer, box, JsonText.Parse("{\"id\":\"m1\",\"kind\":\"note\",\"from\":\"user\",\"to\":\"" + Impl + "\",\"text\":\"hello\",\"created\":\"2026-11-04T00:00:00.000Z\",\"route\":\"pull\"}"), Project);
        };

        var (code, stdout, stderr) = ResumeHeld();

        Assert.True(code == 0, stdout + stderr);
        Assert.Equal(1, (int)JsonText.Parse(stdout).Member("redelivered")!.Value.AsNumber());
        Assert.Contains(mailbox!.ReadEntries(box!).Entries, e => e.Member("id")?.AsString() == "m1-held");
    }

    [Fact]
    public void AHeldResumeNamesTheCommandForEveryOtherHeldTopTeamButNotALane()
    {
        var home = ResumeHome();
        _withPanes = true;
        _sandbox.PlantTeam(Path.Combine(home, "teams","proj.json"), LiveTeamJson("tmux", OneImplementor));
        ScriptCheckpointTrees();
        _ = HoldOn(home, WriteRecord(null));
        _sandbox.PlantTeam(Path.Combine(home, "teams","other.json"), LiveTeamJson("tmux", OneImplementor));
        _ = HoldOn(home, WriteRecord(null), "other");
        _sandbox.PlantTeam(Path.Combine(home, "teams","lane.json"), LiveTeamJson("tmux", OneImplementor).TrimEnd('}') + ",\"parent\":{\"team\":\"proj\",\"stream\":\"s\"}}");
        _ = HoldOn(home, WriteRecord(null), "lane");

        var (code, stdout, stderr) = Roster("resume", "--hold", "--team", "proj", "--cwd", Project, "--orchestrator-pid", Self);

        Assert.True(code == 0, stdout + stderr);
        var other = Assert.Single(JsonText.Parse(stdout).Member("other_held_teams")!.Value.AsArray());
        Assert.Equal("other", other.Member("team")!.Value.AsString());
        Assert.Contains("ah roster resume --hold --team other", other.Member("next")!.Value.AsString(), StringComparison.Ordinal);
    }

    private string OldestOfElevenSnapshots(string home)
    {
        var dir = Path.Combine(home, "snapshots");
        for (var i = 10; i >= 0; i--)
        {
            PutSnapshot(Path.Combine(dir, $"proj-20260101T1200001{i:00}Z.json"), "{}");
        }

        return Path.Combine(dir, "proj-20260101T120000100Z.json");
    }

    private bool SnapshotRowExists(string snapshot) => SnapshotRows.ReadSnapshot(SnapshotHomeOf(snapshot), Path.GetFileName(snapshot)) is not null;

    [Fact]
    public void ASnapshotAHoldNamesIsNotPruned()
    {
        var home = ResumeHome();
        var worktree = FamilyWorkTree("rc2");
        var pinned = OldestOfElevenSnapshots(home);
        _ = HoldOn(home, WriteRecord(pinned));

        _ = TakeSnapshot(home, worktree, "log");

        Assert.True(SnapshotRowExists(pinned));
        Assert.False(SnapshotRowExists(Path.Combine(home, "snapshots", "proj-20260101T120000101Z.json")));
    }

    [Fact]
    public void NegativeWithNoHoldTheOldestSnapshotIsPruned()
    {
        var home = ResumeHome();
        var worktree = FamilyWorkTree("rc2");
        var oldest = OldestOfElevenSnapshots(home);

        _ = TakeSnapshot(home, worktree, "log");

        Assert.False(SnapshotRowExists(oldest));
    }

    /// <summary>files-off-disk F8b: a snapshot is a row (no snapshots/ dir) and resumes; an old file with no row is listed and resumes too, and is never deleted.</summary>
    [Fact]
    public void ASnapshotIsARowAndAnOldSnapshotFileStillResumes()
    {
        var home = ResumeHome();
        var worktree = FamilyWorkTree("rc2");
        var snapshot = TakeSnapshot(home, worktree, "log");
        Assert.False(Directory.Exists(Path.Combine(home, "snapshots")));
        DismissEverything(home);
        var (code, _, text) = ResumeYes(snapshot);
        Assert.True(code == 0, text);

        var old = Path.Combine(home, "snapshots", "proj-29990101T000000000Z.json");
        Directory.CreateDirectory(Path.GetDirectoryName(old)!);
        File.WriteAllText(old, SnapshotText(snapshot));
        DismissEverything(home);
        var (listed, plan, planErr) = Roster("resume", "--team", "proj", "--cwd", Project);
        Assert.True(listed == 0, plan + planErr);
        Assert.Contains(old, plan, StringComparison.Ordinal);
        var (resumed, _, resumedText) = ResumeYes(old);
        Assert.True(resumed == 0, resumedText);
        Assert.True(File.Exists(old));
    }

    [Fact]
    public void NegativeAHeldTeamWhoseOrchestratorIsLiveIsNotOfferedAdopt()
    {
        _callers = new RecordingCallers { StartText = SelfStart };
        var holder = LivePidOfAnotherProcess();
        const string HolderStart = "2026-10-15T21:00:00.000Z";
        _runner.Ps = argv => argv.Contains("lstart=") && argv.Contains(holder.ToString(System.Globalization.CultureInfo.InvariantCulture)) ? HolderStart + "\n" : SelfStartOnly(argv);
        OwnedTeamFile(holder, "sess-previous", HolderStart);

        var (code, stdout, stderr) = ResumeHeld();

        Assert.True(code != 0, stdout + stderr);
        Assert.Contains("resume_not_top", stdout + stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("adopt", stdout + stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeALaneWhoseOwnerIsGoneIsNotToldToAdoptForAHeldResume()
    {
        _callers = new RecordingCallers { StartText = SelfStart };
        ObserverSeesAParent();
        WriteTeam("proj", "{\"version\":1,\"team_id\":\"t-1\",\"transport\":\"terminal\",\"roster_level\":\"repo\",\"orchestrator\":{\"pid\":" + DeadPid() + ",\"pid_start\":\"" + GoneStart + "\"},\"parent\":{\"team\":\"top\",\"stream\":\"s\"},\"members\":[]}");

        var (code, stdout, stderr) = ResumeHeld();

        Assert.True(code != 0, stdout + stderr);
        Assert.Contains("resume_not_top", stdout + stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("adopt", stdout + stderr, StringComparison.Ordinal);
    }
}
