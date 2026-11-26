using Ah.Core.Files;
using Ah.Core.Json;
using System.Globalization;
using System.Text;
using Ah.Abstractions;
using Ah.Config;
using Ah.Hierarchy;
using Ah.Mesh;
using Ah.Platform;
using Ah.Roster;
using Ah.State;

namespace Ah.Cli.Composition;

/// <summary>
/// The product's services, wired once from the platform services and the daemon client: Config, Roster, Hierarchy and the
/// mailbox, with the state domains each one owns. The state writer they call is bound after the domains exist, because the
/// writer is built from the domains it routes to.
/// </summary>
internal sealed class ServiceGraph
{
    private readonly Lazy<LaunchServices> _launch;

    private ServiceGraph(
        PlatformIo io,
        IAhEnvironment environment,
        IProcessRunner runner,
        IHierarchyBootstrap bootstrap,
        IReadOnlyList<IStateDomain> domains,
        IConfigCore config,
        IRosterCore roster,
        HierarchyServices hierarchy,
        IMailbox mailbox,
        Links links,
        IStateWriter writer,
        IStateService state,
        ProcessFacts facts,
        Lazy<DateTimeOffset?> boot)
    {
        _boot = boot;
        Io = io;
        Environment = environment;
        Runner = runner;
        _launch = new Lazy<LaunchServices>(() => LaunchComposition.Create(this), LazyThreadSafetyMode.ExecutionAndPublication);
        Bootstrap = bootstrap;
        Domains = domains;
        Config = config;
        Roster = roster;
        Hierarchy = hierarchy;
        Mailbox = mailbox;
        Links = links;
        Writer = writer;
        State = state;
        Facts = facts;
        Checklists = new ChecklistMemo(() => Ah.StateDb.StateDbModule.DomainSeq(environment.AhHome, "backlog"), (dir, team) => HierarchyModule.ChecklistEntries(hierarchy.Progress, dir, team));
    }

    /// <summary>Every checklist fold of this graph, held per plan counter (b459 step 13): the watch tick, backlog health and the processes lane read through it.</summary>
    internal ChecklistMemo Checklists { get; }

    private LaneCommitMemo? _laneCommits;

    /// <summary>Each lane work tree's latest commit, held per stat of its HEAD and reflog (b459 step 19): the watch tick reads through it.</summary>
    internal LaneCommitMemo LaneCommits => LazyInitializer.EnsureInitialized(ref _laneCommits, () => new LaneCommitMemo(Io.Files));

    /// <summary>Each mailbox's brief addressees, held per box while its entries read the same (b459 step 20): the wake and watch reads go through it.</summary>
    internal BriefAddresseeMemo BriefAddressees { get; } = new();

    /// <summary>Whether each response has landed (an authored body, not a withdrawal), held per message-store version (b459 step 20).</summary>
    internal MsgsVersionMemo<bool?> Landed { get; } = new();

    /// <summary>Each session's dispatch peer records, held per peer-store version (b459 step 24). Callers must not change a record.</summary>
    internal MsgsVersionMemo<IReadOnlyList<Ah.Core.Json.JsonValue>> SessionPeers { get; } = new();

    /// <summary>Each request's front matter, held per message-store version (b459 step 24). Callers must not change it.</summary>
    internal MsgsVersionMemo<Ah.Core.Json.JsonObject?> FrontMatters { get; } = new();

    private readonly Lazy<DateTimeOffset?> _boot;

    /// <summary>When the machine last booted, read once; null when unreadable.</summary>
    internal DateTimeOffset? BootTime => _boot.Value;

    internal PlatformIo Io { get; }

    internal IAhEnvironment Environment { get; }

    internal IProcessRunner Runner { get; }

    /// <summary>The launch layer (member launcher, paste, shell, shapes, teardown), built the first time it is asked for.</summary>
    internal LaunchServices Launch => _launch.Value;

    internal IHierarchyBootstrap Bootstrap { get; }

    internal IReadOnlyList<IStateDomain> Domains { get; }

    internal IConfigCore Config { get; }

    internal IRosterCore Roster { get; }

    internal HierarchyServices Hierarchy { get; }

    private IRunFacts? _runFacts;

    /// <summary>The run facts a run-scoped link reads, the one reader for the send path, the link commands and the daemon's tick.</summary>
    internal IRunFacts RunFacts => _runFacts ??= new RunFactsReader(Hierarchy, Io.Files, Roster, Config);

    /// <summary>The daemon's bound on status refreshes; disposing it runs the refreshes still waiting. Null outside the daemon.</summary>
    internal IDisposable? StatusRefresh { get; init; }

    private Func<Ah.StateDb.StateDbReaderOpen>? PooledReader { get; init; }

    /// <summary>A reader of <paramref name="ahHome"/>'s database; the daemon's own home reads through its one read pool, anything else opens and closes as <see cref="Ah.StateDb.StateDbModule.OpenReader"/>.</summary>
    internal Ah.StateDb.StateDbReaderOpen OpenReader(string ahHome) =>
        PooledReader is { } pooled && ahHome == Environment.AhHome ? pooled() : Ah.StateDb.StateDbModule.OpenReader(ahHome);

    /// <summary>Whether the write engine reads the mode row of the message domains, which a cutover needs before it may flip one.</summary>
    internal bool EngineReadsMessageModes { get; init; }

    /// <summary>Whether the readers and writers of messages follow the msgs mode row, which a msgs cutover needs before it may flip the domain.</summary>
    internal bool EngineReadsMsgsMode { get; init; }

    internal IMailbox Mailbox { get; }

    /// <summary>The decision of the Claude mod's mail poll over this graph's mailbox.</summary>
    internal IMailDue MailDue => MeshModule.CreateMailDue(
        Mailbox,
        MeshModule.CreateMailCore(Io.Clock),
        Io.Files,
        Environment,
        MeshModule.CreateMailboxHooks(Mailbox, MeshModule.CreateRelayObligations(Mailbox, Hierarchy), Hierarchy, Io.Clock));

    /// <summary>The doorbell and the pull behind <c>ah msg next</c> over this graph's mailbox.</summary>
    internal IMailDoorbell MailDoorbell => MeshModule.CreateMailDoorbell(
        Mailbox,
        MeshModule.CreateMailCore(Io.Clock),
        Io.Files,
        Environment,
        MeshModule.CreateMailboxHooks(Mailbox, MeshModule.CreateRelayObligations(Mailbox, Hierarchy), Hierarchy, Io.Clock));

    internal Links Links { get; }

    internal IStateWriter Writer { get; }

    /// <summary>Who the caller is, its registration and the ack that owes a relay.</summary>
    internal IMailCallers Callers { get; private init; } = null!;

    /// <summary>The mesh send: sender resolution, the policy, the mirror and the append.</summary>
    internal IMeshSender Sender { get; private init; } = null!;

    /// <summary>The read-only mailbox views the status verbs and the daemon's inspect endpoints print.</summary>
    internal IMailInspect Inspect { get; private init; } = null!;

    /// <summary>The one reader of the trust files.</summary>
    internal ITrustStore Trust { get; private init; } = null!;

    /// <summary>What a caller's cwd derives its hierarchy directories to, and which a request may claim.</summary>
    internal IHierarchyDirPolicy HierarchyPolicy { get; private init; } = null!;

    /// <summary>The daemon client the msg verbs ask first.</summary>
    internal IDaemonClient Daemon { get; private init; } = null!;

    /// <summary>This graph is the daemon's own (<c>inDaemon</c> at <see cref="Build"/>).</summary>
    internal bool InDaemon { get; private init; }

    /// <summary>The grants tables (per-dir-files-to-db S11e) the graph's reader and the grants domain share.</summary>
    internal IGrantSource Grants { get; private init; } = null!;

    /// <summary>The home documents (b397 group 2): session roles, ultra-gate choices, the permissions ledger.</summary>
    internal IHomeDocs HomeDocs { get; private init; } = null!;
    /// <summary>The bare-member recovery records (<c>recover_records</c>).</summary>
    internal IRecoverRecordStore RecoverRecords { get; private init; } = null!;

    internal IStateService State { get; }

    /// <summary>The in-process reads of <see cref="State"/>, for the daemon's view only.</summary>
    internal IInternalStateRead InternalStateRead => StateModule.InternalReads(State);

    /// <summary>This process's working directory and parent pid, as the libraries were built with them.</summary>
    internal ProcessFacts Facts { get; }

    /// <summary>
    /// The decision rule <paramref name="key"/> of the repo of hierarchy dir <paramref name="dir"/> (a worktree's stands for its main one, as the rules verbs'): an accepted,
    /// live row when there is one, else any decision row of that key, else null. A rules table that cannot be read answers null, so no line may name a rule.
    /// </summary>
    private static RuleRow? DecisionRuleOf(string ahHome, IConfigCore config, string dir, string key)
    {
        try
        {
            var main = Path.GetDirectoryName(Path.GetDirectoryName(dir)) is { Length: > 0 } root ? config.MainHierarchyDir(root) ?? dir : dir;
            var rows = RulesReads.With<IReadOnlyList<RuleRow>>(ahHome, [], query => RulesOfEngagement.Rows(query, main)).Where(r => r.Key == key && r.IsDecision).ToList();
            return rows.FirstOrDefault(r => !r.IsProposed && !r.Off) ?? rows.FirstOrDefault();
        }
        catch (RulesUnreadableException)
        {
            return null;
        }
    }

    // inDaemon: this graph is the daemon's own. The daemon has no socket to ask whether it serves mail claims, so the composition that
    // knows it is the daemon says so here.
    internal static ServiceGraph Build(PlatformIo io, IAhEnvironment environment, IProcessRunner runner, IDaemonClient daemon, bool inDaemon = false, IFileCache? fileCache = null, IStateWriteObserver? writes = null, ITeamAuthority? teamAuthority = null, ISessionAuthority? sessionAuthority = null, IDbOpRunner? opRunner = null, Ah.Core.Json.IMsgSource? msgSource = null)
    {
        ArgumentNullException.ThrowIfNull(io);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(daemon);

        // every reader of the graph lists the message pools through the scope a view build opens
        // per-dir-files-to-db S10: the asks logs are read from the asks table, outside the content cache, whose file stamps they lack
        // per-dir-files-to-db S11e: and the grants from the grants tables, their files shut once the database exists; the domain writes
        // through the same source, so reads and writes switch together
        var ahHome = environment.AhHome;
        var auditFile = Ah.Core.Text.AhPaths.UserEntry(environment.HomeDirectory, Ah.Core.Text.AhPaths.WorktreeAuditFile);
        var grantSource = Ah.StateDb.StateDbModule.CreateGrants(ahHome, auditFile);
        io = io with { Files = new RecordingFileReader(new AskLogFileReader(new ScopedListingFileReader(io.Files, fileCache), Ah.StateDb.StateDbModule.CreateAsks(ahHome), grantSource, () => Ah.StateDb.StateDbModule.DatabaseCreated(ahHome), auditFile)) };
        var writer = new LateStateWriter();
        var facts = new ProcessFacts(Directory.GetCurrentDirectory(), ParentPid(runner));

        // Outside test mode the trust files live under the password database's home, never under $HOME; a user the database does not
        // list trusts nothing.
        var uid = OperatingSystem.IsWindows() ? (uint?)null : PlatformModule.CurrentUid();
        var trust = ConfigModule.CreateTrustStore(
            io.Files,
            () => TrustHome(environment),
            () => uid);
        var policy = ConfigModule.CreateHierarchyDirPolicy(io.Files, environment, trust, () => uid);
        var pooled = inDaemon ? Ah.StateDb.StateDbModule.OpenPooledStores(environment.AhHome) : null;
        IRosterCore? roster = null;
        // b397 group 2: the session roles, the ultra-gate choices and the permissions ledger are meta rows
        var homeDocs = Ah.StateDb.StateDbModule.CreateHomeDocs(environment.AhHome);
        var config = ConfigModule.CreateCore(
            io.Files,
            environment,
            () => roster ?? throw new InvalidOperationException("the roster core is not built yet"),
            facts,
            trust,
            homeDocs);
        // A failed ps makes a caller anonymous, and the daemon refuses it. 40 concurrent ps reads took a median 4.6 s and at most 5.1 s here (b167); no hook budget binds the daemon.
        var processTable = PlatformModule.CreateProcessTable(runner, inDaemon ? TimeSpan.FromSeconds(8) : null);
        IMailbox? registrations = null;
        roster = RosterModule.CreateCore(
            io.Files,
            environment,
            config,
            io.Processes,
            processTable,
            new SessionProofs(
                pid => registrations is null ? null : MeshModule.CallerSessionIds(processTable, pid, registrations),
                session => registrations is null ? null : MeshModule.LiveRegistration(registrations, session))
            {
                RegisteredProcesses = () => registrations is null ? [] : MeshModule.RegisteredProcesses(registrations),
            },
            teamAuthority ?? pooled?.Teams ?? Ah.StateDb.StateDbModule.OpenTeamAuthority(environment.AhHome),
            // per-dir-files-to-db S11a-holds
            Ah.StateDb.StateDbModule.CreateHolds(environment.AhHome));
        var bootstrap = HierarchyModule.CreateBootstrap(io.Writer);
        var dbRunner = opRunner ?? Ah.StateDb.StateDbModule.CreateOpRunner(environment.AhHome);
        var modes = pooled?.Modes ?? Ah.StateDb.StateDbModule.CreateModeReader(environment.AhHome);
        var peerRoute = new PeerRecordsRoute(Ah.StateDb.StateDbModule.CreatePeerRecords(environment.AhHome), modes, () => Ah.StateDb.StateDbModule.DomainFlipped(environment.AhHome, "peer-records"));
        var msgsRoute = new MsgsRoute(msgSource ?? pooled?.Msgs ?? Ah.StateDb.StateDbModule.CreateMsgSource(environment.AhHome), modes, () => Ah.StateDb.StateDbModule.DomainFlipped(environment.AhHome, "msgs"));
        ServiceGraph? built = null;
        IReadOnlyList<PlanItem> BacklogChecklist(string dir) => built is { } graph ? SitrepRows.DirItems(graph, dir) : [];
        var sessions = sessionAuthority ?? pooled?.Sessions ?? Ah.StateDb.StateDbModule.OpenSessionAuthority(environment.AhHome);
        var hierarchy = HierarchyModule.Create(new HierarchyEnvironment(
            io.Files,
            environment,
            io.Clock,
            io.Random,
            io.Processes,
            config,
            roster,
            writer,
            facts,
            bootstrap,
            io.HookErrors,
            (dir, member) => built?.Launch.Teardown.MemberLiveness(dir, member).Get("liveness") is { IsText: true } word ? word.AsString() : null,
            peerRoute,
            msgsRoute,
            dir => Verbs.BacklogVerb.HealthFor(environment.AhHome, config, dir, io.Clock.UtcNow.ToUnixTimeMilliseconds(), evenEmpty: false, BacklogChecklist),
            dir => Verbs.BacklogVerb.PlanFor(environment.AhHome, config, dir),
            pooled?.StatusEntries ?? Ah.StateDb.StateDbModule.CreateStatusEntries(environment.AhHome),
            dir => Verbs.BacklogVerb.FactsFor(environment.AhHome, config, dir, BacklogChecklist),
            sessions,
            (dir, key) => DecisionRuleOf(environment.AhHome, config, dir, key),
            Ah.StateDb.StateDbModule.CreateSessionPids(environment.AhHome),
            Ah.StateDb.StateDbModule.CreateActivity(environment.AhHome),
            Ah.StateDb.StateDbModule.CreateGates(environment.AhHome),
            // per-dir-files-to-db S6
            Ah.StateDb.StateDbModule.CreateQaLog(environment.AhHome),
            // per-dir-files-to-db S7
            Ah.StateDb.StateDbModule.CreateRosterRecords(environment.AhHome),
            // per-dir-files-to-db S8
            Ah.StateDb.StateDbModule.CreateJobs(environment.AhHome),
            // per-dir-files-to-db S9
            Ah.StateDb.StateDbModule.CreateDecisions(environment.AhHome),
            // per-dir-files-to-db S11c
            ProgressLogs: Ah.StateDb.StateDbModule.CreateProgressLogs(environment.AhHome),
            // per-dir-files-to-db S11a-ckpt
            Checkpoints: Ah.StateDb.StateDbModule.CreateCheckpoints(environment.AhHome),
            // per-dir-files-to-db S11d
            ProgressDocs: Ah.StateDb.StateDbModule.CreateProgressDocs(environment.AhHome),
            // b397
            QaJobBodies: () => Ah.StateDb.StateDbModule.QaJobBodies(environment.AhHome),
            KnownDirRows: () => Ah.StateDb.StateDbModule.KnownDirs(environment.AhHome),
            // b397 group 2
            UsageRows: Ah.StateDb.StateDbModule.CreateUsageRows(environment.AhHome),
            DomainSeq: domain => Ah.StateDb.StateDbModule.DomainSeq(environment.AhHome, domain),
            // ah2-509
            ResourceRows: () => Ah.StateDb.StateDbModule.Resources(environment.AhHome),
            ProcessTable: processTable,
            // ah2-512
            Spend: (dir, team) => built is { } graph ? Verbs.SpendVerb.Board(graph, environment.AhHome, dir, team) : []));
        // the daemon's writes would each recompute the status document of their directory; this bounds that to one pass a second
        var statusRefresh = inDaemon ? HierarchyModule.CoalesceStatusRefresh(hierarchy, TimeSpan.FromSeconds(1)) : null;

        var daemonServesClaims = new Lazy<bool>(() => inDaemon || DaemonClaims.MayClaim(DaemonClaims.SupportOf(daemon)));
        var mailbox = MeshModule.CreateMailbox(io.Files, environment, io.Clock, io.Random, hierarchy, roster, writer, processTable, () => daemonServesClaims.Value, sessions, pooled?.Mail ?? Ah.StateDb.StateDbModule.OpenMailStore(environment.AhHome), homeDocs);
        registrations = mailbox;
        if (inDaemon)
        {
            // ah2-46: a grant left in admin.json by a build before the move is taken into the database when the daemon starts
            _ = MeshModule.ImportAdminGrant(homeDocs, environment.AhHome);
        }

        var boot = new Lazy<DateTimeOffset?>(() => PlatformModule.BootTime(runner), LazyThreadSafetyMode.ExecutionAndPublication);
        hierarchy.Core.Liveness = MeshModule.CreateMemberLiveness(mailbox, processTable, io.Processes, () => boot.Value);
        var links = new Links(Ah.StateDb.StateDbModule.CreateLinks(environment.AhHome), environment, roster, hierarchy.Core, hierarchy.Messages, io.Clock, io.Random);
        var rule = MeshModule.CreateMeshRule(links, environment);
        var relay = MeshModule.CreateRelayObligations(mailbox, hierarchy);

        List<IStateDomain> domains =
        [
            .. ConfigModule.CreateStateDomains(
                io.Files,
                environment,
                io.Clock,
                processTable,
                session => MeshModule.LiveRegistration(mailbox, session),
                grantSource,
                homeDocs),
            RosterModule.CreateStateDomain(roster, io.Clock, io.Random, hierarchy.StatusRefresher),
            .. hierarchy.StateDomains,
            mailbox.StateDomain(),
            links.StateDomain(),
            new DispatchesStateDomain(environment, () => built?.DispatchHost),
            new QueueStateDomain(() => built, io.Clock),
            new StallStateDomain(environment, io.Clock),
        ];

        var engine = StateModule.CreateEngine(io.Files, io.Writer, io.Appender, io.Locks, io.Clock, domains, System.Environment.ProcessId, dbRunner, modes, inDaemon ? "daemon" : "direct");
        Func<string, bool>? sandboxOnly = environment.TestMode
            ? path => Ah.Core.Text.RealPathResolver.IsUnderProbeSandbox(Ah.Core.Text.RealPathResolver.ThroughLongestExistingPrefix(io.Files, path))
            : null;
        writer.Target = StateModule.CreateClient(domains, daemon, io.HookErrors, engine, mayWriteHook: sandboxOnly, lockDirectory: Ah.StateDb.StateDbModule.StateLockDirectory(environment.AhHome));
        var state = StateModule.CreateService(domains, engine, io.Files, PlatformModule.CurrentUid(), policy, io.HookErrors, writes);
        built = new ServiceGraph(io, environment, runner, bootstrap, domains, config, roster, hierarchy, mailbox, links, writer, state, facts, boot)
        {
            StatusRefresh = statusRefresh,
            PooledReader = pooled?.OpenReader,
            EngineReadsMessageModes = true,
            EngineReadsMsgsMode = true,
            Callers = MeshModule.CreateMailCallers(mailbox, config, roster, hierarchy, relay, environment, processTable, BuildInfo.Version, io.Clock, io.HookErrors),
            Sender = MeshModule.CreateMeshSender(mailbox, rule, links, hierarchy, io.Clock, writer, io.Files, runFacts: new RunFactsReader(hierarchy, io.Files, roster, config)),
            Inspect = MeshModule.CreateMailInspect(mailbox, io.Files, environment, io.Clock),
            Daemon = daemon,
            InDaemon = inDaemon,
            Grants = grantSource,
            HomeDocs = homeDocs,
            RecoverRecords = Ah.StateDb.StateDbModule.CreateRecoverRecords(environment.AhHome),
            Trust = trust,
            HierarchyPolicy = policy,
        };
        return built;
    }

    private WatchHostAdapter? _dispatchHost;

    /// <summary>
    /// The watch host, the one cell that holds it: the daemon sets it once when it builds its watch, the <c>dispatches.open</c> op and
    /// the daemon read it. Null before then and in the CLI composition. A second set throws.
    /// </summary>
    internal WatchHostAdapter? DispatchHost
    {
        get => Volatile.Read(ref _dispatchHost);
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (Interlocked.CompareExchange(ref _dispatchHost, value, null) is not null)
            {
                throw new InvalidOperationException("the watch host is already set");
            }
        }
    }

    /// <summary>Whether a team member's process is up by the launch layer's one definition: live, or not determinable.</summary>
    internal bool MemberUp(string dir, JsonValue member)
    {
        var state = Launch.Teardown.MemberLiveness(dir, member);
        return state.Get("live") is { IsTruthy: true } || state.Get("indeterminate") is { IsTruthy: true };
    }

    /// <summary>This process's parent pid; the platform's <c>getppid</c> answers, and <c>ps</c> only when that gives nothing. 0 when it cannot be read.</summary>
    /// <summary>The trust files' directory, or empty when the passwd database has no home for the user (nothing is trusted).</summary>
    internal static string TrustHome(IAhEnvironment environment)
    {
        try
        {
            return ConfigModule.TrustHome(environment);
        }
        catch (InvalidOperationException)
        {
            return string.Empty;
        }
    }

    private static int ParentPid(IProcessRunner runner)
    {
        if (PlatformModule.ParentProcessId() is > 0 and var parent)
        {
            return parent;
        }

        var self = System.Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        var result = runner.Run(new ProcessRequest(["/bin/ps", "-o", "ppid=", "-p", self], Timeout: TimeSpan.FromSeconds(2)));
        var text = Encoding.UTF8.GetString(result.Stdout.Span).Trim();
        return result.ExitCode == 0 && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var pid) ? pid : 0;
    }

    private sealed class LateStateWriter : IStateWriter
    {
        internal IStateWriter? Target { get; set; }

        private IStateWriter Bound =>
            Target ?? throw new InvalidOperationException("the state writer is used before the services are composed");

        public StateResult Invoke(string domain, string op, ReadOnlyMemory<byte> argsJson, RequestContext context, StateCallOptions? options = null) =>
            Bound.Invoke(domain, op, argsJson, context, options);

        public bool IsHomeScoped(string domain) => Bound.IsHomeScoped(domain);

        public bool IsDbOnly(string domain) => Bound.IsDbOnly(domain);

        public StateUpdateResult Update(string domain, StateUpdatePlan plan, RequestContext context, StateCallOptions? options = null) =>
            Bound.Update(domain, plan, context, options);
    }
}
