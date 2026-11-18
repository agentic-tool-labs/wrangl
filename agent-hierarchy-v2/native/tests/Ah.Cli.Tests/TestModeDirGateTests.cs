using System.Collections;
using System.Diagnostics;
using System.Runtime.Versioning;
using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Mesh;
using Ah.Platform;
using Ah.Roster;
using Ah.StateDb;
using Ah.TestKit;

namespace Ah.Cli.Tests;

/// <summary>A test-mode daemon treats a hierarchy dir outside its probe root as not listed, and refuses to start in the real home.</summary>
[UnsupportedOSPlatform("windows")]
public sealed class TestModeDirGateTests : IDisposable
{
    private readonly Sandbox _sandbox = new();
    private readonly Sandbox _elsewhere = new();
    private readonly List<string> _log = [];

    public void Dispose()
    {
        _sandbox.Dispose();
        _elsewhere.Dispose();
    }

    private sealed class Routes : IRouteRegistry
    {
        public void Add(string pattern, Func<RouteRequest, CancellationToken, Task<RouteResponse>> handler)
        {
        }
    }

    private FakeEnv Env(bool testMode)
    {
        var vars = new Dictionary<string, string?> { ["AH_HOME"] = _sandbox.AhHome, ["HOME"] = _sandbox.Home };
        if (testMode)
        {
            vars["AH_TEST"] = "1";
        }

        return new FakeEnv(_sandbox.Home, _sandbox.AhHome, vars, new PluginInfo(_sandbox.PluginRoot, "wrangl", "0.0.0", "1"));
    }

    private static ServiceGraph Graph(FakeEnv env) => ServiceGraph.Build(PlatformModule.CreateIo(null), env, new FakeRunner(), new FakeDaemonClient());

    private DaemonServices Services(FakeEnv env, ServiceGraph graph)
    {
        var services = new DaemonServices(new Lazy<ServiceGraph>(graph), env, line =>
        {
            lock (_log)
            {
                _log.Add(line);
            }
        }, tmuxTmpdir: Path.Combine(_sandbox.Home, "tmuxtmp"));
        services.RegisterRoutes(new Routes());
        return services;
    }

    /// <summary>A hierarchy dir with an idle member dir no team owns, the shape the incident swept. Returns the member dir.</summary>
    private static string IdleUnownedMember(string dir)
    {
        var member = Path.Combine(dir, "tmp", "goneteam", "ghost");
        Directory.CreateDirectory(member);
        var file = Path.Combine(member, "scratch.bin");
        File.WriteAllText(file, "x");
        var old = DateTime.UtcNow.AddDays(-10);
        File.SetLastWriteTimeUtc(file, old);
        Directory.SetLastWriteTimeUtc(member, old);
        return member;
    }

    private bool Logged(string prefix)
    {
        lock (_log)
        {
            return _log.Any(l => l.StartsWith(prefix, StringComparison.Ordinal));
        }
    }

    private void KnownDirs(params string[] dirs)
    {
        foreach (var dir in dirs)
        {
            Row("INSERT OR REPLACE INTO meta(key, value) VALUES (?1, '2026-11-02T00:00:00.000Z')", AhPaths.KnownDirKeyPrefix + dir);
        }
    }

    private static string Repo(Sandbox where, string name)
    {
        var dir = Path.Combine(where.Root, name, ".claude", "ah2");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private void Registration(ServiceGraph graph, string session, string team, string dir)
    {
        var teamFile = dir + "/teams/" + team + ".json";
        TestPlant.Team(graph.Roster, graph.Writer, teamFile, new JsonObject { ["version"] = 1, ["team_id"] = team, ["orchestrator"] = new JsonObject { ["pid"] = Environment.ProcessId, ["session_id"] = session, ["address"] = "orchestrator@" + team }, ["members"] = new JsonArray() });
        var box = Path.Combine(_sandbox.Root, "boxes", team);
        Directory.CreateDirectory(box);
        Assert.True(graph.Mailbox.WriteRegistration(new RegistrationWrite(session, [new BoxRef("orchestrator@" + team, box)], teamFile, "claude", Environment.ProcessId)));
    }

    [Fact]
    public void ATestModeDaemonsSweepLeavesAnOutsideDirIntactAndSweepsTheSameShapeInsideItsRoot()
    {
        var env = Env(testMode: true);
        var graph = Graph(env);
        var outside = Repo(_elsewhere, "real");
        var inside = Repo(_sandbox, "probe");
        var outsideMember = IdleUnownedMember(outside);
        var insideMember = IdleUnownedMember(inside);
        KnownDirs(outside, inside);
        var services = Services(env, graph);

        var dirs = services.HierarchyDirs();
        services.SweepScratchHomes(dirs);
        services.SweepScratchHomes(services.HierarchyDirs());

        Assert.True(Directory.Exists(outsideMember));
        Assert.False(Directory.Exists(insideMember));
        Assert.Equal([inside], dirs);
        Assert.Single(_log, l => l.Contains("outside the probe root dropped: " + outside, StringComparison.Ordinal));
        Assert.Single(_log, l => l == $"test mode: HOME={_sandbox.Home}, hierarchy dirs kept 1, dropped 1");
    }

    [Fact]
    public void OutsideTestModeTheSameDirIsStillListedAndNothingIsLogged()
    {
        var env = Env(testMode: false);
        var graph = Graph(env);
        var outside = Repo(_elsewhere, "real");
        KnownDirs(outside);

        var dirs = Services(env, graph).HierarchyDirs();

        Assert.Contains(outside, dirs);
        // the background files import takes the admitted dir into ah.db under state/; the sandbox is deleted only once it has
        var until = DateTime.UtcNow.AddSeconds(30);
        while (!Logged("files import " + outside) && DateTime.UtcNow < until)
        {
            Thread.Sleep(10);
        }

        Assert.True(Logged("files import " + outside));
        Assert.False(Logged("test mode:"));
    }

    [Fact]
    public void ADirInsideTheRootWhoseTeamHomeResolvesOutsideItIsDropped()
    {
        var env = Env(testMode: true);
        var graph = Graph(env);
        var main = Path.Combine(_elsewhere.Root, "main");
        var mainDir = Repo(_elsewhere, "main");
        var mainMember = IdleUnownedMember(mainDir);
        var tree = Path.Combine(_sandbox.Root, "tree");
        Directory.CreateDirectory(Path.Combine(tree, ".claude", "ah2"));
        var gitdir = Path.Combine(main, ".git", "worktrees", "tree");
        Directory.CreateDirectory(gitdir);
        File.WriteAllText(Path.Combine(tree, ".git"), "gitdir: " + gitdir + "\n");
        File.WriteAllText(Path.Combine(gitdir, "gitdir"), tree + "/.git\n");
        var treeDir = Path.Combine(tree, ".claude", "ah2");
        Assert.EndsWith("/main/.claude/ah2", graph.Roster.TeamHome(treeDir), StringComparison.Ordinal);
        KnownDirs(treeDir);
        var services = Services(env, graph);

        var dirs = services.HierarchyDirs();
        services.SweepScratchHomes(dirs);

        Assert.Empty(dirs);
        Assert.True(Directory.Exists(mainMember));
    }

    [Fact]
    public void ACopiedHomeNamingARealDirNeitherSweepsItNorReadsItsTeamRecords()
    {
        var env = Env(testMode: true);
        var graph = Graph(env);
        var real = Repo(_elsewhere, "real");
        var inside = Repo(_sandbox, "probe");
        var realMember = IdleUnownedMember(real);
        Registration(graph, "real-session", "realteam", real);
        Registration(graph, "probe-session", "probeteam", inside);
        KnownDirs(real, inside);
        var services = Services(env, graph);
        var gate = new TestModeDirGate(env, graph.Io.Files, graph.Roster.TeamHome, line => _log.Add(line));

        services.SweepScratchHomes(services.HierarchyDirs());
        var stall = new StallHost(graph, _sandbox.AhHome, () => true, () => null, admitDirs: gate.Admit).Teams();
        var codex = new CodexDependencies(graph, _ => { }, gate.Admit).Teams();
        var watch = new WatchHostAdapter(graph, RosterModule.CreateScreenRules(), graph.Io.Clock, WatchHostSeams.Unavailable, null, gate.Admit);
        var live = watch.LiveHierarchyDirs();

        Assert.True(Directory.Exists(realMember));
        Assert.DoesNotContain(services.HierarchyDirs(), d => d == real);
        Assert.Equal(["probeteam"], stall.Select(t => t.Name));
        Assert.Equal(["probeteam"], codex.Select(t => t.Team));
        Assert.Equal([inside], live);
        Assert.Equal(["probe-session"], watch.LiveRegistrations().Select(r => r.SessionId));
    }

    [Fact]
    public void ADirInASiblingProbeWhoseNameExtendsTheRootsIsOutsideIt()
    {
        var env = Env(testMode: true);
        var graph = Graph(env);
        var sibling = _sandbox.Root + "x";
        var dir = Path.Combine(sibling, "repo", ".claude", "ah2");
        Directory.CreateDirectory(dir);
        try
        {
            var gate = new TestModeDirGate(env, graph.Io.Files, graph.Roster.TeamHome, _ => { });

            Assert.Empty(gate.Admit([dir]));
        }
        finally
        {
            Directory.Delete(sibling, recursive: true);
        }
    }

    [Fact]
    public void ALinkInsideTheRootThatPointsOutsideIsDropped()
    {
        var env = Env(testMode: true);
        var graph = Graph(env);
        var outside = Repo(_elsewhere, "real");
        var link = Path.Combine(_sandbox.Root, "link");
        Directory.CreateSymbolicLink(link, outside);
        var gate = new TestModeDirGate(env, graph.Io.Files, graph.Roster.TeamHome, _ => { });

        Assert.Empty(gate.Admit([link]));
    }

    [Fact]
    public void OutsideTestModeTheGatePassesTheListThroughUntouched()
    {
        var env = Env(testMode: false);
        var graph = Graph(env);
        IReadOnlyList<string> listed = ["/nowhere/.claude/ah2", "/nowhere/.claude/ah2"];

        Assert.Same(listed, new TestModeDirGate(env, graph.Io.Files, graph.Roster.TeamHome, _ => { }).Admit(listed));
    }

    private sealed record Run(int ExitCode, string Stdout, string Stderr);

    // --ah-home does not match, so a daemon that passes the refusal stops at the mismatch: no case here can start in the real home.
    private Run Serve(string home, bool testMode, params string[] args) => Ahd(home, testMode, ["serve", .. args]);

    private Run Ahd(string home, bool testMode, string[] args, string? versionOverride = null)
    {
        // a client that spawns the daemon respawns its own executable, so that one has to be ahd itself and not the dotnet host
        var start = new ProcessStartInfo(versionOverride is null ? "dotnet" : Path.Combine(AppContext.BaseDirectory, "ahd")) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (versionOverride is null)
        {
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "ahd.dll"));
        }

        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        start.Environment.Clear();
        start.Environment["HOME"] = home;
        start.Environment["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin";
        start.Environment["AH_HOME"] = _sandbox.AhHome;
        start.Environment["DOTNET_ROOT"] = Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? string.Empty;

        // a client whose check is taken away, in the proof that these tests can fail, spawns a daemon that must go away by itself
        start.Environment["AHD_IDLE_EXIT_MS"] = "1500";
        if (testMode)
        {
            start.Environment["AH_TEST"] = "1";
        }

        if (versionOverride is not null)
        {
            start.Environment["AHD_VERSION_OVERRIDE"] = versionOverride;
        }

        using var process = Process.Start(start)!;
        try
        {
            process.StandardInput.Close();
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            Assert.True(process.WaitForExit(60_000), "ahd serve did not exit");
            return new Run(process.ExitCode, stdout.Result, stderr.Result);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }

    private static string RealHome => Environment.GetEnvironmentVariable("AH_TEST_REAL_HOME") ?? throw new InvalidOperationException("the test process has no real-home marker");

    [Fact]
    public void ATestModeDaemonWhoseHomeIsTheRealHomeRefusesInOneLine()
    {
        var run = Serve(RealHome, testMode: true, "--ah-home", "/never/matches");

        Assert.Equal(1, run.ExitCode);
        var line = Assert.Single(run.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("HOME=" + RealHome, line, StringComparison.Ordinal);
        Assert.Contains("Set HOME to a temp dir, and copy the real home into it", line, StringComparison.Ordinal);
    }

    [Fact]
    public void ALinkToTheRealHomeIsTheRealHome()
    {
        var link = Path.Combine(_sandbox.Root, "homelink");
        Directory.CreateSymbolicLink(link, RealHome);
        try
        {
            var run = Serve(link, testMode: true, "--ah-home", "/never/matches");

            Assert.Contains("the real home; refusing to start", run.Stdout, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(link);
        }
    }

    [Fact]
    public void ATempHomeThatIsNotUnderTheProbeRootPassesTheRefusal()
    {
        var run = Serve(_elsewhere.Home, testMode: true, "--ah-home", "/never/matches");

        Assert.DoesNotContain("the real home", run.Stdout, StringComparison.Ordinal);
        Assert.Contains("does not match AH_HOME", run.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void ATestModeDaemonWithATempHomeStartsAndExitsOnIdle()
    {
        var run = Serve(_sandbox.Home, testMode: true, "--ah-home", _sandbox.AhHome, "--idle-exit-ms", "1500");

        Assert.Equal(0, run.ExitCode);
        Assert.DoesNotContain("refusing to start", run.Stdout, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- who may migrate the state database

    private string DatabaseOneSchemaBehind()
    {
        // Row opens with mayCreate, which migrates, so going back a schema has to be the last write.
        Row("INSERT INTO meta(key, value) VALUES('second-daemon-probe', 'kept')");
        var draft = Path.Combine(_sandbox.AhHome, "draft--impl--s--request.md");
        File.WriteAllText(draft, "a draft only its file holds");
        Row($"INSERT INTO msg_messages(pool_dir, id, type, \"to\", slug, state, draft_path) VALUES ('{_sandbox.AhHome}', 'd1', 'request', 'impl', 's', 'draft', '{draft}')");
        Row("DROP TRIGGER msg_messages_changed_insert");
        Row("DROP TRIGGER msg_messages_changed_update");
        Row("DROP INDEX msg_messages_changed");
        Row("ALTER TABLE msg_messages DROP COLUMN changed_seq"); // what V13Statements adds, which does not run twice
        Row($"UPDATE meta SET value = '{StateDbModule.SchemaVersion - 1}' WHERE key = 'schema_version'");
        return Path.Combine(_sandbox.AhHome, "state", "ah.db");
    }

    private List<string> MetaRows()
    {
        using var reader = StateDbModule.OpenReader(_sandbox.AhHome).Reader!;
        return [.. reader.Query("SELECT key, value FROM meta ORDER BY key").Select(static row => $"{row[0]}={row[1]}")];
    }

    private List<string> ImportedDrafts()
    {
        using var reader = StateDbModule.OpenReader(_sandbox.AhHome).Reader!;
        return [.. reader.Query("SELECT sealed_by FROM msg_bodies").Select(static row => (string)row[0]!)];
    }

    private string[] PreMigrateBackups()
    {
        var backups = Path.Combine(_sandbox.AhHome, "state", "backups");
        return Directory.Exists(backups) ? Directory.GetFiles(backups, "*pre-migrate-*.db") : [];
    }

    [Fact]
    public void ADaemonThatLosesTheSocketLeavesTheDatabaseOfTheLiveOneUntouched()
    {
        var database = DatabaseOneSchemaBehind();
        var rows = MetaRows();
        var written = File.GetLastWriteTimeUtc(database);
        var run = Path.Combine(_sandbox.AhHome, "run");
        Directory.CreateDirectory(run, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using var live = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified);
        live.Bind(new System.Net.Sockets.UnixDomainSocketEndPoint(Path.Combine(run, "ahd.sock")));
        live.Listen();

        var second = Serve(_sandbox.Home, testMode: true, "--ah-home", _sandbox.AhHome, "--idle-exit-ms", "1500");

        Assert.Equal(1, second.ExitCode);
        Assert.Contains("listen failed: listen EADDRINUSE", second.Stdout, StringComparison.Ordinal);
        Assert.Equal(rows, MetaRows());
        Assert.Equal(written, File.GetLastWriteTimeUtc(database));
        Assert.Empty(PreMigrateBackups());
        Assert.Empty(ImportedDrafts());
    }

    [Fact]
    public void ADaemonThatWinsTheSocketMigratesTheDatabaseWithItsBackup()
    {
        _ = DatabaseOneSchemaBehind();

        var swap = Serve(_sandbox.Home, testMode: true, "--ah-home", _sandbox.AhHome, "--idle-exit-ms", "1500");

        Assert.Equal(0, swap.ExitCode);
        Assert.Contains($"schema_version={StateDbModule.SchemaVersion}", MetaRows());
        Assert.Contains("second-daemon-probe=kept", MetaRows());
        Assert.Single(PreMigrateBackups());
        Assert.Equal(["import"], ImportedDrafts());
    }

    // ---------------------------------------------------------------- a new binary, an older schema, the old daemon still running

    private const string NewerBinary = "99.0.0";

    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _olderLog = new();

    private sealed class OlderDaemon(Process process) : IDisposable
    {
        internal bool Running => !process.HasExited;

        internal bool WaitForExit(int ms) => process.WaitForExit(ms);

        public void Dispose()
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }

            process.Dispose();
        }
    }

    /// <summary>A real daemon that reports an older version, as the one a plugin update left running does, with the database then taken a schema back.</summary>
    private OlderDaemon OlderDaemonWithDatabaseBehind()
    {
        Row("SELECT 1"); // the daemon below must not be the one that creates the schema
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { Path.Combine(AppContext.BaseDirectory, "ahd.dll"), "serve", "--ah-home", _sandbox.AhHome, "--idle-exit-ms", "120000" })
        {
            start.ArgumentList.Add(arg);
        }

        start.Environment.Clear();
        start.Environment["HOME"] = _sandbox.Home;
        start.Environment["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin";
        start.Environment["AH_HOME"] = _sandbox.AhHome;
        start.Environment["DOTNET_ROOT"] = Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? string.Empty;
        start.Environment["AH_TEST"] = "1";
        start.Environment["AHD_VERSION_OVERRIDE"] = "0.0.1";
        var process = Process.Start(start)!;
        process.OutputDataReceived += (_, e) => _olderLog.Enqueue(e.Data ?? string.Empty);
        process.ErrorDataReceived += (_, e) => _olderLog.Enqueue(e.Data ?? string.Empty);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        var info = Path.Combine(_sandbox.AhHome, "run", "ahd.json");
        var waited = Stopwatch.StartNew();
        while (!File.Exists(info) && waited.Elapsed < TimeSpan.FromSeconds(30) && !process.HasExited)
        {
            Thread.Sleep(50);
        }

        Assert.True(File.Exists(info), "the older daemon did not come up");
        _ = DatabaseOneSchemaBehind();
        Assert.Equal(StateDbModule.SchemaVersion - 1, StateDbModule.SchemaBehind(_sandbox.AhHome));
        return new OlderDaemon(process);
    }

    private string[] Ah(params string[] args)
    {
        var manifest = Path.Combine(_sandbox.PluginRoot, ".claude-plugin");
        Directory.CreateDirectory(manifest);
        File.WriteAllText(Path.Combine(manifest, "plugin.json"), "{\"name\":\"wrangl\",\"version\":\"0.0.0\"}");
        return ["--plugin-root", _sandbox.PluginRoot, "ah", .. args];
    }

    /// <summary>Registers this test process as a session, whose children the daemon lets drain it: the shape of a member's or an Orchestrator's own `ah` call.</summary>
    private void RegisterThisProcessAsASession()
    {
        var ps = new ProcessStartInfo("ps") { RedirectStandardOutput = true };
        foreach (var arg in new[] { "-o", "lstart=", "-p", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) })
        {
            ps.ArgumentList.Add(arg);
        }

        using var process = Process.Start(ps)!;
        var started = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        var teamFile = Path.Combine(_sandbox.Root, "hier", "teams", "tm.json");
        Directory.CreateDirectory(Path.GetDirectoryName(teamFile)!);
        File.WriteAllText(teamFile, "{\"team_id\":\"t-tm\",\"members\":[]}\n");
        var mailbox = Graph(Env(testMode: true)).Mailbox;
        Assert.True(mailbox.WriteRegistration(new Ah.Mesh.RegistrationWrite(
            "s-upgrade",
            [new Ah.Mesh.BoxRef("impl@tm", mailbox.BoxDirFor(teamFile, "impl"))],
            teamFile,
            "claude",
            Environment.ProcessId,
            PidStart: started,
            Bound: true)));
    }

    [Fact]
    public async Task ConcurrentVerbsReplaceTheOlderDaemonWaitForOneMigrationAndLoseNoWrite()
    {
        // the registration is a database row: written while the schema is current, before the database is put one behind
        RegisterThisProcessAsASession();
        using var older = OlderDaemonWithDatabaseBehind();
        Directory.CreateDirectory(Path.Combine(_sandbox.AhHome, "hierarchy", "home"));
        string[] titles = ["one", "two", "three"];

        var runs = await Task.WhenAll(titles.Select(title => Task.Run(() => Ahd(_sandbox.Home, testMode: true, Ah("work", "add", "must not be lost: " + title, "--cwd", _sandbox.Home), NewerBinary))));

        foreach (var run in runs)
        {
            var log = Path.Combine(_sandbox.AhHome, "logs", "ahd.log");
            Assert.True(run.ExitCode == 0, run.Stdout + run.Stderr + (File.Exists(log) ? File.ReadAllText(log) : "(no daemon log)") + "\nOLDER LOG:\n" + string.Join("\n", _olderLog.TakeLast(15)));
            Assert.DoesNotContain("older than this binary", run.Stdout, StringComparison.Ordinal);
        }

        Assert.True(older.WaitForExit(10_000), "the older daemon is still running");
        Assert.Null(StateDbModule.SchemaBehind(_sandbox.AhHome));
        Assert.Single(PreMigrateBackups());
        var listed = Ahd(_sandbox.Home, testMode: true, Ah("work", "list", "--cwd", _sandbox.Home), NewerBinary);
        foreach (var title in titles)
        {
            Assert.Contains("must not be lost: " + title, listed.Stdout, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("db", "list")]
    [InlineData("hook", "activity")]
    public void AHookAndAnAhDbVerbLeaveTheOlderDaemonAndTheDatabaseAsTheyAre(string verb, string sub)
    {
        using var older = OlderDaemonWithDatabaseBehind();
        var clock = Stopwatch.StartNew();

        var run = Ahd(_sandbox.Home, testMode: true, Ah(verb, sub));

        Assert.True(run.ExitCode == 0, run.Stdout + run.Stderr);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"it took {clock.Elapsed}");
        Assert.DoesNotContain("replacing the daemon", run.Stderr, StringComparison.Ordinal);
        Assert.True(older.Running);
        Assert.NotNull(StateDbModule.SchemaBehind(_sandbox.AhHome));
        Assert.Empty(PreMigrateBackups());
    }

    [Fact]
    public void ACurrentDatabaseNeverTouchesTheDaemon()
    {
        Row("SELECT 1");
        var stderr = new StringWriter();

        SchemaUpgradeWait.Run(Env(testMode: true), () => throw new InvalidOperationException("the daemon was asked for"), stderr);

        Assert.Empty(stderr.ToString());
    }

    [Fact]
    public void ADaemonSwitchedOffIsNotWaitedForEvenWithADatabaseBehind()
    {
        _ = DatabaseOneSchemaBehind();
        var vars = new Dictionary<string, string?> { ["AH_HOME"] = _sandbox.AhHome, ["HOME"] = _sandbox.Home, ["AH_TEST"] = "1", ["AH_DAEMON"] = "off" };
        var env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, vars, new PluginInfo(_sandbox.PluginRoot, "wrangl", "0.0.0", "1"));

        SchemaUpgradeWait.Run(env, () => throw new InvalidOperationException("the daemon was asked for"), TextWriter.Null);

        Assert.NotNull(StateDbModule.SchemaBehind(_sandbox.AhHome));
    }

    [Fact]
    public void ADatabaseThatNeverCatchesUpEndsTheWaitAtItsBudgetAndNothingMigrates()
    {
        _ = DatabaseOneSchemaBehind();
        var stderr = new StringWriter();
        var clock = Stopwatch.StartNew();

        SchemaUpgradeWait.Run(Env(testMode: true), () => new FakeDaemonClient(), stderr, TimeSpan.FromMilliseconds(300));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"it took {clock.Elapsed}");
        Assert.Contains("replacing the daemon", stderr.ToString(), StringComparison.Ordinal);
        Assert.NotNull(StateDbModule.SchemaBehind(_sandbox.AhHome));
        Assert.Empty(PreMigrateBackups());
    }

    /// <summary>Counts the schema wait's daemon calls; a call migrates the database as the replaced daemon would, so the wait ends at once.</summary>
    private sealed class MigratingDaemon(string ahHome) : IDaemonClient
    {
        public int Ensures { get; private set; }

        public int Drains { get; private set; }

        public Task<DaemonInfo?> DiscoverAsync(TimeSpan timeout, CancellationToken cancellationToken = default) => Task.FromResult<DaemonInfo?>(null);

        public Task<DaemonInfo?> EnsureDaemonAsync(TimeSpan budget, CancellationToken cancellationToken = default)
        {
            Ensures++;
            using (StateDbModule.Open(ahHome, mayCreate: true))
            {
            }

            return Task.FromResult<DaemonInfo?>(null);
        }

        public Task<DaemonReply> RequestAsync(DaemonRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DaemonReply(0, ReadOnlyMemory<byte>.Empty, "down"));

        public Task DrainAsync(DaemonInfo daemon, string reason, CancellationToken cancellationToken = default)
        {
            Drains++;
            return Task.CompletedTask;
        }

        public void RecordFallback(string verb, string reason)
        {
        }
    }

    private (int Exit, string Stderr) InProcessAh(MigratingDaemon daemon, params string[] args)
    {
        var env = Env(testMode: true);
        var graph = new Lazy<ServiceGraph>(() => ServiceGraph.Build(PlatformModule.CreateIo(null), env, new FakeRunner(), daemon));
        var context = new CliContext(env, new RecordingExec(), new FakeRunner(), "ah", _sandbox.Executable("install/ah"), _sandbox.Home, 501, null, graph);
        var stderr = new StringWriter();
        var exit = CliApp.Run(args, context, new StringWriter(), stderr);
        return (exit, stderr.ToString());
    }

    [Fact]
    public void ViewEventsWithADatabaseBehindSkipsTheSchemaWaitAndAnotherViewVerbStillWaits()
    {
        _ = DatabaseOneSchemaBehind();
        var poll = new MigratingDaemon(_sandbox.AhHome);

        var events = InProcessAh(poll, "view", "events", "--sub", "s1");

        Assert.Equal(1, events.Exit);
        Assert.Contains("ahd is not running", events.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("replacing the daemon", events.Stderr, StringComparison.Ordinal);
        Assert.Equal(0, poll.Ensures);
        Assert.Equal(0, poll.Drains);
        Assert.NotNull(StateDbModule.SchemaBehind(_sandbox.AhHome));

        var other = new MigratingDaemon(_sandbox.AhHome);
        var status = InProcessAh(other, "view", "status", "--dir", _sandbox.Home);

        Assert.Contains("replacing the daemon", status.Stderr, StringComparison.Ordinal);
        Assert.Equal(1, other.Ensures);
    }

    // ---------------------------------------------------------------- the scope check at start

    private const string Refused = "refusing to start. Rewrite or strip them in the copy.";

    private static readonly string[] Sources = ["team_file", "team_files", "box", "known-dirs", "plan_events", "status_entries", "msg_messages", "stored_registration"];

    public static TheoryData<string> EachSource()
    {
        var data = new TheoryData<string>();
        foreach (var source in Sources)
        {
            data.Add(source);
        }

        return data;
    }

    private void WriteRegistration(string session, JsonObject fields)
    {
        var directory = Path.Combine(_sandbox.AhHome, "registrations");
        Directory.CreateDirectory(directory);
        fields["session_id"] = session;
        fields["pid"] = 999_999;
        File.WriteAllBytes(Path.Combine(directory, session + ".json"), JsonText.ToUtf8(fields, 0));
    }

    private void Row(string sql, params object?[] args)
    {
        using (StateDbModule.Open(_sandbox.AhHome, mayCreate: true))
        {
        }

        Assert.True(StateDbModule.OpenDirect(_sandbox.AhHome).Write(tx =>
        {
            tx.Execute(sql, args);
            return true;
        }).Ok);
    }

    /// <summary>Puts <paramref name="path"/> in the home where <paramref name="source"/> keeps a directory or team file.</summary>
    private void Name(string source, string path)
    {
        switch (source)
        {
            case "team_file":
                WriteRegistration("s1", new JsonObject { ["team_file"] = path + "/teams/t.json" });
                break;
            case "team_files":
                WriteRegistration("s1", new JsonObject { ["team_files"] = new JsonObject { ["orchestrator@t"] = path + "/teams/t.json" } });
                break;
            case "box":
                WriteRegistration("s1", new JsonObject { ["boxes"] = new JsonObject { ["orchestrator@t"] = path + "/box" } });
                break;
            case "known-dirs":
                KnownDirs(path);
                break;
            case "plan_events":
                Row("INSERT INTO plan_events(hier_dir, stream, ts_ms, row) VALUES (?1, 'backlog', 1, '{}')", path);
                break;
            case "status_entries":
                Row("INSERT INTO status_entries(pool_dir, kind, key, json, changed_ms) VALUES (?1, 'meta', 'k', '{}', 1)", path);
                break;
            case "msg_messages":
                Row("INSERT INTO msg_messages(pool_dir, id, type, state) VALUES (?1, 'i', 'request', 'sealed')", path);
                break;
            case "stored_registration":
                Row(
                    "INSERT INTO sessions_unplaced(session_id, reason, started_at, last_seen_at, doc) VALUES ('s1', 'r', 1, 1, ?1)",
                    JsonText.Stringify(new JsonObject { ["session_id"] = "s1", ["address"] = "orchestrator@t", ["boxes"] = new JsonObject { ["orchestrator@t"] = path + "/box" } }, 0));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(source));
        }
    }

    [Theory]
    [MemberData(nameof(EachSource))]
    public void ACopiedHomeNamingARealPathRefusesAndLeavesItUntouched(string source)
    {
        var real = Repo(_elsewhere, "real");
        var member = IdleUnownedMember(real);
        var before = File.GetLastWriteTimeUtc(Path.Combine(member, "scratch.bin"));
        Name(source, real);

        var run = Serve(_sandbox.Home, testMode: true, "--ah-home", "/never/matches");

        Assert.Equal(1, run.ExitCode);
        var line = Assert.Single(run.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("1 path(s) outside the probe root", line, StringComparison.Ordinal);
        Assert.Contains(real, line, StringComparison.Ordinal);
        Assert.Contains(Refused, line, StringComparison.Ordinal);
        Assert.Equal(before, File.GetLastWriteTimeUtc(Path.Combine(member, "scratch.bin")));
    }

    [Theory]
    [MemberData(nameof(EachSource))]
    public void TheSameHomeWithItsPathRewrittenUnderTheRootStarts(string source)
    {
        Name(source, Repo(_sandbox, "copied"));

        var run = Serve(_sandbox.Home, testMode: true, "--ah-home", _sandbox.AhHome, "--idle-exit-ms", "1500");

        Assert.Equal(0, run.ExitCode);
        Assert.DoesNotContain("refusing to start", run.Stdout, StringComparison.Ordinal);
    }

    private static readonly Dictionary<string, string?> RealMultiplexers = new(StringComparer.Ordinal)
    {
        ["PATH"] = "/usr/bin:/bin",
        ["KEEP"] = "kept",
        ["TMUX"] = "/tmp/tmux-501/default,1,0",
        ["TMUX_PANE"] = "%1",
        ["TMUX_TMPDIR"] = "/tmp/real-tmux",
        ["HERDR_SOCKET_PATH"] = "/real/herdr.sock",
        ["HERDR_PANE_ID"] = "p-real",
        ["HERDR_SESSION"] = "real",
        ["XDG_CONFIG_HOME"] = "/real/config",
    };

    private string ProbeRoot => TestModeDirGate.RootOf(Env(testMode: true), PlatformModule.CreateIo(null).Files)!;

    [Fact]
    public void ATestDaemonsTmuxAndHerdrCallsResolveInsideItsRoot()
    {
        var bin = Path.Combine(_sandbox.Root, "fakebin");
        Directory.CreateDirectory(bin);
        var seen = Path.Combine(_sandbox.Root, "seen");
        foreach (var name in new[] { "tmux", "herdr" })
        {
            var fake = Path.Combine(bin, name);
            File.WriteAllText(fake, $"#!/bin/sh\necho \"{name} $* tmpdir=$TMUX_TMPDIR tmux=${{TMUX-unset}} pane=${{TMUX_PANE-unset}} socket=$HERDR_SOCKET_PATH herdrpane=${{HERDR_PANE_ID-unset}} session=${{HERDR_SESSION-unset}} config=$XDG_CONFIG_HOME keep=$KEEP\" >> \"{seen}\"\n");
            File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var current = new Dictionary<string, string?>(RealMultiplexers) { ["PATH"] = bin + ":/usr/bin:/bin" };
        var after = new System.Collections.Hashtable(current);
        var refusal = TestModeStart.PrivateMultiplexers(Env(testMode: true), PlatformModule.CreateIo(null).Files, current, (name, value) => after[name] = value);
        Assert.Null(refusal);

        // no stated environment: what every daemon child gets, here with the process's environment as it stands after the start
        var live = new Hashtable();
        foreach (DictionaryEntry entry in after)
        {
            if (entry.Value is not null)
            {
                live[entry.Key] = entry.Value;
            }
        }

        var runner = new IdentityScrubbingRunner(PlatformModule.CreateProcessRunner(), () => live);
        Assert.Equal(0, runner.Run(new ProcessRequest(["tmux", "paste-buffer", "-t", "%1"])).ExitCode);
        Assert.Equal(0, runner.Run(new ProcessRequest(["herdr", "agent", "prompt", "cd", "hi"])).ExitCode);

        var root = ProbeRoot;
        Assert.Equal(
            [
                $"tmux paste-buffer -t %1 tmpdir={root}/tmux tmux=unset pane=unset socket={root}/config/herdr/herdr.sock herdrpane=unset session=unset config={root}/config keep=kept",
                $"herdr agent prompt cd hi tmpdir={root}/tmux tmux=unset pane=unset socket={root}/config/herdr/herdr.sock herdrpane=unset session=unset config={root}/config keep=kept",
            ],
            File.ReadAllLines(seen));
        Assert.True(Directory.Exists(root + "/tmux"), "tmux goes back to /tmp when TMUX_TMPDIR is missing");
    }

    [Fact]
    public void OutsideTestModeTheMultiplexerEnvironmentIsUnchanged()
    {
        var changes = new List<string>();

        Assert.Null(TestModeStart.PrivateMultiplexers(Env(testMode: false), PlatformModule.CreateIo(null).Files, RealMultiplexers, (name, _) => changes.Add(name)));

        Assert.Empty(changes);
        Assert.False(Directory.Exists(Path.Combine(_sandbox.Root, "tmux")));
    }

    [Fact]
    public void AVariableAlreadyRightIsNotSetAgainAndNoOtherIsTouched()
    {
        var root = ProbeRoot;
        var current = new Dictionary<string, string?> { ["KEEP"] = "kept", ["TMUX_TMPDIR"] = root + "/tmux" };
        var changes = new List<string>();

        Assert.Null(TestModeStart.PrivateMultiplexers(Env(testMode: true), PlatformModule.CreateIo(null).Files, current, (name, _) => changes.Add(name)));

        Assert.Equal(["HERDR_SOCKET_PATH", "XDG_CONFIG_HOME"], changes.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void WhenThePrivateTmuxDirCannotBeMadeTheDaemonRefusesAndChangesNothing()
    {
        File.WriteAllText(Path.Combine(ProbeRoot, "tmux"), "in the way");
        var changes = new List<string>();

        var refusal = TestModeStart.PrivateMultiplexers(Env(testMode: true), PlatformModule.CreateIo(null).Files, RealMultiplexers, (name, _) => changes.Add(name));

        Assert.Contains("private tmux server", refusal, StringComparison.Ordinal);
        Assert.Empty(changes);
    }

    [Fact]
    public void ServeInTestModeMakesThePrivateTmuxDir()
    {
        var tmux = Path.Combine(ProbeRoot, "tmux");

        var run = Serve(_sandbox.Home, testMode: true, "--ah-home", "/never/matches");
        Assert.Contains("does not match AH_HOME", run.Stdout, StringComparison.Ordinal);
        Assert.True(Directory.Exists(tmux));
    }

    [Fact]
    public void ADamagedDatabaseRefuses()
    {
        var state = Path.Combine(_sandbox.AhHome, "state");
        Directory.CreateDirectory(state);
        File.WriteAllText(Path.Combine(state, "ah.db"), "this is not a database, and it is long enough to be read as a header by the library");

        var run = Serve(_sandbox.Home, testMode: true, "--ah-home", "/never/matches");

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("an unusable state database", run.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingDatabaseBesideABackupRefuses()
    {
        var backups = Path.Combine(_sandbox.AhHome, "state", "backups");
        Directory.CreateDirectory(backups);
        File.WriteAllText(Path.Combine(backups, "ah-20261102T000000Z-v10.db"), "x");
        File.Delete(Path.Combine(_sandbox.AhHome, "state", "ah.db"));

        var refusal = TestModeStart.Refusal(Env(testMode: true), PlatformModule.CreateIo(null).Files);

        Assert.Contains("state/backups could restore it", refusal, StringComparison.Ordinal);
    }

    private void AuthorityMarker(string dbId) =>
        File.WriteAllText(Path.Combine(_sandbox.AhHome, "state", "ah.db.authority.json"), $"{{\"db_id\":\"{dbId}\",\"domains\":{{}}}}");

    private void ABackup()
    {
        var backups = Path.Combine(_sandbox.AhHome, "state", "backups");
        Directory.CreateDirectory(backups);
        File.WriteAllText(Path.Combine(backups, "ah-20261102T000000Z-v10.db"), "x");
    }

    private string LiveDbId()
    {
        using var reader = StateDbModule.OpenReader(_sandbox.AhHome).Reader!;
        return (string)reader.Query("SELECT value FROM meta WHERE key = 'db_id'")[0][0]!;
    }

    [Fact]
    public void ADatabaseTheMarkerDoesNotNameBesideABackupRefuses()
    {
        AuthorityMarker("not-this-database");
        ABackup();

        var refusal = TestModeStart.Refusal(Env(testMode: true), PlatformModule.CreateIo(null).Files);

        Assert.Contains("gone or replaced", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void ADatabaseTheMarkerNamesBesideABackupStarts()
    {
        AuthorityMarker(LiveDbId());
        ABackup();

        Assert.Null(TestModeStart.Refusal(Env(testMode: true), PlatformModule.CreateIo(null).Files));
    }

    [Fact]
    public void ADatabaseTheMarkerDoesNotNameWithNoBackupHasNothingToRestoreAndStarts()
    {
        AuthorityMarker("not-this-database");

        Assert.Null(TestModeStart.Refusal(Env(testMode: true), PlatformModule.CreateIo(null).Files));
    }

    [Fact]
    public void AMissingDatabaseWithNoBackupIsACleanHome()
    {
        Directory.CreateDirectory(Path.Combine(_sandbox.AhHome, "state", "backups"));
        File.Delete(Path.Combine(_sandbox.AhHome, "state", "ah.db"));

        Assert.Null(TestModeStart.Refusal(Env(testMode: true), PlatformModule.CreateIo(null).Files));
    }

    [Fact]
    public void AReadableDatabaseMissingATableReadsAsNoRowsThere()
    {
        Row("DROP TABLE plan_events");

        Assert.Null(TestModeStart.Refusal(Env(testMode: true), PlatformModule.CreateIo(null).Files));
    }

    [Fact]
    public void TheRefusalCountsEveryPathAndNamesThree()
    {
        KnownDirs([.. Enumerable.Range(0, 5).Select(i => Repo(_elsewhere, "real" + i))]);

        var refusal = TestModeStart.Refusal(Env(testMode: true), PlatformModule.CreateIo(null).Files);

        Assert.Contains("5 path(s)", refusal, StringComparison.Ordinal);
        Assert.Equal(3, Enumerable.Range(0, 5).Count(i => refusal!.Contains("real" + i + "/", StringComparison.Ordinal)));
    }

    [Fact]
    public void OutsideTestModeTheScopeCheckAnswersNull()
    {
        KnownDirs(Repo(_elsewhere, "real"));

        Assert.Null(TestModeStart.Refusal(Env(testMode: false), PlatformModule.CreateIo(null).Files));
    }

    private sealed class InRealHome(FakeEnv inner) : IAhEnvironment
    {
        public string? GetVariable(string name) => inner.GetVariable(name);

        public bool TestMode => true;

        public bool TestModeInRealHome => true;

        public string? GetTestOnly(string name) => inner.GetTestOnly(name);

        public string HomeDirectory => inner.HomeDirectory;

        public string AhHome => inner.AhHome;

        public PluginInfo? Plugin => inner.Plugin;
    }

    [Fact]
    public void TheRealHomeRefusalIsTheOneTheClientAndTheDaemonShare()
    {
        var refusal = TestModeStart.Refusal(new InRealHome(Env(testMode: true)), PlatformModule.CreateIo(null).Files);

        Assert.Contains("the real home; refusing to start. Set HOME to a temp dir, and copy the real home into it", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void TheClientReportsTheRefusalAndSpawnsNothing()
    {
        var real = Repo(_elsewhere, "real");
        KnownDirs(real);

        var run = Ahd(_sandbox.Home, testMode: true, ["start"]);

        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains(Refused, run.Stdout + run.Stderr, StringComparison.Ordinal);
        Assert.Contains(real, run.Stdout + run.Stderr, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_sandbox.AhHome, "logs", "ahd.log")));
    }

    [Fact]
    public void OutsideTestModeTheRealHomeIsNotRefusedByThisRule()
    {
        var run = Serve(RealHome, testMode: false, "--ah-home", "/never/matches");

        Assert.DoesNotContain("the real home", run.Stdout + run.Stderr, StringComparison.Ordinal);
    }
}
