using System.Globalization;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Time;
using Ah.Core.Ui;
using Ah.Platform;
using static Ah.Core.Ui.UiService;

namespace Ah.Cli.Verbs.AhdCtl;

/// <summary>
/// <c>ahd ui start|stop|restart|status</c>: the browser page kept up by a background <c>ahd ui</c> that holds the lease (the hidden
/// <c>--service-hold PARENT</c> mode), described by <c>run/ui-service.json</c>. <c>start</c> takes a
/// short lease of its own for the port of the link it prints. Every lease, the background one included, is the daemon's user-only route.
/// </summary>
internal static class UiServiceVerb
{
    internal const string Hold = UiService.Hold;

    internal static readonly TimeSpan StartWait = TimeSpan.FromSeconds(10);

    internal static readonly TimeSpan StopWait = TimeSpan.FromSeconds(5);

    private const int Term = 15;

    private const int Kill = 9;

    /// <summary>What the service half needs from the machine.</summary>
    internal sealed record ServiceSeams(
        string RunDir,
        int Self,
        Func<int> Parent,
        Func<IReadOnlyList<string>, int> Spawn,
        Func<int, string?> StartTime,
        Func<int, int, bool> Signal,
        string ProcessPath,
        TimeSpan StartWait,
        TimeSpan StopWait,
        Func<int, string?>? LocalStartTime = null)
        : UiServiceSeams(RunDir, Self, Parent, Spawn, StartTime, Signal, ProcessPath, StartWait, StopWait, LocalStartTime);

    internal static bool Handles(string arg) => arg is "start" or "stop" or "restart" or "status" or Hold;

    internal static ServiceSeams RealSeams(CliContext context, IReadOnlyDictionary<string, string?> environment)
    {
        var runDir = Path.Combine(context.Env.AhHome, "run");
        var spawner = PlatformModule.CreateDetachedSpawner();
        var table = PlatformModule.CreateProcessTable(context.Runner);
        return new ServiceSeams(
            runDir,
            System.Environment.ProcessId,
            PlatformModule.ParentProcessId,
            argv => spawner.SpawnDetached(new DetachedSpawnRequest(argv, "/", environment, Path.Combine(runDir, "ui-service.log"))),
            pid => table.StartTime(pid),
            PlatformModule.SignalProcess,
            context.ProcessPath,
            StartWait,
            StopWait,
            pid => table.LocalStartTime(pid));
    }

    internal static async Task<int> RunAsync(IReadOnlyList<string> args, TextWriter stdout, TextWriter stderr, UiVerb.Seams seams, ServiceSeams svc, CancellationToken stop)
    {
        var rest = args.Skip(1).ToList();
        var noOpen = rest.Remove("--no-open");
        var json = rest.Remove("--json");
        if (args[0] == Hold)
        {
            return rest.Count == 1 && int.TryParse(rest[0], NumberStyles.None, CultureInfo.InvariantCulture, out var parent)
                ? await HoldAsync(parent, seams, svc, stderr, stop).ConfigureAwait(false)
                : Usage(stderr);
        }

        if (rest.Count > 0 || (noOpen && args[0] is not ("start" or "restart")))
        {
            return Usage(stderr);
        }

        return args[0] switch
        {
            "start" => await StartAsync(noOpen, json, seams, svc, stdout, stderr, stop).ConfigureAwait(false),
            "stop" => await StopAsync(json, svc, stdout, stderr).ConfigureAwait(false),
            "restart" => await RestartAsync(noOpen, json, seams, svc, stdout, stderr, stop).ConfigureAwait(false),
            _ => Status(json, svc, stdout),
        };
    }

    private static async Task<int> RestartAsync(bool noOpen, bool json, UiVerb.Seams seams, ServiceSeams svc, TextWriter stdout, TextWriter stderr, CancellationToken stop)
    {
        var stopped = await StopAsync(json, svc, TextWriter.Null, stderr).ConfigureAwait(false);
        return stopped != 0 ? stopped : await StartAsync(noOpen, json, seams, svc, stdout, stderr, stop).ConfigureAwait(false);
    }

    private static int Usage(TextWriter stderr)
    {
        stderr.WriteLine("ahd ui: usage: ahd ui start|restart [--no-open] [--json] | ahd ui stop [--json] | ahd ui status [--json]");
        return UiVerb.UsageError;
    }

    /// <summary>The background process: takes the service lease while its parent (the <c>start</c> that spawned it) is still its parent, and keeps it.</summary>
    private static async Task<int> HoldAsync(int parent, UiVerb.Seams seams, ServiceSeams svc, TextWriter stderr, CancellationToken stop)
    {
        if (svc.Parent() != parent)
        {
            stderr.WriteLine("ahd ui: the service holder is started by `ahd ui start` only");
            return 1;
        }

        string? failure = null;
        try
        {
            if (await seams.FindSocket(stop).ConfigureAwait(false) is not { } socket)
            {
                failure = "the daemon is not reachable";
            }
            else
            {
                var open = seams.OpenServiceLease ?? seams.OpenLease;
                var held = await open(socket, stop).ConfigureAwait(false);
                await using (held.ConfigureAwait(false))
                {
                    if (held.Status == 200 && UiVerb.TryParseGrant(held.FirstLine.Span, out var port))
                    {
                        // the start that spawned this deleted the record first, so a stop seen here is newer than the spawn
                        if (Read(svc)?.Get("stopped_reason") is { IsText: true } reason && reason.AsString() == "stop")
                        {
                            return 0;
                        }

                        Write(svc, new JsonObject { ["pid"] = svc.Self, ["pid_start"] = svc.StartTime(svc.Self) ?? string.Empty, ["port"] = port, ["started_at"] = Timestamps.ToIsoString(DateTimeOffset.UtcNow) });
                        var interrupted = new TaskCompletionSource();
                        await using var registration = stop.Register(static state => ((TaskCompletionSource)state!).TrySetResult(), interrupted);
                        var ended = await Task.WhenAny(held.Ended, interrupted.Task).ConfigureAwait(false) == held.Ended && !stop.IsCancellationRequested;
                        Finish(svc, ended ? DaemonEnded : "stop", null);
                        return 0;
                    }

                    failure = held.Status == 200 ? "the daemon's answer could not be read" : UiVerb.RefusalOf(held);
                }
            }
        }
        catch (Exception e) when (e is IOException or HttpRequestException or System.Net.Sockets.SocketException or OperationCanceledException)
        {
            failure = $"the daemon is not reachable ({e.GetType().Name})";
        }

        Finish(svc, "refused", failure);
        stderr.WriteLine($"ahd ui: {failure}");
        return UiVerb.Refused;
    }

    private static async Task<int> StartAsync(bool noOpen, bool json, UiVerb.Seams seams, ServiceSeams svc, TextWriter stdout, TextWriter stderr, CancellationToken stop)
    {
        using var gate = await WaitLockAsync(svc, stop).ConfigureAwait(false);
        if (gate is null)
        {
            stderr.WriteLine("ahd ui: another `ahd ui start` is running");
            return UiVerb.Refused;
        }

        var ensured = await EnsureAsync(svc, stop).ConfigureAwait(false);
        var already = ensured.Outcome == UiServiceOutcome.Already;
        if (ensured.Outcome == UiServiceOutcome.Stopped)
        {
            stderr.WriteLine($"ahd ui: {ensured.Message}");
            return UiVerb.Refused;
        }

        if (ensured.Outcome == UiServiceOutcome.TimedOut)
        {
            stderr.WriteLine("ahd ui: the service did not report a lease in time");
            return UiVerb.Refused;
        }

        if (await seams.FindSocket(stop).ConfigureAwait(false) is not { } socket)
        {
            stderr.WriteLine("ahd ui: the daemon is not reachable");
            return UiVerb.Unreachable;
        }

        var held = await seams.OpenLease(socket, stop).ConfigureAwait(false);
        await using (held.ConfigureAwait(false))
        {
            if (held.Status != 200 || !UiVerb.TryParseGrant(held.FirstLine.Span, out var port))
            {
                stderr.WriteLine($"ahd ui: {(held.Status == 200 ? "the daemon's answer could not be read" : UiVerb.RefusalOf(held))}");
                return UiVerb.Refused;
            }

            if (UiVerb.MovedNote(held.FirstLine.Span, port) is { } moved)
            {
                stderr.WriteLine(moved);
            }

            var url = $"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}/ui/";
            var opened = !noOpen && seams.OpenBrowser(url);
            var pid = Pid(Read(svc));
            stdout.WriteLine(
                json
                    ? JsonText.Stringify(new JsonObject { ["ok"] = true, ["running"] = true, ["already_running"] = already, ["pid"] = pid, ["port"] = port, ["url"] = url, ["opened"] = opened }, 0)
                    : $"UI {(already ? "already running" : "running")} · pid {pid.ToString(CultureInfo.InvariantCulture)} · {url}{(noOpen ? string.Empty : opened ? " · opened in the browser" : " · the browser could not be opened: open the link yourself")}");
            return 0;
        }
    }

    private static async Task<int> StopAsync(bool json, ServiceSeams svc, TextWriter stdout, TextWriter stderr)
    {
        // A start, the daemon's reopen included, holds the lock until its holder is live or has stopped, so what is read below is final and
        // a reopen that comes later reads this stop. A holder that reports after a start timed out normally finds the stop left below, and leaves.
        // Ceiling, accepted for beta: such a holder's check and its live write are not atomic, so a stop landing between them is overwritten and
        // the page stays up after a stop that said it was not running; a second stop ends it. A separate marker file would close this.
        using var gate = await WaitLockAsync(svc, CancellationToken.None).ConfigureAwait(false);
        var state = Read(svc);
        if (!IsLive(svc, state))
        {
            // the next daemon start reopens on a record saying the daemon ended it, and a holder still to report needs a stop to find: a stop always wins
            if (state?.Get("stopped_reason") is not { IsText: true } || UiService.DaemonEndedIt(state))
            {
                Finish(svc, "stop", null);
            }

            stdout.WriteLine(json ? JsonText.Stringify(new JsonObject { ["ok"] = true, ["was_running"] = false }, 0) : "UI was not running");
            return 0;
        }

        var pid = Pid(state);
        var recorded = state!.Get("pid_start") is { IsText: true } rs ? rs.AsString() : string.Empty;
        _ = svc.Signal(pid, Term);
        var deadline = DateTime.UtcNow + svc.StopWait;
        while (UiService.HoldsRecorded(svc, pid, recorded) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20).ConfigureAwait(false);
        }

        // a pid whose start time is no longer the recorded one is not the service (it ended and the pid was reused): never signalled again
        if (UiService.HoldsRecorded(svc, pid, recorded))
        {
            _ = svc.Signal(pid, Kill);
            await Task.Delay(100).ConfigureAwait(false);
            if (UiService.HoldsRecorded(svc, pid, recorded))
            {
                stderr.WriteLine($"ahd ui: the service (pid {pid.ToString(CultureInfo.InvariantCulture)}) did not stop");
                return 1;
            }
        }

        // a holder that saw the daemon end as the signal arrived has written that instead: this stop still has to win
        if (Read(svc)?.Get("stopped_reason") is not { IsText: true } ended || ended.AsString() != "stop")
        {
            Finish(svc, "stop", null);
        }

        stdout.WriteLine(json ? JsonText.Stringify(new JsonObject { ["ok"] = true, ["was_running"] = true, ["pid"] = pid }, 0) : $"UI stopped · pid {pid.ToString(CultureInfo.InvariantCulture)}");
        return 0;
    }

    private static int Status(bool json, ServiceSeams svc, TextWriter stdout)
    {
        var state = Read(svc);

        // a stop of a page that never ran leaves a record for a late holder to find; to the user nothing ever ran
        if (state?.Get("started_at") is null && state?.Get("stopped_reason") is { IsText: true } left && left.AsString() == "stop")
        {
            state = null;
        }

        var doc = new JsonObject { ["ok"] = true, ["sign_in"] = "ahd ui start" };
        if (!IsLive(svc, state))
        {
            doc["running"] = false;
            if (state?.Get("stopped_at") is { IsText: true } at)
            {
                doc["stopped_at"] = at.AsString();
            }

            if (state?.Get("stopped_reason") is { IsText: true } reason)
            {
                doc["stopped_reason"] = reason.AsString();
            }

            if (state?.Get("message") is { IsText: true } message)
            {
                doc["message"] = message.AsString();
            }

            stdout.WriteLine(json ? JsonText.Stringify(doc, 0) : StoppedLine(doc));
            return 3;
        }

        var started = state!.Get("started_at") is { IsText: true } s ? s.AsString() : string.Empty;
        doc["running"] = true;
        doc["pid"] = Pid(state);
        doc["port"] = state.Get("port") is { IsNumber: true } p ? (int)p.AsNumber() : 0;
        doc["url"] = $"http://127.0.0.1:{(state.Get("port") is { IsNumber: true } q ? (int)q.AsNumber() : 0).ToString(CultureInfo.InvariantCulture)}/ui/";
        doc["started_at"] = started;
        if (DateTimeOffset.TryParse(started, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var from))
        {
            doc["uptime_s"] = (long)(DateTimeOffset.UtcNow - from).TotalSeconds;
        }

        stdout.WriteLine(json ? JsonText.Stringify(doc, 0) : $"UI running · pid {Pid(state).ToString(CultureInfo.InvariantCulture)} · {doc["url"].AsString()}{(doc.Get("uptime_s") is { IsNumber: true } up ? " · up " + Age((long)up.AsNumber()) : string.Empty)}");
        return 0;
    }

    private static string StoppedLine(JsonObject doc)
    {
        if (doc.Get("stopped_reason") is not { IsText: true } reason)
        {
            return "UI not running";
        }

        var detail = doc.Get("message") is { IsText: true } m ? $"{reason.AsString()}: {m.AsString()}" : reason.AsString();
        var at = doc.Get("stopped_at") is { IsText: true } t && DateTimeOffset.TryParse(t.AsString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when)
            ? $" at {when.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}"
            : string.Empty;
        return $"UI stopped ({detail}){at}";
    }

    private static string Age(long seconds) => seconds switch
    {
        < 60 => $"{seconds.ToString(CultureInfo.InvariantCulture)}s",
        < 3600 => $"{(seconds / 60).ToString(CultureInfo.InvariantCulture)}m",
        < 86400 => $"{(seconds / 3600).ToString(CultureInfo.InvariantCulture)}h {(seconds % 3600 / 60).ToString(CultureInfo.InvariantCulture)}m",
        _ => $"{(seconds / 86400).ToString(CultureInfo.InvariantCulture)}d {(seconds % 86400 / 3600).ToString(CultureInfo.InvariantCulture)}h",
    };
}
