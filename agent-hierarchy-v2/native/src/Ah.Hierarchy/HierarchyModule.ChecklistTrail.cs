using System.Globalization;
using Ah.Core.Json;

namespace Ah.Hierarchy;

/// <summary>One plan row of an item as <c>checklist show --trail</c> prints it: when, which event, who, why (null for a hand-written row), the state before and after, and the item's role and holder after it.</summary>
public sealed record ChecklistTrailRow(string Ts, string Event, string By, PlanCause? Cause, string From, string To, string? Role = null, string? Holder = null);

/// <summary>Who is on the work of one team: the members running and working, and the ids of the open exchanges addressed to a running member.</summary>
public sealed record ChecklistLiveness(IReadOnlySet<string> Working, IReadOnlySet<string> OpenTo)
{
    /// <summary>Every open exchange of the team, whoever it addresses; null when not known.</summary>
    public IReadOnlySet<string>? Open { get; init; }

    /// <summary>Each running member an open exchange addresses, with that exchange's title (else its slug); null when not known.</summary>
    public IReadOnlyDictionary<string, string>? OnExchange { get; init; }
}

public static partial class HierarchyModule
{
    /// <summary>How long an open item may go without activity before it is stale (spec ah-sitrep-events §4). One constant until someone asks for a setting.</summary>
    public const double ChecklistStaleMs = 86_400_000;

    private static readonly HashSet<string> StaleStates = new(
        [PlanStates.InProgress, PlanStates.Blocked, PlanStates.WaitingOrch, PlanStates.Reported, PlanStates.InReview, PlanStates.Approved], StringComparer.Ordinal);

    /// <summary>A title as people read it: one that is a bare slug, with or without its stream (<c>_run/make-the-ship</c>), reads with spaces (<c>make the ship</c>); any other is left alone.</summary>
    public static string ChecklistTitle(string title)
    {
        var at = title.LastIndexOfAny(['/', ':']);
        var slug = title[(at + 1)..];
        return slug.Contains('-', StringComparison.Ordinal) && slug.All(static c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-') ? slug.Replace('-', ' ').Trim() : title;
    }

    /// <summary>The path of a stream's plan log.</summary>
    public static string ChecklistPlanLogPath(string pool, string team, string stream) => ProgressReader.Folder(pool, team) + "/" + stream + ".plan.jsonl";

    /// <summary>Every row of <paramref name="item"/> in <paramref name="planJsonl"/>, oldest first, with the state before and after it, by replaying the fold row by row.</summary>
    public static IReadOnlyList<ChecklistTrailRow> ChecklistTrail(string? planJsonl, string item)
    {
        var fold = new PlanFold();
        var trail = new List<ChecklistTrailRow>();
        foreach (var raw in (planJsonl ?? string.Empty).Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            JsonValue row;
            try
            {
                row = JsonText.Parse(line);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                continue;
            }

            if (!row.IsMap || ProgressFormat.PlanText(row, "item") != item || ProgressFormat.PlanText(row, "event") is not { } eventName)
            {
                fold.Apply(row);
                continue;
            }

            var before = fold.State.Items.FirstOrDefault(i => i.Item == item)?.State ?? string.Empty;
            fold.Apply(row);
            var now = fold.State.Items.FirstOrDefault(i => i.Item == item);
            trail.Add(new ChecklistTrailRow(ProgressFormat.PlanText(row, "ts") ?? string.Empty, eventName, ProgressFormat.PlanText(row, "by") ?? string.Empty, CauseOf(row), before, now?.State ?? string.Empty, now?.Role, now?.Holder));
        }

        return trail;
    }

    /// <summary>The command that undoes the last automatic move of a trail: <c>ah roster checklist set &lt;key&gt; --state &lt;previous&gt; --note "revert &lt;event&gt;"</c>; null when no automatic row moved the state.</summary>
    public static string? ChecklistRevertCommand(string key, IReadOnlyList<ChecklistTrailRow> trail)
    {
        ArgumentNullException.ThrowIfNull(trail);
        return trail.LastOrDefault(static r => ChecklistIsAutomaticMove(r) && r.From.Length > 0) is { } last
            ? $"ah roster checklist set {key} --state {last.From} --note \"revert {last.Cause!.Event}\""
            : null;
    }

    /// <summary>
    /// Whether a trail row is a move the transition table made: it has a cause that is not <see cref="ChecklistCauseManual"/> or <see cref="ChecklistCauseVerb"/> (a hand-written row)
    /// nor a mover's audit stamp (<see cref="ChecklistIsMoverStamp"/>), and it changed the state.
    /// </summary>
    public static bool ChecklistIsAutomaticMove(ChecklistTrailRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.Cause is { } cause && cause.Event is not (ChecklistCauseManual or ChecklistCauseVerb) && !ChecklistIsMoverStamp(cause) && row.From != row.To;
    }

    /// <summary>Whether a cause is one a mover outside the transition table stamps for the audit (board-derive § E2); such rows count as they did unstamped.</summary>
    internal static bool ChecklistIsMoverStamp(PlanCause? cause) =>
        cause?.Event is ChecklistCauseDispatch or ChecklistCauseClose or ChecklistCauseDone or ChecklistCauseRework or ChecklistCauseOrchMessage;

    /// <summary>
    /// An item's last activity in epoch milliseconds: the newest of its latest plan row, its ledger rows whose <c>ref</c> is one of its refs, and
    /// <paramref name="messageMs"/> (the newest message of its exchanges, when the caller has it). Commit times are not read from git. Null when none is known.
    /// </summary>
    public static double? ChecklistLastActivityMs(PlanItem item, IEnumerable<JsonValue> ledger, double? messageMs = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(ledger);
        var refs = (item.Refs ?? []).ToHashSet(StringComparer.Ordinal);
        var times = ledger
            .Where(r => r.Member("ref") is { IsText: true } reference && refs.Contains(reference.AsString()))
            .Select(static r => r.Member("at") is { IsText: true } at ? Ms(at.AsString()) : null)
            .Append(Ms(item.UpdatedAt) ?? Ms(item.StateSince))
            .Append(messageMs)
            .OfType<double>()
            .ToList();
        return times.Count == 0 ? null : times.Max();
    }

    /// <summary>Whether an item in <paramref name="state"/> whose last activity was at <paramref name="lastActivityMs"/> is stale at <paramref name="nowMs"/>: an open, moving or held state idle for more than <see cref="ChecklistStaleMs"/>.</summary>
    public static bool ChecklistIsStale(string state, double? lastActivityMs, double nowMs) =>
        StaleStates.Contains(state) && lastActivityMs is { } last && nowMs - last > ChecklistStaleMs;

    /// <summary>
    /// What an item's <c>work</c> is (spec ah-ui-focus §2), first match winning; null for one that is not open work (<c>not_started</c>, <c>next</c>, <c>done</c>, <c>dropped</c>).
    /// Every count and column reads this: <c>waiting_you</c>, <c>working</c>, <c>stale</c>, <c>with_lead</c>, <c>blocked</c> or <c>no_one</c>. No <paramref name="live"/> means nobody is known to be running.
    /// </summary>
    public static string? ChecklistWork(string state, string? holder, IReadOnlyList<string>? refs, ChecklistLiveness? live, double? lastActivityMs, double nowMs)
    {
        if (state is PlanStates.NotStarted or PlanStates.Next or PlanStates.Done or PlanStates.Dropped)
        {
            return null;
        }

        if (state == PlanStates.WaitingUser)
        {
            return "waiting_you";
        }

        if (state == PlanStates.InProgress && live is { } l && ((holder is not null && l.Working.Contains(holder)) || (refs is not null && refs.Any(l.OpenTo.Contains))))
        {
            return "working";
        }

        // every exchange the dispatch opened has closed, so the next move is the lead's, not a stuck member's
        var closed = state == PlanStates.InProgress && live?.Open is { } open && refs is { Count: > 0 } && !refs.Any(open.Contains);

        // Report in and blocked stay on show at any age: only the work no one is on goes stale (spec ah-ui-focus §2)
        return closed || state is PlanStates.Reported or PlanStates.InReview or PlanStates.Approved or PlanStates.WaitingOrch ? "with_lead"
            : state == PlanStates.Blocked ? "blocked"
            : ChecklistIsStale(state, lastActivityMs, nowMs) ? "stale"
            : "no_one";
    }

    /// <summary>
    /// The one list of what waits on the user (spec ah-ui-answer §2): every open decision, and every item waiting on the user that no open decision names, since such an item
    /// is that decision. Every count and row of the list reads this, for one team at a time.
    /// </summary>
    public static ChecklistWaitingList<TDecision, TItem> ChecklistWaiting<TDecision, TItem>(
        IEnumerable<TDecision> decisions,
        Func<TDecision, string?> itemOf,
        IEnumerable<TItem> items,
        Func<TItem, string?> stateOf,
        Func<TItem, string?> idOf)
    {
        ArgumentNullException.ThrowIfNull(itemOf);
        ArgumentNullException.ThrowIfNull(stateOf);
        ArgumentNullException.ThrowIfNull(idOf);
        var open = decisions.ToList();
        var named = open.Select(itemOf).OfType<string>().ToHashSet(StringComparer.Ordinal);
        return new(open, [.. items.Where(i => ChecklistWork(stateOf(i) ?? string.Empty, null, null, null, null, 0) == "waiting_you" && !named.Contains(idOf(i) ?? string.Empty))]);
    }

    /// <summary>Why <paramref name="labels"/> may not be a question's answers, or null when they may: the rule <c>checklist set --option</c> and the plan log share.</summary>
    public static string? ChecklistOptionsRefusal(IEnumerable<string> labels)
    {
        var options = new JsonArray();
        foreach (var label in labels)
        {
            options.Add(label);
        }

        return ProgressFormat.OptionsRefusal(options);
    }

    /// <summary>
    /// Answers an item waiting on the user (spec ah-ui-answer §4): <paramref name="key"/> is <c>&lt;stream&gt;:&lt;item&gt;</c>, <paramref name="since"/> the item's state time as
    /// the form saw it, <paramref name="choice"/> a letter naming an option or <c>other: &lt;text&gt;</c>. The answer rows by <c>user</c>, which put the item back in its earlier state, then <paramref name="notify"/> (to,
    /// event, text; returns an error or null) tells the Orchestrator, and the item's <c>asked_for</c> member when <see cref="AskerRefusal"/> over <paramref name="readTeam"/> still allows it. Returns <c>{refused: {reason, detail}}</c>, <c>{bad_request}</c> or <c>{id, answers, decision, event_error?}</c>.
    /// </summary>
    public static JsonObject ChecklistAnswer(
        IHierarchyProgress progress, string pool, string team, string key, string since, string choice, DateTimeOffset now, Func<string, string, string, string?>? notify, Func<string?, JsonValue> readTeam)
    {
        ArgumentNullException.ThrowIfNull(choice);
        return ChecklistReply(progress, pool, team, key, since, now, notify, readTeam, options => ChecklistAnswerText(options, choice), null, choice);
    }

    /// <summary>
    /// Dismisses an item waiting on the user (spec ah-ui-answer §6): the same guards and the same wakes as <see cref="ChecklistAnswer"/>, but one <c>drop</c> row by <c>user</c>
    /// carrying <c>answer: "Dismissed: &lt;reason&gt;"</c> under cause <c>ui-dismiss</c>, and the mail says <c>&lt;key&gt; dismissed: &lt;reason&gt;</c>.
    /// </summary>
    public static JsonObject ChecklistDismiss(
        IHierarchyProgress progress, string pool, string team, string key, string since, string reason, DateTimeOffset now, Func<string, string, string, string?>? notify, Func<string?, JsonValue> readTeam)
    {
        ArgumentNullException.ThrowIfNull(reason);
        return ChecklistReply(progress, pool, team, key, since, now, notify, readTeam, _ => ChecklistDismissed + reason, reason, reason);
    }

    /// <summary>The text a dismissal's <c>answer</c> starts with.</summary>
    public const string ChecklistDismissed = "Dismissed: ";

    private static JsonObject ChecklistReply(
        IHierarchyProgress progress, string pool, string team, string key, string since, DateTimeOffset now, Func<string, string, string, string?>? notify, Func<string?, JsonValue> readTeam,
        Func<IReadOnlyList<string>?, string?> answerOf, string? dismiss, string choice)
    {
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(readTeam);
        ArgumentNullException.ThrowIfNull(key);
        static JsonObject Refused(string reason, string detail) => new() { ["refused"] = new JsonObject { ["reason"] = reason, ["detail"] = detail } };
        var folder = ProgressFolderOf(team);
        if (ChecklistEntries(progress, pool, folder).FirstOrDefault(e => e.Stream + ":" + e.Item.Item == key) is not { } entry)
        {
            return Refused("unknown-item", $"no item {key} in this team");
        }

        if (entry.Item.State != PlanStates.WaitingUser)
        {
            return Refused(ProgressFormat.NotWaiting, $"{key} no longer waits on you");
        }

        if (entry.Item.StateSince != since)
        {
            return Refused("question-changed", $"{key} was asked again; read the new question");
        }

        if (answerOf(entry.Item.Options) is not { } answer)
        {
            return new JsonObject { ["bad_request"] = $"{choice} names no option of {key}, and is not other: <text> of at most {ChecklistOtherMaxBytes} bytes" };
        }

        PlanInput[] rows = dismiss is null
            ? ChecklistAnswerRows(entry.Item.Item, answer, string.Empty, now)
            : [new(entry.Item.Item, "drop") { Answer = answer, Cause = new PlanCause(ChecklistCauseDismiss, string.Empty, ProgressFormat.PlanTime(now)) }];
        var written = progress.RecordChecklist(pool, folder, entry.Stream, ChecklistAnswerBy, rows);
        if (written.Refused is { } refused)
        {
            return refused.StartsWith(ProgressFormat.NotWaiting, StringComparison.Ordinal) ? Refused(ProgressFormat.NotWaiting, refused) : Refused("refused", refused);
        }

        var res = new JsonObject { ["id"] = key, ["answers"] = key, ["decision"] = answer };
        var told = dismiss is null ? $"{key} answered: {answer}" : $"{key} dismissed: {dismiss}";
        if (notify is null)
        {
            return res;
        }

        try
        {
            if (notify(OrchestratorAddress(team), "question-answered", told) is { } error)
            {
                res["event_error"] = error;
            }
        }
#pragma warning disable CA1031 // the plan row is the record: no failure of the wake may fail an answer already written
        catch (Exception e)
#pragma warning restore CA1031
        {
            res["event_error"] = e.Message;
        }

        // re-checked at answer time: a member that left the team, or a hand-written row naming another team, gets no copy
        if (entry.Item.AskedFor is { } asker && AskerRefusal(asker, team, readTeam) is { } gone)
        {
            res["asker_event_error"] = $"not sent: {gone}";
        }
        else if (entry.Item.AskedFor is { } member)
        {
            try
            {
                if (notify(member, "question-answered", told) is { } error)
                {
                    res["asker_event_error"] = error;
                }
            }
#pragma warning disable CA1031 // the asker's copy is a courtesy: its failure is noted, never fatal
            catch (Exception e)
#pragma warning restore CA1031
            {
                res["asker_event_error"] = e.Message;
            }
        }

        // ah2-510 L3: a lane child team's answer is copied to the top Orchestrator of its manager; the item team's orchestrator@ above is the lane lead
        if (team is not (null or "" or "default" or "_default") && LaneManager(new LeadRef(team, string.Empty), n => readTeam(n)) is { } manager
            && OrchestratorAddress(manager) is var top && top != OrchestratorAddress(team) && top != entry.Item.AskedFor)
        {
            try
            {
                if (notify(top, "question-answered", told) is { } error)
                {
                    res["manager_event_error"] = error;
                }
            }
#pragma warning disable CA1031 // the top's copy is an alert: its failure is noted, never fatal
            catch (Exception e)
#pragma warning restore CA1031
            {
                res["manager_event_error"] = e.Message;
            }
        }

        return res;
    }

    /// <summary>
    /// The rows that record the user's answer to a waiting item: the answer, with no note, so the item keeps the question, then the move back to the state it held before the question,
    /// and a todo the lead clears by dispatching or settling it, so a lost wake still shows. Their cause is not <c>manual</c>, so later automatic moves still apply.
    /// </summary>
    internal static PlanInput[] ChecklistAnswerRows(string item, string answer, string reference, DateTimeOffset now)
    {
        var cause = new PlanCause(ChecklistCauseAnswer, reference, ProgressFormat.PlanTime(now));
        return [new(item, "wait-orch") { Answer = answer, Cause = cause }, new(item, "unblock") { Cause = cause }, new(item, "todo", Todo: $"{AnswerUserTodo} {item}") { Cause = cause }];
    }

    /// <summary>The label a letter names among <paramref name="options"/>, the text of <c>other: &lt;text&gt;</c>, or null for anything else.</summary>
    private static string? ChecklistAnswerText(IReadOnlyList<string>? options, string choice)
    {
        if (choice is { Length: 1 } && choice[0] is >= 'a' and <= 'd')
        {
            return options is not null && choice[0] - 'a' < options.Count ? options[choice[0] - 'a'] : null;
        }

        return choice.StartsWith("other:", StringComparison.Ordinal) && choice[6..].Trim() is { Length: > 0 } text && System.Text.Encoding.UTF8.GetByteCount(text) <= ChecklistOtherMaxBytes ? text : null;
    }

    /// <summary>The <c>cause.event</c> of the row that records an answer.</summary>
    private const string ChecklistCauseAnswer = "ui-answer";

    /// <summary>The <c>cause.event</c> of the row that records a dismissal.</summary>
    private const string ChecklistCauseDismiss = "ui-dismiss";

    /// <summary>Who wrote an answer the user sent through the web form.</summary>
    private const string ChecklistAnswerBy = "user";

    /// <summary>The longest <c>other:</c> text the form sends.</summary>
    private const int ChecklistOtherMaxBytes = 2000;

    /// <summary>The epoch milliseconds of an ISO time, or null.</summary>
    private static double? Ms(string? text) =>
        text is { Length: > 0 } && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at.ToUnixTimeMilliseconds() : null;

    private static PlanCause? CauseOf(JsonValue row) =>
        row.Member("cause") is { IsMap: true } cause && ProgressFormat.PlanText(cause, "event") is { } ev && ProgressFormat.PlanText(cause, "at") is { } at
            ? new PlanCause(ev, ProgressFormat.PlanText(cause, "ref") ?? string.Empty, at)
            : null;
}
