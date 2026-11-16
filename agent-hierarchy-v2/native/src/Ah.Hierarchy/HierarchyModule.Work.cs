using Ah.Core.Json;

namespace Ah.Hierarchy;

/// <summary>The work registry's reads and op name for <c>ah work</c> and the verbs that accept a work id; <c>query</c> is a read-only state database query.</summary>
public static partial class HierarchyModule
{
    /// <summary>The op (in the <c>progress</c> domain) every registry write goes through.</summary>
    public const string WorkWriteOp = Work.WriteOp;

    /// <summary>The entry types an agent may record, as the footer and a refusal print them.</summary>
    public const string WorkRecordTypes = Work.AgentTypeList;

    /// <summary>The key a repo's registry rows, ADRs included, are stored under for <paramref name="dir"/>.</summary>
    public static string WorkDirKey(string dir) => Backlog.DirKey(dir);

    /// <inheritdoc cref="Work.Prefix"/>
    public static string? WorkPrefix(Func<string, object?[], IReadOnlyList<object?[]>> query, string dir) => Work.Prefix(query, Backlog.DirKey(dir));

    /// <inheritdoc cref="Work.Id"/>
    public static string WorkId(string prefix, long n) => Work.Id(prefix, n);

    /// <inheritdoc cref="Work.Resolve"/>
    public static WorkResolution WorkResolve(Func<string, object?[], IReadOnlyList<object?[]>> query, string dir, string text, string? callerTeam) =>
        Work.Resolve(query, Backlog.DirKey(dir), text, callerTeam);

    /// <summary>
    /// Runs reconcile (§3.2) for the repo at <paramref name="pool"/>: registers what the plans hold that the registry does not. Forced when the repo
    /// has no registry row or a backlog row is unregistered; otherwise it writes only when an item is new.
    /// </summary>
    public static void WorkReconcile(HierarchyServices services, Func<string, object?[], IReadOnlyList<object?[]>>? query, string pool)
    {
        ArgumentNullException.ThrowIfNull(services);
        var key = Backlog.DirKey(pool);
        var force = query is null || Work.Prefix(query, key) is null
            || query("SELECT 1 FROM plan_items WHERE hier_dir = ?1 AND stream = ?2 AND item NOT IN (SELECT item FROM work_items WHERE hier_dir = ?1 AND team = '' AND stream = ?2) LIMIT 1", [key, Backlog.Stream]).Count > 0;
        var known = force ? null : Work.Rows(query!, key).Select(static r => Work.Key(r.Team, r.Stream, r.Item, r.AddedAt)).ToHashSet(StringComparer.Ordinal);
        Work.Reconcile(services.Progress, services.StateCalls, pool, known);
    }

    /// <summary>One registered item with its state; this reads only its own team's plans.</summary>
    public static WorkItemView WorkItemOf(Func<string, object?[], IReadOnlyList<object?[]>> query, IHierarchyProgress progress, string pool, WorkRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new WorkItemView(
            row,
            row.IsBacklog
                ? Backlog.Get(query, pool, row.Item)?.Item
                : ChecklistEntries(progress, pool, row.Team).FirstOrDefault(e => e.Stream == row.Stream && e.Item.Item == row.Item)?.Item);
    }

    /// <summary>
    /// One timeline of an item, by time (§6.1): its plan rows as entries and what agents recorded, from three sources with no dual write. A gone
    /// placement has only its recorded entries. <paramref name="messageTitles"/>, given the timeline's requests, names each and never fails.
    /// </summary>
    public static IReadOnlyList<WorkEntry> WorkTimeline(
        Func<string, object?[], IReadOnlyList<object?[]>> query, Ah.Abstractions.IProgressLogSource logs, string pool, WorkItemView view, Func<IReadOnlyCollection<string>, Func<string, string>> messageTitles)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(logs);
        var row = view.Row;
        var key = Backlog.DirKey(pool);
        IReadOnlyList<JsonValue> planRows = view.Plan is null ? []
            : row.IsBacklog ? Backlog.ItemRows(query, key, row.Item)
            : TeamRows();
        var entries = Work.PlanEntries(planRows, messageTitles).ToList();
        entries.AddRange(query("SELECT ts_ms, type, title, body, by FROM work_log WHERE hier_dir = ?1 AND n = ?2 ORDER BY id", [key, row.N])
            .Select(static r => new WorkEntry((long)r[0]!, (string)r[1]!, (string)r[2]!, r[3] as string ?? string.Empty, r[4] as string ?? string.Empty)));
        return [.. entries.OrderBy(static e => e.Ms)];

        IReadOnlyList<JsonValue> TeamRows()
        {
            var rows = new List<JsonValue>();
            foreach (var line in logs.Rows(pool, row.Team, row.Stream + ".plan.jsonl").Select(static r => r.Body))
            {
                if (line.Length > 0 && JsonText.TryParse(System.Text.Encoding.UTF8.GetBytes(line), out var parsed) && parsed.IsMap && ProgressFormat.PlanText(parsed, "item") == row.Item)
                {
                    rows.Add(parsed);
                }
            }

            return rows;
        }
    }

    /// <summary>The longest external id <c>ah work set --ext</c> takes.</summary>
    public const int WorkMaxExt = Work.MaxExt;

    /// <summary>The longest title of a log entry.</summary>
    public const int WorkMaxTitle = Work.MaxTitle;

    /// <inheritdoc cref="Work.Attachments"/>
    public static IReadOnlyList<WorkAttachment> WorkAttachments(Func<string, object?[], IReadOnlyList<object?[]>> query, string dir, long? n) =>
        Work.Attachments(query, Backlog.DirKey(dir), n);

    /// <inheritdoc cref="Work.Description"/>
    public static string? WorkDescription(Func<string, object?[], IReadOnlyList<object?[]>> query, string dir, long n) =>
        Work.Description(query, Backlog.DirKey(dir), n);

    /// <inheritdoc cref="Work.Clean"/>
    public static string WorkClean(string text) => Work.Clean(text);

    /// <inheritdoc cref="Work.BodyProblem"/>
    public static string? WorkBodyProblem(string cleaned) => Work.BodyProblem(cleaned);

    /// <inheritdoc cref="Work.DescriptionNudge"/>
    public static string WorkDescriptionNudge(string id) => Work.DescriptionNudge(id);

    /// <inheritdoc cref="Work.LogCounts"/>
    public static IReadOnlyDictionary<long, long> WorkLogCounts(Func<string, object?[], IReadOnlyList<object?[]>> query, string dir) =>
        Work.LogCounts(query, Backlog.DirKey(dir));

    /// <inheritdoc cref="Work.CommitLogged"/>
    public static bool WorkCommitLogged(Func<string, object?[], IReadOnlyList<object?[]>> query, string dir, string sha) =>
        Work.CommitLogged(query, Backlog.DirKey(dir), sha);

    /// <summary>Whether <paramref name="kind"/> only contains work: never handed out as next, and closed by hand only.</summary>
    public static bool WorkIsContainerKind(string? kind) => ProgressFormat.IsContainerKind(kind);

    /// <summary>The repo's items with their links.</summary>
    public static WorkBoard WorkBoardOf(Func<string, object?[], IReadOnlyList<object?[]>> query, IHierarchyProgress progress, string pool) =>
        new(WorkItems(query, progress, pool), Work.Links(query, Backlog.DirKey(pool)));

    /// <summary>
    /// Every registered item with its state, read from the plan log (§5.3). A placement that no longer exists has a null plan and keeps its row.
    /// </summary>
    public static IReadOnlyList<WorkItemView> WorkItems(Func<string, object?[], IReadOnlyList<object?[]>> query, IHierarchyProgress progress, string pool)
    {
        // ponytail: reads every team's plan files per list; move team streams to the DB when this is slow
        var rows = Work.Rows(query, Backlog.DirKey(pool));
        var backlog = rows.Any(static r => r.IsBacklog) ? Backlog.List(query, pool).ToDictionary(static b => b.Item.Item, static b => b.Item, StringComparer.Ordinal) : [];
        var teams = new Dictionary<string, Dictionary<(string, string), PlanItem>>(StringComparer.Ordinal);
        return [.. rows.Select(r => new WorkItemView(r, r.IsBacklog ? backlog.GetValueOrDefault(r.Item) : TeamOf(r.Team).GetValueOrDefault((r.Stream, r.Item))))];

        Dictionary<(string, string), PlanItem> TeamOf(string team)
        {
            if (!teams.TryGetValue(team, out var items))
            {
                items = [];
                foreach (var entry in ChecklistEntries(progress, pool, team))
                {
                    items[(entry.Stream, entry.Item.Item)] = entry.Item;
                }

                teams[team] = items;
            }

            return items;
        }
    }
}
