using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Hierarchy;

namespace Ah.Watch;

/// <summary>What the queue rules know about one member: <paramref name="OnJob"/> is true while it holds or waits for a with-slot slot.</summary>
public sealed record QueueMemberFact(string Name, string Role, bool? Live, string? Activity, string? ActivityAt, bool Dismissed, bool OnJob, string? SpawnedAt = null, string? Stream = null, bool StreamClosed = false);

/// <summary>An open or recent dispatch as the queue rules read it; the member is named by <paramref name="ToName"/>, else by <paramref name="ToAddr"/>.</summary>
public sealed record QueueDispatchFact(string? ToName, string? ToAddr, string? Created, bool Open);

/// <summary>A backlog item nobody holds that <c>ah backlog next</c> would offer, in wave <c>now</c> or <c>next</c>.</summary>
public sealed record ReadyBacklogItem(string Item, string Title, string? Wave, int? Rank);

/// <summary>One queued item for the web view: its title, when it was queued, when it stalls, the members that could take it now and when its role was last sent a dispatch (epoch milliseconds).</summary>
public sealed record QueueRow(string Item, string Role, double QueuedSinceMs, double StallAtMs, IReadOnlyList<string> Eligible, double? LastDispatchMs)
{
    /// <summary>The item's plan title (ah-ui-ia U0 (a)); empty when the row was built without one.</summary>
    public string Title { get; init; } = string.Empty;
}

/// <summary>The queued items the files show stalled, with the members the rules saw.</summary>
public sealed record QueueStallScan(IReadOnlyList<QueueEntry> Stalled, IReadOnlyList<QueueMember> Members);

public static partial class WatchModule
{
    /// <summary>One queued item as the queue lane serves it: the times as UTC ISO strings, and <c>last_dispatch_at</c> null when the role was never sent a dispatch.</summary>
    public static JsonValue QueueRowToJson(QueueRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var eligible = new JsonArray();
        foreach (var name in row.Eligible)
        {
            eligible.Add(name);
        }

        static string Iso(double ms) =>
            DateTimeOffset.FromUnixTimeMilliseconds((long)ms).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        return JsonValue.FromObject(new JsonObject
        {
            ["item"] = row.Item,
            ["role"] = row.Role,
            ["queued_since"] = Iso(row.QueuedSinceMs),
            ["stall_at"] = Iso(row.StallAtMs),
            ["eligible_members"] = eligible,
            ["last_dispatch_at"] = row.LastDispatchMs is { } last ? Iso(last) : JsonValue.Null,
            ["title"] = row.Title,
            // ah-ui-ia U0 (a), Orchestrator ruling 2026-10-31: no server `stalled` (it would need a timer); the view takes stalled =
            // idle_eligible && now - queued_since >= stall_at - queued_since, and this changes only on the member and state events that re-read the lane
            ["idle_eligible"] = row.Eligible.Count > 0,
        });
    }

    /// <summary>
    /// The queue rules run over the files: the team's members as the status view describes them, every request the Orchestrator sent in
    /// the team (open or answered) and the checklist entries. The failsafe and the Stop rule both ask this, so they agree with each other.
    /// </summary>
    public static QueueStallScan QueueStallsFromFiles(
        HierarchyServices hs, JsonValue teamRecord, IReadOnlyList<ChecklistEntry> entries, string hierarchyDir, string team, double nowMs, Func<string, bool> onJob)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var (members, dispatches, running) = QueueFactsAndRunningFromFiles(hs, teamRecord, hierarchyDir, team, onJob);
        return new QueueStallScan(HierarchyModule.QueueStalled(HierarchyModule.ChecklistWithoutRunningDispatch(entries, running), members, dispatches, nowMs), members);
    }

    /// <summary>
    /// The queued items that wait for a role with no live member while the team is under <paramref name="budget"/> (null: no limit), read from the files with the
    /// same facts as the stall rule. A spawn already in progress does not hold an item back here: the checkpoint names what is missing, and the daemon's
    /// event is the one that waits for it.
    /// </summary>
    public static IReadOnlyList<QueueEntry> QueueUnstaffedFromFiles(
        HierarchyServices hs, JsonValue teamRecord, IReadOnlyList<ChecklistEntry> entries, string hierarchyDir, string team, double nowMs, Func<string, bool> onJob, int? budget)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var (members, dispatches, running) = QueueFactsAndRunningFromFiles(hs, teamRecord, hierarchyDir, team, onJob);
        return HierarchyModule.QueueUnstaffed(HierarchyModule.ChecklistWithoutRunningDispatch(entries, running), members, dispatches, nowMs, budget, recentSpawnMs: 0);
    }

    /// <summary>The budget a team gets when no run anchor of it or of an ancestor team names a level.</summary>
    public const int DefaultSessionBudget = SessionBudget.Default;

    /// <summary>
    /// The most live members <paramref name="team"/> (null for the default team) may hold: the level of its own open run anchor, else of the nearest
    /// ancestor team's, else <see cref="DefaultSessionBudget"/>; null means no limit. <paramref name="anchorBodyOf"/> answers a team's open anchor text, or null
    /// when it has none; <paramref name="parentOf"/> answers a team's parent team, or null.
    /// </summary>
    public static int? SessionBudgetOf(string? team, Func<string?, string?> anchorBodyOf, Func<string?, string?> parentOf) =>
        SessionBudget.ForTeam(team, anchorBodyOf, parentOf);

    /// <summary>The member and dispatch facts the queue rules read from the files: the one place both the stall rule and the queue view get them.</summary>
    public static (IReadOnlyList<QueueMember> Members, IReadOnlyList<QueueDispatch> Dispatches) QueueFactsFromFiles(
        HierarchyServices hs, JsonValue teamRecord, string hierarchyDir, string team, Func<string, bool> onJob)
    {
        var (members, dispatches, _) = QueueFactsAndRunningFromFiles(hs, teamRecord, hierarchyDir, team, onJob);
        return (members, dispatches);
    }

    private static (IReadOnlyList<QueueMember> Members, IReadOnlyList<QueueDispatch> Dispatches, IReadOnlyList<string> RunningRequestIds) QueueFactsAndRunningFromFiles(
        HierarchyServices hs, JsonValue teamRecord, string hierarchyDir, string team, Func<string, bool> onJob)
    {
        ArgumentNullException.ThrowIfNull(hs);
        ArgumentNullException.ThrowIfNull(onJob);
        static List<JsonObject> Objects(JsonArray array) => [.. array.Where(static v => v.IsMap).Select(static v => v.AsObject())];
        static string? Text(JsonObject o, string key) => o.Get(key) is { IsText: true } v ? v.AsString() : null;
        var rows = teamRecord.IsMap && teamRecord.AsObject().Get("members") is { IsArray: true } array ? Objects(array.AsArray()) : [];
        var described = Objects(hs.Status.DescribeMembers(hierarchyDir, teamRecord, hs.Roster.ReadRoster(hierarchyDir), team));
        // ah-msgs-fast-2 §3.8 item 7 (rulings Q1-Q4): with stored last-dispatch rows, the live listing of the same dir gives every open
        // exchange and the rows give each member's newest dispatch, closed or not; with none, the full listing as before
        var lastDispatches = hs.Status.LastDispatches(hierarchyDir, team);
        var exchanges = lastDispatches is null ? hs.Core.ListExchanges(hierarchyDir) : hs.Core.ListExchanges([hierarchyDir], live: true);
        var ids = exchanges
            .Select(e => e.Member("request")?.Member("path") is { IsText: true } p ? hs.Core.ParseMsgFilename(p.AsString())?.Id : null)
            .Where(static i => i is not null)
            .Select(static i => i!)
            .ToHashSet(StringComparer.Ordinal);
        var peers = ids.Count == 0
            ? []
            : Objects([.. hs.Core.PeerRecordsWhere(new PeerQuery("request_id", null, null, null, ids))]);
        var requests = new List<QueueDispatchFact>();
        var running = new List<string>();
        foreach (var exchange in exchanges)
        {
            if (exchange.Member("request")?.Member("path") is not { IsText: true } path
                || hs.Core.ReadMsgFile(path.AsString()).Member("fm") is not { IsMap: true } frontMatter)
            {
                continue;
            }

            var fm = frontMatter.AsObject();
            if (Text(fm, "from") != HierarchyModule.OrchestratorRole || Text(fm, "type") != "request" || Text(fm, "team") != team)
            {
                continue;
            }

            var id = hs.Core.ParseMsgFilename(path.AsString())?.Id;
            // board-derive § C3 r3: an unarmed plain send holds nobody busy
            if (Text(fm, "reason") == HierarchyModule.PlainSendReason && !peers.Any(r => Text(r, "type") == Liveness.PlainSendArmedRow && Text(r, "request_id") == id))
            {
                continue;
            }

            var toAddr = id is null ? null : peers.FirstOrDefault(r => Text(r, "request_id") == id) is { } row ? Text(row, "to_addr") : null;
            var open = exchange.Member("open") is { IsTruthy: true };
            requests.Add(new QueueDispatchFact(Text(fm, "to_name"), toAddr, Text(fm, "created"), open));
            if (open && id is not null)
            {
                running.Add(id);
            }
        }

        var memberFacts = QueueMemberFacts(described, rows, onJob, teamRecord.IsMap ? teamRecord.AsObject() : null);
        var (members, dispatches) = QueueFacts(memberFacts, requests);
        if (lastDispatches is { Count: > 0 })
        {
            // the rule reads only each role's newest dispatch, so adding the stored newest per member changes no answer; roles roll up
            // from today's roster, and a member no longer in it is dropped, as the listed facts are
            var roles = memberFacts.ToDictionary(static m => m.Name, static m => m.Role, StringComparer.Ordinal);
            dispatches = [.. dispatches, .. lastDispatches.Where(l => roles.ContainsKey(l.Name)).Select(l => new QueueDispatch(roles[l.Name], l.Ms))];
        }

        return (members, dispatches, running);
    }

    /// <summary>The live slot records read once, for asking about many sessions in one scan; the one reader the daemon, the failsafe and the Stop rule share. <paramref name="sessionsOf"/> says which sessions a slot holder's process runs under.</summary>
    public static IReadOnlyList<SlotRecord> SlotRecords(IFileReader files, IAhEnvironment environment, IProcessTable table, Func<int, IReadOnlyList<string>?> sessionsOf, Action<string>? log = null) =>
        SlotReader.Read(files, SlotsRoot(environment), table, sessionsOf, log);

    /// <summary>The default test pool at a glance, read with the one slot reader; <paramref name="waitLimitMs"/> is how long a waiter may wait before it counts as old.</summary>
    public static BucketStatus BucketStatusOf(IFileReader files, IAhEnvironment environment, IProcessTable table, double nowMs, double waitLimitMs) =>
        SlotReader.Status(files, SlotsRoot(environment), table, nowMs, waitLimitMs);

    /// <summary>The slot a session holds or waits for among <paramref name="records"/>: a waiter wins over a holder.</summary>
    public static SlotState? SlotOf(IReadOnlyList<SlotRecord> records, string session)
    {
        ArgumentNullException.ThrowIfNull(records);
        return SlotReader.StateOf(records, session);
    }

    /// <summary>The directory of the with-slot pools: <c>AH_SLOTS_DIR</c>, else the default under the temporary directory.</summary>
    public static string SlotsRoot(IAhEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return environment.GetVariable("AH_SLOTS_DIR") is { Length: > 0 } custom ? custom : "/private/tmp/ahprobe.slots";
    }

    /// <summary>The member facts the queue rules read, from the described members and the team record's member rows; <paramref name="onJob"/> is asked with the member's session id.</summary>
    public static IReadOnlyList<QueueMemberFact> QueueMemberFacts(IEnumerable<JsonObject> described, IReadOnlyList<JsonObject> rows, Func<string, bool> onJob, JsonObject? team = null)
    {
        ArgumentNullException.ThrowIfNull(described);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(onJob);
        static string? Text(JsonObject o, string key) => o.Get(key) is { IsText: true } v ? v.AsString() : null;
        return [.. described.Select(m =>
        {
            var name = Text(m, "name") ?? string.Empty;
            var session = Text(m, "session_id") ?? string.Empty;
            var stream = rows.FirstOrDefault(r => Text(r, "name") == name) is { } own ? Text(own, "stream") : null;
            return new QueueMemberFact(
                name,
                Text(m, "role") ?? string.Empty,
                m.Get("live") is { IsBool: true } live ? live.AsBool() : null,
                Text(m, "activity"),
                Text(m, "activity_at"),
                rows.FirstOrDefault(r => Text(r, "name") == name)?.Get("dismissed") is { IsTruthy: true },
                session.Length > 0 && onJob(session),
                rows.FirstOrDefault(r => Text(r, "name") == name) is { } row ? Text(row, "spawned_at") : null,
                stream,
                QueueEvents.StreamClosed(team, stream));
        })];
    }

    /// <summary>The members and dispatches the queue rules take, built by the one builder the daemon and the failsafe share.</summary>
    public static (IReadOnlyList<QueueMember> Members, IReadOnlyList<QueueDispatch> Dispatches) QueueFacts(
        IReadOnlyList<QueueMemberFact> members, IReadOnlyList<QueueDispatchFact> dispatches) =>
        QueueEvents.Facts(members, dispatches);
}
