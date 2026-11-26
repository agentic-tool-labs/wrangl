using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Core.Json;
using Ah.Platform;
using Ah.Roster;
using Ah.TestKit;
using Ah.Watch;

namespace Ah.Cli.Tests;

/// <summary>The watch host ends a job whose process is gone and finds the process of one that has none.</summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class WatchJobReconcileTests : IDisposable
{
    private const int Dead = 2_000_000_000;

    private readonly Sandbox _sandbox = new();

    public void Dispose() => _sandbox.Dispose();

    private string Hier => _sandbox.Home + "/hier";

    /// <summary>
    /// The owning session's pid. The reconciler asks the real process table whether it is alive, so it is this test process,
    /// alive by construction; a fixed number passed or failed by whether some unrelated process held it.
    /// </summary>
    private static readonly int Owner = Environment.ProcessId;

    private Action? _onWalk;

    private Func<int, IReadOnlyList<ProcessNode>?>? _group;

    private sealed class Tree(IReadOnlyList<ProcessNode> below, Func<Action?> onWalk, Func<int, IReadOnlyList<ProcessNode>?> inGroup) : IProcessTable
    {
        public IReadOnlyList<int>? Ancestors(int pid) => null;

        public bool? HasAncestor(int pid, int candidate) => null;

        public IReadOnlyList<ProcessNode>? InGroup(int pgid) => inGroup(pgid);

        public string? StartTime(long pid) => "Wed Oct 21 08:00:05 2026";

        public IReadOnlyList<ProcessNode>? Descendants(int pid)
        {
            onWalk()?.Invoke();
            return pid == Owner ? below : [];
        }
    }

    private sealed class Host(ServiceGraph graph, IClock clock, IProcessTable tree) : WatchHostAdapter(graph, RosterModule.CreateScreenRules(), clock, WatchHostSeams.Unavailable)
    {
        internal override IProcessTable ProcessTree => tree;
    }

    private (Host Host, ServiceGraph Graph) Make(params ProcessNode[] below)
    {
        var variables = new Dictionary<string, string?> { ["AH_TEST"] = "1", ["AH_HOME"] = _sandbox.AhHome, ["HOME"] = _sandbox.Home };
        var env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables, new PluginInfo(_sandbox.PluginRoot, "wrangl", "1.0.0", "1"));
        var io = PlatformModule.CreateIo(null);
        var graph = ServiceGraph.Build(io, env, new FakeRunner(), new FakeDaemonClient());
        _graph = graph;
        _ = Directory.CreateDirectory(Hier);
        graph.Hierarchy.Status.AppendSessionPid(Hier, JsonValue.FromString("s-a"), JsonValue.FromNumber(Owner));
        return (new Host(graph, io.Clock, new Tree(below, () => _onWalk, group => _group?.Invoke(group))), graph);
    }

    private string Put(ServiceGraph graph, string id, int? pid, string label = "dotnet test Core.csproj")
    {
        var path = Path.Combine(Hier, "jobs", "tm", id + ".json");
        var spec = new JobSpec("test", "large", label, false);
        var record = WatchModule.JobNewRecord(id, "tm", "impl", "s-a", spec, pid, "2026-10-21T08:00:00.000Z", null, null);
        Store(graph,path, record);
        return path;
    }

    private IReadOnlyList<JobFact> Read(ServiceGraph graph) => WatchModule.JobsFromFiles(graph.Hierarchy.Status, Hier, "tm");

    // per-dir-files-to-db S8: a test names a job by its old file path (<hier>/jobs/<team>/<id>.json); the record is the jobs row it keys
    private ServiceGraph? _graph;

    private void Store(ServiceGraph graph, string path, JsonObject record) =>
        graph.Hierarchy.StateCalls.PutJob(Hier, Path.GetFileName(Path.GetDirectoryName(path)!), Path.GetFileNameWithoutExtension(path), record);

    private JsonObject? Rec(string path) =>
        WatchModule.JobRecordFiles(_graph!.Hierarchy.Status, Path.GetDirectoryName(path)!).FirstOrDefault(f => f.Id == Path.GetFileNameWithoutExtension(path))?.Record;

    private bool Has(string path) => Rec(path) is not null;

    [Fact]
    public void AJobWhoseProcessIsGoneIsEndedInItsFileAndDropsOutAndOneWhoseProcessLivesStays()
    {
        var (host, graph) = Make();
        var deadPath = Put(graph, "dead", Dead);
        _ = Put(graph, "alive", Environment.ProcessId);

        var kept = host.ReconcileJobs("s-a", Hier, "tm", Read(graph));

        Assert.Equal(["alive"], kept.Select(static j => j.Id));
        Assert.True(Rec(deadPath)!.Get("ended_at") is { IsText: true });
        Assert.False(Read(graph).Single(static j => j.Id == "alive").Ended);
    }

    [Fact]
    public void TheJobsSliceProbeListsTheRecordsWithTheirThresholdAndEndTime()
    {
        var (_, graph) = Make();
        _ = Put(graph, "open1", null);
        var endedPath = Put(graph, "ended1", null);
        Store(graph,endedPath, WatchModule.JobEnded(Rec(endedPath)!, "2026-10-21T08:30:00.000Z", null));

        var rows = Ah.Cli.Composition.JobRows.Of(graph, Hier, "tm");

        Assert.Equal(["ended1", "open1"], rows.Select(static r => r.Id).Order());
        Assert.All(rows, static r => Assert.True(r.ThresholdSeconds > 0));
        Assert.Null(rows.Single(static r => r.Id == "open1").EndedAtMs);
        Assert.NotNull(rows.Single(static r => r.Id == "ended1").EndedAtMs);
    }

    private void ChildTeam(ServiceGraph graph, string name, string parent, string stream, JsonObject orchestrator, JsonObject? workstream = null)
    {
        var record = new JsonObject
        {
            ["members"] = new JsonArray(),
            ["parent"] = new JsonObject { ["team"] = parent, ["stream"] = stream },
            ["orchestrator"] = orchestrator,
        };
        if (workstream is not null)
        {
            record["workstream"] = workstream;
        }

        WriteTeam(graph, name, record);
    }

    private void WriteTeam(ServiceGraph graph, string name, JsonObject record)
    {
        Ah.TestKit.TestPlant.Team(graph.Roster, graph.Writer, graph.Roster.TeamPath(Hier, name), record);
    }

    private void Manager(ServiceGraph graph, params string[] streams)
    {
        var map = new JsonObject();
        foreach (var stream in streams)
        {
            map[stream] = new JsonObject { ["sub_orchestrator"] = "so-" + stream };
        }

        WriteTeam(graph, "tm", new JsonObject
        {
            ["members"] = new JsonArray(),
            ["orchestrator"] = new JsonObject { ["session_id"] = "s-a" },
            ["streams"] = map,
        });
    }

    private sealed class Verdict(Ah.Core.Time.LivenessVerdict verdict) : Ah.Hierarchy.IMemberLiveness
    {
        public Ah.Hierarchy.MemberRegistrationLiveness? ForMember(string memberName, string team) => null;

        public bool PidIdentityLive(int pid, string? recordedStart) => verdict == Ah.Core.Time.LivenessVerdict.Live;

        public Ah.Core.Time.LivenessVerdict PidVerdict(int pid, string? recordedStart) => verdict;
    }

    [Theory]
    [InlineData(Ah.Core.Time.LivenessVerdict.Unknown, false)]
    [InlineData(Ah.Core.Time.LivenessVerdict.Live, false)]
    [InlineData(Ah.Core.Time.LivenessVerdict.ProvenDead, true)]
    public void ALaneLeadIsDeadOnlyWhenItsDeathIsProven(Ah.Core.Time.LivenessVerdict verdict, bool dead)
    {
        var (host, graph) = Make();
        graph.Hierarchy.Core.Liveness = new Verdict(verdict);
        Manager(graph, "s1");
        ChildTeam(graph, "tm-s1", "tm", "s1", new JsonObject { ["session_id"] = "so-s", ["pid"] = Dead, ["pid_start"] = "x" });

        var lane = Assert.Single(host.Lanes("s-a", Hier, [new OwnedTeam("tm", new JsonObject(), [])]));

        Assert.Equal(dead, lane.Dead);
    }

    // Rows 1-4 of the one-liveness-truth table, over the real judge; OneLivenessTableTests runs the same evidence through the other paths.
    [Theory]
    [InlineData(101, EvidenceProcesses.Start, false)]
    [InlineData(102, EvidenceProcesses.Start, true)]
    [InlineData(103, EvidenceProcesses.Start, true)]
    [InlineData(104, null, false)]
    public void ALaneLeadOverRealEvidenceIsDeadOnlyWhenProven(int pid, string? start, bool dead)
    {
        var (host, graph) = Make();
        var processes = new EvidenceProcesses();
        graph.Hierarchy.Core.Liveness = Ah.Mesh.MeshModule.CreateMemberLiveness(graph.Mailbox, processes, processes);
        var teamFile = graph.Roster.TeamPath(Hier, "tm");
        Assert.True(graph.Mailbox.WriteRegistration(new Ah.Mesh.RegistrationWrite("s-ref", [new Ah.Mesh.BoxRef("ref@tm", graph.Mailbox.BoxDirFor(teamFile, "ref"))], teamFile, "claude", EvidenceProcesses.Reference, null, _sandbox.Home, EvidenceProcesses.Start)));
        Manager(graph, "s1");
        var orchestrator = new JsonObject { ["session_id"] = "so-s", ["pid"] = pid };
        if (start is not null)
        {
            orchestrator["pid_start"] = start;
        }

        ChildTeam(graph, "tm-s1", "tm", "s1", orchestrator);

        Assert.Equal(dead, Assert.Single(host.Lanes("s-a", Hier, [new OwnedTeam("tm", new JsonObject(), [])])).Dead);
    }

    [Fact]
    public void TheFailsafeOwesAnEscalationOnlyForAConfirmedLaneInAnActiveStateWithARecord()
    {
        var (_, graph) = Make();
        Manager(graph, "s1", "s2", "s3", "s4", "s5");
        JsonObject Workstream(string state) => new() { ["state"] = state, ["updated"] = "2026-10-21T08:00:00.000Z" };
        ChildTeam(graph, "tm-s1", "tm", "s1", new JsonObject { ["session_id"] = "so-1" }, Workstream("working"));
        ChildTeam(graph, "tm-s2", "tm", "s2", new JsonObject { ["session_id"] = "so-2" }, Workstream("paused"));
        ChildTeam(graph, "tm-s3", "tm", "s3", new JsonObject { ["session_id"] = "so-3" }, Workstream("done"));
        ChildTeam(graph, "tm-s4", "tm", "s4", new JsonObject { ["session_id"] = "so-4" });
        ChildTeam(graph, "tm-s5", "tm", "s9", new JsonObject { ["session_id"] = "so-5" }, Workstream("working"));

        var owed = new WakeComposition(graph, graph.Environment).LaneStalls(Hier, "tm", "s-a");

        var item = Assert.Single(owed);
        Assert.Equal("lane:tm/s1", item.RequestId);
        Assert.Equal("so-s1", item.SubOrchestrator);
        Assert.Null(item.Charter);
    }

    [Fact]
    public void ALaneLeadWithNoPidIsNeverDeadWhateverTheIdentityCheckSays()
    {
        var (host, graph) = Make();
        graph.Hierarchy.Core.Liveness = new Verdict(Ah.Core.Time.LivenessVerdict.ProvenDead);
        Manager(graph, "s1");
        ChildTeam(graph, "tm-s1", "tm", "s1", new JsonObject { ["session_id"] = "so-s" });

        Assert.False(Assert.Single(host.Lanes("s-a", Hier, [new OwnedTeam("tm", new JsonObject(), [])])).Dead);
    }

    // An unproven pid is unknown, so with an overdue job it is blocked and not dead.
    [Theory]
    [InlineData(Ah.Core.Time.LivenessVerdict.Live, true)]
    [InlineData(Ah.Core.Time.LivenessVerdict.Unknown, true)]
    [InlineData(Ah.Core.Time.LivenessVerdict.ProvenDead, false)]
    public void ALaneLeadIsBlockedOnAnOverdueJobUnlessItsDeathIsProven(Ah.Core.Time.LivenessVerdict verdict, bool blocked)
    {
        var (host, graph) = Make();
        graph.Hierarchy.Core.Liveness = new Verdict(verdict);
        Manager(graph, "s2");
        ChildTeam(graph, "tm-s2", "tm", "s2", new JsonObject { ["session_id"] = "so-2", ["pid"] = Dead, ["pid_start"] = "x" });
        var late = WatchModule.JobNewRecord("20200101-000000-late", "tm-s2", "orchestrator", "so-2", new JobSpec("test", "small", "dotnet test", false), null, "2020-01-01T00:00:00.000Z", null, null);
        Store(graph,Path.Combine(Hier, "jobs", "tm-s2", "20200101-000000-late.json"), late);

        var lane = Assert.Single(host.Lanes("s-a", Hier, [new OwnedTeam("tm", new JsonObject(), [])]));

        Assert.Equal(blocked, lane.BlockedOnJob);
        Assert.Equal(!blocked, lane.Dead);
    }

    [Fact]
    public void AnUnknownLaneLeadWithNothingOverdueIsNeitherDeadNorBlocked()
    {
        var (host, graph) = Make();
        graph.Hierarchy.Core.Liveness = new Verdict(Ah.Core.Time.LivenessVerdict.Unknown);
        Manager(graph, "s2");
        ChildTeam(graph, "tm-s2", "tm", "s2", new JsonObject { ["session_id"] = "so-2", ["pid"] = Dead, ["pid_start"] = "x" });

        var lane = Assert.Single(host.Lanes("s-a", Hier, [new OwnedTeam("tm", new JsonObject(), [])]));

        Assert.False(lane.BlockedOnJob);
        Assert.False(lane.Dead);
    }

    [Fact]
    public void ALaneLeadWithNoPidIsNotBlockedOnAnOverdueJobBecauseItIsNotKnownToBeLive()
    {
        var (host, graph) = Make();
        graph.Hierarchy.Core.Liveness = new Verdict(Ah.Core.Time.LivenessVerdict.Live);
        Manager(graph, "s2");
        ChildTeam(graph, "tm-s2", "tm", "s2", new JsonObject { ["session_id"] = "so-2" });
        var late = WatchModule.JobNewRecord("20200101-000000-late", "tm-s2", "orchestrator", "so-2", new JobSpec("test", "small", "dotnet test", false), null, "2020-01-01T00:00:00.000Z", null, null);
        Store(graph,Path.Combine(Hier, "jobs", "tm-s2", "20200101-000000-late.json"), late);

        var lane = Assert.Single(host.Lanes("s-a", Hier, [new OwnedTeam("tm", new JsonObject(), [])]));

        Assert.False(lane.BlockedOnJob);
        Assert.False(lane.Dead);
        Assert.Equal("20200101-000000-late", lane.JobAnchor);
    }

    [Fact]
    public void TheHostListsOnlyTheConfirmedLanesOfTheTeamsASessionOrchestratesWithTheirDeathStreamStateAndOverdueJob()
    {
        var (host, graph) = Make();
        graph.Hierarchy.Core.Liveness = new Verdict(Ah.Core.Time.LivenessVerdict.ProvenDead);
        var owned = new[] { new OwnedTeam("tm", new JsonObject(), []) };
        Manager(graph, "s1", "s2");
        ChildTeam(graph, "tm-s1", "tm", "s1", new JsonObject { ["session_id"] = "so-s", ["pid"] = Dead, ["pid_start"] = "x" }, new JsonObject { ["state"] = "working", ["updated"] = "2026-10-21T08:00:00.000Z" });
        ChildTeam(graph, "tm-s2", "tm", "s2", new JsonObject { ["session_id"] = "so-2" });
        ChildTeam(graph, "forged", "tm", "s9", new JsonObject { ["session_id"] = "so-3", ["pid"] = Dead });
        ChildTeam(graph, "tm-s3", "tm", "s3", new JsonObject { ["session_id"] = "so-4", ["pid"] = Dead });
        ChildTeam(graph, "tm-s2-copy", "tm", "s2", new JsonObject { ["session_id"] = "so-5", ["pid"] = Dead });
        var late = WatchModule.JobNewRecord("20200101-000000-late", "tm-s2", "orchestrator", "so-2", new JobSpec("test", "small", "dotnet test", false), null, "2020-01-01T00:00:00.000Z", null, null);
        Store(graph,Path.Combine(Hier, "jobs", "tm-s2", "20200101-000000-late.json"), late);

        var lanes = host.Lanes("s-a", Hier, owned).ToDictionary(static l => l.Lane);

        Assert.Equal(["tm-s1", "tm-s2"], lanes.Keys.Order());
        Assert.True(lanes["tm-s1"].Dead);
        Assert.Equal("so-s1", lanes["tm-s1"].SubOrchestrator);
        Assert.Equal("working", lanes["tm-s1"].StreamState);
        Assert.NotNull(lanes["tm-s1"].StreamUpdatedMs);
        Assert.Equal("tm", lanes["tm-s1"].ParentTeam);
        Assert.False(lanes["tm-s1"].BlockedOnJob);
        Assert.False(lanes["tm-s2"].Dead);
        Assert.False(lanes["tm-s2"].BlockedOnJob);
        Assert.Null(lanes["tm-s2"].StreamState);
        Assert.Empty(host.Lanes("s-a", Hier, [new OwnedTeam("no-children", new JsonObject(), [])]));
        Assert.Equal("s1", lanes["tm-s1"].Stream);
        Assert.Equal($"{Dead}:x", lanes["tm-s1"].DeadAnchor);
        Assert.Equal("20200101-000000-late", lanes["tm-s2"].JobAnchor);
        Assert.Null(lanes["tm-s1"].JobAnchor);
        Assert.Null(lanes["tm-s1"].DeadSinceMs);
    }

    private void LaneJob(ServiceGraph graph, string lane, string session, string created, string phase, double sinceMs)
    {
        var job = WatchModule.JobNewRecord("job-" + lane, lane, "orchestrator", session, new JobSpec("test", "small", "dotnet test", false), null, created, null, null);
        job["slot"] = new JsonObject { ["phase"] = phase, ["since_ms"] = sinceMs };
        Store(graph,Path.Combine(Hier, "jobs", lane, "job-" + lane + ".json"), job);
    }

    [Fact]
    public void ALaneWhoseOrchestratorHasAJobQueuedForTheSlotIsWaitingOnTheSlotAndABlockedOrRunningOneIsNot()
    {
        var (host, graph) = Make();
        graph.Hierarchy.Core.Liveness = new Verdict(Ah.Core.Time.LivenessVerdict.Live);
        var owned = new[] { new OwnedTeam("tm", new JsonObject(), []) };
        Manager(graph, "s1", "s2", "s3", "s4");
        foreach (var (lane, stream) in new[] { ("tm-s1", "s1"), ("tm-s2", "s2"), ("tm-s3", "s3"), ("tm-s4", "s4") })
        {
            ChildTeam(graph, lane, "tm", stream, new JsonObject { ["session_id"] = "so-" + stream, ["pid"] = Dead, ["pid_start"] = "x" });
        }

        var now = DateTimeOffset.UtcNow;
        var fresh = now.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);
        var nowMs = (double)now.ToUnixTimeMilliseconds();
        LaneJob(graph, "tm-s1", "so-s1", fresh, "queued", nowMs);
        LaneJob(graph, "tm-s2", "so-s2", "2020-01-01T00:00:00.000Z", "queued", 1577836800000);
        LaneJob(graph, "tm-s3", "so-s3", fresh, "running", nowMs);

        var lanes = host.Lanes("s-a", Hier, owned).ToDictionary(static l => l.Lane);

        Assert.True(lanes["tm-s1"].WaitingOnSlot);
        Assert.False(lanes["tm-s1"].BlockedOnJob);
        Assert.True(lanes["tm-s2"].BlockedOnJob);
        Assert.False(lanes["tm-s2"].WaitingOnSlot);
        Assert.False(lanes["tm-s3"].WaitingOnSlot);
        Assert.False(lanes["tm-s4"].WaitingOnSlot);
    }

    [Fact]
    public void APausedLanesStateReachesTheLaneFactAndTheStreamLookupAndAWorkingLanesDoesNotReadAsPaused()
    {
        var (host, graph) = Make();
        var owned = new[] { new OwnedTeam("tm", new JsonObject(), []) };
        Manager(graph, "s1", "s2");
        ChildTeam(graph, "tm-s1", "tm", "s1", new JsonObject { ["session_id"] = "so-s" }, new JsonObject { ["state"] = "paused", ["updated"] = "2026-10-21T08:00:00.000Z", ["note"] = "wound down" });
        ChildTeam(graph, "tm-s2", "tm", "s2", new JsonObject { ["session_id"] = "so-2" }, new JsonObject { ["state"] = "working", ["updated"] = "2026-10-21T08:00:00.000Z" });

        var lanes = host.Lanes("s-a", Hier, owned).ToDictionary(static l => l.Lane);
        var wake = new WakeComposition(graph, graph.Environment);
        string? Of(string member) => wake.StreamOf(Hier, "tm", member, (dir, name) => graph.Roster.ReadTeamRecord(dir, name)).State;

        Assert.Equal("paused", lanes["tm-s1"].StreamState);
        Assert.Equal("working", lanes["tm-s2"].StreamState);
        Assert.Equal("paused", Of("so-s1"));
        Assert.Equal("working", Of("so-s2"));
        Assert.Null(Of("nobody"));
    }

    [Fact]
    public void AJobWhoseRecordedPidNowBelongsToAnotherProcessIsEndedAndOneWithTheRealStartStays()
    {
        var (host, graph) = Make();
        var pid = Environment.ProcessId;
        var reusedPath = Put(graph, "reused", pid);
        Store(graph,reusedPath, WatchModule.JobMerged(Rec(reusedPath)!, null, null, "Thu Jan  1 00:00:00 1970"));
        var samePath = Put(graph, "same", pid);
        Store(graph,samePath, WatchModule.JobMerged(Rec(samePath)!, null, null, "Wed Oct 21 08:00:05 2026"));

        var kept = host.ReconcileJobs("s-a", Hier, "tm", Read(graph));

        Assert.Equal(["same"], kept.Select(static j => j.Id));
        Assert.True(Rec(reusedPath)!.Get("ended_at") is { IsText: true });
    }

    [Fact]
    public void AReconciledJobIsAliveWhenItsPidRunsWithItsRecordedStartAndNotWithNoRecordedStart()
    {
        var (host, graph) = Make();
        var pid = Environment.ProcessId;
        var samePath = Put(graph, "same", pid);
        Store(graph, samePath, WatchModule.JobMerged(Rec(samePath)!, null, null, "Wed Oct 21 08:00:05 2026"));
        _ = Put(graph, "unstarted", pid);

        var kept = host.ReconcileJobs("s-a", Hier, "tm", Read(graph)).ToDictionary(static j => j.Id);

        Assert.True(kept["same"].Alive);
        Assert.False(kept["unstarted"].Alive);
    }

    [Fact]
    public void ARecordAHookEndedDuringTheSweepKeepsItsOwnEndAndExitAndAnUnracedOneIsEndedBySweep()
    {
        var (host, graph) = Make();
        var raced = Put(graph, "raced", 999999);
        var plain = Put(graph, "plain", 999998);
        var jobs = Read(graph);
        _onWalk = () =>
        {
            _onWalk = null;
            Store(graph,raced, WatchModule.JobEnded(Rec(raced)!, "2026-10-21T08:05:00.000Z", "7"));
        };

        var kept = host.ReconcileJobs("s-a", Hier, "tm", jobs);

        Assert.Empty(kept);
        var hookEnd = Rec(raced)!;
        Assert.Equal("7", hookEnd["exit"].AsString());
        Assert.Equal("2026-10-21T08:05:00.000Z", hookEnd["ended_at"].AsString());
        Assert.True(Rec(plain)!.Get("ended_at") is { IsText: true });
    }

    private string PutAt(ServiceGraph graph, string id, TimeSpan age, bool background)
    {
        var path = Path.Combine(Hier, "jobs", "tm", id + ".json");
        var sent = Ah.Core.Time.Timestamps.ToIsoString(DateTimeOffset.UtcNow - age);
        Store(graph,path, WatchModule.JobNewRecord(id, "tm", "impl", "s-a", new JobSpec(background ? "bg" : "test", "large", "sleep 5", background), null, sent, null, null));
        return path;
    }

    [Fact]
    public void ABackgroundJobWithNoProcessAfterNinetySecondsIsEndedUnseenAndAYoungOneAndAForegroundOneStay()
    {
        var (host, graph) = Make();
        var oldPath = PutAt(graph, "old", TimeSpan.FromSeconds(100), background: true);
        _ = PutAt(graph, "young", TimeSpan.FromSeconds(60), background: true);
        _ = PutAt(graph, "fg", TimeSpan.FromSeconds(20), background: false);
        var gonePath = PutAt(graph, "fg-gone", TimeSpan.FromMinutes(30), background: false);

        var kept = host.ReconcileJobs("s-a", Hier, "tm", Read(graph));

        Assert.Equal(["fg", "young"], kept.Select(static j => j.Id).Order());
        var ended = Rec(oldPath)!;
        Assert.Equal("unseen", ended["exit"].AsString());
        Assert.True(ended.Get("ended_at") is { IsText: true });
        Assert.True(Rec(gonePath)!.Get("ended_at") is { IsText: true });
    }

    [Fact]
    public void AJobsPickedProcessIsMovedDeeperWhenALaterSweepFindsOneAndRecordsItsGroup()
    {
        var sent = DateTimeOffset.Parse("2026-10-21T08:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var wrapper = new ProcessNode(70, sent.AddSeconds(1), "/bin/zsh -c nohup sleep 300", Owner, 70);
        var (host, graph) = Make(wrapper);
        var path = Put(graph, "j", null, "nohup sleep 300");
        var first = host.ReconcileJobs("s-a", Hier, "tm", Read(graph));
        Assert.Equal(70, first.Single().Pid);

        var (host2, _) = Make(wrapper, new ProcessNode(71, sent.AddSeconds(2), "sleep 300", 70, 70));
        var second = host2.ReconcileJobs("s-a", Hier, "tm", Read(graph));

        Assert.Equal(71, second.Single().Pid);
        Assert.Equal(71, (int)Rec(path)!["pid"].AsNumber());
    }

    [Fact]
    public void ASweepDeletesEndedRecordsOlderThanADayAndNeverAYoungerEndedOneOrAnOpenOne()
    {
        var (host, graph) = Make();
        string Ended(string id, TimeSpan endedAgo)
        {
            var path = PutAt(graph, id, endedAgo + TimeSpan.FromHours(1), background: true);
            Store(graph,path, WatchModule.JobEnded(Rec(path)!, Ah.Core.Time.Timestamps.ToIsoString(DateTimeOffset.UtcNow - endedAgo), null));
            return path;
        }

        var old = Ended("old", TimeSpan.FromHours(25));
        var recent = Ended("recent", TimeSpan.FromHours(23));
        var open = PutAt(graph, "open", TimeSpan.FromHours(48), background: false);

        _ = host.ReconcileJobs("s-a", Hier, "tm", Read(graph));

        Assert.False(Has(old));
        Assert.True(Has(recent));
        Assert.True(Has(open));
    }

    private string PutEnded(ServiceGraph graph, string id, TimeSpan endedAgo)
    {
        var path = PutAt(graph, id, endedAgo + TimeSpan.FromHours(1), background: true);
        Store(graph,path, WatchModule.JobEnded(Rec(path)!, Ah.Core.Time.Timestamps.ToIsoString(DateTimeOffset.UtcNow - endedAgo), null));
        return path;
    }

    [Fact]
    public void ACutoffNewerThanADayAgoStillSparesARecordThatEndedAnHourAgo()
    {
        var (_, graph) = Make();
        var old = PutEnded(graph, "old", TimeSpan.FromHours(25));
        var recent = PutEnded(graph, "recent", TimeSpan.FromHours(1));

        graph.Hierarchy.StateCalls.PruneJobs(Hier, DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds());

        Assert.False(Has(old));
        Assert.True(Has(recent));
    }

    [Fact]
    public void TwoJobsOfOneSessionNeverHoldTheSameProcess()
    {
        var (host, graph) = Make(new ProcessNode(77, DateTimeOffset.Parse("2026-10-21T08:00:03Z", System.Globalization.CultureInfo.InvariantCulture), "dotnet test Core.csproj"));
        _ = Put(graph, "a", null);
        _ = Put(graph, "b", null);

        var kept = host.ReconcileJobs("s-a", Hier, "tm", Read(graph));

        Assert.Equal(1, kept.Count(static j => j.Pid == 77));
        Assert.Equal(1, kept.Count(static j => j.Pid is null));
    }

    [Theory]
    [InlineData("/p/bin/ahd --plugin-root /p ah roster wake --cwd /w", null)]
    [InlineData("/p/bin/ahd --plugin-root /p ah roster overdue --cwd /w", null)]
    [InlineData("/p/bin/ahd --plugin-root /p ah roster doctor --cwd /w", 90)]
    public void AnAhJobNeverTakesAWaitersProcessButAnotherAhProcessStillMatches(string command, int? expected)
    {
        var started = new DateTimeOffset(2026, 10, 21, 8, 0, 3, TimeSpan.Zero);
        var (host, graph) = Make(new ProcessNode(90, started, command, Owner));
        _ = Put(graph, "verb", null, label: "ah roster doctor --cwd /w");

        var kept = host.ReconcileJobs("s-a", Hier, "tm", Read(graph));

        Assert.Equal(expected, kept.Single().Pid);
    }

    private const string TestRunLabel = "ah test run --eta large -- node tests/perf/ui-e4.mjs";

    private static ProcessNode Wrapper(int pid, DateTimeOffset started) =>
        new(pid, started, "/bin/bash /p/bin/ah-test-run --eta large -- node tests/perf/ui-e4.mjs", Owner);

    [Theory]
    [InlineData(TestRunLabel)]
    [InlineData("tests/with-slot.sh --eta large -- node tests/perf/ui-e4.mjs")]
    [InlineData("/p/bin/ah-test-run --eta large -- node tests/perf/ui-e4.mjs")]
    public void AnAhTestRunJobBindsItsWrapperAndNotALaterAhProcess(string label)
    {
        // ah2-233: `ah test run` and with-slot.sh exec bin/ah-test-run under their own pid; neither it nor the test below it has an `ah` word
        var started = new DateTimeOffset(2026, 10, 21, 8, 0, 3, TimeSpan.Zero);
        var doctor = new ProcessNode(90, started.AddSeconds(1), "/p/bin/ahd --plugin-root /p ah roster doctor --cwd /w", Owner);
        var node = new ProcessNode(91, started.AddSeconds(1), "node tests/perf/ui-e4.mjs", 89);
        var (host, graph) = Make(Wrapper(89, started), node, doctor);
        _ = Put(graph, "run", null, label: label);

        Assert.Equal(89, host.ReconcileJobs("s-a", Hier, "tm", Read(graph)).Single().Pid);
    }

    [Fact]
    public void AnAhTestRunJobWhoseWrapperIsGoneBindsNothingElse()
    {
        var started = new DateTimeOffset(2026, 10, 21, 8, 0, 3, TimeSpan.Zero);
        var (host, graph) = Make(
            new ProcessNode(91, started, "node tests/perf/ui-e4.mjs", Owner),
            new ProcessNode(90, started.AddSeconds(1), "/p/bin/ahd --plugin-root /p ah roster doctor --cwd /w", Owner));
        _ = Put(graph, "run", null, label: TestRunLabel);

        Assert.Null(host.ReconcileJobs("s-a", Hier, "tm", Read(graph)).Single().Pid);
    }

    [Fact]
    public void TwoTestRunJobsOfOneSessionBindTwoWrappersOldestFirst()
    {
        var started = new DateTimeOffset(2026, 10, 21, 8, 0, 3, TimeSpan.Zero);
        var (host, graph) = Make(Wrapper(89, started.AddSeconds(2)), Wrapper(88, started));
        _ = Put(graph, "a", null, label: TestRunLabel);
        _ = Put(graph, "b", null, label: TestRunLabel);

        var kept = host.ReconcileJobs("s-a", Hier, "tm", Read(graph));

        Assert.Equal([88, 89], kept.Select(static j => j.Pid!.Value));
    }

    [Fact]
    public void ATestRunJobBindsTheOuterWrapperWhenANestedOneIsAlreadyRunning()
    {
        var started = new DateTimeOffset(2026, 10, 21, 8, 0, 3, TimeSpan.Zero);
        var (host, graph) = Make(
            Wrapper(88, started),
            new ProcessNode(91, started.AddSeconds(1), "node tests/perf/ui-e4.mjs", 88),
            new ProcessNode(92, started.AddSeconds(2), "/bin/bash /p/bin/ah-test-run --eta small -- node tests/perf/fixture.mjs", 91));
        _ = Put(graph, "run", null, label: TestRunLabel);

        Assert.Equal(88, host.ReconcileJobs("s-a", Hier, "tm", Read(graph)).Single().Pid);
    }

    [Fact]
    public void ADirectAhTestRunLabelBindsTheWrapperAndNotTheOlderToolCallShellThatNamesIt()
    {
        var started = new DateTimeOffset(2026, 10, 21, 8, 0, 3, TimeSpan.Zero);
        var (host, graph) = Make(
            new ProcessNode(87, started, "/bin/zsh -c source /h/snap.sh && eval '/p/bin/ah-test-run --eta large -- node tests/perf/ui-e4.mjs'", Owner),
            Wrapper(89, started.AddSeconds(1)) with { Parent = 87 });
        _ = Put(graph, "run", null, label: "/p/bin/ah-test-run --eta large -- node tests/perf/ui-e4.mjs");

        Assert.Equal(89, host.ReconcileJobs("s-a", Hier, "tm", Read(graph)).Single().Pid);
    }

    [Theory]
    [InlineData("/bin/bash /p/bin/ah-test-run --eta small -- dotnet test --configuration Release", false)]
    [InlineData("/bin/bash /p/bin/ah-test-run --eta small -- dotnet test --exact", false)]
    [InlineData("bash -lc 'dotnet test'", true)]
    [InlineData("/bin/zsh -c source /h/snap.sh", true)]
    [InlineData("/bin/bash --login -c 'make'", true)]
    [InlineData("/bin/bash -- -c", false)]
    public void AShellIsAWrapperOnlyByItsOwnDashCFlag(string command, bool wrapper)
    {
        Assert.Equal(wrapper, WatchModule.JobIsWrapper(command));
    }

    [Theory]
    [InlineData("cd /w/native; MSBUILDDISABLENODEREUSE=1 ah test run --eta large -- node tests/perf/ui-e4.mjs", 89)]
    [InlineData("cd /w/native && ah test run --eta large -- node tests/perf/ui-e4.mjs", 89)]
    [InlineData("make x && ah test run --eta large -- node tests/perf/ui-e4.mjs", 89)]
    [InlineData("cd /w extra; ah test run --eta large -- node tests/perf/ui-e4.mjs", 89)]
    [InlineData("echo 'ah test run --eta large -- node tests/perf/ui-e4.mjs'", null)]
    public void NoLeadingCommandHidesATestRunAndNegativeAQuotedMentionIsNotOne(string label, int? expected)
    {
        var started = new DateTimeOffset(2026, 10, 21, 8, 0, 3, TimeSpan.Zero);
        var (host, graph) = Make(Wrapper(89, started));
        _ = Put(graph, "run", null, label: label);

        Assert.Equal(expected, host.ReconcileJobs("s-a", Hier, "tm", Read(graph)).Single().Pid);
    }

    [Fact]
    public void AJobWithNoProcessGetsTheMatchingOneBelowItsOwnersSessionAndAnUnrelatedOneIsLeftAlone()
    {
        var started = new DateTimeOffset(2026, 10, 21, 8, 0, 3, TimeSpan.Zero);
        var (host, graph) = Make(new ProcessNode(777, started, "dotnet test Core.csproj"), new ProcessNode(778, started, "sleep 300"));
        var matched = Put(graph, "match", null);
        var other = Put(graph, "other", null, label: "rsync -a x y");

        var kept = host.ReconcileJobs("s-a", Hier, "tm", Read(graph));

        Assert.Equal(777, kept.Single(static j => j.Id == "match").Pid);
        var record = Rec(matched)!;
        Assert.Equal(777, (int)record["pid"].AsNumber());
        Assert.Equal("Wed Oct 21 08:00:05 2026", record["pid_start"].AsString());
        Assert.Null(kept.Single(static j => j.Id == "other").Pid);
        Assert.False(Rec(other)!.Get("pid") is { IsNumber: true });
    }

    private string PutGrouped(ServiceGraph graph, string id, string kind, string label, int group, TimeSpan age)
    {
        var path = Path.Combine(Hier, "jobs", "tm", id + ".json");
        var sent = Ah.Core.Time.Timestamps.ToIsoString(DateTimeOffset.UtcNow - age);
        var record = WatchModule.JobNewRecord(id, "tm", "impl", "s-a", new JobSpec(kind, "large", label, kind == "bg"), Dead, sent, null, null);
        Store(graph,path, WatchModule.JobMerged(record, null, null, null, group));
        return path;
    }

    private static ProcessNode InGroupNode(int pid, int group, string command) => new(pid, DateTimeOffset.UtcNow - TimeSpan.FromSeconds(190), command, 1, group);

    private bool EndedInFile(string path) => Rec(path)!.Get("ended_at") is { IsText: true };

    [Fact]
    public void ABuildJobEndsWhenItsPidExitsAlthoughMsBuildAndTheCompilerServerStayInItsGroup()
    {
        var (host, graph) = Make();
        var build = PutGrouped(graph, "build", "build", "dotnet build x.csproj", 50, TimeSpan.FromSeconds(200));
        var bgBuildNodes = PutGrouped(graph, "bg-nodes", "bg", "nohup dotnet build x.csproj", 51, TimeSpan.FromSeconds(200));
        var bgSleep = PutGrouped(graph, "bg-sleep", "bg", "nohup sleep 300", 52, TimeSpan.FromSeconds(200));
        _group = group => group switch
        {
            50 => [InGroupNode(60, 50, "dotnet /sdk/MSBuild.dll -nodeReuse:true"), InGroupNode(61, 50, "dotnet /sdk/VBCSCompiler.dll"), InGroupNode(65, 50, "dotnet testhost.dll")],
            51 => [InGroupNode(62, 51, "dotnet /sdk/MSBuild.dll -nodeReuse:true"), InGroupNode(63, 51, "dotnet /sdk/VBCSCompiler.dll")],
            52 => [InGroupNode(64, 52, "sleep 300")],
            _ => null,
        };

        var kept = host.ReconcileJobs("s-a", Hier, "tm", Read(graph));

        Assert.Equal(["bg-sleep"], kept.Select(static j => j.Id));
        Assert.True(EndedInFile(build));
        Assert.True(EndedInFile(bgBuildNodes));
        Assert.False(EndedInFile(bgSleep));
    }

    [Fact]
    public void ANohupSleepStaysOpenAfterItsWrapperExitsForAsLongAsTheSleepRunsAndEndsWhenTheSleepEnds()
    {
        var (host, graph) = Make();
        var path = PutGrouped(graph, "nohup", "bg", "nohup sleep 300", 70, TimeSpan.FromSeconds(200));
        _group = group => group == 70 ? [InGroupNode(71, 70, "sleep 300")] : null;

        var during = host.ReconcileJobs("s-a", Hier, "tm", Read(graph));

        Assert.Equal(["nohup"], during.Select(static j => j.Id));
        Assert.False(EndedInFile(path));

        _group = group => group == 70 ? [] : null;

        var after = host.ReconcileJobs("s-a", Hier, "tm", Read(graph));

        Assert.Empty(after);
        Assert.True(EndedInFile(path));
    }

    [Fact]
    public void TheFailsafeOwesTheOverdueStepOfEveryOpenJobAndNothingForAnEndedOrAnUnseenOneAndACapAddsItsOwnStep()
    {
        var (_, graph) = Make();
        var wake = new WakeComposition(graph, graph.Environment);
        _ = Put(graph, "open", Environment.ProcessId);
        _ = PutAt(graph, "fg", TimeSpan.FromSeconds(20), background: false);
        _ = PutAt(graph, "unseen", TimeSpan.FromSeconds(100), background: true);
        _ = PutEnded(graph, "ended", TimeSpan.FromHours(1));
        var capped = WatchModule.JobNewRecord("capped", "tm", "impl", "s-a", new JobSpec("test", "large", "dotnet test", false), Environment.ProcessId, "2026-10-21T08:00:00.000Z", null, "2026-10-21T09:00:00.000Z");
        Store(graph,Path.Combine(Hier, "jobs", "tm", "capped.json"), capped);

        var items = wake.JobStalls(Hier, "tm", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        Assert.Equal(["capped|cap-near", "capped|overdue", "fg|overdue", "open|overdue"], items.Select(static i => i.Job + "|" + i.Step).Order());
        var sent = DateTimeOffset.Parse("2026-10-21T08:00:00Z", System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeMilliseconds();
        var threshold = graph.Hierarchy.Messages.ThresholdFor(JsonValue.FromString("large"));
        Assert.Equal(sent + (threshold * 1000) + 120_000, items.Single(static i => i.Job == "open").AtMs);
        Assert.Equal(sent + (0.9 * 3_600_000), items.Single(static i => i is { Job: "capped", Step: "cap-near" }).AtMs);
    }

    private sealed class LiveMember : Ah.Hierarchy.IMemberLiveness
    {
        public Ah.Hierarchy.MemberRegistrationLiveness? ForMember(string memberName, string team) => new("s-a", true, "registration live");

        public bool PidIdentityLive(int pid, string? recordedStart) => true;

        public Ah.Core.Time.LivenessVerdict PidVerdict(int pid, string? recordedStart) => Ah.Core.Time.LivenessVerdict.Live;
    }

    [Fact]
    public void TheMemberVerdictOfALiveMemberWithAnOverdueJobNamesTheJobAndItsEtaAndOneWithoutAnOverdueJobDoesNot()
    {
        var (_, graph) = Make();
        graph.Hierarchy.Core.Liveness = new LiveMember();
        var wake = new WakeComposition(graph, graph.Environment);
        _ = Put(graph, "late", Environment.ProcessId);

        var blocked = wake.MemberVerdict(Hier, "tm", "impl");
        var other = wake.MemberVerdict(Hier, "tm", "someone-else");

        Assert.StartsWith("blocked-on-job — job late (test dotnet test Core.csproj) is past its ETA large; ", blocked, StringComparison.Ordinal);
        Assert.StartsWith("live — ", other, StringComparison.Ordinal);
    }

    [Fact]
    public void TheMemberVerdictOfALiveMemberWhoseJobIsNotYetDueIsPlainLive()
    {
        var (_, graph) = Make();
        graph.Hierarchy.Core.Liveness = new LiveMember();
        var wake = new WakeComposition(graph, graph.Environment);
        _ = PutAt(graph, "fresh", TimeSpan.FromSeconds(10), background: false);

        Assert.StartsWith("live — ", wake.MemberVerdict(Hier, "tm", "impl"), StringComparison.Ordinal);
    }

    private sealed class MemberHost(WakeComposition wake, string hierarchyDir, string name, string session, int polls, bool member = true, IReadOnlySet<int>? kin = null) : IWakeHost
    {
        private int _sleeps;

        public DateTimeOffset Now { get; private set; } = DateTimeOffset.UtcNow;

        public readonly List<string> Out = [];

        public void Sleep(TimeSpan span)
        {
            Now += span;
            if (++_sleeps > polls)
            {
                throw new TimeoutException("the waiter was still waiting");
            }
        }

        public IReadOnlyList<WakeEvent> Unacked() => [];

        public bool DaemonUp() => true;

        public IReadOnlySet<string> LoadPrinted() => new HashSet<string>();

        public void SavePrinted(IReadOnlyCollection<string> ids)
        {
        }

        public void Print(string line) => Out.Add(line);

        public string? HeartbeatLine => member ? "ahd wake: heartbeat; a background job is still open; restart the waiter to keep watching it" : null;

        public string? NothingLeftLine => member ? "ahd wake: no open background job; nothing left to watch" : null;

        public bool NothingLeft() =>
            !WatchModule.MemberHasOpenBackgroundJob(wake.Jobs(hierarchyDir, "tm"), name, session, wake.JobOpen, kin);
    }

    [Fact]
    public void ABackgroundCommandThatNeverShowedAProcessEndsUnseenRaisesNoOverdueAndLetsTheMemberWaiterExitZero()
    {
        var (host, graph) = Make();
        _ = PutAt(graph, "unseen", TimeSpan.FromSeconds(100), background: true);
        var wake = new WakeComposition(graph, graph.Environment);

        _ = host.ReconcileJobs("s-a", Hier, "tm", Read(graph));

        var jobs = Read(graph);
        var job = Assert.Single(jobs);
        Assert.True(job.Ended);
        Assert.False(wake.JobOpen(job));
        Assert.Null(WatchModule.JobOverdueOf(jobs, "impl", _ => 60, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 3_600_000, 30000));
        var waiter = new MemberHost(wake, Hier, "impl", "s-a", 3);
        Assert.Equal(0, WatchModule.RunWaiter(waiter, StallSettings.Defaults));
        Assert.Equal("ahd wake: no open background job; nothing left to watch", Assert.Single(waiter.Out));
    }

    [Theory]
    [InlineData("/other/install/bin/ah", "/other/install/.claude-plugin/plugin.json", true)]
    [InlineData("/other/install/bin/ah", "/elsewhere/.claude-plugin/plugin.json", false)]
    [InlineData("/other/install/bin/notah", "/other/install/.claude-plugin/plugin.json", false)]
    [InlineData("/other/install/tools/ah", "/other/install/.claude-plugin/plugin.json", false)]
    public void TheWaiterTypedThroughAnotherInstallsLauncherIsNoJobAndALookAlikeIs(string program, string file, bool excluded)
    {
        Assert.Equal(excluded, WatchModule.JobExcluded($"{program} roster wake --cwd /w", word => word, ["/real/bin/ah"], path => path == file));
    }

    [Fact]
    public void AnotherVerbOfAnInstalledLauncherIsStillAJob()
    {
        Assert.False(WatchModule.JobExcluded("/other/install/bin/ah roster doctor --cwd /w", word => word, ["/real/bin/ah"], _ => true));
    }

    private static readonly StallSettings ShortBound = StallSettings.Defaults with { WakePoll = TimeSpan.FromSeconds(10), Heartbeat = TimeSpan.FromSeconds(30) };

    [Fact]
    public void AMemberWhoseOnlyBackgroundCommandIsItsOwnWaiterHasNoOpenJobAndAYoungBackgroundJobKeepsTheWaiterRunningUntilTheBound()
    {
        var (_, graph) = Make();
        var wake = new WakeComposition(graph, graph.Environment);
        Assert.True(WatchModule.JobExcluded("ah roster wake --team tm", word => word == "ah" ? "/real/bin/ah" : null, ["/real/bin/ah"]));
        var alone = new MemberHost(wake, Hier, "impl", "s-a", 3);

        Assert.Equal(0, WatchModule.RunWaiter(alone, StallSettings.Defaults));

        _ = PutAt(graph, "young", TimeSpan.FromSeconds(10), background: true);
        var beforeBound = new MemberHost(wake, Hier, "impl", "s-a", 2);
        _ = Assert.Throws<TimeoutException>(() => WatchModule.RunWaiter(beforeBound, ShortBound));

        var atBound = new MemberHost(wake, Hier, "impl", "s-a", 3);
        Assert.Equal(0, WatchModule.RunWaiter(atBound, ShortBound));
        Assert.Equal("ahd wake: heartbeat; a background job is still open; restart the waiter to keep watching it", Assert.Single(atBound.Out));
    }

    [Fact]
    public void AnOrchestratorsWaiterWithNoEventExitsAtTheBoundAndNotBefore()
    {
        var (_, graph) = Make();
        var wake = new WakeComposition(graph, graph.Environment);

        _ = Assert.Throws<TimeoutException>(() => WatchModule.RunWaiter(new MemberHost(wake, Hier, "orchestrator", "s-o", 2, member: false), ShortBound));

        var waiter = new MemberHost(wake, Hier, "orchestrator", "s-o", 3, member: false);
        Assert.Equal(0, WatchModule.RunWaiter(waiter, ShortBound));
        Assert.Equal("ahd wake: heartbeat, nothing pending; restart the waiter if work is in flight", Assert.Single(waiter.Out));
    }

    [Fact]
    public void AMembersWaiterDoesNotWaitOnAJobWhoseProcessIsItselfButDoesOnAnyOtherProcess()
    {
        var (_, graph) = Make();
        var wake = new WakeComposition(graph, graph.Environment);
        Store(graph, Path.Combine(Hier, "jobs", "tm", "self.json"), WatchModule.JobNewRecord(
            "self", "tm", "impl", "s-a", new JobSpec("slot", "large", "ah test run --eta large -- node x.mjs", true), Environment.ProcessId,
            Ah.Core.Time.Timestamps.ToIsoString(DateTimeOffset.UtcNow), null, null));

        var self = new MemberHost(wake, Hier, "impl", "s-a", 3, kin: wake.Kin());
        Assert.Equal(0, WatchModule.RunWaiter(self, ShortBound));
        Assert.Equal("ahd wake: no open background job; nothing left to watch", Assert.Single(self.Out));

        var other = new MemberHost(wake, Hier, "impl", "s-a", 3, kin: new HashSet<int> { Environment.ProcessId + 1 });
        Assert.Equal(0, WatchModule.RunWaiter(other, ShortBound));
        Assert.Equal("ahd wake: heartbeat; a background job is still open; restart the waiter to keep watching it", Assert.Single(other.Out));
    }
}
