using System.Globalization;
using Ah.Core.Json;
using Ah.Core.Time;

namespace Ah.Core.Ui;

/// <summary>What the browser page's background service needs from the machine.</summary>
public record UiServiceSeams(
    string RunDir,
    int Self,
    Func<int> Parent,
    Func<IReadOnlyList<string>, int> Spawn,
    Func<int, string?> StartTime,
    Func<int, int, bool> Signal,
    string ProcessPath,
    TimeSpan StartWait,
    TimeSpan StopWait,
    Func<int, string?>? LocalStartTime = null);

/// <summary>How an attempt to have the service running ended.</summary>
public enum UiServiceOutcome
{
    /// <summary>A live service was already there.</summary>
    Already,

    /// <summary>A holder was spawned and reported its lease.</summary>
    Started,

    /// <summary>The holder reported that it stopped; <see cref="UiServiceStart.Message"/> says why.</summary>
    Stopped,

    /// <summary>The holder did not report a lease in time.</summary>
    TimedOut,
}

/// <summary>The outcome of <see cref="UiService.EnsureAsync"/>.</summary>
public readonly record struct UiServiceStart(UiServiceOutcome Outcome, string? Message);

/// <summary>
/// The browser page kept up by a background <c>ahd ui</c> that holds the bridge lease (its hidden <c>--service-hold PARENT</c> mode),
/// described by <c>run/ui-service.json</c>. This is the state file, the lock that keeps two starts from both spawning a holder, and
/// the start that spawns one and waits for it to report its lease.
/// </summary>
public static class UiService
{
    /// <summary>The argument of the holder process.</summary>
    public const string Hold = "--service-hold";

    /// <summary>The stop reason meaning the user opened the page and only the daemon ended it: the next daemon start brings it back.</summary>
    public const string DaemonEnded = "daemon_ended";

    /// <summary>
    /// Set on the record a start writes in place of one the daemon ended, so a start that times out or a holder that is refused while
    /// the next daemon is still coming up does not lose the reopen: the record without it would void the page's sessions at that daemon's start.
    /// </summary>
    public const string EndedBefore = "daemon_ended_before";

    /// <summary>Whether the record says the daemon ended the page, now or before a start that did not take: the next daemon start reopens it. A user's stop always wins.</summary>
    public static bool DaemonEndedIt(JsonObject? record) =>
        record?.Get("stopped_reason") is { IsText: true } reason
            ? reason.AsString() == DaemonEnded || (reason.AsString() != "stop" && record.Get(EndedBefore) is { IsTruthy: true })
            : record?.Get(EndedBefore) is { IsTruthy: true };

    /// <summary>Holds <c>run/ui-service.lock</c> until the handle goes (or the process ends); null when another start holds it.</summary>
    public static FileStream? TryLock(UiServiceSeams svc)
    {
        ArgumentNullException.ThrowIfNull(svc);
        _ = Directory.CreateDirectory(svc.RunDir);
        try
        {
            return new FileStream(Path.Combine(svc.RunDir, "ui-service.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>Waits up to <see cref="UiServiceSeams.StartWait"/> for <see cref="TryLock"/>, the longest a start holds it while its holder reports; null when it is still held.</summary>
    public static async Task<FileStream?> WaitLockAsync(UiServiceSeams svc, CancellationToken stop)
    {
        ArgumentNullException.ThrowIfNull(svc);
        var deadline = DateTime.UtcNow + svc.StartWait;
        while (true)
        {
            if (TryLock(svc) is { } gate)
            {
                return gate;
            }

            if (DateTime.UtcNow >= deadline)
            {
                return null;
            }

            await Task.Delay(20, stop).ConfigureAwait(false);
        }
    }

    /// <summary>Whether the pid still holds the process whose start time the state recorded, in the UTC form or the local form older records hold.</summary>
    public static bool HoldsRecorded(UiServiceSeams svc, int pid, string recorded)
    {
        ArgumentNullException.ThrowIfNull(svc);
        return ProcessStart.Compare(svc.StartTime(pid), () => svc.LocalStartTime?.Invoke(pid), recorded) is StartMatch.Same or StartMatch.Legacy;
    }

    /// <summary>The state file's object, or null when there is none or it cannot be read.</summary>
    public static JsonObject? Read(UiServiceSeams svc)
    {
        ArgumentNullException.ThrowIfNull(svc);
        try
        {
            return JsonText.TryParse(File.ReadAllBytes(StatePath(svc)), out var v) && v.IsMap ? v.AsObject() : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Live: the state names a port and no stop, and that pid is alive with the start time the state recorded (a reused pid is not the service).</summary>
    public static bool IsLive(UiServiceSeams svc, JsonObject? state) =>
        state is not null && state.Get("stopped_reason") is null && state.Get("port") is { IsNumber: true }
        && Pid(state) is > 1 and var pid && state.Get("pid_start") is { IsText: true } start && HoldsRecorded(svc, pid, start.AsString());

    /// <summary>The pid the state names, or 0.</summary>
    public static int Pid(JsonObject? state) => state?.Get("pid") is { IsNumber: true } n ? (int)n.AsNumber() : 0;

    /// <summary>Replaces the state file by a temp file and a rename.</summary>
    public static void Write(UiServiceSeams svc, JsonObject doc)
    {
        ArgumentNullException.ThrowIfNull(svc);
        _ = Directory.CreateDirectory(svc.RunDir);
        var temp = $"{StatePath(svc)}.{svc.Self.ToString(CultureInfo.InvariantCulture)}.tmp";
        File.WriteAllText(temp, JsonText.Stringify(doc, 0) + "\n");
        File.Move(temp, StatePath(svc), overwrite: true);
    }

    /// <summary>Records that the service stopped, and why.</summary>
    public static void Finish(UiServiceSeams svc, string reason, string? message)
    {
        var doc = Read(svc) ?? new JsonObject { ["pid"] = svc.Self };
        doc["stopped_at"] = Timestamps.ToIsoString(DateTimeOffset.UtcNow);
        doc["stopped_reason"] = reason;
        if (message is not null)
        {
            doc["message"] = message;
        }

        Write(svc, doc);
    }

    /// <summary>Removes the state file; one that cannot be removed is overwritten by the service.</summary>
    public static void TryDelete(UiServiceSeams svc)
    {
        try
        {
            File.Delete(StatePath(svc));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // a state file that cannot be removed is overwritten by the service
        }
    }

    /// <summary>
    /// Has a live service: when none is, spawns the holder and waits up to <see cref="UiServiceSeams.StartWait"/> for it to report its
    /// lease or its stop. The caller holds <see cref="TryLock"/>.
    /// </summary>
    public static async Task<UiServiceStart> EnsureAsync(UiServiceSeams svc, CancellationToken stop)
    {
        ArgumentNullException.ThrowIfNull(svc);
        if (IsLive(svc, Read(svc)))
        {
            return new UiServiceStart(UiServiceOutcome.Already, null);
        }

        if (DaemonEndedIt(Read(svc)))
        {
            Write(svc, new JsonObject { [EndedBefore] = true });
        }
        else
        {
            TryDelete(svc);
        }

        _ = svc.Spawn([svc.ProcessPath, "ui", Hold, svc.Self.ToString(CultureInfo.InvariantCulture)]);
        var deadline = DateTime.UtcNow + svc.StartWait;
        while (true)
        {
            var state = Read(svc);
            if (state?.Get("stopped_reason") is { IsText: true })
            {
                return new UiServiceStart(UiServiceOutcome.Stopped, state.Get("message") is { IsText: true } m ? m.AsString() : "the service did not start");
            }

            if (IsLive(svc, state))
            {
                return new UiServiceStart(UiServiceOutcome.Started, null);
            }

            if (DateTime.UtcNow >= deadline)
            {
                return new UiServiceStart(UiServiceOutcome.TimedOut, null);
            }

            await Task.Delay(20, stop).ConfigureAwait(false);
        }
    }

    private static string StatePath(UiServiceSeams svc) => Path.Combine(svc.RunDir, "ui-service.json");
}
