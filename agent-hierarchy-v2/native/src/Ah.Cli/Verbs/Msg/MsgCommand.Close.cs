using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.Cli.Verbs.Msg;

internal sealed partial class MsgCommand
{
    private static readonly string[] CloseReasons = ["superseded", "abandoned", "done"];

    /// <summary>
    /// <c>ah msg close &lt;request id&gt; --reason superseded|abandoned|done [--note &lt;text&gt;]</c>: the Orchestrator of the request's team retires a request
    /// nobody can answer, or, with <c>done</c>, one that was answered outside a response file (a note); the linked checklist item is then marked done. A response marked <c>closed: true</c> is written where a reply would be, so the exchange reads as closed everywhere an
    /// answered one does and the watch stops timing it. Closing twice is a no-op; a request that already has a report is refused.
    /// </summary>
    private int MailClose()
    {
        if (_opts.Positional.Count != 1)
        {
            throw new MailFailure("usage", "close needs exactly one request id");
        }

        var id = _opts.Positional[0];
        var reason = Str("reason");
        if (reason is null || !CloseReasons.Contains(reason, StringComparer.Ordinal))
        {
            throw new MailFailure("usage", "close needs --reason superseded|abandoned|done [--note <text>]");
        }

        var (exchange, requestPath, fm) = FindExchange(id, string.Empty, CloseTeams());
        var team = FrontText(fm?.Member("team")) ?? _teamArg
            ?? throw new MailFailure("usage", $"the request {id} names no team; pass --team <team> to close it as that team's Orchestrator");
        // ah2-76: the user, and the session the user made admin, close any request as its team's Orchestrator.
        var closerTeam = CloserTeam(team, fm)
            ?? (Principal.Kind == PrincipalKind.User ? team : null)
            ?? throw new MailFailure("not_your_session", $"only the Orchestrator of team {team}, its parent team's Orchestrator, or the Orchestrator that sent {id} can close it; this caller is none of them");

        var closed = Hier.Messages.CloseExchange(_graph.Bootstrap.EnsureHierarchyDir(HierarchyDir), exchange, requestPath.AsString(), reason, Str("note"), CloserOf(closerTeam), _cwd);
        var response = JsConvertText(closed.Get("response"));
        if (closed.Get("refused") is { IsText: true })
        {
            throw new MailFailure("already_reported", $"{id} already has a report ({response}); it is not stale");
        }

        if (closed.Get("already") is { IsTruthy: true })
        {
            return Print(closed, $"{id} is already closed\nresponse: {response}", 0);
        }

        ChecklistClosed(id, fm, reason, Str("note"));
        return Print(closed, $"closed {id} ({reason})\nresponse: {response}", 0);
    }

    /// <summary>
    /// The team whose Orchestrator seat this caller holds for closing a request of <paramref name="team"/>: that team itself, else its parent team
    /// (the top Orchestrator closes its lane children's exchanges), else the caller's own team when <paramref name="team"/> is gone or orphaned,
    /// else the request's own team when the caller is the Orchestrator that sent it.
    /// Null when the caller is none of them.
    /// </summary>
    private string? CloserTeam(string team, JsonValue? fm)
    {
        if (IsOwnName("orchestrator", team))
        {
            return team;
        }

        if (RosterCore.ReadTeamRecord(HierarchyDir, team).Member("parent") is { IsMap: true } link
            && link.Member("team") is { IsText: true } parentName && parentName.AsString() is { Length: > 0 } parent && IsOwnName("orchestrator", parent))
        {
            return parent;
        }

        // A gone (no team file) or orphaned team has no Orchestrator to close its requests, so any Orchestrator of a live team here may.
        var record = RosterCore.ReadTeamRecord(HierarchyDir, team);
        if ((record.IsNull || RosterCore.TeamIsOrphaned(record)) && RosterCore.OwnedTeams(HierarchyDir, new TeamInvoker(SessionPid, SessionId)) is { Count: > 0 } owned)
        {
            return owned[0] ?? "team";
        }

        var senderRole = FrontText(fm?.Member("from"));
        var senderName = FrontText(fm?.Member("from_name"));
        return senderRole is "orchestrator" or "sub-orchestrator" or "deputy-chief" && (senderName is null ? IsOwnName(senderRole, team) : IsOwnName(senderName, team, nameOnly: true))
            ? team
            : null;
    }

    /// <summary>The teams whose pools a close looks in: the ones <see cref="ExchangeTeams"/> names, plus the parent and the lane children of each owned team.</summary>
    private List<string> CloseTeams()
    {
        var teams = ExchangeTeams();
        foreach (var name in RosterCore.ListTeamNames(HierarchyDir))
        {
            var parent = RosterCore.ReadTeamRecord(HierarchyDir, name).Member("parent") is { IsMap: true } link && link.Member("team") is { IsText: true } p ? p.AsString() : null;
            if (parent is not null && teams.Contains(parent, StringComparer.Ordinal) && !teams.Contains(name, StringComparer.Ordinal))
            {
                teams.Add(name);
            }
        }

        foreach (var name in teams.ToList())
        {
            if (RosterCore.ReadTeamRecord(HierarchyDir, name).Member("parent") is { IsMap: true } link
                && link.Member("team") is { IsText: true } p && p.AsString() is { Length: > 0 } up && !teams.Contains(up, StringComparer.Ordinal))
            {
                teams.Add(up);
            }
        }

        return teams;
    }

    /// <summary>Who ran the close, as the response records it: the Orchestrator's address in the team and, when the caller has one, its session.</summary>
    private string CloserOf(string team) =>
        $"orchestrator@{team}" + (SessionId is { Length: > 0 } session ? $" (session {(session.Length > 8 ? session[..8] + "…" : session)})" : string.Empty);
}
