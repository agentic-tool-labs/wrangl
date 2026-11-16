using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Time;

namespace Ah.Mesh;

/// <summary>A live registration as the predicate reads it: the session, its process and that process's recorded start, and its addresses.</summary>
public sealed record SessionRegistration(string SessionId, int Pid, string? PidStart, IReadOnlyList<string> Addresses);

/// <summary>
/// The caller's principal: its kind and, for a session, every session its nearest registered ancestor holds. The admin (ah2-50) is
/// the user with its own harness process's sessions; any other user holds none.
/// </summary>
public sealed record CallerPrincipal(PrincipalKind Kind, IReadOnlyList<SessionRegistration> Sessions)
{
    public static CallerPrincipal Anonymous { get; } = new(PrincipalKind.Anonymous, []);

    public static CallerPrincipal User { get; } = new(PrincipalKind.User, []);
}

/// <summary>
/// The session a call acts as, chosen from the principal's proven set by the caller's hint, or why none could be chosen
/// (<see cref="Refusal"/>: <c>not_your_session</c>, <c>ambiguous_session</c> or <c>anonymous</c>). <see cref="Addresses"/> are the
/// addresses the call may act for: the chosen session's, or for a read with no hint, every proven session's.
/// </summary>
public sealed record SessionChoice(SessionRegistration? Session, string? Refusal, IReadOnlyList<string> Addresses);

/// <summary>
/// The one caller-identity predicate. A claimed session (a body <c>session_id</c>, <c>--session</c>, an environment variable) is a
/// hint: it selects among the sessions the process tree proves and never adds one.
/// </summary>
internal static class CallerIdentity
{
    public const string NotYourSession = "not_your_session";

    public const string AmbiguousSession = "ambiguous_session";

    public const string AnonymousCode = "anonymous";

    public const string UserOnly = "user_only";

    /// <summary>
    /// The sessions <paramref name="callerPid"/> provably belongs to: walking from the process itself up through its ancestors,
    /// the registrations at the nearest one whose recorded pid is that process and whose recorded start time is its start time
    /// now. Empty when no process on the way has one; null when the process table cannot say. A pid not above 1 is never matched,
    /// and a registration with no recorded start proves nothing.
    /// </summary>
    public static IReadOnlyList<SessionRegistration>? CallerSessions(IProcessTable table, int callerPid, IReadOnlyList<SessionRegistration> live)
    {
        if (callerPid <= 1)
        {
            return [];
        }

        if (table.Ancestors(callerPid) is not { } ancestors)
        {
            return null;
        }

        foreach (var pid in ancestors.Prepend(callerPid))
        {
            if (pid <= 1)
            {
                continue;
            }

            var held = live.Where(r => r.Pid == pid && ProcessIdentity.Same(table, pid, r.PidStart)).ToList();
            if (held.Count > 0)
            {
                return held;
            }
        }

        return [];
    }

    /// <summary>
    /// The principal of <paramref name="callerPid"/> (null when the caller's process cannot be known, which is Anonymous). A
    /// session keeps an <c>orchestrator@T</c> address only while <paramref name="ownsTeam"/> proves its process is T's recorded
    /// Orchestrator.
    /// </summary>
    public static CallerPrincipal Resolve(
        IProcessTable table, int? callerPid, IReadOnlyList<SessionRegistration> live, Func<SessionRegistration, string, bool> ownsTeam)
    {
        try
        {
            if (callerPid is not { } pid || pid <= 1 || CallerSessions(table, pid, live) is not { } sessions)
            {
                return CallerPrincipal.Anonymous;
            }

            if (sessions.Count > 0)
            {
                return new CallerPrincipal(PrincipalKind.Session, [.. sessions.Select(s => s with { Addresses = Owned(s, ownsTeam) })]);
            }

            return IsUser(table, pid, live) ? CallerPrincipal.User : CallerPrincipal.Anonymous;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return CallerPrincipal.Anonymous;
        }
    }

    /// <summary>
    /// Chooses the session a call acts as. A hint must name a proven session. With no hint, one proven session is used; several
    /// refuse a <paramref name="write"/> as ambiguous and let a read act for all of them. A principal that is not a session has
    /// no session: Anonymous is refused, the user acts as no session. The admin (ah2-50) is the user holding its own sessions: a
    /// hint must name one of those, and with none it takes the first (the newest), so it is never refused for lacking a hint.
    /// </summary>
    public static SessionChoice Choose(CallerPrincipal principal, string? hint, bool write)
    {
        if (principal.Kind == PrincipalKind.Anonymous)
        {
            return new SessionChoice(null, AnonymousCode, []);
        }

        if (principal.Kind == PrincipalKind.User && principal.Sessions.Count == 0)
        {
            return string.IsNullOrEmpty(hint) ? new SessionChoice(null, null, []) : new SessionChoice(null, NotYourSession, []);
        }

        if (!string.IsNullOrEmpty(hint))
        {
            var named = principal.Sessions.FirstOrDefault(s => s.SessionId == hint);
            return named is null ? new SessionChoice(null, NotYourSession, []) : new SessionChoice(named, null, named.Addresses);
        }

        if (principal.Sessions.Count == 1 || principal.Kind == PrincipalKind.User)
        {
            return new SessionChoice(principal.Sessions[0], null, principal.Sessions[0].Addresses);
        }

        return write
            ? new SessionChoice(null, AmbiguousSession, [])
            : new SessionChoice(null, null, [.. principal.Sessions.SelectMany(s => s.Addresses).Distinct(StringComparer.Ordinal)]);
    }

    /// <summary>
    /// The live registrations of <paramref name="mailbox"/> as the predicate reads them. One with no usable pid or no recorded
    /// start can never be matched, so it is left out.
    /// </summary>
    public static IReadOnlyList<SessionRegistration> LiveSessions(IMailbox mailbox, IEnumerable<JsonObject>? registrations = null)
    {
        var found = new List<SessionRegistration>();
        foreach (var r in registrations is null ? mailbox.LiveRegistrations() : registrations.Where(mailbox.RegistrationLive))
        {
            if (r.Get("session_id") is { IsText: true } id && id.AsString().Length > 0
                && r.Get("pid") is { IsNumber: true } p && double.IsInteger(p.AsNumber()) && p.AsNumber() > 1 && p.AsNumber() <= int.MaxValue
                && r.Get("pid_start") is { IsText: true } start && start.AsString().Length > 0)
            {
                found.Add(new SessionRegistration(id.AsString(), (int)p.AsNumber(), start.AsString(), [.. mailbox.RegistrationBoxes(r).Select(b => b.Address)]));
            }
        }

        return found;
    }

    /// <summary>
    /// Whether process <paramref name="pid"/> is the recorded Orchestrator of the team in <paramref name="teamFileRecord"/>: the
    /// record names that pid and a start time that is the process's start time now.
    /// </summary>
    public static bool IsRecordedOrchestrator(IProcessTable table, JsonValue teamFileRecord, int pid)
    {
        if (!teamFileRecord.IsMap || teamFileRecord.AsObject().Get("orchestrator") is not { IsMap: true } o)
        {
            return false;
        }

        var record = o.AsObject();
        return record.Get("pid") is { IsNumber: true } recorded && recorded.AsNumber() == pid
            && record.Get("pid_start") is { IsText: true } start && ProcessIdentity.Same(table, pid, start.AsString());
    }

    /// <summary>
    /// <paramref name="pid"/> and every ancestor above it short of pid 1, when the walk reaches pid 1; null when it cannot: a link
    /// that cannot be read, a parent pid of 0 on the way, or more than 64 hops.
    /// </summary>
    internal static IReadOnlyList<int>? WholeLine(IProcessTable table, int pid)
    {
        if (pid <= 1 || table.Ancestors(pid) is not { Count: > 0 } ancestors || ancestors[^1] != 1)
        {
            return null;
        }

        return [pid, .. ancestors.Where(p => p > 1)];
    }

    private static IReadOnlyList<string> Owned(SessionRegistration s, Func<SessionRegistration, string, bool> ownsTeam) =>
        [.. s.Addresses.Where(a => !a.StartsWith(AncestorSearch.OrchestratorName + "@", StringComparison.Ordinal)
            || ownsTeam(s, a[(AncestorSearch.OrchestratorName.Length + 1)..]))];

    /// <summary>
    /// The user heuristic: no process from the caller up is the Claude harness, the caller has a controlling terminal, and no live
    /// registered harness has that terminal (an orphan that kept its harness's terminal is not the user).
    /// </summary>
    private static bool IsUser(IProcessTable table, int pid, IReadOnlyList<SessionRegistration> live)
    {
        // an indeterminate line (null) is never the user
        if (HarnessOnLine(table, pid) is not false || table.Tty(pid) is not { Length: > 0 } tty)
        {
            return false;
        }

        foreach (var r in live)
        {
            switch (ProcessStart.Compare(table, r.Pid, r.PidStart))
            {
                case StartMatch.Different:
                    continue;
                case StartMatch.Unknown:
                    return false; // cannot tell whether this registration is alive: indeterminate
            }

            // a live registration's terminal that cannot be read (null), or is the caller's, is never the user; none ("") never matches
            if (table.Tty(r.Pid) is not { } theirs || theirs == tty)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether a process on the whole line from <paramref name="pid"/> up is the Claude harness: true when one is, false when every
    /// command on the line was read and none is, null when the line or any command on it cannot be read. Each command is read once.
    /// </summary>
    internal static bool? HarnessOnLine(IProcessTable table, int pid)
    {
        if (WholeLine(table, pid) is not { } line)
        {
            return null;
        }

        var unreadable = false;
        foreach (var p in line)
        {
            var command = table.Command(p);
            if (string.IsNullOrWhiteSpace(command))
            {
                unreadable = true;
            }
            else if (HarnessCommand.IsHarnessProcess(p, _ => command))
            {
                return true;
            }
        }

        return unreadable ? null : false;
    }
}
