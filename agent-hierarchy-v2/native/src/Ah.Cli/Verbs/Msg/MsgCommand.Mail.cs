using System.Globalization;
using System.Text;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Mesh;

namespace Ah.Cli.Verbs.Msg;

/// <summary>The mailbox verbs: <c>send</c>, <c>inbox</c>, <c>ack</c> and <c>daemon status</c>.</summary>
internal sealed partial class MsgCommand
{
    private static readonly int LockWaitMs =(int)ILockProvider.CliWait.TotalMilliseconds;

    private static readonly string[] UsageCodes = ["usage", "bad_request", "rejoin_refused", "supersede_refused", "decline_refused", "not_your_session", "ambiguous_session"];

    private static readonly (string Field, string Option)[] RequestFields =
        [("kind", "kind"), ("ref", "ref"), ("text", "text"), ("event", "event"), ("reply_to", "reply-to"), ("eta", "eta"), ("supersedes", "supersedes")];

    private static readonly string[] RegistrationFields = ["address", "harness", "adapter"];

    private static readonly string[] LadderCounts = ["pending", "sending", "paused", "escalated"];

    private bool _warned;

    /// <summary>What the last daemon request told: whether it timed out, and when it went out.</summary>
    private sealed class DaemonTrace
    {
        internal bool TimedOut { get; set; }

        internal DateTimeOffset? SentAt { get; set; }
    }

    /// <summary>A refusal the mailbox verbs print as <c>error: &lt;code&gt;: &lt;message&gt;</c>.</summary>
    private sealed class MailFailure(string code, string message) : Exception(message)
    {
        internal string Code { get; } = code;
    }

    private int MailVerb()
    {
        try
        {
            return _cmd switch
            {
                "send" => MailSend(),
                "inbox" => MailInbox(),
                "next" => MailNext(),
                "release" => MailRelease(),
                "status" => MailStatus(),
                "decline" => MailDecline(),
                "withdraw" => MailWithdraw(),
                "forward" => MailForward(),
                "close" => MailClose(),
                "join" => JoinDecision(),
                "link-request" or "link" => LinkRequestOrLink(),
                _ => MailAck(),
            };
        }
        catch (MailFailure failure)
        {
            var hint = _cmd == "send" && failure.Code == "bad_request" && (failure.Message == "note needs --text or --ref <file>" || failure.Message.Contains("characters; the limit is", StringComparison.Ordinal))
                ? "; a report goes as the response: `ah msg fill --id <id> --from <your role> --body-file -`, then `ah msg send <orchestrator> --kind report --ref <id>`; longer text goes in a file: `ah msg send <address> --kind note --ref <abs file> [--text <short line>]`"
                : string.Empty;
            _out.Note($"error: {failure.Code}: {failure.Message}{hint}");
            return Array.IndexOf(UsageCodes, failure.Code) >= 0 ? 2 : 1;
        }
    }

    private static T Unwrap<T>(MailboxOutcome<T> outcome) =>
        outcome.Failure is { } f ? throw new MailFailure(f.Code, f.Message) : outcome.Value!;

    private string? Str(string key) => _opts.Text(key);

    /// <summary>
    /// The caller's session, derived from this process: <c>--session</c> or <c>CLAUDE_CODE_SESSION_ID</c>, and
    /// <c>--orchestrator-pid</c> or <c>CLAUDE_PID</c>, are only hints, and one this process does not provably hold is refused.
    /// With no hint, the one session it holds; with none, or with several, no session (a write then refuses as ambiguous; see
    /// <see cref="WritingSession"/>).
    /// </summary>
    private string? SessionId => Choice(write: false).Session?.SessionId;

    /// <summary><see cref="SessionId"/> for a verb that writes as the session: several held sessions and no hint is ambiguous.</summary>
    private string? WritingSession => Choice(write: true).Session?.SessionId;

    private SessionChoice Choice(bool write)
    {
        // ah2-32: a hint the environment carries is refused only when this process holds a session it could contradict. A session
        // with no team registers nothing, and the user (the admin session included) holds none: its CLAUDE_PID and session id are
        // then just where it runs, and it writes as no session, like the user at a terminal. A hint given as an option still refuses.
        // ah2-50: the admin holds its own sessions too, but its environment still refuses nothing: its session id is a hint only when
        // it names one of them, else the newest is taken; only an option is checked.
        var holdsNone = Principal.Kind != PrincipalKind.Session || Principal.Sessions.Count == 0;
        var pidHint = _opts.Has("orchestrator-pid") ? _opts.Text("orchestrator-pid") : !holdsNone && _env.GetVariable("CLAUDE_PID") is { Length: > 0 } p ? p : null;
        if (pidHint is not null && !Principal.Sessions.Any(held => held.Pid.ToString(CultureInfo.InvariantCulture) == pidHint.Trim()))
        {
            throw new MailFailure("not_your_session", $"pid {SafeText.Render(pidHint)} is not one this process holds{Recover}");
        }

        // `route --session` names the session whose route is set, not the caller.
        var envSession = _env.GetVariable("CLAUDE_CODE_SESSION_ID") is { Length: > 0 } s ? s : null;
        var hint = (_cmd == "route" ? null : _opts.Text("session"))
            ?? (!holdsNone || Principal.Sessions.Any(held => held.SessionId == envSession) ? envSession : null);
        if (Principal.Kind != PrincipalKind.Session && Principal.Sessions.Count == 0)
        {
            return hint is null
                ? new SessionChoice(null, null, [])
                : throw new MailFailure("not_your_session", $"session {SafeText.Render(hint)} is not one this process holds{Recover}");
        }

        var choice = MeshModule.ChooseSession(Principal, hint, write);
        return choice.Refusal switch
        {
            null => choice,
            "ambiguous_session" => throw new MailFailure("ambiguous_session", $"this process holds {Principal.Sessions.Count} sessions; pass --session to say which"),
            var code => throw new MailFailure(code, $"session {SafeText.Render(hint ?? string.Empty)} is not one this process holds{Recover}"),
        };
    }

    /// <summary>How a session whose registration is lost or unproven gets one back.</summary>
    private const string Recover = " — no live registration this process can prove names it; run `ah roster checkin` in this session to re-register";

    /// <summary>Who this process is, by its own ancestry: the walk starts at the CLI process itself.</summary>
    private CallerPrincipal Principal => _principal ??= _graph.Mailbox.PrincipalOf(Environment.ProcessId);

    private CallerPrincipal? _principal;

    /// <summary>The harness pid of <paramref name="session"/>'s proven registration, or null.</summary>
    private int? PidOf(string session) => Principal.Sessions.FirstOrDefault(r => r.SessionId == session)?.Pid;

    /// <summary>The harness pid of the caller's session, or null when it has none.</summary>
    private int? SessionPid => SessionId is { } session ? PidOf(session) : null;

    private string Where => Path.GetFullPath(_cwd);

    /// <summary>
    /// The caller's boxes. With several held sessions and no hint, a read gets the union of each one's and a write
    /// (<paramref name="write"/>) is refused as ambiguous.
    /// </summary>
    private IReadOnlyList<CallerBox> Me(bool write = false)
    {
        var session = write ? WritingSession : SessionId;
        if (session is null && Principal.Sessions.Count > 1)
        {
            return [.. HeldSessions().SelectMany(s => _graph.Callers.CallerBoxes(CallerFor(s))).DistinctBy(b => b.Address, StringComparer.Ordinal)];
        }

        return _graph.Callers.CallerBoxes(CallerFor(session));
    }

    /// <summary>The request that names <paramref name="session"/> (null: none) with this process's pane; the one place the caller's request is built.</summary>
    private CallerRequest CallerFor(string? session)
    {
        var pane = Ah.Core.Mux.HerdrFlavour.SessionPaneId(_env.GetVariable) is { Length: > 0 } herdr ? herdr : _env.GetVariable("TMUX_PANE");
        return new CallerRequest(
            Where, session, session is null ? null : PidOf(session), string.IsNullOrEmpty(pane) ? null : pane, HerdrFlavour: Ah.Core.Mux.HerdrFlavour.SessionFlavour(_env.GetVariable));
    }

    private IEnumerable<string> HeldSessions() => Principal.Sessions.Select(r => r.SessionId);

    /// <summary>The addresses a write acts as: the caller's boxes. The admin (ah2-50) still sends as the user, so it has none.</summary>
    private List<string> WriteAddresses()
    {
        return Principal.Kind == PrincipalKind.User ? [] : Me(write: true).Select(b => b.Address).ToList();
    }

    /// <summary>ah2-150: the <c>orchestrator@</c> boxes of the admin's own session, which its send with no --from acts as; none for any other caller.</summary>
    private List<string> AdminOrchestratorAddresses()
    {
        if (Principal.Kind != PrincipalKind.User || Principal.Sessions.Count == 0)
        {
            return [];
        }

        try
        {
            return Me(write: true).Select(b => b.Address).Where(static a => a.StartsWith("orchestrator@", StringComparison.Ordinal)).ToList();
        }
        catch (MailFailure)
        {
            // D5: a writing session that cannot be resolved or is not held sends as the user, as before ah2-150, never a refusal
            return [];
        }
    }

    /// <summary>The session a send names: <see cref="WritingSession"/>, but none for the admin, which still sends as the user (ah2-50).</summary>
    private string? SendingSession => Principal.Kind == PrincipalKind.User ? null : WritingSession;

    /// <summary>The caller-derived fields every mailbox request carries.</summary>
    private static void AddContext(JsonObject target, RequestContext ctx)
    {
        target["cwd"] = ctx.Cwd;
        target["hierarchy_dir"] = ctx.HierarchyDirectory;
        target["main_hierarchy_dir"] = ctx.MainHierarchyDirectory.Length > 0 ? ctx.MainHierarchyDirectory : JsonValue.Null;
        target["messaging_socket_dir"] = ctx.SocketDirectory.Length > 0 ? ctx.SocketDirectory : JsonValue.Null;
    }

    // ---------------------------------------------------------------- the daemon first

    /// <summary>Tells once that the files are being used and records why.</summary>
    private void Warn(string reason)
    {
        if (!_warned)
        {
            _out.Note($"ah msg: warning: ahd unavailable ({reason}); using files");
            _graph.Daemon.RecordFallback(_cmd, reason);
        }

        _warned = true;
    }

    /// <summary>
    /// The body of the daemon's answer, or null to use the files. A 4xx carrying an error code is the same refusal the files would
    /// raise; one naming a rejected context field is the daemon's complaint about this client's view, so the files decide.
    /// </summary>
    private JsonValue? ViaDaemon(string method, string path, JsonObject? body, TimeSpan timeout, DaemonTrace? trace = null)
    {
        if (_env.GetVariable("AH_DAEMON") == "off" || Ah.Mesh.MeshModule.HoldExemptActive)
        {
            return null;
        }

        var request = new DaemonRequest(
            method,
            path,
            body is null ? default : Encoding.UTF8.GetBytes(JsonText.Stringify(body, 0)),
            timeout);
        var reply = _graph.Daemon.RequestAsync(request).GetAwaiter().GetResult();
        if (trace is not null)
        {
            trace.TimedOut = reply.TimedOut;
            trace.SentAt = reply.SentAt;
        }

        if (reply.Status == 0)
        {
            Warn(reply.FallbackReason ?? "down");
            return null;
        }

        JsonValue? parsed = JsonText.TryParse(reply.Body.Span, out var value) ? value : (JsonValue?)null;
        if (reply.Status is >= 200 and < 300)
        {
            return parsed is { IsMap: true } ? parsed : null;
        }

        var error = parsed?.Member("error");
        if (error is { IsMap: true } e)
        {
            var code = e.Member("code");
            if (e.Member("field") is { IsTruthy: true })
            {
                var reason = $"http {reply.Status.ToString(CultureInfo.InvariantCulture)} {(code is { IsText: true } c ? c.AsString() : "-")}";
                Warn(reason);
                return null;
            }

            if (code is { IsText: true } named)
            {
                throw new MailFailure(Inert(named.AsString()), e.Member("message") is { } m ? Inert(JsConvert.ToJsString(m)) : string.Empty);
            }
        }

        Warn($"http {reply.Status.ToString(CultureInfo.InvariantCulture)} -");
        return null;
    }

    /// <summary>A daemon's words made safe to print: no control or format characters, so an error text cannot drive the terminal.</summary>
    internal static string Inert(string text)
    {
        var kept = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            if (!Rune.IsControl(rune) && Rune.GetUnicodeCategory(rune) != UnicodeCategory.Format)
            {
                kept.Append(rune.ToString());
            }
        }

        return kept.ToString();
    }

    // ---------------------------------------------------------------- send

    private int MailSend()
    {
        var to = _opts.Positional.Count > 0 ? _opts.Positional[0] : string.Empty;
        if (to.Length == 0)
        {
            throw new MailFailure("bad_request", "send needs a recipient: ah msg send <name@team> ...");
        }

        // an id captured by a pipeline that failed arrives empty: say so where SendCore would give the three-way error (same test as there)
        if (_opts.Has("ref") && string.IsNullOrEmpty(Str("ref")))
        {
            var refGiven = Str("ref") is not null;
            var threeWay = Str("event") is not null ? refGiven : refGiven == (Str("text") is not null) && Str("kind") != "note";
            if (threeWay)
            {
                throw new MailFailure("bad_request", "--ref: the id is empty");
            }
        }

        var ctx = _graph.Callers.RequestContext(Where);
        var mailbox = _graph.Mailbox;
        string team;
        string? toDir = null;
        string? toHome = null;
        if (to.StartsWith("*@", StringComparison.Ordinal))
        {
            team = to[2..];
        }
        else
        {
            var resolved = Unwrap(mailbox.ResolveAddress(to, ctx));
            team = resolved.Team;
            toDir = resolved.Dir;
            toHome = resolved.Home;
        }

        var explicitFrom = Str("from");
        var from = Unwrap(mailbox.ChooseFrom(WriteAddresses(), team, explicitFrom, string.IsNullOrEmpty(explicitFrom) ? AdminOrchestratorAddresses() : null, Str("team")));
        var request = new JsonObject
        {
            ["to"] = to,
            ["from"] = from == "user" && string.IsNullOrEmpty(explicitFrom) ? JsonValue.Null : from,
            // ah2-150: an admin sending as its team's Orchestrator names its own session, which holds that address
            ["session_id"] = (from != "user" && Principal.Kind == PrincipalKind.User ? WritingSession : SendingSession) is { } sid ? sid : JsonValue.Null,
        };
        AddContext(request, ctx);
        request["id"] = Str("id") is { Length: > 0 } given ? given : mailbox.NewId();
        foreach (var (field, option) in RequestFields)
        {
            if (Str(option) is { } value)
            {
                request[field] = field != "ref" || value.Length == 0 || Path.IsPathRooted(value) ? value
                    : !value.Contains('/') && Hier.Messages.IsMessageId(value) ? (Str("kind") == "note" ? ResolveNoteRef(value) : ResolveHandle(value, Str("kind") == "report" ? "response" : "request"))
                    : Path.GetFullPath(value, _cwd);
            }
        }

        if (!request.ContainsKey("reply_to") && request.Get("ref") is { IsText: true } reference && reference.AsString().EndsWith("--response.md", StringComparison.Ordinal)
            && NoteIdOf(reference.AsString()) is { } noteId)
        {
            request["reply_to"] = noteId;
        }

        if (Ah.Mesh.MeshModule.HoldExemptActive)
        {
            request["hold_exempt"] = true;
        }

        JsonObject result;
        try
        {
            result = SendEntry(request);
        }
        catch (MailFailure refused) when (refused.Code == "from_mismatch" && string.IsNullOrEmpty(explicitFrom) && from != "user" && Principal.Kind == PrincipalKind.User)
        {
            // ah2-150 D7: the admin's own choice of sender never costs the send; once more as before ah2-150, as the user
            from = "user";
            request["from"] = JsonValue.Null;
            request["session_id"] = SendingSession is { } own ? own : JsonValue.Null;
            result = SendEntry(request);
        }

        JournalReport(result, ctx, toHome, team, from);
        var text = mailbox.RenderSend(result);
        RecordRelayObligation(result, toDir);
        var printed = Print(result, text, 0);
        if (result.Get("superseded_row_error") is { IsText: true } rowError)
        {
            var replaced = result.Get("entry") is { IsMap: true } sent && sent.Member("supersedes") is { IsText: true } target ? target.AsString() : string.Empty;
            _out.Note($"superseded row for {replaced} not written: {rowError.AsString()}");
            return 1;
        }

        return printed;
    }

    /// <summary>Records, for the sending session, the SendMessage a send's result asks it to make, so its Stop hook can hold it to it.</summary>
    private void RecordRelayObligation(JsonObject result, string? toDir)
    {
        if (result.Get("sendmessage") is { IsTruthy: true } relay && SessionId is { Length: > 0 } session && _graph.Mailbox.LiveClaudeSender(session))
        {
            try
            {
                Hier.Peers.AppendRelayRecord(
                    session,
                    new JsonObject
                    {
                        ["entry"] = result.Get("entry")!.Value.Member("id") ?? JsonValue.Null,
                        ["address"] = result.Get("address") ?? JsonValue.Null,
                        ["dir"] = toDir is null ? JsonValue.Null : toDir,
                        ["to"] = relay.Member("to") ?? JsonValue.Null,
                        ["line"] = _graph.Mailbox.SendMessageLine(relay),
                    });
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // the send itself succeeded; a failed obligation write must not fail it
            }
        }
    }

    /// <summary>
    /// The id in the frontmatter of the response file <paramref name="path"/> when it names a note entry in one of the caller's own
    /// boxes: the report answers that note, and the sent entry says so with <c>reply_to</c>. Null for a response to a request file.
    /// </summary>
    private string? NoteIdOf(string path)
    {
        try
        {
            return Hier.Core.ReadMsgFile(path).Member("fm")?.Member("id") is { IsText: true } id && CallerNote(id.AsString()) is not null ? id.AsString() : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Appends a delivered report to its stream's journal. The send has already succeeded, so a failure here only adds one line on
    /// stderr and a hook-error row.
    /// </summary>
    private void JournalReport(JsonObject result, RequestContext ctx, string? teamHome, string team, string from)
    {
        if (result.Get("entry") is not { IsMap: true } entry
            || entry.Member("kind") is not { IsText: true } kind || kind.AsString() != "report"
            || entry.Member("ref") is not { IsText: true } reference)
        {
            return;
        }

        try
        {
            var pool = teamHome is { Length: > 0 } ? teamHome : ctx.MainHierarchyDirectory.Length > 0 ? ctx.MainHierarchyDirectory : ctx.HierarchyDirectory;
            var progress = Hier.Progress;
            if (progress.IsOff(pool, team))
            {
                return;
            }

            var member = from.Split('@')[0];
            var stream = progress.StreamOf(pool, team, member);
            var appended = progress.AppendReport(pool, team, stream, progress.Describe(pool, team, stream), member, reference.AsString());
            if (appended.Skipped is { } skipped)
            {
                throw new InvalidOperationException($"the journal was written by a newer ah ({skipped})");
            }

            progress.RecordReport(pool, team, stream, member, reference.AsString(), appended);
            try
            {
                _ = progress.UpdateRollupBlock(pool, team, stream, member, reference.AsString());
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                _out.Note($"ah: rollup not updated ({e.Message}); the report was delivered.");
                _graph.Io.HookErrors?.Log(new HookErrorRecord("progress-rollup", e.Message));
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _out.Note($"ah: progress journal not updated ({e.Message}); the report was delivered.");
            _graph.Io.HookErrors?.Log(new HookErrorRecord("progress-journal", e.Message));
        }
    }

    /// <summary>One mailbox send: the daemon when it is up, the files when it is not. An id minted here makes either path safe to repeat.</summary>
    private JsonObject SendEntry(JsonObject request)
    {
        var trace = new DaemonTrace();
        if (ViaDaemon("POST", "/v1/send", request, ILockProvider.DaemonWait, trace) is { IsMap: true } sent)
        {
            return sent.AsObject();
        }

        // Only when the send request itself timed out does the fallback wait for what is left of the bound, measured from the
        // request going out; a timeout is not non-delivery, so a busy answer there means the delivery is unknown.
        var waitMs = trace.TimedOut && trace.SentAt is { } at
            ? Math.Max(0, LockWaitMs - (_graph.Io.Clock.UtcNow - at).TotalMilliseconds)
            : LockWaitMs;
        var outcome = _graph.Sender.Send(request, null, TimeSpan.FromMilliseconds(waitMs));
        if (trace.TimedOut && outcome.Failure is { Code: "busy" })
        {
            throw new MailFailure(
                "unknown",
                $"send timed out; delivery unknown. The entry may still land: retry the same send with --id {JsConvert.ToJsString(request.Get("id"))} (never a new id)");
        }

        return Unwrap(outcome);
    }

    private int Print(JsonValue result, string text, int code)
    {
        if (_opts.IsBare("json"))
        {
            _out.Json(result);
        }
        else
        {
            _out.Text(text);
        }

        return code;
    }

    // ---------------------------------------------------------------- inbox

    private int MailInbox()
    {
        var ctx = _graph.Callers.RequestContext(Where);
        var mailbox = _graph.Mailbox;
        List<(string Address, string Dir)> targets;
        if (Str("address") is { Length: > 0 } address)
        {
            if (!MeshModule.MayActFor(Principal, Choice(write: false), address, mailbox))
            {
                throw new MailFailure("not_your_session", $"{SafeText.Render(address)} is not an address this process may read");
            }

            targets = [(address, Unwrap(mailbox.ResolveAddress(address, ctx)).Dir)];
        }
        else
        {
            targets = Me().Select(b => (b.Address, b.Dir)).ToList();
        }

        if (targets.Count == 0)
        {
            throw new MailFailure("unknown_address", "cannot tell which address this session is; pass --address <name@team>");
        }

        var all = _opts.IsBare("all");
        var overdue = _opts.IsBare("overdue");
        var parts = new List<JsonObject>();
        foreach (var (targetAddress, dir) in targets)
        {
            var query = new StringBuilder("/v1/inbox?address=").Append(Uri.EscapeDataString(targetAddress));
            query.Append("&cwd=").Append(Uri.EscapeDataString(ctx.Cwd));
            query.Append("&hierarchy_dir=").Append(Uri.EscapeDataString(ctx.HierarchyDirectory));
            if (ctx.MainHierarchyDirectory.Length > 0)
            {
                query.Append("&main_hierarchy_dir=").Append(Uri.EscapeDataString(ctx.MainHierarchyDirectory));
            }

            if (ctx.SocketDirectory.Length > 0)
            {
                query.Append("&messaging_socket_dir=").Append(Uri.EscapeDataString(ctx.SocketDirectory));
            }

            if (all)
            {
                query.Append("&all=1");
            }

            if (overdue)
            {
                query.Append("&overdue=1");
            }

            var part = new JsonObject { ["address"] = targetAddress };
            if (ViaDaemon("GET", query.ToString(), null, TimeSpan.FromSeconds(30)) is { IsMap: true } answer)
            {
                foreach (var key in answer.AsObject().Keys)
                {
                    part[key] = answer.AsObject()[key];
                }
            }
            else
            {
                var found = mailbox.Inbox(dir, new InboxOptions(All: all, Overdue: overdue));
                var list = new JsonArray();
                foreach (var entry in found.Entries)
                {
                    list.Add(entry);
                }

                part["entries"] = list;
                part["next"] = found.Next;
            }

            var view = new LadderView(mailbox, RosterCore, Config, Hier, _graph.Io.Clock);
            var nowMs = (double)_graph.Io.Clock.UtcNow.ToUnixTimeMilliseconds();
            if (view.Annotate(Unwrap(mailbox.ResolveAddress(targetAddress, ctx)), EntriesOf(part).ToList(), nowMs) is { } annotated)
            {
                var list = new JsonArray();
                foreach (var entry in annotated)
                {
                    list.Add(entry);
                }

                part["entries"] = list;
            }

            parts.Add(part);
        }

        JsonObject result;
        List<JsonValue> entries;
        if (parts.Count == 1)
        {
            result = parts[0];
            entries = EntriesOf(parts[0]).ToList();
        }
        else
        {
            var addresses = new JsonArray();
            foreach (var p in parts)
            {
                addresses.Add(p.Get("address") ?? JsonValue.Null);
            }

            entries = parts.SelectMany(EntriesOf).OrderBy(e => JsConvert.ToJsString(e.Member("created")), StringComparer.Ordinal).ToList();
            var merged = new JsonArray();
            foreach (var e in entries)
            {
                merged.Add(e);
            }

            result = new JsonObject
            {
                ["address"] = parts[0].Get("address") ?? JsonValue.Null,
                ["addresses"] = addresses,
                ["entries"] = merged,
            };
        }

        return Print(result, mailbox.RenderInbox(entries), 0);
    }

    private static IEnumerable<JsonValue> EntriesOf(JsonObject part) =>
        part.Get("entries") is { IsArray: true } list ? list.AsArray().ToList() : [];

    // ---------------------------------------------------------------- ack

    private int MailAck()
    {
        var targets = Me(write: true);
        if (targets.Count == 0)
        {
            throw new MailFailure("unknown_address", "cannot tell which address this session is");
        }

        if (_opts.Positional.Count == 0)
        {
            throw new MailFailure("bad_request", "ack needs at least one entry id");
        }

        var ids = _opts.Positional.ToList();
        var ctx = _graph.Callers.RequestContext(Where);
        var acked = new HashSet<string>(StringComparer.Ordinal);
        foreach (var target in targets)
        {
            var body = new JsonObject { ["address"] = target.Address };
            AddContext(body, ctx);
            var idList = new JsonArray();
            foreach (var id in ids)
            {
                idList.Add(id);
            }

            body["ids"] = idList;
            body["by"] = "cli";
            if (SessionId is { Length: > 0 } session)
            {
                body["session_id"] = session;
            }

            if (ViaDaemon("POST", "/v1/ack", body, TimeSpan.FromSeconds(30)) is { IsMap: true } answer
                && answer.Member("acked") is { IsArray: true } fromDaemon)
            {
                foreach (var id in fromDaemon.AsArray())
                {
                    acked.Add(JsConvert.ToJsString(id));
                }

                continue;
            }

            foreach (var id in _graph.Mailbox.AckIds(target.Dir, ids, "cli", SessionId).Acked)
            {
                acked.Add(id);
            }
        }

        if (SessionId is { Length: > 0 } reportSession)
        {
            foreach (var target in targets)
            {
                foreach (var entry in _graph.Mailbox.ReadEntries(target.Dir).Entries)
                {
                    if (entry.Member("kind") is { IsText: true } kind && kind.AsString() == "report"
                        && entry.Member("id") is { IsText: true } entryId && acked.Contains(entryId.AsString())
                        && entry.Member("ref") is { IsText: true } reference
                        && Hier.Core.ParseMsgFilename(reference.AsString()) is { Type: "request" or "response" } named)
                    {
                        RecordReportAckFor(reportSession, named.Id);
                        ChecklistAcked(named.Id);
                    }
                }
            }
        }

        // An id no mailbox entry answered to is a request id only when this session dispatched that request; acking it records that
        // this session has read the response filed for it. Any other id stays unknown.
        if (SessionId is { Length: > 0 } ackSession)
        {
            var dispatched = _graph.Hierarchy.Peers.DispatchRecordsFor(ackSession)
                .Select(r => r.Member("request_id") is { IsText: true } rid ? rid.AsString() : null)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var id in ids.Where(id => !acked.Contains(id) && dispatched.Contains(id)))
            {
                if (RequestTeamOf(id) is { } requestTeam && IsOwnName("orchestrator", requestTeam))
                {
                    _graph.Hierarchy.Peers.AppendOrchestratorAck(ackSession, id, requestTeam);
                }
                else
                {
                    _graph.Hierarchy.Peers.AppendReportRecord("report-acked", ackSession, id);
                }

                ChecklistAcked(id);
                _ = acked.Add(id);
            }
        }

        var exchanges = new HashSet<string>(StringComparer.Ordinal);
        var refused = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var id in ids.Where(id => !acked.Contains(id)))
        {
            switch (AckExchange(id))
            {
                case "acked":
                    _ = acked.Add(id);
                    _ = exchanges.Add(id);
                    break;
                case { } why when why != "unknown":
                    refused[id] = why;
                    break;
            }
        }

        var done = ids.Where(acked.Contains).ToList();
        var unknown = ids.Where(id => !acked.Contains(id) && !refused.ContainsKey(id)).ToList();
        var doneJson = new JsonArray();
        foreach (var id in done)
        {
            doneJson.Add(id);
        }

        var unknownJson = new JsonArray();
        foreach (var id in unknown)
        {
            unknownJson.Add(id);
        }

        var refusedJson = new JsonObject();
        foreach (var (id, why) in refused)
        {
            refusedJson[id] = why;
        }

        var result = new JsonObject { ["acked"] = doneJson, ["unknown"] = unknownJson, ["refused"] = refusedJson };
        var text = string.Join(
            "\n",
            ids.Select(id => acked.Contains(id) ? (exchanges.Contains(id) ? $"acked {id} (exchange)" : $"acked {id}")
                : refused.TryGetValue(id, out var reason) ? $"{reason} {id}"
                : $"unknown {id}"));
        return Print(result, text, unknown.Count > 0 || refused.Count > 0 ? 1 : 0);
    }

    /// <summary>
    /// A report entry that names an exchange was acked: the report for that exchange has been read, so the exchange's alarms for a missing report
    /// stop. A ref that names no exchange of this repository records nothing.
    /// </summary>
    private void RecordReportAckFor(string session, string requestId)
    {
        JsonValue? fm;
        try
        {
            fm = FindExchange(requestId, string.Empty).Fm;
        }
        catch (Exception e) when (e is MailFailure { Code: "unknown" } or IOException or UnauthorizedAccessException)
        {
            return;
        }

        if (FrontText(fm?.Member("team")) is { } team && IsOwnName("orchestrator", team))
        {
            _graph.Hierarchy.Peers.AppendOrchestratorAck(session, requestId, team);
        }
        else
        {
            _graph.Hierarchy.Peers.AppendReportRecord("report-acked", session, requestId);
        }
    }

    /// <summary>The team the request <paramref name="id"/> was filed in, or null when no exchange of that id is found or it names no team.</summary>
    private string? RequestTeamOf(string id)
    {
        try
        {
            return FrontText(FindExchange(id, string.Empty).Fm?.Member("team"));
        }
        catch (MailFailure e) when (e.Code == "unknown")
        {
            return null;
        }
    }

    /// <summary>
    /// <c>ack</c> of an exchange id: the dispatcher of the request, holding its filled response, has read it. Records the ack for this session and
    /// moves the linked checklist item. False when the id is no exchange of this caller's, so it stays unknown;
    /// an exchange of the caller's with no filled response is refused.
    /// </summary>
    private string AckExchange(string id)
    {
        (JsonValue Exchange, JsonValue RequestPath, JsonValue? Fm) found;
        try
        {
            found = FindExchange(id, string.Empty);
        }
        catch (MailFailure e) when (e.Code == "unknown")
        {
            return "unknown";
        }

        var senderName = FrontText(found.Fm?.Member("from_name"));
        var sender = senderName ?? FrontText(found.Fm?.Member("from"));
        var requestTeam = FrontText(found.Fm?.Member("team"));
        if (sender is null || (senderName is null && requestTeam is null))
        {
            return "unknown";
        }

        if (!IsOwnName(sender, senderName is null ? requestTeam : null))
        {
            return $"sent by {sender}, not this session:";
        }

        var pools = Hier.Messages.PoolDirs(HierarchyDir, ExchangeTeams());
        if (found.Exchange.Member("response")?.Member("path") is not { IsText: true } responsePath || !Hier.Messages.ResponseFiled(pools, id))
        {
            return "has no response yet";
        }

        var team0 = requestTeam ?? _teamArg ?? "team";
        var topOrchestrator = senderName is null && sender == "orchestrator" && IsOwnName("orchestrator", team0);
        if (topOrchestrator)
        {
            _graph.Hierarchy.Peers.AppendOrchestratorAck(SessionId is { Length: > 0 } s ? JsonValue.FromString(s) : JsonValue.Null, id, team0);
        }
        else if (SessionId is { Length: > 0 } session)
        {
            _graph.Hierarchy.Peers.AppendReportRecord("report-acked", session, id);
            ChecklistAcked(id);
            return "acked";
        }
        else
        {
            return "not recorded: no session";
        }

        ChecklistAcked(id);

        try
        {
            var (rollupPool, rollupTeam) = ChecklistScope(found.Fm);
            _ = Hier.Progress.AcknowledgeRollupLine(rollupPool, rollupTeam, responsePath.AsString());
        }
        catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException)
        {
            _out.Note($"ah: rollup not updated ({e.Message}); the ack was recorded.");
        }

        return "acked";
    }

    /// <summary>
    /// <c>ah msg status &lt;entry-id&gt;</c>: where one entry stands (routed, pending, delivered, acked, superseded or declined) and whether
    /// a relay of it is still owed. Reads only; an entry outside this caller's boxes and teams is unknown.
    /// </summary>
    private int MailStatus()
    {
        if (_opts.Positional.Count != 1)
        {
            throw new MailFailure("usage", "status needs exactly one entry id");
        }

        var id = _opts.Positional[0];
        if (_graph.Mailbox.EntryStatus(id, Me()) is not { } status)
        {
            _out.Note($"ah msg status: unknown entry {id}");
            return 2;
        }

        return Print(status, StatusLine(status), 0);
    }

    private static string StatusLine(JsonObject status)
    {
        static string T(JsonObject o, string key) => o.Get(key) is { IsText: true } t ? t.AsString() : string.Empty;
        static JsonObject Part(JsonObject o, string key) => o.Get(key) is { IsMap: true } m ? m.AsObject() : new JsonObject();
        var delivered = Part(status, "delivered");
        var acked = Part(status, "acked");
        var declined = Part(status, "declined");
        var withdrawn = Part(status, "withdrawn");
        var state = T(status, "state");
        var tail = state switch
        {
            "acked" => $" by {T(acked, "by")}{(T(acked, "at").Length > 0 ? $" at {T(acked, "at")}" : string.Empty)} via {T(delivered, "via")}",
            "delivered" => $" by {T(delivered, "by")}{(T(delivered, "at").Length > 0 ? $" at {T(delivered, "at")}" : string.Empty)} via {T(delivered, "via")}",
            "declined" => $" by {T(declined, "by")} at {T(declined, "at")} ({T(declined, "reason")})",
            "superseded" => $" by {T(status, "superseded_by")}",
            "withdrawn" => $" by {T(withdrawn, "by")} at {T(withdrawn, "at")} ({T(withdrawn, "reason")}){(T(withdrawn, "readdressed_to").Length > 0 ? $"; re-addressed to {T(withdrawn, "readdressed_to")}{(withdrawn.Get("readdress_delivered") is { IsBool: true } d && !d.AsBool() ? " (not delivered)" : string.Empty)}" : string.Empty)}",
            _ => string.Empty,
        };
        var needed = status.Get("relay_needed") is { IsBool: true } n && n.AsBool();
        var why = !needed && T(status, "relay_reason") is { Length: > 0 } reason ? $" ({reason})" : string.Empty;
        return $"{T(status, "entry")} {T(status, "kind")} {T(status, "from")} \u2192 {T(status, "to")}: {state}{tail} relay needed: {(needed ? "yes" : "no")}{why}";
    }

    /// <summary>
    /// <c>ah msg release &lt;entry-id&gt;</c>: gives up a relay that was sent and never shown, so the daemon delivers the entry as a
    /// plugin prompt. Run by the session that holds the entry's box, or by its team's orchestrator.
    /// </summary>
    private int MailRelease()
    {
        var targets = Me(write: true);
        if (targets.Count == 0)
        {
            throw new MailFailure("unknown_address", "cannot tell which address this session is");
        }

        if (_opts.Positional.Count != 1)
        {
            throw new MailFailure("bad_request", "release needs exactly one entry id");
        }

        var id = _opts.Positional[0];
        var mailbox = _graph.Mailbox;
        var boxes = new List<(string Address, string Dir)>(targets.Select(t => (t.Address, t.Dir)));
        foreach (var target in targets.Where(t => t.Address.StartsWith("orchestrator@", StringComparison.Ordinal)))
        {
            var team = mailbox.SplitAddress(target.Address)?.Team;
            boxes.AddRange(mailbox.ListBoxes().Where(b => mailbox.SplitAddress(b.Address)?.Team == team).Select(b => (b.Address, b.Dir)));
        }

        foreach (var (address, dir) in boxes.Distinct())
        {
            if (mailbox.ReadClaim(dir, id) is null)
            {
                continue;
            }

            var actor = SessionId is { Length: > 0 } session ? session : "cli";
            var result = mailbox.Claim(dir, id, ClaimActions.Release, new JsonObject { ["reason"] = "manual-release", ["actor"] = actor });
            if (!result.Won)
            {
                throw new MailFailure("bad_request", $"entry {id} is {result.State}; only a sent relay that has not been acknowledged can be released");
            }

            var released = new JsonObject { ["released"] = id, ["address"] = address, ["state"] = result.State };
            return Print(
                released,
                $"released {id}: ah delivers it to {address} as a plugin prompt. If the message the host is holding is released later too, the entry shows twice.",
                0);
        }

        throw new MailFailure("unknown_address", $"entry {id} has no claim in a mailbox this session may release");
    }

    /// <summary>
    /// <c>ah msg decline &lt;entry-id&gt; --reason already-reported|superseded|withdrawn [--note &lt;text&gt;]</c>: the carrier of a relay request
    /// ends it, so the relay is not owed and never re-fires. The entry is not acknowledged.
    /// </summary>
    private int MailDecline()
    {
        var targets = Me(write: true);
        if (targets.Count == 0)
        {
            throw new MailFailure("unknown_address", "cannot tell which address this session is");
        }

        if (_opts.Positional.Count != 1)
        {
            throw new MailFailure("usage", "decline needs exactly one entry id");
        }

        var id = _opts.Positional[0];
        var reason = Str("reason");
        if (reason is null || !Array.Exists(DeclineReasons, r => r == reason))
        {
            throw new MailFailure("usage", $"decline needs --reason {string.Join("|", DeclineReasons)}");
        }

        var note = Str("note");
        var mailbox = _graph.Mailbox;
        var teams = targets.Select(t => mailbox.SplitAddress(t.Address)?.Team).Where(team => team is not null).ToHashSet(StringComparer.Ordinal);
        var box = mailbox.ListBoxes().Where(b => teams.Contains(mailbox.SplitAddress(b.Address)?.Team))
            .FirstOrDefault(b => mailbox.LadderRowsOf(b.Dir, id).Any(r => r.Get("rung") is { IsNumber: true } n && n.AsNumber() == 3 && r.Get("state") is { IsText: true } s && s.AsString() == "pending"));
        if (box is null)
        {
            throw new MailFailure("decline_refused", $"entry {id} has no relay request in a mailbox of your team");
        }

        var ctx = _graph.Callers.RequestContext(Where);
        var body = new JsonObject { ["address"] = box.Address, ["id"] = id, ["reason"] = reason };
        if (note is not null)
        {
            body["note"] = note;
        }

        AddContext(body, ctx);
        var proven = SessionId is { Length: > 0 } session && mailbox.ReadRegistration(session) is { } held && mailbox.RegistrationLive(held) ? session : null;
        if (proven is not null)
        {
            body["session_id"] = proven;
        }

        var answer = ViaDaemon("POST", "/v1/decline", body, TimeSpan.FromSeconds(30));
        var result = answer is { IsMap: true } fromDaemon
            ? fromDaemon.AsObject()
            : Unwrap(mailbox.Decline(box.Dir, id, reason, note, proven, [.. targets.Select(t => t.Address)]));
        var by = result.Get("by") is { IsText: true } carrier ? carrier.AsString() : string.Empty;
        return Print(result, $"declined {id} ({reason}) by {by}: the relay is no longer owed and the entry is still unread", 0);
    }

    private static readonly string[] DeclineReasons = ["already-reported", "superseded", "withdrawn"];

    // ---------------------------------------------------------------- daemon

    private int Daemon()
    {
        var sub = _opts.Positional.Count > 0 ? _opts.Positional[0] : null;
        switch (sub)
        {
            case "status":
                return DaemonStatus();
            case "start":
            case "stop":
                if (Ah.DaemonClient.DaemonClientModule.Lifecycle is not { } lifecycle)
                {
                    _out.Note("ah: msg: the daemon lifecycle has not been composed");
                    throw new MsgExit(MsgVerb.NotComposed);
                }

                return lifecycle.RunMsgAliasAsync(sub, _stdout, _stderr).GetAwaiter().GetResult();
            default:
                throw _out.Fail("usage: ah msg daemon start|status|stop [--json]");
        }
    }

    /// <summary>The daemon's GET answer for <paramref name="path"/>, or null when it is down or answers nothing usable (the files decide).</summary>
    private JsonObject? AskDaemon(string path)
    {
        var reply = _graph.Daemon
            .RequestAsync(new DaemonRequest("GET", path, default, TimeSpan.FromSeconds(10), StartIfDown: false))
            .GetAwaiter()
            .GetResult();
        if (reply.Status != 200 || !JsonText.TryParse(reply.Body.Span, out var value) || !value.IsMap)
        {
            return null;
        }

        return value.AsObject();
    }

    private int DaemonStatus()
    {
        var health = AskDaemon("/v1/health");
        var up = health is not null && !(health.Get("draining") is { IsTruthy: true });
        var registrations = (up ? AskDaemon("/v1/inspect/registrations") : null) ?? _graph.Inspect.Registrations();
        var boxes = (up ? AskDaemon("/v1/inspect/boxes") : null) ?? _graph.Inspect.Boxes();
        var timers = up ? AskDaemon("/v1/inspect/timers") : null;

        var live = new List<JsonValue>();
        if (registrations.Get("registrations") is { IsArray: true } rows)
        {
            live.AddRange(rows.AsArray().Where(r => r.Member("live") is { IsTruthy: true }));
        }

        var overdue = 0d;
        if (boxes.Get("boxes") is { IsArray: true } boxRows)
        {
            overdue = boxRows.AsArray().Sum(b => JsConvert.ToNumber(b.Member("overdue")));
        }

        var shown = new JsonArray();
        foreach (var r in live)
        {
            var item = new JsonObject();
            foreach (var key in RegistrationFields)
            {
                if (r.Member(key) is { } v)
                {
                    item[key] = v;
                }
            }

            shown.Add(item);
        }

        if (!up)
        {
            if (_opts.IsBare("json"))
            {
                _out.Json(new JsonObject { ["up"] = false, ["registrations"] = shown, ["overdue"] = overdue, ["load"] = JsonValue.Null });
            }
            else
            {
                _out.Text("down");
            }

            return 0;
        }

        var h = health!;
        var timerClock = h.Get("timer_state") is { IsMap: true } ts ? ts.AsObject() : new JsonObject();
        var ticks = new JsonObject();
        var timerRows = new List<JsonValue>();
        if (timers?.Get("timers") is { IsArray: true } listed)
        {
            timerRows.AddRange(listed.AsArray());
        }

        foreach (var t in timerRows)
        {
            var session = JsConvert.ToJsString(t.Member("session"));
            var state = timerClock.Get(session) is { IsMap: true } c ? c : (JsonValue?)null;
            ticks[session] = new JsonObject
            {
                ["last_tick"] = state?.Member("last_tick") ?? JsonValue.Null,
                ["last_event"] = state?.Member("last_event") ?? JsonValue.Null,
            };
        }

        var startedAt = h.Get("started_at") is { IsText: true } sa
            && DateTimeOffset.TryParse(sa.AsString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var started)
                ? started
                : (DateTimeOffset?)null;
        var uptime = startedAt is { } begun
            ? Math.Floor(((_graph.Io.Clock.UtcNow - begun).TotalMilliseconds / 1000) + 0.5)
            : double.NaN;
        var owns = h.Get("owns") is { IsTruthy: true } o ? o : JsonValue.Null;
        var ladder = h.Get("ladder") is { IsTruthy: true } l ? l : JsonValue.Null;
        var version = h.Get("version") ?? JsonValue.Null;
        var pid = h.Get("pid") ?? JsonValue.Null;
        var root = h.Get("root") ?? JsonValue.Null;

        if (_opts.IsBare("json"))
        {
            _out.Json(new JsonObject
            {
                ["up"] = true,
                ["version"] = version,
                ["pid"] = pid,
                ["uptime_s"] = double.IsNaN(uptime) ? JsonValue.Null : uptime,
                ["root"] = root,
                ["owns"] = owns,
                ["ladder"] = ladder,
                ["timer_state"] = ticks,
                ["registrations"] = shown,
                ["overdue"] = overdue,
                ["load"] = h.Get("load") is { IsMap: true } loadObject ? loadObject : JsonValue.Null,
            });
            return 0;
        }

        var ownsMap = owns.IsMap ? owns.AsObject() : new JsonObject();
        var ownedTimers = ownsMap.Get("timers") is { IsArray: true } ot ? ot.AsArray().Select(x => JsConvert.ToJsString(x)).ToList() : [];
        var lines = new List<string>
        {
            "up",
            $"version {JsConvert.ToJsString(version)}  pid {JsConvert.ToJsString(pid)}  uptime {(double.IsNaN(uptime) ? "NaN" : JsConvert.NumberToString(uptime))}s",
            $"root {JsConvert.ToJsString(root)}",
            $"owns mailbox={Undefined(ownsMap.Get("mailbox"))} status={Undefined(ownsMap.Get("status"))} timers={string.Join(",", ownedTimers)}",
        };
        foreach (var sid in ownedTimers)
        {
            var st = ticks.Get(sid) is { IsMap: true } s ? s : (JsonValue?)null;
            var lastTick = st?.Member("last_tick") is { IsTruthy: true } lt ? JsConvert.ToJsString(lt) : "none yet";
            var lastEvent = st?.Member("last_event") is { IsTruthy: true } le
                ? $"{JsConvert.ToJsString(le.Member("kind"))} at {JsConvert.ToJsString(le.Member("at"))}"
                : "none";
            lines.Add($"  timer {sid}: last tick {lastTick}; last event {lastEvent}");
        }

        lines.Add(
            $"registrations: {(live.Count > 0 ? string.Join("; ", live.Select(r => $"{JsConvert.ToJsString(r.Member("address"))} ({(r.Member("harness") is { IsTruthy: true } hv ? JsConvert.ToJsString(hv) : "?")}, adapter {Undefined(r.Member("adapter"))})")) : "none")}");
        lines.Add($"overdue: {JsConvert.NumberToString(overdue)}");
        lines.Add(LadderLine(ladder));
        lines.Add(LoadLine(h.Get("load")));
        _out.Text(string.Join("\n", lines));
        return 0;
    }

    private static string Undefined(JsonValue? v) => v is null ? "undefined" : JsConvert.ToJsString(v.Value);

    private static string LoadLine(JsonValue? load)
    {
        if (load is not { IsMap: true } l)
        {
            return "load: unknown";
        }

        var triggers = l.Member("triggers") is { IsArray: true } t ? string.Join("; ", t.AsArray().Select(x => JsConvert.ToJsString(x))) : string.Empty;
        var cpu = l.Member("cpu_note") is { IsText: true } note
            ? note.AsString()
            : $"cpu_busy {Undefined(l.Member("cpu_busy"))} (threshold {Undefined(l.Member("thresholds")?.Member("cpu_busy"))})";
        return $"load: {JsConvert.ToJsString(l.Member("state"))} since {JsConvert.ToJsString(l.Member("since"))}; {cpu}, load1_per_cpu {Undefined(l.Member("load1_per_cpu"))} of {Undefined(l.Member("cpus"))} cpus, mem_pressure {Undefined(l.Member("mem_pressure"))}, harnesses {Undefined(l.Member("harnesses"))}"
            + (triggers.Length > 0 ? $"; triggers: {triggers}" : string.Empty)
            + (l.Member("problem") is { IsText: true } p ? $"; problem: {p.AsString()}" : string.Empty);
    }

    private static string LadderLine(JsonValue ladder)
    {
        if (ladder.IsMap)
        {
            var l = ladder.AsObject();
            if (LadderCounts.Any(k => l.Get(k) is { IsTruthy: true }))
            {
                return $"ladder: pending {Undefined(l.Get("pending"))}, sending {Undefined(l.Get("sending"))}, paused {Undefined(l.Get("paused"))}, escalated {Undefined(l.Get("escalated"))}";
            }
        }

        return "ladder: none";
    }
}
