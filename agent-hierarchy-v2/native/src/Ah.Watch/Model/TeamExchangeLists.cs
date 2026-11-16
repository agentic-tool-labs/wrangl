using Ah.Core.Json;

namespace Ah.Watch.Model;

/// <summary>
/// Reads the parties and the team of an exchange from its messages-lane record, and files exchanges under the teams of the teams lane.
/// A party is the member the exchange names: a party whose role is <c>orchestrator</c> is the orchestrator pseudo-member whatever its name;
/// any other party is the member whose name equals the front-matter name (<c>to_name</c> or <c>from_name</c>), the same string the team document
/// carries for that member. An exchange with no team, or a team the exchange's directory does not have, belongs to no team.
/// This is the one place that resolves a party, for the member's last exchanges and for the open list alike.
/// </summary>
internal static class ExchangeFiling
{
    /// <summary>
    /// The team an exchange is filed under: the team its front matter names, and the default team when it names none, because the writer
    /// leaves <c>team</c> null for the default team. Whether that team exists in the exchange's directory is for the caller to check.
    /// </summary>
    public static string TeamOf(LaneRecord record) => LaneParts.Text(record.Value, "team") ?? TeamLane.DefaultName;

    /// <summary>Whether the exchange has no response yet.</summary>
    public static bool IsOpen(LaneRecord record) => record.Value.Member("open") is { IsBool: true } open && open.AsBool();

    /// <summary>
    /// The member of <paramref name="team"/> (as the Tree's member ids spell it) each end of the exchange names. A party with the
    /// orchestrator role is the orchestrator pseudo-member whatever its name. Any other party is the member of its name when the team
    /// has one; a party with no name is the member that is the single current holder of its role in the team. A party that resolves to
    /// nobody is null: an unknown name, or a role with no holder or more than one. A name always beats a role.
    /// </summary>
    public static (string? To, string? From) Resolve(LaneRecord record, TeamEntry team) =>
        (Party(record, "to", "to_name", team), Party(record, "from", "from_name", team));

    /// <summary>The distinct members the exchange names, as <see cref="Resolve"/> resolves them.</summary>
    public static IReadOnlyList<string> PartiesOf(LaneRecord record, TeamEntry team)
    {
        var (to, from) = Resolve(record, team);
        return [.. new[] { to, from }.Where(static p => p is not null).Distinct(StringComparer.Ordinal).Select(static p => p!)];
    }

    private static string? Party(LaneRecord record, string roleField, string nameField, TeamEntry team)
    {
        var role = LaneParts.Text(record.Value, roleField);
        var party = role == TreeIds.ReservedName
            ? TreeIds.Orchestrator
            : LaneParts.Text(record.Value, nameField) ?? (role is null ? null : SoleHolder(team, role));
        return party is not null && team.MemberNames.Contains(party) ? party : null;
    }

    private static string? SoleHolder(TeamEntry team, string role)
    {
        string? holder = null;
        foreach (var member in team.MemberNames.Where(static m => m != TreeIds.Orchestrator))
        {
            if (team.Row(member) is { } row && LaneParts.Text(row, "role") == role)
            {
                if (holder is not null)
                {
                    return null;
                }

                holder = member;
            }
        }

        return holder;
    }

    /// <summary>
    /// The exchange ids of the team's directory that name <paramref name="team"/>, newest first, at most <see cref="MessagesLane.MaxExchangesPerDir"/>,
    /// each with its record. Within one composition pass the list is built once per team.
    /// </summary>
    public static IReadOnlyList<(string Id, LaneRecord Record)> OfTeam(IRecordView records, string dirId, string team) =>
        records is IPassMemo memo ? memo.GetOrAdd("team-exchanges:" + dirId + "/" + team, () => Build(records, dirId, team)) : Build(records, dirId, team);

    private static List<(string Id, LaneRecord Record)> Build(IRecordView records, string dirId, string team)
    {
        var found = new List<(string Id, LaneRecord Record)>();
        string? lastDir = null;
        var lastMatches = false;
        foreach (var key in records.Keys(MessagesLane.LaneName))
        {
            var at = key.IndexOf(MessagesLane.Separator, StringComparison.Ordinal);
            if (lastDir is null || !key.AsSpan(0, at).SequenceEqual(lastDir))
            {
                lastDir = key[..at];
                lastMatches = ActivityLane.DirId(lastDir) == dirId;
            }

            if (lastMatches && records.Get(MessagesLane.LaneName, key) is { IsAbsent: false } record && TeamOf(record) == team)
            {
                found.Add((key[(at + 1)..], record));
            }
        }

        return [.. found.OrderByDescending(static f => f.Id, StringComparer.Ordinal).Take(MessagesLane.MaxExchangesPerDir)];
    }
}

/// <summary>
/// A list slice of one team's exchanges, <c>&lt;prefix&gt;:&lt;dirId&gt;/&lt;team&gt;</c>, composed from the messages lane's records and the teams lane,
/// newest first, capped. A team with none of the listed exchanges has no list.
/// </summary>
internal abstract class TeamScopedExchangeList(string slicePrefix, int cap) : ISliceComposer
{
    public string SlicePrefix { get; } = slicePrefix;

    public IReadOnlyList<string> Lanes { get; } = [MessagesLane.LaneName, TeamLane.LaneName];

    /// <summary>Whether the exchange belongs in this list.</summary>
    protected abstract bool Listed(LaneRecord record);

    /// <summary>The slice body for <paramref name="listed"/>, the team's listed exchanges, newest first.</summary>
    protected abstract JsonObject BodyOf(TeamEntry team, IReadOnlyList<(string Id, LaneRecord Record)> listed);

    private string IdOf(string dirId, string team) => SlicePrefix + ":" + dirId + "/" + team;

    public IReadOnlyList<string> Affected(LaneKey changed, LaneRecord? previous, LaneRecord? current, IRecordView records)
    {
        if (changed.Lane == TeamLane.LaneName)
        {
            var (teamDir, path) = LaneParts.Split(changed.Key);
            return TeamLane.NameOf(teamDir, path) is { } name ? [IdOf(ActivityLane.DirId(teamDir), name)] : [];
        }

        var dirId = ActivityLane.DirId(MessagesLane.Split(changed.Key).Dir);
        return [.. new[] { previous, current }
            .Where(static r => r is { IsAbsent: false })
            .Select(static r => ExchangeFiling.TeamOf(r!))
            .Distinct(StringComparer.Ordinal)
            .Where(t => TreeData.FindTeam(records, dirId, t) is not null)
            .Select(t => IdOf(dirId, t))];
    }

    public IReadOnlyList<string> AllIds(IRecordView records)
    {
        var teams = TreeData.Teams(records).Select(static t => (t.DirId, t.Name)).ToHashSet();
        var ids = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var key in records.Keys(MessagesLane.LaneName))
        {
            if (records.Get(MessagesLane.LaneName, key) is { IsAbsent: false } record && Listed(record)
                && teams.Contains((ActivityLane.DirId(MessagesLane.Split(key).Dir), ExchangeFiling.TeamOf(record))))
            {
                _ = ids.Add(IdOf(ActivityLane.DirId(MessagesLane.Split(key).Dir), ExchangeFiling.TeamOf(record)));
            }
        }

        return [.. ids];
    }

    public SliceChange Compose(string sliceId, IRecordView records)
    {
        var head = SlicePrefix + ":";
        var rest = sliceId.StartsWith(head, StringComparison.Ordinal) ? sliceId[head.Length..] : throw new ArgumentException("not a team exchange list of " + SlicePrefix, nameof(sliceId));
        var slash = rest.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0 || slash == rest.Length - 1 || TreeData.FindTeam(records, rest[..slash], rest[(slash + 1)..]) is not { } team)
        {
            return new SliceChange(sliceId, null);
        }

        var listed = ExchangeFiling.OfTeam(records, team.DirId, team.Name).Where(x => Listed(x.Record)).Take(cap).ToList();
        return listed.Count == 0
            ? new SliceChange(sliceId, null)
            : new SliceChange(sliceId, JsonText.ToUtf8(JsonValue.FromObject(BodyOf(team, listed)), 0), listed.Max(static x => x.Record.AsOfMs));
    }
}

/// <summary>
/// One team-keyed exchange list: <c>list:exchanges:team:&lt;dirId&gt;/&lt;team&gt;</c> or <c>list:feed:team:&lt;dirId&gt;/&lt;team&gt;</c>, one composer for
/// each prefix with the same content. It holds the ids of the exchanges that name the team, newest first, and nothing else.
/// </summary>
internal sealed class TeamExchangeListComposer(string slicePrefix, Func<long>? nowMs = null) : TeamScopedExchangeList(slicePrefix, MessagesLane.MaxExchangesPerDir)
{
    private const long HourMs = 3_600_000;
    private const int RecentCap = 100;

    private readonly Func<long> _nowMs = nowMs ?? (static () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    public const string ExchangesPrefix = "list:exchanges:team";
    public const string FeedPrefix = "list:feed:team";

    protected override bool Listed(LaneRecord record) => true;

    protected override JsonObject BodyOf(TeamEntry team, IReadOnlyList<(string Id, LaneRecord Record)> listed)
    {
        var ids = new JsonArray();
        foreach (var (id, _) in listed)
        {
            ids.Add(id);
        }

        var (pairs, unresolved) = PairCounts(team, listed);
        return new JsonObject { ["team"] = TreeIds.Team(team.Dir, team.Name), ["ids"] = ids, ["pairs"] = pairs, ["unresolved"] = unresolved };
    }

    /// <summary>
    /// How many of the listed exchanges each unordered pair of resolved members shares (<c>n</c>), and the creation times (epoch ms, newest first,
    /// at most <see cref="RecentCap"/>) of those created within the hour before now (<c>recent_1h</c>), pairs in name order. The client counts
    /// the hour itself when it needs the number, so the list does not go stale between recompositions. An exchange with an end that resolves
    /// to nobody counts in <c>unresolved</c> only. <c>last_response</c> is the latest <c>response_created</c> (epoch ms) of the pair's exchanges that were answered and not withdrawn, null when there is none. An exchange with no readable <c>created</c> counts in <c>n</c> and has no time in <c>recent_1h</c>.
    /// </summary>
    private (JsonValue Pairs, JsonValue Unresolved) PairCounts(TeamEntry team, IReadOnlyList<(string Id, LaneRecord Record)> listed)
    {
        var since = _nowMs() - HourMs;
        var counts = new SortedDictionary<(string A, string B), (int N, List<long> Recent, long Last)>(Comparer<(string A, string B)>.Create(static (x, y) =>
        {
            var byA = string.CompareOrdinal(x.A, y.A);
            return byA != 0 ? byA : string.CompareOrdinal(x.B, y.B);
        }));
        var unresolved = 0;
        foreach (var (_, record) in listed)
        {
            var created = LaneParts.Ms(LaneParts.Text(record.Value, "created"));
            var (to, from) = ExchangeFiling.Resolve(record, team);
            if (to is null || from is null)
            {
                unresolved++;
                continue;
            }

            var key = string.CompareOrdinal(to, from) <= 0 ? (to, from) : (from, to);
            if (!counts.TryGetValue(key, out var seen))
            {
                seen = (0, [], 0);
            }

            if (created > 0 && created >= since)
            {
                seen.Recent.Add(created);
            }

            var answered = record.Value.Member("withdrawn") is { IsBool: true } withdrawn && withdrawn.AsBool() ? 0 : LaneParts.Ms(LaneParts.Text(record.Value, "response_created"));
            counts[key] = (seen.N + 1, seen.Recent, Math.Max(seen.Last, answered));
        }

        var pairs = new JsonArray();
        foreach (var ((a, b), (n, recent, last)) in counts)
        {
            var times = new JsonArray();
            foreach (var at in recent.OrderByDescending(static t => t).Take(RecentCap))
            {
                times.Add(at);
            }

            pairs.Add(JsonValue.FromObject(new JsonObject { ["a"] = a, ["b"] = b, ["n"] = n, ["recent_1h"] = times, ["last_response"] = last > 0 ? JsonValue.FromNumber(last) : JsonValue.Null }));
        }

        return (JsonValue.FromArray(pairs), JsonValue.FromObject(new JsonObject { ["n"] = unresolved }));
    }
}

/// <summary>
/// <c>list:open:team:&lt;dirId&gt;/&lt;team&gt;</c>: the team's open exchanges, newest first, each with the member slice id of its two ends
/// (null for an end that resolves to nobody), resolved by <see cref="ExchangeFiling.Resolve"/> like the members' last exchanges.
/// </summary>
internal sealed class OpenExchangeListComposer() : TeamScopedExchangeList(Prefix, MaxOpen)
{
    public const string Prefix = "list:open:team";

    /// <summary>The newest open exchanges a team's list holds.</summary>
    public const int MaxOpen = 500;

    protected override bool Listed(LaneRecord record) => ExchangeFiling.IsOpen(record);

    protected override JsonObject BodyOf(TeamEntry team, IReadOnlyList<(string Id, LaneRecord Record)> listed)
    {
        var open = new JsonArray();
        foreach (var (id, record) in listed)
        {
            var (to, from) = ExchangeFiling.Resolve(record, team);
            var row = new JsonObject
            {
                ["id"] = MessagesLane.SliceIdOfDirId(team.DirId, id),
                ["to_member"] = to is null ? JsonValue.Null : TreeIds.Member(team.Dir, team.Name, to),
                ["from_member"] = from is null ? JsonValue.Null : TreeIds.Member(team.Dir, team.Name, from),
            };
            if (to is null)
            {
                // Nobody is known to attend an exchange whose addressee resolves to no member; this says since when.
                row["unattended_at"] = LaneParts.Text(record.Value, "created") ?? SummaryData.Iso(record.AsOfMs);
            }

            open.Add(JsonValue.FromObject(row));
        }

        return new JsonObject { ["team"] = TreeIds.Team(team.Dir, team.Name), ["open"] = JsonValue.FromArray(open) };
    }
}
