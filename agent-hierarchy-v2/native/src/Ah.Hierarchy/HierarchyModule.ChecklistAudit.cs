using System.Globalization;
using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.Hierarchy;

/// <summary>A response to a request, as the board audit replays it: when it was filed, its body, and whether it closed or withdrew the request.</summary>
public sealed record ChecklistAuditResponse(string At, string? Body, bool Closed, string? ClosedReason, bool Withdrawn);

/// <summary>A request an item refs, as the board audit reads it from the message store.</summary>
public sealed record ChecklistAuditRequest(
    string Id, string Created, string? To, string? ToName, string? From, string? Slug, string? Sha, string? Item, string? Parent, IReadOnlyList<ChecklistAuditResponse> Responses);

/// <summary>A row whose stored state is not the state its facts give. <c>PreCause</c>: the row has history written before movers stamped their rows, so the finding is unverifiable, not wrong.</summary>
public sealed record ChecklistAuditFinding(string Id, string Stream, string Stored, string Expected, string Why, bool PreCause);

public static partial class HierarchyModule
{
    /// <summary>
    /// The <c>cause.event</c> values a mover stamps (board-derive § E2): a row carrying one is not replayed as written; its fact is replayed through
    /// the mover instead. This is the audit's one list; every other row is recorded and replayed verbatim.
    /// </summary>
    private static readonly HashSet<string> ChecklistMoverCauses = new(
        [ChecklistCauseDispatch, ChecklistCauseClose, ChecklistCauseDone, ChecklistCauseRework, ChecklistCauseOrchMessage, ChecklistEventReply, ChecklistEventAck, ChecklistEventWithdraw, ChecklistEventShip, "backlog-done"],
        StringComparer.Ordinal);

    /// <summary>
    /// <c>ah roster checklist audit</c> (board-derive § E): every checklist row of <paramref name="streams"/> is replayed from its facts through the
    /// movers against an in-memory store, and each row whose stored state differs from the replay's is returned. Writes nothing to <paramref name="progress"/>.
    /// <paramref name="planLog"/> is a stream's plan log text; <paramref name="requestOf"/> reads a request and its responses from the message store.
    /// <paramref name="isAncestor"/>(rev, sha) answers from git, as <c>checklist ship</c> does; null skips the missed-ship check.
    /// </summary>
    public static (int Checked, IReadOnlyList<ChecklistAuditFinding> Disagree) ChecklistAudit(
        IHierarchyProgress progress, string pool, string team, IReadOnlyList<string> streams, Func<string, string?> planLog, Func<string, ChecklistAuditRequest?> requestOf,
        Func<string, string, bool>? isAncestor = null)
    {
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(streams);
        ArgumentNullException.ThrowIfNull(planLog);
        ArgumentNullException.ThrowIfNull(requestOf);
        var ledger = progress.ReadLedger(pool, team);
        var checkedCount = 0;
        var disagree = new List<ChecklistAuditFinding>();
        foreach (var stream in streams)
        {
            var rows = PlanLogRows(planLog(stream));
            foreach (var item in ProgressFormat.FoldPlan(planLog(stream)).Items)
            {
                checkedCount++;
                var itemRows = rows.Where(r => ProgressFormat.PlanText(r.Row, "item") == item.Item).ToList();
                (string Sha, string At)? missedShip = null;
                if (isAncestor is not null && item.State is not (PlanStates.Done or PlanStates.Dropped) && item.Commits is [.., var reported]
                    && !itemRows.Any(r => r.Row.Member("cause") is { IsMap: true } cause && ProgressFormat.PlanText(cause, "event") == ChecklistEventShip)
                    && ledger.FirstOrDefault(r => LedgerText(r, "kind") == ChecklistEventShip && LedgerText(r, "sha") is { } sha && isAncestor(reported, sha)) is { } shipRow)
                {
                    missedShip = (LedgerText(shipRow, "sha")!, LedgerText(shipRow, "at") is { } at && ParseAt(at) is { } when ? ProgressFormat.PlanTime(when) : string.Empty);
                }

                var (expected, why, preCause) = ChecklistReplay(pool, team, stream, item.Item, itemRows, ledger, requestOf, missedShip);
                if (expected != item.State)
                {
                    disagree.Add(new ChecklistAuditFinding(item.Item, stream, item.State, expected, why, preCause));
                }
            }
        }

        return (checkedCount, disagree);
    }

    /// <summary>The newest ship a plan log records (its rows' <c>cause</c> of event <c>ship</c>), as <c>at ref</c>; null when it records none.</summary>
    public static string? ChecklistLastShip(string? planLog) =>
        PlanLogRows(planLog)
            .Select(static r => r.Row.Member("cause") is { IsMap: true } cause && ProgressFormat.PlanText(cause, "event") == ChecklistEventShip
                ? (ProgressFormat.PlanText(cause, "at") ?? string.Empty) + " " + (ProgressFormat.PlanText(cause, "ref") ?? string.Empty)
                : null)
            .Where(static s => s is not null)
            .Max(StringComparer.Ordinal);

    private static List<(int Index, JsonValue Row)> PlanLogRows(string? jsonl)
    {
        var rows = new List<(int, JsonValue)>();
        foreach (var raw in (jsonl ?? string.Empty).Split('\n'))
        {
            if (raw.Trim() is not { Length: > 0 } line)
            {
                continue;
            }

            try
            {
                if (JsonText.Parse(line) is { IsMap: true } row)
                {
                    rows.Add((rows.Count, row));
                }
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // an unreadable line is skipped, as the fold skips it
            }
        }

        return rows;
    }

    private static (string Expected, string Why, bool PreCause) ChecklistReplay(
        string pool, string team, string stream, string item, IReadOnlyList<(int Index, JsonValue Row)> rows, IReadOnlyList<JsonValue> ledger, Func<string, ChecklistAuditRequest?> requestOf,
        (string Sha, string At)? missedShip)
    {
        static string? CauseEvent(JsonValue row) => row.Member("cause") is { IsMap: true } cause ? ProgressFormat.PlanText(cause, "event") : null;
        static string? CauseRef(JsonValue row) => row.Member("cause") is { IsMap: true } cause ? ProgressFormat.PlanText(cause, "ref") : null;
        static string? CauseAt(JsonValue row) => row.Member("cause") is { IsMap: true } cause ? ProgressFormat.PlanText(cause, "at") : null;
        var key = stream + "/" + item;
        var by = OrchestratorAddress(team);
        var dry = new ChecklistDryRun(ledger);
        string? Slug(string id) => requestOf(id)?.Slug;
        string? Parent(string id) => requestOf(id)?.Parent;
        string? Worker(string id) => requestOf(id) is { } r ? ChecklistWorkerOf(r.To, r.ToName) : null;
        var timeline = new List<(string At, int Order, string Why, Action Apply)>();
        var preCause = false;
        foreach (var (index, row) in rows)
        {
            var ev = ProgressFormat.PlanText(row, "event") ?? string.Empty;
            var cause = CauseEvent(row);
            if (cause is not null && ChecklistMoverCauses.Contains(cause))
            {
                continue;
            }

            preCause |= cause is null && (PlanFold.StateEvents.Contains(ev) || ev is "assign" or "ref");
            var at = ProgressFormat.PlanText(row, "ts") ?? string.Empty;
            timeline.Add((at, index, $"recorded {ev} at {at}", () => dry.Append(stream, row)));
        }

        // the facts of the mover rows: one per distinct cause, at its first row's place in the log
        var facts = rows.Where(r => CauseEvent(r.Row) is { } c && ChecklistMoverCauses.Contains(c))
            .GroupBy(r => (Event: CauseEvent(r.Row)!, Ref: CauseRef(r.Row) ?? string.Empty, At: CauseAt(r.Row) ?? string.Empty))
            .Select(g => (g.Key, Index: g.Min(static r => r.Index), Rows: g.Select(static r => r.Row).ToList()))
            .ToList();
        // the one mapping from a fact to its mover; null when the fact's request, or its response at or before it, is gone
        Action? Mover(string ev, string factRef, string at, IReadOnlyList<JsonValue> factRows)
        {
            var request = ev is ChecklistEventShip or "backlog-done" or ChecklistCauseOrchMessage ? null : requestOf(factRef);
            return ev switch
            {
                ChecklistCauseDispatch when request is not null => () => ChecklistOnDispatch(
                    dry, pool, team, key, request.ToName ?? request.To ?? "unknown", request.Id, (request.From ?? "orchestrator") + "@" + team, null,
                    factRows.Select(static r => ProgressFormat.PlanText(r, "base")).FirstOrDefault(static b => b is not null), request.To, request.Slug, request.Sha, at),
                ChecklistEventReply when request is not null && ResponseAt(request, at) is { } reply => () => ChecklistOnMemberReply(
                    dry, pool, team, request.Id, request.Item, request.ToName ?? request.To ?? "member", request.To, reply.Body, AtOf(at), null, Slug, request.Sha,
                    factRows.Select(static r => r.Member("commits") is { IsArray: true } c && c.AsArray() is [.., { IsText: true } sha] ? sha.AsString() : null).FirstOrDefault(static s => s is not null),
                    Parent, Worker),
                ChecklistEventAck when request is not null && ResponseAt(request, at) is { } acked => () => ChecklistOnAck(
                    dry, pool, team, request.Id, request.Item is { } named && !IsBacklogId(named) ? named : null, acked.Body, AtOf(at), by, null, Slug, Parent, Worker, out _),
                ChecklistEventWithdraw when request is not null => () => ChecklistOnEvent(dry, pool, team, new ChecklistEvent(ChecklistEventWithdraw, at, request.Id, by) { ItemId = request.Item, RequestOpen = r => OpenAt(requestOf(r), at) }, by, null),
                ChecklistCauseDone when request is not null && ResponseAt(request, at) is { } done => () => ChecklistOnDone(dry, pool, team, request.Id, request.Item, (done.Body ?? string.Empty).Trim(), by, null, at),
                ChecklistCauseClose when request is not null && ResponseAt(request, at) is { } closed => () => ChecklistOnClose(dry, pool, team, request.Id, request.Item, by, null, closed.ClosedReason, at),
                ChecklistCauseRework when request is not null && factRows.Select(static r => ProgressFormat.PlanText(r, "holder")).FirstOrDefault(static h => h is not null) is { } worker =>
                    () => ChecklistOnRework(dry, pool, team, request.Id, request.Item, request.ToName ?? string.Empty, worker, (request.ToName ?? "reviewer") + "@" + team, null, at),
                ChecklistCauseOrchMessage => () => AnswerWaitingItems(dry, pool, team, factRef.Split(' ')[0], by, null, factRef.Contains(' ', StringComparison.Ordinal) ? factRef[(factRef.IndexOf(' ', StringComparison.Ordinal) + 1)..] : "sendmessage", at),

                // a ship re-decides against the row as it stood then: it carried the row when the row has a recorded ship for its sha (§ E3)
                ChecklistEventShip => () => ChecklistOnEvent(dry, pool, team, new ChecklistEvent(ChecklistEventShip, at, factRef, by), by, null, contains: e => e.Item.Item == item),
                "backlog-done" => () => ChecklistCloseChildren(dry, pool, team, factRef, AtOf(at), by, null),
                _ => null,
            };
        }

        foreach (var (fact, index, factRows) in facts)
        {
            var apply = Mover(fact.Event, fact.Ref, fact.At, factRows);

            // a fact whose request, or whose response at or before it, is gone cannot be re-decided: its rows stand as recorded
            timeline.Add(apply is null
                ? (fact.At, index, $"recorded {fact.Event} {fact.Ref} at {fact.At}", () => { foreach (var r in factRows) { dry.Append(stream, r); } })
                : (fact.At, index, $"{fact.Event} {fact.Ref} at {fact.At}", () => { dry.Now = fact.At; apply(); }));
        }

        // b285: the message-store facts of each ref (a response filed, its close, a withdraw) that no mover row covers are a move that never happened
        bool Covered(string ev, string factRef) => facts.Any(f => f.Key.Event == ev && f.Key.Ref == factRef);
        var missed = new List<(string Event, string Ref, string At)>();
        foreach (var factRef in rows.Where(static r => ProgressFormat.PlanText(r.Row, "event") == "ref").Select(static r => ProgressFormat.PlanText(r.Row, "ref")).OfType<string>().Distinct(StringComparer.Ordinal))
        {
            foreach (var response in requestOf(factRef)?.Responses ?? [])
            {
                // only a member's report is a reply: a withdraw, or a close a close/done row covers, is its own fact (a close file cannot say whether a report preceded it)
                if (!response.Withdrawn && !(response.Closed && (Covered(ChecklistCauseClose, factRef) || Covered(ChecklistCauseDone, factRef))))
                {
                    missed.Add((ChecklistEventReply, factRef, response.At));
                }

                if (response.Withdrawn)
                {
                    missed.Add((ChecklistEventWithdraw, factRef, response.At));
                }

                if (response.Closed)
                {
                    missed.Add((response.ClosedReason == "done" ? ChecklistCauseDone : ChecklistCauseClose, factRef, response.At));
                }
            }
        }

        // b286: a ledger ship that carried the row's reported sha (git, as checklist ship decides) with no ship row on the row
        if (missedShip is { } ship)
        {
            missed.Add((ChecklistEventShip, ship.Sha, ship.At));
        }

        foreach (var (ev, factRef, at) in missed.Where(m => !Covered(m.Event, m.Ref)).Distinct())
        {
            if (Mover(ev, factRef, at, []) is { } apply)
            {
                timeline.Add((at, int.MaxValue, $"missed {ev} {factRef}", apply));
            }
        }

        var state = string.Empty;
        var last = $"recorded add of {item}";
        foreach (var entry in timeline.OrderBy(static t => t.At, StringComparer.Ordinal).ThenBy(static t => t.Order))
        {
            dry.Now = entry.At;
            entry.Apply();
            if (dry.StateOf(stream, item) is { } now && now != state)
            {
                (state, last) = (now, entry.Why);
            }
        }

        return (state, last, preCause);
    }

    private static ChecklistAuditResponse? ResponseAt(ChecklistAuditRequest request, string at) =>
        request.Responses.LastOrDefault(r => string.CompareOrdinal(r.At, at) <= 0);

    /// <summary>Whether <paramref name="request"/> existed at <paramref name="at"/> with no response (a report, close or withdrawal) at or before it.</summary>
    private static bool OpenAt(ChecklistAuditRequest? request, string at) =>
        request is not null && string.CompareOrdinal(request.Created, at) <= 0 && ResponseAt(request, at) is null;

    private static DateTimeOffset AtOf(string at) =>
        DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : DateTimeOffset.UnixEpoch;

    /// <summary>The worker a request names for the rework assign-back: its <c>to_name</c>, unless it went to a reviewer.</summary>
    public static string? ChecklistWorkerOf(string? to, string? toName) => to == "reviewer" ? null : toName;
}

/// <summary>
/// The board audit's in-memory progress store (board-derive § E1): checklist writes fold into an in-memory plan through the one fold, the ledger is
/// the real one read once, and every other write is dropped. It holds no reference to the real store.
/// </summary>
internal sealed class ChecklistDryRun(IReadOnlyList<JsonValue> ledger) : IHierarchyProgress
{
    private readonly Dictionary<string, List<JsonValue>> _rows = new(StringComparer.Ordinal);

    /// <summary>The replay clock: the time of the fact being replayed, which stamps the rows a mover writes with no time of its own.</summary>
    internal string Now { get; set; } = string.Empty;

    internal void Append(string stream, JsonValue row)
    {
        if (!_rows.TryGetValue(stream, out var list))
        {
            _rows[stream] = list = [];
        }

        list.Add(row);
    }

    internal string? StateOf(string stream, string item) => ReadPlan(string.Empty, string.Empty, stream)?.Items.FirstOrDefault(i => i.Item == item)?.State;

    public PlanWriteResult RecordChecklist(string pool, string team, string stream, string by, IReadOnlyList<PlanInput> events, string? session = null)
    {
        foreach (var e in events)
        {
            Append(stream, ProgressFormat.PlanRow(e.At ?? Now, team, stream, e.Item, e.Event, by, e.Title, e.Commits, e.Branch, e.Issue, e.Note, session, e.Rank, e.Area, e.Holder, e.Role, e.Todo, e.Ref, e.Source, e.Parent, e.Cause, e.Base, e.Options, e.Answer));
        }

        return new PlanWriteResult(false, events.Count, null, null);
    }

    public PlanState? ReadPlan(string pool, string team, string stream)
    {
        if (!_rows.TryGetValue(stream, out var rows))
        {
            return null;
        }

        var fold = new PlanFold();
        foreach (var row in rows)
        {
            fold.Apply(row);
        }

        return fold.State;
    }

    public TeamProgress Read(string pool, string team) =>
        new(false, false, [.. _rows.Keys.Where(static s => s != ProgressFormat.RunStream).Select(static s => new StreamProgress(s, true, false, 0, 0, 0, null, null, null, false, false, []))], []);

    public IReadOnlyList<JsonValue> ReadLedger(string pool, string team) => ledger;

    public bool IsOff(string pool, string team) => false;

    public IReadOnlyList<string> EnsureTeam(string pool, string team, ProgressStreamInfo main) => [];

    public IReadOnlyList<string> EnsureStream(string pool, string team, string stream, ProgressStreamInfo info) => [];

    public ProgressAppend AppendReport(string pool, string team, string stream, ProgressStreamInfo info, string from, string responsePath) => throw new NotSupportedException("the board audit writes no report");

    public bool UpdateRollupBlock(string pool, string team, string stream, string from, string responsePath) => false;

    public bool AcknowledgeRollupLine(string pool, string team, string responsePath) => false;

    public string StreamOf(string pool, string team, string member) => member;

    public ProgressStreamInfo Describe(string pool, string team, string stream) => throw new NotSupportedException("the board audit describes no stream");

    public ProgressDone MarkDone(string pool, string team, string stream) => throw new NotSupportedException("the board audit marks nothing done");

    public IReadOnlyList<ProgressNudge> PendingNudges(string pool, string team) => [];

    public void MarkNudged(string pool, string team, ProgressNudge nudge)
    {
    }

    public void AppendSession(string pool, string team, JsonObject row)
    {
    }

    public LedgerAppend AppendLedger(string pool, string team, LedgerEvent ev) => new(false, false, null);

    public string? RecallDigest(string pool, string team, bool includeLanes) => null;

    public int ForwardLedgers(string pool, IProcessRunner runner, string pluginRoot, string? path, string repo, Action<string, string>? alarm = null) => 0;

    public (long Children, long WallMs) EndForwardRun() => (0, 0);

    public (long Children, long WallMs) LastForwardRun() => (0, 0);

    public void RecordReport(string pool, string team, string stream, string from, string responsePath, ProgressAppend appended)
    {
    }

    public PlanWriteResult RecordPlan(string pool, string team, string stream, string by, IReadOnlyList<PlanInput> events, string? session = null) => new(false, 0, null, null);

    public PlanImportResult ImportPlan(string pool, string team, string stream, string by, string? session = null) => new(false, 0, [], null, null);
}
