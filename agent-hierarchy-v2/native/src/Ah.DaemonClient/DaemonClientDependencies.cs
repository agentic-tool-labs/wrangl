using Ah.Abstractions;

namespace Ah.DaemonClient;

/// <summary>What the daemon client and the lifecycle verbs run on. The composition root builds it.</summary>
/// <param name="Files">Reads <c>ahd.json</c>, the writer stamp, the logs and the spawn-failed marker.</param>
/// <param name="Writer">Writes the spawn-failed marker and removes a stale socket under the start lock.</param>
/// <param name="Appender">Appends the fallback rows.</param>
/// <param name="Locks">The <c>run/ahd.lock</c> start lock.</param>
/// <param name="Transport">One request over the daemon's unix socket.</param>
/// <param name="Spawner">Starts a daemon in a new session with its output appended to the log.</param>
/// <param name="Processes">Runs <c>ps</c> and <c>kill</c> for a hung daemon.</param>
/// <param name="Inspector">Whether a pid is alive.</param>
/// <param name="Clock">The wall clock.</param>
/// <param name="AhEnvironment">The state home and the plugin's identity.</param>
/// <param name="ProcessEnvironment">A snapshot of the process environment; the spawn copies <c>PATH</c> and <c>AH_UI</c> from it and, in test mode only, <c>AH_TEST</c>, <c>AHD_VERSION_OVERRIDE</c>, <c>AHD_JOIN_MS</c>, <c>AHD_IDLE_EXIT_MS</c> and <c>AHD_WATCH_POLL_MS</c>.</param>
/// <param name="SelfExecutable">The real path of this binary, which the spawn runs as <c>serve</c>.</param>
/// <param name="SpawnPluginRoot">The plugin root, already checked to be this plugin's directory, that the spawn hands the daemon as a
/// leading <c>--plugin-root</c> option, or null: the daemon then resolves its own. Asked at spawn time, never at composition.</param>
public sealed record DaemonClientDependencies(
    IFileReader Files,
    IRawFileWriter Writer,
    IRecordAppender Appender,
    ILockProvider Locks,
    IUnixHttpTransport Transport,
    IDetachedSpawner Spawner,
    IProcessRunner Processes,
    IProcessInspector Inspector,
    IClock Clock,
    IAhEnvironment AhEnvironment,
    IReadOnlyDictionary<string, string?> ProcessEnvironment,
    string? SelfExecutable,
    Func<string?>? SpawnPluginRoot = null)
{
    /// <summary>The one line saying why a daemon must not be spawned, or null when it may; null asks nothing.</summary>
    public Func<string?>? StartRefusal { get; init; }

    /// <summary>The start time of a process, as text (null when it cannot be read), checked against the one <c>ahd.pid</c> records; null never reads <c>ahd.pid</c>.</summary>
    public Func<int, string?>? StartTime { get; init; }

    /// <summary>How long a stop waits for the daemon to exit after signalling it, in milliseconds. Logged swaps show a loaded daemon outlasting the old 5 s wait.</summary>
    public int StopExitWaitMs { get; init; } = 60_000;

    /// <summary>How long a client waits for a drained daemon to exit, in milliseconds. A drain may take DaemonTimings.DrainHard (5 s) for in-flight work plus DaemonServer.StopHookLimit (5 s); a shorter wait gives up on a daemon about to go, and an upgrading client then starts no replacement.</summary>
    public int DrainWaitMs { get; init; } = 12_000;

    /// <summary>
    /// Whether the process running the verb is the user (the admin grant counts): the only caller whose <c>ahd heal --force</c> runs.
    /// It answers null when it could not tell who is asking, and <c>--force</c> is refused then too. Unset means nobody is.
    /// </summary>
    public Func<bool?>? CallerIsUser { get; init; }

    /// <summary>Sends one signal to one pid through the platform call, for <c>ahd heal</c>; unset sends nothing.</summary>
    public Func<int, int, bool>? Signal { get; init; }

    /// <summary>Appends one row to the stall journal for <c>ahd heal</c>; null prints the row to the heal's own output instead.</summary>
    public Action<Ah.Core.Json.JsonObject>? HealJournal { get; init; }

    /// <summary>
    /// Whether a daemon pid that once failed to answer discovery is not asked again by this client. A hook sets it: its writes each
    /// asked a hung daemon afresh, and their waits added up past the hook's bound. A different pid (a daemon started since) is asked.
    /// </summary>
    public bool OneStrike { get; init; }
}
