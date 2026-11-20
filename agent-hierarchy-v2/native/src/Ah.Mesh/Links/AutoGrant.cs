using Ah.Core.Json;
using Ah.Hierarchy;

namespace Ah.Mesh;

/// <summary>
/// The facts the daemon's auto-grant judges: the classified parties (requester first), the task's front matter (null when the task is
/// closed or unknown), the team's <c>mesh.auto_grant</c> pairs, the asked limits and the requester's active links (null with no team directory).
/// </summary>
internal sealed record AutoGrantFacts(
    IReadOnlyList<MeshParty> People, string Team, JsonObject? TaskFm, JsonArray Pairs, double AskedMessages, double AskedMinutes, int? ActiveCount);

/// <summary>
/// The conditions under which ahd opens a link with no Orchestrator turn: one judgment for a <c>link-request</c>'s auto-grant and for a
/// send that opens its own link (ah2-343).
/// </summary>
internal static class AutoGrant
{
    /// <summary>The minutes a review loop's link may run, and the review rounds it allows.</summary>
    internal const double LoopMinutes = 120;

    internal const double LoopRounds = 3;

    internal static double Limit(ILinks links, string name, double fallback = 0) => LinkCommands.Num(links.Limits().Get(name), fallback);

    /// <summary>Whether two roles are an implementer and the reviewer: the pair a review loop runs between.</summary>
    internal static bool IsLoopPair(string? a, string? b)
    {
        var roles = new[] { a, b };
        var implementers = Ah.Config.ConfigModule.PeerEligibleBuiltinRolesOfClass("implement").Where(r => r != "qa").ToList();
        return roles.Count(r => r == "reviewer") == 1 && roles.Count(r => r is not null && implementers.Contains(r)) == 1;
    }

    /// <summary>The default minutes of a link, raised for a review loop.</summary>
    internal static double Minutes(JsonObject limits, bool loop)
    {
        var minutes = LinkCommands.Num(limits.Get("minutes"), 0);
        return loop ? Math.Max(LoopMinutes, minutes) : minutes;
    }

    /// <summary>The team's directory, then the caller's hierarchy directory and the main checkout's: where a team's records are looked for.</summary>
    internal static List<string> Pools(string? teamDir, string? hierarchyDir, string? mainDir) =>
        new[] { teamDir, hierarchyDir, mainDir }.Where(d => !string.IsNullOrEmpty(d)).Select(d => d!).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>The exchange of request <paramref name="taskId"/> of <paramref name="team"/> and its home, or null.</summary>
    internal static (string Dir, JsonValue Exchange)? FindTask(IHierarchyMessages messages, IHierarchyCore core, IEnumerable<string> pools, string team, string taskId)
    {
        foreach (var dir in pools)
        {
            var ex = messages.ListExchangesAcross(dir, string.IsNullOrEmpty(team) ? [] : [team]).Cast<JsonValue>().Cast<JsonValue?>()
                .FirstOrDefault(x => x!.Value.Member("id") is { IsText: true } t && t.AsString() == taskId);
            if (ex is { } found && found.Member("request") is { IsTruthy: true }
                && core.ReadMsgFile(LinkCommands.Text(found.Member("request")!.Value.Member("path"))) is { IsNull: false } m
                && m.Member("fm") is { IsMap: true } fm && LinkCommands.OfTeam(fm.AsObject(), team))
            {
                return (HierarchyModule.HomeOf(found, dir), found);
            }
        }

        return null;
    }

    /// <summary>The front matter of a found task's request when the task is open, else null.</summary>
    internal static JsonObject? OpenTaskFm(IHierarchyCore core, (string Dir, JsonValue Exchange)? task)
    {
        if (task is not { } t || Links.ExchangeClosedOnPurpose(core, t.Exchange))
        {
            return null;
        }

        var tm = core.ReadMsgFile(LinkCommands.Text(t.Exchange.Member("request")!.Value.Member("path")));
        return !tm.IsNull && tm.Member("fm") is { IsMap: true } f ? f.AsObject() : new JsonObject();
    }

    /// <summary>Whether ahd may open the link on its own, and why not when it may not.</summary>
    internal static (bool Ok, string? Why) Judge(AutoGrantFacts facts, JsonObject limits)
    {
        double Lim(string name) => LinkCommands.Num(limits.Get(name), 0);
        var people = facts.People;
        if (people.Any(p => p.Kind != "member" || p.Team != facts.Team))
        {
            return (false, "not all members of the team");
        }

        if (people.Count - 1 > Lim("peersPerLink"))
        {
            return (false, "more than the peer cap");
        }

        if (people.Any(p => p.Role == "ultra-advisor"))
        {
            return (false, "ultra-advisor involved");
        }

        if (facts.TaskFm is not { } tfm)
        {
            return (false, "task closed or unknown");
        }

        if (!(tfm.Get("from") is { IsText: true } from && from.AsString() == "orchestrator"))
        {
            return (false, "task not dispatched by the Orchestrator");
        }

        var toName = tfm.Get("to_name");
        var to = tfm.Get("to");
        bool Dispatched(MeshParty p) =>
            StrictEquals(toName, p.Name) || (!(toName is { IsTruthy: true }) && StrictEquals(to, p.Role));
        if (!people.Any(Dispatched))
        {
            return (false, "task not dispatched to the requester or a peer");
        }

        var who = people[0];
        bool Listed(string? a, string? b) => facts.Pairs.Any(pair =>
        {
            var x = LinkCommands.Text(pair.AsArray()[0]);
            var y = LinkCommands.Text(pair.AsArray()[1]);
            return (x == a && y == b) || (x == b && y == a);
        });
        if (!people.Skip(1).All(p => Listed(who.Role, p.Role)))
        {
            return (false, "role pair not listed");
        }

        var loop = people.Count == 2 && IsLoopPair(people[0].Role, people[1].Role);
        if (facts.AskedMessages > Lim("messages") || facts.AskedMinutes > Minutes(limits, loop))
        {
            return (false, "limits above the defaults");
        }

        if (facts.ActiveCount is { } active && active >= Lim("activePerMember"))
        {
            return (false, "requester at its active-link cap");
        }

        return (true, null);
    }

    /// <summary><c>a === b</c> where <paramref name="a"/> null is an absent field and <paramref name="b"/> null an undefined value.</summary>
    private static bool StrictEquals(JsonValue? a, string? b) =>
        a is null ? b is null : a.Value.IsText && b is not null && a.Value.AsString() == b;
}
