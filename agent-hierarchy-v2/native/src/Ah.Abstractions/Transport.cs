namespace Ah.Abstractions;

public sealed record HttpReply(int Status, ReadOnlyMemory<byte> Body);

public interface IUnixHttpTransport
{
    Task<HttpReply> SendAsync(
        string socketPath,
        string method,
        string pathAndQuery,
        ReadOnlyMemory<byte> body,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

/// <summary>One line of the hook-error log: the hook, the message, and the optional event, session and first stack frames.</summary>
public sealed record HookErrorRecord(
    string Hook,
    string Message,
    string? EventName = null,
    string? SessionId = null,
    IReadOnlyList<string>? Stack = null);

/// <summary>One request the daemon refused. Never carries the request's body or arguments.</summary>
public sealed record RejectionRecord(
    string Method,
    string Path,
    int Status,
    string Code,
    string? Message,
    string? Field,
    int? PeerPid,
    string? ClientVersion,
    string? Caller);

/// <summary>The bounded record of requests the daemon answered outside 2xx. Recording never throws.</summary>
public interface IRejectionLog
{
    void Record(RejectionRecord record);
}

/// <summary>One line of the turn-end trace: what a recovery path did with a turn that ended on an API error.</summary>
/// <param name="Event"><c>StopFailure</c> (the hook), <c>detector</c> (the daemon found it in the transcript) or <c>resume</c> (a daemon resume decision).</param>
/// <param name="Session">The session the turn end belongs to.</param>
/// <param name="AgentId">The payload's agent id, or null.</param>
/// <param name="Dir">The hierarchy directory resolved, or null.</param>
/// <param name="Error">The error kind of the last assistant entry, or null.</param>
/// <param name="TurnEndKey">The session plus the error entry's uuid (its timestamp when it has none), or null.</param>
/// <param name="Action"><c>recorded</c>, <c>resumed</c>, <c>held:&lt;outage&gt;</c>, <c>skipped:&lt;reason&gt;</c> or <c>error:&lt;message&gt;</c>.</param>
public sealed record TurnEndRecord(string Event, string Session, string? AgentId, string? Dir, string? Error, string? TurnEndKey, string Action);

/// <summary>The bounded trace of turn ends that ended on an API error. Recording never throws.</summary>
public interface ITurnEndLog
{
    void Record(TurnEndRecord record);
}

public interface IHookErrorLog
{
    /// <summary>Logs a line with no event, session or stack.</summary>
    void Log(string source, string message);

    /// <summary>Logs a line carrying the event name and session id of the hook payload and the first stack frames, as Node writes them.</summary>
    void Log(HookErrorRecord record);
}

/// <summary>The contents of <c>run/ahd.json</c>.</summary>
public sealed record DaemonInfo(int V, int Pid, string Version, int Protocol, string Socket, string Root, string StartedAt);

public sealed record DaemonRequest(
    string Method,
    string PathAndQuery,
    ReadOnlyMemory<byte> Body = default,
    TimeSpan? Timeout = null,
    bool StartIfDown = true);

/// <summary>
/// The daemon's answer. <see cref="Status"/> 0 means "use the files": <see cref="FallbackReason"/> then says why (<c>down</c>,
/// <c>timeout</c>, or <c>http &lt;status&gt; &lt;error code or -&gt;</c>), <see cref="TimedOut"/> is true when the request itself timed
/// out (the daemon may still act on it), and <see cref="SentAt"/> is when the request went out (null when it never did).
/// </summary>
public sealed record DaemonReply(
    int Status,
    ReadOnlyMemory<byte> Body,
    string? FallbackReason = null,
    bool TimedOut = false,
    DateTimeOffset? SentAt = null);

public interface IDaemonClient
{
    Task<DaemonInfo?> DiscoverAsync(TimeSpan timeout, CancellationToken cancellationToken = default);

    /// <summary>Returns the running daemon, starting one within <paramref name="budget"/> when none is up.</summary>
    Task<DaemonInfo?> EnsureDaemonAsync(TimeSpan budget, CancellationToken cancellationToken = default);

    /// <summary>
    /// <see cref="EnsureDaemonAsync"/> for a hook, which must never outlive its budget: every wait stops at the deadline, and a
    /// daemon older than the plugin is told to drain and not waited for (the files serve this call; the next CLI call starts the
    /// right daemon). A client with no such mode answers as <see cref="EnsureDaemonAsync"/> does.
    /// </summary>
    Task<DaemonInfo?> EnsureDaemonForHookAsync(TimeSpan budget, CancellationToken cancellationToken = default) =>
        EnsureDaemonAsync(budget, cancellationToken);

    Task<DaemonReply> RequestAsync(DaemonRequest request, CancellationToken cancellationToken = default);

    Task DrainAsync(DaemonInfo daemon, string reason, CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether a daemon appears to exist on this machine for this state home: a recorded process that is alive, or a socket file. A
    /// request that never went out because discovery found no daemon is "down", and this tells a daemon that is not there from one that
    /// is there and did not answer discovery in time. A client that cannot tell answers false.
    /// </summary>
    bool DaemonPresent() => false;

    /// <summary>
    /// Starts <c>ahd heal</c> detached, to judge whether a daemon that does not answer is hung, and returns its pid; null when the
    /// daemon answers or is not there, when no heal can be started, or for a client that has no heal. Never waits for the heal.
    /// </summary>
    int? LaunchHeal(string trigger) => null;

    /// <summary>Notes that <paramref name="verb"/> used the files instead of the daemon: one <c>{at, verb, reason}</c> row of <c>logs/fallback.jsonl</c>. Best effort; never throws.</summary>
    void RecordFallback(string verb, string reason);
}
