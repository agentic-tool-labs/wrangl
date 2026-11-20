using Ah.Core.Json;
using Ah.Core.Time;
using Ah.Hierarchy;

namespace Ah.Watch;

/// <summary>
/// The checklist alarms for one Orchestrator session: <c>queue-stall</c> (a queued item whose role has a live, idle member and no recent dispatch) and
/// <c>member-waiting</c> (an item that has waited on the Orchestrator). Each is told once per episode: the key holds the item's last change, so a
/// state change opens a new episode.
/// </summary>
internal static class QueueEvents
{
    internal const string StallName = "queue-stall";
    internal const string WaitingName = "member-waiting";
    internal const string UnstaffedName = "queue-stall-unstaffed";
    internal const string IdleCapacityName = "idle-capacity";
    internal const string ReportWaitingName = "report-waiting";
    internal const string StillWaitingName = "work-still-waiting";

    /// <summary>The other way out a staffing alarm about <paramref name="itemKey"/> offers (ah2-209 D4, Amendment 1): only the predecessor is left as <c>&lt;item&gt;</c>.</summary>
    internal static string AfterWayOut(string itemKey) => $"or `ah roster checklist set {itemKey} --after <item>` if it must wait for another";

    /// <summary>How often <c>work-still-waiting</c> lists again what was told and still holds (ah2-180 A3).</summary>
    internal const double LeadRemindMs = 20 * 60_000;

    /// <summary>The key kind of the one copy of a <c>member-waiting</c> item sent to the chief after a second window.</summary>
    private const string WaitingCopyName = "member-waiting-chief";

    /// <summary>The team's <c>duty.deputy</c> names a live member row.</summary>
    internal static bool DeputyLive(OwnedTeam team) =>
        team.Doc.Get("duty")?.Member("deputy") is { IsText: true } deputy && team.Members.Any(m => m.Name == deputy.AsString() && m.Live == true);

    /// <summary>How long a member must have been idle with no open request to count toward <c>idle-capacity</c>.</summary>
    internal const double IdleCapacityIdleMs = 10 * 60_000;

    /// <summary>The least time between two <c>idle-capacity</c> mails to one team; a mail is not repeated at all until its idle members or ready items change.</summary>
    internal const double IdleCapacityRepeatMs = 30 * 60_000;

    /// <summary>What one <c>idle-capacity</c> mail named: its idle members and ready items, so the same pair is told once.</summary>
    internal static string IdleCapacitySignature(WatchEvent ev) =>
        string.Join(',', (ev.Data.Get("members") is { IsArray: true } m ? m.AsArray().Select(static v => v.AsString()) : []).Order(StringComparer.Ordinal))
        + "|" + string.Join(',', (ev.Data.Get("items") is { IsArray: true } i ? i.AsArray().Select(static v => v.AsString()) : []).Order(StringComparer.Ordinal));

    private const int IdleCapacityMembers = 1;

    /// <summary>The key under which the time of a team's last <c>queue-stall-unstaffed</c> is kept.</summary>
    internal static string UnstaffedTeamKey(string session, string? team) => $"{session}|{team ?? "_default"}";

    /// <summary>
    /// The facts the queue rules read, built the same way for the daemon's alarm and the failsafe: a member is idle from the moment
    /// its activity became idle, has an open dispatch when one names it (by name, else by address), and a dispatch is counted for the
    /// role of the member it names.
    /// </summary>
    internal static (IReadOnlyList<QueueMember> Members, IReadOnlyList<QueueDispatch> Dispatches) Facts(
        IReadOnlyList<QueueMemberFact> members, IReadOnlyList<QueueDispatchFact> dispatches)
    {
        var roles = members.ToDictionary(static m => m.Name, static m => m.Role, StringComparer.Ordinal);
        var built = members.Select(m => new QueueMember(
            m.Name,
            m.Role,
            m.Live != false,
            m.Activity == "idle" && m.ActivityAt is { } at && Timestamps.TryParseMs(at, out var idleMs) ? idleMs : null,
            dispatches.Any(d => d.Open && DispatchClock.NameOf(d.ToName, d.ToAddr) == m.Name),
            m.OnJob,
            m.Dismissed,
            m.SpawnedAt is { } spawned && Timestamps.TryParseMs(spawned, out var spawnedMs) ? spawnedMs : null,
            m.Stream,
            m.StreamClosed)).ToList();
        var sent = dispatches
            .Where(d => d.Created is not null && roles.ContainsKey(DispatchClock.NameOf(d.ToName, d.ToAddr)) && Timestamps.TryParseMs(d.Created, out _))
            .Select(d =>
            {
                _ = Timestamps.TryParseMs(d.Created!, out var ms);
                return new QueueDispatch(roles[DispatchClock.NameOf(d.ToName, d.ToAddr)], ms);
            })
            .ToList();
        return (built, sent);
    }

    /// <summary>Whether the team record says <paramref name="stream"/> is no longer open.</summary>
    internal static bool StreamClosed(JsonObject? team, string? stream) =>
        stream is { Length: > 0 }
        && team?.Get("streams") is { IsMap: true } streams
        && streams.AsObject().Get(stream) is { IsMap: true } row
        && row.AsObject().Get("state") is { IsText: true } state
        && state.AsString() != "open";

    internal static IReadOnlyList<WatchEvent> Evaluate(
        string session,
        double now,
        IReadOnlyList<OwnedTeam> owned,
        IReadOnlyList<DispatchCandidate> candidates,
        Func<string?, IReadOnlyList<ChecklistEntry>> checklist,
        Func<MemberView, bool> onJob,
        ISet<string> told,
        out IReadOnlyDictionary<WatchEvent, string> keys,
        double thresholdMs = HierarchyModule.QueueStallMs,
        Func<string?, int?>? budgetOf = null,
        IDictionary<string, double>? unstaffedAt = null,
        Func<string?, IReadOnlyList<ReadyBacklogItem>>? readyBacklog = null,
        IDictionary<string, (double At, string Signature)>? idleCapacityAt = null,
        IDictionary<string, double>? remindAt = null,
        IReadOnlyList<QaJob>? qaJobs = null)
    {
        foreach (var stale in unstaffedAt?.Where(e => now - e.Value >= thresholdMs).Select(static e => e.Key).ToList() ?? [])
        {
            _ = unstaffedAt!.Remove(stale);
        }

        var events = new List<WatchEvent>();
        var keyed = new Dictionary<WatchEvent, string>();
        keys = keyed;
        var active = new HashSet<string>(StringComparer.Ordinal);
        foreach (var team in owned)
        {
            var allEntries = checklist(team.Name);
            var teamName = team.Name ?? "_default";
            var entries = HierarchyModule.ChecklistWithoutRunningDispatch(allEntries, candidates.Where(static c => c.Open).Select(static c => c.RequestId));
            QueueMemberFact[] memberFacts = [.. team.Members.Select(m => new QueueMemberFact(m.Name, m.Row.Str("role") ?? string.Empty, m.Live, m.Activity, m.ActivityAt, m.Row.Get("dismissed") is { IsTruthy: true }, onJob(m), m.Row.Str("spawned_at"), m.Row.Str("stream"), StreamClosed(team.Doc, m.Row.Str("stream"))))];
            var (members, dispatches) = Facts(
                memberFacts,
                [.. candidates.Select(c => new QueueDispatchFact(c.ToName, c.ToAddr, c.Created, c.Open))]);
            var teamStart = events.Count;
            // ah2-180 A3: each condition that holds this tick, with the row work-still-waiting names it by, built from the key's own parts
            var holding = new List<(string Key, string Row)>();
            void Hold(string key, string name, string itemKey)
            {
                _ = active.Add(key);
                holding.Add((key, $"{name} {itemKey}"));
            }

            // b456: a paused member is not idle capacity
            var unpaused = members.Where(m => !team.Members.Any(v => v.Paused && v.Name == m.Name)).ToList();
            void IdleCapacity()
            {
                var key = UnstaffedTeamKey(session, team.Name);
                (double At, string Signature)? last = idleCapacityAt is not null && idleCapacityAt.TryGetValue(key, out var stamp) ? stamp : null;
                if (last is { } told && told.Signature.Split('|')[0].Split(',', StringSplitOptions.RemoveEmptyEntries) is var named
                    && IdleMembers(unpaused, now) is var idleNow && named.Any(n => !idleNow.Contains(n)))
                {
                    // a member the last alarm named stopped being idle: that episode is over, so a true alarm later is told again
                    _ = idleCapacityAt!.Remove(key);
                    last = null;
                }

                if (readyBacklog is null
                    || events.Skip(teamStart).Any(static e => e.Name is StallName or UnstaffedName)
                    || (last is { } recent && now - recent.At < IdleCapacityRepeatMs)
                    || IdleCapacityEvent(team.Name, unpaused, now, readyBacklog) is not { } idleEvent
                    || last?.Signature == IdleCapacitySignature(idleEvent))
                {
                    return;
                }

                events.Add(idleEvent);
            }

            // ah2-180 A3: every LeadRemindMs, one mail listing what was told and still holds; the clock starts when the list first has a row.
            // ah2-247: idle-capacity is told once and never listed here; with nothing outstanding (no row open and not waiting on the user,
            // no open request to a member, no member working, no member on a job) nothing is reminded and the clock does not run.
            void StillWaiting()
            {
                if (remindAt is null)
                {
                    return;
                }

                var outstanding = allEntries.Any(static e => e.Item.State is not ("done" or "dropped" or "waiting_user"))
                    || members.Any(static m => m.OpenDispatch || m.BlockedOnJob)
                    || memberFacts.Any(static m => m.Activity == "working");
                List<string> rows = !outstanding ? [] : holding.Where(h => told.Contains(h.Key))
                    .Select(static h => h.Row)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToList();

                var remindKey = UnstaffedTeamKey(session, team.Name);
                if (rows.Count == 0)
                {
                    _ = remindAt.Remove(remindKey);
                    return;
                }

                if (!remindAt.TryGetValue(remindKey, out var since))
                {
                    remindAt[remindKey] = now;
                    return;
                }

                if (now - since < LeadRemindMs)
                {
                    return;
                }

                var list = new JsonArray();
                foreach (var row in rows)
                {
                    list.Add(row);
                }

                events.Add(new WatchEvent(
                    null,
                    "WORK-STILL-WAITING",
                    $"ahd alarm: work-still-waiting — still waiting on you: {string.Join("; ", rows)}",
                    new JsonObject { ["request_id"] = JsonValue.Null, ["rows"] = list })
                { Name = StillWaitingName, Team = team.Name });
            }

            // ah2-334: a QA entry this team queued thresholdMs ago, while the team has no live qa member, is told once per entry while that holds
            if (qaJobs is not null && !members.Any(static m => m.Role == "qa" && m.Live))
            {
                foreach (var job in qaJobs.Where(j => j.State == "queued" && j.Team == team.Name && now - j.Created >= thresholdMs))
                {
                    var key = $"{session}|{teamName}|{UnstaffedName}|qa:{job.Id}";
                    _ = active.Add(key);
                    if (told.Contains(key))
                    {
                        continue;
                    }

                    var qaEvent = new WatchEvent(
                        null,
                        "QUEUE-STALL-UNSTAFFED",
                        $"ahd alarm: queue-stall-unstaffed — QA entry {job.Id} ({job.Set} at {job.Sha[..Math.Min(7, job.Sha.Length)]} in {job.Repo}) queued {Minutes(now - job.Created)}m, no live qa member\n"
                        + $"Operational call (O2): spawn a QA member: `ah roster spawn-ad-hoc qa --team {teamName} --cwd {job.Repo}`.",
                        new JsonObject { ["request_id"] = JsonValue.Null, ["qa_entry"] = job.Id, ["repo"] = job.Repo, ["sha"] = job.Sha, ["set"] = job.Set })
                    { Name = UnstaffedName, Team = team.Name };
                    events.Add(qaEvent);
                    keyed[qaEvent] = key;
                }
            }

            if (entries.Count == 0)
            {
                IdleCapacity();
                StillWaiting();
                continue;
            }

            foreach (var stalled in HierarchyModule.QueueStalled(entries, members, dispatches, now, thresholdMs))
            {
                var member = stalled.Eligible[0];
                var key = $"{session}|{teamName}|{StallName}|{HierarchyModule.ChecklistKey(stalled.Entry)}|{stalled.Entry.Item.StateSince}";
                Hold(key, StallName, HierarchyModule.ChecklistKey(stalled.Entry));
                if (told.Contains(key))
                {
                    continue;
                }

                var idleFor = members.First(m => m.Name == member).IdleSinceMs is { } since ? Minutes(now - since) : 0;
                var itemKey = HierarchyModule.ChecklistKey(stalled.Entry);
                var stall = new WatchEvent(
                    null,
                    "QUEUE-STALL",
                    $"ahd alarm: queue-stall — dispatch {itemKey} to {member} (queued {Minutes(now - stalled.QueuedSinceMs)}m, {member} idle {idleFor}m)\n"
                    + "Operational call (O3): dispatch it, or mark the item blocked/waiting_user with a note, " + AfterWayOut(itemKey) + ".",
                    new JsonObject { ["request_id"] = JsonValue.Null, ["item"] = itemKey, ["member"] = member })
                { Name = StallName, Team = team.Name };
                events.Add(stall);
                keyed[stall] = key;
            }

            var raisedBefore = unstaffedAt is not null && unstaffedAt.TryGetValue(UnstaffedTeamKey(session, team.Name), out var lastRaised) && now - lastRaised < thresholdMs;
            if (budgetOf is not null && HierarchyModule.QueueUnstaffed(entries, members, dispatches, now, null, thresholdMs).Count > 0)
            {
                var budget = budgetOf(team.Name);
                var emitted = raisedBefore;
                foreach (var unstaffed in HierarchyModule.QueueUnstaffed(entries, members, dispatches, now, budget, thresholdMs))
                {
                    var itemKey = HierarchyModule.ChecklistKey(unstaffed.Entry);
                    var key = $"{session}|{teamName}|{UnstaffedName}|{itemKey}|{unstaffed.Role}|{unstaffed.Entry.Item.StateSince}";
                    Hold(key, UnstaffedName, itemKey);
                    if (told.Contains(key) || emitted)
                    {
                        continue;
                    }

                    emitted = true;
                    var live = HierarchyModule.QueueTeamLive(members);
                    var limit = budget is { } n ? JsonValue.FromNumber(n) : JsonValue.FromString("max");
                    var spawn = $"spawn a {unstaffed.Role} for {itemKey} (spawn-one if the roster has the role, else spawn-ad-hoc)";
                    var unstaffedEvent = new WatchEvent(
                        null,
                        "QUEUE-STALL-UNSTAFFED",
                        $"ahd alarm: queue-stall-unstaffed — {spawn} (queued {Minutes(now - unstaffed.QueuedSinceMs)}m, no live {unstaffed.Role}, team live {live} of {(budget is { } b ? b.ToString(System.Globalization.CultureInfo.InvariantCulture) : "max")})\n"
                        + $"Operational call (O2): {spawn}, or mark the item blocked/waiting_user with a note, {AfterWayOut(itemKey)}. (cadence step 3 applies: one new stream per tick)",
                        new JsonObject { ["request_id"] = JsonValue.Null, ["item"] = itemKey, ["role"] = unstaffed.Role, ["live"] = live, ["budget"] = limit })
                    { Name = UnstaffedName, Team = team.Name };
                    events.Add(unstaffedEvent);
                    keyed[unstaffedEvent] = key;
                }
            }

            foreach (var waiting in HierarchyModule.ChecklistWaitingOnOrchestrator(entries, now, thresholdMs))
            {
                var item = waiting.Item;
                var ownerName = item.Holder;
                // A member's report came by mail (report-landed until it reaches the lead), so its "answer <member>:" todo is not news. "answer user:" came by no report.
                if (item.Todo is { } answering && answering.StartsWith("answer ", StringComparison.Ordinal) && !answering.StartsWith("answer user:", StringComparison.Ordinal))
                {
                    continue;
                }

                // A brief for this item (or its parent) sent to the holder after the item began waiting is the Orchestrator acting on it, though the row was not moved.
                string[] briefedAs = [item.Item, $"{waiting.Stream}/{item.Item}", .. item.Parent is { Length: > 0 } parent ? new[] { parent, $"{waiting.Stream}/{parent}" } : []];
                if (ownerName is not null && candidates.Any(c => DispatchClock.NameOf(c.ToName, c.ToAddr) == ownerName && c.Item is { } briefed && briefedAs.Contains(briefed)
                    && c.Created is { } sent && Timestamps.TryParseMs(sent, out var sentMs) && sentMs > HierarchyModule.ChecklistStateSinceMs(item)))
                {
                    continue;
                }

                var noOwnerNeeded = item.Todo is { } todo && (todo.StartsWith("answer user:", StringComparison.Ordinal) || todo.StartsWith("closed:", StringComparison.Ordinal));
                if (!noOwnerNeeded)
                {
                    var owner = team.Members.FirstOrDefault(m => m.Name == ownerName);
                    if (owner is null || owner.Live == false || owner.Activity != "idle" || owner.Paused)
                    {
                        continue;
                    }
                }

                var itemKey = HierarchyModule.ChecklistKey(waiting);
                var waitedMs = now - HierarchyModule.ChecklistStateSinceMs(item);
                if (waitedMs >= 2 * thresholdMs && team.Name is { } dutyTeam && DeputyLive(team))
                {
                    // The deputy acked it and it still waits a second window: one copy to the chief, as for a stuck member.
                    var copyKey = $"{session}|{teamName}|{WaitingCopyName}|{itemKey}|{item.StateSince}";
                    _ = active.Add(copyKey);
                    if (!told.Contains(copyKey))
                    {
                        var copy = new WatchEvent(
                            null,
                            "MEMBER-WAITING",
                            $"ahd alarm: member-waiting — the deputy has left {ownerName ?? "the owner"} waiting {Minutes(waitedMs)}m: {item.Todo ?? "answer"} ({itemKey})",
                            new JsonObject { ["request_id"] = JsonValue.Null, ["item"] = itemKey, ["member"] = ownerName ?? "the owner", ["copy"] = true })
                        { Name = WaitingName, Team = dutyTeam, To = HierarchyModule.ChiefAddress(dutyTeam) };
                        events.Add(copy);
                        keyed[copy] = copyKey;
                    }
                }

                var key = $"{session}|{teamName}|{WaitingName}|{itemKey}|{item.StateSince}";
                Hold(key, WaitingName, itemKey);
                if (told.Contains(key))
                {
                    continue;
                }

                var request = item.Refs is { Count: > 0 } refs ? refs[^1] : null;
                var who = ownerName ?? "the owner";
                var waitingEvent = new WatchEvent(
                    null,
                    "MEMBER-WAITING",
                    $"ahd alarm: member-waiting — {who} has waited {Minutes(now - HierarchyModule.ChecklistStateSinceMs(item))}m on you: {item.Todo ?? "answer"} ({itemKey}, {request ?? "no request"})",
                    new JsonObject { ["request_id"] = request ?? $"item:{itemKey}", ["item"] = itemKey, ["member"] = who, ["due_slot"] = HierarchyModule.ChecklistStateSinceMs(item) })
                { Name = WaitingName, Team = team.Name };
                events.Add(waitingEvent);
                keyed[waitingEvent] = key;
            }

            // ah2-180 A1: a row reported QueueStallMs ago waits on the Orchestrator whatever its owner is doing.
            foreach (var reported in entries.Where(e => e.Item.State == "reported" && HierarchyModule.ChecklistStateSinceMs(e.Item) > 0
                && now - HierarchyModule.ChecklistStateSinceMs(e.Item) >= thresholdMs))
            {
                var item = reported.Item;
                var itemKey = HierarchyModule.ChecklistKey(reported);
                var key = $"{session}|{teamName}|{ReportWaitingName}|{itemKey}|{item.StateSince}";
                Hold(key, ReportWaitingName, itemKey);
                if (told.Contains(key))
                {
                    continue;
                }

                var request = item.Refs is { Count: > 0 } refs ? refs[^1] : null;
                var who = item.Holder ?? "the owner";
                var reportEvent = new WatchEvent(
                    null,
                    "REPORT-WAITING",
                    $"ahd alarm: report-waiting — {who} reported {itemKey} {Minutes(now - HierarchyModule.ChecklistStateSinceMs(item))}m ago and it waits on you ({request ?? "no request"})",
                    new JsonObject { ["request_id"] = request ?? $"item:{itemKey}", ["item"] = itemKey, ["member"] = who })
                { Name = ReportWaitingName, Team = team.Name };
                events.Add(reportEvent);
                keyed[reportEvent] = key;
            }

            IdleCapacity();
            StillWaiting();
        }

        var prefix = session + "|";
        foreach (var gone in told.Where(k => k.StartsWith(prefix, StringComparison.Ordinal) && !active.Contains(k)).ToList())
        {
            _ = told.Remove(gone);
        }

        return events;
    }

    /// <summary>
    /// The <c>idle-capacity</c> alarm of one team, or null: at least one live member other than the Orchestrator idle 10+ minutes with no
    /// open request, and at least one ready backlog item. The backlog is read only once enough members are idle.
    /// </summary>
    private static WatchEvent? IdleCapacityEvent(string? team, IReadOnlyList<QueueMember> members, double now, Func<string?, IReadOnlyList<ReadyBacklogItem>> readyBacklog)
    {
        var idle = IdleMembers(members, now);
        if (idle.Count < IdleCapacityMembers)
        {
            return null;
        }

        var ready = readyBacklog(team)
            .Where(static i => i.Wave is "now" or "next")
            .OrderBy(static i => i.Wave == "now" ? 0 : 1)
            .ThenBy(static i => i.Rank ?? int.MaxValue)
            .Take(3)
            .ToList();
        if (ready.Count == 0)
        {
            return null;
        }

        var named = string.Join(", ", idle.Take(6)) + (idle.Count > 6 ? $" +{idle.Count - 6} more" : string.Empty);
        var items = string.Join("; ", ready.Select(static i => $"{i.Item} {i.Title}"));
        var memberList = new JsonArray();
        foreach (var name in idle)
        {
            memberList.Add(name);
        }

        var itemList = new JsonArray();
        foreach (var item in ready)
        {
            itemList.Add(item.Item);
        }

        return new WatchEvent(
            null,
            "IDLE-CAPACITY",
            $"ahd alarm: idle-capacity — {idle.Count} member{(idle.Count == 1 ? string.Empty : "s")} idle 10+ min with no open request: {named}. Ready backlog items nobody holds: {items}. "
            + "Run `ah backlog next` and dispatch, or leave them idle if that is deliberate: this is not told again until these members or items change.",
            new JsonObject { ["request_id"] = JsonValue.Null, ["members"] = memberList, ["items"] = itemList })
        { Name = IdleCapacityName, Team = team };
    }

    /// <summary>The live members other than the Orchestrator idle 10+ minutes with no open request, no job and not dismissed: who <c>idle-capacity</c> counts.</summary>
    private static List<string> IdleMembers(IReadOnlyList<QueueMember> members, double now) =>
        [.. members
            .Where(m => m.Live && m.Role != "orchestrator" && !m.OpenDispatch && !m.BlockedOnJob && !m.Dismissed && m.IdleSinceMs is { } since && now - since >= IdleCapacityIdleMs)
            .Select(static m => m.Name)];

    private static int Minutes(double ms) => (int)Math.Max(0, Math.Round(ms / 60000));

}
