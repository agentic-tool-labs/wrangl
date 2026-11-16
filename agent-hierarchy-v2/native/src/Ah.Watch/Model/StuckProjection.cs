using Ah.Core.Json;
using Ah.Core.Time;
using Ah.Hierarchy;

namespace Ah.Watch.Model;

/// <summary>
/// The serve-time half of the stuck rule. An exchange slice carries the file facts (when the task was sent, when its assignee last showed activity);
/// whether the task is stuck at the instant a response is built comes from <see cref="HierarchyModule.ComputeStuck"/> over those facts, the assignee's
/// served state and that instant, so no slice depends on the clock.
/// </summary>
internal static class StuckProjection
{
    /// <summary>The slice id of the summary of the team a member slice id belongs to, or null for an id that is no member's.</summary>
    internal static string? SummaryOf(string? memberId) =>
        memberId is not null && memberId.StartsWith("member:", StringComparison.Ordinal) && memberId.LastIndexOf('/') is var slash and > 7
            ? "team-summary:" + memberId[7..slash]
            : null;

    /// <summary>
    /// The state of a served <c>member_states</c> entry, where <c>gone</c> stands only for a proven death: an entry that says gone without
    /// <c>liveness</c> being <c>proven_dead</c> reads as <c>unknown</c>. Null for an entry with no state.
    /// </summary>
    internal static string? ServedState(JsonValue served)
    {
        if (served.Member("state") is not { IsText: true } state)
        {
            return null;
        }

        var provenDead = served.Member("liveness") is { IsText: true } liveness && liveness.AsString() == "proven_dead";
        return state.AsString() == "gone" && !provenDead ? "unknown" : state.AsString();
    }

    /// <summary><c>stuck</c> and, while stuck, <c>stuck_since</c> and <c>idle_for</c> of the task a body describes; null for a body with no eta facts.</summary>
    /// <param name="body">The exchange slice body.</param>
    /// <param name="assigneeState">The served state of the assignee (its <c>member_states</c> entry), or null when none is served.</param>
    /// <param name="nowMs">The instant the response is built for.</param>
    internal static JsonObject? Of(JsonObject body, string? assigneeState, double nowMs)
    {
        if (body.Get("eta_facts") is not { IsMap: true } map)
        {
            return null;
        }

        var facts = map.AsObject();
        var live = assigneeState is "working" or "idle";
        var result = HierarchyModule.ComputeStuck(live, assigneeState, Number(facts, "start_ms"), LastActivity(facts), nowMs);
        var fields = new JsonObject { ["stuck"] = result.Stuck };
        if (result is { Stuck: true, SinceMs: { } since, IdleForSeconds: { } idle })
        {
            fields["stuck_since"] = Timestamps.ToIsoString(DateTimeOffset.FromUnixTimeMilliseconds((long)since));
            fields["idle_for"] = JsonValue.FromNumber(idle);
        }

        return fields;
    }

    /// <summary>The instant the task a body describes would become stuck if its assignee stays up; null when it has no eta facts.</summary>
    internal static double? StuckAt(JsonObject body) =>
        body.Get("eta_facts") is { IsMap: true } map && Math.Max(Number(map.AsObject(), "start_ms"), LastActivity(map.AsObject())) is > 0 and var baseMs
            ? baseMs + HierarchyModule.QueueStallMs
            : null;

    /// <summary>The assignee's last sign of life: its activity record (state change, tool use, working beat) or its latest word to the dispatcher (<c>heard_ms</c>).</summary>
    private static double LastActivity(JsonObject facts) => Math.Max(Number(facts, "activity_ms"), Number(facts, "heard_ms"));

    private static double Number(JsonObject o, string key) => o.TryGetValue(key, out var v) && v.Kind == JsonKind.Number ? v.AsNumber() : 0;
}
