using Ah.Core.Json;
using Ah.Abstractions;
using Ah.Hierarchy;

namespace Ah.Watch;

public static partial class WatchModule
{
    /// <summary>The state of one dispatch from the rows the watch reads.</summary>
    public static EtaStateResult ComputeEtaState(
        string requestId,
        string eta,
        double thresholdSeconds,
        string? created,
        string? toAddr,
        double floorMs,
        string sessionId,
        IReadOnlyList<JsonObject> peerRecords,
        IReadOnlyList<JsonObject> gateRows,
        bool answered,
        double? heldUntilMs,
        double nowMs,
        double pollMs) =>
        EtaState.Compute(requestId, eta, thresholdSeconds, created, toAddr, floorMs, sessionId, peerRecords, gateRows, answered, heldUntilMs, nowMs, pollMs);

    /// <summary>The cause of a missed dispatch step and its evidence, by the first matching rule.</summary>
    public static DiagnoseVerdict Diagnose(DiagnoseFacts facts) => StallDiagnosis.Decide(facts);

    /// <summary>Runs the ahd waiter until an event wakes it, the heartbeat passes or the daemon stays down; returns the exit code.</summary>
    public static int RunWaiter(IWakeHost host, StallSettings settings) => new Waiter(host, settings, [new EventMailSource(host)]).Run();

    /// <summary>Runs the overdue timer until a dispatch step is missed or the heartbeat passes; returns the exit code.</summary>
    public static int RunOverdueTimer(IOverdueHost host, OverdueSettings settings, string diagnoseCommand, string restartCommand) =>
        new OverdueTimer(host, settings, diagnoseCommand, restartCommand).Run();

    /// <summary>Registers the stall-watch job: it judges every team's members once per period and mails the Orchestrator when one is down.</summary>
    public static void RegisterStallWatch(IJobRegistry jobs, IStallHost host, StallSettings settings, Func<DateTimeOffset> now, Action<string>? stamp = null) =>
        new StallWatch(host, settings, now, stamp).Register(jobs);

    /// <summary>Registers the bare-watch job: it finds members whose Claude ah did not launch, marks their rows and mails the Orchestrator once per change; with <paramref name="episodes"/> it also raises <c>worktrees-piling-up</c>.</summary>
    public static void RegisterBareWatch(IJobRegistry jobs, IBareHost host, TimeSpan period, Func<DateTimeOffset> now, Action<string>? stamp = null, ILoadWatch? load = null, IAlarmEpisodes? episodes = null) =>
        new BareWatch(host, period, now, stamp, load: load, worktrees: episodes is null ? null : new WorktreeWatch(episodes, now)).Register(jobs);

    /// <summary>Makes the daemon's machine-load monitor over <paramref name="source"/>.</summary>
    public static ILoadWatch CreateLoadWatch(ILoadSource source, Func<DateTimeOffset> now) => new LoadWatch(source, now);

    /// <summary>
    /// Makes the store of what a daemon job has already told the Orchestrator; <paramref name="load"/> returns the saved text or null,
    /// <paramref name="save"/> replaces it, and <paramref name="keep"/> receives saved text that cannot be parsed before it is replaced.
    /// </summary>
    public static IAlarmEpisodes CreateAlarmEpisodes(Func<string?> load, Action<string> save, Func<DateTimeOffset> now, Action<string>? keep = null) =>
        new AlarmEpisodes(load, save, now, keep);

    /// <summary>The argv of <c>alarm.command</c> in the user config text <paramref name="configText"/>; null when it is unset, unreadable or not an array of non-empty strings.</summary>
    public static IReadOnlyList<string>? AlarmCommandOf(string? configText)
    {
        try
        {
            if (configText is null || JsonText.Parse(configText).Member("alarm")?.Member("command") is not { IsArray: true } command)
            {
                return null;
            }

            var argv = command.AsArray();
            return argv.Count > 0 && argv.All(static a => a.IsText && a.AsString().Length > 0) ? [.. argv.Select(static a => a.AsString())] : null;
        }
        catch (Exception e) when (e is FormatException or InvalidOperationException or JsonParseException)
        {
            return null;
        }
    }
}
