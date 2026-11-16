using Ah.Config;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Hierarchy;
using Ah.Hooks.Plumbing;
using Ah.Roster;

namespace Ah.Hooks.Gates;

/// <summary>
/// Whether a team's Orchestrator has work in flight that a held turn would strand. The one definition: an open dispatch whose addressee is
/// up, a live lane sub-orchestrator, an unacked entry in the Orchestrator's own box, or an open run anchor. A source that cannot be read
/// counts as live work, so a failure never lets a blocking question through.
/// </summary>
internal static class LiveWork
{
    /// <summary>What is live, in a few words, or null when the team is quiet.</summary>
    internal static string? Find(GateContext c, IReadOnlyList<Ah.Mesh.DeciderBox> boxes)
    {
        using var decisions = c.Services.Hierarchy.Core.DecisionReads();
        foreach (var (name, probe) in new (string, Func<string?>)[]
        {
            ("an open dispatch to a live member", () => OpenDispatch(c, boxes)),
            ("a live lane sub-orchestrator", () => LiveLane(c, boxes)),
            ("unacked mail in the orchestrator's box", () => UnackedMail(c, boxes)),
            ("a run anchor that is open", () => OpenAnchor(c)),
        })
        {
            try
            {
                if (probe() is { } why)
                {
                    return why.Length > 0 ? why : name;
                }
            }
            catch (Exception e)
            {
                c.LogError(e);
                return $"{name} (could not be read)";
            }
        }

        return null;
    }

    private static string? OpenDispatch(GateContext c, IReadOnlyList<Ah.Mesh.DeciderBox> boxes)
    {
        var s = c.Services;
        var sessionId = c.Input.NonEmpty("session_id");
        if (sessionId is null)
        {
            return string.Empty;
        }

        var dir = s.Config.HierarchyDir(c.Cwd());
        var teams = boxes.Select(b => b.Team).Distinct(StringComparer.Ordinal).ToList();
        var exchanges = HierarchyModule.CreateExchangeIndex(s.Hierarchy.Core, s.Hierarchy.Messages.PoolDirs(dir, teams));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in DispatchObligations.Live(s, sessionId))
        {
            if (row.Member("request_id") is not { IsText: true } rid || !seen.Add(rid.AsString()))
            {
                continue;
            }

            var pool = row.Member("path") is { IsText: true } path && path.AsString().Length > 0
                ? NodePath.Dirname(NodePath.Dirname(path.AsString()))
                : null;
            var exchange = exchanges.Find(rid.AsString(), pool);
            if (exchange.IsNull || exchange.Member("open") is not { IsTruthy: true }
                || exchange.Member("response")?.Member("path") is { IsText: true } response && s.Hierarchy.Messages.ResponseLanded(response.AsString()))
            {
                continue;
            }

            if (AddresseeUp(c, dir, row, teams))
            {
                return string.Empty;
            }
        }

        return null;
    }

    /// <summary>Whether the member a dispatch went to is up; a member that cannot be found or proven is taken as up.</summary>
    private static bool AddresseeUp(GateContext c, string dir, JsonValue row, IReadOnlyList<string> teams)
    {
        var s = c.Services;
        if (s.MemberUp is not { } up || row.Member("to_addr") is not { IsText: true } addr)
        {
            return true;
        }

        var at = addr.AsString().IndexOf('@');
        var name = at < 0 ? addr.AsString() : addr.AsString()[..at];
        var team = at < 0 ? null : addr.AsString()[(at + 1)..];
        foreach (var t in team is null ? teams : [team])
        {
            if (s.Roster.ReadTeamRecord(dir, t).Member("members") is { IsArray: true } members
                && members.AsArray().FirstOrDefault(m => m.Member("name") is { IsText: true } n && n.AsString() == name) is { IsMap: true } member)
            {
                return up(dir, member);
            }
        }

        return true;
    }

    private static string? LiveLane(GateContext c, IReadOnlyList<Ah.Mesh.DeciderBox> boxes)
    {
        var s = c.Services;
        var dir = s.Config.HierarchyDir(c.Cwd());
        var resolved = s.Config.ResolveConfig(c.Cwd(), new ResolveOptions(SessionId: c.Input.NonEmpty("session_id")));
        foreach (var team in boxes.Select(b => b.Team).Distinct(StringComparer.Ordinal))
        {
            if (s.Roster.ReadTeamRecord(dir, team).Member("streams") is not { IsMap: true } streams)
            {
                continue;
            }

            foreach (var stream in streams.AsObject().Keys)
            {
                if (streams.Member(stream) is not { IsMap: true } record
                    || record.Member("state") is not { IsText: true } state || state.AsString() != "open"
                    || record.Member("sub_orchestrator") is not { IsText: true } leader)
                {
                    continue;
                }

                var child = s.Roster.FindChildTeam(dir, team, stream);
                if (child is null
                    || RosterModule.LaneLeaderOf(s.Roster, dir, child, name => s.Roster.ReadTeamRecord(dir, name), role => s.Config.RoleClass(role, resolved) == "orchestrate") is not { } lane
                    || lane.Manager != team || lane.Member != leader.AsString())
                {
                    continue;
                }

                if (s.Roster.TeamLifeOf(s.Roster.ReadTeamRecord(dir, child)) != TeamLife.Orphaned)
                {
                    return string.Empty;
                }
            }
        }

        return null;
    }

    private static string? UnackedMail(GateContext c, IReadOnlyList<Ah.Mesh.DeciderBox> boxes)
    {
        if (c.Services.Advisory?.Mailbox is not { } mailbox)
        {
            return null;
        }

        return boxes.Select(b => b.Dir).OfType<string>().Distinct(StringComparer.Ordinal).Any(d => mailbox.Pending(d).Count > 0) ? string.Empty : null;
    }

    private static string? OpenAnchor(GateContext c) =>
        c.Services.Hierarchy.Decisions.RunLiveness(c.Cwd()) == "not" ? null : string.Empty;
}
