using Ah.Hierarchy;
using System.Globalization;

namespace Ah.Watch;

/// <summary>Everything the diagnosis reads, already gathered from files.</summary>
/// <param name="DaemonUp">Whether the run file names a running daemon.</param>
/// <param name="DaemonPid">That daemon's pid, or null.</param>
/// <param name="WatchTickAgeMs">Age of the watch-tick stamp, or null when none was ever written.</param>
/// <param name="StallWatchAgeMs">Age of the stall-watch stamp, or null when none was ever written.</param>
/// <param name="TickPeriodMs">The watch-tick period.</param>
/// <param name="Eta">The dispatch's ETA state.</param>
/// <param name="Events">The ahd events in the Orchestrator's box that name the dispatch.</param>
/// <param name="WaiterLive">Whether the ahd waiter is running.</param>
/// <param name="NowMs">The current instant.</param>
public sealed record DiagnoseFacts(
    bool DaemonUp, int? DaemonPid, double? WatchTickAgeMs, double? StallWatchAgeMs, double TickPeriodMs, EtaStateResult Eta, IReadOnlyList<AhdEventRow> Events, bool WaiterLive, double NowMs);

/// <summary>The cause the first matching rule names, and the evidence for it.</summary>
public sealed record DiagnoseVerdict(string Cause, string Evidence);

/// <summary>The rules of <c>roster stall-diagnose</c>, in order; the first that matches wins.</summary>
internal static class StallDiagnosis
{
    public static DiagnoseVerdict Decide(DiagnoseFacts f)
    {
        ArgumentNullException.ThrowIfNull(f);
        var stale = 3 * f.TickPeriodMs;
        if (!f.DaemonUp)
        {
            return new DiagnoseVerdict("daemon-down", "no identity-live ahd pid in the run file");
        }

        if (f.WatchTickAgeMs is null || f.WatchTickAgeMs > stale)
        {
            return new DiagnoseVerdict("job-not-running", $"watch-tick last ran {Age(f.WatchTickAgeMs)} ago; the daemon is up");
        }

        if (f.StallWatchAgeMs is null || f.StallWatchAgeMs > stale)
        {
            return new DiagnoseVerdict("job-not-running", $"stall-watch last ran {Age(f.StallWatchAgeMs)} ago; the daemon is up");
        }

        var step = f.Eta.Next?.Step ?? (f.Events.Any(static e => e.Step == "check-in") ? "silent" : "check-in");
        var forStep = f.Events.Where(e => e.Step == step).ToList();
        var atMs = f.Eta.Next?.AtMs ?? f.Eta.DueMs;
        if (forStep.Count == 0)
        {
            return f.NowMs > atMs + f.TickPeriodMs
                ? new DiagnoseVerdict("event-not-raised", $"jobs are fresh, the {step} step was due {Age(f.NowMs - atMs)} ago and no event names it")
                : new DiagnoseVerdict("unknown", "the next ahd step is not due yet");
        }

        var undelivered = forStep.Where(static e => e.AckedAtMs is null).ToList();
        if (undelivered.Count == 0)
        {
            return new DiagnoseVerdict("unknown", $"the {step} event was delivered; it may have arrived just after the deadline");
        }

        return f.WaiterLive
            ? new DiagnoseVerdict("event-not-delivered", $"event {undelivered[0].Id} for {step} is unacknowledged and the waiter is running")
            : new DiagnoseVerdict("waiter-dead", $"event {undelivered[0].Id} for {step} is unacknowledged and no waiter is registered");
    }

    private static string Age(double? ms) => ms is null ? "never" : $"{(ms.Value / 1000).ToString("0", CultureInfo.InvariantCulture)}s";
}
