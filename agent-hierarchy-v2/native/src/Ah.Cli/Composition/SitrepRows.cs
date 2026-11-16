using System.Globalization;
using Ah.Core.Json;
using Ah.Hierarchy;
using Ah.Watch;

namespace Ah.Cli.Composition;

/// <summary>
/// The body of <c>sitrep:&lt;dirId&gt;/&lt;team&gt;</c> (ah-ui-ia §3.3, §3.3a): Completed, In progress and Up next for a team and its confirmed lanes, with the
/// progress folders it read. Every time in the body is epoch milliseconds.
/// </summary>
internal static class SitrepRows
{
    internal const int Cap = 10;

    /// <summary>How long an item may sit reported or waiting on the Orchestrator before its row says stale (b447).</summary>
    internal const double ReportedStaleMs = 3_600_000;

    /// <summary>The regions in the order every surface stacks them. <c>list</c> and <c>total</c> name the body's rows and count; an optional region is left out when empty; folded is how it starts.</summary>
    private static readonly (string Col, string List, string? Total, string Label, bool Optional, bool Folded)[] Regions =
    [
        ("c", "completed", "completed_total", "Completed", false, false),
        ("p", "in_progress", "in_progress_total", "In progress", false, false),
        ("n", "up_next", null, "Up next", false, false),
        ("w", "waiting_on_you", "waiting_on_you_total", "Waiting on you", true, false),
        ("b", "blocked", "blocked_total", "Blocked", true, false),
        ("m", "not_moving", "not_moving_total", "Report in, no one on it", true, true),
        ("s", "stale", "stale_total", "Stale", true, true),
        ("o", "up_next_old", "up_next_old_total", "Up next, old", true, true),
    ];

    private static JsonValue RegionsBody()
    {
        var served = new JsonArray();
        foreach (var r in Regions)
        {
            served.Add(JsonValue.FromObject(new JsonObject
            {
                ["col"] = r.Col,
                ["list"] = r.List,
                ["total"] = r.Total is null ? JsonValue.Null : r.Total,
                ["label"] = r.Label,
                ["optional"] = r.Optional,
                ["folded"] = r.Folded,
            }));
        }

        return JsonValue.FromArray(served);
    }

    /// <summary>A plan item in scope: its team, and its lane stream when it belongs to a lane (§2.2).</summary>
    internal sealed record Item(string Team, string? Lane, string Stream, PlanItem Plan);

    /// <summary>A queued row of a team in scope.</summary>
    internal sealed record Queued(string Team, string? Lane, QueueRow Row);

    /// <summary>A ledger row of a team in scope; <see cref="Lane"/> is set when the ledger is a lane team's.</summary>
    internal sealed record Ledger(string Team, string? Lane, JsonValue Row);

    /// <summary>The Sitrep of <paramref name="team"/>; <paramref name="backlog"/> is asked only when the queue leaves room under the cap.</summary>
    internal static (JsonValue Body, IReadOnlyList<string> Teams) Of(ServiceGraph g, string hierarchyDir, string team, double nowMs, Func<IReadOnlyList<PlanItem>> backlog, Func<string, (ChecklistLiveness? Live, IReadOnlyList<JsonValue> Decisions, IReadOnlyDictionary<string, string> Owners)>? work = null)
    {
        // the team first, then each confirmed lane team (§3.1), by stream name
        var scope = new List<(string Team, string? LaneOfTeam)> { (team, null) };
        var closedLanes = new HashSet<string>(StringComparer.Ordinal);
        if (g.Roster.ReadTeamRecord(hierarchyDir, team) is { IsMap: true } record && record.Member("streams") is { IsMap: true } streams)
        {
            foreach (var stream in streams.AsObject().Keys.Order(StringComparer.Ordinal))
            {
                var child = HierarchyModule.LaneChildName(team, stream);
                if (g.Roster.ReadTeamRecord(hierarchyDir, child) is { IsMap: true } childRecord
                    && HierarchyModule.ConfirmedLaneStream(team, record, child, childRecord) == stream)
                {
                    scope.Add((child, stream));
                    if (childRecord.Member("closed") is { IsMap: true })
                    {
                        _ = closedLanes.Add(child);
                    }
                }
            }
        }

        var laneStreams = scope.Where(static s => s.LaneOfTeam is not null).Select(static s => s.LaneOfTeam!).ToHashSet(StringComparer.Ordinal);
        string? LaneOf(string? laneOfTeam, string stream) => laneOfTeam ?? (laneStreams.Contains(stream) ? stream : null);
        var items = new List<Item>();
        var queued = new List<Queued>();
        var ledger = new List<Ledger>();
        foreach (var (name, laneOfTeam) in scope)
        {
            var folder = HierarchyModule.ProgressTeamOf(name);
            var closed = closedLanes.Contains(name);

            // A closed lane is gone: what it finished still counts, what it left open shows nowhere and stays in its plan.
            // ponytail: a member spawned into a closed lane reads open on its summary but stays out of here until the lane is reopened; pass liveness in if that shows up.
            items.AddRange(g.Checklists.Of(hierarchyDir, folder)
                .Where(e => !closed || e.Item.State is "done" or "dropped")
                .Select(e => new Item(name, LaneOf(laneOfTeam, e.Stream), e.Stream, e.Item)));
            if (!closed)
            {
                queued.AddRange(QueueRows.Of(g, hierarchyDir, name, nowMs).Select(r => new Queued(name, LaneOf(laneOfTeam, StreamOfQueueKey(r.Item)), r)));
            }

            ledger.AddRange(g.Hierarchy.Progress.ReadLedger(hierarchyDir, folder).Select(r => new Ledger(name, laneOfTeam, r)));
        }

        var facts = scope.Select(s => s.Team).ToDictionary(static t => t, t => work?.Invoke(t), StringComparer.Ordinal);
        var decisions = facts.Where(f => !closedLanes.Contains(f.Key)).SelectMany(static f => f.Value?.Decisions.Select(d => (f.Key, Row: d)) ?? []).ToList();
        var open = scope.Where(s => !closedLanes.Contains(s.Team)).ToList();
        return (
            Compose(
                items, queued, backlog, () => DirItems(g, hierarchyDir), ledger, nowMs,
                live: i => facts.GetValueOrDefault(i.Team)?.Live,
                owner: i => facts.GetValueOrDefault(i.Team)?.Owners.GetValueOrDefault(KeyOf(i.Stream, i.Plan.Item)),
                decisions: decisions,
                testing: () => [.. backlog().Where(static b => b.State == "testing")],
                working: [.. open.Where(s => facts.GetValueOrDefault(s.Team)?.Live is not null).Select(s => (s.Team, s.LaneOfTeam, facts[s.Team]!.Value.Live!))],
                jobs: [.. open.SelectMany(s => WatchModule.JobsFromFiles(g.Hierarchy.Status, hierarchyDir, s.Team).Select(j => (s.Team, s.LaneOfTeam, j)))]),
            [.. scope.Select(static s => HierarchyModule.ProgressTeamOf(s.Team))]);
    }

    /// <summary>The checklist items of every team in the directory: "unplanned" is judged against these, as S6b reads it (Orchestrator ruling F8).</summary>
    internal static IReadOnlyList<PlanItem> DirItems(ServiceGraph g, string hierarchyDir) =>
    [
        .. new string?[] { null }.Concat(g.Roster.ListTeamNames(hierarchyDir))
            .Select(HierarchyModule.ProgressTeamOf)
            .Distinct(StringComparer.Ordinal)
            .SelectMany(folder => g.Checklists.Of(hierarchyDir, folder).Select(static e => e.Item)),
    ];

    /// <summary>The stream of a queue row's <c>stream/item</c> key.</summary>
    private static string StreamOfQueueKey(string key) => key.IndexOf('/', StringComparison.Ordinal) is var at and >= 0 ? key[..at] : key;

    private static string KeyOf(string stream, string item) => stream + ":" + item;

    private static double? Ms(string? text) =>
        text is { Length: > 0 } && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at.ToUnixTimeMilliseconds() : null;

    private static JsonValue Num(double? ms) => ms is { } v ? (JsonValue)v : JsonValue.Null;

    private static JsonValue Text(string? text) => text is null ? JsonValue.Null : (JsonValue)text;

    /// <summary>A plan item's holder as served: a sheet's <c>—</c> says nobody holds it.</summary>
    private static JsonValue HolderOf(string? holder) => Text(holder is "—" ? null : holder);

    private static JsonValue Copy(JsonValue row, string key) => row.Member(key) is { } v ? v : JsonValue.Null;

    /// <summary>
    /// The body from the items, queue rows and ledger rows in scope. <paramref name="backlog"/> (the directory's backlog rows) and <paramref name="dirItems"/>
    /// (every checklist item of the directory, for the planned test) are asked only when the queue leaves room under the cap.
    /// </summary>
    internal static JsonValue Compose(
        IReadOnlyList<Item> items, IReadOnlyList<Queued> queued, Func<IReadOnlyList<PlanItem>> backlog, Func<IReadOnlyList<PlanItem>> dirItems, IReadOnlyList<Ledger> ledger,
        double? nowMs = null, Func<Item, double?>? messageMs = null, Func<Item, ChecklistLiveness?>? live = null, Func<Item, string?>? owner = null, IReadOnlyList<(string Team, JsonValue Row)>? decisions = null, Func<IReadOnlyList<PlanItem>>? testing = null,
        IReadOnlyList<(string Team, string? Lane, ChecklistLiveness Live)>? working = null, IReadOnlyList<(string Team, string? Lane, JobFact Job)>? jobs = null)
    {
        var times = new List<double>();

        // Completed: the 10 newest done items by their last done event (a reopen clears it); an item with no done event by its own
        // state_since, then its own updated_at (Orchestrator ruling F4); an item with no time of its own sorts last
        var done = items.Where(static i => i.Plan.State == "done")
            .Select(static i => (Item: i, At: Ms(i.Plan.DoneAt) ?? Ms(i.Plan.StateSince) ?? Ms(i.Plan.UpdatedAt)))
            .OrderByDescending(static d => d.At ?? double.MinValue)
            .ThenBy(static d => KeyOf(d.Item.Stream, d.Item.Plan.Item), StringComparer.Ordinal)
            .ToList();
        var top = done.Take(Cap).ToList();
        // the floor is the oldest dated item of the ten; an undated one sets none
        var dated = top.Where(static d => d.At is not null).ToList();
        double? floor = dated.Count > 0 ? dated.Min(static d => d.At!.Value) : null;
        var rows = new List<(double At, bool Record, string Key, JsonObject Row)>();
        foreach (var (item, at) in top)
        {
            var key = KeyOf(item.Stream, item.Plan.Item);
            rows.Add((at ?? double.MinValue, false, key, new JsonObject
            {
                ["kind"] = "item",
                ["key"] = key,
                ["title"] = HierarchyModule.ChecklistTitle(item.Plan.Title),
                ["team"] = item.Team,
                ["stream"] = item.Stream,
                ["lane"] = Text(item.Lane),
                ["holder"] = HolderOf(item.Plan.Holder),
                ["done_at"] = Num(at),
            }));
        }

        // ship and merge records (§3.3a): deduped by (kind, sha, ref) keeping the earliest, lane set if any copy is a lane's
        var records = ledger
            .Select(static l => (l.Team, l.Lane, l.Row, Kind: l.Row.Member("kind") is { IsText: true } k ? k.AsString() : null, At: l.Row.Member("at") is { IsText: true } a ? Ms(a.AsString()) : null))
            .Where(static r => r.Kind is "ship" or "merge" && r.At is not null && r.Row.Member("sha") is { IsText: true } sha && sha.AsString().Length > 0)
            .GroupBy(static r => (r.Kind, Sha: Copy(r.Row, "sha").IsText ? Copy(r.Row, "sha").AsString() : "", Ref: Copy(r.Row, "ref").IsText ? Copy(r.Row, "ref").AsString() : ""))
            .Select(static g =>
            {
                var first = g.OrderBy(static r => r.At).First();
                return (first.Team, Lane: g.Select(static r => r.Lane).FirstOrDefault(static l => l is not null), first.Row, Kind: first.Kind!, At: first.At!.Value, g.Key.Sha, g.Key.Ref);
            })
            .Where(r => floor is null || r.At >= floor)
            .OrderByDescending(static r => r.At)
            .ThenBy(static r => r.Kind + ":" + r.Sha + ":" + r.Ref, StringComparer.Ordinal)
            .Take(Cap);
        foreach (var r in records)
        {
            var key = r.Kind + ":" + r.Sha + ":" + r.Ref;
            var row = new JsonObject { ["kind"] = r.Kind, ["key"] = key, ["sha"] = Copy(r.Row, "sha"), ["ref"] = Copy(r.Row, "ref") };
            if (r.Kind == "ship")
            {
                row["tag"] = Copy(r.Row, "tag");
                row["confirmed"] = Copy(r.Row, "confirmed");
            }
            else
            {
                row["into"] = Copy(r.Row, "into");
            }

            row["team"] = r.Team;
            row["lane"] = Text(r.Lane);
            row["at"] = r.At;
            rows.Add((r.At, true, key, row));
        }

        var completed = new JsonArray();
        foreach (var row in rows.OrderByDescending(static r => r.At).ThenBy(static r => r.Record).ThenBy(static r => r.Key, StringComparer.Ordinal))
        {
            completed.Add(JsonValue.FromObject(row.Row));
            if (row.At > double.MinValue)
            {
                times.Add(row.At);
            }
        }

        // last activity (spec ah-sitrep-events §4): the item's newest plan row, the newest message of its ref exchanges, and its ledger rows by ref.
        // Commit times are not read from git (known gap).
        var ledgerRows = ledger.Select(static l => l.Row).ToList();
        double? LastActivity(Item item) => HierarchyModule.ChecklistLastActivityMs(item.Plan, ledgerRows, messageMs?.Invoke(item));

        // Every open item by its one work (spec ah-ui-focus §2), newest last activity first. With no clock nothing is stale.
        var open = items
            .Select(i =>
            {
                var last = LastActivity(i);
                return (Item: i, Since: Ms(i.Plan.StateSince) ?? Ms(i.Plan.UpdatedAt), Last: last,
                    Work: HierarchyModule.ChecklistWork(i.Plan.State, owner?.Invoke(i) ?? i.Plan.Holder, i.Plan.Refs, live?.Invoke(i), nowMs is null ? null : last, nowMs ?? 0));
            })
            .Where(static m => m.Work is not null)
            .OrderByDescending(static m => m.Last ?? double.MinValue)
            .ThenBy(static m => KeyOf(m.Item.Stream, m.Item.Plan.Item), StringComparer.Ordinal)
            .ToList();
        // the one fold, a team at a time: a decision hides an item of its own team only (the scope can hold lanes)
        var waitingItems = open.GroupBy(static m => m.Item.Team, StringComparer.Ordinal)
            .SelectMany(team => HierarchyModule.ChecklistWaiting(
                (decisions ?? []).Where(d => d.Team == team.Key),
                static d => d.Row.Member("item") is { IsText: true } item ? item.AsString() : null,
                team,
                static m => m.Item.Plan.State,
                static m => m.Item.Plan.Item).Items)
            .OrderByDescending(static m => m.Last ?? double.MinValue)
            .ThenBy(static m => KeyOf(m.Item.Stream, m.Item.Plan.Item), StringComparer.Ordinal)
            .ToList();
        // an item in review or waiting for ship is work under way: it reads as in progress, and leads so an old report stays inside the column's cap
        var moving = open.Where(static m => m.Work == "working" || m.Item.Plan.State is "in_review" or "approved")
            .OrderBy(static m => m.Item.Plan.State is "in_review" or "approved" ? 0 : 1)
            .ToList();
        var held = open.Where(static m => m.Work == "blocked").ToList();
        var stale = open.Where(static m => m.Work == "stale").ToList();
        var notMoving = open.Where(static m => m.Work is "with_lead" or "no_one" && m.Item.Plan.State is not ("in_review" or "approved")).ToList();
        JsonArray Group(IEnumerable<(Item Item, double? Since, double? Last, string? Work)> group)
        {
            var array = new JsonArray();
            foreach (var (item, since, last, work) in group.Take(Cap))
            {
                var row = new JsonObject
                {
                    ["key"] = KeyOf(item.Stream, item.Plan.Item),
                    ["title"] = HierarchyModule.ChecklistTitle(item.Plan.Title),
                    ["team"] = item.Team,
                    ["stream"] = item.Stream,
                    ["lane"] = Text(item.Lane),
                    // an item waiting on the user names who asked, not the member who last held it
                    ["holder"] = item.Plan.State == "waiting_user" ? JsonValue.Null : HolderOf(item.Plan.Holder),
                    ["asker"] = item.Plan.State == "waiting_user" ? Text(item.Plan.AskedBy) : JsonValue.Null,
                    ["role"] = Text(item.Plan.Role),
                    ["state"] = item.Plan.State,
                    ["work"] = Text(work),
                    ["note"] = Text(item.Plan.Note),
                    ["since"] = Num(since),
                    ["last_activity"] = Num(last),
                    // left with the lead too long: a report or a wait on the Orchestrator over an hour old
                    ["stale"] = item.Plan.State is "reported" or "waiting_orch" && nowMs is { } at && since is { } s && at - s > ReportedStaleMs,
                };
                if (item.Plan.Answer is { } answer)
                {
                    row["answer"] = answer;
                }

                array.Add(JsonValue.FromObject(row));
            }

            return array;
        }

        // Waiting on you: the team's open decisions, then its items no decision names
        var waitingRows = new JsonArray();
        foreach (var (team, row) in (decisions ?? []).Take(Cap))
        {
            waitingRows.Add(JsonValue.FromObject(new JsonObject
            {
                ["kind"] = "decision",
                ["key"] = Copy(row, "id"),
                ["title"] = Copy(row, "question"),
                ["team"] = team,
                ["since"] = Num(row.Member("time") is { IsText: true } time ? Ms(time.AsString()) : null),
            }));
        }

        foreach (var row in Group(waitingItems.Take(Math.Max(0, Cap - waitingRows.Count))))
        {
            waitingRows.Add(row);
        }

        var inProgress = Group(moving);

        // backlog rows marked as in a batch under QA: no checklist item says so
        var testingRows = testing?.Invoke() ?? [];
        foreach (var b in testingRows.OrderBy(static b => b.Rank ?? int.MaxValue).ThenBy(static b => b.Item, StringComparer.Ordinal).Take(Math.Max(0, Cap - inProgress.Count)))
        {
            inProgress.Add(JsonValue.FromObject(new JsonObject
            {
                ["key"] = b.Item,
                ["title"] = b.Title,
                ["team"] = JsonValue.Null,
                ["stream"] = JsonValue.Null,
                ["lane"] = JsonValue.Null,
                ["holder"] = Text(b.Holder),
                ["role"] = Text(b.Role),
                ["state"] = b.State,
                ["work"] = JsonValue.Null,
                ["note"] = Text(b.Note),
                ["since"] = Num(Ms(b.StateSince) ?? Ms(b.UpdatedAt)),
                ["last_activity"] = Num(Ms(b.UpdatedAt)),
                ["source"] = "backlog",
            }));
        }

        // members working right now (agent state working, or an open exchange to a live member) that no row above already names as holder
        var shown = moving.Select(m => (m.Item.Team, Holder: owner?.Invoke(m.Item) ?? m.Item.Plan.Holder)).ToHashSet();
        var members = (working ?? [])
            .SelectMany(static w => w.Live.Working.Concat(w.Live.OnExchange?.Keys ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(m => (w.Team, w.Lane, w.Live, Member: m)))
            .Where(w => !shown.Contains((w.Team, w.Member)))
            .ToList();
        foreach (var (team, lane, l, member) in members.Take(Math.Max(0, Cap - inProgress.Count)))
        {
            // what it is on: the open item it holds, else the exchange addressed to it
            var on = open.FirstOrDefault(m => m.Item.Team == team && (owner?.Invoke(m.Item) ?? m.Item.Plan.Holder) == member).Item?.Plan.Title is { } t ? HierarchyModule.ChecklistTitle(t)
                : l.OnExchange?.GetValueOrDefault(member);
            inProgress.Add(JsonValue.FromObject(new JsonObject
            {
                ["key"] = "member:" + team + "/" + member,
                ["title"] = on ?? member,
                ["team"] = team,
                ["stream"] = JsonValue.Null,
                ["lane"] = Text(lane),
                ["holder"] = member,
                ["role"] = JsonValue.Null,
                ["state"] = "working",
                ["work"] = "working",
                ["note"] = JsonValue.Null,
                ["on"] = Text(on),
                ["since"] = JsonValue.Null,
                ["last_activity"] = JsonValue.Null,
                ["source"] = "member",
            }));
        }

        // ship, QA and other jobs ahd knows of that have not ended, with the test slot each holds or waits for
        var running = (jobs ?? []).Where(static j => !j.Job.Ended).OrderBy(static j => j.Job.SentAtMs).ThenBy(static j => j.Job.Id, StringComparer.Ordinal).ToList();
        foreach (var (team, lane, job) in running.Take(Math.Max(0, Cap - inProgress.Count)))
        {
            inProgress.Add(JsonValue.FromObject(new JsonObject
            {
                ["key"] = "job:" + job.Id,
                ["title"] = job.Label,
                ["team"] = team,
                ["stream"] = JsonValue.Null,
                ["lane"] = Text(lane),
                ["holder"] = job.Owner,
                ["role"] = JsonValue.Null,
                ["state"] = job.Slot?.Phase ?? "running",
                ["work"] = JsonValue.Null,
                ["note"] = JsonValue.Null,
                ["kind"] = job.Kind,
                ["slot_holder"] = Text(job.Slot?.Holder is { Length: > 0 } h ? h : null),
                ["since"] = job.SentAtMs,
                ["last_activity"] = Num(job.ExtendedAtMs),
                ["source"] = "job",
            }));
        }

        var blocked = Group(held);
        var staleRows = Group(stale);
        var notMovingRows = Group(notMoving);

        // Up next: the queue rows in queue order, then unplanned backlog rows of wave now, by rank
        // a row left over a day leaves it, unless a member is idle to take it: that one is about to run
        var upNext = new JsonArray();
        var upNextOld = new JsonArray();
        bool Old(Queued q) => nowMs is { } at && q.Row.Eligible.Count == 0 && q.Row.QueuedSinceMs > 0 && at - q.Row.QueuedSinceMs > HierarchyModule.ChecklistStaleMs;
        var oldQueued = queued.Where(Old).ToList();
        foreach (var q in queued.Where(q => !Old(q)).Take(Cap).Select(q => (Row: q, Into: upNext)).Concat(oldQueued.Take(Cap).Select(q => (Row: q, Into: upNextOld))))
        {
            AddQueued(q.Row, q.Into);
        }

        void AddQueued(Queued q, JsonArray into)
        {
            var stream = StreamOfQueueKey(q.Row.Item);
            var eligible = new JsonArray();
            foreach (var name in q.Row.Eligible)
            {
                eligible.Add(name);
            }

            into.Add(JsonValue.FromObject(new JsonObject
            {
                ["key"] = KeyOf(stream, q.Row.Item.Length > stream.Length ? q.Row.Item[(stream.Length + 1)..] : string.Empty),
                ["title"] = q.Row.Title,
                ["team"] = q.Team,
                ["stream"] = stream,
                ["lane"] = Text(q.Lane),
                ["role"] = q.Row.Role,
                ["queued_since"] = q.Row.QueuedSinceMs,
                ["stall_at"] = q.Row.StallAtMs,
                ["idle_eligible"] = q.Row.Eligible.Count > 0,
                ["could_take"] = eligible,
                ["source"] = "queue",
            }));
            times.Add(q.Row.QueuedSinceMs);
        }

        // the backlog is read only when the queue leaves room (F9); a row is planned when a checklist item anywhere in the directory names
        // it as parent and the row outranks the item (ah-epics E-D3)
        var backlogRows = upNext.Count < Cap ? backlog() : [];
        var children = backlogRows.Count > 0 ? dirItems().Where(static i => i.Parent is not null).ToList() : [];
        var planned = HierarchyModule.PlannedBacklogKeys(backlogRows, children);
        foreach (var b in backlogRows
            .Where(b => b.State is "not_started" or "in_progress" && HierarchyModule.BacklogStateOf(b, children) == b.State && b.Wave == "now" && !planned.Contains(b.Item))
            .OrderBy(static b => b.Rank ?? int.MaxValue)
            .ThenBy(static b => b.Item, StringComparer.Ordinal)
            .Take(Math.Max(0, Cap - upNext.Count)))
        {
            upNext.Add(JsonValue.FromObject(new JsonObject
            {
                ["key"] = b.Item,
                ["title"] = b.Title,
                ["team"] = JsonValue.Null,
                ["stream"] = JsonValue.Null,
                ["lane"] = JsonValue.Null,
                ["role"] = Text(b.Role),
                ["queued_since"] = JsonValue.Null,
                ["stall_at"] = JsonValue.Null,
                ["idle_eligible"] = false,
                ["could_take"] = new JsonArray(),
                ["source"] = "backlog",
            }));
        }

        // as_of: the newest time over every plan row and ledger row read, not only the rows shown (§4)
        times.AddRange(items.Select(static i => Ms(i.Plan.UpdatedAt)).OfType<double>());
        times.AddRange(ledger.Select(static l => l.Row.Member("at") is { IsText: true } a ? Ms(a.AsString()) : null).OfType<double>());
        return JsonValue.FromObject(new JsonObject
        {
            ["regions"] = RegionsBody(),
            ["as_of"] = times.Count == 0 ? JsonValue.Null : times.Max(),
            ["completed_total"] = done.Count,
            ["in_progress_total"] = moving.Count + testingRows.Count + members.Count + running.Count,
            ["blocked_total"] = held.Count,
            ["stale_total"] = stale.Count,
            ["waiting_on_you_total"] = (decisions?.Count ?? 0) + waitingItems.Count,
            ["not_moving_total"] = notMoving.Count,
            ["waiting_on_you"] = waitingRows,
            ["not_moving"] = notMovingRows,
            ["completed"] = completed,
            ["in_progress"] = inProgress,
            ["blocked"] = blocked,
            ["stale"] = staleRows,
            ["up_next"] = upNext,
            ["up_next_old"] = upNextOld,
            ["up_next_old_total"] = oldQueued.Count,
        });
    }
}
