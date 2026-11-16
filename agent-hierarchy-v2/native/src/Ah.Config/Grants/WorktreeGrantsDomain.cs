using System.Text;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Core.Time;

namespace Ah.Config;

/// <summary>
/// The <c>worktree-grants</c> state domain: the only writer of a team's grants document
/// (<c>&lt;hierarchy dir&gt;/grants/&lt;team&gt;.json</c>) and of the audit log. Every change appends its audit row first and then
/// replaces the document carrying that row's hash, so a crash between the two reads as an inconsistent document (enforce, no
/// extras), never a looser one. A refused change appends a refusal row and changes nothing else.
/// </summary>
[StateDomain("worktree-grants", "worktree-grants")]
internal sealed class WorktreeGrantsDomain(IFileReader files, IAhEnvironment environment, IClock clock, IGrantSource? grants = null) : IStateDomain
{
    private readonly WorktreeGrants _grants = new(files, environment);

    public string Name => "worktree-grants";

    /// <summary>With the grants tables (S11e) every write runs its DbBody, in one transaction; without them, the files as before.</summary>
    public bool DbOnly => grants is not null;

    public IReadOnlyList<string> StoreNames => ["worktree-grants"];

    public string StateDirectory(RequestContext context) => NodePath.Join(context.HierarchyDirectory, ".state-grants");

    public IReadOnlyList<StateOp> Ops =>
    [
        Read("status", Status),
        Read("keys", Keys, daemon: false),
        Write("assign", Assign),
        Write("unassign", Unassign),
        Write("allow", Allow),
        Write("revoke", Revoke),
        Write("gate", Gate),
        Write("audit", Audit),
    ];

    public void AfterWrite(OpCall operation, OpResult result)
    {
    }

    /// <summary>A change the caller may not make: the reason is what the verb prints.</summary>
    private sealed class Refused(string reason) : Exception(reason);

    private static StateOp Read(string name, Func<JsonObject, JsonValue> body, bool daemon = true) =>
        new(
            name,
            OpKind.Read,
            null,
            false,
            null,
            daemon,
            (call, _) =>
            {
                try
                {
                    var args = JsonText.Parse(call.ArgsJson.Span) is { IsMap: true } v ? v.AsObject() : throw new InvalidOperationException("args must be an object");
                    return new OpResult(true, JsonText.ToUtf8(body(args), 0));
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    return new OpResult(false, ReadOnlyMemory<byte>.Empty, e.Message);
                }
            },
            PathArgs: ["dir"]);

    private StateOp Write(string name, Func<JsonObject, Store, JsonValue> body) =>
        new(
            name,
            OpKind.Write,
            OpStyle.Replace,
            false,
            ".domain.lock",
            true,
            (call, ctx) => Run(call, name, body, _ => new FileStore(this, files, ctx ?? throw new InvalidOperationException("a write op needs a write context"))),
            PathArgs: ["dir"],
            DbBody: grants is null ? null : (call, tx) => Run(call, name, body, a => new DbStore(tx, grants.Take(tx, Required(a, "dir")), clock.UtcNow.ToUnixTimeMilliseconds())));

    private OpResult Run(OpCall call, string name, Func<JsonObject, Store, JsonValue> body, Func<JsonObject, Store> storeOf)
    {
        try
        {
            var args = JsonText.Parse(call.ArgsJson.Span) is { IsMap: true } v ? v.AsObject() : throw new InvalidOperationException("args must be an object");
            var store = storeOf(args);
            try
            {
                return new OpResult(true, JsonText.ToUtf8(body(args, store), 0));
            }
            catch (Refused refused)
            {
                // ponytail: a failed DbBody rolls its transaction back, so the tables keep no refused row (the files do); a refused row
                // in its own transaction needs an engine path for a failed op that still writes
                if (store is FileStore)
                {
                    AppendRow(store, args, name, refused.Message, changed: false, "refused");
                }

                return new OpResult(false, ReadOnlyMemory<byte>.Empty, refused.Message);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return new OpResult(false, ReadOnlyMemory<byte>.Empty, e.Message);
        }
    }

    // ---------------------------------------------------------------- ops

    /// <summary><c>keys</c>: <c>{dir}</c> is <c>{teams: [...]}</c>, the teams that have a grants document in that directory, by one directory listing. It reads no document.</summary>
    private JsonValue Keys(JsonObject a)
    {
        var teams = new JsonArray();
        foreach (var team in _grants.Teams(Required(a, "dir")).Order(StringComparer.Ordinal))
        {
            teams.Add(team);
        }

        return new JsonObject { ["teams"] = teams };
    }

    /// <summary>
    /// Whether the team's grants are in force and, for <c>member</c>, the work tree and stream recorded for it. A team with no
    /// document, or one read as <c>off</c>, is not active.
    /// </summary>
    private JsonValue Status(JsonObject a)
    {
        var view = _grants.Read(Required(a, "dir"), Required(a, "team"));
        var mode = _grants.EffectiveMode(view);
        var result = new JsonObject
        {
            ["present"] = view.Present,
            ["mode"] = mode,
            ["active"] = view.Present && mode is "log" or "enforce",
        };
        if (a.Get("member") is { IsText: true } name && view.Members.TryGetValue(name.AsString(), out var member))
        {
            result["work_root"] = member.WorkRoot;
            result["stream"] = member.Stream is null ? JsonValue.Null : member.Stream;
            result["tmp"] = TmpRow(member.Tmp);
        }

        var rows = new JsonObject();
        foreach (var (key, m) in view.Members)
        {
            var extras = new JsonArray();
            foreach (var x in m.Extra)
            {
                extras.Add(new JsonObject
                {
                    ["path"] = x.Path,
                    ["note"] = x.Note,
                    ["for"] = x.For is null ? JsonValue.Null : x.For,
                    ["outside"] = x.Outside,
                    ["standing"] = x.Standing,
                });
            }

            rows[key] = new JsonObject
            {
                ["work_root"] = m.WorkRoot,
                ["stream"] = m.Stream is null ? JsonValue.Null : m.Stream,
                ["role"] = m.Role is null ? JsonValue.Null : m.Role,
                ["extras"] = extras,
                ["tmp"] = TmpRow(m.Tmp),
            };
        }

        result["members"] = rows;
        result["consistent"] = view.Consistent;
        return result;
    }

    private static JsonObject TmpEntry(JsonObject wanted) => new()
    {
        ["root"] = wanted.Get("root")!.Value,
        ["alias"] = wanted.Get("alias") is { IsText: true } alias ? alias : JsonValue.FromString(string.Empty),
        ["kind"] = wanted.Get("kind") is { IsText: true } kind ? kind : JsonValue.FromString("claude"),
    };

    private static JsonValue TmpRow(GrantTmp? tmp) =>
        tmp is null ? JsonValue.Null : new JsonObject { ["root"] = tmp.Root, ["alias"] = tmp.Alias, ["kind"] = tmp.Kind };

    private JsonValue Assign(JsonObject a, Store ctx)
    {
        var member = Required(a, "member");
        if (a.Get("only_existing") is { Kind: JsonKind.Bool, IsTruthy: true } && !ctx.Exists(a))
        {
            return new JsonObject { ["ok"] = true, ["assigned"] = false };
        }

        if (a.Get("tmp_only") is { Kind: JsonKind.Bool, IsTruthy: true })
        {
            var current = Load(a, ctx);
            if (current.Members().Get(member) is not { IsMap: true } existing || a.Get("set_tmp") is not { IsMap: true } wanted)
            {
                return new JsonObject { ["ok"] = true, ["assigned"] = false };
            }

            existing.AsObject()["tmp"] = TmpEntry(wanted.AsObject());
            Commit(ctx, a, current, "assign", member, null, null);
            return new JsonObject { ["ok"] = true, ["assigned"] = true };
        }

        var root = CanonicalOf(Required(a, "work_root"));
        if (files.Stat(root) is not { Kind: FileKind.Directory })
        {
            throw new Refused($"the work tree {root} does not exist");
        }

        var doc = Load(a, ctx);
        var entry = new JsonObject
        {
            ["work_root"] = root,
            ["stream"] = a.Get("stream") is { IsText: true } s ? s : JsonValue.Null,
            ["role"] = a.Get("role") is { IsText: true } r ? r : JsonValue.Null,
            ["extra"] = new JsonArray(),
        };
        if (a.Get("set_tmp") is { IsMap: true } tmp && tmp.AsObject().Get("root") is { IsText: true })
        {
            entry["tmp"] = TmpEntry(tmp.AsObject());
        }

        doc.Members()[member] = entry;
        Commit(ctx, a, doc, "assign", member, root, null);
        return new JsonObject { ["ok"] = true, ["assigned"] = true, ["work_root"] = root };
    }

    /// <summary>Drops the member's record, and with it every grant it held. A team with no document, or no record of the member, changes nothing.</summary>
    private JsonValue Unassign(JsonObject a, Store ctx)
    {
        var member = Required(a, "member");
        if (!ctx.Exists(a))
        {
            return new JsonObject { ["ok"] = true, ["removed"] = false };
        }

        var doc = Load(a, ctx);
        if (!doc.Members().Remove(member))
        {
            return new JsonObject { ["ok"] = true, ["removed"] = false };
        }

        Commit(ctx, a, doc, "unassign", member, null, null);
        return new JsonObject { ["ok"] = true, ["removed"] = true };
    }

    private JsonValue Allow(JsonObject a, Store ctx)
    {
        var member = Required(a, "member");
        var note = a.Get("note") is { IsText: true } n ? n.AsString().Trim() : string.Empty;
        if (note.Length == 0)
        {
            throw new Refused("allow needs --note: say why the member may write there");
        }

        var requested = Required(a, "path");
        if (!NodePath.IsAbsolute(requested))
        {
            throw new Refused("allow needs an absolute path");
        }

        var doc = Load(a, ctx);
        if (doc.Members().Get(member) is not { IsMap: true } entry)
        {
            // The args describe the member only when it has no record; an existing record's work_root and role are never overwritten.
            if (a.Get("work_root") is not { IsText: true } rootArg)
            {
                throw new Refused($"{member} has no assigned work tree in this team's grants record");
            }

            var root = CanonicalOf(rootArg.AsString());
            if (files.Stat(root) is not { Kind: FileKind.Directory })
            {
                throw new Refused($"the work tree {root} does not exist");
            }

            entry = new JsonObject
            {
                ["work_root"] = root,
                ["stream"] = JsonValue.Null,
                ["role"] = a.Get("role") is { IsText: true } newRole ? newRole : JsonValue.Null,
                ["extra"] = new JsonArray(),
            };
            doc.Members()[member] = entry;
        }

        var path = CanonicalOf(requested);
        var outside = a.Get("outside") is { Kind: JsonKind.Bool, IsTruthy: true };
        if (a.Get("for") is { IsText: true } given && given.AsString().Trim().Length == 0)
        {
            throw new Refused("allow --for needs a request id; an empty one would make the grant permanent. Leave --for out for that, or pass --standing");
        }

        var forId = a.Get("for") is { IsText: true, } f ? f.AsString().Trim() : null;
        var role = entry.AsObject().Get("role") is { IsText: true } rr ? rr.AsString() : null;
        var standing = a.Get("standing") is { Kind: JsonKind.Bool, IsTruthy: true };
        if (role == "reviewer" && forId is null && !standing)
        {
            throw new Refused("ah: a reviewer's grant needs --for <request id> (the review it serves), or --standing to keep it until dismiss.");
        }

        var wasOutside = CheckPath(a, path, outside);
        var extras = entry.AsObject().Get("extra") is { IsArray: true } list ? list.AsArray() : new JsonArray();
        var grant = new JsonObject
        {
            ["path"] = path,
            ["note"] = note,
            ["for"] = forId is null ? JsonValue.Null : forId,
            ["outside"] = wasOutside,
            ["at"] = Timestamps.ToIsoString(clock.UtcNow),
            ["standing"] = standing,
        };
        if (a.Get("by") is { IsText: true } by && by.AsString().Length > 0)
        {
            grant["by"] = by;
        }

        // One extra per (path, request) so a new request's grant neither hides behind an old one nor cuts it: a held permanent grant
        // already covers a request-scoped one, a request's own grant is not doubled, and a request that is over ends through
        // EffectiveExtras. A later standing grant upgrades a held permanent one.
        var all = extras.ToList();
        var heldHere = Enumerable.Range(0, all.Count).Where(i => all[i].Member("path") is { IsText: true } p && p.AsString() == path).ToList();
        var permanent = heldHere.FindIndex(i => all[i].Member("for") is not { IsText: true });
        if (forId is null)
        {
            if (permanent < 0)
            {
                extras.Add(grant);
            }
            else if (standing && !(all[heldHere[permanent]].Member("standing") is { Kind: JsonKind.Bool, IsTruthy: true }))
            {
                extras[heldHere[permanent]] = grant;
            }
        }
        else if (permanent < 0 && !heldHere.Any(i => all[i].Member("for") is { IsText: true } held && held.AsString() == forId))
        {
            extras.Add(grant);
        }

        entry.AsObject()["extra"] = extras;
        Commit(ctx, a, doc, "allow", member, path, note, forId, wasOutside);
        return new JsonObject { ["ok"] = true, ["path"] = path, ["outside"] = wasOutside };
    }

    private JsonValue Revoke(JsonObject a, Store ctx)
    {
        var member = Required(a, "member");
        var requested = Required(a, "path");
        var doc = Load(a, ctx);
        if (doc.Members().Get(member) is not { IsMap: true } entry)
        {
            throw new Refused($"{member} has no assigned work tree in this team's grants record");
        }

        var path = NodePath.IsAbsolute(requested) ? CanonicalOf(requested) : requested;
        var extras = entry.AsObject().Get("extra") is { IsArray: true } list ? list.AsArray() : new JsonArray();
        var kept = new JsonArray();
        var removed = 0;
        foreach (var x in extras)
        {
            if (x.Member("path") is { IsText: true } p && (p.AsString() == path || p.AsString() == requested))
            {
                removed++;
            }
            else
            {
                kept.Add(x);
            }
        }

        if (removed == 0)
        {
            throw new Refused($"{member} has no grant for {path}");
        }

        entry.AsObject()["extra"] = kept;
        Commit(ctx, a, doc, "revoke", member, path, null);
        return new JsonObject { ["ok"] = true, ["removed"] = removed };
    }

    private JsonValue Gate(JsonObject a, Store ctx)
    {
        var mode = Required(a, "mode");
        if (!WorktreeGrants.Modes.Contains(mode))
        {
            throw new Refused($"unknown mode {mode} (expected: {string.Join(", ", WorktreeGrants.Modes)})");
        }

        var doc = Load(a, ctx);
        doc.Doc["mode"] = mode;
        Commit(ctx, a, doc, "gate", null, null, mode);
        return new JsonObject { ["ok"] = true, ["mode"] = mode };
    }

    private JsonValue Audit(JsonObject a, Store ctx)
    {
        var action = a.Get("action") is { IsText: true } t ? t.AsString() : "refused";
        AppendRow(ctx, a, action, a.Get("reason") is { IsText: true } r ? r.AsString() : null, changed: false, a.Get("result") is { IsText: true } res ? res.AsString() : "refused");
        return new JsonObject { ["ok"] = true };
    }

    // ---------------------------------------------------------------- the rules for a granted path

    /// <summary>Refuses a path that no grant may name; returns whether it is outside the repository family and temp.</summary>
    private bool CheckPath(JsonObject a, string path, bool outsideFlag)
    {
        var home = CanonicalOf(environment.HomeDirectory);
        var family = FamilyRoots(Required(a, "dir"));
        if (GrantPaths.Forbidden(path, home, family) is { } forbidden)
        {
            throw new Refused(forbidden);
        }

        var inFamily = family.Any(r => CanonicalPath.IsUnder(path, r));
        var inTemp = TempRoots().Any(r => CanonicalPath.IsUnder(path, r));
        if (!inFamily && !inTemp && !outsideFlag)
        {
            throw new Refused($"{path} is outside the repository family and temp: pass --outside to grant it, then confirm with the user");
        }

        return !inFamily && !inTemp;
    }

    private List<string> FamilyRoots(string hierarchyDir) => GrantPaths.FamilyRoots(files, hierarchyDir, CanonicalOf);

    private List<string> TempRoots() => GrantPaths.TempRoots(environment, CanonicalOf);

    private string CanonicalOf(string path) =>
        CanonicalPath.Resolve(files, path) ?? throw new Refused($"{path} cannot be resolved (a link loop or a path that climbs past a missing name)");

    // ---------------------------------------------------------------- document and audit

    private sealed record Document(JsonObject Doc)
    {
        internal JsonObject Members()
        {
            if (Doc.Get("members") is { IsMap: true } m)
            {
                return m.AsObject();
            }

            var created = new JsonObject();
            Doc["members"] = created;
            return created;
        }
    }

    /// <summary>
    /// The team's grants document. A missing one is a new, unsaved, empty document (nothing is written until <see cref="Commit"/>); one
    /// that exists but cannot be read is never replaced, because it holds the grants and the audit hash.
    /// </summary>
    private Document Load(JsonObject a, Store store)
    {
        var dir = Required(a, "dir");
        var team = Required(a, "team");
        var path = _grants.DocumentPath(dir, team);
        string reason;
        try
        {
            if (JsonText.Parse(store.Text(a)) is { IsMap: true } existing)
            {
                return new Document(existing.AsObject());
            }

            reason = "not a record";
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return new Document(new JsonObject
            {
                ["version"] = WorktreeGrants.Version,
                ["team"] = team,
                ["mode"] = WorktreeGrants.OffMode,
                ["audit_hash"] = JsonValue.Null,
                ["members"] = new JsonObject(),
            });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonParseException)
        {
            reason = e.Message;
        }

        throw new Refused($"{team}'s grants document {path} cannot be read ({reason}); it was left as it is. Move it aside to start a new one.");
    }

    private void Commit(Store ctx, JsonObject a, Document doc, string action, string? member, string? path, string? note, string? forId = null, bool outside = false)
    {
        var line = AppendRow(ctx, a, action, null, changed: true, "allowed", member, path, note, forId, outside);
        doc.Doc["audit_hash"] = WorktreeGrants.HashOf(line);
        ctx.Put(a, SessionMapFile.ToBytes(doc.Doc));
    }

    private string AppendRow(
        Store ctx,
        JsonObject a,
        string action,
        string? reason,
        bool changed,
        string result,
        string? member = null,
        string? path = null,
        string? note = null,
        string? forId = null,
        bool outside = false)
    {
        var at = Timestamps.ToIsoString(clock.UtcNow);
        var row = new JsonObject
        {
            ["at"] = at,
            ["team"] = a.Get("team") ?? JsonValue.Null,
            ["member"] = member is not null ? member : a.Get("member") ?? JsonValue.Null,
            ["action"] = action,
            ["path"] = path is not null ? path : a.Get("path") ?? JsonValue.Null,
            ["note"] = note is not null ? note : a.Get("note") ?? JsonValue.Null,
            ["for"] = forId is not null ? forId : a.Get("for") ?? JsonValue.Null,
            ["outside"] = outside,
            ["result"] = result,
            ["reason"] = reason is null ? JsonValue.Null : reason,
            ["actor"] = a.Get("actor") ?? JsonValue.Null,
            ["doc"] = changed,
        };
        if (a.Get("standing") is { Kind: JsonKind.Bool, IsTruthy: true })
        {
            row["standing"] = true;
        }

        if (a.Get("by") is { IsText: true } by && by.AsString().Length > 0)
        {
            row["by"] = by;
        }

        var line = JsonText.Stringify(row, 0);
        ctx.Append(a, at, line);
        return line;
    }

    /// <summary>
    /// Where a write reads and puts the team's document and appends its audit row. A missing document is a
    /// <see cref="FileNotFoundException"/> in both, so <see cref="Load"/> reads them alike.
    /// </summary>
    private abstract class Store
    {
        internal abstract bool Exists(JsonObject a);

        internal abstract string Text(JsonObject a);

        internal abstract void Put(JsonObject a, byte[] document);

        internal abstract void Append(JsonObject a, string at, string line);
    }

    /// <summary>The files: <c>grants/&lt;team&gt;.json</c> replaced by tmp+rename, the row appended to the user's audit log.</summary>
    private sealed class FileStore(WorktreeGrantsDomain domain, IFileReader files, IStoreWriteContext ctx) : Store
    {
        private string PathOf(JsonObject a) => domain._grants.DocumentPath(Required(a, "dir"), Required(a, "team"));

        internal override bool Exists(JsonObject a) => files.Exists(PathOf(a));

        /// <summary>The team's document through the one grants read; unreadable is an <see cref="IOException"/>, none a <see cref="FileNotFoundException"/>.</summary>
        internal override string Text(JsonObject a)
        {
            var team = Required(a, "team");
            if (!files.ReadGrants(Required(a, "dir"), domain._grants.AuditPath()).TryDocuments(out var documents))
            {
                throw new IOException("the grants store could not be read");
            }

            return documents.FirstOrDefault(d => d.Team == team)?.Body ?? throw new FileNotFoundException("no grants document");
        }

        internal override void Put(JsonObject a, byte[] document)
        {
            var target = PathOf(a);
            ctx.Mkdir(NodePath.Dirname(target));
            ctx.Write(target, SessionMapFile.TmpName(target), document);
        }

        internal override void Append(JsonObject a, string at, string line)
        {
            var audit = domain._grants.AuditPath();
            ctx.Mkdir(NodePath.Dirname(audit));
            ctx.Append(audit, Encoding.UTF8.GetBytes(line + "\n"));
        }
    }

    /// <summary>
    /// The tables (S11e), inside the write's one transaction: the document row read and replaced, the audit row appended. A row whose
    /// body no longer matches its digest (edited out of band) reads as unreadable, so it is never replaced.
    /// </summary>
    private sealed class DbStore(IDbOpTransaction tx, string key, long nowMs) : Store
    {
        private string? Row(JsonObject a)
        {
            if (tx.Query("SELECT body, digest FROM worktree_grants WHERE hier_dir = ?1 AND team = ?2", key, Required(a, "team")) is not [var row])
            {
                return null;
            }

            var body = row[0] as string ?? throw new IOException("the stored grants document is not UTF-8 text");
            return WorktreeGrants.HashOf(body) == row[1] as string ? body : throw new IOException("the stored grants document does not match its digest");
        }

        internal override bool Exists(JsonObject a) =>
            tx.Query("SELECT 1 FROM worktree_grants WHERE hier_dir = ?1 AND team = ?2", key, Required(a, "team")).Count > 0;

        internal override string Text(JsonObject a) => Row(a) ?? throw new FileNotFoundException("no grants document");

        internal override void Put(JsonObject a, byte[] document)
        {
            var body = Encoding.UTF8.GetString(document);
            tx.Execute(
                "INSERT INTO worktree_grants(hier_dir, team, body, digest, updated_at) VALUES (?1, ?2, ?3, ?4, ?5) "
                + "ON CONFLICT(hier_dir, team) DO UPDATE SET body = excluded.body, digest = excluded.digest, updated_at = excluded.updated_at",
                key,
                Required(a, "team"),
                body,
                WorktreeGrants.HashOf(body),
                nowMs);
        }

        internal override void Append(JsonObject a, string at, string line) =>
            tx.Execute(
                "INSERT INTO worktree_grant_audit(at, hier_dir, team, body) VALUES (?1, ?2, ?3, ?4)",
                at,
                key,
                a.Get("team") is { IsText: true } t ? t.AsString() : string.Empty,
                line);
    }

    private static string Required(JsonObject a, string key) =>
        a.Get(key) is { IsText: true } t && t.AsString().Length > 0 ? t.AsString() : throw new InvalidOperationException($"{key} is required");
}
