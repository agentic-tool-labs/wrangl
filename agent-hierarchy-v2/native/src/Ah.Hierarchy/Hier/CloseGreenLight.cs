using System.Globalization;
using Ah.Core.Json;

namespace Ah.Hierarchy;

/// <summary>Why a close green light does or does not hold.</summary>
public enum GreenLightKind
{
    Granted,
    NoGrant,
    Expired,
    ReplacedOrchestrator,
}

/// <summary>The verdict of <see cref="HierarchyModule.EvaluateCloseGreenLight"/>: whether the close may go ahead, and when a grant expired, when.</summary>
public readonly record struct GreenLightVerdict(GreenLightKind Kind, string? ExpiredAt = null, string? GrantedAt = null)
{
    public bool Granted => Kind == GreenLightKind.Granted;
}

/// <summary>
/// The one test of a close green light, shared by the verbs that close a sub-team and by the hook that would otherwise ask. A grant is
/// an entry of <c>streams.&lt;ws&gt;.close_grants</c> in the managing team's file; it holds for one verb on one child team and one plan
/// token, until it expires, and only while the managing team's Orchestrator is the one that gave it.
/// </summary>
internal static class CloseGreenLight
{
    /// <summary>
    /// Whether <paramref name="manager"/> (the managing team's document) holds, for <paramref name="stream"/>, an unexpired grant of
    /// <paramref name="verb"/> on <paramref name="childTeam"/> with <paramref name="token"/> that was given by the session, pid and pid
    /// start its <c>orchestrator</c> record names now. <c>targets</c> is never compared.
    /// </summary>
    public static GreenLightVerdict Evaluate(JsonValue manager, string stream, string verb, string childTeam, string token, DateTimeOffset now)
    {
        var grants = manager.Member("streams")?.Member(stream)?.Member("close_grants") is { IsArray: true } list ? list.AsArray() : null;
        if (grants is null)
        {
            return new GreenLightVerdict(GreenLightKind.NoGrant);
        }

        var current = manager.Member("orchestrator");
        var matching = grants.Where(g => Text(g, "verb") == verb && Text(g, "team") == childTeam && Text(g, "plan_token") == token).ToList();
        if (matching.Count == 0)
        {
            return new GreenLightVerdict(GreenLightKind.NoGrant);
        }

        var live = matching.Where(g => TryTime(Text(g, "expires_at"), out var expires) && now < expires).ToList();
        if (live.Count == 0)
        {
            var latest = matching.Select(g => Text(g, "expires_at")).Where(t => t is not null).OrderByDescending(t => t, StringComparer.Ordinal).FirstOrDefault();
            return new GreenLightVerdict(GreenLightKind.Expired, latest);
        }

        var given = live.FirstOrDefault(g => BySameOrchestrator(g.Member("by"), current));
        return given.IsMap
            ? new GreenLightVerdict(GreenLightKind.Granted, null, Text(given, "granted_at"))
            : new GreenLightVerdict(GreenLightKind.ReplacedOrchestrator);
    }

    /// <summary>The session, pid and pid start of a grant's <c>by</c> all equal the managing Orchestrator's record; a missing field never equals.</summary>
    private static bool BySameOrchestrator(JsonValue? by, JsonValue? orchestrator)
    {
        if (by is not { IsMap: true } b || orchestrator is not { IsMap: true } o)
        {
            return false;
        }

        return Text(b, "session_id") is { Length: > 0 } session && session == Text(o, "session_id")
            && b.Member("pid") is { IsNumber: true } pid && o.Member("pid") is { IsNumber: true } currentPid && pid.AsNumber() == currentPid.AsNumber()
            && Text(b, "pid_start") is { Length: > 0 } start && start == Text(o, "pid_start");
    }

    private static string? Text(JsonValue v, string key) => v.Member(key) is { IsText: true } t ? t.AsString() : null;

    private static bool TryTime(string? text, out DateTimeOffset value)
    {
        value = default;
        return text is not null && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
    }
}
