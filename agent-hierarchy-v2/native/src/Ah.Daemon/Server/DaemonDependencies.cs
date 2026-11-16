using Ah.Abstractions;
using Ah.State;

namespace Ah.Daemon;

/// <summary>What the daemon host runs on. The composition root builds it from the platform services.</summary>
/// <param name="Files">Reads <c>ahd.json</c>, the writer stamp and the socket's inode.</param>
/// <param name="Writer">Writes <c>ahd.json</c>; removes it and the socket when the daemon still owns them at shutdown.</param>
/// <param name="Clock">The wall clock.</param>
/// <param name="AhEnvironment">The state home, <c>AHD_IDLE_EXIT_MS</c> and the plugin's identity.</param>
/// <param name="ErrorLog">The hook-error log a failed request is reported to; may be null.</param>
/// <param name="ProcessId">This process's pid, as <c>ahd.json</c> and the health body carry it.</param>
/// <param name="State">The state request handler behind <c>/v1/state/{domain}/{op}</c>; null leaves the route unregistered.</param>
/// <param name="Peers">Reads the connector's pid from each accepted socket for <c>RouteRequest.PeerPid</c>; null leaves it null.</param>
/// <param name="Rejections">Records each non-health reply outside 2xx; null records nothing.</param>
/// <param name="Principal">Who a peer pid is (null when it could not be read), for the routes served before the router: drain and the UI lease; null makes every caller Anonymous.</param>
/// <param name="ParentOf">The parent pid of a process (null when it cannot be read); the UI service lease uses it to tell the background holder `ahd ui start` launched from any other process. Null refuses that lease to every caller that is not itself the user.</param>
/// <param name="UiStart">What `POST /v1/ui/start` needs to start the page itself; null leaves that route answering 503, and makes no process the holder's parent.</param>
/// <param name="MonotonicMs">Milliseconds on a clock that only moves forward, which the idle exit and the drain's in-flight wait (<c>DrainHard</c>) measure by, not the drain deadline; null reads <c>Environment.TickCount64</c>.</param>
public sealed record DaemonDependencies(
    IFileReader Files,
    IRawFileWriter Writer,
    IClock Clock,
    IAhEnvironment AhEnvironment,
    IHookErrorLog? ErrorLog,
    int ProcessId,
    IStateService? State = null,
    IPeerIdentity? Peers = null,
    IRejectionLog? Rejections = null,
    Func<int?, PrincipalKind>? Principal = null,
    Func<long>? MonotonicMs = null,
    Func<int, int?>? ParentOf = null,
    UiStartSupport? UiStart = null)
{
    /// <summary>Ends this process at once, whatever its threads are doing, for a drain past its deadline; null sets no deadline.</summary>
    public Action? HardExit { get; init; }

    /// <summary>The calling thread's CPU nanoseconds, or null when it cannot be read; the jobs line's per-job <c>cpu=</c> uses it. Null reports every job's cpu as unknown.</summary>
    public Func<long?>? ThreadCpuNs { get; init; }

    /// <summary>Takes <c>run/ahd.life.lock</c>, held for the daemon's whole life so no client starts a second daemon over it; null takes none.</summary>
    public ILockProvider? Locks { get; init; }

    /// <summary>The one line saying why this daemon must not start, or null when it may; null asks nothing.</summary>
    public Func<string?>? StartRefusal { get; init; }

    /// <summary>The bridge's answer route; null leaves <c>POST /ui/v1/answer</c> answering 503 unavailable.</summary>
    public UiAnswerSupport? UiAnswer { get; init; }

    /// <summary>The close core behind <c>POST /v1/team/close</c> and <c>POST /ui/v1/team-close</c>; null leaves both answering 503.</summary>
    public UiTeamCloseSupport? UiTeamClose { get; init; }

    /// <summary>Whether a peer pid was started by the Herdr desktop app with no harness on its line (<c>MeshModule.IsHerdrAppChild</c>), the second user proof <c>POST /v1/team/close</c> accepts; null accepts none.</summary>
    public Func<int, bool>? HerdrAppChild { get; init; }

    /// <summary>The core behind <c>GET</c> and <c>POST /ui/v1/rules</c>; null leaves both answering 503.</summary>
    public UiRulesSupport? UiRules { get; init; }
}
