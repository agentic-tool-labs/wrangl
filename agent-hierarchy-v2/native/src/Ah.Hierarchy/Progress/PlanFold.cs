using System.Globalization;
using Ah.Core.Json;

namespace Ah.Hierarchy;

/// <summary>An incremental fold of a stream's plan log: feed it the whole log or only the rows appended since the last call.</summary>
public interface IPlanFold
{
    /// <summary>The items folded so far, in the order they were added.</summary>
    PlanState State { get; }

    /// <summary>What the events changed, in the order they were folded.</summary>
    IReadOnlyList<PlanChange> Changes { get; }

    /// <summary>How many readable rows the fold has taken.</summary>
    int Rows { get; }

    /// <summary>The time of the latest row folded, or empty.</summary>
    string LastAt { get; }

    /// <summary>Folds the rows of <paramref name="jsonl"/>, one JSON object per line; a line that is not an object is skipped.</summary>
    void ApplyText(string? jsonl);

    /// <summary>Folds one row.</summary>
    void Apply(JsonValue row);
}

/// <summary>
/// The fold of one stream's plan log, row by row. The last applicable event wins; a row that is not a JSON object, or names no known
/// event, is skipped; a row for an item that was never added is ignored and counted in <see cref="PlanState.UnknownEvents"/>. Feed it the
/// whole log or only the rows appended since the last call: the state and the <see cref="Changes"/> are the same either way, because
/// <see cref="PlanChange.Seq"/> counts the readable rows the fold has taken.
/// </summary>
internal sealed class PlanFold : IPlanFold
{
    private readonly List<string> _order = [];
    private readonly Dictionary<string, PlanItem> _items = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _beforeHold = new(StringComparer.Ordinal);
    private readonly List<PlanChange> _changes = [];
    private int _unknown;
    private int _seq;

    /// <summary>The items folded so far, in the order they were added.</summary>
    public PlanState State => new([.. _order.Select(o => _items[o])], _unknown);

    /// <summary>The changes the rows implied, in log order.</summary>
    public IReadOnlyList<PlanChange> Changes => _changes;

    /// <summary>The number of readable rows taken.</summary>
    public int Rows => _seq;

    /// <summary>The latest <c>ts</c> of the rows that named a known event, empty when there was none. Row times are fixed-width UTC text, so the text order is the time order.</summary>
    public string LastAt { get; private set; } = string.Empty;

    /// <summary>Takes every line of <paramref name="jsonl"/>; a line that is not JSON is skipped.</summary>
    public void ApplyText(string? jsonl)
    {
        foreach (var raw in (jsonl ?? string.Empty).Split('\n'))
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

            Apply(row);
        }
    }

    /// <summary>Takes one row.</summary>
    public void Apply(JsonValue row)
    {
        if (!row.IsMap)
        {
            return;
        }

        var seq = _seq++;
        if (ProgressFormat.PlanText(row, "event") is not { } eventName || ProgressFormat.PlanText(row, "item") is not { } item || !ProgressFormat.PlanEvents.Contains(eventName, StringComparer.Ordinal))
        {
            return;
        }

        var ts = ProgressFormat.PlanText(row, "ts") ?? string.Empty;
        if (string.CompareOrdinal(ts, LastAt) > 0)
        {
            LastAt = ts;
        }

        var by = ProgressFormat.PlanText(row, "by") ?? string.Empty;
        if (eventName == "add")
        {
            if (!_items.ContainsKey(item))
            {
                _order.Add(item);
                var title = ProgressFormat.PlanText(row, "title") ?? item;
                _items[item] = new PlanItem(item, title, PlanStates.NotStarted, ts, null, null, [], null, null, null, UpdatedAt: ts, StateSince: ts);
                _changes.Add(new PlanChange(ts, seq, item, "added", null, title, by));
            }

            return;
        }

        if (!_items.TryGetValue(item, out var current))
        {
            _unknown++;
            return;
        }

        var next = eventName switch
        {
            // a failing verdict sends the work back, so its DONE no longer stands; a dispatch's start, such as a reviewer's, leaves it
            "start" => Move(current, PlanStates.InProgress) with
            {
                StartedAt = ts,
                ReportedDone = current.ReportedDone && ProgressFormat.PlanText(row, "note")?.StartsWith(HierarchyModule.ReworkNote, StringComparison.Ordinal) != true,
            },
            "done" => Move(current, PlanStates.Done) with
            {
                DoneAt = ts,
                // a close by ship or ack names no commits of its own and keeps the ones the DONE report recorded
                Commits = row.Member("commits") is { IsArray: true } list ? [.. list.AsArray().Where(static c => c.IsText).Select(static c => c.AsString())] : current.Commits,
                Branch = ProgressFormat.PlanText(row, "branch") ?? current.Branch,
                Note = ProgressFormat.PlanText(row, "note") ?? current.Note,
                DoneCause = row.Member("cause") is { IsMap: true } doneCause && ProgressFormat.PlanText(doneCause, "event") is { } doneEvent
                    ? new PlanCause(doneEvent, ProgressFormat.PlanText(doneCause, "ref") ?? string.Empty, ProgressFormat.PlanText(doneCause, "at") ?? string.Empty)
                    : null,
            },
            "drop" => Move(current, PlanStates.Dropped) with { Note = ProgressFormat.PlanText(row, "note") ?? current.Note, Answer = ProgressFormat.PlanText(row, "answer") ?? current.Answer },
            "reopen" => Move(current, PlanStates.InProgress) with { DoneAt = null },
            "next" => Move(current, PlanStates.Next),
            // each DONE report states where the work stands now: one with no sha leaves no older sha behind for a ship to close on
            "review" => Move(current, PlanStates.InReview) with
            {
                Commits = row.Member("commits") is { IsArray: true } reported ? [.. reported.AsArray().Where(static c => c.IsText).Select(static c => c.AsString())] : [],
                Branch = ProgressFormat.PlanText(row, "branch"),

                // a reviewer's dispatch (role reviewer) reviews what stands and reports nothing; an older review row was the DONE report itself
                ReportedDone = current.ReportedDone || ProgressFormat.PlanText(row, "role") != "reviewer",
            },
            // a DONE report: the same facts as a review row, waiting on the Orchestrator rather than on a reviewer
            "report" => Move(current, PlanStates.Reported) with
            {
                Commits = row.Member("commits") is { IsArray: true } done ? [.. done.AsArray().Where(static c => c.IsText).Select(static c => c.AsString())] : [],
                Branch = ProgressFormat.PlanText(row, "branch"),
                ReportedDone = true,
            },
            "approve" => Move(current, PlanStates.Approved),
            "testing" => Move(current, PlanStates.Testing) with { Note = ProgressFormat.PlanText(row, "note") ?? current.Note },
            "block" => Hold(current, PlanStates.Blocked, ProgressFormat.PlanText(row, "note")),
            // a question replaces the last one's options and answer, so a re-ask with no options has none
            "wait-user" => Hold(current, PlanStates.WaitingUser, ProgressFormat.PlanText(row, "note")) with
            {
                Options = row.Member("options") is { IsArray: true } options ? [.. options.AsArray().Where(static o => o.IsText).Select(static o => o.AsString())] : null,
                Answer = null,
                AskedFor = ProgressFormat.PlanText(row, "asked_for"),
                Class = ProgressFormat.PlanText(row, "class"),
                AskedBy = by.Length > 0 ? by : null,
                HeldNote = null,
            },
            "wait-orch" => Hold(current, PlanStates.WaitingOrch, ProgressFormat.PlanText(row, "note")) with { Answer = ProgressFormat.PlanText(row, "answer") ?? current.Answer },
            "role" => current with { Role = ProgressFormat.PlanText(row, "role") ?? current.Role },
            "todo" => current with { Todo = ProgressFormat.PlanText(row, "todo") is { } todo ? (todo.Length == 0 ? null : todo) : current.Todo },
            "ref" => ProgressFormat.PlanText(row, "ref") is { } reference && !(current.Refs ?? []).Contains(reference, StringComparer.Ordinal)
                ? current with { Refs = [.. current.Refs ?? [], reference] }
                : current,
            "unref" => ProgressFormat.PlanText(row, "unref") is { } gone && current.Refs is { } held && held.Contains(gone, StringComparer.Ordinal)
                ? current with { Refs = [.. held.Where(r => r != gone)] }
                : current,
            "unblock" => Release(current),
            "retitle" => current with { Title = ProgressFormat.PlanText(row, "title") ?? current.Title },
            "link" => current with { Issue = row.Member("issue") is { IsNumber: true } n ? (int)n.AsNumber() : current.Issue },
            // a note on a waiting_user item that is not a manual one (an automatic close's, or an older row with no cause) is trail only: the item keeps its question as the note
            "note" when current.State == PlanStates.WaitingUser && (row.Member("cause") is not { IsMap: true } noteCause || ProgressFormat.PlanText(noteCause, "event") != ManualCause) => current with { HeldNote = ProgressFormat.PlanText(row, "note") },
            "note" => current with { Note = ProgressFormat.PlanText(row, "note") ?? current.Note },
            "rank" => current with { Rank = row.Member("rank") is { IsNumber: true } r ? (int)r.AsNumber() : current.Rank },
            "area" => current with { Area = ProgressFormat.PlanText(row, "area") ?? current.Area },
            "assign" => current with { Holder = ProgressFormat.PlanText(row, "holder") ?? current.Holder },
            "outcome" => current with { Outcome = ProgressFormat.PlanText(row, "outcome") ?? current.Outcome },
            "size" => current with { Size = ProgressFormat.PlanText(row, "size") ?? current.Size },
            "wave" => current with { Wave = ProgressFormat.PlanText(row, "wave") ?? current.Wave },
            "depend" => row.Member("depends") is { IsArray: true } deps
                ? current with { Depends = [.. deps.AsArray().Where(static d => d.IsText).Select(static d => d.AsString())] }
                : current,
            "source" => ProgressFormat.PlanText(row, "source") is { } source && !(current.Sources ?? []).Contains(source, StringComparer.Ordinal)
                ? current with { Sources = [.. current.Sources ?? [], source] }
                : current,

            // the writer sets issue on an ext row only for an issue of the origin repository
            "ext" when row.Member("clear") is { IsTruthy: true } => current with { Ext = null, Issue = null },
            "ext" => current with
            {
                Ext = ProgressFormat.PlanText(row, "ext") ?? current.Ext,
                Issue = row.Member("issue") is { IsNumber: true } issue ? (int)issue.AsNumber() : current.Issue,
            },

            // task and an unknown kind fold to null, so an unset kind and an explicit task look the same; an empty parent clears it
            "kind" => current with { Kind = ProgressFormat.PlanText(row, "kind") is "project" or "goal" or "epic" or "deliverable" ? ProgressFormat.PlanText(row, "kind") : null },
            "parent" => current with { Parent = ProgressFormat.PlanText(row, "parent") is { Length: > 0 } parent ? parent : null },
            _ => current,
        };
        var manual = row.Member("cause") is { IsMap: true } cause && ProgressFormat.PlanText(cause, "event") == ManualCause;
        var overrideAt = StateEvents.Contains(eventName) && manual && by.StartsWith(OrchestratorPrefix, StringComparison.Ordinal) ? ts : current.OverrideAt;
        _items[item] = next with
        {
            UpdatedAt = ts,
            // a re-ask is a new question: an answer typed against the old one must see the time move (spec ah-ui-answer §4)
            StateSince = next.State != current.State || eventName == "wait-user" ? ts : current.StateSince ?? current.UpdatedAt,
            OverrideAt = overrideAt,
            Base = current.Base ?? ProgressFormat.PlanText(row, "base"), // the first dispatch's base: a re-dispatch does not move it
            // rework: a start after a failing verdict or a failed ship gate; any other state row ends it
            Rework = eventName == "start"
                ? ProgressFormat.PlanText(row, "note")?.StartsWith(HierarchyModule.ReworkNote, StringComparison.Ordinal) == true || current.Note?.StartsWith(HierarchyModule.GateFailedNote, StringComparison.Ordinal) == true
                : !StateEvents.Contains(eventName) && next.Rework,
            Reviewing = eventName == "review" ? ProgressFormat.PlanText(row, "role") == "reviewer" : !StateEvents.Contains(eventName) && next.Reviewing,
        };
        var change = eventName switch
        {
            "retitle" when next.Title != current.Title => new PlanChange(ts, seq, item, "retitle", current.Title, next.Title, by),
            "rank" when next.Rank != current.Rank => new PlanChange(ts, seq, item, "priority", Number(current.Rank), Number(next.Rank), by),
            "drop" when next.State != current.State => new PlanChange(ts, seq, item, "dropped", current.State, next.State, by),
            "start" or "done" or "reopen" or "next" or "review" or "report" or "approve" or "testing" or "block" or "wait-user" or "wait-orch" or "unblock" when next.State != current.State =>
                new PlanChange(ts, seq, item, "state", current.State, next.State, by),
            _ => null,
        };
        if (change is not null)
        {
            _changes.Add(change);
        }
    }

    /// <summary>The <c>cause.event</c> of a row the Orchestrator wrote by hand (<c>checklist set</c> or <c>done</c>): such a state row is an override.</summary>
    internal const string ManualCause = "manual";

    /// <summary>The events that set an item's state; an Orchestrator row of one of these whose cause is <see cref="ManualCause"/> is an override.</summary>
    internal static readonly HashSet<string> StateEvents = new(["start", "done", "drop", "reopen", "testing","next", "block", "wait-user", "wait-orch", "unblock", "review", "report", "approve"], StringComparer.Ordinal);

    /// <summary>The start of every Orchestrator address (<see cref="HierarchyModule.OrchestratorAddress"/>).</summary>
    private const string OrchestratorPrefix = "orchestrator@";

    private static string? Number(int? value) => value?.ToString(CultureInfo.InvariantCulture);

    private static PlanItem Move(PlanItem item, string state) => item with { State = state };

    /// <summary>A hold (blocked or waiting for the user): the state before the first hold is kept for <c>unblock</c>.</summary>
    private PlanItem Hold(PlanItem item, string state, string? note)
    {
        if (item.State is not (PlanStates.Blocked or PlanStates.WaitingUser or PlanStates.WaitingOrch))
        {
            _beforeHold[item.Item] = item.State;
        }

        return item with { State = state, Note = note ?? item.Note };
    }

    /// <summary>An <c>unblock</c>: a held item returns to the state before the first hold; any other item is left as it is.</summary>
    private PlanItem Release(PlanItem item)
    {
        if (item.State is not (PlanStates.Blocked or PlanStates.WaitingUser or PlanStates.WaitingOrch))
        {
            return item;
        }

        var back = _beforeHold.GetValueOrDefault(item.Item, PlanStates.NotStarted);
        _ = _beforeHold.Remove(item.Item);
        return item with { State = back };
    }
}
