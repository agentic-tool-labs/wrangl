namespace Ah.Tui.Core;

/// <summary>
/// The status language the terminal shares with the mod and the web: per status, its glyph (one code point, so its shape tells
/// the status without colour), the plain word shown for it, its tone (bad, warn, work or idle) and its sort rank (0 needs
/// attention first). <see cref="Of"/> reads a raw member activity or dispatch state as one of them. The same table is
/// <c>tests/fixtures/status-language.json</c>, and the tests hold this one, the mod's and that file to the same values.
/// </summary>
internal static class StatusLanguage
{
    internal sealed record Status(string Key, string Glyph, string Word, string Tone, int Rank);

    internal static readonly IReadOnlyList<Status> All =
    [
        new("stuck", "▲", "stuck", "bad", 0),
        new("late", "▲", "late", "bad", 1),
        new("needsYou", "■", "needs you", "warn", 2),
        new("dueSoon", "◔", "due soon", "warn", 3),
        new("working", "●", "working", "work", 4),
        new("waitingOrch", "◐", "waiting for the orchestrator", "idle", 5),
        new("idle", "○", "idle", "idle", 6),
        new("held", "‖", "Held", "idle", 6),
        new("done", "✓", "done", "idle", 7),
        new("ended", "×", "ended", "idle", 8),
        new("unknown", "?", "unknown", "idle", 9),
    ];

    internal static readonly IReadOnlyDictionary<string, string> From = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["working"] = "working",
        ["idle"] = "idle",
        ["blocked"] = "needsYou",
        ["needs_you"] = "needsYou",
        ["ended"] = "ended",
        ["waiting_user"] = "needsYou",
        ["waiting_orch"] = "waitingOrch",
        ["stalled"] = "stuck",
        ["overdue"] = "late",
        ["due-soon"] = "dueSoon",
        ["reported"] = "done",
        ["gone"] = "ended",
        ["orphaned"] = "ended",
        ["stale"] = "ended",
        ["expired"] = "ended",
    };

    /// <summary>The word shown beside an agent for each served liveness; a live agent shows none.</summary>
    internal static readonly IReadOnlyDictionary<string, string> Liveness = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["live"] = "",
        ["unknown"] = "unknown",
        ["not_seen"] = "not seen",
        ["proven_dead"] = "gone",
    };

    private static readonly Dictionary<string, Status> ByKey = All.ToDictionary(s => s.Key, StringComparer.Ordinal);

    internal static Status Get(string key) => ByKey[key];

    /// <summary>The status a raw state reads as; anything the table does not name is <c>unknown</c>.</summary>
    internal static Status Of(string? raw) => ByKey[raw is not null && From.TryGetValue(raw, out var key) ? key : "unknown"];
}
