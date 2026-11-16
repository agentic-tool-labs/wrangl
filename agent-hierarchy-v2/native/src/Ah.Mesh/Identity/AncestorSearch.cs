using Ah.Abstractions;
using Ah.Core.Time;

namespace Ah.Mesh;

/// <summary>A team's Orchestrator as its team file records it: the process and the start time it had then.</summary>
public readonly record struct TeamOrchestrator(string Team, int Pid, string? PidStart, string? Deputy = null);

/// <summary>A live session registration: the process it names, that process's recorded start time, and the box address (<c>name@team</c>).</summary>
public readonly record struct RegisteredSession(int Pid, string? PidStart, string Address);

public enum NearestKind
{
    /// <summary>No ancestor is a registered session.</summary>
    None,

    /// <summary>The process table could not say, so nothing can be proved.</summary>
    Unavailable,

    Orchestrator,

    Member,

    /// <summary>A Claude harness session that is neither a team's recorded Orchestrator nor a registration; only <c>resume</c> accepts it.</summary>
    Session,
}

/// <summary>The nearest ancestor that is a registered session (or a team's recorded Orchestrator), and how it matched.</summary>
public readonly record struct NearestSession(NearestKind Kind, string? Team = null, string? Member = null, int Pid = 0, bool LegacyStart = false);

internal static class AncestorSearch
{
    /// <summary>The address a team's Orchestrator registers under.</summary>
    public const string OrchestratorName = "orchestrator";

    /// <summary>The ancestors one search looks at, as <see cref="IProcessTable.Ancestors"/> bounds a walk.</summary>
    private const int MaxHops = 64;

    /// <summary>
    /// Walks the ancestors of <paramref name="ownPid"/>, nearest first, and returns the first one that is a registered session or
    /// a team's recorded Orchestrator with the pid <em>and</em> the start time it was recorded with. A process that has only the
    /// pid of a registered session (a reused pid) is not that session and the walk goes on. A member found before an
    /// Orchestrator decides: the answer is that member. When <paramref name="isSession"/> is given, a pid it accepts that is
    /// neither a recorded Orchestrator nor a registration is a <see cref="NearestKind.Session"/>; it is asked last, for the
    /// nearest candidates only.
    /// </summary>
    public static NearestSession Nearest(
        IProcessTable table,
        int ownPid,
        IReadOnlyList<TeamOrchestrator> teams,
        IReadOnlyList<RegisteredSession> registered,
        Func<int, bool>? isSession = null)
    {
        try
        {
            bool Named(int pid) => teams.Any(t => t.Pid == pid) || registered.Any(r => r.Pid == pid) || isSession?.Invoke(pid) == true;

            var from = ownPid;
            var hops = 0;
            while (hops < MaxHops)
            {
                if (table.AncestorsUntil(from, Named) is not { } chain)
                {
                    return new NearestSession(NearestKind.Unavailable);
                }

                hops += chain.Count;
                if (chain.Count == 0 || !Named(chain[^1]))
                {
                    return new NearestSession(NearestKind.None);
                }

                var pid = chain[^1];
                if (table.StartTime(pid) is null)
                {
                    return new NearestSession(NearestKind.Unavailable);
                }

                foreach (var t in teams.Where(t => t.Pid == pid))
                {
                    var match = ProcessStart.Compare(table, pid, t.PidStart);
                    if (ProcessStart.IsSame(match))
                    {
                        return new NearestSession(NearestKind.Orchestrator, t.Team, OrchestratorName, pid, match == StartMatch.Legacy);
                    }
                }

                var held = registered.Where(r => r.Pid == pid && ProcessStart.IsSame(ProcessStart.Compare(table, pid, r.PidStart))).ToList();
                foreach (var r in held.OrderBy(r => DeputyMemberBox(r.Address, held, teams) ? 0 : 1))
                {
                    var match = ProcessStart.Compare(table, pid, r.PidStart);
                    var at = r.Address.LastIndexOf('@');
                    var name = at > 0 ? r.Address[..at] : r.Address;
                    var team = at > 0 ? r.Address[(at + 1)..] : null;
                    return name == OrchestratorName
                        ? new NearestSession(NearestKind.Orchestrator, team, name, pid, match == StartMatch.Legacy)
                        : new NearestSession(NearestKind.Member, team, name, pid, match == StartMatch.Legacy);
                }

                if (isSession?.Invoke(pid) == true)
                {
                    return new NearestSession(NearestKind.Session, null, null, pid);
                }

                // the pid is a registered one but this process is not that session (a reused pid): keep walking
                from = pid;
            }

            return new NearestSession(NearestKind.None);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return new NearestSession(NearestKind.Unavailable);
        }
    }

    /// <summary>
    /// A member box of a process that also holds its own team's <c>orchestrator@</c> box: a deputy on duty, which is that member
    /// whichever box its registration lists first, but only while the team's record names that member as its <c>duty.deputy</c>.
    /// A lane lead's member box is in its manager's team, not the child team it owns.
    /// </summary>
    private static bool DeputyMemberBox(string address, List<RegisteredSession> held, IReadOnlyList<TeamOrchestrator> teams)
    {
        var at = address.LastIndexOf('@');
        if (at <= 0 || address[..at] == OrchestratorName)
        {
            return false;
        }

        var (member, team) = (address[..at], address[(at + 1)..]);
        return held.Any(h => h.Address == $"{OrchestratorName}@{team}") && teams.Any(t => t.Team == team && t.Deputy == member);
    }
}
