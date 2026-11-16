using Ah.Core.Json;

namespace Ah.Watch;

/// <summary>The JSON form of a <see cref="DispatchCandidate"/>, and the reading of it back; both ends use this one codec.</summary>
internal static class DispatchCandidateJson
{
    private static JsonValue Opt(string? text) => text is null ? JsonValue.Null : JsonValue.FromString(text);

    /// <summary>The candidate as a JSON object.</summary>
    /// <param name="candidate">The candidate.</param>
    /// <returns>The JSON object.</returns>
    public static JsonValue ToJson(DispatchCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return new JsonObject
        {
            ["request_id"] = candidate.RequestId,
            ["to"] = candidate.To,
            ["slug"] = candidate.Slug,
            ["to_addr"] = Opt(candidate.ToAddr),
            ["to_name"] = Opt(candidate.ToName),
            ["eta"] = candidate.Eta,
            ["created"] = Opt(candidate.Created),
            ["open"] = candidate.Open,
            ["response_path"] = candidate.ResponsePath,
            ["response_landed"] = candidate.ResponseLanded,
            ["landed_follow_up"] = candidate.LandedFollowUp,
            ["hierarchy_dir"] = candidate.HierarchyDir,
            ["team"] = Opt(candidate.Team),
            ["stream_active_ms"] = candidate.StreamActiveMs is { } ms ? JsonValue.FromNumber(ms) : JsonValue.Null,
            ["paused_lane"] = JsonValue.FromBool(candidate.PausedLane),
            ["member_paused"] = JsonValue.FromBool(candidate.MemberPaused),
            ["member_resumed_ms"] = candidate.MemberResumedMs is { } resumed ? JsonValue.FromNumber(resumed) : JsonValue.Null,
            ["active_lane"] = JsonValue.FromBool(candidate.ActiveLane),
            ["lane_charter"] = JsonValue.FromBool(candidate.LaneCharter),
            ["lane_delegating"] = JsonValue.FromBool(candidate.LaneDelegating),
            ["manager_team"] = Opt(candidate.ManagerTeam),
            ["after_slug"] = Opt(candidate.AfterSlug),
            ["after_eta"] = Opt(candidate.AfterEta),
            ["after_closed_ms"] = candidate.AfterClosedMs is { } closed ? JsonValue.FromNumber(closed) : JsonValue.Null,
            ["undelivered"] = JsonValue.FromBool(candidate.Undelivered),
            ["brief_unread"] = JsonValue.FromBool(candidate.BriefUnread),
            ["request_path"] = Opt(candidate.RequestPath),
            ["plain_send"] = JsonValue.FromBool(candidate.PlainSend),
        };
    }

    private static string? Text(JsonValue v, string key) => v.Member(key) is { IsText: true } m ? m.AsString() : null;

    private static string Required(JsonValue v, string key) => Text(v, key) ?? throw new InvalidOperationException($"the dispatch candidate has no \"{key}\"");

    /// <summary>Reads a candidate written by <see cref="ToJson"/>.</summary>
    /// <param name="json">The JSON object.</param>
    /// <returns>The candidate.</returns>
    public static DispatchCandidate Read(JsonValue json) =>
        new(
            Required(json, "request_id"),
            Required(json, "to"),
            Required(json, "slug"),
            Text(json, "to_addr"),
            Text(json, "to_name"),
            Required(json, "eta"),
            Text(json, "created"),
            json.Member("open") is { IsTruthy: true },
            Required(json, "response_path"),
            json.Member("response_landed") is { IsTruthy: true },
            Required(json, "landed_follow_up"),
            Required(json, "hierarchy_dir"))
        {
            Team = Text(json, "team"),
            StreamActiveMs = json.Member("stream_active_ms") is { IsNumber: true } n ? n.AsNumber() : null,
            PausedLane = json.Member("paused_lane") is { IsBool: true } paused && paused.AsBool(),
            MemberPaused = json.Member("member_paused") is { IsBool: true } memberPaused && memberPaused.AsBool(),
            MemberResumedMs = json.Member("member_resumed_ms") is { IsNumber: true } resumed ? resumed.AsNumber() : null,
            ActiveLane = json.Member("active_lane") is { IsBool: true } active && active.AsBool(),
            LaneCharter = json.Member("lane_charter") is { IsBool: true } charter && charter.AsBool(),
            LaneDelegating = json.Member("lane_delegating") is { IsBool: true } delegating && delegating.AsBool(),
            ManagerTeam = Text(json, "manager_team"),
            AfterSlug = Text(json, "after_slug"),
            AfterEta = Text(json, "after_eta"),
            AfterClosedMs = json.Member("after_closed_ms") is { IsNumber: true } closed ? closed.AsNumber() : null,
            Undelivered = json.Member("undelivered") is { IsBool: true } undelivered && undelivered.AsBool(),
            BriefUnread = json.Member("brief_unread") is { IsBool: true } unread && unread.AsBool(),
            RequestPath = Text(json, "request_path"),
            PlainSend = json.Member("plain_send") is { IsBool: true } plain && plain.AsBool(),
        };
}
