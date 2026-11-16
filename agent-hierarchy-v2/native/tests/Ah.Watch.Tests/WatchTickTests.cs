using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Hierarchy;
using Ah.TestKit;

namespace Ah.Watch.Tests;

internal sealed class NullFiles : IFileReader
{
    internal HashSet<string> Present { get; } = [];

    internal Dictionary<string, string> Tails { get; } = [];

    public string ReadText(string path) => throw new FileNotFoundException(path);

    public byte[] ReadBytes(string path) => throw new FileNotFoundException(path);

    public FileStat? Stat(string path, bool followSymlinks = true) =>
        Tails.TryGetValue(path, out var tail) ? new FileStat(FileKind.File, tail.Length, 1, 1, 0x1A4, 0) : null;

    public bool Exists(string path) => Present.Contains(path);

    public bool IsExecutable(string path) => false;

    public FileTail ReadTail(string path, int maxBytes)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(Tails[path]);
        return new FileTail(bytes, bytes.Length, false);
    }

    public FileRange ReadRange(string path, long offset, int maxBytes) => throw new NotSupportedException();

    public NoFollowRead ReadNoFollow(string path, int maxBytes) => throw new NotSupportedException();

    public string RealPath(string path) => path;

    public IReadOnlyList<string> List(string directory) => [];
}

internal sealed class MapEnv(Dictionary<string, string?>? vars = null) : IAhEnvironment
{
    public string? GetVariable(string name) => vars is not null && vars.TryGetValue(name, out var v) ? v : null;

    public bool TestMode => false;

    public string? GetTestOnly(string name) => null;

    public string HomeDirectory => "/private/tmp/ahprobe.none";

    public string AhHome => "/private/tmp/ahprobe.none/ah";

    public PluginInfo? Plugin => null;
}

internal sealed class FakeJobs : IJobRegistry
{
    internal List<(string Name, TimeSpan Period, Func<CancellationToken, Task> Run)> Jobs { get; } = [];

    public void Add(string name, TimeSpan period, Func<CancellationToken, Task> handler) => Jobs.Add((name, period, handler));
}

internal class FakeHost : IWatchHost
{
    internal List<WatchRegistration> Registrations { get; } = [];

    internal HashSet<string> Fresh { get; } = [];

    internal Dictionary<string, IReadOnlyList<DispatchCandidate>> Candidates { get; } = [];

    internal Dictionary<string, IReadOnlyList<ContextSubject>> Subjects { get; } = [];

    internal Dictionary<string, IReadOnlyList<OwnedTeam>> Owned { get; } = [];

    internal Dictionary<string, JsonObject?> Activity { get; } = [];

    /// <summary>The checklist each team (by name, "" for the default) holds.</summary>
    internal Dictionary<string, IReadOnlyList<ChecklistEntry>> Checklists { get; } = [];

    IReadOnlyList<ChecklistEntry> IWatchHost.Checklist(string session, string home, string? team) => Checklists.GetValueOrDefault(team ?? string.Empty) ?? [];

    internal List<string> Evaluated { get; } = [];

    /// <summary>Names the attributed roster lists as busy.</summary>
    internal HashSet<string> BusyPeers { get; } = [];

    internal List<WatchMail> Mails { get; set; } = [];

    /// <summary>Teams (by name) the host reports as held.</summary>
    internal HashSet<string> HeldTeams { get; } = [];

    /// <summary>Whether the default team (the one a null team name stands for) is held.</summary>
    internal bool DefaultTeamHeld { get; set; }

    bool IWatchHost.TeamHeld(string home, string? team) => team is null ? DefaultTeamHeld : HeldTeams.Contains(team);

    /// <summary>The lane facts the host answers; empty by default.</summary>
    internal Func<IReadOnlyList<LaneFact>> LaneFacts { get; set; } = static () => [];

    /// <summary>The lane facts per evaluated session; when set it answers instead of <see cref="LaneFacts"/>.</summary>
    internal Func<string, IReadOnlyList<LaneFact>>? LanesOf { get; set; }

    IReadOnlyList<LaneFact> IWatchHost.Lanes(string session, string home, IReadOnlyList<OwnedTeam> owned) => LanesOf is { } by ? by(session) : LaneFacts();

    /// <summary>The job facts per team name; empty by default.</summary>
    internal Func<string?, IReadOnlyList<JobFact>>? JobsOf { get; set; }

    IReadOnlyList<JobFact> IWatchHost.Jobs(string session, string home, string? team) => JobsOf?.Invoke(team) ?? [];

    /// <summary>When set, no mail counts as already in a box, so a restarted or second session tells an episode again.</summary>
    internal bool NothingAlreadyMailed { get; set; }

    bool IWatchHost.AlreadyMailed(WatchMail mail) =>
        !NothingAlreadyMailed && mail.Data.Get("request_id") is { IsText: true } id && mail.Data.Get("due_slot") is { IsText: true } slot
        && Mails.Any(m => m.To == mail.To && m.Event == mail.Event && m.Data.Get("request_id") is { IsText: true } held && held.AsString() == id.AsString()
            && m.Data.Get("due_slot") is { IsText: true } heldSlot && heldSlot.AsString() == slot.AsString());

    internal List<WatchEvent> Logged { get; } = [];

    internal List<GateAppend> Gates { get; } = [];

    internal List<string> Surfaced { get; } = [];

    internal List<string> LiveDirs { get; } = [];

    internal Func<WatchMail, bool>? FailMail { get; set; }

    internal Func<string, bool>? ThrowFor { get; set; }

    internal Func<Task<JsonObject>>? Ladder { get; set; }

    internal int LadderCalls;

    IReadOnlyList<WatchRegistration> IWatchHost.LiveRegistrations() => Registrations;

    bool IWatchHost.PeerBusy(string dir, string name, string? team) => BusyPeers.Contains(name);

    /// <summary>When each busy peer's roster row was written; a peer not listed has a row that does not say.</summary>
    internal Dictionary<string, double> BusySeen { get; } = [];

    double? IWatchHost.PeerBusySeenMs(string dir, string name, string? team) => BusySeen.TryGetValue(name, out var ms) ? ms : null;

    string IWatchHost.RecordVerdict(JsonObject? record, double nowMs, bool busy, double debounceMs) =>
        Ah.Hierarchy.HierarchyModule.RecordVerdict(record is null ? JsonValue.Null : JsonValue.FromObject(record), nowMs, busy ? "busy" : null, dispatchOpen: true, debounceMs: debounceMs);

    IReadOnlyList<string> IWatchHost.LiveHierarchyDirs() => LiveDirs;

    public List<string> FirstSeen { get; } = [];

    void IWatchHost.StatusDirFirstSeen(string dir) => FirstSeen.Add(dir);

    bool IWatchHost.PullFresh(string boxDir, string sessionId, double nowMs) => Fresh.Contains(boxDir);

    internal Dictionary<string, double> BeatMs { get; } = [];

    double IWatchHost.PullBeatMs(string boxDir, string sessionId, double nowMs) => BeatMs.GetValueOrDefault(boxDir, 1);

    internal string SessionCwdValue { get; set; } = "/private/tmp/ahprobe.cwd";

    string? IWatchHost.SessionCwd(string sessionId) => SessionCwdValue;

    double IWatchHost.ThresholdSeconds(string eta) => 600;

    /// <summary>What <c>DispatchInputsStamp</c> answers; null (the default) means the host cannot say.</summary>
    internal string? InputsStamp { get; set; }

    string? IWatchHost.DispatchInputsStamp(string sessionId, double nowMs) => InputsStamp;

    IReadOnlyList<DispatchCandidate> IWatchHost.Dispatches(string sessionId, double nowMs)
    {
        Evaluated.Add(sessionId);
        if (ThrowFor?.Invoke(sessionId) == true)
        {
            throw new IOException("boom");
        }

        return Candidates.GetValueOrDefault(sessionId) ?? [];
    }

    /// <summary>Peer records the host starts with; <see cref="FeedBackGates"/> adds the rows appended since.</summary>
    internal List<JsonObject> SeededPeers { get; } = [];

    internal List<JsonObject> AppendedPeers { get; } = [];

    IReadOnlyList<JsonObject> IWatchHost.PeerRecords() => FeedBackGates ? [.. SeededPeers, .. AppendedPeers] : [.. SeededPeers];

    /// <summary>When set, <see cref="IWatchHost.Gates"/> returns the rows appended so far, as the store does.</summary>
    internal bool FeedBackGates { get; set; }

    /// <summary>The peer records the recovery watch counts a streak from.</summary>
    internal JsonArray Peers { get; } = new();

    /// <summary>When set, answers the activity read instead of <see cref="Activity"/>.</summary>
    internal Func<string, JsonObject?>? ActivityRead { get; set; }

    /// <summary>Gate rows the store already holds.</summary>
    internal List<JsonObject> SeededGates { get; } = [];

    IReadOnlyList<JsonObject> IWatchHost.Gates(string hierarchyDir) => FeedBackGates ? [.. SeededGates, .. Gates.Select(static g => g.Row)] : [.. SeededGates];

    /// <summary>When set, the strict gates read fails.</summary>
    internal bool GatesUnreadable { get; set; }

    IReadOnlyList<JsonObject>? IWatchHost.GatesOrNull(string hierarchyDir, out string? why)
    {
        why = GatesUnreadable ? "disk I/O error" : null;
        return GatesUnreadable ? null : ((IWatchHost)this).Gates(hierarchyDir);
    }

    IReadOnlyList<ContextSubject> IWatchHost.ContextSubjects(string sessionId, string hierarchyDir) => Subjects.GetValueOrDefault(sessionId) ?? [];

    IReadOnlyList<OwnedTeam> IWatchHost.OwnedTeams(string sessionId, string hierarchyDir) => Owned.GetValueOrDefault(sessionId) ?? [];

    IReadOnlyList<JsonObject>? IWatchHost.HerdrAgents() => null;

    internal string? ScreenHashValue { get; set; }

    PromptRead IWatchHost.ReadPrompt(JsonObject memberRow, JsonObject team) => new(ScreenHashValue, string.Empty, null);

    string IWatchHost.KindOf(JsonObject memberRow) => "claude";

    bool IWatchHost.RowAdapted(JsonObject memberRow) => false;

    string? IWatchHost.PromptHeading(string kind, string? recognized) => null;

    double IWatchHost.DispatchBaseMs(string sessionId, string member) => 0;

    string IWatchHost.RosterCommand => "/plugin/bin/ah roster";

    /// <summary>When set, a gate row appended without a time gets this one, as the store gives it.</summary>
    internal Func<string>? Stamp { get; set; }

    void IWatchHost.AppendGates(IReadOnlyList<GateAppend> gates)
    {
        if (Stamp is not null)
        {
            foreach (var g in gates.Where(static g => g.Row.Get("ts") is null))
            {
                g.Row["ts"] = Stamp();
            }
        }

        Gates.AddRange(gates);
    }

    void IWatchHost.AppendPeerRows(IReadOnlyList<PeerAppend> rows) => AppendedPeers.AddRange(rows.Select(static r => r.Row));

    void IWatchHost.MarkSurfaced(string sessionId, IReadOnlyList<string> requestIds) => Surfaced.AddRange(requestIds);

    void IWatchHost.RecordWatchEvents(string sessionId, IReadOnlyList<WatchEvent> events) => Logged.AddRange(events);

    void IWatchHost.Mail(WatchMail mail)
    {
        if (FailMail?.Invoke(mail) == true)
        {
            throw new IOException("mailbox full");
        }

        Mails.Add(mail);
        if (MemberPicksUpResumes && mail.Event == "resume" && mail.ToSession is { } landed)
        {
            Activity[landed] = new JsonObject { ["activity"] = "working", ["at"] = DateTimeOffset.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture) };
        }
    }

    /// <summary>When true a member that is mailed a resume, or whose watcher takes its wake file, goes back to working, as a real one does. Off, a member that never recovers stays failed with the same failure.</summary>
    internal bool MemberPicksUpResumes { get; set; }

    Task IWatchHost.SweepLinksAsync(IReadOnlyList<WatchBox> boxes, string cwd, string hierarchyDir, double nowMs, CancellationToken cancellation) =>
        Task.CompletedTask;

    async Task<JsonObject> IWatchHost.RunLadderAsync(double nowMs, double pollMs, CancellationToken cancellation)
    {
        _ = Interlocked.Increment(ref LadderCalls);
        return Ladder is null ? new JsonObject { ["pending"] = 0 } : await Ladder().ConfigureAwait(false);
    }

    JsonObject? IRecoveryHost.ReadActivity(string dir, string subject) => ActivityRead is { } read ? read(subject) : Activity.GetValueOrDefault(subject);

    /// <summary>What the detector recorded as failed, by subject.</summary>
    internal List<(string Subject, JsonObject Extra)> RecordedFailures { get; } = [];

    void IRecoveryHost.RecordFailed(string dir, string subject, JsonObject extra) => RecordedFailures.Add((subject, extra));

    IReadOnlyList<string> IRecoveryHost.AddressesOf(string sessionId, string name) => [];

    internal int PeerRowsCalls { get; private set; }

    JsonArray IRecoveryHost.PeerRows()
    {
        PeerRowsCalls++;
        return Peers;
    }

    /// <summary>The resume withdrawals asked for, in order.</summary>
    internal List<(string Address, string? Session, string? KeepId, double? CreatedBeforeMs, string Reason)> Withdrawals { get; } = [];

    void IWatchHost.WithdrawRewakes(string address, string? session, string? keepId, double? createdBeforeMs, string reason) =>
        Withdrawals.Add((address, session, keepId, createdBeforeMs, reason));
}

public sealed class WatchTickTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-03-01T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    private sealed class Rig
    {
        internal FakeHost Host { get; } = new();

        internal FakeClock Clock { get; } = new(Start);

        internal NullFiles Files { get; } = new();

        internal List<string> Log { get; } = [];

        internal IStatusDirs Dirs { get; } = WatchModule.CreateStatusDirs();

        internal WatchSnapshot? Observed { get; private set; }

        internal bool Owner { get; set; } = true;

        internal Dictionary<string, string?> Vars { get; } = [];

        internal WatchTick Tick()
        {
            var env = new MapEnv(Vars);
            var recovery = HierarchyModule.CreateRecovery(Files, env, Clock);
            return new WatchTick(new WatchDependencies(
                Host, env, Clock, Files, recovery, Dirs, () => Owner, Log.Add, snapshot => Observed = snapshot)
            {
                Cpu = Cpu,
            });
        }

        internal CountingMeter? Cpu { get; set; }
    }

    private static WatchRegistration Reg(string sid, string? harness = "claude", long? pid = 100, string at = "2026-03-01T11:00:00.000Z", params string[] boxes) =>
        new(
            sid,
            harness,
            pid,
            at,
            [.. (boxes.Length == 0 ? ["orchestrator@alpha"] : boxes).Select(static a => new WatchBox(a, $"/boxes/{a}"))],
            "/private/tmp/ahprobe.h");

    private static DispatchCandidate Landed(string id, string? toAddr = "impl@alpha", string? team = null) =>
        new(id, "implementor", "slug", toAddr, "impl", "small", "2026-03-01T11:00:00.000Z", false, "/r.md", true, string.Empty, "/private/tmp/ahprobe.h") { Team = team };

    private static async Task Pass(Rig rig, WatchTick tick, int polls = 2)
    {
        for (var i = 0; i < polls; i++)
        {
            await tick.RunAsync(CancellationToken.None);
            rig.Clock.UtcNow = rig.Clock.UtcNow.AddMilliseconds(15_000);
        }
    }

    [Fact]
    public async Task ASessionWithNoDispatchIsReadAgainWhenItsInputsMoveOrTheQuietCapPassesAndOneWithADispatchEveryTick()
    {
        var rig = new Rig();
        rig.Host.Registrations.Add(Reg("s1"));
        rig.Host.InputsStamp = "1";
        var tick = rig.Tick();

        // announced, then read once at 15 s: none found
        await Pass(rig, tick);
        Assert.Single(rig.Host.Evaluated);

        // 15 s later the stamp has not moved: not read
        await Pass(rig, tick, 1);
        Assert.Single(rig.Host.Evaluated);

        // a write moves the stamp: read on the very next tick, not at the cap
        rig.Host.InputsStamp = "2";
        await Pass(rig, tick, 1);
        Assert.Equal(2, rig.Host.Evaluated.Count);

        // the stamp holds: 15, 30 and 45 s after that read nothing is read; at 60 s (the cap) it is
        await Pass(rig, tick, 3);
        Assert.Equal(2, rig.Host.Evaluated.Count);
        await Pass(rig, tick, 1);
        Assert.Equal(3, rig.Host.Evaluated.Count);

        // negative: a session with a candidate is read every tick, the stamp holding or not
        rig.Host.Candidates["s1"] = [Landed("r1")];
        rig.Host.InputsStamp = "3";
        await Pass(rig, tick, 3);
        Assert.Equal(6, rig.Host.Evaluated.Count);
    }

    [Fact]
    public async Task ANewSessionIsAnnouncedForOnePollBeforeItsFirstEvaluation()
    {
        var rig = new Rig();
        rig.Host.Registrations.Add(Reg("s1"));
        var tick = rig.Tick();

        await tick.RunAsync(CancellationToken.None);
        Assert.Empty(rig.Host.Evaluated);
        Assert.Equal(["s1"], tick.Snapshot().Timers);

        rig.Clock.UtcNow = Start.AddMilliseconds(14_899);
        await tick.RunAsync(CancellationToken.None);
        Assert.Empty(rig.Host.Evaluated);

        rig.Clock.UtcNow = Start.AddMilliseconds(14_900);
        await tick.RunAsync(CancellationToken.None);
        Assert.Equal(["s1"], rig.Host.Evaluated);
    }

    [Fact]
    public async Task TheTickReportCountsFastTicksAsNotSlowGivesTheirP95AndStartsOverAndATickThatDidNotRunAddsNone()
    {
        var rig = new Rig();
        var tick = rig.Tick();
        Assert.Null(tick.TakeTickReport());

        await Pass(rig, tick, 3);
        var report = tick.TakeTickReport();

        Assert.NotNull(report);
        Assert.Matches(@"^slow=0 p95=\d+ms$", report);
        Assert.Null(tick.TakeTickReport());

        rig.Owner = false;
        await tick.RunAsync(CancellationToken.None);
        Assert.Null(tick.TakeTickReport());
    }

    internal sealed class CountingMeter : ICpuMeter
    {
        public int Resumes { get; private set; }

        public int Pauses { get; private set; }

        public void Measure(Action body) => body();

        public void OpenStretch() => Resumes++;

        public void CloseStretch() => Pauses++;

        public long? Take() => 0;
    }

    [Fact]
    public async Task TheTickBracketsEverySynchronousStretchWithAMeterAndAnUnownedTickOpensNone()
    {
        var rig = new Rig { Cpu = new CountingMeter() };
        rig.Host.Registrations.Add(Reg("s1"));
        var tick = rig.Tick();

        await Pass(rig, tick, 3);

        Assert.Equal(rig.Cpu.Resumes, rig.Cpu.Pauses);
        Assert.True(rig.Cpu.Resumes >= 6, $"{rig.Cpu.Resumes} stretches over 3 ticks");

        var before = rig.Cpu.Resumes;
        rig.Owner = false;
        await tick.RunAsync(CancellationToken.None);
        Assert.Equal(before, rig.Cpu.Resumes);
    }

    [Fact]
    public async Task TheRecoveryPassReadsThePeerRowsOnlyWhenAFailureNeedsThem()
    {
        var rig = new Rig();
        rig.Host.Registrations.Add(Reg("s1"));
        var tick = rig.Tick();

        await Pass(rig, tick, 4);

        Assert.NotEmpty(rig.Host.Evaluated);
        Assert.Equal(0, rig.Host.PeerRowsCalls);
    }

    [Fact]
    public async Task ARegistrationWithoutAnOrchestratorBoxIsNotKept()
    {
        var rig = new Rig();
        rig.Host.Registrations.Add(Reg("s1", boxes: "implementor@alpha"));
        var tick = rig.Tick();

        await Pass(rig, tick);

        Assert.Empty(tick.Snapshot().Timers);
        Assert.Empty(rig.Host.Evaluated);
    }

    [Fact]
    public void OnlyTheNewestClaudeRegistrationOfAPidCountsAndOthersAreUntouched()
    {
        var older = Reg("old", pid: 7, at: "2026-03-01T10:00:00.000Z");
        var newer = Reg("new", pid: 7, at: "2026-03-01T11:00:00.000Z");
        var otherPid = Reg("p8", pid: 8);
        var adapter = Reg("ad", harness: "codex-appserver", pid: 7, at: "2026-03-01T09:00:00.000Z");

        var kept = WatchTick.TimerRegistrations([older, newer, otherPid, adapter]).Select(static r => r.SessionId);

        Assert.Equal(["new", "p8", "ad"], kept);
    }

    [Fact]
    public async Task ALandedReportIsMailedToTheFirstBoxAndMarkedSurfaced()
    {
        var rig = new Rig();
        rig.Host.Registrations.Add(Reg("s1"));
        rig.Host.Candidates["s1"] = [Landed("req-1")];
        var tick = rig.Tick();

        await Pass(rig, tick);

        var mail = Assert.Single(rig.Host.Mails);
        Assert.Equal("orchestrator@alpha", mail.To);
        Assert.Equal("report-landed", mail.Event);
        Assert.Equal("s1", mail.ToSession);
        Assert.Equal(["req-1"], rig.Host.Surfaced);
        Assert.Empty(rig.Host.Logged);
    }

    [Theory]
    [InlineData("chief@alpha", "orchestrator@alpha")]
    [InlineData("alpha-dep@alpha", null)]
    public async Task DeputyOnDutyTheChiefsTickStillMailsTheTeamsEventsToTheDeputysOrchestratorBox(string box, string? to)
    {
        var rig = new Rig();
        rig.Host.Registrations.Add(Reg("s1", boxes: box));
        var member = new MemberView("alpha-dep", new JsonObject { ["role"] = "deputy-chief" }, true, "working", null, null, "dep-s");
        rig.Host.Owned["s1"] = [new OwnedTeam("alpha", new JsonObject { ["duty"] = new JsonObject { ["deputy"] = "alpha-dep" } }, [member])];
        rig.Host.Candidates["s1"] = [Landed("req-1")];

        await Pass(rig, rig.Tick());

        Assert.Equal(to, rig.Host.Mails.SingleOrDefault()?.To);
    }

    [Fact]
    public async Task ARegistrationWhoseEveryTeamIsHeldIsNotEvaluatedAndMailsNothing()
    {
        var rig = new Rig();
        rig.Host.Registrations.Add(Reg("s1"));
        rig.Host.Candidates["s1"] = [Landed("req-1")];
        rig.Host.HeldTeams.Add("alpha");
        var tick = rig.Tick();

        await Pass(rig, tick, polls: 3);

        Assert.Empty(rig.Host.Mails);
        Assert.Empty(rig.Host.Surfaced);
    }

    [Fact]
    public async Task ARegistrationWithOneTeamStillRunningIsEvaluatedAsBefore()
    {
        var rig = new Rig();
        rig.Host.Registrations.Add(Reg("s1", boxes: ["orchestrator@alpha", "orchestrator@beta"]));
        rig.Host.Candidates["s1"] = [Landed("req-1", toAddr: "impl@alpha")];
        rig.Host.HeldTeams.Add("beta");
        var tick = rig.Tick();

        await Pass(rig, tick);

        Assert.Single(rig.Host.Mails);
    }

    [Fact]
    public async Task AnEventForATeamWhoseBoxIsStaleIsNotRedirectedToTheFreshBoxOfAnotherTeam()
    {
        var rig = new Rig();
        rig.Host.Registrations.Add(Reg("s1", boxes: ["orchestrator@a", "orchestrator@b"]));
        rig.Host.Fresh.Add("/boxes/orchestrator@b");
        rig.Host.Candidates["s1"] = [Landed("req-1", toAddr: "impl@a")];
        var tick = rig.Tick();

        await Pass(rig, tick);

        Assert.DoesNotContain(rig.Host.Mails, static m => m.To == "orchestrator@b");
    }

    [Fact]
    public async Task AnEventForATeamWhoseBoxIsStaleFailsOverToTheFreshBoxOfAnOwnedTeamAndNamesItsOwnTeam()
    {
        var rig = new Rig();
        rig.Host.Registrations.Add(Reg("s1", boxes: ["orchestrator@a", "orchestrator@b"]));
        rig.Host.Fresh.Add("/boxes/orchestrator@b");
        rig.Host.Owned["s1"] = [new OwnedTeam("b", new JsonObject(), [])];
        rig.Host.Candidates["s1"] = [Landed("req-1", toAddr: "impl@a")];

        await Pass(rig, rig.Tick());

        var mail = Assert.Single(rig.Host.Mails);
        Assert.Equal("orchestrator@b", mail.To);
        Assert.StartsWith("for team a: ", mail.Text, StringComparison.Ordinal);
        Assert.Equal("a", mail.Data.Str("team"));
    }

    private static async Task<string> NullTeamEventBox(string[] boxes, string[] owned, string[] fresh, Dictionary<string, double>? beats = null)
    {
        var rig = new Rig();
        rig.Host.Registrations.Add(Reg("s1", boxes: boxes));
        foreach (var f in fresh)
        {
            rig.Host.Fresh.Add("/boxes/" + f);
        }

        foreach (var (box, at) in beats ?? [])
        {
            rig.Host.BeatMs["/boxes/" + box] = at;
        }

        rig.Host.Owned["s1"] = [.. owned.Select(static t => new OwnedTeam(t, new JsonObject(), []))];
        rig.Host.Candidates["s1"] = [Landed("req-1", toAddr: "impl")];

        await Pass(rig, rig.Tick());

        return Assert.Single(rig.Host.Mails).To;
    }

    [Fact]
    public async Task ANullTeamEventPrefersTheRegisteredBoxOfAnOwnedNamedTeamOverTheFirstBox() =>
        Assert.Equal("orchestrator@rc3", await NullTeamEventBox(["orchestrator@test", "orchestrator@rc3"], ["rc3"], []));

    [Fact]
    public async Task ANullTeamEventForASessionOwningNothingByNameGoesToTheFirstBox() =>
        Assert.Equal("orchestrator@alpha", await NullTeamEventBox(["orchestrator@alpha", "orchestrator@beta"], [], []));

    [Fact]
    public async Task ANullTeamEventWithNothingOwnedGoesToTheFreshestBox() =>
        Assert.Equal("orchestrator@beta", await NullTeamEventBox(["orchestrator@alpha", "orchestrator@beta"], [], ["orchestrator@beta"]));

    [Fact]
    public async Task ANullTeamEventGoesToTheOwnedBoxWithTheNewestHeartbeat() =>
        Assert.Equal(
            "orchestrator@c",
            await NullTeamEventBox(
                ["orchestrator@a", "orchestrator@b", "orchestrator@c"],
                ["b", "c"],
                ["orchestrator@a", "orchestrator@b", "orchestrator@c"],
                new Dictionary<string, double> { ["orchestrator@a"] = 900, ["orchestrator@b"] = 100, ["orchestrator@c"] = 200 }));

    [Fact]
    public async Task ANullTeamEventPrefersAStaleOwnedBoxToAFreshUnownedOne() =>
        Assert.Equal("orchestrator@rc3", await NullTeamEventBox(["orchestrator@test", "orchestrator@rc3"], ["rc3"], ["orchestrator@test"]));

    [Fact]
    public async Task AnEventForAnOwnedTeamWithNoRegisteredBoxGoesToItsOwnBoxBeforeAnyFailover()
    {
        var rig = new Rig();
        rig.Host.Registrations.Add(Reg("s1", boxes: ["orchestrator@b"]));
        rig.Host.Fresh.Add("/boxes/orchestrator@b");
        rig.Host.Owned["s1"] = [new OwnedTeam("b", new JsonObject(), []), new OwnedTeam("gamma", new JsonObject(), [])];
        rig.Host.Candidates["s1"] = [Landed("req-1", toAddr: "impl@gamma")];

        await Pass(rig, rig.Tick());

        var mail = Assert.Single(rig.Host.Mails);
        Assert.Equal("orchestrator@gamma", mail.To);
        Assert.DoesNotContain("for team", mail.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEventForAnUnownedTeamWithNoBoxIsDroppedEvenWhenAnOwnedBoxIsFresh()
    {
        var rig = new Rig();
        rig.Host.Registrations.Add(Reg("s1", boxes: ["orchestrator@b"]));
        rig.Host.Fresh.Add("/boxes/orchestrator@b");
        rig.Host.Owned["s1"] = [new OwnedTeam("b", new JsonObject(), [])];
        rig.Host.Candidates["s1"] = [Landed("req-1", toAddr: "impl@zeta")];

        await Pass(rig, rig.Tick());

        Assert.Empty(rig.Host.Mails);
        Assert.Contains(rig.Log, static l => l.Contains("for team zeta has no box in session s1; not delivered", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheFailoverTargetIsTheOwnedBoxWithTheNewestHeartbeat()
    {
        var rig = new Rig();
        rig.Host.Registrations.Add(Reg("s1", boxes: ["orchestrator@a", "orchestrator@b", "orchestrator@c"]));
        rig.Host.Fresh.Add("/boxes/orchestrator@b");
        rig.Host.Fresh.Add("/boxes/orchestrator@c");
        rig.Host.BeatMs["/boxes/orchestrator@b"] = 100;
        rig.Host.BeatMs["/boxes/orchestrator@c"] = 200;
        rig.Host.Owned["s1"] = [new OwnedTeam("b", new JsonObject(), []), new OwnedTeam("c", new JsonObject(), [])];
        rig.Host.Candidates["s1"] = [Landed("req-1", toAddr: "impl@a")];

        await Pass(rig, rig.Tick());

        Assert.Equal("orchestrator@c", Assert.Single(rig.Host.Mails).To);
    }

    [Fact]
    public async Task AnEventForATeamWhoseBoxIsPolledStaysInItsOwnBoxWhateverOtherOwnedBoxesAreFresher()
    {
        var rig = new Rig();
        rig.Host.Registrations.Add(Reg("s1", boxes: ["orchestrator@a", "orchestrator@b"]));
        rig.Host.Fresh.Add("/boxes/orchestrator@a");
        rig.Host.Fresh.Add("/boxes/orchestrator@b");
        rig.Host.BeatMs["/boxes/orchestrator@a"] = 100;
        rig.Host.BeatMs["/boxes/orchestrator@b"] = 200;
        rig.Host.Owned["s1"] = [new OwnedTeam("a", new JsonObject(), []), new OwnedTeam("b", new JsonObject(), [])];
        rig.Host.Candidates["s1"] = [Landed("req-1", toAddr: "impl@a")];

        await Pass(rig, rig.Tick());

        var mail = Assert.Single(rig.Host.Mails);
        Assert.Equal("orchestrator@a", mail.To);
        Assert.DoesNotContain("for team", mail.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEventForAnOwnedTeamWhoseBoxIsStaleGoesToThatTeamsBox()
    {
        var rig = new Rig();
        rig.Host.Registrations.Add(Reg("s1", boxes: ["orchestrator@a", "orchestrator@b"]));
        rig.Host.Fresh.Add("/boxes/orchestrator@b");
        rig.Host.Owned["s1"] = [new OwnedTeam("a", new JsonObject(), [])];
        rig.Host.Candidates["s1"] = [Landed("req-1", toAddr: "impl@a")];
        var tick = rig.Tick();

        await Pass(rig, tick);

        Assert.Equal("orchestrator@a", Assert.Single(rig.Host.Mails).To);
    }

    [Theory]
    [InlineData("impl", "beta", "orchestrator@beta")]
    [InlineData("impl", null, "orchestrator@alpha")]
    [InlineData("impl@alpha", "beta", "orchestrator@alpha")]
    public async Task AnEventIsMailedToTheBoxOfTheTeamTheMemberNamesElseTheRequestsTeamElseTheFirstBox(string toAddr, string? team, string expected)
    {
        var rig = new Rig();
        rig.Host.Registrations.Add(Reg("s1", boxes: ["orchestrator@alpha", "orchestrator@beta"]));
        rig.Host.Candidates["s1"] = [Landed("req-1", toAddr, team)];
        var tick = rig.Tick();

        await Pass(rig, tick);

        Assert.Equal(expected, Assert.Single(rig.Host.Mails).To);
    }

    [Fact]
    public async Task ATextOverThreeThousandCharactersIsLoggedAndMailedAsAPointerWhileExactlyThatLongGoesWhole()
    {
        var rig = new Rig();
        var tick = rig.Tick();
        var longEvent = new WatchEvent(null, "BLOCKED", new string('x', WatchTick.MaxEventTextChars + 1), new JsonObject());
        var exact = new WatchEvent(null, "BLOCKED", new string('x', WatchTick.MaxEventTextChars), new JsonObject());
        var compact = new WatchEvent(null, "BLOCKED", new string('x', 4000), new JsonObject()) { Compact = "short" };

        var (longText, longFlag, pointer) = WatchTick.EventMailText(longEvent, "member-blocked");
        var (exactText, exactFlag, _) = WatchTick.EventMailText(exact, "member-blocked");
        var (compactText, compactFlag, _) = WatchTick.EventMailText(compact, "member-blocked");

        Assert.True(longFlag);
        Assert.Equal(pointer, longText);
        Assert.Equal("member-blocked: full text is in the watch log (too long to mail)", pointer);
        Assert.False(exactFlag);
        Assert.Equal(WatchTick.MaxEventTextChars, exactText.Length);
        Assert.True(compactFlag);
        Assert.Equal("short", compactText);
        Assert.NotNull(tick);
    }

    [Fact]
    public async Task AFailedMailIsLoggedThePointerIsTriedAndTheWatchLogHoldsTheEvent()
    {
        var rig = new Rig();
        rig.Host.Registrations.Add(Reg("s1"));
        rig.Host.Candidates["s1"] = [Landed("req-1")];
        rig.Host.FailMail = static m => !m.Text.StartsWith("report-landed:", StringComparison.Ordinal);
        var tick = rig.Tick();

        await Pass(rig, tick);

        var mail = Assert.Single(rig.Host.Mails);
        Assert.StartsWith("report-landed: full text is in the watch log", mail.Text, StringComparison.Ordinal);
        Assert.Single(rig.Host.Logged);
        Assert.Contains(rig.Log, static l => l.Contains("event LANDED for s1 not mailed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task APointerThatCannotBeMailedEitherIsLoggedAndTheTickCarriesOn()
    {
        var rig = new Rig();
        rig.Host.Registrations.Add(Reg("s1"));
        rig.Host.Candidates["s1"] = [Landed("req-1")];
        rig.Host.FailMail = static _ => true;
        var tick = rig.Tick();

        await Pass(rig, tick);

        Assert.Empty(rig.Host.Mails);
        Assert.Single(rig.Host.Logged);
        Assert.Contains(rig.Log, static l => l.Contains("not mailed either", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFailureInOneSessionDoesNotStopTheOthersAndStillStampsItsTick()
    {
        var rig = new Rig();
        rig.Host.Registrations.Add(Reg("bad", pid: 1));
        rig.Host.Registrations.Add(Reg("good", pid: 2));
        rig.Host.ThrowFor = static sid => sid == "bad";
        rig.Host.Candidates["good"] = [Landed("req-9")];
        var tick = rig.Tick();

        await Pass(rig, tick);

        Assert.Contains("good", rig.Host.Evaluated);
        Assert.Single(rig.Host.Mails);
        Assert.Contains(rig.Log, static l => l.StartsWith("tick failed for bad:", StringComparison.Ordinal));
        Assert.NotEqual(JsonValue.Null.Kind, rig.Observed!.TimerState["bad"].AsObject()["last_tick"].Kind);
    }

    [Fact]
    public async Task ASessionThatLeavesIsDroppedFromTheTimers()
    {
        var rig = new Rig();
        rig.Host.Registrations.Add(Reg("s1"));
        var tick = rig.Tick();
        await tick.RunAsync(CancellationToken.None);
        Assert.Equal(["s1"], tick.Snapshot().Timers);

        rig.Host.Registrations.Clear();
        await tick.RunAsync(CancellationToken.None);

        Assert.Empty(tick.Snapshot().Timers);
    }

    [Fact]
    public async Task ADaemonThatNoLongerOwnsItsFilesDoesNothing()
    {
        var rig = new Rig { Owner = false };
        rig.Host.Registrations.Add(Reg("s1"));
        var tick = rig.Tick();

        await Pass(rig, tick);

        Assert.Empty(tick.Snapshot().Timers);
        Assert.Equal(0, rig.Host.LadderCalls);
    }

    [Fact]
    public async Task TheLadderRunsEveryTickAndNeverOverlapsItself()
    {
        var rig = new Rig();
        var gate = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Host.Ladder = () => gate.Task;
        var tick = rig.Tick();

        var first = tick.RunAsync(CancellationToken.None);
        await tick.RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        gate.SetResult(new JsonObject { ["pending"] = 3 });
        await first;
        rig.Host.Ladder = null;
        await tick.RunAsync(CancellationToken.None);

        Assert.Equal(2, rig.Host.LadderCalls);
    }

    [Fact]
    public async Task AContextReadingAboveTheActThresholdIsMailedAsCritical()
    {
        var rig = new Rig();
        rig.Host.Registrations.Add(Reg("s1"));
        rig.Host.Subjects["s1"] =
        [
            new ContextSubject(
                "m1",
                "ah2-tm3-architect",
                "ah2-tm3-architect@alpha",
                new JsonObject { ["pct"] = 90, ["at"] = Start.AddMinutes(-1).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture) },
                "architect"),
        ];
        var tick = rig.Tick();

        await Pass(rig, tick);

        var mail = Assert.Single(rig.Host.Mails);
        Assert.Equal("context-critical", mail.Event);
        Assert.Equal("ah: ah2-tm3-architect (architect) is at 90% of its context window. Ask it to report now; a compaction may follow.", mail.Text);
        Assert.Equal("architect", mail.Data.Str("role"));
        Assert.Contains(rig.Host.Gates, static g => g.Row.Str("type") == "context-episode");
    }

    private static DispatchCandidate Overdue() =>
        new("req-1", "implementor", "slug", "impl@alpha", "impl", "small", "2026-03-01T11:00:00.000Z", true, "/r.md", false, string.Empty, "/private/tmp/ahprobe.h") { Team = "alpha" };

    private static Rig RigWithOverduePeer(string activity, bool busy)
    {
        var rig = RigWithMember("alpha", activity);
        rig.Host.Candidates["s1"] = [Overdue()];
        rig.Host.Activity["m1"] = new JsonObject
        {
            ["activity"] = activity,
            ["state_since"] = Start.AddMinutes(-10).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture),
        };
        if (busy)
        {
            rig.Host.BusyPeers.Add("impl");
        }

        return rig;
    }

    private static bool Alarmed(Rig rig) => rig.Host.Mails.Any(static m => m.Event != "unlinked-dispatch" && m.Text.Contains("req-1", StringComparison.Ordinal));

    [Fact]
    public async Task AnIdlePeerWhoseReportWasReadIsNeitherCheckedInOnNorResumed()
    {
        var rig = RigWithOverduePeer("idle", busy: false);
        rig.Host.SeededPeers.Add(new JsonObject { ["type"] = "report-acked", ["session_id"] = "s1", ["request_id"] = "req-1" });

        await Pass(rig, rig.Tick());

        Assert.False(Alarmed(rig));
        Assert.DoesNotContain(rig.Host.AppendedPeers, static r => r.Str("type") == "overdue-resume");
    }

    [Fact]
    public async Task AnIdlePeerIsStillCheckedOnWhenOnlyAnotherRequestsReportWasRead()
    {
        var rig = RigWithOverduePeer("idle", busy: false);
        rig.Host.SeededPeers.Add(new JsonObject { ["type"] = "report-acked", ["session_id"] = "s1", ["request_id"] = "req-other" });

        await Pass(rig, rig.Tick());

        Assert.True(Alarmed(rig));
    }

    [Fact]
    public async Task AnIdlePeerTheRosterListsAsBusyIsNotCheckedInOn()
    {
        var rig = RigWithOverduePeer("idle", busy: true);

        await Pass(rig, rig.Tick());

        Assert.False(Alarmed(rig));
    }

    [Fact]
    public async Task AnIdlePeerTheRosterDoesNotListAsBusyIsCheckedInOn()
    {
        var rig = RigWithOverduePeer("idle", busy: false);

        await Pass(rig, rig.Tick());

        Assert.True(Alarmed(rig));
    }

    [Fact]
    public async Task ABlockedPeerTheRosterListsAsBusyIsStillCheckedInOn()
    {
        var rig = RigWithOverduePeer("blocked", busy: true);

        await Pass(rig, rig.Tick());

        Assert.True(Alarmed(rig));
    }

    [Fact]
    public async Task AQueuedItemWithAnIdleMemberIsMailedOnceToTheTeamsBoxAsAQueueStall()
    {
        var rig = new Rig();
        rig.Host.Registrations.Add(Reg("s1", boxes: ["orchestrator@alpha", "orchestrator@beta"]));
        var then = Ah.Core.Time.Timestamps.ToIsoString(Start.AddMinutes(-11));
        MemberView[] members = [new("impl", new JsonObject { ["role"] = "implementor" }, true, "idle", null, then, "m1")];
        rig.Host.Owned["s1"] = [new OwnedTeam("beta", new JsonObject(), members)];
        rig.Host.Checklists["beta"] = [new ChecklistEntry("_run", new PlanItem("stab", "Stabilise", "next", then, null, null, [], null, null, null, Role: "implementor", UpdatedAt: then, StateSince: then))];
        var tick = rig.Tick();

        await Pass(rig, tick, 4);

        var mail = Assert.Single(rig.Host.Mails, static m => m.Event == "queue-stall");
        Assert.Equal("orchestrator@beta", mail.To);
        Assert.StartsWith("ahd alarm: queue-stall — dispatch _run/stab to impl", mail.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHeldTeamRaisesNoQueueStallWhileAnotherOwnedTeamStillDoes()
    {
        var rig = new Rig();
        rig.Host.Registrations.Add(Reg("s1", boxes: TwoTeamBoxes));
        var then = Ah.Core.Time.Timestamps.ToIsoString(Start.AddMinutes(-11));
        MemberView[] members = [new("impl", new JsonObject { ["role"] = "implementor" }, true, "idle", null, then, "m1")];
        rig.Host.Owned["s1"] = [new OwnedTeam("alpha", new JsonObject(), members), new OwnedTeam("beta", new JsonObject(), members)];
        foreach (var team in new[] { "alpha", "beta" })
        {
            rig.Host.Checklists[team] = [new ChecklistEntry("_run", new PlanItem("stab", "Stabilise", "next", then, null, null, [], null, null, null, Role: "implementor", UpdatedAt: then, StateSince: then))];
        }

        rig.Host.HeldTeams.Add("beta");

        await Pass(rig, rig.Tick(), 4);

        var mail = Assert.Single(rig.Host.Mails, static m => m.Event == "queue-stall");
        Assert.Equal("orchestrator@alpha", mail.To);
    }

    [Fact]
    public async Task AnUnlinkedOrchestratorRequestIsMailedOnceToTheTeamsBoxAsAnUnlinkedDispatch()
    {
        var rig = new Rig();
        rig.Host.Registrations.Add(Reg("s1", boxes: ["orchestrator@alpha", "orchestrator@beta"]));
        var created = Ah.Core.Time.Timestamps.ToIsoString(Start.AddMinutes(-3));
        rig.Host.Owned["s1"] = [new OwnedTeam("beta", new JsonObject(), [])];
        rig.Host.Checklists["beta"] = [new ChecklistEntry("_run", new PlanItem("other", "Other", "done", created, null, null, [], null, null, null, UpdatedAt: created, StateSince: created))];
        rig.Host.Candidates["s1"] = [new DispatchCandidate("req-u", "implementor", "make-ui", "impl@beta", "impl", "large", created, true, "/h/req-u.response.md", false, string.Empty, "/h") { Team = "beta" }];
        var tick = rig.Tick();

        await Pass(rig, tick, 4);

        var mail = Assert.Single(rig.Host.Mails, static m => m.Event == "unlinked-dispatch");
        Assert.Equal("orchestrator@beta", mail.To);
        Assert.Contains("req-u (make-ui)", mail.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task APlainSendIsAnUnlinkedDispatchOnlyOnceArmed(bool armed, bool mailed)
    {
        var rig = new Rig();
        rig.Host.Registrations.Add(Reg("s1", boxes: ["orchestrator@beta"]));
        var created = Ah.Core.Time.Timestamps.ToIsoString(Start.AddMinutes(-3));
        rig.Host.Owned["s1"] = [new OwnedTeam("beta", new JsonObject(), [])];
        rig.Host.Checklists["beta"] = [new ChecklistEntry("_run", new PlanItem("other", "Other", "done", created, null, null, [], null, null, null, UpdatedAt: created, StateSince: created))];
        rig.Host.Candidates["s1"] = [new DispatchCandidate("req-u", "implementor", "go", "impl@beta", "impl", "small", created, true, "/h/req-u.response.md", false, string.Empty, "/h") { Team = "beta", PlainSend = true }];
        if (armed)
        {
            rig.Host.SeededPeers.Add(new JsonObject { ["type"] = Liveness.PlainSendArmedRow, ["session_id"] = "s1", ["request_id"] = "req-u", ["at"] = created });
        }

        await Pass(rig, rig.Tick(), 4);

        Assert.Equal(mailed, rig.Host.Mails.Any(static m => m.Event == "unlinked-dispatch"));
    }

    [Fact]
    public async Task AQueueStallWhoseMailFailedIsMailedOnTheNextPass()
    {
        var rig = new Rig();
        rig.Host.Registrations.Add(Reg("s1"));
        var then = Ah.Core.Time.Timestamps.ToIsoString(Start.AddMinutes(-11));
        MemberView[] members = [new("impl", new JsonObject { ["role"] = "implementor" }, true, "idle", null, then, "m1")];
        rig.Host.Owned["s1"] = [new OwnedTeam("alpha", new JsonObject(), members)];
        rig.Host.Checklists["alpha"] = [new ChecklistEntry("_run", new PlanItem("stab", "Stabilise", "next", then, null, null, [], null, null, null, Role: "implementor", UpdatedAt: then, StateSince: then))];
        var failing = true;
        rig.Host.FailMail = _ => failing;
        var tick = rig.Tick();

        await Pass(rig, tick, 3);
        Assert.Empty(rig.Host.Mails);
        failing = false;
        await Pass(rig, tick, 2);

        Assert.Single(rig.Host.Mails, static m => m.Event == "queue-stall");
    }

    [Fact]
    public async Task AQueuedItemWithNoIdleMemberIsNotMailed()
    {
        var rig = new Rig();
        rig.Host.Registrations.Add(Reg("s1"));
        var then = Ah.Core.Time.Timestamps.ToIsoString(Start.AddMinutes(-11));
        MemberView[] members = [new("impl", new JsonObject { ["role"] = "implementor" }, true, "working", null, then, "m1")];
        rig.Host.Owned["s1"] = [new OwnedTeam("alpha", new JsonObject(), members)];
        rig.Host.Checklists["alpha"] = [new ChecklistEntry("_run", new PlanItem("stab", "Stabilise", "next", then, null, null, [], null, null, null, Role: "implementor", UpdatedAt: then, StateSince: then))];
        var tick = rig.Tick();

        await Pass(rig, tick, 4);

        Assert.DoesNotContain(rig.Host.Mails, static m => m.Event == "queue-stall");
    }

    private static readonly string[] TwoTeamBoxes = ["orchestrator@alpha", "orchestrator@beta"];

    private static readonly string FailedAt = Start.AddHours(-1).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);

    private static Rig RigWithMember(string? team, string? activity = null, string? failureError = null)
    {
        var rig = new Rig();
        rig.Host.Registrations.Add(Reg("s1", boxes: TwoTeamBoxes));
        MemberView[] members = [new("impl", new JsonObject { ["role"] = "implementor" }, true, activity, activity == "blocked" ? "permission" : null, null, "m1")];
        rig.Host.Owned["s1"] = [new OwnedTeam(team, new JsonObject(), members)];
        if (failureError is not null)
        {
            rig.Host.Activity["m1"] = new JsonObject { ["activity"] = "failed", ["failed_at"] = FailedAt, ["error"] = failureError };
        }

        return rig;
    }

    [Theory]
    [InlineData("beta", "orchestrator@beta")]
    [InlineData("alpha", "orchestrator@alpha")]
    [InlineData(null, "orchestrator@alpha")]
    public async Task AMemberApiFailureIsMailedToTheBoxOfTheMembersTeam(string? team, string expected)
    {
        var rig = RigWithMember(team, failureError: "invalid_request");

        await Pass(rig, rig.Tick());

        var mail = Assert.Single(rig.Host.Mails, static m => m.Event == "api-failed");
        Assert.Equal(expected, mail.To);
    }

    [Theory]
    [InlineData("beta", "orchestrator@beta")]
    [InlineData(null, "orchestrator@alpha")]
    public async Task AMemberResumeIsMailedToTheBoxOfTheMembersTeam(string? team, string expected)
    {
        var rig = RigWithMember(team, failureError: "overloaded");

        await Pass(rig, rig.Tick());

        var mail = rig.Host.Mails.Last();
        Assert.Equal("member-resume", mail.Event);
        Assert.Equal(expected, mail.To);
    }

    [Fact]
    public async Task AMemberResumeIsOneEntryWithItsRewakeIdThatWithdrawsTheOlderOnesAndNegativeNoIdWithoutATurnEndKey()
    {
        var rig = RigWithMember("beta", failureError: "overloaded");
        rig.Host.Activity["m1"]!["turn_end_key"] = "m1:k1";

        await Pass(rig, rig.Tick());

        var resume = Assert.Single(rig.Host.Mails, static m => m.Event == "resume");
        Assert.Equal("impl@beta", resume.To);
        Assert.Equal(Ah.Core.Digests.RewakePaths.Id(resume.HierarchyDir, "m1", "m1:k1"), resume.Id);
        Assert.Contains(rig.Host.Withdrawals, w => w == ("impl@beta", "m1", resume.Id, null, "superseded"));
        Assert.Contains(rig.Host.Withdrawals, static w => w.Address == "impl@beta" && w.Session is null && w.CreatedBeforeMs is not null && w.Reason == "expired");

        var keyless = RigWithMember("beta", failureError: "overloaded");
        await Pass(keyless, keyless.Tick());
        Assert.Null(Assert.Single(keyless.Host.Mails, static m => m.Event == "resume").Id);
        Assert.DoesNotContain(keyless.Host.Withdrawals, static w => w.Reason == "superseded");
    }

    /// <summary>b412: a tick whose gates read failed resumes no one (negative: the test above, gates read, resumes once).</summary>
    [Fact]
    public async Task ATickWhoseGatesCannotBeReadSkipsRecovery()
    {
        var rig = RigWithMember("beta", failureError: "overloaded");
        rig.Host.Activity["m1"]!["turn_end_key"] = "m1:k1";
        rig.Host.GatesUnreadable = true;

        await Pass(rig, rig.Tick());

        Assert.DoesNotContain(rig.Host.Mails, static m => m.Event is "resume" or "member-resume");
        Assert.DoesNotContain(rig.Host.Gates, static g => g.Row.Str("type") == "resume");
    }

    /// <summary>b426: gates unreadable for 10 minutes of ticks tell the Orchestrator once, with the reason (negative: none before 10 minutes).</summary>
    [Fact]
    public async Task GatesUnreadableForTenMinutesAlarmTheOrchestratorOnce()
    {
        var rig = RigWithMember("beta", failureError: "overloaded");
        rig.Host.GatesUnreadable = true;
        var tick = rig.Tick();
        static IEnumerable<WatchMail> Alarms(Rig r) => r.Host.Mails.Where(static m => m.Event == WatchTick.RecoveryPausedName);

        await Pass(rig, tick, polls: 3);
        Assert.Empty(Alarms(rig));

        rig.Clock.UtcNow = rig.Clock.UtcNow.AddMinutes(10);
        await Pass(rig, tick, polls: 3);

        var alarm = Assert.Single(Alarms(rig));
        Assert.Equal("orchestrator@beta", alarm.To);
        Assert.Contains("rewake recovery is paused: the state database cannot be read (disk I/O error)", alarm.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(rig.Host.Mails, static m => m.Event is "resume" or "member-resume");
    }

    private static Rig RigWithHookRecordedFailure(string hookKind, string transcriptText, double secondsAfterFailure)
    {
        const string Transcript = "/private/tmp/ahprobe.none/.claude/projects/c/s.jsonl";
        var rig = RigWithMember("beta", failureError: hookKind);
        rig.Host.Activity["m1"]!["transcript_path"] = Transcript;
        var at = Start.AddHours(-1).AddSeconds(secondsAfterFailure).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);
        var line = new JsonObject { ["type"] = "assistant", ["isApiErrorMessage"] = true, ["timestamp"] = at, ["uuid"] = "u1", ["message"] = new JsonObject { ["content"] = transcriptText } };
        rig.Files.Tails[Transcript] = JsonText.Stringify(line, 0) + "\n";
        return rig;
    }

    [Fact]
    public async Task ATranscriptWeeklyLimitOnTheSameTurnOverridesAHookRateLimitAndIsRecorded()
    {
        var rig = RigWithHookRecordedFailure("rate_limit", "You've hit your weekly limit", secondsAfterFailure: 5);

        await Pass(rig, rig.Tick());

        Assert.Single(rig.Host.Mails, static m => m.Event == "api-failed");
        Assert.DoesNotContain(rig.Host.Mails, static m => m.Event == "member-resume");
        var (subject, extra) = Assert.Single(rig.Host.RecordedFailures);
        Assert.Equal("m1", subject);
        Assert.Equal("weekly_limit", extra.Str("error"));
        Assert.Equal(FailedAt, extra.Str("failed_at"));
    }

    [Theory]
    [InlineData("rate_limit", "You've hit your weekly limit", 60)] // Another turn's verdict.
    [InlineData("rate_limit", "Rate limited, slow down", 5)] // Not a kind that is never resumed.
    [InlineData("overloaded", "You've hit your weekly limit", 5)] // The hook did not say rate_limit.
    public async Task ATranscriptVerdictDoesNotOverrideOutsideASameTurnRateLimit(string hookKind, string transcriptText, double secondsAfterFailure)
    {
        var rig = RigWithHookRecordedFailure(hookKind, transcriptText, secondsAfterFailure);

        await Pass(rig, rig.Tick());

        Assert.Equal("member-resume", rig.Host.Mails.Last().Event);
        Assert.DoesNotContain(rig.Host.Mails, static m => m.Event == "api-failed");
        Assert.Empty(rig.Host.RecordedFailures);
    }

    [Theory]
    [InlineData("beta", "orchestrator@beta")]
    [InlineData(null, "orchestrator@alpha")]
    public async Task ABlockedMemberIsMailedToTheBoxOfTheMembersTeam(string? team, string expected)
    {
        var rig = RigWithMember(team, activity: "blocked");

        await Pass(rig, rig.Tick(), polls: 1 + BlockedEvents.PollsToWake);

        var mail = Assert.Single(rig.Host.Mails);
        Assert.Equal("member-blocked", mail.Event);
        Assert.Equal(expected, mail.To);
    }

    [Theory]
    [InlineData("/private/tmp/ahprobe.cwd")]
    [InlineData("/private")]
    public async Task TheBlockedAlarmNamesTheRepoRootOfTheTeamsHierarchyNotTheSessionCwd(string sessionCwd)
    {
        var rig = new Rig();
        rig.Host.Registrations.Add(Reg("s1", boxes: TwoTeamBoxes));
        MemberView[] members = [new("impl", new JsonObject { ["role"] = "implementor", ["transport"] = "herdr", ["transport_id"] = "w1:p1" }, true, "blocked", "permission", null, "m1")];
        rig.Host.Owned["s1"] = [new OwnedTeam("alpha", new JsonObject(), members)];
        rig.Host.SessionCwdValue = sessionCwd;
        rig.Host.ScreenHashValue = new string('a', 64);

        await Pass(rig, rig.Tick(), polls: 1 + BlockedEvents.PollsToWake);

        var text = Assert.Single(rig.Host.Mails).Text;
        Assert.Contains("--cwd /private ", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ahprobe.cwd", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEventForAnOwnedTeamWithNoRegisteredBoxGoesToThatTeamsBoxNotTheFirstBox()
    {
        var rig = new Rig();
        rig.Host.Registrations.Add(Reg("s1", boxes: "orchestrator@alpha"));
        MemberView[] members = [new("impl", new JsonObject { ["role"] = "implementor" }, true, "blocked", "permission", null, "m1")];
        rig.Host.Owned["s1"] = [new OwnedTeam("gamma", new JsonObject(), members)];

        await Pass(rig, rig.Tick(), polls: 1 + BlockedEvents.PollsToWake);

        var mail = Assert.Single(rig.Host.Mails);
        Assert.Equal("orchestrator@gamma", mail.To);
    }

    [Fact]
    public async Task AnEventForATeamTheSessionDoesNotOwnAndHasNoBoxForIsNotMailedAndIsLogged()
    {
        var rig = new Rig();
        rig.Host.Registrations.Add(Reg("s1", boxes: TwoTeamBoxes));
        var at = Start.AddMinutes(-1).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);
        rig.Host.Subjects["s1"] = [new ContextSubject("m1", "impl", "impl@delta", new JsonObject { ["pct"] = 90, ["at"] = at }, "implementor")];

        await Pass(rig, rig.Tick());

        Assert.Empty(rig.Host.Mails);
        Assert.Single(rig.Log, static l => l.Contains("for team delta has no box in session s1; not delivered", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AContextAlertIsMailedToTheBoxOfTheMembersTeam()
    {
        var rig = new Rig();
        rig.Host.Registrations.Add(Reg("s1", boxes: TwoTeamBoxes));
        var at = Start.AddMinutes(-1).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);
        rig.Host.Subjects["s1"] = [new ContextSubject("m1", "impl", "impl@beta", new JsonObject { ["pct"] = 90, ["at"] = at }, "implementor")];

        await Pass(rig, rig.Tick());

        var mail = Assert.Single(rig.Host.Mails);
        Assert.Equal("context-critical", mail.Event);
        Assert.Equal("orchestrator@beta", mail.To);
    }

    [Fact]
    public void EveryLiveRegistrationsDirIsWatchedWithNoStatusFileAndAnEmptyOneIsNot()
    {
        // files-off-disk F3a: no status.json probe; an unregistered dir is not watched (the negative)
        var rig = new Rig();
        rig.Host.LiveDirs.AddRange(["/h/with", string.Empty]);
        var tick = rig.Tick();

        tick.RefreshStatusDirs();

        Assert.Equal(["/h/with"], rig.Dirs.Snapshot());
        Assert.False(rig.Dirs.Contains("/h/other"));
    }

    /// <summary>ah-msgs-fast-2 §3.8 item 6: the first pass (daemon start) and a newly seen status dir reach the host once; a dir seen before never does.</summary>
    [Fact]
    public void AStatusDirIsReportedFirstSeenOnce()
    {
        var rig = new Rig();
        rig.Host.LiveDirs.AddRange(["/h/a"]);
        var tick = rig.Tick();

        tick.RefreshStatusDirs();
        tick.RefreshStatusDirs();
        rig.Host.LiveDirs.Add("/h/b");
        tick.RefreshStatusDirs();

        Assert.Equal(["/h/a", "/h/b"], rig.Host.FirstSeen);
    }

    [Fact]
    public void RegisterAddsTheTickAtThePollPeriodAndTheBackstopEveryTwoSeconds()
    {
        var rig = new Rig();
        rig.Vars["AHD_WATCH_POLL_MS"] = "1500";
        var jobs = new FakeJobs();
        var env = new MapEnv(rig.Vars);
        var deps = new WatchDependencies(rig.Host, env, rig.Clock, rig.Files, HierarchyModule.CreateRecovery(rig.Files, env, rig.Clock), rig.Dirs);

        WatchModule.Register(jobs, deps);

        Assert.Equal(["watch-tick", "refresh-status-dirs"], jobs.Jobs.Select(static j => j.Name));
        Assert.Equal(TimeSpan.FromMilliseconds(1500), jobs.Jobs[0].Period);
        Assert.Equal(TimeSpan.FromSeconds(2), jobs.Jobs[1].Period);
    }
}
