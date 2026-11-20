using System.Text.RegularExpressions;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Hierarchy;

namespace Ah.Mesh;

/// <summary>What a prompt hook did with a prompt that carries <c>[ah-entry …]</c> markers: the ids it acked, those it acked first, and whether the prompt is a duplicate to block.</summary>
public sealed record HookAckResult(IReadOnlyList<string> Acked, IReadOnlyList<string> Fresh, bool Duplicate);

/// <summary>
/// The full prompt text of a compact mail prompt, read back from the session's boxes. <paramref name="Text"/> is the prompt for the
/// entries that loaded (<paramref name="Ids"/>), null when none did. <paramref name="MissingIds"/> are entries named by a marker
/// that are in none of the session's boxes. <paramref name="Reason"/> says why nothing could be read at all (no registration), and
/// is the reason for <paramref name="MissingIds"/> otherwise.
/// </summary>
public sealed record FullMail(string? Text, IReadOnlyList<string> Ids, string Address, string? Reason, IReadOnlyList<string> MissingIds);

/// <summary>A brief mailed to a session that entered one of its turns and whose exchange still has no response.</summary>
public sealed record OpenBrief(string Id, string Ref, JsonValue? From);

/// <summary>One live registration with its adapter's freshness: <c>fresh</c>, <c>stale</c> or <c>none</c>.</summary>
public sealed record RegistrationSummary(string Address, string? Harness, string Adapter);

/// <summary>The live registrations and the number of overdue entries across all mailboxes, for <c>daemon status</c>.</summary>
public sealed record MailboxSummary(IReadOnlyList<RegistrationSummary> Registrations, int Overdue);

/// <summary>
/// What the sender's SendMessage PreToolUse decides for a message that carries entry markers or a request token: whether to
/// <paramref name="Deny"/> it, the text to give the model when it does, and the entries this call took the relay claim of.
/// </summary>
public sealed record RelayClaimDecision(bool Deny, string? Reason, IReadOnlyList<string> Claimed);

/// <summary>What the tool result of a relaying SendMessage says: it reached the inbox, it did not, or nothing could be read from it.</summary>
public enum RelayResult
{
    Unknown = 0,
    Sent = 1,
    Failed = 2,
}

/// <summary>Where the report owed for a mailed brief stands.</summary>
public enum ReportState
{
    /// <summary>The response file does not exist.</summary>
    NotWritten = 0,

    /// <summary>The response file exists and no report entry for it from this session is in the brief sender's box.</summary>
    WrittenNotSent = 1,

    /// <summary>A report entry for the response, from one of this session's addresses, is in the brief sender's box.</summary>
    Sent = 2,
}

/// <summary>The mailbox's hook-side helpers: what a delivered prompt or SendMessage acks, a session's open briefs, and the status summary.</summary>
public interface IMailboxHooks
{
    /// <summary>
    /// A prompt carrying <c>[ah-entry …]</c> entered a turn of the session's recipient: acks those ids in its own box(es). A prompt of
    /// the adapter's shape is read from its trailing marker block only; any other prompt from its whole text. Only a prompt that is
    /// nothing but this session's exact relay lines, or an adapter prompt whose entries were already received, is ever blocked.
    /// </summary>
    HookAckResult AckFromPrompt(string? sessionId, string? prompt);

    /// <summary>
    /// A SendMessage that carried rung 3 acks the entry it carried, but only in the box of the SendMessage's actual recipient:
    /// <paramref name="recipient"/> (a trailing <c> [ref]</c> stripped) must resolve to one address, else nothing is acked.
    /// </summary>
    IReadOnlyList<string> AckFromSendMessage(string? sessionId, string? recipient, string? message, RequestContext context);

    /// <summary>
    /// The sender's SendMessage is about to run. For each entry its markers or request token name that has a claim record, the
    /// sender session takes the relay claim (<c>pending → relay-claimed</c>), recording <paramref name="toolUseId"/> as the call that
    /// holds it; a message that cannot take every claim it names is denied with the text to give the model. Only the same tool
    /// call may take a held claim again: a second SendMessage for the entry is denied. A message naming no claimed entry is not
    /// this check's, and with single-carrier delivery switched off nothing is resolved, read or written.
    /// </summary>
    RelayClaimDecision ClaimRelay(string? sessionId, string? recipient, string? message, RequestContext context, string? toolUseId = null);

    /// <summary>
    /// The text to give the model when <paramref name="message"/> carries a <c>[hierarchy-msg …]</c> token and no <c>[ah-entry …]</c>
    /// marker naming the entry, and a mailbox entry of any box has that path as its ref (compared with symbolic links resolved):
    /// ah delivers that entry itself, so the message is a second carrier of it. Every token of the message is checked. A required
    /// relay carries the marker of its own entry, so it is never this check's. Null when every entry the tokens match is named by
    /// a marker, when no token matches an entry, and with single-carrier delivery switched off.
    /// </summary>
    string? PointerDuplicate(string? message, RequestContext context);

    /// <summary>
    /// The text to give the model when <paramref name="message"/> carries the <c>[ah-entry …]</c> marker of an entry no relay is owed
    /// for: delivered, acked, superseded or declined, or queued for the plugin prompt to carry. The answer is
    /// <see cref="IMailbox.EntryStatus"/>'s, so the gate and <c>ah msg status</c> cannot disagree. Null when every entry the marker
    /// names has a relay owed, when none exists, and with single-carrier delivery switched off. When a record cannot be read the
    /// answer is null (the send goes on) and <paramref name="warning"/> says the entry could not be checked.
    /// </summary>
    string? SendDenial(string? message, string? sessionId, RequestContext context, out string? warning);

    /// <summary>
    /// The sender's SendMessage ran: moves the entries the message names, held by <paramref name="sessionId"/>, to <c>relay-sent</c>
    /// or <c>relay-failed</c> by <paramref name="result"/>. <see cref="RelayResult.Unknown"/> moves nothing. Returns the entry ids moved.
    /// </summary>
    IReadOnlyList<string> ConfirmRelay(string? sessionId, string? recipient, string? message, RelayResult result, string? reason, RequestContext context, string? msgId = null);

    /// <summary>
    /// A gate denied the SendMessage, so it never ran: returns the entries the message names, claimed by <paramref name="sessionId"/>
    /// on exactly <paramref name="toolUseId"/>, to <c>pending</c>, so that the retry takes them again. A claim another call or
    /// another session holds, and one already sent, is left alone. Returns the entry ids moved.
    /// </summary>
    IReadOnlyList<string> WithdrawRelay(string? sessionId, string? recipient, string? message, string? toolUseId, RequestContext context);

    /// <summary>
    /// The receiver's Stop hook: acks, with writer <c>stop-hook</c>, the relayed entries of the session's own boxes that a cross-session
    /// message in its transcript carries, from where the last scan ended. A message drained while the session was busy never
    /// reaches a prompt hook, so this is where it is seen. Only the host's own rows count (a user row marked as meta whose text opens
    /// with the cross-session wrapper), each marker is judged as <see cref="AckFromPrompt"/> judges it, and a row that cannot be
    /// read acks nothing. Returns the entry ids acked.
    /// </summary>
    IReadOnlyList<string> AckFromTranscript(string? sessionId, string? transcriptPath);

    /// <summary>
    /// The prompt <see cref="IMailCore.BuildPrompt"/> would have made for the entries a compact prompt's markers name, read from the
    /// boxes of the session's registration (acked entries included). Never throws for a missing box or entry.
    /// </summary>
    FullMail FullMailText(string? sessionId, CompactMail compact);

    /// <summary>Briefs mailed to <paramref name="sessionId"/> that entered one of its turns (acked by the prompt hook from this session) and whose exchange has no response.</summary>
    IReadOnlyList<OpenBrief> OpenMailBriefs(string? sessionId);

    /// <summary>
    /// The briefs in the boxes of the session's registration that still owe a report: a <c>brief</c> entry with a <c>ref</c>, whose last ladder
    /// state is not withdrawn, superseded or declined, and whose report is not yet sent. The sender is not looked at, and no ack is needed,
    /// so it also serves a member whose mail an adapter hands over.
    /// </summary>
    IReadOnlyList<OpenBrief> OwedBriefs(string? sessionId);

    /// <summary>
    /// Whether the report owed for <paramref name="brief"/> is written and sent. <paramref name="responsePath"/> is the response file
    /// the brief's request names, or null when it names none.
    /// </summary>
    ReportState ReportStateOf(string? sessionId, OpenBrief brief, out string? responsePath);

    /// <summary>Whether a <c>report</c> entry naming <paramref name="responsePath"/> is in one of the session's boxes, acked or not: the report reached it by mail. The match is by path alone, so a report edited after it was sent still counts as delivered.</summary>
    bool ReportDelivered(string? sessionId, string responsePath);

    /// <summary><see cref="ReportDelivered"/> for many response paths of one session, from one read of its boxes.</summary>
    Func<string, bool> ReportsDelivered(string? sessionId);

    /// <summary>
    /// For many (request id, response path) pairs of one session, from one read of its records: whether the report has reached, or been
    /// accounted for by, the Orchestrator. True when any of these holds: a <c>report</c> entry names the response in one of the session's
    /// boxes; a <c>sent</c> row of any session names it (the SendMessage route); the session was shown it (<c>seen</c> or <c>surfaced</c>);
    /// the session acknowledged it (<c>report-acked</c>); or the Orchestrator wrote the response itself.
    /// </summary>
    Func<string, string, bool> ReportReached(string? sessionId);

    /// <summary>Starts the scope the reached checks of one tick share, so what they read once is not read again for each session.</summary>
    IReachTick BeginReach();

    /// <summary>
    /// <see cref="ReportReached(string?)"/> answering from <paramref name="tick"/>: the registrations, real paths and peer-log index it holds
    /// are used instead of read again. <paramref name="sessionPeerRecords"/>, when given, are the session's own peer records, so the shown
    /// check scans those instead of the whole log.
    /// </summary>
    Func<string, string, bool> ReportReached(string? sessionId, IReachTick tick, IReadOnlyList<JsonValue>? sessionPeerRecords = null);

    /// <summary>The test for "is this session the session itself or another session of its role and team", read from the registrations.</summary>
    Func<string, bool> SameOrchestrator(string? sessionId);

    /// <summary>The teams this session is the Orchestrator of right now, read from its registration (the <c>orchestrator@team</c> addresses).</summary>
    IReadOnlyList<string> OrchestratedTeams(string? sessionId);

    /// <summary>Whether the Orchestrator wrote <paramref name="responsePath"/> itself, so there is no peer report to read or to send.</summary>
    bool WrittenByOrchestrator(string responsePath);

    /// <summary>Whether <paramref name="entry"/>, an event, still describes something true at <paramref name="sessionId"/>. Events with no check apply.</summary>
    bool EventApplies(string? sessionId, JsonValue entry);

    /// <summary>An applying event as the prompt hook shows it: an <c>idle-capacity</c> alarm leaves out the members no longer idle. Any other entry is returned as it is.</summary>
    JsonValue EventShown(string? sessionId, JsonValue entry) => entry;

    /// <summary>Acks <paramref name="id"/> in <paramref name="dir"/> by <paramref name="by"/> with the reason <c>resolved</c>: the event no longer applied, so it was not shown.</summary>
    void ResolveEvent(string dir, string id, string by, string? sessionId);

    /// <summary>Each live registration with its adapter's freshness, and the overdue entries of every mailbox.</summary>
    MailboxSummary Summary(double? nowMs = null);
}

internal sealed partial class MailboxHooks(Mailbox mailbox, RelayObligations relay, HierarchyServices hierarchy, IMailCore core, IClock clock) : IMailboxHooks
{
    // Writers whose ack on an adapter-shaped prompt's entry means the entry already reached the conversation by a hook.
    private static readonly string[] HookDelivered = ["prompt-hook", "stop-hook"];

    // Writers whose ack proves the session already received the entry. `sendmessage` (the mirror, written at the sender before
    // the relay arrives) and `cli` are not proof.
    private static readonly string[] ReceivedBy = ["prompt-hook", "adapter", "watcher", "stop-hook"];

    [GeneratedRegex("[" + JsText.SpaceClass + @"]*\[[^\]]*\][" + JsText.SpaceClass + @"]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingRefRe();

    /// <summary>An ack of ids in one box, with the obligation a freshly acked <c>relay-needed</c> request makes.</summary>
    private AckIdsResult Ack(string dir, IReadOnlyList<string> ids, string by, string? sessionId)
    {
        var result = mailbox.AckIds(dir, ids, by, sessionId);
        try
        {
            relay.ObligationsFor(sessionId, [dir], result.Fresh, by);
        }
        catch (Exception e) when (e is MailboxError or IOException or Ah.Core.Json.StoreUnavailableException)
        {
            // the ack stands; an obligation that could not be written, or whose dedupe read the store could not answer (logged where the
            // read failed), is covered by the rung-3 deadline
        }

        return result;
    }

    private JsonValue? RegistrationValue(string? sessionId) => RegistrationValue(sessionId, null);

    private JsonValue? RegistrationValue(string? sessionId, ReachTick? tick)
    {
        if (string.IsNullOrEmpty(sessionId))
        {
            return null;
        }

        JsonValue? Read(string id)
        {
            JsonValue? value = null;
            if (mailbox.ReadRegistration(id) is { } registration)
            {
                value = registration;
            }

            return value;
        }

        return tick is null ? Read(sessionId) : tick.Registration(sessionId, Read);
    }

    public HookAckResult AckFromPrompt(string? sessionId, string? prompt)
    {
        var trailing = core.MailMarkers(prompt);
        var marks = trailing ?? core.EntryMarkers(prompt);
        var boxes = mailbox.RegistrationBoxes(RegistrationValue(sessionId));
        var acked = new List<string>();
        var fresh = new List<string>();
        var known = new List<(string Id, string Dir)>();
        var bare = trailing is null ? BareRelay(prompt, boxes) : null;
        var blocks = WrapperBlocks(prompt);
        foreach (var mark in marks)
        {
            foreach (var box in boxes)
            {
                if (!string.IsNullOrEmpty(mark.Address) && mark.Address != box.Address)
                {
                    continue;
                }

                var r = Ack(box.Dir, Receivable(box, mark.Ids, blocks), "prompt-hook", sessionId);
                acked.AddRange(r.Acked);
                fresh.AddRange(r.Fresh);
                known.AddRange(r.Acked.Select(id => (id, box.Dir)));
                Delivered(box.Dir, r.Acked);
            }
        }

        foreach (var (box, ids) in TokenClaims(boxes, blocks))
        {
            var r = Ack(box.Dir, ids, "prompt-hook", sessionId);
            acked.AddRange(r.Acked);
            fresh.AddRange(r.Fresh);
            known.AddRange(r.Acked.Select(id => (id, box.Dir)));
            Delivered(box.Dir, r.Acked);
        }

        var duplicate = trailing is not null
            ? IsDuplicate(known, fresh, HookDelivered)
            : bare is not null && IsDuplicate(bare, fresh, ReceivedBy);
        return new HookAckResult(acked, fresh, duplicate);
    }

    /// <summary>
    /// The <c>{id, dir}</c> of every entry in <paramref name="prompt"/> when, with the cross-session wrapper removed, it is nothing but
    /// relay lines: each non-blank line (ends trimmed) is exactly the relay line of an entry in one of this session's boxes. Null for
    /// anything else, which then passes. Lines are compared one at a time, so a relay whose entry text spans several lines never
    /// matches and always passes (a duplicate delivery is possible there, never a dropped message).
    /// </summary>
    private List<(string Id, string Dir)>? BareRelay(string? prompt, IReadOnlyList<BoxRef> boxes)
    {
        if (prompt is null || hierarchy.Peers.UnwrapCrossSession(JsonValue.FromString(prompt)) is not { IsText: true } inner)
        {
            return null;
        }

        var lines = inner.AsString().Split('\n').Select(JsText.Trim).Where(l => l.Length > 0).ToList();
        if (lines.Count == 0)
        {
            return null;
        }

        var found = new List<(string Id, string Dir)>();
        foreach (var raw in lines)
        {
            var line = RelayInstruction.WithoutRelayedPrefix(raw);
            var tail = EntryMarkerText.ParseTrailing(line);
            var box = tail is not null ? boxes.FirstOrDefault(b => b.Address == tail.Address) : null;
            if (box is null)
            {
                return null;
            }

            var entry = mailbox.ReadEntries(box.Dir).Entries.FirstOrDefault(e => e.AsObject().Get("id") is { IsText: true } id && id.AsString() == tail!.Ids[0]);
            if (!entry.IsMap || mailbox.RelayLine(entry, box.Address) != line)
            {
                return null;
            }

            found.Add((tail!.Ids[0], box.Dir));
        }

        return found;
    }

    /// <summary>
    /// The duplicate backstop: a duplicate only when the prompt acks nothing new (no id created by this call) and every id it names that
    /// is known in its box already has an ack written by one of <paramref name="received"/>. A rung-3 ack, an unreadable ack file or no
    /// known id is not a duplicate. The caller passes a duplicate through with a note and never blocks the prompt.
    /// </summary>
    private bool IsDuplicate(List<(string Id, string Dir)> known, List<string> fresh, string[] received)
    {
        if (known.Count == 0 || fresh.Count > 0)
        {
            return false;
        }

        return known.All(k =>
            ReadAckSettled(k.Dir, k.Id) is { IsMap: true } a
            && a.AsObject().Get("by") is { IsText: true } by
            && received.Contains(by.AsString(), StringComparer.Ordinal));
    }

    /// <summary>An ack file another process created exclusively may be read before its content lands; retry briefly before treating it as unreadable.</summary>
    private JsonValue? ReadAckSettled(string dir, string id)
    {
        for (var i = 0; i < 10; i++)
        {
            if (mailbox.ReadAck(dir, id) is { } ack && !ack.IsNull)
            {
                return ack;
            }

            Thread.Sleep(20);
        }

        return null;
    }

    public IReadOnlyList<string> AckFromSendMessage(string? sessionId, string? recipient, string? message, RequestContext context)
    {
        var target = TrailingRefRe().Replace(recipient ?? string.Empty, string.Empty);
        var resolved = mailbox.ResolveAddress(target, context);
        if (resolved.Value is not { } r)
        {
            return [];
        }

        var marks = core.EntryMarkers(message);
        var done = new List<string>();
        if (marks.Count > 0)
        {
            foreach (var mark in marks)
            {
                if (!string.IsNullOrEmpty(mark.Address) && BoxAddressOf(mark.Address, context) != r.Address)
                {
                    continue;
                }

                done.AddRange(Ack(r.Dir, Unclaimed(r.Dir, mark.Ids), "sendmessage", string.IsNullOrEmpty(sessionId) ? null : sessionId).Acked);
            }

            return done;
        }

        if (!string.IsNullOrEmpty(sessionId) && AckNeutralisedRelay(sessionId, target, r, message, context) is { Count: > 0 } relayed)
        {
            return relayed;
        }

        var token = hierarchy.Core.ExtractMsgToken(message);
        var registration = string.IsNullOrEmpty(sessionId) ? null : mailbox.ReadRegistration(sessionId);
        var senders = new HashSet<string>(StringComparer.Ordinal);
        if (registration?.Get("addresses") is { IsArray: true } addresses)
        {
            foreach (var a in addresses.AsArray())
            {
                senders.Add(JsConvert.ToJsString(a));
            }
        }

        if (string.IsNullOrEmpty(token) || senders.Count == 0)
        {
            return [];
        }

        var mine = mailbox.ReadEntries(r.Dir).Entries
            .Select(e => e.AsObject())
            .Where(e => e.Get("ref") is { IsText: true } rf && rf.AsString() == token
                && e.Get("from") is { IsText: true } from && senders.Contains(from.AsString())
                && e.Get("id") is { IsText: true } id && !mailbox.IsAcked(r.Dir, id.AsString()) && ClaimMachine.Live(mailbox.ReadClaim(r.Dir, id.AsString())) is null)
            .Select(e => e.Get("id")!.Value.AsString())
            .ToList();
        return mine.Count > 0 ? Ack(r.Dir, mine, "sendmessage", string.IsNullOrEmpty(sessionId) ? null : sessionId).Acked : [];
    }

    /// <summary>
    /// The box address a marker's recipient names. <c>member@team</c> resolves as a send does; <c>role@team</c> is the team file's one
    /// member of that role, and null when the team has none or several, so an ambiguous role never matches.
    /// </summary>
    private string? BoxAddressOf(string address, RequestContext context)
    {
        if (mailbox.ResolveAddress(address, context).Value is { } resolved)
        {
            return resolved.Address;
        }

        var at = address.IndexOf('@', StringComparison.Ordinal);
        if (at <= 0)
        {
            return null;
        }

        var role = address[..at];
        var team = address[(at + 1)..];
        var names = mailbox.VisibleTeams(context)
            .Where(t => t.Team == team && t.Data.IsMap && t.Data.AsObject().Get("members") is { IsArray: true })
            .SelectMany(t => t.Data.AsObject().Get("members")!.Value.AsArray().Where(m => m.IsMap).Select(m => m.AsObject()))
            .Where(m => m.Get("role") is { IsText: true } r && r.AsString() == role && m.Get("name") is { IsText: true })
            .Select(m => m.Get("name")!.Value.AsString())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return names.Count == 1 ? $"{names[0]}@{team}" : null;
    }

    /// <summary>
    /// A relay line the prompt neutralised (<c>(ah-entry E to A]</c>) acks its entry only for the session that holds the relay
    /// obligation for exactly that entry, and only when the message goes to the obligation's recipient. Text from anyone else, or
    /// naming another entry or address, acks nothing.
    /// </summary>
    private List<string> AckNeutralisedRelay(string sessionId, string target, ResolvedAddress sent, string? message, RequestContext context)
    {
        var done = new List<string>();
        var marks = EntryMarkerText.ParseNeutralised(message);
        if (marks.Count == 0)
        {
            return done;
        }

        foreach (var row in hierarchy.Core.SessionRows(sessionId))
        {
            if (!row.IsMap
                || !(row.AsObject().Get("session_id") is { IsText: true } sid && sid.AsString() == sessionId)
                || !(row.AsObject().Get("task") is { IsText: true } task && task.AsString().StartsWith("relay:", StringComparison.Ordinal))
                || row.AsObject().Get("relay") is not { IsMap: true } relayValue)
            {
                continue;
            }

            var relay = relayValue.AsObject();
            if (!(relay.Get("entry") is { IsText: true } entryText && relay.Get("address") is { IsText: true } addressText
                  && relay.Get("to") is { IsText: true } toText))
            {
                continue;
            }

            var entry = entryText.AsString();
            var address = addressText.AsString();
            var to = toText.AsString();
            var toMatches = to == target || mailbox.ResolveAddress(to, context).Value is { } toBox && toBox.Address == sent.Address;
            if (address != sent.Address || !toMatches || !marks.Any(m => m.Ids[0] == entry && BoxAddressOf(m.Address!, context) == address) || mailbox.IsAcked(sent.Dir, entry)
                || ClaimMachine.Live(mailbox.ReadClaim(sent.Dir, entry)) is not null)
            {
                continue;
            }

            done.AddRange(Ack(sent.Dir, [entry], "sendmessage", sessionId).Acked);
        }

        return done;
    }

    public ReportState ReportStateOf(string? sessionId, OpenBrief brief, out string? responsePath)
    {
        responsePath = hierarchy.Messages.ResponsePlan(brief.Ref) is { IsMap: true } plan && plan.Member("path") is { IsText: true } path ? path.AsString() : null;
        if (responsePath is null || !hierarchy.Core.MsgExists(responsePath))
        {
            return ReportState.NotWritten;
        }

        // A response file `ah msg new` made and nobody has typed into is a template, not a report.
        var requestId = hierarchy.Core.ParseMsgFilename(brief.Ref)?.Id;
        if (requestId is not null && hierarchy.Messages.ReportStatus(responsePath, requestId) == "no-report")
        {
            return ReportState.NotWritten;
        }

        if (brief.From is not { IsText: true } from)
        {
            return ReportState.WrittenNotSent;
        }

        var mine = new HashSet<string>(StringComparer.Ordinal);
        var registration = string.IsNullOrEmpty(sessionId) ? null : mailbox.ReadRegistration(sessionId);
        if (registration?.Get("addresses") is { IsArray: true } addresses)
        {
            foreach (var a in addresses.AsArray())
            {
                mine.Add(JsConvert.ToJsString(a));
            }
        }

        // The brief's recorded sender is a box address for a mailed brief but a socket for a SendMessage one, so the sender's box is
        // also named by the request's own frontmatter (its `from` in its `team`).
        var senders = new HashSet<string>(StringComparer.Ordinal) { from.AsString() };
        if (hierarchy.Core.ReadMsgFile(brief.Ref).Member("fm") is { IsMap: true } fm
            && fm.Member("from") is { IsText: true } fmFrom && fm.Member("team") is { IsText: true } fmTeam
            && fmFrom.AsString().Length > 0 && fmTeam.AsString().Length > 0)
        {
            senders.Add($"{fmFrom.AsString()}@{fmTeam.AsString()}");
        }

        foreach (var box in mailbox.ListBoxes().Where(b => senders.Contains(b.Address)))
        {
            if (mailbox.FindReport(box.Dir, responsePath, null, mine.Contains, null) is not null)
            {
                return ReportState.Sent;
            }
        }

        return ReportState.WrittenNotSent;
    }

    public bool ReportDelivered(string? sessionId, string responsePath) =>
        RegistrationValue(sessionId) is { } registration
        && mailbox.RegistrationBoxes(registration).Any(b => mailbox.FindReport(b.Dir, responsePath, null, _ => true, null) is not null);

    public Func<string, bool> ReportsDelivered(string? sessionId) => ReportsDelivered(sessionId, null);

    private Func<string, bool> ReportsDelivered(string? sessionId, ReachTick? tick) =>
        RegistrationValue(sessionId, tick) is { } registration
            ? mailbox.ReportRefsIn(mailbox.RegistrationBoxes(registration).Select(static b => b.Dir), tick is null ? null : (Func<string, string?>)tick.RealPath)
            : static _ => false;

    public IReachTick BeginReach() => new ReachTick(mailbox.RealPath, () => hierarchy.Core.PeerRecordsWhere(new PeerQuery(Types: ReachTypes)), hierarchy.Peers.PeerReach);

    /// <summary>The types the reach index and the shown-report test read: the answers, the acks, the reopenings, and what the session has seen or been shown.</summary>
    private static readonly IReadOnlySet<string> ReachTypes = new HashSet<string>(StringComparer.Ordinal) { "reopened", "report-acked", "sent", "seen", "surfaced" };

    public Func<string, string, bool> ReportReached(string? sessionId) => ReportReached(sessionId, BeginReach());

    public Func<string, string, bool> ReportReached(string? sessionId, IReachTick scope, IReadOnlyList<JsonValue>? sessionPeerRecords = null)
    {
        var tick = (ReachTick)scope;
        var delivered = ReportsDelivered(sessionId, tick);
        var session = sessionId is null ? JsonValue.Null : JsonValue.FromString(sessionId);
        var shown = hierarchy.Peers.ReportShownFor(session, sessionPeerRecords ?? tick.Records());
        var addresses = AddressesOf(RegistrationValue(sessionId, tick));
        var peers = tick.Peers();
        var acked = peers.AckedBy(SameOrchestrator(sessionId, tick), OrchestratedTeams(sessionId, tick));
        var sent = peers.SentFor(to => addresses.Contains(to));
        return (requestId, responsePath) =>
        {
            var id = JsonValue.FromString(requestId);
            return delivered(responsePath) || sent(responsePath) || shown(id) || acked(id) || WrittenByOrchestrator(responsePath);
        };
    }

    /// <summary>
    /// True for the session itself and for any session whose own primary address is this session's primary address (the same role
    /// and team), so an acknowledgement made before a resume still counts. An address held only as a secondary one does not match.
    /// </summary>
    public Func<string, bool> SameOrchestrator(string? sessionId) => SameOrchestrator(sessionId, null);

    private Func<string, bool> SameOrchestrator(string? sessionId, ReachTick? tick)
    {
        var primary = RegistrationValue(sessionId, tick)?.Member("address") is { IsText: true } own ? own.AsString() : null;
        return other => other == sessionId
            || (primary is not null && RegistrationValue(other, tick)?.Member("address") is { IsText: true } theirs && theirs.AsString() == primary);
    }

    public IReadOnlyList<string> OrchestratedTeams(string? sessionId) => OrchestratedTeams(sessionId, null);

    private IReadOnlyList<string> OrchestratedTeams(string? sessionId, ReachTick? tick) =>
        [.. AddressesOf(RegistrationValue(sessionId, tick)).Where(static a => a.StartsWith("orchestrator@", StringComparison.Ordinal)).Select(static a => a[(a.IndexOf('@', StringComparison.Ordinal) + 1)..])];

    private static HashSet<string> AddressesOf(JsonValue? registration)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (registration?.Member("address") is { IsText: true } one)
        {
            _ = set.Add(one.AsString());
        }

        if (registration?.Member("addresses") is { IsArray: true } list)
        {
            foreach (var a in list.AsArray().Where(static a => a.IsText))
            {
                _ = set.Add(a.AsString());
            }
        }

        return set;
    }

    public bool EventApplies(string? sessionId, JsonValue entry)
    {
        var reached = new Lazy<Func<string, string, bool>>(() => ReportReached(sessionId));
        var (home, team) = AlarmTeamOf(sessionId, entry);
        var acks = new Lazy<Func<JsonValue?, bool>>(() => hierarchy.Peers.ReportAckedBy(SameOrchestrator(sessionId), OrchestratedTeams(sessionId)));
        bool Acked(JsonValue? request) => sessionId is not null && acks.Value(request);
        bool ReadRunningLane(string request) => sessionId is not null && Acked(request) && IsRunningLaneCharter(sessionId, request, home);
        return EventRevalidation.Applies(
            entry,
            (request, response) => reached.Value(request, response),
            request => (!ReadRunningLane(request) && ExchangeClosed(sessionId, request, reached, home)) || HeardSince(sessionId, request, entry.Member("created") is { IsText: true } created ? created.AsString() : string.Empty),
            request => Acked(request),
            ReadRunningLane,
            member => home is not null && MemberGone(home, team, member),
            item => home is null || ItemWaiting(home, team, item, entry.Member("data")?.Member("request_id"), Acked),
            member => home is null || !WatcherClearedBlock(sessionId, home, team, member),
            MemberIdle(sessionId, entry, home, team),
            QaQueued);
    }

    /// <summary>Whether the QA job <paramref name="id"/> is still queued: false once it is claimed, ended or not in the store; a failed read says queued, so the alarm stays.</summary>
    private bool QaQueued(string id)
    {
        try
        {
            return HierarchyModule.QaJobs(hierarchy).FirstOrDefault(j => j.Id == id) is { State: "queued" };
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return true;
        }
    }

    public JsonValue EventShown(string? sessionId, JsonValue entry)
    {
        var (home, team) = AlarmTeamOf(sessionId, entry);
        return EventRevalidation.Shown(entry, MemberIdle(sessionId, entry, home, team));
    }

    /// <summary>
    /// Whether a member an alarm named is idle still: not proved gone from the team, and holding no request this session sent since the alarm was created that is
    /// still open (a dispatch row whose <c>to_addr</c> is the member's name or <c>name@team</c>, its exchange not closed). A failed or missing read says idle, so the alarm stays.
    /// </summary>
    private Func<string, bool> MemberIdle(string? sessionId, JsonValue entry, string? home, string? team)
    {
        var sent = new Lazy<HashSet<string>>(() =>
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            if (sessionId is null || entry.Member("created") is not { IsText: true } created || !Ah.Core.Time.Timestamps.TryParseMs(created.AsString(), out var firedAt))
            {
                return set;
            }

            var reached = new Lazy<Func<string, string, bool>>(() => ReportReached(sessionId));
            foreach (var row in hierarchy.Peers.DispatchRecordsFor(sessionId))
            {
                if (row.Member("created") is { IsText: true } at && Ah.Core.Time.Timestamps.TryParseMs(at.AsString(), out var ms) && ms > firedAt
                    && row.Member("to_addr") is { IsText: true } to
                    && row.Member("request_id") is { IsText: true } request && !ExchangeClosed(sessionId, request.AsString(), reached, home))
                {
                    var address = to.AsString();
                    _ = set.Add(address.IndexOf('@', StringComparison.Ordinal) is var cut and >= 0 ? address[..cut] : address);
                }
            }

            return set;
        });
        return member => !(home is not null && MemberGone(home, team, member)) && !sent.Value.Contains(MemberNamed(member, team).Name);
    }

    /// <summary>Whether the request charters a lane that is still running, in the pool of its dispatch row, else in <paramref name="home"/>. A failed read says it does, so the alarm stays.</summary>
    private bool IsRunningLaneCharter(string sessionId, string requestId, string? home)
    {
        var path = hierarchy.Peers.DispatchRecordsFor(sessionId)
            .Where(r => r.Member("request_id") is { IsText: true } id && id.AsString() == requestId)
            .Select(static r => r.Member("path") is { IsText: true } p ? p.AsString() : null)
            .FirstOrDefault();
        var pool = path is not null ? NodePath.Dirname(NodePath.Dirname(path)) : home;
        try
        {
            return pool is not null && hierarchy.Core.IsOpenLaneCharter(pool, requestId);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException or InvalidOperationException)
        {
            return true;
        }
    }

    /// <summary>The hierarchy directory holding the team of the box an event is in, and that team; a null home when the session's registration does not say.</summary>
    private (string? Home, string? Team) AlarmTeamOf(string? sessionId, JsonValue entry)
    {
        var to = entry.Member("to") is { IsText: true } address ? address.AsString() : string.Empty;
        var at = to.IndexOf('@', StringComparison.Ordinal);
        var registration = RegistrationValue(sessionId) is { IsMap: true } found ? found.AsObject() : null;
        return (
            RegistrationTeams.For(registration, to) is { } file ? Ah.Roster.RosterModule.TeamFileHome(file) : null,
            at >= 0 ? to[(at + 1)..] : null);
    }

    /// <summary>The member and team an event's <c>data.member</c> names; a bare name is on the team of the event's box.</summary>
    private static (string Name, string? Team) MemberNamed(string member, string? boxTeam)
    {
        var at = member.IndexOf('@', StringComparison.Ordinal);
        return at >= 0 ? (member[..at], member[(at + 1)..]) : (member, boxTeam);
    }

    // ponytail: revalidation rereads team and progress state per event; stale entries clear on first read, so this stays small. Cache per Queue() if it shows up in a profile.

    /// <summary>The progress pool the watcher reads for a team whose file is in <paramref name="home"/>: the main checkout's hierarchy directory, else that one.</summary>
    private string ProgressPoolOf(string home) => HierarchyModule.ProgressPoolOf(hierarchy.Messages.PoolContext(home).MainHierarchyDirectory, home);

    /// <summary>
    /// Whether the member is proved gone from the box's own team: its row says dismissed, or it has no row and the team's ledger records it
    /// retired (a dismissal takes the row out of the file). A raw address, a name from another team, a name that was never on the team or an
    /// unreadable record proves nothing.
    /// </summary>
    private bool MemberGone(string home, string? boxTeam, string member)
    {
        var (name, team) = MemberNamed(member, boxTeam);
        if (team != boxTeam)
        {
            return false;
        }

        bool readable;
        JsonValue record;
        try
        {
            (readable, record) = mailbox.TeamRecordOf(home, team);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException or InvalidOperationException or Ah.Core.Json.StoreUnavailableException)
        {
            return false;
        }

        if (!readable)
        {
            return false;
        }

        var row = record.Member("members") is { IsArray: true } rows
            ? rows.AsArray().FirstOrDefault(m => m.Member("name") is { IsText: true } n && n.AsString() == name)
            : default;
        return row.IsMap ? row.Member("dismissed") is { IsTruthy: true } : Retired(home, team, name);
    }

    /// <summary>Whether the team's ledger records the member retired; a failed read says not.</summary>
    private bool Retired(string home, string? team, string name)
    {
        try
        {
            var retired = $"retired {name} (";
            return hierarchy.Progress.ReadLedger(ProgressPoolOf(home), HierarchyModule.ProgressTeamOf(team))
                .Any(r => r.Member("text") is { IsText: true } text && text.AsString().StartsWith(retired, StringComparison.Ordinal));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether the checklist item still waits on the Orchestrator. A failed read says it does, and so does an empty checklist, unless the
    /// Orchestrator acknowledged the report on the alarm's request: nothing to read proves nothing about the item. With a checklist an
    /// ack changes nothing: it means the report was read, and a NEEDS-DECISION report still waits on the lead.
    /// </summary>
    private bool ItemWaiting(string home, string? team, string item, JsonValue? request, Func<JsonValue?, bool> acked)
    {
        try
        {
            var entries = HierarchyModule.ChecklistEntries(hierarchy.Progress, ProgressPoolOf(home), HierarchyModule.ProgressTeamOf(team));
            return entries.Count == 0
                ? !(request is { IsText: true } && acked(request))
                : HierarchyModule.ChecklistWaitingOnOrchestrator(entries, clock.UtcNow.ToUnixTimeMilliseconds(), 0).Any(e => HierarchyModule.ChecklistKey(e) == item);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException or InvalidOperationException)
        {
            return true;
        }
    }

    /// <summary>
    /// Whether the watcher itself closed the member's blocked episode: its latest wake or clear row is a clear, as <c>BlockedEvents.WokeBefore</c>
    /// reads them. Mesh cannot see a pane, so the member's activity alone never proves a block over; a failed read says not closed.
    /// </summary>
    private bool WatcherClearedBlock(string? sessionId, string home, string? boxTeam, string member)
    {
        var (name, team) = MemberNamed(member, boxTeam);
        try
        {
            string Text(JsonValue row, string key) => row.Member(key) is { IsText: true } t ? t.AsString() : string.Empty;
            var last = hierarchy.Roster.ReadGates(home).LastOrDefault(g =>
                ((Text(g, "type") == "blocked-wake" && Text(g, "session_id") == sessionId)
                    || (Text(g, "type") == "blocked-clear" && (Text(g, "session_id") == sessionId || Text(g, "by") == "answer")))
                && Text(g, "team") == (team ?? string.Empty) && Text(g, "member") == name);
            return last.IsMap && Text(last, "type") == "blocked-clear";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether the peer a dispatch of the session went to has written about <paramref name="requestId"/> since <paramref name="alarmCreated"/>:
    /// a message from the addressee, either unnamed or naming this request. An alarm about a request that has been heard from since it fired is stale.
    /// </summary>
    private bool HeardSince(string? sessionId, string requestId, string alarmCreated)
    {
        if (sessionId is null || !Ah.Core.Time.Timestamps.TryParseMs(alarmCreated, out var firedAt))
        {
            return false;
        }

        var addressee = hierarchy.Peers.DispatchRecordsFor(sessionId)
            .Where(r => r.Member("request_id") is { IsText: true } id && id.AsString() == requestId)
            .Select(static r => r.Member("to_addr") is { IsText: true } to ? to.AsString() : string.Empty)
            .FirstOrDefault() ?? string.Empty;
        return hierarchy.Core.SessionRows(sessionId, "heard").Any(row =>
            row.Member("type") is { IsText: true } type && type.AsString() == "heard"
            && row.Member("session_id") is { IsText: true } session && session.AsString() == sessionId
            && row.Member("ts") is { IsText: true } ts && Ah.Core.Time.Timestamps.TryParseMs(ts.AsString(), out var at) && at > firedAt
            && addressee.Length > 0 && row.Member("from") is { IsText: true } from && from.AsString() == addressee
            && (row.Member("request") is not { IsText: true } named || named.AsString() == requestId));
    }

    /// <summary>
    /// Whether the session's dispatch <paramref name="requestId"/> is over: its response holds a report or retires the request, or the report
    /// reached the session. A request with no dispatch row (one the watch derived from the exchange) is looked up in <paramref name="home"/>, and only a retirement closes it.
    /// </summary>
    private bool ExchangeClosed(string? sessionId, string requestId, Lazy<Func<string, string, bool>> reached, string? home)
    {
        if (sessionId is null)
        {
            return false;
        }

        var rows = hierarchy.Peers.DispatchRecordsFor(sessionId).Where(r => r.Member("request_id") is { IsText: true } id && id.AsString() == requestId).ToList();
        if (rows.Count == 0)
        {
            return home is not null && hierarchy.Core.ListExchangesWithIds([home], [requestId]).Any(e => e.Member("id") is { IsText: true } id && id.AsString() == requestId
                && e.Member("response")?.Member("path") is { IsText: true } retired && Links.ClosedOnPurpose(hierarchy.Core, retired.AsString()));
        }

        if (rows[0].Member("path") is not { IsText: true } path || hierarchy.Messages.ResponsePlan(path).Member("path") is not { IsText: true } response)
        {
            return false;
        }

        return reached.Value(requestId, response.AsString())
            || hierarchy.Messages.ResponseLanded(response.AsString(), double.MaxValue)
            || Links.ClosedOnPurpose(hierarchy.Core, response.AsString());
    }

    public void ResolveEvent(string dir, string id, string by, string? sessionId) => _ = mailbox.AckIds(dir, [id], by, sessionId, EventRevalidation.ResolvedReason);

    public bool WrittenByOrchestrator(string responsePath) =>
        hierarchy.Core.ReadMsgFile(responsePath).Member("fm")?.Member("from") is { IsText: true } author && author.AsString() == "orchestrator";

    public FullMail FullMailText(string? sessionId, CompactMail compact)
    {
        if (RegistrationValue(sessionId) is not { } registration)
        {
            return new FullMail(null, [], compact.Address, "no registration", []);
        }

        var boxes = mailbox.RegistrationBoxes(registration);
        var groups = new List<(string Address, List<JsonValue> Entries)>();
        var read = new Dictionary<string, IReadOnlyList<JsonValue>>(StringComparer.Ordinal);
        var loaded = new List<string>();
        var missing = new List<string>();
        foreach (var mark in compact.Markers)
        {
            var address = string.IsNullOrEmpty(mark.Address) ? compact.Address : mark.Address;
            var box = boxes.FirstOrDefault(b => b.Address == address);
            if (box is null)
            {
                missing.AddRange(mark.Ids);
                continue;
            }

            if (!read.TryGetValue(box.Dir, out var entries))
            {
                entries = mailbox.ReadEntries(box.Dir).Entries;
                read[box.Dir] = entries;
            }

            foreach (var id in mark.Ids)
            {
                var entry = entries.FirstOrDefault(e => e.IsMap && e.AsObject().Get("id") is { IsText: true } t && t.AsString() == id);
                if (!entry.IsMap)
                {
                    missing.Add(id);
                    continue;
                }

                if (entry.AsObject().Get("event") is { IsText: true } && !EventApplies(sessionId, entry))
                {
                    ResolveEvent(box.Dir, id, "prompt-hook", sessionId);
                    continue;
                }

                var group = groups.FindIndex(g => g.Address == address);
                if (group < 0)
                {
                    groups.Add((address, []));
                    group = groups.Count - 1;
                }

                if (mailbox.LadderRowsOf(box.Dir, id) is { } rows && DrainedState.Is(rows))
                {
                    entry = DrainedStandIn(entry.AsObject(), rows[^1]);
                }
                else if (entry.AsObject().Get("event") is { IsText: true })
                {
                    entry = EventShown(sessionId, entry);
                }

                groups[group].Entries.Add(entry);
                loaded.Add(id);
            }
        }

        var reason = missing.Count > 0 ? "not in your mailbox" : null;
        if (loaded.Count == 0)
        {
            return new FullMail(null, [], compact.Address, reason, missing);
        }

        var json = new JsonArray();
        foreach (var (address, entries) in groups)
        {
            var list = new JsonArray();
            foreach (var e in entries)
            {
                list.Add(mailbox.MarkRelayVoid(e));
            }

            json.Add(new JsonObject { ["address"] = address, ["entries"] = list });
        }

        return new FullMail(core.BuildPrompt(json), loaded, compact.Address, reason, missing);
    }

    /// <summary>A note that says the drained entry was not delivered, in place of its text, with the ids the prompt keys on kept.</summary>
    private static JsonValue DrainedStandIn(JsonObject entry, JsonObject drainedRow)
    {
        var by = drainedRow.Get("by") is { IsText: true } b ? b.AsString().Split(':') : [];
        var kind = by.Length > 1 ? by[1] : "held";
        var team = drainedRow.Get("team") is { IsText: true } t ? t.AsString() : "this team";
        var id = entry.Get("id") is { IsText: true } i ? i.AsString() : string.Empty;
        return new JsonObject
        {
            ["v"] = 1,
            ["id"] = id,
            ["from"] = "ah",
            ["to"] = entry.Get("to") ?? JsonValue.Null,
            ["kind"] = "note",
            ["route"] = "pull",
            ["created"] = entry.Get("created") ?? JsonValue.Null,
            ["text"] = DrainedState.Line(team, kind, id),
        };
    }

    public IReadOnlyList<OpenBrief> OpenMailBriefs(string? sessionId)
    {
        var open = new List<OpenBrief>();
        foreach (var box in mailbox.RegistrationBoxes(RegistrationValue(sessionId)))
        {
            foreach (var v in mailbox.ReadEntries(box.Dir).Entries)
            {
                var e = v.AsObject();
                if (!(e.Get("kind") is { IsText: true } kind && kind.AsString() == "brief") || e.Get("ref") is not { IsText: true } reference)
                {
                    continue;
                }

                var id = e.Get("id")!.Value.AsString();
                if (mailbox.LadderRowsOf(box.Dir, id) is { Count: > 0 } ladder && ladder[^1].Get("state") is { IsText: true } ended && ended.AsString() is "withdrawn" or DrainedState.State)
                {
                    continue;
                }

                var ack = mailbox.ReadAck(box.Dir, id);
                if (ack is not { IsMap: true } a
                    || !(a.AsObject().Get("by") is { IsText: true } by && by.AsString() is "prompt-hook" or "stop-hook")
                    || !(a.AsObject().Get("session_id") is { IsText: true } sid && sid.AsString() == sessionId))
                {
                    continue;
                }

                var meta = hierarchy.Core.ParseMsgFilename(reference.AsString());
                if (meta is null)
                {
                    continue;
                }

                var exchange = ExchangeOfBrief(reference.AsString(), meta.Id);
                if (exchange.IsMap && exchange.AsObject().Get("open") is { IsTruthy: true })
                {
                    open.Add(new OpenBrief(id, reference.AsString(), e.Get("from")));
                }
            }
        }

        return open;
    }

    public IReadOnlyList<OpenBrief> OwedBriefs(string? sessionId)
    {
        var owed = new List<OpenBrief>();
        foreach (var box in mailbox.RegistrationBoxes(RegistrationValue(sessionId)))
        {
            foreach (var v in mailbox.ReadEntries(box.Dir).Entries)
            {
                var e = v.AsObject();
                if (!(e.Get("kind") is { IsText: true } kind && kind.AsString() == "brief")
                    || e.Get("ref") is not { IsText: true } reference
                    || e.Get("id") is not { IsText: true } idText)
                {
                    continue;
                }

                var id = idText.AsString();
                var rows = mailbox.LadderRowsOf(box.Dir, id);
                if (DrainedState.IsClosed(rows))
                {
                    continue;
                }

                var brief = new OpenBrief(id, reference.AsString(), e.Get("from"));
                if (ReportStateOf(sessionId, brief, out _) != ReportState.Sent)
                {
                    owed.Add(brief);
                }
            }
        }

        return owed;
    }

    /// <summary>The exchange of a brief over the pools of its request, so a response filed in a member's work-tree pool closes it.</summary>
    private JsonValue ExchangeOfBrief(string reference, string requestId) =>
        HierarchyModule.CreateExchangeIndex(hierarchy.Core, hierarchy.Messages.PoolsOfRequest(reference)).Find(requestId);

    public MailboxSummary Summary(double? nowMs = null)
    {
        var now = nowMs ?? clock.UtcNow.ToUnixTimeMilliseconds();
        var registrations = new List<RegistrationSummary>();
        foreach (var r in mailbox.LiveRegistrations())
        {
            var boxes = mailbox.RegistrationBoxes(r);
            var hb = boxes.Count == 0 ? null : mailbox.ReadHeartbeat(boxes[0].Dir);
            var adapter = hb is null ? "none" : mailbox.HeartbeatFresh(hb, now) ? "fresh" : "stale";
            registrations.Add(new RegistrationSummary(
                JsConvert.ToJsString(r.Get("address")),
                r.Get("harness") is { IsTruthy: true } h ? JsConvert.ToJsString(h) : null,
                adapter));
        }

        var overdue = 0;
        foreach (var b in mailbox.ListBoxes())
        {
            overdue += mailbox.Inbox(b.Dir, new InboxOptions(Overdue: true, NowMs: now)).Entries.Count;
        }

        return new MailboxSummary(registrations, overdue);
    }
}
