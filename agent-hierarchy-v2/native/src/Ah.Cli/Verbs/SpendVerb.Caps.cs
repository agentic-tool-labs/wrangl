using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Core.Json;
using Ah.Core.Time;
using Ah.Hierarchy;

namespace Ah.Cli.Verbs;

/// <summary>
/// The spend caps (ah2-512 S3, S6, S7, adr-37): each cap is a rules row of kind <c>cap</c>, summed here over the ledger. Reaching one parks one
/// question for the user per cap version and run, authored by the counter itself (S8). Nothing here stops or refuses a call.
/// </summary>
internal static partial class SpendVerb
{
    internal const string ParkHeader = "Spend cap reached";

    private static readonly string[] ParkOptions = ["Raise the cap", "Keep going, no more asks for this cap", "Tell the team to stop paid calls"];

    /// <summary>One cap with its total: the scope as the question names it, the period it sums over, and the op id of its park.</summary>
    private sealed record CapSum(RuleRow Row, string Unit, long Max, long Total, string Scope, string Period, string OpId);

    /// <summary>The projects and goals an item sits under, itself included, at any depth (Amendment 2 P4).</summary>
    private sealed record Phases(IReadOnlySet<long> Projects, IReadOnlySet<long> Goals)
    {
        public static Phases None { get; } = new(new HashSet<long>(), new HashSet<long>());
    }

    /// <summary>
    /// Every project and goal over each work number: from the item, self then parent, as RulesOfEngagement.Over walks, keeping each one met. The board is read on the
    /// first item asked about, so only a project or goal cap reads it; a cycle or a missing parent ends the walk.
    /// </summary>
    private static Func<long?, Phases> PhasesOf(ServiceGraph g, string ahHome, string dir)
    {
        var board = new Lazy<WorkBoard?>(() =>
        {
            try
            {
                WorkVerb.Reconcile(g, ahHome, dir);
                return Read<WorkBoard?>(ahHome, null, q => HierarchyModule.WorkBoardOf(q, g.Hierarchy.Progress, dir));
            }
            catch (Exception e) when (e is not OutOfMemoryException and not OperationCanceledException)
            {
                return null;
            }
        });
        var memo = new Dictionary<long, Phases>();
        return item =>
        {
            if (item is not { } n || board.Value is not { } read || read.ViewOf(n) is not { } view)
            {
                return Phases.None;
            }

            if (!memo.TryGetValue(n, out var phases))
            {
                var projects = new HashSet<long>();
                var goals = new HashSet<long>();
                var seen = new HashSet<long>();
                for (var at = view; at is not null && seen.Add(at.Row.N); at = at.Plan?.Parent is { Length: > 0 } parent ? read.BacklogRow(parent) : null)
                {
                    _ = at.Plan?.Kind == RulesOfEngagement.Project ? projects.Add(at.Row.N) : at.Plan?.Kind == RulesOfEngagement.Goal && goals.Add(at.Row.N);
                }

                phases = memo[n] = new Phases(projects, goals);
            }

            return phases;
        };
    }

    /// <summary>
    /// Every cap of the repo with its total (S3). A team cap sums its team's rows of the team's open run (<paramref name="runOf"/>), or with no open run the
    /// rows with no run recorded since the cap was set; a project or goal cap sums every row, across runs, whose item sits under that project or goal at any depth.
    /// <paramref name="callerRun"/> is the run a project or goal cap's park is keyed to.
    /// </summary>
    private static List<CapSum> Caps(string ahHome, string dir, string? prefix, IReadOnlyList<SpendRow> rows, Func<string, string?> runOf, string? callerRun, Func<long?, Phases> phaseOf)
    {
        var caps = Read<IReadOnlyList<RuleRow>>(ahHome, [], q => RulesOfEngagement.Rows(q, dir))
            .Where(r => r.Kind == RulesOfEngagement.CapKind && r.Key.StartsWith(RulesOfEngagement.CapKeyPrefix, StringComparison.Ordinal));
        var sums = new List<CapSum>();
        foreach (var cap in caps)
        {
            var unit = cap.Key[RulesOfEngagement.CapKeyPrefix.Length..];
            if (HierarchyModule.SpendMicrosOf(cap.Applies) is not { } max || max <= 0)
            {
                continue;
            }

            long n = 0;
            if (cap.Level != RulesOfEngagement.Team && !long.TryParse(cap.Scope, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out n))
            {
                continue;
            }

            var ofUnit = rows.Where(r => r.Unit == unit);
            string? run;
            string scope;
            string period;
            if (cap.Level == RulesOfEngagement.Team)
            {
                run = runOf(cap.Scope);
                ofUnit = ofUnit.Where(r => r.Team == cap.Scope && (run is not null ? r.Run == run : r.Run is null && r.TsMs >= cap.UpdatedMs));
                scope = $"team {cap.Scope}";
                period = run is null ? "since the cap was set" : "this run";
            }
            else
            {
                run = callerRun;
                var project = cap.Level == RulesOfEngagement.Project;
                ofUnit = ofUnit.Where(r => (project ? phaseOf(r.Item).Projects : phaseOf(r.Item).Goals).Contains(n));
                scope = $"{cap.Level} {HierarchyModule.WorkId(prefix ?? "w", n)}";
                period = "all runs";
            }

            var opId = $"spend-cap:{HierarchyModule.WorkDirKey(dir)}:{cap.Level}:{cap.Scope}:{unit}:{cap.UpdatedMs.ToString(System.Globalization.CultureInfo.InvariantCulture)}:{run ?? "none"}";
            sums.Add(new CapSum(cap, unit, max, ofUnit.Sum(r => r.Micros), scope, period, opId));
        }

        return sums;
    }

    /// <summary>Whether <paramref name="cap"/> counts a row of <paramref name="team"/> on <paramref name="item"/>.</summary>
    private static bool Counts(CapSum cap, string? team, long? item, Func<long?, Phases> phaseOf) => cap.Row.Level switch
    {
        RulesOfEngagement.Team => team is not null && cap.Row.Scope == team,
        RulesOfEngagement.Project => phaseOf(item).Projects.Any(p => p.ToString(System.Globalization.CultureInfo.InvariantCulture) == cap.Row.Scope),
        _ => phaseOf(item).Goals.Any(p => p.ToString(System.Globalization.CultureInfo.InvariantCulture) == cap.Row.Scope),
    };

    /// <summary>The parked-questions store the counter's team asks in: its team's home, else the hierarchy dir.</summary>
    private static string ParkHome(ServiceGraph g, string dir, string? team)
    {
        try
        {
            return team is not null && g.Hierarchy.Messages.TeamHome(dir, null, team) is { Length: > 0 } home ? home : dir;
        }
        catch (Exception e) when (e is not OutOfMemoryException and not OperationCanceledException)
        {
            return dir;
        }
    }

    /// <summary>The question already parked for this cap version and run (its <c>spend_cap</c> op id), answered or not, or null.</summary>
    private static string? ParkedFor(IEnumerable<string> lines, string opId)
    {
        foreach (var line in lines)
        {
            if (JsonText.TryParse(System.Text.Encoding.UTF8.GetBytes(line), out var row) && row.Member("kind") is { IsText: true } kind && kind.AsString() == "parked"
                && row.Member("spend_cap") is { IsText: true } op && op.AsString() == opId && row.Member("id") is { IsText: true } id)
            {
                return id.AsString();
            }
        }

        return null;
    }

    /// <summary>
    /// The <c>spend</c> entries of <paramref name="team"/> on the status board (ah2-512 slice 3): per unit the team has counted in its open run (with no open run,
    /// the rows with no run) or has a team cap for, the total, the max (null with no cap), the scope and the cap's state (null with no cap). Team caps only:
    /// a project or goal cap is listed by <c>ah spend</c>.
    /// </summary>
    internal static JsonArray Board(ServiceGraph g, string ahHome, string hierDir, string? team)
    {
        var entries = new JsonArray();
        if (team is null)
        {
            return entries;
        }

        // a worktree's hierarchy dir stands for its main one, as the verbs' do
        var dir = Path.GetDirectoryName(Path.GetDirectoryName(hierDir)) is { Length: > 0 } root ? g.Config.MainHierarchyDir(root) ?? hierDir : hierDir;
        var (prefix, rows) = Read<(string?, IReadOnlyList<SpendRow>)>(ahHome, (null, []), q => (HierarchyModule.WorkPrefix(q, dir), HierarchyModule.SpendRows(q, dir)));
        // the status compute asks for every team: the run is read only for a team with spend or a cap
        var run = new Lazy<string?>(() => RunOf(g, dir, team));
        var caps = Caps(ahHome, dir, prefix, rows, t => t == team ? run.Value : null, null, static _ => Phases.None)
            .Where(c => c.Row.Level == RulesOfEngagement.Team && c.Row.Scope == team).ToList();
        var counted = rows.Where(r => r.Team == team && r.Run == run.Value).GroupBy(r => r.Unit, StringComparer.Ordinal).ToDictionary(u => u.Key, u => u.Sum(r => r.Micros), StringComparer.Ordinal);
        foreach (var unit in counted.Keys.Union(caps.Select(c => c.Unit), StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var cap = caps.FirstOrDefault(c => c.Unit == unit);
            entries.Add(new JsonObject
            {
                ["unit"] = unit,
                ["total"] = HierarchyModule.SpendFormat(cap?.Total ?? counted.GetValueOrDefault(unit)),
                ["max"] = cap is null ? JsonValue.Null : HierarchyModule.SpendFormat(cap.Max),
                ["scope"] = $"team {team}",
                ["state"] = cap is null ? JsonValue.Null : StateOf(g, dir, cap, team),
            });
        }

        return entries;
    }

    /// <summary>The cap's state as <c>ah spend</c> shows it: <c>off</c>, <c>asked q&lt;k&gt;</c>, <c>reached</c> or <c>under</c>.</summary>
    private static string StateOf(ServiceGraph g, string dir, CapSum cap, string? team)
    {
        if (cap.Row.Off)
        {
            return "off";
        }

        var home = ParkHome(g, dir, team);
        if (g.Io.Files.ReadAskLog(ParkedQuestions.PathIn(home)).TryLines(out var lines) && ParkedFor(lines, cap.OpId) is { } id)
        {
            return $"asked {id}";
        }

        return cap.Total >= cap.Max ? "reached" : "under";
    }

    /// <summary>
    /// Parks the cap's one question (S6, S7), unless this cap version and run already has one (its <c>spend_cap</c> key), whose id it answers. The id is
    /// taken as <c>ah msg decision park</c> takes it, under the shared <c>park:q&lt;N&gt;</c> op id (Amendment 2 P3), so two parks racing in one home never
    /// share an id: the one that lost replays to the winner's line, re-reads, and either finds its cap's question (a concurrent crossing of the same cap) or
    /// takes the next id. Null id and the reason when the store could not be read or written; the count stands either way.
    /// </summary>
    private static (string? Id, bool New, string? Error) Park(ServiceGraph g, string dir, Counter counter, CapSum cap)
    {
        try
        {
            var home = ParkHome(g, dir, counter.Team);
            var path = ParkedQuestions.PathIn(home);
            // as msg Park does: answers recorded in the asks log are matched to their questions before the id is taken
            _ = ParkedQuestions.MatchRecorded(g.Io.Files, g.Hierarchy.StateCalls, home, mayMark: true);
            // ponytail: three tries; a home where every try loses a race reports the park as failed and the count stands
            for (var attempt = 0; attempt < 3; attempt++)
            {
                if (!g.Io.Files.ReadAskLog(path).TryLines(out var stored))
                {
                    return (null, false, "the asks store could not be read");
                }

                if (ParkedFor(stored, cap.OpId) is { } asked)
                {
                    return (asked, attempt > 0, null);
                }

                var id = ParkedQuestions.NextId(ParkedQuestions.Fold(string.Join('\n', stored)));
                g.Hierarchy.StateCalls.AppendParkedQuestion(home, ParkLine(g, counter, cap, id), new StateCallOptions(OperationId: $"park:{id}"));
                g.Hierarchy.Status.RefreshStatus(home);
                if (g.Io.Files.ReadAskLog(path).TryLines(out var after) && ParkedFor(after, cap.OpId) is { } held)
                {
                    return (held, true, null);
                }
            }

            return (null, false, "another question took each id this park tried");
        }
        catch (Exception e) when (e is not OutOfMemoryException and not OperationCanceledException)
        {
            return (null, false, e.Message);
        }
    }

    /// <summary>The parked line of the cap's question (S7), built by the shared builder <c>ah msg decision park</c> uses.</summary>
    private static JsonObject ParkLine(ServiceGraph g, Counter counter, CapSum cap, string id)
    {
        var options = new JsonArray();
        foreach (var label in ParkOptions)
        {
            options.Add(new JsonObject { ["label"] = label });
        }

        var text = $"{cap.Scope}: {HierarchyModule.SpendFormat(cap.Total)}/{HierarchyModule.SpendFormat(cap.Max)} {cap.Unit} ({cap.Period}). Paid calls continue until you answer.";
        var question = new JsonObject { ["question"] = text, ["header"] = ParkHeader, ["multiSelect"] = false, ["options"] = options };
        var line = ParkedQuestions.ParkedLine(id, Timestamps.ToIsoString(g.Io.Clock.UtcNow), counter.Team ?? string.Empty, counter.Session, null, 0, question, null, ParkOptions[1]);
        if (counter.Team is { } team)
        {
            line["asked_for"] = $"orchestrator@{team}";
        }

        line["class"] = HierarchyModule.ChecklistClassOf((string?)null);
        line["spend_cap"] = cap.OpId;
        return line;
    }
}
