namespace Ah.Core.Time;

/// <summary>
/// The one place that turns what is known about a member into <see cref="LivenessEvidence"/> for <see cref="LivenessRules.Judge"/>:
/// its registration, its newest peers row, the latest sign of life, and a reading of the process the evidence names. Every surface that
/// judges a member supplies those parts and a way to look at one pid; none maps them to evidence itself, so two surfaces given the
/// same parts reach the same verdict.
/// </summary>
public static class LivenessAssembler
{
    /// <summary>
    /// The evidence for one member. <paramref name="observe"/> is called once with the pid <see cref="LivenessRules.PidToObserve"/> says
    /// decides this evidence (null when none does) and answers what the observer saw of it, when, and whether the observer can see other
    /// processes at all. <paramref name="peerRowClaudeHarness"/> is whether the member of a peers row (a member with no registration) runs
    /// a claude harness, which the command-line rows of the verdict depend on; a registration carries its own.
    /// <paramref name="registrationPidProof"/> is false for a registration whose pid is not shown to be the harness: its pid can raise the
    /// verdict, never prove the member gone.
    /// </summary>
    public static LivenessEvidence Assemble(
        DateTimeOffset now,
        (long? Pid, string? Start, bool ClaudeHarness, bool HeartbeatFresh)? registration,
        (PeerRowKind Kind, long? Pid, string? Start, DateTimeOffset? At, double? SeenAgeSeconds)? peerRow,
        DateTimeOffset? lastLifeAt,
        Func<long?, (ProcessObservation Observation, string? StartUtc, HarnessCmdline Cmdline, DateTimeOffset? ObservedAt, bool CanSeeReference)> observe,
        bool peerRowClaudeHarness = true,
        bool registrationPidProof = true,
        DateTimeOffset? bootTime = null,
        DateTimeOffset? registrationAt = null)
    {
        ArgumentNullException.ThrowIfNull(observe);
        var target = LivenessRules.PidToObserve(Build(now, registration, peerRow, lastLifeAt, default, peerRowClaudeHarness, registrationPidProof, bootTime, registrationAt));
        return Build(now, registration, peerRow, lastLifeAt, observe(target?.Pid), peerRowClaudeHarness, registrationPidProof, bootTime, registrationAt);
    }

    /// <summary>
    /// The kind of peers row a status text names: up, down or seen; none for any other text, a briefed row included. A briefed row is
    /// written by the sender of a message, not by the member's process, so it never decides liveness.
    /// </summary>
    public static PeerRowKind PeerKindOf(string? status) => status switch
    {
        "up" => PeerRowKind.Up,
        "down" => PeerRowKind.Down,
        "seen" => PeerRowKind.Seen,
        _ => PeerRowKind.None,
    };

    private static LivenessEvidence Build(
        DateTimeOffset now,
        (long? Pid, string? Start, bool ClaudeHarness, bool HeartbeatFresh)? registration,
        (PeerRowKind Kind, long? Pid, string? Start, DateTimeOffset? At, double? SeenAgeSeconds)? peerRow,
        DateTimeOffset? lastLifeAt,
        (ProcessObservation Observation, string? StartUtc, HarnessCmdline Cmdline, DateTimeOffset? ObservedAt, bool CanSeeReference) seen,
        bool peerRowClaudeHarness,
        bool registrationPidProof,
        DateTimeOffset? bootTime,
        DateTimeOffset? registrationAt) =>
        new()
        {
            BootTime = bootTime,
            RegistrationAt = registrationAt,
            RegistrationPidProof = registrationPidProof,
            Now = now,
            MaxObservationAge = TimeSpan.FromSeconds(LivenessRules.ObservationAgeSeconds),
            HasRegistration = registration is not null,
            RegistrationPid = registration?.Pid,
            RegistrationStart = registration?.Start,
            ClaudeHarness = registration?.ClaudeHarness ?? peerRowClaudeHarness,
            HeartbeatFresh = registration?.HeartbeatFresh ?? false,
            PeerRow = peerRow?.Kind ?? PeerRowKind.None,
            PeerPid = peerRow?.Pid,
            PeerStart = peerRow?.Start,
            PeerAt = peerRow?.At,
            SeenAgeSeconds = peerRow?.SeenAgeSeconds,
            Observation = seen.Observation,
            ObservedStartUtc = seen.StartUtc,
            Cmdline = seen.Cmdline,
            ObservedAt = seen.ObservedAt,
            ObserverCanSeeReference = seen.CanSeeReference,
            LastLifeAt = lastLifeAt,
        };
}
