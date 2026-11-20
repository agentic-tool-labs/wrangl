using System.Collections;
using System.Runtime.CompilerServices;
using System.Diagnostics.CodeAnalysis;
using Ah.Abstractions;
using Ah.Cli.Delegation;
using Ah.Core.Json;
using Ah.Daemon;
using Ah.DaemonClient;
using Ah.Platform;

[assembly: SuppressMessage("Interoperability", "CA1416:Validate platform compatibility", Justification = "The executable runs on macOS and Linux only; Ah.Platform marks itself unsupported on Windows and nothing here is built for it.")]

namespace Ah.Cli.Composition;

/// <summary>
/// Builds the daemon client (the lifecycle verbs) and the daemon host (<c>serve</c>) from the platform services, once,
/// when the executable loads. Nothing here does I/O: the services are objects, and the plugin's location is resolved on
/// first use.
/// </summary>
internal static class DaemonComposition
{
    private static Lazy<ServiceGraph>? s_graph;

    /// <summary>The service graph, built on first use; the verbs that read or write the hierarchy take it from the context.</summary>
    internal static Lazy<ServiceGraph>? Graph => Volatile.Read(ref s_graph);

    [ModuleInitializer]
#pragma warning disable CA2255 // the executable composes its own services; no library code runs this
    internal static void Compose()
#pragma warning restore CA2255
    {
        var process = PlatformModule.CreateEnvironment(null);
        var env = new PluginAwareEnvironment(process, reportsBinaryVersion: false);
        var daemonEnv = new PluginAwareEnvironment(process, reportsBinaryVersion: true);
        var io = PlatformModule.CreateIo(null);

        var runner = PlatformModule.CreateProcessRunner();
        if (InDaemon())
        {
            runner = new IdentityScrubbingRunner(runner);
        }

        (io, runner) = ViewTimingComposition.Instrument(io, runner, InDaemon() && Ah.Watch.WatchModule.ViewTimingIsOn(process.GetVariable(Ah.Watch.WatchModule.ViewTimingSwitch)));
        var daemon = CreateDaemonClient(io, daemonEnv, runner);
        if (VerbOfThisProcess() is not ("serve" or "hook" or "heal" or ""))
        {
            // crash-resume §14.11 trigger 1: the write still fails and is not retried; the heal judges the daemon by itself. Only the
            // ah/ahd binary itself launches one: this runs as a module initializer, before Main, so the flag Main sets is read when
            // the write gives up, not here. A test host builds this composition too and never runs Main: its direct writes start nothing.
            Ah.StateDb.StateDbModule.OnDirectWriteHung(_ => Program.IsRealBinary && daemon.LaunchHeal("direct-write") is { } healPid
                ? $"ahd heal started (pid {healPid.ToString(System.Globalization.CultureInfo.InvariantCulture)}): it restarts ahd only if it is proven hung, which takes at least a minute; then run this again"
                : null);
        }

        var relay = InDaemon() ? new ReadModelWriteRelay() : null;
        var stateDb = new Lazy<Ah.StateDb.IStateDb>(() => OpenStateDb(env), LazyThreadSafetyMode.ExecutionAndPublication);
        var graph = new Lazy<ServiceGraph>(
            () => InDaemon()
                ? ServiceGraph.Build(io, env, runner, new InProcessDaemonClient(), inDaemon: true, fileCache: stateDb.Value.FileCache, writes: relay)
                : ServiceGraph.Build(io, env, runner, daemon),
            LazyThreadSafetyMode.ExecutionAndPublication);
        Volatile.Write(ref s_graph, graph);
        var state = StateComposition.Compose(graph);
        RosterComposition.Compose(graph, env);
        HomeTrustComposition.Compose(graph, env);

        var processes = PlatformModule.CreateProcessTable(runner);
        var services = new DaemonServices(
            graph,
            daemonEnv,
            message => Console.Out.Write($"{Ah.Core.Time.Timestamps.ToIsoString(io.Clock.UtcNow)} ahd {message}\n"),
            writeRelay: relay,
            stateDb: InDaemon() ? () => stateDb.Value : null);
        DaemonModule.Compose(
            new DaemonDependencies(io.Files, io.Writer, io.Clock, daemonEnv, io.HookErrors, Environment.ProcessId, StateComposition.ForRoute(state), PlatformModule.CreatePeerIdentity(),
                PlatformModule.CreateRejectionLog(io.Clock, Ah.Core.Text.AhPaths.RejectedLog(daemonEnv.AhHome)),
                pid => graph.Value.Mailbox.PrincipalOf(pid).Kind,
                ParentOf: pid => processes.AncestorsUntil(pid, static _ => true) is { Count: > 0 } chain ? chain[0] : null,
                UiStart: new UiStartSupport(
                    PlatformModule.CreateReapingDetachedSpawner(),
                    Environment.ProcessPath ?? string.Empty,
                    pid => processes.StartTime(pid),
                    ProcessEnvironment,
                    Ah.Core.Ui.SystemOpener.Name,
                    PlatformModule.SignalProcess))
            {
                HardExit = static () => System.Diagnostics.Process.GetCurrentProcess().Kill(),
                ThreadCpuNs = PlatformModule.ThreadCpuNs,
                Locks = io.Locks,
                StartRefusal = () => TestModeStart.Refusal(daemonEnv, io.Files) ?? TestModeStart.PrivateMultiplexers(daemonEnv, io.Files, ProcessEnvironment(), Environment.SetEnvironmentVariable),
                UiAnswer = UiAnswer(graph, services.HierarchyDirs),
                UiTeamClose = new UiTeamCloseSupport(
                    dir => Ah.Watch.WatchModule.DirOfId(dir, services.HierarchyDirs()) ?? services.HierarchyDirs().FirstOrDefault(known => known == dir),
                    input => Ah.Commands.CommandsModule.TeamClose(new RosterComposition(graph.Value, io.Clock, env), input),
                    input => Ah.Commands.CommandsModule.TeamRename(new RosterComposition(graph.Value, io.Clock, env), input)),
                HerdrAppChild = pid => Ah.Mesh.MeshModule.IsHerdrAppChild(processes, pid, p => SessionDirectory.ExecutableOf(runner, p)),
                UiRules = new UiRulesSupport(
                    team => new RulesUi(graph.Value, env.AhHome, services.HierarchyDirs, () => stateDb.Value, () => io.Clock.UtcNow).Read(team),
                    body => new RulesUi(graph.Value, env.AhHome, services.HierarchyDirs, () => stateDb.Value, () => io.Clock.UtcNow).Write(body)),
            },
            Modules.Registries,
            services.RegisterJobs,
            services.RegisterRoutes,
            services.Health,
            services.Stop);
    }

    /// <summary>
    /// The bridge's answer route: the shared answer core run in this process with the graph's hierarchy, its in-process
    /// <c>decisions.add</c> and its mailbox. The bridge auth the route has passed is the proof, so every answer is the user's, relayed by
    /// the UI (<c>recorded_by: ui</c>, <c>caller: web</c>); dirIds resolve among <paramref name="hierarchyDirs"/>, the dirs the
    /// <c>questions:</c> slices are built from. <paramref name="caller"/> names the relay in the stamp: <c>web</c> for the bridge,
    /// <c>herdr</c> for <c>ah msg decision ui-answer</c>.
    /// </summary>
    internal static UiAnswerSupport UiAnswer(Lazy<ServiceGraph> graph, Func<IReadOnlyList<string>> hierarchyDirs, string caller = "web") => new(
        dirId => Ah.Watch.WatchModule.DirOfId(dirId, hierarchyDirs()),
        input =>
        {
            string? Str(string key) => input.Get(key) is { IsText: true } v ? v.AsString() : null;
            var g = graph.Value;
            var dir = Str("dir")!;

            // the stored entry's id, the earlier one's on an unchanged repeat; a dropped event stores none
            string? eventId = null;
            string? Notify(string to, string eventName, string text)
            {
                var sent = g.Mailbox.Send(Ah.Mesh.MeshModule.EventRequest(to, "user", dir, dir, eventName, text), g.Io.Clock.UtcNow);
                if (sent.Failure is { } failure)
                {
                    return $"{failure.Code}: {failure.Message}";
                }

                // answer-status watches the Orchestrator's copy; the asker's copy (b19) must not replace its id
                if (to == Ah.Hierarchy.HierarchyModule.OrchestratorAddress(Str("team")))
                {
                    eventId = sent.Value?.Get("entry")?.Member("id") is { IsText: true } id ? id.AsString() : null;
                }

                return null;
            }

            JsonObject WithEventId(JsonObject answered)
            {
                if (eventId is not null)
                {
                    answered["event_id"] = eventId;
                }

                return answered;
            }

            var dismiss = Str("dismiss");
            if (Str("item") is { } item)
            {
                var pool = Ah.Hierarchy.HierarchyModule.ProgressPoolOf(g.Hierarchy.Messages.PoolContext(dir).MainHierarchyDirectory, dir);
                return WithEventId(dismiss is null
                    ? Ah.Hierarchy.HierarchyModule.ChecklistAnswer(g.Hierarchy.Progress, pool, Str("team")!, item, Str("since")!, Str("choice")!, g.Io.Clock.UtcNow, Notify, team => g.Roster.ReadTeamRecord(dir, team))
                    : Ah.Hierarchy.HierarchyModule.ChecklistDismiss(g.Hierarchy.Progress, pool, Str("team")!, item, Str("since")!, dismiss, g.Io.Clock.UtcNow, Notify, team => g.Roster.ReadTeamRecord(dir, team)));
            }

            // a dismissed decision is answered Other with the reason, so an item it names goes to the lead (spec ah-ui-answer §6)
            var several = input.ContainsKey("choices");
            IReadOnlyList<string> choices = dismiss is not null ? ["other: " + Ah.Hierarchy.HierarchyModule.ChecklistDismissed + dismiss]
                : several ? [.. input["choices"].AsArray().Select(static c => c.AsString())] : [Str("choice")!];
            var res = g.Hierarchy.Decisions.AnswerParked(
                dir,
                Str("team")!,
                Str("run"),
                Str("id")!,
                choices,
                several,
                (_, _) => new JsonObject { ["answered_by"] = "user", ["recorded_by"] = "ui", ["caller"] = caller },
                (path, runId, decision) => g.Hierarchy.StateCalls.AppendDecision(path, runId, decision),
                dismiss is null ? Notify : (to, eventName, _) => Notify(to, eventName, $"{Str("id")} dismissed: {dismiss}"),
                g.Hierarchy.Progress,
                out var eventError) ?? new JsonObject { ["refused"] = new JsonObject { ["reason"] = "unproven", ["detail"] = "the answer was not proven" } };
            if (eventError is not null)
            {
                res["event_error"] = eventError;
            }

            return WithEventId(res);
        },
        (team, eventId) => Ah.Mesh.MeshModule.AnswerSeen(graph.Value.Mailbox, team, eventId),
        (dir, query) =>
        {
            var page = Ah.Hierarchy.HierarchyModule.QaHistoryPage(Ah.Hierarchy.HierarchyModule.QaThreads(graph.Value.Hierarchy.Decisions.QaLogRows(dir)), query);
            return query.Id is null ? Ah.Hierarchy.HierarchyModule.QaHistoryJson(page, dir, heads: true) : Ah.Hierarchy.HierarchyModule.QaHistoryJson(page.Threads, dir);
        });

    /// <summary>The daemon's own environment, handed to the holder and the opener it starts for the band button.</summary>
    private static Dictionary<string, string?> ProcessEnvironment()
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            environment[(string)entry.Key] = entry.Value as string;
        }

        return environment;
    }

    /// <summary>
    /// The daemon's content cache over the state database. A database that cannot be used (library missing or too old, damaged, newer
    /// schema) is logged once and the cache then does nothing, so every file is read from disk as it is without one.
    /// </summary>
    private static Ah.StateDb.IStateDb OpenStateDb(PluginAwareEnvironment env)
    {
        static void Log(string line) => Console.Out.Write($"{DateTime.UtcNow:O} ahd {line}\n");
        var recovery = Ah.StateDb.StateDbModule.RecoverStore(env.AhHome, DateTimeOffset.UtcNow);
        if (recovery.Outcome != Ah.StateDb.RecoveryOutcome.NotNeeded)
        {
            Log("state database recovery: " + recovery.Outcome + ": " + recovery.Detail);
        }

        var db = Ah.StateDb.StateDbModule.Open(env.AhHome, mayCreate: true, Log);
        if (db.Status.Availability != Ah.StateDb.StateDbAvailability.Ready)
        {
            Log("state database: " + db.Status.Detail);
        }
        else
        {
            Ah.StateDb.StateDbModule.ImportMsgDrafts(env.AhHome, Log);
        }

        return db;
    }

    /// <summary>The daemon client over the platform services; the hooks' state writer shares the construction.</summary>
    internal static IDaemonClient CreateDaemonClient(PlatformIo io, IAhEnvironment daemonEnv, IProcessRunner runner, bool oneStrike = false) =>
        DaemonClientModule.Compose(new DaemonClientDependencies(
            io.Files,
            io.Writer,
            io.Appender,
            io.Locks,
            PlatformModule.CreateUnixHttpTransport(BuildInfo.Version),
            PlatformModule.CreateDetachedSpawner(),
            runner,
            io.Processes,
            io.Clock,
            daemonEnv,
            SnapshotEnvironment(),
            SelfExecutable(),
            () => daemonEnv.Plugin?.Root is { Length: > 0 } root && Ah.Cli.Delegation.PluginRootOption.Problem(root, BuildInfo.PluginName) is null ? root : null)
            {
                StartRefusal = () => TestModeStart.Refusal(daemonEnv, io.Files),
                StartTime = pid => PlatformModule.CreateProcessTable(runner).StartTime(pid),
                OneStrike = oneStrike,
                CallerIsUser = CallerIsUser,
                Signal = PlatformModule.SignalProcess,
                HealJournal = static row => StallStore.Journal((Graph ?? throw new InvalidOperationException("the services have not been composed")).Value, row),
            },
            (path, _) => Task.FromResult(Graph is { } graph ? Verbs.AhdCtl.AhdCtlVerb.InspectFiles(graph.Value, path, null, null) : null));

    /// <summary>
    /// Whether the process running this verb is the user: the caller's principal, which the admin grant also makes the user. Null
    /// when it cannot be read (a hung daemon may hold the database): the caller is then not taken for the user, whatever the process
    /// table says or fails to say, and <c>ahd heal --force</c> is refused until it can be read.
    /// </summary>
    internal static bool? CallerIsUser() => CallerIsUser(static () => Graph?.Value.Mailbox.PrincipalOf(Environment.ProcessId).Kind);

    internal static bool? CallerIsUser(Func<PrincipalKind?> principal)
    {
        try
        {
            return principal() is { } kind ? kind == PrincipalKind.User : null;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>
    /// The verb this process runs, after any <c>--plugin-root</c> option and a leading <c>ah</c>; empty when there is none. A direct
    /// write starts the heal only from an ordinary command: never from the daemon, a hook, or the heal itself.
    /// </summary>
    private static string VerbOfThisProcess()
    {
        var argv = Ah.Cli.Delegation.PluginRootOption.Split(Environment.GetCommandLineArgs().Skip(1).ToArray()).Remaining;
        var at = argv.Length > 1 && argv[0] is "ah" or "ahd" ? 1 : 0;
        return argv.Length > at ? argv[at] : string.Empty;
    }

    /// <summary>Whether this process is the daemon (<c>ahd serve</c>): its state writes go to its own engine, never to its own socket.</summary>
    private static bool InDaemon() =>
        Ah.Cli.Delegation.PluginRootOption.Split(Environment.GetCommandLineArgs().Skip(1).ToArray()).Remaining is { Length: > 0 } argv && argv[0] == "serve";

    private static Dictionary<string, string?> SnapshotEnvironment()
    {
        var all = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (DictionaryEntry e in Environment.GetEnvironmentVariables())
        {
            all[(string)e.Key] = (string?)e.Value;
        }

        return all;
    }

    /// <summary>This binary's real path: what a native spawn runs as <c>serve</c>, so a replaced symlink cannot change it.</summary>
    private static string? SelfExecutable()
    {
        var path = SpawnTarget(Environment.ProcessPath, Environment.GetCommandLineArgs()[0], File.Exists);
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        try
        {
            return File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName ?? path;
        }
        catch (IOException)
        {
            return path;
        }
    }

    /// <summary>
    /// Under <c>dotnet ahd.dll</c> the process is the dotnet host, and <c>dotnet serve</c> is no daemon: the spawn runs the apphost a
    /// plain build writes beside <paramref name="entry"/> instead, when it is there. Any other process path is returned as it is.
    /// </summary>
    internal static string? SpawnTarget(string? processPath, string entry, Func<string, bool> exists) =>
        processPath is not null && Path.GetFileNameWithoutExtension(processPath) == "dotnet"
            && Path.ChangeExtension(entry, null) is { } host && host != entry && exists(host)
            ? host
            : processPath;

    /// <summary>
    /// The process environment, with the plugin's identity resolved the first time it is asked for. The daemon and its client
    /// are this binary, so they report the binary's own version and contract even when no plugin directory is found
    /// (<paramref name="reportsBinaryVersion"/>, root empty then); every other consumer sees no plugin in that case, so
    /// code that reads files from the plugin refuses instead of reading from an empty path.
    /// </summary>
    internal sealed class PluginAwareEnvironment(IAhEnvironment inner, bool reportsBinaryVersion) : IAhEnvironment, Ah.Platform.IPinnedAhHome
    {
        private readonly Lazy<PluginInfo?> _plugin = new(() => Resolve(inner, reportsBinaryVersion));

        public string? GetVariable(string name) => inner.GetVariable(name);

        public bool TestMode => inner.TestMode;

        public bool TestModeInRealHome => inner.TestModeInRealHome;

        public bool TestLimiterOff => inner.TestLimiterOff;

        public string? GetTestOnly(string name) => inner.GetTestOnly(name);

        public string HomeDirectory => inner.HomeDirectory;

        public string AhHome => inner.AhHome;

        public string? ResolvedAhHome => (inner as Ah.Platform.IPinnedAhHome)?.ResolvedAhHome;

        public PluginInfo? Plugin => _plugin.Value;

        private static PluginInfo? Resolve(IAhEnvironment env, bool reportsBinaryVersion)
        {
            var found = PluginResolver.Resolve(env, BuildInfo.PluginName, PluginResolver.StampRoot(env, BuildInfo.PluginName), Ah.Cli.Delegation.PluginRootOption.Requested);
            if (found is null && !reportsBinaryVersion)
            {
                return null;
            }

            return new PluginInfo(found?.Root ?? string.Empty, BuildInfo.PluginName, BuildInfo.Version, BuildInfo.Contract);
        }
    }

    /// <summary>The daemon client of the daemon itself: no daemon is ever found, so state writes run in its own process.</summary>
    internal sealed class InProcessDaemonClient : IDaemonClient
    {
        public Task<DaemonInfo?> DiscoverAsync(TimeSpan timeout, CancellationToken cancellationToken = default) =>
            Task.FromResult<DaemonInfo?>(null);

        public Task<DaemonInfo?> EnsureDaemonAsync(TimeSpan budget, CancellationToken cancellationToken = default) =>
            Task.FromResult<DaemonInfo?>(null);

        public Task<DaemonReply> RequestAsync(DaemonRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("the daemon does not call itself");

        public Task DrainAsync(DaemonInfo daemon, string reason, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void RecordFallback(string verb, string reason)
        {
            // the daemon is the daemon: it has no files fallback to record
        }
    }
}
