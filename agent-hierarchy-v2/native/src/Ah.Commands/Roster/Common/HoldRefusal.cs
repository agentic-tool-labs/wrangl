using Ah.Core.Json;
using Ah.Roster;

namespace Ah.Commands;

/// <summary>The one refusal every launch path gives for a held team, so the hold has a single message and a single code.</summary>
internal static class HoldRefusal
{
    /// <summary>Throws <c>team_held</c> when the context's team is under a hold, its own or an ancestor's.</summary>
    internal static void RefuseIfHeld(RosterContext c)
    {
        if (c.Services.Roster.HoldOf(c.Dir, c.TeamFile) is not { } hold)
        {
            return;
        }

        throw Refusal(c, hold);
    }

    internal static RosterFailure Refusal(RosterContext c, EffectiveHold hold)
    {
        var top = hold.Source ?? "@default";
        var message = $"{RosterModule.HoldRefusalCode}: team {hold.Team ?? "default"} is {hold.Kind} since {hold.Since ?? "unknown"}"
            + $"{(hold.CascadeFrom is { } from ? $" (through {from})" : string.Empty)}; only `ah roster resume --hold --team {top}` on the user's order resumes it";
        return c.Refuse(
            new JsonObject
            {
                ["refused"] = RosterModule.HoldRefusalCode,
                ["error"] = RosterModule.HoldRefusalCode,
                ["team"] = hold.Team ?? string.Empty,
                ["kind"] = hold.Kind,
                ["message"] = message,
            },
            message);
    }
}
