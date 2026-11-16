using System.Globalization;
using System.Text;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Time;

namespace Ah.Mesh;

/// <summary>Who is asking the doorbell: the mod's mail poll (a prompt), the Stop hook at the end of a turn, or the mod between tool calls of a busy turn.</summary>
public enum DoorbellCarrier
{
    Prompt,
    Stop,
    Tool,
}

/// <summary>One doorbell question: <paramref name="BusyAt"/> is the start of the session's current turn, or null when it is idle.</summary>
public sealed record DoorbellRequest(string Session, double Now, double? BusyAt, IReadOnlyDictionary<string, HandedState> Handed, DoorbellCarrier Carrier)
{
    /// <summary>The hierarchy directory of the asking session; an escalation is handed to the lead's box only when it is known.</summary>
    public string? HierarchyDir { get; init; }

    /// <summary>The engine refused the mod's last ring because the person stopped the session and has sent nothing since: nothing rings until a prompt enters.</summary>
    public bool Held { get; init; }
}

/// <summary>
/// A ring: the one line the session is handed (<paramref name="Text"/>), the id the mod tracks it by, and the entry ids it names
/// when it carries a single entry inline (<paramref name="Inline"/>), which the existing prompt machinery acknowledges.
/// </summary>
public sealed record DoorbellRing(string Id, string Text, IReadOnlyList<string> EntryIds, bool Urgent, bool Inline, string Kind);

/// <summary>A condition the recipient's lead should hear about: <c>mail-unacked</c> or <c>member-down</c>.</summary>
public sealed record DoorbellEscalation(string Kind, string Session, string Address, string Text);

/// <summary>What the doorbell decided: a ring or none, the ring ids positively acknowledged, the escalation to raise, and when to ask again.</summary>
public sealed record DoorbellDecision(DoorbellRing? Ring, IReadOnlyList<string> Acked, DoorbellEscalation? Escalation, double? NextDeadlineMs);

/// <summary>One digest entry: its box, id, kind, sender, the request it names (if any), its text or <c>--ref</c> path, and when it arrived.</summary>
public sealed record DigestEntry(string Dir, string Id, string Kind, string From, string? RequestId, string Body, string Created);

/// <summary>What <c>ah msg next</c> prints and the counts behind its footer.</summary>
public sealed record NextResult(string Text, int Left, int New, bool Empty);

/// <summary>The doorbell: one decider over the mailbox for the sessions that own an <c>orchestrator@</c> box, and the pull that drains it.</summary>
public interface IMailDoorbell
{
    /// <summary>Whether the doorbell serves <paramref name="session"/>: the kill switch is off and it owns an <c>orchestrator@</c> box.</summary>
    bool Serves(string session);

    /// <summary>Whether any unacked, pullable item in the session's boxes is urgent (rank 2 or better). Claims no ring; like <see cref="Decide"/>, it acks events that no longer apply.</summary>
    bool HasUrgent(string session, double now);

    /// <summary>Decides whether to ring now, claiming the ring atomically; at most one carrier gets any ring.</summary>
    DoorbellDecision Decide(DoorbellRequest request);

    /// <summary>
    /// The unacked digest entries (notes and advisories, rank 5, including those the backstop now rings for) of the session's
    /// <c>orchestrator@</c> boxes, oldest first. Acks nothing; like <see cref="Decide"/>, it acks events that no longer apply.
    /// </summary>
    IReadOnlyList<DigestEntry> DigestEntries(string session, double now);

    /// <summary>
    /// The next item for <paramref name="session"/> (or, with <paramref name="peek"/>, one line per item; with <paramref name="again"/>,
    /// the last items read). <paramref name="team"/> restricts the boxes drained to that team's.
    /// </summary>
    NextResult Pull(string session, double now, bool peek, bool again, string? team);

    /// <summary>
    /// Every queued item in order, each acked as it is printed. With <paramref name="peek"/> it is the plain peek and acks nothing.
    /// A failure on any item stops the drain: the items printed so far stay acked, the rest stay queued, and the text ends with a line saying so.
    /// </summary>
    NextResult PullAll(string session, double now, bool peek, string? team);
}

internal sealed class MailDoorbell(Mailbox mailbox, IMailCore core, IFileReader files, IAhEnvironment environment, IMailboxHooks? hooks) : IMailDoorbell
{
    private const string NoteKind = "doorbell";
    private const string OrchestratorPrefix = "orchestrator@";
    private const string ChiefPrefix = "chief@";
    private const int RecentKept = 5;

    /// <summary>A box the doorbell serves: a team's <c>orchestrator@</c> box, or its <c>chief@</c> box while a deputy is on duty.</summary>
    private static bool IsLeadBox(string address) =>
        address.StartsWith(OrchestratorPrefix, StringComparison.Ordinal) || address.StartsWith(ChiefPrefix, StringComparison.Ordinal);
    private const int PeekChars = 80;
    private const int GroupLines = 30;
    private const int UrgentLinesShown = 3;
    private const int UrgentLineChars = 200;

    /// <summary>How many reminders an unread queue gets before they stop until new mail arrives: about 4.2 h for standard mail, 44 min for urgent.</summary>
    private const int RemindersMax = 6;

    internal sealed record Item(int Rank, string Class, string Dir, string Address, IReadOnlyList<JsonValue> Entries, string? Event, bool Digest = false)
    {
        internal IReadOnlyList<string> Ids { get; } = [.. Entries.Select(e => Text(e, "id"))];

        internal string Newest => Ids.Max(StringComparer.Ordinal)!;

        internal string Created => Text(Entries[0], "created");

        internal string From => Text(Entries[0], "from");

        internal bool Urgent => Rank <= 2;

        /// <summary>Whether this is an alarm: a clock or watch reporting that something is late, which waits for the session to be idle and never interrupts a turn.</summary>
        internal bool Alarm => Event is not null && AlarmEvents.Contains(Event);
    }

    private static readonly HashSet<string> AlarmEvents = new(StringComparer.Ordinal)
    {
        "check-in-due", "peer-silent", "overdue", "idle-without-report", "job-overdue", "queue-stall", "stalled-after-compact", "lane-escalated", "unlinked-dispatch", "member-waiting", "unreviewed-done",
    };

    /// <summary>The watcher's idle-members alarm (<c>QueueEvents.IdleCapacityName</c>).</summary>
    private const string IdleCapacityEvent = "idle-capacity";

    /// <summary>The user's answer to a question: never grouped, so each is pulled, shown and acked alone (b528).</summary>
    private const string QuestionAnsweredEvent = "question-answered";

    /// <summary>The request an alarm entry is about, or empty when it names none.</summary>
    private static string SubjectOf(JsonValue entry) =>
        entry.Member("data")?.Member("request_id") is { IsText: true } id ? id.AsString() : string.Empty;

    internal static string Text(JsonValue e, string key) => e.Member(key) is { IsText: true } t ? t.AsString() : string.Empty;

    public bool Serves(string session) => ServesSession(mailbox, environment, session);

    /// <summary>The one "the doorbell serves this session" test: the kill switch is off and the session registered an <c>orchestrator@</c> box.</summary>
    internal static bool ServesSession(Mailbox mailbox, IAhEnvironment environment, string session) =>
        environment.GetVariable("AH_MAIL_DOORBELL") != "0" && OrchestratorBoxesOf(mailbox, session).Count > 0;

    /// <summary>
    /// A peer's plain note to a member with a live mail poll: it waits for the member's next turn and is shown in the digest. Anything that
    /// may need the member now keeps its carrier: a brief or report, a <c>QUESTION:</c>, a person's or ah's own note (ah's text starts <c>ah:</c>),
    /// a linked message, or an answer to something.
    /// </summary>
    internal static bool IsMemberDigestNote(JsonValue entry, string boxAddress, IAhEnvironment environment) =>
        environment.GetVariable("AH_MAIL_DOORBELL") != "0"
        && !IsLeadBox(boxAddress)
        && Text(entry, "kind") == "note" && Text(entry, "route") == "pull"
        && Text(entry, "from") is not ("ahd" or "user")
        && entry.Member("link") is not { IsTruthy: true } && entry.Member("reply_to") is not { IsTruthy: true }
        && !Text(entry, "text").StartsWith("QUESTION:", StringComparison.Ordinal) && !Text(entry, "text").StartsWith("ah:", StringComparison.Ordinal);

    public bool HasUrgent(string session, double now) => Queue(session, now).Any(static i => i.Urgent);

    public IReadOnlyList<DigestEntry> DigestEntries(string session, double now) =>
        [.. Queue(session, now).Where(static i => i.Digest)
            .SelectMany(i => i.Entries.Select(e => new DigestEntry(
                i.Dir, Text(e, "id"), Text(e, "kind"), Text(e, "from"), SubjectOf(e) is { Length: > 0 } request ? request : null,
                Text(e, "ref") is { Length: > 0 } reference ? reference : Text(e, "text"), Text(e, "created"))))
            .Concat(MemberDigestEntries(session))
            .OrderBy(static d => d.Created, StringComparer.Ordinal).ThenBy(static d => d.Id, StringComparer.Ordinal)];

    private IEnumerable<DigestEntry> MemberDigestEntries(string session)
    {
        var boxes = mailbox.ReadRegistration(session) is { } registration ? mailbox.RegistrationBoxes(registration) : [];
        var options = new PullOptions(SessionId: session);
        return boxes.SelectMany(box => mailbox.ReadEntries(box.Dir).Entries
            .Where(e => IsMemberDigestNote(e, box.Address, environment) && !mailbox.IsAcked(box.Dir, Text(e, "id")) && core.Pullable(e, options))
            .Select(e => MemberDigestEntry(box.Dir, e)));
    }

    /// <summary>A member digest note of the box at <paramref name="dir"/> as the digest shows it.</summary>
    internal static DigestEntry MemberDigestEntry(string dir, JsonValue e) =>
        new(dir, Text(e, "id"), "note", Text(e, "from"), null, Text(e, "text"), Text(e, "created"));

    /// <summary>Most lines in the digest block; rank 5 entries are kept first when it cuts.</summary>
    private const int DigestCap = 20;

    private const int DigestBodyChars = 200;

    /// <summary>
    /// The digest block: <see cref="Ah.Config.IDirectiveText.DigestHeader"/>, one line per entry of <paramref name="entries"/> it shows (kind, sender,
    /// request, the first 200 characters of its text or <c>--ref</c> path), then <paramref name="extraLines"/>, at most 20 lines with a
    /// <c>+K more</c> line for the entries cut. <paramref name="shown"/> is the entries it shows, which the caller acks. Null when empty.
    /// </summary>
    internal static string? DigestText(IReadOnlyList<DigestEntry> entries, IReadOnlyList<string> extraLines, out IReadOnlyList<DigestEntry> shown)
    {
        var taken = entries.Take(DigestCap).ToList();
        shown = taken;
        var lines = taken.Select(static e =>
            $"- {e.Kind} from {e.From}{(e.RequestId is null ? string.Empty : $" ({e.RequestId})")}: {Slice(e.Body.ReplaceLineEndings(" "), DigestBodyChars)}").ToList();
        lines.AddRange(extraLines.Take(DigestCap - lines.Count).Select(static l => "- " + l));
        if (lines.Count == 0)
        {
            return null;
        }

        var left = entries.Count - taken.Count;
        return Ah.Config.IDirectiveText.DigestHeader(lines.Count) + "\n" + string.Join('\n', lines) + (left > 0 ? "\n" + Ah.Config.IDirectiveText.DigestMore(left) : string.Empty);
    }

    private static string Slice(string text, int length) => text.Length <= length ? text : text[..length];

    private List<BoxRef> OrchestratorBoxes(string session) => OrchestratorBoxesOf(mailbox, session);

    private static List<BoxRef> OrchestratorBoxesOf(Mailbox mailbox, string session)
    {
        if (mailbox.ReadRegistration(session) is not { } registration)
        {
            return [];
        }

        var boxes = mailbox.RegistrationBoxes(registration).Where(b => IsLeadBox(b.Address)).ToList();
        boxes.AddRange(boxes.Select(b => ChiefTakesBack(mailbox, registration, b.Address) ?? OwnerKeepsChief(mailbox, registration, b.Address)).OfType<BoxRef>().Where(o => boxes.All(b => b.Address != o.Address)).ToList());
        return boxes;
    }

    /// <summary>The session's <c>chief@T</c> boxes whose team has a live deputy on duty: only urgent items there ring; the rest wait for the tally and a pull.</summary>
    private HashSet<string> QuietChiefBoxes(string session) =>
        mailbox.ReadRegistration(session) is not { } registration
            ? []
            : [.. mailbox.RegistrationBoxes(registration)
                .Where(b => b.Address.StartsWith(ChiefPrefix, StringComparison.Ordinal) && RegistrationTeams.For(registration, b.Address) is { } teamFile
                    && mailbox.DeputyOnDutyLive(teamFile, b.Address[ChiefPrefix.Length..]))
                .Select(static b => b.Address)];

    /// <summary>
    /// For an <c>orchestrator@T</c> box held by T's recorded owner: T's <c>chief@T</c> box, always, since only the recorded owner ever
    /// holds it; mail landing there after <c>duty off</c> rings the chief with nothing re-registered. Null otherwise (a deputy on duty
    /// holds <c>orchestrator@T</c> without being the record's owner).
    /// </summary>
    private static BoxRef? OwnerKeepsChief(Mailbox mailbox, JsonObject registration, string address)
    {
        if (!address.StartsWith(OrchestratorPrefix, StringComparison.Ordinal) || RegistrationTeams.For(registration, address) is not { } teamFile
            || !mailbox.OwnsByRecord(teamFile, registration))
        {
            return null;
        }

        return new BoxRef(ChiefPrefix + address[OrchestratorPrefix.Length..], mailbox.BoxDirFor(teamFile, "chief"));
    }

    /// <summary>
    /// For a <c>chief@T</c> box: T's <c>orchestrator@T</c> box while T has no live deputy on duty, so a chief whose registration still
    /// names <c>chief@T</c> after its deputy died is rung for that box with nothing re-registered. Null otherwise.
    /// </summary>
    private static BoxRef? ChiefTakesBack(Mailbox mailbox, JsonObject registration, string address)
    {
        if (!address.StartsWith(ChiefPrefix, StringComparison.Ordinal) || RegistrationTeams.For(registration, address) is not { } teamFile)
        {
            return null;
        }

        var team = address[ChiefPrefix.Length..];
        return mailbox.DeputyOnDutyLive(teamFile, team) ? null : new BoxRef(OrchestratorPrefix + team, mailbox.BoxDirFor(teamFile, "orchestrator"));
    }

    private const string UncarriedKind = "doorbell-uncarried";

    /// <summary>Leaves the ladder's failed wake for the session's next <see cref="Decide"/>, which alone can reach the person.</summary>
    internal static void MarkUncarried(Mailbox mailbox, string session, double now, string reason)
    {
        if (!Mailbox.IsName(session))
        {
            return;
        }

        var seq = mailbox.ReadNote(UncarriedKind, session)?.Get("seq") is { IsNumber: true } n ? n.AsNumber() : 0;
        _ = mailbox.WriteNoteIf(UncarriedKind, session, new JsonObject { ["seq"] = seq + 1, ["at"] = now, ["reason"] = reason }, seq);
    }

    /// <summary>
    /// Whether an idle session has left its mail unread for <see cref="IMailCore.DoorbellRemindMs"/> and the last ring tried in that time did not enter: its mod polls but no ring
    /// is reaching it (a session running a mod older than the daemon, for one), so the delivery ladder wakes its pane. A mod that knows a Stop reports the drop of that ring within
    /// the grace, and a held session has no clock. A mod older than the drop never reports it, so its Stop can be pasted over until it reloads: accepted, because that same mod
    /// loses every ring and the paste is the only thing that reaches it.
    /// </summary>
    internal static bool RingsGoUnread(Mailbox mailbox, string session, double now)
    {
        if (!Mailbox.IsName(session))
        {
            return false;
        }

        var state = State.From(mailbox.ReadNote(NoteKind, session));
        return state.UnreadSince > 0 && state.BusySince <= 0 && now - state.UnreadSince >= IMailCore.DoorbellRemindMs
            && state.RangAt >= state.UnreadSince && !RingEntered(state) && now - state.RangAt >= IMailCore.DoorbellReringMs;
    }

    // A prompt ring's turn key is rewritten by the turn it starts; a Stop or tool ring is carried inside a turn from the start.
    private static bool RingEntered(State state) => state.RangTurn.Length > 0 && !state.RangTurn.StartsWith("ring:", StringComparison.Ordinal);

    // ---------------------------------------------------------------- the queue

    /// <summary>The unacked, pullable entries of the session's orchestrator boxes as ranked items; an event that no longer applies is acked away.</summary>
    internal List<Item> Queue(string session, double now, string? team = null, bool idle = false, Action<double>? revealAt = null)
    {
        var entries = new List<(BoxRef Box, JsonValue Entry)>();
        foreach (var box in OrchestratorBoxes(session))
        {
            if (team is not null && box.Address != OrchestratorPrefix + team && box.Address != ChiefPrefix + team)
            {
                continue;
            }

            var heartbeat = mailbox.ReadHeartbeat(box.Dir, session) ?? mailbox.ReadHeartbeat(box.Dir);
            var options = new PullOptions(SessionId: session, Now: now, Hb: heartbeat is null ? JsonValue.Null : heartbeat);
            foreach (var entry in mailbox.ReadEntries(box.Dir).Entries)
            {
                var id = Text(entry, "id");
                if (id.Length == 0 || mailbox.IsAcked(box.Dir, id) || !core.Pullable(entry, options))
                {
                    continue;
                }

                if (Text(entry, "kind") == "event" && hooks is not null && !hooks.EventApplies(session, entry))
                {
                    hooks.ResolveEvent(box.Dir, id, "adapter", session);
                    continue;
                }

                if (RelayCarries(box.Dir, id, now, idle, revealAt))
                {
                    continue;
                }

                // b336: a pull shows an event as the prompt hook does (EventRevalidation.Shown)
                entries.Add((box, Text(entry, "kind") == "event" && hooks is not null ? hooks.EventShown(session, entry) : entry));
            }
        }

        var items = new List<Item>();
        foreach (var byBox in entries.GroupBy(x => x.Box.Dir))
        {
            var eventGroups = byBox.Where(x => Text(x.Entry, "kind") == "event" && Text(x.Entry, "event").Length > 0)
                .GroupBy(x => (Event: Text(x.Entry, "event"), Alone: Text(x.Entry, "event") == QuestionAnsweredEvent ? Text(x.Entry, "id") : null));
            foreach (var group in eventGroups)
            {
                var name = group.Key.Event;
                var box = group.First().Box;
                var kept = group.Select(x => x.Entry).ToList();
                if (AlarmEvents.Contains(name) || name == IdleCapacityEvent)
                {
                    // One wake per alarm kind and subject request: a newer alarm replaces the pending one instead of adding a second.
                    // b336: idle-capacity names no request; its box is its team, so the newest one replaces every older one.
                    string Subject(JsonValue e) => name == IdleCapacityEvent ? name : SubjectOf(e);
                    var superseded = kept
                        .Where(e => Subject(e).Length > 0)
                        .GroupBy(Subject)
                        .SelectMany(g => g.OrderBy(e => Text(e, "id"), StringComparer.Ordinal).SkipLast(1))
                        .ToList();
                    foreach (var old in superseded)
                    {
                        _ = mailbox.AckIds(box.Dir, [Text(old, "id")], "adapter", session, EventRevalidation.ResolvedReason);
                    }

                    kept = [.. kept.Except(superseded)];
                }

                var eventRank = RankOfEvent(name);
                items.Add(new Item(Backstopped(eventRank, kept, now), "event", box.Dir, box.Address, kept, name, eventRank == DigestRank));
            }

            foreach (var (box, entry) in byBox.Where(x => !(Text(x.Entry, "kind") == "event" && Text(x.Entry, "event").Length > 0)))
            {
                var entryRank = RankOf(entry);
                items.Add(new Item(Backstopped(entryRank, [entry], now), "entry", box.Dir, box.Address, [entry], null, entryRank == DigestRank));
            }
        }

        return [.. items.OrderBy(i => i.Rank).ThenBy(i => i.Created, StringComparer.Ordinal).ThenBy(i => i.Newest, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Whether a relay SendMessage carries this entry now, so neither a ring nor a pull may hand it over a second time. Mail a relay sent is
    /// acked when the receiver's prompt hook sees it, or at its Stop when it drained mid-turn, so it stays hidden only for a bounded hold: a
    /// relay that never landed comes back here, because the ladder does not release an orchestrator box's relay. An idle receiver would
    /// have taken it at once, so its hold is the short one.
    /// </summary>
    private bool RelayCarries(string dir, string id, double now, bool idle, Action<double>? revealAt)
    {
        if (mailbox.ReadClaim(dir, id) is not { } claim || claim.Get("carrier") is not { IsText: true } carrier || carrier.AsString() != "relay"
            || ClaimMachine.Live(claim) is null)
        {
            return false;
        }

        var step = mailbox.AdvanceClaim(dir, id);
        var record = step.Record ?? claim;
        double? until;
        switch (step.State)
        {
            case ClaimStates.Delivered:
                return true;
            case ClaimStates.Pending or ClaimStates.RelayClaimed:
                until = MailDue.ClaimDeadline(record);
                break;
            case ClaimStates.RelaySent or ClaimStates.RelayUnconfirmed:
                until = record.Get("at") is { IsText: true } at && Timestamps.TryParseMs(at.AsString(), out var sentAt)
                    ? sentAt + (idle ? IMailCore.ResubmitMs : IMailCore.DoorbellRemindMs)
                    : null;
                break;
            default:
                return false;
        }

        if (until is not { } end || end <= now)
        {
            return false;
        }

        revealAt?.Invoke(end);
        return true;
    }

    /// <summary>The digest rank: notes and advisories, which never ring by themselves and are read at the session's next prompt.</summary>
    internal const int DigestRank = 5;

    /// <summary>A digest item still unread <see cref="IMailCore.DoorbellRemindCapMs"/> after its oldest entry arrived is ranked as an alarm (4), so an idle session is rung for it.</summary>
    private static int Backstopped(int rank, IReadOnlyList<JsonValue> entries, double now) =>
        rank == DigestRank && entries.Any(e => CreatedMs(e) > 0 && now - CreatedMs(e) >= IMailCore.DoorbellRemindCapMs) ? 4 : rank;

    private static int RankOfEvent(string name) => name switch
    {
        "user-ask" or "question-answered" => 1,
        "member-blocked" or "member-stuck" or "member-stuck-escalated" or "step-needs-user" or "classifier-denied" or "mail-unacked"
            or "delivery-failed" or "api-failed" or "member-down" or "resume" => 2,
        "report-owed-advisory" => DigestRank,
        _ => 4,
    };

    private int RankOf(JsonValue entry)
    {
        var kind = Text(entry, "kind");
        var text = Text(entry, "text");
        if (kind == "brief" || text.StartsWith("QUESTION:", StringComparison.Ordinal))
        {
            return 1;
        }

        if (kind == "note")
        {
            return DigestRank;
        }

        if (kind == "event")
        {
            return 4;
        }

        return kind == "report" && StatusIsBlocked(Text(entry, "ref")) ? 2 : 3;
    }

    /// <summary>Whether the response file at <paramref name="path"/> says BLOCKED, NEEDS-… or a red build (FAIL, SYSTEMIC, ERROR as its whole first word) on the line under its status heading.</summary>
    private bool StatusIsBlocked(string path)
    {
        if (path.Length == 0)
        {
            return false;
        }

        try
        {
            var lines = files.ReadText(path).Split('\n');
            for (var i = 0; i < lines.Length - 1; i++)
            {
                if (lines[i].TrimEnd().StartsWith("## [1] status", StringComparison.Ordinal))
                {
                    var status = lines.Skip(i + 1).Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? string.Empty;
                    status = status.TrimStart('-', ' ', '*');
                    var firstWord = status.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.TrimEnd(':', '*');
                    return status.StartsWith("BLOCKED", StringComparison.Ordinal) || status.StartsWith("NEEDS-", StringComparison.Ordinal) || firstWord is "FAIL" or "SYSTEMIC" or "ERROR";
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }

        return false;
    }

    private static (int Questions, int Blocked, int Reports, int Alarms) Counts(List<Item> items) =>
        (items.Count(i => i.Rank == 1), items.Count(i => i.Rank == 2), items.Count(i => i.Rank == 3), items.Count(i => i.Rank == 4));

    private static string CountsText(List<Item> items)
    {
        var (q, b, r, a) = Counts(items);
        var parts = new List<string>();
        void Add(int n, string name)
        {
            if (n > 0)
            {
                parts.Add(string.Create(CultureInfo.InvariantCulture, $"{n} {name}"));
            }
        }

        Add(q, q == 1 ? "question" : "questions");
        Add(b, "blocked");
        Add(r, r == 1 ? "report" : "reports");
        Add(a, a == 1 ? "alarm" : "alarms");
        return string.Join(", ", parts);
    }

    private string Line(List<Item> items, int newCount, string kind)
    {
        var counts = CountsText(items);
        var n = items.Count;
        var head = kind == "reminder"
            ? string.Create(CultureInfo.InvariantCulture, $"ah: reminder: {n} unread ({counts}). Run `ah msg next`.")
            : newCount > 0 && newCount < n
                ? string.Create(CultureInfo.InvariantCulture, $"ah: {newCount} new; {n} unread ({counts}). Run `ah msg next`.")
                : string.Create(CultureInfo.InvariantCulture, $"ah: You have {n} {(n == 1 ? "message" : "messages")} ({counts}). Run `ah msg next`.");
        var urgent = items.Where(static i => i.Urgent).ToList();
        var lines = new List<string> { head };
        lines.AddRange(urgent.Take(UrgentLinesShown).Select(UrgentLine));
        if (urgent.Count > UrgentLinesShown)
        {
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"+{urgent.Count - UrgentLinesShown} more urgent"));
        }

        return string.Join('\n', lines);
    }

    /// <summary>What an urgent item is, on one capped line: the event name or kind, then the newest entry's text, else its ref.</summary>
    private string UrgentLine(Item item)
    {
        var newest = item.Entries.First(e => Text(e, "id") == item.Newest);
        var line = $"- {item.Event ?? Text(newest, "kind")}: {core.OneLine(newest.Member(Text(newest, "text").Length > 0 ? "text" : "ref"))}";
        return line.Length > UrgentLineChars ? line[..UrgentLineChars] : line;
    }

    // ---------------------------------------------------------------- the ring state

    private sealed class State
    {
        internal double Seq;
        internal double RangAt;
        internal string RangTurn = string.Empty;
        internal string RingId = string.Empty;
        internal string InlineId = string.Empty;
        internal string ToldMark = string.Empty;
        internal string ReadMark = string.Empty;
        internal double RingAckedAt;
        internal int Rerings;
        internal double RemindAt;
        internal int RemindStep;
        internal bool Escalated;
        internal bool DownTold;
        internal double LastBusyAt;
        internal double BusySince;
        internal bool BusyTold;
        internal double UnreadSince;
        internal double HeldSince;
        internal bool HeldMarked;
        internal bool UncarriedTold;
        internal List<string> Recent = [];

        internal static State From(JsonObject? o)
        {
            var s = new State();
            if (o is null)
            {
                return s;
            }

            double Num(string k) => o.Get(k) is { IsNumber: true } n ? n.AsNumber() : 0;
            string Str(string k) => o.Get(k) is { IsText: true } t ? t.AsString() : string.Empty;
            s.Seq = Num("seq");
            s.RangAt = Num("rang_at");
            s.RangTurn = Str("rang_turn");
            s.RingId = Str("ring_id");
            s.InlineId = Str("inline_id");
            s.ToldMark = Str("told_mark");
            s.ReadMark = Str("read_mark");
            s.RingAckedAt = Num("ring_acked_at");
            s.Rerings = (int)Num("rerings");
            s.RemindAt = Num("remind_at");
            s.RemindStep = (int)Num("remind_step");
            s.Escalated = o.Get("escalated") is { IsBool: true } e && e.AsBool();
            s.DownTold = o.Get("down_told") is { IsBool: true } d && d.AsBool();
            s.LastBusyAt = Num("last_busy_at");
            s.BusySince = Num("busy_since");
            s.BusyTold = o.Get("busy_told") is { IsBool: true } bt && bt.AsBool();
            s.UnreadSince = Num("unread_since");
            s.HeldSince = Num("held_since");
            s.HeldMarked = o.Get("held_marked") is { IsBool: true } hm && hm.AsBool();
            s.UncarriedTold = o.Get("uncarried_told") is { IsBool: true } ut && ut.AsBool();
            if (o.Get("recent") is { IsArray: true } recent)
            {
                s.Recent = [.. recent.AsArray().Where(r => r.IsText).Select(r => r.AsString())];
            }

            return s;
        }

        internal JsonObject ToJson()
        {
            var recent = new JsonArray();
            foreach (var r in Recent)
            {
                recent.Add(r);
            }

            return new JsonObject
            {
                ["v"] = 1d,
                ["seq"] = Seq,
                ["rang_at"] = RangAt,
                ["rang_turn"] = RangTurn,
                ["ring_id"] = RingId,
                ["inline_id"] = InlineId,
                ["told_mark"] = ToldMark,
                ["read_mark"] = ReadMark,
                ["ring_acked_at"] = RingAckedAt,
                ["rerings"] = (double)Rerings,
                ["remind_at"] = RemindAt,
                ["remind_step"] = (double)RemindStep,
                ["escalated"] = Escalated,
                ["down_told"] = DownTold,
                ["last_busy_at"] = LastBusyAt,
                ["busy_since"] = BusySince,
                ["busy_told"] = BusyTold,
                ["unread_since"] = UnreadSince,
                ["held_since"] = HeldSince,
                ["held_marked"] = HeldMarked,
                ["uncarried_told"] = UncarriedTold,
                ["recent"] = recent,
            };
        }
    }

    private State Load(string session) => Mailbox.IsName(session) ? State.From(mailbox.ReadNote(NoteKind, session)) : new State();

    /// <summary>Writes the state only if no other writer changed it since it held <paramref name="loadedSeq"/>; true when this writer won.</summary>
    private bool Save(string session, State state, double loadedSeq)
    {
        state.Seq = loadedSeq + 1;
        return Mailbox.IsName(session) && mailbox.WriteNoteIf(NoteKind, session, state.ToJson(), loadedSeq);
    }

    // ---------------------------------------------------------------- the decider

    // The note's compare-and-set settles two processes; this gate settles two threads of one process, which the file lock does not.
    private static readonly object Gate = new();

    private readonly Dictionary<string, string> _traced = new(StringComparer.Ordinal);

    /// <summary>
    /// One <c>doorbell-trace</c> row in the session's trace file when the inputs of the ring decision change, per session and carrier: the record that b99 lacks of why an idle
    /// session was not rung. A failed write fails nothing.
    /// </summary>
    // ponytail: b99 diagnostic; delete it once the live UI answer has named the cause.
    private void Trace(DoorbellRequest request, string dir, bool idle, bool ringPending, List<Item> items, State state)
    {
        var fresh = items.Count(i => string.CompareOrdinal(i.Newest, state.ToldMark) > 0);
        var handed = request.Handed.TryGetValue(state.RingId, out var record) ? record.State : "none";
        var key = string.Join('|', idle, ringPending, items.Count, fresh, state.RingId, handed, state.ToldMark);
        var slot = request.Session + " " + request.Carrier;
        if (_traced.GetValueOrDefault(slot) == key)
        {
            return;
        }

        _traced[slot] = key;
        try
        {
            mailbox.AppendTrace(request.Session, new JsonObject
            {
                ["state"] = "doorbell-trace",
                ["dir"] = dir,
                ["session_id"] = request.Session,
                ["carrier"] = request.Carrier.ToString(),
                ["now"] = request.Now,
                ["busy_at"] = request.BusyAt is { } busy ? busy : JsonValue.Null,
                ["idle"] = idle,
                ["ring_pending"] = ringPending,
                ["items"] = items.Count,
                ["new_items"] = fresh,
                ["ring_id"] = state.RingId,
                ["handed"] = handed,
                ["told_mark"] = state.ToldMark,
                ["rang_at"] = state.RangAt,
            });
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _ = e;
        }
    }

    public DoorbellDecision Decide(DoorbellRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (Gate)
        {
            var decision = DecideLocked(request);
            return decision.Escalation is { } escalation && request.HierarchyDir is { Length: > 0 } hier && SendToLead(escalation, hier)
                ? decision with { Escalation = null }
                : decision;
        }
    }

    /// <summary>
    /// Sends the escalation to the box of the lead of the escalating team: the team its record names as parent. Returns false, leaving
    /// the escalation to the caller (the top Orchestrator has no parent; the person is told instead), when there is no parent or the
    /// send fails.
    /// </summary>
    private bool SendToLead(DoorbellEscalation escalation, string hierarchyDir)
    {
        try
        {
            var team = escalation.Address.StartsWith(OrchestratorPrefix, StringComparison.Ordinal) ? escalation.Address[OrchestratorPrefix.Length..] : string.Empty;
            if (team.Length == 0 || team.Contains('/') || team.Contains('\\') || team.Contains(".."))
            {
                return false;
            }

            var record = mailbox.TeamRecordOf(hierarchyDir, team).Record;
            string to;
            if (record.Member("parent") is { IsMap: true } parent && parent.Member("team") is { IsText: true } lead && lead.AsString().Length > 0)
            {
                to = OrchestratorPrefix + lead.AsString();
            }
            else if (mailbox.DeputyOnDutyLive(mailbox.TeamPathIn(hierarchyDir, team), team))
            {
                // the deputy on duty is not taking its mail: its lead is the chief
                to = ChiefPrefix + team;
            }
            else
            {
                return false;
            }

            var request = new JsonObject
            {
                ["to"] = to,
                ["from"] = "ahd",
                ["cwd"] = hierarchyDir,
                ["hierarchy_dir"] = hierarchyDir,
                ["event"] = escalation.Kind,
                ["text"] = escalation.Text,
                ["data"] = new JsonObject { ["member"] = escalation.Address, ["session"] = escalation.Session },
            };
            return mailbox.Send(request).Value is { } sent && sent.Get("unchanged") is not { IsTruthy: true };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or MailboxError or InvalidOperationException or FormatException)
        {
            return false;
        }
    }

    public NextResult Pull(string session, double now, bool peek, bool again, string? team)
    {
        lock (Gate)
        {
            return PullLocked(session, now, peek, again, team);
        }
    }

    public NextResult PullAll(string session, double now, bool peek, string? team)
    {
        if (peek)
        {
            return Pull(session, now, true, false, team);
        }

        lock (Gate)
        {
            var texts = new List<string>();
            var fresh = 0;
            var left = 0;
            while (true)
            {
                NextResult one;
                try
                {
                    one = PullLocked(session, now, false, false, team);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or MailboxError or InvalidOperationException or FormatException)
                {
                    texts.Add($"ah: stopped after {texts.Count} item(s): {e.Message}. The rest stay queued; run `ah msg next --all` again.");
                    break;
                }

                if (one.Empty)
                {
                    break;
                }

                // The queue must shrink on every pass. It fails to when an item cannot be acked (an id the mailbox will not accept) or when
                // mail arrives mid-drain; either way the pulled item was already acked, so it is printed before the drain stops.
                texts.Add(one.Text);
                if (one.Left >= left && texts.Count > 1)
                {
                    texts.Add($"ah: stopped after {texts.Count} item(s): the queue stopped shrinking (an item could not be acked, or mail arrived during the drain). The rest stay queued; run `ah msg next --all` again.");
                    break;
                }

                if (one.Text == RecordFailedText)
                {
                    break;
                }

                fresh += one.New;
                left = one.Left;
                if (left == 0)
                {
                    break;
                }
            }

            return new NextResult(string.Join("\n\n", texts), left, fresh, texts.Count == 0);
        }
    }

    private DoorbellDecision DecideLocked(DoorbellRequest request)
    {
        var none = new DoorbellDecision(null, [], null, null);
        if (!Serves(request.Session))
        {
            return none;
        }

        var now = request.Now;
        var state = Load(request.Session);
        var loaded = state.Seq;
        var registration = mailbox.ReadRegistration(request.Session);
        var boxes = OrchestratorBoxes(request.Session);
        var acked = new List<string>();
        var idle = request.BusyAt is null || now - request.BusyAt.Value > IMailCore.BusyMaxMs;

        if (registration is null || !mailbox.RegistrationLive(registration))
        {
            if (state.DownTold)
            {
                return none;
            }

            state.DownTold = true;
            var unread = Queue(request.Session, now).Count;
            var down = new DoorbellEscalation(
                "member-down", request.Session, boxes[0].Address,
                string.Create(CultureInfo.InvariantCulture, $"{boxes[0].Address}: its session is gone; {unread} unread mail waiting."));
            return Save(request.Session, state, loaded) ? new DoorbellDecision(null, [], down, null) : none;
        }

        state.DownTold = false;
        if (idle)
        {
            state.BusySince = 0;
            state.BusyTold = false;
        }

        if (request.BusyAt is { } busy)
        {
            if (!idle && (state.BusySince <= 0 || busy - state.LastBusyAt > BusyContinuityMs))
            {
                state.BusySince = busy;
                state.BusyTold = false;
            }

            state.LastBusyAt = busy;
            if (state.RangTurn.StartsWith("ring:", StringComparison.Ordinal) && busy >= state.RangAt)
            {
                state.RangTurn = busy.ToString("R", CultureInfo.InvariantCulture);
            }
        }

        // A digest item never rings by itself: the ring decision, re-rings and reminders see only the other items.
        double? reveal = null;
        var quiet = QuietChiefBoxes(request.Session);
        var items = Queue(request.Session, now, idle: idle, revealAt: at => reveal = reveal is { } soonest ? Math.Min(soonest, at) : at)
            .Where(i => i.Rank != DigestRank && (i.Urgent || !quiet.Contains(i.Address) || i.Entries.Any(static e => e.Member("data")?.Member("copy") is { IsTruthy: true }))).ToList();
        var ringPending = state.RingId.Length > 0 && request.Handed.TryGetValue(state.RingId, out var handed) && handed.State == "pending";
        var inlineAcked = state.InlineId.Length > 0 && boxes.Any(b => mailbox.IsAcked(b.Dir, state.InlineId));

        // A later ring replaced these, so nothing will ack them: released here, or the mod's claim map grows with every re-ring until its request no longer fits the socket.
        acked.AddRange(request.Handed.Keys.Where(k => k.StartsWith(RingIdPrefix, StringComparison.Ordinal) && k != state.RingId));
        if (state.RingId.Length > 0 && (state.RingAckedAt > 0 || inlineAcked))
        {
            acked.Add(state.RingId);
        }

        Trace(request, boxes[0].Dir, idle, ringPending, items, state);
        if (items.Count == 0 || request.Held || !idle || inlineAcked)
        {
            state.UnreadSince = 0;
        }

        if (items.Count == 0 || !request.Held)
        {
            state.HeldSince = 0;
            state.HeldMarked = false;
        }

        if (items.Count == 0)
        {
            state.RemindStep = 0;
            state.RemindAt = 0;
            state.Rerings = 0;
            state.Escalated = false;
            state.BusyTold = false;
            return Finish(request, state, loaded, null, acked, null, none, reveal);
        }

        var newItems = items.Where(i => string.CompareOrdinal(i.Newest, state.ToldMark) > 0).ToList();
        var urgentNew = newItems.Any(i => i.Urgent);
        var urgentUnread = items.Any(i => i.Urgent);
        var ringAcked = state.RingAckedAt > 0 || inlineAcked;
        double? deadline = null;
        DoorbellRing? ring = null;
        DoorbellEscalation? escalation = null;

        // A Stop the mod has reported is not pasted over or rung through: they are told once, past the limit, what is waiting.
        if (request.Held && items.Any(static i => !i.Alarm))
        {
            state.HeldSince = state.HeldSince > 0 ? state.HeldSince : now;
            if (!state.HeldMarked && now - state.HeldSince >= IMailCore.DoorbellRemindMs)
            {
                state.HeldMarked = true;
                escalation = new DoorbellEscalation(
                    "mail-unacked", request.Session, boxes[0].Address,
                    string.Create(CultureInfo.InvariantCulture, $"{boxes[0].Address} is stopped with {items.Count} unread; type a message to continue."));
            }
        }

        if (request.Carrier == DoorbellCarrier.Stop)
        {
            var turn = state.LastBusyAt.ToString("R", CultureInfo.InvariantCulture);
            if ((newItems.Count > 0 || !ringAcked) && state.RangTurn != turn)
            {
                ring = Claim(state, items, newItems.Count, "ring", now, turn, inline: false);
            }
        }
        else if (request.Carrier == DoorbellCarrier.Tool)
        {
            // Between tool calls only NEW mail rings, once per turn; a re-ring or reminder would only interrupt work in progress.
            // An alarm is not new mail for this carrier: it waits for the session to be idle.
            var toolNew = newItems.Where(static i => !i.Alarm).ToList();
            if (!idle && toolNew.Count > 0)
            {
                var turn = state.LastBusyAt.ToString("R", CultureInfo.InvariantCulture);
                var newest = toolNew.Max(i => i.Entries.Max(e => CreatedMs(e)));
                var oldest = toolNew.Min(i => i.Entries.Min(e => CreatedMs(e)));
                var readyAt = Math.Max(Math.Min(newest + IMailCore.DoorbellQuietMs, oldest + IMailCore.DoorbellMaxWaitMs), state.RangAt + IMailCore.DoorbellMinGapMs);
                if (toolNew.Any(static i => i.Urgent) || (state.RangTurn != turn && now >= readyAt))
                {
                    ring = Claim(state, items, newItems.Count, "ring", now, turn, inline: false);
                }
                else if (state.RangTurn != turn)
                {
                    deadline = readyAt;
                }
            }
        }
        else if (idle && !ringPending && !request.Held)
        {
            if (state.UnreadSince <= 0 && !inlineAcked && items.Any(static i => !i.Alarm))
            {
                state.UnreadSince = now;
            }

            if (newItems.Count > 0)
            {
                var newest = newItems.Max(i => i.Entries.Max(e => CreatedMs(e)));
                var oldest = newItems.Min(i => i.Entries.Min(e => CreatedMs(e)));
                var quietAt = newest + IMailCore.DoorbellQuietMs;
                var maxAt = oldest + IMailCore.DoorbellMaxWaitMs;
                var gapAt = state.RangAt + IMailCore.DoorbellMinGapMs;
                var readyAt = Math.Max(Math.Min(quietAt, maxAt), gapAt);
                if (urgentNew || now >= readyAt)
                {
                    ring = Claim(state, items, newItems.Count, "ring", now, "ring:" + (state.Seq + 1).ToString(CultureInfo.InvariantCulture), inline: items.Count == 1);
                }
                else
                {
                    deadline = readyAt;
                }
            }
            else if (!ringAcked && state.RingId.Length > 0 && !state.Escalated && items.Any(static i => !i.Alarm))
            {
                // Only an alarm is not re-rung or escalated: one wake is one notification, and the mail-unacked toast it would end in
                // reads as the session having been told several times.
                var dueAt = state.RangAt + IMailCore.DoorbellReringMs;
                if (now < dueAt)
                {
                    deadline = dueAt;
                }
                else if (state.Rerings >= IMailCore.DoorbellReringMax)
                {
                    state.Escalated = true;
                    state.RemindAt = now + RemindInterval(0, urgentUnread);
                    escalation = new DoorbellEscalation(
                        "mail-unacked", request.Session, boxes[0].Address,
                        string.Create(CultureInfo.InvariantCulture, $"{boxes[0].Address} has not read its doorbell after {IMailCore.DoorbellReringMax} re-rings; {items.Count} unread."));
                }
                else
                {
                    ring = Claim(state, items, 0, "rering", now, "ring:" + (state.Seq + 1).ToString(CultureInfo.InvariantCulture), inline: false);
                }
            }
            else if ((ringAcked || state.Escalated) && state.RemindStep < RemindersMax)
            {
                if (state.RemindAt <= 0)
                {
                    state.RemindAt = (ringAcked && state.RingAckedAt > 0 ? state.RingAckedAt : now) + RemindInterval(state.RemindStep, urgentUnread);
                }

                if (now >= state.RemindAt)
                {
                    state.RemindStep++;
                    ring = Claim(state, items, 0, "reminder", now, "ring:" + (state.Seq + 1).ToString(CultureInfo.InvariantCulture), inline: false);
                }
                else
                {
                    deadline = state.RemindAt;
                }
            }

            // No paste without a tried ring, and with reminders spent or no ring on record nothing else would try one. A ring that entered needs no wake, so it forces nothing.
            if (ring is null && deadline is null && escalation is null && !ringAcked && !RingEntered(state) && state.UnreadSince > 0 && state.RangAt < state.UnreadSince
                && now - state.UnreadSince >= IMailCore.DoorbellRemindMs && items.Any(static i => !i.Alarm))
            {
                ring = Claim(state, items, 0, "reminder", now, "ring:" + (state.Seq + 1).ToString(CultureInfo.InvariantCulture), inline: false);
            }
        }

        if (state.UnreadSince <= 0)
        {
            state.UncarriedTold = false;
        }
        else if (escalation is null && !state.UncarriedTold && mailbox.ReadNote(UncarriedKind, request.Session) is { } uncarried
            && uncarried.Get("at") is { IsNumber: true } markedAt && markedAt.AsNumber() >= state.UnreadSince)
        {
            state.UncarriedTold = true;
            escalation = new DoorbellEscalation(
                "mail-unacked", request.Session, boxes[0].Address,
                string.Create(CultureInfo.InvariantCulture, $"{boxes[0].Address} could not be woken ({Text(JsonValue.FromObject(uncarried), "reason")}); {items.Count} unread. Tell it to run `ah msg next`."));
        }

        // A session busy for a long unbroken time is not interrupted, but a report or a blocked message waiting behind it is told to the
        // user once, through the same escalation the mod shows for unread mail. An alarm alone never raises it.
        if (escalation is null && !idle && !state.BusyTold && state.BusySince > 0 && now - state.BusySince > BusyReportToldMs
            && items.Any(static i => i.Entries.Any(static e => Text(e, "kind") == "report")))
        {
            state.BusyTold = true;
            escalation = new DoorbellEscalation(
                "mail-unacked", request.Session, boxes[0].Address,
                string.Create(CultureInfo.InvariantCulture, $"{boxes[0].Address} has been busy for {(int)((now - state.BusySince) / 60_000)} min with a report or blocked message waiting; {items.Count} unread."));
        }

        return Finish(request, state, loaded, ring, acked, escalation, none, deadline is { } due && reveal is { } shown ? Math.Min(due, shown) : deadline ?? reveal);
    }

    private const string RingIdPrefix = "doorbell:";

    private const double BusyReportToldMs = 30 * 60 * 1000;

    private const double BusyContinuityMs = 10 * 60 * 1000;

    private DoorbellDecision Finish(DoorbellRequest request, State state, double loaded, DoorbellRing? ring, List<string> acked, DoorbellEscalation? escalation, DoorbellDecision none, double? deadline = null)
    {
        var changed = !JsonText.Stringify(state.ToJson(), 0).Equals(JsonText.Stringify(Load(request.Session).ToJson(), 0), StringComparison.Ordinal);
        if (!changed && ring is null && escalation is null)
        {
            return new DoorbellDecision(null, acked, null, deadline);
        }

        // Another carrier that saved first has claimed the ring; this one rings nothing.
        return Save(request.Session, state, loaded)
            ? new DoorbellDecision(ring, acked, escalation, deadline)
            : none;
    }

    private static string NewestOf(IEnumerable<Item> items) => items.Select(i => i.Newest).Max(StringComparer.Ordinal) ?? string.Empty;

    private static double RemindInterval(int step, bool urgent)
    {
        var first = urgent ? IMailCore.DoorbellRemindUrgentMs : IMailCore.DoorbellRemindMs;
        var cap = urgent ? IMailCore.DoorbellRemindUrgentCapMs : IMailCore.DoorbellRemindCapMs;
        return Math.Min(first * Math.Pow(2, step), cap);
    }

    private static double CreatedMs(JsonValue entry) =>
        Timestamps.TryParseMs(Text(entry, "created"), out var ms) ? ms : 0;

    /// <summary>Marks the ring as claimed in <paramref name="state"/> and returns it; the caller saves the state to make the claim stand.</summary>
    private DoorbellRing Claim(State state, List<Item> items, int newCount, string kind, double now, string turn, bool inline)
    {
        var id = RingIdPrefix + (state.Seq + 1).ToString(CultureInfo.InvariantCulture);
        state.RingId = id;
        state.RangAt = now;
        state.RangTurn = turn;
        state.ToldMark = NewestOf(items);
        state.RingAckedAt = 0;
        state.InlineId = string.Empty;
        if (kind == "rering")
        {
            state.Rerings++;
        }
        else
        {
            state.Rerings = 0;
            state.Escalated = false;
        }

        state.RemindAt = 0;
        if (kind == "ring" && newCount > 0)
        {
            state.RemindStep = 0;
        }

        var urgent = items.Any(i => i.Urgent);
        if (inline && items.Count == 1 && items[0].Entries.Count == 1)
        {
            var entry = items[0].Entries[0];
            var groups = new JsonArray { new JsonObject { ["address"] = items[0].Address, ["entries"] = new JsonArray { entry } } };
            state.InlineId = Text(entry, "id");
            return new DoorbellRing(id, core.BuildPrompt(groups), [state.InlineId], urgent, true, kind);
        }

        return new DoorbellRing(id, Line(items, newCount, kind), [], urgent, false, kind);
    }

    // ---------------------------------------------------------------- ah msg next

    private NextResult PullLocked(string session, double now, bool peek, bool again, string? team)
    {
        if (again)
        {
            var kept = Load(session).Recent;
            return new NextResult(string.Join("\n\n", kept), 0, 0, kept.Count == 0);
        }

        // The read is recorded before the item is acked, so a crash between the two leaves the item both in `recent` and still
        // unread, never acked and lost. The record is a compare-and-set; of two readers only the one that wins it acks.
        for (var attempt = 0; attempt < PullAttempts; attempt++)
        {
            var state = Load(session);
            var loaded = state.Seq;
            var oldReadMark = state.ReadMark;
            var items = Queue(session, now, team);
            AfterQueue?.Invoke();
            state.RingAckedAt = now;
            state.UnreadSince = 0;
            if (peek)
            {
                _ = Save(session, state, loaded);
                return new NextResult(string.Join("\n", items.Select(PeekLine(now))), items.Count, 0, items.Count == 0);
            }

            if (items.Count == 0)
            {
                _ = Save(session, state, loaded);
                return new NextResult(string.Empty, 0, 0, true);
            }

            var taken = items[0];
            var alsoEvents = items.Skip(1).Where(static i => i.Event is not null && i.From == DaemonSender).ToList();
            var remaining = items.Skip(1).Except(alsoEvents).ToList();
            var newSince = remaining.Count(i => string.CompareOrdinal(i.Newest, oldReadMark) > 0);
            var newest = NewestOf(items);
            state.ReadMark = newest;
            if (string.CompareOrdinal(newest, state.ToldMark) > 0)
            {
                state.ToldMark = newest;
            }

            state.RemindStep = 0;
            state.RemindAt = 0;
            var body = string.Join("\n\n", new[] { taken }.Concat(alsoEvents).Select(i => core.QuotedForHook(i.Ids, Render(i))));
            state.Recent.Add(body);
            if (state.Recent.Count > RecentKept)
            {
                state.Recent.RemoveRange(0, state.Recent.Count - RecentKept);
            }

            if (!Save(session, state, loaded))
            {
                continue;
            }

            BetweenSaveAndAck?.Invoke();
            foreach (var read in new[] { taken }.Concat(alsoEvents))
            {
                _ = mailbox.AckIds(read.Dir, read.Ids, "next", session);
            }

            var footer = string.Create(CultureInfo.InvariantCulture, $"queue: {remaining.Count} left · {newSince} new since your last read");
            return new NextResult(body + "\n" + footer, remaining.Count, newSince, false);
        }

        return new NextResult(RecordFailedText, 0, 0, false);
    }

    /// <summary>The sender of the daemon's events. A pull prints and acks every pending one of them with the item it takes, so a session that reads its mail never leaves an event it was shown unacknowledged.</summary>
    private const string DaemonSender = "ahd";

    private const string RecordFailedText ="ah: could not record the read because the mail state kept changing; nothing was marked read. Run `ah msg next` again.";

    private const int PullAttempts = 5;

    // Seams for tests that interleave another writer or a crash with a pull.
    internal Action? AfterQueue { get; set; }

    internal Action? BetweenSaveAndAck { get; set; }

    private string Render(Item item)
    {
        if (item.Entries.Count == 1 && item.Event is null)
        {
            var groups = new JsonArray { new JsonObject { ["address"] = item.Address, ["entries"] = new JsonArray { item.Entries[0] } } };
            return core.BuildPrompt(groups);
        }

        var sb = new StringBuilder();
        var created = item.Entries.Select(e => Text(e, "created")).Where(c => c.Length > 0).Order(StringComparer.Ordinal).ToList();
        sb.Append(CultureInfo.InvariantCulture, $"{item.Event ?? "mail"}: {item.Entries.Count} entries");
        if (created.Count > 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $" (oldest {created[0]}, newest {created[^1]})");
        }

        foreach (var entry in item.Entries.Take(GroupLines))
        {
            sb.Append('\n').Append("- ").Append(core.OneLine(entry.Member("text")));
        }

        if (item.Entries.Count > GroupLines)
        {
            sb.Append(CultureInfo.InvariantCulture, $"\n- … and {item.Entries.Count - GroupLines} more");
        }

        return sb.ToString();
    }

    private Func<Item, string> PeekLine(double now) => item =>
    {
        var text = item.Entries.Count > 1 ? $"{item.Event} x{item.Entries.Count}" : core.OneLine(item.Entries[0].Member("text"));
        if (text.Length > PeekChars)
        {
            text = text[..PeekChars];
        }

        var age = Timestamps.TryParseMs(item.Created, out var ms) ? Math.Max(0, (long)((now - ms) / 1000)) : 0;
        var cls = item.Rank switch { 1 => "question", 2 => "blocked", 3 => "report", DigestRank => "digest", _ => "alarm" };
        return string.Create(CultureInfo.InvariantCulture, $"{item.Rank} {cls} from {item.From} {age}s ago: {text}");
    };
}
