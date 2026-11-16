using System.Text;
using Ah.Core.Json;

namespace Ah.StateDb;

/// <summary>
/// The peer log read from <c>peer_records</c>. The database narrows by <c>type</c>, by the byte range of <c>peer_key</c> for a session
/// by <c>from_name</c>, which holds the JS string of the member and is NULL when the row has none, so the value
/// <c>undefined</c> (what a missing member reads as) is not pushed down, and by <c>request_id</c>, which holds the member only when it
/// is text, so only ids that <see cref="PeerRows.RequestIdColumnAnswers"/> accepts are pushed down, and then the types are left to the
/// in-memory filter so that the request index is the one the planner can use;
/// every rule about what a row means (the key, latest by key, the filter's member tests) is <see cref="PeerRows"/>, run over the
/// text of the rows, so the answers are those of the file. Each call reads through its own read-only connection.
/// </summary>
internal sealed class StateDbPeerRecords(string ahHome) : IPeerRecordSource
{
    private const string Domain = "peer-records";

    /// <summary>More request ids than this are not sent to SQLite, whose bound-parameter limit they could exceed; the in-memory filter then does the work.</summary>
    private const int MaxPushedRequestIds = 900;

    /// <summary>The statement that reads every row, in log order.</summary>
    internal const string ReadAllSql = "SELECT raw FROM peer_records ORDER BY seq";

    public IReadOnlyList<JsonValue> ReadAll() => Select(ReadAllSql).Select(r => Parse(r[0])).ToList();

    private readonly object _heldGate = new();
    private readonly Dictionary<string, (long Seq, List<JsonValue> Rows)> _held = new(StringComparer.Ordinal);

    public IReadOnlyList<JsonValue> Rows(PeerQuery query)
    {
        if (query is { Field: null, Value: null, TaskPrefix: null, Values: null, Types: { Count: > 0 } types })
        {
            return HeldOf(types, query);
        }

        var (sql, args) = RowsQuery(query);
        return Select(sql, args).Select(r => Parse(r[0])).Where(r => PeerRows.Matches(r, query)).ToList();
    }

    /// <summary>The statement that reads the candidate rows of <paramref name="query"/>, and its arguments.</summary>
    internal static (string Sql, object?[] Args) RowsQuery(PeerQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        List<string> where = [];
        List<object?> args = [];
        var requestIds = query.Field == "request_id" ? query.Values ?? (query.Value is { } one ? new HashSet<string> { one } : null) : null;
        var pushRequestIds = requestIds is { Count: > 0 and <= MaxPushedRequestIds } && requestIds.All(PeerRows.RequestIdColumnAnswers);
        if (query.Types is { } types && !pushRequestIds)
        {
            where.Add("type IN (" + string.Join(',', types.Select((_, i) => "?" + (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture))) + ")");
            args.AddRange(types);
        }

        if (pushRequestIds)
        {
            where.Add("request_id IN (" + string.Join(',', requestIds!.Select((_, i) => "?" + (args.Count + i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture))) + ")");
            args.AddRange(requestIds!);
        }

        if (query.Field == "session_id" && query.Values is null)
        {
            var (low, high) = PeerRows.KeyRangeOf(JsonValue.FromString(query.Value ?? string.Empty));
            where.Add($"peer_key >= ?{args.Count + 1} AND peer_key < ?{args.Count + 2}");
            args.Add(low);
            args.Add(high);
        }

        if (query.Field == "from_name" && query.Values is null && query.Value is { } name && name != "undefined")
        {
            where.Add($"from_name = ?{args.Count + 1}");
            args.Add(name);
        }

        return ("SELECT raw FROM peer_records" + (where.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", where)) + " ORDER BY seq", [.. args]);
    }

    /// <summary>The statement that reads the candidate rows of <see cref="LatestByKeyFor"/>: the untyped rows of a key range, in log order.</summary>
    internal const string LatestByKeySql = "SELECT raw FROM peer_records WHERE type_truthy = 0 AND peer_key >= ?1 AND peer_key < ?2 ORDER BY seq";

    public IReadOnlyList<JsonValue> LatestByKeyFor(JsonValue? sessionId)
    {
        var (low, high) = PeerRows.KeyRangeOf(sessionId);
        return PeerRows.LatestByKey(Select(LatestByKeySql, low, high).Select(r => Parse(r[0])));
    }

    public IReadOnlyList<PeerRowAt> Since(long cursor) =>
        Select("SELECT seq, raw FROM peer_records WHERE seq > ?1 ORDER BY seq", cursor).Select(r => new PeerRowAt((long)r[0]!, Parse(r[1]))).ToList();

    public long Version() =>
        Select("SELECT seq FROM domain_seq WHERE domain = ?1", Domain) is { Count: > 0 } rows ? (long)rows[0][0]! : 0;

    public long Count() => (long)Select("SELECT count(*) FROM peer_records")[0][0]!;

    /// <summary>Drops every held row set, so the next read of each type set reads it in full (the on-demand rebuild).</summary>
    public void ForgetHeld()
    {
        lock (_heldGate)
        {
            _held.Clear();
        }
    }

    /// <summary>
    /// The rows of type set <paramref name="types"/>, in log order, held and shared (b459 step 13): a read parses only the rows after the
    /// held seq, and the type set's count, read in the same statement, guards the hold. Held plus new rows that differ from the count mean
    /// a row was deleted or replaced, and the set is read again in full. The rows that pass <paramref name="query"/> come back in a new
    /// list; callers must not change a row.
    /// </summary>
    private List<JsonValue> HeldOf(IReadOnlySet<string> types, PeerQuery query)
    {
        var ordered = types.Order(StringComparer.Ordinal).ToList();
        var key = string.Join('\n', ordered);
        lock (_heldGate)
        {
            var held = _held.TryGetValue(key, out var h) ? h : ((long Seq, List<JsonValue> Rows)?)null;
            var (count, fresh) = After(ordered, held?.Seq ?? 0);
            List<JsonValue> rows;
            if (held is { } kept && kept.Rows.Count + fresh.Count == count)
            {
                rows = kept.Rows;
                rows.AddRange(fresh.Select(static f => f.Row));
            }
            else
            {
                (_, fresh) = After(ordered, 0);
                rows = [.. fresh.Select(static f => f.Row)];
            }

            var seq = fresh.Count > 0 ? fresh[^1].Seq : held?.Seq ?? 0;
            _held[key] = (seq, rows);
            // filtered under the lock: a later read appends to the held list
            return [.. rows.Where(r => PeerRows.Matches(r, query))];
        }
    }

    /// <summary>The count of <paramref name="types"/>' rows and those after <paramref name="seq"/>, in one statement and so one snapshot.</summary>
    private (long Count, List<PeerRowAt> Rows) After(List<string> types, long seq)
    {
        var inList = "type IN (" + string.Join(',', types.Select((_, i) => "?" + (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture))) + ")";
        var seqArg = "?" + (types.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var read = Select(
            $"SELECT -1, NULL, count(*) FROM peer_records WHERE {inList} UNION ALL SELECT seq, raw, 0 FROM peer_records WHERE {inList} AND seq > {seqArg} ORDER BY 1",
            [.. types, seq]);
        var count = read.Count > 0 ? (long)read[0][2]! : 0;
        return (count, [.. read.Skip(1).Select(r => new PeerRowAt((long)r[0]!, Parse(r[1])))]);
    }

    private static JsonValue Parse(object? raw) => JsonText.Parse(Encoding.UTF8.GetString((byte[])raw!));

    private IReadOnlyList<object?[]> Select(string sql, params object?[] args)
    {
        // a database never created holds no records yet (ah-msgs-db-only §13.5); one whose marker remains was lost, and Open says so
        if (StateDbAuthority.NeverCreated(ahHome))
        {
            return [];
        }

        var opened = StateDbReader.Open(ahHome);
        if (opened.Reader is not { } reader)
        {
            throw new InvalidOperationException($"the state database cannot be read: {opened.Message}");
        }

        using (reader)
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
    }
}
