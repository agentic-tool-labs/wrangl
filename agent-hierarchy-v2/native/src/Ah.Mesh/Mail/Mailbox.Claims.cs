using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Core.Time;

namespace Ah.Mesh;

internal sealed partial class Mailbox
{
    internal const string ClaimsDirectory = "claims";

    internal static string ClaimFile(string dir, string id) => NodePath.Join(dir, ClaimsDirectory, id + ".json");

    public JsonObject? ReadClaim(string dir, string id)
    {
        if (!IsName(id))
        {
            return null;
        }

        var record = Stored is { } store ? StoredMap(store.Claim(BoxKey(dir), id)) : AsMap(ReadJson(ClaimFile(dir, id)));
        return record?.Get("state") is { IsText: true } ? record : null;
    }

    /// <summary>The claim a stored row holds, null when it is not one.</summary>
    internal static JsonObject? ClaimRecordOf(byte[] raw) => StoredMap(raw) is { } record && record.Get("state") is { IsText: true } ? record : null;

    public ClaimResult Claim(string dir, string id, string action, JsonObject? args = null)
    {
        if (!IsName(id))
        {
            return new ClaimResult(false, "none", "bad-id", null);
        }

        var call = args is null ? new JsonObject() : args.Clone();
        call["dir"] = dir;
        call["id"] = id;
        call["action"] = action;
        var result = ClaimMachine.FromJson(Invoke("claim", call));
        return result with { Record = ReadClaim(dir, id) };
    }

    public string? ProcessStart(long pid) => processes?.StartTime(pid);

    public StartMatch ProcessStartMatches(long pid, string? recorded) =>
        processes is null ? StartMatch.Unknown : Ah.Core.Time.ProcessStart.Compare(processes, pid, recorded);

    public string? StartToRecord(long pid, string? recorded) =>
        Ah.Core.Time.ProcessStart.IsSame(ProcessStartMatches(pid, recorded)) ? ProcessStart(pid) : recorded;

    internal const string NotesDirectory = "mailbox-notes";

    internal static string NoteFile(string ahHome, string kind, string name) => NodePath.Join(ahHome, NotesDirectory, kind, name + ".json");

    internal JsonObject? ReadNote(string kind, string name)
    {
        if (!IsName(kind) || !IsName(name))
        {
            return null;
        }

        return Stored is { } store ? StoredMap(store.Note(kind, name)) : AsMap(ReadJson(NoteFile(AhHome, kind, name)));
    }

    internal void WriteNote(string kind, string name, JsonObject data) =>
        Invoke("note", new JsonObject { ["kind"] = kind, ["name"] = name, ["data"] = data });

    /// <summary>
    /// The complete lines of the file from byte <paramref name="offset"/> (the start when it lies past the end), decoded, and the
    /// offset after them; null when the file cannot be read. A last line with no newline yet is left for a later read.
    /// </summary>
    internal (string Text, long End)? ReadLinesFrom(string path, long offset) => TranscriptWindow.ReadLinesFrom(files, path, offset);

    /// <summary>Writes the note only when the <c>seq</c> it holds now is <paramref name="expectSeq"/> (0 for none); false when another writer got there first.</summary>
    internal bool WriteNoteIf(string kind, string name, JsonObject data, double expectSeq) =>
        Invoke("note", new JsonObject { ["kind"] = kind, ["name"] = name, ["data"] = data, ["expect_seq"] = expectSeq }).Member("written") is { IsBool: true } written && written.AsBool();

    internal const long TraceFileLimit = 1024 * 1024;

    internal static string TraceFile(string ahHome, string session) => NodePath.Join(ahHome, NotesDirectory, "doorbell-trace", session + ".jsonl");

    /// <summary>Appends a row to <c>mailbox-notes/doorbell-trace/&lt;session&gt;.jsonl</c>; past 1 MiB the file becomes <c>.old</c> and a new one starts.</summary>
    // ponytail: b99 diagnostic, written straight to disk because every state op also adds a row to the op ledger of its store, which a Stop hook must leave alone. Delete it once the live UI answer has named the cause.
    internal void AppendTrace(string session, JsonObject row)
    {
        if (!IsName(session))
        {
            return;
        }

        var file = TraceFile(AhHome, session);
        _ = Directory.CreateDirectory(NodePath.Dirname(file));
        if (File.Exists(file) && new FileInfo(file).Length > TraceFileLimit)
        {
            File.Move(file, file + ".old", overwrite: true);
        }

        File.AppendAllText(file, JsonText.Stringify(row, 0) + "\n");
    }

    internal void ClearNote(string kind, string name) =>
        Invoke("note", new JsonObject { ["kind"] = kind, ["name"] = name, ["clear"] = true });

    public IReadOnlyList<HeldRelay> HeldRelays(string dir, double nowMs, double minAgeMs)
    {
        var held = new List<HeldRelay>();
        List<string> names;
        try
        {
            names = Stored is { } store ? [.. store.ClaimIds(BoxKey(dir)).Select(static id => id + ".json")] : Names(NodePath.Join(dir, ClaimsDirectory));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return held;
        }

        foreach (var name in names.Where(n => n.EndsWith(".json", StringComparison.Ordinal)))
        {
            var id = name[..^".json".Length];
            if (ReadClaim(dir, id) is not { } claim || ClaimMachine.StateOf(claim) is not (ClaimStates.RelaySent or ClaimStates.RelayUnconfirmed) || IsAcked(dir, id)
                || claim.Get("at") is not { IsText: true } at || !Timestamps.TryParseMs(at.AsString(), out var sentMs) || nowMs - sentMs < minAgeMs)
            {
                continue;
            }

            held.Add(new HeldRelay(id, claim.Get("to") is { IsText: true } to ? to.AsString() : string.Empty, at.AsString()));
        }

        return held;
    }

    /// <summary>What an inbox line says about an entry's claim: who carries it, and that a sent relay has not been shown.</summary>
    private static string ClaimTagOf(JsonObject claim) =>
        Js(claim.Get("state")) switch
        {
            "relay-sent" when claim.Get("held") is { IsBool: true } held && held.AsBool() =>
                "(relay sent, not acknowledged)",
            "relay-sent" => "(relay sent)",
            "relay-unconfirmed" when claim.Get("held") is { IsBool: true } held && held.AsBool() => "(relay sent, not confirmed or acknowledged)",
            "relay-unconfirmed" => "(relay not confirmed)",
            "relay-claimed" => "(relay claimed)",
            "relay-failed" => "(relay failed; plugin prompt next)",
            "released" => "(released: no poller; the ladder delivers it)",
            "fallback-claimed" => "(plugin prompt)",
            var other => $"({other})",
        };

    public ClaimResult AdvanceClaim(string dir, string id, bool? receiverIdle = null)
    {
        var record = ReadClaim(dir, id);
        if (record is null)
        {
            return new ClaimResult(false, "none", "no-claim", null);
        }

        if (ClaimMachine.StateOf(record) is ClaimStates.RelayClaimed or ClaimStates.RelayUnconfirmed)
        {
            ApplyConfirmNote(dir, id);
        }

        return Claim(dir, id, ClaimActions.Advance, receiverIdle is { } idle ? new JsonObject { ["idle"] = idle } : null);
    }

    /// <summary>
    /// A confirm or failure the sender's hook could not write into the box is kept as a note outside it; it is applied as that
    /// transition, by the session that wrote it, before any lease acts on the entry.
    /// </summary>
    private void ApplyConfirmNote(string dir, string id)
    {
        if (ReadNote(ConfirmNoteKind, id) is not { } note || Js(note.Get("dir")) != dir || Js(note.Get("session_id")).Length == 0)
        {
            return;
        }

        var failed = Js(note.Get("result")) == "failed";
        var args = new JsonObject { ["session_id"] = Js(note.Get("session_id")) };
        if (Js(note.Get("reason")) is { Length: > 0 } reason)
        {
            args["reason"] = reason;
        }
        else if (!failed)
        {
            args["reason"] = "note";
        }

        if (!failed && Js(note.Get("msg_id")) is { Length: > 0 } noted)
        {
            args["msg_id"] = noted;
        }

        var applied = Claim(dir, id, failed ? ClaimActions.Fail : ClaimActions.Confirm, args);
        if (applied.Won)
        {
            ClearNote(ConfirmNoteKind, id);
        }
    }

    internal const string ConfirmNoteKind = "relay-confirm";

    /// <summary>The single-carrier settings in force for a request: the user's file, then each hierarchy directory's.</summary>
    internal CarrierSettings CarrierSettingsFor(RequestContext context)
    {
        var paths = new List<string> { NodePath.Join(AhHome, "agent-hierarchy.json") };
        paths.AddRange(HierarchyDirs(context).Select(d => NodePath.Join(d, "agent-hierarchy.json")));
        return CarrierSettings.Read(files, paths);
    }

    /// <summary>
    /// The claim record a new entry gets, or null when single-carrier delivery is off or the running daemon does not serve entries
    /// that have one (the entry then follows the earlier ladder). The carrier is decided here and never changes:
    /// <c>relay</c> only for a live Claude sender writing from its own address to a live Claude session of another address
    /// whose pull is not fresh (a fresh pull, route <c>pull</c>, delivers it by prompt); everything else, a daemon event, a person, a dead sender, a non-Claude receiver, mail to oneself, a zero relay lease, is
    /// <c>prompt</c>.
    /// </summary>
    internal JsonObject? ClaimFor(JsonObject entry, JsonObject req, ResolvedAddress to, RequestContext context, string createdIso)
    {
        var settings = CarrierSettingsFor(context);
        if (!settings.SingleCarrier || claimsServed?.Invoke() == false)
        {
            return null;
        }

        var from = Js(entry.Get("from"));
        var id = Js(entry.Get("id"));
        var senderSession = req.Get("session_id") is { IsText: true } sid && sid.AsString().Length > 0 ? sid.AsString() : null;
        var receiver = ClaudeReceiverOf(entry, req, to);
        var relay = senderSession is not null
            && settings.RelayLeaseMs > 0
            && from is not ("ahd" or "user")
            && from != to.Address
            && receiver is not null
            && Js(entry.Get("route")) != "pull"
            && !DoorbellServes(entry, to, receiver)
            && LiveClaudeSender(senderSession)
            && ReadRegistration(senderSession)?.Get("addresses") is { IsArray: true } mine
            && mine.AsArray().Any(a => a.IsText && a.AsString() == from);
        var senderRegistration = relay ? ReadRegistration(senderSession!) : null;
        long? senderPid = senderRegistration?.Get("pid") is { IsNumber: true } pidValue ? (long)pidValue.AsNumber() : null;
        var senderStart = senderRegistration?.Get("pid_start") is { IsText: true } startValue && startValue.AsString().Length > 0 ? startValue.AsString() : null;
        return ClaimMachine.Create(
            id,
            relay ? "relay" : "prompt",
            from,
            to.Address,
            relay ? senderSession : null,
            relay ? receiver : null,
            settings,
            createdIso,
            senderPid,
            senderPid is { } own ? StartToRecord(own, senderStart) : senderStart);
    }

    /// <summary>
    /// Whether the doorbell serves the receiving session, the one predicate (<c>Serves</c> and a fresh pull) the fewer-turns spec names:
    /// the doorbell is not switched off, the address is an <c>orchestrator@</c> box the session registered, and the send's own routing
    /// decision (<c>route</c>, made with the same <c>FreshOf</c> that <see cref="IMailbox.PullFresh"/> reaches through <c>HeartbeatFresh</c>) found the pull fresh. Such a receiver
    /// is rung in batches, so the report needs no relay SendMessage.
    /// </summary>
    private bool DoorbellServes(JsonObject entry, ResolvedAddress to, string receiverSession) =>
        to.Address.StartsWith("orchestrator@", StringComparison.Ordinal)
        && Js(entry.Get("route")) == "pull"
        && MailDoorbell.ServesSession(this, environment, receiverSession);

    /// <summary>The session id of the live Claude session that holds the recipient's address (its <c>to_session</c> when the entry names one), or null.</summary>
    private string? ClaudeReceiverOf(JsonObject entry, JsonObject req, ResolvedAddress to)
    {
        var named = entry.Get("to_session") is { IsText: true } ts && ts.AsString().Length > 0 ? ts.AsString() : null;
        var matches = LiveRegistrations()
            .Where(r => r.Get("addresses") is { IsArray: true } a && a.AsArray().Any(x => x.IsText && x.AsString() == to.Address))
            .Where(r => r.Get("harness") is not { IsText: true } h || h.AsString() is "claude" or "claude-mod")
            .Where(r => named is null || (r.Get("session_id") is { IsText: true } s && s.AsString() == named))
            .ToList();
        return matches.Count == 1 && matches[0].Get("session_id") is { IsText: true } found ? found.AsString() : null;
    }
}
