using System.Globalization;
using Ah.Core.Json;
using Ah.Hierarchy;

namespace Ah.Watch;

/// <summary>One tracked long-running job as its record file says (<c>&lt;hier&gt;/jobs/&lt;team&gt;/&lt;id&gt;.json</c>).</summary>
/// <param name="Id">The job id; it comes from the request-id generator, so every id-keyed path takes it unchanged.</param>
/// <param name="Team">The owner's team.</param>
/// <param name="Owner">The owning member's name, or <c>orchestrator</c>.</param>
/// <param name="Session">The owner's session id.</param>
/// <param name="Kind">One of test, build, slot, smoke, bg.</param>
/// <param name="Label">What the job runs, for the alert.</param>
/// <param name="Pid">The job's process, once known.</param>
/// <param name="Eta">The eta word the clock reads.</param>
/// <param name="SentAtMs">When the job started, in epoch milliseconds.</param>
/// <param name="ExtendedAtMs">When the owner last extended the job, which restarts its clock, or null.</param>
/// <param name="CapAtMs">When the wrapper's hard cap ends the job, or null when no cap is known.</param>
/// <param name="Ended">Whether the job has ended.</param>
/// <param name="Background">Whether the owner's turn is free while the job runs.</param>
/// <param name="Descendants">The pids below <paramref name="Pid"/> (a test host, for one), for the alert text.</param>
/// <param name="PidStart">The start time recorded with <paramref name="Pid"/>, which tells a reused pid from the job's own, or null.</param>
/// <param name="EndedAtMs">When the job ended, in epoch milliseconds, or null while it is open.</param>
/// <param name="Pgid">The process group of <paramref name="Pid"/>, which a child that outlives it stays in, or null.</param>
/// <param name="Slot">Where the job stands in the test-slot queue, or null when it has no slot record.</param>
public sealed record JobFact(
    string Id,
    string Team,
    string Owner,
    string Session,
    string Kind,
    string Label,
    int? Pid,
    string Eta,
    double SentAtMs,
    double? ExtendedAtMs,
    double? CapAtMs,
    bool Ended,
    bool Background,
    IReadOnlyList<int> Descendants,
    string? PidStart = null,
    double? EndedAtMs = null,
    int? Pgid = null,
    JobSlot? Slot = null);

/// <summary>Where a test-slot job stands in the machine-wide slot queue, read from the records <c>with-slot.sh</c> keeps.</summary>
/// <param name="Phase"><c>queued</c> while it waits for the slot, <c>running</c> once it holds it.</param>
/// <param name="SinceMs">When the wait began (queued) or the slot was acquired (running), in epoch milliseconds.</param>
/// <param name="WaitedMs">How long it waited before it acquired the slot; zero while queued.</param>
/// <param name="Pool">The slot pool's name.</param>
/// <param name="Holder">The pool's current holders, each as its label, pid and tokens, joined by commas; empty when none is known.</param>
/// <param name="Tokens">The tokens the job holds, or waits for.</param>
/// <param name="Free">The tokens free in the pool when the job was last read; 0 with no capacity.</param>
/// <param name="Capacity">The pool's capacity in tokens; 0 when the pool recorded none (a slot read before tokens existed).</param>
/// <param name="Ahead">While queued: the live tickets ahead of the job.</param>
public sealed record JobSlot(string Phase, double SinceMs, double WaitedMs, string Pool, string Holder, int Tokens = 1, int Free = 0, int Capacity = 0, int Ahead = 0);

/// <summary>An alert for the owner of a background job, mailed to the owner's own box.</summary>
/// <param name="Owner">The owner's name.</param>
/// <param name="Team">The owner's team.</param>
/// <param name="Session">The owner's session id.</param>
/// <param name="Text">The mail text.</param>
/// <param name="Data">The event data.</param>
/// <param name="Event">The mailbox event name.</param>
public sealed record JobOwnerMail(string Owner, string Team, string Session, string Text, JsonObject Data, string Event);

/// <summary>What one evaluation of the open jobs decided.</summary>
/// <param name="Events">The events for the team's orchestrating session.</param>
/// <param name="OwnerMails">The copies for owners of background jobs.</param>
public sealed record JobOutcome(IReadOnlyList<WatchEvent> Events, IReadOnlyList<JobOwnerMail> OwnerMails);

/// <summary>
/// The overrun alarms for tracked jobs. A job that passes its due time by the margin without ending raises <c>job-overdue</c> to the
/// team's orchestrating session, and a background job raises it to its owner as well; a foreground hang blocks the owner's own turn, so
/// only the Orchestrator can hear it. <c>cap-near</c> is the same event at nine tenths of a known cap. Each (job, step) is told once.
/// </summary>
internal static class JobEvents
{
    internal const string OverdueName = "job-overdue";
    internal const string OverdueStep = "overdue";
    internal const string CapNearStep = "cap-near";

    /// <summary>How far past its due time a job must run before the alarm, in milliseconds.</summary>
    internal const double MarginMs = 2 * 60 * 1000;

    /// <summary>The alarm steps an open job owes and when ahd owes each: shared by the daemon's evaluation and the failsafe that checks ahd kept its word.</summary>
    internal static List<(string Step, double AtMs)> StepsOf(JobFact job, Func<string, double> thresholdSeconds, double now, double pollMs)
    {
        var steps = new List<(string Step, double AtMs)> { (OverdueStep, DueMs(job, thresholdSeconds, now, pollMs) + MarginMs) };
        if (job.CapAtMs is { } cap && job.Slot is not { Phase: QueuedPhase })
        {
            var from = ClockStartMs(job);
            steps.Add((CapNearStep, from + (0.9 * (cap - from))));
        }

        return steps;
    }

    internal static JobOutcome Evaluate(
        string session,
        double now,
        double pollMs,
        IReadOnlyList<string?> teams,
        Func<string?, IReadOnlyList<JobFact>> jobsOf,
        Func<string, double> thresholdSeconds,
        ISet<string> told,
        out IReadOnlyDictionary<WatchEvent, string> keys)
    {
        var events = new List<WatchEvent>();
        var mails = new List<JobOwnerMail>();
        var keyed = new Dictionary<WatchEvent, string>();
        keys = keyed;
        var active = new HashSet<string>(StringComparer.Ordinal);
        foreach (var team in teams)
        {
            foreach (var job in jobsOf(team).Where(static j => !j.Ended))
            {
                var due = DueMs(job, thresholdSeconds, now, pollMs);
                foreach (var (step, atMs) in StepsOf(job, thresholdSeconds, now, pollMs).Where(s => now >= s.AtMs))
                {
                    var key = $"{session}|{job.Team}|{OverdueName}|{job.Id}|{step}";
                    if (!active.Add(key) || told.Contains(key))
                    {
                        continue;
                    }

                    var text = Text(job, step, now);
                    var data = new JsonObject
                    {
                        ["request_id"] = job.Id,
                        ["job"] = job.Id,
                        ["step"] = step,
                        ["kind"] = job.Kind,
                        ["member"] = job.Owner,
                        ["label"] = job.Label,
                        ["background"] = job.Background,
                    };
                    var overdue = new WatchEvent(job.Id, "JOB-OVERDUE", text, data) { Name = OverdueName, Team = team };
                    events.Add(overdue);
                    keyed[overdue] = key;
                    if (job.Background && job.Owner != HierarchyModule.OrchestratorRole)
                    {
                        mails.Add(new JobOwnerMail(job.Owner, job.Team, job.Session, OwnerText(job, step, now, due), data, OverdueName));
                    }
                }
            }
        }

        var prefix = session + "|";
        foreach (var gone in told.Where(k => k.StartsWith(prefix, StringComparison.Ordinal) && k.Contains("|" + OverdueName + "|", StringComparison.Ordinal) && !active.Contains(k)).ToList())
        {
            _ = told.Remove(gone);
        }

        return new JobOutcome(events, mails);
    }

    /// <summary>When the job is due, in epoch milliseconds: <see cref="DueMs"/> for a caller outside the watch.</summary>
    internal static double DueOf(JobFact job, Func<string, double> thresholdSeconds, double now, double pollMs) => DueMs(job, thresholdSeconds, now, pollMs);

    internal const string QueuedPhase = "queued";

    /// <summary>What a queued job waits for: its tokens when the pool recorded a capacity, else the slot.</summary>
    internal static string AskText(JobSlot slot) => slot.Capacity > 0 ? $"{slot.Tokens} test tokens" : "the test slot";

    /// <summary>The state of the bucket a queued job waits on: tokens free, tickets ahead, and who holds the rest.</summary>
    internal static string HolderText(JobSlot slot) =>
        (slot.Capacity > 0 ? $"{slot.Free}/{slot.Capacity} free, {slot.Ahead} ahead; " : string.Empty)
        + $"held by {(slot.Holder.Length > 0 ? slot.Holder : "an unknown holder")}";

    /// <summary>How long <c>with-slot.sh</c> waits for a slot before it gives up and exits, in milliseconds: a job queued longer than this is not plausible.</summary>
    internal const double SlotGiveUpMs = 3600 * 1000;

    /// <summary>When the job's own clock starts: the slot acquisition for a job holding the test slot, else the tool call.</summary>
    internal static double ClockStartMs(JobFact job) => ClockStartMs(job.Slot?.Phase, job.Slot?.SinceMs, job.SentAtMs);

    private static double ClockStartMs(string? slotPhase, double? slotSinceMs, double sentAtMs) => slotPhase is not null and not QueuedPhase && slotSinceMs is { } held ? held : sentAtMs;

    /// <summary>
    /// When the job is due: the one eta function, with the job's clock start (or its last extension) as the dispatch and its end as the answer.
    /// A job queued for the test slot has no due time while it waits; it is due only once it has queued past the script's own give-up.
    /// </summary>
    internal static double DueMs(JobFact job, Func<string, double> thresholdSeconds, double now, double pollMs) => StateOf(job, thresholdSeconds, now, pollMs).DueMs;

    /// <summary><see cref="StateOf(string, string, double, double, double?, string?, double?, bool, double, double)"/> over a job's own facts.</summary>
    internal static EtaStateResult StateOf(JobFact job, Func<string, double> thresholdSeconds, double now, double pollMs) =>
        StateOf(job.Id, job.Eta, thresholdSeconds(job.Eta), job.SentAtMs, job.ExtendedAtMs, job.Slot?.Phase, job.Slot?.SinceMs, job.Ended, now, pollMs);

    /// <summary>
    /// The state of a job from its file facts: the one rule behind the watch's due time and the read model's row state, so a job queued for the
    /// slot or holding it is judged the same in both.
    /// </summary>
    internal static EtaStateResult StateOf(
        string id, string eta, double thresholdSeconds, double sentAtMs, double? extendedAtMs, string? slotPhase, double? slotSinceMs, bool ended, double now, double pollMs)
    {
        if (slotPhase == QueuedPhase && slotSinceMs is { } queuedSince && !ended)
        {
            var due = Math.Max(queuedSince, extendedAtMs ?? 0) + SlotGiveUpMs;
            var late = now >= due;
            return new EtaStateResult(id, eta, null, due, null, late ? "overdue" : "working", late ? (now - due) / 1000 : null, null, 0, late ? null : due);
        }

        return EtaState.Compute(
            id,
            eta,
            thresholdSeconds,
            null,
            null,
            Math.Max(ClockStartMs(slotPhase, slotSinceMs, sentAtMs), extendedAtMs ?? 0),
            string.Empty,
            [],
            [],
            ended,
            null,
            now,
            pollMs);
    }

    /// <summary>The line the owner's waiter prints after <c>ahd wake: </c>: what is wrong with the job and what the owner may do about it.</summary>
    private static string OwnerText(JobFact job, string step, double now, double due)
    {
        var what = $"job {job.Id} ({job.Kind} {job.Label})";
        if (step == CapNearStep && job.CapAtMs is { } cap)
        {
            var left = (int)Math.Max(0, Math.Round((cap - now) / 60000));
            return $"{what} hits its hard cap in {left.ToString(CultureInfo.InvariantCulture)}m (at {Ah.Core.Time.Timestamps.ToIsoString(DateTimeOffset.FromUnixTimeMilliseconds((long)cap))}). Extend it, or expect the kill and report.";
        }

        var late = (int)Math.Max(0, Math.Round((now - due) / 60000));
        var pid = job.Pid is { } p ? $"; pid {p.ToString(CultureInfo.InvariantCulture)}" : string.Empty;
        return $"{what} is {late.ToString(CultureInfo.InvariantCulture)}m past its ETA {job.Eta}{pid}. Check it. Hung: kill the processes you started and report BLOCKED. Progressing: ah roster job extend {job.Id} --eta {job.Eta}.";
    }

    private static string Text(JobFact job, string step, double now)
    {
        var pids = job.Pid is { } pid ? $"pid {pid.ToString(CultureInfo.InvariantCulture)}" : string.Empty;
        if (job.Descendants.Count > 0)
        {
            var below = $"below it: {string.Join(", ", job.Descendants.Select(static d => d.ToString(CultureInfo.InvariantCulture)))}";
            pids = pids.Length > 0 ? $"{pids} ({below})" : below;
        }

        var where = pids.Length > 0 ? $"{pids}; " : string.Empty;
        if (job.Slot is { Phase: QueuedPhase } queued)
        {
            return $"ahd alarm: job-overdue — {job.Owner}'s {job.Kind} job {job.Id} has been queued for {AskText(queued)} for {DispatchClock.FmtAge(Math.Max(0, now - queued.SinceMs) / 1000)}, longer than with-slot's own limit ({job.Label}; {where}{HolderText(queued)})\n"
                + OperationalCall(job);
        }

        var age = DispatchClock.FmtAge(Math.Max(0, now - ClockStartMs(job)) / 1000);
        if (job.Slot is { WaitedMs: > 0 } held)
        {
            age += $" (waited {DispatchClock.FmtAge(held.WaitedMs / 1000)} for the test slot)";
        }

        var lead = step == CapNearStep
            ? $"ahd alarm: job-overdue — {job.Owner}'s {job.Kind} job {job.Id} is near its hard cap"
            : $"ahd alarm: job-overdue — {job.Owner}'s {job.Kind} job {job.Id} has run past its eta {job.Eta}";
        return $"{lead} ({job.Label}; {where}running {age})\n"
            + OperationalCall(job);
    }

    /// <summary>What the lead can do about an overdue job. <c>ah roster job extend</c> refuses anyone but the owner, so the lead asks the owner to run it.</summary>
    private static string OperationalCall(JobFact job) =>
        $"Operational call (O1/O4): under machine load a long run is normal, so wait unless its output has stopped (only {job.Owner} can extend it: tell them to run ah roster job extend {job.Id}). If it has stopped, kill the job's process or re-dispatch the item. The owner's own turn may be blocked on it.";
}
