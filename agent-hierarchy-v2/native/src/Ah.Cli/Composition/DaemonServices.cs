using Ah.Abstractions;
using Ah.Platform;
using Ah.Codex;
using Ah.Config;
using Ah.Core.Json;
using Ah.Daemon;
using Ah.Hierarchy;
using Ah.Launch;
using Ah.Mesh;
using Ah.Roster;
using Ah.State;
using Ah.Watch;

namespace Ah.Cli.Composition;

/// <summary>
/// The daemon's collaborators that need the service graph: the watch tick and the Codex adapter as periodic jobs, and the
/// view and status routes. Everything is built when the daemon first asks for its jobs and routes, so a process that is
/// not the daemon builds none of it.
/// </summary>
internal sealed class DaemonServices
{
    private readonly Lazy<ServiceGraph> _graph;
    private readonly IAhEnvironment _environment;
    private readonly Action<string> _log;
    private readonly Dictionary<string, string> _releaseRefusals = new(StringComparer.Ordinal);
    private readonly Lazy<WatchDependencies> _watch;

    /// <summary>The watcher's coverage (ah2-102 F2), shared in process by the watch tick and the read model.</summary>
    private readonly IWatchCoverage _coverage = WatchModule.CreateCoverage();
    private readonly Lazy<WatchHostSeams> _seams;
    private readonly Lazy<TestModeDirGate> _dirGate;
    private readonly WatchHealth _health;
    private IReadModel? _model;
    private Func<IReadOnlyList<string>>? _hierarchyDirs;

    private BareHost? _bareHost;
    private readonly IAckTally _acks = MeshModule.CreateAckTally();
    private readonly IHeardProcesses _heard = MeshModule.CreateHeardProcesses();
    private readonly ICodexRelay _codexRelay = CodexModule.CreateRelay();

    private readonly string? _tmuxTmpdir;
    private readonly ReadModelWriteRelay? _writeRelay;
    private readonly Func<Ah.StateDb.IStateDb>? _stateDb;

    /// <summary>The daemon's services; <paramref name="tmuxTmpdir"/> is the value of <c>TMUX_TMPDIR</c> tmux keeps its server sockets under (null: the process's own).</summary>
    internal DaemonServices(Lazy<ServiceGraph> graph, IAhEnvironment environment, Action<string>? log = null, string? tmuxTmpdir = null, ReadModelWriteRelay? writeRelay = null, Func<Ah.StateDb.IStateDb>? stateDb = null)
    {
        _writeRelay = writeRelay;
        _stateDb = stateDb;
        _graph = graph;
        _environment = environment;
        _tmuxTmpdir = tmuxTmpdir;
        _log = log ?? (static _ => { });
        _dirGate = new Lazy<TestModeDirGate>(() => new TestModeDirGate(_environment, _graph.Value.Io.Files, _graph.Value.Roster.TeamHome, _log), LazyThreadSafetyMode.ExecutionAndPublication);
        _seams =new Lazy<WatchHostSeams>(() => SeamsOf(_graph.Value, _log), LazyThreadSafetyMode.ExecutionAndPublication);
        _watch = new Lazy<WatchDependencies>(BuildWatch, LazyThreadSafetyMode.ExecutionAndPublication);
        _load = new Lazy<ILoadWatch>(
            () => WatchModule.CreateLoadWatch(new LoadSource(_environment.AhHome, PlatformModule.CreateProcessTable(_graph.Value.Runner)), () => _graph.Value.Io.Clock.UtcNow),
            LazyThreadSafetyMode.ExecutionAndPublication);
        _health = new WatchHealth(
            () => _watch.IsValueCreated ? _watch.Value.StatusDirs.Snapshot().Count > 0 : false,
            () => _acks.Unpulled,
            () => _graph.IsValueCreated ? _load.Value.Status() : null);
    }

    private readonly Lazy<ILoadWatch> _load;

    /// <summary>The health fields the watch tick owns; the daemon reads them for every health call.</summary>
    internal IHealthExtras Health => _health;

    /// <summary>The hierarchy dirs the read model's lanes are built from (empty before the view source exists).</summary>
    internal IReadOnlyList<string> HierarchyDirs() => Volatile.Read(ref _hierarchyDirs)?.Invoke() ?? [];

    /// <summary>Stops the read model (its projector thread and file watchers) and runs the status refreshes still waiting, as the daemon shuts down.
    /// Each step is logged as it starts, so a drain that hits its deadline here names the step it was stuck in.</summary>
    internal void Stop()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        _log("stop: closing the read model");
        var parts = Interlocked.Exchange(ref _model, null) is { } model ? WatchModule.CloseReadModel(model) : string.Empty;
        _log($"stop: read model closed in {clock.ElapsedMilliseconds} ms ({(parts.Length > 0 ? parts : "no model")})");
        if (_graph.IsValueCreated && _graph.Value.StatusRefresh is { } refresh)
        {
            _log("stop: running the waiting status refreshes");
            refresh.Dispose();
        }

        _log($"stop: done in {clock.ElapsedMilliseconds} ms");
    }

    /// <summary>
    /// The watch host's herdr reads, prompt headings and delivery-ladder pass over the service graph. The ladder pastes through the
    /// raw process runner, which no test-mode guard covers: MuxGuard wraps only the multiplexer runner, and only in hermetic runs.
    /// </summary>
    internal static WatchHostSeams SeamsOf(ServiceGraph g, Action<string>? report = null)
    {
        var ladder = new DeliveryLadder(
            g.Mailbox,
            MeshModule.CreateMailCore(g.Io.Clock),
            g.Roster,
            g.Config,
            g.Hierarchy,
            new LaunchDeliveryPaste(g.Launch.Paste),
            g.Io.Clock);
        return WatchHostSeams.FromLaunch(
            g.Runner,
            g.Environment,
            g.Io.Files,
            g.Io.Appender,
            g.Config,
            RosterModule.CreateScreenRules(),
            Environment.ProcessId,
            report,
            new MeshLadderSeam(ladder));
    }

    private WatchDependencies BuildWatch()
    {
        var g = _graph.Value;
        var host = new WatchHostAdapter(g, RosterModule.CreateScreenRules(), g.Io.Clock, _seams.Value, _log, _dirGate.Value.Admit);
        g.DispatchHost = host;
        return new WatchDependencies(
            host,
            _environment,
            g.Io.Clock,
            g.Io.Files,
            HierarchyModule.CreateRecovery(g.Io.Files, _environment, g.Io.Clock),
            WatchModule.CreateStatusDirs(),
            StillOwner: StillOwner,
            Log: _log,
            Observe: _health.Observe,
            TurnEnds: PlatformModule.CreateTurnEndLog(g.Io.Clock, Ah.Core.Text.AhPaths.TurnEndLog(_environment.AhHome)),
            Stamp: job => new TickStamps(g).Stamp(job))
        {
            Cpu = PlatformModule.CreateCpuMeter(),
            SleepGapMs = WatchDependencies.DefaultSleepGapMs,
            Coverage = _coverage,
        };
    }

    /// <summary>Whether <c>run/ahd.json</c> still names this process; a daemon replaced by another stops ticking.</summary>
    /// <summary>Mails the team's live Orchestrator that the memory adapter keeps refusing its ledger; a team with no live Orchestrator is left to the hook-errors log.</summary>
    internal static void AlarmLedgerStuck(StallHost host, string pool, string team, string text)
    {
        var target = host.Teams().FirstOrDefault(t => t.Name == team && t.HierarchyDir == pool && t.OrchestratorAddress is not null);
        if (target is not null)
        {
            host.Mail(new WatchMail(target.OrchestratorAddress!, pool, target.Cwd, "memory-forward-stuck", $"ahd alarm: {team}: {text}. No ledger fact reaches Engram until this is fixed.", new JsonObject(), target.OrchestratorSession));
        }
    }

    internal bool StillOwner()
    {
        var reason = NotOwnerReason();
        if (Interlocked.Exchange(ref _notOwner, reason) is var last && last != reason)
        {
            _log(reason is null
                ? "owner again: run/ahd.json names this daemon; the watch tick and stall watch run"
                : $"not the owner: run/ahd.json {reason}, not pid {Environment.ProcessId}; the watch tick and stall watch skip");
        }

        return reason is null;
    }

    /// <summary>The last reason <see cref="StillOwner"/> gave, null while this daemon owns <c>run/ahd.json</c>; a line is logged when it changes.</summary>
    private string? _notOwner;

    private string? NotOwnerReason()
    {
        try
        {
            var bytes = _graph.Value.Io.Files.ReadBytes(Path.Combine(_environment.AhHome, "run", "ahd.json"));
            if (JsonText.Parse(bytes) is not { IsMap: true } info || info.AsObject().Get("pid") is not { IsNumber: true } pid)
            {
                return "names no pid";
            }

            return (int)pid.AsNumber() == Environment.ProcessId ? null : $"names pid {(int)pid.AsNumber()}";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonParseException)
        {
            return $"is unreadable ({e.GetType().Name}: {e.Message})";
        }
    }

    private string? _lastStatePrune;

    /// <summary>One run of the state-prune job: <see cref="StatePrune.Run"/> with the configured retention and a backup labelled pre-prune.</summary>
    private void PruneState(IReadOnlyList<string> hierarchyDirs, double nowMs)
    {
        try
        {
            var g = _graph.Value;
            var settings = g.Config.StateSettings();
            var line = StatePrune.Run(
                g.Hierarchy,
                hierarchyDirs,
                nowMs,
                (int)settings["retention_days"].AsNumber(),
                () => StatePrune.Backup(Ah.StateDb.StateDbModule.CreateBackups(), _environment.AhHome, g.Io.Clock.UtcNow),
                _log,
                countOnly: StatePrune.CountOnly(settings),
                sessions: Ah.StateDb.StateDbModule.OpenSessionAuthority(_environment.AhHome));
            if (line is not null)
            {
                _lastStatePrune = line;
            }
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            _log($"state prune failed: {e.Message}");
        }

        RefreshPlannerStats();
    }

    /// <summary>Refreshes the state database's planner statistics on every state-prune tick; a failure is logged and the tick goes on.</summary>
    private void RefreshPlannerStats()
    {
        if (!File.Exists(Path.Join(_environment.AhHome, "state", "ah.db")))
        {
            return;
        }

        var outcome = Ah.StateDb.StateDbModule.RefreshStats(_environment.AhHome);
        if (outcome.StartsWith("failed", StringComparison.Ordinal))
        {
            _log($"state db planner statistics not refreshed: {outcome}");
        }
    }

    /// <summary>How often the activity and peer logs are pruned: often enough that a daemon restarted more than hourly still compacts.</summary>
    internal static readonly TimeSpan ActivityPrunePeriod = TimeSpan.FromMinutes(10);

    /// <summary>How often dismissed and dead-team scratch is swept; retention is hours, so an hourly pass is enough.</summary>
    internal static readonly TimeSpan ScratchSweepPeriod = TimeSpan.FromHours(1);

    private int _pruneSkipLogged;

    /// <summary>How often settled state is pruned: listing every pool and parsing the whole peer log is the costly part, and retention is days.</summary>
    internal static readonly TimeSpan StatePrunePeriod = TimeSpan.FromHours(1);

    /// <summary>How often the board audit looks for a new ship: an audit follows a ship by at most this long.</summary>
    internal static readonly TimeSpan BoardAuditPeriod = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Sweeps the scratch of the team home each of <paramref name="hierarchyDirs"/> resolves to. A worktree's hierarchy dir resolves to
    /// the main checkout's team home, so a home reached by several is swept once. Outside test mode, a daemon whose <c>AH_HOME</c> is
    /// not the default home sweeps no team home inside the real <c>HOME</c>: that scratch is the default home's daemon's to sweep.
    /// Returns how many homes were swept.
    /// </summary>
    internal int SweepScratchHomes(IEnumerable<string> hierarchyDirs)
    {
        var g = _graph.Value;
        var scratch = PlatformModule.CreateScratch(g.Io.Files, _environment.AhHome);
        var homes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var dir in hierarchyDirs)
        {
            homes.TryAdd(g.Roster.TeamHome(dir), dir);
        }

        string? Real(string path) => Ah.Core.Text.RealPathResolver.ThroughLongestExistingPrefix(g.Io.Files, path.TrimEnd('/'));
        var realHome = _environment.TestMode ? null : Real(_environment.HomeDirectory);
        var foreign = realHome is not null && Real(_environment.AhHome) != Real(Ah.Core.Text.AhPaths.HomeUnder(_environment.HomeDirectory));
        // macOS volumes are case-insensitive by default, so a team home spelled in another case is still inside the real HOME there.
        var cmp = OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        bool InsideRealHome(string home) =>
            Real(home) is not { } real || string.Equals(real, realHome, cmp) || real.StartsWith(realHome + "/", cmp);
        var swept = 0;
        foreach (var (home, dir) in homes)
        {
            if (foreign && InsideRealHome(home))
            {
                _log($"scratch-sweep {home} skipped: AH_HOME {_environment.AhHome} is not the default home, and this team home is inside the real HOME or has no real path");
                continue;
            }

            swept++;
            try
            {
                var report = LaunchModule.SweepScratch(scratch, g.Launch.Teardown, g.Roster, g.Io.Files, (op, args) => g.Hierarchy.StateCalls.Grants(op, args), dir, home, g.Io.Clock.UtcNow, commit: true);
                if (report.Entries.Count > 0)
                {
                    _log($"scratch-sweep {home}: removed {report.Entries.Count} entries, {report.Bytes} bytes");
                }
            }
            catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                _log($"scratch-sweep {home} failed: {e.Message}");
            }
        }

        return swept;
    }

    /// <summary>How often the daemon retires old teams. It acts only while a client is connected, so a short period means a team the client still shows goes soon.</summary>
    internal static readonly TimeSpan TeamRetirePeriod = TimeSpan.FromMinutes(1);

    private readonly HashSet<string> _retireLogged = new(StringComparer.Ordinal);

    /// <summary>
    /// Writes one retire pass. A failure logs once per team and error text and is simply found again by the next pass; a team file the model
    /// could not read logs once and is skipped. Returns how many writes succeeded.
    /// </summary>
    internal int ApplyRetirePass((IReadOnlyList<(string Dir, string Team, long LastWorkMs)> Retire, IReadOnlyList<string> Unreadable) pass, Func<string, string, long, string?> retire, Func<string, bool> doesNotRead)
    {
        foreach (var path in pass.Unreadable.Where(doesNotRead))
        {
            LogRetireOnce($"team-retire skipped {path}: the team record does not read");
        }

        var written = 0;
        foreach (var candidate in pass.Retire)
        {
            string? error;
            try
            {
                error = retire(candidate.Dir, candidate.Team, candidate.LastWorkMs);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                error = e.Message;
            }

            if (error is null)
            {
                written++;
                _ = _retireLogged.RemoveWhere(k => k.StartsWith($"team-retire {candidate.Team} ", StringComparison.Ordinal));
            }
            else
            {
                LogRetireOnce($"team-retire {candidate.Team} failed: {error}");
            }
        }

        return written;
    }

    private void LogRetireOnce(string line)
    {
        if (_retireLogged.Add(line))
        {
            _log(line);
        }
    }

    internal void RegisterJobs(IJobRegistry jobs)
    {
        var retireCpu = PlatformModule.CreateCpuMeter();
        jobs.Add("team-retire", TeamRetirePeriod, cancellation => Task.Run(() => retireCpu.Measure(() =>
        {
            if (Volatile.Read(ref _model) is not { } model)
            {
                return;
            }

            var g = _graph.Value;
            var services = new RosterComposition(g, g.Io.Clock, _environment);
            var pass = WatchModule.TeamsToRetire(model, g.Io.Clock.UtcNow.ToUnixTimeMilliseconds(), (dir, team) => g.Roster.TeamIsOrphaned(g.Roster.ReadTeamRecord(dir, team)));
            _ = ApplyRetirePass(pass, (dir, team, lastWorkMs) => Ah.Commands.CommandsModule.TeamRetire(services, dir, team, lastWorkMs), path => File.Exists(path) && g.Roster.ReadTeamFileAt(path).IsNull);
        }), cancellation));
        jobs.Meter("team-retire", retireCpu.Take);

        WatchModule.Register(jobs, _watch.Value);
        CodexModule.Register(jobs, new CodexDependencies(_graph.Value, _log, _dirGate.Value.Admit), _codexRelay);

        // Retention is measured in days, so an hourly pass drops a row at most an hour later than a faster one would.
        // The scheduler fires every job at start; this one skips that tick (prune and ANALYZE), so its first run is one period after start.
        var compactCpu = PlatformModule.CreateCpuMeter();
        var statePruneStarted = false;
        jobs.Add("state-prune", StatePrunePeriod, cancellation => Task.Run(() => compactCpu.Measure(() =>
        {
            if (!statePruneStarted)
            {
                statePruneStarted = true;
                return;
            }

            if (Volatile.Read(ref _hierarchyDirs) is { } dirs)
            {
                PruneState(dirs(), _graph.Value.Io.Clock.UtcNow.ToUnixTimeMilliseconds());
            }
        }), cancellation));
        jobs.Meter("state-prune", compactCpu.Take);
        jobs.Detail("state-prune", () => _lastStatePrune);

        // Both jobs run on the thread pool: the scheduler's loop is serial, and a run that blocks it delays every other job and the watch tick.
        var pruneCpu = PlatformModule.CreateCpuMeter();
        var forwardCpu = PlatformModule.CreateCpuMeter();
        jobs.Add("activity-prune", ActivityPrunePeriod, cancellation => Task.Run(() => pruneCpu.Measure(() =>
        {
            if (Volatile.Read(ref _hierarchyDirs) is { } dirs)
            {
                var hierarchyDirs = dirs();
                var nowMs = _graph.Value.Io.Clock.UtcNow.ToUnixTimeMilliseconds();
                _ = HierarchyModule.PruneActivity(_graph.Value.Hierarchy.Status, hierarchyDirs, nowMs, _log);
            }
            else if (Interlocked.Exchange(ref _pruneSkipLogged, 1) == 0)
            {
                _log("activity-prune skipped: hierarchy dirs not ready");
            }
        }), cancellation));
        jobs.Meter("activity-prune", pruneCpu.Take);

        var sweepCpu = PlatformModule.CreateCpuMeter();
        jobs.Add("scratch-sweep", ScratchSweepPeriod, cancellation => Task.Run(() => sweepCpu.Measure(() =>
        {
            if (Volatile.Read(ref _hierarchyDirs) is not { } dirs)
            {
                return;
            }

            SweepScratchHomes(dirs());
        }), cancellation));
        jobs.Meter("scratch-sweep", sweepCpu.Take);

        var stallHost = new StallHost(_graph.Value, _environment.AhHome, StillOwner, () => Volatile.Read(ref _hierarchyDirs)?.Invoke(), admitDirs: _dirGate.Value.Admit, paneTail: (herdr, transport, pane) => Mux(_graph.Value, herdr).ReadRecent(transport, pane, 40), log: _log);

        // Hooks only append to a team's ledger; this is the one place its new lines go on to the memory adapter.
        jobs.Add("memory-forward", TimeSpan.FromSeconds(30), cancellation => Task.Run(() => forwardCpu.Measure(() =>
        {
            try
            {
                if (Volatile.Read(ref _hierarchyDirs) is { } dirs && _environment.Plugin?.Root is { Length: > 0 } pluginRoot)
                {
                    var g = _graph.Value;
                    var path = _environment.GetVariable("PATH");
                    foreach (var dir in dirs())
                    {
                        if (cancellation.IsCancellationRequested)
                        {
                            break;
                        }

                        var repo = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(dir.TrimEnd('/'))) ?? string.Empty);
                        var pool = dir;
                        _ = g.Hierarchy.Progress.ForwardLedgers(pool, g.Runner, pluginRoot, path, repo, (team, text) => AlarmLedgerStuck(stallHost, pool, team, text));
                    }
                }
            }
            finally
            {
                _ = _graph.Value.Hierarchy.Progress.EndForwardRun();
            }
        }), cancellation));
        jobs.Meter("memory-forward", forwardCpu.Take);
        jobs.Detail("memory-forward", () =>
        {
            var (children, wallMs) = _graph.Value.Hierarchy.Progress.LastForwardRun();
            return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"children={children} child_wall_ms={wallMs}");
        });

        // board-audit (b284): each team's `checklist audit` after a new ship on its board, else once a day; disagreements mail its Orchestrator once
        var auditCpu = PlatformModule.CreateCpuMeter();
        var audited = new Dictionary<string, (string? Ship, long AtMs)>(StringComparer.Ordinal);
        jobs.Add("board-audit", BoardAuditPeriod, cancellation => Task.Run(() => auditCpu.Measure(() =>
        {
            var g = _graph.Value;
            var services = new RosterComposition(g, g.Io.Clock, _environment);
            var nowMs = g.Io.Clock.UtcNow.ToUnixTimeMilliseconds();
            foreach (var team in stallHost.Teams())
            {
                if (cancellation.IsCancellationRequested)
                {
                    break;
                }

                var key = team.HierarchyDir + "|" + team.Name;
                try
                {
                    var (ship, findings) = Ah.Commands.CommandsModule.ChecklistAudit(services, team.HierarchyDir, team.Cwd, team.Name, g.Checklists.Of, g.Checklists.LastShip);
                    if (audited.TryGetValue(key, out var last) && last.Ship == ship && nowMs - last.AtMs < (long)WatchModule.BoardAuditDaily.TotalMilliseconds)
                    {
                        continue;
                    }

                    audited[key] = (ship, nowMs);
                    var box = team.OrchestratorAddress is { } to ? g.Mailbox.ListBoxes().FirstOrDefault(b => b.Address == to) : null;
                    _ = WatchModule.MailBoardAudit(team, findings(), box is null ? [] : g.Mailbox.ReadEntries(box.Dir).Entries, stallHost.Mail);
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    _log($"board-audit {team.Name} failed: {e.Message}");
                }
            }
        }), cancellation));
        jobs.Meter("board-audit", auditCpu.Take);

        var stallSettings = StallSettings.From(_environment);
        WatchModule.RegisterStallWatch(
            jobs,
            stallHost,
            stallSettings,
            () => _graph.Value.Io.Clock.UtcNow,
            job => new TickStamps(_graph.Value).Stamp(job));
        // backlog-watch: stamps the job, then republishes status.json of every project with a backlog. The health facts are delta'd by every
        // plan write (status entry "backlog"); this tick only re-judges the time-driven verdicts (due, stale) from those stored facts.
        // The scheduler fires every job at start; this one skips that tick, so its first run is one period after start. With no
        // backlog (no plan_events row) it neither stamps nor republishes anything; its one read opens the state DB read-only.
        var backlogWatchStarted = false;
        jobs.Add(HierarchyModule.BacklogWatchJob, TimeSpan.FromMinutes(10), _ =>
        {
            if (!backlogWatchStarted)
            {
                backlogWatchStarted = true;
                return Task.CompletedTask;
            }

            IReadOnlyList<object?[]> dirs = [];
            var readFailed = false;
            try
            {
                if (_graph.Value.OpenReader(_environment.AhHome).Reader is { } reader)
                {
                    using (reader)
                    {
                        dirs = reader.Query("SELECT DISTINCT hier_dir FROM plan_events");
                    }
                }
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // fail open: a database that cannot be read leaves the stamp, and health shows the backlog as unknown
                readFailed = true;
            }

            if (dirs.Count == 0 && !readFailed)
            {
                return Task.CompletedTask;
            }

            new TickStamps(_graph.Value).Stamp(HierarchyModule.BacklogWatchJob);
            foreach (var row in dirs)
            {
                if (row[0] is string dir && Directory.Exists(dir))
                {
                    try
                    {
                        _graph.Value.Hierarchy.Status.RefreshStatus(dir);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
                    {
                        // the next tick tries again
                    }
                }
            }

            return Task.CompletedTask;
        });
        jobs.Add(HierarchyModule.ReleaseWatchJob, TimeSpan.FromMinutes(1), cancellation => Task.Run(
            () =>
            {
                if (Volatile.Read(ref _hierarchyDirs) is { } dirs)
                {
                    WatchReleases(_dirGate.Value.Admit(dirs()));
                }
            },
            cancellation));
        var bareHost = new BareHost(_graph.Value, stallHost, heard: _heard);
        Volatile.Write(ref _bareHost, bareHost);
        WatchModule.RegisterBareWatch(
            jobs,
            bareHost,
            stallSettings.Period,
            () => _graph.Value.Io.Clock.UtcNow,
            job => new TickStamps(_graph.Value).Stamp(job),
            _load.Value,
            stallHost.Episodes);

        var backups = Ah.StateDb.StateDbModule.CreateBackups();
        jobs.Add("db-backup", TimeSpan.FromMinutes(10), cancellation =>
        {
            if (backups.TakeIfDue(_environment.AhHome, _graph.Value.Io.Clock.UtcNow, Ah.StateDb.BackupSettings.Default, out var failure) is null && failure is not null)
            {
                _log(failure);
            }

            return Task.CompletedTask;
        });

        // What the hooks could not write while the database was busy is applied here, then the log is folded back, because the hook and
        // CLI connections never checkpoint: a passive pass every minute, and a truncating
        // pass whenever that left nothing behind, which is when no reader is holding the log. The scheduler fires every job at start;
        // this one skips that tick, so its first pass is one period after start.
        var dbCheckpointStarted = false;
        jobs.Add("db-checkpoint", TimeSpan.FromMinutes(1), cancellation =>
        {
            if (!dbCheckpointStarted)
            {
                dbCheckpointStarted = true;
                return Task.CompletedTask;
            }

            _ = _stateDb?.Invoke().ImportFallback();
            if (_stateDb?.Invoke() is { } db && db.Checkpoint(truncate: false) is { Ok: true, Blocked: false } pass && pass.LogFrames > 0 && pass.LogFrames == pass.CheckpointedFrames)
            {
                _ = db.Checkpoint(truncate: true);
            }

            return Task.CompletedTask;
        });

        jobs.Add("applied-writes-prune", TimeSpan.FromDays(1), cancellation =>
        {
            _ = _stateDb?.Invoke().PruneAppliedWrites(TimeSpan.FromHours(24));
            return Task.CompletedTask;
        });

        // A job's first run is at daemon start; this one does its work then and never again.
        var pinned = 0;
        jobs.Add("pin-orchestrator-starts", TimeSpan.FromDays(1), cancellation =>
        {
            if (Interlocked.Exchange(ref pinned, 1) == 0)
            {
                _ = _graph.Value.Callers.PinOrchestratorStarts();
            }

            return Task.CompletedTask;
        });
    }

    /// <summary>One release-watch tick over the repositories of <paramref name="hierarchyDirs"/>, each once through its progress pool. A repository that fails is logged and tried again next tick.</summary>
    private void WatchReleases(IReadOnlyList<string> hierarchyDirs)
    {
        var g = _graph.Value;
        var conventions = ConfigModule.CreateConventions(g.Runner, g.Io.Files, () => _environment.HomeDirectory);
        var pools = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dir in hierarchyDirs)
        {
            try
            {
                if (Path.GetDirectoryName(Path.GetDirectoryName(dir)) is not { Length: > 0 } root || !Directory.Exists(root))
                {
                    continue;
                }

                var pool = HierarchyModule.ProgressPoolOf(g.Config.MainHierarchyDir(root), dir);
                if (!pools.Add(pool) || _dirGate.Value.Admit([pool]).Count == 0)
                {
                    continue;
                }

                var scope = new GitScope(root);
                var teams = g.Hierarchy.Progress.DocTeams(pool).Where(t => !g.Hierarchy.Progress.IsOff(pool, t)).ToList();
                ReleaseWatch.Tick(
                    g.Hierarchy.Progress,
                    pool,
                    teams,
                    args => conventions.TryGit(scope, args),
                    id => g.Hierarchy.Messages.FindByIdAndType(pool, id, "request").Member("meta")?.Member("slug") is { IsText: true } slug ? slug.AsString() : null,
                    g.Io.Clock.UtcNow,
                    (key, problem) => ReleaseWatch.LogChanges(_releaseRefusals, text => _log($"release-watch {text}; retrying each tick"))($"{dir} {key}", problem),
                    (row, sha) =>
                    {
                        if (Ah.Cli.Verbs.BacklogVerb.CloseShipped(g, _environment.AhHome, pool, row, sha) is { } failed)
                        {
                            _log($"release-watch {dir} {row}: {failed}");
                        }
                    });
            }
            catch (Exception e) when (e is not OutOfMemoryException and not OperationCanceledException)
            {
                _log($"release-watch {dir}: {e.Message}");
            }
        }
    }

    private void LogTestModeDirs(IViewSource source)
    {
        try
        {
            var kept = source.RegisteredHierarchyDirs().Distinct(StringComparer.Ordinal).Count();
            _log($"test mode: HOME={_environment.HomeDirectory}, hierarchy dirs kept {kept}, dropped {_dirGate.Value.Dropped}");
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            _log($"test mode: HOME={_environment.HomeDirectory}, hierarchy dirs not read: {e.Message}");
        }
    }

    internal void RegisterRoutes(IRouteRegistry routes)
    {
        var g = _graph.Value;
        var watch = _watch.Value;
        // the window runs on the monotonic tick, so a wall clock stepped back never stretches it
        var probeReuse = new StoppedProbeReuse(static () => Environment.TickCount64);
        var filesImport = new FilesImport.Worker(_environment.AhHome,_dirGate.Value.Admit, _log, () => _stateDb?.Invoke());
        var services = new ViewSourceServices(
            g.Io.Files,
            new StreamDerivations(),
            new ViewPlacement(g),
            PlatformModule.CreateProcessTable(g.Runner),
            g.Runner,
            _environment.HomeDirectory,
            State: new StateServiceReader(g.InternalStateRead), Layout: new MailboxLayout(g.Mailbox, g.Environment), Config: new ViewConfig(g.Config),
            Verdict: static record => Ah.Hierarchy.HierarchyModule.RecordVerdict(record, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
            StoredBytes: g.Hierarchy.Core.StoredBytes,
            PidRunning: (pid, start, policy) => g.Hierarchy.Core.PidRunning(pid, start, policy))
        {
            TeamStamp = g.Roster.TeamStamp,
            SessionsToken = g.Mailbox.SessionsChangeToken,
            AdmitDirs = filesImport.OnAdmit,
            PublishedSummary = () => Volatile.Read(ref _model)?.Manifest("summary"),
        };
        var source = WatchModule.CreateViewSource(services);
        filesImport.Start();
        Volatile.Write(ref _hierarchyDirs, () => source.RegisteredHierarchyDirs());
        if (_environment.TestMode)
        {
            LogTestModeDirs(source);
        }

        IReadModel? model = null;
        if (_writeRelay is not null)
        {
            Func<string, string?> messageIdOf = name => g.Hierarchy.Core.ParseMsgFilename(name)?.Id;
            var recovery = HierarchyModule.CreateRecovery(g.Io.Files, g.Environment, g.Io.Clock);
            model = WatchModule.CreateReadModel(
                new StateServiceReader(g.InternalStateRead),
                () => [.. source.RegisteredHierarchyDirs()],
                new ReadModelOptions
                {
                    TeamsChangeToken = g.Roster.TeamsChangeToken,
                    SessionsChangeToken = g.Mailbox.SessionsChangeToken,
                    MessageIdOf = messageIdOf,
                    Processes = PlatformModule.CreateProcessTable(g.Runner),
                    Timeline = new TimelineSettings(Ah.StateDb.StateDbModule.CreateOpener(), Path.Combine(_environment.AhHome, "state", "ah.db"), Ah.StateDb.StateDbModule.BusyTimeoutMs, () => (int)g.Config.UiSettings()["timeline_retention_days"].AsNumber()),
                    Home = _environment.AhHome,
                    TeamHeld = (dir, team) => g.Roster.HoldOf(dir, team == "default" ? null : team) is not null,
                    Coverage = _coverage,
                    TeamGoal = (dir, team) => RulesReads.ShownGoal(_environment.AhHome, () => dir, _ => [team], g.OpenReader),
                    PeerPendingFile = g.Hierarchy.Peers.PeerPendingPath(),
                    Log = _log,
                    ActiveDirs = new ActiveHierarchyDirs(g.Mailbox, g.Config, g.Io.Files, () => Ah.StateDb.StateDbModule.KnownDirs(_environment.AhHome), g.Io.Clock).Of,
                    TranscriptFiles = g.Io.Files,
                    Usage = HierarchyModule.CreateUsage(g.Io.Files, g.Environment, Ah.StateDb.StateDbModule.CreateUsageRows(g.Environment.AhHome)),
                    ValidTranscriptPath = raw => recovery.TranscriptPathOk(JsonValue.FromString(raw)),
                    TranscriptProjects = Path.Combine(_environment.HomeDirectory, ".claude", "projects"),
                    GitProcess = g.Runner,
                    NewPlanFold = Ah.Hierarchy.HierarchyModule.CreatePlanFold,
                    QueueProbe = (dir, team, nowMs) => QueueRows.Of(g, dir, team, nowMs),
                    SitrepProbe = (dir, team, nowMs, work) => SitrepRows.Of(g, dir, team, nowMs, () => Verbs.BacklogVerb.FactsFor(_environment.AhHome, g.Config, dir, (long)nowMs).Items, work),
                    JobsProbe = (dir, team, _) => JobRows.Of(g, dir, team),
                    JobsVersion = () => Ah.StateDb.StateDbModule.DomainSeq(_environment.AhHome, "jobs"),
                    SubagentsProbe = source.SubagentRuns,
                    SubagentsVersion = () => Ah.StateDb.StateDbModule.DomainSeq(_environment.AhHome, "peer-records"),
                    // b517; per-dir-files-to-db §6c G5: one read, and a store that cannot be read is told as such (null)
                    ParkedProbe = dir => g.Io.Files.ReadAskLog(ParkedQuestions.PathIn(dir)).TryLines(out var parkedLines) ? ParkedQuestions.Fold(string.Join('\n', parkedLines)) : null,
                    // ah2-180 Amendments 2 and 4: no row yet is no alarm raised; a store that cannot be read is null, so the lane keeps its record
                    UserAlarmsProbe = () => StallStore.TryRead(_environment.AhHome, StallStore.Reported, out var reported) ? reported ?? "{}" : null,
                    BacklogProbe = (dir, nowMs) =>
                    {
                        var facts = Verbs.BacklogVerb.FactsFor(_environment.AhHome, g.Config, dir, nowMs, key => SitrepRows.DirItems(g, key));
                        var dirItems = SitrepRows.DirItems(g, dir);
                        return facts with
                        {
                            Items = HierarchyModule.BacklogStates(facts.Items, dirItems),
                            PlannedParents = [.. HierarchyModule.PlannedBacklogKeys(facts.Items, dirItems)],
                            Work = WorkRows.Cards(g, _environment.AhHome, dir, string.Empty, HierarchyModule.BacklogStream),
                        };
                    },
                    WorkCardsProbe = (dir, folder, stream) => WorkRows.Cards(g, _environment.AhHome, dir, folder, stream),
                    ItemLogProbe = (dir, n) => WorkRows.ItemLog(g, _environment.AhHome, dir, n),
                    ItemDescProbe = (dir, n) => WorkRows.ItemDesc(g, _environment.AhHome, dir, n),
                    LaneFacts = new LaneFactsHost(g),
                    MemberFacts = dir => MemberFactsOf.Dir(g, dir, g.Checklists.Of, probeReuse),
                    PidsStamp = PidsStampOf(Ah.StateDb.StateDbModule.CreateRosterRecords(_environment.AhHome)),
                });
            _writeRelay.Attach(model, messageIdOf, g.Hierarchy.Core.PeerRecordsInDb);
            WatchModule.StartReadModel(model);
            _model = model;
        }

        // b459 C1: status.read hands its compute's witness out beside the document; this reader pairs the two
        var statusReader = new StateServiceReader(g.InternalStateRead);
        var view = new ViewServiceDependencies(
            source,
            WatchModule.CreateStatusRevs(statusReader, witnessOf: document => statusReader.Beside(document) as Ah.Hierarchy.StatusWitness),
            watch.StatusDirs,
            g.Io.Clock,
            HerdrAgentsAsync,
            InspectTimers: session => g.DispatchHost!.InspectTimers(session, g.Io.Clock.UtcNow.ToUnixTimeMilliseconds(), WatchModule.PollMs(_environment)),
            InspectEvents: address => g.DispatchHost!.InspectEvents(address),
            Log: _log,
            Timing: WatchModule.ViewTimingIsOn(_environment.GetVariable(WatchModule.ViewTimingSwitch)),
            Model: model,
            ReadModelStats: model is null ? null : () => WatchModule.ReadModelStats(model),
            SubscribeWrites: wrote =>
            {
                if (_writeRelay is { } relay)
                {
                    relay.Written = wrote;
                }
            },
            Rebuild: () =>
            {
                if (model is not null)
                {
                    WatchModule.ForgetReadModel(model);
                }

                probeReuse.Forget();
                g.Checklists.Forget();
                g.LaneCommits.Forget();
                g.BriefAddressees.Forget();
                g.Landed.Forget();
                g.SessionPeers.Forget();
                g.FrontMatters.Forget();
                g.Hierarchy.Core.ForgetHeldPeerRows();
                g.Hierarchy.Roster.ForgetGateHolds();
                g.Roster.ForgetTeamDocs();
                source.ForgetHeldDirs();
                g.Hierarchy.Status.ForgetStatusHolds();
                g.Mailbox.ForgetHeldBoxes();
                g.Hierarchy.Roster.ForgetLatestRosters();
                g.Hierarchy.Decisions.ForgetAnchorHolds();
                g.Hierarchy.Core.ForgetListings();
                g.Hierarchy.Progress.ForgetPlans();
                (g.DispatchHost as WatchHostAdapter)?.ForgetReadyBacklogs();
            });
        DaemonModule.RegisterViews(routes, view);
        DaemonModule.RegisterPeerPending(
            routes,
            dir => source.RegisteredHierarchyDirs().Contains(dir, StringComparer.Ordinal),
            (peerPid, session) => g.Mailbox.PrincipalOf(peerPid) is { } caller
                && (caller.Kind == PrincipalKind.User || (caller.Kind == PrincipalKind.Session && caller.Sessions.Any(s => s.SessionId == session))),
            session => g.Hierarchy.Peers.PendingFor(session));
        RegisterMesh(routes, g, _acks, _heard, _log);
        RegisterQa(routes, g);
        routes.Add(CodexAnswerRoute.Path, new CodexAnswerRoute(g, _codexRelay).HandleAsync);
        routes.Add(BareRecoverRoute.Path, new BareRecoverRoute(g, () => Volatile.Read(ref _bareHost)).HandleAsync);
        routes.Add(LoadAdmitRoute.Path, new LoadAdmitRoute(_load.Value).HandleAsync);
        CutoverCaller CallerOf(int? peerPid)
        {
            var table = PlatformModule.CreateProcessTable(g.Runner);
            var facts = new DaemonCutoverFacts(g, table, source.RegisteredHierarchyDirs);
            var principal = g.Mailbox.PrincipalOf(peerPid);

            // ah2-32: a top-level session with no team registers nothing, so it arrives anonymous. A Claude session that is no
            // team's member process is the user's own session, the Orchestrator before it has a team: it is not refused.
            return principal.Kind == PrincipalKind.Anonymous && Ah.Mesh.MeshModule.HarnessOf(table, peerPid) is { } harness && !facts.IsMemberProcess(harness)
                ? new CutoverCaller(CutoverCallerKind.TopOrchestrator, "top-level session with no team")
                : CutoverCallers.Classify(principal, facts);
        }

        routes.Add(LessonsRoute.Path, new LessonsRoute(g, _environment.AhHome, _stateDb, CallerOf, () => g.Io.Clock.UtcNow).HandleAsync);
        routes.Add(RulesRoute.Path, new RulesRoute(
            g,
            _environment.AhHome,
            _stateDb,
            CallerOf,
            () => g.Io.Clock.UtcNow,
            peerPid => peerPid is { } p ? PlatformModule.CreateProcessTable(g.Runner).StartTime(p) : null,
            // ah2-53: a goal lives in ah.db, so no file event tells of its write; the teams key of the team's file is re-read, as a team write's is
            (dir, team) =>
            {
                try
                {
                    if (model is not null && WatchModule.TeamKey(g.Roster.TeamPath(dir, team)) is { } key)
                    {
                        model.Emit("teams", key);
                    }
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    // the goal is written; the row shows it at the team's next change
                }
            }).HandleAsync);
    }

    /// <summary>
    /// The processes lane's pids stamp of a dir: the session pids' counter, the dir's roster stamp (the peers counter and its row count) and
    /// the team records' counter, which the attribution of a roster row reads; null when any cannot be read.
    /// </summary>
    private Func<string, string?> PidsStampOf(IRosterRecordSource roster) => dir =>
        Ah.StateDb.StateDbModule.DomainSeq(_environment.AhHome, "session-pids") is { } pids && roster.Stamp(dir) is { } peers
            && Ah.StateDb.StateDbModule.DomainSeq(_environment.AhHome, "teams") is { } teams
            ? $"{pids}:{peers}:{teams}"
            : null;

    private void RegisterQa(IRouteRegistry routes, ServiceGraph g)
    {
        bool Holds(JsonObject registration, string address) =>
            registration.Get("addresses") is { IsArray: true } list && list.AsArray().Any(a => a.IsText && a.AsString() == address);

        var qa = HierarchyModule.CreateQa(
            g.Hierarchy,
            new QaDependencies(
                g.Io.HostProbe,
                g.Io.Clock,
                g.Io.Files,
                address => g.Mailbox.AllRegistrations().Any(r => Holds(r, address) && g.Mailbox.RegistrationLive(r)),
                g.Hierarchy.Core.NewId,
                [Path.Combine(_environment.AhHome, "agent-hierarchy.json")],
                QaResultsRoot(_environment.HomeDirectory, _environment.GetVariable("AH_RESULTS_DIR"), g.Io.Files.RealPath),
                _log,
                static _ => { })
            {
                NotifyQueued = job => RingLiveQa(
                    g.Mailbox.AllRegistrations(),
                    g.Mailbox.RegistrationLive,
                    (teamFile, name) => TeamMember(g.Roster, teamFile, name, _log),
                    request =>
                    {
                        var outcome = g.Mailbox.Send(request, g.Io.Clock.UtcNow);
                        return outcome.Ok ? null : outcome.Failure?.ToString() ?? "refused";
                    },
                    _log,
                    job),
            });
        new QaRoutes(
            qa,
            pid => g.Mailbox.PrincipalOf(pid),
            session => g.Mailbox.AllRegistrations().FirstOrDefault(r => r.Get("session_id") is { IsText: true } id && id.AsString() == session)?.Get("team_file") is { IsText: true } file ? file.AsString() : null,
            g.Io.Files).Register(routes);
    }

    /// <summary>The member named <paramref name="name"/> in the team file, or null when the file or the member cannot be read.</summary>
    internal static JsonObject? TeamMember(IRosterCore roster, string teamFile, string name, Action<string> log)
    {
        if (roster.ReadTeamFileAt(teamFile).Member("members") is not { IsArray: true } members)
        {
            log($"qa-queued: team file {teamFile} could not be read; no member of it is rung");
            return null;
        }

        return members.AsArray().Where(static m => m.IsMap).Select(static m => m.AsObject()).FirstOrDefault(m => m.Get("name") is { IsText: true } n && n.AsString() == name);
    }

    /// <summary>
    /// Mails the <c>qa-queued</c> event to every live QA box of the job's team, so an idle QA member runs <c>ah qa next</c>. The service
    /// calls this once for each new queued job, so a job rings once; a team with no live QA box gets no mail.
    /// </summary>
    internal static void RingLiveQa(
        IEnumerable<JsonObject> registrations,
        Func<JsonObject, bool> isLive,
        Func<string, string, JsonObject?> memberOf,
        Func<JsonObject, string?> send,
        Action<string> log,
        QaJob job)
    {
        if (job.Team is not { Length: > 0 } team)
        {
            return;
        }

        var suffix = "@" + team;
        var text = $"A QA job was queued ({job.Set} at {job.Sha[..7]}): run `ah qa next`.";
        foreach (var registration in registrations)
        {
            if (!isLive(registration)
                || registration.Get("session_id") is not { IsText: true } session
                || registration.Get("team_file") is not { IsText: true } teamFile
                || registration.Get("addresses") is not { IsArray: true } addresses)
            {
                continue;
            }

            var hierarchyDir = Path.GetDirectoryName(Path.GetDirectoryName(teamFile.AsString()));
            if (hierarchyDir is null)
            {
                continue;
            }

            var cwd = registration.Get("cwd") is { IsText: true } c && Path.IsPathRooted(c.AsString()) ? c.AsString() : hierarchyDir;
            foreach (var address in addresses.AsArray().Where(static a => a.IsText).Select(static a => a.AsString()))
            {
                if (!address.EndsWith(suffix, StringComparison.Ordinal)
                    || !MeshModule.IsQaMember(memberOf(teamFile.AsString(), address[..^suffix.Length])))
                {
                    continue;
                }

                var request = MeshModule.EventRequest(address, "ahd", cwd, hierarchyDir, "qa-queued", text);
                request["session_id"] = JsonValue.Null;
                request["data"] = new JsonObject();
                request["to_session"] = session.AsString();
                if (send(request) is { } refused)
                {
                    log($"qa-queued mail to {address} refused: {refused}");
                }
            }
        }
    }

    /// <summary>
    /// The directory of stored QA results, the one <c>tests/run-set.sh</c> writes: <c>AH_RESULTS_DIR</c> when it is set and (symbolic links
    /// resolved) lies under <c>$HOME/.claude/ah2/qa-results</c> or <c>/private/tmp/ahprobe.*</c>, otherwise that default root.
    /// </summary>
    internal static string QaResultsRoot(string home, string? configured, Func<string, string> realPath)
    {
        var root = Path.Combine(home, ".claude", "ah2", "qa-results");
        if (string.IsNullOrEmpty(configured) || configured.Split('/').Contains(".."))
        {
            return root;
        }

        var real = realPath(configured);
        var underRoot = new[] { root, realPath(root) }.Any(r => real == r || real.StartsWith(r + "/", StringComparison.Ordinal));
        return underRoot || real.StartsWith("/private/tmp/ahprobe.", StringComparison.Ordinal) && real.Length > "/private/tmp/ahprobe.".Length
            ? real
            : root;
    }

    private PaneMux Mux(ServiceGraph g, IHerdr herdr) =>
        new(g.Launch.Mux, g.Io.Files, _tmuxTmpdir ?? _environment.GetVariable("TMUX_TMPDIR"), PlatformModule.CurrentUid(), HerdrDefaultSocket(), socket => HerdrCallEnvironment(ProcessEnvironment(), socket), herdr);

    private string? HerdrDefaultSocket()
    {
        try
        {
            return $"{_environment.HomeDirectory}/.config/herdr/herdr.sock";
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The environment of a herdr call: <paramref name="parent"/> pinned to the instance at <paramref name="socket"/>, or with no instance for <c>session list</c>.</summary>
    internal static IReadOnlyDictionary<string, string?> HerdrCallEnvironment(IReadOnlyDictionary<string, string?> parent, string? socket) =>
        socket is null ? Ah.Core.Mux.HerdrEnvironment.WithoutInstance(parent) : Ah.Core.Mux.HerdrEnvironment.ForInstance(parent, socket);

    internal static Dictionary<string, string?> ProcessEnvironment()
    {
        var map = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string name)
            {
                map[name] = entry.Value as string;
            }
        }

        return map;
    }

    private static void RegisterMesh(IRouteRegistry registry, ServiceGraph g, IAckTally acks, IHeardProcesses heard, Action<string> log)
    {
        var scope = new RequestScope();
        var routes = scope.Scoped(registry);
        var env = g.Environment;
        var rule = MeshModule.CreateMeshRule(g.Links, env);
        var effects = new MeshEffects(g, scope);
        var lookups = MeshModule.CreateContractLookups(g.Mailbox, g.Hierarchy.Core, g.Config);
        var links = MeshModule.CreateLinkCommands(
            rule, g.Links, g.Hierarchy.Core, g.Hierarchy.Messages, g.Io.Files, g.Io.Clock, effects, effects, g.RunFacts);
        var joins = MeshModule.CreateJoinCommands(
            rule, g.Links, g.Hierarchy.Core, g.Hierarchy.Messages, g.Io.Files, g.Io.Clock, g.Mailbox, lookups, g.Config, env,
            g.Io.Processes, effects, effects, g.RunFacts);
        var roles = MeshModule.CreateContractRoles(g.Config, ConfigModule.CreateRoleContracts(g.Config, g.Io.Files, env, g.Io.HookErrors));
        var sender = MeshModule.CreateMeshSender(g.Mailbox, rule, g.Links, g.Hierarchy, g.Io.Clock, g.Writer, g.Io.Files, runFacts: g.RunFacts);
        MeshModule.Register(
            routes,
            new MeshRouteDependencies(
                MeshModule.CreateContract(g.Io.Files, env),
                roles,
                lookups,
                sender,
                joins,
                links,
                g.Mailbox,
                StateModule.CreateRequestContextChecker(g.Io.Files, PlatformModule.CurrentUid(), g.HierarchyPolicy),
                log));
        MeshModule.RegisterMailboxRoutes(
            routes,
            new MailboxRouteDependencies(
                g.Mailbox,
                g.Roster,
                g.Config,
                StateModule.CreateRequestContextChecker(g.Io.Files, PlatformModule.CurrentUid(), g.HierarchyPolicy),
                g.Io.Files,
                CodexModule.RowAdapted,
                acks,
                g.Io.Processes,
                MeshModule.CreateRegistrationBinder(g.Io.Processes.PidAlive, PlatformModule.CreateProcessTable(g.Runner)),
                (pid, start, policy) => g.Hierarchy.Core.PidRunning(pid, start, policy),
                heard));
        MeshModule.RegisterDueRoute(routes, g.MailDue, g.Mailbox, g.Io.Files, log: message => g.Io.HookErrors?.Log(new HookErrorRecord("due-scanner", message)));
    }

    private Task<JsonArray> HerdrAgentsAsync(TimeSpan timeout, CancellationToken cancellation)
    {
        _ = timeout;
        cancellation.ThrowIfCancellationRequested();
        var agents = _seams.Value.Herdr.AgentList();
        if (agents is null)
        {
            throw new InvalidOperationException("herdr is not reachable");
        }

        var all = new JsonArray();
        foreach (var a in agents)
        {
            all.Add(a);
        }

        return Task.FromResult(all);
    }

    /// <summary>The open exchanges of a team, read from the hierarchy's message files.</summary>
    internal sealed class OpenExchangeSource(ServiceGraph graph) : IOpenExchangeSource
    {
        public IReadOnlyList<OpenExchange> OpenExchanges(string dir, string? team, bool allTeams)
        {
            var hier = graph.Hierarchy;
            IEnumerable<JsonValue> items = allTeams
                ? hier.Messages.ListLiveExchangesAllTeams(dir).Where(static e => e.Member("open")?.IsTruthy == true)
                : hier.Messages.OpenExchanges(dir, team is null ? JsonValue.Null : team, hier.Messages.ListLiveExchangesAcross(dir, team is null ? [] : [team]));
            var rows = new List<OpenExchange>();
            foreach (var e in items)
            {
                var id = e.Member("id") is { IsText: true } i ? i.AsString() : null;
                var path = e.Member("request")?.Member("path") is { IsText: true } p ? p.AsString() : null;
                if (id is null || path is null)
                {
                    continue;
                }

                rows.Add(new OpenExchange(id, e.Member("slug") is { IsText: true } s ? s.AsString() : null, path));
            }

            return rows;
        }

        public double AgeSeconds(JsonValue? timestamp) => graph.Hierarchy.Messages.AgeSecOf(timestamp);

        public JsonObject? FrontMatter(string path)
        {
            var parsed = graph.Hierarchy.Core.ReadMsgFile(path);
            return parsed.Member("fm") is { IsMap: true } fm ? fm.AsObject() : null;
        }
    }

    /// <summary>
    /// What the view source reads from services that are not composed yet: pending join requests and their decision
    /// commands (the join command layer) and the mesh policy of a team document. Each answers as "nothing there" rather than
    /// guess. Box counts come from the mailbox and a member's pane placement from the launch layer's paste service.
    /// </summary>
    internal sealed class ViewPlacement(ServiceGraph graph) : IViewPlacement
    {
        public JsonObject Placement(JsonObject memberRow, string? transport) =>
            graph.Launch.Paste.PlacementOf(JsonValue.FromObject(memberRow), transport);
    }

    /// <summary>The state the watch tick reports after each run, as the health body carries it.</summary>
    private sealed class WatchHealth(Func<bool> statusOwned, Func<long> unpulledAcks, Func<JsonObject?> load) : IHealthExtras
    {
        private WatchSnapshot? _last;

        internal void Observe(WatchSnapshot snapshot) => Volatile.Write(ref _last, snapshot);

        public HealthExtras Read()
        {
            var snapshot = Volatile.Read(ref _last);
            return snapshot is null
                ? HealthExtras.None with { StatusOwned = statusOwned(), UnpulledAcks = unpulledAcks(), Load = load() }
                : new HealthExtras(statusOwned(), snapshot.Timers, snapshot.Ladder, snapshot.TimerState, unpulledAcks(), load());
        }
    }
}
