using Ah.Core.Json;

namespace Ah.Commands;

/// <summary>
/// Which Claude environment a Claude member launches with (ah2-570, claude-env Amendment 4): one value per variable of
/// <see cref="Ah.Launch.LaunchModule.ClaudeEnvironment"/>, each recorded under its own field: a path sets it, an empty string unsets it.
/// </summary>
internal static class ClaudeEnvironment
{
    /// <summary>The variables and their fields, in launch-line order.</summary>
    internal static IReadOnlyList<(string Variable, string Field)> Variables => Ah.Launch.LaunchModule.ClaudeEnvironment;

    /// <summary>The caller's own value of <paramref name="variable"/>, empty when it has none.</summary>
    internal static string Caller(RosterContext c, string variable) => c.Services.Environment.GetVariable(variable) ?? string.Empty;

    /// <summary>Writes the caller's own value of every variable onto <paramref name="target"/>.</summary>
    internal static JsonObject PutCaller(RosterContext c, JsonObject target)
    {
        foreach (var (variable, field) in Variables)
        {
            target[field] = Caller(c, variable);
        }

        return target;
    }

    /// <summary>
    /// The values a launch uses, each variable on its own, in order: the member's existing <paramref name="row"/>'s; the caller's when it is proved to
    /// be the team's own Orchestrator or the lane's lead (<see cref="Resolved.TeamTakesCaller"/> then says the team record must take it); the team
    /// record's; the caller's.
    /// </summary>
    internal static IReadOnlyList<Resolved> Resolve(RosterContext c, JsonValue? row, JsonValue team)
    {
        // asked only for a variable the row does not record, as before
        var lead = new Lazy<bool>(() => DismissVerb.OwningOrchestrator(c, team) || c.Proved().Carve is { Disagreement: null });
        return [.. Variables.Select(v => ResolveOne(c, row, team, lead, v.Variable, v.Field))];
    }

    private static Resolved ResolveOne(RosterContext c, JsonValue? row, JsonValue team, Lazy<bool> lead, string variable, string field)
    {
        if (row?.Member(field) is { IsText: true } recorded)
        {
            return new Resolved(field, recorded.AsString(), false);
        }

        if (lead.Value)
        {
            var own = Caller(c, variable);
            return new Resolved(field, own, team.Member(field) is not { IsText: true } held || held.AsString() != own);
        }

        return team.Member(field) is { IsText: true } teamValue ? new Resolved(field, teamValue.AsString(), false) : new Resolved(field, Caller(c, variable), false);
    }

    /// <summary>A resolved value of one variable's <paramref name="Field"/>, and whether the team record is updated to it.</summary>
    internal readonly record struct Resolved(string Field, string Value, bool TeamTakesCaller);
}
