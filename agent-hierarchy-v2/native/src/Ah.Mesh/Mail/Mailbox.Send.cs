using System.Security.Cryptography;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Core.Time;

namespace Ah.Mesh;

internal sealed partial class Mailbox
{
    private static readonly string[] EntryKinds = ["brief", "report", "note", "event"];

    private static bool Has(JsonObject o, string key) => o.Get(key) is { IsNull: false };

    private static string Js(JsonValue? value) => JsConvert.ToJsString(value);

    private static string QuoteJson(string s) => JsonText.Quote(s);

    /// <summary>The caller-context fields of a request object.</summary>
    internal static RequestContext ContextOf(JsonObject o)
    {
        static string Field(JsonObject x, string key) => x.Get(key) is { IsTruthy: true } v ? JsConvert.ToJsString(v) : string.Empty;

        return new RequestContext(Field(o, "cwd"), Field(o, "hierarchy_dir"), Field(o, "main_hierarchy_dir"), Field(o, "messaging_socket_dir"));
    }

    private static IEnumerable<string> MsgsDirs(RequestContext context, string? extraHome)
    {
        var homes = new List<string>(HierarchyDirs(context));
        if (!string.IsNullOrEmpty(extraHome))
        {
            homes.Add(extraHome);
        }

        foreach (var home in homes)
        {
            yield return MsgRows.PoolKey(NodePath.Join(home, "msgs")); // a pool whose messages are all in the store has no such directory
        }
    }

    /// <summary>
    /// The msgs directories of the trees the sender's own team records: the home of its team file (the main checkout) and the
    /// worktree of every stream it opened, and the work root of every member. Only the team named in the sender's <c>name@team</c> address counts, so a tree of
    /// another team is not accepted even when the recipient belongs to it.
    /// </summary>
    private List<string> SenderTeamMsgsDirs(RequestContext context, JsonObject req)
    {
        var dirs = new List<string>();
        var sender = req.Get("from") is { IsText: true } f ? f.AsString().Split('@') : [];
        if (sender.Length != 2)
        {
            return dirs;
        }

        var named = VisibleTeams(context).Where(t => t.Team == sender[1]).ToList();
        if (named.Count != 1)
        {
            return dirs;
        }

        var homes = new List<string>();
        if (Ah.Roster.RosterModule.TeamFileHome(named[0].Path) is { Length: > 0 } main)
        {
            homes.Add(main);
        }

        if (named[0].Data.AsObject().Get("streams") is { IsMap: true } streams)
        {
            foreach (var key in streams.AsObject().Keys)
            {
                if (streams.AsObject().Get(key) is { IsMap: true } stream
                    && stream.AsObject().Get("worktree") is { IsText: true, } tree && tree.AsString().Length > 0)
                {
                    homes.Add(NodePath.Join(tree.AsString(), ".claude", "ah2"));
                }
            }
        }

        // a brief for a named member is written into that member's work root (msg new --to-name), whether or not a stream opened it
        if (named[0].Data.AsObject().Get("members") is { IsArray: true } members)
        {
            foreach (var member in members.AsArray())
            {
                if (member.Member("expected_root") is { IsText: true } root && root.AsString().Length > 0)
                {
                    homes.Add(NodePath.Join(root.AsString(), ".claude", "ah2"));
                }
            }
        }

        foreach (var home in homes)
        {
            dirs.Add(MsgRows.PoolKey(NodePath.Join(home, "msgs")));
        }

        return dirs;
    }

    public MailboxOutcome<JsonObject> Send(JsonObject request, DateTimeOffset? now = null)
    {
        try
        {
            return new MailboxOutcome<JsonObject>(SendCore(request, now ?? clock.UtcNow), null);
        }
        catch (MailboxError e)
        {
            return new MailboxOutcome<JsonObject>(null, e.ToFailure());
        }
    }

    private const string StepNeedsUser = "step-needs-user";

    private static string HierarchyChief(string team) => "chief@" + team;

    public string NewId() => NewIdAt(clock.UtcNow);

    /// <summary>The mint of an id as of <paramref name="now"/>, in the writer's local time.</summary>
    internal string NewIdAt(DateTimeOffset now) => Ids.NewId(new FixedClock(clock, now), random);

    private sealed class FixedClock(IClock inner, DateTimeOffset instant) : IClock
    {
        public DateTimeOffset UtcNow => instant;

        public TimeSpan LocalOffset(DateTimeOffset at) => inner.LocalOffset(at);
    }

    internal static string KindFromPayload(JsonObject r) =>
        r.Get("ref") is { IsTruthy: true } reference
            ? Js(reference).EndsWith("--response.md", StringComparison.Ordinal) ? "report" : "brief"
            : r.Get("event") is { IsTruthy: true } ? "event" : "note";

    /// <summary>Builds, routes and appends one entry; throws <see cref="MailboxError"/> before writing anything on a bad request.</summary>
    internal JsonObject SendCore(JsonObject req, DateTimeOffset now, TimeSpan? lockWait = null, Action<string>? mark = null)
    {
        var context = ContextOf(req);
        if (!NodePath.IsAbsolute(context.Cwd) || !NodePath.IsAbsolute(context.HierarchyDirectory))
        {
            throw new MailboxError("bad_request", "cwd and hierarchy_dir must be absolute paths");
        }

        var to = Resolve(req.Get("to") is { IsTruthy: true } t ? Js(t) : null, context);
        if (to.Name == "orchestrator" && req.Get("event") is { IsText: true } named && named.AsString() == StepNeedsUser && DeputyOnDutyLive(to.TeamPath, to.Team))
        {
            // the deputy on duty cannot ask the person, so a step that needs the user goes straight to the chief
            to = Resolve(HierarchyChief(to.Team), context);
        }
        // An event may carry text, and a note both a file and text (a note with neither is refused below, with its own message); otherwise exactly one of ref and text.
        var askedNote = req.Get("kind") is { IsText: true } asked && asked.AsString() == "note";
        if (Has(req, "event") ? Has(req, "ref") : Has(req, "ref") == Has(req, "text") && !askedNote)
        {
            throw new MailboxError("bad_request", "give exactly one of --ref, --text, --event (an event may also carry --text, a --kind note both --ref and --text)");
        }

        JsonValue kindValue = req.Get("kind") is { IsTruthy: true } k ? k : KindFromPayload(req);
        if (!(kindValue.IsText && EntryKinds.Contains(kindValue.AsString(), StringComparer.Ordinal)))
        {
            throw new MailboxError("bad_request", $"kind {QuoteJson(Js(kindValue))} is not brief|report|note|event");
        }

        var kind = kindValue.AsString();
        var idValue = req.Get("id") is { IsTruthy: true } given ? given : NewIdAt(now);
        var entry = new JsonObject
        {
            ["v"] = 1,
            ["id"] = idValue,
            ["from"] = req.Get("from") is { IsTruthy: true } from ? from : "user",
            ["to"] = to.Address,
            ["kind"] = kind,
            ["route"] = "pull",
            ["created"] = Timestamps.ToIsoString(now),
        };
        if (!IsName(Js(idValue)))
        {
            throw new MailboxError("bad_request", "id must match [A-Za-z0-9._-]+");
        }

        if (req.Get("to_session") is { IsTruthy: true } toSession)
        {
            entry["to_session"] = toSession;
        }

        if (kind is "brief" or "report")
        {
            if (!(req.Get("ref") is { IsText: true } refText && NodePath.IsAbsolute(refText.AsString())))
            {
                throw new MailboxError("bad_request", $"{kind} needs --ref <absolute path>");
            }

            var reference = refText.AsString();
            // A stored message has no file to resolve, and its pool may have no msgs directory, so the key names it.
            var file = RealPath(reference) is { Length: > 0 } real ? real : MsgRows.PathKey(reference);
            var refDir = NodePath.Dirname(file);
            if (!MsgsDirs(context, to.Home).Contains(file) && !MsgsDirs(context, to.Home).Contains(refDir) && !SenderTeamMsgsDirs(context, req).Contains(refDir))
            {
                throw new MailboxError("bad_request", $"--ref {reference} is not inside a hierarchy msgs/ directory");
            }

            if (!hierarchy.Core.MsgExists(reference) && !hierarchy.Core.MsgExists(file))
            {
                throw new MailboxError("bad_request", $"--ref {reference} is not an existing file");
            }

            if (!MsgsDirs(context, to.Home).Contains(refDir) && !SenderTeamMsgsDirs(context, req).Contains(refDir))
            {
                throw new MailboxError("bad_request", $"--ref {reference} is not inside a hierarchy msgs/ directory");
            }

            entry["ref"] = reference;
            if (kind == "report")
            {
                if (file.EndsWith("--response.md", StringComparison.Ordinal)
                    && hierarchy.Core.ParseMsgFilename(file)?.Id is { } responseId
                    && hierarchy.Messages.ReportStatus(file, responseId) == "no-report")
                {
                    throw new MailboxError("bad_request", $"response {responseId} is still the template — fill it with `ah msg fill --id {responseId} --from <your role> --body-file -`, then send again.");
                }

                SealRef(file);
                entry["ref_sha256"] = Convert.ToHexStringLower(SHA256.HashData(hierarchy.Core.MsgBytes(file) ?? throw new MailboxError("bad_request", $"--ref {reference} has no body")));
                if (RepeatOf(to.Dir, entry, file, now) is { } delivered)
                {
                    return Unchanged(delivered, to);
                }
            }
            else
            {
                SealRef(file);
            }
        }
        else if (kind == "note")
        {
            // A note may point at a file (an addendum, a long list) instead of, or as well as, carrying text.
            if (req.Get("ref") is { IsText: true } noteRef && noteRef.AsString().Length > 0)
            {
                // a bare id the CLI resolved is a stored message's key, with no file on disk
                if (!NodePath.IsAbsolute(noteRef.AsString()) || (files.Stat(noteRef.AsString()) is not { Kind: FileKind.File } && !hierarchy.Core.MsgExists(noteRef.AsString())))
                {
                    throw new MailboxError("bad_request", $"--ref {noteRef.AsString()} is not an existing file");
                }

                entry["ref"] = noteRef;
            }

            if (req.Get("text") is { IsText: true } text && text.AsString().Length > 0)
            {
                if (text.AsString().Length > MaxNoteChars)
                {
                    throw new MailboxError("bad_request", $"--text is {text.AsString().Length.ToString(System.Globalization.CultureInfo.InvariantCulture)} characters; the limit is {MaxNoteChars.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
                }

                entry["text"] = text;
            }
            else if (!entry.ContainsKey("ref"))
            {
                throw new MailboxError("bad_request", "note needs --text or --ref <file>");
            }
        }
        else
        {
            if (!(req.Get("event") is { IsText: true } ev && ev.AsString().Length > 0))
            {
                throw new MailboxError("bad_request", "event needs --event <name>");
            }

            entry["event"] = ev;
            if (req.Get("text") is { IsText: true } eventText)
            {
                if (eventText.AsString().Length > MaxEventTextChars)
                {
                    throw new MailboxError("bad_request", $"event text is {eventText.AsString().Length.ToString(System.Globalization.CultureInfo.InvariantCulture)} characters; the limit is {MaxEventTextChars.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
                }

                entry["text"] = eventText;
            }

            if (req.Get("data") is { IsTruthy: true } data && (data.IsMap || data.IsArray))
            {
                entry["data"] = data;
            }
        }

        if (req.Get("reply_to") is { IsTruthy: true } replyTo)
        {
            entry["reply_to"] = Js(replyTo);
        }

        if (kind is "note" or "event" && RepeatOf(to.Dir, entry, null, now) is { } repeated)
        {
            return Unchanged(repeated, to);
        }

        if (kind == "event" && EventStillApplies is { } stillApplies && !stillApplies(entry))
        {
            return Dropped(to);
        }

        var replaced = kind == "event" ? UnackedEventsOfSameMember(to.Dir, entry) : [];

        if (req.Get("eta") is { IsTruthy: true } eta)
        {
            entry["eta"] = Js(eta);
        }

        JsonObject? superseded = null;
        if (req.Get("supersedes") is { IsTruthy: true } supersedes)
        {
            superseded = SupersededEntry(to, entry, Js(supersedes));
            entry["supersedes"] = Js(supersedes);
        }

        // Set by the mesh send on a linked message; a request from outside never reaches here with it.
        if (req.Get("link") is { IsMap: true } link && link.AsObject().Get("id") is { IsText: true } linkId)
        {
            var linkFrom = link.AsObject().Get("from") is { IsTruthy: true } lf ? Js(lf) : Js(entry.Get("from"));
            entry["link"] = new JsonObject { ["id"] = linkId, ["from"] = linkFrom };
        }

        // Set by `ah msg forward`: the original's id and sender, and who forwarded it with what note.
        if (req.Get("fwd") is { IsMap: true } fwd)
        {
            entry["fwd"] = fwd;
        }

        // A quit's own brief is about that quit: a resume does not deliver it again.
        if (req.Get("hold_exempt") is { IsBool: true } holdExempt && holdExempt.AsBool())
        {
            entry["hold_exempt"] = true;
        }

        // An entry for one session counts that session's heartbeat only; otherwise the address is fresh when any session's is.
        var nowMs = (double)now.ToUnixTimeMilliseconds();
        var sessionFilter = req.Get("to_session") is { IsTruthy: true } ts ? ts : (JsonValue?)null;
        var hb = ReadHeartbeatOf(to.Dir, sessionFilter, nowMs);
        entry["route"] = FreshOf(hb, nowMs) ? "pull" : "fallback";
        if (Utf8Text.Encode(JsonText.Stringify(entry, 0) + "\n").Length > MaxEntryBytes)
        {
            throw new MailboxError("bad_request", $"entry would exceed {MaxEntryBytes.ToString(System.Globalization.CultureInfo.InvariantCulture)} bytes");
        }

        var claim = IsMemberDigestNote(JsonValue.FromObject(entry), to.Address) ? null : ClaimFor(entry, req, to, context, Js(entry.Get("created")));
        mark?.Invoke("resolve");
        var appended = Append(to.Dir, entry, AttemptRow(entry, hb), lockWait, claim);
        mark?.Invoke("append");
        var rowError = superseded is not null ? MarkSuperseded(to.Dir, superseded, Js(entry.Get("id"))) : null;
        var stored = appended.Get("entry")!.Value.AsObject();
        if (appended.Get("same") is { IsTruthy: true })
        {
            return WithRowError(Unchanged(stored, to), rowError);
        }

        if (replaced.Count > 0 && appended.Get("existed") is not { IsTruthy: true })
        {
            _ = AckIds(to.Dir, replaced, "superseded");
        }

        if (DrainIfHeld(to, context, req, stored) is { } drained)
        {
            return WithRowError(drained, rowError);
        }

        var existed = appended.Get("existed") is { IsTruthy: true };
        // An earlier send of this id that died between its append and its attempt row left no routing history; write it now.
        var attempt = existed ? AttemptFor(to.Dir, stored.Get("id")) : null;
        if (existed && attempt is null)
        {
            try
            {
                Invoke("attempt", new JsonObject { ["dir"] = to.Dir, ["row"] = AttemptRow(stored, ReadHeartbeat(to.Dir)), ["once"] = true });
            }
            catch (MailboxError)
            {
                // the routing row is evidence only: a repeat of this send writes it
            }

            attempt = AttemptFor(to.Dir, stored.Get("id"));
        }

        var result = WithRowError(Queued(WithCarrier(DescribeSend(stored, to, attempt, hb, context.SocketDirectory), stored, to, context.SocketDirectory), stored, to), rowError);
        mark?.Invoke("describe");
        return result;
    }

    /// <summary>
    /// A note that is not a <c>QUESTION:</c>, to an <c>orchestrator@</c> box the doorbell serves now (<see cref="DoorbellServes"/>, the prompt
    /// carrier's predicate): a digest item, which the recipient reads at its next turn and which never asks the sender for a SendMessage
    /// (fewer-turns S7.4, rulings G6 and S7-R1). So is a member's digest note (<see cref="IsMemberDigestNote"/>). Any other note keeps today's carrier.
    /// </summary>
    private bool IsDigestNote(JsonObject entry, ResolvedAddress to) =>
        IsMemberDigestNote(JsonValue.FromObject(entry), to.Address)
        || (Js(entry.Get("kind")) == "note" && !Js(entry.Get("text")).StartsWith("QUESTION:", StringComparison.Ordinal)
            && ClaudeReceiverOf(entry, new JsonObject(), to) is { } receiver && DoorbellServes(entry, to, receiver));

    /// <summary>
    /// For a digest note, a result that would ask for a SendMessage (a relay or fallback line) or name the plugin prompt is marked queued
    /// instead, and its SendMessage dropped so no relay obligation is recorded. Every other result is unchanged.
    /// </summary>
    private JsonObject Queued(JsonObject described, JsonObject stored, ResolvedAddress to)
    {
        if (!IsDigestNote(stored, to) || described.Get("claim_state") is { IsText: true }
            || (described.Get("sendmessage") is not { IsTruthy: true } && Js(described.Get("carrier")) is not ("prompt" or "relay")))
        {
            return described;
        }

        described.Remove("sendmessage");
        described.Remove("receiver_unregistered");
        described["queued"] = true;
        return described;
    }

    /// <summary>A send whose entry landed but whose <c>superseded</c> row did not carries the reason, so the caller can say so without losing the send.</summary>
    private static JsonObject WithRowError(JsonObject result, string? rowError)
    {
        if (rowError is not null)
        {
            result["superseded_row_error"] = rowError;
        }

        return result;
    }

    /// <summary>
    /// The unacked entry <paramref name="target"/> of the recipient's box that <paramref name="entry"/> replaces. Only the sender of the
    /// target may do this, and not once the target was picked up or its ladder ended; a repeat of the send that already did it passes.
    /// </summary>
    private JsonObject SupersededEntry(ResolvedAddress to, JsonObject entry, string target)
    {
        var old = ReadEntries(to.Dir).Entries.Where(e => e.IsMap).Select(e => e.AsObject())
            .FirstOrDefault(e => e.Get("id") is { IsText: true } id && id.AsString() == target);
        if (old is null || target == Js(entry.Get("id")))
        {
            throw new MailboxError("supersede_refused", $"--supersedes {target} names no entry in the box of {to.Address}");
        }

        if (Js(old.Get("from")) != Js(entry.Get("from")))
        {
            throw new MailboxError("supersede_refused", $"--supersedes {target}: only the sender of an entry can supersede it");
        }

        var rows = LadderRowsOf(to.Dir, target);
        var last = rows.Count > 0 && rows[^1].Get("state") is { IsText: true } state ? state.AsString() : null;
        if (last == "superseded" && rows[^1].Get("by") is { IsText: true } by && by.AsString() == Js(entry.Get("id")))
        {
            return old;
        }

        if (IsAcked(to.Dir, target) || last is "delivered" or "superseded" or "declined")
        {
            throw new MailboxError("supersede_refused", $"--supersedes {target}: it was already {(last is "superseded" or "declined" ? last : "picked up")}");
        }

        return old;
    }

    /// <summary>
    /// Writes the terminal <c>superseded</c> ladder row of <paramref name="old"/>, naming the entry that replaced it; once only. Returns
    /// why the row was not written (the old entry was picked up or its ladder ended in the meantime, or the append kept failing), else null.
    /// </summary>
    private string? MarkSuperseded(string dir, JsonObject old, string by)
    {
        var id = Js(old.Get("id"));
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var rows = LadderRowsOf(dir, id);
            var last = rows.Count > 0 && rows[^1].Get("state") is { IsText: true } state ? state.AsString() : null;
            if (last == "superseded")
            {
                return null;
            }

            if (last is "delivered" or "declined")
            {
                return $"{id} was already {last}";
            }

            if (IsAcked(dir, id))
            {
                return $"{id} was already picked up";
            }

            var rung = rows.Count > 0 && rows[^1].Get("rung") is { IsNumber: true } r ? r : JsonValue.FromNumber(Js(old.Get("route")) == "pull" ? 2 : 3);
            var row = new JsonObject { ["id"] = id, ["rung"] = rung, ["state"] = "superseded", ["by"] = by };
            try
            {
                if (AppendLadderRow(dir, row, rows.Count + 1L))
                {
                    return null;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or MailboxError)
            {
                return $"could not append the row for {id}: {e.Message}";
            }
        }

        return $"could not append the row for {id}";
    }

    private static bool IsClaudeKind(JsonValue member) =>
        member.Member("kind") is not { IsText: true } kind || kind.AsString() is "claude" or "claude-mod";

    /// <summary>
    /// Adds the carrier decided at creation to a send's result, and replaces the delivery hint with the one that carrier needs: the
    /// SendMessage to make for a relay, nothing for the plugin prompt. A result for an entry with no claim record is unchanged.
    /// </summary>
    private JsonObject WithCarrier(JsonObject described, JsonObject stored, ResolvedAddress to, string socketDirectory)
    {
        if (stored.Get("id") is not { IsText: true } id || ReadClaim(to.Dir, id.AsString()) is not { } claim
            || claim.Get("carrier") is not { IsText: true } carrier)
        {
            return described;
        }

        described["carrier"] = carrier.AsString();
        described.Remove("deliver");
        described.Remove("sendmessage");
        var state = ClaimMachine.StateOf(claim);
        if (state != ClaimStates.Pending)
        {
            described["claim_state"] = state;
            return described;
        }

        if (carrier.AsString() == "prompt" && to.Member is { IsMap: true } promptMember && IsClaudeKind(promptMember) && ClaudeReceiverOf(stored, new JsonObject(), to) is null)
        {
            described["receiver_unregistered"] = true;
        }

        if (carrier.AsString() == "relay")
        {
            var member = to.Member is { IsMap: true } m ? m.AsObject() : null;
            described["sendmessage"] = new JsonObject { ["to"] = SendTarget(to, member, socketDirectory), ["text"] = RelayLine(stored, to.Address) };
        }

        return described;
    }

    /// <summary>How long a note or event stays a repeat of an identical later send.</summary>
    internal static readonly TimeSpan RepeatWindow = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The entry of <paramref name="dir"/> that <paramref name="entry"/> repeats, or null. A report is a repeat of any entry, acked or
    /// not, with the same sender, ref file and content digest; an entry written before digests were stored never matches. A note or
    /// event is a repeat of one with the same sender, kind and content created within <see cref="RepeatWindow"/> that is not withdrawn
    /// or superseded.
    /// </summary>
    private JsonObject? RepeatOf(string dir, JsonObject entry, string? realRef, DateTimeOffset now)
    {
        var kind = Js(entry.Get("kind"));
        var sender = Js(entry.Get("from"));
        if (kind == "report")
        {
            return FindReport(dir, Js(entry.Get("ref")), realRef, from => from == sender, Js(entry.Get("ref_sha256")));
        }

        if (kind is not ("note" or "event"))
        {
            return null;
        }

        static string Content(JsonObject e) =>
            Js(e.Get("event")) + "\n" + Js(e.Get("text")) + "\n" + Js(e.Get("reply_to")) + "\n" + Js(e.Get("to_session")) + "\n" + Js(e.Get("ref")) + "\n" + (e.Get("data") is { } data ? JsonText.Stringify(data, 0) : string.Empty);

        var content = Content(entry);
        foreach (var v in ReadEntries(dir).Entries)
        {
            var e = v.AsObject();
            if (Js(e.Get("kind")) == kind && Js(e.Get("from")) == sender && Js(e.Get("to")) == Js(entry.Get("to")) && Js(e.Get("id")) != Js(entry.Get("id")) && Content(e) == content
                && Timestamps.TryParseMs(Js(e.Get("created")), out var createdMs) && now.ToUnixTimeMilliseconds() - createdMs <= RepeatWindow.TotalMilliseconds
                && !(LadderRowsOf(dir, Js(e.Get("id"))) is { Count: > 0 } ladder && ladder[^1].Get("state") is { IsText: true } state && state.AsString() is "withdrawn" or "superseded"))
            {
                return e;
            }
        }

        return null;
    }

    /// <summary>
    /// The <c>report</c> entry of <paramref name="dir"/>, acked or not, whose ref is <paramref name="reference"/> (or the same real path),
    /// whose sender satisfies <paramref name="fromOk"/> and, when <paramref name="digest"/> is given, whose digest equals it.
    /// </summary>
    internal JsonObject? FindReport(string dir, string reference, string? realRef, Func<string, bool> fromOk, string? digest)
    {
        var real = realRef ?? RealPath(reference);
        foreach (var v in ReadEntries(dir).Entries)
        {
            var e = v.AsObject();
            if (e.Get("kind") is { IsText: true } kind && kind.AsString() == "report"
                && (digest is null || (e.Get("ref_sha256") is { IsText: true } d && d.AsString() == digest))
                && e.Get("from") is { IsText: true } from && fromOk(from.AsString())
                && e.Get("ref") is { IsText: true } stored
                && (stored.AsString() == reference || (!string.IsNullOrEmpty(real) && RealPath(stored.AsString()) == real)))
            {
                return e;
            }
        }

        return null;
    }

    /// <summary>
    /// For the boxes <paramref name="dirs"/>, whether a <c>report</c> entry names a given path or the same real path: the answer
    /// <see cref="FindReport"/> gives for each path with any sender and no digest, from one read of the boxes instead of one per path.
    /// </summary>
    internal Func<string, bool> ReportRefsIn(IEnumerable<string> dirs, Func<string, string?>? realPath = null)
    {
        realPath ??= RealPath;
        List<BoxRefs> boxes;
        lock (_reportRefsGate)
        {
            boxes = [.. dirs.Select(ReportRefsOf).OfType<BoxRefs>()];
        }

        List<HashSet<string>>? real = null;
        return path =>
        {
            lock (_reportRefsGate)
            {
                if (boxes.Any(b => b.Stored.Contains(path)))
                {
                    return true;
                }

                if (realPath(path) is not { Length: > 0 } target)
                {
                    return false;
                }

                real ??= [.. boxes.Select(b => b.RealRefs(realPath))];
                return real.Any(refs => refs.Contains(target));
            }
        };
    }

    /// <summary>
    /// One box's report refs kept across calls. The real paths of the refs are resolved only when a path misses the stored names, and
    /// only for refs not yet resolved.
    /// </summary>
    private sealed class BoxRefs
    {
        public ulong Inode { get; init; }

        public ulong Device { get; init; }

        public long Size { get; set; }

        public double MtimeMs { get; set; }

        public long Offset { get; set; }

        public HashSet<string> Stored { get; } = new(StringComparer.Ordinal);

        private readonly List<string> _unresolved = [];
        private readonly HashSet<string> _real = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string?> _parents = new(StringComparer.Ordinal);

        public void Add(string reference)
        {
            if (Stored.Add(reference))
            {
                _unresolved.Add(reference);
            }
        }

        public HashSet<string> RealRefs(Func<string, string?> realPath)
        {
            if (_parents.Any(p => realPath(p.Key) != p.Value))
            {
                _real.Clear();
                _unresolved.Clear();
                _unresolved.AddRange(Stored);
                _parents.Clear();
            }

            var missing = new List<string>();
            foreach (var reference in _unresolved)
            {
                if (realPath(reference) is { Length: > 0 } resolved)
                {
                    _ = _real.Add(resolved);
                }
                else
                {
                    missing.Add(reference);
                }

                var parent = NodePath.Dirname(reference);
                if (!_parents.ContainsKey(parent))
                {
                    _parents[parent] = realPath(parent);
                }
            }

            _unresolved.Clear();
            _unresolved.AddRange(missing);
            return _real;
        }
    }

    private readonly Dictionary<string, BoxRefs> _reportRefs = new(StringComparer.Ordinal);
    private readonly object _reportRefsGate = new();

    /// <summary>
    /// The report refs of the box at <paramref name="dir"/>. A file whose identity, length and modification time are unchanged costs
    /// one stat; one that has only grown is read from the end of its last complete line; one that shrank, was replaced or changed
    /// in place is parsed again from the start. A box file that cannot be read has no refs and no cache entry.
    /// </summary>
    private BoxRefs? ReportRefsOf(string dir)
    {
        if (Stored is not null)
        {
            var stored = new BoxRefs();
            foreach (var row in ScanEntries(dir).Rows)
            {
                if (ReportRefOf(row.Entry) is { } reference)
                {
                    stored.Add(reference);
                }
            }

            return stored;
        }

        var path = NodePath.Join(dir, "entries.jsonl");
        if (files.Stat(path) is not { } now)
        {
            _ = _reportRefs.Remove(dir);
            return null;
        }

        var held = _reportRefs.GetValueOrDefault(dir);
        var reusable = held is not null && held.Inode == now.Inode && held.Device == now.Device
            && now.Size >= held.Size && (now.Size > held.Size || now.MtimeMs == held.MtimeMs);
        var refs = reusable ? held! : new BoxRefs { Inode = now.Inode, Device = now.Device };
        if (!reusable || now.Size > refs.Offset)
        {
            try
            {
                var bytes = reusable ? files.ReadRange(path, refs.Offset, (int)Math.Min(int.MaxValue, now.Size - refs.Offset)).Bytes : files.ReadBytes(path);
                ConsumeReportRefs(refs, bytes);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _ = _reportRefs.Remove(dir);
                return null;
            }
        }

        refs.Size = now.Size;
        refs.MtimeMs = now.MtimeMs;
        _reportRefs[dir] = refs;
        return refs;
    }

    private static void ConsumeReportRefs(BoxRefs refs, byte[] bytes)
    {
        var pos = 0;
        while (pos < bytes.Length)
        {
            var newline = Array.IndexOf(bytes, (byte)0x0a, pos);
            if (newline < 0)
            {
                break;
            }

            var text = JsText.Trim(Utf8Text.Decode(bytes.AsSpan(pos, newline - pos)));
            pos = newline + 1;
            if (text.Length == 0)
            {
                continue;
            }

            try
            {
                if (ReportRefOf(JsonText.Parse(text)) is { } reference)
                {
                    refs.Add(reference);
                }
            }
            catch (JsonParseException)
            {
            }
        }

        refs.Offset += pos;
    }

    private static string? ReportRefOf(JsonValue entry) =>
        entry.IsMap && entry.AsObject() is var e && e.Get("id") is { IsText: true }
        && e.Get("kind") is { IsText: true } kind && kind.AsString() == "report"
        && e.Get("from") is { IsText: true } && e.Get("ref") is { IsText: true } reference
            ? reference.AsString()
            : null;

    /// <summary>
    /// <paramref name="path"/> with its symbolic links resolved and no trailing separator. A path that does not exist keeps its
    /// own name under the resolved form of its nearest existing parent, so two spellings of one missing file still agree.
    /// </summary>
    internal string CanonicalPath(string path)
    {
        var trimmed = path.Length > 1 ? path.TrimEnd('/') : path;
        try
        {
            return files.RealPath(trimmed);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            var parent = NodePath.Dirname(trimmed);
            return parent == trimmed ? trimmed : NodePath.Join(CanonicalPath(parent), NodePath.Basename(trimmed));
        }
    }

    /// <summary>What delivers a <c>pull</c> entry: the plugin prompt for the <c>claude-mod</c> adapter, else that adapter's own pull.</summary>
    internal static string PullCarrierName(string? adapter) => adapter switch
    {
        null or "" => "the pull",
        "claude-mod" => "the plugin prompt",
        _ => $"the {adapter} pull",
    };

    /// <summary>Set by the mailbox hooks: whether an event's subject is still unresolved. An event it rejects is never appended.</summary>
    internal Func<JsonObject, bool>? EventStillApplies { get; set; }

    /// <summary>
    /// Ids of unacked events in the box with the same name, sender, recipient and <c>data.member</c> as <paramref name="entry"/>: a newer
    /// notice about the same member replaces them. An event that names no member replaces nothing.
    /// </summary>
    private List<string> UnackedEventsOfSameMember(string dir, JsonObject entry)
    {
        var ids = new List<string>();
        if (entry.Get("data")?.Member("member") is not { IsText: true } member || member.AsString().Length == 0)
        {
            return ids;
        }

        foreach (var v in ReadEntries(dir).Entries)
        {
            var e = v.AsObject();
            if (Js(e.Get("event")) == Js(entry.Get("event")) && Js(e.Get("from")) == Js(entry.Get("from")) && Js(e.Get("to")) == Js(entry.Get("to"))
                && e.Get("data")?.Member("member") is { IsText: true } other && other.AsString() == member.AsString()
                && SameSubject(e, entry, "request_id") && SameSubject(e, entry, "job_id")
                && e.Get("id") is { IsText: true } id && !IsAcked(dir, id.AsString()))
            {
                ids.Add(id.AsString());
            }
        }

        return ids;
    }

    /// <summary>Whether two events agree on <c>data.&lt;key&gt;</c>; absent on both counts as agreeing, present on only one does not.</summary>
    private static bool SameSubject(JsonObject a, JsonObject b, string key)
    {
        static string Of(JsonObject e, string key) => e.Get("data")?.Member(key) is { IsText: true } v ? v.AsString() : string.Empty;
        return Of(a, key) == Of(b, key);
    }

    private static JsonObject Dropped(ResolvedAddress to) =>
        new() { ["address"] = to.Address, ["delivery"] = "dropped", ["unchanged"] = true };

    /// <summary>Stores the request or response file a pointer names before the pointer is sent, so the recipient is never pointed at bytes the store lacks.</summary>
    private void SealRef(string file)
    {
        try
        {
            hierarchy.Messages.SealForSend(file);
        }
        catch (InvalidOperationException e)
        {
            throw new MailboxError("bad_request", e.Message);
        }
    }

    private static JsonObject Unchanged(JsonObject delivered, ResolvedAddress to)
    {
        var answer = new JsonObject { ["entry"] = delivered, ["address"] = to.Address, ["delivery"] = "unchanged", ["unchanged"] = true };
        if (Js(delivered.Get("kind")) != "report")
        {
            answer["existed"] = true;
        }

        return answer;
    }

    private static JsonObject AttemptRow(JsonObject entry, JsonObject? hb)
    {
        var pull = entry.Get("route") is { IsText: true } r && r.AsString() == "pull";
        var row = new JsonObject { ["id"] = entry.Get("id") ?? JsonValue.Null, ["rung"] = pull ? 2 : 3 };
        if (pull && hb?.Get("adapter") is { } adapter)
        {
            row["adapter"] = adapter;
        }

        row["state"] = pull ? "routed-pull" : "routed-fallback";
        return row;
    }

    /// <summary>The first routing row of entry <paramref name="id"/>, or null.</summary>
    internal JsonObject? AttemptFor(string dir, JsonValue? id)
    {
        if (Stored is not null)
        {
            return AttemptsOf(dir, id).FirstOrDefault(row => StrictEquals(row.Get("id"), id));
        }

        string text;
        try
        {
            text = files.ReadText(NodePath.Join(dir, "attempts.jsonl"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        foreach (var line in text.Split('\n'))
        {
            try
            {
                if (JsonText.Parse(line) is { IsMap: true } v && StrictEquals(v.AsObject().Get("id"), id))
                {
                    return v.AsObject();
                }
            }
            catch (JsonParseException)
            {
                // skip
            }
        }

        return null;
    }

    public JsonObject DescribeSend(JsonObject entry, ResolvedAddress recipient, JsonObject? attempt, JsonObject? heartbeat, string socketDirectory)
    {
        var route = Js(entry.Get("route"));
        var result = new JsonObject { ["entry"] = entry, ["address"] = recipient.Address, ["delivery"] = route };
        if (route == "pull")
        {
            var adapter = attempt?.Get("adapter") is { IsTruthy: true } a ? a : heartbeat?.Get("adapter") is { IsTruthy: true } h ? h : (JsonValue?)null;
            if (adapter is { } found)
            {
                result["adapter"] = found;
            }

            return result;
        }

        var member = recipient.Member is { IsMap: true } m ? m.AsObject() : null;
        if (member?.Get("kind") is { IsText: true } kind && kind.AsString() == "custom")
        {
            return result;
        }

        if (member?.Get("kind") is { IsTruthy: true })
        {
            result["deliver"] = new JsonObject { ["member"] = recipient.Name, ["command"] = $"ah roster deliver {recipient.Name}" };
            return result;
        }

        result["sendmessage"] = new JsonObject { ["to"] = SendTarget(recipient, member, socketDirectory), ["text"] = RelayLine(entry, recipient.Address) };
        return result;
    }

    internal static string SendTarget(ResolvedAddress to, JsonObject? member, string socketDirectory)
    {
        if (member is not null)
        {
            return Js(member.Get("name"));
        }

        if (to.Orchestrator is { IsMap: true } o && o.AsObject().Get("pid") is { IsNumber: true } pid
            && double.IsFinite(pid.AsNumber()) && pid.AsNumber() == Math.Floor(pid.AsNumber()))
        {
            var dir = string.IsNullOrEmpty(socketDirectory) ? "/tmp/cc-socks" : socketDirectory;
            return $"uds:{NodePath.Join(dir, $"{JsConvert.NumberToString(pid.AsNumber())}.sock")}";
        }

        return to.Address;
    }

    public int RelayHeaderLength(JsonValue entry, string address) => RelayHeader(entry.AsObject(), address).Length;

    private static string RelayHeader(JsonObject e, string address)
    {
        var from = e.Get("from") is { IsText: true } sender ? sender.AsString() : string.Empty;
        var to = e.Get("to") is { IsText: true } named && named.AsString().Length > 0 ? named.AsString() : address;
        var kind = e.Get("kind") is { IsText: true } k ? k.AsString() : string.Empty;
        return $"ah mail from {from} to {to}{(kind.Length > 0 ? $" ({kind})" : string.Empty)}: ";
    }

    public string RelayLine(JsonValue entry, string address)
    {
        var e = entry.AsObject();
        var envelope = e.Get("link") is { IsTruthy: true } link ? _core.EnvelopeLine(link.Member("id"), link.Member("from")) + "\n" : string.Empty;
        var body = e.Get("ref") is { IsTruthy: true } reference
            ? MsgPointer.ShowLine(Js(reference))
            : e.Get("text") is { IsTruthy: true } text ? Js(text) : Js(e.Get("event"));
        return $"{RelayHeader(e, address)}{envelope}{body} {EntryMarkerText.Format(Js(e.Get("id")), address)}";
    }

    public string RenderSend(JsonObject result)
    {
        if (result.Get("broadcast") is { IsMap: true } broadcast)
        {
            var delivered = broadcast.AsObject().Get("delivered") is { IsArray: true } list ? list.AsArray().Select(x => Js(x)).ToList() : [];
            return $"broadcast {Js(result.Get("entry")!.Value.AsObject().Get("id"))} → {Js(result.Get("address"))}: {delivered.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)} members ({string.Join(", ", delivered)})";
        }

        var lines = new List<string> { $"entry {Js(result.Get("entry")!.Value.AsObject().Get("id"))} → {Js(result.Get("address"))}" };
        if (result.Get("unchanged") is { IsTruthy: true })
        {
            return lines[0] + $" (already delivered; unchanged; {NothingToDo})";
        }

        if (Js(result.Get("delivery")) == DrainedState.State)
        {
            lines.Add($"delivery: drained — {Js(result.Get("message"))}; {NothingToDo}");
        }
        else if (result.Get("claim_state") is { IsText: true } past)
        {
            lines.Add($"delivery: already {past.AsString()} — {NothingToDo}");
        }
        else if (result.Get("queued") is { IsTruthy: true })
        {
            lines.Add(Ah.Config.IDirectiveText.DigestQueuedDelivery);
        }
        else if (Js(result.Get("carrier")) == "relay" && result.Get("sendmessage") is { IsTruthy: true } relay)
        {
            lines.Add($"delivery: relay — ah delivers it another way if you do not; make this one SendMessage now: SendMessage \"{Js(relay.Member("text"))}\" to {Js(relay.Member("to"))}");
        }
        else if (Js(result.Get("carrier")) == "prompt")
        {
            lines.Add($"delivery: plugin prompt — {NothingToDo}");
            if (result.Get("receiver_unregistered") is { IsTruthy: true })
            {
                lines.Add($"no single registered receiver for {Js(result.Get("address"))}; the ladder delivers it; {NothingToDo}");
            }
        }
        else if (Js(result.Get("delivery")) == "pull")
        {
            var adapter = result.Get("adapter") is { IsTruthy: true } ad ? Js(ad) : null;
            lines.Add($"delivery: pull{(adapter is null ? string.Empty : $" ({adapter})")} — {PullCarrierName(adapter)} delivers it; {NothingToDo}");
        }
        else if (result.Get("deliver") is { IsTruthy: true } deliver)
        {
            lines.Add($"delivery: fallback — {Js(deliver.Member("command"))}");
        }
        else if (result.Get("sendmessage") is { IsTruthy: true } send)
        {
            lines.Add(SendMessageLine(send));
        }
        else
        {
            lines.Add($"delivery: fallback — no carrier; a custom harness takes the entry when it next pulls; {NothingToDo}");
        }

        return string.Join("\n", lines);
    }

    /// <summary>The words every delivery line ends with when the sender has no action to take (ah2-317 E4).</summary>
    internal const string NothingToDo = "nothing more to do";

    public string SendMessageLine(JsonValue sendmessage) => $"delivery: fallback — SendMessage \"{Js(sendmessage.Member("text"))}\" to {Js(sendmessage.Member("to"))}";

    public bool LiveClaudeSender(string? sessionId) =>
        ReadRegistration(sessionId) is { } r
        && r.Get("harness") is { IsText: true } h && h.AsString() == "claude"
        && r.Get("pid") is { IsNumber: true } pid && double.IsFinite(pid.AsNumber()) && pid.AsNumber() == Math.Floor(pid.AsNumber())
        && hierarchy.Core.PidRunning(pid, r.Get("pid_start"), Ah.Core.Time.LivenessPolicy.RunningOrUnknown);

    public IReadOnlyList<string> TeamAncestors(string? team, Func<string, MeshTeam?> lookup)
    {
        var chain = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var name = team; !string.IsNullOrEmpty(name) && seen.Add(name) && chain.Count < 8;)
        {
            var parent = lookup(name)?.Data.Member("parent") is { IsMap: true } link && link.Member("team") is { IsText: true } t ? t.AsString() : null;
            if (string.IsNullOrEmpty(parent) || seen.Contains(parent) || lookup(parent) is null)
            {
                break;
            }

            chain.Add(parent);
            name = parent;
        }

        return chain;
    }

    public MailboxOutcome<string> ChooseFrom(IReadOnlyList<string> callerAddresses, string? toTeam, string? explicitFrom = null, IReadOnlyList<string>? adminOrchestrators = null, string? teamHint = null, IReadOnlyList<string>? toTeamAncestors = null)
    {
        if (!string.IsNullOrEmpty(explicitFrom))
        {
            // A session with no address may only say `user`; otherwise it could stamp any address on its mail.
            if (callerAddresses.Count == 0 && explicitFrom != "user")
            {
                return Refused("bad_request", $"--from {explicitFrom}: this session has no address; only --from user is allowed");
            }

            if (callerAddresses.Count > 0 && !callerAddresses.Contains(explicitFrom))
            {
                return Refused("bad_request", $"--from {explicitFrom} is not one of this session's addresses: {string.Join(", ", callerAddresses)}");
            }

            return new MailboxOutcome<string>(explicitFrom, null);
        }

        if (callerAddresses.Count == 0)
        {
            // ah2-150: an admin that is a team's Orchestrator sends as it: its one team, else the one --team or the recipient names
            var held = (adminOrchestrators ?? []).Distinct(StringComparer.Ordinal).ToList();
            var decided = held.Count == 1
                ? held[0]
                : new[] { teamHint, toTeam }.Where(static t => !string.IsNullOrEmpty(t)).Select(static t => $"orchestrator@{t}").FirstOrDefault(held.Contains);
            return new MailboxOutcome<string>(decided ?? "user", null);
        }

        if (callerAddresses.Count == 1)
        {
            return new MailboxOutcome<string>(callerAddresses[0], null);
        }

        var inTeam = InTeam(toTeam);
        if (inTeam.Count == 1)
        {
            return new MailboxOutcome<string>(inTeam[0], null);
        }

        // ah2-472: none in the recipient's own team (a child team), so its nearest ancestor holding exactly one decides
        if (inTeam.Count == 0 && (toTeamAncestors ?? []).Select(InTeam).FirstOrDefault(static held => held.Count == 1) is [var ancestral])
        {
            return new MailboxOutcome<string>(ancestral, null);
        }

        return new MailboxOutcome<string>(
            null,
            new MailboxFailure("ambiguous_address", $"this session has several addresses: {string.Join(", ", callerAddresses)}; pass --from <address>", callerAddresses));

        static MailboxOutcome<string> Refused(string code, string message) => new(null, new MailboxFailure(code, message));
        List<string> InTeam(string? team) => callerAddresses.Where(a => a.EndsWith($"@{team}", StringComparison.Ordinal)).ToList();
    }
}
