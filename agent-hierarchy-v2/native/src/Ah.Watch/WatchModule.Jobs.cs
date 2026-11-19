using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Time;

namespace Ah.Watch;

public static partial class WatchModule
{
    /// <summary>The directory of a team's job records: <c>&lt;hierarchy&gt;/jobs/&lt;team&gt;</c>, with <c>_default</c> for the default team.</summary>
    public static string JobsDir(string hierarchyDir, string? team) =>
        Path.Combine(hierarchyDir, "jobs", string.IsNullOrEmpty(team) ? "_default" : team);

    /// <summary>
    /// The job records of <paramref name="team"/> (per-dir-files-to-db S8: its <c>jobs</c> rows). A record that cannot be read or lacks an
    /// id, an owner or a start is skipped and named to <paramref name="log"/>, so one bad record never hides the others.
    /// </summary>
    public static IReadOnlyList<JobFact> JobsFromFiles(Ah.Hierarchy.IHierarchyStatus status, string hierarchyDir, string? team, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(status);
        var dir = JobsDir(hierarchyDir, team);
        var folder = Path.GetFileName(dir);
        var jobs = new List<JobFact>();
        foreach (var row in status.JobRows(hierarchyDir).Where(r => r.Team == folder).OrderBy(static r => r.Id, StringComparer.Ordinal))
        {
            try
            {
                if (JobOf(JsonText.Parse(row.Body), team) is { } job)
                {
                    jobs.Add(job);
                }
            }
            catch (Exception e) when (e is JsonParseException or InvalidOperationException)
            {
                log?.Invoke($"job record {Path.Combine(dir, row.Id + ".json")} not read ({e.Message})");
            }
        }

        return jobs;
    }

    /// <summary>When <paramref name="job"/> is due, in epoch milliseconds, by the one eta function; <paramref name="thresholdSeconds"/> maps an eta word to its seconds.</summary>
    public static double JobDueMs(JobFact job, Func<string, double> thresholdSeconds, double nowMs, double pollMs)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(thresholdSeconds);
        return JobEvents.DueOf(job, thresholdSeconds, nowMs, pollMs);
    }

    /// <summary>Whether <paramref name="command"/> is a plain shell run with <c>-c</c>, the wrapper a tool call starts a command under.</summary>
    public static bool JobIsWrapper(string? command) => JobPids.IsShellWrapper(command);

    /// <summary>How long a background job may have no process before it is ended as unseen.</summary>
    public const double JobUnseenAfterMs = 90_000;

    /// <summary>Whether the job's process, or a process of its group that started with it, is still running.</summary>
    public static bool JobRunning(JobFact job, IProcessTable table, Func<int, bool> pidAlive)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(pidAlive);
        if (job.Pid is { } pid && pidAlive(pid) && !Ah.Core.Time.ProcessIdentity.Reused(table, pid, job.PidStart))
        {
            return true;
        }

        return job.Kind == "bg" && job.Pgid is { } group && table.InGroup(group) is { } members
            && members.Any(n => n.Started is { } started && started.ToUnixTimeMilliseconds() + 1000 >= job.SentAtMs && JobPids.RunsJobsProgram(job, n));
    }

    /// <summary>The alarm steps ahd owes for each of <paramref name="openJobs"/>, with when each was owed: what the failsafe checks the Orchestrator's box for.</summary>
    public static IReadOnlyList<OverdueJobItem> OverdueJobItems(IReadOnlyList<JobFact> openJobs, Func<string, double> thresholdSeconds, double nowMs, IAhEnvironment env)
    {
        ArgumentNullException.ThrowIfNull(openJobs);
        ArgumentNullException.ThrowIfNull(thresholdSeconds);
        var pollMs = Liveness.PollMs(env);
        return [.. openJobs.SelectMany(j => JobEvents.StepsOf(j, thresholdSeconds, nowMs, pollMs).Select(s => new OverdueJobItem(j.Id, s.Step, j.Label, j.Owner, s.AtMs)))];
    }

    /// <summary>
    /// What the failsafe checks the Orchestrator's box for on behalf of the lane in <paramref name="stream"/> of <paramref name="parentTeam"/>: the escalation ahd owes once the lane's
    /// status record is stale (the same limit as the lane escalation's fourth reason). Null for a lane in a state that is never stale (done, paused) or with no record yet.
    /// </summary>
    public static OverdueLaneItem? OverdueLaneItemOf(string parentTeam, string stream, string subOrchestrator, string? streamState, double? updatedMs, string? charter) =>
        LaneEscalation.SilentSinceMs(streamState, updatedMs) is { } silent && updatedMs is { } epoch
            ? new OverdueLaneItem(LaneEscalation.RequestIdOf(parentTeam, stream), subOrchestrator, epoch, silent, charter)
            : null;

    /// <summary>
    /// Whether <paramref name="name"/>'s session <paramref name="session"/> still has an open background job: what keeps a member's waiter running.
    /// A job whose recorded process is in <paramref name="kin"/> (the waiter itself, its ancestors and descendants) does not count: a waiter never waits on itself.
    /// </summary>
    public static bool MemberHasOpenBackgroundJob(IReadOnlyList<JobFact> jobs, string name, string session, Func<JobFact, bool> jobOpen, IReadOnlySet<int>? kin = null)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(jobOpen);
        return jobs.Any(j => j.Background && j.Owner == name && j.Session == session && !(j.Pid is { } pid && kin?.Contains(pid) == true) && jobOpen(j));
    }

    /// <summary>
    /// The one test of whether a job is open, shared by the daemon sweep, the member Stop rule, the member waiter and the failsafe: not ended, and either its process is
    /// running (<paramref name="running"/>), or it has no process yet and is a foreground job, or it is a background job less than <see cref="JobUnseenAfterMs"/> old.
    /// </summary>
    public static bool JobOpen(JobFact job, Func<JobFact, bool> running, double nowMs, IReadOnlyList<ProcessNode>? below = null)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(running);
        if (job.Ended)
        {
            return false;
        }

        if (job.Pid is not null)
        {
            return running(job);
        }

        return job.Background ? nowMs - job.SentAtMs < JobUnseenAfterMs : JobForegroundOver(job, below, nowMs) is null;
    }

    /// <summary>How long a foreground job with no process of its own is given before it can count as over.</summary>
    private const double ForegroundOverAfterMs = 30_000;

    /// <summary>The slack around the job's start within which its own process must have started: one table second before, ten after.</summary>
    private const double ForegroundStartBeforeMs = 1000;

    private const double ForegroundStartAfterMs = 10_000;

    /// <summary>
    /// Why a foreground job with no known process is over, or null while it may still run. It is <c>gone</c> only when the owner's
    /// process tree (<paramref name="below"/>, null when the owner is unknown or dead or the walk failed) is known, every process in it
    /// has a known start, and none started within a few seconds of the job: the wrapper that runs it, or waits for a slot, would still be
    /// there. A later job of the same session proves nothing, since parallel tool calls and subagents share the session. The job
    /// must be past 30 s old, and anything unknown keeps it open.
    /// </summary>
    public static string? JobForegroundOver(JobFact job, IReadOnlyList<ProcessNode>? below, double nowMs)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.Ended || job.Background || job.Pid is not null || nowMs - job.SentAtMs <= ForegroundOverAfterMs)
        {
            return null;
        }

        return below is not null
            && !below.Any(n => n.Started is not { } started
                || (started.ToUnixTimeMilliseconds() >= job.SentAtMs - ForegroundStartBeforeMs
                    && started.ToUnixTimeMilliseconds() <= job.SentAtMs + ForegroundStartAfterMs))
            ? "gone"
            : null;
    }

    /// <summary>The process among <paramref name="below"/> (those below the owner's session) that is <paramref name="job"/>'s own and that no job in <paramref name="taken"/> holds, or null.</summary>
    public static ProcessNode? JobPick(JobFact job, IReadOnlyList<ProcessNode> below, ISet<int>? taken = null)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(below);
        return JobPids.Pick(job, below, taken);
    }

    /// <summary>
    /// The open job of <paramref name="owner"/> that is furthest past its due time at <paramref name="nowMs"/>, or null when none is
    /// overdue. A live member that owns one is <c>blocked-on-job</c>.
    /// </summary>
    public static JobFact? JobOverdueOf(IReadOnlyList<JobFact> jobs, string owner, Func<string, double> thresholdSeconds, double nowMs, double pollMs)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(thresholdSeconds);
        return jobs
            .Where(j => !j.Ended && j.Owner == owner && JobEvents.StateOf(j, thresholdSeconds, nowMs, pollMs).State == "overdue")
            .OrderBy(j => JobEvents.DueOf(j, thresholdSeconds, nowMs, pollMs))
            .FirstOrDefault();
    }

    /// <summary>The eta state of an open dispatch at <paramref name="nowMs"/>, by the one eta function.</summary>
    public static string OverdueStateOf(OverdueDispatch dispatch, double nowMs, double pollMs)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        return LaneEscalation.StateOf(dispatch, nowMs, pollMs);
    }

    /// <summary>
    /// Where the job whose process is <paramref name="pid"/> started at <paramref name="pidStart"/> stands in the test-slot queue, joined on the pid
    /// and start time of a live slot record, or null when either is missing or no record matches both (the job then keeps the ordinary clock).
    /// A queued job names the pool's holder.
    /// </summary>
    public static JobSlot? JobSlotFor(IReadOnlyList<SlotRecord> records, int? pid, string? pidStart)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (pid is not { } p || string.IsNullOrEmpty(pidStart) || records.FirstOrDefault(r => r.Pid == p && r.PidStart == pidStart) is not { } record)
        {
            return null;
        }

        if (!record.State.Waiting)
        {
            return new JobSlot("running", record.State.SinceMs, record.State.WaitedMs, record.State.Pool, string.Empty, record.State.Tokens, record.State.Free, record.State.Capacity);
        }

        var named = string.Join(", ", records.Where(r => !r.State.Waiting && r.State.Pool == record.State.Pool)
            .Select(r => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{r.Label} (pid {r.Pid}) \u00d7{r.State.Tokens}")));
        return new JobSlot("queued", record.State.SinceMs, 0, record.State.Pool, named, record.State.Tokens, record.State.Free, record.State.Capacity, record.State.Ahead);
    }

    /// <summary>What a queued job waits for, as the wake and alarm texts say it: its tokens when the pool recorded a capacity, else the slot.</summary>
    public static string QueuedAsk(JobSlot slot)
    {
        ArgumentNullException.ThrowIfNull(slot);
        return JobEvents.AskText(slot);
    }

    /// <summary>The bucket a queued job waits on, as the wake and alarm texts say it: tokens free, tickets ahead and the holders.</summary>
    public static string QueuedState(JobSlot slot)
    {
        ArgumentNullException.ThrowIfNull(slot);
        return JobEvents.HolderText(slot);
    }

    private static JobFact? JobOf(JsonValue value, string? team)
    {
        if (!value.IsMap)
        {
            return null;
        }

        var o = value.AsObject();
        static string? Text(JsonObject o, string key) => o.Get(key) is { IsText: true } v ? v.AsString() : null;
        double? Ms(string key) => Text(o, key) is { } text && Timestamps.TryParseMs(text, out var ms) ? ms : null;
        var kind = Text(o, "kind") ?? "bg";
        if (Text(o, "id") is not { Length: > 0 } id || Text(o, "owner") is not { Length: > 0 } owner || Ms("sent_at") is not { } sentAt)
        {
            return null;
        }

        return new JobFact(
            id,
            Text(o, "team") ?? team ?? "_default",
            owner,
            Text(o, "session_id") ?? string.Empty,
            kind,
            Text(o, "label") ?? string.Empty,
            o.Get("pid") is { IsNumber: true } pid ? (int)pid.AsNumber() : null,
            Text(o, "eta") ?? "medium",
            sentAt,
            Ms("extended_at"),
            Ms("cap_at"),
            Text(o, "ended_at") is { Length: > 0 },
            o.Get("background") is { IsBool: true } background ? background.AsBool() : kind == "bg",
            [],
            Text(o, "pid_start"),
            Ms("ended_at"),
            o.Get("pgid") is { IsNumber: true } pgid ? (int)pgid.AsNumber() : null,
            SlotOf(o.Get("slot")));
    }

    private static JobSlot? SlotOf(JsonValue? value)
    {
        if (value is not { IsMap: true } map)
        {
            return null;
        }

        var o = map.AsObject();
        return o.Get("phase") is { IsText: true } phase && phase.AsString() is "queued" or "running"
            && o.Get("since_ms") is { IsNumber: true } since
            ? new JobSlot(
                phase.AsString(),
                since.AsNumber(),
                o.Get("waited_ms") is { IsNumber: true } waited ? waited.AsNumber() : 0,
                o.Get("pool") is { IsText: true } pool ? pool.AsString() : string.Empty,
                o.Get("holder") is { IsText: true } holder ? holder.AsString() : string.Empty,
                Int(o, "tokens", 1),
                Int(o, "free", 0),
                Int(o, "capacity", 0),
                Int(o, "ahead", 0))
            : null;

        static int Int(JsonObject o, string key, int otherwise) => o.Get(key) is { IsNumber: true } n ? (int)n.AsNumber() : otherwise;
    }
}
