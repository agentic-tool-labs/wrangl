namespace Ah.Core.Json;

/// <summary>
/// Reads of the message tables that the message files answer today. A row is a <see cref="MsgFileRow"/>: <c>Path</c> is the draft path
/// of a draft and the export path of any other message, <c>Name</c> is that path's file name, and <c>Bytes</c> is null unless the
/// call says it loads the body. Archived messages and exchanges are never returned by <see cref="Exchanges"/> or <see cref="Messages"/>,
/// as the files of <c>msgs/archive</c> are not listed today. What an exchange means (open, charter, report status) is not decided here:
/// the caller evaluates its one predicate over these facts.
/// </summary>
public interface IMsgSource
{
    /// <summary>
    /// The exchanges of <paramref name="poolDirs"/>: one pair per (pool, id) that has a request, in the order of the pools given and
    /// then by id, without the archived ones. A response row carries <c>Closed</c>, <c>ClosedBy</c> and <c>ClosedReason</c> from the
    /// exchange row; the database does not store <c>withdrawn</c> apart from <c>closed</c>, so <c>Withdrawn</c> is false.
    /// </summary>
    IReadOnlyList<MsgExchangePair> Exchanges(IReadOnlyList<string> poolDirs);

    /// <summary>
    /// The messages of one pool that are not archived, of any type: those with <paramref name="id"/> when it is given and those of
    /// <paramref name="type"/> when it is given, by id, type, recipient and slug (the order of the key index).
    /// </summary>
    IReadOnlyList<MsgFileRow> Messages(string poolDir, string? id = null, string? type = null);

    /// <summary>The <c>Name</c> of each of <see cref="Messages"/>' rows for <paramref name="poolDir"/>, in that order; the store reads only the paths (ah2-52).</summary>
    IReadOnlyList<string> MessageNames(string poolDir) => [.. Messages(poolDir).Select(static m => m.Name)];

    /// <summary>
    /// The messages with <paramref name="id"/> and <paramref name="type"/> in every pool, archived or not, by pool, recipient and slug:
    /// the one lookup not scoped to a pool, which a message token naming only an id resolves through.
    /// </summary>
    IReadOnlyList<MsgFileRow> MessagesById(string id, string type);

    /// <summary>
    /// <see cref="Messages"/> narrowed to the exchanges that can still be open (<c>live</c>) or whose response was sealed at or after
    /// <paramref name="answeredSinceMs"/>; same order. A source that cannot narrow answers every message, which is never wrong.
    /// </summary>
    IReadOnlyList<MsgFileRow> LiveMessages(string poolDir, long answeredSinceMs) => Messages(poolDir);

    /// <summary>
    /// The pool's requests to <paramref name="recipient"/> with <paramref name="slug"/> whose exchange opened at or after
    /// <paramref name="openedSinceMs"/>, in <see cref="Messages"/>'s order; null when the source cannot narrow (the caller lists the pool).
    /// </summary>
    IReadOnlyList<MsgFileRow>? RequestsOpenedSince(string poolDir, string recipient, string slug, long openedSinceMs) => null;

    /// <summary>The id and request slug of each exchange of the pool that is not archived, as <see cref="Exchanges"/> pairs them.</summary>
    IReadOnlyList<(string Id, string Slug)> ExchangeSlugs(string poolDir) => [.. Exchanges([poolDir]).Select(static p => (p.Request.Id, p.Request.Slug))];

    /// <summary>The ids of the pool's requests whose slug is <paramref name="slug"/>, not archived.</summary>
    IReadOnlyList<string> RequestIdsWithSlug(string poolDir, string slug) =>
        [.. Messages(poolDir, type: "request").Where(m => m.Slug == slug).Select(m => m.Id).Distinct(StringComparer.Ordinal)];

    /// <summary>
    /// The slugs of the pool's requests whose <c>team</c> is <paramref name="team"/> and that were created at or after
    /// <paramref name="openedSinceMs"/>, not archived: the rounds a pipeline run can have.
    /// </summary>
    IReadOnlyList<string> RunSlugs(string poolDir, string? team, long openedSinceMs) =>
        [.. Messages(poolDir, type: "request").Where(m => m.Team == team && m.CreatedMs >= openedSinceMs).Select(m => m.Slug).Distinct(StringComparer.Ordinal)];

    /// <summary>
    /// The ids of the pool's requests that can be a running lane's charter, not archived, ascending: written in <paramref name="team"/> or in
    /// no team, addressed to one of <paramref name="tos"/>, and created at or after <paramref name="openedSinceMs"/>.
    /// A superset; the caller's charter rule decides.
    /// </summary>
    IReadOnlyList<string> CharterCandidateIds(string poolDir, string team, IReadOnlyList<string> tos, long openedSinceMs) =>
        [.. Messages(poolDir, type: "request")
            .Where(m => (m.Team == team || string.IsNullOrEmpty(m.Team)) && tos.Contains(m.To) && m.CreatedMs >= openedSinceMs)
            .Select(static m => m.Id).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    /// <summary>Prune's closed set in one pool: ids no longer live and not answered since <paramref name="answeredBeforeMs"/>; empty (keep all) where the source cannot say.</summary>
    IReadOnlyList<string> ClosedIdsAnsweredBefore(string poolDir, long answeredBeforeMs) => [];

    /// <summary>Whether the pool has an exchange, not archived, whose request is addressed to <paramref name="recipient"/> and that has a response.</summary>
    bool AnyAnswered(string poolDir, string recipient) => Exchanges([poolDir]).Any(p => p.Request.To == recipient && p.Response is not null);

    /// <summary>
    /// The <paramref name="count"/> newest exchange ids of <paramref name="poolDirs"/> below <paramref name="beforeId"/> (all when null), not
    /// archived, with a request row of stored team in <paramref name="teams"/> (any when null; empty text = no team) and addressed to
    /// <paramref name="recipient"/> (any when null), newest first, with every pool's request row for them: its id, recipient, stored team and pool key.
    /// </summary>
    IReadOnlyList<(string Id, string To, string? Team, string Pool)> ExchangeRowsBefore(
        IReadOnlyList<string> poolDirs, string? beforeId, int count, IReadOnlyList<string>? teams, string? recipient)
    {
        ArgumentNullException.ThrowIfNull(poolDirs);
        var rows = Exchanges(poolDirs).Select(static p => (p.Request.Id, p.Request.To, p.Request.Team, p.Request.PoolDir)).ToList();
        var ids = rows.Where(r => (teams is null || teams.Contains(r.Team ?? string.Empty)) && (recipient is null || r.To == recipient)).Select(static r => r.Id)
            .Where(id => beforeId is null || string.CompareOrdinal(id, beforeId) < 0).Distinct(StringComparer.Ordinal)
            .OrderDescending(StringComparer.Ordinal).Take(count).ToHashSet(StringComparer.Ordinal);
        return [.. rows.Where(r => ids.Contains(r.Id)).OrderByDescending(static r => r.Id, StringComparer.Ordinal)];
    }

    /// <summary>How many distinct exchange ids <paramref name="poolDirs"/> have, not archived, with a request row matching <paramref name="teams"/> and <paramref name="recipient"/> as in <see cref="ExchangeRowsBefore"/>.</summary>
    long ExchangeCount(IReadOnlyList<string> poolDirs, IReadOnlyList<string>? teams, string? recipient) =>
        Exchanges(poolDirs).Where(p => (teams is null || teams.Contains(p.Request.Team ?? string.Empty)) && (recipient is null || p.Request.To == recipient))
            .Select(static p => p.Request.Id).Distinct(StringComparer.Ordinal).LongCount();

    /// <summary>The ids of the pool's requests, not archived, whose parent chain reaches one of <paramref name="ids"/>, those excluded.</summary>
    IReadOnlyList<string> RequestDescendants(string poolDir, IReadOnlyList<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var requests = Messages(poolDir, type: "request");
        var found = new HashSet<string>(ids, StringComparer.Ordinal);
        var result = new List<string>();
        for (var grew = true; grew;)
        {
            grew = false;
            foreach (var m in requests.Where(m => m.Parent is { } p && found.Contains(p) && !found.Contains(m.Id)))
            {
                _ = found.Add(m.Id);
                result.Add(m.Id);
                grew = true;
            }
        }

        return result;
    }

    /// <summary><see cref="Messages"/> of the pool with any of <paramref name="ids"/>, all types: per id in the order given, each id's rows in key order.</summary>
    IReadOnlyList<MsgFileRow> MessagesWithIds(string poolDir, IReadOnlyList<string> ids) => [.. ids.SelectMany(id => Messages(poolDir, id))];

    /// <summary>The stored <c>team</c> of each message whose draft path or export path is one of <paramref name="paths"/>, by that path; a path with no message is absent.</summary>
    IReadOnlyDictionary<string, string?> TeamsByPath(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var teams = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            if (!teams.ContainsKey(path) && ByPath(path) is { } row)
            {
                teams[path] = row.Team;
            }
        }

        return teams;
    }

    /// <summary>
    /// The archived messages of one pool, of any type, by id, type, recipient and slug: what <see cref="Messages"/> leaves out. A row's
    /// <c>Path</c> is the path the archive moved its file to, under <c>msgs/archive</c>.
    /// </summary>
    IReadOnlyList<MsgFileRow> ArchivedMessages(string poolDir);

    /// <summary>
    /// The message whose draft path or export path is <paramref name="path"/>, archived or not, with the bytes of its current revision
    /// when it is sealed (null for a draft: its file is its record); null when no message has that path.
    /// </summary>
    MsgFileRow? ByPath(string path);

    /// <summary>
    /// <see cref="Messages"/> for one id, each row with its stored body as <see cref="ByPath"/> reads it (ah2-1: one read where a
    /// listing and a read by path per message were made). Null when the source cannot; the caller then reads each message by path.
    /// </summary>
    IReadOnlyList<MsgFileRow>? MessagesWithBodies(string poolDir, string id) => null;

    /// <summary>The change token of the msgs domain, <c>domain_seq['msgs']</c>: it differs after any write; 0 when nothing was written.</summary>
    long Version();

    /// <summary>The ids of the messages of <paramref name="poolDir"/> a write changed after <see cref="Version"/> was <paramref name="sinceVersion"/>.</summary>
    IReadOnlyList<string> ChangedIds(string poolDir, long sinceVersion);

    /// <summary>How many messages <paramref name="poolDir"/> holds, archived ones included: a delete moves it where <see cref="ChangedIds"/> shows nothing.</summary>
    long MessageCount(string poolDir) => Messages(poolDir).Count + ArchivedMessages(poolDir).Count;

    /// <summary>A source whose calls share one connection until it is disposed, for a reader that makes many calls (a listing reads the pool, then each response).</summary>
    IMsgScope Scope();
}

/// <summary>A <see cref="IMsgSource"/> that holds its connection until disposed. It opens the connection on the first call, so a scope that is never used costs nothing.</summary>
public interface IMsgScope : IMsgSource, IDisposable
{
}
