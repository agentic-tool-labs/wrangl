using Ah.Core.Json;
using Ah.Core.Time;

namespace Ah.Watch;

/// <summary>
/// Which open <c>MEMBER-STUCK</c> peer rows have gone a full window without the member making a tool call, so that the report is
/// copied once to the top Orchestrator. A stuck member that then goes idle raises no further refusal, so the hook side cannot do this.
/// </summary>
internal static class StuckEscalation
{
    internal const string StuckKind = "MEMBER-STUCK";

    internal const string EscalatedKind = "MEMBER-STUCK-ESCALATED";

    internal const string EventName = "member-stuck-escalated";

    /// <summary>A stuck report that has waited out the window: the row, who is stuck, and the team above their lane.</summary>
    internal sealed record Due(string Session, string Member, string Code, string TopTeam, string Text, DateTimeOffset StuckAt);

    private static string? Str(JsonValue row, string key) => row.Member(key) is { IsText: true } t && t.AsString().Length > 0 ? t.AsString() : null;

    private static DateTimeOffset? At(JsonValue row) =>
        Str(row, "ts") is { } ts && Timestamps.TryParseMs(ts, out var ms) ? DateTimeOffset.FromUnixTimeMilliseconds((long)ms) : null;

    /// <summary>
    /// The newest stuck report of each session and class that has a team above its lane, is at least <paramref name="window"/> old and
    /// has no escalation on record. The caller still drops one whose member has made a tool call since <see cref="Due.StuckAt"/>.
    /// </summary>
    internal static IReadOnlyList<Due> Evaluate(IEnumerable<JsonValue> rows, DateTimeOffset now, TimeSpan window)
    {
        var watch = rows.Where(r => r.IsMap && Str(r, "type") == "watch-event" && Str(r, "session_id") is not null).ToList();
        var done = watch.Where(r => Str(r, "kind") == EscalatedKind)
            .Select(r => (Str(r, "session_id"), Str(r, "code"), Str(r, "stuck_at")))
            .ToHashSet();
        var due = new List<Due>();
        foreach (var group in watch.Where(r => Str(r, "kind") == StuckKind && Str(r, "code") is not null).GroupBy(r => (Str(r, "session_id")!, Str(r, "code")!)))
        {
            var newest = group.Last();
            if (Str(newest, "top_team") is not { } top || Str(newest, "member") is not { } member || At(newest) is not { } at || now - at < window
                || done.Contains((group.Key.Item1, group.Key.Item2, Str(newest, "ts"))))
            {
                continue;
            }

            due.Add(new Due(group.Key.Item1, member, group.Key.Item2, top, Str(newest, "text") ?? string.Empty, at));
        }

        return due;
    }
}
