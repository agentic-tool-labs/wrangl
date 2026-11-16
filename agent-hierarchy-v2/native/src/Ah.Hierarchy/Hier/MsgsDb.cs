using System.Text;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Text;

namespace Ah.Hierarchy;

/// <summary>
/// The <c>msgs</c> write ops in a database transaction. The store is the only record: no op reads or writes a message file. A message
/// has a body per revision from its creation on; a draft's single revision is replaced by its first fill, and a seal flips its state.
/// </summary>
internal sealed partial class HierLib
{
    internal JsonValue MsgsCreateDb(OpCall call, IDbOpTransaction tx, JsonValue args)
    {
        var dir = ArgText(args, "dir");
        var path = ArgText(args, "path");
        NotInMsgs(path, dir);
        var content = ArgText(args, "content");
        if (args.Member("req") is not { IsTruthy: true } && HeldCreate(dir, path, content, args.Member("dedupe") is { IsTruthy: true }) is { } held)
        {
            return new JsonObject { ["ok"] = true, ["existing"] = true, ["id"] = held.Id, ["path"] = held.Path };
        }

        var bytes = Utf8Text.Encode(content);
        CheckCap(NodePath.Basename(path), bytes);
        var row = RowOf(dir, path, content, bytes, archived: false, sealedBody: CarriesFullBody(content, path));
        tx.Execute(MsgRows.InsertMessageSql, MsgRows.MessageArgs(row));
        if ((long)tx.Query("SELECT changes()")[0][0]! == 0)
        {
            throw new CodedIoException("EEXIST", $"EEXIST: file already exists, open '{path}'");
        }

        var pk = (long)tx.Query(MsgRows.SelectPkSql, MsgRows.KeyArgs(row))[0][0]!;
        if (row.Bytes is not null)
        {
            tx.Execute(MsgRows.InsertBodySql, MsgRows.BodyArgs(pk, row, SealedAtMs()));
        }
        else
        {
            tx.Execute("INSERT INTO msg_bodies(msg_pk, rev, bytes, sha256, sealed_at_ms, sealed_by) VALUES (?1, 1, ?2, ?3, ?4, NULL)", pk, bytes, MsgRows.Sha256Hex(bytes), SealedAtMs());
            tx.Execute("UPDATE msg_messages SET body_rev = 1 WHERE pk = ?1", pk);
        }

        RefreshExchange(tx, dir, row.Id);
        return new JsonObject { ["ok"] = true };
    }

    internal JsonValue MsgsSettleDb(OpCall call, IDbOpTransaction tx, JsonValue args)
    {
        var dir = ArgText(args, "dir");
        var path = ArgText(args, "path");
        NotInMsgs(path, dir);
        var baseText = CurrentMessage(tx, dir, path);
        var fm = baseText is null ? null : MessageText.ParseFrontmatter(baseText)?.Fields;
        if (!(fm is not null && fm.Get("decision") is { } d && args.Member("from") is { } from && StrictEquals(d, from)))
        {
            throw new InvalidOperationException($"{path} does not hold decision {JsConvert.ToJsString(args.Member("from"))}");
        }

        _ = SealNewBytes(tx, dir, path, Utf8Text.Encode(ArgText(args, "content")));
        return new JsonObject { ["ok"] = true };
    }

    internal JsonValue MsgsRetireDb(OpCall call, IDbOpTransaction tx, JsonValue args)
    {
        var dir = ArgText(args, "dir");
        var path = ArgText(args, "path");
        NotInMsgs(path, dir);
        var baseText = CurrentMessage(tx, dir, path);
        var retired = RetiredText(path, baseText ?? throw new InvalidOperationException($"{path} cannot be read"), args);
        if (retired is null)
        {
            return new JsonObject { ["ok"] = true, ["already"] = true };
        }

        _ = SealNewBytes(tx, dir, path, Utf8Text.Encode(retired));
        return new JsonObject { ["ok"] = true };
    }

    internal JsonValue MsgsFillDb(OpCall call, IDbOpTransaction tx, JsonValue args)
    {
        var dir = ArgText(args, "dir");
        var path = ArgText(args, "path");
        var fillArgs = FillArgsOf(args, path);
        var id = fillArgs.Get("id") is { IsText: true } idText ? idText.AsString() : string.Empty;
        RefuseIfExchangeClosed(tx, dir, id, path);
        var plan = FillPlan(fillArgs).AsObject();
        var bytes = Utf8Text.Encode(plan["content"].AsString());
        var (rev, _) = SealNewBytes(tx, dir, path, bytes);
        return new JsonObject { ["ok"] = true, ["bytes"] = bytes.Length, ["rev"] = rev };
    }

    internal JsonValue MsgsArchiveDb(OpCall call, IDbOpTransaction tx, JsonValue args)
    {
        var dir = ArgText(args, "dir");
        var files = CheckedArchiveFiles(dir, args);
        var ids = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var from in files)
        {
            var to = NodePath.Join(ArchiveDir(dir), NodePath.Basename(from));
            if (MessageText.ParseFilename(from) is { } parts)
            {
                tx.Execute(
                    "UPDATE msg_messages SET state = 'archived', draft_path = CASE WHEN draft_path IS NULL THEN NULL ELSE ?6 END, export_path = CASE WHEN export_path IS NULL THEN NULL ELSE ?6 END, export_sha = CASE WHEN body_rev IS NULL THEN export_sha ELSE NULL END WHERE pool_dir = ?1 AND id = ?2 AND type = ?3 AND \"to\" = ?4 AND slug = ?5",
                    MsgRows.PoolKey(dir), parts.Id, parts.Type, parts.To, parts.Slug, MsgRows.PathKey(to));
                ids.Add(parts.Id);
            }
        }

        foreach (var id in ids)
        {
            RefreshExchange(tx, dir, id);
        }

        return new JsonObject { ["ok"] = true };
    }

    /// <summary>Seals the message at <c>path</c>: a draft's newest revision becomes sealed. A message already sealed changes nothing.</summary>
    internal JsonValue MsgsSealDb(OpCall call, IDbOpTransaction tx, JsonValue args)
    {
        var dir = ArgText(args, "dir");
        var path = ArgText(args, "path");
        NotInMsgs(path, dir);
        if (MessageText.ParseFilename(path) is not { Type: "request" or "response" } parts)
        {
            throw new InvalidOperationException($"{path} is not a request or response file");
        }

        var found = tx.Query("SELECT m.pk, m.state, b.rev, b.sha256, b.bytes FROM msg_messages m LEFT JOIN msg_bodies b ON b.msg_pk = m.pk AND b.rev = m.body_rev WHERE m.pool_dir = ?1 AND m.id = ?2 AND m.type = ?3 AND m.\"to\" = ?4 AND m.slug = ?5", MsgRows.PoolKey(dir), parts.Id, parts.Type, parts.To, parts.Slug);
        if (found.Count == 0)
        {
            throw new InvalidOperationException($"no {parts.Type} {parts.Id} in the message store");
        }

        if (found[0][4] is not byte[] bytes)
        {
            throw new InvalidOperationException($"{parts.Type} {parts.Id} has no body");
        }

        if (MessageText.ParseFrontmatter(Encoding.UTF8.GetString(bytes)) is null)
        {
            throw new InvalidOperationException($"{path} has no front matter");
        }

        var changed = (string)found[0][1]! == MsgRows.Draft;
        if (changed)
        {
            tx.Execute("UPDATE msg_messages SET state = 'sealed', export_path = draft_path, draft_path = NULL WHERE pk = ?1", found[0][0]);
            RefreshExchange(tx, dir, parts.Id);
        }

        return new JsonObject { ["ok"] = true, ["sha"] = (string)found[0][3]!, ["rev"] = (long)found[0][2]!, ["changed"] = changed };
    }

    /// <summary>Seals the request or response <paramref name="path"/> before a pointer to it is sent.</summary>
    public void SealForSend(string path)
    {
        if (_messageModes is null || MessageText.ParseFilename(path) is not { Type: "request" or "response" } || NodePath.Basename(NodePath.Dirname(path)) != "msgs")
        {
            return;
        }

        try
        {
            _ = _messageModes.Read("msgs");
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            throw new InvalidOperationException($"{path} cannot be sent: the message storage mode cannot be read ({e.Message})");
        }

        var dir = NodePath.Dirname(NodePath.Dirname(path));
        _ = Call(PoolContext(dir), "msgs", "seal", new JsonObject { ["dir"] = dir, ["path"] = path });
    }

    public bool HasAuthoredContent(string text) => MessageText.HasAuthoredContent(text);

    private static void CheckCap(string name, byte[] bytes)
    {
        if (bytes.Length > MsgRows.BodyCap)
        {
            throw new InvalidOperationException($"{name} is {bytes.Length} bytes; a sealed message holds at most {MsgRows.BodyCap} bytes");
        }
    }

    /// <summary>A file the create op writes whole: a note, or a response that closes or withdraws its request. Every other create writes a skeleton, which is a draft.</summary>
    private static bool CarriesFullBody(string content, string path)
    {
        var type = MessageText.ParseFilename(path)?.Type;
        if (type is "note" or "link-note")
        {
            return true;
        }

        return type == "response" && MessageText.ParseFrontmatter(content)?.Fields is { } f
            && (f.Get("closed") is { IsText: true } c && c.AsString() == "true" || f.Get("withdrawn") is { IsText: true } w && w.AsString() == "true");
    }

    private long SealedAtMs() => _clock.UtcNow.ToUnixTimeMilliseconds();

    private static MsgFileRow RowOf(string dir, string path, string text, byte[] bytes, bool archived, bool sealedBody)
    {
        var name = NodePath.Basename(path);
        var parts = MessageText.ParseFilename(name) ?? throw new InvalidOperationException($"{name} is not a message file name");
        var fm = MessageText.ParseFrontmatter(text)?.Fields;
        return MsgFileRows.FromFrontMatter(
            dir, path, name, parts.Id, parts.Type, parts.To, parts.Slug, fm is null ? default(JsonValue?) : JsonValue.FromObject(fm), archived,
            sealedBody ? MsgRows.Sealed : MsgRows.Draft, sealedBody ? bytes : null);
    }

    /// <summary>The text the message holds now: its newest body; null when it has none.</summary>
    private static string? CurrentMessage(IDbOpTransaction tx, string dir, string path) =>
        MessageText.ParseFilename(path) is { } parts
            && tx.Query(MsgRows.SelectPkSql, MsgRows.PoolKey(dir), parts.Id, parts.Type, parts.To, parts.Slug) is { Count: > 0 } found
            && NewestBody(tx, (long)found[0][0]!) is { } body
            ? Encoding.UTF8.GetString(body)
            : null;

    private static byte[]? NewestBody(IDbOpTransaction tx, long pk) =>
        tx.Query("SELECT bytes FROM msg_bodies WHERE msg_pk = ?1 ORDER BY rev DESC LIMIT 1", pk) is { Count: > 0 } r ? (byte[])r[0][0]! : null;

    /// <summary>
    /// Stores <paramref name="bytes"/> as the message at <paramref name="path"/> and seals it: a draft's revision is replaced, so a first fill
    /// is revision 1; a sealed message gets the next revision, or nothing when its newest revision holds these bytes.
    /// </summary>
    private (long Rev, bool Changed) SealNewBytes(IDbOpTransaction tx, string dir, string path, byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes);
        var name = NodePath.Basename(path);
        CheckCap(name, bytes);
        var parts = MessageText.ParseFilename(name) ?? throw new InvalidOperationException($"{name} is not a message file name");
        var sha = MsgRows.Sha256Hex(bytes);
        var found = tx.Query("SELECT pk, state FROM msg_messages WHERE pool_dir = ?1 AND id = ?2 AND type = ?3 AND \"to\" = ?4 AND slug = ?5", MsgRows.PoolKey(dir), parts.Id, parts.Type, parts.To, parts.Slug);
        long rev;
        bool changed;
        if (found.Count == 0)
        {
            var row = RowOf(dir, path, text, bytes, archived: false, sealedBody: true);
            tx.Execute(MsgRows.InsertMessageSql, MsgRows.MessageArgs(row));
            var pk = (long)tx.Query(MsgRows.SelectPkSql, MsgRows.KeyArgs(row))[0][0]!;
            tx.Execute(MsgRows.InsertBodySql, MsgRows.BodyArgs(pk, row, SealedAtMs()));
            rev = 1;
            changed = true;
        }
        else
        {
            var pk = (long)found[0][0]!;
            var draft = (string)found[0][1]! == MsgRows.Draft;
            var newest = tx.Query("SELECT rev, sha256 FROM msg_bodies WHERE msg_pk = ?1 ORDER BY rev DESC LIMIT 1", pk);
            if (!draft && newest.Count > 0 && (string)newest[0][1]! == sha)
            {
                rev = (long)newest[0][0]!;
                changed = false;
            }
            else
            {
                changed = true;
                if (draft && newest.Count > 0)
                {
                    rev = (long)newest[0][0]!;
                    tx.Execute("UPDATE msg_bodies SET bytes = ?3, sha256 = ?4, sealed_at_ms = ?5, sealed_by = NULL WHERE msg_pk = ?1 AND rev = ?2", pk, rev, bytes, sha, SealedAtMs());
                }
                else
                {
                    rev = newest.Count > 0 ? (long)newest[0][0]! + 1 : 1;
                    tx.Execute("INSERT INTO msg_bodies(msg_pk, rev, bytes, sha256, sealed_at_ms, sealed_by) VALUES (?1, ?2, ?3, ?4, ?5, NULL)", pk, rev, bytes, sha, SealedAtMs());
                }

                tx.Execute(
                    "UPDATE msg_messages SET state = CASE WHEN state = 'archived' THEN 'archived' ELSE 'sealed' END, body_rev = ?2, export_path = ?3, draft_path = NULL WHERE pk = ?1",
                    pk, rev, MsgRows.PathKey(path));
            }
        }

        RefreshExchange(tx, dir, parts.Id);
        return (rev, changed);
    }

    /// <summary>
    /// Brings the exchange row of <paramref name="id"/> in line with its request and response rows: the first of each by name, the state
    /// and the closure facts from <see cref="MsgRows.ExchangeArgs"/>, the response's from its newest body. An id with no request has no exchange.
    /// </summary>
    private static void RefreshExchange(IDbOpTransaction tx, string dir, string id)
    {
        dir = MsgRows.PoolKey(dir);
        var rows = tx.Query(
            "SELECT pk, type, state, \"to\", \"from\", team, created_ms, slug FROM msg_messages WHERE pool_dir = ?1 AND id = ?2 AND type IN ('request', 'response')",
            dir, id).OrderBy(r => (string)r[3]! + "--" + (string)r[7]!, StringComparer.Ordinal).ToList();
        var request = MsgRows.LastBy(rows.Where(r => (string)r[1]! == "request"), _ => 0).GetValueOrDefault(0);
        if (request is null)
        {
            return;
        }

        var response = MsgRows.LastBy(rows.Where(r => (string)r[1]! == "response"), _ => 0).GetValueOrDefault(0);
        MsgFileRow Shell(object?[] r, bool closed = false, bool withdrawn = false, string? closedBy = null, string? closedReason = null) => new(
            dir, string.Empty, id, (string)r[1]!, (string)r[3]!, string.Empty, r[4] as string, null, null, r[5] as string, null, null, r[6] as long?,
            (string)r[2]! == "archived", MsgRows.Draft, null, string.Empty, closed, withdrawn, closedBy, closedReason);

        MsgFileRow? responseRow = null;
        long? answeredMs = null;
        if (response is not null)
        {
            // a draft is no answer: its only revision is the skeleton
            var newest = (string)response[2]! == MsgRows.Draft ? [] : tx.Query("SELECT bytes, sealed_at_ms FROM msg_bodies WHERE msg_pk = ?1 ORDER BY rev DESC LIMIT 1", (long)response[0]!);
            var body = newest.Count > 0 ? (byte[])newest[0][0]! : null;
            // an archived skeleton was never answered either
            answeredMs = newest.Count > 0 && ((string)response[2]! != "archived" || MessageText.HasAuthoredContent(Encoding.UTF8.GetString(body!))) ? newest[0][1] as long? : null;
            var fm = body is null ? null : MessageText.ParseFrontmatter(Encoding.UTF8.GetString(body))?.Fields;
            string? Field(string key) => fm?.Get(key) is { IsText: true } v ? v.AsString() : null;
            responseRow = Shell(response, Field("closed") == "true", Field("withdrawn") == "true", Field("closed_by"), Field("closed_reason")) with { Bytes = body };
        }

        var args = MsgRows.ExchangeArgs(new MsgExchangePair(Shell(request), responseRow), (long)request[0]!, response is null ? null : (long)response[0]!, answeredMs);
        tx.Execute(MsgRows.InsertExchangeSql, args);

        // F6: closed_ms is when the exchange first closed (closed or withdrawn), by the database clock; prune ages it from there
        tx.Execute(
            "UPDATE msg_exchanges SET request_pk = ?3, response_pk = ?4, state = ?5, opened_ms = ?6, closed_by = ?7, reason = ?8, team = ?9, \"to\" = ?10, \"from\" = ?11, live = ?12, answered_ms = ?13, "
            + "closed_ms = CASE WHEN ?5 = 'closed' THEN COALESCE(closed_ms, " + StatusDirty.NowMsSql + ") ELSE NULL END WHERE pool_dir = ?1 AND id = ?2",
            args);
    }

    /// <summary>Refuses a fill into the response of an exchange whose stored response closes or withdraws it, with the refusal a fill into the file gives.</summary>
    private void RefuseIfExchangeClosed(IDbOpTransaction tx, string dir, string id, string path)
    {
        var closed = tx.Query("SELECT m.pk FROM msg_exchanges e JOIN msg_messages m ON m.pk = e.response_pk WHERE e.pool_dir = ?1 AND e.id = ?2 AND e.state = 'closed'", MsgRows.PoolKey(dir), id);
        if (closed.Count > 0 && NewestBody(tx, (long)closed[0][0]!) is { } body && MessageText.ParseFrontmatter(Encoding.UTF8.GetString(body))?.Fields is { } fields)
        {
            RefuseIfClosed(path, id, fields);
        }
    }
}
