using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Ah.Core.Json;

namespace Ah.Hierarchy;

/// <summary>
/// One planned item as the plan log folds it: its slug, state (done, in_progress, not_started, next, blocked, waiting_user or dropped),
/// the times of its events, the commits of its last <c>done</c> event, the issue its <c>link</c> named, and its priority
/// (<c>Rank</c>, lower is higher), grouping (<c>Area</c>) and holder when an event set them.
/// </summary>
public sealed record PlanItem(
    string Item,
    string Title,
    string State,
    string AddedAt,
    string? StartedAt,
    string? DoneAt,
    IReadOnlyList<string> Commits,
    string? Branch,
    int? Issue,
    string? Note,
    int? Rank = null,
    string? Area = null,
    string? Holder = null,
    string? Role = null,
    string? Todo = null,
    IReadOnlyList<string>? Refs = null,
    string? UpdatedAt = null,
    string? StateSince = null,
    string? Outcome = null,
    string? Size = null,
    string? Wave = null,
    IReadOnlyList<string>? Depends = null,
    IReadOnlyList<string>? Sources = null,
    string? Ext = null)
{
    /// <summary>The item's kind: <c>epic</c> or <c>deliverable</c>, or null for a task.</summary>
    public string? Kind { get; init; }

    /// <summary>The backlog id (<c>b&lt;N&gt;</c>) of the item's parent, or null.</summary>
    public string? Parent { get; init; }

    /// <summary>The <c>ts</c> of the item's latest override: a state row the Orchestrator wrote by hand (cause <c>manual</c>). Null when there is none.</summary>
    public string? OverrideAt { get; init; }

    /// <summary>The sha the item's <c>ah/&lt;slug&gt;</c> branch stood at when it was first dispatched (the <c>base</c> of its first row that has one); null for an item dispatched before it was recorded. A fact only: nothing closes on it.</summary>
    public string? Base { get; init; }

    /// <summary>The answers the item's latest question offers; null when it offers none.</summary>
    public IReadOnlyList<string>? Options { get; init; }

    /// <summary>The user's answer to the item's latest question; a later question clears it.</summary>
    public string? Answer { get; init; }

    /// <summary>The member (<c>name@team</c>) that asked the item's latest question, who also gets its answer; null when the Orchestrator alone does.</summary>
    public string? AskedFor { get; init; }

    /// <summary>How urgent the item's latest question is: <c>blocker</c>, <c>soon</c> or <c>eventually</c>; null (read as <c>soon</c>) when its wait-user row named none.</summary>
    public string? Class { get; init; }

    /// <summary>Who wrote the item's latest <c>wait-user</c> row (its <c>by</c>): the asker a Q&amp;A card names. Null before any.</summary>
    public string? AskedBy { get; init; }

    /// <summary>The note of the last caused <c>note</c> row on the item while it waited on the user; its question stays <see cref="Note"/>. A new question clears it.</summary>
    public string? HeldNote { get; init; }

    /// <summary>Whether the item has a DONE report standing (a <c>review</c> row, with or without a sha, and no failing verdict since): a reviewer's pass then approves the work and does not close it.</summary>
    public bool ReportedDone { get; init; }

    /// <summary>Whether the item is back in progress after a failing verdict (its <c>start</c> note begins <c>verdict </c>) or a failed ship gate (a <c>gate failed</c> note before the start): shown as rework.</summary>
    public bool Rework { get; init; }

    /// <summary>The cause of the item's latest <c>done</c> row (event <c>ship</c> for a ship's close, <c>manual</c> for <c>checklist done</c>); null when that row had none.</summary>
    public PlanCause? DoneCause { get; init; }

    /// <summary>Whether the item is in review because a reviewer's request opened on it (a <c>review</c> row with role <c>reviewer</c>), not because an older DONE report put it there.</summary>
    public bool Reviewing { get; init; }

    /// <summary>The requests whose DONE <c>report</c> row carried <c>at_base</c> (nothing committed); null when none (ah2-312 M3'').</summary>
    public IReadOnlyList<string>? AtBaseRefs { get; init; }

    /// <summary>The checklist item (<c>stream/id</c>) this item comes after, from its latest <c>after</c> row; null when none (an empty row clears it).</summary>
    public string? After { get; init; }

    /// <summary>Who wrote the item's <c>add</c> row (its <c>by</c>); null when the row named none.</summary>
    public string? AddedBy { get; init; }
}

/// <summary>One change the plan events imply, in the order the log holds them: <c>Kind</c> is added, priority, state, retitle or dropped, and <c>From</c>/<c>To</c> are the values before and after (null when there was none). <c>Seq</c> is the row's place among the stream's readable rows.</summary>
public sealed record PlanChange(string At, int Seq, string Item, string Kind, string? From, string? To, string By);

/// <summary>Why the transition table wrote a row: the triggering event's kind, its ref (request id, sha or member) and its time.</summary>
public sealed record PlanCause(string Event, string Ref, string At);

/// <summary>The states a planned item can be in.</summary>
internal static class PlanStates
{
    /// <summary>Added and not yet started.</summary>
    public const string NotStarted = "not_started";

    /// <summary>Started, or reopened.</summary>
    public const string InProgress = "in_progress";

    /// <summary>Finished, with the commits that finished it.</summary>
    public const string Done = "done";

    /// <summary>Abandoned.</summary>
    public const string Dropped = "dropped";

    /// <summary>Picked as the one to start next.</summary>
    public const string Next = "next";

    /// <summary>Held up by something other than the user.</summary>
    public const string Blocked = "blocked";

    /// <summary>Held up until the user answers.</summary>
    public const string WaitingUser = "waiting_user";

    /// <summary>The ball is in the Orchestrator's court.</summary>
    public const string WaitingOrch = "waiting_orch";

    /// <summary>The member reported DONE; the work waits on the Orchestrator.</summary>
    public const string Reported = "reported";

    /// <summary>A reviewer request on the item is open.</summary>
    public const string InReview = "in_review";

    /// <summary>A reviewer passed the work; it waits for ship.</summary>
    public const string Approved = "approved";

    /// <summary>A backlog row the Orchestrator marked as in a ship batch under QA.</summary>
    public const string Testing = "testing";
}

/// <summary>A stream's plan log folded: its items in the order they were added, and how many rows named an item that was never added.</summary>
public sealed record PlanState(IReadOnlyList<PlanItem> Items, int UnknownEvents);

/// <summary>An item line of an existing checklist read for <c>import</c>; <c>Unparsed</c> says why its commits were not taken (a range, or no title) and is null when the line read cleanly.</summary>
internal sealed record ImportLine(string Section, string Title, IReadOnlyList<string> Commits, string? DoneAt, string? Unparsed);

/// <summary>
/// The plan log: the rows a stream's <c>&lt;stream&gt;.plan.jsonl</c> holds, how they fold into items, how the Checklist block is
/// rendered from them, and how an existing checklist is read for import. Pure text in, text out.
/// </summary>
internal static partial class ProgressFormat
{
    internal const string PlanNotStarted = PlanStates.NotStarted;
    internal const string PlanInProgress = PlanStates.InProgress;
    internal const string PlanDone = PlanStates.Done;
    internal const string PlanNext = PlanStates.Next;
    internal const string PlanBlocked = PlanStates.Blocked;
    internal const string PlanWaitingUser = PlanStates.WaitingUser;
    internal const string PlanWaitingOrch = PlanStates.WaitingOrch;
    internal const string PlanInReview = PlanStates.InReview;
    internal const string PlanReported = PlanStates.Reported;
    internal const string PlanApproved = PlanStates.Approved;

    /// <summary>The stream that holds the loose ends of a team that belong to no stream; reachable only through the checklist core.</summary>
    internal const string RunStream = "_run";

    internal const int MaxTitle = 120;
    internal const int MaxNote = 300;
    internal const int MaxCommits = 8;

    internal static readonly IReadOnlyList<string> PlanEvents =
        ["add", "start", "done", "drop", "reopen", "testing", "retitle", "link", "note", "rank", "area", "assign", "next", "block", "wait-user", "unblock", "wait-orch", "role", "todo", "ref", "unref", "outcome", "size", "wave", "depend", "source", "ext", "kind", "parent", "review", "approve", "report", "after"];

    /// <summary>The kinds a plan item may have, with their rank: a parent link is valid only when the parent's rank is higher.</summary>
    internal static readonly IReadOnlyList<string> Kinds = ["task", "deliverable", "epic", "goal", "project"];

    /// <summary>Whether <paramref name="kind"/> only contains work: never handed out as next, and closed by hand only.</summary>
    internal static bool IsContainerKind(string? kind) => kind is "project" or "goal";

    /// <summary>The rank of <paramref name="kind"/>: project 5, goal 4, epic 3, deliverable 2, task (or none) 1.</summary>
    internal static int KindRank(string? kind) => kind switch
    {
        "project" => 5,
        "goal" => 4,
        "epic" => 3,
        "deliverable" => 2,
        _ => 1,
    };

    /// <summary>The sizes a backlog item may have.</summary>
    internal static readonly IReadOnlyList<string> Sizes = ["S", "M", "L"];

    /// <summary>The waves of the backlog, in list order.</summary>
    internal static readonly IReadOnlyList<string> Waves = ["now", "next", "later", "untriaged"];

    internal const int MaxLabel = 64;

    /// <summary>The classes a question for the user may have, most urgent first: a member cannot move until it is answered, it becomes a blocker if it waits, or it can wait.</summary>
    internal static readonly IReadOnlyList<string> Classes = ["blocker", "soon", "eventually"];

    /// <summary>The class a question has when none was named.</summary>
    internal const string DefaultClass = "soon";

    /// <summary>Where <paramref name="cls"/> sorts among <see cref="Classes"/>: blocker 0, soon (or none, or an unknown name) 1, eventually 2.</summary>
    internal static int ClassRank(string? cls) => cls switch
    {
        "blocker" => 0,
        "eventually" => 2,
        _ => 1,
    };

    /// <summary>The most answers a question offers: the answer form's letters a to d.</summary>
    internal const int MaxOptions = 4;

    /// <summary>The longest <c>answer</c> a plan row carries.</summary>
    internal const int MaxAnswerBytes = 2048;

    /// <summary>The start of the refusal of an answer to an item that no longer waits on the user.</summary>
    internal const string NotWaiting = "not-waiting";

    /// <summary>Why <paramref name="options"/> may not be a question's answers, or null when they may.</summary>
    internal static string? OptionsRefusal(JsonValue options)
    {
        if (!options.IsArray || options.AsArray().Count is 0 or > MaxOptions)
        {
            return $"a question takes 1 to {MaxOptions} options";
        }

        var labels = options.AsArray().Select(static o => o.IsText ? o.AsString() : string.Empty).ToList();
        return labels.Any(static l => string.IsNullOrWhiteSpace(l) || l.Length > MaxLabel) ? $"an option needs 1 to {MaxLabel} characters"
            : labels.Distinct(StringComparer.Ordinal).Count() != labels.Count ? "the options repeat a label"
            : null;
    }

    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]{0,63}\z")]
    private static partial Regex ItemSlugPattern();

    [GeneratedRegex(@"^[0-9a-f]{7,40}\z")]
    private static partial Regex ShaPattern();

    public static bool IsItemSlug(string text) => ItemSlugPattern().IsMatch(text);

    public static bool IsPlanSha(string text) => ShaPattern().IsMatch(text);

    /// <summary>Whether <paramref name="text"/> is a checklist key an <c>after</c> row may name: <c>&lt;stream&gt;/&lt;id&gt;</c>, a non-empty stream of at most <see cref="MaxLabel"/> characters and an item slug.</summary>
    internal static bool IsAfterKey(string text) =>
        text.IndexOf('/', StringComparison.Ordinal) is var slash and > 0 and <= MaxLabel && text.IndexOf('/', slash + 1) < 0 && IsItemSlug(text[(slash + 1)..]);

    /// <summary>Whether <paramref name="text"/> is a backlog id, <c>b&lt;N&gt;</c>.</summary>
    public static bool IsBacklogId(string text) => text.Length > 1 && text[0] == 'b' && text.Skip(1).All(char.IsAsciiDigit);

    public static string PlanTime(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    // ---------------------------------------------------------------- rows

    /// <summary>One plan row as the object appended to the log: the version, time, team, stream, item and event first, then what the event carries, then who wrote it.</summary>
    internal static JsonObject PlanRow(
        string ts, string team, string stream, string item, string eventName, string by,
        string? title = null, IReadOnlyList<string>? commits = null, string? branch = null, int? issue = null, string? note = null, string? session = null,
        int? rank = null, string? area = null, string? holder = null, string? role = null, string? todo = null, string? reference = null, string? source = null, string? parent = null,
        PlanCause? cause = null, string? baseSha = null, IReadOnlyList<string>? options = null, string? answer = null, string? askedFor = null, string? cls = null, string? after = null, bool atBase = false)
    {
        var row = new JsonObject { ["v"] = PlanVersion, ["ts"] = ts, ["team"] = team, ["stream"] = stream, ["item"] = item, ["event"] = eventName };
        if (title is not null)
        {
            row["title"] = title;
        }

        if (commits is { Count: > 0 })
        {
            var list = new JsonArray();
            foreach (var sha in commits)
            {
                list.Add(sha);
            }

            row["commits"] = list;
        }

        if (branch is not null)
        {
            row["branch"] = branch;
        }

        if (issue is { } number)
        {
            row["issue"] = number;
        }

        if (note is not null)
        {
            row["note"] = note;
        }

        if (rank is { } number1)
        {
            row["rank"] = number1;
        }

        if (area is not null)
        {
            row["area"] = area;
        }

        if (holder is not null)
        {
            row["holder"] = holder;
        }

        if (role is not null)
        {
            row["role"] = role;
        }

        if (todo is not null)
        {
            row["todo"] = todo;
        }

        if (reference is not null)
        {
            row["ref"] = reference;
        }

        if (source is not null)
        {
            row["source"] = source;
        }

        if (parent is not null)
        {
            row["parent"] = parent;
        }

        if (cause is not null)
        {
            row["cause"] = new JsonObject { ["event"] = cause.Event, ["ref"] = cause.Ref, ["at"] = cause.At };
        }

        if (baseSha is not null)
        {
            row["base"] = baseSha;
        }

        if (options is { Count: > 0 })
        {
            var labels = new JsonArray();
            foreach (var label in options)
            {
                labels.Add(label);
            }

            row["options"] = labels;
        }

        if (answer is not null)
        {
            row["answer"] = answer;
        }

        if (askedFor is not null)
        {
            row["asked_for"] = askedFor;
        }

        if (cls is not null)
        {
            row["class"] = cls;
        }

        if (after is not null)
        {
            row["after"] = after;
        }

        if (atBase)
        {
            row["at_base"] = true;
        }

        row["by"] = by;
        row["session"] = session is null ? JsonValue.Null : JsonValue.FromString(session);
        return row;
    }

    private static string? Text(JsonValue row, string key) => row.Member(key) is { IsText: true } v ? v.AsString() : null;

    /// <summary>
    /// Why <paramref name="row"/> may not be appended to a log whose folded state is <paramref name="state"/>, or null when it may.
    /// <paramref name="import"/> lets a <c>done</c> row carry no commits (a line whose commits could not be read).
    /// </summary>
    internal static string? RefusePlanRow(JsonValue row, PlanState state, bool import)
    {
        var eventName = Text(row, "event");
        var item = Text(row, "item");
        if (eventName is null || !PlanEvents.Contains(eventName, StringComparer.Ordinal))
        {
            return $"event {eventName ?? "(none)"} is not one of {string.Join(", ", PlanEvents)}";
        }

        if (item is null || !IsItemSlug(item))
        {
            return $"item {item ?? "(none)"} is not a slug (lowercase letters, digits and dashes, at most 64 characters, starting with a letter or digit)";
        }

        var title = Text(row, "title");
        var note = Text(row, "note");
        if (title is { Length: > MaxTitle })
        {
            return $"the title is longer than {MaxTitle} characters";
        }

        if (note is { Length: > MaxNote })
        {
            return $"the note is longer than {MaxNote} characters";
        }

        if (row.Member("cause") is { IsNull: false } cause
            && !(cause.IsMap && Text(cause, "event") is { Length: > 0 and <= MaxLabel } && Text(cause, "ref") is { Length: <= MaxLabel } && Text(cause, "at") is { Length: > 0 and <= MaxLabel }))
        {
            return $"cause needs an event and a time of 1 to {MaxLabel} characters, and a ref of at most {MaxLabel}";
        }

        if (row.Member("base") is { IsNull: false } baseSha && !(baseSha.IsText && IsPlanSha(baseSha.AsString())))
        {
            return "base needs a commit of 7 to 40 lowercase hex digits";
        }

        if (row.Member("options") is { IsNull: false } options)
        {
            if (eventName != "wait-user")
            {
                return "options go only on a wait-user row";
            }

            if (OptionsRefusal(options) is { } refusedOptions)
            {
                return refusedOptions;
            }
        }

        if (row.Member("asked_for") is { IsNull: false } askedFor
            && !(eventName == "wait-user" && askedFor.IsText && HierarchyModule.IsMemberAddress(askedFor.AsString())))
        {
            return "asked_for goes only on a wait-user row, as <name>@<team>";
        }

        if (row.Member("class") is { IsNull: false } cls
            && !(eventName == "wait-user" && cls.IsText && Classes.Contains(cls.AsString(), StringComparer.Ordinal)))
        {
            return $"class goes only on a wait-user row, as one of {string.Join(", ", Classes)}";
        }

        var answer = row.Member("answer") is { IsNull: false } a0 ? a0 : (JsonValue?)null;
        if (answer is { } given)
        {
            // a wait-orch row answers; a drop row is the user's dismissal (spec ah-ui-answer §6)
            if (eventName is not ("wait-orch" or "drop") || !given.IsText || string.IsNullOrWhiteSpace(given.AsString()) || Encoding.UTF8.GetByteCount(given.AsString()) > MaxAnswerBytes)
            {
                return $"answer goes only on a wait-orch or drop row, 1 to {MaxAnswerBytes} bytes";
            }

            // the exactly-once guard: this runs on the folded log inside plan-append, under the domain lock
            if (state.Items.FirstOrDefault(i => i.Item == item) is { State: not PlanWaitingUser } answered)
            {
                return $"{NotWaiting}: item {item} is {answered.State}";
            }
        }

        var exists = state.Items.Any(i => i.Item == item);
        if (eventName == "add")
        {
            return exists ? $"item {item} already exists" : string.IsNullOrWhiteSpace(title) ? "add needs a title" : null;
        }

        if (!exists)
        {
            return $"item {item} was never added";
        }

        switch (eventName)
        {
            case "retitle" when string.IsNullOrWhiteSpace(title):
                return "retitle needs a title";
            case "note" when string.IsNullOrWhiteSpace(note):
                return "note needs a note";
            // an answer row keeps the question as the note, so it brings none
            case "block" or "wait-user" or "wait-orch" when string.IsNullOrWhiteSpace(note) && answer is null:
                return eventName + " needs a note";
            case "rank" when !(row.Member("rank") is { IsNumber: true } r && r.AsNumber() >= 0 && r.AsNumber() == Math.Floor(r.AsNumber()) && r.AsNumber() <= int.MaxValue):
                return "rank needs a whole number, 0 or more";
            case "area" when !(Text(row, "area") is { Length: > 0 and <= MaxLabel } a && !string.IsNullOrWhiteSpace(a)):
                return $"area needs a name of 1 to {MaxLabel} characters";
            case "assign" when !(Text(row, "holder") is { Length: > 0 and <= MaxLabel } h && !string.IsNullOrWhiteSpace(h)):
                return $"assign needs a holder of 1 to {MaxLabel} characters";
            case "role" when !(Text(row, "role") is { Length: > 0 and <= MaxLabel } r && !string.IsNullOrWhiteSpace(r)):
                return $"role needs a name of 1 to {MaxLabel} characters";
            case "todo" when !(Text(row, "todo") is { Length: <= MaxNote } t && (t.Length == 0 || !string.IsNullOrWhiteSpace(t))):
                return $"todo needs a line of at most {MaxNote} characters (empty clears it)";
            case "ref" or "unref" when !(Text(row, eventName) is { Length: > 0 and <= MaxLabel } f && IsItemSlug(f)):
                return $"{eventName} needs a request id or commit of 1 to {MaxLabel} letters, digits and dashes";
            case "link" when !(row.Member("issue") is { IsNumber: true } n && n.AsNumber() >= 1 && n.AsNumber() == Math.Floor(n.AsNumber())):
                return "link needs a positive whole issue number";
            case "ext" when row.Member("clear") is { IsTruthy: true }:
                break;
            case "outcome" or "source" or "ext" when !(Text(row, eventName) is { Length: > 0 and <= MaxNote } o && !string.IsNullOrWhiteSpace(o)):
                return $"{eventName} needs a line of 1 to {MaxNote} characters";
            case "size" when !Sizes.Contains(Text(row, "size") ?? string.Empty, StringComparer.Ordinal):
                return $"size needs one of {string.Join(", ", Sizes)}";
            case "wave" when !Waves.Contains(Text(row, "wave") ?? string.Empty, StringComparer.Ordinal):
                return $"wave needs one of {string.Join(", ", Waves)}";
            case "depend" when !(row.Member("depends") is { IsArray: true } deps && deps.AsArray().All(d => d.IsText && IsItemSlug(d.AsString()))):
                return "depend needs a list of item ids";
            case "kind" when !Kinds.Contains(Text(row, "kind") ?? string.Empty, StringComparer.Ordinal):
                return $"kind needs one of {string.Join(", ", Kinds)}";
            case "parent" when !(Text(row, "parent") is { } p && (p.Length == 0 || IsBacklogId(p))):
                return "parent needs a backlog id (b<N>) or empty";
            case "after" when !(Text(row, "after") is { } a && (a.Length == 0 || IsAfterKey(a))):
                return "after needs an item as <stream>/<id>, or empty";
            case "done":
                var commits = row.Member("commits") is { IsArray: true } list ? list.AsArray().Select(c => c.IsText ? c.AsString() : string.Empty).ToList() : [];
                if (commits.Count == 0 && !import)
                {
                    return "done needs at least one commit";
                }

                if (commits.Count > MaxCommits)
                {
                    return $"done takes at most {MaxCommits} commits";
                }

                var bad = commits.FirstOrDefault(c => !IsPlanSha(c));
                return bad is null ? null : $"commit {(bad.Length == 0 ? "(empty)" : bad)} is not 7 to 40 lowercase hex digits";
        }

        return null;
    }

    // ---------------------------------------------------------------- fold

    /// <summary>
    /// The items a plan log folds to. The last applicable event wins; a row that is not JSON, or is not an object, or names no known
    /// event is skipped; a row for an item that was never added is ignored and counted in <c>UnknownEvents</c>.
    /// </summary>
    internal static PlanState FoldPlan(string? jsonl)
    {
        var fold = new PlanFold();
        fold.ApplyText(jsonl);
        return fold.State;
    }

    internal static string? PlanText(JsonValue row, string key) => Text(row, key);

    // ---------------------------------------------------------------- the Checklist block

    /// <summary>
    /// The text of the Checklist block for <paramref name="plan"/>: the heading and the three lists in today's checkbox line form,
    /// each item in the order it was added, dropped items left out, and a blank line after the last list.
    /// </summary>
    internal static string RenderChecklistBlock(PlanState plan)
    {
        var sb = new StringBuilder("## Checklist\n");
        void Section(string heading, string[] states, Func<PlanItem, string> line)
        {
            sb.Append("### ").Append(heading).Append('\n');
            foreach (var item in plan.Items.Where(i => states.Contains(i.State, StringComparer.Ordinal)))
            {
                sb.Append(line(item)).Append('\n');
            }

            sb.Append('\n');
        }

        Section("Done", [PlanDone], static i => $"- [x] {i.Title}{(i.Commits.Count > 0 ? $" ({string.Join(", ", i.Commits)})" : string.Empty)}{(i.DoneAt is null ? string.Empty : $" (done {i.DoneAt})")}");
        Section("In progress", [PlanInProgress, PlanBlocked, PlanWaitingUser, PlanWaitingOrch, PlanReported, PlanInReview, PlanApproved], static i => $"- [ ] {i.Title}{(i.Answer is null ? string.Empty : $" (answered: {i.Answer.ReplaceLineEndings(" ")})")}");
        Section("Not started", [PlanNotStarted, PlanNext], static i => $"- [ ] {i.Title}");
        return sb.ToString();
    }

    /// <summary>
    /// <paramref name="text"/> with its Checklist block (from the <c>## Checklist</c> heading up to the next <c>## </c> heading)
    /// replaced by the one rendered from <paramref name="plan"/> and its <c>Updated:</c> line set to <paramref name="updated"/>;
    /// every other byte is kept. With <paramref name="optIn"/> the version line becomes the plan marker, added when there is none.
    /// Null when the text has no Checklist block.
    /// </summary>
    internal static string? WithRenderedChecklist(string text, PlanState plan, string updated, bool optIn)
    {
        var lines = text.Split('\n').ToList();
        var start = lines.FindIndex(static l => l.TrimEnd().Equals("## Checklist", StringComparison.Ordinal));
        if (start < 0)
        {
            return null;
        }

        var next = lines.FindIndex(start + 1, static l => l.StartsWith("## ", StringComparison.Ordinal));
        var end = next < 0 ? lines.Count : next;
        var block = RenderChecklistBlock(plan).TrimEnd('\n').Split('\n').ToList();
        block.Add(string.Empty);
        lines.RemoveRange(start, end - start);
        lines.InsertRange(start, block);
        var stamp = lines.FindIndex(static l => l.StartsWith("Updated:", StringComparison.Ordinal));
        if (stamp >= 0 && stamp < start)
        {
            lines[stamp] = "Updated: " + updated;
        }

        if (optIn)
        {
            if (ReadMarker(text).Present)
            {
                lines[0] = PlanMarker();
            }
            else
            {
                lines.Insert(0, PlanMarker());
            }
        }

        return string.Join('\n', lines);
    }

    /// <summary>Whether the Checklist block of <paramref name="text"/> is what <paramref name="plan"/> renders to.</summary>
    internal static bool ChecklistMatches(string text, PlanState plan) =>
        WithRenderedChecklist(text, plan, "-", optIn: false) is { } rendered
        && BlockOf(rendered) == BlockOf(text);

    private static string? BlockOf(string text)
    {
        var lines = text.Split('\n');
        var start = Array.FindIndex(lines, static l => l.TrimEnd().Equals("## Checklist", StringComparison.Ordinal));
        if (start < 0)
        {
            return null;
        }

        var next = Array.FindIndex(lines, start + 1, static l => l.StartsWith("## ", StringComparison.Ordinal));
        return string.Join('\n', lines[start..(next < 0 ? lines.Length : next)]).TrimEnd();
    }

    // ---------------------------------------------------------------- import

    [GeneratedRegex(@"\(([^()]*)\)")]
    private static partial Regex ParenGroup();

    [GeneratedRegex(@"^done[ \t]+(.+)\z")]
    private static partial Regex DoneGroup();

    [GeneratedRegex(@"[ \t]+")]
    private static partial Regex Blanks();

    /// <summary>The slug for an imported title: lowercase, anything that is not a letter or digit becomes a dash, at most 48 characters, <c>-2</c>, <c>-3</c> for a slug already taken.</summary>
    internal static string ImportSlug(string title, ISet<string> taken)
    {
        var sb = new StringBuilder();
        foreach (var ch in title.ToLowerInvariant())
        {
            var keep = ch is (>= 'a' and <= 'z') or (>= '0' and <= '9');
            if (keep)
            {
                sb.Append(ch);
            }
            else if (sb.Length > 0 && sb[^1] != '-')
            {
                sb.Append('-');
            }
        }

        var slug = sb.ToString().Trim('-');
        slug = slug.Length > 48 ? slug[..48].Trim('-') : slug;
        if (slug.Length == 0)
        {
            slug = "item";
        }

        var candidate = slug;
        for (var n = 2; !taken.Add(candidate); n++)
        {
            candidate = $"{slug}-{n}";
        }

        return candidate;
    }

    /// <summary>
    /// One <see cref="ImportLine"/> per item line of a checklist, plain <c>- </c> lines included. The commits of a line are the hex
    /// words of 7 to 40 characters inside its parentheses and its time is its <c>(done ISO)</c>; a line with a range, or no title,
    /// is taken with its state and no commits and says why in <c>Unparsed</c>.
    /// </summary>
    internal static IReadOnlyList<ImportLine> ReadImportLines(string text)
    {
        var lines = new List<ImportLine>();
        foreach (var item in ReadItems(text, acceptPlain: true))
        {
            var commits = new List<string>();
            string? doneAt = null;
            string? unparsed = null;
            var title = item.Text;
            foreach (Match group in ParenGroup().Matches(item.Text))
            {
                var inner = group.Groups[1].Value.Trim();
                if (DoneGroup().Match(inner) is { Success: true } done)
                {
                    if (DateTimeOffset.TryParse(done.Groups[1].Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at))
                    {
                        doneAt = PlanTime(at);
                        title = title.Replace(group.Value, string.Empty, StringComparison.Ordinal);
                    }

                    continue;
                }

                var words = inner.Split([',', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                if (words.Length == 0 || !words.All(static w => IsPlanSha(w) || w.Contains("..", StringComparison.Ordinal) && w.Split("..").All(static p => IsPlanSha(p))))
                {
                    continue;
                }

                if (words.Any(static w => w.Contains("..", StringComparison.Ordinal)))
                {
                    unparsed = "a commit range: " + inner;
                }
                else
                {
                    commits.AddRange(words);
                }

                title = title.Replace(group.Value, string.Empty, StringComparison.Ordinal);
            }

            title = Blanks().Replace(title, " ").Trim();
            if (title.Length == 0)
            {
                title = "(untitled)";
                unparsed = "no title";
            }

            if (title.Length > MaxTitle)
            {
                title = title[..MaxTitle];
            }

            lines.Add(new ImportLine(item.Section, title, unparsed is null ? [.. commits.Take(MaxCommits)] : [], doneAt, unparsed));
        }

        return lines;
    }
}
