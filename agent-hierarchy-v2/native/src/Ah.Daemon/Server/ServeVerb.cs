using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Core.Time;

namespace Ah.Daemon;

/// <summary>The <c>serve</c> verb: the daemon process. Started by a client, never by hand.</summary>
internal static class ServeVerb
{
    /// <summary>The exit code of a daemon that could not start.</summary>
    internal const int StartFailed = 1;

    [Verb("serve")]
    internal static int Run(string[] rest, TextWriter stdout, TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(rest);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        if (DaemonModule.Dependencies is not { } deps)
        {
            stderr.WriteLine("ahd serve: the daemon has not been composed");
            return StartFailed;
        }

        if (deps.StartRefusal?.Invoke() is { } refusal)
        {
            stdout.Write($"{Timestamps.ToIsoString(deps.Clock.UtcNow)} {refusal}\n");
            stdout.Flush();
            return StartFailed;
        }

        var tag = Array.IndexOf(rest, "--ah-home");
        var home = deps.AhEnvironment.AhHome;
        if (tag >= 0 && (tag + 1 >= rest.Length || rest[tag + 1] != home))
        {
            var named = tag + 1 < rest.Length ? rest[tag + 1] : "undefined";
            stdout.Write($"{Timestamps.ToIsoString(deps.Clock.UtcNow)} ahd --ah-home {named} does not match AH_HOME {home}; refusing to start\n");
            stdout.Flush();
            return StartFailed;
        }

        var settings = DaemonSettings.Production;
        var idle = Array.IndexOf(rest, "--idle-exit-ms");
        if (idle >= 0)
        {
            var text = idle + 1 < rest.Length ? rest[idle + 1] : string.Empty;
            if (text.Length == 0 || text.Length > 9 || !text.All(static c => c is >= '0' and <= '9') || long.Parse(text, System.Globalization.CultureInfo.InvariantCulture) <= 0)
            {
                stdout.Write($"{Timestamps.ToIsoString(deps.Clock.UtcNow)} ahd --idle-exit-ms {(text.Length == 0 ? "undefined" : text)} is not a positive whole number of milliseconds; refusing to start\n");
                stdout.Flush();
                return StartFailed;
            }

            settings = settings with { IdleExitMs = long.Parse(text, System.Globalization.CultureInfo.InvariantCulture) };
        }

        return RunToExit(deps, DaemonModule.Registries, settings, stdout, DaemonModule.RegisterJobs, DaemonModule.RegisterRoutes, DaemonModule.HealthExtrasSource, DaemonModule.OnStop);
    }

    /// <summary>
    /// The process boundary: the main thread waits for the daemon to exit on a wait handle. The loop and the workers
    /// never wait on anything this way.
    /// </summary>
    internal static int RunToExit(
        DaemonDependencies deps,
        IReadOnlyList<ModuleRegistry> registries,
        DaemonSettings settings,
        TextWriter log,
        Action<IJobRegistry>? registerJobs = null,
        Action<IRouteRegistry>? registerRoutes = null,
        IHealthExtras? healthExtras = null,
        Action? onStop = null)
    {
        // Held until the process exits, when the kernel drops it: a client that finds it held starts no daemon over this one, even
        // after this one has removed its record while shutting down. A lock file that cannot be opened leaves the guards below.
        IDisposable? life = null;
        try
        {
            life = deps.Locks?.TryHold(Path.Combine(deps.AhEnvironment.AhHome, "run", "ahd.life.lock"));
            if (deps.Locks is not null && life is null)
            {
                log.Write($"{Timestamps.ToIsoString(deps.Clock.UtcNow)} ahd another daemon holds run/ahd.life.lock; exiting without migrating\n");
                log.Flush();
                return StartFailed;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.Write($"{Timestamps.ToIsoString(deps.Clock.UtcNow)} ahd run/ahd.life.lock could not be taken ({e.GetType().Name}); starting without it\n");
        }

        // The bound socket is what makes this process the one daemon, and collecting the jobs and routes opens and migrates the state
        // database: a process that loses the bind has to leave before that, or it migrates the database under the daemon that holds it.
        var sock = DaemonServer.SocketPathIn(deps.AhEnvironment.AhHome);
        var pidFile = Path.Combine(deps.AhEnvironment.AhHome, "run", "ahd.pid");
        var owned = SocketBinder.Bind(sock, out var bindError, () => LiveDaemonIn(deps, pidFile) is null);
        if (owned is null)
        {
            log.Write($"{Timestamps.ToIsoString(deps.Clock.UtcNow)} ahd {DaemonServer.BindFailure(bindError!)}\n");
            log.Flush();
            return StartFailed;
        }

        var inode = deps.Files.Stat(sock)?.Inode ?? 0;

        // A bind proves only that the socket file was gone: a live daemon's file can be removed, or a client can unlink it while that
        // daemon is still migrating, before it has written ahd.json. ahd.pid is written here, before the migration, and names a live
        // daemon only while its pid still has the start time recorded in it.
        if (LiveDaemonIn(deps, pidFile) is { } live)
        {
            log.Write($"{Timestamps.ToIsoString(deps.Clock.UtcNow)} ahd another daemon (pid {live}) is live; exiting without migrating\n");
            log.Flush();
            if (deps.Files.Stat(sock) is { } mine && mine.Inode == inode)
            {
                _ = deps.Writer.Unlink(sock);
            }

            // Not disposed: disposing removes the path by name, which may by now be a replacement's socket; the handle goes with the process.
            return StartFailed;
        }

        if (deps.UiStart?.StartTime(deps.ProcessId) is { } selfStart)
        {
            var record = new JsonObject { ["pid"] = deps.ProcessId, ["start"] = selfStart };
            deps.Writer.WriteAtomic(pidFile, $"{pidFile}.{deps.ProcessId}.tmp", Utf8Text.Encode(JsonText.Stringify(record, 0) + "\n"));
        }

        DaemonServer server;
        try
        {
            server = new DaemonServer(
                deps, settings, registries, log, JobRegistry.Collect(registries, registerJobs), RouteRegistry.Collect(registries, registerRoutes), (owned, inode));
        }
        catch
        {
            if (deps.Files.Stat(sock) is { } stat && stat.Inode == inode)
            {
                _ = deps.Writer.Unlink(sock);
                owned.Dispose();
            }

            // ponytail: a replaced socket's handle stays open until exit; disposing it removed the replacement's file in ServeSocketTests.
            throw;
        }

        server.HealthExtrasSource = healthExtras is null ? null : healthExtras.Read;
        server.OnStop = onStop;
        var code = StartFailed;
        var run = Task.Run(async () => code = await server.RunAsync().ConfigureAwait(false));
        _ = ((IAsyncResult)run).AsyncWaitHandle.WaitOne();
        GC.KeepAlive(life);
        return code;
    }

    /// <summary>The pid <paramref name="pidFile"/> names when it is another process that still has the start time recorded there; null otherwise.</summary>
    private static int? LiveDaemonIn(DaemonDependencies deps, string pidFile)
    {
        JsonValue doc;
        try
        {
            doc = JsonText.Parse(deps.Files.ReadBytes(pidFile));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonParseException)
        {
            return null;
        }

        if (doc.Member("pid") is not { IsNumber: true } number || doc.Member("start") is not { IsText: true } start || deps.UiStart is not { } support)
        {
            return null;
        }

        var pid = (int)number.AsNumber();
        return pid != deps.ProcessId && ProcessStart.Compare(support.StartTime(pid), static () => null, start.AsString()) == StartMatch.Same ? pid : null;
    }
}
