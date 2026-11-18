using Ah.Codex;
using Ah.Config;
using System.Globalization;
using Ah.Abstractions;
using Ah.Commands;
using Ah.Core.Json;
using Ah.Hierarchy;
using Ah.Hierarchy.StateCalls;
using Ah.Launch;
using Ah.Mesh;
using Ah.Platform;
using Ah.Roster;

namespace Ah.Cli.Composition;

/// <summary>
/// The launch layer's teardown reads and closes, built over the multiplexer and herdr runners, the team row store and the
/// service graph. Nothing here starts a process until a verb asks for a live read.
/// </summary>
internal static class LaunchComposition
{
    /// <summary>The environment this process runs in, which a headless member inherits.</summary>
    private static Dictionary<string, string?> ProcessEnvironment()
    {
        var process = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry entry in System.Environment.GetEnvironmentVariables())
        {
            process[(string)entry.Key] = entry.Value as string;
        }

        return process;
    }

    internal static LaunchServices Create(ServiceGraph graph)
    {
        var environment = graph.Environment;
        var files = graph.Io.Files;
        var clock = graph.Io.Clock;
        var guard = new MuxGuard(
            environment,
            files,
            graph.Io.Appender,
            System.Environment.ProcessId,
            ReportError: message => System.Console.Error.WriteLine(message),
            Terminate: pid => LaunchModule.IsSignalablePid(pid)
                && graph.Runner.Run(new ProcessRequest(
                    ["/bin/kill", "-TERM", pid.ToString(CultureInfo.InvariantCulture)],
                    Timeout: TimeSpan.FromSeconds(5))).ExitCode == 0);
        var mux = new MuxRunner(graph.Runner, guard);
        var herdr = new Herdr(mux, environment, files);
        var shell = new ShellRunner(graph.Runner, guard);
        var sleeper = new ThreadSleeper();
        var appserver = new AppserverProcesses(
            graph.Runner,
            graph.Io.Processes,
            environment,
            files,
            graph.Io.Writer,
            PlatformModule.CreateDetachedSpawner(),
            PlatformModule.CurrentUid());
        var registry = new ConfigMemberRegistry(graph.Config, files, environment);
        var harnesses = new KindHarnesses(registry.KindDefault);
        var screens = RosterModule.CreateScreenRules();
        var reading = new ScreenReading(herdr, environment, graph.Config, screens, harnesses);
        var trust = RosterModule.CreateCodexTrust(files, environment, cwd => graph.Config.CheckoutRoot(cwd));
        var rules = RosterModule.CreateMemberRules(registry);
        var layout = new LayoutRules();
        var topology = new HerdrTopology(herdr);
        var transports = new MuxTransports(environment, mux);
        var shapes = new SpawnShapes(
            environment,
            files,
            graph.Runner,
            graph.Config,
            graph.Roster,
            rules,
            registry,
            harnesses,
            graph.Hierarchy.Messages,
            new ModelTiers(graph.Config),
            graph.HierarchyPolicy)
        {
            LaunchSettings = new MemberLaunchSettingsComposition(environment, files, graph.Io.Writer, graph.Config, graph.Roster, clock).Prepare,
        };
        // the launcher quotes the roster command into the shapes it builds, which needs the plugin's location: built on first use
        var launcher = new Lazy<IMemberLauncher>(
            () => new MemberLauncher(
                environment,
                files,
                graph.Io.Writer,
                clock,
                herdr,
                mux,
                shell,
                graph.Config,
                screens,
                reading,
                harnesses,
                registry,
                trust,
                shapes,
                layout,
                sleeper,
                new ActivityRecords(graph),
                appserver,
                graph.Config.CliCommand(CliTool.Roster, alwaysQuote: true),
                Spawner: PlatformModule.CreateDetachedSpawner(),
                Processes: PlatformModule.CreateProcessTable(graph.Runner),
                ProcessEnvironment: ProcessEnvironment(),
                // test mode: fake panes never register, so the wait would add its full length to every spawn
                Registered: environment.TestMode
                    ? null
                    : (name, team) => graph.Hierarchy.Core.Liveness is not { } liveness || liveness.ForMember(name, team) is { Live: true },
                HomeDocs: graph.HomeDocs),
            LazyThreadSafetyMode.ExecutionAndPublication);
        var paste = new PasteService(
            environment,
            files,
            graph.Io.Writer,
            clock,
            graph.Runner,
            graph.Config,
            screens,
            harnesses,
            graph.Mailbox,
            PlatformModule.CreateLockProvider(clock, graph.Io.Random, PasteService.LockStaleAfter, graph.Io.HookErrors),
            sleeper,
            System.Environment.ProcessId,
            herdr);
        var links = new MeshLinks(graph, environment, clock);
        var teamRows = new TeamRows(graph, links);
        var snapshots = new SnapshotBuilder(
            files, graph.Runner, clock, graph.Config, graph.Roster, graph.Hierarchy.Roster, graph.Hierarchy.Core, graph.Mailbox, graph.Hierarchy.StateCalls, graph.Io.HookErrors);
        var teardown = new Teardown(
            environment,
            files,
            graph.Config,
            graph.Roster,
            registry,
            graph.Hierarchy.Roster,
            graph.Hierarchy.Messages,
            herdr,
            topology,
            mux,
            clock,
            sleeper,
            appserver,
            teamRows,
            cwd => graph.Config.CheckoutRoot(cwd))
        {
            Registration = graph.Mailbox.ReadRegistration,
            SessionOf = snapshots.SessionIdOf,
        };
        var snapshotRules = new SnapshotRules(files, registry.KindDefault);
        return new LaunchServices(
            launcher, paste, shell, shapes, teardown, mux, herdr, layout, transports, topology, rules, registry, harnesses, snapshots, snapshotRules,
            teamRows, links, new StreamRules(files, new DaemonServices.OpenExchangeSource(graph)), graph.Runner,
            PlatformModule.CreateLockProvider(clock, graph.Io.Random, TimeSpan.FromSeconds(300), graph.Io.HookErrors))
        {
            PaneState = PaneStateReader.Over(mux, files, environment, graph.Runner, herdr),
            PaneStateOver = teamHerdr => PaneStateReader.Over(mux, files, environment, graph.Runner, teamHerdr),
            Trust = trust,
        };
    }

    /// <summary>The models of a kind that the user's configuration declares at a tier, in declaration order.</summary>
    private sealed class ModelTiers(Ah.Config.IConfigCore config) : IModelTiers
    {
        public IReadOnlyList<string> DeclaredModelsAt(string kind, IReadOnlyList<string> tiers)
        {
            var declared = config.DeclaredModelTiers();
            if (declared.Get("tiers") is not { IsMap: true } all || all.AsObject().Get(kind) is not { IsMap: true } byModel)
            {
                return [];
            }

            var models = new List<string>();
            var map = byModel.AsObject();
            foreach (var model in map.Keys)
            {
                if (map[model] is { IsText: true } tier && tiers.Contains(tier.AsString()))
                {
                    models.Add(model);
                }
            }

            return models;
        }
    }

    /// <summary>A member's idle and cleared activity, recorded through the hierarchy's routed activity writes.</summary>
    private sealed class ActivityRecords(ServiceGraph graph) : IActivityRecorder
    {
        public void RecordIdle(string dir, string subject) =>
            graph.Hierarchy.Status.RecordActivityRouted(dir, subject, new JsonObject { ["activity"] = "idle" });

        public void Clear(string dir, string subject) => graph.Hierarchy.Status.ClearActivityRouted(dir, subject);
    }

    private sealed class ThreadSleeper : ISleeper
    {
        public void Sleep(TimeSpan duration) => Thread.Sleep(duration);
    }

    /// <summary>
    /// A member's own Codex app-server: its private temp directory, the detached process in its socket directory, the spawn's
    /// conversation with it, and ending it. The server outlives this command and the daemon, so it is started in a session of its own.
    /// </summary>
    internal sealed class AppserverProcesses(
        IProcessRunner runner,
        IProcessInspector inspector,
        IAhEnvironment environment,
        IFileReader files,
        IRawFileWriter writer,
        IDetachedSpawner spawner,
        uint uid,
        int socketPolls = 100,
        int pollMilliseconds = 50,
        IReadOnlyDictionary<string, string?>? inheritedEnvironment = null) : IAppserverLauncher
    {

        public string? EnsureMemberTmp(string tmpDirectory) =>
            CodexModule.EnsureMemberTmp(tmpDirectory, environment.AhHome, uid, files, writer);

        public int? Start(string socket, IReadOnlyList<string> serverArgs, string startCwd)
        {
            var dir = Path.GetDirectoryName(socket)!;
            try
            {
                writer.Mkdir(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                if (files.Exists(socket))
                {
                    File.Delete(socket);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return null;
            }

            if (OnPath("codex") is not { } codex)
            {
                return null;
            }

            int pid;
            try
            {
                var process = new Dictionary<string, string?>(StringComparer.Ordinal);
                foreach (System.Collections.DictionaryEntry entry in System.Environment.GetEnvironmentVariables())
                {
                    process[(string)entry.Key] = entry.Value as string;
                }

                var serverEnvironment = CodexModule.ServerEnvironment(inheritedEnvironment ?? process);
                pid = spawner.SpawnDetached(new DetachedSpawnRequest([codex, .. serverArgs], startCwd, serverEnvironment, "/dev/null"));
            }
            catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return null;
            }

            for (var i = 0; i < socketPolls; i++)
            {
                if (files.Exists(socket))
                {
                    return pid;
                }

                Thread.Sleep(pollMilliseconds);
            }

            Terminate(pid, socket);
            return null;
        }

        public AppserverBringUp BringUp(string socket, JsonObject modes, string cwd)
        {
            var sandbox = modes.Get("sandbox") is { IsText: true } s ? s.AsString() : CodexModule.DefaultSandbox;
            var approval = modes.Get("approval") is { IsText: true } a ? a.AsString() : CodexModule.DefaultApproval;
            var result = CodexModule.BringUp(socket, new CodexModes(sandbox, approval), cwd);
            return new AppserverBringUp(result.Ok, result.ThreadId, result.Note);
        }

        public bool Terminate(int pid, string socket)
        {
            // never signal what is not a single process of ours: 0 is the caller's group, 1 init, a negative every process
            if (!LaunchModule.IsSignalablePid(pid))
            {
                return false;
            }

            // a pid can be reused: only the app-server this socket names is stopped
            if (!LaunchModule.IsAppserverOf(inspector.CommandLine(pid), socket))
            {
                return false;
            }

            var result = runner.Run(new ProcessRequest(
                ["/bin/kill", "-TERM", pid.ToString(CultureInfo.InvariantCulture)],
                Timeout: TimeSpan.FromSeconds(5)));
            return result.ExitCode == 0;
        }

        /// <summary>The first executable <paramref name="name"/> on the environment's PATH, or null.</summary>
        private string? OnPath(string name)
        {
            foreach (var dir in (environment.GetVariable("PATH") ?? string.Empty).Split(':', StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = Path.Combine(dir, name);
                if (files.Exists(candidate)
                    && (File.GetUnixFileMode(candidate) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0)
                {
                    return candidate;
                }
            }

            return null;
        }
    }

    /// <summary>The mesh's link revocation, over a gate built the first time a member's links end.</summary>
    private sealed class MeshLinks(ServiceGraph graph, IAhEnvironment environment, IClock clock) : IRosterLinks
    {
        private IMeshGate? _gate;

        public void RevokeLinksFor(string teamPath, IReadOnlyList<string>? names)
        {
            _gate ??= MeshModule.CreateMeshGate(
                graph.Mailbox,
                MeshModule.CreateMeshRule(graph.Links, environment),
                graph.Links,
                graph.Hierarchy,
                clock,
                graph.Writer,
                graph.Io.Files,
                graph.RunFacts);
            _gate.RevokeLinksFor(teamPath, names);
        }
    }

    /// <summary>
    /// Rewrites a team file's member rows under the team domain's digest, or removes the file when none remain. A member that leaves
    /// takes its links and its pane activity record with it, once the file is written.
    /// </summary>
    private sealed class TeamRows(ServiceGraph graph, IRosterLinks links) : ITeamRowStore
    {
        public TeamRowsUpdate UpdateRows(string dir, string path, Func<JsonObject, IReadOnlyList<JsonValue>?> pick, bool keepFile = false, JsonObject? journal = null)
        {
            IReadOnlyList<JsonValue>? picked = null;
            JsonObject? basis = null;
            _ = graph.Hierarchy.StateCalls.UpdateTeamFile(
                path,
                dir,
                doc =>
                {
                    picked = null;
                    basis = null;
                    if (!doc.IsMap)
                    {
                        return null;
                    }

                    picked = pick(doc.AsObject());
                    if (picked is null)
                    {
                        return null;
                    }

                    basis = doc.AsObject();
                    if (picked.Count == 0 && !keepFile)
                    {
                        return TeamFileOutcome.Clear();
                    }

                    var members = new JsonArray();
                    members.AddRange(picked);
                    var written = doc.AsObject().Clone();
                    written["members"] = members;
                    if (journal is not null)
                    {
                        written["parent_actions"] = LaunchModule.AppendedParentAction(doc, journal);
                    }

                    return TeamFileOutcome.Write(written);
                });
            if (picked is null || basis is null)
            {
                return new TeamRowsUpdate(false, null);
            }

            var kept = picked;
            var left = basis.Get("members") is { IsArray: true } all
                ? all.AsArray().Where(m => m.IsMap && !kept.Any(k => k.IsMap && ReferenceEquals(k.AsObject(), m.AsObject()))).ToList()
                : [];
            links.RevokeLinksFor(
                path,
                kept.Count == 0 ? null : left.Where(m => m.Member("name") is { IsTruthy: true }).Select(m => JsConvert.ToJsString(m.Member("name"))).ToList());
            foreach (var m in left)
            {
                if (m.Member("name") is { IsNull: false } name)
                {
                    graph.Hierarchy.Status.ClearActivityRouted(dir, $"pane-{JsConvert.ToJsString(name)}");
                }
            }

            return new TeamRowsUpdate(picked.Count == 0 && !keepFile, picked);
        }
    }
}

/// <summary>The launch layer's services, built together over one multiplexer guard and one process runner.</summary>
internal sealed class LaunchServices(
    Lazy<IMemberLauncher> launcher,
    IPaste paste,
    IShellRunner shell,
    ISpawnShapes shapes,
    ITeardown teardown,
    IMuxRunner mux,
    IHerdr herdr,
    ILayoutRules layout,
    IMuxTransports transports,
    IHerdrTopology topology,
    IMemberRules rules,
    IMemberRegistry registry,
    IKindHarnesses harnesses,
    ISnapshotBuilder snapshots,
    ISnapshotRules snapshotRules,
    ITeamRowStore teamRows,
    IRosterLinks links,
    IStreamRules streams,
    IProcessRunner processes,
    ILockProvider resumeLocks) : IRosterLaunch
{
    /// <summary>The member launcher; asking for it needs the plugin's location (the roster command it quotes).</summary>
    public IMemberLauncher Launcher => launcher.Value;

    public ICodexTrust Trust { get; init; } = null!;

    internal IPaste Paste => paste;

    internal IShellRunner Shell => shell;

    public ISpawnShapes Shapes => shapes;

    public ITeardown Teardown => teardown;

    internal IMuxRunner Mux => mux;

    public IHerdr Herdr => herdr;

    private readonly Dictionary<string, IHerdr> _byFlavour = new(StringComparer.Ordinal);

    /// <summary>
    /// ahd only: the herdr adapter for the flavour <paramref name="teamRecord"/> stores (no <c>herdr_flavour</c>: tui). A session command
    /// uses <see cref="Herdr"/>, whose flavour is the env's. One instance per flavour.
    /// </summary>
    public IHerdr HerdrFor(Ah.Core.Json.JsonValue? teamRecord) => HerdrFor(Ah.Core.Mux.HerdrFlavour.OfTeam(teamRecord));

    /// <summary>The herdr adapter for <paramref name="flavour"/> (<see cref="Ah.Core.Mux.HerdrFlavour.Resolve"/>); null is <see cref="Herdr"/>.</summary>
    public IHerdr HerdrFor(string? flavour)
    {
        if (flavour is null)
        {
            return herdr;
        }

        lock (_byFlavour)
        {
            if (!_byFlavour.TryGetValue(flavour, out var found))
            {
                found = herdr is Ah.Launch.Herdr h ? h.ForFlavour(flavour) : herdr;
                _byFlavour[flavour] = found;
            }

            return found;
        }
    }

    /// <summary>Builds a pane reader over one herdr adapter; <see cref="PaneStateFor(string?)"/> uses it for a team whose flavour is not the env's.</summary>
    public Func<IHerdr, IPaneState>? PaneStateOver { get; init; }

    private readonly Dictionary<string, IPaneState> _paneStates = new(StringComparer.Ordinal);

    public IPaneState? PaneStateFor(Ah.Core.Json.JsonValue? teamRecord) => PaneStateFor(Ah.Core.Mux.HerdrFlavour.OfTeam(teamRecord));

    public IPaneState? PaneStateFor(string? flavour)
    {
        if (flavour is null || PaneStateOver is not { } over || flavour == herdr.Flavour)
        {
            return PaneState;
        }

        lock (_paneStates)
        {
            if (!_paneStates.TryGetValue(flavour, out var state))
            {
                state = over(HerdrFor(flavour));
                _paneStates[flavour] = state;
            }

            return state;
        }
    }

    public ILayoutRules Layout => layout;

    public IMuxTransports Transports => transports;

    public IHerdrTopology Topology => topology;

    public IMemberRules Rules => rules;

    public IMemberRegistry Registry => registry;

    public IKindHarnesses Harnesses => harnesses;

    public ISnapshotBuilder Snapshots => snapshots;

    public ISnapshotRules SnapshotRules => snapshotRules;

    public ITeamRowStore TeamRows => teamRows;

    public IRosterLinks Links => links;

    public IStreamRules Streams => streams;

    public IProcessRunner Processes => processes;

    public ILockProvider ResumeLocks => resumeLocks;

    public IPaneState? PaneState { get; set; }
}
