using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Core.Time;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ah.Daemon;

/// <summary>Per-process daemon settings that are not read from the environment.</summary>
/// <param name="Timings">The time constants.</param>
/// <param name="HandleSignals">Whether SIGTERM and SIGINT start the drain.</param>
/// <param name="IdleExitMs">How long the daemon may sit with no request before it exits; the <c>--idle-exit-ms</c> option of <c>serve</c>. Null: the test-only knob, then thirty minutes.</param>
internal sealed record DaemonSettings(DaemonTimings Timings, bool HandleSignals, long? IdleExitMs = null)
{
    internal static DaemonSettings Production { get; } = new(DaemonTimings.Default, HandleSignals: true);
}

/// <summary>
/// The daemon: binds the socket, serves the registered routes under the concurrency model (one loop thread for in-memory
/// state, one worker thread per domain, reads on the request thread), runs the registered jobs and the fence and idle
/// job, and drains. It owns its lifetime: no generic host lifecycle, no SIGHUP handler.
/// </summary>
internal sealed class DaemonServer : IAsyncDisposable
{
    private readonly DaemonDependencies _deps;
    private readonly DaemonSettings _settings;
    private readonly Router _router;
    private readonly IReadOnlyList<JobRegistration> _jobs;
    private readonly TextWriter _log;
    private readonly object _logGate = new();
    private readonly SerialExecutor _loop = new("ahd-loop");
    private readonly SerialExecutor _jobExecutor = new("ahd-jobs");
    private readonly DomainWorkers _workers = new();
    private readonly CancellationTokenSource _drainCts = new();
    private readonly TaskCompletionSource _uiRestart = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _errorCounts = new(StringComparer.Ordinal);
    private readonly List<Func<bool>> _idleVetoes = [];
    private readonly List<Timer> _timers = [];
    private readonly List<PosixSignalRegistration> _signals = [];
    private readonly UiBridge _ui;
    private readonly BandToken _band;
    private readonly UiStarter? _uiStarter;
    private readonly string? _selfStart;
    private readonly string _bandToken;
    private readonly string _sock;
    private readonly string _info;
    private readonly string _root;
    private readonly string _version;
    private readonly long _idleExitMs;
    private readonly bool _stay;
    private readonly DateTimeOffset _startedAt;

    /// <summary>The spellings all requests share. A request runs on a flow of the server's own, so the scope the program opened around its run does not reach it.</summary>
    private readonly Func<IDisposable> _enterCases = Ah.Core.Text.PathGuard.ShareCases();

    private Socket? _owned;
    private WebApplication? _app;
    private ulong _ino;
    private int _draining;
    private int _drainWatched;
    private string _drainStep = "starting";
    private long _active;
    private long _handled;
    private long _healthCalls;
    private long _errors;
    private long _lastRequestMs;

    internal DaemonServer(
        DaemonDependencies deps,
        DaemonSettings settings,
        IReadOnlyList<ModuleRegistry> registries,
        TextWriter log,
        IReadOnlyList<JobRegistration>? registeredJobs = null,
        IReadOnlyList<RouteRegistration>? registeredRoutes = null,
        (Socket Socket, ulong Inode)? owned = null)
    {
        _deps = deps;
        _settings = settings;
        _log = log;
        _owned = owned?.Socket;
        _ino = owned?.Inode ?? 0;
        var env = deps.AhEnvironment;
        _sock = SocketPathIn(env.AhHome);
        _info = Path.Combine(env.AhHome, "run", "ahd.json");
        _bandToken = Path.Combine(env.AhHome, "run", "band.token");
        _band = new BandToken(deps.Writer, _bandToken, deps.ProcessId);
        _root = env.Plugin?.Root ?? string.Empty;
        _version = env.GetTestOnly("AHD_VERSION_OVERRIDE") is { Length: > 0 } over ? over : env.Plugin?.Version ?? "?";
        _idleExitMs = settings.IdleExitMs is > 0 ? settings.IdleExitMs.Value
            : long.TryParse(env.GetTestOnly("AHD_IDLE_EXIT_MS"), out var ms) && ms > 0 ? ms : 30 * 60 * 1000;
        _stay = ReadStay(deps.Files, Path.Combine(env.AhHome, "login.json"));
        _router = new Router(registries.SelectMany(static r => r.Routes).Concat(registeredRoutes ?? []).Concat(StateRoutes.Create(deps.State, _workers, Log)));
        _jobs = [.. registries.SelectMany(static r => r.Jobs), .. registeredJobs ?? []];
        _startedAt = deps.Clock.UtcNow;
        _lastRequestMs = MonotonicMs();
        _ui = new UiBridge(new UiBridgeOptions(
            deps.Clock,
            deps.Files,
            deps.Writer,
            Path.Combine(env.AhHome, "run"),
            _root.Length == 0 ? string.Empty : Path.Combine(_root, "web", "ui"),
            Log,
            ViewForBridge,
            deps.ProcessId,
            () => string.Equals(env.GetVariable("AH_UI"), "off", StringComparison.OrdinalIgnoreCase),
            settings.Timings.RequestHeaders,
            settings.Timings.KeepAlive,
            settings.Timings.RequestRead,
            _drainCts.Token)
        {
            Answer = deps.UiAnswer,
            TeamClose = deps.UiTeamClose,
            Rules = deps.UiRules,
        });
        _idleVetoes.Add(() => _ui.Active);
        _uiStarter = deps.UiStart is { } start ? new UiStarter(_ui, start, Path.Combine(env.AhHome, "run"), deps.ProcessId) : null;
        _selfStart = deps.UiStart?.StartTime(deps.ProcessId);
    }

    /// <summary>The loopback bridge: its listener exists only while a lease is held.</summary>
    internal UiBridge Ui => _ui;

    /// <summary>The one-shot token a press of the band button presents; it is new at every start and after each use.</summary>
    internal BandToken Band => _band;

    /// <summary>Completes when the start-time reading of the last holder's record is done; it does not hold startup up.</summary>
    internal Task UiRestart => _uiRestart.Task;

    /// <summary>The <c>GET /v1/view</c> handler of this daemon's own table, so the bridge serves the view documents through the one implementation.</summary>
    private Task<RouteResponse> ViewForBridge(RouteRequest request, CancellationToken cancellation) =>
        _router.Find("GET", request.Path is "/v1/view" or "/v1/view/" ? "/v1/view" : request.Path) is { } match
            ? match.Entry.Handle(request with { PathParameters = match.Parameters }, cancellation)
            : Task.FromResult(new RouteResponse(404, new Dictionary<string, string>(), JsonText.ToUtf8(new JsonObject { ["error"] = "not_found" }, 0)));

    /// <summary>The loop: the one serial executor for in-memory state.</summary>
    internal SerialExecutor Loop => _loop;

    internal bool Draining => Volatile.Read(ref _draining) != 0;

    internal string Version => _version;

    internal string SocketPath => _sock;

    /// <summary>Cancelled when the drain begins: a long-poll answers its <c>unchanged</c> body at once.</summary>
    internal CancellationToken DrainToken => _drainCts.Token;

    /// <summary>Requests accepted and not yet answered.</summary>
    internal long Active => Interlocked.Read(ref _active);

    /// <summary>The health fields other modules own: what <c>owns.status</c>, <c>owns.timers</c>, <c>ladder</c>, <c>timer_state</c> and <c>unpulled_acks</c> report. Runs on the loop.</summary>
    internal Func<HealthExtras>? HealthExtrasSource { get; set; }

    /// <summary>Run once during shutdown, after the drain and before the daemon's files are removed.</summary>
    internal Action? OnStop { get; set; }

    /// <summary>Registers a predicate that, while true, keeps the daemon from idle-exiting (live registrations, timers). Runs on the loop.</summary>
    internal void AddIdleVeto(Func<bool> busy)
    {
        lock (_idleVetoes)
        {
            _idleVetoes.Add(busy);
        }
    }

    internal void Log(string message)
    {
        lock (_logGate)
        {
            _log.Write($"{Timestamps.ToIsoString(_deps.Clock.UtcNow)} ahd {message}\n");
            _log.Flush();
        }
    }

    private static bool ReadStay(IFileReader files, string path)
    {
        try
        {
            return JsonText.Parse(files.ReadBytes(path)) is { IsMap: true } doc
                && doc.AsObject().TryGetValue("stay", out var v) && v.Kind == JsonKind.Bool && v.AsBool();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonParseException)
        {
            return false;
        }
    }

    // ---------------------------------------------------------------- run

    /// <summary>The daemon's socket under a state home.</summary>
    internal static string SocketPathIn(string ahHome) => Path.Combine(ahHome, "run", "ahd.sock");

    /// <summary>The log line of a bind that failed.</summary>
    internal static string BindFailure(string error) =>
        error.StartsWith("socket path", StringComparison.Ordinal) ? error : $"listen failed: {error}";

    /// <summary>Starts serving and completes with the process exit code once the daemon has drained or been fenced out.</summary>
    internal async Task<int> RunAsync()
    {
        if (_version == "?")
        {
            Log("plugin version unreadable; refusing to start");
            return 1;
        }

        if (_owned is null)
        {
            _owned = SocketBinder.Bind(_sock, out var bindError);
            if (_owned is null)
            {
                Log(BindFailure(bindError!));
                return 1;
            }

            _ino = _deps.Files.Stat(_sock)?.Inode ?? 0;
        }

        // Registering the jobs and routes can outlast a client's start lock, and the next client unlinks the socket and spawns another
        // daemon: this one is then bound to a path it no longer has, and listening there would leave a live pid nobody can reach.
        if (_deps.Files.Stat(_sock)?.Inode != _ino)
        {
            Fence("the socket was replaced before this daemon listened");
            return await _exit.Task.ConfigureAwait(false);
        }

        try
        {
            _app = BuildApp(_owned);
            await _app.StartAsync().ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or SocketException)
        {
            Log($"listen failed: {e.Message}");
            return 1;
        }

        _ui.RemoveStale();
        _band.Write();
        WriteInfo();
        Log($"listening on {_sock} version {_version} pid {_deps.ProcessId} root {_root}");
        StartJobs();
        _timers.Add(new Timer(UiIdleTick, null, UiIdleCheckPeriod, UiIdleCheckPeriod));
        if (_settings.HandleSignals)
        {
            _signals.Add(PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnSignal));
            _signals.Add(PosixSignalRegistration.Create(PosixSignal.SIGINT, OnSignal));
        }

        _ = Task.Run(async () =>
        {
            await UiStarter.ApplyRestartAsync(_uiStarter, Path.Combine(_deps.AhEnvironment.AhHome, "run"), _deps.ProcessId, Log, _drainCts.Token).ConfigureAwait(false);
            _uiRestart.SetResult();
        });
        return await _exit.Task.ConfigureAwait(false);
    }

    private static readonly TimeSpan UiIdleCheckPeriod = TimeSpan.FromSeconds(5);

    private async void UiIdleTick(object? state) => await GuardedIdleCheckAsync(_ui.CheckIdleAsync, Log).ConfigureAwait(false);

    /// <summary>Runs one idle check. A timer callback is <c>async void</c>, so an exception that escaped it would end the daemon; any failure is logged by type and the next tick tries again.</summary>
    internal static async Task GuardedIdleCheckAsync(Func<Task> check, Action<string> log)
    {
        try
        {
            await check().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            log($"bridge idle check failed: {e.GetType().Name}");
        }
    }

    internal void OnSignal(PosixSignalContext context)
    {
        context.Cancel = true;
        Log($"signal {context.Signal}");
        RequestShutdown(0);
    }

    private const string PeerPidItem = "ah.peer-pid";

    /// <summary>
    /// Reads the connector's pid when the connection is accepted, before any request byte is read: on macOS a later read names
    /// whichever process wrote last, so a forked child writing on the connection would otherwise be named instead of its connector.
    /// </summary>
    private ConnectionDelegate CapturePeer(ConnectionDelegate next) => context =>
    {
        if (_deps.Peers is { } peers && context.Features.Get<IConnectionSocketFeature>()?.Socket is { } accepted)
        {
            context.Items[PeerPidItem] = peers.PeerPid(accepted);
        }

        return next(context);
    };

    /// <summary>The principal of the connection's peer, read under one process-table reading; Anonymous with no resolver.</summary>
    private PrincipalKind CallerOf(HttpContext ctx)
    {
        using var scope = ProcessSnapshotScope.Begin(shared: true);
        return _deps.Principal?.Invoke(PeerPidOf(ctx)) ?? PrincipalKind.Anonymous;
    }

    private bool LaunchedByUser(HttpContext ctx)
    {
        using var scope = ProcessSnapshotScope.Begin(shared: true);
        return PeerPidOf(ctx) is { } pid && _deps.ParentOf?.Invoke(pid) is { } parent && (_deps.Principal?.Invoke(parent) == PrincipalKind.User || IsThisDaemon(parent));
    }

    private static int? PeerPidOf(HttpContext ctx) =>
        ctx.Features.Get<IConnectionItemsFeature>()?.Items.TryGetValue(PeerPidItem, out var pid) == true && pid is int value ? value : null;

    /// <summary>
    /// Reloading the default config files starts a file watcher, sampled on macOS calling sync() here: on a machine with busy disks
    /// that outlasts the client's start window, before this daemon has written ahd.json. Neither the daemon nor the UI bridge reads a config file.
    /// </summary>
    internal const string NoConfigReload = "--hostBuilder:reloadConfigOnChange=false";

    private WebApplication BuildApp(Socket socket)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
            Args = [NoConfigReload],
        });
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IHostLifetime, HostLifetime>();
        var t = _settings.Timings;
        builder.WebHost.ConfigureKestrel(o =>
        {
            o.AddServerHeader = false;
            o.Limits.MaxRequestBodySize = null;
            o.Limits.MinRequestBodyDataRate = null;
            o.Limits.RequestHeadersTimeout = t.RequestHeaders;
            o.Limits.KeepAliveTimeout = t.KeepAlive;
            o.ListenHandle((ulong)socket.Handle, listen => listen.Use(CapturePeer));
        });
        var app = builder.Build();
        app.Run(async ctx =>
        {
            using var cases = _enterCases();
            await HandleAsync(ctx).ConfigureAwait(false);
        });
        return app;
    }

    private void WriteInfo()
    {
        var doc = new JsonObject
        {
            ["v"] = 1,
            ["pid"] = _deps.ProcessId,
            ["version"] = _version,
            ["protocol"] = 1,
            ["socket"] = _sock,
            ["root"] = _root,
            ["started_at"] = Timestamps.ToIsoString(_startedAt),
        };
        var bytes = Utf8Text.Encode(JsonText.Stringify(doc, 0) + "\n");
        _deps.Writer.WriteAtomic(_info, $"{_info}.{_deps.ProcessId}.tmp", bytes);
    }

    // ---------------------------------------------------------------- ownership, fence, drain

    private JsonObject? ReadInfo()
    {
        try
        {
            return JsonText.Parse(_deps.Files.ReadBytes(_info)).AsObject();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonParseException)
        {
            return null;
        }
    }

    /// <summary>Whether <c>ahd.json</c> still names this pid and the socket file is still the inode this daemon bound.</summary>
    internal bool OwnsFiles()
    {
        var info = ReadInfo();
        if (info is null || !info.TryGetValue("pid", out var pid) || pid.Kind != JsonKind.Number || (int)pid.AsNumber() != _deps.ProcessId)
        {
            return false;
        }

        return _deps.Files.Stat(_sock) is { } stat && stat.Inode == _ino;
    }

    /// <summary>Leaves at once, removing nothing: another daemon owns the files now.</summary>
    private void Fence(string why)
    {
        Log($"fenced out ({why}); exiting");
        _ = _exit.TrySetResult(0);
    }

    /// <summary>Starts the drain: 503 for every new request, a wait for in-flight requests and running ops, cleanup, exit.</summary>
    internal void RequestShutdown(int code)
    {
        WatchDrain();

        // Its own thread: on a starved pool the drain must still begin at once, so new requests get 503 and no more work piles up.
        new Thread(() => _ = ShutdownAsync(code)) { IsBackground = true, Name = "ahd-drain-start" }.Start();
    }

    /// <summary>
    /// The bound on the whole drain, on threads of its own because a starved pool or a stuck stop hook is what it guards against: past
    /// <see cref="DaemonTimings.DrainDeadline"/> the process exits at once. The store takes that: it is WAL, with each commit on disk before it returns.
    /// </summary>
    private void WatchDrain()
    {
        if (_deps.HardExit is not { } hardExit || Interlocked.Exchange(ref _drainWatched, 1) != 0)
        {
            return;
        }

        new Thread(() =>
        {
            if (_exit.Task.Wait(_settings.Timings.DrainDeadline))
            {
                return;
            }

            // Logging and removing the files can block on what stuck the drain, so they get a moment and no more.
            new Thread(() =>
            {
                Thread.Sleep(TimeSpan.FromSeconds(2));
                hardExit();
            }) { IsBackground = true, Name = "ahd-drain-exit" }.Start();
            try
            {
                Log($"drain did not finish within {_settings.Timings.DrainDeadline.TotalSeconds.ToString(CultureInfo.InvariantCulture)} s (at: {Volatile.Read(ref _drainStep)}; open: {Interlocked.Read(ref _active).ToString(CultureInfo.InvariantCulture)} request(s), busy workers: {(_workers.BusyDomains is { Length: > 0 } busy ? busy : "none")}); exiting now");
                if (_ino == 0 || OwnsFiles())
                {
                    Cleanup();
                }
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // the next daemon takes over a dead pid's files
            }

            hardExit();
        }) { IsBackground = true, Name = "ahd-drain-deadline" }.Start();
    }

    private async Task ShutdownAsync(int code)
    {
        if (_ino != 0 && !OwnsFiles())
        {
            RunOnStop();
            Fence("not the owner at shutdown");
            return;
        }

        if (Interlocked.Exchange(ref _draining, 1) != 0)
        {
            return;
        }

        Log("draining");
        Volatile.Write(ref _drainStep, "bridge");
        await _ui.StopAsync("daemon stop", refuseNewLeases: true).ConfigureAwait(false);
        Volatile.Write(ref _drainStep, "in-flight work");
        await _drainCts.CancelAsync().ConfigureAwait(false);
        var hard = MonotonicMs() + (long)_settings.Timings.DrainHard.TotalMilliseconds;
        while ((Interlocked.Read(ref _active) > 0 || _workers.AnyBusy) && MonotonicMs() < hard)
        {
            await Task.Delay(50).ConfigureAwait(false);
        }

        if (_ino != 0 && !OwnsFiles())
        {
            RunOnStop();
            Fence("lost ownership before removing the socket");
            return;
        }

        Volatile.Write(ref _drainStep, "stop hook");

        // The hook gets StopHookLimit and no more: a hook still running is left to the exit, which the store takes (it is WAL, each
        // commit on disk before it returns), the same as the drain deadline's hard exit.
        var hook = new Thread(RunOnStop) { IsBackground = true, Name = "ahd-stop-hook" };
        hook.Start();
        if (!hook.Join(StopHookLimit))
        {
            Log($"stop hook still running after {StopHookLimit.TotalSeconds.ToString(CultureInfo.InvariantCulture)} s (its last \"stop:\" line names the step); exiting without it");
        }

        Cleanup();
        _ = _exit.TrySetResult(code);
    }

    /// <summary>How long the drain waits on the stop hook before it cleans up and exits without it.</summary>
    internal static readonly TimeSpan StopHookLimit = TimeSpan.FromSeconds(5);

    private void RunOnStop()
    {
        try
        {
            OnStop?.Invoke();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log("stop hook failed: " + e.GetType().Name);
        }
    }

    /// <summary>Removes <c>ahd.json</c> and, if it is still this daemon's inode, the socket. Only called while this daemon owns both.</summary>
    private void Cleanup()
    {
        var info = ReadInfo();
        if (info is null || !info.TryGetValue("pid", out var pid) || pid.Kind != JsonKind.Number || (int)pid.AsNumber() != _deps.ProcessId)
        {
            return;
        }

        var sockIsOurs = _deps.Files.Stat(_sock) is { } stat && stat.Inode == _ino;
        if (sockIsOurs)
        {
            _ = _deps.Writer.Unlink(_sock);
        }

        _ = _deps.Writer.Unlink(_info);
        _ = _deps.Writer.Unlink(_bandToken);

        // ahd.pid left naming this still-draining pid would make the successor's serve refuse to start.
        var pidFile = Path.Combine(Path.GetDirectoryName(_info)!, "ahd.pid");
        try
        {
            if (JsonText.Parse(_deps.Files.ReadBytes(pidFile)).Member("pid") is { IsNumber: true } named && (int)named.AsNumber() == _deps.ProcessId)
            {
                _ = _deps.Writer.Unlink(pidFile);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonParseException)
        {
            // no pid file, or not this daemon's
        }
    }

    // ---------------------------------------------------------------- jobs

    private void StartJobs()
    {
        // The fence and idle job: the one place the daemon checks that ahd.json and the socket still belong to it, and
        // whether it has been idle long enough to leave.
        var fence = new JobRegistration("fence-idle", _settings.Timings.FenceInterval, _ => Task.FromResult(FenceAndIdle()));
        foreach (var job in _jobs.Append(fence))
        {
            var stat = _jobStats[job.Name] = new JobStat(job.Period);
            if (job.Detail is { } detail)
            {
                _jobDetails[job.Name] = detail;
            }

            if (job.TakeCpuNs is { } meter)
            {
                _jobMeters[job.Name] = meter;
            }

            var running = 0;
            var period = job.Period;
            void Tick(object? _)
            {
                if (Draining && job.Name != "fence-idle")
                {
                    return;
                }

                if (Interlocked.Exchange(ref running, 1) != 0)
                {
                    return;
                }

                var queued = Stopwatch.GetTimestamp();
                _ = _jobExecutor.Run(() => RunJob(job, stat, queued)).Unwrap().ContinueWith(
                    _ => Interlocked.Exchange(ref running, 0),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }

            _timers.Add(new Timer(Tick, null, job.Name == "fence-idle" ? period : TimeSpan.Zero, period));
        }

        _timers.Add(new Timer(
            _ =>
            {
                Log(JobReport());
                if (RouteReport() is { } routes)
                {
                    Log(routes);
                }

                if (Ah.State.StateModule.TakeOpLine(_opsSeen) is { } ops)
                {
                    Log(ops);
                }
            },
            null,
            JobReportPeriod,
            JobReportPeriod));
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, JobStat> _routeStats = new(StringComparer.Ordinal);

    /// <summary>The op totals this daemon's last ops line reported; only the report timer touches it.</summary>
    private readonly Dictionary<string, (long Runs, long Ticks)> _opsSeen = new(StringComparer.Ordinal);

    /// <summary>
    /// One line per minute: each route that answered a request since the last line, with its count, total and longest handling time;
    /// null when none did.
    /// </summary>
    internal string? RouteReport()
    {
        var parts = new List<string>();
        foreach (var (route, stat) in _routeStats.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var (count, total, max, _, _) = stat.Take();
            if (count > 0)
            {
                parts.Add(string.Create(CultureInfo.InvariantCulture, $"{route} n={count} total={total:F0}ms max={max:F0}ms"));
            }
        }

        return parts.Count == 0 ? null : "routes last 60s: " + string.Join("; ", parts);
    }

    /// <summary>
    /// The route-ledger key of one request: a state request names its op (<c>POST state mail.heartbeat</c>), so the minute line shows
    /// which ops run; a pair <paramref name="known"/> does not register keeps the route's own key, so a caller cannot add keys.
    /// </summary>
    internal static string RouteStatKey(string routeKey, IReadOnlyDictionary<string, string> parameters, Func<string, string, bool>? known) =>
        routeKey.EndsWith(" /v1/state/{domain}/{op}", StringComparison.Ordinal) && known is not null
            && parameters.TryGetValue("domain", out var domain) && parameters.TryGetValue("op", out var op) && known(domain, op)
            ? routeKey[..routeKey.IndexOf(' ', StringComparison.Ordinal)] + " state " + domain + "." + op
            : routeKey;

    internal static readonly TimeSpan JobReportPeriod = TimeSpan.FromSeconds(60);

    private readonly Dictionary<string, JobStat> _jobStats = [];

    /// <summary>Counts and times the runs of one timer job. Updated by the job executor and read by the report timer, so every field moves by interlock.</summary>
    internal sealed class JobStat(TimeSpan period)
    {
        private long _runs;
        private long _ticks;
        private long _maxTicks;
        private long _waitTicks;
        private long _maxWaitTicks;

        public TimeSpan Period { get; } = period;

        /// <summary>Records the time one run spent queued on the job executor before its body started.</summary>
        public void AddWait(long ticks)
        {
            Interlocked.Add(ref _waitTicks, ticks);
            long seen;
            while (ticks > (seen = Interlocked.Read(ref _maxWaitTicks)) && Interlocked.CompareExchange(ref _maxWaitTicks, ticks, seen) != seen)
            {
            }
        }

        public long Runs => Interlocked.Read(ref _runs);

        private long _cpuNs;
        private int _cpuUnknown;

        /// <summary>Notes a run: its wall <paramref name="ticks"/> and the CPU nanoseconds its thread used, or null when the job body moved threads and the number would be wrong.</summary>
        public void Add(long ticks, long? cpuNs = null)
        {
            if (cpuNs is { } cpu)
            {
                Interlocked.Add(ref _cpuNs, cpu);
            }
            else
            {
                Interlocked.Exchange(ref _cpuUnknown, 1);
            }

            Interlocked.Increment(ref _runs);
            Interlocked.Add(ref _ticks, ticks);
            long seen;
            while (ticks > (seen = Interlocked.Read(ref _maxTicks)) && Interlocked.CompareExchange(ref _maxTicks, ticks, seen) != seen)
            {
            }
        }

        /// <summary>The CPU time since the last call as <c>12ms</c>, or <c>?</c> when any run since then could not be measured; then starts over.</summary>
        public string TakeCpu()
        {
            var ns = Interlocked.Exchange(ref _cpuNs, 0);
            return Interlocked.Exchange(ref _cpuUnknown, 0) == 1 ? "?" : string.Create(CultureInfo.InvariantCulture, $"{ns / 1_000_000.0:F0}ms");
        }

        /// <summary>The runs, total and longest time, and total and longest queue wait since the last call, then starts over.</summary>
        public (long Runs, double TotalMs, double MaxMs, double WaitMs, double MaxWaitMs) Take() =>
            (
                Interlocked.Exchange(ref _runs, 0),
                Interlocked.Exchange(ref _ticks, 0) * 1000.0 / Stopwatch.Frequency,
                Interlocked.Exchange(ref _maxTicks, 0) * 1000.0 / Stopwatch.Frequency,
                Interlocked.Exchange(ref _waitTicks, 0) * 1000.0 / Stopwatch.Frequency,
                Interlocked.Exchange(ref _maxWaitTicks, 0) * 1000.0 / Stopwatch.Frequency);
    }

    private TimeSpan _reportedProcessCpu;
    private readonly Dictionary<string, Func<string?>> _jobDetails = [];
    private readonly Dictionary<string, Func<long?>> _jobMeters = [];

    /// <summary>A job's CPU for the line: its own meter when it has one, else what the daemon thread measured around the run.</summary>
    private string CpuOf(string name, JobStat stat)
    {
        var measured = stat.TakeCpu();
        if (!_jobMeters.TryGetValue(name, out var meter))
        {
            return measured;
        }

        return meter() is { } ns ? string.Create(CultureInfo.InvariantCulture, $"{ns / 1_000_000.0:F0}ms") : "?";
    }
    private long _reportedCompleted;
    private long _reportedContention;

    /// <summary>
    /// One line per minute: each job that ran (its runs, total and longest wall time, and its period; jobs that did not run are left
    /// out), then the thread pool's thread count and queue, and how many work items it completed and how many lock waits it saw since the last line.
    /// </summary>
    internal string JobReport()
    {
        var parts = new List<string>();
        foreach (var (name, stat) in _jobStats.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var (runs, total, max, wait, maxWait) = stat.Take();
            if (runs > 0)
            {
                var detail = _jobDetails.TryGetValue(name, out var take) ? take() : null;
                parts.Add(string.Create(CultureInfo.InvariantCulture, $"{name} n={runs} total={total:F0}ms max={max:F0}ms wait_ms={wait:F0} wait_max_ms={maxWait:F0} period={stat.Period.TotalMilliseconds:F0}ms cpu={CpuOf(name, stat)}{(string.IsNullOrEmpty(detail) ? string.Empty : " " + detail)}"));
            }
        }

        var processCpu = Process.GetCurrentProcess().TotalProcessorTime;
        var processCpuNow = processCpu - _reportedProcessCpu;
        _reportedProcessCpu = processCpu;

        var completed = ThreadPool.CompletedWorkItemCount;
        var contention = Monitor.LockContentionCount;
        var completedNow = completed - Interlocked.Exchange(ref _reportedCompleted, completed);
        var contentionNow = contention - Interlocked.Exchange(ref _reportedContention, contention);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"jobs last 60s: {(parts.Count == 0 ? "none" : string.Join("; ", parts))}; pool threads={ThreadPool.ThreadCount} pending={ThreadPool.PendingWorkItemCount} completed=+{completedNow} lock-waits=+{contentionNow} proc-cpu={processCpuNow.TotalMilliseconds:F0}ms");
    }

    private Task RunJob(JobRegistration job, JobStat stat, long queued)
    {
        var started = Stopwatch.GetTimestamp();
        stat.AddWait(started - queued);
        var cpuStart = _deps.ThreadCpuNs?.Invoke();
        try
        {
            var task = job.Run(_drainCts.Token);
            long? cpuNs = task.IsCompleted && cpuStart is { } from && _deps.ThreadCpuNs?.Invoke() is { } to ? to - from : null;
            return task.ContinueWith(
                t =>
                {
                    stat.Add(Stopwatch.GetTimestamp() - started, cpuNs);
                    if (t.IsFaulted)
                    {
                        Log($"job {job.Name} failed: {t.Exception!.GetBaseException().Message}");
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        catch (Exception e)
        {
            stat.Add(Stopwatch.GetTimestamp() - started, null);
            Log($"job {job.Name} failed: {e.Message}");
            return Task.CompletedTask;
        }
    }

    private bool FenceAndIdle()
    {
        if (_exit.Task.IsCompleted)
        {
            return true;
        }

        if (!OwnsFiles())
        {
            Fence("ahd.json or the socket no longer belongs to this daemon");
            return true;
        }

        if (Draining)
        {
            return true;
        }

        var idleFor = MonotonicMs() - Interlocked.Read(ref _lastRequestMs);
        if (!_stay && idleFor >= _idleExitMs && !_workers.AnyBusy && !AnyVeto())
        {
            Log("idle with no live registration");
            RequestShutdown(0);
        }

        return true;
    }

    /// <summary>Idle time and the drain's in-flight wait (<c>DrainHard</c>) are measured on a clock that only moves forward, so a wall-clock step cannot end or extend either; the drain deadline is wall time.</summary>
    private long MonotonicMs() => _deps.MonotonicMs is { } read ? read() : Environment.TickCount64;

    private bool AnyVeto()
    {
        Func<bool>[] vetoes;
        lock (_idleVetoes)
        {
            vetoes = [.. _idleVetoes];
        }

        return vetoes.Any(static v => v());
    }

    // ---------------------------------------------------------------- requests

    internal async Task HandleAsync(HttpContext ctx)
    {
        _ = Interlocked.Increment(ref _active);
        Interlocked.Exchange(ref _lastRequestMs, MonotonicMs());
        try
        {
            await ServeAsync(ctx).ConfigureAwait(false);
        }
        finally
        {
            _ = Interlocked.Decrement(ref _active);
        }
    }

    private async Task ServeAsync(HttpContext ctx)
    {
        var method = ctx.Request.Method;
        var path = ctx.Request.Path.Value ?? "/";
        var isHealth = path == "/v1/health";
        try
        {
            if (Draining)
            {
                await ReplyErrorAsync(ctx, isHealth, 503, "draining", "the daemon is shutting down", new() { ["Retry-After"] = "1" }).ConfigureAwait(false);
                return;
            }

            if (isHealth && method == "GET")
            {
                await ReplyJsonAsync(ctx, true, 200, BuildHealth()).ConfigureAwait(false);
                return;
            }

            if (path == "/v1/drain" && method == "POST")
            {
                var drainBody = await ReadBodyAsync(ctx, isHealth).ConfigureAwait(false);
                if (drainBody is null)
                {
                    return;
                }

                var reason = JsonText.Parse(drainBody.Value.Span).AsObject()?.Str("reason");
                reason = reason is null ? null : string.Concat(reason.Where(static c => !char.IsControl(c)));
                var caller = CallerOf(ctx);
                var peer = PeerPidOf(ctx) is { } p ? p.ToString(System.Globalization.CultureInfo.InvariantCulture) : "unknown";
                // A test-mode daemon's socket lies in its sandbox AH_HOME, so any caller that reached it named that sandbox home.
                if (caller == PrincipalKind.Anonymous && !_deps.AhEnvironment.TestMode)
                {
                    Log($"drain refused: caller pid {peer} is neither a session nor the user");
                    await ReplyErrorAsync(ctx, false, 403, "anonymous", "a drain needs a caller the daemon can name: a session or the user", null).ConfigureAwait(false);
                    return;
                }

                Log($"drain requested{(reason is { Length: > 0 } ? $": {(reason.Length > 80 ? reason[..80] : reason)}" : string.Empty)} (by {caller.ToString().ToLowerInvariant()}, pid {peer})");
                await ReplyJsonAsync(ctx, false, 200, Jx.Ok()).ConfigureAwait(false);
                RequestShutdown(0);
                return;
            }

            if (path == UiLeasePath)
            {
                await ServeLeaseAsync(ctx).ConfigureAwait(false);
                return;
            }

            if (path == UiStartPath)
            {
                await ServeUiStartAsync(ctx).ConfigureAwait(false);
                return;
            }

            if (path == TeamClosePath)
            {
                await ServeTeamCloseAsync(ctx).ConfigureAwait(false);
                return;
            }

            var match = path is "/v1/health" or "/v1/drain" ? null : _router.Find(method, path);
            if (match is null)
            {
                var methods = new List<string>(_router.MethodsFor(path));
                if (path == "/v1/health")
                {
                    methods.Insert(0, "GET");
                }

                if (path == "/v1/drain")
                {
                    methods.Insert(0, "POST");
                }

                if (methods.Count > 0)
                {
                    await ReplyErrorAsync(ctx, isHealth, 405, "bad_request", $"{method} is not allowed here", new() { ["Allow"] = string.Join(", ", methods.Distinct()) }).ConfigureAwait(false);
                }
                else
                {
                    await ReplyErrorAsync(ctx, isHealth, 404, "not_found", $"no such path {path}", null).ConfigureAwait(false);
                }

                return;
            }

            ReadOnlyMemory<byte> body = default;
            if (method == "POST")
            {
                var read = await ReadBodyAsync(ctx, isHealth).ConfigureAwait(false);
                if (read is null)
                {
                    return;
                }

                body = read.Value;
            }

            var request = new RouteRequest(
                method,
                path,
                ctx.Request.QueryString.HasValue ? ctx.Request.QueryString.Value![1..] : string.Empty,
                ctx.Request.Headers.ToDictionary(static h => h.Key.ToLowerInvariant(), static h => h.Value.ToString(), StringComparer.Ordinal),
                body,
                match.Parameters,
                PeerPidOf(ctx));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, _drainCts.Token);
            RouteResponse response;
            var handling = Stopwatch.GetTimestamp();
            try
            {
                using (ProcessSnapshotScope.Begin())
                {
                    response = await match.Entry.Handle(request, linked.Token).ConfigureAwait(false);
                }
            }
            finally
            {
                _routeStats.GetOrAdd(RouteStatKey(match.Entry.Key, match.Parameters, _deps.State is { } state ? state.HasOp : null), static _ => new JobStat(TimeSpan.Zero)).Add(Stopwatch.GetTimestamp() - handling);
            }

            await ReplyAsync(ctx, isHealth, response.Status, response.Headers, response.Body, null).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
        {
            // the client went away
        }
        catch (OperationCanceledException) when (Draining)
        {
            await ReplyErrorAsync(ctx, isHealth, 503, "draining", "the daemon is shutting down", new() { ["Retry-After"] = "1" }).ConfigureAwait(false);
        }
        catch (StoreUnavailableException e)
        {
            Log($"request failed: {e.Message}");
            if (!ctx.Response.HasStarted)
            {
                await ReplyErrorAsync(ctx, isHealth, 503, "store-unavailable", e.Message, new() { ["Retry-After"] = "1" }).ConfigureAwait(false);
            }
        }
        catch (Exception e)
        {
            Log($"request failed: {e}");
            _deps.ErrorLog?.Log("ahd", e.ToString());
            if (!ctx.Response.HasStarted)
            {
                await ReplyErrorAsync(ctx, isHealth, 500, "internal", "internal error", null).ConfigureAwait(false);
            }
        }
    }

    private const string UiLeasePath = "/v1/ui/lease";

    private const string UiStartPath = "/v1/ui/start";

    private const string TeamClosePath = "/v1/team/close";

    /// <summary><c>POST /v1/team/close</c>, on the socket: the user only, by the same principal check as <c>/v1/ui/start</c>, or a process the Herdr desktop app started with no harness on its line (recorded <c>by: ui</c>, as the bridge's close is). A session gets <c>403 user_only</c>.</summary>
    private async Task ServeTeamCloseAsync(HttpContext ctx)
    {
        var method = ctx.Request.Method;
        if (method != "POST")
        {
            await ReplyErrorAsync(ctx, false, 405, "bad_request", $"{method} is not allowed here", new() { ["Allow"] = "POST" }).ConfigureAwait(false);
            return;
        }

        var body = await ReadBodyAsync(ctx, false).ConfigureAwait(false);
        if (body is null)
        {
            return;
        }

        var user = CallerOf(ctx) == PrincipalKind.User;
        if (!user && !(PeerPidOf(ctx) is { } peer && _deps.HerdrAppChild?.Invoke(peer) == true))
        {
            await ReplyErrorAsync(ctx, false, 403, "user_only", "a team is closed or reopened only by the user", null).ConfigureAwait(false);
            return;
        }

        if (_deps.UiTeamClose is not { } support)
        {
            await ReplyErrorAsync(ctx, false, 503, "unavailable", "this daemon cannot close a team", null).ConfigureAwait(false);
            return;
        }

        // not cancelled by the client going away: sessions may already be closing
        var (status, reply, _) = await Task.Run(() => TeamCloseRoute.Run(support, body.Value.Span, user ? "user" : "ui"),CancellationToken.None).ConfigureAwait(false);
        await ReplyJsonAsync(ctx, false, status, reply).ConfigureAwait(false);
    }

    /// <summary>Whether <paramref name="pid"/> is this daemon: its pid, and the start time it had when the daemon began (a reused pid is not).</summary>
    private bool IsThisDaemon(int pid) =>
        pid == _deps.ProcessId && _deps.UiStart is { } start
        && ProcessStart.Compare(start.StartTime(pid), () => null, _selfStart) is StartMatch.Same or StartMatch.Legacy;

    /// <summary>
    /// <c>POST /v1/ui/start</c>, on the socket only: has the browser page running and handed to the system's opener, and answers
    /// <c>{status, opened}</c> and nothing else, so the one-time code and the address never reach the caller. The user needs no proof;
    /// a session is accepted only with the band token, which a press of the band button reads from its file and which is replaced at
    /// once. Any other caller, a wrong token or none, gets <c>403 user_only</c> and the token stays.
    /// </summary>
    private async Task ServeUiStartAsync(HttpContext ctx)
    {
        var method = ctx.Request.Method;
        if (method != "POST")
        {
            await ReplyErrorAsync(ctx, false, 405, "bad_request", $"{method} is not allowed here", new() { ["Allow"] = "POST" }).ConfigureAwait(false);
            return;
        }

        var body = await ReadBodyAsync(ctx, false).ConfigureAwait(false);
        if (body is null)
        {
            return;
        }

        var caller = CallerOf(ctx);
        if (caller != PrincipalKind.User && !(caller == PrincipalKind.Session && _band.TryUse(JsonText.Parse(body.Value.Span).AsObject()?.Str("token"))))
        {
            await ReplyErrorAsync(ctx, false, 403, "user_only", "the UI bridge is opened only by the user, from a terminal", null).ConfigureAwait(false);
            return;
        }

        if (_uiStarter is null)
        {
            await ReplyErrorAsync(ctx, false, 503, "ui_unavailable", "this daemon cannot start the page", null).ConfigureAwait(false);
            return;
        }

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, _drainCts.Token);
        limit.CancelAfter(UiStartLimit);
        UiStartResult result;
        try
        {
            result = await _uiStarter.RunAsync(limit.Token).ConfigureAwait(false);
        }
        catch (UiDisabledException e)
        {
            await ReplyErrorAsync(ctx, false, 403, "ui_disabled", e.Message, null).ConfigureAwait(false);
            return;
        }
        catch (UiClosedException)
        {
            await ReplyErrorAsync(ctx, false, 503, "draining", "the daemon is shutting down", new() { ["Retry-After"] = "1" }).ConfigureAwait(false);
            return;
        }
        catch (UiBindException)
        {
            await ReplyErrorAsync(ctx, false, 503, "bridge_bind", "the bridge could not bind a loopback listener", null).ConfigureAwait(false);
            return;
        }
        catch (OperationCanceledException)
        {
            await ReplyErrorAsync(ctx, false, 503, "ui_start_timeout", "the page did not start in time", null).ConfigureAwait(false);
            return;
        }

        if (result.Kind is UiStartKind.Started or UiStartKind.Reused)
        {
            await ReplyJsonAsync(ctx, false, 200, new JsonObject { ["status"] = result.Kind == UiStartKind.Started ? "started" : "reused", ["opened"] = result.Opened }).ConfigureAwait(false);
            return;
        }

        await ReplyErrorAsync(ctx, false, 503, result.Kind == UiStartKind.Busy ? "ui_busy" : "ui_service", result.Message ?? "the page did not start", null).ConfigureAwait(false);
    }

    private static readonly TimeSpan UiStartLimit = TimeSpan.FromSeconds(12);

    /// <summary>
    /// <c>POST /v1/ui/lease</c>, on the socket only: opens a bridge lease. The reply is held open after its first line, which carries
    /// the port and the code; the lease lasts until the client hangs up or the bridge ends it, and the reply then ends.
    /// </summary>
    private async Task ServeLeaseAsync(HttpContext ctx)
    {
        var method = ctx.Request.Method;
        if (method != "POST")
        {
            await ReplyErrorAsync(ctx, false, 405, "bad_request", $"{method} is not allowed here", new() { ["Allow"] = "POST" }).ConfigureAwait(false);
            return;
        }

        // the bridge is for a human at a terminal; a session or an unknown caller gets no lease. The background holder of `ahd ui start`
        // has no terminal of its own, so a service lease is also given to a process whose parent is the user (the `start` that launched it)
        if (CallerOf(ctx) != PrincipalKind.User && !(ctx.Request.Query["service"] == "1" && LaunchedByUser(ctx)))
        {
            await ReplyErrorAsync(ctx, false, 403, "user_only", "the UI bridge is opened only by the user, from a terminal", null).ConfigureAwait(false);
            return;
        }

        UiGrant grant;
        try
        {
            grant = await _ui.OpenLeaseAsync(PeerPidOf(ctx) ?? 0, ctx.RequestAborted, ctx.Request.Query["service"] == "1").ConfigureAwait(false);
        }
        catch (UiDisabledException e)
        {
            await ReplyErrorAsync(ctx, false, 403, "ui_disabled", e.Message, null).ConfigureAwait(false);
            return;
        }
        catch (UiClosedException)
        {
            await ReplyErrorAsync(ctx, false, 503, "draining", "the daemon is shutting down", new() { ["Retry-After"] = "1" }).ConfigureAwait(false);
            return;
        }
        catch (UiBindException)
        {
            await ReplyErrorAsync(ctx, false, 503, "bridge_bind", "the bridge could not bind a loopback listener", null).ConfigureAwait(false);
            return;
        }

        try
        {
            var answer = new JsonObject { ["port"] = grant.Port };
            if (grant.MovedFrom != 0)
            {
                answer["moved_from"] = grant.MovedFrom;
            }

            var first = Utf8Text.Encode(JsonText.Stringify(answer, 0) + "\n");
            await _loop.Run(() => Count(false, 200, null, first)).ConfigureAwait(false);
            var res = ctx.Response;
            res.StatusCode = 200;
            res.ContentType = "application/json";
            res.Headers["Ah-Daemon-Version"] = _version;
            res.Headers["Ah-Protocol"] = "1";
            await res.Body.WriteAsync(first, ctx.RequestAborted).ConfigureAwait(false);
            await res.Body.FlushAsync(ctx.RequestAborted).ConfigureAwait(false);
            using var held = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, _drainCts.Token);
            try
            {
                await grant.Lease.Ended.Task.WaitAsync(held.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // the client hung up, or the daemon is draining
            }
        }
        finally
        {
            await _ui.DropLeaseAsync(grant.Lease).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The request body as JSON object bytes (<c>{}</c> when empty), up to the route's cap. A body over the cap is refused
    /// with 413 and read and thrown away for at most 1 MiB or 2 s so the client sees the answer. Null when the request was answered.
    /// </summary>
    private async Task<ReadOnlyMemory<byte>?> ReadBodyAsync(HttpContext ctx, bool isHealth)
    {
        var cap = BodyCaps.Of(ctx.Request.Method, ctx.Request.Path.Value ?? "/");
        var declared = ctx.Request.ContentLength;
        if (declared is { } d && d > cap)
        {
            await RefuseTooLargeAsync(ctx, isHealth, cap, $"{d} bytes").ConfigureAwait(false);
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
        timeout.CancelAfter(_settings.Timings.RequestRead);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        try
        {
            int read;
            while ((read = await ctx.Request.Body.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > cap)
                {
                    await RefuseTooLargeAsync(ctx, isHealth, cap, "over the limit").ConfigureAwait(false);
                    return null;
                }

                buffer.Write(chunk, 0, read);
            }
        }
        catch (OperationCanceledException) when (!ctx.RequestAborted.IsCancellationRequested)
        {
            await ReplyErrorAsync(ctx, isHealth, 408, "request_timeout", "the request was not received in time", new() { ["Connection"] = "close" }).ConfigureAwait(false);
            return null;
        }

        if (buffer.Length == 0)
        {
            return "{}"u8.ToArray();
        }

        var bytes = buffer.ToArray();
        if (!JsonText.TryParse(bytes, out var parsed) || parsed.Kind != JsonKind.Map)
        {
            await ReplyErrorAsync(ctx, isHealth, 400, "bad_request", "body must be a JSON object", null).ConfigureAwait(false);
            return null;
        }

        return bytes;
    }

    private async Task RefuseTooLargeAsync(HttpContext ctx, bool isHealth, int cap, string what)
    {
        var extra = new JsonObject { ["max_bytes"] = cap };
        await ReplyErrorAsync(
            ctx, isHealth, 413, "too_large", $"body is {what}; the limit is {cap}", new() { ["Connection"] = "close" }, extra).ConfigureAwait(false);
        await ctx.Response.CompleteAsync().ConfigureAwait(false);

        // Let the refused request finish arriving before the connection goes, or a client still writing gets a reset
        // before it can read the 413. No FIN is sent first: a client that sees one stops writing.
        using var cts = new CancellationTokenSource(_settings.Timings.DiscardTime);
        var buffer = new byte[16 * 1024];
        long discarded = 0;
        try
        {
            int read;
            while (discarded < _settings.Timings.DiscardBytes
                && (read = await ctx.Request.Body.ReadAsync(buffer, cts.Token).ConfigureAwait(false)) > 0)
            {
                discarded += read;
            }
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or InvalidOperationException)
        {
            // the client closed or the window passed
        }
    }

    private JsonObject BuildHealth()
    {
        var extras = HealthExtrasSource?.Invoke() ?? HealthExtras.None;
        var calls = Interlocked.Increment(ref _healthCalls);
        var byCode = new JsonObject();
        foreach (var (code, count) in _errorCounts)
        {
            byCode[code] = count;
        }

        var health = new JsonObject
        {
            ["pid"] = _deps.ProcessId,
            ["version"] = _version,
            ["protocol"] = 1,
            ["protocols"] = Jx.Arr([1, 2]),
            ["impl"] = "native",
            ["started_at"] = Timestamps.ToIsoString(_startedAt),
            ["root"] = _root,
            ["owns"] = new JsonObject
            {
                ["mailbox"] = true,
                ["status"] = extras.StatusOwned,
                ["timers"] = Jx.Arr(extras.Timers.Select(static t => (JsonValue)t)),
            },
            ["ladder"] = extras.Ladder,
            ["timer_state"] = extras.TimerState,
            ["requests"] = Interlocked.Read(ref _handled),
            ["health_calls"] = calls,
            ["unpulled_acks"] = extras.UnpulledAcks,
            ["errors"] = Interlocked.Read(ref _errors),
            ["errors_by_code"] = byCode,
            ["load"] = extras.Load is null ? JsonValue.Null : extras.Load,
            ["gc"] = GcStats(),
        };
        return health;
    }

    /// <summary>The managed heap now: what it holds, what it has allocated in all, and how often each generation was collected (b459).</summary>
    private static JsonObject GcStats()
    {
        var info = GC.GetGCMemoryInfo();
        return new JsonObject
        {
            ["heap_bytes"] = info.HeapSizeBytes,
            ["committed_bytes"] = info.TotalCommittedBytes,
            ["fragmented_bytes"] = info.FragmentedBytes,
            ["live_bytes"] = GC.GetTotalMemory(false),
            ["allocated_bytes"] = GC.GetTotalAllocatedBytes(false),
            ["gen0"] = GC.CollectionCount(0),
            ["gen1"] = GC.CollectionCount(1),
            ["gen2"] = GC.CollectionCount(2),
            ["census"] = Ah.Core.CoreModule.CensusRead(),
        };
    }

    private Task ReplyJsonAsync(HttpContext ctx, bool isHealth, int status, JsonObject body) =>
        ReplyAsync(ctx, isHealth, status, null, JsonText.ToUtf8(body, 0), null);

    private Task ReplyErrorAsync(
        HttpContext ctx, bool isHealth, int status, string code, string message, Dictionary<string, string>? headers, JsonObject? extra = null)
    {
        var error = new JsonObject { ["code"] = code, ["message"] = message };
        if (extra is not null)
        {
            foreach (var key in extra.Keys)
            {
                error[key] = extra[key];
            }
        }

        var body = new JsonObject { ["error"] = error };
        return ReplyAsync(ctx, isHealth, status, headers, JsonText.ToUtf8(body, 0), code);
    }

    /// <summary>
    /// Counts the answer, then writes it (a health answer is counted on the request thread, so it never waits behind the loop): a health call made after this one sees it. A handler's own loop
    /// state change is made before it returns its response, so it too precedes the answer.
    /// </summary>
    private async Task ReplyAsync(
        HttpContext ctx, bool isHealth, int status, IReadOnlyDictionary<string, string>? headers, ReadOnlyMemory<byte> body, string? errorCode)
    {
        if (isHealth)
        {
            _ = Count(isHealth, status, errorCode, body);
        }
        else
        {
            await _loop.Run(() => Count(isHealth, status, errorCode, body)).ConfigureAwait(false);
        }

        var res = ctx.Response;
        res.StatusCode = status;
        res.ContentType = "application/json";
        res.ContentLength = body.Length;
        res.Headers["Ah-Daemon-Version"] = _version;
        res.Headers["Ah-Protocol"] = "1";
        if (headers is not null)
        {
            foreach (var (k, v) in headers)
            {
                res.Headers[k] = v;
            }
        }

        if (Draining)
        {
            res.Headers.Connection = "close";
        }

        var rejected = !isHealth && (status < 200 || status >= 300);
        try
        {
            await res.Body.WriteAsync(body, ctx.RequestAborted).ConfigureAwait(false);
            if (rejected)
            {
                await res.Body.FlushAsync(ctx.RequestAborted).ConfigureAwait(false);
            }
        }
        finally
        {
            // after the reply is on the wire, so a slow log file delays nothing the caller waits for; and also when the caller
            // hung up, which is a refusal worth seeing
            if (rejected)
            {
                RecordRejection(ctx, status, errorCode, body);
            }
        }
    }

    /// <summary>Hands one refused request to the rejection log. What it says is the request's line and the refusal, never the body.</summary>
    private void RecordRejection(HttpContext ctx, int status, string? errorCode, ReadOnlyMemory<byte> body)
    {
        if (_deps.Rejections is not { } log || IsDueProbe(ctx, status))
        {
            return;
        }

        try
        {
            string? message = null;
            string? field = null;
            if (JsonText.TryParse(body.Span, out var parsed))
            {
                message = Jx.ErrorField(parsed, "message");
                field = Jx.ErrorField(parsed, "field");
            }

            var headers = ctx.Request.Headers;
            string? Header(string name) => headers.TryGetValue(name, out var v) && v.ToString().Length > 0 ? v.ToString() : null;
            log.Record(new RejectionRecord(
                ctx.Request.Method,
                ctx.Request.Path.Value ?? string.Empty,
                status,
                ErrorCodeOf(errorCode, body),
                message,
                field,
                PeerPidOf(ctx),
                Header("Ah-Client-Version"),
                Header("Ah-Caller")));
        }
        catch (Exception)
        {
            // the record is telemetry: it never changes the reply
        }
    }

    /// <summary>
    /// <c>ah msg due</c> asks every daemon whether it serves <c>GET /v1/due</c> with a request that names no session; a daemon
    /// that has the route is meant to answer 400. That answer is the probe working, not a client being refused, so it is not
    /// logged: it would otherwise fill the rejection log with rows a reader looking for a wire mismatch has to discard.
    /// </summary>
    private static bool IsDueProbe(HttpContext ctx, int status) =>
        status == 400
        && string.Equals(ctx.Request.Method, "GET", StringComparison.Ordinal)
        && string.Equals(ctx.Request.Path.Value + ctx.Request.QueryString.Value, Ah.Mesh.MeshModule.DueProbePath, StringComparison.Ordinal);

    private static string ErrorCodeOf(string? errorCode, ReadOnlyMemory<byte> body) =>
        errorCode ?? (JsonText.TryParse(body.Span, out var parsed) && Jx.ErrorCode(parsed) is { } fromBody ? fromBody : "unknown");

    private bool Count(bool isHealth, int status, string? errorCode, ReadOnlyMemory<byte> body)
    {
        if (!isHealth)
        {
            _ = Interlocked.Increment(ref _handled);
        }

        if (status >= 500)
        {
            _ = Interlocked.Increment(ref _errors);
        }

        if (status < 200 || status >= 300)
        {
            var code = ErrorCodeOf(errorCode, body);
            _ = _errorCounts.AddOrUpdate(code, 1, static (_, n) => n + 1);
        }

        return true;
    }

    // ---------------------------------------------------------------- disposal

    /// <summary>Stops listening and releases the threads. Not called on a process exit, which leaves the socket path as it is.</summary>
    public async ValueTask DisposeAsync()
    {
        foreach (var t in _timers)
        {
            await t.DisposeAsync().ConfigureAwait(false);
        }

        foreach (var s in _signals)
        {
            s.Dispose();
        }

        await _ui.DisposeAsync().ConfigureAwait(false);

        if (_app is not null)
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            try
            {
                await _app.StopAsync(stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // connections still open are dropped with the listener
            }

            await _app.DisposeAsync().ConfigureAwait(false);
        }

        _owned?.Dispose();
        _workers.Dispose();
        _jobExecutor.Dispose();
        _loop.Dispose();
        _drainCts.Dispose();
    }
}

/// <summary>The health fields that modules other than the host own.</summary>
/// <param name="StatusOwned">Whether the daemon watches any hierarchy status document.</param>
/// <param name="Timers">The session ids the daemon keeps the dispatch clock for.</param>
/// <param name="Ladder">The delivery ladder's counts.</param>
/// <param name="TimerState">Per-session last tick and last event.</param>
/// <param name="UnpulledAcks">Acks since start for an entry no pull returned.</param>
/// <param name="Load">The machine-load object of the health body; null when no monitor runs.</param>
public sealed record HealthExtras(bool StatusOwned, IReadOnlyList<string> Timers, JsonObject Ladder, JsonObject TimerState, long UnpulledAcks, JsonObject? Load = null)
{
    public static HealthExtras None { get; } = new(
        false,
        [],
        new JsonObject { ["pending"] = 0, ["sending"] = 0, ["paused"] = 0, ["escalated"] = 0 },
        new JsonObject(),
        0);
}

/// <summary>Where the daemon reads the health fields that modules other than the host own; read on the daemon's loop for every health call.</summary>
public interface IHealthExtras
{
    /// <summary>The current values. Must not block.</summary>
    HealthExtras Read();
}
