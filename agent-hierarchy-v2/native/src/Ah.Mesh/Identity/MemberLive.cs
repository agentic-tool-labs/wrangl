using System.Globalization;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Time;
using Ah.Hierarchy;

namespace Ah.Mesh;

/// <summary>
/// Whether a member's session is running, decided from its registration and the identity of the process it names: the pid is
/// alive, started when the registration says, and (for a Claude session) runs the harness. A pid that merely exists never counts.
/// </summary>
internal sealed class MemberLive(IMailbox mailbox, IProcessTable table, IProcessInspector processes, Func<DateTimeOffset?>? bootTime = null) : IMemberLiveness
{
    private DateTimeOffset? Boot() => bootTime?.Invoke();

    // ended registrations stay evidence: a member whose dead session was ended is still proven dead by the process it names
    public MemberRegistrationLiveness? ForMember(string memberName, string team) => ForMember(memberName, team, mailbox.EvidenceRegistrations());

    public IReadOnlyList<JsonObject> Registrations() => mailbox.EvidenceRegistrations();

    // the record EvidenceRegistrations would hold for the session: one that names an address
    public JsonObject? RegistrationOf(string sessionId) =>
        mailbox.ReadRegistration(sessionId) is { } r && r.Get("address") is { IsText: true } ? r
        : mailbox.EvidenceRegistrations().FirstOrDefault(e => e.Get("session_id") is { IsText: true } id && id.AsString() == sessionId);

    public Ah.Roster.TeamLife? TeamLifeOf(JsonValue team, IReadOnlyList<JsonObject> registrations)
    {
        var live = CallerIdentity.LiveSessions(mailbox, registrations);
        return Ah.Roster.RosterModule.TeamLifeOf(team, s => MeshModule.LiveRegistration(live, s), table, MeshModule.RegisteredProcesses(registrations));
    }

    public MemberRegistrationLiveness? ForMember(string memberName, string team, IReadOnlyList<JsonObject> registrations)
    {
        ArgumentNullException.ThrowIfNull(memberName);
        ArgumentNullException.ThrowIfNull(team);
        ArgumentNullException.ThrowIfNull(registrations);
        var address = $"{memberName}@{team}";
        JsonObject? newest = null;
        var newestAt = string.Empty;
        foreach (var registration in registrations)
        {
            if (!Names(registration, address))
            {
                continue;
            }

            var at = registration.Get("at") is { IsText: true } text ? text.AsString() : string.Empty;
            if (newest is null || IsLater(at, newestAt))
            {
                newest = registration;
                newestAt = at;
            }
        }

        if (newest is null)
        {
            return null;
        }

        var session = newest.Get("session_id") is { IsText: true } id ? id.AsString() : string.Empty;
        DateTimeOffset? written = DateTimeOffset.TryParse(newestAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;
        var proof = newest.Get("pid_proof") is not { IsNull: false, IsTruthy: false };
        var (live, why, provenDead) = Judge(newest, written, proof);
        var pidGone = !live && !provenDead && !proof && Judge(newest, written, true).ProvenDead;
        var cwd = newest.Get("cwd") is { IsText: true } recorded && recorded.AsString().Length > 0 ? recorded.AsString() : null;
        return new MemberRegistrationLiveness(session, live, why, written, cwd, provenDead, pidGone);
    }

    public bool PidIdentityLive(int pid, string? recordedStart) =>
        !LivenessRules.RecordedBeforeBoot(recordedStart, null, Boot()) && Identity(pid, recordedStart, claudeCommand: true) is null;

    public bool PidReused(int pid, string? recordedStart) => ProcessIdentity.Reused(table, pid, recordedStart);

    /// <summary>
    /// One reading of <paramref name="pid"/> judged with the recorded start and the boot time, as <see cref="PidVerdict"/> judges it, but
    /// with no reference check: that reads every registration, and the doorbell calls this with its lock held (one-liveness.md §3.1).
    /// </summary>
    public bool PidRunning(int pid, string? recordedStart, LivenessPolicy policy, bool claudeHarness = true)
    {
        if (pid <= 0)
        {
            return false;
        }

        var seen = ProcessObservation.None;
        var verdict = LivenessRules.Judge(LivenessAssembler.Assemble(
            DateTimeOffset.UtcNow,
            (pid, string.IsNullOrEmpty(recordedStart) ? null : recordedStart, claudeHarness, false),
            null,
            null,
            observed =>
            {
                var (observation, start, cmdline) = Observe(observed is > 0 and <= int.MaxValue ? (int)observed.Value : null);
                seen = observation;
                return (observation, start, cmdline, DateTimeOffset.UtcNow, false);
            },
            bootTime: Boot())).Verdict;
        return policy == LivenessPolicy.ProvenRunning
            ? verdict == LivenessVerdict.Live
            : seen == ProcessObservation.Present && verdict != LivenessVerdict.ProvenDead;
    }

    public (LivenessVerdict Verdict, string Why) JudgePeerRow(PeerRowKind kind, long? pid, string? start, DateTimeOffset? rowAt, double? seenAgeSeconds, DateTimeOffset? lastLifeAt) =>
        JudgePeerRow(kind, pid, start, rowAt, seenAgeSeconds, lastLifeAt, true);

    public (LivenessVerdict Verdict, string Why) JudgePeerRow(PeerRowKind kind, long? pid, string? start, DateTimeOffset? rowAt, double? seenAgeSeconds, DateTimeOffset? lastLifeAt, bool claudeHarness)
    {
        return LivenessRules.Judge(LivenessAssembler.Assemble(DateTimeOffset.UtcNow, null, (kind, pid, start, rowAt, seenAgeSeconds), lastLifeAt, Observed, claudeHarness, bootTime: Boot()));
    }

    /// <summary>A reading of <paramref name="pid"/> taken now, with whether the observer can see other processes.</summary>
    private (ProcessObservation Observation, string? StartUtc, HarnessCmdline Cmdline, DateTimeOffset? ObservedAt, bool CanSeeReference) Observed(long? pid)
    {
        var (observation, start, cmdline) = Observe(pid is > 0 and <= int.MaxValue ? (int)pid.Value : null);
        return (observation, start, cmdline, DateTimeOffset.UtcNow, ObserverSeesAReference(pid));
    }

    public LivenessVerdict PidVerdict(int pid, string? recordedStart)
    {
        if (pid <= 0)
        {
            return LivenessVerdict.Unknown;
        }

        return LivenessRules.Judge(LivenessAssembler.Assemble(DateTimeOffset.UtcNow, (pid, string.IsNullOrEmpty(recordedStart) ? null : recordedStart, true, false), null, null, Observed, bootTime: Boot())).Verdict;
    }

    /// <summary>
    /// A reading of the process <paramref name="pid"/> taken now: not observed for no pid. Ownership is asked first, so a pid that exits
    /// between the two reads has no start and reads Absent, not another user's.
    /// </summary>
    private (ProcessObservation Observation, string? Start, HarnessCmdline Cmdline) Observe(int? pid)
    {
        if (pid is not { } n)
        {
            return (ProcessObservation.None, null, HarnessCmdline.Unreadable);
        }

        var owned = processes.PidOwned(n);
        var start = table.StartTime(n);
        var observation = owned ? ProcessObservation.Present : start is null ? ProcessObservation.Absent : ProcessObservation.NotOwned;
        var cmdline = HarnessCmdline.Unreadable;
        if (observation == ProcessObservation.Present && processes.CommandLine(n) is { } command)
        {
            cmdline = MeshModule.IsHarnessCommand(command) ? HarnessCmdline.Harness : HarnessCmdline.NotHarness;
        }

        return (observation, start, cmdline);
    }

    /// <summary>Whether this observer can see another process: its parent, or any registered session whose process shows the start it was registered with.</summary>
    private bool ObserverSeesAReference(long? subjectPid) =>
        _reference.Sees(
            table,
            Environment.ProcessId,
            [.. mailbox.AllRegistrations().Select(static r => (
                r.Get("pid") is { IsNumber: true } p ? (long)p.AsNumber() : 0L,
                r.Get("pid_start") is { IsText: true } s ? s.AsString() : null))],
            subjectPid);

    private readonly ObserverReference _reference = new();

    /// <summary>The shared verdict over the registration and a reading of its process taken now; only a proven death is reported as one.</summary>
    private (bool Live, string Why, bool ProvenDead) Judge(JsonObject registration, DateTimeOffset? written, bool pidProof)
    {
        int? number = registration.Get("pid") is { IsNumber: true } pid && IsPid(pid.AsNumber()) ? (int)pid.AsNumber() : null;
        var recordedStart = registration.Get("pid_start") is { IsText: true } start && start.AsString().Length > 0 ? start.AsString() : null;
        var harness = registration.Get("harness") is { IsText: true } named && named.AsString().Length > 0 ? named.AsString() : "claude";
        var (verdict, why) = LivenessRules.Judge(LivenessAssembler.Assemble(
            DateTimeOffset.UtcNow,
            (number, recordedStart, harness == "claude", number is null && mailbox.RegistrationLive(registration)),
            null,
            written,
            Observed,
            registrationPidProof: pidProof,
            bootTime: Boot(),
            registrationAt: written));
        return (verdict == LivenessVerdict.Live, why, verdict == LivenessVerdict.ProvenDead);
    }

    /// <summary>What is wrong with a process that is not the registered session, and whether that shows the session is gone rather than merely unconfirmed.</summary>
    private sealed record IdentityProblem(string Text, bool Proven);

    /// <summary>
    /// Null when the process is the registered session; else what is wrong with it. Every session ah tracks runs as this user, so a
    /// pid that exists but cannot be signalled is another user's process. A recorded start that matches proves the session; without
    /// one, a Claude session must show a readable harness command line, because a pid alone is also true of a recycled one.
    /// </summary>
    private IdentityProblem? Identity(int pid, string? recordedStart, bool claudeCommand)
    {
        if (!processes.PidOwned(pid))
        {
            return new IdentityProblem("is not running as this user", true);
        }

        if (ProcessIdentity.Reused(table, pid, recordedStart))
        {
            return new IdentityProblem("now belongs to another process (its start time differs)", true);
        }

        if (!claudeCommand)
        {
            return null;
        }

        if (recordedStart is not null && ProcessIdentity.Same(table, pid, recordedStart))
        {
            return null;
        }

        if (processes.CommandLine(pid) is not { } command)
        {
            return new IdentityProblem("has no readable command line and no matching recorded start to show it is the session", false);
        }

        // A command line unlike the harness never proves the session gone: a native install's real binary, a re-exec, a symlink-resolving launcher or `exec -a` all make a live session look so.
        return MeshModule.IsHarnessCommand(command) ? null : new IdentityProblem("is not a harness session", false);
    }

    /// <summary>Whether instant <paramref name="at"/> is after <paramref name="than"/>; text that is not an ISO instant falls back to ordinal order.</summary>
    internal static bool IsLater(string at, string than) =>
        DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var a)
        && DateTimeOffset.TryParse(than, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var b)
            ? a > b
            : string.CompareOrdinal(at, than) > 0;

    private static bool IsPid(double number) => number == Math.Floor(number) && number > 0 && number <= int.MaxValue;

    /// <summary>Whether one of the registration's addresses is exactly <paramref name="address"/> (<c>name@team</c>).</summary>
    private static bool Names(JsonObject registration, string address)
    {
        if (registration.Get("addresses") is { IsArray: true } addresses)
        {
            // an index loop, not Any: this runs for every registration on every member judgement, and a closure and a boxed enumerator each time add up
            var list = addresses.AsArray();
            for (var i = 0; i < list.Count; i++)
            {
                if (list[i] is { IsText: true } text && text.AsString() == address)
                {
                    return true;
                }
            }

            return false;
        }

        return registration.Get("address") is { IsText: true } single && single.AsString() == address;
    }
}
