namespace Ah.Roster;

/// <summary>
/// What the roster reads from the registrations to prove a team's Orchestrator: the session ids a process provably holds (null when
/// the process table cannot say), and a session's live registration as its pid and start time.
/// </summary>
public sealed record SessionProofs(Func<int, IReadOnlyList<string>?> CallerSessions, Func<string, (long Pid, string Start)?> LiveRegistration)
{
    /// <summary>Every registered session's process and the start it was registered with: the reference an observer whose parent is pid 1 looks for.</summary>
    public Func<IEnumerable<(long Pid, string? Start)>>? RegisteredProcesses { get; init; }
}
