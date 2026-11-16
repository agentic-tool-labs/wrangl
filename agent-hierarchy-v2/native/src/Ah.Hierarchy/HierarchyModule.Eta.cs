using Ah.Core.Json;

namespace Ah.Hierarchy;

public static partial class HierarchyModule
{
    /// <summary>The role the top-level Orchestrator files its dispatches under.</summary>
    public const string OrchestratorRole = OrchestratingSessions.TopRole;

    /// <summary>The reason of a request a hook filed for a bare chat message to a member: a note, which has no deadline and raises no check-in.</summary>
    public const string AutoTrackedNoteReason = "auto-tracked-note";

    /// <summary>
    /// The reason of a request the SendMessage hook filed for a plain Orchestrator send to a member with no open request (board-derive § C3): its clock is
    /// unarmed until the member has worked on it for a minute; unarmed, it stays open and silent and the watch never closes it.
    /// </summary>
    public const string PlainSendReason = "plain-send";

    /// <summary>The peer row ahd writes once a plain-send request's member has worked on it: <c>{type, session_id, request_id, at}</c>, <c>at</c> when the work began.</summary>
    public const string PlainSendArmedRow = "plain-send-armed";

    /// <summary>The state of one dispatch at <paramref name="nowMs"/>: the one function every overdue reading comes from.</summary>
    public static EtaStateResult ComputeEtaState(EtaFacts facts, double nowMs, double pollMs) => EtaClock.Compute(facts, nowMs, pollMs);

    /// <summary>Whether an open task is stuck at <paramref name="nowMs"/>; see <see cref="StuckClock"/>. The stall limit defaults to <see cref="QueueStallMs"/>.</summary>
    public static StuckResult ComputeStuck(bool assigneeLive, string? activity, double createdMs, double lastActivityMs, double nowMs, double stallMs = QueueStallMs) =>
        StuckClock.Compute(assigneeLive, activity, createdMs, lastActivityMs, nowMs, stallMs);

    /// <summary>Distinct episodes among ascending timestamps (ms): rows closer than <paramref name="gapMs"/> to the previous one belong to the same episode.</summary>
    public static List<double> EtaEpisodes(IReadOnlyList<double> sortedTimes, double gapMs) => EtaClock.Episodes(sortedTimes, gapMs);

    /// <summary>When the silence warning is due after a first nudge at <paramref name="checkinMs"/>, given a busy look at <paramref name="lookMs"/>.</summary>
    public static double EtaSilentDueAt(double checkinMs, double lookMs, double baseMs, double thresholdMs, double gapFactor) =>
        EtaClock.SilentDueAt(checkinMs, lookMs, baseMs, thresholdMs, gapFactor);

    /// <summary>The clock base: the later of the send and the latest word from the addressee.</summary>
    public static double EtaBase(double startMs, double heardMs) => EtaClock.BaseOf(startMs, heardMs);

    /// <summary>The one reading of "is this member running": the registration decides first, then the pane probe.</summary>
    public static MemberVerdict JudgeMember(MemberRegistrationLiveness? registration, string? paneId, Func<PaneProbe>? probe, int? startingAgeSeconds = null) =>
        MemberVerdicts.Judge(registration, paneId, probe, startingAgeSeconds);

    /// <summary>The orchestrating session of <paramref name="team"/> as its team record names it, or null when it names none.</summary>
    public static OrchestratingSession? OrchestratingSessionOf(JsonValue? teamRecord, string team) => OrchestratingSessions.Of(teamRecord, team);
}
