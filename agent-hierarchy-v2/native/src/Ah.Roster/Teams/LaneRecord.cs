using Ah.Core.Json;

namespace Ah.Roster;

/// <summary>
/// The delegation record of a lane: the caps and the base kept on the manager team's stream. A lane spawned before the record
/// existed has no caps and gets the defaults. The child team holds no copy, so a sub-orchestrator can never widen its own lane.
/// </summary>
internal static class LaneRecord
{
    internal const int DefaultMembers = 4;

    internal const int DefaultStreams = 8;

    internal const int CapMin = 1;

    internal const int CapMax = 16;

    internal const int DefaultMaxLanes = 6;

    /// <summary>
    /// The most live sub-orchestrators the team may have: its top-level <c>max_lanes</c>, or the default when absent. A value
    /// that is present but not a whole number from <see cref="CapMin"/> to <see cref="CapMax"/> reads as the default, and
    /// <c>Rejected</c> is its text so a check can name it.
    /// </summary>
    internal static (int Value, bool IsDefault, string? Rejected) MaxLanes(JsonValue team)
    {
        if (team.Member("max_lanes") is not { IsNull: false } raw)
        {
            return (DefaultMaxLanes, true, null);
        }

        return WholeCap(raw) is { } whole ? (whole, false, null) : (DefaultMaxLanes, true, JsonText.Stringify(raw, 0));
    }

    private static int? WholeCap(JsonValue raw) =>
        raw is { IsNumber: true } n && n.AsNumber() is var v && v >= CapMin && v <= CapMax && v == Math.Floor(v) ? (int)v : null;

    /// <summary>
    /// The cap that holds and where it comes from: the team's own valid <c>max_lanes</c> (<c>team</c>), else the valid <c>max_lanes</c>
    /// of the roster block the team was built from (<c>roster &lt;block&gt;</c>), else the default. An invalid value at either level is skipped
    /// and its text returned so a check can name it.
    /// </summary>
    internal static (int Value, string Source, string? RejectedTeam, string? RejectedRoster) Effective(JsonValue team, JsonValue roster)
    {
        var (teamValue, teamIsDefault, rejectedTeam) = MaxLanes(team);
        int? fromRoster = null;
        string? rejectedRoster = null;
        if (roster.Member("max_lanes") is { IsNull: false } raw)
        {
            fromRoster = WholeCap(raw);
            rejectedRoster = fromRoster is null ? JsonText.Stringify(raw, 0) : null;
        }

        if (!teamIsDefault)
        {
            return (teamValue, "team", null, rejectedRoster);
        }

        if (fromRoster is { } value)
        {
            var block = roster.Member("teamKey") is { IsText: true } key ? key.AsString() : "(default)";
            return (value, $"roster {block}", rejectedTeam, null);
        }

        return (DefaultMaxLanes, "default", rejectedTeam, rejectedRoster);
    }

    internal static (int Members, int Streams) Caps(JsonValue manager, string stream)
    {
        var lane = manager.Member("streams")?.Member(stream)?.Member("lane");
        return (Cap(lane, "max_members", DefaultMembers), Cap(lane, "max_streams", DefaultStreams));
    }

    /// <summary>The ref the top Orchestrator opened the lane stream from, or null when none was recorded.</summary>
    internal static string? Base(JsonValue manager, string stream) =>
        manager.Member("streams")?.Member(stream)?.Member("base") is { IsText: true } b && b.AsString().Length > 0 ? b.AsString() : null;

    private static int Cap(JsonValue? lane, string key, int fallback) =>
        lane?.Member(key) is { IsNumber: true } n && n.AsNumber() is var v && v >= CapMin && v <= CapMax && v == Math.Floor(v) ? (int)v : fallback;
}
