namespace Ah.Hierarchy;

public static partial class HierarchyModule
{
    /// <summary>How long a queued item, an idle member and the absence of a dispatch must all have lasted before a queue stall is raised, and how long an item may wait on the Orchestrator.</summary>
    public const double QueueStallMs = 600_000;

    /// <summary>How long after it was created an Orchestrator request with no checklist item may go unlinked before <c>unlinked-dispatch</c> tells of it.</summary>
    public const double UnlinkedDispatchGraceMs = 120_000;

    /// <summary>How old an unlinked Orchestrator request may be and still be told of: older ones are out of the scan.</summary>
    public const double UnlinkedDispatchWindowMs = 86_400_000;

    /// <summary>The least time between two <c>unlinked-dispatch</c> mails to one session.</summary>
    public const double UnlinkedDispatchCooldownMs = 900_000;

    /// <summary>How many request ids one <c>unlinked-dispatch</c> mail lists before "and N more".</summary>
    public const int UnlinkedDispatchListCap = 5;

    /// <summary>The checklist of a team: every item of every stream's plan log, and of the team's loose-end stream.</summary>
    public static IReadOnlyList<ChecklistEntry> ChecklistEntries(IHierarchyProgress progress, string pool, string team)
    {
        ArgumentNullException.ThrowIfNull(progress);
        var streams = progress.StreamNames(pool, team).Append(ProgressFormat.RunStream).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        var entries = new List<ChecklistEntry>();
        foreach (var stream in streams)
        {
            if (progress.ReadPlan(pool, team, stream) is { } plan)
            {
                entries.AddRange(plan.Items.Select(i => new ChecklistEntry(stream, i)));
            }
        }

        return ChecklistRules.WithWaiting(entries);
    }

    /// <summary><paramref name="entries"/> with each one's <see cref="ChecklistEntry.WaitingOn"/> computed from the others, as <see cref="ChecklistEntries"/> returns them.</summary>
    public static IReadOnlyList<ChecklistEntry> ChecklistWithWaiting(IReadOnlyList<ChecklistEntry> entries) => ChecklistRules.WithWaiting(entries);

    /// <summary>The queued items a live idle member of their role could take now, per the stall rules.</summary>
    public static IReadOnlyList<QueueEntry> QueueStalled(
        IReadOnlyList<ChecklistEntry> entries, IReadOnlyList<QueueMember> members, IReadOnlyList<QueueDispatch> dispatches, double nowMs, double thresholdMs = QueueStallMs) =>
        ChecklistRules.Stalled(entries, members, dispatches, nowMs, thresholdMs, thresholdMs, thresholdMs);

    /// <summary>The queued items past <paramref name="thresholdMs"/> whose role has no live member while the team is under <paramref name="budget"/> live members (null: no limit) and none of the role was spawned within <paramref name="thresholdMs"/>.</summary>
    public static IReadOnlyList<QueueEntry> QueueUnstaffed(
        IReadOnlyList<ChecklistEntry> entries, IReadOnlyList<QueueMember> members, IReadOnlyList<QueueDispatch> dispatches, double nowMs, int? budget, double thresholdMs = QueueStallMs, double? recentSpawnMs = null) =>
        ChecklistRules.Unstaffed(entries, members, dispatches, nowMs, thresholdMs, budget, recentSpawnMs ?? thresholdMs);

    /// <summary>The queue line for a queued item whose role has no live member: <c>spawn &lt;role&gt; for &lt;item&gt;</c>.</summary>
    public static string ChecklistSpawnLine(string role, string itemKey) => $"spawn {role} for {itemKey}";

    /// <summary>How many of <paramref name="members"/> are live: the team's count against its session budget.</summary>
    public static int QueueTeamLive(IReadOnlyList<QueueMember> members) => ChecklistRules.TeamLive(members);

    /// <summary>Every queued item with the members of its role idle now and the last dispatch to that role; <see cref="QueueStalled"/> is the subset past <see cref="QueueStallMs"/>.</summary>
    public static IReadOnlyList<QueueEntry> QueueAll(IReadOnlyList<ChecklistEntry> entries, IReadOnlyList<QueueMember> members, IReadOnlyList<QueueDispatch> dispatches, double nowMs) =>
        ChecklistRules.Queued(entries, members, dispatches, nowMs);

    /// <summary>Whether <see cref="QueueAll"/> can list anything for <paramref name="entries"/>: it reads only items in the <c>next</c> state, so with none the member and dispatch facts need not be read.</summary>
    public static bool QueueMayHaveItems(IReadOnlyList<ChecklistEntry> entries) => entries.Any(static e => e.Item.State == PlanStates.Next);

    /// <summary>The items that have waited on the Orchestrator for at least <paramref name="thresholdMs"/>.</summary>
    public static IReadOnlyList<ChecklistEntry> ChecklistWaitingOnOrchestrator(IReadOnlyList<ChecklistEntry> entries, double nowMs, double thresholdMs = QueueStallMs) =>
        ChecklistRules.WaitingOnOrchestrator(entries, nowMs, thresholdMs);

    /// <summary>The lines <c>ah checklist show</c> prints for <paramref name="entries"/>, <paramref name="stalled"/> items first after the ones waiting on the Orchestrator.</summary>
    public static IReadOnlyList<string> ChecklistShow(IReadOnlyList<ChecklistEntry> entries, IReadOnlyList<QueueEntry> stalled, double nowMs, bool all) =>
        ChecklistRules.Show(entries, stalled.Select(static s => s.Entry.Item.Item).ToHashSet(StringComparer.Ordinal), nowMs, all);

    /// <summary>The items <c>show</c> lists, in its order: every item with <paramref name="all"/>, else the open ones.</summary>
    public static IReadOnlyList<ChecklistEntry> ChecklistShown(IReadOnlyList<ChecklistEntry> entries, bool all) =>
        ChecklistRules.Shown(entries, new HashSet<string>(), all);

    /// <summary>The item a repeated <c>add</c> of <paramref name="what"/> to <paramref name="stream"/> means: the same live title added within the last ten minutes, or null.</summary>
    public static ChecklistEntry? ChecklistRecentTwin(IReadOnlyList<ChecklistEntry> entries, string stream, string what, double nowMs) =>
        ChecklistRules.RecentTwin(entries, stream, what, nowMs);

    /// <summary>The item id for a new item titled <paramref name="what"/>: a slug of the title, made unique among <paramref name="taken"/>, which gains it.</summary>
    public static string ChecklistSlug(string what, ISet<string> taken) => ProgressFormat.ImportSlug(what, taken);

    /// <summary>The classes a question for the user may have, most urgent first: <c>blocker</c>, <c>soon</c>, <c>eventually</c>.</summary>
    public static IReadOnlyList<string> ChecklistClasses => ProgressFormat.Classes;

    /// <summary>The class of <paramref name="item"/>'s latest question, <c>soon</c> when it named none.</summary>
    public static string ChecklistClassOf(PlanItem item) => ChecklistClassOf(item?.Class);

    /// <summary><paramref name="cls"/> as a class name: <c>soon</c> when it is null or not one of <see cref="ChecklistClasses"/>.</summary>
    public static string ChecklistClassOf(string? cls) => cls is not null && ProgressFormat.Classes.Contains(cls, StringComparer.Ordinal) ? cls : ProgressFormat.DefaultClass;

    /// <summary>Where <paramref name="cls"/> sorts: blocker 0, soon (or none) 1, eventually 2.</summary>
    public static int ChecklistClassRank(string? cls) => ProgressFormat.ClassRank(cls);

    /// <summary>The name <c>show</c> gives a stored state: <c>queued</c>, <c>dispatched</c>, <c>waiting-orch</c>, or the state itself.</summary>
    public static string ChecklistDisplayState(string state) => ChecklistRules.DisplayState(state);

    /// <summary>How a person reads <paramref name="item"/>'s state: rework and reported are spelled out.</summary>
    public static string ChecklistDisplayLabel(PlanItem item) => ChecklistRules.DisplayState(item);

    /// <summary>The id prefix of an item <c>ah msg new</c> added for a request sent without <c>--item</c>. A name only: an ordinary add can make the same prefix, so it marks nothing.</summary>
    public const string ChecklistAutoPrefix = "auto-";

    /// <summary>The <c>source</c> line that marks an item <c>ah msg new</c> added for a request sent without <c>--item</c>. No verb writes a <c>source</c> event from the command line.</summary>
    public const string ChecklistAutoSource = "auto-created by ah msg new";

    /// <summary>The <c>source</c> event that marks <paramref name="itemId"/> as added by <c>ah msg new</c>.</summary>
    public static PlanInput ChecklistAutoMark(string itemId) => new(itemId, "source") { Source = ChecklistAutoSource };

    /// <summary>Whether <paramref name="item"/> is one <c>ah msg new</c> added for a request sent without <c>--item</c> (it carries <see cref="ChecklistAutoSource"/>): its ref yields to a real item.</summary>
    public static bool ChecklistIsAuto(PlanItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return (item.Sources ?? []).Contains(ChecklistAutoSource, StringComparer.Ordinal);
    }

    /// <summary>An auto item dropped because its request's ref moved to a real item: it no longer answers for that request.</summary>
    public static bool ChecklistSuperseded(ChecklistEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return ChecklistIsAuto(entry.Item) && entry.Item.State == PlanStates.Dropped;
    }

    /// <summary>The entry that lists <paramref name="requestId"/> among its refs, one not superseded first, or null.</summary>
    public static ChecklistEntry? ChecklistItemOfRequest(IReadOnlyList<ChecklistEntry> entries, string requestId) =>
        entries.Where(e => (e.Item.Refs ?? []).Contains(requestId, StringComparer.Ordinal)).OrderBy(ChecklistSuperseded).FirstOrDefault();

    /// <summary>
    /// The item <paramref name="id"/> names among <paramref name="entries"/>: a bare item id, or <c>stream/id</c> when the same id lives in more
    /// than one stream. The error reads without a verb prefix; null on success.
    /// </summary>
    public static string? ChecklistResolve(IReadOnlyList<ChecklistEntry> entries, string id, out ChecklistEntry? entry)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var slash = id.IndexOf('/', StringComparison.Ordinal);
        var stream = slash > 0 ? id[..slash] : null;
        var item = slash > 0 ? id[(slash + 1)..] : id;
        var found = entries.Where(e => e.Item.Item == item && (stream is null || e.Stream == stream)).ToList();
        entry = found.Count == 1 ? found[0] : null;
        return found.Count switch
        {
            0 => $"no item {id}",
            1 => null,
            _ => $"item {id} is in more than one stream ({string.Join(", ", found.Select(static e => e.Stream + "/" + e.Item.Item))}); name it as <stream>/<id>",
        };
    }

    /// <summary>The leading tag of a reply that makes it a question for the Orchestrator, or <c>report</c> for any other text.</summary>
    public static string ChecklistReplyKind(string? text)
    {
        var first = FirstReplyLine(text);
        foreach (var tag in new[] { "NEEDS-DECISION", "BLOCKED", "QUESTION", "STATUS" })
        {
            if (StartsWithTag(first, tag))
            {
                return tag;
            }
        }

        return "report";
    }

    /// <summary>Whether a reply says its work is finished: it opens with <c>DONE</c> and that line does not say more is coming.</summary>
    public static bool ChecklistReplyIsDone(string? text) => FirstReplyLine(text) is var line && StartsWithTag(line, "DONE") && !MoreToCome().IsMatch(line);

    /// <summary>A <c>DONE</c> line about a part of the work, so not a finish: it names a step that is not the last (<c>step 1</c>, not <c>step 2 of 2</c>), or says work goes on.</summary>
    private static bool IsPartialDone(string line) => StartsWithTag(line, "DONE") && MoreToCome().IsMatch(line);

    [System.Text.RegularExpressions.GeneratedRegex("\\b(?:(?:step|part|phase|stage|piece)\\s+(\\d+)\\b(?!\\s*(?:of|/)\\s*\\1\\b)|continuing|continues|still running|(?<!\\b(?:no|not|nothing)\\s+)in progress|to follow|(?<!\\bno\\s+)more to come)\\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex MoreToCome();

    /// <summary>
    /// Whether acking a reply settles what it was waiting for: only a <c>STATUS</c> or <c>PASS</c> verdict, or a reply that opens with no verdict word
    /// at all (a plain reply). Any other leading ALL-CAPS word (FAIL, NEEDS-*, BLOCKED, CHANGES REQUIRED, one not known yet) leaves something to answer.
    /// </summary>
    private static bool AckSettlesReply(string? text) =>
        LineSettlesAck(FirstReplyLine(text))
        && LineSettlesAck((text ?? string.Empty).Split('\n').Select(static l => l.Trim()).FirstOrDefault(static l => l.Length > 0) ?? string.Empty);

    private static bool LineSettlesAck(string line)
    {
        var first = line.TrimStart(' ', '\t', '#', '>', '*', '-', '_', '`');
        if (IsPartialDone(first))
        {
            return true;
        }

        var parts = first.Split([' ', '\t', ':'], 2);
        var word = parts[0].Trim('*', '_', '`', '.', ',', ';', '!', '?', '(', ')', '[', ']');
        if (word.Equals("STATUS", StringComparison.OrdinalIgnoreCase) || word.Equals("PASS", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var changesRequired = word.Equals("CHANGES", StringComparison.OrdinalIgnoreCase)
            && parts.Length > 1 && parts[1].TrimStart(' ', '*', '_', '`').StartsWith("REQUIRED", StringComparison.OrdinalIgnoreCase);
        if (changesRequired || word.StartsWith("NEEDS-", StringComparison.OrdinalIgnoreCase) || KnownVerdictWords.Contains(word))
        {
            return false;
        }

        return word.Length < 3 || !word.All(static c => c is (>= 'A' and <= 'Z') or '-');
    }

    private static readonly HashSet<string> KnownVerdictWords = new(["FAIL", "PARTIAL", "BLOCKED", "QUESTION", "VERDICT"], StringComparer.OrdinalIgnoreCase);

    private static string FirstReplyLine(string? text) =>
        (text ?? string.Empty).Split('\n').Select(static l => l.Trim()).Where(static l => l.Length > 0 && l[0] != '#').Select(static l => WithoutStatusLabel(SlotKey().Replace(l.TrimStart('-', '*', ' ').Trim(), string.Empty))).FirstOrDefault(static l => l.Length > 0) ?? string.Empty;

    /// <summary>A <c>status:</c> label in front of a word that is the report's real status reads as that word: DONE, a word of <see cref="BackTags"/>, or NEEDS-*. Any other <c>status:</c> line is a STATUS reply.</summary>
    private static string WithoutStatusLabel(string line)
    {
        if (StatusLabel().Match(line) is not { Success: true } label)
        {
            return line;
        }

        var rest = line[label.Length..];
        bool Opens(string tag) => rest.StartsWith(tag, StringComparison.OrdinalIgnoreCase) && (rest.Length == tag.Length || !(char.IsLetterOrDigit(rest[tag.Length]) || rest[tag.Length] == '-'));
        return Opens("DONE") || BackTags.Any(Opens) || (rest.StartsWith("NEEDS-", StringComparison.OrdinalIgnoreCase) && rest.Length > 6 && char.IsLetter(rest[6])) ? rest : line;
    }

    private static bool StartsWithTag(string line, string tag) =>
        line.StartsWith(tag, StringComparison.OrdinalIgnoreCase) && (line.Length == tag.Length || !char.IsLetterOrDigit(line[tag.Length]));

    [System.Text.RegularExpressions.GeneratedRegex("^\\[[0-9]+\\][ \t]*[A-Za-z_-]+:[ \t]*")]
    private static partial System.Text.RegularExpressions.Regex SlotKey();

    [System.Text.RegularExpressions.GeneratedRegex("^status\\**[ \t]*:[ \t]*\\**[ \t]*", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex StatusLabel();

    private static string? ChecklistWrite(IHierarchyProgress progress, string pool, string team, ChecklistEntry entry, string by, string? session, params PlanInput[] events)
    {
        var result = progress.RecordChecklist(pool, team, entry.Stream, by, events, session);
        return result.Off ? null : result.Refused;
    }

    /// <summary>
    /// The <c>cause.event</c> values a mover stamps on the rows it writes outside the transition table (board-derive § E2). With the transition
    /// table's kinds and ship and backlog-done, they are how the audit tells a mover's row from a recorded one.
    /// </summary>
    public const string ChecklistCauseDispatch = "dispatch", ChecklistCauseClose = "close", ChecklistCauseDone = "done", ChecklistCauseRework = "rework", ChecklistCauseOrchMessage = "orch-message";

    /// <summary>The <c>cause.event</c> of a row <c>checklist add</c>, <c>note</c> or <c>todo</c> wrote: a recorded row, replayed as it is.</summary>
    public const string ChecklistCauseVerb = "verb";

    /// <summary>A mover's write with its cause on every row; no <paramref name="at"/> (a caller that predates the stamp) writes the rows unstamped.</summary>
    private static string? ChecklistWriteCaused(IHierarchyProgress progress, string pool, string team, ChecklistEntry entry, string by, string? session, string causeEvent, string causeRef, string? at, params PlanInput[] events) =>
        ChecklistWrite(progress, pool, team, entry, by, session, at is { Length: > 0 } ? [.. events.Select(e => e with { Cause = e.Cause ?? new PlanCause(causeEvent, causeRef, at) })] : events);

    /// <summary>The progress pool: the main checkout's hierarchy directory when there is one, else <paramref name="dir"/>.</summary>
    public static string ProgressPoolOf(string? mainDir, string dir) => mainDir is { Length: > 0 } ? mainDir : dir;

    /// <summary>The team's folder name under <c>progress/</c>: its alias, or <c>_default</c> for the default team (no team name starts with <c>_</c>).</summary>
    public static string ProgressTeamOf(string? alias) => alias is { Length: > 0 } && alias != "null" ? alias : "_default";

    /// <summary>The folder of a team by the name the read model serves: the default team reads <c>default</c> there, and its plans are in <c>_default</c>.</summary>
    public static string ProgressFolderOf(string? teamName) => teamName == "default" ? "_default" : ProgressTeamOf(teamName);

    /// <summary>Whether any queued item has been queued for at least <see cref="QueueStallMs"/>; the cheap test before the member facts are read.</summary>
    public static bool ChecklistAnyQueuedLongEnough(IReadOnlyList<ChecklistEntry> entries, double nowMs)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return entries.Any(e => e.Item.State == PlanStates.Next && ChecklistRules.QueuedMs(e.Item) > 0 && nowMs - ChecklistRules.QueuedMs(e.Item) >= QueueStallMs);
    }

    /// <summary>
    /// The entries the queue rules read: a queued item whose latest ref is a dispatch still running (one of <paramref name="runningRequestIds"/>) is not
    /// waiting for a dispatch, so it is left out. A finished dispatch does not hold an item back.
    /// </summary>
    public static IReadOnlyList<ChecklistEntry> ChecklistWithoutRunningDispatch(IReadOnlyList<ChecklistEntry> entries, IEnumerable<string> runningRequestIds)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(runningRequestIds);
        var running = runningRequestIds.ToHashSet(StringComparer.Ordinal);
        return [.. entries.Where(e => ChecklistDisplayState(e.Item.State) != "queued" || e.Item.Refs is not { Count: > 0 } refs || !running.Contains(refs[^1]))];
    }

    /// <summary>The queue line for a stalled queued item:<c>dispatch &lt;item&gt; to &lt;member&gt;</c>.</summary>
    public static string ChecklistDispatchLine(string itemKey, string member) => $"dispatch {itemKey} to {member}";

    /// <summary>
    /// The lines of the <c>Queue:</c> section <c>checklist show</c> prints: the given <paramref name="dispatchLines"/> (the queue stalls,
    /// already worded <c>dispatch &lt;item&gt; to &lt;member&gt;</c>), then <c>answer &lt;member&gt; (&lt;kind&gt;, &lt;N&gt;m)</c> for each item waiting on the Orchestrator.
    /// </summary>
    public static IReadOnlyList<string> ChecklistQueueLines(IReadOnlyList<ChecklistEntry> entries, IReadOnlyList<string> dispatchLines, double nowMs)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(dispatchLines);
        var lines = new List<string>(dispatchLines);
        foreach (var waiting in ChecklistWaitingOnOrchestrator(entries, nowMs, 0))
        {
            var todo = waiting.Item.Todo ?? string.Empty;
            var colon = todo.IndexOf(':', StringComparison.Ordinal);
            var kind = colon >= 0 && todo[(colon + 1)..].Trim() is { Length: > 0 } after ? after : "reply";
            var minutes = (long)Math.Max(0, Math.Round((nowMs - ChecklistStateSinceMs(waiting.Item)) / 60000));
            lines.Add($"answer {waiting.Item.Holder ?? "the owner"} ({kind}, {minutes.ToString(System.Globalization.CultureInfo.InvariantCulture)}m)");
        }

        return lines;
    }

    /// <summary>When the item last entered its current state, in epoch milliseconds (a note or a retitle does not move it); 0 when unknown.</summary>
    public static double ChecklistStateSinceMs(PlanItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return ChecklistRules.QueuedMs(item);
    }

    /// <summary>The canonical name of an item in a request's <c>item:</c> field: <c>stream/id</c>, which stays unambiguous when the same id later appears in another stream.</summary>
    public static string ChecklistKey(ChecklistEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.Stream + "/" + entry.Item.Item;
    }

    /// <summary>
    /// The item <paramref name="itemId"/> names, for a dispatch: null with <paramref name="entry"/> set, else why it cannot be linked (unknown,
    /// ambiguous, or already done). A dispatch moves an item back from any other state, dropped included.
    /// </summary>
    public static string? ChecklistDispatchTarget(IHierarchyProgress progress, string pool, string team, string itemId, out ChecklistEntry? entry)
    {
        ArgumentNullException.ThrowIfNull(progress);
        var error = ChecklistResolve(ChecklistEntries(progress, pool, team), itemId, out entry);
        if (error is not null || entry is null)
        {
            entry = null;
            return error ?? $"no item {itemId}";
        }

        if (entry.Item.State == PlanStates.Done && !ShipClosed(entry.Item))
        {
            var state = ChecklistDisplayState(entry.Item.State);
            entry = null;
            return $"item {itemId} is {state}";
        }

        return null;
    }

    /// <summary>
    /// A request was sent for <paramref name="itemId"/>: the item's owner becomes <paramref name="owner"/>, its state dispatched, and
    /// <paramref name="requestId"/> is linked. <paramref name="baseSha"/>, when given, is where the item's <c>ah/&lt;slug&gt;</c> branch stood (else the
    /// repository HEAD); it is stored on the <c>ref</c> row so a later ship or merge closes the item only once the branch has moved past it.
    /// Null on success, else why nothing was written. A dispatch to <paramref name="role"/> <c>qa</c> also writes a ledger <c>qa start</c> row (spec ah-stage-timing §3).
    /// </summary>
    public static string? ChecklistOnDispatch(
        IHierarchyProgress progress, string pool, string team, string itemId, string owner, string requestId, string by, string? session, string? baseSha = null,
        string? role = null, string? slug = null, string? sha = null, string? at = null)
    {
        ArgumentNullException.ThrowIfNull(progress);
        var error = ChecklistDispatchTarget(progress, pool, team, itemId, out var entry);
        if (error is not null || entry is null)
        {
            return error ?? $"no item {itemId}";
        }

        QaLedger(progress, pool, team, role, "start", requestId, slug, sha);
        var id = entry.Item.Item;

        // a reviewer works on the item without taking it from its worker: in review while its request is open, reported sha kept; an item with no worker is the reviewer's
        var rows = role == "reviewer"
            ? new List<PlanInput> { new(id, "review", Commits: entry.Item.Commits.Count > 0 ? entry.Item.Commits : null, Branch: entry.Item.Branch, Role: "reviewer"), new(id, "ref", Ref: requestId) { Base = baseSha } }
            : [new(id, "assign", Holder: owner), new(id, "start"), new(id, "ref", Ref: requestId) { Base = baseSha }];
        if (role == "reviewer" && entry.Item.Holder is null or { Length: 0 })
        {
            rows.Insert(0, new PlanInput(id, "assign", Holder: owner));
        }
        if (OwesAnswer(entry.Item))
        {
            rows.Add(new PlanInput(id, "todo", Todo: string.Empty));
        }

        // b525: a ship closed one step of a multi-step item; the next step's request reopens it
        if (ShipClosed(entry.Item))
        {
            var shipSha = entry.Item.DoneCause!.Ref;
            rows.InsertRange(0, [new PlanInput(id, "reopen"), new PlanInput(id, "note", Note: $"reopened by request {requestId} after ship {(shipSha.Length > 8 ? shipSha[..8] : shipSha)}")]);
        }

        return ChecklistWriteCaused(progress, pool, team, entry, by, session, ChecklistCauseDispatch, requestId, at, [.. rows]);
    }

    /// <summary>Whether <paramref name="item"/> is done by a ship's close (its done row's cause is <c>ship</c>), not by an explicit <c>checklist done</c>.</summary>
    private static bool ShipClosed(PlanItem item) => item is { State: PlanStates.Done, DoneCause.Event: ChecklistEventShip };

    private static bool OwesAnswer(PlanItem item) => item.Todo is { } todo && todo.StartsWith("answer ", StringComparison.Ordinal);

    internal const string AnswerUserTodo = "answer user:";

    /// <summary>Whether the user's answer is still owed a reply: a member's later report must not overwrite that todo.</summary>
    private static bool OwesUser(PlanItem item) => item.Todo is { } todo && todo.StartsWith(AnswerUserTodo, StringComparison.Ordinal);

    /// <summary>
    /// A response landed on <paramref name="requestId"/>, or a member's message was paired to it: the linked item (named by the request's
    /// <c>item:</c> field, else the one item that lists the request among its refs) waits on the Orchestrator with "answer &lt;member&gt;: &lt;kind&gt;".
    /// An item in review or approved (it reported DONE) keeps its state: a later STATUS, follow-up or plain fill records only the "answer" todo, so a
    /// later verdict still moves it. No linked item, a done or dropped one, or one already waiting with the same text writes nothing. On a ref older than the item's latest dispatch
    /// nothing is written, except that a <paramref name="question"/> records its todo so it stays visible. Null on success or when there is nothing to do.
    /// </summary>
    public static string? ChecklistOnReply(IHierarchyProgress progress, string pool, string team, string requestId, string? itemId, string member, string kind, string by, string? session, bool question = false, string? at = null) =>
        WaitOnOrchestrator(progress, pool, team, requestId, itemId, $"answer {member}: {kind}", by, session, holdReview: true, lateTodo: question, cause: (ChecklistEventReply, at));

    /// <summary>
    /// The Orchestrator closed <paramref name="requestId"/> (superseded or abandoned, which is not finished work): the linked item waits on the
    /// Orchestrator to be marked done or dispatched again, with <paramref name="reason"/> in its note. A done or dropped item, or no linked item, writes nothing; an item in
    /// review or approved keeps its state and records only the todo.
    /// </summary>
    public static string? ChecklistOnClose(IHierarchyProgress progress, string pool, string team, string requestId, string? itemId, string by, string? session, string? reason = null, string? at = null)
    {
        ArgumentNullException.ThrowIfNull(progress);

        // a review closed with no verdict: the work is reported again, waiting on the Orchestrator
        if (LinkedItem(progress, pool, team, requestId, itemId, out var entry) is null && entry is not null && ReviewOpenOn(entry.Item, requestId))
        {
            return ChecklistWriteCaused(progress, pool, team, entry, by, session, ChecklistCauseClose, requestId, at, BackToReported(entry.Item));
        }

        return WaitOnOrchestrator(progress, pool, team, requestId, itemId, "closed: decide done or re-dispatch", by, session, string.IsNullOrWhiteSpace(reason) ? null : $"closed ({reason}): decide done or re-dispatch", holdReview: true, cause: (ChecklistCauseClose, at));
    }

    /// <summary>Whether <paramref name="requestId"/> is the review <paramref name="item"/> is in: a reviewer's request put it in review, and that request is its newest.</summary>
    private static bool ReviewOpenOn(PlanItem item, string requestId) => item is { State: PlanStates.InReview, Reviewing: true } && item.Refs is [.., var newest] && newest == requestId;

    /// <summary>The row that puts an item back to reported, keeping the sha and branch its DONE report recorded.</summary>
    private static PlanInput BackToReported(PlanItem item) => new(item.Item, "report", Commits: item.Commits.Count > 0 ? item.Commits : null, Branch: item.Branch);

    /// <summary>The Orchestrator closed <paramref name="requestId"/> as answered outside a response file: the linked open item is marked done. Null on success or when there is nothing to do.</summary>
    public static string? ChecklistOnDone(IHierarchyProgress progress, string pool, string team, string requestId, string? itemId, string note, string by, string? session, string? at = null)
    {
        ArgumentNullException.ThrowIfNull(progress);
        if (LinkedItem(progress, pool, team, requestId, itemId, out var entry) is { } problem)
        {
            return problem;
        }

        return entry is null || !ChecklistRules.IsOpen(entry.Item)
            ? null
            : ChecklistWriteCaused(progress, pool, team, entry, by, session, ChecklistCauseDone, requestId, at, new PlanInput(entry.Item.Item, "done", Note: note.Length > 0 ? note : null));
    }

    /// <summary>
    /// The lead acted on the verdict filed for the review request <paramref name="requestId"/> (acked it, or sent a request under it): the auto item
    /// that request made is done when it waits on the lead and the request is its newest. Any other item is left alone. Null on success or when there is nothing to do.
    /// </summary>
    public static string? ChecklistOnVerdictActed(IHierarchyProgress progress, string pool, string team, string requestId, string by, string? session, string? at = null)
    {
        ArgumentNullException.ThrowIfNull(progress);
        return ChecklistItemOfRequest(ChecklistEntries(progress, pool, team), requestId) is { Item: { State: PlanStates.WaitingOrch, Refs: [.., var newest] } item } entry
            && newest == requestId && ChecklistIsAuto(item)
            ? ChecklistWriteCaused(progress, pool, team, entry, by, session, ChecklistCauseDone, requestId, at, new PlanInput(item.Item, "done", Note: "verdict acted on"))
            : null;
    }

    /// <summary>
    /// The lead acked a response, which is its acceptance (spec ah-ui-focus §4): the transition table moves the item. <paramref name="at"/> is when the
    /// report was filed, so a <c>checklist set</c> made after it still wins. <paramref name="noItem"/> says the report would have moved an item and has none
    /// to move. Null on success or when there is nothing to do.
    /// </summary>
    public static string? ChecklistOnAck(
        IHierarchyProgress progress, string pool, string team, string requestId, string? itemId, string? responseText, DateTimeOffset at, string by, string? session, Func<string, string?>? slugOfRequest, Func<string, string?>? parentOfRequest, Func<string, string?>? workerOfRequest, out bool noItem)
    {
        ArgumentNullException.ThrowIfNull(progress);
        noItem = false;
        if (LinkedItem(progress, pool, team, requestId, itemId, out var entry) is { } error)
        {
            return error;
        }

        var ev = new ChecklistEvent(ChecklistEventAck, ProgressFormat.PlanTime(at), requestId, by) { Text = responseText, ItemId = itemId, Chain = RequestChain(requestId, parentOfRequest), WorkerOfRequest = workerOfRequest };
        noItem = entry is null && AckVerdictOf(responseText) is AckVerdict.Done or AckVerdict.Pass or AckVerdict.Back
            && ChecklistTransitions(ev, ChecklistEntries(progress, pool, team), slugOfRequest).Count == 0;
        return ChecklistOnEvent(progress, pool, team, ev, by, session, slugOfRequest);
    }

    private enum AckVerdict
    {
        /// <summary>The report asks nothing of the item: a STATUS or a note.</summary>
        None,

        /// <summary>The member says the work is finished, which no review has judged.</summary>
        Done,

        /// <summary>A review or a check passed the work.</summary>
        Pass,

        /// <summary>The next move is the lead's: changes, blocked, or a decision.</summary>
        Back,
    }

    private static AckVerdict AckVerdictOf(string? text)
    {
        var line = FirstReplyLine(text);
        if (StartsWithTag(line, "VERDICT"))
        {
            line = line["VERDICT".Length..].TrimStart();
        }

        if (IsPartialDone(line))
        {
            return AckVerdict.None;
        }

        if (StartsWithTag(line, "DONE"))
        {
            return AckVerdict.Done;
        }

        if (PassTags.Any(t => StartsWithTag(line, t)))
        {
            return AckVerdict.Pass;
        }

        return BackTags.Any(t => StartsWithTag(line, t)) || line.StartsWith("NEEDS-", StringComparison.OrdinalIgnoreCase)
            ? AckVerdict.Back
            : AckVerdict.None;
    }

    private static readonly string[] PassTags = ["PASS", "APPROVED"];

    private static readonly string[] BackTags = ["CHANGES REQUIRED", "FAIL", "PARTIAL", "BLOCKED", "QUESTION"];

    /// <summary>Whether the work an item reported left nothing to ship: its reported sha is where its dispatch started.</summary>
    private static bool NothingToShip(PlanItem item) =>
        item.Commits is [.., var reported] && item.Base is { Length: > 0 } start && SameSha(reported, start);

    private static bool SameSha(string a, string b) =>
        a.StartsWith(b, StringComparison.OrdinalIgnoreCase) || b.StartsWith(a, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a request is linked to an item of the team's checklist, by its <c>item</c> field or the item's refs.</summary>
    public static bool ChecklistIsLinked(IHierarchyProgress progress, string pool, string team, string requestId, string? itemId) =>
        LinkedItem(progress, pool, team, requestId, itemId, out var entry) is null && entry is not null;

    /// <summary>HEAD when the tree is clean: untracked files count, and a failed status counts as dirty. Null otherwise. <paramref name="git"/> runs a command and is null on failure.</summary>
    public static string? ChecklistCleanHead(Func<string[], string?> git)
    {
        ArgumentNullException.ThrowIfNull(git);
        return git(["status", "--porcelain"])?.Trim() is { Length: 0 } && git(["rev-parse", "--verify", "-q", "HEAD^{commit}"])?.Trim() is { Length: > 0 } head ? head : null;
    }

    /// <summary>The roles whose report at the dispatch's base sha means there is nothing to ship. An allowlist, because a custom role can commit.</summary>
    private static readonly HashSet<string> NothingToShipRoles = new(StringComparer.Ordinal) { "reviewer", QaRole, "ultra-advisor", "backlog-groomer" };

    /// <summary>Whether a DONE's <paramref name="reported"/> sha is recorded: at the item's base it proves nothing from any other role, so the ack approves rather than closes.</summary>
    private static bool RecordsSha(PlanItem item, string reported, string? role) =>
        item.Base is not { Length: > 0 } start || !SameSha(reported, start) || (role is not null && NothingToShipRoles.Contains(role));

    /// <summary>Whether a dispatch newer than <paramref name="requestId"/> took over <paramref name="item"/>. Request ids sort by time.</summary>
    private static bool DispatchedSince(PlanItem item, string requestId) =>
        item.Refs is { Count: > 0 } refs && string.CompareOrdinal(refs[^1], requestId) > 0;

    /// <summary>Whether <paramref name="item"/> carries another worker's request than the one this event answers and that work never reported DONE: a pass on it reviews a part, so it neither closes nor approves the item.</summary>
    private static bool WorkerStillOwes(PlanItem item, ChecklistEvent ev) =>
        !item.ReportedDone && (item.Refs ?? []).Any(r => r != ev.Ref && ev.WorkerOfRequest?.Invoke(r) is { Length: > 0 });

    /// <summary>The note an ack leaves when an item goes back to the lead: the report's first line.</summary>
    private static string AckWhy(string? text)
    {
        var line = FirstReplyLine(text);
        return line.Length > 200 ? line[..200] : line;
    }

    private static void AckMoves(ChecklistEvent ev, IReadOnlyList<ChecklistEntry> entries, ChecklistEntry? own, Func<string, string?>? slugOfRequest, Action<ChecklistEntry, PlanInput[]> move)
    {
        var verdict = AckVerdictOf(ev.Text);
        if (verdict == AckVerdict.None)
        {
            // a STATUS or a note moves nothing: an item that reply parked with an answer owed goes back to where it was
            if (own is not null && own.Item.State == PlanStates.WaitingOrch && OwesAnswer(own.Item) && own.Item.Refs is { Count: > 0 } refs && refs[^1] == ev.Ref && AckSettlesReply(ev.Text))
            {
                move(own, [new PlanInput(own.Item.Item, "start"), new PlanInput(own.Item.Item, "note", Note: "acked " + ChecklistReplyKind(ev.Text))]);
            }

            return;
        }

        void Settle(ChecklistEntry e)
        {
            if (DispatchedSince(e.Item, ev.Ref))
            {
                return;
            }

            var id = e.Item.Item;
            var rows = new List<PlanInput>();
            if (verdict == AckVerdict.Back)
            {
                if (e.Item.State != PlanStates.WaitingOrch || e.Item.Note != AckWhy(ev.Text))
                {
                    rows.Add(new PlanInput(id, "wait-orch", Note: AckWhy(ev.Text)));
                }
            }
            else if (!WorkerStillOwes(e.Item, ev))
            {
                if (NothingToShip(e.Item))
                {
                    rows.Add(new PlanInput(id, "done", Note: "acked: nothing to ship"));
                }
                else if (verdict == AckVerdict.Pass && e.Item.State != PlanStates.Approved)
                {
                    rows.Add(new PlanInput(id, "approve"));
                }
                else if (verdict == AckVerdict.Done && e.Item.State is not (PlanStates.Reported or PlanStates.InReview or PlanStates.Approved))
                {
                    // DONE is not a review: only a pass approves
                    rows.Add(new PlanInput(id, "report"));
                }

                if (OwesAnswer(e.Item))
                {
                    rows.Add(new PlanInput(id, "todo", Todo: string.Empty));
                }
            }
            else if (verdict == AckVerdict.Pass && e.Item.State == PlanStates.WaitingOrch && OwesAnswer(e.Item))
            {
                // a pass on part of the work: the item stays open, and the lead has now answered the report that parked it
                rows.Add(new PlanInput(id, "start"));
                rows.Add(new PlanInput(id, "todo", Todo: string.Empty));
            }

            if (rows.Count > 0)
            {
                move(e, [.. rows]);
            }
        }

        if (own is not null)
        {
            Settle(own);
        }

        // a reviewer's verdict is about the work under its slug, which sits on another item when the review had its own
        if (LedgerEvents.Verdict(FirstReplyLine(ev.Text)) is { } named)
        {
            var states = verdict == AckVerdict.Pass ? new[] { PlanStates.Reported, PlanStates.InReview, PlanStates.Approved } : [PlanStates.InProgress];
            foreach (var e in entries.Where(e => e != own && states.Contains(e.Item.State) && VerdictReaches(e.Item, named.Slug, ev, slugOfRequest, verdict == AckVerdict.Pass)))
            {
                Settle(e);
            }
        }
    }

    /// <summary>
    /// Whether a verdict is about <paramref name="item"/>: a request of the item carries the verdict's slug or is on the verdict's request chain. A failing verdict also reaches the item
    /// that reported the commit it names, so a review with its own item and slug still sends the build back.
    /// </summary>
    private static bool VerdictReaches(PlanItem item, string slug, ChecklistEvent ev, Func<string, string?>? slugOfRequest, bool pass) =>
        (item.Refs ?? []).Any(r => slugOfRequest?.Invoke(r) == slug || (ev.Chain is { } chain && chain.Contains(r, StringComparer.Ordinal)))
        || (!pass && LedgerEvents.VerdictSha(FirstReplyLine(ev.Text)) is { } sha && item.Commits.Any(c => c.StartsWith(sha, StringComparison.Ordinal) || sha.StartsWith(c, StringComparison.Ordinal)));

    /// <summary>How the note of a failing verdict's <c>start</c> row begins; the plan fold reads it to retire the item's DONE report.</summary>
    internal const string ReworkNote = "verdict ";

    /// <summary>How the note <c>checklist ship --failed</c> writes begins; the plan fold reads it to show the next start as rework.</summary>
    internal const string GateFailedNote = "gate failed";

    /// <summary>The rows a failing verdict writes on <paramref name="entry"/>: back to in progress, and when the reviewer holds it, to the member of its last request who is not a reviewer.</summary>
    private static PlanInput[] Rework(ChecklistEntry entry, ChecklistEvent ev, string word)
    {
        var start = new PlanInput(entry.Item.Item, "start", Note: ReworkNote + word);
        var worker = entry.Item.Holder == ev.Actor ? (entry.Item.Refs ?? []).Reverse().Select(r => ev.WorkerOfRequest?.Invoke(r)).FirstOrDefault(static w => w is { Length: > 0 }) : null;
        return worker is null ? [start] : [new PlanInput(entry.Item.Item, "assign", Holder: worker), start];
    }

    /// <summary>
    /// A reviewer linked <paramref name="worker"/> to fix an item it reviewed: the item the review request belongs to moves to the worker, with no hand update. Only an item that reported work
    /// and the reviewer holds moves, so a review's own item stays. Null on success or when nothing moves.
    /// </summary>
    public static string? ChecklistOnRework(IHierarchyProgress progress, string pool, string team, string requestId, string? itemId, string reviewer, string worker, string by, string? session, string? at = null)
    {
        ArgumentNullException.ThrowIfNull(progress);
        if (LinkedItem(progress, pool, team, requestId, itemId, out var entry) is { } error)
        {
            return error;
        }

        // the reviewer holds it, or it is in this review, which leaves the holder with its worker
        return entry is { Item.ReportedDone: true } && (entry.Item.Holder == reviewer || ReviewOpenOn(entry.Item, requestId)) && ChecklistRules.IsOpen(entry.Item)
            ? ChecklistWriteCaused(progress, pool, team, entry, by, session, ChecklistCauseRework, requestId, at, new PlanInput(entry.Item.Item, "assign", Holder: worker))
            : null;
    }

    /// <summary>The requests above <paramref name="requestId"/> by <c>parent</c>, nearest first. A send over a link must already reach the link's task this way (<c>MeshRule</c>).</summary>
    private static List<string> RequestChain(string requestId, Func<string, string?>? parentOfRequest)
    {
        var chain = new List<string>();
        for (var at = parentOfRequest?.Invoke(requestId); at is { Length: > 0 } and not "null" && at != requestId && !chain.Contains(at); at = parentOfRequest!(at))
        {
            chain.Add(at);
        }

        return chain;
    }

    /// <summary>
    /// The Orchestrator sent <paramref name="member"/> a message: every item waiting on the Orchestrator with the todo "answer &lt;member&gt;: …"
    /// goes back to dispatched, with the note "answered-by: sendmessage". No request is filed for it. Null on success or when nothing waits.
    /// </summary>
    public static string? ChecklistOnOrchestratorMessage(IHierarchyProgress progress, string pool, string team, string member, string by, string? session, string? at = null) =>
        AnswerWaitingItems(progress, pool, team, member, by, session, "sendmessage", at);

    /// <summary>
    /// The Orchestrator filed a new brief to <paramref name="member"/>: the brief is the answer to everything that member is owed, so every item
    /// waiting on the Orchestrator with the todo "answer &lt;member&gt;: …" goes back to dispatched, with the note "answered-by: new brief".
    /// Items waiting on another member stay. Null on success or when nothing waits.
    /// </summary>
    public static string? ChecklistOnBrief(IHierarchyProgress progress, string pool, string team, string member, string by, string? session, string? at = null) =>
        AnswerWaitingItems(progress, pool, team, member, by, session, "new brief", at);

    /// <summary>Puts <paramref name="member"/>'s waiting items back to dispatched; the rows' <see cref="ChecklistCauseOrchMessage"/> cause has the ref <c>&lt;member&gt; &lt;via&gt;</c>.</summary>
    private static string? AnswerWaitingItems(IHierarchyProgress progress, string pool, string team, string member, string by, string? session, string via, string? at = null)
    {
        ArgumentNullException.ThrowIfNull(progress);
        string? first = null;
        foreach (var entry in ChecklistEntries(progress, pool, team).Where(e => e.Item.State == PlanStates.WaitingOrch && e.Item.Todo is { } todo && todo.StartsWith($"answer {member}:", StringComparison.Ordinal)))
        {
            var id = entry.Item.Item;
            first ??= ChecklistWriteCaused(progress, pool, team, entry, by, session, ChecklistCauseOrchMessage, member + " " + via, at, new PlanInput(id, "start"), new PlanInput(id, "note", Note: "answered-by: " + via));
        }

        return first;
    }

    private static string? WaitOnOrchestrator(IHierarchyProgress progress, string pool, string team, string requestId, string? itemId, string text, string by, string? session, string? note = null, bool holdReview = false, bool lateTodo = false, (string Event, string? At) cause = default)
    {
        ArgumentNullException.ThrowIfNull(progress);
        if (LinkedItem(progress, pool, team, requestId, itemId, out var entry) is { } error)
        {
            return error;
        }

        string? ChecklistWrite(IHierarchyProgress progress, string pool, string team, ChecklistEntry entry, string by, string? session, params PlanInput[] events) =>
            ChecklistWriteCaused(progress, pool, team, entry, by, session, cause.Event ?? string.Empty, requestId, cause.Event is null ? null : cause.At, events);

        var keepTodo = entry is not null && OwesUser(entry.Item);
        if (entry is null || !ChecklistRules.IsOpen(entry.Item) || entry.Item.State == PlanStates.WaitingUser || (DispatchedSince(entry.Item, requestId) && !lateTodo) || (entry.Item.State == "waiting_orch" && (entry.Item.Todo == text || keepTodo)))
        {
            return null;
        }

        var id = entry.Item.Item;
        if (DispatchedSince(entry.Item, requestId) || (holdReview && entry.Item.State is PlanStates.Reported or PlanStates.InReview or PlanStates.Approved))
        {
            return entry.Item.Todo == text || keepTodo ? null : ChecklistWrite(progress, pool, team, entry, by, session, new PlanInput(id, "todo", Todo: text));
        }

        var wait = new PlanInput(id, "wait-orch", Note: note ?? text);
        return keepTodo ? ChecklistWrite(progress, pool, team, entry, by, session, wait) : ChecklistWrite(progress, pool, team, entry, by, session, wait, new PlanInput(id, "todo", Todo: text));
    }

    /// <summary>The kind of a <see cref="ChecklistEvent"/> for a member's reply to a request (<c>Ref</c> is the request id).</summary>
    public const string ChecklistEventReply = "reply";

    /// <summary>The kind of a <see cref="ChecklistEvent"/> for a withdrawn brief (<c>Ref</c> is the request id).</summary>
    public const string ChecklistEventWithdraw = "withdraw";

    /// <summary>The kind of a <see cref="ChecklistEvent"/> for a ledger ship row (<c>Ref</c> is the sha).</summary>
    public const string ChecklistEventShip = "ship";

    /// <summary>The kind of a <see cref="ChecklistEvent"/> for a ledger merge row (<c>Ref</c> is the sha).</summary>
    public const string ChecklistEventMerge = "merge";

    /// <summary>The kind of a <see cref="ChecklistEvent"/> for the lead's ack of a report (<c>Ref</c> is the request id, <c>At</c> when the report was filed).</summary>
    public const string ChecklistEventAck = "ack";

    /// <summary>The kind of a <see cref="ChecklistEvent"/> for the user's answer to an AskUserQuestion tagged with an item (<c>Ref</c> is the tool use).</summary>
    public const string ChecklistEventAnswered = "answered";

    /// <summary>The kind of a <see cref="ChecklistEvent"/> for a question put to the user about an item (<c>Text</c> is the question): the item waits on the user.</summary>
    public const string ChecklistEventAsked = "asked";

    /// <summary>The item an AskUserQuestion's <c>metadata.source</c> names with <c>item=&lt;stream/id&gt;</c>, or null.</summary>
    public static string? ChecklistAskItem(string? source) =>
        (source ?? string.Empty).Split(';').Where(static p => p.StartsWith("item=", StringComparison.Ordinal)).Select(static p => p[5..]).FirstOrDefault(static p => p.Length > 0);

    /// <summary>
    /// The transition table (spec ah-sitrep-events §1): the plan rows an observed event implies, per item. Pure: nothing is read or written.
    /// <paramref name="slugOfRequest"/> gives a request's slug (reviewer verdicts); <paramref name="contains"/> says whether a ship or merge sha
    /// contains an item's branch tip. Every row carries <c>cause</c>; an item whose latest override is not older than the event gets none (§2).
    /// </summary>
    public static IReadOnlyList<(ChecklistEntry Entry, IReadOnlyList<PlanInput> Rows)> ChecklistTransitions(
        ChecklistEvent ev, IReadOnlyList<ChecklistEntry> entries, Func<string, string?>? slugOfRequest = null, Func<ChecklistEntry, bool>? contains = null)
    {
        ArgumentNullException.ThrowIfNull(ev);
        ArgumentNullException.ThrowIfNull(entries);
        var cause = new PlanCause(ev.Kind, ev.Ref, ev.At);
        var moves = new List<(ChecklistEntry, IReadOnlyList<PlanInput>)>();
        void Move(ChecklistEntry e, params PlanInput[] rows)
        {
            // only an answer or a checklist set takes an item off the user's list; a set does not come through here; a note moves nothing
            if (ChecklistRules.IsOpen(e.Item) && !OverriddenSince(e.Item, ev.At) && !moves.Any(m => m.Item1 == e) && (e.Item.State != PlanStates.WaitingUser || ev.Kind == ChecklistEventAnswered || rows.All(static r => r.Event == "note")))
            {
                moves.Add((e, [.. rows.Select(r => r with { Cause = cause })]));
            }
        }

        var own = ev.Kind is ChecklistEventReply or ChecklistEventWithdraw or ChecklistEventAck or ChecklistEventAnswered or ChecklistEventAsked ? OwnItem(entries, ev.Ref, ev.ItemId) : null;
        switch (ev.Kind)
        {
            case ChecklistEventAck:
                AckMoves(ev, entries, own, slugOfRequest, Move);
                break;
            case ChecklistEventAnswered when own is { Item.State: PlanStates.WaitingUser }:
                Move(own, new PlanInput(own.Item.Item, "unblock"));
                break;
            case ChecklistEventAsked when own is not null:
                Move(own, new PlanInput(own.Item.Item, "wait-user", Note: ev.Text));
                break;
            case ChecklistEventReply when ev.Role == "reviewer" && LedgerEvents.ReviewVerdict(FirstReplyLine(ev.Text)) is { } verdict:
                var word = verdict.Verdict.Trim().ToUpperInvariant();

                // a bare verdict word names no slug: it is about what its own request is about
                var verdictSlug = verdict.Slug.Length > 0 ? verdict.Slug : slugOfRequest?.Invoke(ev.Ref) ?? string.Empty;
                var pass = word is "PASS" or "PASS WITH NITS" or "APPROVED";
                var fail = word is "CHANGES REQUIRED" or "FAIL";

                // only a pass closes, and only the review's own item, one that never reported DONE and has no worker still owing it; a fail reopens; a late verdict leaves a newer dispatch alone
                if (own is not null && !DispatchedSince(own.Item, ev.Ref))
                {
                    if (fail)
                    {
                        Move(own, Rework(own, ev, word));
                    }
                    else if (pass && !WorkerStillOwes(own.Item, ev))
                    {
                        Move(own, own.Item.ReportedDone ? new PlanInput(own.Item.Item, "approve") : new PlanInput(own.Item.Item, "done", Note: $"verdict {word}"));
                    }
                }

                // only items that reported DONE are reviewed: a pass moves in_review items, a fail moves in_review or approved ones back
                if (pass || fail)
                {
                    foreach (var e in entries.Where(e => e != own
                        && !DispatchedSince(e.Item, ev.Ref)
                        && (e.Item.State is PlanStates.Reported or PlanStates.InReview || (fail && e.Item.State == PlanStates.Approved))
                        && VerdictReaches(e.Item, verdictSlug, ev, slugOfRequest, pass)))
                    {
                        Move(e, pass ? [new PlanInput(e.Item.Item, "approve")] : Rework(e, ev, word));
                    }
                }

                break;
            // a late DONE leaves a newer dispatch alone, and its sha is not the work now under way
            case ChecklistEventReply when own is not null && ChecklistReplyIsDone(ev.Text) && !DispatchedSince(own.Item, ev.Ref):
                var review = new PlanInput(own.Item.Item, "report", Commits: ev.Sha is { Length: > 0 } reported && RecordsSha(own.Item, reported, ev.Role) ? [reported] : null);
                Move(own, OwesUser(own.Item) ? [review] : [review, new PlanInput(own.Item.Item, "todo", Todo: $"answer {ev.Actor}: report")]);
                break;
            // a withdrawn review leaves the work it reviewed reported; any other withdrawn brief drops its item
            case ChecklistEventWithdraw when own is not null && ReviewOpenOn(own.Item, ev.Ref):
                Move(own, BackToReported(own.Item));
                break;
            // another open brief still holds the item: it keeps its state
            case ChecklistEventWithdraw when own is not null && (own.Item.Refs ?? []).Any(r => r != ev.Ref && ev.RequestOpen?.Invoke(r) == true):
                Move(own, new PlanInput(own.Item.Item, "note", Note: $"brief {ev.Ref} withdrawn; another brief is open"));
                break;
            case ChecklistEventWithdraw when own is not null:
                Move(own, new PlanInput(own.Item.Item, "drop", Note: "brief withdrawn"));
                break;
            case ChecklistEventShip when contains is not null:
                foreach (var e in entries.Where(contains).Where(e => !(AutoCloseHeld(e.Item) && AlreadyNotClosed(e.Item, ev.Kind))))
                {
                    Move(e, AutoCloseHeld(e.Item) ? new PlanInput(e.Item.Item, "note", Note: NotClosedText(e.Item, ev.Kind)) :new PlanInput(e.Item.Item, "done", Note: $"{ev.Kind} {(ev.Ref.Length > 7 ? ev.Ref[..7] : ev.Ref)}"));
                }

                break;
        }

        return moves;
    }

    /// <summary>
    /// The one entry point for a member's reply to <paramref name="requestId"/>, from the CLI or the AutoTrack hook: a DONE report or a reviewer's
    /// verdict goes through the transition table, any other reply waits on the Orchestrator (<see cref="ChecklistOnReply"/>). Null on success.
    /// A reply from <paramref name="role"/> <c>qa</c> that is not a STATUS also writes a ledger <c>qa end</c> row (spec ah-stage-timing §3).
    /// <paramref name="reportedSha"/> is where the member's work stood; a DONE report records it, so a ship closes the item by ancestry, and one with none clears the last.
    /// </summary>
    public static string? ChecklistOnMemberReply(
        IHierarchyProgress progress, string pool, string team, string requestId, string? itemId, string member, string? role, string? text, DateTimeOffset at, string? session,
        Func<string, string?>? slugOfRequest, string? sha = null, string? reportedSha = null, Func<string, string?>? parentOfRequest = null, Func<string, string?>? workerOfRequest = null)
    {
        ArgumentNullException.ThrowIfNull(progress);
        if (ChecklistReplyKind(text) != "STATUS")
        {
            QaLedger(progress, pool, team, role, "end", requestId, slugOfRequest?.Invoke(requestId), sha);
        }

        var by = member + "@" + team;
        return ChecklistReplyIsEvent(text, role)
            ? ChecklistOnEvent(progress, pool, team, new ChecklistEvent(ChecklistEventReply, ProgressFormat.PlanTime(at), requestId, member) { Text = text, ItemId = itemId, Role = role, Sha = reportedSha, Chain = RequestChain(requestId, parentOfRequest), WorkerOfRequest = workerOfRequest }, by, session, slugOfRequest)
            : ChecklistOnReply(progress, pool, team, requestId, itemId, member, ChecklistReplyKind(text), by, session, AckVerdictOf(text) == AckVerdict.Back, ProgressFormat.PlanTime(at));
    }

    /// <summary>A ledger <c>qa</c> row of <paramref name="phase"/> for a dispatch to or a report from <see cref="QaRole"/>; any other role writes nothing. A failed append is not the caller's failure.</summary>
    private static void QaLedger(IHierarchyProgress progress, string pool, string team, string? role, string phase, string requestId, string? slug, string? sha)
    {
        if (role == QaRole)
        {
            _ = progress.AppendLedger(pool, team, new LedgerEvent(QaRole, Sha: sha, Ref: requestId, Slug: slug, Text: "qa " + phase, Phase: phase));
        }
    }

    /// <summary>Whether the transition table handles <paramref name="text"/> as a reply from a member of <paramref name="role"/>: a DONE report, or a reviewer's verdict, with or without the VERDICT line.</summary>
    public static bool ChecklistReplyIsEvent(string? text, string? role) =>
        ChecklistReplyIsDone(text) || (role == "reviewer" && LedgerEvents.ReviewVerdict(FirstReplyLine(text)) is not null);

    /// <summary>Runs <see cref="ChecklistTransitions"/> for <paramref name="ev"/> over the team's checklist and appends the rows, each item through the one plan writer. The first refusal, or null.</summary>
    public static string? ChecklistOnEvent(
        IHierarchyProgress progress, string pool, string team, ChecklistEvent ev, string by, string? session, Func<string, string?>? slugOfRequest = null, Func<ChecklistEntry, bool>? contains = null)
    {
        ArgumentNullException.ThrowIfNull(progress);
        string? first = null;
        foreach (var (entry, rows) in ChecklistTransitions(ev, ChecklistEntries(progress, pool, team), slugOfRequest, contains))
        {
            var refused = ChecklistWrite(progress, pool, team, entry, by, session, [.. rows]);
            first ??= refused;
        }

        return first;
    }

    /// <summary>
    /// A release ref moved from <paramref name="priorTip"/> to <paramref name="sha"/>, and its ship row is in the ledger; only the release watch calls this (spec ah-ui-focus §4,
    /// "Wrong closes"). An item waiting in review or approved with a reported sha is done when that sha differs from its base and this move carried it: an ancestor of
    /// <paramref name="sha"/> and not of <paramref name="priorTip"/>. Any other open item keeps the branch rule: the same test on its branch <c>ah/&lt;slug&gt;</c> (the slug of its first
    /// ref request that has one), so a branch cut from the release tip and not yet committed to lands nothing; nor does one still at its item's base, cut from the commit this ship carries. An item with no base, an unreadable base, or a branch cut from a commit not in its base still closes. A rollback carries nothing. <paramref name="isAncestor"/>(rev, sha)
    /// answers from git for a full ref or a commit; a missing rev answers false. A prior tip git cannot read, or no row, closes nothing: every doubt resolves to late. The first refusal, or null.
    /// </summary>
    public static string? ChecklistOnLedgerRow(
        IHierarchyProgress progress, string pool, string team, string sha, string priorTip, DateTimeOffset at, string by, string? session,
        Func<string, string?> slugOfRequest, Func<string, string, bool> isAncestor)
    {
        ArgumentNullException.ThrowIfNull(slugOfRequest);
        ArgumentNullException.ThrowIfNull(isAncestor);
        if (!isAncestor(priorTip, priorTip))
        {
            return null;
        }

        // the row of this ship: its at stamps the cause
        var ledger = progress.ReadLedger(pool, team).ToList();
        var index = ledger.FindLastIndex(r => LedgerText(r, "kind") == ChecklistEventShip && LedgerText(r, "sha") == sha);
        if (index < 0)
        {
            return null;
        }

        var when = LedgerText(ledger[index], "at") is { } rowAt && ParseAt(rowAt) is { } parsed ? parsed : at;
        bool Carried(string rev) => isAncestor(rev, sha) && !isAncestor(rev, priorTip);
        bool Contains(ChecklistEntry e)
        {
            if (e.Item.State is PlanStates.Reported or PlanStates.InReview or PlanStates.Approved && e.Item.Commits is [.., var reported])
            {
                return !NothingToShip(e.Item) && Carried(reported);
            }

            return (e.Item.Refs ?? []).Select(slugOfRequest).FirstOrDefault(static s => s is { Length: > 0 }) is { } slug
                && Carried("refs/heads/ah/" + slug)
                && !(e.Item.Base is { Length: > 0 } start && isAncestor("refs/heads/ah/" + slug, start));
        }

        return ChecklistOnEvent(progress, pool, team, new ChecklistEvent(ChecklistEventShip, ProgressFormat.PlanTime(when), sha, by), by, session, contains: Contains);
    }

    /// <summary>
    /// <c>ah roster checklist ship &lt;sha&gt; [--failed]</c>, which a ship script calls after its push (or its failed gate). An open item is carried by
    /// <paramref name="sha"/> when its reported sha (in review or approved) differs from its base and is an ancestor of <paramref name="sha"/>, or else when its
    /// branch <c>ah/&lt;slug&gt;</c> is an ancestor of <paramref name="sha"/> and not of its base: the release watch's test with no prior tip. Carried items
    /// are marked done, or with <paramref name="failed"/> get a <c>gate failed</c> note and keep their state. A carried item that is held (<see cref="AutoCloseHeld"/>)
    /// keeps its state with a <c>not closed</c> note, and is listed in Skipped. Returns the items moved, the items skipped with their states, and the first refusal.
    /// <paramref name="closeBacklog"/>(row, sha) closes a backlog row whose last open item this ship closed, as the release watch does.
    /// </summary>
    public static (IReadOnlyList<string> Items, IReadOnlyList<string> Skipped, string? Refused) ChecklistOnShip(
        IHierarchyProgress progress, string pool, string team, string sha, bool failed, DateTimeOffset at, string by, string? session,
        Func<string, string?> slugOfRequest, Func<string, string, bool> isAncestor, Action<string, string>? closeBacklog = null)
    {
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(slugOfRequest);
        ArgumentNullException.ThrowIfNull(isAncestor);
        var open = closeBacklog is null || failed ? [] : ChecklistEntries(progress, pool, team).Where(static e => ChecklistRules.IsOpen(e.Item)).ToList();
        bool Carried(ChecklistEntry e)
        {
            // a failed gate closes nothing, so a held item gets no note either
            if (e.Item.State is PlanStates.Done or PlanStates.Dropped || (failed && AutoCloseHeld(e.Item)))
            {
                return false;
            }

            if (e.Item.State is PlanStates.Reported or PlanStates.InReview or PlanStates.Approved && e.Item.Commits is [.., var reported])
            {
                return !NothingToShip(e.Item) && isAncestor(reported, sha);
            }

            return (e.Item.Refs ?? []).Select(slugOfRequest).FirstOrDefault(static s => s is { Length: > 0 }) is { } slug
                && isAncestor("refs/heads/ah/" + slug, sha)
                && !(e.Item.Base is { Length: > 0 } start && isAncestor("refs/heads/ah/" + slug, start));
        }

        var short7 = sha.Length > 7 ? sha[..7] : sha;
        var moved = new List<string>();
        var skipped = new List<string>();
        string? first = null;
        foreach (var e in ChecklistEntries(progress, pool, team).Where(Carried).ToList())
        {
            var held = AutoCloseHeld(e.Item);
            var row = held ? NotClosed(e.Item, ChecklistEventShip, sha, at)
                : failed ? new PlanInput(e.Item.Item, "note", Note: $"{GateFailedNote} at {short7}")
                : new PlanInput(e.Item.Item, "done", Note: $"{ChecklistEventShip} {short7}") { Cause = new PlanCause(ChecklistEventShip, sha, ProgressFormat.PlanTime(at)) };

            // a held row every later ship carries again is listed each time, noted once
            var refused = held && AlreadyNotClosed(e.Item, ChecklistEventShip) ? null : ChecklistWrite(progress, pool, team, e, by, session, row);
            first ??= refused;
            if (refused is null)
            {
                (held ? skipped : moved).Add(held ? $"{ChecklistKey(e)} ({e.Item.State})" : ChecklistKey(e));
            }
        }

        // b471: as the release watch does, close a backlog row whose last open item, in any team of the pool, this ship just closed
        if (closeBacklog is not null && moved.Count > 0)
        {
            CloseFinishedParents(progress, pool, team, [.. progress.DocTeams(pool).Append(team).Distinct(StringComparer.Ordinal)], open, sha, closeBacklog);
        }

        return (moved, skipped, first);
    }

    /// <summary>
    /// Whether an automatic close (a ship, the release watch, <c>backlog done</c>) leaves <paramref name="item"/> open: it waits on the user or the Orchestrator,
    /// or is blocked, and still owes that whatever its work did. A manual close (<c>checklist done</c>/<c>set</c>) does not ask.
    /// </summary>
    private static bool AutoCloseHeld(PlanItem item) => item.State is PlanStates.WaitingUser or PlanStates.WaitingOrch or PlanStates.Blocked;

    /// <summary>The note an automatic close of <paramref name="cause"/> leaves on a held item instead of closing it.</summary>
    /// <remarks>The row carries its cause, so on a waiting_user item it lands in the trail only and the question stays the note.</remarks>
    private static PlanInput NotClosed(PlanItem item, string cause, string reference, DateTimeOffset at) =>
        new(item.Item, "note", Note: NotClosedText(item, cause)) { Cause = new PlanCause(cause, reference, ProgressFormat.PlanTime(at)) };

    private static string NotClosedText(PlanItem item, string cause) => $"not closed: {cause} while {item.State}";

    /// <summary>Whether <paramref name="item"/>'s last note already says it was not closed for <paramref name="cause"/>: a later ship that carries it again writes nothing.</summary>
    private static bool AlreadyNotClosed(PlanItem item, string cause) => (item.State == PlanStates.WaitingUser ? item.HeldNote : item.Note) == NotClosedText(item, cause);

    /// <summary>
    /// <c>ah backlog done b&lt;N&gt;</c> closed backlog item <paramref name="parent"/>: every open checklist item of <paramref name="team"/> whose parent it is
    /// is marked done with the note <c>backlog b&lt;N&gt; done</c>; a held one (<see cref="AutoCloseHeld"/>) keeps its state with a <c>not closed</c> note.
    /// Returns the items closed, the items skipped with their states, and the first refusal.
    /// </summary>
    public static (IReadOnlyList<string> Items, IReadOnlyList<string> Skipped, string? Refused) ChecklistCloseChildren(
        IHierarchyProgress progress, string pool, string team, string parent, DateTimeOffset at, string by, string? session)
    {
        ArgumentNullException.ThrowIfNull(progress);
        var closed = new List<string>();
        var skipped = new List<string>();
        string? first = null;
        foreach (var e in ChecklistEntries(progress, pool, team).Where(e => e.Item.Parent == parent && e.Item.State is not (PlanStates.Done or PlanStates.Dropped)).ToList())
        {
            var held = AutoCloseHeld(e.Item);
            var refused = held && AlreadyNotClosed(e.Item, "backlog-done") ? null : ChecklistWrite(progress, pool, team, e, by, session, held
                ? NotClosed(e.Item, "backlog-done", parent, at)
                : new PlanInput(e.Item.Item, "done", Note: $"backlog {parent} done") { Cause = new PlanCause("backlog-done", parent, ProgressFormat.PlanTime(at)) });
            first ??= refused;
            if (refused is null)
            {
                (held ? skipped : closed).Add(held ? $"{ChecklistKey(e)} ({e.Item.State})" : ChecklistKey(e));
            }
        }

        return (closed, skipped, first);
    }

    /// <summary>
    /// A checklist row under backlog item <paramref name="parent"/> was closed done (board-truth D2): the commits to close <paramref name="parent"/> with, the done rows' commits
    /// of every team of the pool in order, or null while a row under it is still open work. A row waiting on the user is a question, not work, and does not hold it.
    /// </summary>
    public static IReadOnlyList<string>? ChecklistParentFinished(IHierarchyProgress progress, string pool, string team, string parent)
    {
        ArgumentNullException.ThrowIfNull(progress);
        var rows = progress.DocTeams(pool).Append(team).Distinct(StringComparer.Ordinal).SelectMany(t => ChecklistEntries(progress, pool, t)).Where(e => e.Item.Parent == parent).ToList();
        // ponytail: a backlog done takes at most MaxCommits commits, so the newest are kept; the rows keep them all
        return rows.Any(static e => ChecklistRules.IsOpen(e.Item) && e.Item.State != PlanStates.WaitingUser)
            ? null
            : [.. rows.Where(static e => e.Item.State == PlanStates.Done).SelectMany(static e => e.Item.Commits).Distinct(StringComparer.Ordinal).TakeLast(ProgressFormat.MaxCommits)];
    }

    /// <summary>The teams of <paramref name="pool"/> with rows in the state db's progress logs (ah2-168 Amendment 3), in order. A read error throws.</summary>
    public static IReadOnlyList<string> ChecklistTeams(Func<string, object?[], IReadOnlyList<object?[]>> query, string pool)
    {
        ArgumentNullException.ThrowIfNull(query);
        return [.. query("SELECT DISTINCT team FROM progress_logs WHERE hier_dir = ?1 ORDER BY team", [Ah.Core.Json.MsgRows.PoolKey(pool)]).Select(static r => (string)r[0]!)];
    }

    /// <summary>
    /// A team sees a release ref for the first time, with no prior tip to compare: an item waiting in review closes when its reported sha differs from its base and is an ancestor of
    /// <paramref name="sha"/>. A newer dispatch has already moved the item out of review. Any other item waits for a move. The first refusal, or null.
    /// </summary>
    private static string? ChecklistOnFirstSight(IHierarchyProgress progress, string pool, string team, string sha, DateTimeOffset at, string by, Func<string, string, bool> isAncestor) =>
        ChecklistOnEvent(
            progress, pool, team, new ChecklistEvent(ChecklistEventShip, ProgressFormat.PlanTime(at), sha, by), by, null,
            contains: e => e.Item.State is PlanStates.Reported or PlanStates.InReview && e.Item.Commits is [.., var reported] && !NothingToShip(e.Item) && isAncestor(reported, sha));

    /// <summary>The <c>source.verb</c> of a ship row written by <c>ah ledger ship</c>.</summary>
    public const string LedgerShipVerb = "ah ledger ship";

    private static string? LedgerText(Ah.Core.Json.JsonValue row, string key) => row.Member(key) is { IsText: true } v ? v.AsString() : null;

    /// <summary>The <c>cause.event</c> that marks a hand-written row (<c>checklist set</c> or <c>done</c>): its state rows are overrides, and the transition table never writes it.</summary>
    public const string ChecklistCauseManual = PlanFold.ManualCause;

    /// <summary>The cause a hand-written row carries at <paramref name="at"/>: it marks the row as an override.</summary>
    public static PlanCause ChecklistManualCause(DateTimeOffset at) => new(ChecklistCauseManual, string.Empty, ProgressFormat.PlanTime(at));

    /// <summary>A time in the plan log's UTC form.</summary>
    public static string ChecklistEventTime(DateTimeOffset at) => ProgressFormat.PlanTime(at);

    /// <summary>Whether an override of <paramref name="item"/> is strictly newer than an event at <paramref name="at"/>: the event then moves nothing.</summary>
    private static bool OverriddenSince(PlanItem item, string at) =>
        item.OverrideAt is { } o && ParseAt(o) is { } overrideAt && (ParseAt(at) is not { } eventAt || eventAt < overrideAt);

    private static DateTimeOffset? ParseAt(string text) =>
        DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var at) ? at : null;

    /// <summary>The item a request is about for the transition table: its <c>item:</c> field, else the one item (not superseded) that lists it.</summary>
    private static ChecklistEntry? OwnItem(IReadOnlyList<ChecklistEntry> entries, string requestId, string? itemId)
    {
        if (itemId is { Length: > 0 })
        {
            return ChecklistResolve(entries, itemId, out var named) is null ? named : null;
        }

        var linked = entries.Where(e => (e.Item.Refs ?? []).Contains(requestId, StringComparer.Ordinal) && !ChecklistSuperseded(e)).ToList();
        return linked.Count == 1 ? linked[0] : null;
    }

    /// <summary>The item a request is about: the one its <c>item:</c> field names, else the one item that lists the request among its refs. The error text, or null with <paramref name="entry"/> (null when there is none).</summary>
    private static string? LinkedItem(IHierarchyProgress progress, string pool, string team, string requestId, string? itemId, out ChecklistEntry? entry)
    {
        var entries = ChecklistEntries(progress, pool, team);
        if (itemId is { Length: > 0 })
        {
            return ChecklistResolve(entries, itemId, out entry);
        }

        var linked = entries.Where(e => (e.Item.Refs ?? []).Contains(requestId, StringComparer.Ordinal)).ToList();
        if (linked.Count > 1)
        {
            linked = [.. linked.Where(static e => !ChecklistSuperseded(e))];
        }

        if (linked.Count > 1)
        {
            entry = null;
            return $"request {requestId} is listed by several items ({string.Join(", ", linked.Select(ChecklistKey))}); nothing written";
        }

        entry = linked.FirstOrDefault();
        return null;
    }
}

/// <summary>
/// An event the transition table reads: its kind (<c>HierarchyModule.ChecklistEvent*</c>), when it happened, its ref (request id or sha) and who
/// caused it. A reply also carries its text, the request's <c>item:</c> field and the replying member's role.
/// </summary>
public sealed record ChecklistEvent(string Kind, string At, string Ref, string Actor)
{
    /// <summary>The reply's text.</summary>
    public string? Text { get; init; }

    /// <summary>The request's <c>item:</c> field.</summary>
    public string? ItemId { get; init; }

    /// <summary>The replying member's role.</summary>
    public string? Role { get; init; }

    /// <summary>The commit a DONE reply reported: where the member's work stood.</summary>
    public string? Sha { get; init; }

    /// <summary>The requests above the event's request by <c>parent</c>: a verdict moves the item one of them belongs to.</summary>
    public IReadOnlyList<string>? Chain { get; init; }

    /// <summary>The member a request went to, when that member is not a reviewer: who a failing verdict's rework goes back to.</summary>
    public Func<string, string?>? WorkerOfRequest { get; init; }

    /// <summary>Whether a request is still open (not answered, closed or withdrawn): a withdrawal leaves an item another open brief holds.</summary>
    public Func<string, bool>? RequestOpen { get; init; }
}
