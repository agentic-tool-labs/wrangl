using System.Text;
using Ah.Abstractions;
using Ah.Core.Files;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Core.Time;

namespace Ah.Mesh;

/// <summary>
/// The <c>mailbox</c> state domain. A box operation locks the box's <c>append.lock</c> and keeps its executed-op log in the box;
/// a registration's lock and log sit in <c>&lt;AH_HOME&gt;/registrations</c>.
/// </summary>
internal sealed partial class MailboxStateDomain : IStateDomain
{
    private const string AppendLock = "append.lock";
    private const string NoteLock = "note.lock";

    /// <summary>
    /// The mode key of session registration, so that the mail domain's mode never gates register and unregister. These ops have no
    /// database body, so this key must never read <c>db</c>: a sessions cutover sets <c>authority.sessions</c> and never <c>mode.sessions</c>.
    /// </summary>
    internal const string SessionsModeKey = "sessions";

    private readonly Mailbox _mailbox;
    private readonly IFileReader _files;
    private readonly IAhEnvironment _environment;
    private readonly IClock _clock;
    private readonly ISessionAuthority? _sessions;

    internal MailboxStateDomain(Mailbox mailbox, IFileReader files, IAhEnvironment environment, IClock clock, ISessionAuthority? sessions = null)
    {
        _mailbox = mailbox;
        _sessions = sessions;
        _files = files;
        _environment = environment;
        _clock = clock;
        Ops =
        [
            Op("register", OpStyle.Replace, false, null, Register, SessionsModeKey) with { BeforeEveryRun = WriteSessionMark },
            Op("unregister", OpStyle.Replace, false, null, Unregister, SessionsModeKey),
            Op("append", OpStyle.Append, false, AppendLock, Append, db: DbAppend),
            Op("attempt", OpStyle.Append, true, AppendLock, Attempt, db: DbAttempt),
            // heartbeats are read fresh by age and never through the mail counter; moving it on each (about every 7.5 s per session) emptied every mail cache
            Op("heartbeat", OpStyle.Overwrite, true, AppendLock, Heartbeat, db: DbHeartbeat) with { LeavesSeq = true },
            Op("ack", OpStyle.Replace, false, AppendLock, Ack, db: DbAck),
            Op("claim", OpStyle.Replace, false, AppendLock, Claim, db: DbClaim),
            Op("note", OpStyle.Replace, false, NoteLock, Note, db: DbNote),
            ReadOp("registrations", Registrations),
            ReadOp("registration", Registration),
            ReadOp("keys", RegistrationKeys),
            ReadOp("entries", Entries),
            ReadOp("stamp", Stamp),
            ReadOp("counts", Counts),
        ];
    }

    public string Name => "mailbox";

    public IReadOnlyList<string> StoreNames { get; } =
        ["mailbox-registrations", "mailbox-adapters", "mailbox-entries", "mailbox-attempts", "mailbox-acks", "mailbox-claims", "mailbox-notes"];

    public IReadOnlyList<StateOp> Ops { get; }

    public string StateDirectory(RequestContext context) => NodePath.Join(_environment.AhHome, "state", "mailbox-ops");

    public string StateDirectory(RequestContext context, ReadOnlyMemory<byte> argsJson)
    {
        try
        {
            if (!argsJson.IsEmpty && JsonText.Parse(argsJson.Span) is { IsMap: true } args && args.AsObject().Get("dir") is { IsText: true } dir)
            {
                return dir.AsString();
            }
        }
        catch (JsonParseException)
        {
            // an op with unreadable args is refused by the engine; the lock directory is the default one
        }

        return StateDirectory(context);
    }

    public void AfterWrite(OpCall operation, OpResult result)
    {
        // H3: a register or unregister changes the registration status entry of its session
        if (operation.Op is "register" or "unregister")
        {
            _mailbox.StatusDelta(operation, result);
        }
    }

    private static StateOp Op(string name, OpStyle style, bool lockOptional, string? lockName, Func<OpCall, JsonValue, IStoreWriteContext, JsonValue> body, string? modeKey = null, Func<JsonValue, IDbOpTransaction, JsonValue>? db = null) =>
        new(
            name,
            OpKind.Write,
            style,
            lockOptional,
            lockName,
            false,
            (call, ctx) => Running(call, args =>
            {
                var write = ctx ?? throw new InvalidOperationException("a write operation needs a write context");
                return body(call, args, write);
            }),
            ModeKey: modeKey,
            DbBody: db is null ? null : (call, tx) => Running(call, args => db(args, tx)));

    private static OpResult Running(OpCall call, Func<JsonValue, JsonValue> body)
    {
        try
        {
            var args = call.ArgsJson.IsEmpty ? new JsonObject() : JsonText.Parse(call.ArgsJson.Span);
            return new OpResult(true, JsonText.ToUtf8(body(args), 0));
        }
        catch (MailboxError e)
        {
            return new OpResult(false, ReadOnlyMemory<byte>.Empty, e.Message, e.Code);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return new OpResult(false, ReadOnlyMemory<byte>.Empty, e.Message);
        }
    }

    /// <summary>
    /// A read op. Like the write ops it is <c>Daemon=false</c> and declares no path argument, because a box dir lives under the
    /// ah home's mailbox root, not under a hierarchy dir; an op that takes a box dir refuses one outside that root itself.
    /// </summary>
    private static StateOp ReadOp(string name, Func<JsonValue, JsonValue> body) =>
        new(
            name,
            OpKind.Read,
            null,
            false,
            null,
            false,
            (call, _) =>
            {
                try
                {
                    var args = call.ArgsJson.IsEmpty ? new JsonObject() : JsonText.Parse(call.ArgsJson.Span);
                    return new OpResult(true, JsonText.ToUtf8(body(args), 0));
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    return new OpResult(false, ReadOnlyMemory<byte>.Empty, e.Message);
                }
            });

    /// <summary>The box dir named by <c>dir</c>: an absolute path under the mailbox root with no relative segment.</summary>
    private string BoxDir(JsonValue args)
    {
        var dir = TextOf(args, "dir");
        if (!dir.StartsWith(NodePath.Join(_environment.AhHome, "mailbox") + "/", StringComparison.Ordinal)
            || dir.Split('/').Any(static part => part is "." or ".."))
        {
            throw new ArgumentException("the box dir is not under the mailbox root");
        }

        return dir;
    }

    /// <summary><c>registrations</c>: <c>{}</c> is <c>{registrations: [{registration, live, team_files}]}</c>, every registration in file-name order.</summary>
    private JsonValue Registrations(JsonValue args)
    {
        var rows = new JsonArray();
        // ended registrations stay listed as evidence (live false), so a reader still proves their member dead
        foreach (var registration in _mailbox.EvidenceRegistrations())
        {
            var teamFiles = new JsonArray();
            foreach (var file in _mailbox.RegistrationTeamFiles(registration))
            {
                teamFiles.Add(file);
            }

            rows.Add(new JsonObject { ["registration"] = registration, ["live"] = _mailbox.RegistrationLive(registration), ["team_files"] = teamFiles });
        }

        return new JsonObject { ["registrations"] = rows };
    }

    /// <summary>
    /// <c>registration</c>: <c>{session_id}</c> is <c>{registration, team_files}</c> of that one session, or null when it has none. It reads that
    /// session's own record only and carries no liveness verdict.
    /// </summary>
    private JsonValue Registration(JsonValue args)
    {
        var session = TextOf(args, "session_id");
        if ((_mailbox.ReadRegistration(session)
                ?? _mailbox.EvidenceRegistrations().FirstOrDefault(e => e.Get("session_id") is { IsText: true } id && id.AsString() == session)) is not { } registration)
        {
            return JsonValue.Null;
        }

        var teamFiles = new JsonArray();
        foreach (var file in _mailbox.RegistrationTeamFiles(registration))
        {
            teamFiles.Add(file);
        }

        return new JsonObject { ["registration"] = registration, ["team_files"] = teamFiles };
    }

    /// <summary>
    /// <c>keys</c>: <c>{}</c> is <c>{sessions: [...], stamps?: {id: hash}}</c>, the session ids that have a registration, by one listing of the rows or of the directory.
    /// It parses no registration. <c>stamps</c> is there only for stored rows: a stamp that has not moved is a registration that has not.
    /// </summary>
    private JsonValue RegistrationKeys(JsonValue args)
    {
        var sessions = new JsonArray();
        IReadOnlyList<string> names;
        var stamps = _mailbox.StoredSessionStamps();
        try
        {
            names = stamps is not null ? [.. stamps.Keys.Select(static id => id + ".json")] : _files.List(NodePath.Join(_environment.AhHome, "registrations"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            names = [];
        }

        foreach (var name in names.Where(static n => n.Length > 5 && n.EndsWith(".json", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            sessions.Add(name[..^5]);
        }

        var reply = new JsonObject { ["sessions"] = sessions };
        if (stamps is not null)
        {
            var held = new JsonObject();
            foreach (var (id, stamp) in stamps)
            {
                held[id] = stamp;
            }

            reply["stamps"] = held;
        }

        return reply;
    }

    /// <summary><c>entries</c>: <c>{dir}</c> is <c>{entries: [{entry, acked}]}</c>, every entry of the box (acked or not) that is a record.</summary>
    private JsonValue Entries(JsonValue args)
    {
        var dir = BoxDir(args);
        var rows = new JsonArray();
        foreach (var entry in _mailbox.Inbox(dir, new InboxOptions(All: true)).Entries.Where(static e => e.IsMap))
        {
            var id = entry.Member("id") is { IsText: true } text ? text.AsString() : null;
            rows.Add(new JsonObject { ["entry"] = entry, ["acked"] = id is not null && _mailbox.IsAcked(dir, id) });
        }

        return new JsonObject { ["entries"] = rows };
    }

    /// <summary><c>counts</c>: <c>{dir, now_ms}</c> is <c>{unacked, overdue}</c>, how many entries of the box are unacked and how many of those are overdue at <c>now_ms</c>.</summary>
    private JsonValue Counts(JsonValue args)
    {
        var dir = BoxDir(args);
        var now = args.Member("now_ms") is { IsNumber: true } n ? n.AsNumber() : throw new ArgumentException("now_ms must be a number");
        return new JsonObject
        {
            ["unacked"] = _mailbox.Inbox(dir).Entries.Count,
            ["overdue"] = _mailbox.Inbox(dir, new InboxOptions(Overdue: true, NowMs: now)).Entries.Count,
        };
    }

    /// <summary><c>stamp</c>: <c>{dir}</c> is <c>{stamp}</c>, the change marker of the box's entries file and acks.</summary>
    private JsonValue Stamp(JsonValue args)
    {
        var dir = BoxDir(args);
        return new JsonObject { ["stamp"] = _mailbox.StoredBoxStamp(dir) ?? FileStamps.OfBox(_files, dir) };
    }

    private static JsonValue Flag(string name, bool value) => new JsonObject { [name] = value };

    private static string TextOf(JsonValue args, string key) =>
        args.Member(key) is { IsText: true } t ? t.AsString() : throw new ArgumentException($"{key} must be a string");

    private string Now => Timestamps.ToIsoString(_clock.UtcNow);

    private static string Falsy(JsonValue? value) => value is { IsTruthy: true } v ? JsConvert.ToJsString(v) : string.Empty;

    private JsonValue Register(OpCall call, JsonValue a, IStoreWriteContext ctx)
    {
        var sessionId = Falsy(a.Member("session_id"));
        if (!Mailbox.IsName(sessionId) || a.Member("boxes") is not { IsArray: true } list || list.AsArray().Count == 0)
        {
            return Flag("registered", false);
        }

        var boxes = list.AsArray();
        var addresses = new JsonArray();
        var map = new JsonObject();
        for (var i = 0; i < boxes.Count; i++)
        {
            var address = boxes[i].Member("address") ?? JsonValue.Null;
            addresses.Add(address);
            map[JsConvert.ToJsString(address)] = boxes[i].Member("dir") ?? JsonValue.Null;
        }

        var record = new JsonObject
        {
            ["v"] = 1,
            ["session_id"] = a.Member("session_id")!.Value,
            ["address"] = boxes[0].Member("address") ?? JsonValue.Null,
            ["addresses"] = addresses,
            ["boxes"] = map,
            ["team_file"] = a.Member("team_file") ?? JsonValue.Null,
            ["harness"] = a.Member("harness") ?? JsonValue.FromString("claude"),
            ["pid"] = a.Member("pid") ?? JsonValue.Null,
            ["pane_id"] = a.Member("pane_id") ?? JsonValue.Null,
        };
        if (a.Member("team_files") is { IsMap: true } teamFiles && teamFiles.AsObject().Keys.Any())
        {
            record["team_files"] = teamFiles;
        }

        if (a.Member("cwd") is { IsText: true } cwd && cwd.AsString().Length > 0)
        {
            record["cwd"] = cwd;
        }

        if (a.Member("pid_start") is { IsText: true } pidStart && pidStart.AsString().Length > 0)
        {
            record["pid_start"] = pidStart;
        }

        if (a.Member("pid_proof") is { IsNull: false } pidProof)
        {
            record["pid_proof"] = IsTrue(pidProof);
        }

        if (a.Member("ah_version") is { IsText: true } ahVersion && ahVersion.AsString().Length > 0)
        {
            record["ah_version"] = ahVersion;
        }

        if (a.Member(Ah.Core.Mux.HerdrFlavour.Field) is { IsText: true } flavour && flavour.AsString() is Ah.Core.Mux.HerdrFlavour.Tui or Ah.Core.Mux.HerdrFlavour.Core)
        {
            record[Ah.Core.Mux.HerdrFlavour.Field] = flavour;
        }

        if (a.Member("teams_stamp") is { IsText: true } teamsStamp && teamsStamp.AsString().Length > 0)
        {
            record["teams_stamp"] = teamsStamp;
        }

        var bound = IsTrue(a.Member("bound"));
        record["bound"] = bound;

        var path = _mailbox.RegistrationFile(JsConvert.ToJsString(a.Member("session_id")));

        // The one judgement of a registration write, whichever store holds the registration it is judged against.
        SessionDecision Decide(JsonObject? stored)
        {
            if (stored is not null)
            {
                // a write from a client that does not name its build must not erase the build the session registered with
                if (!record.ContainsKey("ah_version") && stored.Get("ah_version") is { IsText: true } keptVersion && keptVersion.AsString().Length > 0)
                {
                    record["ah_version"] = keptVersion;
                }

                // a write that names no flavour keeps the stored one: a resume into another herdr server names its own and re-stamps,
                // so only a resume into tmux or a plain terminal keeps an old value, and herdr routing is not used there
                if (!record.ContainsKey(Ah.Core.Mux.HerdrFlavour.Field) && stored.Get(Ah.Core.Mux.HerdrFlavour.Field) is { IsText: true } keptFlavour)
                {
                    // in the place a stated flavour takes (before bound), so an otherwise equal write still reads unchanged
                    record.Remove("bound");
                    record[Ah.Core.Mux.HerdrFlavour.Field] = keptFlavour;
                    record["bound"] = bound;
                }

                var heldBound = IsTrue(stored.Get("bound"));
                var heldLive = HeldIsLive(stored, a);
                if (heldBound && !bound && heldLive)
                {
                    return new SessionDecision(SessionVerdict.Refused, Why: "bound");
                }

                if (IsTrue(a.Member("guard")) && !(bound && !heldBound) && heldLive && ClaimsAnotherProcess(stored, a))
                {
                    return new SessionDecision(SessionVerdict.Refused, Why: "held");
                }

                var rest = stored.Clone();
                rest.Remove("at");

                // a registration written before the key existed is unbound; it stays as it is until a claim differs from it
                var comparable = record;
                if (!bound && stored.Get("bound") is null)
                {
                    comparable = record.Clone();
                    comparable.Remove("bound");
                }

                if (JsonText.Stringify(rest, 0) == JsonText.Stringify(comparable, 0))
                {
                    // `at` means "registered since": an unchanged registration keeps its bytes and mtime
                    return new SessionDecision(SessionVerdict.Unchanged);
                }
            }

            record["at"] = Now;
            return new SessionDecision(SessionVerdict.Write, Utf8Text.Encode(JsonText.Stringify(record, 0) + "\n"));
        }

        if (Database() is { } authority)
        {
            var viaDatabase = authority.Register(path, sessionId, held => Decide(held is { } document ? ObjectOf(Encoding.UTF8.GetString(document.Span)) : null));
            if (viaDatabase.Code != NotDatabase)
            {
                if (viaDatabase.Code == "refused")
                {
                    return Refused(viaDatabase.Message);
                }

                if (!viaDatabase.Ok)
                {
                    throw new MailboxError(viaDatabase.Code, viaDatabase.Message);
                }

                return Flag("registered", true);
            }
        }

        // the files-mode store: the registration file is the record
        ctx.Mkdir(NodePath.Dirname(path));
        var decision = Decide(ReadObject(path));
        switch (decision.Verdict)
        {
            case SessionVerdict.Refused:
                return Refused(decision.Why!);
            case SessionVerdict.Write:
                ctx.Write(path, TmpNames.For(TmpNameStyle.PidTmp, path, System.Environment.ProcessId), decision.Document.ToArray());
                break;
        }

        return Flag("registered", true);
    }

    private const string NotDatabase = "not-database";

    /// <summary>The database's authority; null only where a rig composes no database.</summary>
    private ISessionAuthority? Database() => _sessions;

    private static bool IsTrue(JsonValue? value) => value is { Kind: JsonKind.Bool, IsTruthy: true };

    private static JsonValue Refused(string why) => new JsonObject { ["registered"] = false, ["refused"] = why };

    /// <summary>
    /// Whether the held registration names a live process. The caller's <c>held_alive</c> and <c>held_start</c> speak for
    /// <c>held_pid</c> only; a held start time that differs from the recorded one is a reused pid, so that registration is dead.
    /// </summary>
    private bool HeldIsLive(JsonObject held, JsonValue a)
    {
        if (held.Get("pid") is not { IsNumber: true } heldPid || !double.IsInteger(heldPid.AsNumber())
            || heldPid.AsNumber() <= 1 || heldPid.AsNumber() > int.MaxValue)
        {
            return false;
        }

        var hinted = a.Member("held_pid") is { IsNumber: true } hintPid && hintPid.AsNumber() == heldPid.AsNumber();
        var alive = hinted && a.Member("held_alive") is { Kind: JsonKind.Bool } hintAlive ? IsTrue(hintAlive) : _mailbox.PidAlive(heldPid);
        if (!alive)
        {
            return false;
        }

        if (hinted && held.Get("pid_start") is { IsText: true } recorded && recorded.AsString().Length > 0
            && a.Member("held_start") is { IsText: true } hintStart && hintStart.AsString() != recorded.AsString())
        {
            return false;
        }

        return true;
    }

    /// <summary>Whether the claim names a pid other than the held registration's (or none).</summary>
    private static bool ClaimsAnotherProcess(JsonObject held, JsonValue a) =>
        a.Member("pid") is not { IsNumber: true } claimed || held.Get("pid") is not { IsNumber: true } heldPid || claimed.AsNumber() != heldPid.AsNumber();

    private JsonObject? ReadObject(string path)
    {
        try
        {
            return ObjectOf(_files.ReadText(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The registration a document holds; null when it is not a JSON object.</summary>
    private static JsonObject? ObjectOf(string text)
    {
        try
        {
            return JsonText.Parse(text) is { IsMap: true } v ? v.AsObject() : null;
        }
        catch (JsonParseException)
        {
            return null;
        }
    }

    private JsonValue Unregister(OpCall call, JsonValue a, IStoreWriteContext ctx)
    {
        var sessionId = Falsy(a.Member("session_id"));
        if (!Mailbox.IsName(sessionId))
        {
            return Flag("removed", false);
        }

        var path = _mailbox.RegistrationFile(sessionId);
        if (Database() is { } authority)
        {
            var viaDatabase = authority.Unregister(path, sessionId);
            if (viaDatabase.Code != NotDatabase)
            {
                if (!viaDatabase.Ok)
                {
                    throw new MailboxError(viaDatabase.Code, viaDatabase.Message);
                }

                RemoveSessionMark(ctx, sessionId);
                return Flag("removed", viaDatabase.Changed);
            }
        }

        bool removed;
        try
        {
            removed = ctx.Remove(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            removed = false;
        }

        RemoveSessionMark(ctx, sessionId);
        return Flag("removed", removed);
    }

    private string SessionMark(string sessionId) => NodePath.Join(_environment.AhHome, "session-marks", sessionId);

    /// <summary>The mod's hint that this session registered, in place before its registration and on a replay too: a stale mark is harmless and a missing one loses mail.</summary>
    private void WriteSessionMark(OpCall call, IStoreWriteContext ctx)
    {
        var sessionId = Falsy((call.ArgsJson.IsEmpty ? new JsonObject() : JsonText.Parse(call.ArgsJson.Span)).Member("session_id"));
        if (Mailbox.IsName(sessionId))
        {
            var mark = SessionMark(sessionId);
            ctx.Mkdir(NodePath.Dirname(mark));
            _ = ctx.CreateExclusive(mark, []);
        }
    }

    private void RemoveSessionMark(IStoreWriteContext ctx, string sessionId)
    {
        try
        {
            _ = ctx.Remove(SessionMark(sessionId));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // a stale mark costs one CLI answer
        }
    }

    /// <summary>
    /// A routing row. <c>seq</c> makes it a ladder step: written only while the entry's ladder has exactly <c>seq - 1</c> rows, which
    /// needs the lock. <c>once</c> writes it only when the entry has no routing row yet, and is skipped without the lock.
    /// </summary>
    private JsonValue Attempt(OpCall call, JsonValue a, IStoreWriteContext ctx)
    {
        var dir = TextOf(a, "dir");
        var row = a.Member("row") ?? JsonValue.Null;
        var id = row.Member("id");
        if (a.Member("seq") is { IsNumber: true } seq)
        {
            if (!call.Locked)
            {
                throw new IOException($"the mailbox at {dir} is locked by another writer; try again");
            }

            var ladder = _mailbox.AttemptsOf(dir, id).Count(r => r.Get("ladder") is { IsBool: true } l && l.AsBool() && StrictEquals(r.Get("id"), id));
            if (ladder + 1 != seq.AsNumber())
            {
                return Flag("written", false);
            }
        }

        if (a.Member("once") is { IsTruthy: true } && (!call.Locked || _mailbox.AttemptsOf(dir, id).Any(r => StrictEquals(r.Get("id"), id))))
        {
            return Flag("written", false);
        }

        WriteAttemptRow(ctx, dir, row);
        return Flag("written", true);
    }

    private static bool StrictEquals(JsonValue? a, JsonValue? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }

        var (x, y) = (a.Value, b.Value);
        return (x.IsNull && y.IsNull)
            || (x.IsText && y.IsText && string.Equals(x.AsString(), y.AsString(), StringComparison.Ordinal))
            || (x.IsNumber && y.IsNumber && x.AsNumber() == y.AsNumber())
            || (x.IsBool && y.IsBool && x.AsBool() == y.AsBool());
    }

    /// <summary>The id check, the entry and its routing row, under the box's append lock.</summary>
    private JsonValue Append(OpCall call, JsonValue a, IStoreWriteContext ctx)
    {
        var dir = TextOf(a, "dir");
        var entry = a.Member("entry") ?? throw new ArgumentException("entry is required");
        var line = EntryLine(entry);
        ctx.Mkdir(dir);
        var id = entry.Member("id");
        var rows = _mailbox.ScanEntries(dir).Rows;
        if (rows.Any(r => StrictEquals(r.Entry.AsObject().Get("id"), id)))
        {
            return Flag("existed", true);
        }

        if (entry.Member("kind") is { IsText: true } kind && kind.AsString() == "report" && entry.Member("ref_sha256") is { IsText: true } digest)
        {
            var same = rows.Select(r => r.Entry.AsObject()).FirstOrDefault(e =>
                StrictEquals(e.Get("kind"), kind) && StrictEquals(e.Get("ref_sha256"), digest)
                && StrictEquals(e.Get("from"), entry.Member("from")) && StrictEquals(e.Get("ref"), entry.Member("ref")));
            if (same?.Get("id") is { IsText: true } sameId)
            {
                return new JsonObject { ["existed"] = true, ["same"] = sameId };
            }
        }

        var file = NodePath.Join(dir, "entries.jsonl");
        var lead = EndsWithoutNewline(file) ? "\n" : string.Empty;
        ctx.Append(file, Utf8Text.Encode(lead + line));
        if (a.Member("attempt") is { IsMap: true } attempt)
        {
            WriteAttemptRow(ctx, dir, attempt);
        }

        if (a.Member("claim") is { IsMap: true } claim && id is { IsText: true } claimId && Mailbox.IsName(claimId.AsString())
            && _mailbox.ReadClaim(dir, claimId.AsString()) is null)
        {
            WriteClaim(ctx, dir, claimId.AsString(), claim.AsObject());
        }

        return Flag("existed", false);
    }

    /// <summary>The entry as one line of text, refused over the entry limit.</summary>
    private static string EntryLine(JsonValue entry)
    {
        var line = JsonText.Stringify(entry, 0) + "\n";
        var bytes = Utf8Text.Encode(line).Length;
        if (bytes > 4096)
        {
            throw new MailboxError("bad_request", $"entry would be {bytes.ToString(System.Globalization.CultureInfo.InvariantCulture)} bytes; the limit is 4096");
        }

        return line;
    }

    private bool EndsWithoutNewline(string file)
    {
        try
        {
            var bytes = _files.ReadBytes(file);
            return bytes.Length > 0 && bytes[^1] != 0x0a;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private JsonObject AttemptRecord(JsonValue row)
    {
        var record = new JsonObject { ["v"] = 1 };
        if (row.IsMap)
        {
            foreach (var key in row.AsObject().Keys)
            {
                record[key] = row.AsObject()[key];
            }
        }

        record["at"] = Now;
        return record;
    }

    private void WriteAttemptRow(IStoreWriteContext ctx, string dir, JsonValue row)
    {
        var record = AttemptRecord(row);
        ctx.Mkdir(dir);
        ctx.Append(NodePath.Join(dir, "attempts.jsonl"), Utf8Text.Encode(JsonText.Stringify(record, 0) + "\n"));
    }

    private static string HeartbeatName(JsonValue a) => Mailbox.IsName(Falsy(a.Member("session_id"))) ? JsConvert.ToJsString(a.Member("session_id")) : "anon";

    private JsonObject HeartbeatRecord(JsonValue a, long ackedThrough)
    {
        var record = new JsonObject { ["v"] = 1 };
        if (a.Member("adapter") is { } adapter)
        {
            record["adapter"] = adapter;
        }

        record["session_id"] = a.Member("session_id") ?? JsonValue.Null;
        if (a.Member("poll_ms") is { } poll)
        {
            record["poll_ms"] = poll;
        }

        record["at"] = Now;
        record["acked_through"] = ackedThrough;
        return record;
    }

    private JsonValue Heartbeat(OpCall call, JsonValue a, IStoreWriteContext ctx)
    {
        var dir = TextOf(a, "dir");
        var adapters = NodePath.Join(dir, "adapters");
        ctx.Mkdir(adapters);
        var record = HeartbeatRecord(a, _mailbox.AckedThrough(dir));
        var path = NodePath.Join(adapters, $"{HeartbeatName(a)}.json");
        var tmp = TmpNames.For(TmpNameStyle.PidTmp, path, System.Environment.ProcessId);
        ctx.Write(path, tmp, Utf8Text.Encode(JsonText.Stringify(record, 0) + "\n"));
        return Flag("ok", true);
    }

    private static void WriteClaim(IStoreWriteContext ctx, string dir, string id, JsonObject record)
    {
        var file = Mailbox.ClaimFile(dir, id);
        ctx.Mkdir(NodePath.Dirname(file));
        var tmp = TmpNames.For(TmpNameStyle.PidTmp, file, System.Environment.ProcessId);
        ctx.Write(file, tmp, Utf8Text.Encode(JsonText.Stringify(record, 0) + "\n"));
    }

    /// <summary>One claim transition: the record is read, the action applied, and the new record and its attempt rows written in the same lock hold.</summary>
    private JsonValue Claim(OpCall call, JsonValue a, IStoreWriteContext ctx)
    {
        var dir = TextOf(a, "dir");
        var id = TextOf(a, "id");
        var action = TextOf(a, "action");
        if (!Mailbox.IsName(id))
        {
            throw new ArgumentException("id must match [A-Za-z0-9._-]+");
        }

        var step = ClaimMachine.Apply(_mailbox.ReadClaim(dir, id), action, a.AsObject(), _clock.UtcNow, _mailbox.IsAcked(dir, id));
        if (step.Record is { } record)
        {
            WriteClaim(ctx, dir, id, record);
            foreach (var row in step.Rows)
            {
                var full = new JsonObject { ["id"] = id };
                foreach (var key in row.Keys)
                {
                    full[key] = row[key];
                }

                WriteAttemptRow(ctx, dir, full);
            }
        }

        return ClaimMachine.ToJson(step.Result);
    }

    /// <summary>
    /// A small record a hook keeps outside any box (a relay confirm that could not reach its box, a transcript scan offset), written
    /// or removed without a box lock under <c>&lt;AH_HOME&gt;/mailbox-notes/&lt;kind&gt;/&lt;name&gt;.json</c>.
    /// </summary>
    private JsonValue Note(OpCall call, JsonValue a, IStoreWriteContext ctx)
    {
        var kind = TextOf(a, "kind");
        var name = TextOf(a, "name");
        if (!Mailbox.IsName(kind) || !Mailbox.IsName(name))
        {
            throw new ArgumentException("kind and name must match [A-Za-z0-9._-]+");
        }

        var file = Mailbox.NoteFile(_environment.AhHome, kind, name);
        if (IsTrue(a.Member("clear")))
        {
            return Flag("removed", ctx.Remove(file));
        }

        var data = a.Member("data") is { IsMap: true } map ? map : throw new ArgumentException("data must be an object");
        if (a.Member("expect_seq") is { IsNumber: true } expect)
        {
            // a compare-and-set under the domain lock: of two writers that read the same record, only the first writes
            var held = _mailbox.ReadNote(kind, name)?.Get("seq") is { IsNumber: true } seq ? seq.AsNumber() : 0;
            if (held != expect.AsNumber())
            {
                return Flag("written", false);
            }
        }

        ctx.Mkdir(NodePath.Dirname(file));
        var tmp = TmpNames.For(TmpNameStyle.PidTmp, file, System.Environment.ProcessId);
        ctx.Write(file, tmp, Utf8Text.Encode(JsonText.Stringify(data, 0) + "\n"));
        return Flag("written", true);
    }

    private JsonValue Ack(OpCall call, JsonValue a, IStoreWriteContext ctx)
    {
        var dir = TextOf(a, "dir");
        var id = TextOf(a, "id");
        var file = Mailbox.AckFile(dir, id);
        ctx.Mkdir(NodePath.Dirname(file));
        return Flag("fresh", ctx.CreateExclusive(file, Utf8Text.Encode(JsonText.Stringify(AckRecord(a, id), 0) + "\n")));
    }

    private JsonObject AckRecord(JsonValue a, string id)
    {
        var record = new JsonObject { ["v"] = 1, ["id"] = id, ["at"] = Now };
        if (a.Member("by") is { } by)
        {
            record["by"] = by;
        }

        if (a.Member("reason") is { IsText: true } reason)
        {
            record["reason"] = reason;
        }

        if (a.Member("session_id") is { IsTruthy: true } session)
        {
            record["session_id"] = session;
        }

        return record;
    }
}
