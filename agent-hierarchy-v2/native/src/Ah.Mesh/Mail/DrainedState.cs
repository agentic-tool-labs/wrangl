using Ah.Core.Json;

namespace Ah.Mesh;

/// <summary>
/// The terminal ladder state a held team's mail ends in. The entry stays in <c>entries.jsonl</c>; the <c>drained</c> row is the audit
/// record, and every reader that closes an entry for another terminal state closes it for this one through <see cref="Is(IReadOnlyList{JsonObject})"/>.
/// </summary>
internal static class DrainedState
{
    public const string State = "drained";

    /// <summary>Whether <paramref name="state"/> is a ladder state after which the entry is never sent again: delivered, unconfirmed, superseded, declined, withdrawn or drained.</summary>
    public static bool IsTerminal(string? state) => state is "delivered" or "unconfirmed" or "superseded" or "declined" or "withdrawn" or State;

    /// <summary>The ladder states after which the delivery ladder leaves an entry alone, acked or not: no rung, no row, no alarm.</summary>
    public static readonly IReadOnlyList<string> Settled = ["delivered", "superseded", "declined", "withdrawn", State];

    /// <summary>Whether <paramref name="state"/> is one of <see cref="Settled"/>.</summary>
    public static bool IsSettled(string? state) => state is not null && Settled.Contains(state);

    /// <summary>Whether the last ladder row of an entry is <c>drained</c>.</summary>
    public static bool Is(IReadOnlyList<JsonObject> ladderRows) =>
        ladderRows.Count > 0 && ladderRows[^1].Get("state") is { IsText: true } s && s.AsString() == State;

    /// <summary>Whether the last ladder row of an entry closes it without a delivery: withdrawn, superseded, declined or drained.</summary>
    public static bool IsClosed(IReadOnlyList<JsonObject> ladderRows) =>
        ladderRows.Count > 0 && ladderRows[^1].Get("state") is { IsText: true } s && IsClosedState(s.AsString());

    /// <summary>Whether <paramref name="state"/> closes an entry without a delivery: withdrawn, superseded, declined or drained (all <see cref="Settled"/>).</summary>
    public static bool IsClosedState(string? state) => state is "withdrawn" or "superseded" or "declined" or State;

    /// <summary>The one line that stands in for a drained entry's text.</summary>
    public static string Line(string team, string kind, string id) =>
        $"[drained: team {team} is {kind}; message {id} not delivered]";
}
