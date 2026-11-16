using Ah.Abstractions;

namespace Ah.Platform;

/// <summary>The file, clock, environment and process-inspection services of the platform layer.</summary>
public sealed record PlatformIo(
    IFileReader Files,
    IRawFileWriter Writer,
    IRecordAppender Appender,
    ILockProvider Locks,
    IClock Clock,
    IHostProbe HostProbe,
    IRandom Random,
    IAhEnvironment Environment,
    IProcessInspector Processes,
    IHookErrorLog HookErrors);

public static partial class PlatformModule
{
    /// <summary>
    /// Builds the I/O services over the real operating system. <paramref name="plugin"/> is what the composition root
    /// knows about the installed plugin (null when it does not). Locks go stale after 10 seconds.
    /// </summary>
    public static PlatformIo CreateIo(PluginInfo? plugin)
    {
        if (OperatingSystem.IsMacOS())
        {
            Ah.Core.Text.PathGuard.UseNameOnDisk(Interop.Libc.NameOnDisk);
        }

        var clock = new SystemClock();
        var random = new SystemRandom();
        var environment = new SystemEnvironment(plugin);
        var writer = new RawFileWriter();
        var appender = new RecordAppender();
        var files = new FileReader();
        var hookErrors = new HookErrorLog(clock, environment, files, writer, appender);
        return new PlatformIo(
            files,
            writer,
            appender,
            new LockProvider(clock, random, ILockProvider.StaleAfter, errors: hookErrors),
            clock,
            new SystemHostProbe(),
            random,
            environment,
            new ProcessInspector(),
            hookErrors);
    }

    /// <summary>The environment over the real process: variables, the passwd home, and the one test-mode predicate.</summary>
    public static IAhEnvironment CreateEnvironment(PluginInfo? plugin) => new SystemEnvironment(plugin);

    /// <summary>
    /// The same environment over injected seams: <paramref name="getVariable"/>, a file reader for the test-mode and trust-file
    /// checks, the invoking user id, and the passwd home (null when the passwd database has none).
    /// </summary>
    public static IAhEnvironment CreateEnvironment(Func<string, string?> getVariable, PluginInfo? plugin, IFileReader files, uint uid, Func<string?> passwdHome)
    {
        ArgumentNullException.ThrowIfNull(getVariable);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(passwdHome);
        return new SystemEnvironment(getVariable, plugin, files, uid, passwdHome);
    }

    /// <summary>A rejection log over the real file system that appends to <paramref name="path"/>.</summary>
    public static IRejectionLog CreateRejectionLog(IClock clock, string path)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentException.ThrowIfNullOrEmpty(path);
        return new RejectionLog(clock, path, new RawFileWriter(), new RecordAppender());
    }

    /// <summary>A turn-end trace over the real file system that appends to <paramref name="path"/>.</summary>
    public static ITurnEndLog CreateTurnEndLog(IClock clock, string path)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentException.ThrowIfNullOrEmpty(path);
        return new TurnEndLog(clock, path, new RawFileWriter(), new RecordAppender());
    }

    /// <summary>A hook-error log over the real file system that reads time from <paramref name="clock"/> and the home directory from <paramref name="environment"/>.</summary>
    public static IHookErrorLog CreateHookErrorLog(IClock clock, IAhEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(environment);
        return new HookErrorLog(clock, environment, new FileReader(), new RawFileWriter(), new RecordAppender());
    }

    /// <summary>The member tmp operations over the real file system; aliases live under <c>&lt;ahHome&gt;/t</c>.</summary>
    public static IScratchDirectories CreateScratch(IFileReader files, string ahHome)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentException.ThrowIfNullOrEmpty(ahHome);
        return new ScratchDirectories(files, ahHome);
    }

    /// <summary>A lock provider whose locks go stale after <paramref name="staleAfter"/> (the paste and resume locks use other ages than 10 seconds).</summary>
    public static ILockProvider CreateLockProvider(IClock clock, IRandom random, TimeSpan staleAfter, IHookErrorLog? errors = null)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(random);
        return new LockProvider(clock, random, staleAfter, errors: errors);
    }
}
