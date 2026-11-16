using Ah.Core.Time;

namespace Ah.TestKit;

/// <summary>
/// The shared liveness verdict over a peers row for a test host that can only say whether the process is running: a running process
/// is the session (its start is taken to match), a gone one is absent and seen as absent by an observer that can see other processes.
/// </summary>
public static class PeerRowJudge
{
    private static readonly AsyncLocal<bool> Blind = new();

    /// <summary>While set, the host cannot read any process: the verdict has no observation, as for a table that lists nothing.</summary>
    public static bool CannotObserve
    {
        get => Blind.Value;
        set => Blind.Value = value;
    }

    public static (LivenessVerdict Verdict, string Why) Judge(PeerRowKind kind, long? pid, string? start, DateTimeOffset? rowAt, double? seenAgeSeconds, DateTimeOffset? lastLifeAt, bool processRunning)
    {
        var now = DateTimeOffset.UtcNow;
        if (CannotObserve)
        {
            return LivenessRules.Judge(new LivenessEvidence
            {
                Now = now,
                MaxObservationAge = TimeSpan.FromSeconds(LivenessRules.ObservationAgeSeconds),
                PeerRow = kind,
                PeerPid = pid,
                PeerStart = start,
                PeerAt = rowAt,
                SeenAgeSeconds = seenAgeSeconds,
                Observation = ProcessObservation.None,
                Cmdline = HarnessCmdline.Unreadable,
                LastLifeAt = lastLifeAt,
            });
        }

        return LivenessRules.Judge(new LivenessEvidence
        {
            Now = now,
            MaxObservationAge = TimeSpan.FromSeconds(LivenessRules.ObservationAgeSeconds),
            PeerRow = kind,
            PeerPid = pid,
            PeerStart = start,
            PeerAt = rowAt,
            SeenAgeSeconds = seenAgeSeconds,
            Observation = processRunning ? ProcessObservation.Present : ProcessObservation.Absent,
            ObservedStartUtc = processRunning ? start : null,
            Cmdline = HarnessCmdline.Harness,
            ObservedAt = now,
            ObserverCanSeeReference = true,
            LastLifeAt = lastLifeAt,
        });
    }
}
