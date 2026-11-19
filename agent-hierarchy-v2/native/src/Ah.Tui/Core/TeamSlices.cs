using System.Globalization;
using Ah.Core.Json;

namespace Ah.Tui.Core;

/// <summary>
/// All teams and the Team screen read from the read model, the slices the web reads, so the counts are the daemon's own: a
/// team's agents up, working, needing you and in flight come from its <c>team-summary</c> slice, its members from its <c>team</c>
/// slice, its open tasks from <c>list:open:team</c> and the <c>exchange</c> slices it names. A lane (a sub-orchestrator's team)
/// is the team whose <c>team</c> slice names its parent in <c>lane.parent_team</c>; it is listed under that parent, whose row
/// sums itself and its lanes. Nothing here decides whether an agent is up: an agent shows the activity its record serves.
/// </summary>
internal static class TeamSlices
{
    /// <summary>The All teams model with lanes: <c>Teams</c> are the top-level rows (a parent's row summed over its lanes), <c>Own</c>
    /// a team's own row by id, and <c>Lanes</c> a parent's lane rows by the parent's id.</summary>
    internal sealed record Tree(AllTeams.Model Model, IReadOnlyDictionary<string, AllTeams.TeamRow> Own, IReadOnlyDictionary<string, IReadOnlyList<AllTeams.TeamRow>> Lanes, int LaneCount);

    /// <summary>The exchange ids the summary serves as stale: open, with an addressee that is gone.</summary>
    internal static HashSet<string> StaleIds(JsonValue? summary) =>
        [.. Items(summary, "stale_ids").Where(static s => s.IsText).Select(static s => s.AsString())];

    /// <summary>Whether the summary says its plan has not been read, so its counts from the plan say nothing yet.</summary>
    internal static bool PlanNotRead(JsonValue summary) => summary.IsMap && summary.Member("plan_loaded") is { IsBool: true } loaded && !loaded.AsBool();

    /// <summary>Whether the team list is held and names no team.</summary>
    internal static bool ListIsEmpty(IReadOnlyDictionary<string, JsonValue> slices) =>
        slices.TryGetValue("list:teams", out var list) && list.IsMap && list.Member("teams") is { IsArray: true } teams && teams.AsArray().Count == 0;

    /// <summary>Whether a listed team's summary says its plan has not been read.</summary>
    internal static bool AnyPlanNotRead(IReadOnlyDictionary<string, JsonValue> slices) =>
        TeamIds(slices).Any(id => slices.TryGetValue("team-summary:" + id["team:".Length..], out var summary) && PlanNotRead(summary));

    /// <summary>The header's count: a zero from a plan not yet read is no count at all.</summary>
    internal static int? NeedsShown(int needs, bool planPending) => planPending && needs == 0 ? null : needs;

    /// <summary>The suffix of a team's plan slices, as its team slice names its plan: a team's folder need not be its name.</summary>
    private static string PlanSuffix(IReadOnlyDictionary<string, JsonValue> slices, string id) =>
        Text(slices.GetValueOrDefault(id), "plan") is { } plan ? plan["plan:".Length..] : id["team:".Length..];

    private static string DirOf(string id) => id["team:".Length..].Split('/')[0];

    /// <summary>Whether a team's summary reads quiet at <paramref name="now"/>: one comparison against the served instant.</summary>
    private static bool Quiet(JsonValue summary, DateTimeOffset now) => now >= When(Text(summary, "quiet_at"));

    /// <summary>Whether a team is another's lane: a confirmed lane or a claim to be one. A team slice not held yet is not one.</summary>
    internal static bool IsLane(IReadOnlyDictionary<string, JsonValue> slices, string id) =>
        slices.GetValueOrDefault(id) is { IsMap: true } team && (Obj(team, "lane") is not null || Obj(team, "lane_claim") is not null);

    /// <summary>The team's on-duty line, <c>Deputy on duty: &lt;name&gt; since &lt;time&gt; · the chief is with the user</c>; null when no deputy is on duty.</summary>
    internal static string? DutyLine(IReadOnlyDictionary<string, JsonValue> slices, string id) => Duty(slices, id)?.Line;

    /// <summary>What the duty line starts with, before the deputy's name.</summary>
    internal const string DutyLead = "Deputy on duty: ";

    /// <summary>The duty line, the deputy's name in it, and the deputy's role (null when the deputy is not a member).</summary>
    internal sealed record DutyText(string Line, string Deputy, string? Role);

    internal static DutyText? Duty(IReadOnlyDictionary<string, JsonValue> slices, string id) =>
        slices.GetValueOrDefault(id) is { IsMap: true } team && Obj(team, "duty") is { } duty && duty.Member("deputy") is { IsText: true } deputy
            ? new DutyText(
                $"{DutyLead}{deputy.AsString()}{(duty.Member("since") is { IsText: true } since ? " since " + since.AsString() : string.Empty)} · the chief is with the user{TallyText(duty)}",
                deputy.AsString(),
                DutyMember(team) is { } member ? RoleOf(slices, team, member) : null)
            : null;

    /// <summary><c> · N handled, M escalated, K waiting on you</c> from the duty's recorded tally; empty before the first one.</summary>
    private static string TallyText(JsonValue duty) =>
        Obj(duty, "tally") is { } t
            ? $" · {Count(t, "handled")} handled, {Count(t, "escalated")} escalated, {Count(t, "waiting")} waiting on you"
            : string.Empty;

    private static string Count(JsonValue v, string key) =>
        v.Member(key) is { IsNumber: true } n ? n.AsNumber().ToString(System.Globalization.CultureInfo.InvariantCulture) : "?";

    /// <summary>Every row key the team's Sitrep serves, with the index of its section; null until it has arrived.</summary>
    internal static Dictionary<string, int>? SitrepSnap(IReadOnlyDictionary<string, JsonValue> slices, string id)
    {
        if (slices.GetValueOrDefault("sitrep:" + id["team:".Length..]) is not { IsMap: true } body)
        {
            return null;
        }

        var snap = new Dictionary<string, int>(StringComparer.Ordinal);
        var regions = SitrepRegions(body);
        for (var i = 0; i < regions.Count; i++)
        {
            foreach (var r in Items(body, regions[i].List))
            {
                snap[Text(r, "key") ?? string.Empty] = i;
            }
        }

        return snap;
    }

    private static List<SitrepRegion> SitrepRegions(JsonValue body) =>
        [.. Items(body, "regions").Select(static r => new SitrepRegion(Text(r, "list") ?? string.Empty, Text(r, "total"), TuiText.Plain(Text(r, "label") ?? string.Empty), Flag(r, "optional"), Flag(r, "folded")))];

    private static bool Flag(JsonValue v, string key) => v.Member(key) is { IsBool: true } b && b.AsBool();

    /// <summary>The Sitrep's model from its served slice, or null until it has arrived. <paramref name="seen"/> is what was on show when the team last closed, to count what left.</summary>
    internal static SitrepModel? Sitrep(IReadOnlyDictionary<string, JsonValue> slices, string id, IReadOnlyDictionary<string, int>? seen, DateTimeOffset now)
    {
        var suffix = id["team:".Length..];
        if (slices.GetValueOrDefault("sitrep:" + suffix) is not { IsMap: true } body)
        {
            return null;
        }

        var snap = SitrepSnap(slices, id)!;
        var counts = new List<string>();
        var rows = new List<SitrepRow>();
        var regions = SitrepRegions(body);
        for (var i = 0; i < regions.Count; i++)
        {
            var section = regions[i];
            var served = Items(body, section.List);
            // A ship or merge record is shown among the completed rows but is no item, and the total counts items.
            var work = served.Count(static r => Text(r, "kind") is not ("ship" or "merge"));
            var total = section.Total is { } t && body.Member(t) is { IsNumber: true } n ? (int)n.AsNumber() : work;
            counts.Add(total > work ? $"{work} of {total}" : work.ToString(CultureInfo.InvariantCulture));
            rows.AddRange(served.Select(r => SitrepRowOf(r, i, id, slices)));
        }

        var summary = slices.GetValueOrDefault("team-summary:" + suffix);
        var updated = Instant(body, "as_of") is { } asOf ? $"updated {AllTeams.Age(now, asOf)} ago" : null;
        return new SitrepModel(TuiText.Plain(NameOf(slices, id)), Num(summary, "needs_you"), Num(summary, "working"), Num(summary, "blocked"), updated, seen?.Keys.Count(k => !snap.ContainsKey(k)) ?? 0, regions, counts, rows, PlanNotRead(summary));
    }

    private static SitrepRow SitrepRowOf(JsonValue r, int section, string id, IReadOnlyDictionary<string, JsonValue> slices)
    {
        var key = Text(r, "key") ?? string.Empty;
        var kind = Text(r, "kind");
        if (kind is "ship" or "merge")
        {
            var sha = Text(r, "sha") is { } s ? s[..Math.Min(7, s.Length)] : string.Empty;
            var words = kind == "ship"
                ? $"Shipped {Text(r, "ref")} · {sha}" + (Text(r, "tag") is null ? string.Empty : " · tag") + (r.Member("confirmed") is { IsBool: true } c && !c.AsBool() ? " · unconfirmed" : string.Empty)
                : $"Merged {Text(r, "ref")} into {Text(r, "into")} · {sha}";
            return new SitrepRow(key, TuiText.Plain(words), section, null);
        }

        var title = TuiText.Plain(Text(r, "title") ?? key);
        if (kind == "decision")
        {
            return new SitrepRow(key, title, section, null);
        }

        if (Text(r, "source") == "backlog")
        {
            return new SitrepRow(key, title, section, $"{BacklogCard}{DirOf(id)}/{key}");
        }

        var stream = Text(r, "stream") ?? string.Empty;
        var own = Text(r, "team") is { } team ? "team:" + DirOf(id) + "/" + (team == "_default" ? "default" : team) : id;
        var item = key.StartsWith(stream + ":", StringComparison.Ordinal) ? key[(stream.Length + 1)..] : key;
        return new SitrepRow(key, title, section, $"{(TeamIds(slices).Contains(own) ? own : id)}|{stream}|{item}");
    }

    private const string BacklogCard = "bl:";

    /// <summary>A work item as its dialog shows it; <paramref name="Gone"/>: its list has arrived without it.</summary>
    internal sealed record ItemDetail(string Title, string Key, string State, string Holder, string Note, string? LogId, bool Gone = false);

    /// <summary>The item a card names, the team its log is read through, and the slices it comes from: a team's stream, or the directory's backlog.</summary>
    private static (JsonValue? Item, string Key, string Team, List<string> Ids, JsonValue? Listed) CardItem(IReadOnlyDictionary<string, JsonValue> slices, string card)
    {
        if (card.StartsWith(BacklogCard, StringComparison.Ordinal))
        {
            var (dir, backlogKey) = (card[BacklogCard.Length..card.IndexOf('/', StringComparison.Ordinal)], card[(card.IndexOf('/', StringComparison.Ordinal) + 1)..]);
            var backlog = slices.GetValueOrDefault("backlog:" + dir);
            return (Items(backlog, "items").Cast<JsonValue?>().FirstOrDefault(i => Text(i, "key") == backlogKey), backlogKey, $"team:{dir}/", ["backlog:" + dir], backlog);
        }

        var parts = card.Split('|', 3);
        var list = slices.GetValueOrDefault($"plan-items:{PlanSuffix(slices, parts[0])}/{parts[1]}");
        return (Items(list, "items").Cast<JsonValue?>().FirstOrDefault(i => Text(i, "key") == parts[2]), parts[2], parts[0], PlanIds(slices, parts[0]), list);
    }

    /// <summary>The slice ids a card's dialog reads: where its item is listed, then its log once the item names one.</summary>
    internal static List<string> DetailIds(IReadOnlyDictionary<string, JsonValue> slices, string card)
    {
        var (item, _, team, ids, _) = CardItem(slices, card);
        if (ItemLogId(team, Text(item, "work_id")) is { } log)
        {
            ids.Add(log);
        }

        return ids;
    }

    /// <summary>The card's item, or null until the list that holds it has arrived.</summary>
    internal static ItemDetail? Detail(IReadOnlyDictionary<string, JsonValue> slices, string card)
    {
        var (item, key, team, _, listed) = CardItem(slices, card);
        if (item is not { } found)
        {
            return listed is { IsMap: true } ? new ItemDetail(TuiText.Plain(key), TuiText.Plain(key), string.Empty, string.Empty, string.Empty, null, true) : null;
        }

        var work = Text(found, "work_id");
        return new ItemDetail(
            TuiText.Plain(Text(found, "title") ?? key),
            TuiText.Plain(work ?? key),
            TuiText.Plain(Text(found, "state") ?? string.Empty),
            TuiText.Plain(Text(found, "holder") is { } holder ? Last(holder) : string.Empty),
            TuiText.Plain(Text(found, "note") ?? string.Empty),
            ItemLogId(team, work));
    }

    /// <summary>Finished today reads as the viewer's clock time, an older one as its age.</summary>
    private static string SitrepDone(DateTimeOffset? at, DateTimeOffset now, TimeZoneInfo zone)
    {
        if (at is not { } done)
        {
            return string.Empty;
        }

        var local = TimeZoneInfo.ConvertTime(done, zone);
        return local.Date == TimeZoneInfo.ConvertTime(now, zone).Date ? local.ToString("HH:mm", CultureInfo.InvariantCulture) : $"{AllTeams.Age(now, done)} ago";
    }

    private static DateTimeOffset? Instant(JsonValue v, string key) =>
        v.Member(key) is { IsNumber: true } n && n.AsNumber() > 0 ? DateTimeOffset.FromUnixTimeMilliseconds((long)n.AsNumber()) : null;

    /// <summary>A team's name as its summary or team slice serves it, else the last part of its id.</summary>
    internal static string NameOf(IReadOnlyDictionary<string, JsonValue> slices, string id) =>
        Text(slices.GetValueOrDefault("team-summary:" + id["team:".Length..]), "name") ?? Text(slices.GetValueOrDefault(id), "name") ?? Last(id);

    internal static List<string> TeamIds(IReadOnlyDictionary<string, JsonValue> slices) =>
        [.. Items(slices.GetValueOrDefault("list:teams"), "teams").Select(static t => Text(t, "id")).OfType<string>()];

    /// <summary>The model, or null until the team list has arrived.</summary>
    internal static Tree? Build(IReadOnlyDictionary<string, JsonValue> slices, DateTimeOffset now)
    {
        if (!slices.ContainsKey("list:teams"))
        {
            return null;
        }

        var own = new Dictionary<string, AllTeams.TeamRow>(StringComparer.Ordinal);
        var needs = new List<AllTeams.NeedRow>();
        var flight = new List<AllTeams.FlightRow>();
        foreach (var id in TeamIds(slices))
        {
            var (row, teamNeeds, teamFlight) = One(slices, id, now);
            own[id] = row;
            needs.AddRange(teamNeeds);
            flight.AddRange(teamFlight);
        }

        var lanes = own.Values.Where(r => r.Parent is { } p && own.ContainsKey(p)).GroupBy(r => r.Parent!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<AllTeams.TeamRow>)[.. g.OrderBy(r => r.Name, StringComparer.Ordinal)], StringComparer.Ordinal);
        var top = new List<AllTeams.TeamRow>();
        foreach (var r in own.Values.Where(r => r.Parent is not { } p || !own.ContainsKey(p)))
        {
            top.Add(lanes.TryGetValue(r.Path!, out var mine) ? Rollup(r, mine) : r);
        }

        var model = new AllTeams.Model(
            [.. top.OrderBy(t => t.Status.Rank).ThenBy(t => t.Name, StringComparer.Ordinal)],
            [.. needs.OrderBy(n => n.At)],
            [.. flight.OrderBy(f => f.At)]);
        return new Tree(model, own, lanes, lanes.Values.Sum(static l => l.Count));
    }

    /// <summary>A parent's row summed over itself and its lanes, with the worst status among them.</summary>
    private static AllTeams.TeamRow Rollup(AllTeams.TeamRow parent, IReadOnlyList<AllTeams.TeamRow> lanes)
    {
        var all = lanes.Prepend(parent).ToList();
        var worst = all.OrderBy(static r => r.Status.Rank).First();
        return parent with
        {
            Status = worst.Status,
            Since = worst.Since,
            Up = all.Sum(static r => r.Up),
            Agents = all.Any(static r => r.Agents < 0) ? -1 : all.Sum(static r => r.Agents),
            InFlight = all.Sum(static r => r.InFlight),
            NeedsYou = all.Sum(static r => r.NeedsYou),
            Unknown = all.Sum(static r => r.Unknown),
            Ended = all.All(static r => r.Ended),
            Quiet = all.All(static r => r.Quiet),
            ItemsDone = all.Sum(static r => r.ItemsDone),
            ItemsTotal = all.Sum(static r => r.ItemsTotal),
            Working = all.Sum(static r => r.Working),
            Blocked = all.Sum(static r => r.Blocked),
        };
    }

    private static (AllTeams.TeamRow Row, List<AllTeams.NeedRow> Needs, List<AllTeams.FlightRow> Flight) One(IReadOnlyDictionary<string, JsonValue> slices, string id, DateTimeOffset now)
    {
        var team = slices.GetValueOrDefault(id);
        var summary = slices.GetValueOrDefault("team-summary:" + id["team:".Length..]);
        var name = NameOf(slices, id);
        var names = AgentNames(slices, team);
        // The served liveness counts give the up count; a daemon that does not serve them leaves the running count of the team's
        // members, with nothing marked unknown.
        var states = Obj(summary, "member_states");
        var counts = Obj(summary, "liveness_counts");
        var served = states is not null && counts is not null;
        var up = served ? Num(counts, "live") : Num(summary, "members");
        var agents = served ? states!.Value.AsObject().Count : team.IsMap ? MemberIds(team).Count : -1;
        var unknown = served ? Num(counts, "unknown") + Num(counts, "not_seen") : 0;
        var needsYou = Num(summary, "needs_you");
        var inFlight = Num(summary, "in_flight");
        var ended = summary.IsMap && summary.Member("ended") is { IsBool: true } e && e.AsBool();

        var needs = new List<AllTeams.NeedRow>();
        // An orchestrator-audience alert (a stuck task) is not the user's: the daemon's needs_you count leaves it out, so the list does too.
        foreach (var a in Items(summary, "alerts").Where(static a => Text(a, "audience") != "orchestrator"))
        {
            // A stuck task is the Orchestrator's to handle: it is never the user's.
            if (Text(a, "audience") == "orchestrator")
            {
                continue;
            }

            var since = When(Text(a, "since"));
            var member = Text(a, "member");
            var who = member is null ? "orchestrator" : names.GetValueOrDefault(member, Last(member));
            // The row already names the member, so it shows the card's detail; a decision has no member, and its title says how many.
            needs.Add(new(StatusLanguage.Get("needsYou"), name, who, Text(a, member is null ? "title" : "detail") ?? "─", AllTeams.Age(now, since), since ?? now, id, member));
        }

        var flight = new List<AllTeams.FlightRow>();
        var staleIds = StaleIds(summary);
        foreach (var o in Items(slices.GetValueOrDefault("list:open:" + id), "open"))
        {
            if (Text(o, "to_member") is not { } to || staleIds.Contains(Text(o, "id") ?? string.Empty))
            {
                continue;
            }

            var x = slices.GetValueOrDefault(Text(o, "id") ?? string.Empty);
            var sent = When(Text(x, "created"));
            var what = AllTeams.Title(Text(x, "title"), Text(x, "slug"));
            flight.Add(new(StatusLanguage.Get("working"), name, names.GetValueOrDefault(to, Last(to)), what, AllTeams.Age(now, sent), sent ?? now, id, to));
        }

        var status = summary.IsMap && summary.Member("held") is { IsBool: true } held && held.AsBool() ? StatusLanguage.Get("held") : StatusLanguage.Of(Text(summary, "state"));
        var oldestNeed = needs.Count == 0 ? (DateTimeOffset?)null : needs.Min(static n => n.At);
        var row = new AllTeams.TeamRow(name, status, status.Key == "needsYou" ? AllTeams.Age(now, oldestNeed) : null, up, agents, inFlight, needsYou, null, id)
        {
            Parent = Text(Obj(team, "lane"), "parent_team") is { } parent ? "team:" + Dir(id) + "/" + parent : null,
            Updated = When(Text(summary, "updated_at")) is { } at ? $"{AllTeams.Age(now, at)} ago" : null,
            Unknown = unknown,
            LeadUnsure = Text(team, "orchestrator") is { } lead && Text(Obj(states, lead), "liveness") is "unknown" or "not_seen",
            Ended = ended,
            Quiet = Quiet(summary, now),
            ItemsDone = Num(summary, "items_done"),
            ItemsTotal = Num(summary, "items_total"),
            SummaryRead = summary.IsMap,
            PlanLoaded = summary.IsMap && (summary.Member("plan_loaded") is not { IsBool: true } loaded || loaded.AsBool()),
            Working = Num(summary, "working"),
            Blocked = Num(summary, "blocked"),
        };
        return (row, needs, flight);
    }

    /// <summary>The team's orchestrator-audience alerts (a stuck task), one line each: who, what, how long. They are not the user's, so no count holds them.</summary>
    internal static IReadOnlyList<string> OrchestratorAlerts(IReadOnlyDictionary<string, JsonValue> slices, string id, DateTimeOffset now)
    {
        var names = AgentNames(slices, slices.GetValueOrDefault(id));
        return [.. Items(slices.GetValueOrDefault("team-summary:" + id["team:".Length..]), "alerts")
            .Where(static a => Text(a, "audience") == "orchestrator")
            .Select(a =>
            {
                var member = Text(a, "member");
                var who = string.IsNullOrEmpty(member) ? "orchestrator" : names.GetValueOrDefault(member, Last(member));
                return $"{who}  {Text(a, "detail") ?? Text(a, "title") ?? "─"}  {AllTeams.Age(now, When(Text(a, "since")))}";
            })];
    }

    /// <summary>The Team screen's model from the slices: the team's own row, its agents in the order the team slice lists them
    /// (the orchestrator first), its open tasks, and its lanes.</summary>
    internal static (AllTeams.TeamDetail Detail, IReadOnlyList<AllTeams.TeamRow> Lanes)? Team(Tree tree, IReadOnlyDictionary<string, JsonValue> slices, string id, DateTimeOffset now)
    {
        if (!tree.Own.TryGetValue(id, out var row))
        {
            return null;
        }

        var team = slices.GetValueOrDefault(id);
        var names = AgentNames(slices, team);
        var states = Obj(slices.GetValueOrDefault("team-summary:" + id["team:".Length..]), "member_states");
        var flight = tree.Model.Flight.Where(f => f.TeamPath == id).ToList();
        var agents = new List<AllTeams.AgentRow>();
        foreach (var m in MemberIds(team))
        {
            var member = slices.GetValueOrDefault(m);
            var activity = slices.GetValueOrDefault(Text(member, "activity") ?? string.Empty);
            // A daemon that serves member states but no state for this member cannot vouch for it either way.
            var (status, since, liveness) = states is null ? AgentState(activity, now)
                : Obj(states, m) is { } state && Text(state, "state") is { } word
                    // An Orchestrator's open turn (ah2-62) is served only while it is live, and says more than its idle or missing activity.
                    ? (StatusLanguage.Of(word is "idle" or "unknown" && state.Member("turn_open") is { IsBool: true } open && open.AsBool() ? "working" : word), When(Text(state, "state_at")) is { } at ? AllTeams.Age(now, at) : null, StatusLanguage.Liveness.GetValueOrDefault(Text(state, "liveness") ?? string.Empty))
                    : (StatusLanguage.Get("unknown"), null, StatusLanguage.Liveness["unknown"]);
            var mine = flight.Where(f => f.AgentId == m).OrderBy(static f => f.At).FirstOrDefault();
            var memberName = Text(member, "name") ?? Last(m);
            var shortName = memberName.StartsWith(row.Name + "-", StringComparison.Ordinal) && memberName.Length > row.Name.Length + 1 ? memberName[(row.Name.Length + 1)..] : memberName;
            var label = names.GetValueOrDefault(m, Last(m));
            if (DutyMember(team) is { } onDuty)
            {
                label = m == onDuty ? label + " · on duty" : m == Text(team, "orchestrator") ? "Chief Orchestrator: with user" : label;
                shortName = m == onDuty ? shortName + " · on duty" : m == Text(team, "orchestrator") ? "chief · with user" : shortName;
            }

            agents.Add(new(m, label, status, since, mine?.What ?? "–", shortName, liveness is "" ? null : liveness)
            {
                Role = RoleOf(slices, team, m),
                Item = Text(Obj(states, m), "item"),
                Live = states is null ? "–" : liveness is "" ? "live" : liveness ?? "–",
                Tool = Text(Obj(states, m), "tool"),
                StateBranch = Text(Obj(states, m), "branch"),
            });
        }

        return (new AllTeams.TeamDetail(row, agents, flight), tree.Lanes.GetValueOrDefault(id) ?? []);
    }

    /// <summary>The slice ids of one team's screen, what is on show first: its record, its open list and sitrep, its members, the exchanges its open list names.</summary>
    internal static List<string> TeamScreenIds(IReadOnlyDictionary<string, JsonValue> slices, string id)
    {
        List<string> ids = [id, "list:open:" + id];
        if (!IsLane(slices, id))
        {
            ids.Add("sitrep:" + id["team:".Length..]);
        }

        ids.AddRange(MemberIds(slices.GetValueOrDefault(id)));
        ids.AddRange(Items(slices.GetValueOrDefault("list:open:" + id), "open").Select(static o => Text(o, "id")).OfType<string>());
        return ids;
    }

    /// <summary>The slice ids a member's work items come from: the team's plan and its streams' items.</summary>
    internal static List<string> PlanIds(IReadOnlyDictionary<string, JsonValue> slices, string id)
    {
        var suffix = PlanSuffix(slices, id);
        List<string> ids = ["plan:" + suffix];
        ids.AddRange(Items(slices.GetValueOrDefault("plan:" + suffix), "streams").Select(static s => Text(s, "stream")).OfType<string>().Select(s => $"plan-items:{suffix}/{s}"));
        return ids;
    }

    /// <summary>One item a member holds, as its agent screen lists it.</summary>
    internal sealed record HeldItem(string Id, string Key, string Title, string State, string Age, string? WorkId = null);

    internal const string ItemLogHead = "item-log:";

    /// <summary>The log slice of a work id, the number after its last <c>-</c> as the web's <c>itemLogId</c> reads it; null when it has no such number.</summary>
    internal static string? ItemLogId(string team, string? workId) =>
        workId?.LastIndexOf('-') is >= 0 and var dash && long.TryParse(workId[(dash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0
            ? $"{ItemLogHead}{DirOf(team)}/{n.ToString(CultureInfo.InvariantCulture)}"
            : null;

    /// <summary>One entry of an item's log, as its agent screen lists it.</summary>
    internal sealed record LogEntry(string When, string Type, string Title, string By);

    /// <summary>An item's log: its entries oldest first, how many older ones the daemon left out, its links and attachments in a line, and its description's lines, plain, with no bold markers or blank lines.</summary>
    internal sealed record ItemLog(IReadOnlyList<LogEntry> Entries, int More, string Facts, IReadOnlyList<string> Description);

    /// <summary>The log an <c>item-log</c> body serves.</summary>
    internal static ItemLog LogOf(JsonValue body, DateTimeOffset now, TimeZoneInfo zone)
    {
        var links = body.Member("links");
        var blockedBy = Items(links, "blocked_by").Where(static i => i.IsText).Select(static i => TuiText.Plain(i.AsString())).ToList();
        var blocks = Items(links, "blocks").Where(static i => i.IsText).Select(static i => TuiText.Plain(i.AsString())).ToList();
        var attachments = Items(body, "attachments").Count;
        string[] facts =
        [
            blockedBy.Count > 0 ? "blocked by " + string.Join(", ", blockedBy) : string.Empty,
            blocks.Count > 0 ? "blocks " + string.Join(", ", blocks) : string.Empty,
            attachments > 0 ? attachments.ToString(CultureInfo.InvariantCulture) + (attachments == 1 ? " attachment" : " attachments") : string.Empty,
        ];
        return new ItemLog(
            [.. Items(body, "entries").Select(e => new LogEntry(SitrepDone(Instant(e, "ts_ms"), now, zone), TuiText.Plain(Text(e, "type")), TuiText.Plain(Text(e, "title")), TuiText.Plain(Text(e, "by"))))],
            Num(body, "more"),
            string.Join(" · ", facts.Where(static f => f.Length > 0)),
            [.. (Text(body, "description") ?? string.Empty).Split('\n').Select(static l => TuiText.Plain(l.Replace("**", string.Empty, StringComparison.Ordinal))).Where(static l => !string.IsNullOrWhiteSpace(l))]);
    }

    /// <summary>
    /// The items a member holds across the team's streams, its name or its id being the holder, as the web's member page filters them; <c>in_progress</c> first, then newest.
    /// Null until the plan and every stream's items have arrived. Backlog items are not in a team's streams, so they are not listed.
    /// </summary>
    internal static List<HeldItem>? HeldItems(IReadOnlyDictionary<string, JsonValue> slices, string id, string member, DateTimeOffset now)
    {
        var suffix = PlanSuffix(slices, id);
        if (slices.GetValueOrDefault("plan:" + suffix) is not { IsMap: true } plan)
        {
            return null;
        }

        var name = Text(slices.GetValueOrDefault(member), "name");
        var held = new List<(JsonValue Item, string Stream, DateTimeOffset? At)>();
        foreach (var stream in Items(plan, "streams").Select(static s => Text(s, "stream")).OfType<string>())
        {
            if (slices.GetValueOrDefault($"plan-items:{suffix}/{stream}") is not { IsMap: true } items)
            {
                return null;
            }

            held.AddRange(Items(items, "items").Where(i => Text(i, "holder") is { } h && (h == member || h == name)).Select(i => (i, stream, ItemAt(i))));
        }

        return [.. held
            .OrderBy(static h => Text(h.Item, "state") != "in_progress")
            .ThenByDescending(static h => h.At ?? DateTimeOffset.MinValue)
            .Select(h => new HeldItem(
                h.Stream + "|" + (Text(h.Item, "key") ?? string.Empty),
                TuiText.Plain(Text(h.Item, "work_id") ?? Text(h.Item, "key") ?? string.Empty),
                TuiText.Plain(Text(h.Item, "title") ?? string.Empty),
                TuiText.Plain(Text(h.Item, "state") ?? string.Empty),
                h.At is { } at ? AllTeams.Age(now, at) : "–",
                Text(h.Item, "work_id")))];
    }

    private static DateTimeOffset? ItemAt(JsonValue item) =>
        When(Text(item, "updated_at")) ?? When(Text(item, "state_since")) ?? Instant(item, "done_seen_at");

    /// <summary>
    /// An agent's status for a daemon that serves no state for the member: the activity its record serves, failed read as
    /// blocked, or unknown with no record. Such a daemon serves no liveness either, so there is no liveness word.
    /// </summary>
    /// <remarks>TODO: delete once the web drops its old-daemon fallbacks too.</remarks>
    private static (StatusLanguage.Status Status, string? Since, string? Liveness) AgentState(JsonValue activity, DateTimeOffset now)
    {
        if (!activity.IsMap || Text(activity, "activity") is not { } word)
        {
            return (StatusLanguage.Get("unknown"), null, null);
        }

        var status = StatusLanguage.Of(word == "failed" ? "blocked" : word);
        var at = activity.Member("state_at") is { IsNumber: true } n && n.AsNumber() > 0 ? DateTimeOffset.FromUnixTimeMilliseconds((long)n.AsNumber()) : When(Text(activity, "turn_at"));
        return (status, at is null ? null : AllTeams.Age(now, at), null);
    }

    /// <summary>Each member id of a team mapped to its screen name: its role, or "role (name)" when the role repeats.</summary>
    private static Dictionary<string, string> AgentNames(IReadOnlyDictionary<string, JsonValue> slices, JsonValue team)
    {
        var ids = MemberIds(team);
        var roleOf = (string id) => RoleOf(slices, team, id);
        var repeated = ids.GroupBy(roleOf, StringComparer.Ordinal).Where(static g => g.Count() > 1).Select(static g => g.Key).ToHashSet(StringComparer.Ordinal);
        return ids.ToDictionary(id => id, id => repeated.Contains(roleOf(id)) ? $"{roleOf(id)} ({Last(id)})" : roleOf(id), StringComparer.Ordinal);
    }

    /// <summary>A member's role: its record's, else the orchestrator's for the team's orchestrator, else the last part of its id.</summary>
    private static string RoleOf(IReadOnlyDictionary<string, JsonValue> slices, JsonValue team, string id) =>
        Text(slices.GetValueOrDefault(id), "role") ?? (id == Text(team, "orchestrator") ? "orchestrator" : Last(id));

    private static List<string> MemberIds(JsonValue? team)
    {
        var ids = new List<string>();
        if (Text(team, "orchestrator") is { } o)
        {
            ids.Add(o);
        }

        // the deputy on duty sits in the lead slot, right under the chief, and is not listed again among the members
        if (DutyMember(team) is { } deputy)
        {
            ids.Add(deputy);
        }

        ids.AddRange(Items(team, "members").Where(static m => m.IsText).Select(static m => m.AsString()).Where(m => !ids.Contains(m)));
        return ids;
    }

    /// <summary>The member slice id of the team's deputy on duty, from its slice's <c>duty</c> block; null off duty.</summary>
    private static string? DutyMember(JsonValue? team) => Text(Obj(team, "duty"), "member");

    private static JsonValue? Obj(JsonValue? v, string key) => v is { IsMap: true } m && m.Member(key) is { IsMap: true } o ? (JsonValue?)o : null;

    private static string Dir(string teamId) => teamId["team:".Length..teamId.LastIndexOf('/')];

    private static string Last(string id) => id[(id.LastIndexOf('/') + 1)..];

    private static List<JsonValue> Items(JsonValue? v, string key) => v is { IsMap: true } m && m.Member(key) is { IsArray: true } a ? [.. a.AsArray()] : [];

    private static string? Text(JsonValue? v, string key) => v is { IsMap: true } m ? m.Member(key)?.NonEmptyText : null;

    private static int Num(JsonValue? v, string key) => v is { IsMap: true } m && m.Member(key) is { IsNumber: true } n ? (int)n.AsNumber() : 0;

    private static DateTimeOffset? When(string? s) =>
        s is not null && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t : null;
}
