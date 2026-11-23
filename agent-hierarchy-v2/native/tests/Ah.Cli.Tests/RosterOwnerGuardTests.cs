using System.Diagnostics;
using System.Globalization;
using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Commands;
using Ah.Config;
using Ah.Core.Json;
using Ah.Hierarchy;
using Ah.Launch;
using Ah.Mesh;
using Ah.Platform;
using Ah.Roster;
using Ah.TestKit;

namespace Ah.Cli.Tests;

/// <summary>
/// The one check every owner verb routes through: the member marker, the process ancestry, the stated-pid cross-check and team
/// ownership, each refusal beside the nearest input the guard lets through.
/// </summary>
[Collection("RosterServices")]
public sealed partial class RosterOwnerGuardTests : IDisposable
{
    private readonly Sandbox _sandbox = new();
    private readonly List<Process> _children = [];
    private readonly Dictionary<string, string?> _env = [];
    private readonly FakeRunner _runner = new();
    private bool _withScratch;

    /// <summary>The start time the scripted <c>ps</c> gives this test process, which a team it owns records.</summary>
    private const string SelfStart = "2026-10-15T20:38:50.000Z";

    /// <summary>The start times the scripted <c>ps</c> answers, by pid: this process and each live process a test starts.</summary>
    private readonly Dictionary<string, string> _starts = new() { [Self] = SelfStart };

    /// <summary>The recorded-start member of an Orchestrator record for <paramref name="pid"/>: its scripted start, or none for a pid nothing started.</summary>
    private string StartPart(string pid) =>
        _starts.TryGetValue(pid, out var start) ? ",\"pid_start\":\"" + start + "\"" : pid == GonePid.ToString(System.Globalization.CultureInfo.InvariantCulture) || pid == "999999" ? GoneStartPart : string.Empty;
    private NearestSession _nearest = new(NearestKind.Session, null, null, Environment.ProcessId);
    private int _walks;

    public RosterOwnerGuardTests()
    {
        _starts[ObserverParent] = SelfStart;
        _runner.Ps = argv => argv.Contains("lstart=") && argv.Length > 0 && _starts.TryGetValue(argv[^1], out var start) ? start + "\n"
            : argv.Contains("ppid=") && argv.Length > 0 && argv[^1] == Self ? ObserverParent + "\n"
            : argv.Contains("ppid=") && argv.Length > 0 && argv[^1] == ObserverParent ? "1\n"
            : null;
        Directory.CreateDirectory(Project);
        Directory.CreateDirectory(Hierarchy);
    }

    /// <summary>A parent the scripted table gives this process, so an observer sees a process other than itself and pid 1.</summary>
    private const string ObserverParent = "9999";

    private string _projectName = "proj";

    private string Project => Path.Combine(_sandbox.Root, _projectName);

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

    private sealed class CountingProof(Func<NearestSession> nearest, OwnerProofResult? check = null, Func<int?, int?>? harness = null, Func<int, bool?>? isAncestor = null) : IRosterOwnerProof
    {
        public OwnerProofResult Check(string dir, string? team) => check ?? new(true, null, "orchestrator", false, null);

        public OwnerActor Actor() => new(4242, "Thu Oct 15 20:38:50 2026", [], "ah roster gate");

        public NearestSession NearestAncestor(string dir, IReadOnlyList<string> rowDirs) => nearest();

        public int? HarnessAncestor(int? stated = null) => harness?.Invoke(stated);

        public bool? IsAncestor(int pid) => isAncestor?.Invoke(pid);
    }

    /// <summary>The nearest harness the proof's command-only walk finds above the command; null for none.</summary>
    private int? _harness;

    /// <summary>Further harnesses on the ancestor line, which a <c>CLAUDE_PID</c> may name.</summary>
    private readonly HashSet<int> _harnessLine = [];

    /// <summary>The pid the fake's harness walk last answered, which the fake walk then finds as a session once it has a row.</summary>
    private int? _picked;

    private int? PickHarness(int? stated)
    {
        _picked = stated is { } s && _harnessLine.Contains(s) ? s : _harness;
        return _picked;
    }

    private sealed class StartedCallers : IRosterCallerRegistry
    {
        public const string Started = "Wed Oct 21 08:00:05 2026";

        public IReadOnlyList<string> Register(string cwd, string? sessionId, int pid, string? paneId, string? teamArg, bool teamDefaultExplicit) => [];

        public IReadOnlyList<string> OwnedTeams(string cwd, string? sessionId, int pid) => [];

        public string? ProcessStartText(int pid) => Started;
    }

    /// <summary>A resumed session: this process is bound to <paramref name="session"/>, and the rebind is the test's callback.</summary>
    private sealed class ResumedCallers(string session, Func<string, int, IReadOnlyList<string>> rebind) : IRosterCallerRegistry
    {
        public IReadOnlyList<string> Register(string cwd, string? sessionId, int pid, string? paneId, string? teamArg, bool teamDefaultExplicit) => [];

        public IReadOnlyList<string> OwnedTeams(string cwd, string? sessionId, int pid) => [];

        public IReadOnlyList<BoundSession> BoundSessions(int pid) => pid == Environment.ProcessId ? [new(session, DateTimeOffset.UtcNow)] : [];

        public IReadOnlyList<string> RebindTeams(string cwd, string sessionId, int pid) => rebind(sessionId, pid);
    }

    private IRosterCallerRegistry? _callers;

    private sealed class Services(ServiceGraph graph, IFileReader files, IClock clock, IAhEnvironment environment, string cwd, IRosterOwnerProof? proof, ICodexTrust? codexTrust = null, bool withScratch = false, IRosterCallerRegistry? callers = null)
        : IRosterCliServices
    {
        public IRosterScratch? Scratch => withScratch ? new RosterComposition(graph, clock, environment).Scratch : null;

        public ICodexTrust? CodexTrust => codexTrust;

        public IHomeDocs? HomeDocs => graph.HomeDocs;

        public IRosterWake? Wake => new RosterComposition(graph, clock, environment).Wake;

        public string? WorkDescribe(string pool, string team, string key, string body, string by, string? session) =>
            new RosterComposition(graph, clock, environment).WorkDescribe(pool, team, key, body, by, session);

        public IConfigCore Config => graph.Config;

        public IRosterCore Roster => graph.Roster;

        public HierarchyServices Hierarchy => graph.Hierarchy;

        public IAhEnvironment Environment => environment;

        public IClock Clock => clock;

        public IFileReader Files => files;

        public string CurrentDirectory => cwd;

        public int ParentPid => 4242;

        public ITeardown? Teardown => graph.Launch.Teardown;

        public IRosterLaunch? Launch => graph.Launch;

        public IRosterCallerRegistry? Callers { get; } = callers ?? new StartedCallers();

        public IRosterOwnerProof? OwnerProof => proof;
    }

    private (int Code, string Stdout, string Stderr) Roster(bool withProof, params string[] args)
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
        foreach (var (key, value) in _env)
        {
            variables[key] = value;
        }

        var env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables, new PluginInfo(_sandbox.PluginRoot, "wrangl", "1.0.0", "1"));
        var io = PlatformModule.CreateIo(null);
        var graph = ServiceGraph.Build(io, env, _runner, new FakeDaemonClient());
        var proof = withProof ? new CountingProof(() =>
        {
            _walks++;
            return _picked is { } h && _nearest.Kind == NearestKind.None && RowSessions(h).Count > 0 ? new NearestSession(NearestKind.Session, null, null, h) : _nearest;
        }, null, PickHarness, pid => pid == _harness || _harnessLine.Contains(pid)) : null;
        var services = new Services(graph, io.Files, io.Clock, env, Project, proof, _codexTrust?.Invoke(io.Files, env), _withScratch, _callers);
        CommandsModule.ComposeRoster(() => services);
        var verb = CommandsModule.Registry.Verbs.Single(v => v.Name == "roster");
        var stdout = new StringWriter(CultureInfo.InvariantCulture);
        var stderr = new StringWriter(CultureInfo.InvariantCulture);
        var code = verb.Run(args, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private (int Code, string Stdout, string Stderr) Run(params string[] args) => Roster(true, args);

    private int LivePidOfAnotherProcess()
    {
        var start = new ProcessStartInfo("sleep", "120") { UseShellExecute = false, RedirectStandardOutput = true };
        var child = Process.Start(start) ?? throw new InvalidOperationException("sleep did not start");
        _children.Add(child);
        _starts[child.Id.ToString(CultureInfo.InvariantCulture)] = "2026-10-15T21:00:00.000Z";
        return child.Id;
    }

    /// <summary>A graph over this sandbox's home, to read what the store holds.</summary>
    private ServiceGraph StoreGraph()
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        return ServiceGraph.Build(
            PlatformModule.CreateIo(null), new FakeEnv(_sandbox.Home, _sandbox.AhHome, new Dictionary<string, string?> { ["HOME"] = _sandbox.Home, ["AH_HOME"] = _sandbox.AhHome }), new FakeRunner(), new FakeDaemonClient());
    }

    /// <summary>The team record the store holds at <paramref name="path"/>, compact; <c>null</c> when it holds none.</summary>
    private string StoredTeam(string path) => JsonText.Stringify(StoreGraph().Roster.ReadTeamFileAt(path), 0);

    private void TeamOwnedBy(string pid, string streams = "{\"s1\":{\"state\":\"open\"},\"old\":{\"state\":\"done\"}}", string prefix = "tm")
    {
        _sandbox.PlantTeam(
            Path.Combine(Hierarchy, "teams", prefix + ".json"),
            "{\"version\":1,\"team_id\":\"t-tm\",\"transport\":\"terminal\",\"roster_level\":\"repo\",\"orchestrator\":{\"session_id\":null,\"pid\":" + pid + StartPart(pid) + "},\"members\":[],\"streams\":" + streams + "}");
    }

    private static (string? Refused, string? Proof) Refusal(string stdout)
    {
        if (!stdout.StartsWith('{'))
        {
            return (null, null);
        }

        var o = JsonText.Parse(stdout).AsObject();
        return o.Get("status") is { IsText: true } status && status.AsString() == "refused"
            ? (o["refused"].AsString(), o["proof"].AsString())
            : (null, null);
    }

    public static TheoryData<string> GuardedVerbs() =>
        ["create", "spawn-one", "spawn-ad-hoc", "dismiss", "disband", "untrack", "reap", "adopt", "move", "transfer", "stream-open", "stream-done", "progress", "checkpoint"];

    public static TheoryData<string> VerbsOnAnExistingTeam() =>
        ["spawn-one", "spawn-ad-hoc", "dismiss", "disband", "untrack", "move", "transfer", "stream-open", "stream-done", "progress", "checkpoint"];

    [Theory]
    [MemberData(nameof(GuardedVerbs))]
    public void AMemberMarkerWithNoRegisteredOrchestrateMemberRefusesTheVerbAfterOneWalk(string verb)
    {
        _env[MeshModule.MemberMarkerEnv] = "proj-implementor";

        var (code, stdout, stderr) = Run(verb, "--team", "tm");

        Assert.Equal(2, code);
        Assert.Equal(($"member-cannot-{verb}", "member-marker"), Refusal(stdout));
        Assert.StartsWith($"ah roster {verb}: this shell carries a member marker", stderr, StringComparison.Ordinal);
        Assert.Contains("owner proof: this shell carries a member marker.", stderr, StringComparison.Ordinal);
        Assert.Equal(1, _walks);
    }

    [Theory]
    [MemberData(nameof(GuardedVerbs))]
    public void AnEmptyMemberMarkerIsNotAMarker(string verb)
    {
        _env[MeshModule.MemberMarkerEnv] = string.Empty;
        TeamOwnedBy(Self);

        var (_, stdout, _) = Run(verb, "--team", "tm");

        Assert.Null(Refusal(stdout).Refused);
        Assert.Equal(1, _walks);
    }

    [Theory]
    [MemberData(nameof(GuardedVerbs))]
    public void AMemberAboveTheCommandIsRefused(string verb)
    {
        _nearest = new NearestSession(NearestKind.Member, "proj", "proj-implementor", Environment.ProcessId);

        var (code, stdout, _) = Run(verb, "--team", "tm");

        Assert.Equal(2, code);
        Assert.Equal(($"member-cannot-{verb}", "member-ancestor"), Refusal(stdout));
    }

    [Theory]
    [MemberData(nameof(GuardedVerbs))]
    public void NoSessionAboveTheCommandIsUnproven(string verb)
    {
        _nearest = new NearestSession(NearestKind.None);

        var (code, stdout, _) = Run(verb, "--team", "tm");

        Assert.Equal(2, code);
        Assert.Equal(("owner-unproven", "no-orchestrator"), Refusal(stdout));
    }

    [Theory]
    [MemberData(nameof(GuardedVerbs))]
    public void AnUnreadableProcessTableIsUnprovenAndNothingIsWritten(string verb)
    {
        TeamOwnedBy(LivePidOfAnotherProcess().ToString(CultureInfo.InvariantCulture));
        _nearest = new NearestSession(NearestKind.Unavailable);
        var files = () => Directory.GetFiles(Hierarchy, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(f => (f, File.ReadAllText(f))).ToArray();
        var before = files();

        var (code, stdout, _) = Run(verb, "--team", "tm");

        Assert.Equal(2, code);
        Assert.Equal(("owner-unproven", "unproven"), Refusal(stdout));
        Assert.Equal(before, files());
    }

    [Fact]
    public void ARunWithNoOwnerProofServiceIsUnproven()
    {
        var (code, stdout, _) = Roster(false, "dismiss", "--team", "tm");

        Assert.Equal(2, code);
        Assert.Equal(("owner-unproven", "unproven"), Refusal(stdout));
    }

    [Theory]
    [MemberData(nameof(VerbsOnAnExistingTeam))]
    public void ASessionThatOwnsTheTeamPassesTheGuard(string verb)
    {
        TeamOwnedBy(Self);

        var (_, stdout, _) = Run(verb, "--team", "tm");

        Assert.Null(Refusal(stdout).Refused);
    }

    [Theory]
    [MemberData(nameof(VerbsOnAnExistingTeam))]
    public void AnOrchestratorThatOwnsTheTeamPassesTheGuard(string verb)
    {
        _nearest = new NearestSession(NearestKind.Orchestrator, null, null, Environment.ProcessId);
        TeamOwnedBy(Self);

        var (_, stdout, _) = Run(verb, "--team", "tm");

        Assert.Null(Refusal(stdout).Refused);
    }

    [Theory]
    [MemberData(nameof(VerbsOnAnExistingTeam))]
    public void ASessionThatDoesNotOwnALiveTeamIsRefusedAndNothingChanges(string verb)
    {
        TeamOwnedBy(LivePidOfAnotherProcess().ToString(CultureInfo.InvariantCulture));
        var before = StoredTeam(Path.Combine(Hierarchy,"teams", "tm.json"));

        var (code, stdout, stderr) = Run(verb, "--team", "tm");

        Assert.Equal(2, code);
        Assert.Equal(("not-team-owner", "other-team"), Refusal(stdout));
        Assert.StartsWith($"ah roster {verb}: this session does not own team \"tm\"; nothing was changed.", stderr, StringComparison.Ordinal);
        Assert.Equal(before, StoredTeam(Path.Combine(Hierarchy,"teams", "tm.json")));
    }

    [Fact]
    public void AResumedSessionWhoseOldPidIsGoneReOwnsItsTeamByItsSessionIdOnDismiss()
    {
        var path = Path.Combine(Hierarchy, "teams", "tm.json");
        string Team(string pid) =>
            "{\"version\":1,\"team_id\":\"t-tm\",\"transport\":\"terminal\",\"roster_level\":\"repo\",\"orchestrator\":{\"session_id\":\"s-resumed\",\"pid\":" + pid + StartPart(pid) + "},\"members\":[],\"streams\":{}}";
        _sandbox.PlantTeam(path, Team("999999"));
        var rebinds = 0;
        _callers = new ResumedCallers("s-resumed", (session, pid) =>
        {
            rebinds++;
            _sandbox.PlantTeam(path, Team(pid.ToString(CultureInfo.InvariantCulture)));
            return ["tm"];
        });

        var (_, stdout, _) = Run("dismiss", "--team", "tm");

        Assert.Null(Refusal(stdout).Refused);
        Assert.Equal(1, rebinds);
    }

    [Theory]
    [InlineData("create")]
    [InlineData("reap")]
    [InlineData("adopt")]
    public void TheVerbsThatDoNotActOnAnExistingTeamNeedNoOwnership(string verb)
    {
        TeamOwnedBy(LivePidOfAnotherProcess().ToString(CultureInfo.InvariantCulture));

        var (_, stdout, _) = Run(verb, "--team", "tm");

        Assert.NotEqual("not-team-owner", Refusal(stdout).Refused);
    }

    [Fact]
    public void AnOrphanedTeamCanBeDisbandedFromAnywhereButNotDismissedFrom()
    {
        TeamOwnedBy("999999");

        var (_, disband, _) = Run("disband", "--team", "tm");
        var (dismissCode, dismiss, _) = Run("dismiss", "--team", "tm");

        Assert.Null(Refusal(disband).Refused);
        Assert.Equal(2, dismissCode);
        Assert.Equal("not-team-owner", Refusal(dismiss).Refused);
    }

    [Fact]
    public void AnOrphanedTeamIsStillNotDisbandedFromAMemberShell()
    {
        TeamOwnedBy("999999");
        _env[MeshModule.MemberMarkerEnv] = "proj-implementor";

        var (code, stdout, _) = Run("disband", "--team", "tm");

        Assert.Equal(2, code);
        Assert.Equal(("member-cannot-disband", "member-marker"), Refusal(stdout));
    }

    [Fact]
    public void AnOrphanedTeamIsDisbandedWhateverTheAncestryShows()
    {
        TeamOwnedBy("999999");
        _nearest = new NearestSession(NearestKind.Member, "proj", "proj-implementor", Environment.ProcessId);

        var (_, fromMember, _) = Run("disband", "--team", "tm");

        Assert.Null(Refusal(fromMember).Refused);
    }

    private void LegacyTeamOwnedBy(string pid)
    {
        _sandbox.PlantTeam(
            Path.Combine(Hierarchy, "team.json"),
            "{\"version\":1,\"team_id\":\"t-legacy\",\"transport\":\"terminal\",\"roster_level\":\"repo\",\"orchestrator\":{\"session_id\":null,\"pid\":" + pid + StartPart(pid) + "},\"members\":[]}");
    }

    [Theory]
    [InlineData("spawn-one")]
    [InlineData("spawn-ad-hoc")]
    [InlineData("stream-open")]
    public void ACreateThatDefaultedOntoAnOrphanedLegacyTeamIsNotRefusedAsNotItsOwner(string verb)
    {
        LegacyTeamOwnedBy("999999");

        var (_, stdout, _) = Run(verb);

        Assert.NotEqual("not-team-owner", Refusal(stdout).Refused);
    }

    [Theory]
    [InlineData("spawn-one")]
    [InlineData("spawn-ad-hoc")]
    [InlineData("stream-open")]
    public void AnOrphanedLegacyTeamDoesNotLetACreateIntoAnotherOwnersLivePrefixTeam(string verb)
    {
        LegacyTeamOwnedBy("999999");
        TeamOwnedBy(LivePidOfAnotherProcess().ToString(CultureInfo.InvariantCulture), prefix: Path.GetFileName(Project));
        var legacy = StoredTeam(Path.Combine(Hierarchy,"team.json"));
        var prefixed = StoredTeam(Path.Combine(Hierarchy,"teams", Path.GetFileName(Project) + ".json"));

        var (code, stdout, _) = Run(verb);

        Assert.Equal(2, code);
        Assert.Equal(("not-team-owner", "other-team"), Refusal(stdout));
        Assert.Equal(legacy, StoredTeam(Path.Combine(Hierarchy,"team.json")));
        Assert.Equal(prefixed, StoredTeam(Path.Combine(Hierarchy,"teams", Path.GetFileName(Project) + ".json")));
    }

    [Theory]
    [InlineData("spawn-one")]
    [InlineData("spawn-ad-hoc")]
    [InlineData("stream-open")]
    public void AnUnnamablePrefixIsRefusedByNameBeforeOwnershipAndNothingIsWritten(string verb)
    {
        _projectName = "my_proj";
        Directory.CreateDirectory(Project);
        LegacyTeamOwnedBy("999999");
        var legacy = StoredTeam(Path.Combine(Hierarchy,"team.json"));

        var (code, stdout, stderr) = Run(verb);

        Assert.Equal(2, code);
        Assert.NotEqual("not-team-owner", Refusal(stdout).Refused);
        Assert.StartsWith("ah roster: Team name \"my_proj\" (basename) can't be", stderr, StringComparison.Ordinal);
        Assert.Empty(StoreGraph().Roster.ListTeamNames(Hierarchy));
        Assert.Equal(legacy, StoredTeam(Path.Combine(Hierarchy,"team.json")));
    }

    [Theory]
    [InlineData("spawn-one")]
    [InlineData("spawn-ad-hoc")]
    [InlineData("stream-open")]
    public void AnOrphanedPrefixTeamAsTheDefaultedTargetIsStillRefusedToANonOwner(string verb)
    {
        TeamOwnedBy("999999", prefix: Path.GetFileName(Project));

        var (code, stdout, _) = Run(verb);

        Assert.Equal(2, code);
        Assert.Equal(("not-team-owner", "other-team"), Refusal(stdout));
    }

    [Theory]
    [InlineData("spawn-one")]
    [InlineData("spawn-ad-hoc")]
    [InlineData("stream-open")]
    public void ACreateThatNamesAnOrphanedTeamWithTeamStillGoesThroughOwnership(string verb)
    {
        TeamOwnedBy("999999");

        var (code, stdout, _) = Run(verb, "--team", "tm");

        Assert.Equal(2, code);
        Assert.Equal(("not-team-owner", "other-team"), Refusal(stdout));
    }

    [Fact]
    public void AVerbThatActsOnAnExistingTeamIsStillRefusedOnAnOrphanedLegacyTeam()
    {
        LegacyTeamOwnedBy("999999");

        var (code, stdout, _) = Run("dismiss");

        Assert.Equal(2, code);
        Assert.Equal("not-team-owner", Refusal(stdout).Refused);
    }

    [Fact]
    public void AFreshSessionMayCreateATeamAndAMemberMayNot()
    {
        var (_, fromSession, _) = Run("create", "--team", "tm");
        _nearest = new NearestSession(NearestKind.Member, "proj", "proj-implementor", Environment.ProcessId);
        var (code, fromMember, _) = Run("create", "--team", "tm");

        Assert.Null(Refusal(fromSession).Refused);
        Assert.Equal(2, code);
        Assert.Equal(("member-cannot-create", "member-ancestor"), Refusal(fromMember));
    }

    [Theory]
    [InlineData("--orchestrator-pid")]
    [InlineData("CLAUDE_PID")]
    public void AStatedPidThatIsNotTheProvedSessionIsRefused(string source)
    {
        TeamOwnedBy(Self);
        var other = LivePidOfAnotherProcess().ToString(CultureInfo.InvariantCulture);
        string[] args = source == "CLAUDE_PID" ? ["dismiss", "--team", "tm"] : ["dismiss", "--team", "tm", "--orchestrator-pid", other];
        if (source == "CLAUDE_PID")
        {
            _env["CLAUDE_PID"] = other;
        }

        var (code, stdout, stderr) = Run(args);

        Assert.Equal(2, code);
        Assert.Equal("orchestrator-pid-mismatch", Refusal(stdout).Refused);
        Assert.StartsWith($"ah roster dismiss: {source} names pid {other}, but this command runs under pid {Self}; nothing was changed.", stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--orchestrator-pid")]
    [InlineData("CLAUDE_PID")]
    public void AStatedPidThatIsTheProvedSessionPassesTheGuard(string source)
    {
        TeamOwnedBy(Self);
        string[] args = source == "CLAUDE_PID" ? ["dismiss", "--team", "tm"] : ["dismiss", "--team", "tm", "--orchestrator-pid", Self];
        if (source == "CLAUDE_PID")
        {
            _env["CLAUDE_PID"] = Self;
        }

        var (_, stdout, _) = Run(args);

        Assert.Null(Refusal(stdout).Refused);
    }

    [Fact]
    public void TheAncestryIsWalkedOncePerInvocation()
    {
        TeamOwnedBy(Self);

        Run("spawn-ad-hoc", "reviewer", "--kind", "custom", "--harness", "mytool", "--team", "tm");

        Assert.Equal(1, _walks);
    }

    // ---------------------------------------------------------------- ah progress item

    private string ProgressFolder => Path.Combine(Hierarchy, "progress", "tm");

    private string PlanLog => Path.Combine(ProgressFolder, "s1.plan.jsonl");

    [Fact]
    public void AnOwnerRecordsAPlanEventAndGetsTheStateLineAndTheChecklistIsRendered()
    {
        TeamOwnedBy(Self);

        var (code, stdout, stderr) = Run("progress", "item", "add", "s1", "a", "--title", "Do a", "--team", "tm");

        Assert.Equal(0, code);
        Assert.Equal("progress: s1/a → not_started\n", stdout);
        Assert.Equal(string.Empty, stderr);
        Assert.Single(ReadLogLines(PlanLog));
        Assert.Contains("- [ ] Do a", Ah.TestKit.TestProgressDocs.Read(_sandbox.AhHome, Path.Combine(ProgressFolder, "s1.md")));
    }

    [Fact]
    public void AnOwnerRanksAreasAssignsBlocksAndUnblocksAnItemThroughTheVerbFlags()
    {
        TeamOwnedBy(Self);
        Run("progress", "item", "add", "s1", "a", "--title", "Do a", "--team", "tm");

        Assert.Equal("progress: s1/a → not_started\n", Run("progress", "item", "rank", "s1", "a", "--rank", "3", "--team", "tm").Stdout);
        Run("progress", "item", "area", "s1", "a", "--area", "core", "--team", "tm");
        Run("progress", "item", "assign", "s1", "a", "--holder", "impl-1", "--team", "tm");
        var log = ReadLog(PlanLog);
        Assert.Contains("\"rank\":3", log, StringComparison.Ordinal);
        Assert.Contains("\"area\":\"core\"", log, StringComparison.Ordinal);
        Assert.Contains("\"holder\":\"impl-1\"", log, StringComparison.Ordinal);

        Assert.Equal("progress: s1/a → blocked\n", Run("progress", "item", "block", "s1", "a", "--note", "needs R3", "--team", "tm").Stdout);
        Assert.Equal("progress: s1/a → not_started\n", Run("progress", "item", "unblock", "s1", "a", "--team", "tm").Stdout);
    }

    [Fact]
    public void NegativeABlockWithNoNoteARankThatIsNotANumberAndAnAssignWithNoHolderAreRefusedAndNothingIsWritten()
    {
        TeamOwnedBy(Self);
        Run("progress", "item", "add", "s1", "a", "--title", "Do a", "--team", "tm");
        var before = ReadLog(PlanLog);

        Assert.Equal(2, Run("progress", "item", "block", "s1", "a", "--team", "tm").Code);
        var rank = Run("progress", "item", "rank", "s1", "a", "--rank", "-1", "--team", "tm");
        Assert.Equal(2, rank.Code);
        Assert.Contains("--rank -1 is not a whole number", rank.Stderr, StringComparison.Ordinal);
        Assert.Equal(2, Run("progress", "item", "assign", "s1", "a", "--team", "tm").Code);
        Assert.Equal(before, ReadLog(PlanLog));
    }

    [Fact]
    public void NegativeAnotherSessionsTeamIsRefusedAndNothingIsRecorded()
    {
        TeamOwnedBy(LivePidOfAnotherProcess().ToString(CultureInfo.InvariantCulture));

        var (code, stdout, _) = Run("progress", "item", "add", "s1", "a", "--title", "Do a", "--team", "tm");

        Assert.Equal(2, code);
        Assert.Equal("not-team-owner", Refusal(stdout).Refused);
        Assert.False(LogExists(PlanLog));
    }

    [Fact]
    public void P2ADoneWithACommitGitCannotResolveIsRefusedNamingItAndNothingIsWritten()
    {
        TeamOwnedBy(Self);
        Run("progress", "item", "add", "s1", "a", "--title", "A", "--team", "tm");
        var rows = ReadLog(PlanLog);
        var md = Ah.TestKit.TestProgressDocs.Read(_sandbox.AhHome, Path.Combine(ProgressFolder, "s1.md"));
        _runner.Exit = argv => argv.Any(static w => w.StartsWith("deadbee", StringComparison.Ordinal)) ? 128 : 0;

        var (code, _, stderr) = Run("progress", "item", "done", "s1", "a", "--commit", "abc1234", "--commit", "deadbee", "--team", "tm");

        Assert.Equal(2, code);
        Assert.Contains("progress item done: commit deadbee not found; nothing written", stderr, StringComparison.Ordinal);
        Assert.Equal(rows, ReadLog(PlanLog));
        Assert.Equal(md, Ah.TestKit.TestProgressDocs.Read(_sandbox.AhHome, Path.Combine(ProgressFolder, "s1.md")));
    }

    [Fact]
    public void NegativeDoneWithEveryCommitResolvingAsksGitTheReadOnlyQuestionAndRecordsThemAll()
    {
        TeamOwnedBy(Self);
        Run("progress", "item", "add", "s1", "a", "--title", "A", "--team", "tm");

        var (code, stdout, _) = Run("progress", "item", "done", "s1", "a", "--commit", "abc1234", "--commit", "def5678", "--branch", "ah/s1", "--team", "tm");

        Assert.Equal(0, code);
        Assert.Equal("progress: s1/a → done\n", stdout);
        Assert.Contains(_runner.Calls, static c => c.SequenceEqual(["git", "-c", "core.fsmonitor=false", "rev-parse", "--verify", "abc1234^{commit}"]));
        Assert.Contains(_runner.Calls, static c => c.SequenceEqual(["git", "-c", "core.fsmonitor=false", "rev-parse", "--verify", "def5678^{commit}"]));
        Assert.Contains("- [x] A (abc1234, def5678) (done ", Ah.TestKit.TestProgressDocs.Read(_sandbox.AhHome, Path.Combine(ProgressFolder, "s1.md")), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("add nosuch a --title A", "progress item add: stream nosuch is not one of this team's streams (old, s1); nothing written")]
    [InlineData("import nosuch", "progress item import: stream nosuch is not one of this team's streams (old, s1); nothing written")]
    [InlineData("add S1 a --title A", "stream S1 is not one of this team's streams")]
    public void AStreamTheTeamRecordDoesNotListIsRefusedNamingTheKnownOnesAndNothingIsWritten(string words, string message)
    {
        TeamOwnedBy(Self);

        var (code, _, stderr) = Run([.. ("progress item " + words + " --team tm").Split(' ')]);

        Assert.Equal(2, code);
        Assert.Contains(message, stderr, StringComparison.Ordinal);
        Assert.Empty(TestProgressLogs.PlanLogs(_sandbox.AhHome, Hierarchy, "tm"));
    }

    [Fact]
    public void ATeamRecordWithNoStreamsRefusesAnyStreamAndSaysNoneAreKnown()
    {
        TeamOwnedBy(Self, "{}");

        var (code, _, stderr) = Run("progress", "item", "add", "s1", "a", "--title", "A", "--team", "tm");

        Assert.Equal(2, code);
        Assert.Contains("stream s1 is not one of this team's streams (none)", stderr, StringComparison.Ordinal);
        Assert.False(LogExists(PlanLog));
    }

    [Fact]
    public void NegativeADoneStreamTheRecordStillListsIsAccepted()
    {
        TeamOwnedBy(Self);

        var (code, stdout, _) = Run("progress", "item", "add", "old", "a", "--title", "A", "--team", "tm");

        Assert.Equal(0, code);
        Assert.Equal("progress: old/a → not_started\n", stdout);
    }

    [Fact]
    public void ADoneCommitIsLookedUpOnlyInTheWorkTreeTheStreamRecords()
    {
        TeamOwnedBy(Self, "{\"s1\":{\"state\":\"open\",\"worktree\":\"/wt/s1\"}}");
        Run("progress", "item", "add", "s1", "a", "--title", "A", "--team", "tm");
        var before = _runner.Calls.Count;

        var (code, _, _) = Run("progress", "item", "done", "s1", "a", "--commit", "abc1234", "--team", "tm");

        Assert.Equal(0, code);
        var asked = _runner.Directories.Skip(before).ToList();
        Assert.NotEmpty(asked);
        Assert.All(asked, static d => Assert.Equal("/wt/s1", d));
    }

    [Fact]
    public void ACommitMissingFromTheRecordedWorkTreeIsRefusedWithoutAskingWhereTheCommandRuns()
    {
        TeamOwnedBy(Self, "{\"s1\":{\"state\":\"open\",\"worktree\":\"/wt/s1\"}}");
        Run("progress", "item", "add", "s1", "a", "--title", "A", "--team", "tm");
        var before = _runner.Calls.Count;
        _runner.Exit = argv => argv.Contains("rev-parse") ? 128 : 0;

        var (code, _, stderr) = Run("progress", "item", "done", "s1", "a", "--commit", "abc1234", "--team", "tm");

        Assert.Equal(2, code);
        Assert.Contains("progress item done: commit abc1234 not found; nothing written", stderr, StringComparison.Ordinal);
        Assert.All(_runner.Directories.Skip(before), static d => Assert.Equal("/wt/s1", d));
    }

    private const string TwoStreams = "{\"s1\":{\"state\":\"open\",\"worktree\":\"/wt/s1\",\"branch\":\"ah/s1\"},\"s2\":{\"state\":\"open\",\"worktree\":\"/wt/s2\",\"branch\":\"ah/s2\"}}";

    [Fact]
    public void ACommitOnAnotherStreamsBranchOfTheSameTeamIsAcceptedAsDone()
    {
        TeamOwnedBy(Self, TwoStreams);
        Run("progress", "item", "add", "s1", "a", "--title", "A", "--team", "tm");
        _runner.Exit = argv => argv.Contains("rev-parse") && _runner.Directories[^1] != "/wt/s2" ? 128 : 0;

        var (code, stdout, _) = Run("progress", "item", "done", "s1", "a", "--commit", "abc1234", "--team", "tm");

        Assert.Equal(0, code);
        Assert.Equal("progress: s1/a → done\n", stdout);
    }

    [Fact]
    public void ACommitOnNoBranchOfTheTeamIsRefusedNamingTheBranchesChecked()
    {
        TeamOwnedBy(Self, TwoStreams);
        Run("progress", "item", "add", "s1", "a", "--title", "A", "--team", "tm");
        _runner.Exit = argv => argv.Contains("rev-parse") ? 128 : 0;

        var (code, _, stderr) = Run("progress", "item", "done", "s1", "a", "--commit", "abc1234", "--team", "tm");

        Assert.Equal(2, code);
        Assert.Contains("progress item done: commit abc1234 not found; nothing written (branches checked: ah/s1, ah/s2)", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeAStreamThatRecordsNoWorkTreeIsCheckedWhereTheCommandRuns()
    {
        TeamOwnedBy(Self, "{\"s1\":{\"state\":\"open\",\"worktree\":null}}");
        Run("progress", "item", "add", "s1", "a", "--title", "A", "--team", "tm");
        var before = _runner.Calls.Count;

        var (code, _, _) = Run("progress", "item", "done", "s1", "a", "--commit", "abc1234", "--team", "tm");

        Assert.Equal(0, code);
        Assert.Equal([Project], _runner.Directories.Skip(before).Distinct());
    }

    [Theory]
    [InlineData("done s1 a --commit --upload-pack=x", "commit --upload-pack=x not found")]
    [InlineData("done s1 a --commit zzzzzzz", "commit zzzzzzz not found")]
    [InlineData("done s1 a --commit ABC1234", "commit ABC1234 not found")]
    public void ACommitThatIsNotLowercaseHexIsRefusedBeforeGitIsAsked(string words, string message)
    {
        TeamOwnedBy(Self);
        Run("progress", "item", "add", "s1", "a", "--title", "A", "--team", "tm");
        var asked = _runner.Calls.Count;

        var (code, _, stderr) = Run([.. ("progress item " + words + " --team tm").Split(' ')]);

        Assert.Equal(2, code);
        Assert.Contains(message, stderr, StringComparison.Ordinal);
        Assert.Equal(asked, _runner.Calls.Count);
    }

    [Theory]
    [InlineData("add s1 a --bogus x", "unknown flag --bogus")]
    [InlineData("add s1", "usage:")]
    [InlineData("explode s1 a", "usage:")]
    [InlineData("link s1 a --issue 0", "not a positive whole number")]
    [InlineData("import", "usage:")]
    [InlineData("start s1 ghost", "item ghost was never added")]
    [InlineData("add s1 a", "add needs a title")]
    public void ABadInvocationIsRefusedWithItsReasonAndWritesNothing(string words, string message)
    {
        TeamOwnedBy(Self);

        var (code, _, stderr) = Run([.. ("progress item " + words + " --team tm").Split(' ')]);

        Assert.Equal(2, code);
        Assert.Contains(message, stderr, StringComparison.Ordinal);
        Assert.False(LogExists(PlanLog));
    }

    [Fact]
    public void WhenProgressIsOffTheVerbSaysSoAndWritesNothing()
    {
        TeamOwnedBy(Self);
        Ah.TestKit.TestProgressDocs.Off(_sandbox.AhHome, Hierarchy, "tm");

        var (code, _, stderr) = Run("progress", "item", "add", "s1", "a", "--title", "A", "--team", "tm");

        Assert.Equal(2, code);
        Assert.Contains("progress item add: progress is off for tm; nothing written", stderr, StringComparison.Ordinal);
        Assert.False(LogExists(PlanLog));
    }

    [Fact]
    public void ImportOptsAnExistingChecklistInAndListsTheLineItCouldNotReadAndASecondImportIsRefused()
    {
        TeamOwnedBy(Self);
        Directory.CreateDirectory(ProgressFolder);
        TestProgressDocs.WriteFile(
            _sandbox.AhHome,
            Path.Combine(ProgressFolder, "s1.md"),
            "<!-- ah-progress v1.0 kind=stream -->\n# s1 — goal\nOwner: o   State: open\nUpdated: 2026-10-16T00:00:00+00:00\n\n## Checklist\n### Done\n- One (abc1234) (done 2026-10-16T01:00:00Z)\n- Two (9074df35..38a6ccb1)\n### In progress\n- Three\n### Not started\n\n## Related\n");

        var (code, stdout, _) = Run("progress", "item", "import", "s1", "--team", "tm");
        var (again, _, stderr) = Run("progress", "item", "import", "s1", "--team", "tm");

        Assert.Equal(0, code);
        Assert.Equal("progress: s1 → planned (3 item(s) imported)\nunparsed: two: a commit range: 9074df35..38a6ccb1\n", stdout);
        Assert.Equal(2, again);
        Assert.Contains("already has a plan", stderr, StringComparison.Ordinal);
    }
}
