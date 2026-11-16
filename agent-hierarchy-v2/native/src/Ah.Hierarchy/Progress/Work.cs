using System.Text.RegularExpressions;
using Ah.Core.Json;

namespace Ah.Hierarchy;

/// <summary>A registered work item: its number in the repo, its placement, and what only the registry owns.</summary>
public sealed record WorkRow(long N, string Team, string Stream, string Item, string? AddedAt, string? ExtId, string? ExtUrl, long UpdatedMs)
{
    /// <summary>Whether the placement is the repo's backlog.</summary>
    public bool IsBacklog => Team.Length == 0 && Stream == Backlog.Stream;

    /// <summary>Where the item sits, as a person reads it.</summary>
    public string Placement => IsBacklog ? Backlog.Stream : Team + "/" + Stream;
}

/// <summary>What a text names: one row, or the candidates when it names several; neither when it names none.</summary>
public sealed record WorkResolution(WorkRow? Row, IReadOnlyList<WorkRow> Candidates);

/// <summary>A work item with the state its plan log holds; a null plan means the placement no longer exists.</summary>
public sealed record WorkItemView(WorkRow Row, PlanItem? Plan);

/// <summary>One entry of an item's history: when, its type, its one-line title, its body (may be empty) and who wrote it.</summary>
public sealed record WorkEntry(long Ms, string Type, string Title, string Body, string By);

/// <summary>What an item points at (§8): a repo-relative path or a URL, its kind (<c>spec</c>, <c>file</c>, <c>url</c>), an optional title, and for a committed file its last commit.</summary>
public sealed record WorkAttachment(long N, string Ref, string Kind, string? Title, string? Sha);

/// <summary>One blocked-by link: <paramref name="Blocker"/> blocks <paramref name="Blocked"/>.</summary>
public sealed record WorkLink(long Blocker, long Blocked);

/// <summary>
/// The repo's items with their links, read once, and the one place that decides what is open, blocked, ready and under what (§7). A gone placement counts as closed.
/// Links gate nothing: this only answers.
/// </summary>
public sealed record WorkBoard(IReadOnlyList<WorkItemView> Items, IReadOnlyList<WorkLink> Links)
{
    private readonly Dictionary<long, WorkItemView> _byN = Items.ToDictionary(static v => v.Row.N);
    private readonly Dictionary<string, WorkItemView> _backlog = Items.Where(static v => v.Row.IsBacklog).ToDictionary(static v => v.Row.Item, StringComparer.Ordinal);

    /// <summary>The item numbered <paramref name="n"/>, or null.</summary>
    public WorkItemView? ViewOf(long n) => _byN.GetValueOrDefault(n);

    /// <summary>The backlog row <paramref name="item"/> (<c>b&lt;N&gt;</c>), or null.</summary>
    public WorkItemView? BacklogRow(string item) => _backlog.GetValueOrDefault(item);

    /// <summary>Whether an item is still to do; a gone placement is not.</summary>
    public static bool IsOpen(WorkItemView view) => view?.Plan is { State: not (PlanStates.Done or PlanStates.Dropped) };

    /// <summary>Whether an open item is held under one of <paramref name="names"/>, in <paramref name="team"/> or on the backlog.</summary>
    public static bool HeldBy(WorkItemView view, IReadOnlySet<string> names, string? team)
    {
        ArgumentNullException.ThrowIfNull(names);
        return view?.Plan is { State: not (PlanStates.Done or PlanStates.Dropped), Holder: { Length: > 0 } holder } && names.Contains(holder) && (view.Row.IsBacklog || view.Row.Team == team);
    }

    /// <summary>What blocks item <paramref name="n"/>, open or not.</summary>
    public IReadOnlyList<long> BlockedBy(long n) => [.. Links.Where(l => l.Blocked == n).Select(static l => l.Blocker)];

    /// <summary>What item <paramref name="n"/> blocks.</summary>
    public IReadOnlyList<long> Blocks(long n) => [.. Links.Where(l => l.Blocker == n).Select(static l => l.Blocked)];

    /// <summary>The blockers of item <paramref name="n"/> that are still open.</summary>
    public IReadOnlyList<long> OpenBlockers(long n) => [.. BlockedBy(n).Where(b => ViewOf(b) is { } v && IsOpen(v))];

    /// <summary>
    /// Work that can start, as <c>ah backlog next</c> reads it: not started or next, no open blocker, not held, not a project or goal, and for a backlog row not untriaged
    /// and with no child row under way (any state but not started, next, done or dropped): a held child holds its parent.
    /// </summary>
    public bool IsReady(WorkItemView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        return view.Plan is { State: PlanStates.NotStarted or PlanStates.Next, Holder: null or { Length: 0 } } plan
            && OpenBlockers(view.Row.N).Count == 0
            && !ProgressFormat.IsContainerKind(plan.Kind)
            && !(view.Row.IsBacklog && plan.Wave is null or "untriaged")
            && !Children(view.Row).Any(static c => c.Plan is { State: not (PlanStates.NotStarted or PlanStates.Next or PlanStates.Done or PlanStates.Dropped) });
    }

    /// <summary>The direct children of <paramref name="row"/>; only a backlog row has any.</summary>
    public IReadOnlyList<WorkItemView> Children(WorkRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.IsBacklog ? [.. Items.Where(v => v.Plan?.Parent == row.Item)] : [];
    }

    /// <summary>The numbers under <paramref name="row"/>, to any depth, not counting itself. A dropped level still holds what is under it.</summary>
    public IReadOnlySet<long> Under(WorkRow row)
    {
        var found = new HashSet<long>();
        var walk = new Stack<WorkRow>([row]);
        while (walk.TryPop(out var at))
        {
            foreach (var child in Children(at).Where(c => found.Add(c.Row.N)))
            {
                walk.Push(child.Row);
            }
        }

        return found;
    }

    /// <summary>The first ready item: backlog rows in <paramref name="backlogOrder"/>, then team items by number; each only if <paramref name="within"/> holds it, when given.</summary>
    public WorkItemView? Next(IEnumerable<string> backlogOrder, IReadOnlySet<long>? within)
    {
        var rows = backlogOrder.Select(BacklogRow).OfType<WorkItemView>().Concat(Items.Where(static v => !v.Row.IsBacklog));
        return rows.FirstOrDefault(v => (within is null || within.Contains(v.Row.N)) && IsReady(v));
    }
}

/// <summary>
/// The work registry's rules and reads (ah-work-items §4). Numbers and placements live in <c>work_*</c>; state is read from the plan log and
/// never copied. <c>query</c> is a read-only state database query, or the write transaction's.
/// </summary>
internal static partial class Work
{
    /// <summary>The op (in the <c>progress</c> domain) every registry write goes through.</summary>
    public const string WriteOp = "work-write";

    [GeneratedRegex(@"^[a-z][a-z0-9]{0,7}\z")]
    private static partial Regex PrefixPattern();

    [GeneratedRegex(@"^([a-z][a-z0-9]{0,7})-([0-9]{1,18})\z")]
    private static partial Regex IdPattern();

    /// <summary>The columns <see cref="Row"/> reads, in order.</summary>
    private const string Columns = "n, team, stream, item, added_at, ext_id, ext_url, updated_ms";

    /// <summary>The entry types an agent may record; the machine types (<c>state</c>, <c>assignment</c>, <c>field</c>, <c>message</c>) are derived, never recorded.</summary>
    public const string AgentTypeList = "coding|docs|testing|design|review|research|decision|handoff|note|description";

    public static readonly IReadOnlyList<string> AgentTypes = AgentTypeList.Split('|');

    public const int MaxTitle = 120;

    public const int MaxBodyBytes = 8 * 1024;

    private static readonly HashSet<string> StateEvents = new(StringComparer.Ordinal) { "start", "next", "review", "report", "approve", "testing", "done", "drop", "reopen","block", "wait-user", "unblock", "wait-orch" };

    private static readonly HashSet<string> Envelope = new(StringComparer.Ordinal) { "v", "ts", "team", "stream", "item", "event", "by", "session", "cause" };

    /// <summary>Text with every control character but newline and tab removed.</summary>
    public static string Clean(string text) => string.Concat(text.Where(static c => !char.IsControl(c) || c is '\n' or '\t'));

    /// <summary>
    /// The entries an item's plan rows read as (§6.1): nothing writes them for the log, so every present and future writer of a plan row shows.
    /// <paramref name="messageTitles"/>, given all the requests at once and asked only if there are any, names each; an event this build does not know reads as its name.
    /// </summary>
    public static IReadOnlyList<WorkEntry> PlanEntries(IReadOnlyList<JsonValue> rows, Func<IReadOnlyCollection<string>, Func<string, string>> messageTitles)
    {
        var fold = new PlanFold();
        var entries = new List<WorkEntry>();
        var requests = new HashSet<string>(StringComparer.Ordinal);
        string[] linked = [.. rows.Select(static r => ProgressFormat.PlanText(r, "ref")).OfType<string>().Distinct(StringComparer.Ordinal)];
        var messageTitle = linked.Length > 0 ? messageTitles(linked) : static r => r;
        foreach (var row in rows)
        {
            fold.Apply(row);
            var name = ProgressFormat.PlanText(row, "event") ?? string.Empty;
            var by = ProgressFormat.PlanText(row, "by") ?? string.Empty;
            var ms = DateTimeOffset.TryParse(ProgressFormat.PlanText(row, "ts"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var at) ? at.ToUnixTimeMilliseconds() : 0;
            string? Text(string key) => ProgressFormat.PlanText(row, key);
            switch (name)
            {
                case "add":
                    entries.Add(new(ms, "state", "created", string.Empty, by));
                    break;
                case var _ when StateEvents.Contains(name):
                    var state = ChecklistRules.DisplayState(Backlog.First(fold.State)?.State ?? string.Empty);
                    var cause = row.Member("cause") is { IsMap: true } c ? ProgressFormat.PlanText(c, "event") : null;
                    entries.Add(new(ms, "state", state + (Text("note") is { } note ? " " + note : string.Empty) + (cause is null ? string.Empty : " (" + cause + ")"), string.Empty, by));
                    break;
                case "assign":
                    entries.Add(new(ms, "assignment", $"assigned to {Text("holder")} by {by}", string.Empty, by));
                    break;
                case "note":
                case "todo":
                    // a cleared todo says nothing, and must not use up the tail
                    if (Text(name) is { Length: > 0 } said)
                    {
                        entries.Add(new(ms, "note", said, string.Empty, by));
                    }

                    break;
                case "ref":
                    if (Text("ref") is { } request && requests.Add(request))
                    {
                        entries.Add(new(ms, "message", messageTitle(request), string.Empty, by));
                    }

                    break;
                default:
                    entries.Add(new(ms, "field", FieldTitle(name, row), string.Empty, by));
                    break;
            }
        }

        return entries;
    }

    private static string FieldTitle(string name, JsonValue row)
    {
        var values = row.IsMap
            ? row.AsObject().Keys.Where(static k => !Envelope.Contains(k)).Select(k => row.Member(k) is { IsText: true } t ? t.AsString() : JsonText.Stringify(row.Member(k)!.Value, 0))
            : [];
        return string.Join(" ", values) is { Length: > 0 } value ? name + ": " + value : name;
    }

    public static bool ValidPrefix(string prefix) => PrefixPattern().IsMatch(prefix);

    /// <summary>
    /// The prefix a repo starts with, from its root's basename (the grandparent of a <c>.claude/ah2</c> dir, else the dir's own basename): two or more
    /// words give their initials, one word its first three letters; <c>w</c> when that fails the pattern.
    /// </summary>
    public static string DefaultPrefix(string dirKey)
    {
        var parts = dirKey.TrimEnd('/').Split('/');
        var hierarchy = parts.Length >= 3 && parts[^1] == "ah2" && parts[^2] == ".claude";
        var name = (hierarchy ? parts[^3] : parts[^1]).ToLowerInvariant();
        var words = name.Split(['-', '_', '.'], StringSplitOptions.RemoveEmptyEntries);
        var prefix = words.Length >= 2 ? string.Concat(words.Select(static w => w[0])) : words.Length == 1 ? words[0][..Math.Min(3, words[0].Length)] : string.Empty;
        return ValidPrefix(prefix) ? prefix : "w";
    }

    /// <summary>The repo's current prefix, or null before its first use.</summary>
    public static string? Prefix(Func<string, object?[], IReadOnlyList<object?[]>> query, string dirKey) =>
        query("SELECT prefix FROM work_repos WHERE hier_dir = ?1", [dirKey]) is { Count: > 0 } rows ? (string)rows[0][0]! : null;

    public static string Id(string prefix, long n) => prefix + "-" + n.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Every registered item of the repo, by number.</summary>
    public static IReadOnlyList<WorkRow> Rows(Func<string, object?[], IReadOnlyList<object?[]>> query, string dirKey) =>
        [.. query($"SELECT {Columns} FROM work_items WHERE hier_dir = ?1 ORDER BY n", [dirKey]).Select(Row)];

    /// <summary>The longest external id.</summary>
    public const int MaxExt = 128;

    /// <summary>The longest external URL.</summary>
    public const int MaxUrl = 512;

    /// <summary>Whether <paramref name="url"/> is an absolute http or https URL, the only scheme that may reach the UI (§9).</summary>
    public static bool ValidUrl(string url) =>
        url.Length <= MaxUrl && !url.Any(static c => char.IsControl(c) || char.IsWhiteSpace(c))
        && Uri.TryCreate(url, UriKind.Absolute, out var parsed) && parsed.Scheme is "http" or "https";

    /// <summary>The repo's attachments, of one item when <paramref name="n"/> is given.</summary>
    public static IReadOnlyList<WorkAttachment> Attachments(Func<string, object?[], IReadOnlyList<object?[]>> query, string dirKey, long? n) =>
        [.. query("SELECT n, ref, kind, title, sha FROM work_attachments WHERE hier_dir = ?1 AND (?2 IS NULL OR n = ?2) ORDER BY n, ts_ms, ref", [dirKey, n])
            .Select(static r => new WorkAttachment((long)r[0]!, (string)r[1]!, (string)r[2]!, r[3] as string, r[4] as string))];

    /// <summary>The item's description: the body of its newest <c>description</c> entry that has one, read past any timeline window. Null when there is none.</summary>
    public static string? Description(Func<string, object?[], IReadOnlyList<object?[]>> query, string dirKey, long n) =>
        query("SELECT body FROM work_log WHERE hier_dir = ?1 AND n = ?2 AND type = 'description' AND trim(body, char(32, 9, 10)) <> '' ORDER BY id DESC LIMIT 1", [dirKey, n]) is [var row] ? row[0] as string : null;

    /// <summary>Why <paramref name="cleaned"/> may not be an entry's body, or null when it may.</summary>
    public static string? BodyProblem(string cleaned) =>
        System.Text.Encoding.UTF8.GetByteCount(cleaned) > MaxBodyBytes ? $"the body is over {MaxBodyBytes / 1024} KiB; attach a file instead" : null;

    /// <summary>The line a verb that added an item with no description prints on stderr.</summary>
    public static string DescriptionNudge(string id) =>
        $"ah: {id} has no description; add one: ah work record {id} --type description --title Description --body-file - (see the write-ticket skill)";

    /// <summary>How many recorded entries each item has.</summary>
    public static IReadOnlyDictionary<long, long> LogCounts(Func<string, object?[], IReadOnlyList<object?[]>> query, string dirKey) =>
        query("SELECT n, COUNT(*) FROM work_log WHERE hier_dir = ?1 GROUP BY n", [dirKey]).ToDictionary(static r => (long)r[0]!, static r => (long)r[1]!);

    /// <summary>Whether any item of the directory already has a coding entry for this sha.</summary>
    public static bool CommitLogged(Func<string, object?[], IReadOnlyList<object?[]>> query, string dirKey, string sha) =>
        query("SELECT 1 FROM work_log WHERE hier_dir = ?1 AND type = 'coding' AND body = ?2 LIMIT 1", [dirKey, sha]).Count > 0;

    /// <summary>Every blocked-by link of the repo.</summary>
    public static IReadOnlyList<WorkLink> Links(Func<string, object?[], IReadOnlyList<object?[]>> query, string dirKey) =>
        [.. query("SELECT blocker_n, blocked_n FROM work_links WHERE hier_dir = ?1 ORDER BY blocked_n, blocker_n", [dirKey]).Select(static r => new WorkLink((long)r[0]!, (long)r[1]!))];

    /// <summary>
    /// The one resolver every verb uses (§4), in order: a work id under any prefix the repo has had; <c>b&lt;N&gt;</c>; <c>team/stream/slug</c> and
    /// <c>stream/slug</c>; a bare slug, the caller's team first and then the repo; an exact external id.
    /// </summary>
    public static WorkResolution Resolve(Func<string, object?[], IReadOnlyList<object?[]>> query, string dirKey, string text, string? callerTeam)
    {
        WorkResolution One(IEnumerable<WorkRow> rows) => rows.ToList() is { Count: 1 } one ? new(one[0], []) : new(null, []);

        if (IdPattern().Match(text) is { Success: true } id
            && query("SELECT 1 FROM work_prefixes WHERE hier_dir = ?1 AND prefix = ?2", [dirKey, id.Groups[1].Value]).Count > 0
            && One(query($"SELECT {Columns} FROM work_items WHERE hier_dir = ?1 AND n = ?2", [dirKey, long.Parse(id.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture)]).Select(Row)) is { Row: not null } byId)
        {
            return byId;
        }

        if (ProgressFormat.IsBacklogId(text)
            && One(query($"SELECT {Columns} FROM work_items WHERE hier_dir = ?1 AND team = '' AND stream = ?2 AND item = ?3", [dirKey, Backlog.Stream, text]).Select(Row)) is { Row: not null } byBacklog)
        {
            return byBacklog;
        }

        var parts = text.Split('/');
        var (team, stream, slug) = parts.Length switch
        {
            3 => (HierarchyModule.ProgressFolderOf(parts[0]), parts[1], parts[2]),
            2 => ((string?)null, parts[0], parts[1]),
            1 => (null, (string?)null, parts[0]),
            _ => (null, null, string.Empty),
        };
        var named = query($"SELECT {Columns} FROM work_items WHERE hier_dir = ?1 AND item = ?2 AND (?3 IS NULL OR team = ?3) AND (?4 IS NULL OR stream = ?4) ORDER BY n", [dirKey, slug, team, stream])
            .Select(Row).ToList();
        var mine = callerTeam is null ? [] : named.Where(r => r.Team == callerTeam).ToList();
        var candidates = mine.Count > 0 ? mine : named;
        if (candidates.Count == 1)
        {
            return new(candidates[0], []);
        }

        if (candidates.Count > 1)
        {
            return new(null, candidates);
        }

        return One(query($"SELECT {Columns} FROM work_items WHERE hier_dir = ?1 AND ext_id = ?2", [dirKey, text]).Select(Row));
    }

    /// <summary>
    /// Registers every item the repo's plans hold, in <c>AddedAt</c> order, and the backlog's. Idempotent. With <paramref name="known"/> (the
    /// registered <see cref="Key"/>s, or null to force the write) a pass that finds nothing new writes nothing.
    /// </summary>
    public static void Reconcile(IHierarchyProgress progress, StateCalls.IHierarchyStateCalls calls, string pool, IReadOnlySet<string>? known)
    {
        var rows = new List<(string Team, string Stream, PlanItem Item)>();
        var keys = calls.Progress("keys", new JsonObject { ["dir"] = pool });
        foreach (var team in keys.Member("teams") is { IsArray: true } teams ? teams.AsArray().Where(static t => t.IsText).Select(static t => t.AsString()) : [])
        {
            rows.AddRange(HierarchyModule.ChecklistEntries(progress, pool, team).Select(e => (team, e.Stream, e.Item)));
        }

        var missing = rows
            .Where(r => known is null || !known.Contains(Key(r.Team, r.Stream, r.Item.Item, r.Item.AddedAt)))
            .OrderBy(static r => r.Item.AddedAt, StringComparer.Ordinal).ThenBy(static r => r.Team, StringComparer.Ordinal).ThenBy(static r => r.Stream, StringComparer.Ordinal).ThenBy(static r => r.Item.Item, StringComparer.Ordinal)
            .ToList();
        if (known is not null && missing.Count == 0)
        {
            return;
        }

        var register = new JsonArray();
        foreach (var (team, stream, item) in missing)
        {
            register.Add(RegisterRow(team, stream, item.Item, item.AddedAt));
        }

        calls.Progress(WriteOp, new JsonObject { ["dir"] = pool, ["backfill"] = true, ["register"] = register });
    }

    /// <summary>What identifies one registration of a placement: a slug reused later has another time.</summary>
    public static string Key(string team, string stream, string item, string? addedAt) => team + "\n" + stream + "\n" + item + "\n" + addedAt;

    public static JsonObject RegisterRow(string team, string stream, string item, string? addedAt) =>
        new() { ["team"] = team, ["stream"] = stream, ["item"] = item, ["added_at"] = addedAt is null ? JsonValue.Null : addedAt };

    private static WorkRow Row(object?[] r) =>
        new((long)r[0]!, (string)r[1]!, (string)r[2]!, (string)r[3]!, r[4] as string, r[5] as string, r[6] as string, (long)r[7]!);
}
