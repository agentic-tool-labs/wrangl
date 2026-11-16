namespace Ah.Core.Time;

/// <summary>What the evidence says about a member's session. Only <see cref="ProvenDead"/> may close, sweep, report down or show it gone.</summary>
public enum LivenessVerdict
{
    /// <summary>Positive evidence that the session is running.</summary>
    Live,

    /// <summary>Positive evidence that the session is gone: an observation or a record that is not older than the latest sign of life.</summary>
    ProvenDead,

    /// <summary>Nothing proves either; it counts as running wherever a member would be ended or removed.</summary>
    Unknown,
}

/// <summary>How a caller of the process seam treats doubt about a pid (one-liveness.md §3.1).</summary>
public enum LivenessPolicy
{
    /// <summary>Running only on a <see cref="LivenessVerdict.Live"/> verdict.</summary>
    ProvenRunning,

    /// <summary>Running when the pid is this user's running process and the verdict is not <see cref="LivenessVerdict.ProvenDead"/>.</summary>
    RunningOrUnknown,
}

/// <summary>What an observer saw of one pid.</summary>
public enum ProcessObservation
{
    /// <summary>No observation was taken.</summary>
    None,

    /// <summary>The pid is not running.</summary>
    Absent,

    /// <summary>The pid exists but belongs to another user.</summary>
    NotOwned,

    /// <summary>The pid is running as this user.</summary>
    Present,
}

/// <summary>Whether a process command line is the harness. Only ever raises a verdict.</summary>
public enum HarnessCmdline
{
    /// <summary>No command line could be read, or the caller cannot evaluate it.</summary>
    Unreadable,

    /// <summary>The command line is the harness.</summary>
    Harness,

    /// <summary>The command line is readable and is not the harness.</summary>
    NotHarness,
}

/// <summary>The newest peers.jsonl row of a member that has no registration.</summary>
public enum PeerRowKind
{
    /// <summary>No row.</summary>
    None,

    /// <summary>The row says up.</summary>
    Up,

    /// <summary>The row says down.</summary>
    Down,

    /// <summary>The row says the member was seen or briefed; <see cref="LivenessEvidence.SeenAgeSeconds"/> says when.</summary>
    Seen,
}

/// <summary>Every input of <see cref="LivenessRules.Judge"/>. The function reads no clock, file or process.</summary>
public sealed class LivenessEvidence
{
    public DateTimeOffset Now { get; init; }

    public bool HasRegistration { get; init; }

    public long? RegistrationPid { get; init; }

    public string? RegistrationStart { get; init; }

    /// <summary>
    /// False when the registration was written from a process whose command line was readable and is not a harness (a shell wrapper that
    /// can exit while the session goes on): its pid can show the member running, never that it is gone.
    /// </summary>
    public bool RegistrationPidProof { get; init; } = true;

    /// <summary>False for a harness other than Claude, which is not held to a start time or a command line.</summary>
    public bool ClaudeHarness { get; init; } = true;

    public bool HeartbeatFresh { get; init; }

    public PeerRowKind PeerRow { get; init; }

    public long? PeerPid { get; init; }

    public string? PeerStart { get; init; }

    public DateTimeOffset? PeerAt { get; init; }

    /// <summary>When the registration row was written, or null; it stands in for the process start when none was recorded.</summary>
    public DateTimeOffset? RegistrationAt { get; init; }

    /// <summary>When the machine last booted, or null when unknown. Null leaves every verdict as it would be without the boot rule.</summary>
    public DateTimeOffset? BootTime { get; init; }

    public double? SeenAgeSeconds { get; init; }

    /// <summary>What the observer saw of <see cref="LivenessRules.PidToObserve"/>.</summary>
    public ProcessObservation Observation { get; init; }

    public string? ObservedStartUtc { get; init; }

    /// <summary>The zone an older record's local start text was written in. Defaults to the machine's.</summary>
    public TimeZoneInfo LocalZone { get; init; } = TimeZoneInfo.Local;

    public HarnessCmdline Cmdline { get; init; }

    public DateTimeOffset? ObservedAt { get; init; }

    /// <summary>In the same observation the observer saw a live pid that is neither itself nor pid 1.</summary>
    public bool ObserverCanSeeReference { get; init; }

    /// <summary>The latest sign of life: registration, heartbeat, activity or an up row. Null when there is none.</summary>
    public DateTimeOffset? LastLifeAt { get; init; }

    /// <summary>The oldest an observation may be and still count. The caller names it from the refresh period of the source it observed.</summary>
    public required TimeSpan MaxObservationAge { get; init; }

    /// <summary>Why a live herdr answer says the member's pane shows a running agent (<see cref="LivenessRules.PaneShowsAgent"/>), or null. A stale pane file is not this.</summary>
    public string? PaneAgentWhy { get; init; }

    /// <summary>How long ago the member's row was spawned, while that is inside the starting window; otherwise null.</summary>
    public int? StartingAgeSeconds { get; init; }
}

/// <summary>The one verdict function. Only a positive observation or a record newer than any sign of life proves death; age, silence and missing data never do.</summary>
public static class LivenessRules
{
    /// <summary>How old the latest sign of life may be for a not-live member to still hold a name or a tree.</summary>
    public const int FreshSeconds = 1800;

    /// <summary>The start times of one process may differ by this much and still be the same process.</summary>
    public const int StartToleranceSeconds = 120;

    /// <summary>The oldest an observation may be, in seconds: twice the stall watch's down window (90 s). Callers set <see cref="LivenessEvidence.MaxObservationAge"/> from it.</summary>
    public const int ObservationAgeSeconds = 180;

    /// <summary>The pid and recorded start the observer must look at for this evidence, or null when no pid decides it.</summary>
    public static (long Pid, string? Start)? PidToObserve(LivenessEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (evidence.HasRegistration)
        {
            return evidence.RegistrationPid is { } registered ? (registered, evidence.RegistrationStart) : null;
        }

        return evidence.PeerRow is PeerRowKind.Up or PeerRowKind.Down && evidence.PeerPid is { } pid ? (pid, evidence.PeerStart) : null;
    }

    /// <summary>
    /// Whether a process was recorded before the machine booted, so the pid names nothing that is running now: by the process start
    /// recorded beside it when that reads as an instant, else by <paramref name="rowTime"/>. False when the boot time is unknown or
    /// neither instant is.
    /// </summary>
    public static bool RecordedBeforeBoot(string? recordedStart, DateTimeOffset? rowTime, DateTimeOffset? bootTime)
    {
        if (bootTime is not { } boot)
        {
            return false;
        }

        DateTimeOffset? recorded = !string.IsNullOrEmpty(recordedStart) && ProcessStart.TryParseUtc(recordedStart, out var started) ? started : rowTime;
        return recorded is { } at && at < boot;
    }

    /// <summary>The reason a verdict gives when <see cref="RecordedBeforeBoot"/> holds.</summary>
    public static string BeforeBootWhy(DateTimeOffset boot) => $"recorded before the boot at {boot.UtcDateTime:O}";

    private static readonly string[] RunningPaneStatuses = ["working", "blocked", "idle", "done"];

    /// <summary>Whether a herdr answer shows a running agent. A pane whose agent exited can fall back to a shell that herdr still lists, so a shell does not count.</summary>
    public static bool PaneShowsAgent(string? agentStatus, string? agentType) =>
        agentStatus is not null && RunningPaneStatuses.Contains(agentStatus, StringComparer.Ordinal)
        && !string.Equals(agentType, "shell", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The last step of <see cref="Judge"/>, for a caller that already holds a verdict: a running pane agent raises it to live, and a verdict
    /// that is still not live inside the starting window is unknown. A pane can only raise: no pane agent changes nothing.
    /// </summary>
    public static (LivenessVerdict Verdict, string Why) Settle((LivenessVerdict Verdict, string Why) verdict, string? paneAgentWhy, int? startingAgeSeconds)
    {
        if (paneAgentWhy is not null)
        {
            return (LivenessVerdict.Live, paneAgentWhy);
        }

        return verdict.Verdict != LivenessVerdict.Live && startingAgeSeconds is { } age
            ? (LivenessVerdict.Unknown, $"starting (spawned {age}s ago)")
            : verdict;
    }

    public static (LivenessVerdict Verdict, string Why) Judge(LivenessEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        return Settle(JudgeEvidence(evidence), evidence.PaneAgentWhy, evidence.StartingAgeSeconds);
    }

    private static (LivenessVerdict Verdict, string Why) JudgeEvidence(LivenessEvidence evidence)
    {
        if (evidence.BootTime is { } boot
            && (evidence.HasRegistration
                ? evidence.RegistrationPid is not null && RecordedBeforeBoot(evidence.RegistrationStart, evidence.RegistrationAt, boot)
                : evidence.PeerRow is PeerRowKind.Up or PeerRowKind.Down && evidence.PeerPid is not null && RecordedBeforeBoot(evidence.PeerStart, evidence.PeerAt, boot)))
        {
            return (LivenessVerdict.ProvenDead, BeforeBootWhy(boot));
        }

        if (evidence.HasRegistration)
        {
            if (evidence.RegistrationPid is null)
            {
                return evidence.HeartbeatFresh
                    ? (LivenessVerdict.Live, "registration live (heartbeat)")
                    : (LivenessVerdict.Unknown, "registration has no pid and no fresh heartbeat");
            }

            var byPid = ByPid(evidence, evidence.RegistrationStart);
            return !evidence.RegistrationPidProof && byPid.Verdict == LivenessVerdict.ProvenDead
                ? (LivenessVerdict.Unknown, "its registration's pid is not shown to be the harness, so its exit does not show the session is gone")
                : byPid;
        }

        switch (evidence.PeerRow)
        {
            case PeerRowKind.Down:
                // The status alone is a written claim, not an observation: only a pid and the start it was recorded with can be checked.
                if (evidence.PeerPid is null || string.IsNullOrEmpty(evidence.PeerStart))
                {
                    return (LivenessVerdict.Unknown, "its latest peers.jsonl row is down, but it names no process and start to prove it by");
                }

                if (ByPid(evidence, evidence.PeerStart) is { Verdict: LivenessVerdict.Live } overrides)
                {
                    return overrides;
                }

                // A row dated after now (a skewed clock) can never be outlived by a later sign of life, so it cannot be set against one.
                if (evidence.PeerAt is { } rowAt && rowAt > evidence.Now.AddSeconds(FutureSlackSeconds))
                {
                    return (LivenessVerdict.Unknown, "its down row is dated in the future");
                }

                return NotOlderThanLife(evidence, evidence.PeerAt)
                    ? (LivenessVerdict.ProvenDead, "its latest peers.jsonl row is down")
                    : (LivenessVerdict.Unknown, "its down row is older than the latest sign of life");
            case PeerRowKind.Up:
                if (evidence.PeerPid is null)
                {
                    return (LivenessVerdict.Unknown, "its peers.jsonl row names no pid");
                }

                // A peers row is not screened for wrapper pids when it is written, so without the start it was recorded with, nothing ties
                // its pid to the session: the pid can show the member running, never that it is gone.
                return ByPid(evidence, evidence.PeerStart) is { Verdict: LivenessVerdict.ProvenDead } && string.IsNullOrEmpty(evidence.PeerStart)
                    ? (LivenessVerdict.Unknown, "its peers.jsonl row records no start time, so its pid does not show the session is gone")
                    : ByPid(evidence, evidence.PeerStart);
            case PeerRowKind.Seen:
                return evidence.SeenAgeSeconds is { } age && age < FreshSeconds
                    ? (LivenessVerdict.Live, "seen recently")
                    : (LivenessVerdict.Unknown, "last seen too long ago to say");
            default:
                return (LivenessVerdict.Unknown, "no registration and no peers.jsonl row");
        }
    }

    /// <summary>
    /// Whether a member holds its name or work tree: it is <see cref="LivenessVerdict.Live"/>, or <see cref="LivenessVerdict.Unknown"/>
    /// with a sign of life newer than <see cref="FreshSeconds"/>. Proven dead, or unknown with only old or no evidence, does not.
    /// </summary>
    public static bool HoldsClaim(LivenessVerdict verdict, DateTimeOffset? lastLifeAt, DateTimeOffset now) =>
        verdict == LivenessVerdict.Live
        || (verdict == LivenessVerdict.Unknown && lastLifeAt is { } life && (now - life).TotalSeconds < FreshSeconds);

    /// <summary>
    /// Whether the observer, in this same pass, reads the start of its own parent, a pid that is neither itself nor pid 1. Only an observer
    /// that can see another process can say a pid it does not see is gone.
    /// </summary>
    public static bool ObserverSeesAParent(Ah.Abstractions.IProcessTable table, int observerPid)
    {
        ArgumentNullException.ThrowIfNull(table);
        return table.Ancestors(observerPid) is { Count: > 0 } chain && chain[0] > 1 && table.StartTime(chain[0]) is not null;
    }

    /// <summary>
    /// <see cref="ObserverSeesAParent"/>, or else a registered process (a pid above 1 and the start it was recorded with) that the table
    /// shows with exactly that start. An observer whose parent is 1 (a daemon) still sees other processes when it sees one it knows.
    /// </summary>
    public static bool ObserverSeesAReference(Ah.Abstractions.IProcessTable table, int observerPid, IEnumerable<(long Pid, string? Start)> registered, long? exceptPid = null) =>
        ReferenceSeen(table, observerPid, registered, exceptPid) is not null;

    /// <summary>The process that makes <see cref="ObserverSeesAReference"/> true, with the start the table showed for it, and whether it is the parent; null when none.</summary>
    internal static (long Pid, string Start, bool Parent)? ReferenceSeen(Ah.Abstractions.IProcessTable table, int observerPid, IEnumerable<(long Pid, string? Start)> registered, long? exceptPid)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(registered);
        if (table.Ancestors(observerPid) is { Count: > 0 } chain && chain[0] > 1 && table.StartTime(chain[0]) is { } parentStart)
        {
            return (chain[0], parentStart, true);
        }

        foreach (var (pid, start) in registered)
        {
            if (pid > 1 && pid <= int.MaxValue && pid != exceptPid && !string.IsNullOrEmpty(start) && table.StartTime((int)pid) is { } observed
                && ProcessStart.IsSame(ProcessStart.Compare(observed, static () => null, start)))
            {
                return (pid, start, false);
            }
        }

        return null;
    }

    private const int FutureSlackSeconds = 120;

    private static bool NotOlderThanLife(LivenessEvidence e, DateTimeOffset? at) =>
        at is { } instant && (e.LastLifeAt is not { } life || instant >= life);

    private static (LivenessVerdict Verdict, string Why) ByPid(LivenessEvidence e, string? recordedStart)
    {
        if (e.Observation == ProcessObservation.None || e.ObservedAt is not { } observed
            || e.Now - observed > e.MaxObservationAge || !NotOlderThanLife(e, observed))
        {
            return (LivenessVerdict.Unknown, "no usable observation of its process");
        }

        if (e.Observation is ProcessObservation.Absent or ProcessObservation.NotOwned)
        {
            return e.ObserverCanSeeReference
                ? (LivenessVerdict.ProvenDead, e.Observation == ProcessObservation.Absent ? "its process is not running" : "its process is not running as this user")
                : (LivenessVerdict.Unknown, "its process was not seen, and the observer could not see other processes");
        }

        if (!string.IsNullOrEmpty(recordedStart))
        {
            return Start(e, recordedStart) switch
            {
                StartRelation.Same => (LivenessVerdict.Live, "its process started when recorded"),
                StartRelation.Reused => (LivenessVerdict.ProvenDead, "its pid now belongs to another process (its start time differs)"),
                _ => (LivenessVerdict.Unknown, "its recorded start time cannot be compared with the process's"),
            };
        }

        return !e.ClaudeHarness || e.Cmdline == HarnessCmdline.Harness
            ? (LivenessVerdict.Live, "its process runs the harness")
            : (LivenessVerdict.Unknown, "its process has no recorded start and no harness command line to show it is the session");
    }

    /// <summary>The instant a start text names: an ISO instant with a zone designator (one reading, whatever the zone), else the process-table shape read in <paramref name="zone"/>.</summary>
    private static bool TryParseStart(string text, TimeZoneInfo zone, out double epochMs)
    {
        if (DateTimeOffset.TryParseExact(
                text,
                ["yyyy-MM-dd'T'HH:mm:ss.fff'Z'", "yyyy-MM-dd'T'HH:mm:ss'Z'"],
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal,
                out var iso))
        {
            epochMs = iso.ToUnixTimeMilliseconds();
            return true;
        }

        return Timestamps.TryParseProcessStart(text, zone, out epochMs);
    }

    private enum StartRelation
    {
        Same,
        Reused,
        Undecided,
    }

    private static StartRelation Start(LivenessEvidence e, string recorded)
    {
        if (e.ObservedStartUtc is null)
        {
            return StartRelation.Undecided;
        }

        if (recorded == e.ObservedStartUtc)
        {
            return StartRelation.Same;
        }

        // A recorded start is the UTC form or an older record's local form, and both have one shape, so it is read both ways against
        // the observed UTC instant: the same process under either reading, a different one only when it differs under both.
        if (!TryParseStart(e.ObservedStartUtc, TimeZoneInfo.Utc, out var observed)
            || !TryParseStart(recorded, TimeZoneInfo.Utc, out var asUtc)
            || !TryParseStart(recorded, e.LocalZone, out var asLocal))
        {
            return StartRelation.Undecided;
        }

        var utcDelta = (observed - asUtc) / 1000;
        var localDelta = (observed - asLocal) / 1000;
        if (Math.Abs(utcDelta) <= StartToleranceSeconds || Math.Abs(localDelta) <= StartToleranceSeconds)
        {
            return StartRelation.Same;
        }

        return utcDelta > 0 && localDelta > 0 ? StartRelation.Reused : StartRelation.Undecided;
    }
}
