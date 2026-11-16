using Ah.Hierarchy;

namespace Ah.Cli.Composition;

/// <summary>What the decision verbs record of who ran them: the caller's class, and the processes and session that class rests on.</summary>
internal sealed record DecisionCaller(CutoverCaller Class, IReadOnlyList<int> OrchestratorPids, string? SessionId, int Pid, string? PidStart, string? Tty, string? TeamHierDir = null);

/// <summary>Which decision verb is being judged.</summary>
internal enum DecisionAction
{
    Answer,
    Add,
}

/// <summary>The outcome of judging a caller: no <see cref="Code"/> means allowed; <see cref="Caller"/> and <see cref="RecordedBy"/> are what an allowed line records.</summary>
internal sealed record DecisionVerdict(string? Code, string Caller, string RecordedBy);

/// <summary>
/// Who may write the decision log. The class comes from <see cref="CutoverCallers.Classify"/>, the one definition of the user and the
/// top-level Orchestrator, and a refusal code is that class's name; the only addition is the explicit compare with the run
/// anchor's team, because Classify names the first top-level verdict among every team the caller holds.
/// </summary>
internal static class DecisionProof
{
    public const string NotTheRunOrchestrator = "not-the-run-orchestrator";

    public const string Unproven = "unproven";

    public const string DangerousNeedsUser = "dangerous-needs-user";

    public const string AuditUnwritable = "audit-unwritable";

    public const string HomeMismatch = "home-mismatch";

    public const string HomeUnresolved = "home-unresolved";

    /// <summary>The deputy on duty answers a decision only as the user's surrogate (D1-D5 under a surrogate anchor); it never relays the user's own answer.</summary>
    public const string DeputyNeedsSurrogate = "deputy-needs-surrogate";

    public static string AuditPath(string ahHome) => System.IO.Path.Join(ahHome, "state", "decision-audit.jsonl");

    /// <summary>
    /// Judges <paramref name="caller"/> against the run anchor's team. <paramref name="memberOfAnchorTeam"/> is whether the caller's
    /// process is a member row of that team's record.
    /// </summary>
    public static DecisionVerdict Judge(CutoverCaller caller, string? anchorTeam, bool memberOfAnchorTeam, bool teamDirIsThisDir = true)
    {
        ArgumentNullException.ThrowIfNull(caller);
        switch (caller.Kind)
        {
            case CutoverCallerKind.User:
                return new DecisionVerdict(null, "user", "user");
            case CutoverCallerKind.TopOrchestrator:
                var team = TeamOf(caller.Detail);
                return anchorTeam is null || team != anchorTeam || memberOfAnchorTeam || !teamDirIsThisDir
                    ? new DecisionVerdict(NotTheRunOrchestrator, "top-orchestrator", string.Empty)
                    : new DecisionVerdict(null, "top-orchestrator", HierarchyModule.OrchestratorAddress(team));
            case CutoverCallerKind.DeputyChief:
                var dutyTeam = TeamOf(caller.Detail);
                return anchorTeam is null || dutyTeam != anchorTeam || !teamDirIsThisDir
                    ? new DecisionVerdict(NotTheRunOrchestrator, "deputy-chief", string.Empty)
                    : new DecisionVerdict(null, "deputy-chief", HierarchyModule.OrchestratorAddress(dutyTeam));
            case CutoverCallerKind.SubOrchestrator:
                return new DecisionVerdict("sub-orchestrator", caller.Name, string.Empty);
            case CutoverCallerKind.Member:
                return new DecisionVerdict("member", caller.Name, string.Empty);
            default:
                return new DecisionVerdict(Unproven, caller.Name, string.Empty);
        }
    }

    /// <summary>The team of a top-level verdict's detail, which is its <c>orchestrator@team</c> address; null for any other shape.</summary>
    internal static string? TeamOf(string detail) =>
        detail.StartsWith("orchestrator@", StringComparison.Ordinal) && detail.Length > "orchestrator@".Length ? detail["orchestrator@".Length..] : null;

    /// <summary>Whether <paramref name="surrogateClass"/> names one of the delegable classes D1 to D5.</summary>
    internal static bool IsDelegableClass(string surrogateClass) =>
        surrogateClass.Length == 2 && surrogateClass[0] == 'D' && surrogateClass[1] is >= '1' and <= '5';
}
