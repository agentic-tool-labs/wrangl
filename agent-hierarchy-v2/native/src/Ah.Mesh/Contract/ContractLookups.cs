using Ah.Abstractions;
using Ah.Config;
using Ah.Core.Json;
using Ah.Hierarchy;

namespace Ah.Mesh;

/// <summary>The lookups a participant makes before it registers: which teams the daemon can see, and what its own member row says.</summary>
public interface IContractLookups
{
    /// <summary>
    /// <c>[{team, orchestrator_live}]</c> for the named teams visible from <paramref name="context"/> and from the live
    /// registrations, ordered by team name.
    /// </summary>
    JsonArray Teams(RequestContext context);

    /// <summary>
    /// The values <c>register</c> needs for the admitted custom member at <paramref name="address"/>, read from its team file:
    /// <c>{address, role, team_file, boxes, hierarchy_dir, poll_ms}</c>; null when the address is not a <c>kind: "custom"</c>
    /// member of a visible team that names a valid harness.
    /// </summary>
    JsonObject? Join(string address, RequestContext context);
}

internal sealed class ContractLookups(IMailbox mailbox, IHierarchyCore hierarchy, IConfigCore config) : IContractLookups
{
    public JsonArray Teams(RequestContext context)
    {
        var live = mailbox.LiveRegistrations()
            .Select(r => r.Get("team_file"))
            .Where(f => f is { IsText: true })
            .Select(f => f!.Value.AsString())
            .ToHashSet(StringComparer.Ordinal);
        var rows = new JsonArray();
        foreach (var t in mailbox.VisibleTeams(context)
                     .Where(t => t.Live || live.Contains(t.Path))
                     .OrderBy(t => t.Team, StringComparer.Ordinal))
        {
            var pid = t.Data.Member("orchestrator") is { IsMap: true } o ? o.AsObject().Get("pid") : null;
            rows.Add(new JsonObject
            {
                ["team"] = t.Team,
                ["orchestrator_live"] = t.Live && pid is { } p && hierarchy.PidAlive(p),
            });
        }

        return rows;
    }

    public JsonObject? Join(string address, RequestContext context)
    {
        var resolved = mailbox.ResolveAddress(address, context);
        if (resolved.Failure is not null || resolved.Value is not { } r)
        {
            return null;
        }

        if (r.Member is not { IsMap: true } member)
        {
            return null;
        }

        var row = member.AsObject();
        if (row.Get("kind") is not { IsText: true } kind || kind.AsString() != "custom"
            || row.Get("harness") is not { IsText: true } harness || !config.IsHarnessName(harness))
        {
            return null;
        }

        var answer = new JsonObject { ["address"] = r.Address };
        if (row.Get("role") is { } role)
        {
            answer["role"] = role;
        }

        answer["team_file"] = r.TeamPath;
        answer["boxes"] = new JsonObject { [r.Address] = r.Dir };
        answer["hierarchy_dir"] = r.Home is { } home ? home : JsonValue.Null;
        answer["poll_ms"] = IMailCore.DefaultPollMs;
        return answer;
    }
}
