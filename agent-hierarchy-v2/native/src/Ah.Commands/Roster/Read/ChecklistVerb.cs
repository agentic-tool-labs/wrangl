using Ah.Core.Json;
using Ah.Hierarchy;

namespace Ah.Commands;

/// <summary>
/// <c>roster checklist show|add|set|done</c>: the Orchestrator's durable list of what is outstanding. It is every plan item of the team's
/// streams plus the team's loose-end stream <c>_run</c>; every write goes through the one plan writer.
/// </summary>
internal static partial class ChecklistVerb
{
    /// <summary>A title that ends with a work id in brackets, <c>[ah2-12]</c> or <c>[b12]</c>: the id.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"\[([a-z][a-z0-9]{0,7}-[0-9]{1,18}|b[0-9]+)\]\s*\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex NamedWork();

    private const string Usage =
        "usage: ah roster checklist show [--all] [--state S] [--stale] [--since <ISO|6h>] | show <id> --trail | add <what> [--role R] [--stream S] [--todo T] [--ref ID] [--parent bN] [--after <id>|last] | set <id> [--after <id>|none] [--state queued|dispatched|waiting-orch|blocked*|waiting-user*|dropped, or a stored name in_progress|waiting_orch|waiting_user*|next|reported|in_review|approved  (* needs --note)] [--owner M] [--todo T] [--ref ID] [--note T] [--option L]... [--for name@team] [--class blocker|soon|eventually] [--parent bN|none] | done <id> [--note T] [--commit SHA]... | ship <sha> [--failed] [--team T] [--plain] | audit [--stream S] [--plain]  — to finish an item use: checklist done <id>. --parent names a backlog epic or deliverable; only its shape is checked, not that it exists. A waiting-user --note (at most 300 characters) names what is asked; the item's description (ah work record <stream>/<item> --type description --title Description --body-file -) says why it reaches the user, what each --option does and what Not needed (Already done, No longer needed) does: the Needs-you card shows it under the note, so the user can answer without coming back to you. Output is JSON; --plain prints text";

    private const string StateList = "queued, dispatched, waiting-orch, blocked*, waiting-user*, dropped, or a stored name: in_progress, waiting_orch, waiting_user*, next, reported, in_review, approved (* needs --note)";

    private static readonly HashSet<string> Flags = new(StringComparer.Ordinal)
    {
        "cwd", "team", "orchestrator-pid", "all", "role", "stream", "todo", "ref", "state", "owner", "note", "commit", "plain", "parent", "trail", "stale", "since", "option", "failed", "for", "class", "after",
    };

    internal static int Run(RosterContext c)
    {
        foreach (var key in c.Opts.Keys)
        {
            if (!Flags.Contains(key))
            {
                throw new RosterFailure($"checklist: unrecognized flag --{key}; {Usage}");
            }
        }

        var words = c.Opts.Positionals.Where(static p => p != "checklist").ToList();
        if (words.Count == 0)
        {
            throw new RosterFailure(Usage);
        }

        var progress = c.Services.Hierarchy.Progress;
        var nowMs = c.Services.Clock.UtcNow.ToUnixTimeMilliseconds();
        var entries = HierarchyModule.ChecklistEntries(progress, c.ProgressPool, c.ProgressTeam);
        switch (words[0])
        {
            case "show" when c.Opts.IsTrue("trail"):
                return words.Count == 2 ? Trail(c, entries, words[1]) : throw new RosterFailure($"checklist show --trail needs an item id; {Usage}");
            case "show" when words.Count == 1 && (c.Opts.Text("state") is not null || c.Opts.IsTrue("stale") || c.Opts.Text("since") is not null):
                return ShowFiltered(c, progress, entries, nowMs);
            case "show" when words.Count == 1:
                var queue = HierarchyModule.ChecklistQueueLines(entries, StallLines(c, nowMs), nowMs);
                if (!c.Opts.IsTrue("plain"))
                {
                    c.Out(ShowJson(c, entries, queue));
                    return 0;
                }

                foreach (var line in HierarchyModule.ChecklistShow(entries, [], nowMs, c.Opts.IsTrue("all")))
                {
                    c.Stdout.Write(line + "\n");
                }

                if (queue.Count > 0)
                {
                    c.Stdout.Write("Queue:\n");
                    foreach (var line in queue)
                    {
                        c.Stdout.Write("  " + line + "\n");
                    }
                }

                return 0;
            case "add" when words.Count >= 2:
                return Add(c, progress, entries, string.Join(' ', words.Skip(1)));
            case "set" when words.Count == 2:
                return Set(c, progress, entries, words[1]);
            case "done" when words.Count == 2 || (words.Count > 2 && c.Opts.IsTrue("commit")):
                return Done(c, progress, entries, words[1], words.Skip(2).ToList());
            case "ship" when words.Count == 2:
                return Ship(c, progress, words[1]);
            case "audit" when words.Count == 1:
                return Audit(c, progress, entries);
            default:
                throw new RosterFailure(Usage);
        }
    }

    /// <summary>The queue stalls as <c>dispatch &lt;item&gt; to &lt;member&gt;</c> lines, from the one file-based scan the failsafe and the Stop rule use, then <c>spawn &lt;role&gt; for &lt;item&gt;</c> for each queued item whose role has no live member; none when that cannot be read.</summary>
    private static List<string> StallLines(RosterContext c, double nowMs)
    {
        try
        {
            var team = c.TeamFile ?? c.ProgressTeam;
            return c.Services.Wake is { } wake
                ? [
                    .. wake.QueueStalls(c.ProgressPool, c.Dir, team, nowMs).Select(static s => HierarchyModule.ChecklistDispatchLine(s.Item, s.Member)),
                    .. wake.QueueUnstaffed(c.ProgressPool, c.Dir, team, nowMs).Select(static s => HierarchyModule.ChecklistSpawnLine(s.Role, s.Item)),
                ]
                : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonParseException or InvalidOperationException)
        {
            c.Stderr.WriteLine($"checklist: queue lines skipped ({e.Message})");
            return [];
        }
    }

    /// <summary><c>show</c> with <c>--state</c>, <c>--stale</c> or <c>--since</c>: the items every given filter matches, in <c>show --all</c> order.</summary>
    private static int ShowFiltered(RosterContext c, IHierarchyProgress progress, IReadOnlyList<ChecklistEntry> entries, double nowMs)
    {
        IEnumerable<ChecklistEntry> shown = HierarchyModule.ChecklistShown(entries, all: true);
        if (c.Opts.Text("state") is { } state)
        {
            shown = shown.Where(e => e.Item.State == state || HierarchyModule.ChecklistDisplayState(e.Item.State) == state);
        }

        if (c.Opts.IsTrue("stale"))
        {
            var ledger = progress.ReadLedger(c.ProgressPool, c.ProgressTeam);
            shown = shown.Where(e => HierarchyModule.ChecklistIsStale(e.Item.State, HierarchyModule.ChecklistLastActivityMs(e.Item, ledger), nowMs));
        }

        if (c.Opts.Text("since") is { } since)
        {
            var from = SinceMs(since, nowMs) ?? throw new RosterFailure($"checklist show: --since {since} is not an ISO time or an age like 6h, 30m or 2d");
            shown = shown.Where(e => HierarchyModule.ChecklistTrail(PlanLog(c, e.Stream), e.Item.Item)
                .Any(r => HierarchyModule.ChecklistIsAutomaticMove(r) && Ms(r.Ts) is { } at && at >= from));
        }

        var list = shown.ToList();
        if (!c.Opts.IsTrue("plain"))
        {
            c.Out(ShowJson(c, list, [], list));
            return 0;
        }

        foreach (var line in HierarchyModule.ChecklistShow(list, [], nowMs, all: true))
        {
            c.Stdout.Write(line + "\n");
        }

        return 0;
    }

    /// <summary><c>show &lt;id&gt; --trail</c>: the item and every row for it, oldest first, then the command that undoes the last automatic move.</summary>
    private static int Trail(RosterContext c, IReadOnlyList<ChecklistEntry> entries, string id)
    {
        var entry = Find(c, entries, "show", id);
        var trail = HierarchyModule.ChecklistTrail(PlanLog(c, entry.Stream), entry.Item.Item);
        var key = HierarchyModule.ChecklistKey(entry);
        var revert = HierarchyModule.ChecklistRevertCommand(key, trail);
        if (c.Opts.IsTrue("plain"))
        {
            c.Stdout.Write($"{key} {HierarchyModule.ChecklistDisplayLabel(entry.Item)} | {entry.Item.Title}\n");
            foreach (var r in trail)
            {
                var cause = r.Cause is { } why ? $" cause={why.Event} ref={why.Ref} at={why.At}" : string.Empty;
                c.Stdout.Write($"  {r.Ts} {r.Event} by={r.By} {(r.From.Length == 0 ? "-" : r.From)}→{r.To}{cause}\n");
            }

            if (revert is not null)
            {
                c.Stdout.Write("undo: " + revert + "\n");
            }

            return 0;
        }

        var rows = new JsonArray();
        foreach (var r in trail)
        {
            rows.Add(new JsonObject
            {
                ["ts"] = r.Ts,
                ["event"] = r.Event,
                ["by"] = r.By,
                ["cause"] = r.Cause is { } why ? new JsonObject { ["event"] = why.Event, ["ref"] = why.Ref, ["at"] = why.At } : JsonValue.Null,
                ["from"] = r.From,
                ["to"] = r.To,
            });
        }

        c.Out(new JsonObject
        {
            ["id"] = entry.Item.Item,
            ["stream"] = entry.Stream,
            ["title"] = entry.Item.Title,
            ["state"] = HierarchyModule.ChecklistDisplayState(entry.Item.State),
            ["rows"] = rows,
            ["undo"] = revert is null ? JsonValue.Null : revert,
        });
        return 0;
    }

    /// <summary>
    /// <c>checklist audit [--stream S] [--plain]</c> (board-derive § E): every checklist row replayed from its facts through the movers against an
    /// in-memory store; prints the rows whose stored state differs. Read only; exit 0 with findings too.
    /// </summary>
    private static int Audit(RosterContext c, IHierarchyProgress progress, IReadOnlyList<ChecklistEntry> entries)
    {
        IReadOnlyList<string> streams = c.Opts.Text("stream") is { } only ? [only] : [.. entries.Select(static e => e.Stream).Distinct(StringComparer.Ordinal)];
        var (count, disagree) = AuditOf(c, progress, streams);
        if (c.Opts.IsTrue("plain"))
        {
            foreach (var f in disagree)
            {
                c.Stdout.Write($"{f.Stream}/{f.Id}  stored {f.Stored}  expected {f.Expected}  why {f.Why}{(f.PreCause ? "  (pre-cause history: unverifiable)" : string.Empty)}\n");
            }

            c.Stdout.Write($"checked {count}, disagree {disagree.Count}\n");
            return 0;
        }

        var rows = new JsonArray();
        foreach (var f in disagree)
        {
            rows.Add(new JsonObject { ["id"] = f.Id, ["stream"] = f.Stream, ["stored"] = f.Stored, ["expected"] = f.Expected, ["why"] = f.Why, ["pre_cause"] = f.PreCause });
        }

        c.Out(new JsonObject { ["checked"] = count, ["disagree"] = rows });
        return 0;
    }

    /// <summary>The daemon's board audit of <paramref name="team"/>: the audit verb's findings over every stream, and the newest ship its plan logs record. Read only.</summary>
    internal static (string? LastShip, Func<IReadOnlyList<ChecklistAuditFinding>> Findings) DaemonAudit(
        IRosterCliServices services, string dir, string projectRoot, string team, Func<string, string, IReadOnlyList<ChecklistEntry>> entriesOf,
        Func<string, string, string, Func<string?>, string?> lastShipOf)
    {
        var c = new RosterContext(services, RosterArgs.Parse(["--team", team]), "checklist", projectRoot, TextWriter.Null, TextWriter.Null) { TeamArg = team, TeamFile = team };
        c.PinDir(dir);
        var progress = services.Hierarchy.Progress;
        IReadOnlyList<string> streams = [.. entriesOf(c.ProgressPool, c.ProgressTeam).Select(static e => e.Stream).Distinct(StringComparer.Ordinal)];
        var lastShip = streams.Select(s => lastShipOf(c.ProgressPool, c.ProgressTeam, s, () => HierarchyModule.ChecklistLastShip(PlanLog(c, s))))
            .Where(static s => s is not null).Max(StringComparer.Ordinal);
        return (lastShip, () => AuditOf(c, progress, streams).Disagree);
    }

    private static (int Count, IReadOnlyList<ChecklistAuditFinding> Disagree) AuditOf(RosterContext c, IHierarchyProgress progress, IReadOnlyList<string> streams)
    {
        var core = c.Services.Hierarchy.Core;
        var exchanges = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var e in ReleaseGate.ExchangesFor(c, c.Dir, null, []))
        {
            if (MemberPlanning.Text(e.Get("id")) is { } id)
            {
                exchanges[id] = e;
            }
        }

        static string Time(string? text) =>
            text is not null && DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var at) ? HierarchyModule.ChecklistEventTime(at) : text ?? string.Empty;
        ChecklistAuditRequest? RequestOf(string id)
        {
            if (!exchanges.TryGetValue(id, out var e) || e.Get("request")?.Member("path") is not { IsText: true } path || core.ReadMsgFile(path.AsString()).Member("fm") is not { } fm)
            {
                return null;
            }

            string? F(JsonValue? from, string key) => MemberPlanning.Text(from?.Member(key)) is { Length: > 0 } v && v != "null" ? v : null;
            var responses = new List<ChecklistAuditResponse>();
            if (e.Get("response")?.Member("path") is { IsText: true } responsePath && core.ReadMsgFile(responsePath.AsString()) is { } read && read.Member("fm") is { } rfm)
            {
                var text = read.Member("text") is { IsText: true } stored ? stored.AsString() : string.Empty;
                var body = text.StartsWith("---", StringComparison.Ordinal) && text.IndexOf("\n---", 3, StringComparison.Ordinal) is var end and > 0 ? text[(end + 4)..] : text;
                responses.Add(new ChecklistAuditResponse(Time(F(rfm, "created")), body, F(rfm, "closed") == "true", F(rfm, "closed_reason"), F(rfm, "withdrawn") == "true"));
            }

            return new ChecklistAuditRequest(id, Time(F(fm, "created")), F(fm, "to"), F(fm, "to_name"), F(fm, "from"), F(fm, "slug"), F(fm, "sha"), F(fm, "item"), F(fm, "parent"), responses);
        }

        return HierarchyModule.ChecklistAudit(progress, c.ProgressPool, c.ProgressTeam, streams, stream => PlanLog(c, stream), RequestOf,
            (rev, commit) => ReleaseGate.Git(c, c.Cwd, "merge-base", "--is-ancestor", rev, commit) is { Ok: true });
    }

    internal static string? PlanLog(RosterContext c, string stream) =>
        c.Services.Hierarchy.Progress.PlanLogText(c.ProgressPool, c.ProgressTeam, stream);

    private static double? Ms(string text) =>
        DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var at) ? at.ToUnixTimeMilliseconds() : null;

    /// <summary>An ISO time, or an age (<c>30m</c>, <c>6h</c>, <c>2d</c>) before <paramref name="nowMs"/>, in epoch milliseconds; null when it is neither.</summary>
    private static double? SinceMs(string text, double nowMs)
    {
        if (text.Length >= 2 && long.TryParse(text[..^1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n))
        {
            var unit = text[^1] switch { 'm' => 60_000d, 'h' => 3_600_000d, 'd' => 86_400_000d, _ => 0d };
            if (unit > 0)
            {
                return nowMs - (n * unit);
            }
        }

        return text.Contains('T', StringComparison.Ordinal) ? Ms(text) : null;
    }

    private static JsonValue ShowJson(RosterContext c, IReadOnlyList<ChecklistEntry> entries, IReadOnlyList<string> queue, IReadOnlyList<ChecklistEntry>? shown = null)
    {
        var items = new JsonArray();
        foreach (var e in shown ?? HierarchyModule.ChecklistShown(entries, c.Opts.IsTrue("all")))
        {
            var refs = new JsonArray();
            foreach (var reference in e.Item.Refs ?? [])
            {
                refs.Add(reference);
            }

            items.Add(new JsonObject
            {
                ["id"] = e.Item.Item,
                ["stream"] = e.Stream,
                ["title"] = e.Item.Title,
                ["state"] = HierarchyModule.ChecklistDisplayState(e.Item.State),
                ["owner"] = e.Item.Holder is { } owner ? owner : JsonValue.Null,
                ["note"] = e.Item.Note is { } note ? note : JsonValue.Null,
                ["answer"] = e.Item.Answer is { } answer ? answer : JsonValue.Null,
                ["ref"] = (e.Item.Refs ?? []).Count > 0 ? e.Item.Refs![0] : JsonValue.Null,
                ["refs"] = refs,
                ["class"] = e.Item.State == "waiting_user" ? HierarchyModule.ChecklistClassOf(e.Item) : JsonValue.Null,
                ["after"] = e.Item.After is { } after ? after : JsonValue.Null,
            });
        }

        var lines = new JsonArray();
        foreach (var line in queue)
        {
            lines.Add(line);
        }

        return new JsonObject { ["team"] = c.ProgressTeam, ["items"] = items, ["queue"] = lines };
    }

    /// <summary>Who a row this verb writes is by: the member the env names, else the team's Orchestrator.</summary>
    private static string By(RosterContext c) =>
        c.EnvText(Ah.Mesh.MeshModule.MemberMarkerEnv) is { } member ? $"{member}@{c.ProgressTeam}" : HierarchyModule.OrchestratorAddress(c.ProgressTeam);

    private static int Write(RosterContext c, IHierarchyProgress progress, string stream, string item, string verb, List<PlanInput> events, bool? created = null, string? asked = null)
    {
        var by = By(c);
        // a row the verb wrote is a recorded fact the board audit replays as it is (board-derive § E2)
        var verbCause = new PlanCause(HierarchyModule.ChecklistCauseVerb, verb, HierarchyModule.ChecklistEventTime(c.Services.Clock.UtcNow));
        var result = progress.RecordChecklist(c.ProgressPool, c.ProgressTeam, stream, by, [.. events.Select(e => e with { Cause = e.Cause ?? verbCause })], c.Proved().SessionId);
        if (result.Off)
        {
            throw new RosterFailure($"checklist {verb}: progress is off for {c.ProgressTeam}; nothing written");
        }

        if (result.Refused is { } refused)
        {
            throw new RosterFailure($"checklist {verb}: {refused}; nothing written");
        }

        if (result.Warning is { } warning)
        {
            c.Warn($"checklist {verb}: the event was recorded, but {warning}");
        }

        var after = HierarchyModule.ChecklistEntries(progress, c.ProgressPool, c.ProgressTeam).FirstOrDefault(e => e.Stream == stream && e.Item.Item == item);
        var state = after is null ? "unknown" : HierarchyModule.ChecklistDisplayState(after.Item.State);
        // ah2-210 D5: a state asked under another name than the one stored is echoed, so the caller sees both
        var echoed = asked is not null && asked != state ? asked : null;
        if (echoed is not null)
        {
            c.Stderr.Write($"`{echoed}` is shown as `{state}`\n");
        }

        return Report(c, stream, item, state, created, echoed);
    }

    private static int Report(RosterContext c, string stream, string item, string state, bool? created, string? asked = null)
    {
        if (c.Opts.IsTrue("plain"))
        {
            c.Stdout.Write($"checklist: {item} → {state}\n");
            return 0;
        }

        var result = new JsonObject { ["item"] = item, ["stream"] = stream, ["state"] = state };
        if (asked is not null)
        {
            result["asked"] = asked;
        }

        if (created is { } made)
        {
            result["created"] = made;
        }

        c.Out(result);
        return 0;
    }

    private static int Add(RosterContext c, IHierarchyProgress progress, IReadOnlyList<ChecklistEntry> entries, string what)
    {
        var stream = c.Opts.Text("stream") ?? ProgressStreamRun;
        if (HierarchyModule.ChecklistRecentTwin(entries, stream, what, c.Services.Clock.UtcNow.ToUnixTimeMilliseconds()) is { } twin)
        {
            return Report(c, stream, twin.Item.Item, HierarchyModule.ChecklistDisplayState(twin.Item.State), false);
        }

        var item = HierarchyModule.ChecklistSlug(what, entries.Select(static e => e.Item.Item).ToHashSet(StringComparer.Ordinal));
        var events = new List<PlanInput> { new(item, "add", what), new(item, "next") };
        events.AddRange(StateAndOwner(c, item, c.Opts.Text("note"), []));
        if (c.Opts.Text("role") is { } role)
        {
            events.Add(new PlanInput(item, "role", Role: role));
        }

        if (c.Opts.Text("todo") is { } todo)
        {
            events.Add(new PlanInput(item, "todo", Todo: todo));
        }

        ChecklistEntry? auto = null;
        if (c.Opts.Text("ref") is { } reference)
        {
            auto = AutoHoldingRef(entries, reference, null);
            events.Add(new PlanInput(item, "ref", Ref: reference));
        }

        if (Parent(c) is { } parent)
        {
            events.Add(parent with { Item = item });
        }
        else if (NamedWork().Match(what) is { Success: true } named && c.Services.WorkParent(c.ProgressPool, c.ProgressTeam, named.Groups[1].Value) is { } row)
        {
            // board-truth D3: a title ending in a work id that names a backlog row is linked to it; an id that names none is ignored
            events.Add(new PlanInput(item, "parent") { Parent = row });
        }

        if (After(c, entries, "add", stream, item) is { } after)
        {
            events.Add(after);
        }

        var code = Write(c, progress, stream, item, "add", events, true);
        Supersede(c, progress, auto, stream + "/" + item);
        c.Stderr.Write(HierarchyModule.WorkDescriptionNudge(stream + "/" + item) + "\n");
        return code;
    }

    /// <summary>
    /// The item <c>ah msg new</c> added for the request that already lists <paramref name="reference"/>, not yet superseded: its ref yields to the
    /// item being linked. Null when none; any other item may share the ref (ah2-346).
    /// </summary>
    private static ChecklistEntry? AutoHoldingRef(IReadOnlyList<ChecklistEntry> entries, string reference, ChecklistEntry? self) =>
        entries.FirstOrDefault(e => e != self && HierarchyModule.ChecklistIsAuto(e.Item) && !HierarchyModule.ChecklistSuperseded(e) && (e.Item.Refs ?? []).Contains(reference, StringComparer.Ordinal));

    /// <summary>
    /// After the ref has been written to <paramref name="target"/>: the auto item that held it is dropped with the note "superseded by &lt;target&gt;".
    /// A refused drop leaves the ref on both; the warning says so, and the request's replies stay ambiguous until the auto item is dropped by hand.
    /// </summary>
    private static void Supersede(RosterContext c, IHierarchyProgress progress, ChecklistEntry? auto, string target)
    {
        if (auto is null)
        {
            return;
        }

        var result = progress.RecordChecklist(c.ProgressPool, c.ProgressTeam, auto.Stream, HierarchyModule.OrchestratorAddress(c.ProgressTeam), [new PlanInput(auto.Item.Item, "drop", Note: "superseded by " + target) { Cause = new PlanCause(HierarchyModule.ChecklistCauseVerb, "supersede", HierarchyModule.ChecklistEventTime(c.Services.Clock.UtcNow)) }], c.Proved().SessionId);
        if (result.Off || result.Refused is not null)
        {
            c.Warn($"checklist: the ref was linked to {target}, but auto item {HierarchyModule.ChecklistKey(auto)} could not be dropped ({result.Refused ?? "progress is off"}); drop it: ah roster checklist set {HierarchyModule.ChecklistKey(auto)} --state dropped");
        }
    }

    /// <summary>
    /// The <c>parent</c> event <c>--parent</c> asks for (<c>none</c> clears it), or null without the flag. Only the shape is checked: the backlog
    /// is not read here, and a link to a missing or lower-ranked item is ignored where the links are drawn.
    /// </summary>
    private static PlanInput? Parent(RosterContext c) => c.Opts.Text("parent") switch
    {
        null => null,
        "none" or "" => new PlanInput(string.Empty, "parent") { Parent = string.Empty },
        var p when HierarchyModule.IsBacklogId(p) => new PlanInput(string.Empty, "parent") { Parent = p },
        var p when c.Services.WorkParent(c.ProgressPool, c.ProgressTeam, p) is { } row => new PlanInput(string.Empty, "parent") { Parent = row },
        var p => throw new RosterFailure($"checklist: --parent {p} is not a backlog id (b<N>) or none; nothing written"),
    };

    /// <summary>
    /// The <c>after</c> event <c>--after</c> asks for on <paramref name="stream"/>/<paramref name="item"/> (<c>none</c> clears it on <c>set</c>;
    /// <c>last</c> on <c>add</c> is the item this caller last added to the stream), or null without the flag. A predecessor that is unknown, the
    /// item itself, or one that would close a cycle is not stored: one stderr line says why and the rest of the command still applies (ah2-209 D1, D6).
    /// </summary>
    private static PlanInput? After(RosterContext c, IReadOnlyList<ChecklistEntry> entries, string verb, string stream, string item)
    {
        if (c.Opts.Text("after") is not { } named)
        {
            return null;
        }

        void Note(string why) => c.Stderr.Write($"checklist {verb}: --after {named} not stored ({why})\n");
        if (named is "none" or "")
        {
            return verb == "set" ? new PlanInput(item, "after") { After = string.Empty } : null;
        }

        ChecklistEntry? target;
        if (verb == "add" && named == "last")
        {
            var by = By(c);
            target = entries.LastOrDefault(e => e.Stream == stream && e.Item.AddedBy == by);
            if (target is null)
            {
                c.Stderr.Write($"checklist add: --after last ignored (no item added to {stream} by {by})\n");
                return null;
            }
        }
        else
        {
            var error = HierarchyModule.ChecklistResolve(entries, named, out target);
            if (target is null && error == $"no item {named}" && c.Services.WorkAlias(c.ProgressPool, c.ProgressTeam, named) is { } alias)
            {
                error = HierarchyModule.ChecklistResolve(entries, alias, out target);
            }

            if (target is null)
            {
                Note(error == $"no item {named}" ? $"{error} in team {c.ProgressTeam}; a predecessor in another team or pool is not supported" : error ?? "no item " + named);
                return null;
            }
        }

        var self = stream + "/" + item;
        var key = HierarchyModule.ChecklistKey(target);
        if (key == self)
        {
            Note("an item cannot come after itself");
            return null;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal) { key };
        for (var at = target.Item.After; at is not null && seen.Add(at); at = entries.FirstOrDefault(e => HierarchyModule.ChecklistKey(e) == at)?.Item.After)
        {
            if (at == self)
            {
                Note($"{key} already comes after {self}, so this would make a cycle");
                return null;
            }
        }

        return new PlanInput(item, "after") { After = key };
    }

    private static ChecklistEntry Find(RosterContext c, IReadOnlyList<ChecklistEntry> entries, string verb, string id)
    {
        var error = HierarchyModule.ChecklistResolve(entries, id, out var entry);
        if (entry is null && error == $"no item {id}" && c.Services.WorkAlias(c.ProgressPool, c.ProgressTeam, id) is { } alias)
        {
            // only after the lookup misses, so a literal slug named like a work id wins
            _ = HierarchyModule.ChecklistResolve(entries, alias, out entry);
        }

        return entry ?? throw new RosterFailure($"checklist {verb}: {error ?? "no item " + id}; nothing written");
    }

    /// <summary>The events <c>--state</c> and <c>--owner</c> ask for, on an item being set or just added.</summary>
    private static List<PlanInput> StateAndOwner(RosterContext c, string id, string? note, List<string> options)
    {
        var events = new List<PlanInput>();
        if (c.Opts.Text("state") is { } state)
        {
            // a hand-written state row is an override: the transition table's older events no longer move the item
            var manual = HierarchyModule.ChecklistManualCause(c.Services.Clock.UtcNow);
            events.Add((state switch
            {
                "queued" or "next" => new PlanInput(id, "next"),
                "dispatched" or "in_progress" => new PlanInput(id, "start"),
                "in_review" => new PlanInput(id, "review"),
                "reported" => new PlanInput(id, "report"),
                "approved" => new PlanInput(id, "approve"),
                "waiting-orch" or "waiting_orch" => new PlanInput(id, "wait-orch", Note: note ?? c.Opts.Text("todo") ?? "waiting on the Orchestrator"),
                "blocked" => new PlanInput(id, "block", Note: note ?? throw new RosterFailure("checklist: --state blocked needs --note; nothing written")),
                "waiting-user" or "waiting_user" => new PlanInput(id, "wait-user", Note: note ?? throw new RosterFailure("checklist: --state waiting-user needs --note; nothing written")) { Options = options.Count > 0 ? options : null },
                "dropped" => new PlanInput(id, "drop", Note: note),
                "done" => throw new RosterFailure($"ah: done is not a state; run ah roster checklist done {id}; nothing written"),
                _ => throw new RosterFailure($"checklist: --state {state} is not one of {StateList}; nothing written"),
            }) with { Cause = manual });
        }

        if (c.Opts.Text("owner") is { } owner)
        {
            events.Add(new PlanInput(id, "assign", Holder: owner));
        }

        return events;
    }

    private static int Set(RosterContext c, IHierarchyProgress progress, IReadOnlyList<ChecklistEntry> entries, string id)
    {
        var entry = Find(c, entries, "set", id);
        id = entry.Item.Item;
        var events = new List<PlanInput>();
        var note = c.Opts.Text("note");
        var options = c.Opts.All("option").ToList();
        if (options.Count > 0 && c.Opts.Text("state") is not ("waiting-user" or "waiting_user"))
        {
            throw new RosterFailure("checklist set: --option goes only with --state waiting-user; nothing written");
        }

        if (options.Count > 0 && HierarchyModule.ChecklistOptionsRefusal(options) is { } refusedOptions)
        {
            throw new RosterFailure($"checklist set: {refusedOptions}; nothing written");
        }

        var askedFor = c.Opts.Text("for");
        if (askedFor is not null && c.Opts.Text("state") is not ("waiting-user" or "waiting_user"))
        {
            throw new RosterFailure("checklist set: --for goes only with --state waiting-user; nothing written");
        }

        if (askedFor is not null && HierarchyModule.AskerRefusal(askedFor, c.TeamFile, team => c.Services.Roster.ReadTeamRecord(c.Dir, team)) is { } notMember)
        {
            throw new RosterFailure($"checklist set: {notMember}; nothing written");
        }

        var cls = c.Opts.Text("class");
        if (cls is not null && c.Opts.Text("state") is not ("waiting-user" or "waiting_user"))
        {
            throw new RosterFailure("checklist set: --class goes only with --state waiting-user; nothing written");
        }

        if (cls is not null && !HierarchyModule.ChecklistClasses.Contains(cls, StringComparer.Ordinal))
        {
            throw new RosterFailure($"checklist set: --class {cls} is not one of {string.Join(", ", HierarchyModule.ChecklistClasses)}; nothing written");
        }

        events.AddRange(StateAndOwner(c, id, note, options).Select(e => e.Event == "wait-user" ? e with { AskedFor = askedFor, Class = cls } : e));

        if (c.Opts.Text("todo") is { } todo)
        {
            events.Add(new PlanInput(id, "todo", Todo: todo));
        }

        ChecklistEntry? auto = null;
        if (c.Opts.Text("ref") is { } reference)
        {
            auto = AutoHoldingRef(entries, reference, entry);
            events.Add(new PlanInput(id, "ref", Ref: reference));
        }

        if (note is not null && c.Opts.Text("state") is null or "queued" or "dispatched")
        {
            events.Add(new PlanInput(id, "note", Note: note));
        }

        if (Parent(c) is { } parent)
        {
            events.Add(parent with { Item = id });
        }

        if (After(c, entries, "set", entry.Stream, id) is { } after)
        {
            events.Add(after);
        }
        else if (events.Count == 0 && c.Opts.Text("after") is not null)
        {
            // ah2-209 D1: an --after that was not stored is a note, not a failure; with nothing else asked there is nothing to write
            return Report(c, entry.Stream, id, HierarchyModule.ChecklistDisplayState(entry.Item.State), null);
        }

        if (events.Count == 0)
        {
            throw new RosterFailure($"checklist set: nothing to change; {Usage}");
        }

        var code = Write(c, progress, entry.Stream, id, "set", events, asked: c.Opts.Text("state"));
        Supersede(c, progress, auto, HierarchyModule.ChecklistKey(entry));
        return code;
    }

    /// <summary><c>commit</c> is a flag that never takes a value everywhere else, so the shas given after <c>--commit</c> arrive as the words after the item id.</summary>
    private static int Done(RosterContext c, IHierarchyProgress progress, IReadOnlyList<ChecklistEntry> entries, string id, List<string> commits)
    {
        var entry = Find(c, entries, "done", id);
        id = entry.Item.Item;
        commits.AddRange(c.Opts.All("commit"));
        var code = Write(c, progress, entry.Stream, id, "done", [new PlanInput(id, "done", Commits: commits.Count > 0 ? commits : null, Note: c.Opts.Text("note")) { Cause = HierarchyModule.ChecklistManualCause(c.Services.Clock.UtcNow) }]);
        // board-truth D2: the last open work row under a backlog item closes it; an answered question closed with no commits is not work (b204 D1, D3)
        if (entry.Item.Parent is { Length: > 0 } parent && !(commits.Count == 0 && HierarchyModule.ChecklistAnsweredWithNoWork(entry.Item)) && HierarchyModule.ChecklistParentFinished(progress, c.ProgressPool, c.ProgressTeam, parent) is { } done
            && c.Services.CloseDoneBacklog(c.ProgressPool, parent, done, $"checklist {HierarchyModule.ChecklistKey(entry)} done") is { } problem)
        {
            c.Warn($"checklist done: backlog row {parent} not closed ({problem})");
        }

        return code;
    }

    /// <summary>
    /// <c>ship &lt;sha&gt; [--failed]</c>: a ship script's call after its push. Every open item whose work <paramref name="given"/> carries is marked done;
    /// with <c>--failed</c> (its gate failed) each gets a note instead. The sha is read in <c>--cwd</c>'s repo.
    /// </summary>
    private static int Ship(RosterContext c, IHierarchyProgress progress, string given)
    {
        var sha = ReleaseGate.Git(c, c.Cwd, "rev-parse", "--verify", "-q", given + "^{commit}") is { Ok: true } full && full.Stdout.Trim() is { Length: > 0 } text
            ? text
            : throw new RosterFailure($"checklist ship: commit {given} not found in {c.Cwd}; nothing written");
        var failed = c.Opts.IsTrue("failed");
        if (failed && progress.AppendLedger(c.ProgressPool, c.ProgressTeam, new LedgerEvent(HierarchyModule.LedgerKindGate, Sha: sha, Text: $"gate failed {sha[..8]}", Result: "fail")) is { Error: { } error })
        {
            c.Warn($"checklist ship: gate row not written ({error})");
        }

        var (items, skipped, refused) = HierarchyModule.ChecklistOnShip(
            progress, c.ProgressPool, c.ProgressTeam, sha, failed, c.Services.Clock.UtcNow, HierarchyModule.OrchestratorAddress(c.ProgressTeam), c.Proved().SessionId,
            id => LedgerVerb.SlugOf(c, id),
            (rev, commit) => ReleaseGate.Git(c, c.Cwd, "merge-base", "--is-ancestor", rev, commit) is { Ok: true },
            failed ? null : (row, at) =>
            {
                if (c.Services.CloseShippedBacklog(c.ProgressPool, row, at) is { } problem)
                {
                    c.Warn($"checklist ship: backlog row {row} not closed ({problem})");
                }
            });
        if (refused is not null)
        {
            c.Warn($"checklist ship: {refused}");
        }

        if (c.Opts.IsTrue("plain"))
        {
            c.Stdout.Write($"checklist ship {sha[..8]}: {items.Count} item(s) {(failed ? "noted gate failed" : "done")}{(items.Count > 0 ? ": " + string.Join(", ", items) : string.Empty)}{(skipped.Count > 0 ? "; not closed: " + string.Join(", ", skipped) : string.Empty)}\n");
            return 0;
        }

        var list = new JsonArray();
        foreach (var item in items)
        {
            list.Add(item);
        }

        var skips = new JsonArray();
        foreach (var skip in skipped)
        {
            skips.Add(skip);
        }

        c.Out(new JsonObject { ["sha"] = sha, ["failed"] = failed, ["items"] = list, ["skipped"] = skips, ["team"] = c.ProgressTeam });
        return 0;
    }

    private const string ProgressStreamRun = "_run";
}
