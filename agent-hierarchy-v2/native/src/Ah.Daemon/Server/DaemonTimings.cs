namespace Ah.Daemon;

/// <summary>The daemon's time constants. Production values are <see cref="Default"/>; a test may shorten them.</summary>
/// <param name="RequestHeaders">Time allowed to receive a request's headers. Node's observed effective bound.</param>
/// <param name="RequestRead">Time allowed from accepting a request to having read its body, excluding any long-poll wait.</param>
/// <param name="KeepAlive">How long an idle keep-alive connection is kept.</param>
/// <param name="DrainHard">The longest a drain waits for in-flight requests and running ops.</param>
/// <param name="FenceInterval">The period of the fence and idle job.</param>
/// <param name="DiscardBytes">How much of a refused body is read and thrown away.</param>
/// <param name="DiscardTime">How long a refused body is read and thrown away.</param>
internal sealed record DaemonTimings(
    TimeSpan RequestHeaders,
    TimeSpan RequestRead,
    TimeSpan KeepAlive,
    TimeSpan DrainHard,
    TimeSpan FenceInterval,
    int DiscardBytes,
    TimeSpan DiscardTime)
{
    /// <summary>The longest the whole shutdown takes before the process exits regardless. Below the client's wait (<c>ahd stop</c> gives a signalled daemon 60 s), so a stop reports success.</summary>
    internal TimeSpan DrainDeadline { get; init; } = TimeSpan.FromSeconds(45);

    /// <summary>Node's observed bounds (59.6 s, 89.6 s, 6.0 s), the 5 s drain bound and the 1 MiB / 2 s discard.</summary>
    internal static DaemonTimings Default { get; } = new(
        RequestHeaders: TimeSpan.FromSeconds(60),
        RequestRead: TimeSpan.FromSeconds(90),
        KeepAlive: TimeSpan.FromSeconds(6),
        DrainHard: TimeSpan.FromMilliseconds(5000),
        FenceInterval: TimeSpan.FromSeconds(1),
        DiscardBytes: 1024 * 1024,
        DiscardTime: TimeSpan.FromSeconds(2));
}
