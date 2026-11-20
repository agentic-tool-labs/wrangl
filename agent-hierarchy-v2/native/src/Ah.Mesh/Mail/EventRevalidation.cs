using Ah.Core.Json;

namespace Ah.Mesh;

/// <summary>
/// The delivery-time check of an ahd event: whether what it says is still true. A carrier asks before it shows an event and acks
/// one that no longer applies with the reason <see cref="ResolvedReason"/>; an event name with no check here applies. A check-in or
/// silence event applies only while its exchange is open and unanswered, or its report was read and the lane it charters is still running,
/// and an alarm about a member only while that member is on the team.
/// </summary>
internal static partial class EventRevalidation
{
    internal const string ResolvedReason = "resolved";

    /// <summary>
    /// Whether <paramref name="entry"/> still applies; <paramref name="reached"/> answers, for a request id and a response path, whether the
    /// report reached the Orchestrator. <paramref name="memberGone"/>, <paramref name="itemWaiting"/> and <paramref name="memberBlocked"/> take
    /// the entry's <c>data.member</c> or <c>data.item</c>, and answer so that a failed read leaves the alarm applying.
    /// </summary>
    internal static bool Applies(
        JsonValue entry,
        Func<string, string, bool> reached,
        Func<string, bool> exchangeClosed,
        Func<string, bool> reportAcked,
        Func<string, bool> readRunningLane,
        Func<string, bool> memberGone,
        Func<string, bool> itemWaiting,
        Func<string, bool> memberBlocked,
        Func<string, bool> memberIdle)
    {
        if (entry.Member("event") is not { IsText: true } name)
        {
            return true;
        }

        var data = entry.Member("data");
        var member = data?.Member("member") is { IsText: true } named ? named.AsString() : null;
        var asked = data?.Member("request_id") is { IsText: true } id ? id.AsString() : null;
        bool Here() => member is null || !memberGone(member);
        return name.AsString() switch
        {
            "report-landed" => data?.Member("request_id") is not { IsText: true } request
                || data.Value.Member("response_path") is not { IsText: true } response
                || !reached(request.AsString(), response.AsString()),
            "check-in-due" or "peer-silent" => Here() && (asked is null || !(exchangeClosed(asked) || (reportAcked(asked) && !readRunningLane(asked)))),
            "idle-without-report" => Here() && (asked is null || !(exchangeClosed(asked) || reportAcked(asked))),
            "member-waiting" => Here() && (data?.Member("item") is not { IsText: true } item || itemWaiting(item.AsString())),
            "member-blocked" => Here() && (member is null || memberBlocked(member)),
            // b331: the members an idle-capacity alarm named may have been dismissed or sent work since it fired; ready items alone are no reason to deliver it
            IdleCapacity => NamedMembers(data) is not { } idleNamed || idleNamed.Any(memberIdle),
            // ponytail: a member-down entry for a member who came back stays until read, next to its member-recovered; check liveness here if that misleads.
            // a cap kill reaches the Orchestrator though its owner is gone; only the owner's own copy is dropped
            "job-overdue" when data?.Member("step") is { IsText: true } step && step.AsString() == "capped" => data.Value.Member("owner_copy") is not { IsBool: true } copy || !copy.AsBool() || Here(),
            "job-overdue" or "queue-stall" or "stalled-after-compact" or "member-down" or "member-stuck" or "step-needs-user" => Here(),
            _ => true,
        };
    }

    /// <summary>The event name of the watcher's idle-members alarm (<c>QueueEvents.IdleCapacityName</c>).</summary>
    private const string IdleCapacity = "idle-capacity";

    private static List<string>? NamedMembers(JsonValue? data) =>
        data?.Member("members") is { IsArray: true } list ? [.. list.AsArray().Where(static m => m.IsText).Select(static m => m.AsString())] : null;

    /// <summary>
    /// An <c>idle-capacity</c> entry as it is shown: the members no longer idle are left out of its text and its <c>data.members</c>. Any other entry,
    /// or one whose members are all still idle (or none, which <see cref="Applies"/> resolves), is returned as it is.
    /// </summary>
    internal static JsonValue Shown(JsonValue entry, Func<string, bool> memberIdle)
    {
        if (entry.Member("event") is not { IsText: true } name || name.AsString() != IdleCapacity
            || NamedMembers(entry.Member("data")) is not { } named
            || named.Where(memberIdle).ToList() is var idle && (idle.Count == named.Count || idle.Count == 0))
        {
            return entry;
        }

        var copy = entry.AsObject().Clone();
        var data = entry.Member("data")!.Value.AsObject().Clone();
        var members = new JsonArray();
        foreach (var m in idle)
        {
            members.Add(m);
        }

        data["members"] = members;
        copy["data"] = data;
        if (copy.Get("text") is { IsText: true } text)
        {
            // ponytail: rewrites the member clause QueueEvents.IdleCapacityEvent writes; the two change together.
            var list = string.Join(", ", idle.Take(6)) + (idle.Count > 6 ? $" +{idle.Count - 6} more" : string.Empty);
            copy["text"] = MemberClause().Replace(text.AsString(), $"{idle.Count} member{(idle.Count == 1 ? string.Empty : "s")} idle 10+ min with no open request: {list}.", 1);
        }

        return copy;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\d+ members? idle 10\+ min with no open request: [^.]*\.")]
    private static partial System.Text.RegularExpressions.Regex MemberClause();
}
