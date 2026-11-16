using Ah.Hierarchy;
using System.Globalization;
using Ah.Core.Json;
using Ah.Core.Time;

namespace Ah.Watch;

/// <summary>An open dispatch as the overdue timer reads it from the files: what <see cref="EtaState.Compute"/> needs.</summary>
public sealed record OverdueDispatch(
    string RequestId,
    string Slug,
    string ToName,
    string Eta,
    double ThresholdSeconds,
    string? Created,
    string? ToAddr,
    double FloorMs,
    string Session,
    IReadOnlyList<JsonObject> PeerRecords,
    IReadOnlyList<JsonObject> Gates,
    double? HeldUntilMs)
{
    /// <summary>Whether the request was filed for a plain send: unarmed, ahd owes it nothing (<see cref="Liveness.PlainSendArmedAt"/>).</summary>
    public bool PlainSend { get; init; }
}

/// <summary>An ahd event in the Orchestrator's box as the files show it. <paramref name="AckedAtMs"/> is null while nothing has acknowledged it; <paramref name="Created"/> is when the daemon wrote it.</summary>
public sealed record AhdEventRow(string Id, string? RequestId, string? Step, double? AckedAtMs, string? Created = null, string? Item = null, string? Name = null);

/// <summary>A queued item the files show stalled: <paramref name="Key"/> names the episode, <paramref name="AtMs"/> is when ahd owed its <c>queue-stall</c> event.</summary>
public sealed record OverdueQueueItem(string Key, string Item, string Member, double AtMs);

/// <summary>An open job and one alarm step ahd owes for it: <paramref name="AtMs"/> is when ahd owed its <c>job-overdue</c> event for <paramref name="Step"/>.</summary>
public sealed record OverdueJobItem(string Job, string Step, string Label, string Member, double AtMs);

/// <summary>
/// A lane whose status record is stale: <paramref name="RequestId"/> is the id ahd's <c>lane-escalated</c> event for it carries, <paramref name="EpochMs"/> the record's last write
/// (an escalation older than that belongs to an earlier episode), <paramref name="AtMs"/> when ahd owed the event, and <paramref name="Charter"/> the open request the Orchestrator sent
/// the lane's sub-orchestrator, or null when none is open.
/// </summary>
public sealed record OverdueLaneItem(string RequestId, string SubOrchestrator, double EpochMs, double AtMs, string? Charter);

/// <summary>What one scan of the files found.</summary>
public sealed record OverdueScan(IReadOnlyList<OverdueDispatch> Open, IReadOnlyList<AhdEventRow> Events, IReadOnlyList<OverdueQueueItem>? Queue = null, IReadOnlyList<OverdueJobItem>? Jobs = null, IReadOnlyList<OverdueLaneItem>? Lanes = null);

/// <summary>The steps already reported, kept across restarts of the timer.</summary>
public sealed record OverdueMemory(IReadOnlySet<string> Reported, IReadOnlySet<string> UnknownLogged, double? UnhealthySinceMs);

/// <summary>What the overdue timer reads and writes. It reads files and writes only its own memory, health file and the journal.</summary>
public interface IOverdueHost
{
    DateTimeOffset Now { get; }

    void Sleep(TimeSpan span);

    OverdueScan Scan();

    OverdueMemory LoadMemory();

    void SaveMemory(OverdueMemory memory);

    void WriteHealth(string requestId, string stage, double deadlineMs, double unhealthySinceMs);

    void ClearHealth();

    void Journal(JsonObject row);

    void Print(string line);

    /// <summary>
    /// Called once per report, when ahd has missed something it owed: the host starts <c>ahd heal</c> if the daemon is there and does
    /// not answer. The heal judges whether it is hung; a daemon that answers is left alone. A host with no heal does nothing.
    /// </summary>
    void StartHeal()
    {
    }

    /// <summary>The line every report starts with (the checklist to read first), or null for none.</summary>
    string? FirstLine => null;
}

/// <summary>The timings of the overdue timer.</summary>
public sealed record OverdueSettings(TimeSpan Rescan, TimeSpan Margin, TimeSpan Heartbeat)
{
    public static OverdueSettings Defaults { get; } = new(TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(20));

    /// <summary>The defaults with <c>AH_STALL_RESCAN</c>, <c>AH_STALL_MARGIN</c> and <c>AH_STALL_HEARTBEAT</c> (seconds, also read as <c>AHD_STALL_*</c>) applied.</summary>
    public static OverdueSettings From(Ah.Abstractions.IAhEnvironment env)
    {
        ArgumentNullException.ThrowIfNull(env);
        TimeSpan Seconds(string name, TimeSpan fallback) =>
            (env.GetVariable($"AH_STALL_{name}") is { Length: > 0 } a ? a : env.GetVariable($"AHD_STALL_{name}")) is { } text
            && double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && n > 0 && n < 86_400 * 30
                ? TimeSpan.FromSeconds(n)
                : fallback;
        return new OverdueSettings(Seconds("RESCAN", Defaults.Rescan), Seconds("MARGIN", Defaults.Margin), Seconds("HEARTBEAT", Defaults.Heartbeat));
    }
}

/// <summary>
/// The failsafe behind ahd's dispatch clock. It shares no delivery path with ahd: it reads the request files, the gate rows and the
/// Orchestrator's mail files, arms each open dispatch for ahd's next step, and exits 0 when ahd has not delivered that step by its
/// expected time plus a margin. A step counts as delivered when an ahd event carrying the dispatch's request id and that step has been
/// acknowledged; an event that was written and never acknowledged does not count.
/// </summary>
internal sealed class OverdueTimer(IOverdueHost host, OverdueSettings settings, string diagnoseCommand, string restartCommand)
{
    private readonly HashSet<string> _known = new(StringComparer.Ordinal);

    public int Run()
    {
        var started = host.Now;
        var memory = host.LoadMemory();
        var reported = new HashSet<string>(memory.Reported, StringComparer.Ordinal);
        var unknownLogged = new HashSet<string>(memory.UnknownLogged, StringComparer.Ordinal);
        var unhealthySince = memory.UnhealthySinceMs;
        var awake = new AwakeClock(Math.Max(WatchDependencies.DefaultSleepGapMs, 2 * settings.Rescan.TotalMilliseconds));
        while (true)
        {
            var nowMs = host.Now.ToUnixTimeMilliseconds();

            // ahd does not count time asleep, so neither does this. A timer restarted just after a wake would not know of the sleep, so a wake restarts the
            // heartbeat too: that outlasts ahd's check-in as long as the largest threshold is no more than the heartbeat. A restart for any other reason still loses it.
            if (awake.Poll(nowMs))
            {
                started = host.Now;
            }

            var scan = host.Scan();
            foreach (var d in scan.Open)
            {
                _ = _known.Add(d.RequestId);
            }

            foreach (var j in scan.Jobs ?? [])
            {
                _ = _known.Add(j.Job);
            }

            foreach (var l in scan.Lanes ?? [])
            {
                _ = _known.Add(l.RequestId);
            }

            var misses = new List<(OverdueDispatch D, string Step, double DueMs, double DeadlineMs)>();
            foreach (var d in scan.Open)
            {
                if (Armed(d, scan.Events, nowMs) is not { } armed || reported.Contains($"{d.RequestId}|{armed.Step}"))
                {
                    continue;
                }

                if (awake.Of(nowMs) < awake.Of(armed.AtMs - armed.IntervalMs) + armed.IntervalMs + settings.Margin.TotalMilliseconds)
                {
                    continue;
                }

                misses.Add((d, armed.Step, armed.DueMs, armed.AtMs + settings.Margin.TotalMilliseconds));
            }

            foreach (var e in scan.Events.Where(static e => e.AckedAtMs is not null))
            {
                if (unhealthySince is { } since && e.AckedAtMs > since)
                {
                    host.Journal(new JsonObject { ["at"] = Timestamps.ToIsoString(host.Now), ["kind"] = "ahd-recovered", ["subject"] = e.RequestId ?? e.Id });
                    host.ClearHealth();
                    unhealthySince = null;
                }

                if ((e.RequestId is null || !_known.Contains(e.RequestId)) && unknownLogged.Add(e.Id))
                {
                    host.Journal(new JsonObject
                    {
                        ["at"] = Timestamps.ToIsoString(host.Now),
                        ["kind"] = "unknown-id",
                        ["subject"] = e.Id,
                        ["evidence"] = e.RequestId is null ? "delivered ahd event carries no request_id" : $"delivered ahd event names request {e.RequestId}, which is not an open dispatch",
                    });
                }
            }

            var queueMisses = (scan.Queue ?? [])
                .Where(q => nowMs >= q.AtMs + settings.Margin.TotalMilliseconds && !reported.Contains("queue|" + q.Key) && !QueueDelivered(q, scan.Events))
                .ToList();
            var jobMisses = (scan.Jobs ?? [])
                .Where(j => nowMs >= j.AtMs + settings.Margin.TotalMilliseconds && !reported.Contains(JobKey(j)) && !JobDelivered(j, scan.Events))
                .ToList();
            var laneMisses = (scan.Lanes ?? [])
                .Where(l => nowMs >= l.AtMs + settings.Margin.TotalMilliseconds && !reported.Contains(LaneKey(l)) && !LaneDelivered(l, scan.Events))
                .ToList();
            if (misses.Count > 0 || queueMisses.Count > 0 || jobMisses.Count > 0 || laneMisses.Count > 0)
            {
                unhealthySince ??= nowMs;
                foreach (var l in laneMisses)
                {
                    _ = reported.Add(LaneKey(l));
                    host.Journal(new JsonObject
                    {
                        ["at"] = Timestamps.ToIsoString(host.Now),
                        ["kind"] = "ahd-missed",
                        ["subject"] = l.RequestId,
                        ["evidence"] = $"ahd lane-escalated not delivered by {Timestamps.ToIsoString(DateTimeOffset.FromUnixTimeMilliseconds((long)(l.AtMs + settings.Margin.TotalMilliseconds)))}",
                    });
                }

                foreach (var j in jobMisses)
                {
                    _ = reported.Add(JobKey(j));
                    host.Journal(new JsonObject
                    {
                        ["at"] = Timestamps.ToIsoString(host.Now),
                        ["kind"] = "ahd-missed",
                        ["subject"] = j.Job,
                        ["evidence"] = $"ahd job-overdue {j.Step} not delivered by {Timestamps.ToIsoString(DateTimeOffset.FromUnixTimeMilliseconds((long)(j.AtMs + settings.Margin.TotalMilliseconds)))}",
                    });
                }

                foreach (var q in queueMisses)
                {
                    _ = reported.Add("queue|" + q.Key);
                    host.Journal(new JsonObject
                    {
                        ["at"] = Timestamps.ToIsoString(host.Now),
                        ["kind"] = "ahd-missed",
                        ["subject"] = q.Item,
                        ["evidence"] = $"ahd queue-stall not delivered by {Timestamps.ToIsoString(DateTimeOffset.FromUnixTimeMilliseconds((long)(q.AtMs + settings.Margin.TotalMilliseconds)))}",
                    });
                }

                foreach (var (d, step, _, deadline) in misses)
                {
                    _ = reported.Add($"{d.RequestId}|{step}");
                    host.WriteHealth(d.RequestId, step, deadline, unhealthySince.Value);
                    host.Journal(new JsonObject
                    {
                        ["at"] = Timestamps.ToIsoString(host.Now),
                        ["kind"] = "ahd-missed",
                        ["subject"] = d.RequestId,
                        ["evidence"] = $"ahd {step} not delivered by {Timestamps.ToIsoString(DateTimeOffset.FromUnixTimeMilliseconds((long)deadline))}",
                    });
                }

                host.SaveMemory(new OverdueMemory(reported, unknownLogged, unhealthySince));
                host.StartHeal();
                if (host.FirstLine is { } first)
                {
                    host.Print(first);
                }

                ReportQueue(queueMisses, nowMs);
                ReportJobs(jobMisses, nowMs);
                ReportLanes(laneMisses, nowMs);
                if (misses.Count > 0)
                {
                    Report(misses);
                }

                return 0;
            }

            if (host.Now - started >= settings.Heartbeat)
            {
                host.SaveMemory(new OverdueMemory(reported, unknownLogged, unhealthySince));
                var due = scan.Open
                    .Select(d => Armed(d, scan.Events, nowMs))
                    .Where(static a => a is not null)
                    .Select(static a => a!.Value.AtMs)
                    .DefaultIfEmpty(double.NaN)
                    .Min();
                var overdue = scan.Open.Count(d => EtaOf(d, nowMs).State == "overdue");
                host.Print($"overdue timer: heartbeat; open dispatches: {scan.Open.Count.ToString(CultureInfo.InvariantCulture)}; overdue: {overdue.ToString(CultureInfo.InvariantCulture)}; earliest due {(double.IsNaN(due) ? "-" : Clock(due))}");
                host.Print($"restart: {restartCommand}");
                return 0;
            }

            host.SaveMemory(new OverdueMemory(reported, unknownLogged, unhealthySince));
            host.Sleep(settings.Rescan);
        }
    }

    private static bool QueueDelivered(OverdueQueueItem q, IReadOnlyList<AhdEventRow> events) =>
        events.Any(e => e.Name == "queue-stall" && e.Item == q.Item && e.AckedAtMs is not null && e.Created is not null && Timestamps.TryParseMs(e.Created, out var created) && created >= q.AtMs);

    private static string JobKey(OverdueJobItem j) => $"job|{j.Job}|{j.Step}";

    private static bool JobDelivered(OverdueJobItem j, IReadOnlyList<AhdEventRow> events) =>
        events.Any(e => e.Name == JobEvents.OverdueName && e.RequestId == j.Job && e.Step == j.Step && e.AckedAtMs is not null);

    private static string LaneKey(OverdueLaneItem l) => $"lane|{l.RequestId}|{l.EpochMs.ToString("F0", CultureInfo.InvariantCulture)}";

    /// <summary>Whether the Orchestrator acknowledged an escalation of the lane written since its status record was last written: one for an older episode does not count.</summary>
    private static bool LaneDelivered(OverdueLaneItem l, IReadOnlyList<AhdEventRow> events) =>
        events.Any(e => e.Name == LaneEscalation.EscalatedName && e.RequestId == l.RequestId && e.AckedAtMs is not null
            && e.Created is not null && Timestamps.TryParseMs(e.Created, out var created) && created >= l.EpochMs);

    private void ReportLanes(List<OverdueLaneItem> laneMisses, double nowMs)
    {
        foreach (var l in laneMisses)
        {
            var minutes = Math.Max(0, (long)((nowMs - l.EpochMs) / 60000));
            host.Print($"FAILSAFE HIT (ah bug): lane {l.SubOrchestrator} silent; its status record is {minutes.ToString(CultureInfo.InvariantCulture)}m old; ahd lane-escalated not delivered or not acknowledged");
        }

        if (laneMisses.Count > 0)
        {
            var diagnose = laneMisses[0].Charter is { } charter ? $"{diagnoseCommand} {charter}" : $"read lane {laneMisses[0].SubOrchestrator}'s requests (no open charter request to diagnose)";
            host.Print($"1) {diagnose}  2) tell the user the diagnosis line (PushNotification if unattended)  3) add the diagnosis to the run's friction list as an ah bug  4) restart: {restartCommand} (run_in_background)");
        }
    }

    private void ReportJobs(List<OverdueJobItem> jobMisses, double nowMs)
    {
        foreach (var j in jobMisses)
        {
            var minutes = Math.Max(0, (long)((nowMs - j.AtMs) / 60000));
            host.Print($"FAILSAFE HIT (ah bug): job-overdue {j.Job} ({j.Label}) of {j.Member} {j.Step} overdue by {minutes.ToString(CultureInfo.InvariantCulture)}m; ahd job-overdue not delivered or not acknowledged");
        }

        if (jobMisses.Count > 0)
        {
            host.Print($"1) check job {jobMisses[0].Job}: hung, kill it and tell {jobMisses[0].Member}; progressing, only {jobMisses[0].Member} can extend it: tell them to run ah roster job extend {jobMisses[0].Job}  2) tell the user the diagnosis line (PushNotification if unattended)  3) add the diagnosis to the run's friction list as an ah bug  4) restart: {restartCommand} (run_in_background)");
        }
    }

    private void ReportQueue(List<OverdueQueueItem> queueMisses, double nowMs)
    {
        foreach (var q in queueMisses)
        {
            var minutes = Math.Max(0, (long)((nowMs - q.AtMs) / 60000));
            host.Print($"FAILSAFE HIT (ah bug): queue-stall {q.Item} → {q.Member} overdue by {minutes.ToString(CultureInfo.InvariantCulture)}m; ahd queue-stall not delivered or not acknowledged");
        }

        if (queueMisses.Count > 0)
        {
            host.Print($"1) dispatch {queueMisses[0].Item} to {queueMisses[0].Member}, or mark it blocked/waiting_user with a note  2) tell the user the diagnosis line (PushNotification if unattended)  3) add the diagnosis to the run's friction list as an ah bug  4) restart: {restartCommand} (run_in_background)");
        }
    }

    private static EtaStateResult EtaOf(OverdueDispatch d, double nowMs) =>
        EtaState.Compute(d.RequestId, d.Eta, d.ThresholdSeconds, d.Created, d.ToAddr, Math.Max(d.FloorMs, ArmedAt(d) ?? 0), d.Session, d.PeerRecords, d.Gates, false, d.HeldUntilMs, nowMs, 15_000);

    private static double? ArmedAt(OverdueDispatch d) => d.PlainSend ? Liveness.PlainSendArmedAt(d.PeerRecords, d.Session, d.RequestId) : null;

    /// <summary>
    /// The step ahd still owes the dispatch, when it was due and the awake time that had to pass up to then: null once both steps were delivered. While
    /// ahd writes that it is holding the silent step back it owes nothing yet, so the step counts from its last such row; if ahd stops, or stops holding, the row goes old.
    /// </summary>
    private static (string Step, double AtMs, double DueMs, double IntervalMs)? Armed(OverdueDispatch d, IReadOnlyList<AhdEventRow> events, double nowMs)
    {
        // An unarmed plain send has no clock, so ahd owes no step for it: the same reading the dispatch clock skips it by (fault 1 of lane-ops-batches § A5).
        if (d.PlainSend && ArmedAt(d) is null)
        {
            return null;
        }

        var eta = EtaOf(d, nowMs);
        bool Delivered(string step) => events.Any(e => e.RequestId == d.RequestId && e.Step == step && e.AckedAtMs is not null);
        var t = d.ThresholdSeconds * 1000;
        if (!Delivered("check-in"))
        {
            return ("check-in", eta.Checkins == 0 && eta.Next is { } n ? n.AtMs : eta.DueMs, eta.DueMs, t);
        }

        if (!Delivered("silent") && eta.SilentDueMs is { } silent)
        {
            // The step waits out a hold with a known end, such as a paused lane's, which writes no row.
            var at = Math.Max(eta.Next?.Step == "silent" ? eta.Next.AtMs : silent, d.HeldUntilMs ?? 0);

            // A firing ends a hold and writes a nudge: the lead gets its whole margin from there, not from the last row before it.
            var heldAt = Liveness.GateTimes(d.Gates, DispatchClock.SilentHeldRow, d.Session, d.RequestId).DefaultIfEmpty(0).Max();
            heldAt = heldAt > 0 ? Math.Max(heldAt, Liveness.GateTimes(d.Gates, "liveness-nudge", d.Session, d.RequestId).DefaultIfEmpty(0).Max()) : 0;
            return heldAt >= at ? ("silent", heldAt, silent, 0) : ("silent", at, silent, t * HierarchyModule.CheckinCadence[1]);
        }

        return null;
    }

    private static string Clock(double ms) =>
        DateTimeOffset.FromUnixTimeMilliseconds((long)ms).ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);

    private void Report(List<(OverdueDispatch D, string Step, double DueMs, double DeadlineMs)> misses)
    {
        var nowMs = host.Now.ToUnixTimeMilliseconds();
        foreach (var (d, step, dueMs, _) in misses)
        {
            var minutes = Math.Max(0, (long)((nowMs - dueMs) / 60000));
            host.Print(
                $"FAILSAFE HIT (ah bug): {d.RequestId} {d.Slug} → {d.ToName} eta {d.Eta} sent {(d.Created is not null && Timestamps.TryParseMs(d.Created, out var ms) ? Clock(ms) : "?")} overdue by {minutes.ToString(CultureInfo.InvariantCulture)}m; ahd {step} not delivered");
        }

        var first = misses[0].D.RequestId;
        host.Print(
            $"1) {diagnoseCommand} {first}  2) handle the late dispatch yourself  3) tell the user the diagnosis line (PushNotification if unattended)  4) add the diagnosis to the run's friction list as an ah bug  5) restart: {restartCommand} (run_in_background)");
    }
}
