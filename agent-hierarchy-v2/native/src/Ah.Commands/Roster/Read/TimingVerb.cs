using System.Globalization;
using Ah.Core.Json;
using Ah.Hierarchy;

namespace Ah.Commands;

/// <summary>
/// <c>roster timing [--item &lt;id|slug&gt; | --batch &lt;sha&gt;] [--plain]</c>: how long each stage of a work item and of a ship took, and what gated the
/// ship (spec ah-stage-timing §2). It formats <see cref="HierarchyModule.StageTiming"/> and writes nothing.
/// </summary>
internal static class TimingVerb
{
    private const string Usage = "usage: ah roster timing [--item <id|slug> | --batch <sha>] [--plain] [--team T] [--cwd <path>]";

    private static readonly HashSet<string> Flags = new(StringComparer.Ordinal) { "cwd", "team", "orchestrator-pid", "item", "batch", "plain" };

    internal static int Run(RosterContext c)
    {
        foreach (var key in c.Opts.Keys)
        {
            if (!Flags.Contains(key))
            {
                throw new RosterFailure($"timing: unrecognized flag --{key}; {Usage}");
            }
        }

        if (c.Opts.Positionals.Any(static p => p != "timing"))
        {
            throw new RosterFailure(Usage);
        }

        var item = c.Opts.Text("item");
        var batch = c.Opts.Text("batch");
        if (item is not null && batch is not null)
        {
            throw new RosterFailure("timing: --item and --batch cannot be used together; " + Usage);
        }

        var progress = c.Services.Hierarchy.Progress;
        var nowMs = c.Services.Clock.UtcNow.ToUnixTimeMilliseconds();
        var logs = HierarchyModule.ChecklistEntries(progress, c.ProgressPool, c.ProgressTeam)
            .Select(static e => e.Stream).Distinct(StringComparer.Ordinal).Select(s => ChecklistVerb.PlanLog(c, s)).ToList();
        var ledger = progress.ReadLedger(c.ProgressPool, c.ProgressTeam).ToList();
        var report = HierarchyModule.StageTiming(logs, ledger, id => LedgerVerb.SlugOf(c, id), nowMs);

        IReadOnlyList<StageGroup> groups;
        IReadOnlyList<StageBatch> batches;
        IReadOnlyList<StageSignal> signals;
        IReadOnlyList<StageRecentBatch> recent = [];
        if (batch is not null)
        {
            var found = report.Batches.LastOrDefault(b => batch.Length >= 4 && b.Sha.StartsWith(batch, StringComparison.OrdinalIgnoreCase))
                ?? throw new RosterFailure($"timing: no ship {batch} in the {c.ProgressTeam} ledger");
            batches = [found];
            groups = [.. report.Groups.Where(g => found.Groups.Contains(g.Key, StringComparer.Ordinal))];
            signals = HierarchyModule.StageSignals(report, found, groups, [], ledger, nowMs);
            recent = HierarchyModule.RecentBatches(report, found, ledger, nowMs);
        }
        else if (item is not null)
        {
            var found = report.Groups.FirstOrDefault(g => g.Key == item || g.Items.Contains(item, StringComparer.Ordinal) || g.Items.Any(i => item.EndsWith("/" + i, StringComparison.Ordinal)))
                ?? throw new RosterFailure($"timing: no item or slug {item}");
            groups = [found];
            batches = [];
            signals = HierarchyModule.StageSignals(report, null, groups, [], [], nowMs);
        }
        else
        {
            batches = report.Batches.Count == 0 ? [] : [report.Batches[^1]];
            groups = [.. report.Groups.Where(static g => g.Open)];
            var last = batches.Count == 0 ? null : batches[0];
            signals = HierarchyModule.StageSignals(
                report, last, [.. report.Groups.Where(g => g.Open || (last?.Groups.Contains(g.Key, StringComparer.Ordinal) ?? false))], Queue(c, nowMs), ledger, nowMs);
            recent = HierarchyModule.RecentBatches(report, last, ledger, nowMs);
        }

        if (c.Opts.IsTrue("plain"))
        {
            foreach (var s in signals)
            {
                c.Stdout.Write($"signal {s.Id} (move {s.Move}): {s.Evidence}\n");
            }

            foreach (var r in recent)
            {
                c.Stdout.Write($"recent {Short(r.Sha)} {(r.Signals.Count == 0 ? "-" : string.Join(',', r.Signals))}\n");
            }

            foreach (var b in batches)
            {
                c.Stdout.Write(
                    $"batch {Short(b.Sha)} at {b.At}: {b.Items.Count} item(s), {b.Groups.Count} group(s), wall {Dur(b.WallMs)}, rework {b.ReworkRounds}\n  {Stages(b.StageMs)}\n");
                if (b.Critical is { } gate)
                {
                    c.Stdout.Write($"  gated by {gate.Group} in {gate.Stage}; ready {Dur(gate.ShipWaitMs)} before ship\n");
                }
            }

            foreach (var g in groups)
            {
                var open = g.Open ? $" (open: {g.CurrentStage} for {Dur(g.CurrentAgeMs)})" : string.Empty;
                c.Stdout.Write($"group {g.Key}{open}: wall {Dur(g.WallMs)}, rework {g.ReworkRounds}\n  {Stages(g.StageMs)}\n");
                if (item is not null)
                {
                    foreach (var i in g.Intervals)
                    {
                        c.Stdout.Write($"  {Iso(i.StartMs)} {i.Stage} {Dur(i.EndMs - i.StartMs)} {i.Item}{(i.Open ? " (open)" : string.Empty)}\n");
                    }
                }
            }

            return 0;
        }

        var groupsJson = new JsonArray();
        foreach (var g in groups)
        {
            var json = new JsonObject
            {
                ["key"] = g.Key,
                ["items"] = Texts(g.Items),
                ["stages"] = StagesJson(g.StageMs),
                ["rework_rounds"] = g.ReworkRounds,
                ["wall_s"] = Seconds(g.WallMs),
                ["open"] = g.Open,
                ["current"] = g.Open ? new JsonObject { ["stage"] = g.CurrentStage ?? string.Empty, ["age_s"] = Seconds(g.CurrentAgeMs) } : JsonValue.Null,
            };
            if (item is not null)
            {
                var intervals = new JsonArray();
                foreach (var i in g.Intervals)
                {
                    intervals.Add(new JsonObject { ["item"] = i.Item, ["stage"] = i.Stage, ["start"] = Iso(i.StartMs), ["end"] = Iso(i.EndMs), ["s"] = Seconds(i.EndMs - i.StartMs), ["open"] = i.Open });
                }

                json["intervals"] = intervals;
            }

            groupsJson.Add(json);
        }

        var batchesJson = new JsonArray();
        foreach (var b in batches)
        {
            batchesJson.Add(new JsonObject
            {
                ["sha"] = b.Sha,
                ["at"] = b.At,
                ["items"] = Texts(b.Items),
                ["groups"] = Texts(b.Groups),
                ["stages"] = StagesJson(b.StageMs),
                ["rework_rounds"] = b.ReworkRounds,
                ["wall_s"] = Seconds(b.WallMs),
                ["critical"] = b.Critical is { } gate ? new JsonObject { ["group"] = gate.Group, ["stage"] = gate.Stage, ["ship_wait_s"] = Seconds(gate.ShipWaitMs) } : JsonValue.Null,
            });
        }

        var signalsJson = new JsonArray();
        foreach (var s in signals)
        {
            signalsJson.Add(new JsonObject { ["id"] = s.Id, ["evidence"] = s.Evidence, ["move"] = s.Move });
        }

        var recentJson = new JsonArray();
        foreach (var r in recent)
        {
            var shares = new JsonObject();
            foreach (var (stage, share) in r.Shares)
            {
                shares[stage] = share;
            }

            recentJson.Add(new JsonObject { ["sha"] = r.Sha, ["signals"] = Texts(r.Signals), ["shares"] = shares });
        }

        c.Out(new JsonObject { ["team"] = c.ProgressTeam, ["groups"] = groupsJson, ["batches"] = batchesJson, ["signals"] = signalsJson, ["recent"] = recentJson });
        return 0;
    }

    /// <summary>The team's queued items now, with the idle members of their role, for S-build and S-review; empty with no team record.</summary>
    private static IReadOnlyList<QueueEntry> Queue(RosterContext c, double nowMs)
    {
        if (c.TeamFile is not { Length: > 0 } team || c.Services.Roster.ReadTeamRecord(c.Dir, team) is not { IsMap: true } record)
        {
            return [];
        }

        // ponytail: no slot read, so a member blocked on a test job counts as idle and holds S-build back; read the slot files if that misfires
        var (members, dispatches) = Ah.Watch.WatchModule.QueueFactsFromFiles(c.Services.Hierarchy, record, c.Dir, team, static _ => false);
        // ah2-209 D3: an item waiting on another is not ready for a builder or a reviewer
        return HierarchyModule.QueueAll([.. HierarchyModule.ChecklistEntries(c.Services.Hierarchy.Progress, c.ProgressPool, c.ProgressTeam).Where(static e => e.WaitingOn is null)], members, dispatches, nowMs);
    }

    private static JsonArray Texts(IEnumerable<string> texts)
    {
        var list = new JsonArray();
        foreach (var t in texts)
        {
            list.Add(t);
        }

        return list;
    }

    private static JsonObject StagesJson(IReadOnlyDictionary<string, double> stages)
    {
        var json = new JsonObject();
        foreach (var stage in HierarchyModule.TimingStages)
        {
            json[stage] = Seconds(stages.TryGetValue(stage, out var ms) ? ms : 0);
        }

        return json;
    }

    /// <summary>The stages with time in them, in order, as <c>build 1h40m review 55m</c>; <c>no stage time</c> when none has any.</summary>
    internal static string Stages(IReadOnlyDictionary<string, double> stages)
    {
        var parts = HierarchyModule.TimingStages.Where(s => stages.TryGetValue(s, out var ms) && ms >= 1000).Select(s => s + " " + Dur(stages[s])).ToList();
        return parts.Count == 0 ? "no stage time" : string.Join(' ', parts);
    }

    private static double Seconds(double ms) => Math.Round(Math.Max(0, ms) / 1000);

    internal static string Dur(double ms)
    {
        var s = (long)Seconds(ms);
        return s < 60 ? s.ToString(CultureInfo.InvariantCulture) + "s"
            : s < 3600 ? (s / 60).ToString(CultureInfo.InvariantCulture) + "m"
            : (s / 3600).ToString(CultureInfo.InvariantCulture) + "h" + (s % 3600 / 60).ToString("00", CultureInfo.InvariantCulture) + "m";
    }

    private static string Iso(double ms) => HierarchyModule.ChecklistEventTime(DateTimeOffset.FromUnixTimeMilliseconds((long)ms));

    private static string Short(string sha) => sha.Length > 9 ? sha[..9] : sha;
}
