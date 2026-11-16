using Ah.Abstractions;
using Ah.Core.Ui;

namespace Ah.Daemon;

/// <summary>How a start of the browser page ended.</summary>
internal enum UiStartKind
{
    Started,
    Reused,
    Busy,
    ServiceStopped,
    TimedOut,
}

/// <summary>The end of a start: what happened, whether the browser was handed the page, and a reason when the service did not run.</summary>
internal readonly record struct UiStartResult(UiStartKind Kind, bool Opened, string? Message);

/// <summary>
/// What `ahd ui start` does for the user, done in the daemon: has the background service holding the bridge lease running (the
/// holder is spawned from here, so its parent is the daemon), takes a lease of its own for the port, hands the page to the
/// system's opener and lets the lease go. The address never leaves this class. A new daemon also uses it to bring back a
/// bridge the old daemon took away, which stops at the holder: no lease of its own, no browser.
/// </summary>
internal sealed class UiStarter(UiBridge ui, UiStartSupport support, string runDir, int self)
{
    internal static readonly TimeSpan StartWait = TimeSpan.FromSeconds(10);

    /// <summary>How long a daemon start waits for a holder that still looks live to finish exiting.</summary>
    internal static readonly TimeSpan LiveWait = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan LivePoll = TimeSpan.FromMilliseconds(50);

    private const int Term = 15;

    private readonly UiServiceSeams _svc = new(
        runDir,
        self,
        () => self,
        argv => support.Spawner.SpawnDetached(new DetachedSpawnRequest(argv, "/", support.Environment(), Path.Combine(runDir, "ui-service.log"))),
        support.StartTime,
        (_, _) => false,
        support.ProcessPath,
        StartWait,
        TimeSpan.Zero);

    internal async Task<UiStartResult> RunAsync(CancellationToken cancellation)
    {
        using var gate = await UiService.WaitLockAsync(_svc, cancellation).ConfigureAwait(false);
        if (gate is null)
        {
            return new UiStartResult(UiStartKind.Busy, false, "another start is running");
        }

        var ensured = await UiService.EnsureAsync(_svc, cancellation).ConfigureAwait(false);
        if (ensured.Outcome == UiServiceOutcome.Stopped)
        {
            return new UiStartResult(UiStartKind.ServiceStopped, false, ensured.Message);
        }

        if (ensured.Outcome == UiServiceOutcome.TimedOut)
        {
            return new UiStartResult(UiStartKind.TimedOut, false, "the service did not report a lease in time");
        }

        var grant = await ui.OpenLeaseAsync(self, cancellation).ConfigureAwait(false);
        try
        {
            var url = $"http://127.0.0.1:{grant.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)}/ui/";
            var opened = SystemOpener.Open(support.Spawner, support.Environment(), support.Opener, url);
            return new UiStartResult(ensured.Outcome == UiServiceOutcome.Already ? UiStartKind.Reused : UiStartKind.Started, opened, null);
        }
        finally
        {
            await ui.DropLeaseAsync(grant.Lease).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Run once at daemon start, after the socket listens: acts on the record the last holder left. A bridge the daemon took away comes
    /// back; any other record leaves it closed. With no <paramref name="starter"/> nothing reopens.
    /// </summary>
    internal static async Task ApplyRestartAsync(UiStarter? starter, string runDir, int self, Action<string> log, CancellationToken cancellation)
    {
        try
        {
            var svc = starter?._svc ?? new UiServiceSeams(runDir, self, () => self, static _ => 0, static _ => null, static (_, _) => false, string.Empty, StartWait, TimeSpan.Zero);
            var record = UiService.Read(svc);
            var deadline = DateTime.UtcNow + LiveWait;
            while (UiService.IsLive(svc, record) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(LivePoll, cancellation).ConfigureAwait(false);
                record = UiService.Read(svc);
            }

            // a start or a stop may hold the lock: what it leaves there when it lets go is what counts, and a start's holder has its lease by then
            using var gate = await UiService.WaitLockAsync(svc, cancellation).ConfigureAwait(false);
            if (gate is null)
            {
                log("bridge not reopened: another start or stop is running");
                return;
            }

            record = UiService.Read(svc);
            if (UiService.IsLive(svc, record))
            {
                log("bridge not reopened: a holder on this daemon owns it");
            }
            else if (IsDaemonEnded(record) && starter is not null)
            {
                await starter.ReopenAsync(log, cancellation).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            log("bridge reopen abandoned: the daemon is stopping");
        }
        catch (Exception e)
        {
            log($"bridge restart check failed: {e.GetType().Name}");
        }
    }

    private static bool IsDaemonEnded(Ah.Core.Json.JsonObject? record) => UiService.DaemonEndedIt(record);

    /// <summary>
    /// One attempt, no retry. The caller holds the lock until the holder reports, so a stop that comes later waits for it and then stops that holder.
    /// A daemon stop that cancels the wait ends the holder spawned here: left running, it would find no daemon and start one after the stop.
    /// </summary>
    private async Task ReopenAsync(Action<string> log, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        (int Pid, string? Start) spawned = default;
        var svc = _svc with
        {
            Spawn = argv =>
            {
                var pid = _svc.Spawn(argv);
                spawned = (pid, pid > 1 ? support.StartTime(pid) : null);
                return pid;
            },
        };
        try
        {
            var ensured = await UiService.EnsureAsync(svc, cancellation).ConfigureAwait(false);
            log($"bridge reopen: {ensured.Outcome}{(ensured.Message is { } why ? $": {why}" : string.Empty)}");
        }
        catch (OperationCanceledException) when (spawned.Start is { } start && Ah.Core.Time.ProcessStart.Compare(support.StartTime(spawned.Pid), static () => null, start) == Ah.Core.Time.StartMatch.Same &&!UiService.IsLive(_svc, UiService.Read(_svc)))
        {
            // the same start time: still that holder, not a reused pid. One that already holds the lease ends with the daemon and records
            // that itself; a TERM would record a user's stop, and the next start would not reopen.
            var sent = support.Signal?.Invoke(spawned.Pid, Term) == true;
            log($"bridge reopen: TERM {(sent ? "sent" : "not sent")} to holder {spawned.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            throw;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            UiService.Finish(_svc, "refused", $"the holder could not be started ({e.GetType().Name})");
            log($"bridge reopen failed: {e.GetType().Name}");
        }
    }
}
