using Ah.Core.Json;

namespace Ah.Roster;

/// <summary>The read-only view of a team's <c>duty</c>: who is on duty, since when, and why. The views show it; nothing decides by it.</summary>
internal static class DutyView
{
    /// <summary><c>{deputy, since, cause}</c> when the record's <c>duty.deputy</c> names one of its member rows; null otherwise (no duty, or a stale one).</summary>
    internal static JsonObject? Of(JsonValue team)
    {
        if (team.Member("duty") is not { IsMap: true } duty || duty.Member("deputy") is not { IsText: true } deputy || deputy.AsString().Length == 0
            || team.Member("members") is not { IsArray: true } members
            || !members.AsArray().Any(m => m.Member("name") is { IsText: true } n && n.AsString() == deputy.AsString()))
        {
            return null;
        }

        return new JsonObject
        {
            ["deputy"] = deputy,
            ["since"] = duty.Member("since") is { IsText: true } since ? since : JsonValue.Null,
            ["cause"] = duty.Member("cause") is { IsText: true } cause ? cause : JsonValue.Null,
            ["tally"] = duty.Member("tally") is { IsMap: true } tally ? tally : JsonValue.Null,
        };
    }
}
