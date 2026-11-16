using System.Security.Cryptography;
using Ah.Core.Text;

namespace Ah.Core.Json;

/// <summary>
/// One message file as the database stores it. A draft has no <paramref name="Bytes"/>: its file is its record. A sealed message carries
/// the file's bytes, which become revision 1.
/// </summary>
/// <param name="PoolDir">The absolute hierarchy directory that holds the <c>msgs</c> directory.</param>
/// <param name="Name">The file name.</param>
/// <param name="Id">The exchange id from the file name.</param>
/// <param name="Type"><c>request</c>, <c>response</c>, <c>note</c> or <c>link-note</c>.</param>
/// <param name="To">The recipient from the file name; empty when the name has none.</param>
/// <param name="Slug">The slug from the file name; empty when the name has none.</param>
/// <param name="From">The <c>from</c> header.</param>
/// <param name="ToName">The <c>to_name</c> header.</param>
/// <param name="FromName">The <c>from_name</c> header.</param>
/// <param name="Team">The <c>team</c> header.</param>
/// <param name="Parent">The <c>parent</c> header.</param>
/// <param name="Created">The <c>created</c> header text.</param>
/// <param name="CreatedMs">The <c>created</c> header in Unix milliseconds; null when it does not parse.</param>
/// <param name="Archived">Whether the file sits in <c>msgs/archive</c>.</param>
/// <param name="State"><see cref="MsgRows.Draft"/> or <see cref="MsgRows.Sealed"/>.</param>
/// <param name="Bytes">The file's bytes for a sealed message; null for a draft.</param>
/// <param name="Path">The absolute path of the file.</param>
/// <param name="Closed">Whether the <c>closed</c> header is the text <c>true</c>; it matters on a response.</param>
/// <param name="Withdrawn">Whether the <c>withdrawn</c> header is the text <c>true</c>; it matters on a response.</param>
/// <param name="ClosedBy">The <c>closed_by</c> header when it is text.</param>
/// <param name="ClosedReason">The <c>closed_reason</c> header when it is text.</param>
/// <param name="SealedAtMs">When the current body was sealed, in Unix milliseconds; null for a draft or a row not read by path.</param>
public sealed record MsgFileRow(
    string PoolDir,
    string Name,
    string Id,
    string Type,
    string To,
    string Slug,
    string? From,
    string? ToName,
    string? FromName,
    string? Team,
    string? Parent,
    string? Created,
    long? CreatedMs,
    bool Archived,
    string State,
    byte[]? Bytes,
    string Path,
    bool Closed = false,
    bool Withdrawn = false,
    string? ClosedBy = null,
    string? ClosedReason = null,
    long? SealedAtMs = null);

/// <summary>The rows of one pool and the counts its import manifest records.</summary>
/// <param name="PoolDir">The pool's hierarchy directory.</param>
/// <param name="Rows">One row per parseable file.</param>
/// <param name="Skipped">The names that did not parse, which are left out of <paramref name="Rows"/>.</param>
public sealed record MsgPoolBatch(string PoolDir, IReadOnlyList<MsgFileRow> Rows, IReadOnlyList<string> Skipped)
{
    /// <summary>The number of parseable files.</summary>
    public int Files => Rows.Count;

    /// <summary>The bytes of the sealed bodies.</summary>
    public long TotalBytes => Rows.Sum(r => (long)(r.Bytes?.Length ?? 0));

    /// <summary>The number of drafts.</summary>
    public int Drafts => Rows.Count(r => r.State == MsgRows.Draft);
}

/// <summary>A request with its response, when it has one: the two files of one exchange.</summary>
public sealed record MsgExchangePair(MsgFileRow Request, MsgFileRow? Response);

/// <summary>The shape of the message tables that every writer shares, so the importer and the later write path cannot disagree on it.</summary>
public static class MsgRows
{
    /// <summary>The state of a message whose file is its record.</summary>
    public const string Draft = "draft";

    /// <summary>The state of a message whose bytes are stored.</summary>
    public const string Sealed = "sealed";

    /// <summary>The largest body a sealed message can hold; a larger file stays a draft.</summary>
    public const int BodyCap = 262144;

    /// <summary>The statement that stores one message; <see cref="MessageArgs"/> gives its arguments. A message whose key is already stored is left out.</summary>
    public static string InsertMessageSql =>
        "INSERT OR IGNORE INTO msg_messages(pool_dir, id, type, \"to\", \"from\", to_name, from_name, slug, team, parent, created, created_ms, state, draft_path, export_path, body_rev, export_sha) VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12, ?13, ?14, ?15, ?16, ?17)";

    /// <summary>
    /// The arguments of <see cref="InsertMessageSql"/>. An archived message is stored as <c>archived</c> whether or not it has bytes.
    /// A draft keeps its path in <c>draft_path</c> and has no revision or export hash; any other message keeps it in <c>export_path</c>
    /// with revision 1 and the hash of its bytes.
    /// </summary>
    public static object?[] MessageArgs(MsgFileRow r)
    {
        var hasBody = r.Bytes is not null;
        return
        [
            r.PoolDir, r.Id, r.Type, r.To, r.From, r.ToName, r.FromName, r.Slug, r.Team, r.Parent, r.Created, r.CreatedMs,
            r.Archived ? "archived" : r.State,
            hasBody ? null : r.Path,
            hasBody ? r.Path : null,
            hasBody ? 1L : null,
            hasBody ? Sha256Hex(r.Bytes!) : null,
        ];
    }

    /// <summary>The statement that finds the row of a message by its key; the arguments are <see cref="KeyArgs"/>.</summary>
    public static string SelectPkSql =>
        "SELECT pk FROM msg_messages WHERE pool_dir = ?1 AND id = ?2 AND type = ?3 AND \"to\" = ?4 AND slug = ?5";

    /// <summary>The arguments of <see cref="SelectPkSql"/>.</summary>
    public static object?[] KeyArgs(MsgFileRow r) => [r.PoolDir, r.Id, r.Type, r.To, r.Slug];

    /// <summary>
    /// The statement that turns a stored message that has no body yet into a sealed one: its state, its export path and revision 1 with the hash of its
    /// bytes, and no draft path. A message that already has a body is left as it is. It runs before <see cref="InsertBodySql"/> in the same transaction,
    /// so a message is never left as a draft with a body.
    /// </summary>
    public static string UpgradeToSealedSql =>
        "UPDATE msg_messages SET state = ?2, draft_path = NULL, export_path = ?3, body_rev = 1, export_sha = ?4 WHERE pk = ?1 AND body_rev IS NULL";

    /// <summary>The arguments of <see cref="UpgradeToSealedSql"/> for a message with a body; an archived message stays <c>archived</c>.</summary>
    public static object?[] UpgradeArgs(long msgPk, MsgFileRow r) => [msgPk, r.Archived ? "archived" : r.State, r.Path, Sha256Hex(r.Bytes!)];

    /// <summary>The statement that stores revision 1 of a sealed message; <see cref="BodyArgs"/> gives its arguments.</summary>
    public static string InsertBodySql =>
        "INSERT OR IGNORE INTO msg_bodies(msg_pk, rev, bytes, sha256, sealed_at_ms, sealed_by) VALUES (?1, 1, ?2, ?3, ?4, NULL)";

    /// <summary>The arguments of <see cref="InsertBodySql"/> for a message with a body.</summary>
    public static object?[] BodyArgs(long msgPk, MsgFileRow r, long nowMs) => [msgPk, r.Bytes!, Sha256Hex(r.Bytes!), nowMs];

    /// <summary>The statement that stores one exchange; <see cref="ExchangeArgs"/> gives its arguments. An exchange already stored is left as it is.</summary>
    public static string InsertExchangeSql =>
        "INSERT OR IGNORE INTO msg_exchanges(pool_dir, id, request_pk, response_pk, state, opened_ms, closed_ms, closed_by, reason, team, \"to\", \"from\", live, answered_ms) VALUES (?1, ?2, ?3, ?4, ?5, ?6, NULL, ?7, ?8, ?9, ?10, ?11, ?12, ?13)";

    /// <summary>
    /// The arguments of <see cref="InsertExchangeSql"/>: the state is <c>archived</c> for an archived request, else <c>closed</c> when the
    /// response is closed or withdrawn, else <c>answered</c> when a response exists, else <c>open</c>. The closer and the reason are the
    /// response's <c>closed_by</c> and <c>closed_reason</c>. Then <see cref="Live(MsgExchangePair)"/> as 1 or 0, and <paramref name="answeredMs"/>, when the
    /// response's current body was sealed.
    /// </summary>
    public static object?[] ExchangeArgs(MsgExchangePair p, long requestPk, long? responsePk, long? answeredMs) =>
    [
        p.Request.PoolDir, p.Request.Id, requestPk, responsePk,
        p.Request.Archived ? "archived" : p.Response is { Closed: true } or { Withdrawn: true } ? "closed" : p.Response is null ? "open" : "answered",
        p.Request.CreatedMs, p.Response?.ClosedBy, p.Response?.ClosedReason, p.Request.Team, p.Request.To, p.Request.From,
        Live(p) ? 1L : 0L, answeredMs,
    ];

    /// <summary>The largest response body a report status is read from; a larger one has a report.</summary>
    public const long ReportBytes = 4096;

    /// <summary>
    /// <c>reported</c> when a response's text holds authored content, <c>no-report</c> when it holds only its skeleton, and
    /// <c>malformed-report</c> when its front matter is missing or names another id.
    /// </summary>
    public static string ReportStatusOf(string text, string id)
    {
        var fm = MessageFront.ParseFrontmatter(text);
        if (fm is null || fm.Value.Fields.Get("id") is not { IsText: true } fid || fid.AsString() != id)
        {
            return "malformed-report";
        }

        return MessageFront.HasAuthoredContent(text) ? "reported" : "no-report";
    }

    /// <summary>
    /// Whether an exchange can still be open for a reader (<c>msg_exchanges.live</c>). It is a superset of open: false only where the
    /// reader's open test is false whatever the files hold. A response with no stored body (a draft) lives in its file, so it stays true;
    /// a stored body counts as no report only within <see cref="ReportBytes"/>, as the reader's test does.
    /// Known gap: it is per pool, so a request and authored response in pool A (live 0) plus a placeholder response with the same id in a
    /// pool B listed first reads open in the full list but is hidden by the live scope.
    /// </summary>
    public static bool Live(MsgExchangePair p)
    {
        ArgumentNullException.ThrowIfNull(p);
        return Live(p.Request.Archived, p.Request.To, p.Request.Id, p.Response is not null, p.Response?.Bytes);
    }

    /// <summary><see cref="Live(MsgExchangePair)"/> from the facts it reads: <paramref name="responseBody"/> is the response's stored body, null for a draft.</summary>
    public static bool Live(bool requestArchived, string requestTo, string id, bool hasResponse, byte[]? responseBody)
    {
        if (requestArchived)
        {
            return false;
        }

        if (!hasResponse)
        {
            return true;
        }

        if (requestTo == "orchestrator")
        {
            return false;
        }

        return responseBody is null
            || (responseBody.Length <= ReportBytes && ReportStatusOf(System.Text.Encoding.UTF8.GetString(responseBody), id) == "no-report")
            || IsPartial(System.Text.Encoding.UTF8.GetString(responseBody));
    }

    /// <summary>Whether a response holds only partial reports (<c>ah msg fill --part</c>, front matter <c>partial: true</c>): its request is still open.</summary>
    public static bool IsPartial(string text) =>
        MessageFront.ParseFrontmatter(text)?.Fields.Get("partial") is { IsText: true } p && p.AsString() == "true";

    /// <summary>
    /// The exchanges of the rows: one per (pool, id) that has a request. The last request and the last response by name, compared
    /// ordinally (<see cref="LastBy"/>), are the pair; a response with no request has no exchange, and neither has a note.
    /// </summary>
    public static IReadOnlyList<MsgExchangePair> Pair(IEnumerable<MsgFileRow> rows)
    {
        var ordered = rows.OrderBy(r => r.Name, StringComparer.Ordinal).ToList();
        var responses = LastBy(ordered.Where(r => r.Type == "response"), r => (r.PoolDir, r.Id));
        return [.. LastBy(ordered.Where(r => r.Type == "request"), r => (r.PoolDir, r.Id)).Select(p => new MsgExchangePair(p.Value, responses.GetValueOrDefault(p.Key)))];
    }

    /// <summary>
    /// The last item of each key, in the order the keys first appear. When two files share an id and type (they differ only by recipient or slug),
    /// the one listed last in file-name order is the exchange's request or response; every reader and writer of an exchange picks it through this.
    /// </summary>
    public static Dictionary<TKey, T> LastBy<T, TKey>(IEnumerable<T> ordered, Func<T, TKey> key)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(ordered);
        ArgumentNullException.ThrowIfNull(key);
        var last = new Dictionary<TKey, T>();
        foreach (var item in ordered)
        {
            last[key(item)] = item;
        }

        return last;
    }

    /// <summary>
    /// The key a pool directory is stored and looked up under: its real path (every link followed) with no trailing slash, so every
    /// spelling of one pool gives one key. Every <c>pool_dir</c> write and filter goes through it.
    /// </summary>
    public static string PoolKey(string dir)
    {
        // empty or relative is no directory to resolve: joining it to the process cwd would key it under ahd's own dir
        if (string.IsNullOrEmpty(dir) || dir[0] != '/')
        {
            return dir;
        }

        var real = PathGuard.RealPath(dir, strict: false) ?? dir;
        return real.Length > 1 ? real.TrimEnd('/') : real;
    }

    /// <summary>The key a message path is stored and looked up under: its directory through <see cref="PoolKey"/>, then its name.</summary>
    public static string PathKey(string path) => PoolKey(NodePath.Dirname(path)) + "/" + NodePath.Basename(path);

    /// <summary>The lower-case hexadecimal SHA-256 of <paramref name="bytes"/>.</summary>
    public static string Sha256Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
