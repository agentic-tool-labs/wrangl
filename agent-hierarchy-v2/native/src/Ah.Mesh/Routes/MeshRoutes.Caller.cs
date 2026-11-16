using Ah.Abstractions;

namespace Ah.Mesh;

/// <summary>Who is calling a mail route, as the peer's process tree proves it, and which boxes it may act for.</summary>
internal static partial class MeshRoutes
{
    /// <summary>The 403 for a caller the predicate refused, with the predicate's code.</summary>
    private static RouteResponse CallerRefusal(string code) => Error(403, code, code switch
    {
        CallerIdentity.AnonymousCode => "the daemon cannot tell which session or user is calling, so it accepts no writes from it",
        CallerIdentity.AmbiguousSession => "this process holds several sessions; name one with session_id",
        CallerIdentity.UserOnly => "only the user may do this",
        _ => "that session or address is not one this process belongs to",
    });

    /// <summary>
    /// The session a mail call acts as: the peer's principal and the session its hint (<paramref name="hint"/>) chooses, or the
    /// refusal to answer with.
    /// </summary>
    private static (CallerPrincipal Principal, SessionChoice Choice, RouteResponse? Refused) CallerOf(
        RouteRequest request, IMailbox mailbox, string? hint, bool write)
    {
        var principal = mailbox.PrincipalOf(request.PeerPid);
        var choice = CallerIdentity.Choose(principal, hint, write);
        return (principal, choice, choice.Refusal is { } code ? CallerRefusal(code) : null);
    }

    /// <summary>
    /// Whether the caller may read or act on the box of <paramref name="address"/>: it is the user, the address is one its session
    /// holds, or it holds <c>orchestrator@T</c> (proven) for the address's team T.
    /// </summary>
    internal static bool MayActFor(CallerPrincipal principal, SessionChoice choice, string address, IMailbox mailbox)
    {
        if (principal.Kind == PrincipalKind.User)
        {
            return true;
        }

        if (principal.Kind != PrincipalKind.Session)
        {
            return false;
        }

        return choice.Addresses.Contains(address, StringComparer.Ordinal)
            || (mailbox.SplitAddress(address) is { } parts
                && choice.Addresses.Contains($"{AncestorSearch.OrchestratorName}@{parts.Team}", StringComparer.Ordinal));
    }

    /// <summary>Whether the call's chosen session itself holds <paramref name="address"/>: only then may it write that box's heartbeat.</summary>
    private static bool OwnBox(SessionChoice choice, string address) =>
        choice.Session is { } session && session.Addresses.Contains(address, StringComparer.Ordinal);
}
