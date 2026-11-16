using Ah.Abstractions;
using Ah.Core.Time;

namespace Ah.Mesh;

/// <summary>One process of an <c>ah</c> call's ancestry, for the audit row of an owner verb.</summary>
public readonly record struct ChainHop(int Pid, string? Start, string? Command);

/// <param name="Allowed">Whether the nearest registered session is the team's Orchestrator, with no member marker.</param>
/// <param name="Text">The refusal to print; null when allowed.</param>
/// <param name="Reason">orchestrator, member-marker, member-ancestor, no-orchestrator, other-team, stale-registration or unproven.</param>
/// <param name="Stale">The team's recorded Orchestrator is not alive or no longer has the recorded start time.</param>
/// <param name="CurrentStart">The Orchestrator's UTC start time to store when the recorded one was in the legacy local form; else null.</param>
public sealed record OwnerProofResult(bool Allowed, string? Text, string Reason, bool Stale, string? CurrentStart)
{
    /// <summary>One plain line saying which step of the proof failed, printed after <see cref="Text"/>; null when allowed.</summary>
    public string? Why { get; init; }
}

public sealed record OwnerProofInputs(
    IProcessTable Table,
    string? MemberMarker,
    int OwnPid,
    TeamOrchestrator Team,
    IReadOnlyList<TeamOrchestrator> Teams,
    IReadOnlyList<RegisteredSession> Registered,
    Func<int, bool> Alive,
    Func<int, bool>? IsSession = null);

/// <summary>
/// The proof that an owner-only verb really runs through the team's Orchestrator. Identity claims that a caller can set for
/// itself (<c>CLAUDE_PID</c>, <c>--orchestrator-pid</c>, <c>--session</c>) are not inputs. Anything that cannot be established
/// is a refusal.
/// </summary>
internal static class OwnerProof
{
    public const string Refusal = "ah: this verb runs only through the orchestrator's session: ask the orchestrator to run it.";

    public const string StaleSuffix =
        "The team's orchestrator registration is stale: re-adopt it with ah roster adopt from the orchestrator session, then retry.";

    public const string Unproven =
        "ah: could not prove ownership: the process table could not be read. This verb runs only through the orchestrator's session: ask the orchestrator to run it.";

    public static OwnerProofResult Check(OwnerProofInputs input)
    {
        try
        {
            var stale = IsStale(input);
            if (!string.IsNullOrEmpty(input.MemberMarker))
            {
                return Refused("member-marker", stale, "owner proof: this shell carries a member marker.");
            }

            var nearest = AncestorSearch.Nearest(input.Table, input.OwnPid, input.Teams, input.Registered, input.IsSession);
            switch (nearest.Kind)
            {
                case NearestKind.Unavailable:
                    return new OwnerProofResult(false, Unproven, "unproven", stale, null) { Why = UnprovenWhy };
                case NearestKind.Member:
                    return Refused("member-ancestor", stale, $"owner proof: this command runs under member {nearest.Member}.");
                case NearestKind.None:
                case NearestKind.Session:
                    return Refused("no-orchestrator", stale, "owner proof: no registered session is an ancestor of this command.");
            }

            // A session that owns several teams is registered under one address; the team asked about is also its own when its
            // record names this very process, start time included.
            var ownsAskedTeam = nearest.Pid == input.Team.Pid
                && ProcessStart.Compare(input.Table, input.Team.Pid, input.Team.PidStart) == StartMatch.Same;
            if (nearest.Team != input.Team.Team && !ownsAskedTeam)
            {
                return Refused(
                    "other-team",
                    stale,
                    $"owner proof: this session is the orchestrator of {nearest.Team ?? "another team"}, not {input.Team.Team}. "
                    + $"Run ah roster resume --team {input.Team.Team} --yes in this session, or run the verb from that team's orchestrator.");
            }

            if (nearest.Pid != input.Team.Pid)
            {
                return Refused(
                    "stale-registration",
                    true,
                    $"owner proof: {input.Team.Team}'s orchestrator record names pid {input.Team.Pid}, which is not this session.");
            }

            return new OwnerProofResult(true, null, "orchestrator", stale, nearest.LegacyStart ? input.Table.StartTime(nearest.Pid) : null);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return new OwnerProofResult(false, Unproven, "unproven", false, null) { Why = UnprovenWhy };
        }
    }

    private const string UnprovenWhy = "owner proof: the process table could not be read.";

    /// <summary>The ancestors of <paramref name="ownPid"/> with each one's UTC start time and command line, for the audit row.</summary>
    public static IReadOnlyList<ChainHop> Chain(IProcessTable table, int ownPid, Func<int, string?> commandOf)
    {
        try
        {
            return table.Ancestors(ownPid) is { } chain
                ? [.. chain.Select(pid => new ChainHop(pid, table.StartTime(pid), commandOf(pid)))]
                : [];
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return [];
        }
    }

    private static OwnerProofResult Refused(string reason, bool stale, string why) =>
        new(false, stale ? Refusal + " " + StaleSuffix : Refusal, reason, stale, null) { Why = why };

    private static bool IsStale(OwnerProofInputs input)
    {
        var team = input.Team;
        return team.Pid <= 1
            || !input.Alive(team.Pid)
            || ProcessIdentity.Reused(input.Table, team.Pid, team.PidStart);
    }
}
