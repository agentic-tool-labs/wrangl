using System.Diagnostics;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Core.Time;

namespace Ah.DaemonClient;

internal sealed record HttpJsonReply(int Status, JsonValue Body);

/// <summary>A daemon that answered discovery: draining (exiting, not down), or healthy with its <c>ahd.json</c> and health body.</summary>
internal sealed record Discovered(bool Draining, int Pid, JsonObject? Info, JsonObject? Health)
{
    internal string Socket => Info!.Str("socket")!;
}

internal sealed record EnsureOptions(
    int BudgetMs = 6000,
    bool Hook = false,
    Action<string, string>? Warn = null,
    bool Start = true,
    bool Explicit = false,
    bool AnyVersion = false);

/// <summary>
/// Discovery, lazy start, the version and runtime handshake, and a request helper. "Daemon unavailable" is an
/// ordinary outcome (null), never an error: the caller has the mailbox files to fall back on.
/// </summary>
internal sealed class AhdClient(DaemonClientDependencies deps) : IDaemonClient
{
    internal const int DiscoverTimeoutMs = 500;
    internal const int HungProbeMs = 10_000;
    internal const int StartWaitMs = 3000;
    internal const int SpawnBackoffMs = 60_000;
    private int DrainWaitMs => deps.DrainWaitMs;
    private const int HookDrainTimeoutMs = 300;
    private const long LogRotateBytes = 5L * 1024 * 1024;
    private const long FallbackRotateBytes = 1024 * 1024;

    private readonly RunPaths _paths = new(deps.AhEnvironment.AhHome);

    /// <summary>The longest one request may take, discovery and start included; past it the reply is "daemon busy".</summary>
    internal int RequestDeadlineMs { get; set; } = 45_000;

    internal RunPaths Paths => _paths;

    internal bool DaemonOff => Var("AH_DAEMON") == "off";

    /// <summary>The version this process reports. The override is honoured only in test mode.</summary>
    internal string EffectiveVersion =>
        deps.AhEnvironment.GetTestOnly("AHD_VERSION_OVERRIDE") is { Length: > 0 } overridden
            ? overridden
            : deps.AhEnvironment.Plugin?.Version ?? "?";

    internal DaemonClientDependencies Dependencies => deps;

    private static long Now() => Environment.TickCount64;

    private string? Var(string name) => deps.ProcessEnvironment.TryGetValue(name, out var v) ? v : null;

    // ---------------------------------------------------------------- the spawn-failed marker

    /// <summary>The marker while it is fresh: <c>{at, pid, reason, retry_at}</c>, else null.</summary>
    internal JsonObject? ReadSpawnFailed()
    {
        try
        {
            var v = JsonText.Parse(deps.Files.ReadBytes(_paths.SpawnFailed)).AsObject();
            if (v is null || v.Str("at") is not { } at || !Timestamps.TryParseMs(at, out var atMs))
            {
                return null;
            }

            var now = (double)deps.Clock.UtcNow.ToUnixTimeMilliseconds();
            if (now - atMs >= SpawnBackoffMs)
            {
                return null;
            }

            var retry = DateTimeOffset.FromUnixTimeMilliseconds((long)(atMs + SpawnBackoffMs));
            return Jx.Obj(
                ("at", at),
                ("pid", v.TryGetValue("pid", out var pid) && pid.Kind != JsonKind.Null ? pid : JsonValue.Null),
                ("reason", v.Str("reason") ?? "unknown"),
                ("retry_at", Timestamps.ToIsoString(retry)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonParseException)
        {
            return null;
        }
    }

    private void MarkSpawnFailed(int? pid, string reason)
    {
        try
        {
            deps.Writer.Mkdir(_paths.Run);
            var marker = Jx.Obj(
                ("at", Timestamps.ToIsoString(deps.Clock.UtcNow)),
                ("pid", pid is null ? JsonValue.Null : pid.Value),
                ("reason", reason));
            var bytes = JsonText.ToUtf8(marker, 0).Concat("\n"u8.ToArray()).ToArray();
            deps.Writer.WriteAtomic(_paths.SpawnFailed, $"{_paths.SpawnFailed}.{Environment.ProcessId}.tmp", bytes);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // a marker that cannot be written only means the next client tries again
        }
    }

    internal void ClearSpawnFailed()
    {
        try
        {
            deps.Writer.Unlink(_paths.SpawnFailed);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // no marker
        }
    }

    // ---------------------------------------------------------------- one request

    /// <summary>One request over the socket. Throws on a transport failure or a timeout.</summary>
    internal async Task<HttpJsonReply> HttpJsonAsync(
        string socket, string method, string pathAndQuery, JsonObject? body, int timeoutMs, CancellationToken ct)
    {
        var payload = body is null ? ReadOnlyMemory<byte>.Empty : JsonText.ToUtf8(JsonValue.FromObject(body), 0);
        var reply = await deps.Transport
            .SendAsync(socket, method, pathAndQuery, payload, TimeSpan.FromMilliseconds(timeoutMs), ct)
            .ConfigureAwait(false);
        _ = JsonText.TryParse(reply.Body.Span, out var parsed);
        return new HttpJsonReply(reply.Status, parsed);
    }

    /// <summary>
    /// The daemon's pid as <c>ahd.json</c> names it while that pid lives; else, for a daemon still migrating (no <c>ahd.json</c> yet, or a
    /// crashed one's left behind), as <c>ahd.pid</c> names it; else whatever <c>ahd.json</c> names, dead or not.
    /// </summary>
    internal long? DaemonPidOnFile() => DaemonPidOnFile(ReadInfo());

    private long? DaemonPidOnFile(JsonObject? raw) =>
        raw?.Int("pid") is { } named && deps.Inspector.PidAlive((int)named) ? named : PidFileDaemon() ?? raw?.Int("pid");

    /// <summary>The pid <c>ahd.pid</c> names while that pid still has the start time recorded there; null otherwise.</summary>
    private long? PidFileDaemon()
    {
        try
        {
            var doc = JsonText.Parse(deps.Files.ReadBytes(_paths.PidFile));
            return doc.Member("pid") is { IsNumber: true } pid && doc.Member("start") is { IsText: true } start && deps.StartTime is { } startOf
                && ProcessStart.Compare(startOf((int)pid.AsNumber()), static () => null, start.AsString()) == StartMatch.Same
                ? (long)pid.AsNumber()
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonParseException)
        {
            return null;
        }
    }

    internal JsonObject? ReadInfo()
    {
        try
        {
            return JsonText.Parse(deps.Files.ReadBytes(_paths.Info)).AsObject();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonParseException)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- discovery

    /// <summary>
    /// The running daemon's info and health; draining when its pid answers 503 <c>draining</c>; null when down (no file,
    /// refused, timeout, bad JSON, pid mismatch). Never starts anything.
    /// </summary>
    internal Task<Discovered?> DiscoverCoreAsync(int timeoutMs, CancellationToken ct = default) =>
        DiscoverCoreAsync(timeoutMs, DiscoverTimeoutMs, ct);

    /// <summary>As above, with the per-request timeout capped at <paramref name="capMs"/> instead of the discovery default.</summary>
    internal async Task<Discovered?> DiscoverCoreAsync(int timeoutMs, int capMs, CancellationToken ct)
    {
        if (DaemonOff)
        {
            return null;
        }

        var info = ReadInfo();
        if (info is null || info.Str("socket") is not { } socket || info.Int("pid") is not { } pidValue)
        {
            return null;
        }

        var pid = (int)pidValue;
        if (deps.OneStrike && _silentPid == pid)
        {
            return null;
        }

        try
        {
            var res = await HttpJsonAsync(socket, "GET", "/v1/health", null, Math.Max(1, Math.Min(timeoutMs, capMs)), ct).ConfigureAwait(false);
            if (res.Status == 503 && Jx.ErrorCode(res.Body) == "draining")
            {
                return new Discovered(true, pid, null, null);
            }

            if (res.Status != 200 || res.Body.AsObject() is not { } health || health.Int("pid") != pidValue)
            {
                return null;
            }

            // A healthy daemon makes a spawn-failed marker stale: a slow start that a hook's budget gave up on, say.
            ClearSpawnFailed();
            return new Discovered(false, pid, info, health);
        }
        catch (Exception e)
        {
            // the caller's own deadline ending the wait is a pid that did not answer in time too
            _silentPid = pid;
            if (e is OperationCanceledException && ct.IsCancellationRequested)
            {
                throw;
            }

            return null;
        }
    }

    /// <summary>The daemon pid whose discovery last failed, for <see cref="DaemonClientDependencies.OneStrike"/>.</summary>
    private int? _silentPid;

    public async Task<DaemonInfo?> DiscoverAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var found = await DiscoverCoreAsync((int)Math.Max(1, timeout.TotalMilliseconds), cancellationToken).ConfigureAwait(false);
        if (found is null || found.Draining || found.Info is null)
        {
            return null;
        }

        return InfoOf(found.Info);
    }

    private static DaemonInfo InfoOf(JsonObject info) => new(
        (int)(info.Int("v") ?? 1),
        (int)(info.Int("pid") ?? 0),
        info.Str("version") ?? string.Empty,
        (int)(info.Int("protocol") ?? 1),
        info.Str("socket") ?? string.Empty,
        info.Str("root") ?? string.Empty,
        info.Str("started_at") ?? string.Empty);

    // ---------------------------------------------------------------- stop, drain

    /// <summary>Whether <paramref name="pid"/> exited within <paramref name="ms"/>. A cancelled wait returns false (still running) and does not throw.</summary>
    internal async Task<bool> WaitForPidExitAsync(int pid, int ms, CancellationToken ct)
    {
        var end = Now() + ms;
        while (Now() < end)
        {
            if (!deps.Inspector.PidAlive(pid))
            {
                return true;
            }

            try
            {
                await Task.Delay((int)Math.Max(1, Math.Min(50, end - Now())), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return false;
            }
        }

        return !deps.Inspector.PidAlive(pid);
    }

    /// <summary>Sends the drain request. The daemon's reply, or null when it gave none (gone, unreachable, timed out).</summary>
    internal async Task<HttpJsonReply?> PostDrainAsync(string socket, string reason, int timeoutMs, CancellationToken ct = default)
    {
        try
        {
            return await HttpJsonAsync(socket, "POST", "/v1/drain", Jx.Obj(("reason", reason)), timeoutMs, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>What the daemon said when it answered a drain with anything but 200; null when it accepted, is already draining, or gave no answer.</summary>
    internal static string? DrainRefusal(HttpJsonReply? reply)
    {
        if (reply is null || reply.Status == 200)
        {
            return null;
        }

        var error = reply.Body.AsObject()?.Map("error");
        return error?.Str("code") == "draining"
            ? null
            : error?.Str("message") ?? $"HTTP {reply.Status.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
    }

    /// <summary><see cref="DrainRefusal"/> when the daemon decided no (a 4xx). A 5xx is the daemon failing, which counts as no answer.</summary>
    internal static string? ActualRefusal(HttpJsonReply? reply) =>
        reply is { Status: >= 400 and < 500 } ? DrainRefusal(reply) : null;

    public bool DaemonPresent()
    {
        try
        {
            if (ReadInfo()?.Int("pid") is { } pid && deps.Inspector.PidAlive((int)pid))
            {
                return true;
            }

            return deps.Files.Exists(_paths.Sock);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public async Task DrainAsync(DaemonInfo daemon, string reason, CancellationToken cancellationToken = default)
    {
        var reply = await PostDrainAsync(daemon.Socket, reason, timeoutMs: 2000, cancellationToken).ConfigureAwait(false);
        if (ActualRefusal(reply) is null)
        {
            _ = await WaitForPidExitAsync(daemon.Pid, DrainWaitMs, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Whether <paramref name="pid"/> is this state home's daemon: its command line ends with <c>--ah-home &lt;home&gt;</c> and names a daemon entry point.</summary>
    internal bool IsOurDaemon(int pid) => OwnershipOf(pid) == true;

    /// <summary>
    /// True when <paramref name="pid"/> is this state home's daemon, false when it is dead or its command line is someone else's,
    /// and null when it lives but <c>ps</c> could not say (it failed, timed out or printed nothing, as it does on a loaded machine).
    /// </summary>
    internal bool? OwnershipOf(int pid)
    {
        if (!deps.Inspector.PidAlive(pid))
        {
            return false;
        }

        var ps = deps.Processes.Run(new ProcessRequest(["ps", "-p", pid.ToString(System.Globalization.CultureInfo.InvariantCulture), "-o", "command="]));
        // The tag is the last argument, so matching the command's tail keeps a state home with spaces in it exact.
        var cmd = ps.ExitCode == 0 ? Utf8Text.Decode(ps.Stdout.Span).Trim() : string.Empty;
        if (cmd.Length == 0)
        {
            return deps.Inspector.PidAlive(pid) ? null : false;
        }

        return cmd.Contains(" serve ", StringComparison.Ordinal)
            && cmd.EndsWith($" --ah-home {deps.AhEnvironment.AhHome}", StringComparison.Ordinal);
    }

    /// <summary>
    /// Appends one line to the daemon log saying who did what and why. A line that cannot be written is dropped: the
    /// log never blocks a start or a stop.
    /// </summary>
    private static readonly Lazy<string> CallerName = new(static () =>
    {
        using var self = Process.GetCurrentProcess();
        return self.ProcessName;
    });

    private void LogAction(string action, string detail)
    {
        try
        {
            var root = deps.SpawnPluginRoot?.Invoke() is { Length: > 0 } spawnRoot ? spawnRoot : deps.AhEnvironment.Plugin?.Root ?? "-";
            var name = CallerName.Value;
            deps.Writer.Mkdir(Path.GetDirectoryName(_paths.Log)!);
            var line = $"{Timestamps.ToIsoString(deps.Clock.UtcNow)} ahd-client {action} {detail} caller={Environment.ProcessId}/{name} root={root}\n";
            deps.Appender.Append(_paths.Log, Utf8Text.Encode(line));
        }
        catch (Exception)
        {
            // see the summary
        }
    }

    /// <summary>A daemon that does not answer is told to stop, but only if the pid is this state home's daemon. <paramref name="reason"/> and <paramref name="probes"/> go to the log line written first.</summary>
    internal bool StopHung(JsonObject? info, string reason, string probes = "")
    {
        return info?.Int("pid") is { } pid && StopHung((int)pid, reason, probes);
    }

    private bool StopHung(int pid, string reason, string probes = "")
    {
        if (!IsOurDaemon(pid))
        {
            return false;
        }

        LogAction("kill", $"target={pid} reason={reason}{(probes.Length > 0 ? $" probes={probes}" : string.Empty)}");
        var kill = deps.Processes.Run(new ProcessRequest(["kill", "-TERM", pid.ToString(System.Globalization.CultureInfo.InvariantCulture)]));
        return kill.ExitCode == 0;
    }

    /// <summary>Stops the daemon if one is running (a hung one by signal). Whether one was stopped, and what it said if it refused. Never starts anything.</summary>
    internal async Task<(bool Stopped, string? Refusal)> StopDaemonAsync(string reason = "stop", CancellationToken ct = default)
    {
        var info = await DiscoverCoreAsync(DiscoverTimeoutMs, ct).ConfigureAwait(false);
        if (info is { Draining: true })
        {
            if (await WaitForPidExitAsync(info.Pid, DrainWaitMs, ct).ConfigureAwait(false))
            {
                return (true, null);
            }
        }
        else if (info is not null)
        {
            var reply = await PostDrainAsync(info.Socket, "daemon stop", timeoutMs: 2000, ct).ConfigureAwait(false);

            // the daemon answered no: that is its decision, and a signal must not overrule it
            if (ActualRefusal(reply) is { } refusal)
            {
                return (false, refusal);
            }

            if (await WaitForPidExitAsync(info.Pid, DrainWaitMs, ct).ConfigureAwait(false))
            {
                return (true, null);
            }
        }

        // no answer, or still running after the wait. TERM starts a drain whose request never arrived; to a drain already running it adds nothing but the second wait below
        if (DaemonPidOnFile() is { } pid && StopHung((int)pid, reason))
        {
            return (await WaitForPidExitAsync((int)pid, deps.StopExitWaitMs, ct).ConfigureAwait(false), null);
        }

        return (false, null);
    }

    // ---------------------------------------------------------------- start

    private void RotateLog(string log)
    {
        var stat = deps.Files.Stat(log);
        if (stat is { Size: > LogRotateBytes })
        {
            deps.Writer.Rename(log, log + ".1");
        }
    }

    /// <summary>
    /// Starts <c>ahd heal</c> detached for a daemon that is recorded, alive and does not answer discovery, and returns the heal's pid.
    /// Null, and nothing started, when the daemon answers, when none is recorded alive, or when the heal cannot be started. The heal
    /// judges the daemon itself and is single-flight, so a caller only ever starts it and never waits for it.
    /// </summary>
    public int? LaunchHeal(string trigger)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        try
        {
            // AH_DAEMON=off asks no daemon anything, so there is no failed probe to act on: nothing is launched
            if (DaemonOff || deps.SelfExecutable is null || ReadInfo()?.Int("pid") is not { } pid || !deps.Inspector.PidAlive((int)pid)
                || DiscoverCoreAsync(DiscoverTimeoutMs, CancellationToken.None).GetAwaiter().GetResult() is not null)
            {
                return null;
            }

            var head = deps.SpawnPluginRoot?.Invoke() is { Length: > 0 } root
                ? new[] { deps.SelfExecutable, "--plugin-root", root, "heal" }
                : [deps.SelfExecutable, "heal"];
            var log = Path.Combine(_paths.Home, "logs", "heal-" + deps.Clock.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture) + ".log");
            deps.Writer.Mkdir(Path.GetDirectoryName(log)!);
            return deps.Spawner.SpawnDetached(new DetachedSpawnRequest([.. head, "--trigger", trigger], deps.AhEnvironment.AhHome, SpawnEnvironment(), log));
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The command that starts the daemon: this executable's <c>serve</c>, behind the plugin root the caller already checked when
    /// there is one, so the daemon uses that root and does not resolve its own; null when the executable's path is unknown.
    /// </summary>
    internal IReadOnlyList<string>? SpawnCommand()
    {
        if (deps.SelfExecutable is null)
        {
            return null;
        }

        // The idle option goes before --ah-home: the state home stays the last argument, which is how a daemon is recognised.
        var idle = SpawnIdleExitMs is { } ms ? new[] { "--idle-exit-ms", ms.ToString(System.Globalization.CultureInfo.InvariantCulture) } : [];
        var head = deps.SpawnPluginRoot?.Invoke() is { Length: > 0 } root
            ? new[] { deps.SelfExecutable, "--plugin-root", root, "serve" }
            : [deps.SelfExecutable, "serve"];
        return [.. head, .. idle, "--ah-home", deps.AhEnvironment.AhHome];
    }

    /// <summary>
    /// How long a daemon this client spawns may sit idle before it exits, as the <c>--idle-exit-ms</c> option of <c>serve</c>; null
    /// leaves the daemon's own default. Set by the <c>start</c> verb's option, for callers whose daemon must not outlive them.
    /// </summary>
    internal long? SpawnIdleExitMs { get; set; }

    /// <summary>The test-only variables a spawned daemon receives, and only when the spawner is itself in test mode.</summary>
    private static readonly string[] TestKnobs = ["AH_TEST", "AHD_VERSION_OVERRIDE", "AHD_JOIN_MS", "AHD_IDLE_EXIT_MS", "AHD_WATCH_POLL_MS"];

    private Dictionary<string, string?> SpawnEnvironment()
    {
        var env = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["PATH"] = Var("PATH"),
            ["HOME"] = deps.AhEnvironment.HomeDirectory,
            ["AH_HOME"] = deps.AhEnvironment.AhHome,
        };
        if (deps.AhEnvironment.Plugin?.Root is { } pluginRoot)
        {
            env["AH_PLUGIN_ROOT"] = pluginRoot;
        }

        // The browser UI's kill switch, read by the daemon: without this, AH_UI=off never reaches a daemon this client starts.
        if (Var("AH_UI") is { } ui)
        {
            env["AH_UI"] = ui;
        }

        foreach (var name in deps.AhEnvironment.TestMode ? TestKnobs : [])
        {
            if (Var(name) is { } value)
            {
                env[name] = value;
            }
        }

        return env;
    }

    /// <summary>Whether a live daemon holds <see cref="RunPaths.Life"/>; a lock that cannot be tried proves nothing and counts as free.</summary>
    private bool LifeHeld()
    {
        try
        {
            using var probe = deps.Locks.TryHold(_paths.Life);
            return probe is null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task<Discovered?> StartLockedAsync(long deadline, bool hook, string? drainReason, Action<string> busy, CancellationToken ct)
    {
        long Remaining() => Math.Max(0, deadline - Now());
        if (Remaining() <= 0)
        {
            return null;
        }

        var command = SpawnCommand();
        if (command is null)
        {
            return null;
        }

        var again = await DiscoverCoreAsync((int)Remaining(), ct).ConfigureAwait(false);
        if (again is { Draining: false })
        {
            return again;
        }

        // A daemon that began draining since the caller looked is exiting, not down: no replacement while its pid lives.
        if (again is not null && !await WaitForPidExitAsync(again.Pid, (int)Remaining(), ct).ConfigureAwait(false))
        {
            return null;
        }

        if (Remaining() <= 0)
        {
            return null;
        }

        var raw = ReadInfo();
        var spawnReason = drainReason ?? "no-daemon";

        // With no ahd.json yet, or a dead daemon's left behind, a daemon still migrating is named only by ahd.pid: slow, not down.
        if (DaemonPidOnFile(raw) is { } rawPid)
        {
            if (!deps.Inspector.PidAlive((int)rawPid))
            {
                spawnReason = drainReason ?? "dead-pid";
            }
            else if (OwnershipOf((int)rawPid) is not false)
            {
                // Alive and ours (or not provably anyone else's: a ps that cannot answer under load proves nothing), but it did not
                // answer: slow, not down. A daemon whose pid lives is never replaced because it was slow; `ahd restart` is the way.
                if (hook)
                {
                    return null;
                }

                busy($"ahd is busy (pid {rawPid} alive); waiting up to {HungProbeMs / 1000} s for it");
                var late = await DiscoverCoreAsync(HungProbeMs, HungProbeMs, ct).ConfigureAwait(false);
                if (late is { Draining: false })
                {
                    busy("answered; not replaced");
                    return late;
                }

                if (late is not null && !await WaitForPidExitAsync(late.Pid, DrainWaitMs, ct).ConfigureAwait(false))
                {
                    busy("answered; not replaced");
                    return null;
                }

                if (late is null)
                {
                    busy($"ahd is slow (pid {rawPid} alive, no answer in {HungProbeMs / 1000} s); not starting another; `ahd restart` replaces it");
                    return null;
                }

                deadline = Math.Max(deadline, Now() + StartWaitMs);
            }
        }

        // A daemon that removed its record while shutting down, or is stuck past it, still holds its life lock and may still hold
        // the database: no second daemon over it, whatever the files say.
        // A daemon shutting down cleanly removes its record before it exits, so it is waited for like a draining one.
        var lifeEnd = Now() + Math.Min(Remaining(), DrainWaitMs);
        while (LifeHeld())
        {
            if (Now() >= lifeEnd)
            {
                busy($"a previous ahd for {_paths.Home} is still alive (it holds run/ahd.life.lock) but does not answer; not starting another until it exits; `ahd heal` replaces it only while it is recorded, otherwise end it (pgrep -fl 'ahd.* serve')");
                return null;
            }

            await Task.Delay(100, ct).ConfigureAwait(false);
        }

        _ = deps.Writer.Unlink(_paths.Sock);
        deps.Writer.Mkdir(Path.GetDirectoryName(_paths.Log)!);
        RotateLog(_paths.Log);
        LogAction("spawn", $"reason={spawnReason} command={string.Join(' ', command)}");

        int childPid;
        string? exited = null;
        try
        {
            childPid = deps.Spawner.SpawnDetached(new DetachedSpawnRequest(command, deps.AhEnvironment.AhHome, SpawnEnvironment(), _paths.Log));
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            MarkSpawnFailed(null, $"could not start ({e.GetType().Name})");
            return null;
        }

        var started = Now();
        var end = Math.Min(deadline, started + StartWaitMs);
        while (Now() < end)
        {
            var info = await DiscoverCoreAsync((int)(end - Now()), ct).ConfigureAwait(false);
            if (info is { Draining: false } && info.Pid == childPid)
            {
                ClearSpawnFailed();
                return info;
            }

            if (!deps.Inspector.PidAlive(childPid))
            {
                exited = "exited";
            }

            await Task.Delay((int)Math.Max(1, Math.Min(100, end - Now())), ct).ConfigureAwait(false);
        }

        // The next spawns back off when the daemon exited, or did not become healthy in its whole window. A caller whose own
        // budget ran out sooner has no verdict to record: a daemon that is merely slow is found by discovery.
        if (exited is not null || Now() - started >= StartWaitMs)
        {
            MarkSpawnFailed(childPid, exited ?? $"no healthy daemon within {StartWaitMs} ms");
        }

        return null;
    }

    /// <summary>
    /// Finds the daemon or starts one, within the budget. Null means use the files; <c>Warn(text, reason)</c> is told why.
    /// <c>Hook</c> is budget mode: every wait stops at the deadline, and an older daemon is sent a drain and not waited for.
    /// </summary>
    internal async Task<Discovered?> EnsureAsync(EnsureOptions options, CancellationToken ct = default)
    {
        string? last = null;
        var drained = false;
        var found = await EnsureCoreAsync(options with { Warn = (text, reason) => { last = text; options.Warn?.Invoke(text, reason); } }, () => drained = true, ct).ConfigureAwait(false);
        if (found is null && drained)
        {
            // ah2-100 D5: a replace that ends with no daemon says so in one line.
            LogAction("replace-failed", $"reason={last ?? "unknown"}");
        }

        return found;
    }

    private async Task<Discovered?> EnsureCoreAsync(EnsureOptions options, Action onDrained, CancellationToken ct)
    {
        void Warn(string text) => options.Warn?.Invoke(text, "down");
        if (DaemonOff)
        {
            return null;
        }

        var deadline = Now() + options.BudgetMs - (options.Hook ? 25 : 0);
        long Remaining() => Math.Max(0, deadline - Now());
        var p = _paths;
        if (RunPaths.SocketBytes(p.Sock) > RunPaths.MaxSocketPath)
        {
            Warn($"socket path is {RunPaths.SocketBytes(p.Sock)} bytes; the limit is {RunPaths.MaxSocketPath}");
            return null;
        }

        string? startWhy = null;
        int? drainedPid = null;
        for (var round = 0; round < 2; round++)
        {
            if (options.Hook && Remaining() <= 0)
            {
                Warn("out of time");
                return null;
            }

            var info = await DiscoverCoreAsync(options.Hook ? (int)Remaining() : DiscoverTimeoutMs, ct).ConfigureAwait(false);

            // ah2-136 D2: a 2.0.3 to 2.0.5 daemon answers health as live for a moment after accepting the drain; the one this call drained is draining.
            if (info is { Draining: true } || (round == 1 && info is not null && info.Pid == drainedPid))
            {
                // A request that may not start the daemon has nothing to wait for: the files serve it now.
                if (!options.Start)
                {
                    Warn("the daemon is still draining");
                    return null;
                }

                // An exiting daemon is not a down one: starting a replacement now races its shutdown.
                if (!await WaitForPidExitAsync(info.Pid, options.Hook ? (int)Remaining() : DrainWaitMs, ct).ConfigureAwait(false))
                {
                    Warn("the daemon is still draining");
                    return null;
                }

                info = null;
            }

            if (info is null && options.Start)
            {
                // Only an implicit start backs off: a user's own `ahd start` makes its attempt and rewrites the marker if it fails.
                var failed = options.Explicit ? null : ReadSpawnFailed();
                if (failed is not null)
                {
                    Warn($"ahd failed to start ({failed.Str("reason")}); not trying again before {failed.Str("retry_at")}");
                    return null;
                }

                if (deps.StartRefusal?.Invoke() is { } refusal)
                {
                    MarkSpawnFailed(null, refusal);
                    Warn(refusal);
                    return null;
                }

                deps.Writer.Mkdir(p.Run, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                TryChmod(p.Run);
                var held = deps.Locks.TryAcquire(p.Lock);
                if (held is not null)
                {
                    try
                    {
                        info = await StartLockedAsync(options.Hook ? deadline : Now() + (StartWaitMs * 2L), options.Hook, round == 1 ? "version-drain" : null, text => { startWhy = text; if (!options.Hook) { Warn(text); } }, ct).ConfigureAwait(false);
                    }
                    finally
                    {
                        held.Dispose();
                    }
                }
                else
                {
                    var end = Math.Min(options.Hook ? deadline : long.MaxValue, Now() + StartWaitMs);
                    while ((info is null || info.Draining) && Now() < end)
                    {
                        await Task.Delay((int)Math.Max(1, Math.Min(100, end - Now())), ct).ConfigureAwait(false);
                        info = await DiscoverCoreAsync((int)(end - Now()), ct).ConfigureAwait(false);
                    }

                    if (info is { Draining: true })
                    {
                        info = null;
                    }
                }
            }

            if (info is null)
            {
                // A caller that keeps only the last warning still sees why the start did not go ahead.
                Warn(startWhy is null ? "could not start or reach the daemon" : $"could not start or reach the daemon: {startWhy}");
                return null;
            }

            var health = info.Health!;
            var protocols = health.TryGetValue("protocols", out var ps) && ps.Kind == JsonKind.Array
                ? ps.AsArray().Where(static x => x.Kind == JsonKind.Number).Select(static x => x.AsNumber()).ToArray()
                : health.Int("protocol") is { } single ? [(double)single] : [];
            if (!protocols.Contains(1))
            {
                Warn("the daemon does not speak protocol 1");
                return null;
            }

            // A daemon is drained when it is older than this plugin.
            var version = health.Str("version") ?? "?";
            var cmp = Versions.Compare(EffectiveVersion, version);
            if (cmp <= 0 || options.AnyVersion)
            {
                return info;
            }

            var why = $"the daemon is older ({version}) than this plugin ({EffectiveVersion})";
            if (!options.Start)
            {
                Warn(why);
                return null;
            }

            if (round == 1)
            {
                Warn("the daemon is still older after a restart");
                return null;
            }

            if (options.Hook)
            {
                // ah2-100: whoever drains to replace starts the replacement; the next round waits for the exit and starts it within the budget.
                var hookReply = await PostDrainAsync(info.Socket, "client upgrade", (int)Math.Max(50, Math.Min(Remaining(), HookDrainTimeoutMs)), ct).ConfigureAwait(false);
                if (ActualRefusal(hookReply) is { } hookRefusal)
                {
                    Warn($"the daemon is older ({version}) and refused to drain: {hookRefusal}; using files for now");
                    return null;
                }

                onDrained();
                drainedPid = info.Pid;
                continue;
            }

            var drainReply = await PostDrainAsync(info.Socket, "client upgrade", 2000, ct).ConfigureAwait(false);
            if (ActualRefusal(drainReply) is { } drainRefusal)
            {
                Warn($"the old daemon refused to drain: {drainRefusal}");
                return null;
            }

            onDrained();
            if (!await WaitForPidExitAsync(info.Pid, DrainWaitMs, ct).ConfigureAwait(false))
            {
                Warn("the old daemon did not exit after a drain");
                return null;
            }
        }

        return null;
    }

    private static void TryChmod(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // the mode is a hardening, not a precondition
        }
    }

    public async Task<DaemonInfo?> EnsureDaemonAsync(TimeSpan budget, CancellationToken cancellationToken = default)
    {
        var found = await EnsureAsync(new EnsureOptions((int)budget.TotalMilliseconds), cancellationToken).ConfigureAwait(false);
        return found?.Info is null ? null : InfoOf(found.Info);
    }

    public async Task<DaemonInfo?> EnsureDaemonForHookAsync(TimeSpan budget, CancellationToken cancellationToken = default)
    {
        var found = await EnsureAsync(new EnsureOptions((int)budget.TotalMilliseconds, Hook: true), cancellationToken).ConfigureAwait(false);
        return found?.Info is null ? null : InfoOf(found.Info);
    }

    /// <summary>
    /// One request to the daemon, started on demand. A <see cref="DaemonReply"/> with status 0 means the files should
    /// be used instead: the daemon is unavailable, the transport failed, the answer was a 5xx, or a 404 that means the
    /// protocol is not served.
    /// </summary>
    public async Task<DaemonReply> RequestAsync(DaemonRequest request, CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(RequestDeadlineMs);
        var ct = deadline.Token;
        DateTimeOffset? sentAt = null;
        try
        {
            var found = await EnsureAsync(new EnsureOptions(Start: request.StartIfDown, AnyVersion: request.AnyVersion), ct).ConfigureAwait(false);
            if (found is null)
            {
                return new DaemonReply(0, ReadOnlyMemory<byte>.Empty, "down");
            }

            var timeout = (int)(request.Timeout?.TotalMilliseconds ?? 30_000);
            sentAt = deps.Clock.UtcNow;
            var reply = await deps.Transport
                .SendAsync(found.Socket, request.Method, request.PathAndQuery, request.Body, TimeSpan.FromMilliseconds(timeout), ct)
                .ConfigureAwait(false);
            _ = JsonText.TryParse(reply.Body.Span, out var body);
            var code = Jx.ErrorCode(body);
            if ((reply.Status >= 500 && code != "mirror_failed") || (reply.Status == 404 && (code is null or "not_found")))
            {
                return new DaemonReply(
                    0,
                    ReadOnlyMemory<byte>.Empty,
                    $"http {reply.Status.ToString(System.Globalization.CultureInfo.InvariantCulture)} {code ?? "-"}",
                    SentAt: sentAt);
            }

            return new DaemonReply(reply.Status, reply.Body, SentAt: sentAt);
        }
        catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // an HttpClient timeout surfaces as an OperationCanceledException that the caller's own token did not raise
            var timedOut = e is OperationCanceledException;
            var reason = !timedOut ? "down" : deadline.IsCancellationRequested ? "daemon busy" : "timeout";
            return new DaemonReply(0, ReadOnlyMemory<byte>.Empty, reason, timedOut, sentAt);
        }
    }

    // ---------------------------------------------------------------- the fallback log

    /// <summary>Notes that <paramref name="verb"/> used the files instead of the daemon: one <c>{at, verb, reason}</c> row, rotated to <c>.1</c> past 1 MB. Best effort.</summary>
    public void RecordFallback(string verb, string reason)
    {
        try
        {
            var f = _paths.Fallback;
            deps.Writer.Mkdir(Path.GetDirectoryName(f)!);
            if (deps.Files.Stat(f) is { Size: > FallbackRotateBytes })
            {
                deps.Writer.Rename(f, f + ".1");
            }

            var row = Jx.Obj(("at", Timestamps.ToIsoString(deps.Clock.UtcNow)), ("verb", verb), ("reason", reason));
            deps.Appender.Append(f, JsonText.ToUtf8(row, 0).Concat("\n"u8.ToArray()).ToArray());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // a fallback record must never fail the verb
        }
    }

    /// <summary>The recorded fallbacks no older than <paramref name="since"/> (an ISO time, or null for all): their count and the newest <paramref name="last"/>.</summary>
    internal JsonObject ReadFallbacks(string? since, int last = 10)
    {
        var rows = new List<JsonObject>();
        foreach (var f in new[] { _paths.Fallback + ".1", _paths.Fallback })
        {
            string text;
            try
            {
                text = deps.Files.ReadText(f);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var line in text.Split('\n'))
            {
                if (!JsonText.TryParse(Utf8Text.Encode(line), out var v) || v.AsObject() is not { } o || o.Str("at") is not { } at)
                {
                    continue;
                }

                if (since is not null && string.CompareOrdinal(at, since) < 0)
                {
                    continue;
                }

                rows.Add(Jx.Obj(
                    ("at", at),
                    ("verb", o.TryGetValue("verb", out var verb) ? verb : JsonValue.Null),
                    ("reason", o.TryGetValue("reason", out var reason) ? reason : JsonValue.Null)));
            }
        }

        return Jx.Obj(
            ("count", rows.Count),
            ("last", Jx.Arr(rows.Skip(Math.Max(0, rows.Count - last)).Select(static r => (JsonValue)r))));
    }

    internal static long Elapsed(long since) => Stopwatch.GetElapsedTime(since).Ticks;
}
