using Ah.Core.Json;

namespace Ah.Hierarchy;

/// <summary>The backlog's reads and op names for the <c>ah backlog</c> verbs; <c>query</c> is a read-only state database query.</summary>
public static partial class HierarchyModule
{
    /// <summary>The reserved stream name of the project's backlog.</summary>
    public const string BacklogStream = Backlog.Stream;

    /// <summary>The op (in the <c>progress</c> domain) every backlog write goes through.</summary>
    public const string BacklogWriteOp = Backlog.WriteOp;

    /// <summary>The error code of a backlog write refused because the holder or the ext belongs to another.</summary>
    public const string BacklogConflictCode = Backlog.ConflictCode;

    /// <inheritdoc cref="ProgressFormat.IsBacklogId"/>
    public static bool IsBacklogId(string text) => ProgressFormat.IsBacklogId(text);

    /// <inheritdoc cref="Backlog.List"/>
    public static IReadOnlyList<BacklogRow> BacklogList(
        Func<string, object?[], IReadOnlyList<object?[]>> query, string dir, string? wave = null, string? state = null, string? theme = null, string? holder = null) =>
        Backlog.List(query, dir, wave, state, theme, holder);

    /// <inheritdoc cref="Backlog.StateOf"/>
    public static string BacklogStateOf(PlanItem row, IEnumerable<PlanItem> items) => Backlog.StateOf(row, items);

    /// <summary><paramref name="rows"/> each with the state it reads as (<see cref="Backlog.StateOf"/>).</summary>
    public static IReadOnlyList<PlanItem> BacklogStates(IEnumerable<PlanItem> rows, IReadOnlyList<PlanItem> items) =>
        [.. rows.Select(r => r with { State = Backlog.StateOf(r, items) })];

    /// <inheritdoc cref="Backlog.Get"/>
    public static BacklogRow? BacklogGet(Func<string, object?[], IReadOnlyList<object?[]>> query, string dir, string id) => Backlog.Get(query, dir, id);

    /// <inheritdoc cref="Backlog.Next"/>
    public static BacklogRow? BacklogNext(Func<string, object?[], IReadOnlyList<object?[]>> query, string dir, string? theme = null, IEnumerable<PlanItem>? items = null) =>
        Backlog.Next(query, dir, theme, items);

    /// <inheritdoc cref="Backlog.History"/>
    public static IReadOnlyList<JsonValue> BacklogHistory(Func<string, object?[], IReadOnlyList<object?[]>> query, string dir, string id) => Backlog.History(query, dir, id);

    /// <inheritdoc cref="Backlog.Export"/>
    public static IReadOnlyList<BacklogRow> BacklogExport(Func<string, object?[], IReadOnlyList<object?[]>> query, string dir, long? sinceMs) => Backlog.Export(query, dir, sinceMs);

    /// <summary>The name of the daemon job that refreshes the backlog's health, and the key of its stamp in the <c>stall.ticks</c> meta row (<c>StallStore.Ticks</c>).</summary>
    public const string BacklogWatchJob = "backlog-watch";

    /// <inheritdoc cref="Backlog.Health"/>
    public static BacklogHealth BacklogHealthOf(Func<string, object?[], IReadOnlyList<object?[]>> query, string dir, long nowMs, long? stampMs, IEnumerable<PlanItem>? items = null) =>
        Backlog.Health(query, dir, nowMs, stampMs, Backlog.ChildrenOf(items));

    /// <inheritdoc cref="Backlog.HealthFacts"/>
    public static JsonObject BacklogHealthFacts(Func<string, object?[], IReadOnlyList<object?[]>> query, string dir, IEnumerable<PlanItem>? items = null) =>
        Backlog.HealthFacts(query, dir, items);

    /// <summary>Whether a status shows <paramref name="health"/>: the backlog has an item or a groom.</summary>
    public static bool BacklogShown(BacklogHealth health)
    {
        ArgumentNullException.ThrowIfNull(health);
        return health.Items.Count > 0 || health.LastGroomMs is not null;
    }

    /// <summary>
    /// <paramref name="health"/> as JSON. The short form is status.json's <c>backlog</c> block: untriaged, stale, last_groom_at, due, due_since.
    /// The full form adds the counts, the last groom summary, the reason, the job's last run and <c>stalled</c>.
    /// </summary>
    public static JsonObject BacklogHealthJson(BacklogHealth health, bool full)
    {
        ArgumentNullException.ThrowIfNull(health);
        static JsonValue Iso(long? ms) => ms is { } v ? ProgressFormat.PlanTime(DateTimeOffset.FromUnixTimeMilliseconds(v)) : JsonValue.Null;
        var json = new JsonObject
        {
            ["untriaged"] = health.Untriaged,
            ["stale"] = health.Stale,
            ["last_groom_at"] = Iso(health.LastGroomMs),
            ["due"] = health.Due,
            ["due_since"] = Iso(health.DueSinceMs),
        };
        if (!full)
        {
            return json;
        }

        var items = new JsonObject();
        foreach (var (wave, states) in health.Items.OrderBy(static w => w.Key, StringComparer.Ordinal))
        {
            var byState = new JsonObject();
            foreach (var (state, count) in states.OrderBy(static s => s.Key, StringComparer.Ordinal))
            {
                byState[state] = count;
            }

            items[wave] = byState;
        }

        json["items"] = items;
        json["last_groom_summary"] = health.LastGroomSummary ?? JsonValue.Null;
        json["reason"] = health.Reason;
        json["job_last_run_at"] = Iso(health.JobStampMs);
        json["stalled"] = health.Stalled;
        return json;
    }

    /// <summary>The one line the Orchestrator is told when a groom is due.</summary>
    public static string BacklogDueLine(string reason) =>
        $"ah: backlog groom due ({reason}). Dispatch backlog-groomer (Agent, subagent_type wrangl:backlog-groomer); its decisions for the user land on Needs-you as waiting-user items.";
}
