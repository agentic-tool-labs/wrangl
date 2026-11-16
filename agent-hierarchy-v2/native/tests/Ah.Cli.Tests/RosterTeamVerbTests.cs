using System.Diagnostics;
using System.Globalization;
using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Commands;
using Ah.Config;
using Ah.Core.Json;
using Ah.Hierarchy;
using Ah.Mesh;
using Ah.Platform;
using Ah.Roster;
using Ah.TestKit;

namespace Ah.Cli.Tests;

/// <summary>
/// The team-creating roster verbs (<c>create</c>, <c>spawn-one</c>, <c>spawn-ad-hoc</c>) over the real libraries and a sandbox
/// hierarchy dir, with every process started by the launch layer going to a scripted runner: each refusal beside the nearest
/// input that is accepted, and what is written on each path.
/// </summary>
[Collection("RosterServices")]
public sealed partial class RosterTeamVerbTests : IDisposable
{
    private readonly Sandbox _sandbox = Sandbox.PlantedFiles();
    private readonly List<Process> _children = [];
    private FakeRunner _runner = new() { Ps = SelfStartOnly };

    /// <summary>The scripted <c>ps</c>: this test process started at <see cref="SelfStart"/>, which the recorded Orchestrator starts name.</summary>
    private static string? SelfStartOnly(string[] argv) => argv.Contains("lstart=") && argv.Contains(Self) ? SelfStart + "\n" : null;

    /// <summary>The process table now shows the running CLI a parent that is neither it nor pid 1, so a pid it does not list reads as gone.</summary>
    private void ObserverSeesAParent()
    {
        var before = _runner.Ps;
        _runner.Ps = argv => before?.Invoke(argv)
            ?? (argv.Contains("ppid=") ? (argv.Contains("9999") ? "1\n" : "9999\n")
                : argv.Contains("lstart=") && argv.Contains("9999") ? SelfStart + "\n" : null);
    }

    public RosterTeamVerbTests()
    {
        Directory.CreateDirectory(Project);
        Directory.CreateDirectory(Hierarchy);
    }

    private string Project => Path.Combine(_sandbox.Root, "proj");

    private string Hierarchy => Path.Combine(_sandbox.Root, "hier");

    private static string Self => Environment.ProcessId.ToString(CultureInfo.InvariantCulture);

    public void Dispose()
    {
        CommandsModule.ComposeRoster(() => null!);
        foreach (var child in _children)
        {
            try
            {
                child.Kill();
            }
            catch (InvalidOperationException)
            {
                // already gone
            }

            child.Dispose();
        }

        _sandbox.Dispose();
    }

    private sealed class RecordingCallers : IRosterCallerRegistry
    {
        internal List<(string Cwd, string? Session, int Pid, string? Pane, string? Team)> Calls { get; } = [];

        public IProcessTable? ProcessTable { get; init; }

        internal bool Throws { get; init; }

        public IReadOnlyList<string> Register(string cwd, string? sessionId, int pid, string? paneId, string? teamArg, bool teamDefaultExplicit)
        {
            Calls.Add((cwd, sessionId, pid, paneId, teamArg));
            if (Throws)
            {
                throw new IOException("mailbox unavailable");
            }

            return [teamArg is null ? "orchestrator@proj" : $"m{paneId}@{teamArg}"];
        }

        public IReadOnlyList<string> OwnedTeams(string cwd, string? sessionId, int pid) => [];

        /// <summary>Live registrations by session: (pid, addresses).</summary>
        internal Dictionary<string, (int Pid, string[] Addresses)> Held { get; } = [];

        public IReadOnlyList<string>? HeldAddresses(string? sessionId, int pid, string? paneId) =>
            sessionId is not null && Held.TryGetValue(sessionId, out var h) && h.Pid == pid ? h.Addresses : null;

        internal List<BoundSession> Bound { get; } = [];

        internal List<string> Cwds { get; } = [];

        public IReadOnlyList<string> RegistrationCwds(int pid) => Cwds;

        internal DateTimeOffset? Started { get; init; }

        public IReadOnlyList<BoundSession> BoundSessions(int pid) => Bound;

        public DateTimeOffset? ProcessStart(int pid) => Started;

        internal string? StartText { get; init; }

        public string? ProcessStartText(int pid) => StartText;

        internal Func<int, int, bool>? Signal { get; init; }

        public bool SignalProcess(int pid, int signal) => Signal?.Invoke(pid, signal) ?? false;

        internal bool OwnerGone { get; init; }

        public bool OwnerProcessIsGone(double pid, string? recordedStart) => OwnerGone;

        internal List<(string Cwd, string Session, int Pid)> Rebinds { get; } = [];

        public IReadOnlyList<string> RebindTeams(string cwd, string sessionId, int pid)
        {
            Rebinds.Add((cwd, sessionId, pid));
            return [];
        }
    }

    private RecordingCallers? _callers;

    private readonly Dictionary<string, string> _extraEnv = [];

    private IRosterEventMail? _eventMail;

    private readonly List<string> _detached = [];

    private bool _withPanes;

    private sealed class Services(
        ServiceGraph graph, IFileReader files, IClock clock, IAhEnvironment environment, string cwd, IRosterCallerRegistry? callers)
        : IRosterCliServices
    {
        private IRosterPanes? _composedPanes;

        public IConfigCore Config => graph.Config;

        public IHomeDocs? HomeDocs => graph.HomeDocs;

        /// <summary>The pane services and hold locks, for the verbs that need them (stop); off by default so every other test runs as before.</summary>
        public bool WithPanes { get; init; }

        public IRosterPanes? Panes => WithPanes ? _composedPanes ??= new PaneComposition(graph, environment, clock) : null;

        public ILockProvider? HoldLocks => WithPanes && !OperatingSystem.IsWindows() ? graph.Io.Locks : null;

        public IRawFileWriter? Writer => WithPanes && !OperatingSystem.IsWindows() ? graph.Io.Writer : null;

        public IRosterSpawnAdmission? SpawnAdmission { get; init; }

        public IRosterCore Roster => graph.Roster;

        public Func<Ah.Hierarchy.StateCalls.IHierarchyStateCalls, Ah.Hierarchy.StateCalls.IHierarchyStateCalls>? WrapStateCalls { get; init; }

        public IRosterEventMail? EventMail { get; init; }

        /// <summary>The command lines a verb started detached; recorded, never run.</summary>
        public List<string>? Detached { get; init; }

        public bool StartDetached(string commandLine, string cwd, string log)
        {
            Detached?.Add(commandLine);
            return Detached is not null;
        }

        public HierarchyServices Hierarchy => WrapStateCalls is null ? graph.Hierarchy : graph.Hierarchy with { StateCalls = WrapStateCalls(graph.Hierarchy.StateCalls) };

        public IAhEnvironment Environment => environment;

        public IClock Clock => clock;

        public Action<TimeSpan>? OnSleep { get; init; }

        public void Sleep(TimeSpan span)
        {
            if (OnSleep is { } step)
            {
                step(span);
            }
            else
            {
                Thread.Sleep(span);
            }
        }

        public IFileReader Files => files;

        public IRecoverRecordStore? RecoverRecords => graph.RecoverRecords;

        public string CurrentDirectory => cwd;

        public int ParentPid => 4242;

        public Ah.Launch.ITeardown? Teardown => graph.Launch.Teardown;

        public IRosterLaunch? Launch => graph.Launch;

        public IRosterCallerRegistry? Callers => callers;

        public IRosterScratch? Scratch => new RosterComposition(graph, clock, environment).Scratch;

        public IHookErrorLog? HookErrors { get; init; }

        public IRosterOwnerProof? OwnerProof { get; init; }

        public bool ExposeMailbox { get; init; }

        public Ah.Mesh.IMailbox? Mailbox => ExposeMailbox ? graph.Mailbox : null;

        public string? MemberBoxRoot(string teamFile) => new RosterComposition(graph, clock, environment).MemberBoxRoot(teamFile);

        public Func<string, string, string, string, BriefSent?>? Briefs { get; init; }

        public BriefSent? SendBrief(string cwd, string member, string team, string refPath) => Briefs?.Invoke(cwd, member, team, refPath);
    }

    private sealed class AllowingProof(Func<NearestSession> nearest, Func<IReadOnlyList<LiveSession>>? live = null, Func<bool>? user = null) : IRosterOwnerProof
    {
        public bool IsUser() => user?.Invoke() ?? false;

        public IReadOnlyList<LiveSession> LiveSessions() => live?.Invoke() ?? [];

        public OwnerProofResult Check(string dir, string? team) => new(true, null, "orchestrator", false, null);

        public OwnerActor Actor() => new(4242, "Thu Oct 15 20:38:50 2026", [], "ah roster gate");

        public NearestSession NearestAncestor(string dir, IReadOnlyList<string> rowDirs) => nearest();
    }

    private const string SelfStart = "2026-10-15T20:38:50.000Z";

    /// <summary>What the owner-proof fake answers when resume asks which session runs the command: by default this test process, as a harness session.</summary>
    private NearestSession _nearest = new(NearestKind.Session, null, null, Environment.ProcessId);

    /// <summary>The caller is the user at a plain terminal: no session above the command.</summary>
    private bool _plainUser;

    private int _nearestAsked;

    private List<LiveSession> _liveSessions = [];

    /// <summary>Use the production owner proof, so the ancestor walk runs over the registrations and team files of the sandbox.</summary>
    private bool _realWalk;

    /// <summary>Wraps the state calls a verb makes, to inject write conflicts.</summary>
    private Func<Ah.Hierarchy.StateCalls.IHierarchyStateCalls, Ah.Hierarchy.StateCalls.IHierarchyStateCalls>? _wrapStateCalls;

    private sealed class RecordedHookErrors : IHookErrorLog
    {
        internal List<string> Rows { get; } = [];

        public void Log(string source, string message) => Rows.Add(source + ": " + message);

        public void Log(HookErrorRecord record) => Rows.Add(record.Hook + ": " + record.Message);
    }

    private readonly RecordedHookErrors _hookErrors = new();

    /// <summary>Whether the graph's liveness answers <c>PidRunning</c> with its real seam (one-liveness.md) instead of the pid alone.</summary>
    private bool _realPidRunning;

    /// <summary>
    /// The graph's liveness with the harness-command half of its pid check left out: these tests stand a test host or a
    /// <c>sleep</c> child in for a session, and neither runs a harness. Registrations are still judged by the real predicate.
    /// </summary>
    private sealed class PidOnlyRows(Ah.Hierarchy.IMemberLiveness inner, Ah.Abstractions.IProcessInspector processes, bool realPidRunning = false) : Ah.Hierarchy.IMemberLiveness
    {
        public Ah.Hierarchy.MemberRegistrationLiveness? ForMember(string memberName, string team) => inner.ForMember(memberName, team);

        public bool PidIdentityLive(int pid, string? recordedStart) => processes.PidAlive(pid);

        /// <summary>The graph's own process seam when a test asks for it; otherwise the pid alone, as <see cref="PidIdentityLive"/>.</summary>
        public bool PidRunning(int pid, string? recordedStart, Ah.Core.Time.LivenessPolicy policy, bool claudeHarness = true) =>
            realPidRunning ? inner.PidRunning(pid, recordedStart, policy, claudeHarness) : pid > 0 && processes.PidAlive(pid);

        public (Ah.Core.Time.LivenessVerdict Verdict, string Why) JudgePeerRow(Ah.Core.Time.PeerRowKind kind, long? pid, string? start, DateTimeOffset? rowAt, double? seenAgeSeconds, DateTimeOffset? lastLifeAt) =>
            Ah.TestKit.PeerRowJudge.Judge(kind, pid, start, rowAt, seenAgeSeconds, lastLifeAt, pid is > 0 and <= int.MaxValue && processes.PidAlive((int)pid.Value));
    }

    /// <summary>What the registrations say about each member (keyed <c>name@team</c>); a member not in it has no registration.</summary>
    private sealed class ScriptedRows(Dictionary<string, Ah.Hierarchy.MemberRegistrationLiveness> rows, Dictionary<string, Ah.Hierarchy.MemberRegistrationLiveness?> later, Ah.Abstractions.IProcessInspector processes) : Ah.Hierarchy.IMemberLiveness
    {
        private readonly HashSet<string> _asked = [];

        /// <summary>The scripted row for the first question about a member; a member in <c>later</c> answers that row (or none) from its second question on.</summary>
        public Ah.Hierarchy.MemberRegistrationLiveness? ForMember(string memberName, string team)
        {
            var key = memberName + "@" + team;
            return !_asked.Add(key) && later.TryGetValue(key, out var again) ? again : rows.GetValueOrDefault(key);
        }

        public bool PidIdentityLive(int pid, string? recordedStart) => processes.PidAlive(pid);

        public (Ah.Core.Time.LivenessVerdict Verdict, string Why) JudgePeerRow(Ah.Core.Time.PeerRowKind kind, long? pid, string? start, DateTimeOffset? rowAt, double? seenAgeSeconds, DateTimeOffset? lastLifeAt) =>
            Ah.TestKit.PeerRowJudge.Judge(kind, pid, start, rowAt, seenAgeSeconds, lastLifeAt, pid is > 0 and <= int.MaxValue && processes.PidAlive((int)pid.Value));
    }

    /// <summary>What the multiplexer says about every pane (<c>Default</c>, gone unless a test says otherwise), and what the process table says about a harness below a pane's root.</summary>
    private sealed class FakePanes : IPaneState
    {
        internal PaneKnowledge Default { get; set; } = PaneKnowledge.Absent;

        internal bool? Harness { get; set; } = false;

        internal JsonArray? AgentRows { get; set; }

        public JsonArray? Agents() => AgentRows;

        /// <summary>A present pane the listing carries no root pid for (herdr could not read its process).</summary>
        internal bool NoRoot { get; set; }

        internal int Listed { get; private set; }

        /// <summary>The reason an Unknown reading carries.</summary>
        internal string? Reason { get; set; }

        public PaneReading Pane(string transport, string transportId)
        {
            Listed++;
            return new PaneReading(Default, Default == PaneKnowledge.Present && !NoRoot ? 4242 : null, Default == PaneKnowledge.Unknown ? Reason : null);
        }

        /// <summary>The panes of a tab, read when asked; null (the default) is a listing that could not be had.</summary>
        internal Func<string, IReadOnlyList<(string Pane, int? Pid)>?>? TabSource { get; set; }

        /// <summary>Panes whose process tree holds a harness; every other pane's does not.</summary>
        internal HashSet<string> HarnessPanes { get; } = [];

        private readonly Dictionary<int, string> _paneOfPid = [];

        public IReadOnlyList<TabPane>? TabPanes(string transport, string tabId) =>
            TabSource?.Invoke(tabId)?.Select(p =>
            {
                if (p.Pid is { } pid)
                {
                    _paneOfPid[pid] = p.Pane;
                }

                return new TabPane(p.Pane, p.Pid);
            }).ToList();

        public bool? HarnessRunning(int rootPid) => _paneOfPid.TryGetValue(rootPid, out var pane) && HarnessPanes.Contains(pane) ? true : Harness;

        internal int Refreshed { get; private set; }

        /// <summary>The state every pane reads as after the first refresh, when set.</summary>
        internal PaneKnowledge? AfterRefresh { get; set; }

        public void Refresh()
        {
            Refreshed++;
            if (AfterRefresh is { } next)
            {
                Default = next;
            }
        }
    }

    private readonly FakePanes _panes = new();


    private IPaneState? _graphPanes;

    private Dictionary<string, Ah.Hierarchy.MemberRegistrationLiveness>? _registrations;

    /// <summary>Runs on each composed service graph before the roster verb, so a test can write real registrations the real liveness judges.</summary>
    private Action<ServiceGraph>? _seedGraph;
    private bool _exposeMailbox;

    private IRosterSpawnAdmission? _spawnAdmission;

    private readonly Dictionary<string, Ah.Hierarchy.MemberRegistrationLiveness?> _registrationsLater = [];

    private (int Code, string Stdout, string Stderr) Roster(params string[] args)
    {
        ComposeServices();
        var verb = CommandsModule.Registry.Verbs.Single(v => v.Name == "roster");
        var stdout = new StringWriter(CultureInfo.InvariantCulture);
        var stderr = new StringWriter(CultureInfo.InvariantCulture);
        var code = _sandbox.Mirrored(() => verb.Run(args, stdout, stderr));
        return (code, stdout.ToString().Replace("\r\n", "\n", StringComparison.Ordinal), stderr.ToString().Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    private Services ComposeServices()
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        // what ahd's start would do: files a test planted reach the store through the import pass (files-off-disk §4: a read no longer takes them)
        foreach (var dir in Ah.StateDb.StateDbModule.FilesImportDirs(_sandbox.AhHome, [Hierarchy]))
        {
            _ = Ah.StateDb.StateDbModule.ImportHierDirFiles(_sandbox.AhHome, dir, force: true);
        }

        var variables = new Dictionary<string, string?>
        {
            ["AH_TEST"] = "1",
            ["AH_HOME"] = _sandbox.AhHome,
            ["HOME"] = _sandbox.Home,
            ["WRANGL_HIERARCHY_DIR"] = Hierarchy,
        };
        foreach (var (name, value) in _extraEnv)
        {
            variables[name] = value;
        }

        var fake = new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables, new PluginInfo(_sandbox.PluginRoot, "wrangl", "0.134.10", "1"));
        var io = PlatformModule.CreateIo(null);
        var graph = ServiceGraph.Build(io, fake, _runner, new FakeDaemonClient(), msgSource: _msgSource);
        _seedGraph?.Invoke(graph);
        graph.Hierarchy.Core.Liveness = _registrations is { } scripted
            ? new ScriptedRows(scripted, _registrationsLater, io.Processes)
            : new PidOnlyRows(graph.Hierarchy.Core.Liveness!, io.Processes, _realPidRunning);
        graph.Launch.PaneState = _graphPanes ?? _panes;
        var services = new Services(graph, io.Files, (IClock?)_stepClock ?? io.Clock, fake, Project, _callers) { OnSleep = _stepClock is null ? null : span => { _stepClock.UtcNow += span; _onQuitSleep?.Invoke(); }, ExposeMailbox = _exposeMailbox, WithPanes = _withPanes, SpawnAdmission = _spawnAdmission, WrapStateCalls = _wrapStateCalls, EventMail = _eventMail, Detached = _detached, HookErrors = _hookErrors, Briefs = SendBriefFake, OwnerProof = _realWalk ? new RosterComposition(graph, io.Clock, fake).OwnerProof : new AllowingProof(() =>
        {
            _nearestAsked++;
            return _nearest;
        }, () => _liveSessions, () => _plainUser) };
        CommandsModule.ComposeRoster(() => services);
        return services;
    }

    private int LivePidOfAnotherProcess()
    {
        var start = new ProcessStartInfo("sleep", "120") { UseShellExecute = false, RedirectStandardOutput = true };
        var child = Process.Start(start) ?? throw new InvalidOperationException("sleep did not start");
        _children.Add(child);
        return child.Id;
    }

    private string TeamPath(string name) => Path.Combine(Hierarchy, "teams", name + ".json");

    private void WriteTeam(string name, string json)
    {
        _sandbox.PlantTeam(TeamPath(name), json);
        ProveMembersGone(name, json);
    }

    /// <summary>
    /// A member nothing was said about is undecided, and undecided is held. A fixture that wants its members gone says so with an old
    /// <c>down</c> row naming a process that has exited; a row written later by the test, or a registration, outranks it.
    /// </summary>
    /// <summary>The pid of a process that has started and exited, so nothing is running under it.</summary>
    private int GonePid()
    {
        // each call in a test gets a pid of its own; one started for an earlier test serves again while nothing has taken its number
        var at = _gonePids++;
        lock (GonePids)
        {
            if (at >= GonePids.Count)
            {
                GonePids.Add(StartAndExit());
            }
            else if (IsRunning(GonePids[at]))
            {
                GonePids[at] = StartAndExit();
            }

            return GonePids[at];
        }

        static int StartAndExit()
        {
            using var gone = Process.Start(new ProcessStartInfo("true") { UseShellExecute = false }) ?? throw new InvalidOperationException("true did not start");
            gone.WaitForExit();
            return gone.Id;
        }

        static bool IsRunning(int pid)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }
    }

    private static readonly List<int> GonePids = [];

    private int _gonePids;

    private void ProveMembersGone(string team, string json)
    {
        if (JsonText.Parse(json).Member("members") is not { IsArray: true } members)
        {
            return;
        }

        var existing = string.Join("\n", _sandbox.PeerLines(Hierarchy));
        foreach (var member in members.AsArray())
        {
            if (member.Member("name") is { IsText: true } who && !existing.Contains($"\"name\":\"{who.AsString()}\"", StringComparison.Ordinal))
            {
                AppendGoneRow(who.AsString(), team);
            }
        }
    }

    /// <summary>An old <c>down</c> row for <paramref name="name"/> that names a process which has exited: proof the member's session is gone.</summary>
    private void AppendGoneRow(string name, string team, string? pool = null)
    {
        pool ??= Hierarchy;
        Directory.CreateDirectory(pool);
        _sandbox.PlantPeers(
            pool,
            $"{{\"type\":\"peer\",\"status\":\"down\",\"name\":\"{name}\",\"role\":\"implementor\",\"session_id\":\"gone-{name}\",\"pid\":{GonePid()},\"pid_start\":\"2026-01-01T00:00:00.000Z\",\"team\":\"{team}\",\"ts\":\"2026-01-01T00:00:00.000Z\"}}\n");
    }

    private string[] Tree() => Directory.EnumerateFiles(_sandbox.Root, "*", SearchOption.AllDirectories)
        .Select(p => Path.GetRelativePath(_sandbox.Root, p))
        .Order(StringComparer.Ordinal)
        .ToArray();

    // ---------------------------------------------------------------- create: mode and arguments

    [Fact]
    public void CreateRefusesTwoModesAtOnceAndWritesNothing()
    {
        var before = Tree();

        var (code, stdout, stderr) = Roster("create", "--plan", "--commit");

        Assert.Equal(2, code);
        Assert.Equal(string.Empty, stdout);
        Assert.Equal("ah roster: create: --plan, --commit, --spawn, and --yes are mutually exclusive\n", stderr);
        Assert.Equal(before, Tree());
    }

    [Fact]
    public void CommitNeedsItsVerifiedListThenItsTransportThenItsRosterLevel()
    {
        var (verifiedCode, _, verifiedErr) = Roster("create", "--commit");
        var (transportCode, _, transportErr) = Roster("create", "--commit", "--verified", "[]");
        var (levelCode, _, levelErr) = Roster("create", "--commit", "--verified", "[]", "--transport", "terminal");

        Assert.Equal(2, verifiedCode);
        Assert.Equal("ah roster: --commit needs --verified <json array>\n", verifiedErr);
        Assert.Equal(2, transportCode);
        Assert.Equal("ah roster: --commit needs --transport\n", transportErr);
        Assert.Equal(2, levelCode);
        Assert.Equal("ah roster: --commit needs --roster-level\n", levelErr);
        Assert.False(_sandbox.TeamExists(TeamPath("proj")));
    }

    [Theory]
    [InlineData("{}", "object")]
    [InlineData("\"x\"", "string")]
    [InlineData("3", "number")]
    [InlineData("null", "object")]
    public void CommitRefusesAVerifiedListThatIsNotAnArray(string verified, string typeName)
    {
        var (code, _, stderr) = Roster("create", "--commit", "--verified", verified, "--transport", "terminal", "--roster-level", "repo");

        Assert.Equal(2, code);
        Assert.Equal($"ah roster: create --commit: --verified must be a JSON array, got {typeName}\n", stderr);
    }

    [Fact]
    public void CommitRefusesAVerifiedListThatMixesNamesAndObjects()
    {
        var (code, _, stderr) = Roster(
            "create", "--commit", "--verified", "[\"proj-implementor\",{\"role\":\"implementor\"}]", "--transport", "terminal",
            "--roster-level", "repo", "--orchestrator-pid", Self);

        Assert.Equal(2, code);
        Assert.Equal("ah roster: create --commit: --verified must be either all member objects or all member-name strings, not a mix\n", stderr);
        Assert.False(_sandbox.TeamExists(TeamPath("proj")));
    }

    [Fact]
    public void CommitRefusesAnOwnerPidThatIsNotAliveAndAcceptsOneThatIs()
    {
        var args = new[] { "create", "--commit", "--verified", "[]", "--transport", "terminal", "--roster-level", "repo" };

        var (deadCode, _, deadErr) = Roster([.. args, "--orchestrator-pid", "99999999"]);
        var (liveCode, liveOut, _) = Roster([.. args, "--orchestrator-pid", Self]);

        Assert.Equal(2, deadCode);
        Assert.StartsWith($"ah roster create: --orchestrator-pid names pid 99999999, but this command runs under pid {Self}; nothing was changed.\n", deadErr, StringComparison.Ordinal);
        Assert.Equal(0, liveCode);
        var written = JsonText.Parse(liveOut);
        Assert.True(written.Member("committed")!.Value.AsBool());
        Assert.True(_sandbox.TeamExists(TeamPath("proj")));
    }

    [Fact]
    public void CommitRefusesAnOwnerPidThatIsNotAnInteger()
    {
        var (code, _, stderr) = Roster(
            "create", "--commit", "--verified", "[]", "--transport", "terminal", "--roster-level", "repo", "--orchestrator-pid", "abc");

        Assert.Equal(2, code);
        Assert.StartsWith($"ah roster create: --orchestrator-pid names pid abc, but this command runs under pid {Self}; nothing was changed.\n", stderr, StringComparison.Ordinal);
        Assert.False(_sandbox.TeamExists(TeamPath("proj")));
    }

    [Fact]
    public void CommitRecordsTheTeamTheCommitterOwnsWithItsHistoryRow()
    {
        var (code, stdout, _) = Roster(
            "create", "--commit", "--verified", "[]", "--transport", "terminal", "--roster-level", "repo", "--orchestrator-pid", Self,
            "--session", "sess-1");

        Assert.Equal(0, code);
        var result = JsonText.Parse(stdout);
        var team = result.Member("team")!.Value;
        Assert.Equal("terminal", team.Member("transport")!.Value.AsString());
        Assert.Equal("repo", team.Member("roster_level")!.Value.AsString());
        Assert.Equal("sess-1", team.Member("orchestrator")!.Value.Member("session_id")!.Value.AsString());
        Assert.Empty(team.Member("members")!.Value.AsArray());
        Assert.Equal("auto", team.Member("layout")!.Value.AsString());
        Assert.Null(result.Member("needs_resync"));
        var history = JsonText.Parse(Roster("history").Stdout);
        Assert.Single(history.Member("teams")!.Value.AsArray());
        Assert.False(File.Exists(Path.Combine(Hierarchy, "team-history.json")));
    }

    /// <summary>files-off-disk F8a: a history file with no row is listed, and the next create carries its entries into the row; the file is left alone.</summary>
    [Fact]
    public void APlantedHistoryFileWithNoRowIsListedAndTheNextCreateKeepsItsEntries()
    {
        var file = Path.Combine(Hierarchy, "team-history.json");
        const string planted = "{\"version\":1,\"teams\":[{\"id\":\"h1\",\"fingerprint\":\"old\",\"alias\":\"old\",\"last_used\":\"2000-01-01T00:00:00\",\"members\":[]}]}";
        File.WriteAllText(file, planted);
        Assert.Equal("h1", JsonText.Parse(Roster("history").Stdout).Member("teams")!.Value.AsArray()[0].Member("id")!.Value.AsString());

        var (code, _, _) = Roster("create", "--commit", "--verified", "[]", "--transport", "terminal", "--roster-level", "repo", "--orchestrator-pid", Self);

        Assert.Equal(0, code);
        var ids = JsonText.Parse(Roster("history").Stdout).Member("teams")!.Value.AsArray().Select(t => t.Member("id")!.Value.AsString()).ToList();
        Assert.Equal(2, ids.Count);
        Assert.Contains("h1", ids);
        Assert.Equal(planted, File.ReadAllText(file));
    }

    [Fact]
    public void CommitNeverWritesAnUnclaimedOrchestratorRecord()
    {
        var (code, _, _) = Roster("create", "--commit", "--verified", "[]", "--transport", "terminal", "--roster-level", "repo", "--orchestrator-pid", Self);

        Assert.Equal(0, code);
        var orchestrator = JsonText.Parse(_sandbox.TeamText(TeamPath("proj"))).Member("orchestrator")!.Value;
        Assert.True(Ah.Roster.RosterModule.OrchestratorIsClaimed(orchestrator), JsonText.Stringify(orchestrator, 0));
        Assert.True(orchestrator.Member("pid")!.Value.AsNumber() > 1);
    }

    [Fact]
    public void CommitOfNamesOnlyAsksForAResyncAndRefusesANameTheRosterLacks()
    {
        var common = new[] { "create", "--commit", "--transport", "terminal", "--roster-level", "repo", "--orchestrator-pid", Self };

        var (code, _, stderr) = Roster([.. common, "--verified", "[\"proj-nobody\"]"]);

        Assert.Equal(2, code);
        Assert.Equal(
            "ah roster: create --commit: --verified names no member \"proj-nobody\" in the repo roster — it defines: (none)\n", stderr);
        Assert.False(_sandbox.TeamExists(TeamPath("proj")));
    }

    [Fact]
    public void CommitRefusesATeamAnotherLiveOrchestratorOwnsAndAcceptsItsOwnOwner()
    {
        var other = LivePidOfAnotherProcess();
        const string OtherStart = "2026-10-15T21:00:00.000Z";
        var otherText = other.ToString(CultureInfo.InvariantCulture);
        _runner.Ps = argv => argv.Contains("lstart=") && argv.Contains(otherText) ? OtherStart + "\n" : SelfStartOnly(argv);
        var created = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture);
        var team = $$"""{"version":1,"team_id":"t-other","created":"{{created}}","roster_level":"repo","transport":"terminal","orchestrator":{"session_id":"sess-other","pid":{{other}},"pid_start":"{{OtherStart}}"},"members":[],"expected_root":"{{Project}}","roster":null,"layout":"auto"}""";
        WriteTeam("proj", team);
        var args = new[] { "create", "--commit", "--verified", "[]", "--transport", "terminal", "--roster-level", "repo", "--team", "proj" };

        var (code, _, stderr) = Roster(args);

        Assert.Equal(2, code);
        Assert.Contains("is owned by another live orchestrator", stderr, StringComparison.Ordinal);
        Assert.Equal(team, _sandbox.TeamText(TeamPath("proj")));

        _nearest = new NearestSession(NearestKind.Session, null, null, other);
        var (ownCode, _, _) = Roster(args);
        Assert.Equal(0, ownCode);

        _nearest = new NearestSession(NearestKind.Session, null, null, Environment.ProcessId);
        WriteTeam("proj", team.Replace(created, "2026-01-01T00:00:00", StringComparison.Ordinal));
        var (staleCode, _, _) = Roster(args);
        Assert.Equal(0, staleCode);
    }

    [Fact]
    public void CommitRefusesAMemberThatCheckedInToAnotherTeam()
    {
        const string Member = "{\"role\":\"implementor\",\"name\":\"proj-implementor\",\"route\":\"peer\",\"model\":\"sonnet\",\"transport_id\":\"%1\",\"team\":\"other\"}";
        var common = new[] { "create", "--commit", "--transport", "tmux", "--roster-level", "repo", "--orchestrator-pid", Self, "--team", "proj" };

        var (code, _, stderr) = Roster([.. common, "--verified", $"[{Member}]"]);

        Assert.Equal(2, code);
        Assert.Contains("proj-implementor checked in to team \"other\", but this commit writes team \"proj\"", stderr, StringComparison.Ordinal);
        Assert.False(_sandbox.TeamExists(TeamPath("proj")));

        var (okCode, okOut, _) = Roster([.. common, "--verified", $"[{Member.Replace("\"other\"", "\"proj\"", StringComparison.Ordinal)}]"]);
        Assert.Equal(0, okCode);
        Assert.False(JsonText.Parse(okOut).Member("team")!.Value.Member("members")!.Value.AsArray()[0].Member("team") is not null);
    }

    [Fact]
    public void CommitRefusesALayoutModeItDoesNotKnowAndRecordsAKnownOne()
    {
        var common = new[]
        {
            "create", "--commit", "--verified", "[]", "--transport", "terminal", "--roster-level", "repo", "--orchestrator-pid", Self,
        };

        var (badCode, _, badErr) = Roster([.. common, "--mode", "diagonal"]);
        Assert.Equal(2, badCode);
        Assert.Equal("ah roster: --mode must be one of auto, columns, grid, got \"diagonal\"\n", badErr);
        Assert.False(_sandbox.TeamExists(TeamPath("proj")));

        var (code, stdout, _) = Roster([.. common, "--mode", "grid"]);
        Assert.Equal(0, code);
        Assert.Equal("grid", JsonText.Parse(stdout).Member("team")!.Value.Member("layout")!.Value.AsString());
    }

    private string GlobalConfig => Path.Combine(_sandbox.Home, ".claude", "ah2", "agent-hierarchy.json");

    private static string[] CommitWith(params string[] extra) => [
        "create", "--commit", "--verified", "[]", "--transport", "terminal", "--roster-level", "repo", "--orchestrator-pid", Self, .. extra,
    ];

    [Fact]
    public void AnExplicitModeBecomesTheGlobalDefaultAndKeepsTheOtherKeys()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(GlobalConfig)!);
        File.WriteAllText(GlobalConfig, "{\"version\":1,\"notes\":\"kept\"}");

        var (code, _, stderr) = Roster(CommitWith("--mode", "grid"));

        Assert.True(code == 0, stderr);
        Assert.DoesNotContain("was not stored", stderr, StringComparison.Ordinal);
        var stored = JsonText.Parse(File.ReadAllText(GlobalConfig));
        Assert.Equal("grid", stored.Member("teamLayout")!.Value.AsString());
        Assert.Equal("kept", stored.Member("notes")!.Value.AsString());
        Assert.Equal(1, (int)stored.Member("version")!.Value.AsNumber());
    }

    [Fact]
    public void AnExplicitModeCreatesTheGlobalFileWhenThereIsNone()
    {
        Assert.False(File.Exists(GlobalConfig));

        var (code, _, stderr) = Roster(CommitWith("--mode", "columns"));

        Assert.Equal(0, code);
        Assert.DoesNotContain("was not stored", stderr, StringComparison.Ordinal);
        var stored = JsonText.Parse(File.ReadAllText(GlobalConfig));
        Assert.Equal("columns", stored.Member("teamLayout")!.Value.AsString());
        Assert.Equal(1, (int)stored.Member("version")!.Value.AsNumber());
    }

    [Fact]
    public void ACreateWithNoModeStoresNothing()
    {
        var (code, _, _) = Roster(CommitWith());

        Assert.Equal(0, code);
        Assert.False(File.Exists(GlobalConfig));
    }

    [Fact]
    public void AModeAlreadyStoredLeavesTheFileUntouched()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(GlobalConfig)!);
        const string Compact = "{\"teamLayout\":\"grid\"}";
        File.WriteAllText(GlobalConfig, Compact);

        var (code, _, stderr) = Roster(CommitWith("--mode", "grid"));

        Assert.Equal(0, code);
        Assert.DoesNotContain("was not stored", stderr, StringComparison.Ordinal);
        Assert.Equal(Compact, File.ReadAllText(GlobalConfig));
    }

    [Fact]
    public void AGlobalFileThatDoesNotParseIsLeftAloneAndTheCreateStillSucceeds()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(GlobalConfig)!);
        const string Broken = "{not json";
        File.WriteAllText(GlobalConfig, Broken);

        var (code, stdout, stderr) = Roster(CommitWith("--mode", "grid"));

        Assert.Equal(0, code);
        Assert.Contains("layout \"grid\" was not stored as the default for future teams", stderr, StringComparison.Ordinal);
        Assert.Equal(Broken, File.ReadAllText(GlobalConfig));
        Assert.Equal("grid", JsonText.Parse(stdout).Member("team")!.Value.Member("layout")!.Value.AsString());
    }

    [Fact]
    public void AGlobalFileThatIsNotAnObjectIsLeftAlone()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(GlobalConfig)!);
        File.WriteAllText(GlobalConfig, "[1]");

        var (code, _, stderr) = Roster(CommitWith("--mode", "grid"));

        Assert.Equal(0, code);
        Assert.Contains("is not a JSON object", stderr, StringComparison.Ordinal);
        Assert.Equal("[1]", File.ReadAllText(GlobalConfig));
    }

    // ---------------------------------------------------------------- registration of a committed team

    private void PeersRow(string pane, int pid, string status = "up", string ts = "2026-03-01T12:00:00.000Z")
    {
        var row = $"{{\"type\":\"peer\",\"status\":\"{status}\",\"role\":\"x\",\"session_id\":\"sess-{pane}\",\"pid\":{pid},\"pane_id\":\"{pane}\",\"ts\":\"{ts}\"}}\n";
        _sandbox.PlantPeers(Hierarchy, row);
    }

    private static string[] CommitTwoPeers() => ["create", "--commit", "--verified", $"[{TwoPeers}]", "--transport", "tmux", "--roster-level", "repo",
        "--orchestrator-pid", Self, "--session", "sess-1", "--team", "proj"];

    [Fact]
    public void CommitRegistersTheOrchestratorAndEveryMemberWithAPeersRow()
    {
        _callers = new RecordingCallers();
        var first = LivePidOfAnotherProcess();
        var second = LivePidOfAnotherProcess();
        PeersRow("%1", first);
        PeersRow("%2", second);

        var (code, stdout, stderr) = Roster(CommitTwoPeers());

        Assert.True(code == 0, stderr);
        var calls = _callers.Calls;
        Assert.Equal(3, calls.Count);
        Assert.Equal((Project, "sess-1", Environment.ProcessId, null, null), (calls[0].Cwd, calls[0].Session, calls[0].Pid, calls[0].Pane, calls[0].Team));
        Assert.Equal(("sess-%1", first, "%1", "proj"), (calls[1].Session, calls[1].Pid, calls[1].Pane, calls[1].Team));
        Assert.Equal(("sess-%2", second, "%2", "proj"), (calls[2].Session, calls[2].Pid, calls[2].Pane, calls[2].Team));
        var registered = JsonText.Parse(stdout).Member("registered")!.Value.AsArray().Select(a => a.AsString()).ToArray();
        Assert.Equal(["orchestrator@proj", "m%1@proj", "m%2@proj"], registered);
    }

    // ---------------------------------------------------------------- the orchestrator's session id

    private static readonly string[] CommitNoSession = ["create", "--commit", "--verified", "[]", "--transport", "terminal", "--roster-level", "repo",
        "--orchestrator-pid", Self];

    /// <summary>A session-pids row through <c>session-pids.append</c>, stamped now (per-dir-files-to-db S1: the table is its only home).</summary>
    private void SessionPidsRow(string session, int pid, string? dir = null)
    {
        Directory.CreateDirectory(dir ?? Hierarchy);
        ComposeServices().Hierarchy.Status.AppendSessionPid(dir ?? Hierarchy, JsonValue.FromString(session), JsonValue.FromNumber(pid));
    }

    private static JsonValue OrchestratorOf(string stdout) => JsonText.Parse(stdout).Member("team")!.Value.Member("orchestrator")!.Value;

    private static string[] WarningsOf(string stdout) =>
        JsonText.Parse(stdout).Member("warnings") is { IsArray: true } w ? w.AsArray().Select(x => x.AsString()).ToArray() : [];

    private string TeamOrchestratorSession() =>
        JsonText.Parse(_sandbox.TeamText(TeamPath("proj"))).Member("orchestrator")!.Value.Member("session_id") is { IsText: true } sid ? sid.AsString() : "<none>";

    private static readonly DateTimeOffset Started = DateTimeOffset.UtcNow.AddHours(-1);

    [Fact]
    public void CommitRecordsTheOrchestratorSessionWithoutSessionFlagOrEnv()
    {
        _callers = new RecordingCallers { Started = Started };
        _callers.Bound.Add(new BoundSession("sess-bound", DateTimeOffset.UtcNow));

        var (code, stdout, stderr) = Roster(CommitNoSession);

        Assert.True(code == 0, stderr);
        Assert.Equal("sess-bound", OrchestratorOf(stdout).Member("session_id")!.Value.AsString());
        Assert.Empty(WarningsOf(stdout));
    }

    [Fact]
    public void CommitWithAnExplicitSessionStillWritesIt()
    {
        _callers = new RecordingCallers { Started = Started };

        var (code, stdout, _) = Roster([.. CommitNoSession, "--session", "sess-flag"]);

        Assert.Equal(0, code);
        Assert.Equal("sess-flag", OrchestratorOf(stdout).Member("session_id")!.Value.AsString());
    }

    private void ExistingTeamWithSession(string session)
    {
        WriteTeam("proj", $"{{\"version\":1,\"team_id\":\"t-1\",\"transport\":\"terminal\",\"roster_level\":\"repo\",\"orchestrator\":{{\"pid\":{Environment.ProcessId},\"session_id\":\"{session}\"}},\"members\":[]}}");
    }

    [Fact]
    public void CommitWithAnUnprovenSessionDoesNotOverwriteAnExistingId()
    {
        _callers = new RecordingCallers { Started = Started };
        ExistingTeamWithSession("sess-existing");

        var (code, stdout, stderr) = Roster([.. CommitNoSession, "--session", "sess-intruder", "--team", "proj"]);

        Assert.True(code == 0, stderr);
        Assert.Equal("sess-existing", OrchestratorOf(stdout).Member("session_id")!.Value.AsString());
        Assert.Equal("sess-existing", TeamOrchestratorSession());
        Assert.NotEmpty(WarningsOf(stdout));
    }

    [Fact]
    public void CommitWithAProcessTiedSessionReplacesAnExistingId()
    {
        _callers = new RecordingCallers { Started = Started };
        _callers.Bound.Add(new BoundSession("sess-proven", Started.AddMinutes(5)));
        ExistingTeamWithSession("sess-existing");

        var (code, stdout, stderr) = Roster([.. CommitNoSession, "--session", "sess-proven", "--team", "proj"]);

        Assert.True(code == 0, stderr);
        Assert.Equal("sess-proven", OrchestratorOf(stdout).Member("session_id")!.Value.AsString());
        Assert.Empty(WarningsOf(stdout));
    }

    [Fact]
    public void CommitWithNoResolvableSessionWarnsAndSucceeds()
    {
        _callers = new RecordingCallers { Started = Started };

        var (code, stdout, _) = Roster(CommitNoSession);

        Assert.Equal(0, code);
        Assert.True(OrchestratorOf(stdout).Member("session_id")!.Value.IsNull);
        Assert.Contains(WarningsOf(stdout), w => w.StartsWith("orchestrator session unknown:", StringComparison.Ordinal));
    }

    [Fact]
    public void WithoutARegistrationTheNewestSessionPidsRowSinceProcessStartIsUsed()
    {
        _callers = new RecordingCallers { Started = Started };
        SessionPidsRow("sess-first", Environment.ProcessId);
        SessionPidsRow("sess-newest", Environment.ProcessId);
        SessionPidsRow("sess-other-pid", Environment.ProcessId + 1);

        var (_, stdout, _) = Roster(CommitNoSession);

        Assert.Equal("sess-newest", OrchestratorOf(stdout).Member("session_id")!.Value.AsString());
    }

    [Fact]
    public void AfterClearTheNewerRowBeatsTheOlderBoundRegistration()
    {
        _callers = new RecordingCallers { Started = Started };
        _callers.Bound.Add(new BoundSession("sess-old", Started.AddMinutes(1)));
        SessionPidsRow("sess-new", Environment.ProcessId);

        var (_, stdout, _) = Roster(CommitNoSession);

        Assert.Equal("sess-new", OrchestratorOf(stdout).Member("session_id")!.Value.AsString());
    }

    [Fact]
    public void ANewerBoundRegistrationStillBeatsAnOlderRow()
    {
        _callers = new RecordingCallers { Started = Started };
        SessionPidsRow("sess-row", Environment.ProcessId);
        _callers.Bound.Add(new BoundSession("sess-bound", DateTimeOffset.UtcNow.AddMinutes(10)));

        var (_, stdout, _) = Roster(CommitNoSession);

        Assert.Equal("sess-bound", OrchestratorOf(stdout).Member("session_id")!.Value.AsString());
    }

    [Fact]
    public void ABackfillNeverOverwritesTheNewSessionWithTheOldBoundOne()
    {
        _callers = new RecordingCallers { Started = Started };
        _callers.Bound.Add(new BoundSession("sess-old", Started.AddMinutes(1)));
        SessionPidsRow("sess-new", Environment.ProcessId);
        OwnedTeamFile(Environment.ProcessId, "sess-new");

        var (code, _, stderr) = AdHoc();

        Assert.True(code == 0, stderr);
        Assert.Equal("sess-new", TeamOrchestratorSession());
    }

    private string OtherRepoWithRow(string session, bool writeRow)
    {
        var repoA = Path.Combine(_sandbox.Root, "repo-a");
        Directory.CreateDirectory(Path.Combine(repoA, ".git"));
        Directory.CreateDirectory(Path.Combine(Project, ".git"));
        _extraEnv["WRANGL_HIERARCHY_DIR"] = string.Empty;
        if (writeRow)
        {
            SessionPidsRow(session, Environment.ProcessId, Path.Combine(repoA, ".claude", "ah2"));
        }

        return repoA;
    }

    [Fact]
    public void ACrossRepoOrchestratorsSessionIsFoundInItsOwnCwdDir()
    {
        _callers = new RecordingCallers { Started = Started };
        _callers.Cwds.Add(OtherRepoWithRow("sess-cross", writeRow: true));

        var (code, stdout, stderr) = Roster(CommitNoSession);

        Assert.True(code == 0, stderr);
        Assert.Equal("sess-cross", OrchestratorOf(stdout).Member("session_id")!.Value.AsString());
    }

    [Fact]
    public void ARegistrationCwdWithoutAMatchingRowGivesNothing()
    {
        _callers = new RecordingCallers { Started = Started };
        _callers.Cwds.Add(OtherRepoWithRow("unused", writeRow: false));

        var (code, stdout, stderr) = Roster(CommitNoSession);

        Assert.True(code == 0, stderr);
        Assert.True(OrchestratorOf(stdout).Member("session_id")!.Value.IsNull);
        Assert.NotEmpty(WarningsOf(stdout));
    }

    [Fact]
    public void ARowWrittenBeforeTheProcessStartedIsIgnored()
    {
        SessionPidsRow("sess-reused-pid", Environment.ProcessId);
        _callers = new RecordingCallers { Started = DateTimeOffset.UtcNow.AddMinutes(5) };

        var (code, stdout, _) = Roster(CommitNoSession);

        Assert.Equal(0, code);
        Assert.True(OrchestratorOf(stdout).Member("session_id")!.Value.IsNull);
        Assert.NotEmpty(WarningsOf(stdout));
    }

    [Fact]
    public void AnUnknownStartTimeMatchesNothing()
    {
        SessionPidsRow("sess-fine", Environment.ProcessId);
        _callers = new RecordingCallers { Started = null };

        var (_, noStart, _) = Roster(CommitNoSession);

        Assert.True(OrchestratorOf(noStart).Member("session_id")!.Value.IsNull);
    }

    [Fact]
    public void ADeadPidResolvesNothingAndAFreshPidWithNoEvidenceToo()
    {
        _callers = new RecordingCallers { Started = Started };
        _callers.Bound.Add(new BoundSession("sess-bound", DateTimeOffset.UtcNow));
        var other = LivePidOfAnotherProcess();
        _nearest = new NearestSession(NearestKind.Session, null, null, other);
        var args = new[] { "create", "--commit", "--verified", "[]", "--transport", "terminal", "--roster-level", "repo" };
        SessionPidsRow("sess-of-other-pid", Environment.ProcessId);

        var (code, stdout, _) = Roster(args);

        Assert.Equal(0, code);
        Assert.Equal("sess-bound", OrchestratorOf(stdout).Member("session_id")!.Value.AsString());
    }

    private static int DeadPid() => 99999999;

    private void OwnedTeamFile(double pid, string? session, string? recordedStart = null)
    {
        var start = recordedStart is not null ? $",\"pid_start\":\"{recordedStart}\"" : pid == Environment.ProcessId ? $",\"pid_start\":\"{SelfStart}\"" : string.Empty;
        var owner = session is null ? $"{{\"pid\":{pid}{start}}}" : $"{{\"pid\":{pid}{start},\"session_id\":\"{session}\"}}";
        WriteTeam("proj", $"{{\"version\":1,\"team_id\":\"t-1\",\"transport\":\"terminal\",\"roster_level\":\"repo\",\"orchestrator\":{owner},\"members\":[]}}");
    }

    private (int Code, string Stdout, string Stderr) AdHoc(string? claimedSession = null)
    {
        if (claimedSession is not null)
        {
            _extraEnv["CLAUDE_CODE_SESSION_ID"] = claimedSession;
        }

        return Roster("spawn-ad-hoc", "reviewer", "--kind", "custom", "--harness", "mytool", "--team", "proj", "--orchestrator-pid", Self);
    }

    [Fact]
    public void SpawnAdHocBackfillsANullOrchestratorSession()
    {
        _callers = new RecordingCallers { Started = Started };
        _callers.Bound.Add(new BoundSession("sess-bound", DateTimeOffset.UtcNow));
        OwnedTeamFile(Environment.ProcessId, null);

        var (code, _, stderr) = AdHoc();

        Assert.True(code == 0, stderr);
        Assert.Equal("sess-bound", TeamOrchestratorSession());
    }

    [Fact]
    public void BackfillLeavesATeamOwnedByAnotherPidAlone()
    {
        _callers = new RecordingCallers { Started = Started };
        _callers.Bound.Add(new BoundSession("sess-bound", DateTimeOffset.UtcNow));
        OwnedTeamFile(LivePidOfAnotherProcess(), "sess-theirs");

        var (code, stdout, stderr) = AdHoc();

        Assert.Equal(2, code);
        Assert.Equal("not-team-owner", JsonText.Parse(stdout).Member("refused")!.Value.AsString());
        Assert.StartsWith("ah roster spawn-ad-hoc: this session does not own team \"proj\"; nothing was changed.", stderr, StringComparison.Ordinal);
        Assert.Equal("sess-theirs", TeamOrchestratorSession());
    }

    [Fact]
    public void BackfillReplacesAStaleSessionWithAProcessTiedOne()
    {
        _callers = new RecordingCallers { Started = Started };
        _callers.Bound.Add(new BoundSession("sess-new", DateTimeOffset.UtcNow));
        OwnedTeamFile(Environment.ProcessId, "sess-old");

        var (code, _, stderr) = AdHoc();

        Assert.True(code == 0, stderr);
        Assert.Equal("sess-new", TeamOrchestratorSession());
    }

    [Fact]
    public void SpawnAdHocReclaimsATeamWithProcessTiedProof()
    {
        _callers = new RecordingCallers { Started = Started };
        _callers.Bound.Add(new BoundSession("sess-new", DateTimeOffset.UtcNow));
        OwnedTeamFile(Environment.ProcessId, "sess-old");

        var (code, _, stderr) = AdHoc();

        Assert.True(code == 0, stderr);
        var rebind = Assert.Single(_callers.Rebinds);
        Assert.Equal(("sess-new", Environment.ProcessId), (rebind.Session, rebind.Pid));
    }

    [Theory]
    [InlineData(SelfStart, 0)]
    [InlineData("2001-01-01T00:00:00.000Z", 2)]
    public void TheGuardComparesTheProvedStartWithTheRecordedOne(string recordedStart, int expectedCode)
    {
        _callers = new RecordingCallers { StartText = SelfStart, Started = Started };
        WriteTeam(
            "proj",
            $"{{\"version\":1,\"team_id\":\"t-1\",\"transport\":\"terminal\",\"roster_level\":\"repo\",\"orchestrator\":{{\"pid\":{Environment.ProcessId},\"pid_start\":\"{recordedStart}\",\"session_id\":null}},\"members\":[]}}");

        var (code, stdout, stderr) = AdHoc();

        Assert.True(code == expectedCode, stderr);
        Assert.Equal(expectedCode == 0 ? null : "not-team-owner", expectedCode == 0 ? null : JsonText.Parse(stdout).Member("refused")!.Value.AsString());
    }

    [Fact]
    public void ABareSessionFlagDoesNotReclaimATeam()
    {
        _callers = new RecordingCallers { Started = Started };
        OwnedTeamFile(Environment.ProcessId, "sess-old");

        var (code, _, stderr) = AdHoc("sess-x");

        Assert.True(code == 0, stderr);
        Assert.Empty(_callers.Rebinds);
    }

    [Fact]
    public void BackfillIgnoresAnUncorroboratedSessionFlag()
    {
        _callers = new RecordingCallers { Started = Started };
        OwnedTeamFile(Environment.ProcessId, null);

        var (code, stdout, stderr) = AdHoc("sess-x");

        Assert.True(code == 0, stderr);
        Assert.Equal("<none>", TeamOrchestratorSession());
        Assert.Contains(WarningsOf(stdout), w => w.StartsWith("orchestrator session unknown:", StringComparison.Ordinal));
    }

    [Fact]
    public void ACorroboratedSessionFlagIsRecorded()
    {
        _callers = new RecordingCallers { Started = Started };
        SessionPidsRow("sess-x", Environment.ProcessId);
        OwnedTeamFile(Environment.ProcessId, null);

        var (code, _, stderr) = AdHoc("sess-x");

        Assert.True(code == 0, stderr);
        Assert.Equal("sess-x", TeamOrchestratorSession());
    }

    [Fact]
    public void AMemberCannotWriteItsOwnSessionIntoTheOrchestratorsTeamFile()
    {
        _callers = new RecordingCallers { Started = Started };
        _callers.Bound.Add(new BoundSession("sess-orchestrator", DateTimeOffset.UtcNow));
        OwnedTeamFile(Environment.ProcessId, null);

        var (code, _, stderr) = AdHoc("sess-member");

        Assert.True(code == 0, stderr);
        Assert.NotEqual("sess-member", TeamOrchestratorSession());
        Assert.Equal("<none>", TeamOrchestratorSession());
    }

    [Fact]
    public void AdoptRecordsTheCallersSession()
    {
        _callers = new RecordingCallers { Started = Started };
        _callers.Bound.Add(new BoundSession("sess-bound", DateTimeOffset.UtcNow));
        ObserverSeesAParent();
        OwnedTeamFile(DeadPid(), "sess-previous", GoneStart);

        var (code, _, stderr) = Roster("adopt", "--team", "proj", "--orchestrator-pid", Self);

        Assert.True(code == 0, stderr);
        Assert.Equal("sess-bound", TeamOrchestratorSession());
    }

    [Fact]
    public void AdoptRefusesATeamWhoseRecordedOwnerIsAliveWithTheRecordedStartTimeEvenWhenForced()
    {
        _callers = new RecordingCallers { Started = Started };
        var holder = LivePidOfAnotherProcess();
        const string HolderStart = "2026-10-15T21:00:00.000Z";
        _runner.Ps = argv => argv.Contains("lstart=") && argv.Contains(holder.ToString(CultureInfo.InvariantCulture)) ? HolderStart + "\n" : SelfStartOnly(argv);
        OwnedTeamFile(holder, "sess-previous", HolderStart);

        var (code, _, stderr) = Roster("adopt", "--team", "proj", "--orchestrator-pid", Self);
        var (forcedCode, _, forcedErr) = Roster("adopt", "--team", "proj", "--orchestrator-pid", Self, "--force");

        Assert.NotEqual(0, code);
        Assert.Contains("refusing to hijack a live team", stderr, StringComparison.Ordinal);
        Assert.NotEqual(0, forcedCode);
        Assert.Contains("refusing to hijack a live team", forcedErr, StringComparison.Ordinal);
    }

    [Fact]
    public void AdoptTakesATeamWhoseRecordedPidWasReusedByAStranger()
    {
        _callers = new RecordingCallers { Started = Started };
        var holder = LivePidOfAnotherProcess();
        _runner.Ps = argv => argv.Contains("lstart=") && argv.Contains(holder.ToString(CultureInfo.InvariantCulture)) ? "2026-10-17T05:00:00.000Z\n" : SelfStartOnly(argv);
        OwnedTeamFile(holder, "sess-previous", "2026-10-15T21:00:00.000Z");

        var (code, _, stderr) = Roster("adopt", "--team", "proj", "--orchestrator-pid", Self);

        Assert.True(code == 0, stderr);
    }

    [Fact]
    public void NegativeAdoptRefusesALiveHolderWithNoRecordedStartUnlessForced()
    {
        _callers = new RecordingCallers { Started = Started };
        OwnedTeamFile(LivePidOfAnotherProcess(), "sess-previous");

        var (code, _, stderr) = Roster("adopt", "--team", "proj", "--orchestrator-pid", Self);
        var (forcedCode, _, forcedErr) = Roster("adopt", "--team", "proj", "--orchestrator-pid", Self, "--force");

        Assert.NotEqual(0, code);
        Assert.Contains("cannot be proven gone", stderr, StringComparison.Ordinal);
        Assert.True(forcedCode == 0, forcedErr);
    }

    [Fact]
    public void AdoptByAnotherPidClearsTheOldSession()
    {
        _callers = new RecordingCallers { Started = Started };
        ObserverSeesAParent();
        OwnedTeamFile(DeadPid(), "sess-previous", GoneStart);

        var another = LivePidOfAnotherProcess();
        _nearest = new NearestSession(NearestKind.Session, null, null, another);

        var (code, _, stderr) = Roster("adopt", "--team", "proj", "--orchestrator-pid", another.ToString(CultureInfo.InvariantCulture));

        Assert.True(code == 0, stderr);
        Assert.Equal("<none>", TeamOrchestratorSession());
    }

    [Fact]
    public void AdoptWithAnUncorroboratedSessionFlagRecordsNothing()
    {
        _callers = new RecordingCallers { Started = Started };
        ObserverSeesAParent();
        OwnedTeamFile(DeadPid(), "sess-previous", GoneStart);

        _extraEnv["CLAUDE_CODE_SESSION_ID"] = "sess-x";

        var (code, _, stderr) = Roster("adopt", "--team", "proj", "--orchestrator-pid", Self);

        Assert.True(code == 0, stderr);
        Assert.Equal("<none>", TeamOrchestratorSession());
    }

    [Fact]
    public void CommitSkipsAMemberWithNoPeersRowAndStillSucceeds()
    {
        _callers = new RecordingCallers();
        PeersRow("%1", LivePidOfAnotherProcess());

        var (code, stdout, stderr) = Roster(CommitTwoPeers());

        Assert.True(code == 0, stderr);
        Assert.Equal(["orchestrator@proj", "m%1@proj"], JsonText.Parse(stdout).Member("registered")!.Value.AsArray().Select(a => a.AsString()).ToArray());
        Assert.DoesNotContain(_callers.Calls, call => call.Pane == "%2");
    }

    [Fact]
    public void CommitDoesNotReclaimAnAddressTheLiveRegistrationAlreadyHolds()
    {
        _callers = new RecordingCallers();
        var first = LivePidOfAnotherProcess();
        var second = LivePidOfAnotherProcess();
        PeersRow("%1", first);
        PeersRow("%2", second);
        _callers.Held["sess-1"] = (Environment.ProcessId, ["orchestrator@proj"]);
        _callers.Held["sess-%1"] = (first, ["proj-implementor@proj"]);
        // negative: %2's registration holds some other address, so it is still claimed
        _callers.Held["sess-%2"] = (second, ["proj-reviewer@other"]);

        var (code, stdout, stderr) = Roster(CommitTwoPeers());

        Assert.True(code == 0, stderr);
        Assert.Equal("%2", Assert.Single(_callers.Calls).Pane);
        var registered = JsonText.Parse(stdout).Member("registered")!.Value.AsArray().Select(a => a.AsString()).ToArray();
        Assert.Equal(["orchestrator@proj", "proj-implementor@proj", "m%2@proj"], registered);
    }

    [Fact]
    public void CommitSkipsAMemberWhosePeersRowIsDeadOrNotUp()
    {
        _callers = new RecordingCallers();
        PeersRow("%1", 99999999);
        PeersRow("%2", LivePidOfAnotherProcess(), status: "down");

        var (code, _, stderr) = Roster(CommitTwoPeers());

        Assert.True(code == 0, stderr);
        Assert.Null(Assert.Single(_callers.Calls).Team);
    }

    [Fact]
    public void CommitPrefersTheNewestPeersRowOfAPane()
    {
        _callers = new RecordingCallers();
        var older = LivePidOfAnotherProcess();
        var newer = LivePidOfAnotherProcess();
        _sandbox.PlantPeers(Hierarchy,
            $"{{\"type\":\"peer\",\"status\":\"up\",\"session_id\":\"sess-new\",\"pid\":{newer},\"pane_id\":\"%1\",\"ts\":\"2026-03-02T00:00:00.000Z\"}}\n"
            + $"{{\"type\":\"peer\",\"status\":\"up\",\"session_id\":\"sess-old\",\"pid\":{older},\"pane_id\":\"%1\",\"ts\":\"2026-03-01T00:00:00.000Z\"}}\n");

        var (code, _, stderr) = Roster(CommitTwoPeers());

        Assert.True(code == 0, stderr);
        var member = Assert.Single(_callers.Calls, c => c.Pane == "%1");
        Assert.Equal(("sess-new", newer), (member.Session, member.Pid));
    }

    [Fact]
    public void ARegistrationRefusalDoesNotFailCommit()
    {
        _callers = new RecordingCallers { Throws = true };
        PeersRow("%1", LivePidOfAnotherProcess());

        var (code, stdout, stderr) = Roster(CommitTwoPeers());

        Assert.True(code == 0, stderr);
        Assert.True(_sandbox.TeamExists(TeamPath("proj")));
        Assert.True(JsonText.Parse(stdout).Member("committed")!.Value.AsBool());
        Assert.Null(JsonText.Parse(stdout).Member("registered"));
        Assert.NotEmpty(_callers.Calls);
        Assert.Contains("could not register the Orchestrator of proj: mailbox unavailable", stderr, StringComparison.Ordinal);
        var warnings = JsonText.Parse(stdout).Member("registration_warnings")!.Value.AsArray();
        Assert.Contains(warnings, w => w.AsString().Contains("mailbox unavailable", StringComparison.Ordinal));
    }

    [Fact]
    public void ACommitWhoseRegistrationsAllSucceedCarriesNoWarnings()
    {
        _callers = new RecordingCallers();
        PeersRow("%1", LivePidOfAnotherProcess());

        var (code, stdout, stderr) = Roster(CommitTwoPeers());

        Assert.True(code == 0, stderr);
        Assert.Null(JsonText.Parse(stdout).Member("registration_warnings"));
        Assert.DoesNotContain("could not register", stderr, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- spawn-one

    [Fact]
    public void SpawnOneRefusesAFlagItDoesNotTake()
    {
        var (unknownCode, _, unknownErr) = Roster("spawn-one", "implementor", "--bogus", "x");

        Assert.Equal(2, unknownCode);
        Assert.Equal(
            "ah roster: spawn-one: unrecognized flag --bogus (use --cwd, --dry-run, --allow-global, --team, --orchestrator-pid, --member, --model, --names-in-use, --here, --tab, --split, --headless, or --prompt)\n",
            unknownErr);
    }

    [Fact]
    public void SpawnOneHeadlessWithoutAPromptIsRefused()
    {
        var (code, _, stderr) = Roster("spawn-one", "implementor", "--team", "proj", "--headless");

        Assert.Equal(2, code);
        Assert.Contains("--headless needs --prompt", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void SpawnOneHeadlessTakesNoValue()
    {
        var (code, _, stderr) = Roster("spawn-one", "implementor", "--team", "proj", "--headless", "yes", "--prompt", "go");

        Assert.Equal(2, code);
        Assert.Contains("--headless takes no value", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void SpawnOnePromptWithoutHeadlessIsRefused()
    {
        var (code, _, stderr) = Roster("spawn-one", "implementor", "--team", "proj", "--prompt", "go");

        Assert.Equal(2, code);
        Assert.Contains("--prompt applies only with --headless", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void SpawnOneHeadlessWithAPromptPassesTheFlagChecks()
    {
        var (code, _, stderr) = Roster("spawn-one", "implementor", "--team", "proj", "--headless", "--prompt", "go");

        Assert.DoesNotContain("--headless", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("--prompt", stderr, StringComparison.Ordinal);
        Assert.Equal(2, code);
        Assert.Contains("run `spawn-ad-hoc implementor` instead", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void SpawnOneNamesTheRolesWhenTheRoleIsNotOneOfThem()
    {
        var (code, _, stderr) = Roster("spawn-one", "nobody", "--team", "proj");

        Assert.Equal(2, code);
        Assert.StartsWith("ah roster: spawn-one: role must be one of ", stderr, StringComparison.Ordinal);
        Assert.EndsWith(", got \"nobody\"\n", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void SpawnOneWithNoRosterPointsToTheAdHocVerb()
    {
        var (code, _, stderr) = Roster("spawn-one", "implementor", "--team", "proj");

        Assert.Equal(2, code);
        Assert.Contains("run `spawn-ad-hoc implementor` instead", stderr, StringComparison.Ordinal);
        Assert.False(_sandbox.TeamExists(TeamPath("proj")));
    }

    // ---------------------------------------------------------------- spawn-ad-hoc

    [Fact]
    public void SpawnAdHocWithNoRoleOrARoleItCannotStandUpIsUnderSpecified()
    {
        var (noneCode, _, noneErr) = Roster("spawn-ad-hoc");
        var (badCode, _, badErr) = Roster("spawn-ad-hoc", "nobody");

        Assert.Equal(2, noneCode);
        Assert.StartsWith("ah roster: spawn-ad-hoc: under-specified — no role named (spawn-ad-hoc <role>, one of ", noneErr, StringComparison.Ordinal);
        Assert.Equal(2, badCode);
        Assert.StartsWith("ah roster: spawn-ad-hoc: under-specified — \"nobody\" is not a role spawn-ad-hoc can stand up", badErr, StringComparison.Ordinal);
    }

    [Fact]
    public void SpawnAdHocRefusesAValuelessKindAndHarnessOnANonCustomKind()
    {
        var (kindCode, _, kindErr) = Roster("spawn-ad-hoc", "implementor", "--kind");
        var (harnessCode, _, harnessErr) = Roster("spawn-ad-hoc", "implementor", "--harness", "h");

        Assert.Equal(2, kindCode);
        Assert.Equal("ah roster: spawn-ad-hoc: --kind requires a value (e.g. claude, codex, pi)\n", kindErr);
        Assert.Equal(2, harnessCode);
        Assert.Equal("ah roster: spawn-ad-hoc: --harness and --command apply only to --kind custom\n", harnessErr);
    }

    [Fact]
    public void SpawnAdHocRefusesArgsThatAreNotJson()
    {
        var (code, _, stderr) = Roster("spawn-ad-hoc", "implementor", "--kind", "codex", "--args", "--fast");

        Assert.Equal(2, code);
        Assert.Contains("--args", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void SpawnAdHocStandsUpQaAsAPeerAndStillRefusesItOnTheSubagentRoute()
    {
        var (code, stdout, stderr) = Roster("spawn-ad-hoc", "qa", "--model", "sonnet", "--dry-run");
        var (subCode, _, subErr) = Roster("spawn-ad-hoc", "qa", "--route", "subagent");

        Assert.True(code == 0, stderr);
        Assert.Equal("qa", JsonText.Parse(stdout).AsObject()["role"].AsString());
        Assert.Contains("claude --agent wrangl:qa --name proj-qa", stdout, StringComparison.Ordinal);
        Assert.Equal(2, subCode);
        Assert.StartsWith("ah roster: route \"subagent\" is not allowed for role", subErr, StringComparison.Ordinal);
    }

    [Fact]
    public void SpawnAdHocRefusesARouteTheRoleDoesNotAllow()
    {
        var (code, _, stderr) = Roster("spawn-ad-hoc", "implementor", "--route", "subagent");

        Assert.Equal(2, code);
        Assert.StartsWith("ah roster: route \"subagent\" is not allowed for role", stderr, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- teardown: disband, dismiss, untrack, reap

    /// <summary>The start a fixture records for an orchestrator pid it means to be gone: with it the exited pid is proof, without it nothing is.</summary>
    private const string GoneStart = "Mon Jan  1 00:00:00 2001";

    private string LiveTeamJson(string transport, string members, string? pid = null, string? created = null, string? pidStart = null) =>
        $$"""{"version":1,"team_id":"t-1","created":"{{created ?? DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture)}}","roster_level":"repo","transport":"{{transport}}","orchestrator":{"session_id":null,"pid":{{pid ?? Self}}{{((pidStart ?? (pid is null ? SelfStart : null)) is { } recordedStart ? $",\"pid_start\":\"{recordedStart}\"" : string.Empty)}}},"members":[{{members}}],"expected_root":"{{Project}}","roster":null,"layout":"auto"}""";

    private const string TwoPeers =
        "{\"role\":\"implementor\",\"name\":\"proj-implementor\",\"route\":\"peer\",\"transport_id\":\"%1\",\"model\":\"sonnet\"},"
        + "{\"role\":\"reviewer\",\"name\":\"proj-reviewer\",\"route\":\"peer\",\"transport_id\":\"%2\",\"model\":\"sonnet\"}";

    private static string TokenOf(string planOutput) => JsonText.Parse(planOutput).Member("close_token")!.Value.AsString();

    [Fact]
    public void DisbandPlanNamesTheCloseSetAndOnlyTheMembersWithAPane()
    {
        const string Members = TwoPeers + ",{\"role\":\"advisor\",\"name\":\"proj-advisor\",\"route\":\"subagent\"}";
        WriteTeam("proj", LiveTeamJson("tmux", Members));
        var before = _sandbox.TeamText(TeamPath("proj"));

        var (code, stdout, planErr) = Roster("disband", "--ignore-release", "--team", "proj");

        Assert.True(code == 0, planErr);
        var plan = JsonText.Parse(stdout);
        Assert.Equal(2, (int)plan.Member("expected")!.Value.AsNumber());
        Assert.Equal(3, plan.Member("close")!.Value.AsArray().Count);
        Assert.Matches("^[0-9a-f]{16}$", plan.Member("close_token")!.Value.AsString());
        Assert.Equal("tmux kill-pane -t %1", plan.Member("close")!.Value.AsArray()[0].Member("command")!.Value.AsString());
        Assert.Equal(before, _sandbox.TeamText(TeamPath("proj")));
        Assert.DoesNotContain(_runner.Calls, argv => argv.Contains("kill-pane"));
    }

    [Fact]
    public void DisbandCloseNamesTheSnapshotItWroteAndThePlanWritesNone()
    {
        WriteTeam("proj", LiveTeamJson("tmux", TwoPeers));
        var plan = Roster("disband", "--ignore-release", "--team", "proj");
        var token = TokenOf(plan.Stdout);
        Assert.Null(JsonText.Parse(plan.Stdout).Member("snapshot"));

        var (code, stdout, stderr) = Roster("disband", "--close", "--confirm", "--ignore-release", "--plan-token", token, "--team", "proj");

        Assert.True(code == 0, stderr);
        var named = JsonText.Parse(stdout).Member("snapshot");
        Assert.True(named is { IsText: true }, stdout);
        Assert.EndsWith(".json", named!.Value.AsString(), StringComparison.Ordinal);
        Assert.Contains("\"members\"", SnapshotText(named.Value.AsString()), StringComparison.Ordinal);
    }

    [Fact]
    public void DisbandRefusesTheModesRemovedFromIt()
    {
        var (commitCode, _, commitErr) = Roster("disband", "--commit");
        var (killCode, _, killErr) = Roster("disband", "--close", "--kill");
        var (flagCode, _, flagErr) = Roster("disband", "--bogus");

        Assert.Equal(2, commitCode);
        Assert.StartsWith("ah roster: disband: --commit and --keep-sessions were removed — ", commitErr, StringComparison.Ordinal);
        Assert.Equal(2, killCode);
        Assert.Equal("ah roster: disband --close cannot be combined with --kill\n", killErr);
        Assert.Equal(2, flagCode);
        Assert.StartsWith("ah roster: disband: unrecognized flag --bogus (use --plan, or --close --confirm --plan-token <tok>;", flagErr, StringComparison.Ordinal);
    }

    [Fact]
    public void DismissNamesTheMembersWhenTheNameIsAKnownRoleOrNobody()
    {
        WriteTeam("proj", LiveTeamJson("tmux", TwoPeers));

        var (roleCode, _, roleErr) = Roster("dismiss", "implementor", "--team", "proj");
        var (nobodyCode, _, nobodyErr) = Roster("dismiss", "ghost", "--team", "proj");

        Assert.Equal(2, roleCode);
        Assert.Contains("that is a role, not a member name", roleErr, StringComparison.Ordinal);
        Assert.Equal(2, nobodyCode);
        Assert.Contains("it has: proj-implementor, proj-reviewer", nobodyErr, StringComparison.Ordinal);
    }

    [Fact]
    public void DismissRefusesTrackingOnlyModesAndAlsoConfigOutsideAClose()
    {
        var (commitCode, _, commitErr) = Roster("dismiss", "x", "--commit");
        var (configCode, _, configErr) = Roster("dismiss", "x", "--also-config");

        Assert.Equal(2, commitCode);
        Assert.StartsWith("ah roster: dismiss: --commit and --keep-sessions were removed — ", commitErr, StringComparison.Ordinal);
        Assert.Equal(2, configCode);
        Assert.Equal("ah roster: dismiss: --also-config and --level are only valid with --close — a plan removes nothing\n", configErr);
    }

    [Fact]
    public void UntrackForgetsTheRowOfAMemberThatIsNotLiveAndRemovesTheEmptiedTeam()
    {
        WriteTeam("proj", LiveTeamJson("terminal", TwoPeers));

        var (planCode, planOut, _) = Roster("untrack", "proj-implementor", "--team", "proj");
        Assert.Equal(0, planCode);
        Assert.Equal("untrack", JsonText.Parse(planOut).Member("plan")!.Value.AsString());
        Assert.Equal(2, JsonText.Parse(_sandbox.TeamText(TeamPath("proj"))).Member("members")!.Value.AsArray().Count);

        var (code, stdout, _) = Roster("untrack", "proj-implementor", "--commit", "--team", "proj");
        Assert.Equal(0, code);
        Assert.True(JsonText.Parse(stdout).Member("untracked")!.Value.AsBool());
        Assert.Single(JsonText.Parse(_sandbox.TeamText(TeamPath("proj"))).Member("members")!.Value.AsArray());

        var (lastCode, lastOut, lastErr) = Roster("untrack", "proj-reviewer", "--commit", "--team", "proj");
        Assert.Equal(0, lastCode);
        Assert.True(JsonText.Parse(lastOut).Member("team_removed")!.Value.AsBool());
        Assert.Contains("has no members left, so it was removed", lastErr, StringComparison.Ordinal);
        Assert.False(_sandbox.TeamExists(TeamPath("proj")));
    }

    [Fact]
    public void AMemberThatIsAnOrchestratorIsNeverOnACloseList()
    {
        const string Members = TwoPeers
            + ",{\"role\":\"orchestrator\",\"name\":\"proj-orchestrator\",\"route\":\"peer\",\"transport_id\":\"%9\"}";
        WriteTeam("proj", LiveTeamJson("tmux", Members));

        var (code, stdout, _) = Roster("disband", "--ignore-release", "--team", "proj");

        Assert.Equal(0, code);
        var plan = JsonText.Parse(stdout);
        Assert.Equal(2, (int)plan.Member("expected")!.Value.AsNumber());
        Assert.Equal(2, plan.Member("close")!.Value.AsArray().Count);
        Assert.DoesNotContain("%9", stdout, StringComparison.Ordinal);
        Assert.Contains("proj-orchestrator", plan.Member("warnings")!.Value.AsArray()[0].AsString(), StringComparison.Ordinal);

        var (dismissCode, _, dismissErr) = Roster("dismiss", "proj-orchestrator", "--team", "proj");
        Assert.Equal(2, dismissCode);
        Assert.Contains("is the session running this command or an orchestrator", dismissErr, StringComparison.Ordinal);
        var (otherCode, _, _) = Roster("dismiss", "proj-reviewer", "--plan", "--team", "proj");
        Assert.Equal(0, otherCode);
    }

    [Fact]
    public void UntrackNeedsExactlyOneOfANameAndAll()
    {
        var (noneCode, _, noneErr) = Roster("untrack");
        var (bothCode, _, bothErr) = Roster("untrack", "x", "--all");
        var (planCommitCode, _, planCommitErr) = Roster("untrack", "x", "--plan", "--commit");

        Assert.Equal(2, noneCode);
        Assert.StartsWith("ah roster: untrack needs a member name or --all", noneErr, StringComparison.Ordinal);
        Assert.Equal(2, bothCode);
        Assert.Equal("ah roster: untrack: pass a member name or --all, not both\n", bothErr);
        Assert.Equal(2, planCommitCode);
        Assert.Equal("ah roster: untrack --plan cannot be combined with --commit\n", planCommitErr);
    }

    [Fact]
    public void ReapListsOnlyTeamsWhoseOrchestratorIsGoneAndCommitRemovesJustThose()
    {
        ObserverSeesAParent();
        WriteTeam("dead", LiveTeamJson("terminal", TwoPeers, pid: "99999999", pidStart: GoneStart));
        WriteTeam("alive", LiveTeamJson("terminal", TwoPeers));

        var (planCode, planOut, _) = Roster("reap");
        Assert.Equal(0, planCode);
        var orphans = JsonText.Parse(planOut).Member("orphans")!.Value.AsArray();
        Assert.Single(orphans);
        Assert.Equal("dead", orphans[0].Member("name")!.Value.AsString());
        Assert.True(_sandbox.TeamExists(TeamPath("dead")));

        var (code, stdout, _) = Roster("reap", "--commit");
        Assert.Equal(0, code);
        Assert.True(JsonText.Parse(stdout).Member("committed")!.Value.AsBool());
        Assert.False(_sandbox.TeamExists(TeamPath("dead")));
        Assert.True(_sandbox.TeamExists(TeamPath("alive")));
    }

    [Fact]
    public void ReapNamesTheAdoptCommandOfTheSessionTheAncestryProvesNotTheOneTheEnvironmentClaims()
    {
        _callers = new RecordingCallers();
        ObserverSeesAParent();
        WriteTeam("dead", LiveTeamJson("terminal", TwoPeers, pid: "99999999", pidStart: GoneStart));
        _sandbox.PlantPeers(
            Hierarchy,
            $"{{\"type\":\"peer\",\"status\":\"up\",\"name\":\"proj-implementor\",\"role\":\"implementor\",\"session_id\":\"sess-live\",\"pid\":{LivePidOfAnotherProcess()},\"pane_id\":\"%1\",\"ts\":\"{DateTimeOffset.UtcNow:yyyy-MM-dd'T'HH:mm:ss.fff'Z'}\"}}\n");

        var (code, stdout, stderr) = Roster("reap", "--commit");

        Assert.True(code == 0, stderr);
        var kept = Assert.Single(JsonText.Parse(stdout).Member("kept")!.Value.AsArray());
        var next = kept.Member("next")!.Value;
        Assert.False(next.IsNull);
        Assert.Contains($"adopt --orchestrator-pid {Self}", next.AsString(), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- resync, move, layout

    [Fact]
    public void ResyncDoesNothingWithoutAHerdrTeamAndRefusesABindThatIsNotAnObject()
    {
        var (noneCode, noneOut, _) = Roster("resync", "--team", "proj");
        WriteTeam("proj", LiveTeamJson("tmux", TwoPeers));
        var (tmuxCode, tmuxOut, _) = Roster("resync", "--team", "proj");

        Assert.Equal(0, noneCode);
        Assert.Equal("no active team", JsonText.Parse(noneOut).Member("reason")!.Value.AsString());
        Assert.Equal(0, tmuxCode);
        Assert.False(JsonText.Parse(tmuxOut).Member("resynced")!.Value.AsBool());

        WriteTeam("proj", LiveTeamJson("herdr", TwoPeers));
        var (badCode, _, badErr) = Roster("resync", "--bind", "not json", "--team", "proj");
        var (arrayCode, _, arrayErr) = Roster("resync", "--bind", "[1]", "--team", "proj");

        Assert.Equal(2, badCode);
        Assert.Equal("ah roster: resync: --bind is not valid JSON\n", badErr);
        Assert.Equal(2, arrayCode);
        Assert.Equal("ah roster: resync: --bind must be a JSON object mapping member name to pane id\n", arrayErr);
    }

    private void HerdrKnowsPaneOnlyThroughPaneGet(bool paneExists)
    {
        _runner.Output = argv =>
        {
            if (argv.Contains("agent") && argv.Contains("list"))
            {
                return "{\"result\":{\"agents\":[]}}";
            }

            if (argv.Contains("pane") && argv.Contains("get"))
            {
                return paneExists
                    ? "{\"type\":\"pane_info\",\"result\":{\"pane\":{\"agent_status\":\"unknown\",\"pane_id\":\"%9\",\"tab_id\":\"t9\",\"workspace_id\":\"w9\"}}}"
                    : "{\"error\":{\"code\":\"pane_not_found\"}}";
            }

            return "{}";
        };
    }

    [Fact]
    public void BindAcceptsAPaneAtAShellThatHerdrKnowsOnlyThroughPaneGetAndTakesItsPlacement()
    {
        WriteTeam("proj", LiveTeamJson("herdr", TwoPeers));
        HerdrKnowsPaneOnlyThroughPaneGet(paneExists: true);

        var (code, outText, err) = Roster("resync", "--bind", "{\"proj-reviewer\":\"%9\"}", "--dry-run", "--team", "proj");

        Assert.True(code == 0, err);
        var row = JsonText.Parse(outText).Member("members")!.Value.AsArray().Single(m => m.Member("name")!.Value.AsString() == "proj-reviewer");
        Assert.Equal("updated", row.Member("status")!.Value.AsString());
        Assert.Equal("bind", row.Member("match_by")!.Value.AsString());
        Assert.Equal("%9", row.Member("to")!.Value.Member("transport_id")!.Value.AsString());
        Assert.Equal("t9", row.Member("to")!.Value.Member("tab_id")!.Value.AsString());
    }

    [Fact]
    public void BindStillRefusesAPaneThatNeitherTheAgentListNorPaneGetKnows()
    {
        WriteTeam("proj", LiveTeamJson("herdr", TwoPeers));
        HerdrKnowsPaneOnlyThroughPaneGet(paneExists: false);

        var (code, _, err) = Roster("resync", "--bind", "{\"proj-reviewer\":\"%9\"}", "--dry-run", "--team", "proj");

        Assert.NotEqual(0, code);
        Assert.Contains("is not in live herdr topology", err, StringComparison.Ordinal);
    }

    [Fact]
    public void MoveNeedsAKnownMemberWithAPaneAndExactlyOneDestination()
    {
        WriteTeam("proj", LiveTeamJson("herdr", TwoPeers + ",{\"role\":\"advisor\",\"name\":\"proj-advisor\",\"route\":\"subagent\"}"));

        var (unknownCode, _, unknownErr) = Roster("move", "ghost", "--new-tab", "--team", "proj");
        var (noPaneCode, _, noPaneErr) = Roster("move", "proj-advisor", "--new-tab", "--team", "proj");
        var (noModeCode, _, noModeErr) = Roster("move", "proj-implementor", "--team", "proj");
        var (twoCode, _, twoErr) = Roster("move", "proj-implementor", "--new-tab", "--new-workspace", "--team", "proj");
        var (splitCode, _, splitErr) = Roster("move", "proj-implementor", "--tab", "t1", "--team", "proj");

        Assert.Equal(2, unknownCode);
        Assert.StartsWith("ah roster: move: no member named \"ghost\" — known members: proj-implementor, proj-reviewer, proj-advisor", unknownErr, StringComparison.Ordinal);
        Assert.Equal(2, noPaneCode);
        Assert.Equal("ah roster: move: member proj-advisor has no pane to move\n", noPaneErr);
        Assert.Equal(2, noModeCode);
        Assert.Equal("ah roster: move: exactly one of --tab, --new-tab, --new-workspace is required\n", noModeErr);
        Assert.Equal(2, twoCode);
        Assert.Equal("ah roster: move: exactly one of --tab, --new-tab, --new-workspace is required\n", twoErr);
        Assert.Equal(2, splitCode);
        Assert.StartsWith("ah roster: move: --tab requires --split right|down", splitErr, StringComparison.Ordinal);
        Assert.DoesNotContain(_runner.Calls, argv => argv.Contains("move"));

        var (dryCode, dryOut, _) = Roster("move", "proj-implementor", "--tab", "t1", "--split", "right", "--dry-run", "--team", "proj");
        Assert.Equal(0, dryCode);
        Assert.Equal("herdr pane move %1 --tab t1 --split right", JsonText.Parse(dryOut).Member("command")!.Value.AsString());
    }

    private string MoveStreamTree => Path.Combine(_sandbox.Root, "stream-tree");

    private string MoveOtherTree => Path.Combine(_sandbox.Root, "other-tree");

    /// <summary>
    /// A herdr team of one implementor in tab <c>tab-a</c> (carrying <paramref name="memberStream"/>), a grants record at
    /// <paramref name="recordedRoot"/>, an open stream <c>s</c> in tab <c>tab-s</c> with work tree <paramref name="streamTree"/>
    /// and a done stream <c>gone</c> that kept tab <c>tab-done</c>. Herdr answers the agent list with the member in the tab it
    /// was moved to, and a tab list for stream-status.
    /// </summary>
    private void ArrangeMove(string? streamTree, string recordedRoot, string? memberStream = null, string transport = "herdr")
    {
        Directory.CreateDirectory(MoveStreamTree);
        Directory.CreateDirectory(MoveOtherTree);
        var stream = memberStream is null ? string.Empty : $",\"stream\":\"{memberStream}\"";
        var member = $"{{\"role\":\"implementor\",\"name\":\"proj-implementor\",\"route\":\"peer\",\"transport_id\":\"%1\",\"tab_id\":\"tab-a\",\"workspace_id\":\"w1\",\"model\":\"sonnet\"{stream}}}";
        var tree = streamTree is null ? "null" : $"\"{streamTree}\"";
        var streams =
            $"\"streams\":{{\"s\":{{\"state\":\"open\",\"branch\":\"feat/s\",\"base\":\"main\",\"worktree\":{tree},\"tab_id\":\"tab-s\",\"opened\":\"2026-10-11T10:00:00-04:00\"}},"
            + "\"gone\":{\"state\":\"done\",\"branch\":\"feat/gone\",\"base\":\"main\",\"worktree\":null,\"tab_id\":\"tab-done\",\"opened\":\"2026-10-10T10:00:00-04:00\",\"closed\":\"2026-10-10T12:00:00-04:00\"}}";
        WriteTeam("proj", LiveTeamJson(transport, member).Replace("\"layout\":\"auto\"}", "\"layout\":\"auto\"," + streams + "}", StringComparison.Ordinal));
        WriteGrants("enforce", $"{{\"proj-implementor\":{{\"work_root\":\"{recordedRoot}\",\"stream\":null,\"role\":\"implementor\",\"extra\":[]}}}}");
        _runner.Output = argv =>
        {
            if (argv.Contains("pane") && argv.Contains("layout"))
            {
                return "{\"result\":{\"layout\":{\"tab_id\":\"tab-a\",\"zoomed\":false,\"panes\":[]}}}";
            }

            if (argv.Contains("agent") && argv.Contains("list"))
            {
                var tab = "tab-a";
                if (_runner.Calls.LastOrDefault(c => c.Contains("move")) is { } moveCall)
                {
                    var at = Array.IndexOf(moveCall, "--tab");
                    tab = at >= 0 ? moveCall[at + 1] : "tab-new";
                }

                return "{\"result\":{\"agents\":[{\"name\":\"proj-implementor\",\"pane_id\":\"%1\",\"tab_id\":\"" + tab + "\",\"workspace_id\":\"w1\"}]}}";
            }

            return argv.Contains("tab") && argv.Contains("list")
                ? "{\"result\":{\"tabs\":[{\"tab_id\":\"tab-s\",\"label\":\"s\"},{\"tab_id\":\"tab-a\",\"label\":\"a\"}]}}"
                : "{}";
        };
    }

    private JsonValue MovedMember() =>
        JsonText.Parse(_sandbox.TeamText(TeamPath("proj"))).Member("members")!.Value.AsArray()[0];

    private string? MovedStream() => MovedMember().Member("stream") is { IsText: true } s ? s.AsString() : null;

    /// <summary>The <c>stream_attach</c> object of a move's output, or null when the key is absent or null.</summary>
    private static JsonValue? AttachOf(string stdout) =>
        JsonText.Parse(stdout).Member("stream_attach") is { IsMap: true } a ? a : (JsonValue?)null;

    private string PeersLine()
    {
        var (code, stdout, stderr) = Roster("stream-status", "--plain", "--team", "proj");
        Assert.True(code == 0, stderr);
        return stdout.Split('\n').FirstOrDefault(l => l.Contains(" s  feat/s  ", StringComparison.Ordinal)) ?? $"no line for s in: {stdout}";
    }

    [Fact]
    public void MoveIntoAStreamTabAttachesAMemberWhoseWorkRootIsTheStreamsWorkTree()
    {
        ArrangeMove(MoveStreamTree, MoveStreamTree);
        Assert.Contains(" s  feat/s  0 peers (", PeersLine(), StringComparison.Ordinal);

        var (code, stdout, stderr) = Roster("move", "proj-implementor", "--tab", "tab-s", "--split", "right", "--team", "proj");

        Assert.True(code == 0, stderr);
        Assert.Equal("s", MovedStream());
        var attach = AttachOf(stdout)!.Value;
        Assert.Equal("s", attach.Member("stream")!.Value.AsString());
        Assert.True(attach.Member("attached")!.Value.AsBool());
        Assert.Contains(_runner.Calls, argv => argv.Contains("move") && argv.Contains("tab-s"));
        Assert.Contains(" s  feat/s  1 peers (", PeersLine(), StringComparison.Ordinal);
    }

    [Fact]
    public void MoveIntoTheTabOfAStreamWithNoWorkTreeAttachesTheMember()
    {
        ArrangeMove(null, MoveOtherTree);

        var (code, stdout, stderr) = Roster("move", "proj-implementor", "--tab", "tab-s", "--split", "down", "--team", "proj");

        Assert.True(code == 0, stderr);
        Assert.Equal("s", MovedStream());
        Assert.True(AttachOf(stdout)!.Value.Member("attached")!.Value.AsBool());
    }

    [Fact]
    public void MoveIntoAStreamTabLeavesAMemberWorkingElsewhereUnattachedAndSaysWhy()
    {
        ArrangeMove(MoveStreamTree, MoveOtherTree);

        var (code, stdout, stderr) = Roster("move", "proj-implementor", "--tab", "tab-s", "--split", "right", "--team", "proj");

        Assert.True(code == 0, stderr);
        Assert.True(JsonText.Parse(stdout).Member("moved")!.Value.AsBool());
        Assert.Null(MovedStream());
        var attach = AttachOf(stdout)!.Value;
        Assert.Equal("s", attach.Member("stream")!.Value.AsString());
        Assert.False(attach.Member("attached")!.Value.AsBool());
        Assert.Equal(
            $"member works in {MoveOtherTree}, stream work tree is {MoveStreamTree}; respawn it with --stream s",
            attach.Member("why")!.Value.AsString());
    }

    [Fact]
    public void AnAttachingMoveLeavesTheMembersGrantsEntryByteForByte()
    {
        ArrangeMove(MoveStreamTree, MoveStreamTree);
        WriteGrants(
            "enforce",
            $"{{\"proj-implementor\":{{\"work_root\":\"{MoveStreamTree}\",\"stream\":null,\"role\":\"implementor\","
            + $"\"extra\":[{{\"path\":\"{MoveOtherTree}\",\"note\":\"n\",\"for\":null,\"outside\":false,\"at\":\"2026-10-16T00:00:00.000Z\",\"standing\":false}}],"
            + $"\"tmp\":{{\"root\":\"{MoveOtherTree}\",\"alias\":\"a\",\"kind\":\"claude\"}}}}}}");
        var before = GrantsText(Hierarchy);

        var (code, stdout, stderr) = Roster("move", "proj-implementor", "--tab", "tab-s", "--split", "right", "--team", "proj");

        Assert.True(code == 0, stderr);
        Assert.Equal("s", MovedStream());
        Assert.True(AttachOf(stdout)!.Value.Member("attached")!.Value.AsBool());
        Assert.Equal(before, GrantsText(Hierarchy));
    }

    [Fact]
    public void MoveIntoAStreamTabOfAMemberWithNoRecordedWorkTreeIsNotAttached()
    {
        ArrangeMove(MoveStreamTree, MoveStreamTree);
        WriteGrants("enforce");

        var (code, stdout, stderr) = Roster("move", "proj-implementor", "--tab", "tab-s", "--split", "right", "--team", "proj");

        Assert.True(code == 0, stderr);
        Assert.Null(MovedStream());
        var attach = AttachOf(stdout)!.Value;
        Assert.False(attach.Member("attached")!.Value.AsBool());
        Assert.Equal("member has no recorded work tree; respawn it with --stream s", attach.Member("why")!.Value.AsString());
    }

    [Theory]
    [InlineData("tab-s", "s")]
    [InlineData("tab-plain", null)]
    public void AMoveWhoseResyncQueryFailsSaysTheStreamWasNotAttachedAndWritesNothing(string tab, string? owner)
    {
        ArrangeMove(MoveStreamTree, MoveStreamTree);
        _runner.Exit = argv => argv.Contains("agent") && argv.Contains("list") ? 1 : 0;
        var before = _sandbox.TeamText(TeamPath("proj"));

        var (code, stdout, stderr) = Roster("move", "proj-implementor", "--tab", tab, "--split", "right", "--team", "proj");

        Assert.True(code == 0, stderr);
        var output = JsonText.Parse(stdout);
        Assert.True(output.Member("moved")!.Value.AsBool());
        Assert.False(output.Member("resync")!.Value.Member("ok")!.Value.AsBool());
        var attach = output.Member("stream_attach")!.Value;
        Assert.Equal(owner, attach.Member("stream")!.Value.IsText ? attach.Member("stream")!.Value.AsString() : null);
        Assert.False(attach.Member("attached")!.Value.AsBool());
        Assert.Equal("resync query failed; stream not attached, run ah roster move again", attach.Member("why")!.Value.AsString());
        Assert.Equal(before, _sandbox.TeamText(TeamPath("proj")));
    }

    [Fact]
    public void MoveIntoTheTabOfADoneStreamResolvesNoStream()
    {
        ArrangeMove(MoveStreamTree, MoveStreamTree);

        var (code, stdout, stderr) = Roster("move", "proj-implementor", "--tab", "tab-done", "--split", "right", "--team", "proj");

        Assert.True(code == 0, stderr);
        Assert.Null(MovedStream());
        Assert.Null(AttachOf(stdout));
    }

    [Fact]
    public void MoveIntoANonStreamTabKeepsAStaleStream()
    {
        ArrangeMove(MoveStreamTree, MoveStreamTree, memberStream: "s");

        var (code, stdout, stderr) = Roster("move", "proj-implementor", "--tab", "tab-plain", "--split", "right", "--team", "proj");

        Assert.True(code == 0, stderr);
        Assert.Equal("s", MovedStream());
        Assert.Null(AttachOf(stdout));
    }

    [Fact]
    public void MoveToANewTabNeitherAttachesNorDetaches()
    {
        ArrangeMove(MoveStreamTree, MoveStreamTree, memberStream: "s");

        var (code, stdout, stderr) = Roster("move", "proj-implementor", "--new-tab", "--team", "proj");

        Assert.True(code == 0, stderr);
        Assert.Equal("s", MovedStream());
        Assert.Null(AttachOf(stdout));
    }

    [Fact]
    public void ADryRunMoveIntoAStreamTabWritesNothing()
    {
        ArrangeMove(MoveStreamTree, MoveStreamTree);
        var before = _sandbox.TeamText(TeamPath("proj"));

        var (code, stdout, stderr) = Roster("move", "proj-implementor", "--tab", "tab-s", "--split", "right", "--dry-run", "--team", "proj");

        Assert.True(code == 0, stderr);
        Assert.Equal(before, _sandbox.TeamText(TeamPath("proj")));
        Assert.Null(AttachOf(stdout));
        Assert.DoesNotContain(_runner.Calls, argv => argv.Contains("move"));
    }

    [Fact]
    public void MoveOnATransportThatIsNotHerdrDoesNotAttach()
    {
        ArrangeMove(MoveStreamTree, MoveStreamTree, transport: "tmux");
        var before = _sandbox.TeamText(TeamPath("proj"));

        var (code, stdout, stderr) = Roster("move", "proj-implementor", "--tab", "tab-s", "--split", "right", "--team", "proj");

        Assert.True(code == 0, stderr);
        Assert.False(JsonText.Parse(stdout).Member("moved")!.Value.AsBool());
        Assert.Equal(before, _sandbox.TeamText(TeamPath("proj")));
        Assert.Null(AttachOf(stdout));
    }

    [Fact]
    public void NextSplitRefusesAModeAndACountItDoesNotKnow()
    {
        var (modeCode, _, modeErr) = Roster("next-split", "--mode", "diagonal", "--pane-count", "2", "--self", "p", "--created", "[]", "--geometry", "[]");
        var (countCode, _, countErr) = Roster("next-split", "--mode", "grid", "--pane-count", "-1", "--self", "p", "--created", "[]", "--geometry", "[]");
        var (missingCode, _, missingErr) = Roster("next-split", "--mode", "grid", "--pane-count", "2", "--created", "[]", "--geometry", "[]");

        Assert.Equal(2, modeCode);
        Assert.Equal("ah roster: --mode must be one of auto, columns, grid, got \"diagonal\"\n", modeErr);
        Assert.Equal(2, countCode);
        Assert.Equal("ah roster: --pane-count must be a non-negative integer, got \"-1\"\n", countErr);
        Assert.Equal(2, missingCode);
        Assert.Equal("ah roster: next-split needs --self <pane-id>\n", missingErr);
    }

    // ---------------------------------------------------------------- streams

    [Fact]
    public void StreamOpenRefusesAnInvalidNameAndPlansAValidOneWithoutWriting()
    {
        var (badCode, badOut, badErr) = Roster("stream-open", "Bad Name");
        Assert.Equal(2, badCode);
        Assert.Equal("stream-name-invalid", JsonText.Parse(badOut).Member("refused")!.Value.AsString());
        Assert.False(JsonText.Parse(badOut).Member("ok")!.Value.AsBool());
        Assert.Contains("must match /^[a-z][a-z0-9_-]{0,39}$/", badErr, StringComparison.Ordinal);

        var before = Tree();
        var (code, stdout, _) = Roster("stream-open", "feature-1", "--no-worktree", "--dry-run", "--orchestrator-pid", Self);
        Assert.Equal(0, code);
        var plan = JsonText.Parse(stdout);
        Assert.True(plan.Member("dry_run")!.Value.AsBool());
        Assert.Equal("feature-1", plan.Member("stream")!.Value.Member("name")!.Value.AsString());
        Assert.Empty(plan.Member("would_run")!.Value.Member("git")!.Value.AsArray());
        Assert.Equal(before, Tree());
    }

    private void InitProjectRepo()
    {
        foreach (var args in new[] { new[] { "init", "-q" }, new[] { "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "--allow-empty", "-m", "x" } })
        {
            var start = new ProcessStartInfo("git") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("-C");
            start.ArgumentList.Add(Project);
            foreach (var a in args)
            {
                start.ArgumentList.Add(a);
            }

            using var git = Process.Start(start) ?? throw new InvalidOperationException("git did not start");
            git.WaitForExit();
            Assert.Equal(0, git.ExitCode);
        }
    }

    [Theory]
    [InlineData("--branch", "-b", "branch")]
    [InlineData("--branch", "a..b", "branch")]
    [InlineData("--base", "-f", "base")]
    public void StreamOpenRefusesABranchOrBaseGitCouldReadAsAnOption(string flag, string value, string which)
    {
        InitProjectRepo();
        _runner.Exit = argv => argv.Contains("check-ref-format") && argv.Any(a => a.Contains("..", StringComparison.Ordinal)) ? 1 : 0;
        var before = Tree();
        var (code, stdout, stderr) = Roster("stream-open", "s1", flag, value, "--dry-run", "--orchestrator-pid", Self);
        Assert.True(code == 2, stdout + stderr);
        var refusal = JsonText.Parse(stdout);
        Assert.Equal("bad-ref", refusal.Member("refused")!.Value.AsString());
        Assert.Equal(which, refusal.Member("flag")!.Value.AsString());
        Assert.Equal(before, Tree());
        Assert.DoesNotContain(_runner.Calls, c => c.Contains("worktree") && c.Contains("add"));
    }

    [Theory]
    [InlineData("--branch", "feature/s-1")]
    [InlineData("--base", "HEAD")]
    public void StreamOpenPlansABranchOrBaseWithAnInnerDash(string flag, string value)
    {
        InitProjectRepo();
        var (code, stdout, stderr) = Roster("stream-open", "s1", flag, value, "--dry-run", "--orchestrator-pid", Self);
        Assert.True(code == 0, stdout + stderr);
        Assert.True(JsonText.Parse(stdout).Member("dry_run")!.Value.AsBool());
    }

    [Fact]
    public void StreamDoneRefusesAStreamTheTeamDoesNotHaveAndMarksOneItDoes()
    {
        var (unknownCode, unknownOut, _) = Roster("stream-done", "nope", "--team", "proj");
        Assert.Equal(2, unknownCode);
        Assert.Equal("unknown-stream", JsonText.Parse(unknownOut).Member("refused")!.Value.AsString());

        var team = LiveTeamJson("terminal", TwoPeers).TrimEnd('}')
            + ",\"streams\":{\"feature-1\":{\"state\":\"open\",\"branch\":null,\"base\":null,\"worktree\":null,\"tab_id\":null,\"opened\":\"2026-10-11T00:00:00.000Z\"}}}";
        WriteTeam("proj", team);

        var (code, stdout, _) = Roster("stream-done", "feature-1", "--team", "proj");

        Assert.Equal(0, code);
        var done = JsonText.Parse(stdout);
        Assert.True(done.Member("done")!.Value.AsBool());
        Assert.False(done.Member("already_done")!.Value.AsBool());
        var written = JsonText.Parse(_sandbox.TeamText(TeamPath("proj")));
        Assert.Equal("done", written.Member("streams")!.Value.Member("feature-1")!.Value.Member("state")!.Value.AsString());

        var (againCode, againOut, _) = Roster("stream-done", "feature-1", "--team", "proj");
        Assert.Equal(0, againCode);
        Assert.True(JsonText.Parse(againOut).Member("already_done")!.Value.AsBool());
    }

    // ---------------------------------------------------------------- progress records

    private string ProgressDir => Path.Combine(Hierarchy, "progress", "proj");

    // per-dir-files-to-db S11d: the checklists, journals and rollup are progress_docs rows, read and planted here by their old path
    private string DocText(string path) => Ah.TestKit.TestProgressDocs.Read(_sandbox.AhHome, path);

    private void PutDoc(string path, string body) => Ah.TestKit.TestProgressDocs.WriteFile(_sandbox.AhHome, path, body);

    private void DropDoc(string path) => Ah.TestKit.TestProgressDocs.Delete(_sandbox.AhHome, path);

    private bool DocExists(string path) => Ah.TestKit.TestProgressDocs.Exists(_sandbox.AhHome, path);

    /// <summary>Turns progress off for the team: its <c>.off</c> row (files-off-disk §6).</summary>
    private void ProgressOffFile()
    {
        Directory.CreateDirectory(ProgressDir);
        Ah.TestKit.TestProgressDocs.Off(_sandbox.AhHome, Hierarchy, "proj");
    }

    private (int Code, string Stdout, string Stderr) OpenStream(string name = "feature-1")
    {
        if (!_sandbox.TeamExists(TeamPath("proj")))
        {
            WriteTeam("proj", LiveTeamJson("terminal", TwoPeers));
        }

        return Roster("stream-open", name, "--no-worktree", "--team", "proj", "--orchestrator-pid", Self);
    }

    private JsonValue StreamRecord(string name = "feature-1") =>
        JsonText.Parse(_sandbox.TeamText(TeamPath("proj"))).Member("streams")!.Value.Member(name)!.Value;

    [Fact]
    public void StreamOpenThatCreatesTheTeamRegistersTheCallerAsItsOrchestrator()
    {
        _callers = new RecordingCallers();

        var (code, stdout, stderr) = Roster("stream-open", "feature-1", "--no-worktree", "--team", "proj", "--orchestrator-pid", Self);

        Assert.True(code == 0, stdout + stderr);
        Assert.Equal(["orchestrator@proj"], JsonText.Parse(stdout).Member("registered")!.Value.AsArray().Select(a => a.AsString()).ToArray());
    }

    [Fact]
    public void StreamOpenIntoAnExistingTeamRegistersNobody()
    {
        _callers = new RecordingCallers();

        var (code, stdout, stderr) = OpenStream();

        Assert.True(code == 0, stdout + stderr);
        Assert.Null(JsonText.Parse(stdout).Member("registered"));
        Assert.Empty(_callers.Calls);
    }

    [Fact]
    public void StreamOpenCreatesItsChecklistAndAReopenLeavesItAndOpenedAtAlone()
    {
        var (code, stdout, stderr) = OpenStream();

        Assert.True(code == 0, stdout + stderr);
        var path = Path.Combine(ProgressDir, "feature-1.md");
        var text = DocText(path);
        Assert.StartsWith("<!-- ah-progress v1.0 kind=stream -->\n# feature-1 — ", text, StringComparison.Ordinal);
        Assert.Contains("State: open", text, StringComparison.Ordinal);
        var openedAt = StreamRecord().Member("opened_at")!.Value.AsString();
        Assert.EndsWith("Z", openedAt, StringComparison.Ordinal);
        PutDoc(path,text + "- [ ] added by the orchestrator\n");

        var (again, againOut, againErr) = OpenStream();

        Assert.True(again == 0, againOut + againErr);
        Assert.Equal(text + "- [ ] added by the orchestrator\n", DocText(path));
        Assert.Equal(openedAt, StreamRecord().Member("opened_at")!.Value.AsString());
    }

    private static readonly string[] CommitArgs = ["create", "--commit", "--verified", "[]", "--transport", "terminal", "--roster-level", "repo"];

    [Fact]
    public void CommitCreatesTheRollupAndTheMainChecklist()
    {
        var (code, stdout, stderr) = Roster([.. CommitArgs, "--orchestrator-pid", Self]);

        Assert.True(code == 0, stdout + stderr);
        Assert.StartsWith("<!-- ah-progress v1.0 kind=rollup -->\n", DocText(Path.Combine(ProgressDir, "rollup.md")), StringComparison.Ordinal);
        Assert.StartsWith("<!-- ah-progress v1.0 kind=stream -->\n# main — ", DocText(Path.Combine(ProgressDir, "main.md")), StringComparison.Ordinal);
        Assert.Equal(2, JsonText.Parse(stdout).Member("progress")!.Value.Member("created")!.Value.AsArray().Count);
    }

    [Fact]
    public void CommitCreatesNoProgressFilesWhenProgressIsOff()
    {
        ProgressOffFile();

        var (code, stdout, stderr) = Roster([.. CommitArgs, "--orchestrator-pid", Self]);

        Assert.True(code == 0, stdout + stderr);
        Assert.Empty(Directory.GetFileSystemEntries(ProgressDir));
        Assert.Null(JsonText.Parse(stdout).Member("progress"));
    }

    [Fact]
    public void StreamOpenCreatesNoChecklistWhenProgressIsOff()
    {
        ProgressOffFile();

        var (code, stdout, stderr) = OpenStream();

        Assert.True(code == 0, stdout + stderr);
        Assert.Empty(Directory.GetFileSystemEntries(ProgressDir));
    }

    private const string Checklist = "<!-- ah-progress v1.0 kind=stream -->\n# feature-1 — goal\nOwner: m   Branch: b   Work tree: w   State: open\n"
        + "Updated: 2026-10-16T00:30:00+00:00\n\n## Checklist\n### Done\n- [x] one\n### In progress\n- [ ] two\n- [ ] three\n### Not started\n- [ ] four\n\n## Mystery\nkept: line\n";

    [Fact]
    public void StreamDoneWarnsOfOpenItemsRewritesOnlyTheStateWordAndRecordsDoneAt()
    {
        OpenStream();
        var path = Path.Combine(ProgressDir, "feature-1.md");
        PutDoc(path,Checklist);

        var (code, stdout, stderr) = Roster("stream-done", "feature-1", "--team", "proj");

        Assert.True(code == 0, stdout + stderr);
        Assert.Equal(Checklist.Replace("State: open", "State: done", StringComparison.Ordinal), DocText(path));
        var warnings = JsonText.Parse(stdout).Member("warnings")!.Value.AsArray().Select(w => w.AsString()).ToList();
        Assert.Contains(warnings, w => w.Contains("2 item(s) In progress", StringComparison.Ordinal));
        var record = StreamRecord();
        Assert.EndsWith("Z", record.Member("done_at")!.Value.AsString(), StringComparison.Ordinal);
        Assert.False(string.IsNullOrEmpty(record.Member("closed")!.Value.AsString()));
    }

    [Fact]
    public void StreamDoneOfATeamWithProgressOffWarnsOfNothingAndLeavesTheChecklistAlone()
    {
        OpenStream();
        var path = Path.Combine(ProgressDir, "feature-1.md");
        PutDoc(path,Checklist);
        ProgressOffFile();

        var (code, stdout, stderr) = Roster("stream-done", "feature-1", "--team", "proj");

        Assert.True(code == 0, stdout + stderr);
        Assert.Equal(Checklist, DocText(path));
        Assert.Null(JsonText.Parse(stdout).Member("warnings"));
        Assert.False(string.IsNullOrEmpty(StreamRecord().Member("closed")!.Value.AsString()));
    }

    [Fact]
    public void StreamDoneWithNothingInProgressAndNoStateLineWarnsOfNothingAndLeavesTheFileAlone()
    {
        OpenStream();
        var path = Path.Combine(ProgressDir, "feature-1.md");
        const string bare = "# hand written\n### Done\n- [x] one\n";
        PutDoc(path,bare);

        var (code, stdout, stderr) = Roster("stream-done", "feature-1", "--team", "proj");

        Assert.True(code == 0, stdout + stderr);
        Assert.Equal(bare, DocText(path));
        Assert.Null(JsonText.Parse(stdout).Member("warnings"));
    }

    [Theory]
    [InlineData("2026-10-16T01:01:00+00:00", true)]
    [InlineData("2026-10-16T00:59:00+00:00", false)]
    public void StreamStatusShowsTheCountsAndCallsAStreamStaleOnlyPastThirtyMinutes(string newestEntry, bool stale)
    {
        OpenStream();
        PutDoc(Path.Combine(ProgressDir, "feature-1.md"),Checklist);
        PutDoc(
            Path.Combine(ProgressDir, "feature-1.journal.md"),
            "<!-- ah-progress v1.0 kind=journal -->\n\n## " + newestEntry + " · m · slice id1\n- Status: s\n- Report: r\n<!-- ah-entry id=id1 sha=x -->\n");

        var (code, stdout, stderr) = Roster("stream-status", "feature-1", "--team", "proj");

        Assert.True(code == 0, stdout + stderr);
        var progress = JsonText.Parse(stdout).Member("streams")!.Value.AsArray()[0].Member("progress")!.Value;
        Assert.Equal((1, 2, 1), (
            (int)progress.Member("done")!.Value.AsNumber(),
            (int)progress.Member("in_progress")!.Value.AsNumber(),
            (int)progress.Member("not_started")!.Value.AsNumber()));
        Assert.Equal(stale, progress.Member("stale")!.Value.AsBool());
    }

    [Fact]
    public void StreamStatusNamesTheFileAndLineOfAChecklistLineItCouldNotRead()
    {
        OpenStream();
        PutDoc(Path.Combine(ProgressDir, "feature-1.md"),Checklist.Replace("State: open", "State: bogus", StringComparison.Ordinal));

        var (code, stdout, stderr) = Roster("stream-status", "feature-1", "--team", "proj");

        Assert.True(code == 0, stdout + stderr);
        var warnings = JsonText.Parse(stdout).Member("warnings")!.Value.AsArray().Select(w => w.AsString()).ToList();
        Assert.Contains("feature-1.md: line 3: State: \"bogus\" is not open, done or parked", warnings);
    }

    [Fact]
    public void StreamStatusNamesTheFileAndLineOfAPlainChecklistLineItDoesNotCount()
    {
        OpenStream();
        PutDoc(Path.Combine(ProgressDir, "feature-1.md"),Checklist.Replace("- [ ] four", "- four", StringComparison.Ordinal));

        var (code, stdout, stderr) = Roster("stream-status", "feature-1", "--team", "proj");

        Assert.True(code == 0, stdout + stderr);
        var warnings = JsonText.Parse(stdout).Member("warnings")!.Value.AsArray().Select(w => w.AsString()).ToList();
        Assert.Contains("feature-1.md: line 13: \"- four\" under Not started is not a checkbox line (- [ ] or - [x]), so it is not counted", warnings);
    }

    [Fact]
    public void DoctorWarnsOnTheProgressRowWithTheLineOfAPlainChecklistLineItDoesNotCount()
    {
        OpenStream();
        PutDoc(Path.Combine(ProgressDir, "feature-1.md"),Checklist.Replace("- [ ] four", "- four", StringComparison.Ordinal));

        var row = RowNamed(DoctorRows(), "progress")!;

        Assert.Equal("warn", row["status"].AsString());
        Assert.Contains(
            "feature-1.md: line 13: \"- four\" under Not started is not a checkbox line (- [ ] or - [x]), so it is not counted",
            row["detail"].AsString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void DoctorDoesNotWarnOnTheProgressRowOfAChecklistOfCheckboxLines()
    {
        OpenStream();
        PutDoc(Path.Combine(ProgressDir, "feature-1.md"),Checklist);

        var row = RowNamed(DoctorRows(), "progress")!;

        Assert.Equal("ok", row["status"].AsString());
        Assert.DoesNotContain("not a checkbox line", row["detail"].AsString(), StringComparison.Ordinal);
    }

    [Fact]
    public void StreamStatusOfAReadableChecklistAddsNoWarning()
    {
        OpenStream();
        PutDoc(Path.Combine(ProgressDir, "feature-1.md"),Checklist);

        var (code, stdout, stderr) = Roster("stream-status", "feature-1", "--team", "proj");

        Assert.True(code == 0, stdout + stderr);
        Assert.Null(JsonText.Parse(stdout).Member("warnings"));
    }

    [Fact]
    public void StreamStatusOfATeamWithProgressOffShowsNoProgress()
    {
        ProgressOffFile();
        OpenStream();

        var (code, stdout, stderr) = Roster("stream-status", "feature-1", "--team", "proj");

        Assert.True(code == 0, stdout + stderr);
        Assert.Null(JsonText.Parse(stdout).Member("streams")!.Value.AsArray()[0].Member("progress"));
    }

    private void WriteProgress(string stream, string checklist, string? entryTime)
    {
        PutDoc(Path.Combine(ProgressDir, stream + ".md"), checklist.Replace("feature-1", stream, StringComparison.Ordinal));
        if (entryTime is not null)
        {
            PutDoc(
                Path.Combine(ProgressDir, stream + ".journal.md"),
                "<!-- ah-progress v1.0 kind=journal -->\n\n## " + entryTime + " · m · slice id1\n- Status: s\n- Report: r\n<!-- ah-entry id=id1 sha=x -->\n");
        }
    }

    private (string Status, string Detail) ProgressRowOf()
    {
        var row = RowNamed(DoctorRows(), "progress")!;
        return (row["status"].AsString(), row["detail"].AsString());
    }

    [Fact]
    public void DoctorProgressIsOkWhenEveryOpenStreamHasAChecklistAndNoneIsStale()
    {
        OpenStream();
        WriteProgress("main", Checklist, "2026-10-16T00:59:00+00:00");
        WriteProgress("feature-1", Checklist, "2026-10-16T00:59:00+00:00");

        var (status, detail) = ProgressRowOf();

        Assert.Equal("ok", status);
        Assert.Contains("none stale", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void DoctorProgressWarnsOfAStreamWhoseNewestReportIsMoreThanThirtyMinutesPastItsChecklist()
    {
        OpenStream();
        WriteProgress("main", Checklist, "2026-10-16T00:59:00+00:00");
        WriteProgress("feature-1", Checklist, "2026-10-16T01:01:00+00:00");

        var (status, detail) = ProgressRowOf();

        Assert.Equal("warn", status);
        Assert.Contains("checklist behind its journal for feature-1", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("main", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void DoctorProgressWarnsOfAnOpenStreamWithNoChecklist()
    {
        OpenStream();
        WriteProgress("main", Checklist, null);
        DropDoc(Path.Combine(ProgressDir, "feature-1.md"));

        var (status, detail) = ProgressRowOf();

        Assert.Equal("warn", status);
        Assert.Contains("no checklist for feature-1", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void DoctorProgressIsOkForATeamWhoseFirstReportCameFromAStreamAndSoHasNoMainChecklist()
    {
        OpenStream();
        PutDoc(Path.Combine(ProgressDir, "rollup.md"), "<!-- ah-progress v1.0 kind=rollup -->\n# proj\n");
        WriteProgress("feature-1", Checklist, "2026-10-16T00:59:00+00:00");
        Assert.False(DocExists(Path.Combine(ProgressDir, "main.md")));

        var (status, detail) = ProgressRowOf();

        Assert.Equal("ok", status);
        Assert.DoesNotContain("main", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void DoctorProgressStillWarnsOfMainWhenItHasAJournalButNoChecklist()
    {
        OpenStream();
        WriteProgress("feature-1", Checklist, null);
        WriteProgress("main", Checklist, "2026-10-16T00:59:00+00:00");
        DropDoc(Path.Combine(ProgressDir, "main.md"));

        var (status, detail) = ProgressRowOf();

        Assert.Equal("warn", status);
        Assert.Contains("no checklist for main", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void DoctorProgressWarnsOfAChecklistLineItCouldNotReadNamingTheFileAndLine()
    {
        OpenStream();
        WriteProgress("main", Checklist, null);
        WriteProgress("feature-1", Checklist.Replace("Updated: 2026-10-16T00:30:00+00:00", "Updated: yesterday", StringComparison.Ordinal), null);

        var (status, detail) = ProgressRowOf();

        Assert.Equal("warn", status);
        Assert.Contains("feature-1.md: line 4: Updated: \"yesterday\" is not a time", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void DoctorProgressSaysOffAsAnOkRowWhenTheTeamTurnedProgressOff()
    {
        OpenStream();
        DropDoc(Path.Combine(ProgressDir, "feature-1.md"));
        ProgressOffFile();

        var (status, detail) = ProgressRowOf();

        Assert.Equal("ok", status);
        Assert.StartsWith("off:", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void DoctorProgressNamesAFileANewerAhWroteAndNeverGoesRed()
    {
        OpenStream();
        WriteProgress("main", Checklist, null);
        WriteProgress("feature-1", Checklist.Replace("v1.0", "v2.0", StringComparison.Ordinal), null);

        var (status, detail) = ProgressRowOf();

        Assert.Equal("warn", status);
        Assert.Contains("written by a newer ah, read only: feature-1", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void DoctorProgressIsOkForATeamWithNoProgressRecordsYet()
    {
        WriteTeam("proj", LiveTeamJson("terminal", TwoPeers));

        var (status, detail) = ProgressRowOf();

        Assert.Equal("ok", status);
        Assert.Contains("no progress records yet", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void DoctorProgressOfTheDefaultScopeTeamReadsTheUnderscoreDefaultFolder()
    {
        _sandbox.PlantTeam(Path.Combine(Hierarchy, "team.json"), LiveTeamJson("terminal", TwoPeers));
        var folder = Path.Combine(Hierarchy, "progress", "_default");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "rollup.md"), "<!-- ah-progress v1.0 kind=rollup -->\n# team\n");
        File.WriteAllText(Path.Combine(folder, "main.md"), Checklist.Replace("feature-1", "main", StringComparison.Ordinal));
        Directory.CreateDirectory(Path.Combine(Hierarchy, "progress", "team"));

        var (code, stdout, stderr) = Roster("doctor");
        Assert.True(code == 0, stdout + stderr);
        var row = RowNamed(JsonText.Parse(stdout).Member("rows")!.Value.AsArray(), "progress")!;

        Assert.Contains("1 open stream(s) with checklists", row["detail"].AsString(), StringComparison.Ordinal);
    }

    /// <summary>A linked work tree of the project with the team file in the main checkout's home; returns the tree and both progress folders.</summary>
    private (string Tree, string MainFolder, string TreeFolder) LinkedTreeWithTeam()
    {
        _extraEnv["WRANGL_HIERARCHY_DIR"] = string.Empty;
        var home = Path.Combine(Project, ".claude", "ah2");
        Directory.CreateDirectory(Path.Combine(home, "teams"));
        _sandbox.PlantTeam(Path.Combine(home, "teams", "proj.json"),LiveTeamJson("terminal", TwoPeers));
        var tree = FamilyWorkTree("wt");
        return (tree, Path.Combine(home, "progress", "proj"), Path.Combine(tree, ".claude", "ah2", "progress", "proj"));
    }

    [Fact]
    public void StreamOpenInALinkedWorkTreeCreatesItsChecklistInTheMainCheckoutsPool()
    {
        var (tree, mainFolder, treeFolder) = LinkedTreeWithTeam();

        var (code, stdout, stderr) = Roster("stream-open", "feature-1", "--no-worktree", "--team", "proj", "--orchestrator-pid", Self, "--cwd", tree);

        Assert.True(code == 0, stdout + stderr);
        Assert.True(DocExists(Path.Combine(mainFolder, "feature-1.md")));
        Assert.False(Directory.Exists(treeFolder));
    }

    [Fact]
    public void StreamStatusAndDoctorInALinkedWorkTreeReadTheMainCheckoutsPool()
    {
        var (tree, mainFolder, treeFolder) = LinkedTreeWithTeam();
        _sandbox.PlantTeam(Path.Combine(Project, ".claude", "ah2", "teams", "proj.json"),TeamWithStream(tree));
        Directory.CreateDirectory(mainFolder);
        TestProgressDocs.WriteFile(_sandbox.AhHome, Path.Combine(mainFolder, "rollup.md"), "<!-- ah-progress v1.0 kind=rollup -->\n# proj\n");
        TestProgressDocs.WriteFile(_sandbox.AhHome, Path.Combine(mainFolder, "main.md"), Checklist.Replace("feature-1", "main", StringComparison.Ordinal));
        TestProgressDocs.WriteFile(_sandbox.AhHome, Path.Combine(mainFolder, "s1.md"), Checklist.Replace("feature-1", "s1", StringComparison.Ordinal));

        var (code, stdout, stderr) = Roster("stream-status", "s1", "--team", "proj", "--cwd", tree);
        Assert.True(code == 0, stdout + stderr);
        var progress = JsonText.Parse(stdout).Member("streams")!.Value.AsArray()[0].Member("progress")!.Value;
        var (doctorCode, doctorOut, doctorErr) = Roster("doctor", "--team", "proj", "--cwd", tree);
        Assert.True(doctorCode == 0, doctorOut + doctorErr);
        var row = RowNamed(JsonText.Parse(doctorOut).Member("rows")!.Value.AsArray(), "progress")!;

        Assert.Equal(2, (int)progress.Member("in_progress")!.Value.AsNumber());
        Assert.Contains("2 open stream(s) with checklists", row["detail"].AsString(), StringComparison.Ordinal);

        var (doneCode, doneOut, doneErr) = Roster("stream-done", "s1", "--team", "proj", "--cwd", tree);
        Assert.True(doneCode == 0, doneOut + doneErr);
        Assert.Contains("State: done", DocText(Path.Combine(mainFolder, "s1.md")), StringComparison.Ordinal);
        Assert.False(Directory.Exists(treeFolder));
    }

    // ---------------------------------------------------------------- snapshot and resume

    [Fact]
    public void ResumeRefusesWhenThereIsNoSnapshotAndOnlyPlansWithoutYes()
    {
        var (noneCode, _, noneErr) = Roster("resume", "--team", "proj");
        Assert.Equal(2, noneCode);
        Assert.Contains("resume: no snapshot of team \"proj\" in ", noneErr, StringComparison.Ordinal);
        Assert.Contains("take one with `snapshot`", noneErr, StringComparison.Ordinal);

        WriteTeam("proj", LiveTeamJson("terminal", TwoPeers));
        var (snapCode, snapOut, _) = Roster("snapshot", "--team", "proj");
        Assert.Equal(0, snapCode);
        var snapshotPath = JsonText.Parse(snapOut).Member("snapshot")!.Value.AsString();
        Assert.NotNull(SnapshotRows.ReadSnapshot(SnapshotHomeOf(snapshotPath), Path.GetFileName(snapshotPath)));
        var callsBefore = _runner.Calls.Count;

        var (planCode, planOut, _) = Roster("resume", "--team", "proj");

        Assert.Equal(0, planCode);
        var plan = JsonText.Parse(planOut);
        Assert.Equal("planned", plan.Member("status")!.Value.AsString());
        Assert.Equal(2, plan.Member("members")!.Value.AsArray().Count);
        Assert.DoesNotContain(_runner.Calls.Skip(callsBefore), argv => argv.Any(a => a.Contains("claude", StringComparison.Ordinal)));
    }

    [Fact]
    public void ResumeRefusesATeamFlagThatIsNotTheSnapshotsTeamAndAFileThatIsNotASnapshot()
    {
        WriteTeam("proj", LiveTeamJson("terminal", TwoPeers));
        var snapshotPath = JsonText.Parse(Roster("snapshot", "--team", "proj").Stdout).Member("snapshot")!.Value.AsString();

        var (mismatchCode, _, mismatchErr) = Roster("resume", snapshotPath, "--team", "other");
        Assert.Equal(2, mismatchCode);
        Assert.Equal("ah roster: resume: --team other does not match the snapshot's team proj\n", mismatchErr);

        var notSnapshot = Path.Combine(_sandbox.Root, "not-a-snapshot.json");
        File.WriteAllText(notSnapshot, "{\"hello\":1}");
        var (badCode, _, badErr) = Roster("resume", notSnapshot);
        Assert.Equal(2, badCode);
        Assert.Contains("is not a team snapshot", badErr, StringComparison.Ordinal);

        var (okCode, _, _) = Roster("resume", snapshotPath, "--team", "proj");
        Assert.Equal(0, okCode);
    }

    [Fact]
    public void UntrackAlsoConfigReportsAConfigThatCannotBeEditedAndStillForgetsTheRow()
    {
        WriteTeam("proj", LiveTeamJson("terminal", TwoPeers));

        var (plainCode, plainOut, _) = Roster("untrack", "proj-implementor", "--commit", "--team", "proj");
        Assert.Equal(0, plainCode);
        Assert.True(JsonText.Parse(plainOut).Member("config")!.Value.IsNull);

        var (code, stdout, stderr) = Roster("untrack", "proj-reviewer", "--commit", "--also-config", "--team", "proj");

        Assert.Equal(0, code);
        var result = JsonText.Parse(stdout);
        Assert.True(result.Member("untracked")!.Value.AsBool());
        var config = result.Member("config")!.Value;
        Assert.False(config.Member("removed")!.Value.AsBool());
        Assert.Contains("was not removed from the config", stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0x124, false)] // r--r--r--
    [InlineData(0x1A4, true)] // rw-r--r--
    public void UntrackAlsoConfigLeavesAConfigFileThatForbidsWritingByteForByte(int mode, bool removed)
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
        {
            return;
        }

        WriteTeam("proj", LiveTeamJson("terminal", TwoPeers));
        var config = Path.Combine(Project, ".claude", "ah2", "agent-hierarchy.json");
        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        const string Body = "{\"version\":1,\"roster\":{\"route\":\"peer\",\"members\":[{\"role\":\"reviewer\",\"model\":\"sonnet\"},{\"role\":\"implementor\",\"model\":\"sonnet\"}]}}\n";
        File.WriteAllText(config, Body);
        File.SetUnixFileMode(config, (UnixFileMode)mode);

        var (code, stdout, stderr) = Roster("untrack", "proj-reviewer", "--commit", "--also-config", "--team", "proj");

        Assert.Equal(0, code);
        var result = JsonText.Parse(stdout).Member("config")!.Value;
        Assert.Equal(removed, result.Member("removed")!.Value.AsBool());
        if (!removed)
        {
            Assert.Equal(Body, File.ReadAllText(config));
            Assert.Contains("could not be written", stderr, StringComparison.Ordinal);
        }
        else
        {
            Assert.DoesNotContain("reviewer", File.ReadAllText(config), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ASpawnIntoAStreamTheTeamDoesNotHaveOrHasDoneIsRefusedBeforeAnythingLaunches()
    {
        var (unknownCode, unknownOut, _) = Roster("spawn-ad-hoc", "implementor", "--stream", "s1", "--team", "proj", "--orchestrator-pid", Self);
        Assert.Equal(2, unknownCode);
        Assert.Equal("unknown-stream", JsonText.Parse(unknownOut).Member("refused")!.Value.AsString());

        var team = LiveTeamJson("terminal", TwoPeers).TrimEnd('}')
            + ",\"streams\":{\"s1\":{\"state\":\"done\",\"branch\":null,\"base\":null,\"worktree\":null,\"tab_id\":null,\"opened\":\"2026-10-11T00:00:00.000Z\"}}}";
        WriteTeam("proj", team);

        var (doneCode, doneOut, _) = Roster("spawn-ad-hoc", "implementor", "--stream", "s1", "--team", "proj", "--orchestrator-pid", Self);
        Assert.Equal(2, doneCode);
        var refused = JsonText.Parse(doneOut);
        Assert.Equal("stream-done", refused.Member("refused")!.Value.AsString());
        Assert.Equal("ah roster stream-open s1 --team proj", refused.Member("next")!.Value.AsArray()[0].AsString());
        Assert.DoesNotContain(_runner.Calls, argv => argv.Any(a => a.Contains("claude", StringComparison.Ordinal)));
    }

    // ---------------------------------------------------------------- work tree assignment


    private void WriteGrants(string mode, string members = "{}") => WriteHomeGrants(Hierarchy, mode, members);

    /// <summary><paramref name="home"/>'s <c>proj</c> grants document text, from wherever the home keeps it (S11e: the store once a database exists); null when none.</summary>
    private string? GrantsText(string home, string team = "proj") => TestGrants.Text(_sandbox.AhHome, _sandbox.Home, home, team);

    private bool HasGrants(string home, string team = "proj") => GrantsText(home, team) is not null;

    private void DeleteGrants(string home, string team = "proj") => TestGrants.Delete(_sandbox.AhHome, home, team);

    private JsonObject GrantedMembers() => JsonText.Parse(GrantsText(Hierarchy)!).Member("members")!.Value.AsObject();

    private string MakeWorkTree(string name)
    {
        var tree = Path.Combine(Project, ".claude", "worktrees", name);
        Directory.CreateDirectory(tree);
        File.WriteAllText(Path.Combine(tree, ".git"), "gitdir: " + Path.Combine(Project, ".git", "worktrees", name) + "\n");
        return tree;
    }

    private string TeamWithStream(string worktree) =>
        LiveTeamJson("terminal", string.Empty).TrimEnd('}')
        + ",\"streams\":{\"s1\":{\"state\":\"open\",\"branch\":\"feat/s1\",\"base\":\"main\",\"worktree\":\"" + worktree
        + "\",\"tab_id\":null,\"opened\":\"2026-10-11T00:00:00.000Z\"}}}";

    private (int Code, JsonObject Out) SpawnImplementor(params string[] extra)
    {
        AgeSpawnedRows();
        var (code, stdout, _) = Roster(["spawn-ad-hoc", "implementor", "--model", "sonnet", "--team", "proj", "--orchestrator-pid", Self, .. extra]);
        return (code, stdout.Length > 0 ? JsonText.Parse(stdout).AsObject() : new JsonObject());
    }

    private string BoxRootOf(string team)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        var variables = new Dictionary<string, string?>
        {
            ["AH_TEST"] = "1",
            ["AH_HOME"] = _sandbox.AhHome,
            ["HOME"] = _sandbox.Home,
            ["WRANGL_HIERARCHY_DIR"] = Hierarchy,
        };
        var fake = new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables, new PluginInfo(_sandbox.PluginRoot, "wrangl", "0.134.10", "1"));
        var io = PlatformModule.CreateIo(null);
        var graph = ServiceGraph.Build(io, fake, _runner, new FakeDaemonClient());
        return new RosterComposition(graph, io.Clock, fake).MemberBoxRoot(TeamPath(team))!;
    }

    private void MakeBoxes(string team, params string[] members)
    {
        foreach (var member in members)
        {
            Directory.CreateDirectory(Path.Combine(BoxRootOf(team), member));
        }
    }

    private string NextImplementorName()
    {
        var (code, result) = SpawnImplementor("--dry-run");
        Assert.Equal(0, code);
        return result["name"].AsString();
    }

    private static string PeerRow(string name) =>
        "{\"role\":\"implementor\",\"name\":\"" + name + "\",\"route\":\"peer\",\"transport_id\":\"%9\",\"model\":\"sonnet\"}";

    [Fact]
    public void ADismissedMembersSuffixIsNotHandedOutAgainWhileItsBoxRemains()
    {
        WriteTeam("proj", LiveTeamJson("terminal", PeerRow("proj-implementor")));
        MakeBoxes("proj", "proj-implementor", "proj-implementor-2");

        Assert.Equal("proj-implementor-3", NextImplementorName());
    }

    [Fact]
    public void TheNextSuffixIsOneAboveTheHighestBoxEvenWhenOnlyALowerMemberIsLive()
    {
        WriteTeam("proj", LiveTeamJson("terminal", PeerRow("proj-implementor-2")));
        MakeBoxes("proj", "proj-implementor", "proj-implementor-2", "proj-implementor-3", "proj-implementor-4");

        Assert.Equal("proj-implementor-5", NextImplementorName());
    }

    [Fact]
    public void ATeamHistoryEntryOfThreeImplementorsRaisesTheNextSuffixToFour()
    {
        WriteTeam("proj", LiveTeamJson("terminal", string.Empty));
        File.WriteAllText(
            Path.Combine(Hierarchy, "team-history.json"),
            "{\"version\":1,\"teams\":[{\"id\":\"h1\",\"fingerprint\":\"f1\",\"alias\":\"proj\",\"members\":["
            + "{\"role\":\"implementor\"},{\"role\":\"implementor\"},{\"role\":\"implementor\"},{\"role\":\"reviewer\"}]}]}");

        Assert.Equal("proj-implementor-4", NextImplementorName());
    }

    private static readonly string[] LiveImplementorSuffixes = ["", "-2", "-6", "-7", "-8", "-19", "-28", "-29"];

    [Fact]
    public void ALiveTeamWithGapsInItsImplementorSuffixesGetsAFreshNameNotALiveMembers()
    {
        WriteTeam("proj", LiveTeamJson("terminal", string.Join(",", LiveImplementorSuffixes.Select(s => PeerRow("proj-implementor" + s)))));

        Assert.Equal("proj-implementor-30", NextImplementorName());
    }

    [Fact]
    public void ANameFreedByARenameIsNotHandedOutWhileTheOldSessionMayStillAnswerToIt()
    {
        var renamed = "{\"role\":\"implementor\",\"name\":\"proj-implementor-2\",\"renamed_from\":\"proj-implementor-7\",\"route\":\"peer\",\"transport_id\":\"%9\",\"model\":\"sonnet\"}";
        WriteTeam("proj", LiveTeamJson("terminal", renamed));

        Assert.Equal("proj-implementor-8", NextImplementorName());
    }

    [Fact]
    public void ARenamedFromNameOfAnotherRoleDoesNotRaiseTheSuffix()
    {
        var renamed = "{\"role\":\"implementor\",\"name\":\"proj-implementor-2\",\"renamed_from\":\"proj-reviewer-7\",\"route\":\"peer\",\"transport_id\":\"%9\",\"model\":\"sonnet\"}";
        WriteTeam("proj", LiveTeamJson("terminal", renamed));

        Assert.Equal("proj-implementor-3", NextImplementorName());
    }

    [Fact]
    public void ACodexMemberWithNoRouteDefaultsToThePane()
    {
        WriteTeam("proj", LiveTeamJson("terminal", string.Empty));

        var (_, _, stderr) = Roster("spawn-ad-hoc", "implementor", "--kind", "codex", "--team", "proj", "--orchestrator-pid", Self, "--dry-run");

        Assert.DoesNotContain("route must be", stderr, StringComparison.Ordinal);
    }

    private (int Code, string Stdout, string Stderr) CodexDryRun()
    {
        WriteTeam("proj", LiveTeamJson("terminal", string.Empty));
        return Roster("spawn-ad-hoc", "implementor", "--kind", "codex", "--team", "proj", "--orchestrator-pid", Self, "--dry-run");
    }

    [Fact]
    public void ACodexDryRunInADirectoryCodexDoesNotTrustIsRefusedBeforeAnyPaneOpens()
    {
        var codexHome = Path.Combine(_sandbox.Home, ".codex");
        Directory.CreateDirectory(codexHome);
        File.WriteAllText(Path.Combine(codexHome, "config.toml"), "[projects.\"/somewhere/else\"]\ntrust_level = \"trusted\"\n");

        var (code, _, stderr) = CodexDryRun();

        Assert.True(code == 2 && stderr.Contains("Codex has not been told to trust", StringComparison.Ordinal), code + "|" + stderr);
    }

    [Fact]
    public void ACodexDryRunInATrustedDirectoryIsNotRefusedForTrust()
    {
        var codexHome = Path.Combine(_sandbox.Home, ".codex");
        Directory.CreateDirectory(codexHome);
        File.WriteAllText(Path.Combine(codexHome, "config.toml"), $"[projects.\"{Project}\"]\ntrust_level = \"trusted\"\n");

        var (_, stdout, stderr) = CodexDryRun();

        Assert.DoesNotContain("Codex has not been told to trust", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("harness-cwd-untrusted", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void ACodexDryRunWithNoCodexConfigCarriesTheTrustNeedAndIsNotRefused()
    {
        PutHerdrOnPath();
        WriteTeam("proj", LiveTeamJson("terminal", string.Empty));

        var (code, stdout, stderr) = Roster("spawn-ad-hoc", "implementor", "--kind", "codex", "--model", "gpt-5", "--team", "proj", "--orchestrator-pid", Self, "--dry-run");

        Assert.True(code == 0, stderr);
        Assert.Contains("\"needs\":[{\"kind\":\"codex-trust\",\"root\":", stdout.Replace(" ", string.Empty, StringComparison.Ordinal).Replace("\n", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains(" codex-trust ", stdout, StringComparison.Ordinal);
        Assert.Contains(" --yes", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAdHocSpawnUnderALongTeamNameGetsAMemberNameWithinTheLimitAndKeepsTheRole()
    {
        const string team = "alarms-team-long-name-abc";
        PutHerdrOnPath();
        WriteTeam(team, LiveTeamJson("terminal", string.Empty));

        var (code, stdout, stderr) = Roster("spawn-ad-hoc", "implementor", "--kind", "codex", "--model", "gpt-5", "--team", team, "--orchestrator-pid", Self, "--dry-run");

        Assert.True(code == 0, stderr);
        var capped = Ah.Core.Text.MemberNames.Fit(team, "implementor");
        Assert.True(capped.Length <= Ah.Core.Text.MemberNames.MaxLength);
        Assert.NotEqual(team + "-implementor", capped);
        Assert.Contains(capped, stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(team + "-implementor", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAdHocSpawnNextToACappedMemberOfTheSameTeamWarnsOfNoMixedPrefix()
    {
        const string team = "alarms-team-long-name-abc";
        PutHerdrOnPath();
        var existing = "{\"role\":\"implementor\",\"name\":\"" + Ah.Core.Text.MemberNames.Fit(team, "implementor") + "\",\"route\":\"peer\",\"transport_id\":\"%1\",\"model\":\"sonnet\"}";
        WriteTeam(team, LiveTeamJson("terminal", existing));

        var (code, stdout, stderr) = Roster("spawn-ad-hoc", "implementor", "--kind", "codex", "--model", "gpt-5", "--team", team, "--orchestrator-pid", Self, "--dry-run");

        Assert.True(code == 0, stderr);
        Assert.Contains(Ah.Core.Text.MemberNames.Fit(team, "implementor", 2), stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("hold both prefixes", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void ARefusedUntrustedCodexSpawnNamesTheTrustNeedItself()
    {
        var codexHome = Path.Combine(_sandbox.Home, ".codex");
        Directory.CreateDirectory(codexHome);
        File.WriteAllText(Path.Combine(codexHome, "config.toml"), "[projects.\"/somewhere/else\"]\ntrust_level = \"trusted\"\n");

        var (code, stdout, stderr) = CodexDryRun();

        Assert.Equal(2, code);
        Assert.Contains("codex-trust", stdout + stderr, StringComparison.Ordinal);
        Assert.Contains("--yes", stdout + stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void DoctorWarnsOfAnUntrustedCodexMemberWithoutGrantingAndStaysQuietOnceTrusted()
    {
        WriteGlobalRosterMembers("""[{"role":"implementor","kind":"codex","route":"pane"}]""");
        var codexHome = Path.Combine(_sandbox.Home, ".codex");
        var config = Path.Combine(codexHome, "config.toml");
        Directory.CreateDirectory(codexHome);
        File.WriteAllText(config, "[projects.\"/somewhere/else\"]\ntrust_level = \"trusted\"\n");

        var (_, stdout, _) = Roster("doctor", "--fix");

        Assert.Contains("\"name\":\"codex-trust\"", stdout.Replace(" ", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Equal("[projects.\"/somewhere/else\"]\ntrust_level = \"trusted\"\n", File.ReadAllText(config));

        File.WriteAllText(config, $"[projects.\"{Project}\"]\ntrust_level = \"trusted\"\n");
        var (_, quiet, _) = Roster("doctor");

        Assert.DoesNotContain("codex-trust", quiet, StringComparison.Ordinal);
    }

    [Fact]
    public void ATrustedCodexDryRunCarriesNoTrustNeed()
    {
        var codexHome = Path.Combine(_sandbox.Home, ".codex");
        Directory.CreateDirectory(codexHome);
        File.WriteAllText(Path.Combine(codexHome, "config.toml"), $"[projects.\"{Project}\"]\ntrust_level = \"trusted\"\n");

        var (_, stdout, _) = CodexDryRun();

        Assert.DoesNotContain("\"needs\"", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void AClaudeDryRunCarriesNoTrustNeedEvenWithNoCodexConfig()
    {
        WriteTeam("proj", LiveTeamJson("terminal", string.Empty));

        var (_, stdout, _) = Roster("spawn-ad-hoc", "implementor", "--kind", "claude", "--team", "proj", "--orchestrator-pid", Self, "--dry-run");

        Assert.DoesNotContain("\"needs\"", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void ACodexMemberWithAnExplicitNonPaneRouteIsStillRefused()
    {
        WriteTeam("proj", LiveTeamJson("terminal", string.Empty));

        var (code, _, stderr) = Roster("spawn-ad-hoc", "implementor", "--kind", "codex", "--route", "peer", "--team", "proj", "--orchestrator-pid", Self, "--dry-run");

        Assert.Equal(2, code);
        Assert.Contains("route must be \"pane\" for kind \"codex\"", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void ATeamWithNoRecordOfAnImplementorStillGetsTheBareName()
    {
        WriteTeam("proj", LiveTeamJson("terminal", string.Empty));

        Assert.Equal("proj-implementor", NextImplementorName());
    }

    [Fact]
    public void BoxesThatAreNotThisRolesNumberedNamesDoNotRaiseTheSuffix()
    {
        WriteTeam("proj", LiveTeamJson("terminal", string.Empty));
        MakeBoxes("proj", "proj-reviewer-5", "proj-implementor-x", "proj-implementors-7", "orchestrator");
        File.WriteAllText(Path.Combine(BoxRootOf("proj"), "proj-implementor-9"), string.Empty);
        File.WriteAllText(
            Path.Combine(Hierarchy, "team-history.json"),
            "{\"version\":1,\"teams\":[{\"id\":\"h1\",\"fingerprint\":\"f1\",\"alias\":\"other\",\"members\":["
            + "{\"role\":\"implementor\"},{\"role\":\"implementor\"}]}]}");

        Assert.Equal("proj-implementor", NextImplementorName());
    }

    [Fact]
    public void AnImplementorWhoseWorkTreeIsTheMainCheckoutIsRefusedWhileTheTeamsGrantsAreInForce()
    {
        Directory.CreateDirectory(Path.Combine(Project, ".git"));
        WriteGrants("enforce");

        var (code, refused) = SpawnImplementor("--dry-run");

        Assert.Equal(2, code);
        Assert.Equal("main-checkout", refused["refused"].AsString());
        Assert.DoesNotContain(_runner.Calls, argv => argv.Any(a => a.Contains("claude", StringComparison.Ordinal)));
    }

    [Fact]
    public void TheMainCheckoutRefusalKeysOnTheRoleClassNotOnTheRoleName()
    {
        Directory.CreateDirectory(Path.Combine(Project, ".git"));
        WriteGrants("enforce");
        var builder = Roster("role", "set", "builder", "--class", "implement", "--routes", "builds", "--description", "Builds", "--level", "repo", "--scaffold", "repo");
        var checker = Roster("role", "set", "checker", "--class", "review", "--routes", "checks", "--description", "Checks", "--level", "repo", "--scaffold", "repo");
        Assert.True(builder.Code == 0 && checker.Code == 0, builder.Stderr + checker.Stderr);

        var (implementCode, implementOut, _) = Roster("spawn-ad-hoc", "builder", "--model", "sonnet", "--team", "proj", "--orchestrator-pid", Self, "--dry-run");
        var (reviewCode, _, reviewErr) = Roster("spawn-ad-hoc", "checker", "--model", "sonnet", "--team", "proj", "--orchestrator-pid", Self, "--dry-run");

        Assert.Equal(2, implementCode);
        Assert.Equal("main-checkout", JsonText.Parse(implementOut).AsObject()["refused"].AsString());
        Assert.True(reviewCode == 0, reviewErr);
    }

    [Fact]
    public void TheMainCheckoutRefusalLiftsForTheNearestInputsThatAreNotItsCase()
    {
        Directory.CreateDirectory(Path.Combine(Project, ".git"));
        WriteGrants("enforce");
        var worktree = MakeWorkTree("wt");

        Assert.Equal(0, SpawnImplementor("--dry-run", "--allow-main").Code);
        Assert.Equal(0, SpawnImplementor("--dry-run", "--cwd", worktree).Code);
        var (reviewerCode, _, _) = Roster("spawn-ad-hoc", "reviewer", "--model", "sonnet", "--team", "proj", "--orchestrator-pid", Self, "--dry-run");
        Assert.Equal(0, reviewerCode);
        WriteGrants("off");
        Assert.Equal(0, SpawnImplementor("--dry-run").Code);
        DeleteGrants(Hierarchy);
        Assert.Equal(0, SpawnImplementor("--dry-run").Code);
    }

    private void OnTmux()
    {
        _extraEnv["TMUX"] = "/tmp/tmux-1/default,1,0";
        _extraEnv["TMUX_PANE"] = "%0";
        _runner.Output = argv => argv.Contains("new-window") ? "%7\n" : string.Empty;
    }

    private bool WindowOpenedIn(string directory) =>
        _runner.Calls.Any(argv =>
        {
            var words = argv.ToList();
            var flag = words.IndexOf("-c");
            return words.Contains("new-window") && flag >= 0 && words[flag + 1].EndsWith(directory, StringComparison.Ordinal);
        });

    [Fact]
    public void AStreamMembersPaneIsOpenedInTheStreamsWorkTreeNotInTheOrchestratorsDirectory()
    {
        OnTmux();
        var worktree = MakeWorkTree("s1");
        WriteTeam("proj", TeamWithStream(worktree).Replace("\"transport\":\"terminal\"", "\"transport\":\"tmux\"", StringComparison.Ordinal));
        WriteGrants("enforce");

        var (code, spawned) = SpawnImplementor("--stream", "s1");

        Assert.Equal(0, code);
        Assert.True(spawned["spawned"].AsBool());
        Assert.True(WindowOpenedIn(Path.Combine(".claude", "worktrees", "s1")));
        Assert.False(WindowOpenedIn("proj"));
    }

    [Fact]
    public void AMemberOutsideAStreamStillGetsItsPaneInTheOrchestratorsDirectory()
    {
        OnTmux();
        WriteTeam("proj", TeamWithStream(MakeWorkTree("s1")).Replace("\"transport\":\"terminal\"", "\"transport\":\"tmux\"", StringComparison.Ordinal));
        WriteGrants("enforce");

        var (code, _) = SpawnImplementor("--allow-main");

        Assert.Equal(0, code);
        Assert.True(WindowOpenedIn("proj"));
        Assert.False(WindowOpenedIn(Path.Combine(".claude", "worktrees", "s1")));
    }

    [Fact]
    public void AStreamMemberLaunchesInTheStreamsWorkTreeWhateverTheTeamsGrantsAreAndAMemberOutsideAStreamDoesNot()
    {
        var worktree = MakeWorkTree("s1");
        WriteTeam("proj", TeamWithStream(worktree));
        WriteGrants("enforce");

        var (streamCode, streamed) = SpawnImplementor("--dry-run", "--stream", "s1");
        var (plainCode, plain) = SpawnImplementor("--dry-run");
        DeleteGrants(Hierarchy);
        var (bareCode, bare) = SpawnImplementor("--dry-run", "--stream", "s1");

        Assert.Equal(0, streamCode);
        Assert.Matches(@"WRANGL_EXPECTED_ROOT\\+"":\\+""[^""\\]*/\.claude/worktrees/s1\\+""", JsonText.Stringify(streamed["launch"], 0));
        Assert.Equal(0, plainCode);
        Assert.DoesNotMatch(@"/\.claude/worktrees/s1", JsonText.Stringify(plain["launch"], 0));
        Assert.Equal(0, bareCode);
        Assert.Matches(@"WRANGL_EXPECTED_ROOT\\+"":\\+""[^""\\]*/\.claude/worktrees/s1\\+""", JsonText.Stringify(bare["launch"], 0));
        Assert.False(HasGrants(Hierarchy));
    }

    [Fact]
    public void AStreamWithNoRecordedWorkTreeLaunchesInTheMainCwdWithOrWithoutGrants()
    {
        var worktree = MakeWorkTree("s1");
        WriteTeam("proj", TeamWithStream(worktree).Replace("\"worktree\":\"" + worktree + "\"", "\"worktree\":null", StringComparison.Ordinal));

        var (code, planned) = SpawnImplementor("--dry-run", "--stream", "s1");

        Assert.Equal(0, code);
        Assert.DoesNotMatch(@"/\.claude/worktrees/s1", JsonText.Stringify(planned["launch"], 0));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AStreamWhoseWorkTreeIsGoneRefusesBeforeAnythingLaunches(bool withGrants)
    {
        WriteTeam("proj", TeamWithStream(Path.Combine(Project, ".claude", "worktrees", "gone")));
        if (withGrants)
        {
            WriteGrants("enforce");
        }

        var (code, refused) = SpawnImplementor("--stream", "s1");

        Assert.Equal(2, code);
        Assert.Equal("stream-worktree-missing", refused["refused"].AsString());
        Assert.DoesNotContain(_runner.Calls, argv => argv.Any(a => a.Contains("claude", StringComparison.Ordinal)));
    }

    [Fact]
    public void ASpawnRecordsTheMemberWorkTreeInAGrantsDocumentThatExistsAndCreatesNoneWhenThereIsNone()
    {
        var worktree = MakeWorkTree("s1");
        WriteTeam("proj", TeamWithStream(worktree).Replace("\"transport\":\"terminal\"", "\"transport\":\"tmux\"", StringComparison.Ordinal));
        _extraEnv["TMUX"] = "/tmp/tmux-1/default,1,0";
        _extraEnv["TMUX_PANE"] = "%0";
        _runner.Output = argv => argv.Contains("new-window") ? "%7\n" : string.Empty;

        var (noDocCode, noDocOut, noDocErr) = Roster("spawn-ad-hoc", "implementor", "--model", "sonnet", "--team", "proj", "--orchestrator-pid", Self, "--stream", "s1");
        Assert.True(noDocCode == 0, noDocOut + noDocErr);
        var noDoc = JsonText.Parse(noDocOut).AsObject();

        Assert.True(noDoc["spawned"].AsBool());
        Assert.False(HasGrants(Hierarchy));

        WriteGrants("enforce");
        var (code, spawned) = SpawnImplementor("--stream", "s1");

        Assert.Equal(0, code);
        Assert.True(spawned["spawned"].AsBool());
        var name = spawned["member"].AsObject()["name"].AsString();
        var entry = GrantedMembers()[name].AsObject();
        Assert.EndsWith("/.claude/worktrees/s1", entry["work_root"].AsString(), StringComparison.Ordinal);
        Assert.Equal("s1", entry["stream"].AsString());
        Assert.Equal("implementor", entry["role"].AsString());
    }

    [Fact]
    public void CommittingANewTeamWritesTheGrantsDocumentInEnforceAndRecordsEachMembersWorkTree()
    {
        var verified = "[{\"role\":\"implementor\",\"name\":\"proj-implementor\",\"route\":\"peer\",\"expected_root\":\"" + Project + "\"}]";
        var args = new[] { "create", "--commit", "--verified", verified, "--transport", "terminal", "--roster-level", "repo", "--orchestrator-pid", Self };

        var (code, _, err) = Roster([.. args]);

        Assert.True(code == 0, err);
        Assert.Equal("enforce", JsonText.Parse(GrantsText(Hierarchy)!).Member("mode")!.Value.AsString());
        Assert.Contains("proj-implementor", GrantedMembers().Keys);
    }

    [Fact]
    public void CommittingATeamKeepsTheModeOfAGrantsDocumentThatAlreadyExists()
    {
        var verified = "[{\"role\":\"implementor\",\"name\":\"proj-implementor\",\"route\":\"peer\",\"expected_root\":\"" + Project + "\"}]";
        var args = new[] { "create", "--commit", "--verified", verified, "--transport", "terminal", "--roster-level", "repo", "--orchestrator-pid", Self };
        WriteGrants("log");

        var (code, _, err) = Roster([.. args]);

        Assert.True(code == 0, err);
        Assert.Equal("log", JsonText.Parse(GrantsText(Hierarchy)!).Member("mode")!.Value.AsString());
        Assert.Contains("proj-implementor", GrantedMembers().Keys);
    }

    [Fact]
    public void ASpawnWithCwdInAWorkTreeWritesTheRecordUnderTheTeamHomeAndNothingUnderTheWorkTreesOwnPool()
    {
        _extraEnv["WRANGL_HIERARCHY_DIR"] = string.Empty;
        Directory.CreateDirectory(Path.Combine(Project, ".git", "worktrees", "s2"));
        File.WriteAllText(Path.Combine(Project, ".git", "worktrees", "s2", "commondir"), "../..\n");
        var worktree = MakeWorkTree("s2");
        File.WriteAllText(Path.Combine(Project, ".git", "worktrees", "s2", "gitdir"), Path.Combine(worktree, ".git") + "\n");
        var home = Path.Combine(Project, ".claude", "ah2");
        Directory.CreateDirectory(Path.Combine(home, "teams"));
        Directory.CreateDirectory(Path.Combine(home, "grants"));
        _sandbox.PlantTeam(
            Path.Combine(home, "teams", "proj.json"),
            TeamWithStream(worktree).Replace("\"transport\":\"terminal\"", "\"transport\":\"tmux\"", StringComparison.Ordinal));
        TestGrants.Plant(_sandbox.AhHome, _sandbox.Home, home, "proj","""{"version":1,"team":"proj","mode":"enforce","audit_hash":null,"members":{}}""");
        _extraEnv["TMUX"] = "/tmp/tmux-1/default,1,0";
        _extraEnv["TMUX_PANE"] = "%0";
        _runner.Output = argv => argv.Contains("new-window") ? "%7\n" : string.Empty;

        var (code, stdout, stderr) = Roster(["spawn-ad-hoc", "implementor", "--model", "sonnet", "--team", "proj", "--orchestrator-pid", Self, "--cwd", worktree]);

        Assert.True(code == 0, stdout + stderr);
        var name = JsonText.Parse(stdout).AsObject()["member"].AsObject()["name"].AsString();
        var members = JsonText.Parse(GrantsText(home)!).Member("members")!.Value.AsObject();
        Assert.Contains(name, members.Keys);
        Assert.False(Directory.Exists(Path.Combine(worktree, ".claude", "ah2", "grants")));
    }

    [Fact]
    public void DismissingAMemberDropsItsRecordAndLeavesTheOthers()
    {
        WriteTeam("proj", LiveTeamJson("tmux", TwoPeers));
        WriteGrants(
            "enforce",
            $$$"""{"proj-implementor":{"work_root":"{{{Project}}}","stream":null,"role":"implementor","extra":[]},"proj-reviewer":{"work_root":"{{{Project}}}","stream":null,"role":"reviewer","extra":[]}}""");
        var token = TokenOf(Roster("dismiss", "proj-implementor", "--plan", "--team", "proj").Stdout);

        var (code, _, err) = Roster("dismiss", "proj-implementor", "--close", "--confirm", "--plan-token", token, "--team", "proj");

        Assert.True(code == 0, err);
        var left = GrantedMembers();
        Assert.DoesNotContain("proj-implementor", left.Keys);
        Assert.Contains("proj-reviewer", left.Keys);
    }

    [Fact]
    public void DisbandingATeamDropsTheRecordOfEveryMemberItPruned()
    {
        WriteTeam("proj", LiveTeamJson("tmux", TwoPeers));
        WriteGrants(
            "enforce",
            $$$"""{"proj-implementor":{"work_root":"{{{Project}}}","stream":null,"role":"implementor","extra":[]},"proj-reviewer":{"work_root":"{{{Project}}}","stream":null,"role":"reviewer","extra":[]}}""");
        var token = TokenOf(Roster("disband", "--team", "proj").Stdout);

        var (code, _, err) = Roster("disband", "--close", "--confirm", "--plan-token", token, "--team", "proj");

        Assert.True(code == 0, err);
        Assert.Empty(GrantedMembers().Keys);
    }

    // ---------------------------------------------------------------- resume into the recorded work tree

    private string FamilyWorkTree(string name)
    {
        var gitdir = Path.Combine(Project, ".git", "worktrees", name);
        Directory.CreateDirectory(gitdir);
        File.WriteAllText(Path.Combine(gitdir, "commondir"), "../..\n");
        var tree = MakeWorkTree(name);
        File.WriteAllText(Path.Combine(gitdir, "gitdir"), Path.Combine(tree, ".git") + "\n");
        return tree;
    }

    private string ResumeHome()
    {
        _callers ??= new RecordingCallers { StartText = SelfStart };
        _extraEnv["WRANGL_HIERARCHY_DIR"] = string.Empty;
        _extraEnv["TMUX"] = "/tmp/tmux-1/default,1,0";
        _extraEnv["TMUX_PANE"] = "%0";
        _runner.Output = argv => argv.Contains("new-window") ? "%7\n" : string.Empty;
        var home = Path.Combine(Project, ".claude", "ah2");
        Directory.CreateDirectory(Path.Combine(home, "teams"));
        Directory.CreateDirectory(Path.Combine(home, "grants"));
        Directory.CreateDirectory(Path.Combine(_sandbox.Root, "home", ".claude", "ah2", "run"));
        return home;
    }

    private static string MemberRow(string expectedRoot) =>
        $$"""{"role":"implementor","name":"proj-implementor","route":"peer","transport_id":"%1","model":"sonnet","expected_root":"{{expectedRoot}}"}""";

    private void WriteHomeGrants(string home, string mode, string members) =>
        TestGrants.Plant(_sandbox.AhHome, _sandbox.Home, home, "proj", $$"""{"version":1,"team":"proj","mode":"{{mode}}","audit_hash":null,"members":{{members}}}""");

    private string WorkRootOf(string home) =>
        JsonText.Parse(GrantsText(home)!).Member("members")!.Value.AsObject()["proj-implementor"].AsObject()["work_root"].AsString();

    private string TakeSnapshot(string home, string worktree, string mode, string? expectedRoot = null)
    {
        _sandbox.PlantTeam(Path.Combine(home, "teams", "proj.json"),LiveTeamJson("tmux", MemberRow(expectedRoot ?? Project)));
        WriteHomeGrants(
            home,
            mode,
            $$$"""{"proj-implementor":{"work_root":"{{{worktree}}}","stream":null,"role":"implementor","extra":[{"path":"{{{Project}}}/extra","note":"n","for":null,"outside":false,"at":"2026-10-15T00:00:00Z"}]}}""");
        var (code, stdout, stderr) = Roster("snapshot", "--team", "proj", "--cwd", Project);
        Assert.True(code == 0, stdout + stderr);
        return JsonText.Parse(stdout).Member("snapshot")!.Value.AsString();
    }

    private void DismissEverything(string home)
    {
        _sandbox.PlantTeam(Path.Combine(home, "teams", "proj.json"),LiveTeamJson("tmux", string.Empty));
        DeleteGrants(home);
        _runner.Calls.Clear();
    }

    private (int Code, JsonObject Row, string Err) ResumeYes(string snapshot)
    {
        var (code, stdout, stderr) = Roster("resume", snapshot, "--yes", "--team", "proj", "--orchestrator-pid", Self, "--cwd", Project);
        var rows = stdout.Length > 0 && JsonText.Parse(stdout).Member("members") is { IsArray: true } list ? list.AsArray() : null;
        return (code, rows is { Count: > 0 } ? rows[0].AsObject() : new JsonObject(), stdout + stderr);
    }

    [Fact]
    public void ASnapshotRecordsEachMembersWorkTreeAndTheGateModeAndAResumeLaunchesThereAndRestampsTheRecord()
    {
        var home = ResumeHome();
        var worktree = FamilyWorkTree("r1");
        var snapshot = TakeSnapshot(home, worktree, "log");
        Assert.Contains("\"gate_mode\"", SnapshotText(snapshot), StringComparison.Ordinal);
        Assert.Contains("\"work_root\"", SnapshotText(snapshot), StringComparison.Ordinal);
        DismissEverything(home);

        var (code, row, text) = ResumeYes(snapshot);

        Assert.True(code == 0, text);
        Assert.Equal("fresh", row["status"].AsString());
        Assert.Contains(_runner.Calls, argv => argv.Contains("new-window") && argv.Any(a => a.Contains(Path.Combine(".claude", "worktrees", "r1"), StringComparison.Ordinal)));
        Assert.EndsWith(Path.Combine(".claude", "worktrees", "r1"), WorkRootOf(home), StringComparison.Ordinal);
    }

    [Fact]
    public void AResumeRestoresNoExtrasAndRecreatesAMissingDocumentInTheRecordedMode()
    {
        var home = ResumeHome();
        var worktree = FamilyWorkTree("r2");
        var snapshot = TakeSnapshot(home, worktree, "log");
        DismissEverything(home);

        var (code, _, text) = ResumeYes(snapshot);

        Assert.True(code == 0, text);
        var written = JsonText.Parse(GrantsText(home)!);
        Assert.Equal("log", written.Member("mode")!.Value.AsString());
        Assert.Empty(written.Member("members")!.Value.AsObject()["proj-implementor"].AsObject()["extra"].AsArray());
    }

    [Fact]
    public void AResumeRefusesAMemberWhoseRecordedWorkTreeIsGoneAndLaunchesNothing()
    {
        var home = ResumeHome();
        var worktree = FamilyWorkTree("r3");
        var snapshot = TakeSnapshot(home, worktree, "enforce");
        Directory.Delete(worktree, recursive: true);
        DismissEverything(home);

        var (code, row, text) = ResumeYes(snapshot);

        Assert.True(code == 3, text);
        Assert.Equal("work-tree-missing", row["refused"].AsString());
        Assert.DoesNotContain(_runner.Calls, argv => argv.Contains("new-window"));
    }

    [Fact]
    public void AResumeOfASnapshotWithoutAWorkRootLaunchesInTheExpectedRoot()
    {
        var home = ResumeHome();
        var recorded = FamilyWorkTree("r4");
        var expected = FamilyWorkTree("r4b");
        var snapshot = TakeSnapshot(home, recorded, "enforce", expected);
        var stripped = SnapshotText(snapshot).Replace("\"work_root\"", "\"work_root_unused\"", StringComparison.Ordinal);
        PutSnapshot(snapshot, stripped);
        DismissEverything(home);

        var (code, row, text) = ResumeYes(snapshot);

        Assert.True(code == 0, text);
        Assert.Equal("fresh", row["status"].AsString());
        Assert.Contains(_runner.Calls, argv => argv.Contains("new-window") && argv.Any(a => a.Contains(Path.Combine(".claude", "worktrees", "r4b"), StringComparison.Ordinal)));
        Assert.DoesNotContain(_runner.Calls, argv => argv.Contains("new-window") && argv.Any(a => a.EndsWith(Path.Combine(".claude", "worktrees", "r4"), StringComparison.Ordinal)));
    }

    [Fact]
    public void ASnapshotOfATeamWithNoGrantsDocumentAddsNeitherAGateModeNorAWorkRoot()
    {
        var home = ResumeHome();
        Directory.CreateDirectory(Path.Combine(Project, ".git"));
        _sandbox.PlantTeam(Path.Combine(home, "teams", "proj.json"),LiveTeamJson("tmux", MemberRow(Project)));

        var (code, stdout, stderr) = Roster("snapshot", "--team", "proj", "--cwd", Project);

        Assert.True(code == 0, stdout + stderr);
        var text = SnapshotText(JsonText.Parse(stdout).Member("snapshot")!.Value.AsString());
        Assert.DoesNotContain("gate_mode", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"work_root\"", text, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- resume: where a member is launched

    private const string ResumeSession = "0b5f1c3e-1d2a-4b6c-8e7f-9a0b1c2d3e4f";

    private const string OtherSession = "7c1e9d20-3b4a-4f58-a6d1-2e3f4a5b6c7d";

    private const string HomeHint ="(no work tree recorded for proj-implementor; respawn with --stream <name> or resume with --allow-main)";

    /// <summary>Rewrites member 0 of a snapshot file: <paramref name="edit"/> may set or remove its fields.</summary>
    private string PatchSnapshot(string snapshot, Action<JsonObject> edit)
    {
        var doc = JsonText.Parse(SnapshotText(snapshot));
        edit(doc.Member("members")!.Value.AsArray()[0].AsObject());
        PutSnapshot(snapshot, JsonText.Stringify(doc, 2));
        return snapshot;
    }

    private Ah.Abstractions.ITeamAuthority SnapshotRows => Ah.StateDb.StateDbModule.OpenTeamAuthority(_sandbox.AhHome);

    private static string SnapshotHomeOf(string snapshot) => Path.GetDirectoryName(Path.GetDirectoryName(snapshot))!;

    /// <summary>The stored text of the snapshot at <paramref name="snapshot"/>: its row (files-off-disk §12.3), else its file.</summary>
    private string SnapshotText(string snapshot) => SnapshotRows.ReadSnapshot(SnapshotHomeOf(snapshot), Path.GetFileName(snapshot)) ?? File.ReadAllText(snapshot);

    /// <summary>Replaces the row of the snapshot at <paramref name="snapshot"/> with <paramref name="text"/>, pruning nothing.</summary>
    private void PutSnapshot(string snapshot, string text)
    {
        var name = Path.GetFileNameWithoutExtension(snapshot);
        var cut = name.LastIndexOf('-');
        Assert.True(SnapshotRows.WriteSnapshot(SnapshotHomeOf(snapshot), name[..cut], name[(cut + 1)..], text, null, null).Ok);
    }

    /// <summary>A snapshot as the SessionEnd writer used to leave it: no gate mode, and no work root, stream or expected root of the tree.</summary>
    private string AutoSnapshot(string home, string? sessionId, string role = "implementor")
    {
        Directory.CreateDirectory(Path.Combine(Project, ".git", "refs"));
        _sandbox.PlantTeam(Path.Combine(home, "teams", "proj.json"),LiveTeamJson("tmux", MemberRow(Project)));
        var (code, stdout, stderr) = Roster("snapshot", "--team", "proj", "--cwd", Project);
        Assert.True(code == 0, stdout + stderr);
        var doc = JsonText.Parse(stdout);
        return PatchSnapshot(
            doc.Member("snapshot")!.Value.AsString(),
            member =>
            {
                member.Remove("work_root");
                member["role"] = JsonValue.FromString(role);
                member["name"] = JsonValue.FromString("proj-" + role);
                if (sessionId is null)
                {
                    member.Remove("session_id");
                }
                else
                {
                    member["session_id"] = JsonValue.FromString(sessionId);
                }
            });
    }

    private void WritePeerRow(string pool, string sessionId, string cwd, DateTimeOffset at) =>
        WritePeerRow(pool, sessionId, cwd, at.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));

    private void WritePeerRow(string pool, string sessionId, string cwd, string ts)
    {
        Directory.CreateDirectory(pool);
        _sandbox.PlantPeers(
            pool,
            $"{{\"type\":\"peer\",\"status\":\"up\",\"session_id\":\"{sessionId}\",\"role\":\"implementor\",\"pid\":1,\"cwd\":\"{cwd}\",\"ts\":\"{ts}\"}}\n");
    }

    private bool LaunchedUnderAWorkTree(string name) =>
        _runner.Calls.Any(argv => argv.Contains("new-window") && argv.Any(a => a.Contains(Path.Combine(".claude", "worktrees", name), StringComparison.Ordinal)));

    private bool LaunchedInTheMainCheckout() =>
        _runner.Calls.Any(argv => argv.Contains("new-window"))
        && !_runner.Calls.Any(argv => argv.Contains("new-window") && argv.Any(a => a.Contains(Path.Combine(".claude", "worktrees"), StringComparison.Ordinal)));

    private static readonly DateTimeOffset Earlier = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset Later = new(2026, 10, 2, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AResumeNamesTheRecordedWorkRootAsWhereItLaunchedFrom()
    {
        var home = ResumeHome();
        var snapshot = TakeSnapshot(home, FamilyWorkTree("h1"), "log");
        DismissEverything(home);

        var (code, row, text) = ResumeYes(snapshot);

        Assert.True(code == 0, text);
        Assert.Equal("work_root", row["launch_from"].AsString());
    }

    [Fact]
    public void AnAutoSnapshotMemberWhoseStreamIsOpenLaunchesInTheStreamsWorkTree()
    {
        var home = ResumeHome();
        var tree = FamilyWorkTree("s1");
        var snapshot = PatchSnapshot(AutoSnapshot(home, null), member => member["stream"] = JsonValue.FromString("s1"));
        _sandbox.PlantTeam(
            Path.Combine(home, "teams", "proj.json"),
            TeamWithStream(tree).Replace("\"transport\":\"terminal\"", "\"transport\":\"tmux\"", StringComparison.Ordinal));
        _runner.Calls.Clear();

        var (code, row, text) = ResumeYes(snapshot);

        Assert.True(code == 0, text);
        Assert.Equal("stream", row["launch_from"].AsString());
        Assert.True(LaunchedUnderAWorkTree("s1"), text);
    }

    [Fact]
    public void AMemberWhoseStreamIsDoneFallsThroughToItsLastWorkTreeInsteadOfFailingStreamDone()
    {
        var home = ResumeHome();
        var stream = FamilyWorkTree("s1");
        var last = FamilyWorkTree("w3");
        var snapshot = PatchSnapshot(
            AutoSnapshot(home, ResumeSession),
            member => member["stream"] = JsonValue.FromString("s1"));
        _sandbox.PlantTeam(
            Path.Combine(home, "teams", "proj.json"),
            TeamWithStream(stream).Replace("\"transport\":\"terminal\"", "\"transport\":\"tmux\"", StringComparison.Ordinal).Replace("\"state\":\"open\"", "\"state\":\"done\"", StringComparison.Ordinal));
        WritePeerRow(Path.Combine(last, ".claude", "ah2"), ResumeSession, last, Later);
        _runner.Calls.Clear();

        var (code, row, text) = ResumeYes(snapshot);

        Assert.True(code == 0, text);
        Assert.True(row["status"].AsString() == "resumed", text);
        Assert.Equal("last_cwd", row["launch_from"].AsString());
        Assert.True(LaunchedUnderAWorkTree("w3"), text);
        Assert.False(LaunchedUnderAWorkTree("s1"), text);
    }

    [Fact]
    public void AMemberWhoseStreamIsNotInTheTeamFileFallsThroughToTheExpectedRootInsteadOfFailingUnknownStream()
    {
        var home = ResumeHome();
        var snapshot = PatchSnapshot(AutoSnapshot(home, null), member => member["stream"] = JsonValue.FromString("gone"));
        _runner.Calls.Clear();

        var (code, row, text) = ResumeYes(snapshot);

        Assert.True(code == 0, text);
        Assert.True(row["status"].AsString() == "fresh", text);
        Assert.Equal("expected_root", row["launch_from"].AsString());
        Assert.True(LaunchedInTheMainCheckout(), text);
    }

    [Fact]
    public void AnAutoSnapshotMemberLaunchesInTheWorkTreeOfItsNewestRosterRowFoundInALinkedWorkTreesPool()
    {
        var home = ResumeHome();
        var last = FamilyWorkTree("w3");
        var snapshot = AutoSnapshot(home, ResumeSession);
        WriteHomeGrants(home, "enforce", "{}");
        WritePeerRow(home, ResumeSession, Project, Earlier);
        WritePeerRow(Path.Combine(last, ".claude", "ah2"), ResumeSession, last, Later);
        _runner.Calls.Clear();

        var (code, row, text) = ResumeYes(snapshot);

        Assert.True(code == 0, text);
        Assert.Equal("resumed", row["status"].AsString());
        Assert.Equal("last_cwd", row["launch_from"].AsString());
        Assert.True(LaunchedUnderAWorkTree("w3"), text);
    }

    [Fact]
    public void AMemberWhoseNewestRosterRowIsTheMainCheckoutIsRefusedWithTheHintAndNothingLaunches()
    {
        var home = ResumeHome();
        var last = FamilyWorkTree("w3");
        Directory.CreateDirectory(Path.Combine(Project, ".git", "refs"));
        var snapshot = AutoSnapshot(home, ResumeSession);
        WriteHomeGrants(home, "enforce", "{}");
        WritePeerRow(Path.Combine(last, ".claude", "ah2"), ResumeSession, last, Earlier);
        WritePeerRow(home, ResumeSession, Project, Later);
        _runner.Calls.Clear();

        var (code, row, text) = ResumeYes(snapshot);

        Assert.True(code == 3, text);
        Assert.Equal("failed", row["status"].AsString());
        Assert.Contains(HomeHint, row["why"].AsString(), StringComparison.Ordinal);
        Assert.DoesNotContain(_runner.Calls, argv => argv.Contains("new-window"));
    }

    [Fact]
    public void ASessionEndShapedSnapshotOfAnImplementorWhoseLastRowIsAFamilyWorkTreeResumesThereNotRefused()
    {
        var home = ResumeHome();
        var last = FamilyWorkTree("w3");
        Directory.CreateDirectory(Path.Combine(Project, ".git", "refs"));
        var snapshot = AutoSnapshot(home, ResumeSession);
        WriteHomeGrants(home, "enforce", "{}");
        WritePeerRow(home, ResumeSession, Project, Earlier);
        WritePeerRow(Path.Combine(last, ".claude", "ah2"), ResumeSession, last, Later);
        _runner.Calls.Clear();

        var (code, row, text) = ResumeYes(snapshot);

        Assert.True(code == 0, text);
        Assert.False(row.Get("refused") is { IsTruthy: true }, text);
        Assert.True(LaunchedUnderAWorkTree("w3"), text);
    }

    [Fact]
    public void AnOpenStreamWhoseWorkTreeIsGoneFallsThroughToTheLastWorkTreeAndSpawnDoesNotTargetTheStream()
    {
        var home = ResumeHome();
        var gone = Path.Combine(Project, ".claude", "worktrees", "gone");
        var last = FamilyWorkTree("w3");
        var snapshot = PatchSnapshot(AutoSnapshot(home, ResumeSession), member => member["stream"] = JsonValue.FromString("s1"));
        _sandbox.PlantTeam(
            Path.Combine(home, "teams", "proj.json"),
            TeamWithStream(gone).Replace("\"transport\":\"terminal\"", "\"transport\":\"tmux\"", StringComparison.Ordinal));
        WriteHomeGrants(home, "enforce", "{}");
        WritePeerRow(Path.Combine(last, ".claude", "ah2"), ResumeSession, last, Later);
        _runner.Calls.Clear();

        var (code, row, text) = ResumeYes(snapshot);

        Assert.True(code == 0, text);
        Assert.True(row["status"].AsString() == "resumed", text);
        Assert.Equal("last_cwd", row["launch_from"].AsString());
        Assert.True(LaunchedUnderAWorkTree("w3"), text);
    }

    [Fact]
    public void ARowWhoseWorkTreeIsGoneFallsThroughToTheExpectedRoot()
    {
        var home = ResumeHome();
        var gone = Path.Combine(Project, ".claude", "worktrees", "gone");
        var snapshot = AutoSnapshot(home, ResumeSession);
        WritePeerRow(home, ResumeSession, gone, Later);
        _runner.Calls.Clear();

        var (code, row, text) = ResumeYes(snapshot);

        Assert.True(code == 0, text);
        Assert.Equal("expected_root", row["launch_from"].AsString());
        Assert.True(LaunchedInTheMainCheckout(), text);
    }

    [Fact]
    public void TheNewestRosterRowIsTheLatestInstantNotTheLatestStringAcrossAUtcOffsetChange()
    {
        var home = ResumeHome();
        var last = FamilyWorkTree("w3");
        var snapshot = AutoSnapshot(home, ResumeSession);
        WriteHomeGrants(home, "enforce", "{}");
        WritePeerRow(home, ResumeSession, Project, "2026-11-01T01:30:00.000-04:00");
        WritePeerRow(Path.Combine(last, ".claude", "ah2"), ResumeSession, last, "2026-11-01T01:10:00.000-05:00");
        _runner.Calls.Clear();

        var (code, row, text) = ResumeYes(snapshot);

        Assert.True(code == 0, text);
        Assert.Equal("last_cwd", row["launch_from"].AsString());
        Assert.True(LaunchedUnderAWorkTree("w3"), text);
    }

    [Fact]
    public void AFreshMemberWithNoSessionLaunchesInTheExpectedRootEvenWhenAWorkTreeHoldsAnotherSessionsRow()
    {
        var home = ResumeHome();
        var other = FamilyWorkTree("w3");
        var snapshot = AutoSnapshot(home, null);
        WritePeerRow(Path.Combine(other, ".claude", "ah2"), OtherSession, other, Later);
        _runner.Calls.Clear();

        var (code, row, text) = ResumeYes(snapshot);

        Assert.True(code == 0, text);
        Assert.Equal("fresh", row["status"].AsString());
        Assert.True(LaunchedInTheMainCheckout(), text);
    }

    [Fact]
    public void AReviewerWhoseOnlyRootIsTheMainCheckoutStillLaunchesThereWhileGrantsAreInForce()
    {
        var home = ResumeHome();
        Directory.CreateDirectory(Path.Combine(Project, ".git", "refs"));
        var snapshot = AutoSnapshot(home, null, "reviewer");
        WriteHomeGrants(home, "enforce", "{}");
        _runner.Calls.Clear();

        var (code, row, text) = ResumeYes(snapshot);

        Assert.True(code == 0, text);
        Assert.True(row["status"].AsString() == "fresh", text);
        Assert.True(LaunchedInTheMainCheckout(), text);
    }

    // ---------------------------------------------------------------- the member tmp

    private string SpawnTeamHome()
    {
        _extraEnv["WRANGL_HIERARCHY_DIR"] = string.Empty;
        _extraEnv["TMUX"] = "/tmp/tmux-1/default,1,0";
        _extraEnv["TMUX_PANE"] = "%0";
        _runner.Output = argv => argv.Contains("new-window") ? "%7\n" : string.Empty;
        Directory.CreateDirectory(Path.Combine(Project, ".git", "worktrees", "t1"));
        File.WriteAllText(Path.Combine(Project, ".git", "worktrees", "t1", "commondir"), "../..\n");
        var worktree = MakeWorkTree("t1");
        File.WriteAllText(Path.Combine(Project, ".git", "worktrees", "t1", "gitdir"), Path.Combine(worktree, ".git") + "\n");
        var home = Path.Combine(Project, ".claude", "ah2");
        Directory.CreateDirectory(Path.Combine(home, "teams"));
        Directory.CreateDirectory(Path.Combine(home, "grants"));
        _sandbox.PlantTeam(Path.Combine(home, "teams", "proj.json"), LiveTeamJson("tmux", string.Empty));
        return home;
    }

    private (int Code, JsonObject Out, string Err) SpawnInWorkTree(string worktree) =>
        SpawnWith(["--cwd", worktree]);

    /// <summary>Makes every recorded spawn old enough that a member without a registration is dead rather than still starting.</summary>
    private void AgeSpawnedRows()
    {
        foreach (var file in new[] { Hierarchy, Path.Combine(Project, ".claude", "ah2") }
            .SelectMany(dir => _sandbox.StoredTeamNames(dir).Select(name => Path.Combine(dir, "teams", name + ".json"))))
        {
            var text = _sandbox.TeamText(file);
            var aged = System.Text.RegularExpressions.Regex.Replace(text, "\"spawned_at\"\\s*:\\s*\"[^\"]*\"", "\"spawned_at\":\"2020-01-01T00:00:00.000Z\"");
            if (aged != text)
            {
                _sandbox.PlantTeam(file, aged);
            }
        }
    }

    private (int Code, JsonObject Out, string Err) SpawnWith(string[] extra)
    {
        AgeSpawnedRows();
        var (code, stdout, stderr) = Roster(["spawn-ad-hoc", "implementor", "--model", "sonnet", "--team", "proj", "--orchestrator-pid", Self, .. extra]);
        return (code, stdout.Length > 0 ? JsonText.Parse(stdout).AsObject() : new JsonObject(), stdout + stderr);
    }

    private static string AliasKey(string teamId, string member) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData([.. System.Text.Encoding.UTF8.GetBytes(teamId), 0, .. System.Text.Encoding.UTF8.GetBytes(member)]));

    private static string RealPathOf(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        return PlatformModule.CreateIo(null).Files.RealPath(path);
    }

    private JsonObject RecordedTmp(string home, string member) =>
        JsonText.Parse(GrantsText(home)!).Member("members")!.Value.AsObject()[member].AsObject()["tmp"].AsObject();

    private void WriteHomeDocument(string home, string members = "{}") =>
        TestGrants.Plant(_sandbox.AhHome, _sandbox.Home, home, "proj", $$"""{"version":1,"team":"proj","mode":"enforce","audit_hash":null,"members":{{members}}}""");

    [Fact]
    public void ASpawnMakesThePrivateTmpUnderTheTeamHomeAliasesItRecordsItAndExportsTmpdir()
    {
        var home = SpawnTeamHome();
        WriteHomeDocument(home);
        var worktree = MakeWorkTree("t1");

        var (code, spawned, err) = SpawnInWorkTree(worktree);

        Assert.True(code == 0, err);
        var name = spawned["member"].AsObject()["name"].AsString();
        var tmp = RecordedTmp(home, name);
        var dir = tmp["root"].AsString();
        Assert.Equal(Path.Combine(Project, ".claude", "ah2", "tmp", "proj", name), dir.Replace("/private/tmp", "/tmp", StringComparison.Ordinal).Replace(Path.GetFullPath(Project), Project, StringComparison.Ordinal));
        Assert.True(Directory.Exists(dir));
        Assert.False(Directory.Exists(Path.Combine(worktree, ".claude", "ah2", "tmp")));
        Assert.Equal("claude", tmp["kind"].AsString());
        var alias = tmp["alias"].AsString();
        Assert.Equal(Path.Combine(_sandbox.AhHome, "t", AliasKey("t-1", name)[..8]), alias);
        Assert.Equal(dir, new FileInfo(alias).LinkTarget);
        Assert.Contains(_runner.Calls, argv => argv.Any(a => a.Contains("TMPDIR", StringComparison.Ordinal) && a.Contains(alias, StringComparison.Ordinal)));
    }

    [Fact]
    public void ASpawnOutsideAGitRepositoryStillWorksRecordsNoneWithTheReasonWarnsAndSetsNoTmpdir()
    {
        var home = SpawnTeamHome();
        WriteHomeDocument(home);
        _runner.Exit = argv => argv.Contains("check-ignore") ? 1 : 0;

        var (code, spawned, err) = SpawnInWorkTree(MakeWorkTree("t1"));

        Assert.True(code == 0, err);
        var name = spawned["member"].AsObject()["name"].AsString();
        var row = JsonText.Parse(_sandbox.TeamText(Path.Combine(home, "teams", "proj.json"))).Member("members")!.Value.AsArray()
            .Single(m => m.Member("name")!.Value.AsString() == name);
        Assert.Equal("none (not a git repository)", row.Member("tmp")!.Value.AsString());
        Assert.Contains(spawned["warnings"].AsArray().Select(w => w.AsString()), w => w.Contains("not a git repository", StringComparison.Ordinal));
        Assert.False(Directory.Exists(Path.Combine(home, "tmp")));
        Assert.DoesNotContain(_runner.Calls, argv => argv.Any(a => a.Contains("TMPDIR", StringComparison.Ordinal)));
        Assert.DoesNotContain("\"tmp\"", GrantsText(home)!, StringComparison.Ordinal);
    }

    private string RealRepoWithWorkTree()
    {
        Directory.Delete(Path.Combine(Project, ".git"), recursive: true);
        RealGit("init", "-q");
        File.WriteAllText(Path.Combine(Project, "a.txt"), "a");
        RealGit("add", "a.txt");
        RealGit("commit", "-q", "-m", "one");
        var tree = Path.Combine(_sandbox.Root, "wtreal");
        RealGit("worktree", "add", "-q", "-b", "wt", tree);
        _runner.GitPassthrough = RealRunner();
        return tree;
    }

    private string Exclude() => File.ReadAllText(Path.Combine(Project, ".git", "info", "exclude"));

    [Fact]
    public void ASpawnWhereNothingIgnoresTheTmpRootAppendsOneExcludeLineSetsTmpdirAndNeverWritesGitignore()
    {
        var home = SpawnTeamHome();
        WriteHomeDocument(home);
        var tree = RealRepoWithWorkTree();

        var (code, spawned, err) = SpawnInWorkTree(tree);

        Assert.True(code == 0, err);
        Assert.Single(Exclude().Split('\n'), l => l == "/.claude/ah2/tmp/");
        Assert.False(File.Exists(Path.Combine(Project, ".gitignore")));
        Assert.True(Directory.Exists(RecordedTmp(home, spawned["member"].AsObject()["name"].AsString())["root"].AsString()));
        Assert.Contains(_runner.Calls, argv => argv.Any(a => a.Contains("TMPDIR", StringComparison.Ordinal)));
    }

    [Fact]
    public void ASecondSpawnAddsNoSecondExcludeLine()
    {
        var home = SpawnTeamHome();
        WriteHomeDocument(home);
        var tree = RealRepoWithWorkTree();

        var firstSpawn = SpawnInWorkTree(tree);
        Assert.True(firstSpawn.Code == 0, firstSpawn.Err);
        var secondSpawn = SpawnInWorkTree(tree);
        Assert.True(secondSpawn.Code == 0, secondSpawn.Err);

        Assert.Single(Exclude().Split('\n'), l => l == "/.claude/ah2/tmp/");
    }

    [Fact]
    public void ATmpRootAGitignoreAlreadyIgnoresGetsNoExcludeLine()
    {
        var home = SpawnTeamHome();
        WriteHomeDocument(home);
        var tree = RealRepoWithWorkTree();
        File.WriteAllText(Path.Combine(Project, ".gitignore"), ".claude/\n");
        var before = Exclude();

        var (code, _, err) = SpawnInWorkTree(tree);

        Assert.True(code == 0, err);
        Assert.Equal(before, Exclude());
    }

    [Fact]
    public void ASpawnWhoseTmpDirectoryCannotBeMadeIsRefusedWithTheCauseAndStartsNoMember()
    {
        var home = SpawnTeamHome();
        WriteHomeDocument(home);
        File.WriteAllText(Path.Combine(home, "tmp"), "in the way");

        var (code, _, err) = SpawnInWorkTree(MakeWorkTree("t1"));

        Assert.NotEqual(0, code);
        Assert.Contains("cannot create member tmp " + Path.Combine(home, "tmp", "proj"), err, StringComparison.Ordinal);
        Assert.DoesNotContain(_runner.Calls, argv => argv.Any(a => a.Contains("new-window", StringComparison.Ordinal)));
    }

    [Fact]
    public void TwoMembersGetTwoDistinctTmpdirs()
    {
        var home = SpawnTeamHome();
        WriteHomeDocument(home);
        var tree = MakeWorkTree("t1");

        var first = SpawnInWorkTree(tree).Out["member"].AsObject()["name"].AsString();
        var second = SpawnInWorkTree(tree).Out["member"].AsObject()["name"].AsString();

        Assert.NotEqual(first, second);
        Assert.NotEqual(RecordedTmp(home, first)["root"].AsString(), RecordedTmp(home, second)["root"].AsString());
        Assert.NotEqual(RecordedTmp(home, first)["alias"].AsString(), RecordedTmp(home, second)["alias"].AsString());
    }

    [Fact]
    public void ATeamWithNoGrantsDocumentStillGetsItsMemberATmpAndATmpdir()
    {
        var home = SpawnTeamHome();

        var (code, spawned, err) = SpawnInWorkTree(MakeWorkTree("t1"));

        Assert.True(code == 0, err);
        var name = spawned["member"].AsObject()["name"].AsString();
        Assert.True(Directory.Exists(Path.Combine(home, "tmp", "proj", name)));
        Assert.Contains(_runner.Calls, argv => argv.Any(a => a.Contains("TMPDIR", StringComparison.Ordinal)));
        Assert.False(HasGrants(home));
    }

    [Fact]
    public void AnAliasHeldByAnotherMembersDirectoryIsNotRepointedAndTheKeyIsLengthened()
    {
        var home = SpawnTeamHome();
        WriteHomeDocument(home);
        var other = Path.Combine(_sandbox.Root, "someone-elses-tmp");
        Directory.CreateDirectory(other);
        Directory.CreateDirectory(Path.Combine(_sandbox.AhHome, "t"));
        var key = AliasKey("t-1", "proj-implementor");
        File.CreateSymbolicLink(Path.Combine(_sandbox.AhHome, "t", key[..8]), other);

        var (code, spawned, err) = SpawnInWorkTree(MakeWorkTree("t1"));

        Assert.True(code == 0, err);
        Assert.Equal("proj-implementor", spawned["member"].AsObject()["name"].AsString());
        Assert.Equal(Path.Combine(_sandbox.AhHome, "t", key[..12]), RecordedTmp(home, "proj-implementor")["alias"].AsString());
        Assert.Equal(other, new FileInfo(Path.Combine(_sandbox.AhHome, "t", key[..8])).LinkTarget);
    }

    [Fact]
    public void ADismissedTmpOlderThan72HoursIsDeletedByTheNextSpawnAndALiveMembersTmpIsNot()
    {
        var home = SpawnTeamHome();
        WriteHomeDocument(home);
        var stale = Path.Combine(home, "tmp", "_dismissed", "gone-" + DateTimeOffset.UtcNow.AddHours(-73).ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture));
        var fresh = Path.Combine(home, "tmp", "_dismissed", "recent-" + DateTimeOffset.UtcNow.AddHours(-71).ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture));
        var live = Path.Combine(home, "tmp", "someone-live");
        foreach (var dir in new[] { stale, fresh, live })
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "f"), "x");
        }

        Directory.SetLastWriteTimeUtc(live, DateTime.UtcNow.AddDays(-30));

        var (code, _, err) = SpawnInWorkTree(MakeWorkTree("t1"));

        Assert.True(code == 0, err);
        Assert.False(Directory.Exists(stale));
        Assert.True(Directory.Exists(fresh));
        Assert.True(File.Exists(Path.Combine(live, "f")));
    }

    [Fact]
    public void DismissPlansTheTmpPathAndSizeAndTheCloseRenamesItIntoDismissedAndDropsTheAlias()
    {
        WriteTeam("proj", LiveTeamJson("tmux", TwoPeers));
        var tmpRoot = Path.Combine(Hierarchy, "tmp");
        Directory.CreateDirectory(Path.Combine(tmpRoot, "proj-implementor"));
        var dir = RealPathOf(Path.Combine(tmpRoot, "proj-implementor"));
        File.WriteAllText(Path.Combine(dir, "f"), "12345");
        Directory.CreateDirectory(Path.Combine(_sandbox.AhHome, "t"));
        var alias = Path.Combine(_sandbox.AhHome, "t", "abcd1234");
        File.CreateSymbolicLink(alias, dir);
        WriteGrants(
            "enforce",
            $$$$"""{"proj-implementor":{"work_root":"{{{{Project}}}}","stream":null,"role":"implementor","extra":[],"tmp":{"root":"{{{{dir}}}}","alias":"{{{{alias}}}}","kind":"claude"}}}""");

        var (planCode, planOut, planErr) = Roster("dismiss", "proj-implementor", "--plan", "--team", "proj");
        Assert.True(planCode == 0, planErr);
        var plan = JsonText.Parse(planOut).AsObject();
        Assert.Equal(dir, plan["tmp"].AsObject()["path"].AsString());
        Assert.Equal(5, (int)plan["tmp"].AsObject()["size_bytes"].AsNumber());
        Assert.Equal(5, (int)plan["tmp_bytes"].AsNumber());
        Assert.True(Directory.Exists(dir));

        var (code, _, err) = Roster("dismiss", "proj-implementor", "--close", "--confirm", "--plan-token", plan["close_token"].AsString(), "--team", "proj");

        Assert.True(code == 0, err);
        Assert.False(Directory.Exists(dir));
        Assert.False(Directory.Exists(Path.Combine(tmpRoot, "_dismissed"))); // reaped, not parked (worktree-cleanup §5a)
        Assert.False(File.Exists(alias) || Directory.Exists(alias));
    }

    [Fact]
    public void DisbandPlansTheBytesOfTheTmpOfEveryMemberItWouldClose()
    {
        WriteTeam("proj", LiveTeamJson("tmux", TwoPeers));
        var tmpRoot = Path.Combine(Hierarchy, "tmp");
        Directory.CreateDirectory(Path.Combine(tmpRoot, "proj-implementor"));
        var dir = RealPathOf(Path.Combine(tmpRoot, "proj-implementor"));
        File.WriteAllText(Path.Combine(dir, "f"), "12345");
        WriteGrants(
            "enforce",
            $$$$"""{"proj-implementor":{"work_root":"{{{{Project}}}}","stream":null,"role":"implementor","extra":[],"tmp":{"root":"{{{{dir}}}}","alias":"","kind":"claude"}}}""");

        var (code, stdout, err) = Roster("disband", "--team", "proj");

        Assert.True(code == 0, err);
        Assert.Equal(5, (int)JsonText.Parse(stdout).AsObject()["tmp_bytes"].AsNumber());
    }

    [Fact]
    public void DoctorBriefPrintsOneTextLinePerRowWithDetailsOnlyOnRowsThatAreNotOk()
    {
        var (fullCode, fullOut, _) = Roster("doctor");
        var (code, stdout, err) = Roster("doctor", "--brief");

        Assert.True(fullCode == 0 && code == 0, err);
        var rows = JsonText.Parse(fullOut).Member("rows")!.Value.AsArray();
        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(rows.Count, lines.Length);
        Assert.DoesNotContain("{", stdout, StringComparison.Ordinal);
        foreach (var row in rows)
        {
            var name = row.Member("name")!.Value.AsString();
            var status = row.Member("status")!.Value.AsString();
            var line = Assert.Single(lines, l => l.StartsWith($"{status} {name}", StringComparison.Ordinal));
            Assert.Equal(status == "ok", line == $"ok {name}");
            if (status != "ok")
            {
                Assert.Equal($"{status} {name}: {row.Member("detail")!.Value.AsString()}", line);
            }
        }
    }

    [Fact]
    public void DoctorBriefWithFixIsRefused()
    {
        var (code, _, err) = Roster("doctor", "--brief", "--fix");

        Assert.NotEqual(0, code);
        Assert.Contains("--brief cannot be combined with --fix", err, StringComparison.Ordinal);
    }

    [Fact]
    public void ScratchSweepRefusesAnUnknownFlag()
    {
        var (code, _, err) = Roster("scratch-sweep", "--dry-run");

        Assert.NotEqual(0, code);
        Assert.Contains("unrecognized flag --dry-run", err, StringComparison.Ordinal);
    }

    [Fact]
    public void DismissLeavesAnAliasThatNowPointsElsewhereAndAnotherMembersTmpAlone()
    {
        WriteTeam("proj", LiveTeamJson("tmux", TwoPeers));
        var tmpRoot = Path.Combine(Hierarchy, "tmp");
        Directory.CreateDirectory(Path.Combine(tmpRoot, "proj-implementor"));
        Directory.CreateDirectory(Path.Combine(tmpRoot, "proj-reviewer"));
        var dir = RealPathOf(Path.Combine(tmpRoot, "proj-implementor"));
        var reviewer = RealPathOf(Path.Combine(tmpRoot, "proj-reviewer"));
        Directory.CreateDirectory(Path.Combine(_sandbox.AhHome, "t"));
        var alias = Path.Combine(_sandbox.AhHome, "t", "abcd1234");
        File.CreateSymbolicLink(alias, reviewer);
        WriteGrants(
            "enforce",
            $$$$"""{"proj-implementor":{"work_root":"{{{{Project}}}}","stream":null,"role":"implementor","extra":[],"tmp":{"root":"{{{{dir}}}}","alias":"{{{{alias}}}}","kind":"claude"}}}""");
        var token = TokenOf(Roster("dismiss", "proj-implementor", "--plan", "--team", "proj").Stdout);

        var (code, _, err) = Roster("dismiss", "proj-implementor", "--close", "--confirm", "--plan-token", token, "--team", "proj");

        Assert.True(code == 0, err);
        Assert.Equal(reviewer, new FileInfo(alias).LinkTarget);
        Assert.True(Directory.Exists(reviewer));
    }

    [Fact]
    public void ADismissalRemovesATmpOfTheTeamComponentFormAndParksNothing()
    {
        WriteTeam("proj", LiveTeamJson("tmux", TwoPeers));
        var teamTmp = Path.Combine(Hierarchy, "tmp", "proj");
        Directory.CreateDirectory(Path.Combine(teamTmp, "proj-implementor"));
        var dir = RealPathOf(Path.Combine(teamTmp, "proj-implementor"));
        File.WriteAllText(Path.Combine(dir, "f"), "x");
        WriteGrants(
            "enforce",
            $$$$"""{"proj-implementor":{"work_root":"{{{{Project}}}}","stream":null,"role":"implementor","extra":[],"tmp":{"root":"{{{{dir}}}}","alias":"","kind":"claude"}}}""");
        var token = TokenOf(Roster("dismiss", "proj-implementor", "--plan", "--team", "proj").Stdout);

        var (code, _, err) = Roster("dismiss", "proj-implementor", "--close", "--confirm", "--plan-token", token, "--team", "proj");

        Assert.True(code == 0, err);
        Assert.False(Directory.Exists(dir));
        Assert.False(Directory.Exists(Path.Combine(teamTmp, "_dismissed")));
        Assert.False(Directory.Exists(Path.Combine(Hierarchy, "tmp", "_dismissed")));
    }

    [Fact]
    public void ADismissalWithNoGrantsRowRemovesTheMembersTmpAtItsSpawnPathAndLeavesAnotherMembersTmp()
    {
        WriteTeam("proj", LiveTeamJson("tmux", TwoPeers));
        var teamTmp = Path.Combine(Hierarchy, "tmp", "proj");
        Directory.CreateDirectory(Path.Combine(teamTmp, "proj-implementor"));
        Directory.CreateDirectory(Path.Combine(teamTmp, "proj-reviewer"));
        var dir = RealPathOf(Path.Combine(teamTmp, "proj-implementor"));
        var reviewer = RealPathOf(Path.Combine(teamTmp, "proj-reviewer"));
        File.WriteAllText(Path.Combine(dir, "f"), "x");
        var token = TokenOf(Roster("dismiss", "proj-implementor", "--plan", "--team", "proj").Stdout);

        var (code, _, err) = Roster("dismiss", "proj-implementor", "--close", "--confirm", "--plan-token", token, "--team", "proj");

        Assert.True(code == 0, err);
        Assert.False(Directory.Exists(dir));
        Assert.True(Directory.Exists(reviewer));
    }

    [Fact]
    public void DismissingATeamsMemberLeavesAnotherTeamsTmpOfTheSameNameIntactAndWritable()
    {
        var home = SpawnTeamHome();
        WriteHomeDocument(home);
        var otherTeamsTmp = Path.Combine(home, "tmp", "proj-implementor");
        Directory.CreateDirectory(otherTeamsTmp);
        File.WriteAllText(Path.Combine(otherTeamsTmp, "kept"), "x");
        var (spawnCode, spawned, spawnErr) = SpawnInWorkTree(MakeWorkTree("t1"));
        Assert.True(spawnCode == 0, spawnErr);
        var name = spawned["member"].AsObject()["name"].AsString();
        Assert.Equal("proj-implementor", name);
        var mine = RecordedTmp(home, name)["root"].AsString();
        Assert.NotEqual(RealPathOf(otherTeamsTmp), mine);
        AgeSpawnedRows();
        AppendGoneRow(name, "proj", Path.Combine(Project, ".claude", "ah2"));
        var token = TokenOf(Roster("dismiss", name, "--plan", "--team", "proj", "--cwd", Project).Stdout);

        var (code, _, err) = Roster("dismiss", name, "--close", "--confirm", "--plan-token", token, "--team", "proj", "--cwd", Project);

        Assert.True(code == 0, err);
        Assert.False(Directory.Exists(mine));
        Assert.True(File.Exists(Path.Combine(otherTeamsTmp, "kept")));
        File.WriteAllText(Path.Combine(otherTeamsTmp, "still-writable"), "y");
        Assert.True(File.Exists(Path.Combine(otherTeamsTmp, "still-writable")));
    }

    [Fact]
    public void ADismissedTmpOlderThanAWeekUnderTheTeamDirectoryIsDeletedByTheNextSpawn()
    {
        var home = SpawnTeamHome();
        WriteHomeDocument(home);
        var dismissed = Path.Combine(home, "tmp", "proj", "_dismissed");
        var stale = Path.Combine(dismissed, "gone-" + DateTimeOffset.UtcNow.AddDays(-9).ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture));
        var fresh = Path.Combine(dismissed, "recent-" + DateTimeOffset.UtcNow.AddDays(-1).ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture));
        foreach (var dir in new[] { stale, fresh })
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "f"), "x");
        }

        var (code, _, err) = SpawnInWorkTree(MakeWorkTree("t1"));

        Assert.True(code == 0, err);
        Assert.False(Directory.Exists(stale));
        Assert.True(File.Exists(Path.Combine(fresh, "f")));
    }

    [Fact]
    public void WhenAllThreeAliasNamesAreHeldByOtherMembersTheRealTmpIsExportedWithNoAliasAndNoAliasIsRepointed()
    {
        var home = SpawnTeamHome();
        WriteHomeDocument(home);
        Directory.CreateDirectory(Path.Combine(_sandbox.AhHome, "t"));
        var keys = AliasKey("t-1", "proj-implementor");
        var held = new List<(string Alias, string Target)>();
        foreach (var key in new[] { keys[..8], keys[..12], keys[..16] })
        {
            var target = Directory.CreateDirectory(Path.Combine(_sandbox.Root, "held-" + key)).FullName;
            var alias = Path.Combine(_sandbox.AhHome, "t", key);
            File.CreateSymbolicLink(alias, target);
            held.Add((alias, target));
        }

        var (code, spawned, err) = SpawnInWorkTree(MakeWorkTree("t1"));

        Assert.True(code == 0, err);
        var name = spawned["member"].AsObject()["name"].AsString();
        var recorded = RecordedTmp(home, name);
        Assert.Equal(string.Empty, recorded["alias"].AsString());
        Assert.Contains(spawned["warnings"].AsArray().Select(w => w.AsString()), w => w.Contains("no alias could be made", StringComparison.Ordinal));
        Assert.Contains(_runner.Calls, argv => argv.Any(a => a.Contains("TMPDIR", StringComparison.Ordinal) && a.Contains(recorded["root"].AsString(), StringComparison.Ordinal)));
        Assert.All(held, h => Assert.Equal(h.Target, new FileInfo(h.Alias).LinkTarget));
    }

    private string StreamTeamJson(string members, string? worktree) =>
        worktree is null
            ? LiveTeamJson("tmux", members)
            : LiveTeamJson("tmux", members).TrimEnd('}')
                + ",\"streams\":{\"s1\":{\"state\":\"open\",\"branch\":\"feat/s1\",\"base\":\"main\",\"worktree\":\"" + worktree
                + "\",\"tab_id\":null,\"opened\":\"2026-10-11T00:00:00.000Z\"}}}";

    private string TakeStreamSnapshot(string home, string worktree, bool recordStream = true)
    {
        var row = $$"""{"role":"implementor","name":"proj-implementor","route":"peer","transport_id":"%1","model":"sonnet","expected_root":"{{worktree}}","stream":"s1"}""";
        _sandbox.PlantTeam(Path.Combine(home, "teams", "proj.json"),StreamTeamJson(row, recordStream ? worktree : null));
        WriteHomeGrants(home, "enforce", $$$"""{"proj-implementor":{"work_root":"{{{worktree}}}","stream":"s1","role":"implementor","extra":[]}}""");
        var (code, stdout, stderr) = Roster("snapshot", "--team", "proj", "--cwd", Project);
        Assert.True(code == 0, stdout + stderr);
        return JsonText.Parse(stdout).Member("snapshot")!.Value.AsString();
    }

    [Fact]
    public void AResumedStreamMemberRejoinsItsStreamAndTheRecordCarriesIt()
    {
        var home = ResumeHome();
        var worktree = FamilyWorkTree("rs1");
        var snapshot = TakeStreamSnapshot(home, worktree);
        _sandbox.PlantTeam(Path.Combine(home, "teams", "proj.json"),StreamTeamJson(string.Empty, worktree));
        DeleteGrants(home);
        _runner.Calls.Clear();

        var (code, row, text) = ResumeYes(snapshot);

        Assert.True(code == 0, text);
        Assert.Equal("fresh", row["status"].AsString());
        var record = JsonText.Parse(GrantsText(home)!).Member("members")!.Value.AsObject()["proj-implementor"].AsObject();
        Assert.Equal("s1", record["stream"].AsString());
        var team = JsonText.Parse(_sandbox.TeamText(Path.Combine(home, "teams", "proj.json")));
        Assert.Equal("s1", team.Member("members")!.Value.AsArray()[0].AsObject()["stream"].AsString());
    }

    [Fact]
    public void AResumedStreamMemberWhoseStreamWasRemovedWithTheTeamFileGetsTheStreamBackFromTheSnapshot()
    {
        var home = ResumeHome();
        var worktree = FamilyWorkTree("rs3");
        var snapshot = TakeStreamSnapshot(home, worktree);
        Assert.Equal("feat/s1", JsonText.Parse(SnapshotText(snapshot)).Member("streams")!.Value.Member("s1")!.Value.Member("branch")!.Value.AsString());
        _sandbox.PlantTeam(Path.Combine(home, "teams", "proj.json"),StreamTeamJson(string.Empty, null));
        DeleteGrants(home);
        _runner.Calls.Clear();

        var (code, row, text) = ResumeYes(snapshot);

        Assert.True(code == 0, text);
        Assert.Equal("fresh", row["status"].AsString());
        var stream = JsonText.Parse(_sandbox.TeamText(Path.Combine(home, "teams", "proj.json"))).Member("streams")!.Value.Member("s1")!.Value;
        Assert.Equal("open", stream.Member("state")!.Value.AsString());
        Assert.Equal("feat/s1", stream.Member("branch")!.Value.AsString());
    }

    [Fact]
    public void AResumedStreamMemberWhoseStreamIsGoneIsRefusedWithTheStreamsNameAndLaunchesNothing()
    {
        var home = ResumeHome();
        var worktree = FamilyWorkTree("rs2");
        var snapshot = TakeStreamSnapshot(home, worktree, recordStream: false);
        _sandbox.PlantTeam(Path.Combine(home, "teams", "proj.json"),StreamTeamJson(string.Empty, null));
        DeleteGrants(home);
        _runner.Calls.Clear();

        var (code, row, text) = ResumeYes(snapshot);

        Assert.True(code == 3, text);
        Assert.Equal("failed", row["status"].AsString());
        Assert.Contains("no stream named \"s1\"", row["why"].AsString(), StringComparison.Ordinal);
        Assert.DoesNotContain(_runner.Calls, argv => argv.Contains("new-window"));
    }

    /// <summary>State calls whose grants <c>gate</c> write fails, as a store that cannot be written fails it.</summary>
    public class FailingGrantsGate : System.Reflection.DispatchProxy
    {
        internal Ah.Hierarchy.StateCalls.IHierarchyStateCalls Inner { get; set; } = null!;

        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(Ah.Hierarchy.StateCalls.IHierarchyStateCalls.Grants) && args is ["gate", ..])
            {
                throw new IOException("the grants store cannot be written");
            }

            try
            {
                return targetMethod.Invoke(Inner, args);
            }
            catch (System.Reflection.TargetInvocationException e) when (e.InnerException is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();
                throw;
            }
        }
    }

    [Fact]
    public void AFailedRestoreOfTheDocumentWarnsAndLeavesAHookErrorsRow()
    {
        var home = ResumeHome();
        var worktree = FamilyWorkTree("rh1");
        var snapshot = TakeSnapshot(home, worktree, "log");
        DismissEverything(home);
        // the document is gone and the restore's write fails (S11e: it goes to the store, so it is failed at the state call)
        DeleteGrants(home);
        _wrapStateCalls = inner =>
        {
            var proxy = System.Reflection.DispatchProxy.Create<Ah.Hierarchy.StateCalls.IHierarchyStateCalls, FailingGrantsGate>();
            ((FailingGrantsGate)(object)proxy).Inner = inner;
            return proxy;
        };

        var (code, stdout, stderr) = Roster("resume", snapshot, "--yes", "--team", "proj", "--orchestrator-pid", Self, "--cwd", Project);

        Assert.True(code == 0, stdout + stderr);
        Assert.Contains("the team's grants document was not restored", stderr, StringComparison.Ordinal);
        Assert.Contains(_hookErrors.Rows, r => r.StartsWith("roster-resume: the team's grants document was not restored", StringComparison.Ordinal));
    }

    [Fact]
    public void ASuccessfulResumeLeavesNoHookErrorsRow()
    {
        var home = ResumeHome();
        var snapshot = TakeSnapshot(home, FamilyWorkTree("rh2"), "log");
        DismissEverything(home);

        var (code, _, text) = ResumeYes(snapshot);

        Assert.True(code == 0, text);
        Assert.Empty(_hookErrors.Rows);
    }

    // ---------------------------------------------------------------- resume: which process becomes the Orchestrator

    private (string Home, string Snapshot) ResumeReady(string name)
    {
        var home = ResumeHome();
        var snapshot = TakeSnapshot(home, FamilyWorkTree(name), "log");
        DismissEverything(home);
        return (home, snapshot);
    }

    private JsonObject RecordedOrchestrator(string home) =>
        JsonText.Parse(_sandbox.TeamText(Path.Combine(home, "teams", "proj.json"))).Member("orchestrator")!.Value.AsObject();

    private (int Code, string Stdout, string Stderr) ResumeAs(string snapshot, params string[] extra) =>
        Roster(["resume", snapshot, "--yes", "--team", "proj", "--cwd", Project, .. extra]);

    [Fact]
    public void AResumeRecordsTheProvedProcessWithItsStartAndSaysTheSessionIsUnproven()
    {
        var (home, snapshot) = ResumeReady("op1");
        ObserverSeesAParent();
        var teamFile = Path.Combine(home, "teams", "proj.json");
        var unstarted = JsonText.Parse(_sandbox.TeamText(teamFile)).AsObject();
        unstarted["orchestrator"].AsObject()["pid_start"] = GoneStart;
        unstarted["orchestrator"].AsObject()["pid"] = 2147483000;
        _sandbox.PlantTeam(teamFile, JsonText.Stringify(unstarted, 0));
        Assert.Equal(GoneStart, RecordedOrchestrator(home)["pid_start"].AsString());

        var (code, stdout, stderr) = ResumeAs(snapshot);

        Assert.True(code == 0, stdout + stderr);
        var record = RecordedOrchestrator(home);
        Assert.Equal(Environment.ProcessId, (int)record["pid"].AsNumber());
        Assert.Equal(SelfStart, record["pid_start"].AsString());
        Assert.True(record["session_id"].IsNull);
        Assert.Equal("unproven", JsonText.Parse(stdout).Member("session")!.Value.AsString());
        Assert.Empty(_callers!.Rebinds);
    }

    [Fact]
    public void AProcessTiedSessionIsRecordedAndTheTeamsAreRebound()
    {
        _callers = new RecordingCallers { StartText = SelfStart, Started = DateTimeOffset.UtcNow.AddHours(-1) };
        _callers.Bound.Add(new BoundSession("sess-1", DateTimeOffset.UtcNow));
        var (home, snapshot) = ResumeReady("op2");

        var (code, stdout, stderr) = ResumeAs(snapshot);

        Assert.True(code == 0, stdout + stderr);
        Assert.Equal("sess-1", RecordedOrchestrator(home)["session_id"].AsString());
        Assert.NotEmpty(_callers.Rebinds);
        Assert.All(_callers.Rebinds, r => Assert.Equal(("sess-1", Environment.ProcessId), (r.Session, r.Pid)));
        Assert.Null(JsonText.Parse(stdout).Member("session"));
    }

    [Fact]
    public void ASessionIdTheEnvironmentMerelyStatesIsNotRecordedNorUsedToRebind()
    {
        _extraEnv["CLAUDE_CODE_SESSION_ID"] = "stated-only";
        var (home, snapshot) = ResumeReady("op3");

        var (code, stdout, stderr) = ResumeAs(snapshot);

        Assert.True(code == 0, stdout + stderr);
        Assert.True(RecordedOrchestrator(home)["session_id"].IsNull);
        Assert.Empty(_callers!.Rebinds);
    }

    [Theory]
    [InlineData("self")]
    [InlineData("other")]
    public void AMemberSessionCannotResumeWhateverPidItClaimsAndNothingIsWritten(string claim)
    {
        _nearest = new NearestSession(NearestKind.Member, "proj", "proj-implementor", Environment.ProcessId);
        var (home, snapshot) = ResumeReady("op4" + claim);
        _extraEnv["CLAUDE_PID"] = claim == "self" ? Self : LivePidOfAnotherProcess().ToString(CultureInfo.InvariantCulture);
        var before = _sandbox.TeamText(Path.Combine(home, "teams", "proj.json"));

        var (code, stdout, stderr) = ResumeAs(snapshot, "--orchestrator-pid", Self);

        Assert.Equal(2, code);
        Assert.Equal("member-cannot-resume", JsonText.Parse(stdout).Member("refused")!.Value.AsString());
        Assert.Equal("member-ancestor", JsonText.Parse(stdout).Member("proof")!.Value.AsString());
        Assert.Contains("owner proof: this command runs under member proj-implementor.", stderr, StringComparison.Ordinal);
        Assert.Equal(before, _sandbox.TeamText(Path.Combine(home, "teams", "proj.json")));
        Assert.DoesNotContain(_runner.Calls, argv => argv.Contains("new-window"));
    }

    [Fact]
    public void AMarkedSessionIsRefusedBeforeTheWalkEvenWhenNoRegistrationNamesItAsAMember()
    {
        _extraEnv[MeshModule.MemberMarkerEnv] = "proj-implementor";
        var (home, snapshot) = ResumeReady("op12");
        var before = _sandbox.TeamText(Path.Combine(home, "teams", "proj.json"));

        var (code, stdout, stderr) = ResumeAs(snapshot);

        Assert.Equal(2, code);
        Assert.Equal("member-cannot-resume", JsonText.Parse(stdout).Member("refused")!.Value.AsString());
        Assert.Equal("member-marker", JsonText.Parse(stdout).Member("proof")!.Value.AsString());
        Assert.Contains("owner proof: this shell carries a member marker.", stderr, StringComparison.Ordinal);
        Assert.Equal(0, _nearestAsked);
        Assert.Equal(before, _sandbox.TeamText(Path.Combine(home, "teams", "proj.json")));
        Assert.DoesNotContain(_runner.Calls, argv => argv.Contains("new-window"));
    }

    [Fact]
    public void AnEmptyMemberMarkerIsNotAMarkerAndTheWalkRuns()
    {
        _extraEnv[MeshModule.MemberMarkerEnv] = string.Empty;
        var (_, snapshot) = ResumeReady("op13");

        var (code, stdout, stderr) = ResumeAs(snapshot);

        Assert.True(code == 0, stdout + stderr);
        Assert.Equal(1, _nearestAsked);
    }

    [Theory]
    [InlineData("env")]
    [InlineData("flag")]
    public void AStatedPidThatIsNotTheProvedProcessIsRefusedAndNothingIsWritten(string how)
    {
        var (home, snapshot) = ResumeReady("op5" + how);
        var other = LivePidOfAnotherProcess().ToString(CultureInfo.InvariantCulture);
        if (how == "env")
        {
            _extraEnv["CLAUDE_PID"] = other;
        }

        var before = _sandbox.TeamText(Path.Combine(home, "teams", "proj.json"));

        var (code, stdout, stderr) = how == "flag" ? ResumeAs(snapshot, "--orchestrator-pid", other) : ResumeAs(snapshot);

        Assert.Equal(2, code);
        Assert.Equal("orchestrator-pid-mismatch", JsonText.Parse(stdout).Member("refused")!.Value.AsString());
        Assert.Contains(other, stderr, StringComparison.Ordinal);
        Assert.Contains(Self, stderr, StringComparison.Ordinal);
        Assert.Equal(before, _sandbox.TeamText(Path.Combine(home, "teams", "proj.json")));
        Assert.DoesNotContain(_runner.Calls, argv => argv.Contains("new-window"));
    }

    [Fact]
    public void AStatedPidThatEqualsTheProvedProcessIsAllowedFromBothSources()
    {
        _extraEnv["CLAUDE_PID"] = Self;
        var (home, snapshot) = ResumeReady("op6");

        var (code, stdout, stderr) = ResumeAs(snapshot, "--orchestrator-pid", Self);

        Assert.True(code == 0, stdout + stderr);
        Assert.Equal(SelfStart, RecordedOrchestrator(home)["pid_start"].AsString());
    }

    [Theory]
    [InlineData(NearestKind.None, "no-orchestrator", "owner proof: no registered session is an ancestor of this command.")]
    [InlineData(NearestKind.Unavailable, "unproven", "owner proof: the process table could not be read.")]
    public void NoProvedSessionAboveTheCommandIsUnprovenAndNothingIsLaunchedOrWritten(NearestKind kind, string proof, string line)
    {
        _nearest = new NearestSession(kind);
        var (home, snapshot) = ResumeReady("op7" + kind);
        _extraEnv["CLAUDE_PID"] = Self;
        var before = _sandbox.TeamText(Path.Combine(home, "teams", "proj.json"));

        var (code, stdout, stderr) = ResumeAs(snapshot, "--orchestrator-pid", Self);

        Assert.Equal(2, code);
        Assert.Equal("owner-unproven", JsonText.Parse(stdout).Member("refused")!.Value.AsString());
        Assert.Equal(proof, JsonText.Parse(stdout).Member("proof")!.Value.AsString());
        Assert.Contains(line, stderr, StringComparison.Ordinal);
        Assert.Contains("plain terminal", stderr, StringComparison.Ordinal);
        Assert.Equal(before, _sandbox.TeamText(Path.Combine(home, "teams", "proj.json")));
        Assert.DoesNotContain(_runner.Calls, argv => argv.Contains("new-window"));
    }

    [Fact]
    public void AProvedProcessWhoseStartCannotBeReadIsUnprovenAndNothingIsWritten()
    {
        _callers = new RecordingCallers { StartText = null };
        var (home, snapshot) = ResumeReady("op8");
        var before = _sandbox.TeamText(Path.Combine(home, "teams", "proj.json"));

        var (code, stdout, stderr) = ResumeAs(snapshot);

        Assert.Equal(2, code);
        Assert.Equal("owner-unproven", JsonText.Parse(stdout).Member("refused")!.Value.AsString());
        Assert.Contains("start time", stderr, StringComparison.Ordinal);
        Assert.Equal(before, _sandbox.TeamText(Path.Combine(home, "teams", "proj.json")));
    }

    private void WriteOrchestratorRecord(string home, string pid, string? start, string extra = "")
    {
        var startPart = start is null ? string.Empty : $",\"pid_start\":\"{start}\"";
        var text = LiveTeamJson("tmux", string.Empty).Replace(
            "\"session_id\":null,\"pid\":" + Self + ",\"pid_start\":\"" + SelfStart + "\"",
            $"\"session_id\":null,\"pid\":{pid}{startPart}{extra}",
            StringComparison.Ordinal);
        _sandbox.PlantTeam(Path.Combine(home, "teams", "proj.json"),text);
    }

    [Fact]
    public void ARecordWithTheRightPidAndStartIsLeftAloneButOneWithNoStartOrAnotherStartIsRewritten()
    {
        var (home, snapshot) = ResumeReady("op9");
        ObserverSeesAParent();
        WriteOrchestratorRecord(home, Self, SelfStart, ",\"keep\":\"yes\"");
        Assert.Equal("yes", RecordedOrchestrator(home)["keep"].AsString());

        var (sameCode, _, sameErr) = ResumeAs(snapshot);
        var kept = RecordedOrchestrator(home).Get("keep");

        _callers = new RecordingCallers { StartText = SelfStart, OwnerGone = true };
        WriteOrchestratorRecord(home, Self, "2001-01-01T00:00:00.000Z", ",\"keep\":\"yes\"");
        var (otherCode, _, otherErr) = ResumeAs(snapshot);
        var afterOtherStart = RecordedOrchestrator(home);

        WriteOrchestratorRecord(home, Self, null, ",\"keep\":\"yes\"");
        var (heldCode, _, _) = ResumeAs(snapshot);
        var (noneCode, _, noneErr) = ResumeAs(snapshot, "--force");
        var afterNoStart = RecordedOrchestrator(home);

        Assert.NotEqual(0, heldCode);
        Assert.True(sameCode == 0 && otherCode == 0 && noneCode == 0, sameErr + otherErr + noneErr);
        Assert.Equal("yes", kept!.Value.AsString());
        Assert.Null(afterOtherStart.Get("keep"));
        Assert.Equal(SelfStart, afterOtherStart["pid_start"].AsString());
        Assert.Null(afterNoStart.Get("keep"));
        Assert.Equal(SelfStart, afterNoStart["pid_start"].AsString());
    }

    [Fact]
    public void ALiveHolderWhoseRecordedStartIsItsOwnStillHoldsTheTeamAndNothingIsLaunched()
    {
        var (home, snapshot) = ResumeReady("op10");
        var holder = LivePidOfAnotherProcess().ToString(CultureInfo.InvariantCulture);
        const string HolderStart = "2026-10-15T21:00:00.000Z";
        _runner.Ps = argv => argv.Contains("lstart=") && argv.Contains(holder) ? HolderStart + "\n" : SelfStartOnly(argv);
        WriteOrchestratorRecord(home, holder, HolderStart);

        var (code, stdout, stderr) = ResumeAs(snapshot);

        Assert.NotEqual(0, code);
        Assert.Contains("held by a live Orchestrator", stdout + stderr, StringComparison.Ordinal);
        Assert.Equal(holder, ((int)RecordedOrchestrator(home)["pid"].AsNumber()).ToString(CultureInfo.InvariantCulture));
        Assert.DoesNotContain(_runner.Calls, argv => argv.Contains("new-window"));
    }

    [Fact]
    public void ALiveHolderWithNoRecordedStartAndNoProvingRegistrationStillHoldsTheTeamUnlessResumeIsForced()
    {
        var (home, snapshot) = ResumeReady("op10b");
        var holder = LivePidOfAnotherProcess().ToString(CultureInfo.InvariantCulture);
        WriteOrchestratorRecord(home, holder, null);

        var (refusedCode, refusedOut, refusedErr) = ResumeAs(snapshot);
        var heldAfterRefusal = (int)RecordedOrchestrator(home)["pid"].AsNumber();
        var (code, stdout, stderr) = ResumeAs(snapshot, "--force");

        Assert.NotEqual(0, refusedCode);
        Assert.Contains("resume --force", refusedOut + refusedErr, StringComparison.Ordinal);
        Assert.Equal(holder, heldAfterRefusal.ToString(CultureInfo.InvariantCulture));
        Assert.True(code == 0, stdout + stderr);
        Assert.Equal(Environment.ProcessId, (int)RecordedOrchestrator(home)["pid"].AsNumber());
    }

    [Fact]
    public void NegativeForceDoesNotTakeOverATeamWhoseHolderIsProvenLive()
    {
        var (home, snapshot) = ResumeReady("op10d");
        var holder = LivePidOfAnotherProcess().ToString(CultureInfo.InvariantCulture);
        const string HolderStart = "2026-10-15T21:00:00.000Z";
        _runner.Ps = argv => argv.Contains("lstart=") && argv.Contains(holder) ? HolderStart + "\n" : SelfStartOnly(argv);
        WriteOrchestratorRecord(home, holder, HolderStart);

        var (code, stdout, stderr) = ResumeAs(snapshot, "--force");

        Assert.NotEqual(0, code);
        Assert.Contains("held by a live Orchestrator", stdout + stderr, StringComparison.Ordinal);
        Assert.Equal(holder, ((int)RecordedOrchestrator(home)["pid"].AsNumber()).ToString(CultureInfo.InvariantCulture));
    }

    [Fact]
    public void ADeadHolderDoesNotHoldTheTeam()
    {
        var (home, snapshot) = ResumeReady("op10c");
        var start = new ProcessStartInfo("true") { UseShellExecute = false };
        using (var child = Process.Start(start) ?? throw new InvalidOperationException("true did not start"))
        {
            child.WaitForExit();
            var gone = child.Id.ToString(CultureInfo.InvariantCulture);
            _runner.Ps = argv => argv.Contains("ppid=") ? (argv.Contains("9999") ? "1\n" : "9999\n")
                : argv.Contains("lstart=") && !argv.Contains(gone) ? SelfStart + "\n" : null;
            WriteOrchestratorRecord(home, gone, SelfStart);
        }

        var (code, stdout, stderr) = ResumeAs(snapshot);

        Assert.True(code == 0, stdout + stderr);
        Assert.Equal(Environment.ProcessId, (int)RecordedOrchestrator(home)["pid"].AsNumber());
    }

    [Theory]
    [InlineData(",\"closed\":{\"at\":\"2026-01-01T00:00:00Z\"}", "closed")]
    [InlineData(",\"closed\":null", "owned-live")]
    public void TeamsSaysAClosedTeamIsClosedThoughItsOwnerStillRunsAndNegativeANullMarkIsNotClosed(string mark, string life)
    {
        WriteTeam("proj", LiveTeamJson("tmux", string.Empty).TrimEnd('}') + mark + "}");

        var (code, stdout, stderr) = Roster("teams");

        Assert.True(code == 0, stdout + stderr);
        var row = JsonText.Parse(stdout).Member("teams")!.Value.AsArray().Single(r => r.Member("name") is { IsText: true } n && n.AsString() == "proj");
        Assert.Equal(life, row.Member("life")!.Value.AsString());
    }

    [Fact]
    public void TeamsSaysAProvedLiveOrchestratorsTeamIsOwnedLiveAndNotOrphaned()
    {
        WriteTeam("proj", LiveTeamJson("tmux", string.Empty));

        var (code, stdout, stderr) = Roster("teams");

        Assert.True(code == 0, stdout + stderr);
        var row = JsonText.Parse(stdout).Member("teams")!.Value.AsArray().Single(r => r.Member("name") is { IsText: true } n && n.AsString() == "proj");
        Assert.Equal("owned-live", row.Member("life")!.Value.AsString());
        Assert.False(row.Member("orphaned")!.Value.AsBool());
    }

    [Fact]
    public void ALiveHolderWhoseRecordedStartIsNowAnotherProcessesDoesNotHoldTheTeam()
    {
        _callers = new RecordingCallers { StartText = SelfStart, OwnerGone = true };
        var (home, snapshot) = ResumeReady("op11");
        var holder = LivePidOfAnotherProcess().ToString(CultureInfo.InvariantCulture);
        _runner.Ps = argv => argv.Contains("lstart=") ? SelfStart + "\n" : null;
        WriteOrchestratorRecord(home, holder, "2001-01-01T00:00:00.000Z");

        var (code, stdout, stderr) = ResumeAs(snapshot);

        Assert.True(code == 0, stdout + stderr);
        Assert.Equal(Environment.ProcessId, (int)RecordedOrchestrator(home)["pid"].AsNumber());
        Assert.Equal(SelfStart, RecordedOrchestrator(home)["pid_start"].AsString());
        Assert.Null(JsonText.Parse(stdout).Member("ownership_refused"));
    }

    // ---------------------------------------------------------------- reporting: teams gate and doctor rows

    private string AuditPath => Ah.Core.Text.AhPaths.UserEntry(_sandbox.Home, Ah.Core.Text.AhPaths.WorktreeAuditFile);

    private void WriteAudit(params (string Team, string Result, string Reason, double HoursAgo)[] rows)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(AuditPath)!);
        var lines = rows.Select(r =>
            $$"""{"at":"{{DateTimeOffset.UtcNow.AddHours(-r.HoursAgo).ToString("o", CultureInfo.InvariantCulture)}}","team":"{{r.Team}}","member":"proj-implementor","action":"write","path":"/x","note":"Write","result":"{{r.Result}}","reason":"{{r.Reason}}","actor":"marker"}""");
        File.WriteAllLines(AuditPath, lines);
    }

    private string GateTeam(string members = "{}")
    {
        Directory.CreateDirectory(Path.Combine(Project, ".git"));
        WriteTeam("proj", LiveTeamJson("tmux", TwoPeers));
        WriteGrants("enforce", members);
        return Project;
    }

    private JsonArray DoctorRows()
    {
        var (code, stdout, stderr) = Roster("doctor", "--team", "proj");
        Assert.True(code == 0, stdout + stderr);
        return JsonText.Parse(stdout).Member("rows")!.Value.AsArray();
    }

    private static JsonObject? RowNamed(JsonArray rows, string prefix) =>
        rows.Select(r => r.AsObject()).FirstOrDefault(r => r["name"].AsString().StartsWith(prefix, StringComparison.Ordinal));

    [Fact]
    public void TeamsReportsTheGateOfATeamWithADocumentAndNullForOneWithout()
    {
        GateTeam($$$$"""{"proj-implementor":{"work_root":"{{{{Project}}}}","stream":"s1","role":"implementor","extra":[{"path":"/a","note":"n","for":null,"outside":false,"at":"2026-10-15T00:00:00Z"}],"tmp":{"root":"/t/x","alias":"/t/a","kind":"claude"}}}""");
        WriteAudit(
            ("proj", "would_refuse", "outside", 1),
            ("proj", "would_refuse", "outside", 2),
            ("proj", "would_refuse", "protected", 3),
            ("proj", "deny", "outside", 4),
            ("proj", "would_refuse", "outside", 30),
            ("other", "deny", "outside", 1));

        var (code, stdout, stderr) = Roster("teams");

        Assert.True(code == 0, stdout + stderr);
        var teams = JsonText.Parse(stdout).Member("teams")!.Value.AsArray().Select(t => t.AsObject()).ToList();
        var gate = teams.Single(t => t["name"].AsString() == "proj")["gate"].AsObject();
        Assert.Equal("enforce", gate["mode"].AsString());
        var member = Assert.Single(gate["members"].AsArray()).AsObject();
        Assert.Equal(("proj-implementor", Project, "s1", 1, "/t/x"), (member["name"].AsString(), member["work_root"].AsString(), member["stream"].AsString(), (int)member["extras"].AsNumber(), member["tmp_root"].AsString()));
        Assert.Equal(2, (int)gate["would_refuse_24h"].AsObject()["outside"].AsNumber());
        Assert.Equal(1, (int)gate["would_refuse_24h"].AsObject()["protected"].AsNumber());
        Assert.Equal(1, (int)gate["deny_24h"].AsNumber());
    }

    [Fact]
    public void TeamsGateIsNullWithoutAGrantsDocument()
    {
        Directory.CreateDirectory(Path.Combine(Project, ".git"));
        WriteTeam("proj", LiveTeamJson("tmux", TwoPeers));

        var (code, stdout, _) = Roster("teams");

        Assert.Equal(0, code);
        Assert.True(JsonText.Parse(stdout).Member("teams")!.Value.AsArray().Single(t => t.AsObject()["name"].AsString() == "proj").AsObject()["gate"].IsNull);
    }

    private (int Code, string Stdout, string Stderr) SpawnDocsWriter(params string[] extra) =>
        Roster(["spawn-ad-hoc", "docs-writer", "--team", "proj", "--orchestrator-pid", Self, "--dry-run", .. extra]);

    [Fact]
    public void ADocsWriterIsRefusedInTheMainCheckoutUnderAnActiveGateAndTheNearestInputsLiftIt()
    {
        Directory.CreateDirectory(Path.Combine(Project, ".git"));
        WriteGrants("enforce");
        var worktree = MakeWorkTree("wt");

        var (code, stdout, _) = SpawnDocsWriter("--model", "sonnet");

        Assert.Equal(2, code);
        Assert.Equal("main-checkout", JsonText.Parse(stdout).AsObject()["refused"].AsString());
        Assert.Equal(0, SpawnDocsWriter("--model", "sonnet", "--allow-main").Code);
        Assert.Equal(0, SpawnDocsWriter("--model", "sonnet", "--cwd", worktree).Code);
        WriteGrants("off");
        Assert.Equal(0, SpawnDocsWriter("--model", "sonnet").Code);
    }

    [Fact]
    public void ADocsWriterMemberWithNoModelLaunchesOnTheShippedSonnetAndIsNotAsked()
    {
        var (code, stdout, stderr) = SpawnDocsWriter();

        Assert.True(code == 0, stdout + stderr);
        Assert.Contains("--model sonnet", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("member-model-undefined", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void ACreatePlanFillsADocsWriterMembersModelFromTheShippedDefaultAndStillAsksForAnImplementors()
    {
        var file = Path.Combine(_sandbox.Home, ".claude", "ah2", "agent-hierarchy.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, """{"version":1,"enabled":true,"roster":{"route":"peer","members":[{"role":"docs-writer"},{"role":"implementor"}]}}""");

        var (code, stdout, stderr) = Roster("create", "--plan", "--team", "proj");

        Assert.True(code == 0, stdout + stderr);
        var plan = JsonText.Parse(stdout).AsObject();
        var members = plan["members"].AsArray().Select(m => m.AsObject()).ToList();
        var docs = members.Single(m => m["role"].AsString() == "docs-writer");
        Assert.Equal("sonnet", docs["model"].AsString());
        Assert.Equal("sonnet (shipped default)", docs["model_note"].AsString());
        var asked = plan["members_needing_model"].AsArray().Select(m => m.AsObject()["role"].AsString()).ToList();
        Assert.Equal(["implementor"], asked);
    }

    [Fact]
    public void AfterRoleRemoveClearsAFormerRowsOldKeysADocsWriterMemberLaunchesOnTheModelThatWasKept()
    {
        var file = Path.Combine(_sandbox.Home, ".claude", "ah2", "agent-hierarchy.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, """{"version":1,"enabled":true,"roles":{"docs-writer":{"class":"implement","routes":"x","model":"opus"}}}""");

        var removed = Roster("role", "remove", "docs-writer", "--level", "global");
        var (code, stdout, stderr) = SpawnDocsWriter();

        Assert.True(removed.Code == 0, removed.Stderr);
        Assert.True(code == 0, stdout + stderr);
        Assert.Contains("--model opus", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("built-in replaces", stdout + stderr, StringComparison.Ordinal);
        var row = JsonText.Parse(File.ReadAllText(file)).AsObject()["roles"].AsObject()["docs-writer"].AsObject();
        Assert.Equal(["model"], row.Keys.ToList());
    }

    [Fact]
    public void ADocsWriterMemberOfAnotherKindGetsNoShippedModelAndIsHandledAsAnImplementorOfThatKind()
    {
        var file = Path.Combine(_sandbox.Home, ".claude", "ah2", "agent-hierarchy.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, """{"version":1,"enabled":true,"roster":{"route":"peer","members":[{"role":"docs-writer","kind":"codex"},{"role":"implementor","kind":"codex"}]}}""");

        var (code, stdout, stderr) = Roster("create", "--plan", "--team", "proj");

        Assert.True(code == 0, stdout + stderr);
        var plan = JsonText.Parse(stdout).AsObject();
        var docs = plan["members"].AsArray().Select(m => m.AsObject()).Single(m => m["role"].AsString() == "docs-writer");
        Assert.Null(docs.Get("model_note"));
        Assert.DoesNotContain("sonnet", docs.Get("model")?.ToString() ?? string.Empty, StringComparison.Ordinal);
        var asked = plan["members_needing_model"].AsArray().Select(m => m.AsObject()).ToList();
        string Shape(JsonObject m, string role) => JsonText.Stringify(m, 0).Replace(role, "ROLE", StringComparison.Ordinal);
        var askedDocs = asked.Where(m => m["role"].AsString() == "docs-writer").Select(m => Shape(m, "docs-writer")).ToList();
        var askedImplementor = asked.Where(m => m["role"].AsString() == "implementor").Select(m => Shape(m, "implementor")).ToList();
        Assert.Equal(askedImplementor.Count, askedDocs.Count);
        Assert.Equal(askedImplementor, askedDocs);
    }

    [Fact]
    public void AnExplicitModelBeatsTheShippedDefault()
    {
        var (code, stdout, stderr) = SpawnDocsWriter("--model", "opus");

        Assert.True(code == 0, stdout + stderr);
        Assert.Contains("--model opus", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("--model sonnet", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void ACreatePlanFillsAUxEngineerMembersModelFromTheShippedDefaultOpus()
    {
        var file = Path.Combine(_sandbox.Home, ".claude", "ah2", "agent-hierarchy.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, """{"version":1,"enabled":true,"roster":{"route":"peer","members":[{"role":"ux-engineer"},{"role":"implementor"}]}}""");

        var (code, stdout, stderr) = Roster("create", "--plan", "--team", "proj");

        Assert.True(code == 0, stdout + stderr);
        var plan = JsonText.Parse(stdout).AsObject();
        var ux = plan["members"].AsArray().Select(m => m.AsObject()).Single(m => m["role"].AsString() == "ux-engineer");
        Assert.Equal("opus", ux["model"].AsString());
        Assert.Equal("opus (shipped default)", ux["model_note"].AsString());
        var asked = plan["members_needing_model"].AsArray().Select(m => m.AsObject()["role"].AsString()).ToList();
        Assert.Equal(["implementor"], asked);
    }

    [Fact]
    public void ARoleLevelModelBeatsTheShippedUxEngineerDefault()
    {
        var set = Roster("role", "set", "ux-engineer", "--model", "sonnet", "--level", "repo");
        Assert.True(set.Code == 0, set.Stderr);
        var file = Path.Combine(_sandbox.Home, ".claude", "ah2", "agent-hierarchy.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, """{"version":1,"enabled":true,"roster":{"route":"peer","members":[{"role":"ux-engineer"}]}}""");

        var (code, stdout, stderr) = Roster("create", "--plan", "--team", "proj");

        Assert.True(code == 0, stdout + stderr);
        var ux = JsonText.Parse(stdout).AsObject()["members"].AsArray().Select(m => m.AsObject()).Single(m => m["role"].AsString() == "ux-engineer");
        Assert.Equal("sonnet", ux["model"].AsString());
    }

    [Fact]
    public void ARoleLevelModelBeatsTheShippedDefault()
    {
        var set = Roster("role", "set", "docs-writer", "--model", "opus", "--level", "repo");
        Assert.True(set.Code == 0, set.Stderr);

        var (code, stdout, stderr) = SpawnDocsWriter();

        Assert.True(code == 0, stdout + stderr);
        Assert.Contains("--model opus", stdout, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("implementor")]
    [InlineData("architect")]
    [InlineData("reviewer")]
    public void AMemberOfAnotherBuiltInRoleWithNoModelIsStillAsked(string role)
    {
        var (code, stdout, _) = Roster("spawn-ad-hoc", role, "--team", "proj", "--orchestrator-pid", Self, "--dry-run");

        Assert.Equal(2, code);
        Assert.Equal("member-model-undefined", JsonText.Parse(stdout).AsObject()["refused"].AsString());
    }

    [Fact]
    public void AMemberOfACustomImplementRoleWithNoModelIsStillAsked()
    {
        Directory.CreateDirectory(Path.Combine(Project, ".git"));
        var set = Roster("role", "set", "tech-writer", "--class", "implement", "--routes", "docs", "--description", "Docs", "--level", "repo", "--scaffold", "repo");
        Assert.True(set.Code == 0, set.Stderr);

        var (code, stdout, _) = Roster("spawn-ad-hoc", "tech-writer", "--team", "proj", "--orchestrator-pid", Self, "--dry-run");

        Assert.Equal(2, code);
        Assert.Equal("member-model-undefined", JsonText.Parse(stdout).AsObject()["refused"].AsString());
    }

    private void WriteGlobalRosterMembers(string membersJson)
    {
        var file = Path.Combine(_sandbox.Home, ".claude", "ah2", "agent-hierarchy.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, """{"version":1,"enabled":true,"roster":{"route":"peer","members":""" + membersJson + "}}");
    }

    private void PutHerdrOnPath()
    {
        _sandbox.Executable("bin/herdr");
        _extraEnv["PATH"] = _sandbox.Bin;
        _runner.Output = argv => argv.Contains("get") ? """{"error":{"code":"agent_not_found"}}""" : "{}";
    }

    private (int Code, string Stdout, string Stderr) SpawnArchitect(params string[] extra) =>
        Roster(["spawn-ad-hoc", "architect", "--team", "proj", "--orchestrator-pid", Self, "--dry-run", .. extra]);

    [Fact]
    public void AnAdHocMemberTakesModelAndEffortFromItsRolesRosterRow()
    {
        WriteGlobalRosterMembers("""[{"role":"architect","model":"opus","effort":"medium"}]""");

        var (code, stdout, stderr) = SpawnArchitect();

        Assert.True(code == 0, stdout + stderr);
        Assert.Contains("--model opus", stdout, StringComparison.Ordinal);
        Assert.Contains("medium", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAdHocModelFlagBeatsTheRowsModelAndTheRowStillSuppliesEffort()
    {
        WriteGlobalRosterMembers("""[{"role":"architect","model":"opus","effort":"medium"}]""");

        var (code, stdout, stderr) = SpawnArchitect("--model", "sonnet");

        Assert.True(code == 0, stdout + stderr);
        Assert.Contains("--model sonnet", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("--model opus", stdout, StringComparison.Ordinal);
        Assert.Contains("medium", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAdHocMemberTakesAutoModeFromItsRolesRosterRow()
    {
        WriteGlobalRosterMembers("""[{"role":"architect","model":"opus","autoMode":"plan"}]""");

        var (code, stdout, stderr) = SpawnArchitect();

        Assert.True(code == 0, stdout + stderr);
        Assert.Contains("--permission-mode plan", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAdHocAutoModeFlagBeatsTheRowsAutoMode()
    {
        WriteGlobalRosterMembers("""[{"role":"architect","model":"opus","autoMode":"plan"}]""");

        var (code, stdout, stderr) = SpawnArchitect("--auto-mode", "acceptEdits");

        Assert.True(code == 0, stdout + stderr);
        Assert.Contains("--permission-mode acceptEdits", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("--permission-mode plan", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAdHocMemberWithNoRowForItsRoleIsStillAskedForAModel()
    {
        WriteGlobalRosterMembers("""[{"role":"reviewer","model":"opus"}]""");

        var (code, stdout, _) = SpawnArchitect();

        Assert.Equal(2, code);
        Assert.Equal("member-model-undefined", JsonText.Parse(stdout).AsObject()["refused"].AsString());
    }

    [Fact]
    public void AnAdHocMemberWhoseRowHasNoModelIsStillAskedForOne()
    {
        WriteGlobalRosterMembers("""[{"role":"architect","effort":"medium"}]""");

        var (code, stdout, _) = SpawnArchitect();

        Assert.Equal(2, code);
        Assert.Equal("member-model-undefined", JsonText.Parse(stdout).AsObject()["refused"].AsString());
    }

    [Fact]
    public void AnAdHocDocsWriterWhoseRowHasNoModelLaunchesOnTheShippedSonnet()
    {
        WriteGlobalRosterMembers("""[{"role":"docs-writer","effort":"medium"}]""");

        var (code, stdout, stderr) = SpawnDocsWriter();

        Assert.True(code == 0, stdout + stderr);
        Assert.Contains("--model sonnet", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAdHocMemberTakesTheFirstOfTwoRowsForItsRole()
    {
        WriteGlobalRosterMembers("""[{"role":"architect","model":"opus"},{"role":"architect","model":"haiku"}]""");

        var (code, stdout, stderr) = SpawnArchitect();

        Assert.True(code == 0, stdout + stderr);
        Assert.Contains("--model opus", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("haiku", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAdHocClaudeMemberTakesNothingFromACodexRow()
    {
        WriteGlobalRosterMembers("""[{"role":"architect","kind":"codex","model":"opus","effort":"medium"}]""");

        var (code, stdout, _) = SpawnArchitect();

        Assert.Equal(2, code);
        Assert.Equal("member-model-undefined", JsonText.Parse(stdout).AsObject()["refused"].AsString());
    }

    [Fact]
    public void AnAdHocCodexMemberTakesNothingFromAClaudeRow()
    {
        WriteGlobalRosterMembers("""[{"role":"architect","model":"opus","effort":"medium"}]""");

        PutHerdrOnPath();

        var (code, stdout, _) = SpawnArchitect("--kind", "codex", "--route", "pane");

        Assert.Equal(2, code);
        Assert.Equal("member-model-undefined", JsonText.Parse(stdout).AsObject()["refused"].AsString());
    }
}
