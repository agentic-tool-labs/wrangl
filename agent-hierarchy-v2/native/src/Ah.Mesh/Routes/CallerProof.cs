using Ah.Abstractions;

namespace Ah.Mesh;

/// <summary>Who a team-scoped call is from, as its peer's process tree proves it: <c>By</c> is <c>user</c> or <c>orchestrator</c>, or null with the refusal's code and message.</summary>
public sealed record CallerVerdict(string? By, string? Code, string? Message);

/// <summary>The proof a route needing the team's Orchestrator (or the user) applies, the same one the mail routes use.</summary>
internal static class CallerProof
{
    public const string NotTeamOwner = "not-team-owner";

    /// <summary>
    /// The user, or the session that holds <c>orchestrator@team</c>. Any other caller is refused <see cref="NotTeamOwner"/>, and a
    /// caller the mailbox cannot identify is refused with the identity predicate's own code.
    /// </summary>
    public static CallerVerdict OrchestratorOrUser(RouteRequest request, IMailbox mailbox, string team, string? sessionHint)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(mailbox);
        return OrchestratorOrUser(mailbox.PrincipalOf(request.PeerPid), sessionHint, team);
    }

    /// <summary>The same decision for a principal the caller has already resolved.</summary>
    public static CallerVerdict OrchestratorOrUser(CallerPrincipal principal, string? sessionHint, string team)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var choice = CallerIdentity.Choose(principal, sessionHint, write: true);
        if (choice.Refusal is { } code)
        {
            return new CallerVerdict(null, code, "the daemon cannot tell which session or user is calling");
        }

        if (principal.Kind == PrincipalKind.User)
        {
            return new CallerVerdict("user", null, null);
        }

        return principal.Kind == PrincipalKind.Session && choice.Addresses.Contains($"{AncestorSearch.OrchestratorName}@{team}", StringComparer.Ordinal)
            ? new CallerVerdict("orchestrator", null, null)
            : new CallerVerdict(null, NotTeamOwner, $"only the user or {AncestorSearch.OrchestratorName}@{team} may do this");
    }
}
