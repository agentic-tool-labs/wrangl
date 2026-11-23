using Ah.Core.Json;

namespace Ah.Commands;

/// <summary>
/// Which <c>CLAUDE_CONFIG_DIR</c> a Claude member launches with (ah2-570), recorded as <see cref="Ah.Launch.LaunchModule.ClaudeConfigField"/>:
/// a path sets it, an empty string unsets it.
/// </summary>
internal static class ClaudeEnvironment
{
    internal const string Field = Ah.Launch.LaunchModule.ClaudeConfigField;

    /// <summary>The caller's own value: its <c>CLAUDE_CONFIG_DIR</c>, empty when it has none.</summary>
    internal static string Caller(RosterContext c) => c.Services.Environment.GetVariable("CLAUDE_CONFIG_DIR") ?? string.Empty;

    /// <summary>
    /// The value a launch uses, in order: the member's existing <paramref name="row"/>'s; the caller's when it is proved to be the team's own
    /// Orchestrator or the lane's lead (<see cref="Resolved.TeamTakesCaller"/> then says the team record must take it); the team record's; the caller's.
    /// </summary>
    internal static Resolved Resolve(RosterContext c, JsonValue? row, JsonValue team)
    {
        if (row?.Member(Field) is { IsText: true } recorded)
        {
            return new Resolved(recorded.AsString(), false);
        }

        if (DismissVerb.OwningOrchestrator(c, team) || c.Proved().Carve is { Disagreement: null })
        {
            var own = Caller(c);
            return new Resolved(own, team.Member(Field) is not { IsText: true } held || held.AsString() != own);
        }

        return team.Member(Field) is { IsText: true } teamValue ? new Resolved(teamValue.AsString(), false) : new Resolved(Caller(c), false);
    }

    /// <summary>A resolved value, and whether the team record is updated to it.</summary>
    internal readonly record struct Resolved(string Value, bool TeamTakesCaller);
}
