using System.Globalization;
using System.Text;
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
/// <c>doctor</c> over the real libraries and a sandbox repository: one row per check in a fixed order, nothing written, and the
/// exit code <c>--check</c> gives when a row is red.
/// </summary>
[Collection("RosterServices")]
public sealed partial class RosterDoctorVerbTests : IDisposable
{
    private static readonly string[] RowNames =
        [
            "version", "install", "identity", "owner-proof", "release-handshake", "lifecycle-permissions", "runtime-dir", "config", "team", "progress", "jobs-unseen", "alarm", "test-bucket", "peers", "work-roots", "tree-ownership", "registrations", "codex-grants", "state-db", "ahd-heal", "hook-errors", "rejected-requests", "hooks-syntax", "marketplace", "ux-skills",
            "trusted-homes", "trusted-hierarchy-dirs",
        ];

    private readonly Sandbox _sandbox = new();
    private IRosterDoctor? _doctorOverride;
    private NearestKind? _ancestry;
    private IHomeDocs? _homeDocsOverride;
    private string? _cwdUnderTest;
    private IProcessRunner? _runner;

    public RosterDoctorVerbTests()
    {
        Directory.CreateDirectory(Path.Combine(Project, ".git"));
        Directory.CreateDirectory(Path.Combine(Project, ".claude", "ah2"));
        InstallPlugin(Path.Combine(_sandbox.Home, ".claude", "plugins", "cache", "mkt", "wrangl", "1.2.3"));
    }

    private string Project => Path.Combine(_sandbox.Root, "proj");

    public void Dispose()
    {
        CommandsModule.ComposeRoster(() => null!);
        _sandbox.Dispose();
    }

    private sealed class FixedDoctor(IReadOnlyList<TrustFileReport> trust, string? note = null, IReadOnlyList<string>? registered = null, string? restart = null, string? unanswered = null, string? readModel = null, IReadOnlyList<string>? designSources = null, (string Level, string Detail)? stateDb = null) : IRosterDoctor
    {
        public IReadOnlyList<string> DesignSources() => designSources ?? [];

        public (string Level, string Detail) StateDb() => stateDb ?? ("ok", "schema v1, integrity ok, no backups");

        public (string Level, string Detail) TestBucket() => ("ok", "16 tokens, 0 held by 0 runs, 0 waiting");

        public string StatusReport(string cwd) => string.Empty;

        public string GitPorcelain(string directory) => string.Empty;

        public IReadOnlyList<TrustFileReport> TrustFiles() => trust;

        public string? ConventionsNote(string cwd) => note;

        public IReadOnlyList<string> LiveRegistrationAddresses() => registered ?? [];

        public IReadOnlyList<string> HeldRelays() => [];

        public string? DaemonRestartNote() => restart;

        public string? DaemonUnansweredNote() => unanswered;

        public string? DaemonReadModelNote() => readModel;
    }

    private sealed class Services(ServiceGraph graph, IFileReader files, IClock clock, IAhEnvironment environment, string cwd, IRosterDoctor? doctorOverride) : IRosterCliServices
    {
        private IRosterDoctor? _doctor;

        public IConfigCore Config => graph.Config;

        public IRosterCore Roster => graph.Roster;

        public HierarchyServices Hierarchy => graph.Hierarchy;

        public IHomeDocs? HomeDocs => HomeDocsOverride ?? Ah.StateDb.StateDbModule.CreateHomeDocs(environment.AhHome);

        public IHomeDocs? HomeDocsOverride { get; init; }

        public IAhEnvironment Environment => environment;

        public IClock Clock => clock;

        public IFileReader Files => files;

        public string CurrentDirectory => cwd;

        public int ParentPid => 4242;

        public ITeardown? Teardown => graph.Launch.Teardown;

        public IRosterLaunch? Launch => graph.Launch;

        public IRosterCallerRegistry? Callers => null;

        public IRosterDoctor? Doctor => _doctor ??= doctorOverride ?? new DoctorComposition(graph, environment);

        public IRosterOwnerProof? OwnerProof { get; init; }

        public uint? CurrentUserId { get; init; }
    }

    private sealed class FixedAncestry(NearestKind kind) : IRosterOwnerProof
    {
        public OwnerProofResult Check(string dir, string? team) => new(true, null, "orchestrator", false, null);

        public OwnerActor Actor() => new(4242, "Thu Oct 15 20:38:50 2026", [], "ah roster gate");

        public NearestSession NearestAncestor(string dir, IReadOnlyList<string> rowDirs) => new(kind);
    }

    /// <summary>The graph a run of <see cref="Roster"/> composes, for a test that plants state through the real ops.</summary>
    private ServiceGraph PlantGraph() => Compose(null).Graph;

    private (int Code, string Stdout, string Stderr) Roster(Dictionary<string, string?>? extra, params string[] args)
    {
        var (graph, io, env) = Compose(extra);
        var services = new Services(graph, io.Files, io.Clock, env, Project, _doctorOverride) { HomeDocsOverride = _homeDocsOverride, OwnerProof =_ancestry is { } kind ? new FixedAncestry(kind) : null, CurrentUserId = _userId ?? PlatformModule.CurrentUid() };
        CommandsModule.ComposeRoster(() => services);
        var verb = CommandsModule.Registry.Verbs.Single(v => v.Name == "roster");
        var stdout = new StringWriter(CultureInfo.InvariantCulture);
        var stderr = new StringWriter(CultureInfo.InvariantCulture);
        var code = verb.Run([.. args, "--cwd", _cwdUnderTest ?? Project], stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private (ServiceGraph Graph, PlatformIo Io, FakeEnv Env) Compose(Dictionary<string, string?>? extra)
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
            ["PATH"] = _sandbox.Bin,
            ["WRANGL_HIERARCHY_DIR"] = Path.Combine(Project, ".claude", "ah2"),
        };
        if (extra is not null)
        {
            foreach (var (k, v) in extra)
            {
                variables[k] = v;
            }
        }

        var env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables, new PluginInfo(_rootUnderTest ?? _pluginRoot, "wrangl", "1.2.3", "1"));
        var io = PlatformModule.CreateIo(null);
        return (ServiceGraph.Build(io, env, _runner ?? new FakeRunner(), new FakeDaemonClient()), io, env);
    }

    private static JsonObject Parse(string text) => JsonText.Parse(Encoding.UTF8.GetBytes(text)).AsObject();

    private static JsonObject Row(JsonObject report, string name) =>
        report["rows"].AsArray().Single(r => r.Member("name")!.Value.AsString() == name).AsObject();

    private List<string> Tree() =>
        Directory.EnumerateFileSystemEntries(_sandbox.Root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToList();

    [Fact]
    public void DoctorPrintsOneRowPerCheckInOrderAndWritesNothing()
    {
        var before = Tree();

        var (code, stdout, stderr) = Roster(null, "doctor");

        Assert.True(code == 0, stderr);
        var report = Parse(stdout);
        Assert.Equal(Project, report["cwd"].AsString());
        Assert.Equal(RowNames, report["rows"].AsArray().Select(r => r.Member("name")!.Value.AsString()).Take(RowNames.Length));
        Assert.Equal(before, Tree());
    }

    [Theory]
    [InlineData(NearestKind.Session)]
    [InlineData(NearestKind.Orchestrator)]
    [InlineData(NearestKind.Member)]
    [InlineData(NearestKind.None)]
    public void DoctorSaysTheOwnerProofIsOkWheneverTheProcessTableCanBeWalked(NearestKind kind)
    {
        _ancestry = kind;

        var row = Row(Parse(Roster(null, "doctor").Stdout), "owner-proof");

        Assert.Equal("ok", row["status"].AsString());
        Assert.Equal("ancestry", row["detail"].AsString());
    }

    [Fact]
    public void DoctorSaysTheReleaseHandshakeIsOkAndOnlyOnceBecauseThisBuildHoldsMembersThatHaveNotReleased()
    {
        var report = Parse(Roster(null, "doctor").Stdout);

        var row = Row(report, "release-handshake");
        Assert.Equal("ok", row["status"].AsString());
        Assert.Equal(1, report["rows"].AsArray().Count(r => r.Member("name")!.Value.AsString() == "release-handshake"));
        Assert.DoesNotContain("release-handshake", report["red"].AsArray().Select(r => r.AsString()));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DoctorWarnsWhenTheProcessTableCannotBeReadOrNoProofIsProvided(bool provided)
    {
        _ancestry = provided ? NearestKind.Unavailable : null;

        var report = Parse(Roster(null, "doctor").Stdout);

        var row = Row(report, "owner-proof");
        Assert.Equal("warn", row["status"].AsString());
        Assert.Equal("process table unavailable", row["detail"].AsString());
        Assert.DoesNotContain("owner-proof", report["red"].AsArray().Select(r => r.AsString()));
    }

    [Fact]
    public void DoctorSaysTheIdentityIsRedWithoutAPidAndOkWithALiveOne()
    {
        var none = Parse(Roster(null, "doctor").Stdout);
        var row = Row(none, "identity");
        Assert.Equal("red", row["status"].AsString());
        Assert.Contains("CLAUDE_PID unset", row["detail"].AsString(), StringComparison.Ordinal);
        Assert.Contains("identity", none["red"].AsArray().Select(r => r.AsString()));

        var live = Parse(Roster(new Dictionary<string, string?> { ["CLAUDE_PID"] = Environment.ProcessId.ToString(CultureInfo.InvariantCulture) }, "doctor").Stdout);
        Assert.Equal("ok", Row(live, "identity")["status"].AsString());
        Assert.DoesNotContain("identity", live["red"].AsArray().Select(r => r.AsString()));

        var bad = Parse(Roster(new Dictionary<string, string?> { ["CLAUDE_PID"] = "soon" }, "doctor").Stdout);
        Assert.Equal("red", Row(bad, "identity")["status"].AsString());
        Assert.Contains("CLAUDE_PID is not an integer: \"soon\"", Row(bad, "identity")["detail"].AsString(), StringComparison.Ordinal);
    }

    [Fact]
    public void DoctorExitsOneOnlyWhenAskedToCheckAndARowIsRed()
    {
        var plain = Roster(null, "doctor");
        var check = Roster(null, "doctor", "--check");
        var pid = new Dictionary<string, string?> { ["CLAUDE_PID"] = Environment.ProcessId.ToString(CultureInfo.InvariantCulture) };
        var healthy = Roster(pid, "doctor", "--check");

        Assert.Equal(0, plain.Code);
        Assert.Equal(1, check.Code);
        Assert.Contains("\"identity\"", check.Stdout, StringComparison.Ordinal);
        Assert.Equal(0, healthy.Code);
    }

    [Fact]
    public void DoctorNamesABrokenRosterSelectionAsARedRowAfterTheOthers()
    {
        File.WriteAllText(
            Path.Combine(Project, ".claude", "ah2", "agent-hierarchy.json"),
            "{\"version\":1,\"activeRoster\":\"ghost\",\"roster\":{\"route\":\"peer\",\"members\":[]}}");

        var report = Parse(Roster(null, "doctor").Stdout);

        var rows = report["rows"].AsArray();
        var last = rows[rows.Count - 1].AsObject();
        Assert.Equal("roster-selection", last["name"].AsString());
        Assert.Equal("red", last["status"].AsString());
        Assert.Contains("ghost", last["detail"].AsString(), StringComparison.Ordinal);
        Assert.Contains("roster-selection", report["red"].AsArray().Select(r => r.AsString()));
    }

    [Fact]
    public void DoctorReportsNoTeamFileAndNoPeersOnAFreshRepository()
    {
        var report = Parse(Roster(null, "doctor").Stdout);

        Assert.Contains("no team file at", Row(report, "team")["detail"].AsString(), StringComparison.Ordinal);
        Assert.Equal("no roster records",Row(report, "peers")["detail"].AsString());
        Assert.Contains("no hook errors in the last 24 h", Row(report, "hook-errors")["detail"].AsString(), StringComparison.Ordinal);
        Assert.Equal("ok", Row(report, "hooks-syntax")["status"].AsString());
    }

    private void RejectedRow(DateTimeOffset at, int status, string code, string? caller, int pid, string message)
    {
        var path = Path.Combine(_sandbox.AhHome, "logs", "rejected.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var callerJson = caller is null ? "null" : "\"" + caller + "\"";
        File.AppendAllText(
            path,
            $"{{\"ts\":\"{at.UtcDateTime:yyyy-MM-ddTHH:mm:ss.fffZ}\",\"method\":\"POST\",\"path\":\"/v1/state/gates/append\",\"status\":{status},\"code\":\"{code}\",\"message\":\"{message}\",\"field\":null,\"peer_pid\":{pid},\"client_version\":\"1\",\"caller\":{callerJson}}}\n");
    }

    [Fact]
    public void RuntimeDirSaysWhenItWillBeCreated()
    {
        if (Directory.Exists(Hierarchy))
        {
            Directory.Delete(Hierarchy, recursive: true);
        }

        var row = Row(Parse(Roster(null, "doctor").Stdout), "runtime-dir");

        Assert.Equal("ok", row["status"].AsString());
        Assert.Contains("does not exist yet — created at session start or by the first project state write", row["detail"].AsString(), StringComparison.Ordinal);
        Assert.DoesNotContain("created on first write", row["detail"].AsString(), StringComparison.Ordinal);
    }

    [Fact]
    public void DoctorIsOkWithNoRejections()
    {
        var row = Row(Parse(Roster(null, "doctor").Stdout), "rejected-requests");

        Assert.Equal("ok", row["status"].AsString());
        Assert.Contains("no rejected daemon requests in the last 24 h", row["detail"].AsString(), StringComparison.Ordinal);
    }

    [Fact]
    public void DoctorShowsRecentRejectedRequests()
    {
        RejectedRow(DateTimeOffset.UtcNow.AddHours(-2), 400, "bad_request", "stop-hook", 4242, "hierarchy_dir /x does not exist");
        RejectedRow(DateTimeOffset.UtcNow.AddMinutes(-5), 404, "not_found", null, 777, "no such path");
        RejectedRow(DateTimeOffset.UtcNow.AddHours(-30), 500, "internal", null, 1, "too old to show");

        var row = Row(Parse(Roster(null, "doctor").Stdout), "rejected-requests");

        Assert.Equal("warn", row["status"].AsString());
        var detail = row["detail"].AsString();
        Assert.Contains("2 rejected daemon request(s) in 24 h", detail, StringComparison.Ordinal);
        Assert.Contains("400 bad_request POST /v1/state/gates/append stop-hook: hierarchy_dir /x does not exist", detail, StringComparison.Ordinal);
        Assert.Contains("404 not_found POST /v1/state/gates/append pid 777: no such path", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("too old to show", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void DoctorSaysToCommitTheConventionsFileAtTheAh2PathOnlyWhenV1sPathIsTheSource()
    {
        _doctorOverride = new FixedDoctor([], "conventions are read from v1's path: commit the file at .claude/ah2/ah-conventions.json");
        var withNote = Parse(Roster(null, "doctor").Stdout);
        _doctorOverride = new FixedDoctor([]);
        var without = Parse(Roster(null, "doctor").Stdout);

        var row = Row(withNote, "conventions");
        Assert.Equal("warn", row["status"].AsString());
        Assert.Contains(".claude/ah2/ah-conventions.json", row["detail"].AsString(), StringComparison.Ordinal);
        Assert.DoesNotContain("conventions", withNote["red"].AsArray().Select(r => r.AsString()));
        Assert.DoesNotContain(without["rows"].AsArray(), r => r.Member("name")!.Value.AsString() == "conventions");
    }

    [Fact]
    public void ADaemonThatDidNotAnswerIsAWarningAndDoesNotFailTheCheck()
    {
        _doctorOverride = new FixedDoctor([]);
        var baseline = Roster(null, "doctor", "--check");
        _doctorOverride = new FixedDoctor([], unanswered: "the running ah daemon did not answer the mail protocol check in time");
        var result = Roster(null, "doctor", "--check");
        var parsed = Parse(result.Stdout);

        var row = Row(parsed, "daemon-contract");
        Assert.Equal("warn", row["status"].AsString());
        Assert.DoesNotContain("daemon-contract", parsed["red"].AsArray().Select(r => r.AsString()));
        Assert.Equal(baseline.Code, result.Code);
    }

    [Fact]
    public void TheReadModelCountersShowAsOneRowWhenTheDaemonReportsThemAndNoRowOtherwiseAndNeverFailTheCheck()
    {
        _doctorOverride = new FixedDoctor([]);
        var baseline = Roster(null, "doctor", "--check");
        var without = Parse(baseline.Stdout);
        _doctorOverride = new FixedDoctor([], readModel: "unclassified_hint=2 drift=0 coalesce_ms=100");
        var result = Roster(null, "doctor", "--check");
        var parsed = Parse(result.Stdout);

        var row = Row(parsed, "read-model");
        Assert.Equal("ok", row["status"].AsString());
        Assert.Contains("unclassified_hint=2", row["detail"].AsString(), StringComparison.Ordinal);
        Assert.DoesNotContain("read-model", parsed["red"].AsArray().Select(r => r.AsString()));
        Assert.DoesNotContain(without["rows"].AsArray(), r => r.Member("name")!.Value.AsString() == "read-model");
        Assert.Equal(baseline.Code, result.Code);
    }

    [Fact]
    public void AnOlderDaemonStaysRedAndFailsTheCheck()
    {
        _doctorOverride = new FixedDoctor([], restart: "the running ah daemon is older than this install");
        var result = Roster(null, "doctor", "--check");

        Assert.Equal(1, result.Code);
        Assert.Equal("red", Row(Parse(result.Stdout), "daemon-contract")["status"].AsString());
    }

    [Fact]
    public void DoctorIsRedAndSaysToRestartWhenTheRunningDaemonIsOlderThanTheInstall()
    {
        _doctorOverride = new FixedDoctor([], restart: "the running ah daemon is older than this install; restart it with `ahd restart`");
        var older = Parse(Roster(null, "doctor").Stdout);
        _doctorOverride = new FixedDoctor([]);
        var current = Parse(Roster(null, "doctor").Stdout);

        var row = Row(older, "daemon-contract");
        Assert.Equal("red", row["status"].AsString());
        Assert.Contains("restart", row["detail"].AsString(), StringComparison.Ordinal);
        Assert.Contains("daemon-contract", older["red"].AsArray().Select(r => r.AsString()));
        Assert.DoesNotContain(current["rows"].AsArray(), r => r.Member("name")!.Value.AsString() == "daemon-contract");
    }

    private string LockPath => Path.Combine(_sandbox.AhHome, "bin", ".rebuild.lock");

    private void WriteLock(string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LockPath)!);
        File.WriteAllText(LockPath, content);
    }

    private static string LockAged(int minutes) =>
        $"{{\"pid\":4242,\"at\":{(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - (minutes * 60_000L)).ToString(CultureInfo.InvariantCulture)},\"nonce\":\"n\"}}";

    [Fact]
    public void DoctorHasNoRebuildLockRowWhenThereIsNoLock()
    {
        var report = Parse(Roster(null, "doctor").Stdout);

        Assert.DoesNotContain(report["rows"].AsArray(), r => r.Member("name")!.Value.AsString() == "rebuild-lock");
    }

    [Fact]
    public void DoctorCallsAFreshRebuildLockAnInstallAtWork()
    {
        WriteLock(LockAged(9));

        var report = Parse(Roster(null, "doctor").Stdout);

        var row = Row(report, "rebuild-lock");
        Assert.Equal("ok", row["status"].AsString());
        Assert.Contains("held by an install or rebuild", row["detail"].AsString(), StringComparison.Ordinal);
        Assert.Contains("pid 4242", row["detail"].AsString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(11)]
    [InlineData(600)]
    public void DoctorWarnsOfAStaleRebuildLockNamingTheFileAndTheFix(int minutes)
    {
        WriteLock(LockAged(minutes));
        var before = File.ReadAllText(LockPath);

        var pid = new Dictionary<string, string?> { ["CLAUDE_PID"] = Environment.ProcessId.ToString(CultureInfo.InvariantCulture) };

        var (code, stdout, _) = Roster(pid, "doctor", "--check");

        var report = Parse(stdout);
        var row = Row(report, "rebuild-lock");
        Assert.Equal("warn", row["status"].AsString());
        Assert.Contains(LockPath, row["detail"].AsString(), StringComparison.Ordinal);
        Assert.Contains("rm " + LockPath, row["detail"].AsString(), StringComparison.Ordinal);
        Assert.Contains("flock(1)", row["detail"].AsString(), StringComparison.Ordinal);
        Assert.DoesNotContain("rebuild-lock", report["red"].AsArray().Select(r => r.AsString()));
        Assert.Equal(0, code);
        Assert.Equal(before, File.ReadAllText(LockPath));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"pid\":1}")]
    [InlineData("")]
    public void ALockWithNoReadableAgeIsStaleAsTheScriptCountsIt(string content)
    {
        WriteLock(content);

        var row = Row(Parse(Roster(null, "doctor").Stdout), "rebuild-lock");

        Assert.Equal("warn", row["status"].AsString());
        Assert.Contains("age unknown", row["detail"].AsString(), StringComparison.Ordinal);
    }

    [Fact]
    public void DoctorShowsEachTrustFileAbsentIgnoredOrEntryByEntry()
    {
        _doctorOverride = new FixedDoctor(
        [
            new TrustFileReport(TrustFile.Homes, "/h/trusted-homes", false, null, []),
            new TrustFileReport(
                TrustFile.HierarchyDirs,
                "/h/trusted-hierarchy-dirs",
                true,
                null,
                [new TrustEntry("/a", "/a", true, null), new TrustEntry("/b", null, false, "is not a directory")]),
        ]);

        var report = Parse(Roster(null, "doctor").Stdout);

        var homes = Row(report, "trusted-homes");
        Assert.Equal("ok", homes["status"].AsString());
        Assert.Equal("/h/trusted-homes is absent: nothing is trusted from it", homes["detail"].AsString());
        var dirs = Row(report, "trusted-hierarchy-dirs");
        Assert.Equal("warn", dirs["status"].AsString());
        Assert.Equal("/h/trusted-hierarchy-dirs: 2 entries: /a (trusted); /b (ignored: is not a directory)", dirs["detail"].AsString());

        _doctorOverride = new FixedDoctor([new TrustFileReport(TrustFile.Homes, "/h/trusted-homes", true, "is writable by its group", [])]);
        var ignored = Row(Parse(Roster(null, "doctor").Stdout), "trusted-homes");
        Assert.Equal("warn", ignored["status"].AsString());
        Assert.Equal("/h/trusted-homes is ignored: is writable by its group", ignored["detail"].AsString());
    }

    private string Hierarchy => Path.Combine(Project, ".claude", "ah2");

    private void SeedTeamWithOnePeer(int panePid, string status = "up", string? spawnedAt = null)
    {
        Directory.CreateDirectory(Path.Combine(Hierarchy, "teams"));
        File.WriteAllText(
            Path.Combine(Hierarchy, "teams", "proj.json"),
            "{\"version\":1,\"orchestrator\":{\"session_id\":\"o\",\"pid\":99999999},\"members\":[{\"role\":\"implementor\",\"name\":\"proj-implementor\",\"route\":\"peer\",\"transport_id\":\"%1\"" + (spawnedAt is null ? string.Empty : ",\"spawned_at\":\"" + spawnedAt + "\"") + "}]}");
        _sandbox.PlantPeers(
            Hierarchy,
            $"{{\"type\":\"peer\",\"status\":\"{status}\",\"session_id\":\"s1\",\"pid\":{panePid},\"pane_id\":\"%1\",\"ts\":\"2026-03-01T00:00:00.000Z\"}}\n");
    }

    [Fact]
    public void DoctorWarnsAboutALiveMemberTheDaemonDoesNotKnow()
    {
        SeedTeamWithOnePeer(Environment.ProcessId);
        _doctorOverride = new FixedDoctor([], registered: []);

        var (code, stdout, _) = Roster(null, "doctor");

        var row = Row(Parse(stdout), "registrations");
        Assert.Equal(0, code);
        Assert.Equal("warn", row["status"].AsString());
        Assert.Equal(
            "1 live but unregistered: proj-implementor@proj (pane %1) — run `ah roster checkin --team proj` in that session",
            row["detail"].AsString());
    }

    [Theory]
    [InlineData(0, "ok")]
    [InlineData(600, "warn")]
    public void DoctorSkipsAMemberThatIsStillStartingAndNoOther(int ageSeconds, string status)
    {
        SeedTeamWithOnePeer(Environment.ProcessId, spawnedAt: Ah.Core.Time.Timestamps.ToIsoString(DateTimeOffset.UtcNow.AddSeconds(-ageSeconds)));
        _doctorOverride = new FixedDoctor([], registered: []);

        var row = Row(Parse(Roster(null, "doctor").Stdout), "registrations");

        Assert.Equal(status, row["status"].AsString());
    }

    [Fact]
    public void DoctorIsOkWhenEveryLiveMemberIsRegistered()
    {
        SeedTeamWithOnePeer(Environment.ProcessId);
        _doctorOverride = new FixedDoctor([], registered: ["proj-implementor@proj"]);

        var row = Row(Parse(Roster(null, "doctor").Stdout), "registrations");

        Assert.Equal("ok", row["status"].AsString());
        Assert.Equal("every live member is registered", row["detail"].AsString());
    }

    [Fact]
    public void DoctorIgnoresAMemberWhosePidIsDead()
    {
        SeedTeamWithOnePeer(99999999);
        _doctorOverride = new FixedDoctor([], registered: []);

        var row = Row(Parse(Roster(null, "doctor").Stdout), "registrations");

        Assert.Equal("ok", row["status"].AsString());
    }

    [Fact]
    public void DoctorIgnoresAMemberWhosePeersRowIsNotUp()
    {
        SeedTeamWithOnePeer(Environment.ProcessId, status: "down");
        _doctorOverride = new FixedDoctor([], registered: []);

        var row = Row(Parse(Roster(null, "doctor").Stdout), "registrations");

        Assert.Equal("ok", row["status"].AsString());
    }
}
