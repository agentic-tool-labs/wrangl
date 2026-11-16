using System.Globalization;
using Ah.Core.Json;

namespace Ah.StateDb;

/// <summary>
/// The message tables read as <see cref="IMsgSource"/>. It returns facts only; what an exchange means is the caller's predicate.
/// Each call reads through its own read-only connection.
/// </summary>
internal sealed class StateDbMsgReader(string ahHome, StateDbReadPool? pool = null) : IMsgSource
{
    private delegate IReadOnlyList<object?[]> Query(string sql, object?[] args);

    private const string Domain = "msgs";

    private const int Columns = 17;

    private static string Cols(string a) =>
        $"{a}.pool_dir, {a}.id, {a}.type, {a}.\"to\", {a}.slug, {a}.\"from\", {a}.to_name, {a}.from_name, {a}.team, {a}.parent, {a}.created, {a}.created_ms, {a}.state, {a}.draft_path, {a}.export_path, {a}.body_rev, {a}.pk";

    public IReadOnlyList<MsgExchangePair> Exchanges(IReadOnlyList<string> poolDirs) => Once(select => ExchangesFrom(select, poolDirs));

    public IReadOnlyList<MsgFileRow> Messages(string poolDir, string? id = null, string? type = null) => Once(select => MessagesFrom(select, poolDir, id, type));

    public IReadOnlyList<string> MessageNames(string poolDir) => Once(select => NamesFrom(select, poolDir));

    /// <summary><see cref="MessagesSql"/> with no id or type, reading only the two paths <see cref="Map"/> takes the name from.</summary>
    internal static string NamesSql { get; } =
        "SELECT m.draft_path, m.export_path FROM msg_messages m WHERE m.pool_dir = ?1 AND m.state <> 'archived' ORDER BY m.id, m.type, m.\"to\", m.slug";

    private static List<string> NamesFrom(Query select, string poolDir)
    {
        ArgumentNullException.ThrowIfNull(poolDir);
        return [.. select(NamesSql, [MsgRows.PoolKey(poolDir)]).Select(static r => NameOf(r[0] as string ?? r[1] as string ?? string.Empty))];
    }

    private static string NameOf(string path) => path[(path.LastIndexOf('/') + 1)..];

    public IReadOnlyList<MsgFileRow> ArchivedMessages(string poolDir) => Once(select => ArchivedFrom(select, poolDir));

    public IReadOnlyList<MsgFileRow> MessagesById(string id, string type) => Once(select => ByIdFrom(select, id, type));

    internal static string ByIdSql => $"SELECT {Cols("m")} FROM msg_messages m WHERE m.id = ?1 AND m.type = ?2 ORDER BY m.pool_dir, m.\"to\", m.slug";

    private static IReadOnlyList<MsgFileRow> ByIdFrom(Query select, string id, string type) => [.. select(ByIdSql, [id, type]).Select(static r => Map(r, 0))];

    public IReadOnlyList<MsgFileRow>? RequestsOpenedSince(string poolDir, string recipient, string slug, long openedSinceMs) =>
        Once(select => RequestsOpenedSinceFrom(select, poolDir, recipient, slug, openedSinceMs));

    public IReadOnlyList<MsgFileRow> LiveMessages(string poolDir, long answeredSinceMs) => Once(select => LiveFrom(select, poolDir, answeredSinceMs));

    public MsgFileRow? ByPath(string path) => Once(select => ByPathFrom(select, path));

    public IReadOnlyList<MsgFileRow>? MessagesWithBodies(string poolDir, string id) => Once(select => WithBodiesFrom(select, poolDir, id));

    /// <summary><see cref="MessagesSql"/> by id, with each message's current body joined as <see cref="ByPathSql"/> joins it.</summary>
    internal static string WithBodiesSql { get; } =
        $"SELECT {Cols("m")}, b.bytes, b.sealed_at_ms FROM msg_messages m LEFT JOIN msg_bodies b ON b.msg_pk = m.pk AND b.rev = m.body_rev "
        + "WHERE m.pool_dir = ?1 AND m.state <> 'archived' AND m.id = ?2 ORDER BY m.id, m.type, m.\"to\", m.slug";

    private static List<MsgFileRow> WithBodiesFrom(Query select, string poolDir, string id) =>
        [.. select(WithBodiesSql, [MsgRows.PoolKey(poolDir), id]).Select(static r => Map(r, 0) with { Bytes = r[Columns] as byte[], SealedAtMs = r[Columns + 1] as long? })];

    internal static string LiveSql { get; } =
        $"SELECT {Cols("m")} FROM msg_messages m WHERE m.pool_dir = ?1 AND m.state <> 'archived' AND m.id IN (SELECT e.id FROM msg_exchanges e WHERE e.pool_dir = ?1 AND e.live = 1 UNION SELECT e.id FROM msg_exchanges e WHERE e.pool_dir = ?1 AND e.answered_ms >= ?2) ORDER BY m.id, m.type, m.\"to\", m.slug";

    private static List<MsgFileRow> LiveFrom(Query select, string poolDir, long answeredSinceMs)
    {
        ArgumentNullException.ThrowIfNull(poolDir);
        return [.. select(LiveSql, [MsgRows.PoolKey(poolDir), answeredSinceMs]).Select(r => Map(r, 0))];
    }

    public IReadOnlyList<string> RequestIdsWithSlug(string poolDir, string slug) => Once(select => SlugIdsFrom(select, poolDir, slug));

    public IReadOnlyList<string> RunSlugs(string poolDir, string? team, long openedSinceMs) => Once(select => RunSlugsFrom(select, poolDir, team, openedSinceMs));

    public IReadOnlyList<(string Id, string Slug)> ExchangeSlugs(string poolDir) => Once(select => ExchangeSlugsFrom(select, poolDir));

    internal static string ExchangeSlugsSql =>
        "SELECT e.id, q.slug FROM msg_exchanges e JOIN msg_messages q ON q.pk = e.request_pk WHERE e.pool_dir = ?1 AND e.state <> 'archived' AND q.state <> 'archived'";

    private static List<(string Id, string Slug)> ExchangeSlugsFrom(Query select, string poolDir)
    {
        ArgumentNullException.ThrowIfNull(poolDir);
        return [.. select(ExchangeSlugsSql, [MsgRows.PoolKey(poolDir)]).Select(static r => ((string)r[0]!, r[1] as string ?? string.Empty))];
    }

    internal static string SlugIdsSql => "SELECT DISTINCT id FROM msg_messages WHERE slug = ?2 AND type = 'request' AND pool_dir = ?1 AND state <> 'archived'";

    internal static string RunSlugsSql =>
        "SELECT DISTINCT q.slug FROM msg_exchanges e JOIN msg_messages q ON q.pk = e.request_pk WHERE e.team IS ?2 AND e.opened_ms >= ?3 AND e.pool_dir = ?1 AND e.state <> 'archived' AND q.state <> 'archived'";

    private static List<string> SlugIdsFrom(Query select, string poolDir, string slug)
    {
        ArgumentNullException.ThrowIfNull(poolDir);
        return [.. select(SlugIdsSql, [MsgRows.PoolKey(poolDir), slug]).Select(static r => (string)r[0]!)];
    }

    private static List<string> RunSlugsFrom(Query select, string poolDir, string? team, long openedSinceMs)
    {
        ArgumentNullException.ThrowIfNull(poolDir);
        return [.. select(RunSlugsSql, [MsgRows.PoolKey(poolDir), team, openedSinceMs]).Select(static r => (string)r[0]!)];
    }

    public IReadOnlyList<string> CharterCandidateIds(string poolDir, string team, IReadOnlyList<string> tos, long openedSinceMs) =>
        Once(select => CharterCandidatesFrom(select, poolDir, team, tos, openedSinceMs));

    /// <summary>
    /// The statement for <see cref="CharterCandidateIds"/> with <paramref name="tos"/> recipients bound from ?4. A NULL or empty team is kept,
    /// since the charter rule then takes the team from the request's team file. A NULL <c>opened_ms</c> is not: it comes from a <c>created</c>
    /// that does not parse, and the rule never takes such a request.
    /// </summary>
    internal static string CharterCandidatesSql(int tos) =>
        "SELECT e.id FROM msg_exchanges e JOIN msg_messages q ON q.pk = e.request_pk WHERE e.pool_dir = ?1 AND (e.team = ?2 OR e.team IS NULL OR e.team = '') AND e.opened_ms >= ?3"
        + " AND e.\"to\" IN (" + string.Join(", ", Enumerable.Range(4, tos).Select(static i => "?" + i.ToString(CultureInfo.InvariantCulture))) + ")"
        + " AND e.state <> 'archived' AND q.state <> 'archived' ORDER BY e.id";

    private static List<string> CharterCandidatesFrom(Query select, string poolDir, string team, IReadOnlyList<string> tos, long openedSinceMs)
    {
        ArgumentNullException.ThrowIfNull(poolDir);
        ArgumentNullException.ThrowIfNull(tos);
        return tos.Count == 0 ? [] : [.. select(CharterCandidatesSql(tos.Count), [MsgRows.PoolKey(poolDir), team, openedSinceMs, .. tos]).Select(static r => (string)r[0]!)];
    }

    public bool AnyAnswered(string poolDir, string recipient) => Once(select => AnyAnsweredFrom(select, poolDir, recipient));

    public IReadOnlyList<string> ClosedIdsAnsweredBefore(string poolDir, long answeredBeforeMs) => Once(select => ClosedIdsFrom(select, poolDir, answeredBeforeMs));

    // ah-msgs-fast-2 §3.8 item 7 (ruling Q5, F6): prune's closed set by query, never as "not in the listing"; the cutoff applies to the
    // exchange's last change the row holds (its answer, else when it closed, else its open), so a closed exchange never answered is kept
    // for the window from its close; one closed before closed_ms was recorded got the V12 upgrade time instead
    internal const string ClosedIdsSql =
        "SELECT id FROM msg_exchanges WHERE pool_dir = ?1 AND live = 0 AND COALESCE(answered_ms, closed_ms, opened_ms) < ?2 AND state <> 'archived' ORDER BY id";

    private static List<string> ClosedIdsFrom(Query select, string poolDir, long answeredBeforeMs)
    {
        ArgumentNullException.ThrowIfNull(poolDir);
        return [.. select(ClosedIdsSql, [MsgRows.PoolKey(poolDir), answeredBeforeMs]).Select(static r => (string)r[0]!)];
    }

    internal static string AnyAnsweredSql =>
        "SELECT 1 FROM msg_exchanges e JOIN msg_messages q ON q.pk = e.request_pk WHERE e.pool_dir = ?1 AND e.\"to\" = ?2 AND e.response_pk IS NOT NULL AND e.state <> 'archived' AND q.state <> 'archived' LIMIT 1";

    private static bool AnyAnsweredFrom(Query select, string poolDir, string to)
    {
        ArgumentNullException.ThrowIfNull(poolDir);
        return select(AnyAnsweredSql, [MsgRows.PoolKey(poolDir), to]).Count > 0;
    }

    public IReadOnlyDictionary<string, string?> TeamsByPath(IReadOnlyList<string> paths) => Once(select => TeamsByPathFrom(select, paths));

    public IReadOnlyList<MsgFileRow> MessagesWithIds(string poolDir, IReadOnlyList<string> ids) => Once(select => MessagesWithIdsFrom(select, poolDir, ids));

    public IReadOnlyList<(string Id, string To, string? Team, string Pool)> ExchangeRowsBefore(
        IReadOnlyList<string> poolDirs, string? beforeId, int count, IReadOnlyList<string>? teams, string? recipient) =>
        Once(select => ExchangeRowsBeforeFrom(select, poolDirs, beforeId, count, teams, recipient));

    public long ExchangeCount(IReadOnlyList<string> poolDirs, IReadOnlyList<string>? teams, string? recipient) => Once(select => ExchangeCountFrom(select, poolDirs, teams, recipient));

    public IReadOnlyList<string> RequestDescendants(string poolDir, IReadOnlyList<string> ids) => Once(select => RequestDescendantsFrom(select, poolDir, ids));

    /// <summary>The statement for <see cref="RequestDescendants"/> over <paramref name="count"/> ids bound from ?2: the children by the parent index, then theirs.</summary>
    internal static string RequestDescendantsSql(int count) =>
        "WITH RECURSIVE d(id) AS (SELECT id FROM msg_messages WHERE pool_dir = ?1 AND type = 'request' AND parent IN ("
        + string.Join(", ", Enumerable.Range(2, count).Select(static i => "?" + i.ToString(CultureInfo.InvariantCulture)))
        + ") AND state <> 'archived' UNION SELECT m.id FROM msg_messages m JOIN d ON m.parent = d.id WHERE m.pool_dir = ?1 AND m.type = 'request' AND m.state <> 'archived') SELECT id FROM d";

    private static List<string> RequestDescendantsFrom(Query select, string poolDir, IReadOnlyList<string> ids)
    {
        ArgumentNullException.ThrowIfNull(poolDir);
        ArgumentNullException.ThrowIfNull(ids);
        var asked = ids.ToHashSet(StringComparer.Ordinal);
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var chunk in ids.Distinct(StringComparer.Ordinal).Chunk(PathsPerQuery))
        {
            found.UnionWith(select(RequestDescendantsSql(chunk.Length), [MsgRows.PoolKey(poolDir), .. chunk]).Select(static r => (string)r[0]!));
        }

        return [.. found.Where(id => !asked.Contains(id))];
    }

    private const string ExchangeRowsFrom = "FROM msg_exchanges e JOIN msg_messages q ON q.pk = e.request_pk WHERE e.state <> 'archived' AND q.state <> 'archived'";

    private static string Pools(int count) => "e.pool_dir IN (" + string.Join(", ", Enumerable.Range(1, count).Select(static i => "?" + i.ToString(CultureInfo.InvariantCulture))) + ")";

    /// <summary>
    /// The team and recipient filter of a page or count: teams by the msg_exchanges_team_id index (?{first}.. , an empty text standing for no
    /// team), then the recipient (?{first + teams}); null teams = any. msg_exchanges.team is the request's stored team.
    /// </summary>
    private static string Filter(int first, int? teams) =>
        (teams is { } n ? " AND ifnull(e.team, '') IN (" + string.Join(", ", Enumerable.Range(first, n).Select(static i => "?" + i.ToString(CultureInfo.InvariantCulture))) + ")" : string.Empty)
        + $" AND (?{first + (teams ?? 0)} IS NULL OR e.\"to\" = ?{first + (teams ?? 0)})";

    /// <summary>The statements of <see cref="ExchangeRowsBefore"/>: the page of ids (pools ?1.., the bound, the count, then the filter), then their rows.</summary>
    internal static string ExchangeIdsBeforeSql(int pools, int? teams) =>
        $"SELECT DISTINCT e.id {ExchangeRowsFrom} AND {Pools(pools)} AND (?{pools + 1} IS NULL OR e.id < ?{pools + 1}){Filter(pools + 3, teams)} ORDER BY e.id DESC LIMIT ?{pools + 2}";

    internal static string ExchangeRowsOfSql(int pools, int ids) =>
        $"SELECT e.id, q.\"to\", q.team, e.pool_dir {ExchangeRowsFrom} AND {Pools(pools)} AND e.id IN ("
        + string.Join(", ", Enumerable.Range(pools + 1, ids).Select(static i => "?" + i.ToString(CultureInfo.InvariantCulture))) + ") ORDER BY e.id DESC";

    internal static string ExchangeCountSql(int pools, int? teams) =>
        $"SELECT count(DISTINCT e.id) {ExchangeRowsFrom} AND {Pools(pools)}{Filter(pools + 1, teams)}";

    private static List<(string Id, string To, string? Team, string Pool)> ExchangeRowsBeforeFrom(
        Query select, IReadOnlyList<string> poolDirs, string? beforeId, int count, IReadOnlyList<string>? teams, string? recipient)
    {
        ArgumentNullException.ThrowIfNull(poolDirs);
        object?[] pools = [.. poolDirs.Select(MsgRows.PoolKey).Distinct(StringComparer.Ordinal)];
        object?[] filter = [.. (teams ?? []).Distinct(StringComparer.Ordinal), recipient];
        var ids = select(ExchangeIdsBeforeSql(pools.Length, teams is null ? null : filter.Length - 1), [.. pools, beforeId, count, .. filter])
            .Select(static r => (object?)(string)r[0]!).ToList();
        return ids.Count == 0
            ? []
            : [.. select(ExchangeRowsOfSql(pools.Length, ids.Count), [.. pools, .. ids]).Select(static r => ((string)r[0]!, r[1] as string ?? string.Empty, r[2] as string, (string)r[3]!))];
    }

    private static long ExchangeCountFrom(Query select, IReadOnlyList<string> poolDirs, IReadOnlyList<string>? teams, string? recipient)
    {
        ArgumentNullException.ThrowIfNull(poolDirs);
        object?[] pools = [.. poolDirs.Select(MsgRows.PoolKey).Distinct(StringComparer.Ordinal)];
        object?[] filter = [.. (teams ?? []).Distinct(StringComparer.Ordinal), recipient];
        return (long)select(ExchangeCountSql(pools.Length, teams is null ? null : filter.Length - 1), [.. pools, .. filter])[0][0]!;
    }

    /// <summary>The statement for <see cref="MessagesWithIds"/> over <paramref name="count"/> ids bound from ?2, in key order.</summary>
    internal static string MessagesWithIdsSql(int count) =>
        $"SELECT {Cols("m")} FROM msg_messages m WHERE m.pool_dir = ?1 AND m.state <> 'archived' AND m.id IN ("
        + string.Join(", ", Enumerable.Range(2, count).Select(static i => "?" + i.ToString(CultureInfo.InvariantCulture)))
        + ") ORDER BY m.id, m.type, m.\"to\", m.slug";

    private static List<MsgFileRow> MessagesWithIdsFrom(Query select, string poolDir, IReadOnlyList<string> ids)
    {
        ArgumentNullException.ThrowIfNull(poolDir);
        ArgumentNullException.ThrowIfNull(ids);
        var rows = new List<MsgFileRow>();
        foreach (var chunk in ids.Distinct(StringComparer.Ordinal).Chunk(PathsPerQuery))
        {
            rows.AddRange(select(MessagesWithIdsSql(chunk.Length), [MsgRows.PoolKey(poolDir), .. chunk]).Select(r => Map(r, 0)));
        }

        return rows;
    }

    private const int PathsPerQuery = 400;

    /// <summary>The statement for <see cref="TeamsByPath"/> over <paramref name="count"/> paths bound from ?1; each path index answers its half.</summary>
    internal static string TeamsByPathSql(int count)
    {
        var args = string.Join(", ", Enumerable.Range(1, count).Select(static i => "?" + i.ToString(CultureInfo.InvariantCulture)));
        return $"SELECT draft_path, export_path, team FROM msg_messages WHERE draft_path IN ({args}) UNION ALL SELECT draft_path, export_path, team FROM msg_messages WHERE export_path IN ({args})";
    }

    private static Dictionary<string, string?> TeamsByPathFrom(Query select, IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var wanted = paths.Distinct(StringComparer.Ordinal).ToList();
        var asked = wanted.ToHashSet(StringComparer.Ordinal);
        var teams = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var chunk in wanted.Chunk(PathsPerQuery))
        {
            foreach (var row in select(TeamsByPathSql(chunk.Length), [.. chunk]))
            {
                foreach (var path in new[] { row[0] as string, row[1] as string })
                {
                    if (path is not null && asked.Contains(path))
                    {
                        teams.TryAdd(path, row[2] as string);
                    }
                }
            }
        }

        return teams;
    }

    public long Version() => Once(VersionFrom);

    public IReadOnlyList<string> ChangedIds(string poolDir, long sinceVersion) => Once(select => ChangedIdsFrom(select, poolDir, sinceVersion));

    public long MessageCount(string poolDir) => Once(select => MessageCountFrom(select, poolDir));

    private static long MessageCountFrom(Query select, string poolDir) =>
        select("SELECT count(*) FROM msg_messages WHERE pool_dir = ?1", [MsgRows.PoolKey(poolDir)]) is { Count: > 0 } rows ? (long)rows[0][0]! : 0;

    private static List<string> ChangedIdsFrom(Query select, string poolDir, long sinceVersion) =>
        [.. select("SELECT DISTINCT id FROM msg_messages WHERE pool_dir = ?1 AND changed_seq > ?2", [MsgRows.PoolKey(poolDir), sinceVersion]).Select(static r => (string)r[0]!)];

    internal static string ArchivedSql { get; } =
        $"SELECT {Cols("m")} FROM msg_messages m WHERE m.pool_dir = ?1 AND m.state = 'archived' ORDER BY m.id, m.type, m.\"to\", m.slug";

    private static List<MsgFileRow> ArchivedFrom(Query select, string poolDir)
    {
        ArgumentNullException.ThrowIfNull(poolDir);
        return [.. select(ArchivedSql, [MsgRows.PoolKey(poolDir)]).Select(r => Map(r, 0))];
    }

    private static List<MsgExchangePair> ExchangesFrom(Query select, IReadOnlyList<string> poolDirs)
    {
        ArgumentNullException.ThrowIfNull(poolDirs);
        var result = new List<MsgExchangePair>();
        foreach (var pool in poolDirs.Select(MsgRows.PoolKey).Distinct(StringComparer.Ordinal))
        {
            var rows = select(ExchangesSql, [pool]);
            foreach (var row in rows)
            {
                var closed = (string?)row[0] == "closed";
                var request = Map(row, 3);
                var response = row[3 + Columns] is null
                    ? null
                    : Map(row, 4 + Columns) with { Closed = closed, ClosedBy = (string?)row[1], ClosedReason = (string?)row[2] };
                result.Add(new MsgExchangePair(request, response));
            }
        }

        return result;
    }

    private static List<MsgFileRow> MessagesFrom(Query select, string poolDir, string? id, string? type)
    {
        ArgumentNullException.ThrowIfNull(poolDir);
        List<object?> args = [MsgRows.PoolKey(poolDir)];
        if (id is not null)
        {
            args.Add(id);
            if (type is not null)
            {
                args.Add(type);
            }
        }

        var rows = select(MessagesSql(id is not null, id is not null && type is not null), [.. args]).Select(r => Map(r, 0));
        return (type is not null && id is null ? rows.Where(r => r.Type == type) : rows).ToList();
    }

    internal static string ExchangesSql { get; } =
        $"SELECT e.state, e.closed_by, e.reason, {Cols("q")}, r.pk, {Cols("r")} FROM msg_exchanges e JOIN msg_messages q ON q.pk = e.request_pk LEFT JOIN msg_messages r ON r.pk = e.response_pk WHERE e.pool_dir = ?1 AND e.state <> 'archived' AND q.state <> 'archived' ORDER BY e.id";

    /// <summary>The statement for a pool's messages, narrowed by id (?2) and, with an id, by type (?3); a type with no id is filtered after the read, since no index leads with it; it returns the rows in the order of the key index.</summary>
    internal static string MessagesSql(bool byId, bool byType)
    {
        var sql = $"SELECT {Cols("m")} FROM msg_messages m WHERE m.pool_dir = ?1 AND m.state <> 'archived'";
        var next = 2;
        if (byId)
        {
            sql += " AND m.id = ?" + (next++).ToString(CultureInfo.InvariantCulture);
        }

        if (byType)
        {
            sql += " AND m.type = ?" + next.ToString(CultureInfo.InvariantCulture);
        }

        return sql + " ORDER BY m.id, m.type, m.\"to\", m.slug";
    }

    /// <summary>
    /// A pool's requests to ?2 with slug ?4 opened at or after ?3 (ms), over the <c>msg_exchanges_to_opened</c> index, in
    /// <see cref="MessagesSql"/>'s order: the candidates of a create's retry check without listing the pool. A request whose created
    /// header gave no opened_ms is a candidate too: the caller's own created check decides it, as a listing would have.
    /// </summary>
    internal static string RequestsOpenedSinceSql { get; } =
        $"SELECT {Cols("q")} FROM msg_exchanges e JOIN msg_messages q ON q.pk = e.request_pk WHERE e.pool_dir = ?1 AND e.\"to\" = ?2 AND (e.opened_ms >= ?3 OR e.opened_ms IS NULL) "
        + "AND q.slug = ?4 AND q.type = 'request' AND q.state <> 'archived' ORDER BY q.id, q.type, q.\"to\", q.slug";

    private static List<MsgFileRow> RequestsOpenedSinceFrom(Query select, string poolDir, string to, string slug, long openedSinceMs) =>
        [.. select(RequestsOpenedSinceSql, [MsgRows.PoolKey(poolDir), to, openedSinceMs, slug]).Select(r => Map(r, 0))];

    internal static string ByPathSql { get; } =
        $"SELECT {Cols("m")}, b.bytes, b.sealed_at_ms FROM msg_messages m LEFT JOIN msg_bodies b ON b.msg_pk = m.pk AND b.rev = m.body_rev WHERE m.draft_path = ?1 OR m.export_path = ?1 LIMIT 1";

    internal static string VersionSql => "SELECT seq FROM domain_seq WHERE domain = ?1";

    private static MsgFileRow? ByPathFrom(Query select, string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        // a path read back from a row is already its key, so it is tried as written first; resolving it on disk lists every parent
        // directory, which made each per-exchange lookup cost milliseconds
        var rows = select(ByPathSql, [path]);
        if (rows.Count == 0 && MsgRows.PathKey(path) is var key && key != path)
        {
            rows = select(ByPathSql, [key]);
        }

        return rows.Count == 0 ? null : Map(rows[0], 0) with { Bytes = rows[0][Columns] as byte[], SealedAtMs = rows[0][Columns + 1] as long? };
    }

    private static long VersionFrom(Query select) =>
        select(VersionSql, [Domain]) is { Count: > 0 } rows ? (long)rows[0][0]! : 0;

    private static MsgFileRow Map(object?[] r, int at)
    {
        string? S(int i) => r[at + i] as string;
        var path = S(13) ?? S(14) ?? string.Empty;
        var state = S(12);
        var archived = state == "archived";
        return new MsgFileRow(
            PoolDir: S(0)!,
            Name: NameOf(path),
            Id: S(1)!,
            Type: S(2)!,
            To: S(3) ?? string.Empty,
            Slug: S(4) ?? string.Empty,
            From: S(5),
            ToName: S(6),
            FromName: S(7),
            Team: S(8),
            Parent: S(9),
            Created: S(10),
            CreatedMs: r[at + 11] as long?,
            Archived: archived,
            State: r[at + 15] is null || r[at + 12] as string == MsgRows.Draft ? MsgRows.Draft : MsgRows.Sealed,
            Bytes: null,
            Path: path);
    }

    public IMsgScope Scope() => new Scoped(ahHome, pool);

    private T Once<T>(Func<Query, T> work)
    {
        using var reader = OpenReader();
        return work((sql, args) => Run(reader, sql, args));
    }

    private IStateDbReader OpenReader()
    {
        // a database never created holds no messages yet (ah-msgs-db-only §13.5); one whose marker remains existed and was lost, an error
        if (StateDbAuthority.LostDatabase(ahHome) is { } lost)
        {
            throw new InvalidOperationException($"the state database cannot be read: {lost}");
        }

        if (StateDbAuthority.NeverCreated(ahHome))
        {
            return NoRows.Instance;
        }

        var opened = StateDbReader.Open(ahHome, pool);
        return opened.Reader ?? throw new InvalidOperationException($"the state database cannot be read: {opened.Message}");
    }

    /// <summary>The reader of a database that was never created: every query answers no rows.</summary>
    private sealed class NoRows : IStateDbReader
    {
        internal static readonly NoRows Instance = new();

        public IReadOnlyList<object?[]> Query(string sql, params object?[] args) => [];

        public void Dispose()
        {
        }
    }

    private static IReadOnlyList<object?[]> Run(IStateDbReader reader, string sql, object?[] args)
    {
        try
        {
            return reader.Query(sql, args);
        }
        catch (SqliteException error)
        {
            throw new InvalidOperationException($"the state database cannot be read: {error.Message}", error);
        }
    }

    /// <summary>The same reads over one connection, opened by the first of them and closed on dispose.</summary>
    private sealed class Scoped(string ahHome, StateDbReadPool? pool) : IMsgScope
    {
        private readonly StateDbMsgReader _reads = new(ahHome, pool);
        private readonly object _gate = new();
        private IStateDbReader? _reader;

        private T Read<T>(Func<Query, T> work)
        {
            lock (_gate)
            {
                return work((sql, args) => Run(_reader ??= _reads.OpenReader(), sql, args));
            }
        }

        public IReadOnlyList<MsgExchangePair> Exchanges(IReadOnlyList<string> poolDirs) => Read(select => ExchangesFrom(select, poolDirs));

        public IReadOnlyList<MsgFileRow> Messages(string poolDir, string? id = null, string? type = null) => Read(select => MessagesFrom(select, poolDir, id, type));

        public IReadOnlyList<string> MessageNames(string poolDir) => Read(select => NamesFrom(select, poolDir));

        public IReadOnlyList<MsgFileRow> ArchivedMessages(string poolDir) => Read(select => ArchivedFrom(select, poolDir));

        public IReadOnlyList<MsgFileRow> MessagesById(string id, string type) => Read(select => ByIdFrom(select, id, type));

        public IReadOnlyList<MsgFileRow>? RequestsOpenedSince(string poolDir, string recipient, string slug, long openedSinceMs) =>
            Read(select => RequestsOpenedSinceFrom(select, poolDir, recipient, slug, openedSinceMs));

        public IReadOnlyList<MsgFileRow> LiveMessages(string poolDir, long answeredSinceMs) => Read(select => LiveFrom(select, poolDir, answeredSinceMs));

        public IReadOnlyList<string> RequestIdsWithSlug(string poolDir, string slug) => Read(select => SlugIdsFrom(select, poolDir, slug));

        public IReadOnlyList<(string Id, string Slug)> ExchangeSlugs(string poolDir) => Read(select => ExchangeSlugsFrom(select, poolDir));

        public IReadOnlyList<string> RunSlugs(string poolDir, string? team, long openedSinceMs) => Read(select => RunSlugsFrom(select, poolDir, team, openedSinceMs));

        public IReadOnlyList<string> CharterCandidateIds(string poolDir, string team, IReadOnlyList<string> tos, long openedSinceMs) =>
            Read(select => CharterCandidatesFrom(select, poolDir, team, tos, openedSinceMs));

        public bool AnyAnswered(string poolDir, string recipient) => Read(select => AnyAnsweredFrom(select, poolDir, recipient));

        public IReadOnlyList<string> ClosedIdsAnsweredBefore(string poolDir, long answeredBeforeMs) => Read(select => ClosedIdsFrom(select, poolDir, answeredBeforeMs));

        public IReadOnlyDictionary<string, string?> TeamsByPath(IReadOnlyList<string> paths) => Read(select => TeamsByPathFrom(select, paths));

        public IReadOnlyList<MsgFileRow> MessagesWithIds(string poolDir, IReadOnlyList<string> ids) => Read(select => MessagesWithIdsFrom(select, poolDir, ids));

        public IReadOnlyList<(string Id, string To, string? Team, string Pool)> ExchangeRowsBefore(
            IReadOnlyList<string> poolDirs, string? beforeId, int count, IReadOnlyList<string>? teams, string? recipient) =>
            Read(select => ExchangeRowsBeforeFrom(select, poolDirs, beforeId, count, teams, recipient));

        public long ExchangeCount(IReadOnlyList<string> poolDirs, IReadOnlyList<string>? teams, string? recipient) => Read(select => ExchangeCountFrom(select, poolDirs, teams, recipient));

        public IReadOnlyList<string> RequestDescendants(string poolDir, IReadOnlyList<string> ids) => Read(select => RequestDescendantsFrom(select, poolDir, ids));

        public MsgFileRow? ByPath(string path) => Read(select => ByPathFrom(select, path));

        public IReadOnlyList<MsgFileRow>? MessagesWithBodies(string poolDir, string id) => Read(select => WithBodiesFrom(select, poolDir, id));

        public long Version() => Read(VersionFrom);

        public IReadOnlyList<string> ChangedIds(string poolDir, long sinceVersion) => Read(select => ChangedIdsFrom(select, poolDir, sinceVersion));

        public long MessageCount(string poolDir) => Read(select => MessageCountFrom(select, poolDir));

        public IMsgScope Scope() => this;

        public void Dispose()
        {
            lock (_gate)
            {
                _reader?.Dispose();
                _reader = null;
            }
        }
    }
}
