using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Time;

namespace Ah.Roster;

internal sealed partial class RosterCore
{
    /// <summary>One team's members, as <c>t.members</c> reads them: a null element throws, as property access on null does.</summary>
    private static IEnumerable<JsonValue> MemberRows(JsonValue team)
    {
        if (team.IsNull || team.Member("members") is not { IsArray: true } list)
        {
            yield break;
        }

        foreach (var m in list.AsArray())
        {
            if (m.IsNull)
            {
                throw new InvalidOperationException("cannot read a property of a null team member");
            }

            yield return m;
        }
    }

    private HashSet<string> MemberNameSet(string dir, string? team)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in MemberRows(ReadTeamRecord(dir, team)))
        {
            if (m.Member("name") is { IsText: true } n && n.AsString().Length > 0)
            {
                names.Add(n.AsString());
            }
        }

        return names;
    }

    public Func<string?, PaneMatch?> PaneResolver(string dir, bool liveOnly = false)
    {
        var teams = new List<(string? Name, JsonValue Team)>();
        foreach (var name in new string?[] { null }.Concat(ListTeamNames(dir)))
        {
            var team = ReadTeamRecord(dir, name);
            if (!liveOnly || (!team.IsNull && !TeamIsOrphaned(team)))
            {
                teams.Add((name, team));
            }
        }

        return PaneResolverOver(teams);
    }

    /// <summary>The pane rule of <see cref="PaneResolver"/> over given team records: the one member row whose <c>transport_id</c> is the pane, null when none or several.</summary>
    internal static Func<string?, PaneMatch?> PaneResolverOver(IReadOnlyList<(string? Name, JsonValue Team)> teams)
    {
        return paneId =>
        {
            if (string.IsNullOrEmpty(paneId))
            {
                return null;
            }

            PaneMatch? match = null;
            foreach (var (name, team) in teams)
            {
                if (team.IsNull || team.Member("members") is not { IsArray: true } list)
                {
                    continue;
                }

                var rows = list.AsArray()
                    .Where(m => m.IsTruthy && m.Member("transport_id") is { IsText: true } t && t.AsString() == paneId)
                    .ToList();
                if (rows.Count == 0)
                {
                    continue;
                }

                if (match is not null || rows.Count > 1)
                {
                    return null;
                }

                match = new PaneMatch(name, team, rows[0]);
            }

            return match;
        };
    }

    public MemberTeamLookup ResolveMemberTeam(string dir, string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return new MemberTeamLookup(false, null);
        }

        if (MemberNameSet(dir, null).Contains(name))
        {
            return new MemberTeamLookup(true, null);
        }

        foreach (var team in ListTeamNames(dir))
        {
            if (MemberNameSet(dir, team).Contains(name))
            {
                return new MemberTeamLookup(true, team);
            }
        }

        return new MemberTeamLookup(false, null);
    }

    public JsonValue TeamMemberByName(string dir, string? name, string? team = null)
    {
        var t = ReadTeamRecord(dir, team);
        if (t.IsNull || string.IsNullOrEmpty(name))
        {
            return JsonValue.Null;
        }

        foreach (var m in MemberRows(t))
        {
            if (m.Member("name") is { IsText: true } n && n.AsString() == name)
            {
                return m;
            }
        }

        return JsonValue.Null;
    }

    public bool TeamIsLive(JsonValue team, TeamInvoker? invoker, DateTimeOffset now)
    {
        if (team.IsNull)
        {
            return false;
        }

        if (invoker is not null && TeamOwnedBy(team, invoker))
        {
            return true;
        }

        var pid = team.Member("orchestrator") is { IsTruthy: true } o ? o.Member("pid") : null;
        if (!(JsConvert.AsInteger(pid) is { } p && p > 0 && p <= int.MaxValue && processes.PidAlive((int)p)))
        {
            return false;
        }

        var created = team.Member("created");
        if (created is not { IsText: true } c || !Timestamps.TryParseMs(c.AsString(), out var ms))
        {
            return false;
        }

        var ageSeconds = Math.Max(0, (now.ToUnixTimeMilliseconds() - ms) / 1000);
        return ageSeconds <= RosterValues.TeamStaleAgeSeconds;
    }
}
