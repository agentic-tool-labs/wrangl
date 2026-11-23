using Ah.Core.Json;

namespace Ah.Roster;

/// <summary>The implicit team (ah2-34) as the owned-team lists see it, and when it yields to another team the session owns (ah2-470).</summary>
internal static class ImplicitTeams
{
    /// <summary>Whether <paramref name="team"/> is an implicit team that no member has joined: <c>implicit</c> is boolean true and <c>members</c> an empty array.</summary>
    public static bool IsEmptyImplicit(JsonValue team) =>
        team.IsMap && team.Member("implicit") is { IsBool: true } flag && flag.AsBool() && team.Member("members") is { IsArray: true } members && members.AsArray().Count == 0;

    /// <summary>Whether <paramref name="team"/> yields to another owned team: an empty implicit team that records no stream.</summary>
    public static bool Yields(JsonValue team) =>
        IsEmptyImplicit(team) && !(team.Member("streams") is { IsMap: true } streams && streams.AsObject().Count > 0);

    /// <summary><paramref name="teams"/> without each team that <see cref="Yields"/>, when at least one other team is in the list; else as given.</summary>
    public static IReadOnlyList<T> WithoutYielded<T>(IReadOnlyList<T> teams, Func<T, JsonValue> record)
    {
        if (teams.Count < 2)
        {
            return teams;
        }

        var kept = teams.Where(t => !Yields(record(t))).ToList();
        return kept.Count == 0 || kept.Count == teams.Count ? teams : kept;
    }
}
