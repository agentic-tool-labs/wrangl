using Ah.Core.Json;

namespace Ah.Watch.Model;

/// <summary>The slice ids of the Tree. A team is <c>(dirId, team name)</c>; a member and a worktree add the member's name.</summary>
internal static class TreeIds
{
    /// <summary>The member name of the orchestrator pseudo-member; <c>@</c> cannot occur in a real member name.</summary>
    public const string Orchestrator = "@orchestrator";

    /// <summary>The orchestrator's display name, which a real member may also carry; that member is flagged.</summary>
    public const string ReservedName = "orchestrator";

    public static string Team(string dir, string name) => "team:" + ActivityLane.DirId(dir) + "/" + name;

    public static string Topology(string dir, string name) => "topology:" + ActivityLane.DirId(dir) + "/" + name;

    public static string Member(string dir, string team, string member) => "member:" + ActivityLane.DirId(dir) + "/" + team + "/" + member;

    public static string Worktree(string dir, string team, string member) => "worktree:" + ActivityLane.DirId(dir) + "/" + team + "/" + member;

    public const string TeamList = "list:teams";

    /// <summary>The Flow's column of a role: Lead 0, Advise and design 1, Implement 2, Review 3. A role the table does not know goes to Implement.</summary>
    public static int FlowColumn(string? role) => role switch
    {
        "orchestrator" or "sub-orchestrator" => 0,
        "ultra-advisor" or "architect" or "docs-writer" => 1,
        "reviewer" or "qa" => 3,
        _ => 2,
    };

    /// <summary>The Flow's column of a team member: the Orchestrator's is the reserved role's, anyone else's its row's role.</summary>
    public static int MemberColumn(TeamEntry team, string member) =>
        FlowColumn(member == Orchestrator ? ReservedName : team.Row(member) is { } row ? LaneParts.Text(row, "role") : null);

    /// <summary>The first segment of a <see cref="NodePath"/>.</summary>
    public const string NodePathRoot = "t";

    /// <summary>
    /// The view path of a member by what its slice knows. The tree's own path starts at the process holding the team, which a slice cannot know and
    /// which changes; the view resolves this one to that node when it is asked.
    /// </summary>
    public static string NodePath(string dirId, string team, string member) =>
        "/" + NodePathRoot + "/" + ViewBuilder.Seg(dirId) + "/" + ViewBuilder.Seg(team) + "/m/" + ViewBuilder.Seg(member);

    /// <summary>
    /// The path live mode verifies for <paramref name="member"/> of team document <paramref name="doc"/>, for the tree and the topology alike: the orchestrator
    /// node of a child team, when an open stream names the member as its sub-orchestrator and that child team is held by the member's own session. Null otherwise,
    /// which leaves the member read-only: a child team can be held by another session, and that session's pane must never take this member's keys.
    /// </summary>
    public static string? LivePathOf(string dir, string team, JsonValue doc, string member, Func<string, JsonValue?> teamNamed)
    {
        if (doc.Member("streams") is not { IsMap: true } streams || doc.Member("members") is not { IsArray: true } rows
            || rows.AsArray().Where(r => LaneParts.Text(r, "name") == member).Select(static r => (JsonValue?)r).FirstOrDefault() is not { } row)
        {
            return null;
        }

        var all = streams.AsObject();
        foreach (var stream in all.Keys.Order(StringComparer.Ordinal))
        {
            if (LaneParts.Text(all[stream], "state") == "open" && LaneParts.Text(all[stream], "sub_orchestrator") == member)
            {
                var child = Ah.Hierarchy.HierarchyModule.LaneChildName(team, stream);
                if (teamNamed(child)?.Member("orchestrator") is { IsMap: true } holder && SameSession(row, holder))
                {
                    return NodePath(ActivityLane.DirId(dir), child, Orchestrator);
                }
            }
        }

        return null;
    }

    /// <summary>Whether two identity blocks are one session: by session id when both have one, else by the same pid with the same recorded start.</summary>
    private static bool SameSession(JsonValue a, JsonValue b)
    {
        if (LaneParts.Text(a, "session_id") is { Length: > 0 } sessionA && LaneParts.Text(b, "session_id") is { Length: > 0 } sessionB)
        {
            return sessionA == sessionB;
        }

        return a.Member("pid") is { IsNumber: true } pidA && b.Member("pid") is { IsNumber: true } pidB && pidA.AsNumber() > 1 && pidA.AsNumber() == pidB.AsNumber()
            && LaneParts.Text(a, "pid_start") is { Length: > 0 } startA && startA == LaneParts.Text(b, "pid_start");
    }

    /// <summary>The (dirId, team) of <c>team:</c> and <c>topology:</c> ids.</summary>
    public static (string DirId, string Team)? OfTeam(string id)
    {
        var rest = id[(id.IndexOf(':', StringComparison.Ordinal) + 1)..];
        var at = rest.IndexOf('/', StringComparison.Ordinal);
        return at <= 0 || at == rest.Length - 1 ? null : (rest[..at], rest[(at + 1)..]);
    }

    /// <summary>The (dirId, team, member) of <c>member:</c> and <c>worktree:</c> ids.</summary>
    public static (string DirId, string Team, string Member)? OfMember(string id)
    {
        var rest = id[(id.IndexOf(':', StringComparison.Ordinal) + 1)..].Split('/');
        return rest.Length == 3 && rest.All(static p => p.Length > 0) ? (rest[0], rest[1], rest[2]) : null;
    }
}

/// <summary>The lanes and composers of the Tree, made in one place for the daemon and the tests.</summary>
internal static class TreeModel
{
    public static IReadOnlyList<IModelLane> Lanes(IViewStateReader reader, Func<IReadOnlyList<string>> canonicalDirs, string home, Func<IReadOnlyList<string>>? currentDirs = null, Func<string, string?>? branchOf = null, Func<string, string, JsonObject?>? goalOf = null) =>
    [
        new TeamLane(reader, canonicalDirs, currentDirs, branchOf, goalOf),
        new WorktreeLane(reader, canonicalDirs, currentDirs),
        new DecisionsLane(reader, canonicalDirs, currentDirs),
        new RegistrationLane(reader, home),
        new KnownDirsLane(reader, home),
    ];

    public static ComposerRegistry Composers(Func<long>? nowMs = null, ILaneFacts? lanes = null, Func<long>? processesObservedAtMs = null, Func<string, string, bool>? teamHeld = null, Action<string, string>? workMoved = null, Func<string, string, bool>? planPending = null) =>
        new ComposerRegistry()
            .Register(new TeamComposer(lanes))
            .Register(new TeamSummaryComposer(nowMs, processesObservedAtMs, lanes, workMoved, teamHeld, planPending))
            .Register(new MemberComposer(lanes))
            .Register(new TopologyComposer(lanes))
            .Register(new WorktreeComposer())
            .Register(new TeamListComposer())
            .Register(new TeamExchangeListComposer(TeamExchangeListComposer.ExchangesPrefix, nowMs))
            .Register(new TeamExchangeListComposer(TeamExchangeListComposer.FeedPrefix, nowMs))
            .Register(new OpenExchangeListComposer());
}

/// <summary>One team record from the teams lane, with the parts of its key decoded.</summary>
internal sealed record TeamEntry(string Dir, string Path, string Name, JsonValue Value, long AsOfMs)
{
    public string DirId => ActivityLane.DirId(Dir);

    /// <summary>The orchestrator first, then the member rows in the document's order.</summary>
    public IReadOnlyList<string> MemberNames
    {
        get
        {
            var names = new List<string>();
            if (Value.Member("orchestrator") is { IsMap: true })
            {
                names.Add(TreeIds.Orchestrator);
            }

            if (Value.Member("members") is { IsArray: true } rows)
            {
                names.AddRange(rows.AsArray().Select(static r => LaneParts.Text(r, "name")).Where(static n => n is not null).Select(static n => n!));
            }

            return [.. names.Distinct(StringComparer.Ordinal)];
        }
    }

    public JsonValue? Row(string member)
    {
        if (Value.Member("members") is not { IsArray: true } rows)
        {
            return null;
        }

        foreach (var row in rows.AsArray().Where(r => LaneParts.Text(r, "name") == member))
        {
            return row;
        }

        return null;
    }
}

/// <summary>Reads the lane records the Tree composers share. It performs no reads of truth.</summary>
internal static class TreeData
{
    /// <summary>The teams that exist. The plan of a team with no record at all stays on disk and shows nowhere.</summary>
    public static IReadOnlyList<TeamEntry> Teams(IRecordView records) =>
        records is IPassMemo memo ? memo.GetOrAdd<IReadOnlyList<TeamEntry>>("tree-teams", () => BuildTeams(records)) : BuildTeams(records);

    // Built once per composition pass (the view holds still): every composer's Affected and Compose asks for it, per changed key.
    private static List<TeamEntry> BuildTeams(IRecordView records)
    {
        var all = new List<TeamEntry>();
        foreach (var key in records.Keys(TeamLane.LaneName))
        {
            if (records.Get(TeamLane.LaneName, key) is { IsAbsent: false } record)
            {
                all.Add(Entry(key, record));
            }
        }

        // A team whose record exists but does not read keeps its question on show, as a team with no members.
        foreach (var key in records.Keys(ProgressLane.LaneName))
        {
            var (dir, folder) = ProgressLane.Split(key);
            var name = ProgressLane.TeamNameOf(folder);
            if (records.Get(ProgressLane.LaneName, key) is { IsAbsent: false } plan
                && plan.Value.Member("team_exists") is { IsBool: true }
                && !all.Exists(t => t.Dir == dir && t.Name == name))
            {
                all.Add(new TeamEntry(dir, string.Empty, name, JsonValue.FromObject(new JsonObject()), plan.AsOfMs));
            }
        }

        return all;
    }

    public static TeamEntry Entry(string key, LaneRecord record)
    {
        var (dir, path) = LaneParts.Split(key);
        return new TeamEntry(dir, path, TeamLane.NameOf(dir, path) ?? string.Empty, record.Value, record.AsOfMs);
    }

    public static TeamEntry? FindTeam(IRecordView records, string dirId, string name) =>
        Teams(records).FirstOrDefault(t => t.DirId == dirId && t.Name == name);

    public static LaneRecord? Grants(IRecordView records, string dirId, string team) =>
        records.Keys(WorktreeLane.LaneName)
            .Where(k => LaneParts.Split(k) is var (dir, t) && t == team && ActivityLane.DirId(dir) == dirId)
            .Select(k => records.Get(WorktreeLane.LaneName, k))
            .FirstOrDefault(static r => r is { IsAbsent: false });

    public static IReadOnlyList<string> GrantMembers(LaneRecord? grants) =>
        grants is not null && grants.Value.Member("members") is { IsMap: true } m ? [.. m.AsObject().Keys.Order(StringComparer.Ordinal)] : [];

    /// <summary>The run of the team's directory with the latest decision; the greater run id breaks a tie.</summary>
    public static LaneRecord? Pipeline(IRecordView records, string dirId)
    {
        LaneRecord? best = null;
        string? bestRun = null;
        foreach (var key in records.Keys(DecisionsLane.LaneName))
        {
            var (dir, run) = LaneParts.Split(key);
            if (ActivityLane.DirId(dir) != dirId || records.Get(DecisionsLane.LaneName, key) is not { IsAbsent: false } record)
            {
                continue;
            }

            var at = LaneParts.Text(record.Value, "last_decision_at") ?? string.Empty;
            var bestAt = best is null ? null : LaneParts.Text(best.Value, "last_decision_at") ?? string.Empty;
            if (best is null || string.CompareOrdinal(at, bestAt) > 0 || (at == bestAt && string.CompareOrdinal(run, bestRun) > 0))
            {
                best = record;
                bestRun = run;
            }
        }

        return best;
    }

    /// <summary>Whether a registration stands for <paramref name="member"/> of <paramref name="team"/>.</summary>
    public static bool Registers(JsonValue registration, TeamEntry team, string member)
    {
        if (member == TreeIds.ReservedName)
        {
            return false;
        }

        // index loops, not Any: this runs for every registration record on every member lookup, and a closure and a boxed enumerator each time add up (ah2-52)
        if (registration.Member("team_files") is not { IsArray: true } files || !AnyTextIn(files.AsArray(), [team.Path]))
        {
            return false;
        }

        // A member registers as <name>@<team>; a record written with the bare name is still its own.
        string[] wanted = member == TreeIds.Orchestrator ? [TreeIds.ReservedName + "@" + team.Name] : [member + "@" + team.Name, member];
        return (LaneParts.Text(registration, "address") is { } own && wanted.Contains(own))
            || (registration.Member("addresses") is { IsArray: true } addresses && AnyTextIn(addresses.AsArray(), wanted));
    }

    /// <summary>Whether an item of <paramref name="list"/> is text equal to one of <paramref name="wanted"/>.</summary>
    private static bool AnyTextIn(JsonArray list, ReadOnlySpan<string> wanted)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i] is { IsText: true } text && wanted.Contains(text.AsString()))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A session's activity record for a team: the one in the team's own directory, else its newest elsewhere, since a lead may run in a checkout that does not hold its team file.</summary>
    public static LaneRecord? ActivityOf(IRecordView records, TeamEntry team, string session)
    {
        if (records.Get(ActivityLane.LaneName, ActivityLane.KeyOf(team.Dir, session)) is { IsAbsent: false } own)
        {
            return own;
        }

        LaneRecord? best = null;
        foreach (var key in records.Keys(ActivityLane.LaneName))
        {
            if (ActivityLane.Split(key).Subject == session && records.Get(ActivityLane.LaneName, key) is { IsAbsent: false } record && (best is null || record.AsOfMs > best.AsOfMs))
            {
                best = record;
            }
        }

        return best;
    }

    /// <summary>The registration record that stands for the member: the latest <c>at</c>, the greater session id breaking a tie.</summary>
    public static LaneRecord? RegistrationOf(IRecordView records, TeamEntry team, string member)
    {
        LaneRecord? best = null;
        string? bestSession = null;
        foreach (var key in records.Keys(RegistrationLane.LaneName))
        {
            if (records.Get(RegistrationLane.LaneName, key) is not { IsAbsent: false } record || !Registers(record.Value, team, member))
            {
                continue;
            }

            if (best is null || record.AsOfMs > best.AsOfMs || (record.AsOfMs == best.AsOfMs && string.CompareOrdinal(key, bestSession) > 0))
            {
                best = record;
                bestSession = key;
            }
        }

        return best;
    }

    public static JsonValue Ids(IEnumerable<string> ids)
    {
        var array = new JsonArray();
        foreach (var id in ids)
        {
            array.Add(id);
        }

        return JsonValue.FromArray(array);
    }

    public static IReadOnlyList<string> TeamIdsOfDir(IRecordView records, string dirId) =>
        [.. Teams(records).Where(t => t.DirId == dirId).Select(static t => TreeIds.Team(t.Dir, t.Name))];
}

/// <summary><c>team:&lt;dirId&gt;/&lt;team&gt;</c>: identity, member ids, the pipeline of its directory and its worktree ids.</summary>
internal sealed class TeamComposer(ILaneFacts? lanes = null) : ISliceComposer
{
    public string SlicePrefix => "team";

    public IReadOnlyList<string> Lanes => [TeamLane.LaneName, DecisionsLane.LaneName, WorktreeLane.LaneName, StatusLane.LaneName, ProgressLane.LaneName];

    public IReadOnlyList<string> Affected(LaneKey changed, LaneRecord? previous, LaneRecord? current, IRecordView records)
    {
        if (changed.Lane == StatusLane.LaneName)
        {
            return TreeData.TeamIdsOfDir(records, ActivityLane.DirId(changed.Key));
        }

        var (dir, second) = LaneParts.Split(changed.Key);
        return changed.Lane switch
        {
            TeamLane.LaneName => LaneView.TeamsAround(changed, previous, current, records).Select(t => TreeIds.Team(t.Dir, t.Name)).ToList(),
            WorktreeLane.LaneName => [TreeIds.Team(dir, second)],
            ProgressLane.LaneName => [TreeIds.Team(dir, ProgressLane.TeamNameOf(second))],
            _ => TreeData.TeamIdsOfDir(records, ActivityLane.DirId(dir)),
        };
    }

    public IReadOnlyList<string> AllIds(IRecordView records) => [.. TreeData.Teams(records).Select(static t => TreeIds.Team(t.Dir, t.Name))];

    public SliceChange Compose(string sliceId, IRecordView records)
    {
        if (TreeIds.OfTeam(sliceId) is not var (dirId, name) || TreeData.FindTeam(records, dirId, name) is not { } team)
        {
            return new SliceChange(sliceId, null);
        }

        var grants = TreeData.Grants(records, dirId, name);
        var pipeline = TreeData.Pipeline(records, dirId);
        var pipelineBody = pipeline is null
            ? null
            : new JsonObject
            {
                ["run"] = LaneParts.TextOrNull(pipeline.Value, "run"),
                ["decisions_count"] = LaneParts.NumberOrNull(pipeline.Value, "decisions_count"),
                ["last_decision_at"] = LaneParts.TextOrNull(pipeline.Value, "last_decision_at"),
                ["scope"] = "dir",
            };
        if (pipelineBody is not null && StatusPipeline(records, team) is { } statusPipeline)
        {
            if (statusPipeline.Member("waiting_on_user") is { IsNumber: true } waiting)
            {
                pipelineBody["waiting_on_user"] = waiting;
            }

            if (RoundItems(statusPipeline) is { } items)
            {
                pipelineBody["items"] = items;
            }
        }

        var body = new JsonObject
        {
            ["name"] = name,
            ["display_name"] = LaneParts.TextOrNull(team.Value, "display_name"),
            ["dir_id"] = dirId,
            ["orchestrator"] = team.Value.Member("orchestrator") is { IsMap: true } ? TreeIds.Member(team.Dir, name, TreeIds.Orchestrator) : JsonValue.Null,
            ["members"] = TreeData.Ids(team.MemberNames.Where(static n => n != TreeIds.Orchestrator).Select(n => TreeIds.Member(team.Dir, name, n))),
            ["plan"] = ProgressLane.PlanSliceId(team.Dir, name),
            ["pipeline"] = pipelineBody is null ? JsonValue.Null : JsonValue.FromObject(pipelineBody),
            ["worktrees"] = TreeData.Ids(TreeData.GrantMembers(grants).Select(m => TreeIds.Worktree(team.Dir, name, m))),
        };
        if (Ah.Roster.RosterModule.DutyOf(team.Value) is { } duty)
        {
            // the slot still names the recorded chief; the deputy on duty is named here, by its member slice
            duty["member"] = TreeIds.Member(team.Dir, name, duty["deputy"].AsString());
            body["duty"] = duty;
        }

        LaneView.Apply(body, team, TreeData.Teams(records).Where(t => t.Dir == team.Dir).ToList(), lanes);
        return new SliceChange(sliceId, JsonText.ToUtf8(JsonValue.FromObject(body), 0), Math.Max(team.AsOfMs, pipeline?.AsOfMs ?? 0));
    }

    /// <summary>The team's pipeline as the status document of its directory records it; null when the document holds none for the team.</summary>
    internal static JsonValue? StatusPipeline(IRecordView records, TeamEntry team) =>
        records.Get(StatusLane.LaneName, team.Dir) is { IsAbsent: false } status
        && status.Value.Member("teams") is { IsArray: true } teams
        && teams.AsArray().FirstOrDefault(t => LaneParts.Text(t, "team") == team.Name) is { IsMap: true } row
        && row.Member("pipeline") is { IsMap: true } statusPipeline
            ? (JsonValue?)statusPipeline
            : null;

    /// <summary>
    /// The review rounds of a status pipeline, <c>[{slug, rounds, round_cap}]</c> in the document's order; null when it holds no items.
    /// </summary>
    private static JsonValue? RoundItems(JsonValue statusPipeline)
    {
        if (statusPipeline.Member("items") is not { IsArray: true } rows)
        {
            return null;
        }

        var cap = LaneParts.NumberOrNull(statusPipeline, "round_cap");
        var items = new JsonArray();
        foreach (var item in rows.AsArray().Where(static i => i.IsMap && LaneParts.Text(i, "slug") is not null))
        {
            items.Add(JsonValue.FromObject(new JsonObject
            {
                ["slug"] = LaneParts.Text(item, "slug")!,
                ["rounds"] = LaneParts.NumberOrNull(item, "rounds"),
                ["round_cap"] = cap,
            }));
        }

        if (items.Count == 0)
        {
            return null;
        }

        return JsonValue.FromArray(items);
    }
}

/// <summary><c>member:&lt;dirId&gt;/&lt;team&gt;/&lt;member&gt;</c>: identity from the team record, the activity slice it refers to and the liveness inputs of its registration.</summary>
internal sealed class MemberComposer(ILaneFacts? lanes = null) : ISliceComposer
{
    public string SlicePrefix => "member";

    public const int LastExchanges = 3;

    public IReadOnlyList<string> Lanes => [TeamLane.LaneName, RegistrationLane.LaneName, MessagesLane.LaneName, ProcessesLane.LaneName, ActivityLane.LaneName];

    /// <summary>The context reading a stored activity record states: the model it names and the tokens used.</summary>
    internal static (string? Model, long? Used, string? At) ContextOf(LaneRecord? record)
    {
        if (record is not { IsAbsent: false } || record.Value.Member("context") is not { IsMap: true } context)
        {
            return (null, null, null);
        }

        return (LaneParts.Text(context, "model"), context.Member("used") is { IsNumber: true } used && double.IsFinite(used.AsNumber()) ? (long)used.AsNumber() : null, LaneParts.Text(context, "at"));
    }

    public IReadOnlyList<string> Affected(LaneKey changed, LaneRecord? previous, LaneRecord? current, IRecordView records)
    {
        var ids = new SortedSet<string>(StringComparer.Ordinal);
        if (changed.Lane == ActivityLane.LaneName)
        {
            // Only a new context reading changes a member's body; the tool and turn fields of every other activity write do not.
            if (ContextOf(previous) == ContextOf(current))
            {
                return [];
            }

            foreach (var team in TreeData.Teams(records).Where(t => changed.Key.StartsWith(ActivityLane.KeyOf(t.Dir, string.Empty), StringComparison.Ordinal)))
            {
                ids.UnionWith(team.MemberNames
                    .Where(n => TreeData.RegistrationOf(records, team, n) is { } r && LaneParts.Text(r.Value, "session_id") is { } s && ActivityLane.KeyOf(team.Dir, s) == changed.Key)
                    .Select(n => TreeIds.Member(team.Dir, team.Name, n)));
            }

            return [.. ids];
        }

        if (changed.Lane == ProcessesLane.LaneName)
        {
            // A lead's liveness decides routes_to, and it changes with the process table, not with any file.
            foreach (var team in TreeData.Teams(records).Where(t => t.Dir == changed.Key))
            {
                ids.UnionWith(team.MemberNames.Where(n => LeadRow(team, n)).Select(n => TreeIds.Member(team.Dir, team.Name, n)));
            }

            return [.. ids];
        }

        if (changed.Lane == MessagesLane.LaneName)
        {
            var dirId = ActivityLane.DirId(MessagesLane.Split(changed.Key).Dir);
            foreach (var record in new[] { previous, current }.Where(static r => r is { IsAbsent: false }))
            {
                if (ExchangeFiling.TeamOf(record!) is { } teamName && TreeData.FindTeam(records, dirId, teamName) is { } filed)
                {
                    ids.UnionWith(ExchangeFiling.PartiesOf(record!, filed).Where(filed.MemberNames.Contains).Select(p => TreeIds.Member(filed.Dir, filed.Name, p)));
                }
            }

            return [.. ids];
        }

        if (changed.Lane == TeamLane.LaneName)
        {
            foreach (var team in LaneView.TeamsAround(changed, previous, current, records))
            {
                ids.UnionWith(team.MemberNames.Select(n => TreeIds.Member(team.Dir, team.Name, n)));
            }

            return [.. ids];
        }

        foreach (var team in TreeData.Teams(records))
        {
            foreach (var member in team.MemberNames)
            {
                if (new[] { previous, current }.Any(r => r is { IsAbsent: false } && TreeData.Registers(r.Value, team, member)))
                {
                    ids.Add(TreeIds.Member(team.Dir, team.Name, member));
                }
            }
        }

        return [.. ids];
    }

    private static bool LeadRow(TeamEntry team, string member) =>
        team.Value.Member("members") is { IsArray: true } rows
        && rows.AsArray().Any(r => r.IsMap && LaneParts.Text(r, "name") == member && LaneParts.LeadOf(r) is not null);

    public IReadOnlyList<string> AllIds(IRecordView records) =>
        [.. TreeData.Teams(records).SelectMany(t => t.MemberNames.Select(n => TreeIds.Member(t.Dir, t.Name, n)))];

    /// <summary>When the member last answered an exchange addressed to it; null when it never has.</summary>
    private static JsonValue LastReportAt(IReadOnlyList<(string Id, LaneRecord Record)> exchanges, TeamEntry team, string member)
    {
        var newest = JsonValue.Null;
        var newestMs = 0.0;
        foreach (var (_, record) in exchanges)
        {
            var withdrawn = record.Value.Member("withdrawn") is { IsBool: true } w && w.AsBool();
            if (!withdrawn && ExchangeFiling.Resolve(record, team).To == member && LaneParts.Text(record.Value, "response_created") is { } at && LaneParts.Ms(at) > newestMs)
            {
                newestMs = LaneParts.Ms(at);
                newest = at;
            }
        }

        return newest;
    }

    public SliceChange Compose(string sliceId, IRecordView records)
    {
        if (TreeIds.OfMember(sliceId) is not var (dirId, teamName, member)
            || TreeData.FindTeam(records, dirId, teamName) is not { } team
            || !team.MemberNames.Contains(member))
        {
            return new SliceChange(sliceId, null);
        }

        var row = team.Row(member);
        var registration = TreeData.RegistrationOf(records, team, member);
        var session = registration is null ? null : LaneParts.Text(registration.Value, "session_id");
        var exchanges = ExchangeFiling.OfTeam(records, dirId, teamName);
        var last = exchanges.Where(x => ExchangeFiling.PartiesOf(x.Record, team).Contains(member)).Take(LastExchanges).ToList();
        var body = new JsonObject
        {
            ["id"] = sliceId,
            ["name"] = member == TreeIds.Orchestrator ? TreeIds.ReservedName : member,
            ["role"] = member == TreeIds.Orchestrator ? TreeIds.ReservedName : row is { } r ? LaneParts.TextOrNull(r, "role") : JsonValue.Null,
            ["session"] = session is null ? JsonValue.Null : session,
            ["activity"] = session is null ? JsonValue.Null : "activity:" + dirId + "/" + session,
            ["liveness"] = JsonValue.FromObject(new JsonObject
            {
                ["at"] = registration is null ? JsonValue.Null : LaneParts.TextOrNull(registration.Value, "at"),
                ["pid"] = registration is null ? JsonValue.Null : LaneParts.NumberOrNull(registration.Value, "pid"),
                ["pid_start"] = registration is null ? JsonValue.Null : LaneParts.TextOrNull(registration.Value, "pid_start"),
                ["session_start"] = JsonValue.Null,
            }),
            ["placement"] = JsonValue.FromObject(new JsonObject
            {
                ["transport_id"] = row is { } t ? LaneParts.TextOrNull(t, "transport_id") : JsonValue.Null,
                ["stream"] = row is { } s ? LaneParts.TextOrNull(s, "stream") : JsonValue.Null,
            }),
            ["last_exchanges"] = TreeData.Ids(last.Select(x => MessagesLane.SliceIdOfDirId(dirId, x.Id))),
            ["last_report_at"] = LastReportAt(exchanges, team, member),
            ["node_path"] = TreeIds.NodePath(dirId, teamName, member),
            ["column"] = TreeIds.MemberColumn(team, member),
        };
        var rowModel = row is { } modelRow ? LaneParts.Text(modelRow, "model") : null;
        if (rowModel is not null)
        {
            body["model"] = rowModel;
        }

        var reading = ContextOf(session is null ? null : records.Get(ActivityLane.LaneName, ActivityLane.KeyOf(team.Dir, session)));
        ContextReading.Apply(body, rowModel ?? reading.Model, reading.Used, reading.At);

        if (row is { } leadRow)
        {
            if (LaneParts.LeadOf(leadRow) is { } lead)
            {
                body["lead"] = lead;
            }

            // ponytail: each routed member re-reads the team files (and a team file change recomposes every member of the teams around it), O(members x files); cache the parsed records per recompose pass if a hierarchy grows past a few hundred members.
            if (member != TreeIds.Orchestrator && lanes?.RouteOf(team.Dir, teamName, member) is { } route)
            {
                body["routes_to"] = JsonValue.FromObject(new JsonObject { ["team"] = route.Team, ["name"] = route.Name, ["via"] = route.Via });
            }
        }

        if (member == TreeIds.ReservedName)
        {
            body["conflict"] = "reserved_name";
        }

        return new SliceChange(sliceId, JsonText.ToUtf8(JsonValue.FromObject(body), 0), Math.Max(Math.Max(team.AsOfMs, registration?.AsOfMs ?? 0), last.Select(static x => x.Record.AsOfMs).DefaultIfEmpty(0).Max()));
    }
}

/// <summary><c>topology:&lt;dirId&gt;/&lt;team&gt;</c>: the member and worktree nodes and the orchestrator-to-member and member-to-worktree edges.</summary>
internal sealed class TopologyComposer(ILaneFacts? lanes = null) : ISliceComposer
{
    public string SlicePrefix => "topology";

    public IReadOnlyList<string> Lanes => [TeamLane.LaneName, WorktreeLane.LaneName, MessagesLane.LaneName, ProgressLane.LaneName];

    public IReadOnlyList<string> Affected(LaneKey changed, LaneRecord? previous, LaneRecord? current, IRecordView records)
    {
        if (changed.Lane == MessagesLane.LaneName)
        {
            return [.. SummaryData.TeamsAffected(changed, previous, current, records).Select(static t => "topology:" + t.DirId + "/" + t.Team).Distinct(StringComparer.Ordinal)];
        }

        var (dir, second) = LaneParts.Split(changed.Key);
        if (changed.Lane == ProgressLane.LaneName)
        {
            return [TreeIds.Topology(dir, ProgressLane.TeamNameOf(second))];
        }

        return changed.Lane == TeamLane.LaneName
            ? [.. (TeamLane.NameOf(dir, second) is { } name ? [TreeIds.Topology(dir, name)] : Array.Empty<string>())
                .Concat(LaneView.TeamsAround(changed, previous, current, records).Select(static t => "topology:" + t.DirId + "/" + t.Name))
                .Distinct(StringComparer.Ordinal)]
            : [TreeIds.Topology(dir, second)];
    }

    /// <summary>
    /// Each member's lane leader, for the Flow's bands: a member named as an open stream's sub-orchestrator, or one another member's lead names when that
    /// lead is this team or the confirmed child team of a stream it runs. The orchestrator and the members of no lane have none.
    /// </summary>
    private (JsonObject Bands, JsonObject Teams, JsonObject LivePaths) Bands(TeamEntry team, IRecordView records)
    {
        var served = new JsonObject();
        var sameDir = TreeData.Teams(records).Where(t => t.DirId == team.DirId).ToList();
        LaneView.Apply(served, team, sameDir, lanes);
        var open = (served.Get("streams") is { IsArray: true } streams ? streams.AsArray() : new JsonArray()).Where(static s => LaneParts.Text(s, "state") == "open").ToList();
        string? LeadOf(string member)
        {
            if (team.Row(member) is not { } row || LaneParts.LeadOf(row) is not { } lead || LaneParts.Text(lead, "name") is not { } to || LaneParts.Text(lead, "team") is not { } at)
            {
                return null;
            }

            var ours = at == team.Name || open.Any(s => LaneParts.Text(s, "sub_orchestrator") == to && LaneParts.Text(s, "child_team") == at);
            return ours && to != member && to != TreeIds.Orchestrator && team.MemberNames.Contains(to) ? to : null;
        }

        var leaders = team.MemberNames.Where(n => open.Any(s => LaneParts.Text(s, "sub_orchestrator") == n)).ToHashSet(StringComparer.Ordinal);
        leaders.UnionWith(team.MemberNames.Select(LeadOf).OfType<string>());
        var bands = new JsonObject();
        foreach (var member in team.MemberNames.Where(static n => n != TreeIds.Orchestrator))
        {
            if ((leaders.Contains(member) ? member : LeadOf(member)) is { } leader)
            {
                bands[TreeIds.Member(team.Dir, team.Name, member)] = TreeIds.Member(team.Dir, team.Name, leader);
            }
        }

        // An open lane's people live in its child team: they sit in its leader's band, and say which team they are of.
        var teams = new JsonObject();
        foreach (var stream in open)
        {
            if (LaneParts.Text(stream, "sub_orchestrator") is { } leader && team.MemberNames.Contains(leader)
                && LaneParts.Text(stream, "child_team") is { } childName && sameDir.FirstOrDefault(t => t.Name == childName) is { } child)
            {
                foreach (var member in child.MemberNames.Where(static n => n != TreeIds.Orchestrator))
                {
                    var id = TreeIds.Member(child.Dir, child.Name, member);
                    bands[id] = TreeIds.Member(team.Dir, team.Name, leader);
                    teams[id] = TreeIds.Team(child.Dir, child.Name);
                }
            }
        }

        var livePaths = new JsonObject();
        foreach (var member in team.MemberNames)
        {
            if (TreeIds.LivePathOf(team.Dir, team.Name, team.Value, member, n => sameDir.FirstOrDefault(t => t.Name == n)?.Value) is { } path)
            {
                livePaths[TreeIds.Member(team.Dir, team.Name, member)] = path;
            }
        }

        return (bands, teams, livePaths);
    }

    /// <summary>The team's open exchanges between two of its members: <c>review</c> when an implementor sent it to a reviewer, else <c>in_flight</c>. The stale ones are told apart by the summary's <c>stale_ids</c>.</summary>
    private static JsonArray Briefs(TeamEntry team, string dirId, IRecordView records)
    {
        var briefs = new JsonArray();
        foreach (var (id, record) in ExchangeFiling.OfTeam(records, dirId, team.Name).Where(static x => ExchangeFiling.IsOpen(x.Record)).Take(OpenExchangeListComposer.MaxOpen).Select(static x => (x.Id, x.Record)))
        {
            if (ExchangeFiling.Resolve(record, team) is { To: { } to, From: { } from })
            {
                var review = team.Row(from) is { } f && LaneParts.Text(f, "role") == "implementor" && team.Row(to) is { } t && LaneParts.Text(t, "role") == "reviewer";
                briefs.Add(JsonValue.FromObject(new JsonObject
                {
                    ["id"] = MessagesLane.SliceIdOfDirId(dirId, id),
                    ["from"] = TreeIds.Member(team.Dir, team.Name, from),
                    ["to"] = TreeIds.Member(team.Dir, team.Name, to),
                    ["brief"] = review ? "review" : "in_flight",
                }));
            }
        }

        return briefs;
    }

    /// <summary>
    /// The team's exchanges whose response is filed and whose checklist row is <c>reported</c> with its last ref naming the exchange (the
    /// rule report-waiting uses), each with the brief's two ends; an end that resolves to nobody is null.
    /// </summary>
    private static JsonArray Reports(TeamEntry team, string dirId, IRecordView records, IReadOnlyList<JsonValue> items)
    {
        var reported = items
            .Where(static i => LaneParts.Text(i, "state") == "reported")
            .Select(static i => LaneParts.Text(i, "last_ref"))
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
        var reports = new JsonArray();
        foreach (var (id, record) in ExchangeFiling.OfTeam(records, dirId, team.Name).Where(x => reported.Contains(x.Id) && LaneParts.Text(x.Record.Value, "response_created") is not null))
        {
            var (to, from) = ExchangeFiling.Resolve(record, team);
            reports.Add(JsonValue.FromObject(new JsonObject
            {
                ["id"] = MessagesLane.SliceIdOfDirId(dirId, id),
                ["from_member"] = from is null ? JsonValue.Null : TreeIds.Member(team.Dir, team.Name, from),
                ["to_member"] = to is null ? JsonValue.Null : TreeIds.Member(team.Dir, team.Name, to),
            }));
        }

        return reports;
    }

    public IReadOnlyList<string> AllIds(IRecordView records) => [.. TreeData.Teams(records).Select(static t => TreeIds.Topology(t.Dir, t.Name))];

    public SliceChange Compose(string sliceId, IRecordView records)
    {
        if (TreeIds.OfTeam(sliceId) is not var (dirId, name) || TreeData.FindTeam(records, dirId, name) is not { } team)
        {
            return new SliceChange(sliceId, null);
        }

        var members = team.MemberNames.Select(n => TreeIds.Member(team.Dir, name, n)).ToList();
        var worktrees = TreeData.GrantMembers(TreeData.Grants(records, dirId, name)).Select(m => (Member: m, Id: TreeIds.Worktree(team.Dir, name, m))).ToList();
        var parentChild = new JsonArray();
        foreach (var child in team.MemberNames.Where(static n => n != TreeIds.Orchestrator))
        {
            parentChild.Add(JsonValue.FromObject(new JsonObject { ["from"] = TreeIds.Member(team.Dir, name, TreeIds.Orchestrator), ["to"] = TreeIds.Member(team.Dir, name, child) }));
        }

        var memberWorktree = new JsonArray();
        foreach (var (member, id) in worktrees)
        {
            memberWorktree.Add(JsonValue.FromObject(new JsonObject { ["from"] = TreeIds.Member(team.Dir, name, member), ["to"] = id }));
        }

        var (bands, bandTeams, livePaths) = Bands(team, records);
        var nodes = new JsonObject { ["members"] = TreeData.Ids(members), ["worktrees"] = TreeData.Ids(worktrees.Select(static w => w.Id)), ["bands"] = JsonValue.FromObject(bands), ["band_teams"] = JsonValue.FromObject(bandTeams) };
        if (livePaths.Count > 0)
        {
            nodes["live_paths"] = JsonValue.FromObject(livePaths);
        }

        var plan = SummaryData.PlanRecord(records, dirId, name);
        IReadOnlyList<JsonValue> items = plan?.Value.Member("items") is { IsArray: true } listed ? listed.AsArray() : [];
        var body = new JsonObject
        {
            ["team"] = TreeIds.Team(team.Dir, name),
            ["nodes"] = JsonValue.FromObject(nodes),
            ["edges"] = JsonValue.FromObject(new JsonObject { ["parent_child"] = JsonValue.FromArray(parentChild), ["member_worktree"] = JsonValue.FromArray(memberWorktree), ["brief"] = JsonValue.FromArray(Briefs(team, dirId, records)), ["report"] = JsonValue.FromArray(Reports(team, dirId, records, items)) }),
        };
        return new SliceChange(sliceId, JsonText.ToUtf8(JsonValue.FromObject(body), 0), Math.Max(team.AsOfMs, plan?.AsOfMs ?? 0));
    }
}

/// <summary><c>worktree:&lt;dirId&gt;/&lt;team&gt;/&lt;member&gt;</c>: one member's grant. Path and branch come only from the grant record; the branch is not in it.</summary>
internal sealed class WorktreeComposer : ISliceComposer
{
    public string SlicePrefix => "worktree";

    public IReadOnlyList<string> Lanes => [WorktreeLane.LaneName];

    public IReadOnlyList<string> Affected(LaneKey changed, LaneRecord? previous, LaneRecord? current, IRecordView records)
    {
        var (dir, team) = LaneParts.Split(changed.Key);
        var ids = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var record in new[] { previous, current }.Where(static r => r is { IsAbsent: false }))
        {
            ids.UnionWith(TreeData.GrantMembers(record).Select(m => TreeIds.Worktree(dir, team, m)));
        }

        return [.. ids];
    }

    public IReadOnlyList<string> AllIds(IRecordView records) =>
        [.. records.Keys(WorktreeLane.LaneName).SelectMany(k =>
        {
            var (dir, team) = LaneParts.Split(k);
            return TreeData.GrantMembers(records.Get(WorktreeLane.LaneName, k)).Select(m => TreeIds.Worktree(dir, team, m));
        })];

    public SliceChange Compose(string sliceId, IRecordView records)
    {
        if (TreeIds.OfMember(sliceId) is not var (dirId, team, member) || TreeData.Grants(records, dirId, team) is not { } grants
            || grants.Value.Member("members") is not { IsMap: true } members || members.Member(member) is not { IsMap: true } row)
        {
            return new SliceChange(sliceId, null);
        }

        var body = new JsonObject
        {
            ["id"] = sliceId,
            ["team"] = team,
            ["assigned_member"] = member,
            ["grant_state"] = LaneParts.Text(grants.Value, "mode") ?? "off",
            ["work_root"] = LaneParts.TextOrNull(row, "work_root"),
            ["stream"] = LaneParts.TextOrNull(row, "stream"),
        };
        return new SliceChange(sliceId, JsonText.ToUtf8(JsonValue.FromObject(body), 0), grants.AsOfMs);
    }
}

/// <summary><c>list:teams</c>: the teams of the known directories, sorted by id.</summary>
internal sealed class TeamListComposer : ISliceComposer
{
    public string SlicePrefix => TreeIds.TeamList;

    public IReadOnlyList<string> Lanes => [KnownDirsLane.LaneName, TeamLane.LaneName, ProgressLane.LaneName];

    public IReadOnlyList<string> Affected(LaneKey changed, LaneRecord? previous, LaneRecord? current, IRecordView records) => [TreeIds.TeamList];

    public IReadOnlyList<string> AllIds(IRecordView records) => records.Get(KnownDirsLane.LaneName, KnownDirsLane.Key) is { IsAbsent: false } ? [TreeIds.TeamList] : [];

    public SliceChange Compose(string sliceId, IRecordView records)
    {
        if (sliceId != TreeIds.TeamList || records.Get(KnownDirsLane.LaneName, KnownDirsLane.Key) is not { IsAbsent: false } known)
        {
            return new SliceChange(sliceId, null);
        }

        var dirs = known.Value.Member("dirs") is { IsArray: true } d ? d.AsArray().Where(static x => x.IsText).Select(static x => x.AsString()).ToHashSet(StringComparer.Ordinal) : [];
        var teams = new JsonArray();
        var asOf = 0L;
        foreach (var team in TreeData.Teams(records).Where(t => dirs.Contains(t.Dir)).OrderBy(static t => TreeIds.Team(t.Dir, t.Name), StringComparer.Ordinal))
        {
            teams.Add(JsonValue.FromObject(new JsonObject { ["id"] = TreeIds.Team(team.Dir, team.Name), ["name"] = team.Name, ["display_name"] = LaneParts.TextOrNull(team.Value, "display_name"), ["dir_id"] = team.DirId }));
            asOf = Math.Max(asOf, team.AsOfMs);
        }

        return new SliceChange(sliceId, JsonText.ToUtf8(JsonValue.FromObject(new JsonObject { ["teams"] = JsonValue.FromArray(teams) }), 0), asOf);
    }
}

/// <summary>
/// One member as the summary reads it: the state the client draws, from the member's registration, activity record and the process table, and the
/// time that state began (0 when unknown). That time is a state transition, never the record's own as_of, which moves on every tool call.
/// </summary>
internal sealed record MemberReading(string Member, string State, string Activity, long StateAtMs, Ah.Core.Time.LivenessVerdict Verdict = Ah.Core.Time.LivenessVerdict.Unknown, long LastLifeMs = 0, string? Tool = null, long? ContextPct = null, string? Question = null, string? Pane = null, bool TurnOpen = false, bool Located = false, bool StuckAsUnknown = false);

/// <summary>Reads what the summary slices share: the members of a team and their states, from member rows and never from activity keys.</summary>
internal static class SummaryData
{
    /// <summary>The team's stream and item counts from its plan record: how many streams are open and in all, how many items are done and in all. All zero when the lane holds no record.</summary>
    public static (long StreamsOpen, long StreamsTotal, long ItemsDone, long ItemsTotal, long AsOfMs) PlanCounts(IRecordView records, string dirId, string team)
    {
        var folder = ProgressLane.FolderOf(team);
        foreach (var key in records.Keys(ProgressLane.LaneName))
        {
            var (dir, t) = ProgressLane.Split(key);
            if (t != folder || ActivityLane.DirId(dir) != dirId || records.Get(ProgressLane.LaneName, key) is not { IsAbsent: false } record)
            {
                continue;
            }

            long open = 0;
            long total = 0;
            long done = 0;
            long items = 0;
            foreach (var stream in record.Value.Member("streams")!.Value.AsArray())
            {
                total++;
                open += stream.Member("state") is { IsText: true } state && state.AsString() == "open" ? 1 : 0;
                if (stream.Member("counts") is { IsMap: true } counts)
                {
                    foreach (var name in ProgressLane.CountStateNames)
                    {
                        var n = counts.Member(name) is { IsNumber: true } v ? (long)v.AsNumber() : 0;
                        items += n;
                        done += name == "done" ? n : 0;
                    }
                }
            }

            return (open, total, done, items, record.AsOfMs);
        }

        return (0, 0, 0, 0, 0);
    }

    /// <summary>Who is on the work of a team, from the same readings as its summary. The exchanges are the team's, open and addressed to a member that is not gone.</summary>
    public static Ah.Hierarchy.ChecklistLiveness Liveness(IReadOnlyList<MemberReading> members, IEnumerable<string> openToRunning, IEnumerable<string> open) =>
        new(members.Where(static m => IsRunning(m) && m.State == "working").Select(static m => m.Member).ToHashSet(StringComparer.Ordinal), openToRunning.ToHashSet(StringComparer.Ordinal))
        {
            Open = open.ToHashSet(StringComparer.Ordinal),
        };

    /// <summary>Who is on the work of the team named <paramref name="name"/>; null for a team the model does not hold.</summary>
    public static Ah.Hierarchy.ChecklistLiveness? LivenessOf(IRecordView records, string dirId, string name, long nowMs, long? observedAtMs)
    {
        if (TreeData.FindTeam(records, dirId, name) is not { } team)
        {
            return null;
        }

        var members = Members(records, team, nowMs, observedAtMs);
        var live = members.Where(IsRunning).Select(static m => m.Member).ToHashSet(StringComparer.Ordinal);
        var open = ExchangeFiling.OfTeam(records, dirId, name).Where(static x => ExchangeFiling.IsOpen(x.Record)).ToList();
        var toLive = open.Select(x => (x.Id, x.Record, To: ExchangeFiling.Resolve(x.Record, team).To)).Where(x => x.To is not null && live.Contains(x.To)).ToList();
        return Liveness(members, toLive.Select(static x => x.Id), open.Select(static x => x.Id)) with
        {
            OnExchange = toLive.GroupBy(static x => x.To!, StringComparer.Ordinal)
                .ToDictionary(static g => g.Key, static g => Ah.Hierarchy.HierarchyModule.ChecklistTitle(LaneParts.Text(g.First().Record.Value, "title") ?? LaneParts.Text(g.First().Record.Value, "slug") ?? g.First().Id), StringComparer.Ordinal),
        };
    }

    /// <summary>The open parked decisions of a team's run, as the decisions lane holds them; none when the team has no run or the lane holds no record of it.</summary>
    public static IReadOnlyList<JsonValue> OpenDecisions(IRecordView records, TeamEntry team)
    {
        if (TeamComposer.StatusPipeline(records, team) is not { } pipeline || LaneParts.Text(pipeline, "anchor_id") is not { } run
            || records.Get(DecisionsLane.LaneName, DecisionsLane.KeyOf(team.Dir, run)) is not { IsAbsent: false } record
            || record.Value.Member("open") is not { IsArray: true } open)
        {
            return [];
        }

        return open.AsArray();
    }

    /// <summary>The open questions parked for the user by a team (<c>q&lt;k&gt;</c>), as the parked lane holds them for the team's directory; none when it holds no record.</summary>
    public static IReadOnlyList<JsonValue> ParkedOpen(IRecordView records, TeamEntry team) =>
        records.Get(ParkedLane.LaneName, team.Dir) is { IsAbsent: false } record && record.Value.Member("open") is { IsArray: true } open
            // a question that names no team is the default team's
            ? [.. open.AsArray().Where(q => (LaneParts.Text(q, "team") is { Length: > 0 } named) ? named == team.Name : team.Name == TeamLane.DefaultName)]
            : [];

    /// <summary>The items of a team's plan record that wait on the user or are under way, as the progress lane holds them.</summary>
    public static IReadOnlyList<JsonValue> WorkItems(IRecordView records, string dirId, string name) =>
        PlanRecord(records, dirId, name) is { } record && record.Value.Member("work_items") is { IsArray: true } items ? items.AsArray() : [];

    /// <summary>The team's plan record in the progress lane, or null when the lane holds none.</summary>
    public static LaneRecord? PlanRecord(IRecordView records, string dirId, string name)
    {
        var folder = ProgressLane.FolderOf(name);
        foreach (var key in records.Keys(ProgressLane.LaneName))
        {
            var (dir, t) = ProgressLane.Split(key);
            if (t == folder && ActivityLane.DirId(dir) == dirId && records.Get(ProgressLane.LaneName, key) is { IsAbsent: false } record)
            {
                return record;
            }
        }

        return null;
    }

    /// <summary>
    /// What a team's plan record says is waiting and moving (spec ah-ui-focus §3): how many items are <c>waiting_you</c> and <c>working</c>, and the rows of what waits on the user,
    /// a decision and then an item, newest first. The count is the rows, whatever the status document says (spec ah-ui-answer §2).
    /// </summary>
    public static (long Waiting, long Working, long Stuck, long Unfinished, IReadOnlyList<JsonObject> Rows) PlanWork(IRecordView records, string dirId, TeamEntry team, Ah.Hierarchy.ChecklistLiveness live, long nowMs)
    {
        var waiting = Waiting(records, dirId, team);
        var rows = new List<JsonObject>();
        foreach (var d in waiting.Decisions)
        {
            var row = new JsonObject
            {
                ["kind"] = "decision",
                ["key"] = LaneParts.TextOrNull(d, "id"),
                ["title"] = LaneParts.TextOrNull(d, "question"),
                ["team"] = team.Name,
                ["stream"] = JsonValue.Null,
                ["since"] = LaneParts.TextOrNull(d, "time"),
                ["note"] = JsonValue.Null,
                ["class"] = Ah.Hierarchy.HierarchyModule.ChecklistClassOf(LaneParts.Text(d, "class")),
            };
            // only a parked question (q<k>) says where it came from, so a run's decision row keeps its bytes
            if (LaneParts.Text(d, "source") == ParkedLane.Source)
            {
                row["source"] = ParkedLane.Source;
            }

            rows.Add(row);
        }

        foreach (var item in waiting.Items)
        {
            rows.Add(new JsonObject
            {
                ["kind"] = "item",
                ["key"] = LaneParts.TextOrNull(item, "key"),
                ["title"] = LaneParts.Text(item, "title") is { } title ? Ah.Hierarchy.HierarchyModule.ChecklistTitle(title) : JsonValue.Null,
                ["team"] = team.Name,
                ["stream"] = LaneParts.TextOrNull(item, "stream"),
                ["since"] = LaneParts.TextOrNull(item, "since"),
                ["note"] = LaneParts.TextOrNull(item, "note"),
                ["class"] = Ah.Hierarchy.HierarchyModule.ChecklistClassOf(LaneParts.Text(item, "class")),
            });
        }

        var items = WorkItems(records, dirId, team.Name).ToList();
        var works = items
            .Select(item => Ah.Hierarchy.HierarchyModule.ChecklistWork(LaneParts.Text(item, "state") ?? string.Empty, LaneParts.Text(item, "owner"), [.. item.Member("refs")!.Value.AsArray().Select(static r => r.AsString())], live, LaneParts.Text(item, "last") is { } last ? LaneParts.Ms(last) : null, nowMs))
            .ToList();
        // Stuck is what the Stuck column draws by default: a stale item is in it too, but carries the quiet_at that hides it.
        return (waiting.Count, works.Count(static w => w == "working"), works.Count(static w => w is "blocked" or "no_one"), items.Count(static i => LaneParts.Text(i, "state") is "reported" or "in_review" or "approved" or "waiting_orch" or "blocked"), [.. rows.OrderBy(static r => Ah.Hierarchy.HierarchyModule.ChecklistClassRank(r["class"].AsString())).ThenByDescending(static r => r["since"].IsText ? r["since"].AsString() : string.Empty, StringComparer.Ordinal).ThenBy(static r => r["key"].IsText ? r["key"].AsString() : string.Empty, StringComparer.Ordinal)]);
    }

    /// <summary>How long a team stays live with nothing new from its summary or its plan.</summary>
    public const long StaleAfterMs = 24 * 3_600_000;

    /// <summary>
    /// What the front ends read off a summary's own fields. <c>ended</c>: it has members and none runs (<c>state</c> ranks needs_you above ended, so a team that
    /// stopped with a decision outstanding reads needs_you and has ended too). <c>status</c>: ended with nobody in it and no open stream, stale with nobody in it, else live; a
    /// live team reads stale once the client's clock passes <c>stale_at</c>. Run again wherever <c>state</c> is, since the liveness projection can move both.
    /// </summary>
    public static void ApplyStatus(JsonObject body)
    {
        var running = body.Get("running") is { IsBool: true } r && r.AsBool();
        var known = body.Get("member_states") is { IsMap: true } states && states.AsObject().Keys.Count > 0;
        var state = body.Get("state") is { IsText: true } s ? s.AsString() : string.Empty;
        var members = body.Get("members") is { IsNumber: true } m ? m.AsNumber() : 0;
        var open = body.Get("streams_open") is { IsNumber: true } o ? o.AsNumber() : 0;
        body["ended"] = state == "ended" || (state == "needs_you" && !running && known);
        body["status"] = members > 0 ? "live" : open > 0 ? "stale" : "ended";
        body["quiet_at"] = QuietAt(body);
        ApplyClosed(body);
    }

    /// <summary>
    /// The close marker (ah-ui-focus §6), served as <c>closed_mark</c>: while no member has a live verdict the team reads closed, ended, not running and with
    /// nothing waiting. A member with a live verdict makes the team read as usual, so a spawn into a closed team shows by itself. The Orchestrator is no member
    /// here: it may be leading another team by now, and a close never ends it. The record itself is not touched.
    /// </summary>
    private static void ApplyClosed(JsonObject body)
    {
        _ = body.Remove("closed_at");
        if (body.Get("closed_mark") is not { IsText: true } mark
            || (body.Get("member_states") is { IsMap: true } states
                && states.AsObject().Keys.Any(k => !k.EndsWith("/" + TreeIds.Orchestrator, StringComparison.Ordinal) && LaneParts.Text(states.AsObject()[k], "liveness") == "live")))
        {
            return;
        }

        body["closed_at"] = mark;
        body["quiet_at"] = mark;
        body["state"] = "ended";
        body["running"] = false;
        body["ended"] = true;
        body["status"] = "ended";
        body["needs_you"] = JsonValue.FromNumber(0);
        body["waiting_on_you"] = JsonValue.FromNumber(0);
        body["waiting_blockers"] = JsonValue.FromNumber(0);
        body["waiting_rows"] = new JsonArray();
        _ = body.Remove("waiting_truncated");
        _ = body.Remove("queue_waiting");
        // no alert either: each is a card under this team's name, and a live Orchestrator's own may be about the team it leads now
        body["alerts"] = new JsonArray();
        _ = body.Remove("alerts_truncated");
    }

    /// <summary>
    /// When a team goes quiet (spec ah-ui-focus §5), the one instant every client compares its clock with: never while something waits on you, while an item is being worked
    /// by a member that is still seen (ah2-31: an item whose every member is no longer seen stops counting, so its team goes quiet at <c>stale_at</c>),
    /// or while a member of a team not held is working or proven live. A held team or one with no member left then goes quiet at once, any other at <c>stale_at</c>. An Orchestrator
    /// does not keep a team on show, since it may be leading another.
    /// </summary>
    private static JsonValue QuietAt(JsonObject body)
    {
        double Count(string key) => body.Get(key) is { IsNumber: true } n ? n.AsNumber() : 0;
        bool Flag(string key) => body.Get(key) is { IsBool: true } b && b.AsBool();
        var held = Flag("held");
        var liveMember = body.Get("member_states") is { IsMap: true } states
            && states.AsObject().Keys.Any(k => !k.EndsWith("/" + TreeIds.Orchestrator, StringComparison.Ordinal) && LaneParts.Text(states.AsObject()[k], "liveness") == "live");
        return Count("waiting_on_you") > 0 || Count("in_progress_now") > 0 || Count("unfinished") > 0 || (!held && (Count("working") > 0 || liveMember))
            ? JsonValue.Null
            : body.Get(held || Flag("ended") || Count("members") == 0 ? "updated_at" : "stale_at") ?? JsonValue.Null;
    }

    /// <summary>A member's process line as both front ends draw it: from its liveness word, else from its state.</summary>
    public static string ProcessLine(string? liveness, string? state) => liveness switch
    {
        "live" => "running",
        "proven_dead" => "not running",
        "unknown" => "unknown",
        "not_seen" when state != "gone" => "not seen",
        _ => state == "gone" ? "not running" : "running",
    };

    /// <summary>An alert's card as both front ends draw it: its title and the line under it. <paramref name="member"/> is the member's slice id, or empty.</summary>
    public static (string Title, string Detail) AlertCard(string kind, string? member, string text)
    {
        var who = string.IsNullOrEmpty(member) ? string.Empty : member[(member.LastIndexOf('/') + 1)..];
        return kind switch
        {
            "needs_you" => (text, "Waiting on you"),
            "stuck" => ((who.Length > 0 ? who + ": " : string.Empty) + "task stuck", text),
            "error" => (who + " failed", text),
            "alarm" => (text, "Alarm"),
            _ => (who + " is blocked", text),
        };
    }

    /// <summary>
    /// The member's state: gone when its pid is known not to be live, idle when the activity is idle or there is no activity record (ah2-242 C1;
    /// No signal is decided at serve time by <see cref="LivenessProjection"/>), blocked when it is blocked or failed, else working.
    /// </summary>
    public static IReadOnlyList<MemberReading> Members(IRecordView records, TeamEntry team, long nowMs, long? observedAtMs = null, bool observed = true)
    {
        var processLane = observed && records.Get(ProcessesLane.LaneName, team.Dir) is { IsAbsent: false } p ? p.Value : (JsonValue?)null;
        var referenceSeen = processLane is not null && ReferenceSeen(records);
        var peers = records.Get(PeersLane.LaneName, team.Dir) is { IsAbsent: false } peerSlice ? peerSlice.Value : (JsonValue?)null;
        var readings = new List<MemberReading>();
        foreach (var member in team.MemberNames)
        {
            var registration = TreeData.RegistrationOf(records, team, member);
            var session = registration is null ? null : LaneParts.Text(registration.Value, "session_id");
            var record = session is null ? null : TreeData.ActivityOf(records, team, session);
            var question = record is { IsAbsent: false } && record.Value.Member("question") is { IsText: true } asked ? asked.AsString() : null;
            var activity = record is { IsAbsent: false } ? LaneParts.Text(record.Value, "activity") ?? LaneParts.Text(record.Value, "status") ?? string.Empty : string.Empty;
            var atMs = record is { IsAbsent: false } ? StateAt(record.Value) : 0;
            var registeredMs = registration is null ? 0 : Math.Max(registration.AsOfMs, LaneParts.Ms(LaneParts.Text(registration.Value, "at")));
            var lastLifeMs = Math.Max(Math.Max(atMs, record is { IsAbsent: false } ? record.AsOfMs : 0), registeredMs);
            var claude = team.Row(member) is not { } memberRow || LaneParts.Text(memberRow, "kind") is null or "" or "claude";
            var (verdict, blind, starting) = VerdictOf(registration?.Value, peers?.Member(member), processLane, lastLifeMs, nowMs, observedAtMs ?? nowMs, referenceSeen, claude, member);
            // ah2-74 B3: a blocked record whose screen the watcher read and found no prompt on (screen_clear_at set) is working.
            // ah2-102 F5: a permission Notification with a prompt mark (a prompt read, or no screen) is blocked; with neither mark it is working
            // only while the watcher's published coverage holds the session (its read is pending), else blocked at once.
            // ah2-242 S1: or while a herdr Claude member is inside the starting window, before the watcher's coverage has caught up with it.
            var screenClear = activity == "blocked" && record is { IsAbsent: false } && LaneParts.Text(record.Value, "screen_clear_at") is { Length: > 0 };
            var permission = activity == "blocked" && question is null && record is { IsAbsent: false } && LaneParts.Text(record.Value, "blocked_by") == "permission";
            var prompted = permission && LaneParts.Text(record!.Value, "screen_prompt_at") is { Length: > 0 };
            var startingRead = starting && claude && team.Row(member) is { } herdrRow && LaneParts.Text(herdrRow, "transport_id") is { Length: > 0 }
                && Ah.Core.Mux.HerdrFlavour.OnHerdr(herdrRow, LaneParts.Text(herdrRow, "transport"));
            var pending = permission && !prompted && !screenClear && (CoverageLane.Covers(records, session!) || startingRead);
            // ah2-242 C1: a member with no activity record is idle; whether anything locates it is decided at serve time (LivenessProjection).
            var state = verdict == Ah.Core.Time.LivenessVerdict.ProvenDead ? "gone" : activity.Length == 0 ? "idle" : activity == "idle" ? "idle" : !prompted && (screenClear || pending) ? "working" : activity is "blocked" or "failed" ? "blocked" : "working";
            // ah2-242 G1: the stuck rule reads such a member as unknown where the ah2-133 D2 rule did (not registered and live), so no stuck answer moves.
            var stuckAsUnknown = state == "idle" && activity.Length == 0 && !(registration is not null && verdict == Ah.Core.Time.LivenessVerdict.Live);
            var context = MemberComposer.ContextOf(record);
            var contextPct = ContextReading.Of((team.Row(member) is { } modelRow ? LaneParts.Text(modelRow, "model") : null) ?? context.Model, context.Used).Pct;
            // The Orchestrator records no activity through its turns, only whether one is open (ah2-62); it counts only while the session is live.
            var turnOpen = member == TreeIds.Orchestrator && verdict == Ah.Core.Time.LivenessVerdict.Live && record is { IsAbsent: false } && record.Value.Member("turn_open") is { IsBool: true } open && open.AsBool();
            // ah2-242 D1 L3-L5: located whatever its last sign of life says: the Orchestrator not proven dead, a blind lane, a starting member.
            var located = (member == TreeIds.Orchestrator && verdict != Ah.Core.Time.LivenessVerdict.ProvenDead) || blind || starting;
            readings.Add(new MemberReading(member, state, activity, atMs, verdict, lastLifeMs, record is { IsAbsent: false } ? LaneParts.Text(record.Value, "tool") : null, contextPct, question, registration is null ? null : LaneParts.Text(registration.Value, "pane_id"), turnOpen, located, stuckAsUnknown));
        }

        return readings;
    }

    /// <summary>
    /// Whether a member counts as running for the team's counts and state: its verdict is not proven dead, so an unknown member counts as
    /// running and a team is never shown ended on silence. The one place that says.
    /// </summary>
    public static bool IsRunning(MemberReading member) => member.Verdict != Ah.Core.Time.LivenessVerdict.ProvenDead;

    /// <summary>The wire word for a verdict.</summary>
    public static string LivenessWord(Ah.Core.Time.LivenessVerdict verdict) => verdict switch
    {
        Ah.Core.Time.LivenessVerdict.Live => "live",
        Ah.Core.Time.LivenessVerdict.ProvenDead => "proven_dead",
        _ => "unknown",
    };

    /// <summary>
    /// The team's one-word state, first match winning: needs_you when anything waits on the user (a decision, a blocked member, a stuck task), ended when
    /// the team has members and none is running, working when a running member is, else idle (a team with no members is idle).
    /// </summary>
    public static string TeamState(bool hasMembers, bool running, long needsYou, long working) =>
        needsYou > 0 ? "needs_you" : hasMembers && !running ? "ended" : working > 0 ? "working" : "idle";

    /// <summary>When the member's activity last changed: the transition time the timeline recorder stamped on the record, else the turn's start.</summary>
    private static long StateAt(JsonValue record) =>
        record.Member("state_at") is { IsNumber: true } at && at.AsNumber() > 0 ? (long)at.AsNumber() : LaneParts.Ms(LaneParts.Text(record, "turn_at"));

    /// <summary>
    /// The member's verdict from its registration and the processes lane's reading of the directory. A pid the lane did not list, or a
    /// lane with no observation time, gives no observation, so nothing is proven. The lane's heartbeat and pane facts count only while its
    /// reading is within the observation bound.
    /// </summary>
    private static (Ah.Core.Time.LivenessVerdict Verdict, bool Blind, bool Starting) VerdictOf(JsonValue? registration, JsonValue? peerRow, JsonValue? lane, long lastLifeMs, long nowMs, long observedAtMs, bool referenceSeen, bool claudeHarness, string member)
    {
        var now = DateTimeOffset.FromUnixTimeMilliseconds(nowMs);
        var facts = nowMs - observedAtMs <= Ah.Core.Time.LivenessRules.ObservationAgeSeconds * 1000L ? lane : null;
        (long? Pid, string? Start, bool ClaudeHarness, bool HeartbeatFresh)? registered = registration is { } reg
            ? (PidOf(LaneParts.NumberOrNull(reg, "pid")), LaneParts.Text(reg, "pid_start"), LaneParts.Text(reg, "harness") is null or "claude", HeartbeatFresh(facts, LaneParts.Text(reg, "session_id")))
            : null;

        (Ah.Core.Time.PeerRowKind Kind, long? Pid, string? Start, DateTimeOffset? At, double? SeenAgeSeconds)? row = null;
        if (peerRow is { IsMap: true } peer)
        {
            var at = LaneParts.Text(peer, "at") is { } stamp && DateTimeOffset.TryParse(stamp, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : (DateTimeOffset?)null;
            row = (Ah.Core.Time.LivenessAssembler.PeerKindOf(LaneParts.Text(peer, "status")), PidOf(LaneParts.NumberOrNull(peer, "pid")), LaneParts.Text(peer, "pid_start") is { Length: > 0 } rowStart ? rowStart : null, at, at is { } written ? (now - written).TotalSeconds : null);
        }

        var evidence = Ah.Core.Time.LivenessAssembler.Assemble(
            now,
            registered,
            registration is null ? row : null,
            lastLifeMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(lastLifeMs) : null,
            pid => Observed(lane, pid, observedAtMs, referenceSeen),
            claudeHarness,
            registration?.Member("pid_proof") is not { IsNull: false, IsTruthy: false });

        // Only the lane says a member is starting (the team slice carries no spawned_at), and an old reading of that errs towards unknown, so it is not bounded.
        var listed = Lists(lane?.Member("starting"), member);
        var starting = listed ? 0 : (int?)null;
        return (Ah.Core.Time.LivenessRules.Settle(Ah.Core.Time.LivenessRules.Judge(evidence), LaneParts.Text(facts?.Member("panes") ?? JsonValue.Null, member), starting).Verdict, facts is null, listed);
    }

    private static bool HeartbeatFresh(JsonValue? facts, string? session) => session is not null && Lists(facts?.Member("heartbeats"), session);

    private static bool Lists(JsonValue? list, string text) => list is { IsArray: true } array && array.AsArray().Any(s => s.IsText && s.AsString() == text);

    private static long? PidOf(JsonValue? value) => value is { IsNumber: true } n ? (long)n.AsNumber() : null;

    /// <summary>What the processes lane read of <paramref name="pid"/>: nothing for a pid it did not list or a lane with no observation time.</summary>
    private static (Ah.Core.Time.ProcessObservation Observation, string? StartUtc, Ah.Core.Time.HarnessCmdline Cmdline, DateTimeOffset? ObservedAt, bool CanSeeReference) Observed(JsonValue? lane, long? pid, long observedAtMs, bool referenceSeen)
    {
        JsonValue? entry = null;
        if (pid is { } wanted && lane?.Member("pids") is { IsArray: true } listed)
        {
            foreach (var p in listed.AsArray())
            {
                if (p.Member("pid") is { IsNumber: true } n && n.AsNumber() == wanted)
                {
                    entry = p;
                    break;
                }
            }
        }

        var found = entry is not null;
        var observedStart = found ? LaneParts.Text(entry!.Value, "start") : null;
        return (
            !found ? Ah.Core.Time.ProcessObservation.None : observedStart is null ? Ah.Core.Time.ProcessObservation.Absent : Ah.Core.Time.ProcessObservation.Present,
            observedStart,
            found ? LaneParts.Text(entry!.Value, "cmdline") switch
            {
                "harness" => Ah.Core.Time.HarnessCmdline.Harness,
                "not_harness" => Ah.Core.Time.HarnessCmdline.NotHarness,
                _ => Ah.Core.Time.HarnessCmdline.Unreadable,
            } : Ah.Core.Time.HarnessCmdline.Unreadable,
            lane is not null && observedAtMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(observedAtMs) : null,
            referenceSeen && lane is { } r && r.Member("observer_can_see_reference") is { IsBool: true } seen && seen.AsBool());
    }

    /// <summary>
    /// Whether the observer saw a registered process in the table that is that process: some registration's recorded start equals the start
    /// the processes lane read for its pid. A pid that is merely present, or a recycled one, is not a reference, so an absent pid proves
    /// nothing until one registered process is seen as itself.
    /// </summary>
    private static bool ReferenceSeen(IRecordView records)
    {
        var starts = new Dictionary<double, string>();
        foreach (var key in records.Keys(ProcessesLane.LaneName))
        {
            if (records.Get(ProcessesLane.LaneName, key) is { IsAbsent: false } lane && lane.Value.Member("pids") is { IsArray: true } listed)
            {
                foreach (var entry in listed.AsArray())
                {
                    if (entry.Member("pid") is { IsNumber: true } pid && LaneParts.Text(entry, "start") is { } start)
                    {
                        starts[pid.AsNumber()] = start;
                    }
                }
            }
        }

        foreach (var key in records.Keys(RegistrationLane.LaneName))
        {
            if (records.Get(RegistrationLane.LaneName, key) is { IsAbsent: false } registration
                && registration.Value.Member("pid") is { IsNumber: true } pid && pid.AsNumber() > 1 && starts.TryGetValue(pid.AsNumber(), out var observed)
                && LaneParts.Text(registration.Value, "pid_start") is { Length: > 0 } recorded
                && Ah.Core.Time.ProcessStart.IsSame(Ah.Core.Time.ProcessStart.Compare(observed, static () => null, recorded)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>What waits on the user in a team, by the one fold over the lanes' records; the summary and the <c>questions:</c> slice both read this.</summary>
    public static Ah.Hierarchy.ChecklistWaitingList<JsonValue, JsonValue> Waiting(IRecordView records, string dirId, TeamEntry team) =>
        Ah.Hierarchy.HierarchyModule.ChecklistWaiting(
            // b517: a question parked for the user waits too, with or without a run; one the run already lists by id shows once
            OpenDecisions(records, team).Concat(ParkedOpen(records, team)).DistinctBy(static d => LaneParts.Text(d, "id")),
            static d => LaneParts.Text(d, "item"),
            WorkItems(records, dirId, team.Name),
            static i => LaneParts.Text(i, "state"),
            static i => LaneParts.Text(i, "item"));

    /// <summary>A time in the form the slices carry, or null for no time.</summary>
    public static JsonValue Iso(long ms) => ms <= 0 ? JsonValue.Null : DateTimeOffset.FromUnixTimeMilliseconds(ms).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// The (dirId, team) pairs a changed lane key can touch, for the composers that fold every input of a team. A team that the change
    /// removes is still named, so its slice is composed to nothing.
    /// </summary>
    public static IReadOnlyList<(string DirId, string Team)> TeamsAffected(LaneKey changed, LaneRecord? previous, LaneRecord? current, IRecordView records)
    {
        var all = TreeData.Teams(records);
        var present = new[] { previous, current }.Where(static r => r is { IsAbsent: false }).Select(static r => r!).ToList();
        switch (changed.Lane)
        {
            case TeamLane.LaneName:
            {
                var (dir, path) = LaneParts.Split(changed.Key);
                var names = present.Select(r => TreeData.Entry(changed.Key, r).Name).Append(TeamLane.NameOf(dir, path) ?? string.Empty).Where(static n => n.Length > 0).Distinct(StringComparer.Ordinal);
                return [.. names.Select(n => (ActivityLane.DirId(dir), n))];
            }

            case WorktreeLane.LaneName:
            {
                var (dir, team) = LaneParts.Split(changed.Key);
                return [(ActivityLane.DirId(dir), team)];
            }

            case ProgressLane.LaneName:
            {
                var (dir, folder) = ProgressLane.Split(changed.Key);
                return [(ActivityLane.DirId(dir), ProgressLane.TeamNameOf(folder))];
            }

            case StatusLane.LaneName:
            case ProcessesLane.LaneName:
            case PeersLane.LaneName:
            case ParkedLane.LaneName:
                return [.. all.Where(t => t.Dir == changed.Key).Select(static t => (t.DirId, t.Name))];
            case DecisionsLane.LaneName:
            {
                var dirId = ActivityLane.DirId(LaneParts.Split(changed.Key).First);
                return [.. all.Where(t => t.DirId == dirId).Select(static t => (t.DirId, t.Name))];
            }

            case MessagesLane.LaneName:
            {
                var dirId = ActivityLane.DirId(MessagesLane.Split(changed.Key).Dir);
                return [.. present.Select(static r => ExchangeFiling.TeamOf(r)).Distinct(StringComparer.Ordinal).Select(n => (dirId, n))];
            }

            case ActivityLane.LaneName:
            {
                var (dir, subject) = ActivityLane.Split(changed.Key);
                return records.Get(RegistrationLane.LaneName, subject) is { IsAbsent: false } owner
                    ? [.. all.Where(t => (t.Dir == dir || records.Get(ActivityLane.LaneName, ActivityLane.KeyOf(t.Dir, subject)) is null or { IsAbsent: true }) && t.MemberNames.Any(m => TreeData.Registers(owner.Value, t, m))).Select(static t => (t.DirId, t.Name))]
                    : [];
            }

            default:
                return [.. all.Where(t => present.Any(r => t.MemberNames.Any(m => TreeData.Registers(r.Value, t, m)))).Select(static t => (t.DirId, t.Name))];
        }
    }
}

/// <summary>
/// <c>team-summary:&lt;dirId&gt;/&lt;team&gt;</c>: what a row on All teams needs, counted from the team's member rows, with the team's alerts. Every field is a count,
/// a name or a state-bearing time (a transition or a creation), never an as_of or a tool time, so a tool call that changes no state composes the
/// same bytes and publishes nothing.
/// </summary>
internal sealed class TeamSummaryComposer(Func<long>? nowMs = null, Func<long>? observedAtMs = null, ILaneFacts? lanes = null, Action<string, string>? workMoved = null, Func<string, string, bool>? teamHeld = null, Func<string, string, bool>? planPending = null) : ISliceComposer
{
    /// <summary>What each team's work last read as, so <c>workMoved</c> is told of a change of who is working and never of a tool call that changes nothing.</summary>
    private readonly Dictionary<string, string> _workSeen = new(StringComparer.Ordinal);

    public const int AlertCap = 20;

    public const int WaitingRowCap = 20;

    /// <summary>
    /// The member of a body that holds the fields it would carry were the processes lane's reading no longer fresh. Present only when a member
    /// is proven dead, and never served: the serve-time projection swaps it in once the reading is older than the observation bound.
    /// </summary>
    public const string UnobservedKey = "if_unobserved";

    private readonly Func<long> _now = nowMs ?? (static () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    private readonly Func<long>? _observedAtMs = observedAtMs;

    public string SlicePrefix => "team-summary";

    public IReadOnlyList<string> Lanes =>
        [TeamLane.LaneName, DecisionsLane.LaneName, WorktreeLane.LaneName, RegistrationLane.LaneName, StatusLane.LaneName, ActivityLane.LaneName, ProcessesLane.LaneName, PeersLane.LaneName, MessagesLane.LaneName, ProgressLane.LaneName, ParkedLane.LaneName, CoverageLane.LaneName, UserAlarmsLane.LaneName];

    public IReadOnlyList<string> Affected(LaneKey changed, LaneRecord? previous, LaneRecord? current, IRecordView records) =>
        changed.Lane is CoverageLane.LaneName or UserAlarmsLane.LaneName
            ? AllIds(records)
            : [.. SummaryData.TeamsAffected(changed, previous, current, records).Select(static t => "team-summary:" + t.DirId + "/" + t.Team)
            .Concat(changed.Lane == TeamLane.LaneName
                ? LaneView.TeamsAround(changed, previous, current, records).Select(static t => "team-summary:" + t.DirId + "/" + t.Name)
                : [])
            .Distinct(StringComparer.Ordinal)];

    public IReadOnlyList<string> AllIds(IRecordView records) => [.. TreeData.Teams(records).Select(static t => "team-summary:" + t.DirId + "/" + t.Name)];

    public SliceChange Compose(string sliceId, IRecordView records)
    {
        if (TreeIds.OfTeam(sliceId) is not var (dirId, name) || TreeData.FindTeam(records, dirId, name) is not { } team)
        {
            return new SliceChange(sliceId, null);
        }

        var now = _now();
        var observedAt = _observedAtMs?.Invoke();
        var members = SummaryData.Members(records, team, now, observedAt);
        var body = Build(sliceId, team, dirId, name, records, members, now);
        NoteWork(sliceId, team, name, members);
        if (members.Any(static m => m.Verdict == Ah.Core.Time.LivenessVerdict.ProvenDead))
        {
            body[UnobservedKey] = JsonValue.FromObject(Build(sliceId, team, dirId, name, records, SummaryData.Members(records, team, now, observedAt, observed: false), now));
        }

        return new SliceChange(sliceId, JsonText.ToUtf8(JsonValue.FromObject(body), 0), team.AsOfMs);
    }

    /// <summary>Tells <c>workMoved</c> when the members running or working changed, since the Board and Sitrep read those for each item's work.</summary>
    private void NoteWork(string sliceId, TeamEntry team, string name, IReadOnlyList<MemberReading> members)
    {
        var sign = string.Join('\n', members.Where(SummaryData.IsRunning).Select(static m => m.Member + (m.State == "working" ? "+" : string.Empty)).Order(StringComparer.Ordinal));
        if (_workSeen.GetValueOrDefault(sliceId) != sign)
        {
            _workSeen[sliceId] = sign;
            workMoved?.Invoke(team.Dir, name);
        }
    }

    private JsonObject Build(string sliceId, TeamEntry team, string dirId, string name, IRecordView records, IReadOnlyList<MemberReading> members, long nowMs)
    {
        var working = members.Count(static m => m.State == "working");
        var blocked = members.Count(static m => m.State == "blocked");
        var exchanges = ExchangeFiling.OfTeam(records, dirId, name);
        var live = members.Where(SummaryData.IsRunning).Select(static m => m.Member).ToHashSet(StringComparer.Ordinal);
        var asked = exchanges.Where(static x => ExchangeFiling.IsOpen(x.Record)).ToList();
        // An open request is in flight only while its addressee is a member that is not gone; the rest are stale, counted apart.
        var open = asked.Where(x => ExchangeFiling.Resolve(x.Record, team).To is { } to && live.Contains(to)).ToList();
        var oldest = open.Select(static x => LaneParts.Ms(LaneParts.Text(x.Record.Value, "created"))).Where(static ms => ms > 0).DefaultIfEmpty(0).Min();
        var feedNewest = exchanges.Select(static x => x.Record.AsOfMs).DefaultIfEmpty(0).Max();
        var grants = TreeData.Grants(records, dirId, name);
        var orchestratorRow = grants is not null && grants.Value.Member("members") is { IsMap: true } rows
            ? rows.Member(TreeIds.ReservedName) is { IsMap: true } named ? named : rows.Member(TreeIds.Orchestrator) is { IsMap: true } at ? at : (JsonValue?)null
            : null;
        var grantedRoot = orchestratorRow is { } granted ? LaneParts.Text(granted, "work_root") : null;
        var plan = SummaryData.PlanCounts(records, dirId, name);
        // The team's own last work, the one date retiring reads too, never the dir's: the status document is stamped each time it is composed and is shared with every team of the
        // dir, as the pipeline is, and an Orchestrator's activity may be another team's work.
        var updated = new[] { team.AsOfMs, grants?.AsOfMs ?? 0, feedNewest, plan.AsOfMs }
            .Concat(members.Where(static m => m.Member != TreeIds.Orchestrator).Select(static m => m.StateAtMs))
            .Max();
        var work = SummaryData.PlanWork(records, dirId, team, SummaryData.Liveness(members, open.Select(static x => x.Id), asked.Select(static x => x.Id)), nowMs);
        var (alerts, truncated) = AlertsOf(team, members, work.Waiting, work.Rows, UserAlarmsLane.Of(records, team.Name));
        var waitingRows = new JsonArray();
        foreach (var row in work.Rows.Take(WaitingRowCap))
        {
            waitingRows.Add(JsonValue.FromObject(row));
        }

        var body = new JsonObject
        {
            ["team"] = TreeIds.Team(team.Dir, name),
            ["name"] = name,
            ["display_name"] = LaneParts.TextOrNull(team.Value, "display_name"),
            // The Orchestrator's grant when it has one; no live team has, so else the team file's root. The branch read from that root says nothing of a granted root.
            ["work_root"] = grantedRoot is not null ? grantedRoot : LaneParts.TextOrNull(team.Value, "expected_root"),
            ["branch"] = orchestratorRow is { } b && LaneParts.Text(b, "stream") is { } grantedBranch ? grantedBranch : grantedRoot is null ? LaneParts.TextOrNull(team.Value, "branch") : JsonValue.Null,
            ["running"] = members.Any(SummaryData.IsRunning),
            ["members"] = live.Count,
            ["working"] = working,
            ["blocked"] = blocked,
            ["needs_you"] = work.Waiting + blocked,
            ["waiting_on_you"] = work.Waiting,
            ["waiting_blockers"] = work.Rows.Count(static r => r["class"].AsString() == "blocker"),
            ["waiting_rows"] = waitingRows,
            ["in_progress_now"] = work.Working,
            ["stuck"] = work.Stuck,
            ["unfinished"] = work.Unfinished,
            ["state"] = SummaryData.TeamState(members.Count > 0, members.Any(SummaryData.IsRunning), work.Waiting + blocked, working),
            ["in_flight"] = open.Count,
            ["stale"] = asked.Count - open.Count,
            // The open exchanges whose addressee is a member that is gone. An exchange with no resolved addressee is unattended, not stale; its row says so.
            ["stale_ids"] = TreeData.Ids(asked.Where(x => ExchangeFiling.Resolve(x.Record, team).To is { } to && !live.Contains(to)).Take(OpenExchangeListComposer.MaxOpen).Select(x => MessagesLane.SliceIdOfDirId(dirId, x.Id))),
            ["streams_open"] = plan.StreamsOpen,
            ["streams_total"] = plan.StreamsTotal,
            ["items_done"] = plan.ItemsDone,
            ["items_total"] = plan.ItemsTotal,
            // False while the plan has not been read: every count from the plan then says nothing, and a client must not draw it as zero.
            ["plan_loaded"] = planPending?.Invoke(team.Dir, ProgressLane.FolderOf(name)) != true,
            ["worktrees"] = team.MemberNames.Select(m => RootOf(team, m)).OfType<string>().Distinct(StringComparer.Ordinal).Count(),
            ["oldest_in_flight_at"] = SummaryData.Iso(oldest),
            ["updated_at"] = SummaryData.Iso(updated),
            ["feed_newest_at"] = SummaryData.Iso(feedNewest),
            ["alerts"] = alerts,
            ["member_states"] = MemberStates(team, members, SummaryData.WorkItems(records, dirId, name)),
            ["stale_at"] = SummaryData.Iso(updated + SummaryData.StaleAfterMs),

            // ponytail: read when the summary composes, so a hold shows at the team's next change or reconcile; a lane on the hold file if that lag matters
            ["held"] = teamHeld?.Invoke(team.Dir, name) == true,
        };
        // Items counted in in_progress_now only on the word of members whose verdict is undecided: the response drops each once all of them
        // are no longer seen (ah2-31), so the team can go quiet. Absent when there is none, which is nearly always.
        if (UnseenWork(SummaryData.WorkItems(records, dirId, name), team, members, open.Select(x => (x.Id, ExchangeFiling.Resolve(x.Record, team).To))) is { Count: > 0 } unseen)
        {
            body[UnseenWorkKey] = unseen;
        }

        if (team.Value.Member("closed") is { IsMap: true } closed && LaneParts.Text(closed, "at") is { } closedAt)
        {
            body["closed_mark"] = closedAt;
        }

        // A team known only from a question in its plan: there is no readable record for a close to act on.
        if (team.Path.Length == 0)
        {
            body["no_record"] = true;
        }

        SummaryData.ApplyStatus(body);
        // ah-ui-ia §1.1 / U0 (b): the team slice's lane facts, cut to what a Home row needs; absent for a team that is no lane
        var laneFacts = new JsonObject();
        LaneView.Apply(laneFacts, team, TreeData.Teams(records).Where(t => t.Dir == team.Dir).ToList(), lanes);
        if (laneFacts.Get("lane") is { IsMap: true } lane)
        {
            body["lane"] = JsonValue.FromObject(new JsonObject { ["parent_team"] = lane.Member("parent_team")!.Value, ["stream"] = lane.Member("stream")!.Value });
        }
        else if (laneFacts.Get("lane_claim") is { } claim)
        {
            body["lane_claim"] = claim;
        }

        // ah2-53: the team's own goal, else its lane parent's; its team names whose row it is. Absent when none applies.
        var goal = team.Value.Member("goal");
        if (goal is not { IsMap: true } && laneFacts.Get("lane") is { IsMap: true } laneOf && LaneParts.Text(laneOf, "parent_team") is { } parentTeam)
        {
            goal = TreeData.FindTeam(records, dirId, parentTeam)?.Value.Member("goal");
        }

        if (goal is { IsMap: true } applies)
        {
            body["goal"] = applies;
        }

        if (truncated)
        {
            body["alerts_truncated"] = true;
        }

        if (records.Get(ProcessesLane.LaneName, team.Dir) is { IsAbsent: false } processes
            && processes.Value.Member("queue_waiting") is { IsMap: true } queueWaiting
            && queueWaiting.Member(name) is { IsNumber: true } queued)
        {
            body["queue_waiting"] = queued;
        }

        if (work.Rows.Count > WaitingRowCap)
        {
            body["waiting_truncated"] = true;
        }

        return body;
    }

    /// <summary>Each member's state, keyed by member slice id, from the same readings the counts come from; <c>state_at</c> is absent when the state's start is unknown.</summary>
    /// <summary>The work tree a member works in, as the CLI reads it: its own row's root, else the team's. The Orchestrator has no row, so the team's.</summary>
    private static string? RootOf(TeamEntry team, string member) =>
        (team.Row(member) is { } row ? LaneParts.Text(row, "expected_root") : null) ?? LaneParts.Text(team.Value, "expected_root");

    /// <summary>
    /// A composed-only member of the summary, removed before it is served: one entry per item <c>in_progress_now</c> counts that no member with a
    /// live verdict is on, listing the <c>member_states</c> keys of the undecided members that make it count (its holder, the addressees of its open requests).
    /// </summary>
    public const string UnseenWorkKey = "in_progress_if_seen";

    /// <summary>
    /// A composed-only member of a <c>member_states</c> entry, removed before it is served: the member is located (ah2-242 D1 L3-L5: the
    /// Orchestrator not proven dead, a blind processes lane, a starting member) however old its last sign of life, so it is not served as No signal.
    /// </summary>
    public const string LocatedKey = "located";

    /// <summary>
    /// A composed-only member of a <c>member_states</c> entry, removed before it is served: an entry idle only for want of an activity record
    /// (ah2-242 C1) that the stuck rule reads as <c>unknown</c>, as it did before C1.
    /// </summary>
    public const string StuckAsUnknownKey = "stuck_as_unknown";

    internal static JsonArray UnseenWork(IReadOnlyList<JsonValue> workItems, TeamEntry team, IReadOnlyList<MemberReading> members, IEnumerable<(string Id, string? To)> openToRunning)
    {
        var byName = members.ToDictionary(static m => m.Member, StringComparer.Ordinal);
        var addressee = openToRunning.Where(static x => x.To is not null).GroupBy(static x => x.Id, StringComparer.Ordinal).ToDictionary(static g => g.Key, static g => g.First().To!, StringComparer.Ordinal);
        var list = new JsonArray();
        foreach (var item in workItems.Where(static i => LaneParts.Text(i, "state") == "in_progress"))
        {
            // the same two reasons ChecklistWork calls an item working: its holder is working, or a request of its is open to a running member
            var on = new List<MemberReading>();
            if (LaneParts.Text(item, "owner") is { } owner && byName.GetValueOrDefault(owner) is { } holder && SummaryData.IsRunning(holder) && holder.State == "working")
            {
                on.Add(holder);
            }

            if (item.Member("refs") is { IsArray: true } refs)
            {
                on.AddRange(refs.AsArray().Where(static r => r.IsText).Select(r => addressee.GetValueOrDefault(r.AsString())).OfType<string>().Select(byName.GetValueOrDefault).OfType<MemberReading>());
            }

            if (on.Count == 0 || on.Any(static m => m.Verdict == Ah.Core.Time.LivenessVerdict.Live))
            {
                continue;
            }

            var keys = new JsonArray();
            keys.AddRange(on.Select(m => m.Member).Distinct(StringComparer.Ordinal).Select(m => (JsonValue)TreeIds.Member(team.Dir, team.Name, m)));
            list.Add(keys);
        }

        return list;
    }

    private static JsonValue MemberStates(TeamEntry team, IReadOnlyList<MemberReading> members, IReadOnlyList<JsonValue> workItems)
    {
        var states = new JsonObject();
        foreach (var m in members)
        {
            var entry = new JsonObject { ["state"] = m.State, ["liveness"] = SummaryData.LivenessWord(m.Verdict) };
            entry["process"] = SummaryData.ProcessLine(SummaryData.LivenessWord(m.Verdict), m.State);
            entry["column"] = TreeIds.MemberColumn(team, m.Member);

            // What the Overview tells of a member, each only when known: its tool, its item, its branch and its worktree.
            var row = team.Row(m.Member);
            var known = new (string Key, string? Value)[]
            {
                ("tool", m.Tool),
                ("item", workItems.FirstOrDefault(i => LaneParts.Text(i, "state") == "in_progress" && LaneParts.Text(i, "owner") == (m.Member == TreeIds.Orchestrator ? TreeIds.ReservedName : m.Member)) is { IsMap: true } held ? LaneParts.Text(held, "title") : null),
                ("branch", (row is { } r ? LaneParts.Text(r, "stream") : null) ?? (RootOf(team, m.Member) == LaneParts.Text(team.Value, "expected_root") ? LaneParts.Text(team.Value, "branch") : null)),
                ("work_root", RootOf(team, m.Member)),
            };
            foreach (var (key, value) in known.Where(static k => k.Value is { Length: > 0 }))
            {
                entry[key] = value!;
            }

            if (m.StateAtMs > 0)
            {
                entry["state_at"] = SummaryData.Iso(m.StateAtMs);
            }

            // A live member's registered pane (every session start registers it afresh); a member not live serves none, since its pane may
            // since hold another program.
            // ah2-73: the Orchestrator's row keeps its pane while nothing proves the session dead. An idle Orchestrator, or one
            // waiting at a question, writes no heartbeat and so reads Unknown, which is exactly when the user needs to click
            // through to it; and unlike a member it carries no label a click could fall back on.
            var servesPane = m.Verdict == Ah.Core.Time.LivenessVerdict.Live
                || (m.Member == TreeIds.Orchestrator && m.Verdict != Ah.Core.Time.LivenessVerdict.ProvenDead);
            if (servesPane && m.Pane is { } pane)
            {
                entry["pane_id"] = pane;
            }

            if (m.TurnOpen)
            {
                entry["turn_open"] = true;
            }

            if (m.Located)
            {
                entry[LocatedKey] = true;
            }

            if (m.StuckAsUnknown)
            {
                entry[StuckAsUnknownKey] = true;
            }

            // A whole percent, so a tool call republishes nothing; a dead member holds no context.
            if (m.ContextPct is { } contextPct && m.State != "gone")
            {
                entry["context_pct"] = JsonValue.FromNumber(contextPct);
            }

            // A member that is not live needs its last sign of life: it is served as not seen once undecided and old enough, including a death
            // that is later served as unproven. It is floored to the minute so that a member working under an undecided verdict republishes
            // the summary once a minute, not once per tool call.
            if (m.Verdict != Ah.Core.Time.LivenessVerdict.Live && m.LastLifeMs > 0)
            {
                entry["last_life_at"] = SummaryData.Iso(m.LastLifeMs / 60_000 * 60_000);
            }

            states[TreeIds.Member(team.Dir, team.Name, m.Member)] = JsonValue.FromObject(entry);
        }

        return JsonValue.FromObject(states);
    }

    /// <summary>
    /// The team's alerts, newest first: a decision waiting, a member blocked on a permission, a member whose last turn failed. A member that is
    /// gone is not an alert. <c>since</c> is when the state began, so it does not move while the state holds. For what waits on the user that is
    /// the oldest time a waiting row records, a fact of the plan that a daemon restart leaves alone; null when no row records one.
    /// </summary>
    /// <summary>The alert kind of a raised user alarm.</summary>
    public const string UserAlarmKind = "alarm";

    private static (JsonValue Alerts, bool Truncated) AlertsOf(TeamEntry team, IReadOnlyList<MemberReading> members, long waiting, IEnumerable<JsonObject> waitingRows, IReadOnlyList<JsonValue> alarms)
    {
        var items = new List<(long Since, string Kind, string? Member, string Text)>();
        if (waiting > 0)
        {
            var began = waitingRows.Select(static row => LaneParts.Ms(row.Str("since"))).Where(static ms => ms > 0).DefaultIfEmpty(0).Min();
            items.Add((began, "needs_you", null, "Waiting on you · " + waiting.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        foreach (var m in members.Where(static m => m.State == "blocked"))
        {
            var failed = m.Activity == "failed";
            items.Add((m.StateAtMs, failed ? "error" : "blocked", TreeIds.Member(team.Dir, team.Name, m.Member), failed ? "Last turn failed" : m.Question is not { } asked ? "Waiting on a permission" : asked.Length > 0 ? "Asking in the terminal: " + asked : "Asking in the terminal"));
        }

        // ah2-180 Amendment 2 (c): each user alarm the stall watch has raised for the team, its own line as the text, until it clears
        foreach (var alarm in alarms)
        {
            items.Add((LaneParts.Ms(LaneParts.Text(alarm, "raised_at")), UserAlarmKind, null, LaneParts.Text(alarm, "line") ?? string.Empty));
        }

        var array = new JsonArray();
        foreach (var i in items.OrderByDescending(static i => i.Since).ThenBy(static i => i.Member, StringComparer.Ordinal).ThenBy(static i => i.Kind, StringComparer.Ordinal).Take(AlertCap))
        {
            var item = new JsonObject { ["kind"] = i.Kind };
            if (i.Kind == UserAlarmKind)
            {
                item["audience"] = "user";
            }

            if (i.Member is not null)
            {
                item["member"] = i.Member;
            }

            item["since"] = SummaryData.Iso(i.Since);
            item["text"] = i.Text;
            var (title, detail) = SummaryData.AlertCard(i.Kind, i.Member, i.Text);
            item["title"] = title;
            item["detail"] = detail;
            array.Add(JsonValue.FromObject(item));
        }

        return (JsonValue.FromArray(array), items.Count > AlertCap);
    }
}
