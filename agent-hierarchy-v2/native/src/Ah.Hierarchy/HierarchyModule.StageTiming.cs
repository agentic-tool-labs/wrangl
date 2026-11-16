using Ah.Core.Json;

namespace Ah.Hierarchy;

/// <summary>One stretch of an item, or of a QA run, in one stage (spec ah-stage-timing §1), in epoch milliseconds; <c>Open</c> when it runs to now.</summary>
public sealed record StageInterval(string Item, string Stage, double StartMs, double EndMs, bool Open);

/// <summary>A work item: the plan items that share a request slug (or one item with none). Stage times are the union of its items' intervals.</summary>
public sealed record StageGroup(
    string Key, IReadOnlyList<string> Items, IReadOnlyList<StageInterval> Intervals, IReadOnlyDictionary<string, double> StageMs, int ReworkRounds, double WallMs, bool Open, string? CurrentStage, double CurrentAgeMs);

/// <summary>What gated a ship: the group last to become ready (or <c>qa</c>), the stage it was in, and how long the batch then sat ready.</summary>
public sealed record StageCritical(string Group, string Stage, double ShipWaitMs);

/// <summary>One ship: the items it closed, their groups, the per-stage union, wall-clock and the critical path (null with no items).</summary>
public sealed record StageBatch(
    string Sha, string At, IReadOnlyList<string> Items, IReadOnlyList<string> Groups, IReadOnlyDictionary<string, double> StageMs, int ReworkRounds, double WallMs, StageCritical? Critical);

/// <summary>Every group and every batch the plan logs and the ledger show.</summary>
public sealed record StageTimingReport(IReadOnlyList<StageGroup> Groups, IReadOnlyList<StageBatch> Batches);

/// <summary>A bottleneck read off a batch and its groups (spec b29 §2): the signal id, one evidence line naming groups and seconds, and the suggested move id.</summary>
public sealed record StageSignal(string Id, string Evidence, string Move);

/// <summary>A shipped batch before the current one (spec b29 §7 slice 3): its sha, the signal ids it fired, and its review, qa and orch shares of its wall time.</summary>
public sealed record StageRecentBatch(string Sha, IReadOnlyList<string> Signals, IReadOnlyDictionary<string, double> Shares);

public static partial class HierarchyModule
{
    /// <summary>The stages a breakdown counts, in the order it prints them. <see cref="StageQueued"/> is shown, not counted.</summary>
    public static readonly IReadOnlyList<string> TimingStages = ["spec", "build", "review", "rework", "orch", "blocked", "qa", "ship_wait"];

    /// <summary>The stage of an item not yet dispatched.</summary>
    internal const string StageQueued = "queued";

    /// <summary>The role whose dispatches and reports are timed as QA (ledger kind <c>qa</c>).</summary>
    internal const string QaRole = "qa";

    private sealed record Stretch(string Item, string Stage, string State, double Start, double End, bool Open);

    /// <summary>How long a wait must last before S-review or S-build fires (spec b29 §2).</summary>
    public const double SignalHoldMs = 600_000;

    /// <summary>The ship_wait a group must reach for S-shipwait.</summary>
    public const double SignalShipWaitMs = 1_800_000;

    /// <summary>The share of a batch's wall time past which review (S-review) or qa (S-qa) fires.</summary>
    public const double SignalStageShare = 0.3;

    /// <summary>The share of a batch's wall time past which orch fires S-orch.</summary>
    public const double SignalOrchShare = 0.2;

    /// <summary>The rework rounds an open group must reach for S-rework.</summary>
    public const int SignalReworkRounds = 2;

    /// <summary>The ledger kind of a gate failure (spec b29 slice 2): <c>{sha, at, result: "fail"}</c>.</summary>
    public const string LedgerKindGate = "gate";

    /// <summary>
    /// The bottleneck signals (spec b29 §2) over <paramref name="batch"/> (null: none yet) and <paramref name="groups"/>, with the team's
    /// queued items now (<paramref name="queue"/>, empty for a past batch) and the ledger's gate rows since the ship before the batch.
    /// One read: <c>ah roster timing</c> prints it and the directive reads that output. Read-only.
    /// </summary>
    public static IReadOnlyList<StageSignal> StageSignals(
        StageTimingReport report, StageBatch? batch, IReadOnlyList<StageGroup> groups, IReadOnlyList<QueueEntry> queue, IEnumerable<JsonValue> ledger, double nowMs)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(ledger);
        static string Sec(double ms) => Math.Round(Math.Max(0, ms) / 1000).ToString(System.Globalization.CultureInfo.InvariantCulture) + "s";
        double Share(string stage) => batch is { WallMs: > 0 } b && b.StageMs.TryGetValue(stage, out var ms) ? ms / b.WallMs : 0;
        string ShareText(string stage) => $"{stage} {Sec(batch!.StageMs[stage])} of {Sec(batch.WallMs)} wall in batch {batch.Sha[..Math.Min(9, batch.Sha.Length)]}";
        var signals = new List<StageSignal>();

        // A queued reviewer item counts under its group's key (an item in no group under its own key), so one group counts once
        var groupOf = groups.SelectMany(static g => g.Items.Select(i => (Item: i, g.Key))).GroupBy(static p => p.Item, StringComparer.Ordinal)
            .ToDictionary(static p => p.Key, static p => p.First().Key, StringComparer.Ordinal);
        var reviewWaits = groups
            .Select(static g => (g.Key, Spans: g.Intervals.Where(static i => i.Stage == "review").Select(static i => (i.StartMs, i.EndMs)).ToList()))
            .Concat(queue.Where(static q => q.Role == "reviewer").Select(q => (Key: groupOf.GetValueOrDefault(q.Entry.Item.Item) ?? ChecklistKey(q.Entry), Spans: new List<(double, double)> { (q.QueuedSinceMs, nowMs) })))
            .GroupBy(static w => w.Key, StringComparer.Ordinal)
            .Select(static w => (w.Key, Spans: w.SelectMany(static x => x.Spans).ToList()))
            .Where(static w => w.Spans.Count > 0)
            .ToList();
        var (overlapMs, overlapKeys) = LongestOverlap(reviewWaits);
        if (overlapMs >= SignalHoldMs || Share("review") > SignalStageShare)
        {
            signals.Add(new StageSignal(
                "S-review",
                overlapMs >= SignalHoldMs ? $"{overlapKeys.Count} groups in review at once for {Sec(overlapMs)} ({string.Join(", ", overlapKeys)})" : ShareText("review"),
                "M2"));
        }

        // ponytail: "every live implementor holds an item" is read now (no idle implementor), not as lasting 10 min; only the queued items' wait is timed
        var builds = queue.Where(q => q.Role == "implementor" && q.Eligible.Count == 0 && q.RoleLiveMembers > 0 && nowMs - q.QueuedSinceMs >= SignalHoldMs).ToList();
        if (builds.Count >= 2)
        {
            signals.Add(new StageSignal(
                "S-build",
                $"{builds.Count} items ready for a builder for at least {Sec(nowMs - builds.Max(static q => q.QueuedSinceMs))} while every live implementor holds one ({string.Join(", ", builds.Select(static q => ChecklistKey(q.Entry)))})",
                "M1"));
        }

        var rework = groups.Where(static g => g.Open && g.ReworkRounds >= SignalReworkRounds).ToList();
        if (rework.Count > 0)
        {
            signals.Add(new StageSignal("S-rework", string.Join(", ", rework.Select(static g => $"{g.Key} at rework round {g.ReworkRounds}")), "M3"));
        }

        if (batch is { Critical: { } gate })
        {
            var waited = groups.Where(g => batch.Groups.Contains(g.Key, StringComparer.Ordinal) && g.Key != gate.Group && g.StageMs.TryGetValue("ship_wait", out var w) && w >= SignalShipWaitMs).ToList();
            if (waited.Count * 2 > batch.Groups.Count)
            {
                signals.Add(new StageSignal(
                    "S-shipwait",
                    $"{waited.Count} of {batch.Groups.Count} groups waited to ship ({string.Join(", ", waited.Select(static g => $"{g.Key} {Sec(g.StageMs["ship_wait"])}"))}) while {gate.Group} gated it in {gate.Stage}",
                    "M4-split"));
            }
        }

        if (GateSignal(report, batch, ledger, nowMs) is { } gateEvidence)
        {
            signals.Add(new StageSignal("S-gate", gateEvidence, "M6"));
        }

        if (batch is not null && (batch.Critical?.Stage == "qa" || Share("qa") > SignalStageShare))
        {
            signals.Add(new StageSignal("S-qa", batch.Critical?.Stage == "qa" ? $"qa ({batch.Critical.Group}) gated batch {batch.Sha[..Math.Min(9, batch.Sha.Length)]}; {ShareText("qa")}" : ShareText("qa"), "M5"));
        }

        if (Share("orch") > SignalOrchShare)
        {
            signals.Add(new StageSignal("S-orch", ShareText("orch"), "M0"));
        }

        return signals;
    }

    /// <summary>The stages whose share of a batch's wall time <see cref="RecentBatches"/> reports.</summary>
    private static readonly string[] RecentShareStages = ["review", "qa", "orch"];

    /// <summary>
    /// The 3 shipped batches before <paramref name="current"/> (null: none), newest first, each with the signals <see cref="StageSignals"/> fires
    /// over it and its groups with an empty queue, as for a past batch. Fewer, or none, when the ledger has fewer earlier ships. Read-only.
    /// </summary>
    public static IReadOnlyList<StageRecentBatch> RecentBatches(StageTimingReport report, StageBatch? current, IReadOnlyList<JsonValue> ledger, double nowMs)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(ledger);
        var index = current is null ? -1 : report.Batches.ToList().IndexOf(current);
        return
        [
            .. report.Batches.Take(Math.Max(0, index)).Reverse().Take(3).Select(b => new StageRecentBatch(
                b.Sha,
                [.. StageSignals(report, b, [.. report.Groups.Where(g => b.Groups.Contains(g.Key, StringComparer.Ordinal))], [], ledger, nowMs).Select(static s => s.Id)],
                RecentShareStages.ToDictionary(static s => s, s => b.WallMs > 0 && b.StageMs.TryGetValue(s, out var ms) ? ms / b.WallMs : 0, StringComparer.Ordinal))),
        ];
    }

    /// <summary>The longest stretch during which at least two of <paramref name="waits"/> (each a group's spans) overlap, and the groups in it.</summary>
    private static (double Ms, IReadOnlyList<string> Keys) LongestOverlap(List<(string Key, List<(double Start, double End)> Spans)> waits)
    {
        static IEnumerable<(double Start, double End)> Union(List<(double Start, double End)> spans)
        {
            (double Start, double End)? run = null;
            foreach (var s in spans.OrderBy(static s => s.Start))
            {
                if (run is { } r && s.Start <= r.End)
                {
                    run = (r.Start, Math.Max(r.End, s.End));
                    continue;
                }

                if (run is { } done)
                {
                    yield return done;
                }

                run = s;
            }

            if (run is { } last)
            {
                yield return last;
            }
        }

        var points = waits.SelectMany(static w => Union(w.Spans).SelectMany(static s => new[] { (At: s.Start, Delta: 1), (At: s.End, Delta: -1) })).OrderBy(static p => p.At).ThenBy(static p => p.Delta).ToList();
        var (count, best, from, bestFrom, bestTo) = (0, 0d, 0d, 0d, 0d);
        foreach (var (at, delta) in points)
        {
            if (count >= 2 && delta < 0 && count + delta < 2 && at - from > best)
            {
                (best, bestFrom, bestTo) = (at - from, from, at);
            }

            count += delta;
            if (count == 2 && delta > 0)
            {
                from = at;
            }
        }

        var keys = waits.Where(w => w.Spans.Any(s => s.Start < bestTo && s.End > bestFrom)).Select(static w => w.Key).Distinct(StringComparer.Ordinal).ToList();
        return best > 0 ? (best, keys) : (0, []);
    }

    /// <summary>S-gate's evidence: one sha with 2 or more gate failures, or two different shas failing back to back, in the ledger's gate and ship rows since the ship before <paramref name="batch"/>; null when neither.</summary>
    private static string? GateSignal(StageTimingReport report, StageBatch? batch, IEnumerable<JsonValue> ledger, double nowMs)
    {
        var index = batch is null ? report.Batches.Count : report.Batches.ToList().IndexOf(batch);
        var from = index > 0 ? Ms(report.Batches[index - 1].At) ?? double.MinValue : double.MinValue;
        var to = batch is null || index == report.Batches.Count - 1 ? double.MaxValue : Ms(batch.At) ?? nowMs;
        var rows = ledger
            .Select(static r => (Kind: LedgerText(r, "kind"), Sha: LedgerText(r, "sha"), Result: LedgerText(r, "result"), At: Ms(LedgerText(r, "at"))))
            .Where(r => r.Kind is LedgerKindGate or ChecklistEventShip && r.Sha is { Length: > 0 } && r.At is { } at && at > from && at <= to)
            .Select(static r => (r.Sha, Fail: r.Kind == LedgerKindGate && r.Result == "fail", At: r.At!.Value))
            .OrderBy(static r => r.At)
            .ToList();
        static string Short(string? sha) => sha is { Length: > 9 } s ? s[..9] : sha ?? string.Empty;
        if (rows.Where(static r => r.Fail).GroupBy(static r => r.Sha, StringComparer.Ordinal).FirstOrDefault(static g => g.Count() >= 2) is { } twice)
        {
            return $"{Short(twice.Key)} failed the gate {twice.Count()} times";
        }

        return rows.Zip(rows.Skip(1)).FirstOrDefault(static p => p.First.Fail && p.Second.Fail && p.First.Sha != p.Second.Sha) is { First.Sha: { } a, Second.Sha: { } b }
            ? $"{Short(a)} then {Short(b)} failed the gate in a row"
            : null;
    }

    /// <summary>
    /// The stage fold (spec ah-stage-timing §1): each item's trail (<see cref="ChecklistTrail"/>) cut into stage intervals, grouped by request slug,
    /// plus the ledger's <c>qa</c> runs, and one batch per ship row. Read-only.
    /// </summary>
    public static StageTimingReport StageTiming(IReadOnlyList<string?> planLogs, IEnumerable<JsonValue> ledger, Func<string, string?> slugOfRequest, double nowMs)
    {
        ArgumentNullException.ThrowIfNull(planLogs);
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(slugOfRequest);
        var rows = ledger.ToList();
        var byGroup = new Dictionary<string, List<Stretch>>(StringComparer.Ordinal);
        var itemsOf = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var groupOfItem = new Dictionary<string, string>(StringComparer.Ordinal);
        var doneSha = new Dictionary<string, string>(StringComparer.Ordinal);
        void Add(string key, string item, IEnumerable<Stretch> stretches)
        {
            (byGroup.TryGetValue(key, out var list) ? list : byGroup[key] = []).AddRange(stretches);
            var items = itemsOf.TryGetValue(key, out var known) ? known : itemsOf[key] = [];
            if (!items.Contains(item))
            {
                items.Add(item);
            }
        }

        // ponytail: one ChecklistTrail replay per item (O(items × rows)); a single pass if plan logs grow large
        foreach (var log in planLogs.Where(static l => l is { Length: > 0 }))
        {
            foreach (var item in ProgressFormat.FoldPlan(log!).Items)
            {
                var trail = ChecklistTrail(log, item.Item);
                if (trail.Count == 0)
                {
                    continue;
                }

                var key = (item.Refs ?? []).Select(slugOfRequest).FirstOrDefault(static s => s is { Length: > 0 }) ?? item.Item;
                groupOfItem[item.Item] = key;
                Add(key, item.Item, ItemStretches(item.Item, trail, nowMs));
                if (trail.LastOrDefault(static r => r.To == PlanStates.Done) is { Cause: { Event: ChecklistEventShip or ChecklistEventMerge } cause })
                {
                    doneSha[item.Item] = cause.Ref;
                }
            }
        }

        foreach (var key in byGroup.Keys.ToList())
        {
            byGroup[key] = HandOff(byGroup[key]);
        }

        var batches = BatchRows(rows);
        var qaOfBatch = new Dictionary<string, List<(string Group, Stretch Run)>>(StringComparer.Ordinal);
        foreach (var (group, run) in QaRuns(rows, nowMs))
        {
            Add(group, run.Item, [run]);
            if (!run.Open && batches.FirstOrDefault(b => b.AtMs >= run.End) is { Sha: not null } next)
            {
                (qaOfBatch.TryGetValue(next.Sha, out var list) ? list : qaOfBatch[next.Sha] = []).Add((group, run));
            }
        }

        var groups = byGroup
            .Select(g => Group(g.Key, itemsOf[g.Key], g.Value, nowMs))
            .OrderBy(static g => g.Intervals.Count == 0 ? double.MaxValue : g.Intervals.Min(static i => i.StartMs))
            .ToList();
        var result = new List<StageBatch>();
        foreach (var (sha, at, atMs) in batches)
        {
            var items = doneSha.Where(d => d.Value == sha).Select(static d => d.Key).Order(StringComparer.Ordinal).ToList();
            var keys = items.Select(i => groupOfItem[i]).Distinct(StringComparer.Ordinal).ToList();

            // a group's stretches count up to the ship only; its QA runs enter only through qaOfBatch (paired, ended by the ship)
            var shipped = keys.SelectMany(k => byGroup[k].Where(s => s.Start < atMs && !IsQaRun(s)).Select(s => (Group: k, Run: s with { End = Math.Min(s.End, atMs) }))).ToList();
            var qa = qaOfBatch.TryGetValue(sha, out var runs) ? runs : [];
            var all = shipped.Concat(qa).Select(static p => p.Run).ToList();
            var starts = shipped.Where(p => items.Contains(p.Run.Item) && p.Run.State == PlanStates.InProgress).Select(static p => p.Run.Start).ToList();
            result.Add(new StageBatch(
                sha, at, items, keys, StageUnion(all), all.Count(static s => s.Stage == "rework"),
                items.Count == 0 || starts.Count == 0 ? 0 : atMs - starts.Min(), items.Count == 0 ? null : Critical(shipped, qa, atMs)));
        }

        return new StageTimingReport(groups, result);
    }

    /// <summary>The stage of a trail row by the state it entered and the role holding the item (spec ah-stage-timing §1 table); null for done or dropped.</summary>
    internal static string? StageOf(string state, string? role, PlanCause? cause) => state switch
    {
        PlanStates.InProgress => role switch
        {
            "architect" => "spec",
            "reviewer" => "review",
            QaRole => "qa",

            // the transition table moves an item back to in_progress only on a reviewer's fail verdict, with cause.event reply
            _ => cause?.Event == ChecklistEventReply ? "rework" : "build",
        },
        PlanStates.InReview => "review",
        PlanStates.WaitingOrch or PlanStates.Reported => "orch",
        PlanStates.Blocked or PlanStates.WaitingUser => "blocked",
        PlanStates.Approved => "ship_wait",
        PlanStates.Done or PlanStates.Dropped => null,
        _ => StageQueued,
    };

    /// <summary>
    /// An item's stretches: a row that changes the state or the role starts one, at its <c>cause.at</c> (else <c>ts</c>); a row that changes
    /// neither (a note, a todo, a re-dispatch) extends the current one. The last runs to <paramref name="nowMs"/> unless the item ended.
    /// </summary>
    private static List<Stretch> ItemStretches(string item, IReadOnlyList<ChecklistTrailRow> trail, double nowMs)
    {
        var stretches = new List<Stretch>();
        (string Stage, string State, double Start)? open = null;
        string? state = null;
        string? role = null;
        foreach (var r in trail)
        {
            if ((ChecklistIsMoverStamp(r.Cause) ? Ms(r.Ts) : Ms(r.Cause?.At) ?? Ms(r.Ts)) is not { } at || (r.To == state && r.Role == role))
            {
                continue;
            }

            if (open is { } o)
            {
                stretches.Add(new Stretch(item, o.Stage, o.State, o.Start, Math.Max(o.Start, at), false));
            }

            state = r.To;
            role = r.Role;
            open = StageOf(r.To, r.Role, r.Cause) is { } stage ? (stage, r.To, at) : null;
        }

        if (open is { } last)
        {
            stretches.Add(new Stretch(item, last.Stage, last.State, last.Start, Math.Max(last.Start, nowMs), true));
        }

        return stretches;
    }

    /// <summary>A ledger QA run, not a plan item: its item is <c>qa:&lt;request id&gt;</c>, which no item slug (<c>[a-z0-9-]</c>) can be.</summary>
    private static bool IsQaRun(Stretch s) => s.Item.StartsWith("qa:", StringComparison.Ordinal);

    /// <summary>
    /// Within a group, an item's waiting stretches (orch, review, ship_wait, blocked) end when a later item of the group starts (its first stretch that
    /// is not queued): a finished upstream item waiting on the Orchestrator does not cover the downstream build. Work stretches (spec, build, rework) are never cut.
    /// </summary>
    private static List<Stretch> HandOff(List<Stretch> stretches)
    {
        static bool Waiting(Stretch s) => s.Stage is "orch" or "review" or "ship_wait" or "blocked";
        var firstStart = stretches.GroupBy(static s => s.Item).ToDictionary(
            static g => g.Key, static g => g.Where(static s => s.Stage != StageQueued).Select(static s => s.Start).DefaultIfEmpty(double.MaxValue).Min(), StringComparer.Ordinal);
        var result = new List<Stretch>();
        foreach (var s in stretches)
        {
            var mine = firstStart[s.Item];
            var cut = firstStart.Values.Where(v => v > mine && v < double.MaxValue).DefaultIfEmpty(double.MaxValue).Min();
            if (!Waiting(s))
            {
                result.Add(s);
            }
            else if (s.Start < cut)
            {
                result.Add(s.End > cut ? s with { End = cut, Open = false } : s);
            }
        }

        return result;
    }

    private static StageGroup Group(string key, IReadOnlyList<string> items, List<Stretch> stretches, double nowMs)
    {
        var sorted = stretches.OrderBy(static s => s.Start).ToList();
        var open = sorted.LastOrDefault(static s => s.Open);
        var starts = sorted.Where(static s => s.State == PlanStates.InProgress).Select(static s => s.Start).ToList();
        var end = open is not null ? nowMs : sorted.Count == 0 ? 0 : sorted.Max(static s => s.End);
        return new StageGroup(
            key, items, [.. sorted.Select(static s => new StageInterval(s.Item, s.Stage, s.Start, s.End, s.Open))], StageUnion(sorted),
            sorted.Count(static s => s.Stage == "rework"), starts.Count == 0 ? 0 : Math.Max(0, end - starts.Min()), open is not null, open?.Stage, open is null ? 0 : nowMs - open.Start);
    }

    /// <summary>Per counted stage, the wall-clock union of the stretches in it: overlap is not counted twice.</summary>
    private static Dictionary<string, double> StageUnion(IEnumerable<Stretch> stretches)
    {
        var list = stretches.ToList();
        var stages = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var stage in TimingStages)
        {
            double total = 0;
            double? from = null;
            double to = 0;
            foreach (var s in list.Where(s => s.Stage == stage).OrderBy(static s => s.Start))
            {
                if (from is null || s.Start > to)
                {
                    total += from is null ? 0 : to - from.Value;
                    (from, to) = (s.Start, s.End);
                }
                else
                {
                    to = Math.Max(to, s.End);
                }
            }

            stages[stage] = total + (from is null ? 0 : to - from.Value);
        }

        return stages;
    }

    /// <summary>The group last to become ready (its last stretch that is not ship_wait or queued ended latest), or <c>qa</c> when a QA run ended after that.</summary>
    private static StageCritical? Critical(List<(string Group, Stretch Run)> shipped, List<(string Group, Stretch Run)> qa, double shipMs)
    {
        var gate = shipped.Where(static p => p.Run.Stage is not ("ship_wait" or StageQueued)).OrderBy(static p => p.Run.End).LastOrDefault();
        var lastQa = qa.OrderBy(static p => p.Run.End).LastOrDefault();
        if (lastQa.Run is { } run && (gate.Run is null || run.End > gate.Run.End))
        {
            return new StageCritical(lastQa.Group, "qa", Math.Max(0, shipMs - run.End));
        }

        return gate.Run is { } g ? new StageCritical(gate.Group, g.Stage, Math.Max(0, shipMs - g.End)) : null;
    }

    /// <summary>One batch per ship sha (its first ship row), and per merge sha no ship row names, oldest first.</summary>
    private static List<(string Sha, string At, double AtMs)> BatchRows(List<JsonValue> rows)
    {
        var shipped = rows.Where(static r => LedgerText(r, "kind") == ChecklistEventShip).Select(static r => LedgerText(r, "sha")).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return
        [
            .. rows
                .Where(r => LedgerText(r, "kind") is { } kind && (kind == ChecklistEventShip || (kind == ChecklistEventMerge && LedgerText(r, "sha") is { } s && !shipped.Contains(s))))
                .Select(static r => (Sha: LedgerText(r, "sha"), At: LedgerText(r, "at")))
                .Where(static r => r.Sha is { Length: > 0 } && Ms(r.At) is not null)
                .Where(r => seen.Add(r.Sha!))
                .Select(static r => (r.Sha!, r.At!, Ms(r.At)!.Value))
                .OrderBy(static r => r.Item3),
        ];
    }

    /// <summary>The ledger's QA runs: each <c>qa start</c> paired with the first later <c>qa end</c> of the same request; no end runs to now. The group is the run's slug, else its request id.</summary>
    private static IEnumerable<(string Group, Stretch Run)> QaRuns(List<JsonValue> rows, double nowMs)
    {
        var qa = rows
            .Where(static r => LedgerText(r, "kind") == QaRole && LedgerText(r, "ref") is { Length: > 0 } && Ms(LedgerText(r, "at")) is not null)
            .Select(static r => (Phase: LedgerText(r, "phase"), Ref: LedgerText(r, "ref")!, Slug: LedgerText(r, "slug"), At: Ms(LedgerText(r, "at"))!.Value))
            .OrderBy(static r => r.At)
            .ToList();
        var used = new HashSet<int>();
        foreach (var start in qa.Where(static r => r.Phase == "start"))
        {
            var end = -1;
            for (var i = 0; i < qa.Count && end < 0; i++)
            {
                end = qa[i].Phase == "end" && qa[i].Ref == start.Ref && qa[i].At >= start.At && !used.Contains(i) ? i : -1;
            }

            var item = "qa:" + start.Ref;
            var group = start.Slug is { Length: > 0 } slug ? slug : start.Ref;
            if (end >= 0)
            {
                _ = used.Add(end);
                yield return (group, new Stretch(item, "qa", PlanStates.InProgress, start.At, qa[end].At, false));
            }
            else
            {
                yield return (group, new Stretch(item, "qa", PlanStates.InProgress, start.At, Math.Max(start.At, nowMs), true));
            }
        }
    }
}
