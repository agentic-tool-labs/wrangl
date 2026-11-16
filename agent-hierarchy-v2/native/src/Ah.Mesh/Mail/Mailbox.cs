using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Core.Time;
using Ah.Hierarchy;
using Ah.Roster;

namespace Ah.Mesh;

/// <summary>A mailbox address split into its member name and team name.</summary>
public sealed record AddressParts(string Name, string Team);

/// <summary>The outcome of <see cref="IMailbox.AckIds"/>: the ids acked (new or already), the ids that name no entry, and the ids this call acked first.</summary>
public sealed record AckIdsResult(IReadOnlyList<string> Acked, IReadOnlyList<string> Unknown, IReadOnlyList<string> Fresh);

/// <summary>What <see cref="IMailbox.AckPaste"/> did: the entry was already acked, the paste acked it, or the ack could not be written.</summary>
public enum PasteAck
{
    Already,
    Paste,
    Failed,
}

/// <summary>An entry a paste carried: its id and the box directory it sits in (null for a paste with no mailbox entry).</summary>
public sealed record PasteEntry(string Id, string? Box);

/// <summary>
/// A session's registration. <paramref name="Boxes"/> is primary first. <paramref name="Harness"/> defaults to <c>claude</c>;
/// <paramref name="Pid"/> is null for a session recorded with no process. <paramref name="PidStart"/> is the start time of that
/// process as <c>ps -o lstart=</c> printed it when the registration was written; a later holder of a reused pid has another one.
/// <paramref name="Bound"/> is true when <see cref="IRegistrationBinder"/> found <paramref name="Pid"/> to be the registrant or one of
/// its ancestors; only a consumer that acts on which process a session is reads it, and mail routing never does.
/// <paramref name="AhVersion"/> is the build that registered the session; a registration without one was written by a build that did not record it.
/// <paramref name="HerdrFlavour"/> is the herdr server the session's pane lives on (<c>tui</c> or <c>core</c>); null keeps the stored one.
/// <paramref name="TeamsStamp"/> is <see cref="Mailbox.TeamsStamp"/> as it was before the boxes were resolved; only the prompt self-heal writes it.
/// </summary>
public sealed record RegistrationWrite(
    string SessionId,
    IReadOnlyList<BoxRef> Boxes,
    string? TeamFile = null,
    string? Harness = "claude",
    long? Pid = null,
    string? PaneId = null,
    string? Cwd = null,
    string? PidStart = null,
    bool Bound = false,
    IReadOnlyDictionary<string, string>? TeamFiles = null,
    string? AhVersion = null,
    string? HerdrFlavour = null,
    string? TeamsStamp = null);

/// <summary>What a guarded registration did.</summary>
public enum RegisterOutcome
{
    /// <summary>The registration was written, or already held exactly this.</summary>
    Written,

    /// <summary>The session id or boxes cannot be recorded; nothing was written.</summary>
    Invalid,

    /// <summary>The session's registration names a live process other than the claimed one; nothing was written.</summary>
    HeldByAnotherProcess,

    /// <summary>The session already holds a bound registration and this claim is not bound; nothing was written.</summary>
    BoundExists,
}

/// <summary>
/// What a guarded registration found out, before it takes the lock, about the process the held registration names:
/// <paramref name="Pid"/>, whether it is <paramref name="Alive"/>, and its start time <paramref name="Start"/> now as the system
/// <c>ps</c> printed it (null: not asked). A property of the process, not of the file, so it is used only while that pid is still
/// the one the lock finds held; any other held pid is judged by the mailbox's own process inspector.
/// </summary>
public sealed record RegistrationHeld(long Pid, bool Alive, string? Start = null);

/// <summary>
/// The mailbox's registrations, acks, heartbeats and routing rows over <c>AH_HOME</c>. Reads are in-process; every write is
/// an op of the <c>mailbox</c> state domain run through the state writer, so a box write takes the box's <c>append.lock</c>.
/// </summary>
public interface IMailbox
{
    /// <summary>The mailbox directory of member <paramref name="name"/> of the team whose file is <paramref name="teamFile"/>.</summary>
    string BoxDirFor(string teamFile, string name);

    /// <summary><c>name@team</c> split in two, or null when <paramref name="address"/> is not of that shape.</summary>
    AddressParts? SplitAddress(string? address);

    /// <summary>Every mailbox on disk, the address read off the directory names.</summary>
    IReadOnlyList<BoxRef> ListBoxes();

    /// <summary>Drops the held box list, so the next <see cref="ListBoxes"/> reads the store (the on-demand rebuild, b459 step 11).</summary>
    void ForgetHeldBoxes()
    {
    }

    /// <summary>The file a session's registration is kept in, whether or not it exists.</summary>
    string RegistrationFile(string sessionId);

    /// <summary>The session's registration, or null when there is none, its id is not a valid name, or it names no address.</summary>
    JsonObject? ReadRegistration(string? sessionId);

    /// <summary>A counter that moves whenever a stored registration changes, whichever process changed it; null while files hold them.</summary>
    long? SessionsChangeToken() => null;

    /// <summary>A counter that moves on every mail write (an entry, an ack, a box), whichever process made it; null while files hold the boxes.</summary>
    long? MailChangeToken() => null;

    /// <summary>The change marker of the box at <paramref name="dir"/>, whichever process changed it; null while files hold the mail.</summary>
    string? StoredBoxStamp(string dir) => null;

    /// <summary>The key of the newest entry of the box at <paramref name="dir"/>; null while files hold the mail.</summary>
    long? StoredNewestEntry(string dir) => null;

    /// <summary>Whether the boxes are in the database. One mode check, where <see cref="StoredBoxStamp"/> reads a box.</summary>
    bool BoxesAreStored => false;

    /// <summary>The stored entries of the box at <paramref name="dir"/> after the cursor <paramref name="from"/>; null while files hold the mail.</summary>
    IReadOnlyList<ScannedEntry>? StoredRows(string dir, long from) => null;

    /// <summary>The ids of the stored live registrations, in order; null while files hold them.</summary>
    IReadOnlyList<string>? StoredSessionIds() => null;

    /// <summary>The stored live registrations as id to a hash of the stored document, in order; null while files hold them.</summary>
    IReadOnlyDictionary<string, string>? StoredSessionStamps() => null;

    /// <summary>When the stored registration of the session was written, in Unix ms; null while files hold them, or when it has none.</summary>
    double? RegistrationWrittenMs(string sessionId) => null;

    /// <summary>A registration's boxes, primary first.</summary>
    IReadOnlyList<BoxRef> RegistrationBoxes(JsonValue? registration);

    /// <summary>
    /// The team files a registration was made under: the values of <c>team_files</c> when present, else <c>[team_file]</c>
    /// (a record that predates <c>team_files</c>, or one made through <c>POST /v1/register</c>), else empty. Distinct, in order.
    /// </summary>
    IReadOnlyList<string> RegistrationTeamFiles(JsonValue? registration);

    /// <summary>Whether a registration's session is alive: its process is, or (a custom harness with no pid) its heartbeat is fresh.</summary>
    bool RegistrationLive(JsonObject registration);

    /// <summary>Every registration on disk, live or not, in file-name order.</summary>
    IReadOnlyList<JsonObject> AllRegistrations();

    /// <summary>
    /// <see cref="AllRegistrations"/> and the ended registrations not yet pruned: liveness evidence only, so a member whose dead
    /// session was ended is still judged by the process it names.
    /// </summary>
    IReadOnlyList<JsonObject> EvidenceRegistrations() => AllRegistrations();

    /// <summary>Registrations whose session is alive.</summary>
    IReadOnlyList<JsonObject> LiveRegistrations();

    /// <summary>
    /// The principal of the process <paramref name="callerPid"/> (a route's peer pid, the CLI's own pid) by the one caller-identity
    /// predicate over the live registrations. An <c>orchestrator@T</c> address counts only while T's team file records the
    /// session's process (pid and start time). Anonymous when this mailbox has no process table.
    /// </summary>
    CallerPrincipal PrincipalOf(int? callerPid) => CallerPrincipal.Anonymous;

    /// <summary>Records a session under its boxes. Returns false, writing nothing, for a session id or boxes it cannot record.</summary>
    bool WriteRegistration(RegistrationWrite registration);

    /// <summary>
    /// Records a session like <see cref="WriteRegistration"/> unless it would displace a registration it must not. Under one lock
    /// the held registration is read and the claim is decided and written: a claim that is not bound never replaces a bound
    /// registration whose process is live, a bound claim replaces a held one that is not bound, and any other claim naming a
    /// different pid (or none) than a live held registration does not replace it.
    /// </summary>
    RegisterOutcome RegisterGuarded(RegistrationWrite registration, RegistrationHeld? held = null);

    /// <summary>Deletes a session's registration; true when there was one.</summary>
    bool RemoveRegistration(string? sessionId);

    /// <summary>Whether entry <paramref name="id"/> is acked in box <paramref name="dir"/>.</summary>
    bool IsAcked(string dir, string id);

    /// <summary>Whether an entry in a member's box waits for that member's next turn in the digest, and so wakes nobody.</summary>
    bool IsMemberDigestNote(JsonValue entry, string boxAddress) => false;

    /// <summary>
    /// <see cref="IsAcked"/> for box <paramref name="dir"/>, answered from one listing of the box's acks taken now instead of one
    /// lookup per id. An ack written after the listing is not seen by it.
    /// </summary>
    Func<string, bool> AckedIn(string dir);

    /// <summary>The ack record of <paramref name="id"/>, or null.</summary>
    JsonValue? ReadAck(string dir, string id);

    /// <summary>Acks <paramref name="ids"/> in the box. An id already acked is success; an id that names no entry is unknown.</summary>
    AckIdsResult AckIds(string dir, IReadOnlyList<string> ids, string by, string? sessionId = null, string? reason = null);

    /// <summary>Acks the entry a confirmed paste carried, <c>by: "paste"</c>. An entry already acked by anything stands.</summary>
    PasteAck AckPaste(PasteEntry? entry);

    /// <summary>The claim record of entry <paramref name="id"/> (<c>state</c>, <c>carrier</c>, <c>holder</c>, the leases), or null when it has none.</summary>
    JsonObject? ReadClaim(string dir, string id);

    /// <summary>
    /// Applies <paramref name="action"/> (<see cref="ClaimActions"/>) to the entry's claim record under the box's append lock: the
    /// record is read, the transition checked and written in one hold, so of two callers exactly one wins and the other is told
    /// the state it found. <paramref name="args"/> carries <c>session_id</c>, <c>holder</c>, <c>reason</c> and <c>actor</c>.
    /// </summary>
    ClaimResult Claim(string dir, string id, string action, JsonObject? args = null);

    /// <summary>
    /// The daemon's step for one entry with a claim record: a relay whose receiver session is gone is released, then every
    /// transition the leases and a failed relay make due is applied. The result carries the record as it stands.
    /// </summary>
    ClaimResult AdvanceClaim(string dir, string id, bool? receiverIdle = null);

    /// <summary>
    /// The entries of the box whose relay was sent and has not been acked for at least <paramref name="minAgeMs"/>: shown to nobody
    /// yet, usually because the receiver sits at a permission prompt.
    /// </summary>
    IReadOnlyList<HeldRelay> HeldRelays(string dir, double nowMs, double minAgeMs);

    /// <summary>The start time of process <paramref name="pid"/> in UTC as the system printed it, or null when it cannot be read.</summary>
    string? ProcessStart(long pid);

    /// <summary>How <paramref name="recorded"/> stands against the process now holding <paramref name="pid"/>, in either recorded form.</summary>
    StartMatch ProcessStartMatches(long pid, string? recorded);

    /// <summary>
    /// The start time to store for <paramref name="pid"/> when a registration holds <paramref name="recorded"/>: the process's UTC
    /// start time when <paramref name="recorded"/> is that process's, otherwise <paramref name="recorded"/> unchanged.
    /// </summary>
    string? StartToRecord(long pid, string? recorded);

    /// <summary>
    /// Writes a paste attempt's row: to the entry's box, or to <c>AH_HOME/run/paste/attempts.jsonl</c> when there is no entry or box.
    /// A failed write is swallowed.
    /// </summary>
    void LogPasteAttempt(PasteEntry? entry, JsonObject row);

    /// <summary>Appends one routing row to the box's <c>attempts.jsonl</c>; throws when the write fails.</summary>
    void AppendAttempt(string dir, JsonObject row);

    /// <summary>Every routing row of the box, oldest first; a torn or unreadable line is skipped.</summary>
    IReadOnlyList<JsonObject> ReadAttempts(string dir);

    /// <summary>
    /// The routing rows that can belong to entry <paramref name="id"/>, oldest first: with the store and a text id, that id's rows by index;
    /// otherwise every row of the box. The caller still filters by id.
    /// </summary>
    IReadOnlyList<JsonObject> AttemptsOf(string dir, JsonValue? id);

    /// <summary>Whether a routing row is a ladder step: it carries <c>ladder: true</c>; routing, paste and rung-0 rows do not.</summary>
    bool IsLadderRow(JsonValue row);

    /// <summary>The ladder rows of entry <paramref name="id"/>, oldest first.</summary>
    IReadOnlyList<JsonObject> LadderRowsOf(string dir, string id);

    /// <summary>The ladder rows of a box grouped by entry, in the order each entry first appears, rows oldest first.</summary>
    IReadOnlyList<LadderEntryRows> LadderRowsByEntry(string dir);

    /// <summary>
    /// <see cref="LadderRowsByEntry"/> split on each entry's newest ladder state: the rows of the entries whose ladder is still open, and the
    /// newest state of the ones <see cref="DrainedState.Settled"/> names. With the store, kept until a mail write moves the token.
    /// </summary>
    LadderSweepRows LadderSweep(string dir);

    /// <summary>
    /// <paramref name="entry"/> unchanged, unless it is a <c>relay-needed</c> event from <c>ahd</c> whose entry no longer needs a relay
    /// (acked, delivered, superseded, declined or held by a relay): then a copy carrying a <c>void</c> marker the prompt builders print as
    /// the "no longer needed" line.
    /// </summary>
    JsonValue MarkRelayVoid(JsonValue entry);

    /// <summary>
    /// Ends the relay of entry <paramref name="id"/> of the box <paramref name="dir"/> with a terminal <c>declined</c> ladder row, for one of
    /// <c>already-reported</c>, <c>superseded</c> or <c>withdrawn</c>. Only the carrier named on the entry's rung-3 row may; the entry is not acked.
    /// </summary>
    MailboxOutcome<JsonObject> Decline(string dir, string id, string reason, string? note, string? sessionId, IReadOnlyList<string> callerAddresses);

    /// <summary>
    /// What withdrawing brief <paramref name="id"/> as <paramref name="from"/> would act on, with nothing written: <c>{entry, dir, address, from,
    /// to, ref, role, progress, acked_by, acked_at, response}</c>. Refused <c>unknown</c>, <c>not_sender</c>, <c>bad_request</c> (not a brief),
    /// <c>already_closed</c> (withdrawn, superseded or declined) or <c>already_reported</c>.
    /// </summary>
    MailboxOutcome<JsonObject> WithdrawPlan(string id, string from);

    /// <summary>
    /// Ends entry <paramref name="id"/> of the box <paramref name="dir"/> with a terminal <c>withdrawn</c> ladder row by <paramref name="by"/>,
    /// naming <paramref name="readdressedTo"/> (the entry id of the re-address) when there is one. Allowed after an ack; the entry is not edited.
    /// </summary>
    MailboxOutcome<JsonObject> Withdraw(string dir, string id, string by, string reason, string? readdressedTo);

    /// <summary>
    /// Withdraws, as <c>ahd</c> with <paramref name="reason"/>, every unacked member resume entry (id prefix <c>rewake-</c>) of the box at
    /// <paramref name="dir"/>: only those to <paramref name="session"/> when given, never <paramref name="keepId"/>, and only those created
    /// before <paramref name="createdBeforeMs"/> when given. An entry already closed is skipped. Returns how many were withdrawn.
    /// </summary>
    int WithdrawRewakes(string dir, string? session, string? keepId, double? createdBeforeMs, string reason);

    /// <summary>
    /// The file names of the request files with a withdrawn brief and no live one (a brief is live while its last ladder state is not
    /// withdrawn, superseded or declined). Reads every box.
    /// </summary>
    IReadOnlySet<string> WithdrawnRequestFiles();

    /// <summary>Why entry <paramref name="entry"/> of the box <paramref name="dir"/> needs no relay (<c>acked &lt;at&gt; &lt;by&gt;</c>, <c>delivered</c>, <c>superseded &lt;N&gt;</c>, <c>declined &lt;reason&gt;</c>, <c>claimed &lt;state&gt;</c>), or null when it is still owed.</summary>
    string? RelayVoidNote(string dir, string entry);

    /// <summary>
    /// Where entry <paramref name="id"/> stands, as <c>{entry, kind, from, to, state, delivered, acked, superseded_by, declined,
    /// relay_needed, relay_reason}</c>; nothing is written. Null when no box holds the entry, or when none of the boxes
    /// <paramref name="callers"/> are registered as, the entry's sender among them, or of a team they orchestrate, does.
    /// </summary>
    JsonObject? EntryStatus(string id, IReadOnlyList<CallerBox> callers);

    /// <summary>False when a file that holds the ladder rows, the entries or the ack of entry <paramref name="id"/> in <paramref name="dir"/> exists and cannot be read; the readers answer empty for such a file.</summary>
    bool RecordsReadable(string dir, string id);

    /// <summary>
    /// Appends the ladder row that is step <paramref name="seq"/> of entry <c>row.id</c> (1 + the ladder rows it has), under the box
    /// lock <c>send</c> uses. Returns false, writing nothing, when that step is already there: two evaluations that derived the same
    /// step write it once. A box that is locked by another writer is a failure.
    /// </summary>
    bool AppendLadderRow(string dir, JsonObject row, long seq);

    /// <summary>True when <paramref name="path"/> is a named team file (<c>teams/&lt;name&gt;.json</c>), not the legacy default <c>team.json</c>.</summary>
    bool IsNamedTeamPath(string path);

    /// <summary>The team a team file names: its basename without <c>.json</c>.</summary>
    string TeamNameOf(string path);

    /// <summary>The ladder line a <c>sendmessage</c> relay of <see cref="DescribeSend"/> prints: <c>delivery: fallback — SendMessage "&lt;text&gt;" to &lt;to&gt;</c>.</summary>
    string SendMessageLine(JsonValue sendmessage);

    /// <summary>A fresh entry id: the time of minting and a random suffix.</summary>
    string NewId();

    /// <summary>The ladder tag <see cref="RenderInbox"/> appends to an entry's line: <c>(rung 4 pending attempt 2/4 next &lt;iso&gt;)</c>.</summary>
    string LadderTag(JsonObject ladder);

    /// <summary>
    /// How a stored entry reaches its recipient, as <c>send</c> reports it: <c>{entry, address, delivery, adapter?}</c> for a pull, else
    /// the <c>deliver</c> hint of a member that runs a harness, nothing for a custom harness, or the <c>sendmessage</c> relay to carry.
    /// </summary>
    JsonObject DescribeSend(JsonObject entry, ResolvedAddress recipient, JsonObject? attempt, JsonObject? heartbeat, string socketDirectory);

    /// <summary>
    /// Drains every box of the team whose file is <paramref name="teamFile"/>: each entry that is neither acked nor at a terminal ladder
    /// state gets a <c>drained</c> row. Returns <c>{entries, rows:[{id, box, from, kind, created, summary}]}</c>.
    /// </summary>
    JsonObject DrainTeam(string teamFile, string team, EffectiveHold hold);

    /// <summary>The entries of the team's boxes whose ladder ended in a <c>drained</c> row written by <paramref name="hold"/>, in the shape <see cref="DrainTeam"/> reports.</summary>
    JsonObject DrainedBy(string teamFile, string team, EffectiveHold hold);

    /// <summary>
    /// Queues again the mail <paramref name="hold"/> drained from the team's boxes, once each, leaving out events (they describe a moment that has passed)
    /// and a quit's own briefs. Returns the shape of <see cref="DrainTeam"/>, listing what was queued.
    /// </summary>
    JsonObject RedeliverDrained(string teamFile, string team, EffectiveHold hold);

    /// <summary>
    /// The heartbeat of the box. With <paramref name="sessionId"/>, only that session's; without, the newest fresh heartbeat of any
    /// session, else the newest stale one. <paramref name="nowMs"/> defaults to the clock.
    /// </summary>
    JsonObject? ReadHeartbeat(string dir, string? sessionId = null, double? nowMs = null);

    /// <summary>How old <paramref name="sessionId"/>'s heartbeat in <paramref name="dir"/> is, in ms, when it was written with <paramref name="pollMs"/>; else null.</summary>
    double? HeartbeatAgeMs(string dir, string sessionId, double pollMs) => null;

    /// <summary>Fresh means no older than three poll intervals.</summary>
    bool HeartbeatFresh(JsonValue? heartbeat, double? nowMs = null);

    /// <summary>Whether the pull heartbeat of <paramref name="sessionId"/> in <paramref name="boxDir"/> is fresh at <paramref name="nowMs"/>. The one implementation every caller uses.</summary>
    bool PullFresh(string boxDir, string sessionId, double nowMs);

    /// <summary>Whether the session's pull is fresh in any of its registered <c>orchestrator@</c> boxes.</summary>
    bool OrchestratorPullFresh(string sessionId, double nowMs);

    /// <summary>
    /// Whether the doorbell carries the mail of the box: it is an <c>orchestrator@</c> box and a Claude mod session the doorbell serves
    /// (<paramref name="toSession"/> when the entry names one) polled it within the pull timeout.
    /// </summary>
    bool DoorbellLive(string boxDir, string address, string? toSession, double nowMs);

    /// <summary>The session of a live doorbell on this box that has left its mail unread past the limit while idle, or null: its poll runs, but no ring is reaching it.</summary>
    string? DoorbellGoneUnread(string boxDir, string address, string? toSession, double nowMs);

    /// <summary>
    /// The chief's one tally line for each team whose live deputy is on duty while it holds <c>chief@T</c>, only when it changed since
    /// the last one it was told; null otherwise. Writes the told line, so a second call with nothing new returns null.
    /// </summary>
    string? DeputyTally(string session) => null;

    /// <summary>Records that the session could not be woken for its unread mail: its doorbell tells the person once, in its next answer.</summary>
    void MarkDoorbellUncarried(string session, double nowMs, string reason);

    /// <summary>Whether a live registered session is the kind the doorbell serves, whether or not its mod still polls.</summary>
    bool DoorbellSessionLive(string session);

    /// <summary>When the session's mod last polled this box, in ms; 0 if it never has.</summary>
    double DoorbellLastPoll(string boxDir, string session);

    /// <summary>The byte offset before which every entry of the box is acked.</summary>
    long AckedThrough(string dir);

    /// <summary>Refreshes a recipient's adapter heartbeat on the adapter's behalf.</summary>
    void WriteHeartbeat(string dir, string adapter, string? sessionId, double pollMs);

    /// <summary>Every named team file visible from the request: its hierarchy directories' teams, then each live registration's team file.</summary>
    IReadOnlyList<VisibleTeam> VisibleTeams(RequestContext context);

    /// <summary>The team lookup the send rule takes: a team name to its record and the hierarchy directory its file lives in.</summary>
    Func<string, MeshTeam?> TeamLookup(RequestContext context);

    /// <summary>Every member named <paramref name="name"/> on a visible team.</summary>
    IReadOnlyList<MemberMatch> MembersNamed(RequestContext context, string name);

    /// <summary><c>name@team</c> (or a bare name defined by exactly one live team) to its team and box.</summary>
    MailboxOutcome<ResolvedAddress> ResolveAddress(string? address, RequestContext context);

    /// <summary>Parsed entries from byte <paramref name="from"/>, oldest first, and the cursor after the last complete line; a torn line is skipped.</summary>
    EntriesResult ReadEntries(string dir, long from = 0);

    /// <summary>The unacked entries of a box, oldest first (see <see cref="InboxOptions"/>).</summary>
    EntriesResult Inbox(string dir, InboxOptions? options = null);

    /// <summary>The unacked entries of a box, oldest first.</summary>
    IReadOnlyList<JsonValue> Pending(string dir);

    /// <summary>The one prompt that hands <paramref name="entries"/> of <paramref name="address"/> to a model.</summary>
    string BuildPrompt(string address, IReadOnlyList<JsonValue> entries);

    /// <summary><c>inbox</c> as text, one line per entry.</summary>
    string RenderInbox(IReadOnlyList<JsonValue> entries);

    /// <summary>
    /// Builds, routes and appends one entry. <paramref name="request"/> is <c>send</c>'s request object
    /// (<c>to</c>, <c>from</c>, <c>session_id</c>, the context fields, <c>id</c>, <c>kind</c>, <c>ref</c>, <c>text</c>, <c>event</c>, ...);
    /// the result is the <c>send --json</c> object. Nothing is written when the request is refused.
    /// </summary>
    MailboxOutcome<JsonObject> Send(JsonObject request, DateTimeOffset? now = null);

    /// <summary>Appends <paramref name="entry"/> to the box, idempotent on its id; the result is <c>{entry, existed}</c>.</summary>
    MailboxOutcome<JsonObject> AppendEntry(string dir, JsonObject entry, JsonObject? attempt = null);

    /// <summary>The address a send is from: the caller's only address, the one in the recipient's team, or the explicit one.</summary>
    MailboxOutcome<string> ChooseFrom(IReadOnlyList<string> callerAddresses, string? toTeam, string? explicitFrom = null);

    /// <summary>The text a SendMessage relay carries for <paramref name="entry"/> in the box of <paramref name="address"/>.</summary>
    string RelayLine(JsonValue entry, string address);

    /// <summary>The length of the leading <c>ah mail from … to …: </c> header of <see cref="RelayLine"/>.</summary>
    int RelayHeaderLength(JsonValue entry, string address);

    /// <summary>A send's result as the lines <c>ah msg send</c> prints.</summary>
    string RenderSend(JsonObject result);

    /// <summary>Whether <paramref name="sessionId"/> is a live Claude sender: harness claude whose registered pid is alive.</summary>
    bool LiveClaudeSender(string? sessionId);

    /// <summary>Records that a <c>star-warn</c> send would have been refused; evidence only.</summary>
    void LogWouldRefuse(string dir, string id, string? would);

    /// <summary>Records that a SendMessage named a recipient nothing could place; evidence only.</summary>
    void LogUnclassified(string dir, string id, JsonObject detail);

    /// <summary>The <c>mailbox</c> state domain: register, unregister, append, attempt, heartbeat, ack.</summary>
    IStateDomain StateDomain();
}

internal sealed partial class Mailbox(
    IFileReader files,
    IAhEnvironment environment,
    IClock clock,
    IRandom random,
    HierarchyServices hierarchy,
    IRosterCore roster,
    IStateWriter state,
    IProcessTable? processes = null,
    Func<bool>? claimsServed = null,
    ISessionAuthority? sessions = null,
    IMailStore? mail = null,
    IHomeDocs? homeDocs = null) : IMailbox
{
    private readonly MailCore _core = new(clock);

    /// <summary>The mail tables, once the database holds the mail; null while files do.</summary>
    private IMailStore? Stored => mail is { IsDatabase: true } store ? store : null;

    /// <summary>The key of the box at <paramref name="dir"/> in the mail tables: its path under the mailbox root.</summary>
    internal string BoxKey(string dir)
    {
        var root = NodePath.Join(AhHome, "mailbox") + "/";
        return dir.StartsWith(root, StringComparison.Ordinal) ? dir[root.Length..] : dir;
    }

    /// <summary>The parsed record a stored row holds, null when it is not a JSON object.</summary>
    private static JsonObject? StoredMap(ReadOnlyMemory<byte>? raw) => raw is { } bytes ? AsMap(ParseOrNull(Utf8Text.Decode(bytes.Span))) : null;

    public string? StoredBoxStamp(string dir) => Stored?.BoxStamp(BoxKey(dir));

    public long? StoredNewestEntry(string dir) => Stored?.NewestEntry(BoxKey(dir));

    public bool BoxesAreStored => Stored is not null;

    public long? MailChangeToken() => Stored is { } store ? store.ChangeToken() : null;

    public IReadOnlyList<ScannedEntry>? StoredRows(string dir, long from) => Stored is null ? null : ScanEntries(dir, from).Rows;

    /// <summary>What <paramref name="build"/> reads from the mail tables, kept until a mail write moves the token.</summary>
    private T CachedByToken<T>(Dictionary<string, (long Token, T Value)> cache, string key, IMailStore store, Func<T> build)
    {
        // the token is read before the rows: a write between the two is read again next time, never missed
        var token = store.ChangeToken();
        lock (_jsonCacheGate)
        {
            if (cache.TryGetValue(key, out var held) && held.Token == token)
            {
                return held.Value;
            }
        }

        var value = build();
        lock (_jsonCacheGate)
        {
            cache[key] = (token, value);
        }

        return value;
    }

    private ScanResult ScanStored(IMailStore store, string dir, long from)
    {
        var rows = new List<ScannedEntry>();
        var next = from;
        foreach (var row in store.Entries(BoxKey(dir), from))
        {
            next = row.Key;
            AddParsed(rows, row);
        }

        return new ScanResult(rows, next);
    }

    private static void AddParsed(List<ScannedEntry> rows, KeyValuePair<long, ReadOnlyMemory<byte>> row)
    {
        if (ParseOrNull(Utf8Text.Decode(row.Value.Span)) is { IsMap: true } entry && entry.AsObject().Get("id") is { IsText: true })
        {
            rows.Add(new ScannedEntry(entry, row.Key));
        }
    }

    /// <summary>A box's held scan: the parsed rows up to <c>Scan.Next</c> and how many stored rows that read covered.</summary>
    private sealed class HeldScan
    {
        internal ScanResult? Scan { get; set; }

        internal long Stored { get; set; }

        /// <summary>The id of the row at <c>Scan.Next</c>, null when none.</summary>
        internal string? LastId { get; set; }

        internal string? AckStamp { get; set; }

        internal IReadOnlySet<string> Acked { get; set; } = new HashSet<string>(StringComparer.Ordinal);
    }

    private readonly Dictionary<string, HeldScan> _heldScans = new(StringComparer.Ordinal);

    private HeldScan HeldOf(string box)
    {
        lock (_jsonCacheGate)
        {
            if (!_heldScans.TryGetValue(box, out var held))
            {
                held = new HeldScan();
                _heldScans[box] = held;
            }

            return held;
        }
    }

    /// <summary>
    /// The box's whole scan, held (b459 step 15): each read parses only the rows after the held cursor, from the same read that counts
    /// the box. When the held rows plus the new ones are not the count, a row was deleted (a prune) and the box is read again in full;
    /// so too when the row at the held cursor is not the one held there (b500: its pk was deleted and reused).
    /// </summary>
    private ScanResult HeldStoredScan(IMailStore store, string dir)
    {
        var box = BoxKey(dir);
        var held = HeldOf(box);
        lock (held)
        {
            var from = held.Scan?.Next ?? 0;
            var (count, cursorId, lastId, fresh) = store.CountedEntries(box, from);
            if (held.Scan is { } scan && held.Stored + fresh.Count == count && string.Equals(held.LastId, cursorId, StringComparison.Ordinal))
            {
                if (fresh.Count == 0)
                {
                    return scan;
                }

                var grown = new List<ScannedEntry>(scan.Rows.Count + fresh.Count);
                grown.AddRange(scan.Rows);
                foreach (var row in fresh)
                {
                    AddParsed(grown, row);
                }

                held.Scan = new ScanResult(grown, fresh[^1].Key);
                held.Stored = count;
                held.LastId = lastId;
                return held.Scan;
            }

            (count, _, lastId, fresh) = from == 0 ? (count, cursorId, lastId, fresh) : store.CountedEntries(box, 0);
            var rows = new List<ScannedEntry>(fresh.Count);
            foreach (var row in fresh)
            {
                AddParsed(rows, row);
            }

            held.Scan = new ScanResult(rows, fresh.Count > 0 ? fresh[^1].Key : 0);
            held.Stored = fresh.Count;
            held.LastId = lastId;
            return held.Scan;
        }
    }

    /// <summary>The box's acked ids, held while its ack count reads the same (spec Amendment 1); acks are only inserted.</summary>
    private IReadOnlySet<string> HeldAcked(IMailStore store, string dir)
    {
        var box = BoxKey(dir);
        var held = HeldOf(box);
        lock (held)
        {
            var stamp = store.AckStamp(box);
            if (held.AckStamp != stamp)
            {
                held.Acked = new HashSet<string>(store.AckIds(box), StringComparer.Ordinal);
                held.AckStamp = stamp;
            }

            return held.Acked;
        }
    }

    private (string Key, IReadOnlyList<BoxRef> Boxes)? _storedBoxes;

    public void ForgetHeldBoxes()
    {
        lock (_jsonCacheGate)
        {
            _storedBoxes = null;
            _heldScans.Clear();
        }
    }

    private IReadOnlyList<BoxRef> StoredBoxes(IMailStore store)
    {
        var root = NodePath.Join(AhHome, "mailbox");
        return
        [
            .. store.Boxes()
                .Select(static key => (Key: key, Slash: key.IndexOf('/', StringComparison.Ordinal)))
                .Where(static b => b.Slash > 0)
                .OrderBy(static b => b.Key[..b.Slash], StringComparer.Ordinal)
                .ThenBy(static b => b.Key[(b.Slash + 1)..], StringComparer.Ordinal)
                .Select(b =>
                {
                    var team = b.Key[..b.Slash];
                    var tilde = team.IndexOf('~', StringComparison.Ordinal);
                    return new BoxRef($"{b.Key[(b.Slash + 1)..]}@{(tilde < 0 ? team : team[..tilde])}", NodePath.Join(root, b.Key));
                }),
        ];
    }

    private List<JsonObject> StoredHeartbeats(IMailStore store, string dir)
    {
        var all = new List<JsonObject>();
        foreach (var row in store.Adapters(BoxKey(dir)))
        {
            if (StoredMap(row.Value) is { } hb && hb.Get("at") is { IsText: true })
            {
                if (hb.Get("session_id") is not { IsNull: false })
                {
                    hb["session_id"] = row.Key;
                }

                all.Add(hb);
            }
        }

        return all;
    }

    /// <summary>A heartbeat is fresh while it is no older than this many poll intervals.</summary>
    internal const double HeartbeatFreshFactor = 3;

    private static readonly string[] BuiltinHarnesses = ["claude", "codex", "claude-mod", "stub", "probe"];

    private static readonly RequestContext NoContext = new(string.Empty, string.Empty, string.Empty, string.Empty);

    [GeneratedRegex(@"\A[A-Za-z0-9._-]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex NameRe();

    [GeneratedRegex(@"\A([A-Za-z0-9._-]+)@([A-Za-z0-9._-]+)\z", RegexOptions.CultureInvariant)]
    private static partial Regex AddressRe();

    internal static bool IsName(string? text) => text is not null && NameRe().IsMatch(text);

    private string AhHome => environment.AhHome;

    private double NowMs => clock.UtcNow.ToUnixTimeMilliseconds();

    private string RegistrationsDirectory => NodePath.Join(AhHome, "registrations");

    // ---------------------------------------------------------------- boxes

    /// <summary>Whether the team whose file is <paramref name="teamFile"/> has a deputy on duty whose row is live; false for no duty or a dead deputy.</summary>
    internal bool DeputyOnDutyLive(string teamFile, string team) =>
        roster.ReadTeamFileAt(teamFile).Member("duty")?.Member("deputy") is { IsText: true } deputy && deputy.AsString().Length > 0
        && hierarchy.Core.Liveness?.ForMember(deputy.AsString(), team) is { Live: true };

    internal string TeamPathIn(string home, string team) => roster.TeamPath(home, team);

    private const string TallyNote = "deputy-tally";

    public string? DeputyTally(string session)
    {
        if (!IsName(session) || ReadRegistration(session) is not { } registration)
        {
            return null;
        }

        var lines = new List<string>();
        var counts = new List<(string TeamFile, int Handled, int Escalated, int Waiting)>();
        foreach (var box in RegistrationBoxes(registration).Where(static b => b.Address.StartsWith("chief@", StringComparison.Ordinal)))
        {
            var team = box.Address["chief@".Length..];
            if (RegistrationTeams.For(registration, box.Address) is not { } teamFile || !DeputyOnDutyLive(teamFile, team))
            {
                continue;
            }

            var since = roster.ReadTeamFileAt(teamFile).Member("duty")?.Member("since") is { IsText: true } s && Timestamps.TryParseMs(s.AsString(), out var ms) ? ms : 0;
            bool After(JsonValue e) => e.Member("created") is { IsText: true } c && Timestamps.TryParseMs(c.AsString(), out var at) && at >= since;
            var orchestratorDir = BoxDirFor(teamFile, "orchestrator");
            var orchestratorAcked = AckedIn(orchestratorDir);
            var handled = ScanEntries(orchestratorDir).Rows.Count(r => After(r.Entry) && r.Entry.Member("id") is { IsText: true } id && orchestratorAcked(id.AsString()));
            var chiefAcked = AckedIn(box.Dir);
            var toChief = ScanEntries(box.Dir).Rows.Where(r => After(r.Entry)).ToList();
            var waiting = toChief.Count(r => r.Entry.Member("id") is { IsText: true } id && !chiefAcked(id.AsString()));
            lines.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Deputy on duty ({team}): {handled} handled, {toChief.Count} escalated, {waiting} waiting on you."));
            counts.Add((teamFile, handled, toChief.Count, waiting));
        }

        if (lines.Count == 0)
        {
            return null;
        }

        var tally = string.Join('\n', lines);
        if (ReadNote(TallyNote, session)?.Get("line") is { IsText: true } told && told.AsString() == tally)
        {
            return null;
        }

        WriteNote(TallyNote, session, new JsonObject { ["line"] = tally });
        foreach (var (teamFile, handled, escalated, waiting) in counts)
        {
            RecordTally(teamFile, handled, escalated, waiting);
        }

        return tally;
    }

    /// <summary>
    /// Keeps the counts on the record's <c>duty.tally</c>, the one record the views read, so no view scans a mailbox. Written only
    /// when the told tally changed; a failed write leaves the views a tick behind, which is all it costs.
    /// </summary>
    private void RecordTally(string teamFile, int handled, int escalated, int waiting)
    {
        try
        {
            var dir = NodePath.Dirname(NodePath.Dirname(teamFile));
            hierarchy.StateCalls.UpdateTeamFile(teamFile, dir, doc =>
            {
                if (!doc.IsMap || doc.AsObject().Get("duty") is not { IsMap: true } duty)
                {
                    return null;
                }

                duty.AsObject()["tally"] = new JsonObject { ["handled"] = handled, ["escalated"] = escalated, ["waiting"] = waiting };
                return new Ah.Hierarchy.StateCalls.TeamFileOutcome(doc);
            }, new StateCallOptions(Hook: true));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _ = e;
        }
    }

    /// <summary>Whether <paramref name="registration"/> is the team's recorded owner: its session or its pid is the record's <c>orchestrator</c>.</summary>
    internal bool OwnsByRecord(string teamFile, JsonObject registration)
    {
        var owner = roster.ReadTeamFileAt(teamFile).Member("orchestrator");
        return owner is { IsMap: true }
            && ((owner.Value.Member("session_id") is { IsText: true } s && registration.Get("session_id") is { IsText: true } rs && s.AsString() == rs.AsString())
                || (owner.Value.Member("pid") is { IsNumber: true } p && registration.Get("pid") is { IsNumber: true } rp && p.AsNumber() == rp.AsNumber()));
    }

    public string BoxDirFor(string teamFile, string name)
    {
        var team = NodePath.Basename(teamFile);
        if (team.EndsWith(".json", StringComparison.Ordinal))
        {
            team = team[..^".json".Length];
        }

        var real = RealPath(teamFile);
        var hash = Convert.ToHexString(SHA256.HashData(Utf8Text.Encode(string.IsNullOrEmpty(real) ? teamFile : real))).ToLowerInvariant()[..10];
        return NodePath.Join(AhHome, "mailbox", $"{team}~{hash}", name);
    }

    /// <summary>Whether two paths name the same place once symbolic links are resolved; a path that cannot be resolved is compared as written.</summary>
    internal bool SamePlace(string left, string right) =>
        string.Equals(Resolved(left), Resolved(right), StringComparison.Ordinal);

    private string Resolved(string path) => RealPath(path) is { Length: > 0 } real ? real : path;

    internal string? RealPath(string path)
    {
        try
        {
            return files.RealPath(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public AddressParts? SplitAddress(string? address) => Split(address);

    internal static AddressParts? Split(string? address)
    {
        var m = AddressRe().Match(address ?? string.Empty);
        return m.Success ? new AddressParts(m.Groups[1].Value, m.Groups[2].Value) : null;
    }

    public IReadOnlyList<BoxRef> ListBoxes()
    {
        if (Stored is { } store)
        {
            // b459 step 11: listed once per mail write (the token) or new heartbeat box, not once per tick; a box only a heartbeat holds moves no token
            var token = store.ChangeToken().ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + store.HeartbeatBoxes().ToString(System.Globalization.CultureInfo.InvariantCulture);
            lock (_jsonCacheGate)
            {
                if (_storedBoxes is { } held && held.Key == token)
                {
                    return held.Boxes;
                }
            }

            var listed = StoredBoxes(store);
            lock (_jsonCacheGate)
            {
                _storedBoxes = (token, listed);
            }

            return listed;
        }

        var root = NodePath.Join(AhHome, "mailbox");
        var boxes = new List<BoxRef>();
        try
        {
            foreach (var key in Names(root))
            {
                var tilde = key.IndexOf('~', StringComparison.Ordinal);
                var teamName = tilde < 0 ? key : key[..tilde];
                foreach (var name in Names(NodePath.Join(root, key)))
                {
                    boxes.Add(new BoxRef($"{name}@{teamName}", NodePath.Join(root, key, name)));
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // an entry that is not a directory ends the walk with what it found
        }

        return boxes;
    }

    /// <summary>The names in a directory in byte order, the order a directory read hands them in.</summary>
    private List<string> Names(string directory)
    {
        var names = new List<string>(files.List(directory));
        names.Sort(StringComparer.Ordinal);
        return names;
    }

    private readonly Dictionary<string, (ChangedSince Gate, string Text)> _jsonCache = new(StringComparer.Ordinal);
    private readonly object _jsonCacheGate = new();

    /// <summary>
    /// <see cref="ReadJson"/> for a file read on every request (the registrations, the heartbeats): the text is kept with the
    /// stat it was read under, so a file nobody has rewritten costs one <c>stat</c> instead of an open, a read and a close. A
    /// rewrite is a rename onto a new inode, which the stat sees.
    /// </summary>
    private JsonValue? ReadJsonCached(string path) => ReadTextCached(path) is { } text ? ParseOrNull(text) : null;

    private static JsonValue? ParseOrNull(string text)
    {
        try
        {
            return JsonText.Parse(text);
        }
        catch (JsonParseException)
        {
            return null;
        }
    }

    private string? ReadTextCached(string path)
    {
        try
        {
            var readAt = NowMs;
            if (files.Stat(path) is not { } stat)
            {
                lock (_jsonCacheGate)
                {
                    _ = _jsonCache.Remove(path);
                }

                return null;
            }

            lock (_jsonCacheGate)
            {
                if (_jsonCache.TryGetValue(path, out var held) && held.Gate.Unchanged(stat))
                {
                    return held.Text;
                }
            }

            var text = files.ReadText(path);
            var gate = new ChangedSince();
            gate.Read(stat, readAt);
            lock (_jsonCacheGate)
            {
                _jsonCache[path] = (gate, text);
            }

            return text;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Drops the cached files under <paramref name="directory"/> that are not in <paramref name="seen"/>.</summary>
    private void ForgetUnseen(string directory, HashSet<string> seen)
    {
        var prefix = directory + "/";
        lock (_jsonCacheGate)
        {
            foreach (var stale in _jsonCache.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal) && !seen.Contains(k)).ToList())
            {
                _ = _jsonCache.Remove(stale);
            }
        }
    }

    private JsonValue? ReadJson(string path)
    {
        try
        {
            return JsonText.Parse(files.ReadText(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonParseException)
        {
            return null;
        }
    }

    private static JsonObject? AsMap(JsonValue? value) => value is { IsMap: true } v ? v.AsObject() : null;

    // ---------------------------------------------------------------- registrations

    public string RegistrationFile(string sessionId) => NodePath.Join(RegistrationsDirectory, $"{sessionId}.json");

    public JsonObject? ReadRegistration(string? sessionId)
    {
        var id = sessionId ?? string.Empty;
        if (!IsName(id))
        {
            return null;
        }

        if (sessions is { IsDatabase: true } store)
        {
            return store.ReadStored(id) is { } stored ? RegistrationOf(ParseOrNull(Utf8Text.Decode(stored.Span))) : null;
        }

        return RegistrationOf(ReadJson(RegistrationFile(id)));
    }

    public long? SessionsChangeToken() => sessions is { IsDatabase: true } store ? store.ChangeToken() : null;

    public IReadOnlyList<string>? StoredSessionIds() => sessions is { IsDatabase: true } store ? [.. store.ListStored().Select(static row => row.Key)] : null;

    public IReadOnlyDictionary<string, string>? StoredSessionStamps() => sessions is { IsDatabase: true } store
        ? store.ListStored().Concat(store.ListEnded()).ToDictionary(static row => row.Key, static row => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(row.Value.Span)), StringComparer.Ordinal)
        : null;

    public double? RegistrationWrittenMs(string sessionId) =>
        sessions is { IsDatabase: true }
        && ReadRegistration(sessionId)?.Get("at") is { IsText: true } at
        && DateTimeOffset.TryParse(at.AsString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var written)
            ? written.ToUnixTimeMilliseconds()
            : null;

    private static JsonObject? RegistrationOf(JsonValue? value) =>
        AsMap(value) is { } o && o.Get("address") is { IsText: true } ? o : null;

    public IReadOnlyList<BoxRef> RegistrationBoxes(JsonValue? registration) => Ah.Mesh.RegistrationTeams.Boxes(AsMap(registration));

    public IReadOnlyList<string> RegistrationTeamFiles(JsonValue? registration) => Ah.Mesh.RegistrationTeams.Of(AsMap(registration));

    internal bool PidAlive(JsonValue pid) => hierarchy.Core.PidAlive(pid);

    public bool RegistrationLive(JsonObject registration)
    {
        var pid = registration.Get("pid") ?? JsonValue.Null;
        var start = registration.Get("pid_start");
        (int Pid, string Start)? identity = pid.IsNumber && pid.AsNumber() is var n && n == Math.Floor(n) && n > 1 && n <= int.MaxValue
            && start is { IsText: true } s && s.AsString().Length > 0 ? ((int)n, s.AsString()) : null;
        if (identity is { } known && _goneProcesses.ContainsKey(known))
        {
            return CustomLive(registration);
        }

        if (hierarchy.Core.PidRunning(
                pid,
                start,
                Ah.Core.Time.LivenessPolicy.RunningOrUnknown,
                registration.Get("harness") is not { IsText: true } h || h.AsString().Length == 0 || h.AsString() == "claude"))
        {
            return true;
        }

        if (identity is { } checkedOnce && processes is { } table && ProcessGone(table, checkedOnce.Pid, checkedOnce.Start))
        {
            if (_goneProcesses.TryAdd(checkedOnce, 0) && sessions is { IsDatabase: true } store
                && registration.Get("session_id") is { IsText: true } id && IsName(id.AsString()))
            {
                // the row is ended once, so every later read skips it; a process that could not be checked never reaches here
                _ = store.EndGone(id.AsString(), checkedOnce.Pid, checkedOnce.Start);
            }
        }

        return CustomLive(registration);
    }

    /// <summary>
    /// The processes, by pid and recorded start, that the kernel showed gone: that process can never run again, so its registration
    /// is not judged again. The daemon reads hundreds of never-ended registrations several times a second, and judging each spent
    /// most of a slow watch tick in process lookups.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(int Pid, string Start), byte> _goneProcesses = new();

    /// <summary>Whether the process recorded as <paramref name="pid"/> started at <paramref name="recordedStart"/> is gone for good: its pid now has another start, or no process has it.</summary>
    private bool ProcessGone(IProcessTable table, int pid, string recordedStart) =>
        table.StartTime(pid) is null
            ? !hierarchy.Core.PidAlive(JsonValue.FromNumber(pid))
            : Ah.Core.Time.ProcessStart.Compare(table, pid, recordedStart) == StartMatch.Different;

    /// <summary>A registration of a harness ah has no code for that gave no pid is live while a pull keeps a heartbeat of its own session fresh.</summary>
    private bool CustomLive(JsonObject r)
    {
        var harness = r.Get("harness") is { IsTruthy: true } h ? h : JsonValue.FromString("claude");
        if (harness is { IsText: true } named && BuiltinHarnesses.Contains(named.AsString(), StringComparer.Ordinal))
        {
            return false;
        }

        if (r.Get("pid") is { IsNumber: true } pid && double.IsFinite(pid.AsNumber()) && pid.AsNumber() == Math.Floor(pid.AsNumber()))
        {
            return false;
        }

        var session = r.Get("session_id");
        return RegistrationBoxes(r).Any(b => FreshOf(ReadHeartbeatOf(b.Dir, session, NowMs), NowMs));
    }

    private readonly ChangedSince _registrationsDirGate = new();
    private List<JsonObject>? _registrationRows;
    private List<JsonObject>? _storedRegistrationRows;
    private long _storedRegistrationsToken;

    /// <summary>
    /// Every registration file. While the registrations directory has the stat it had when the files were last read, the rows
    /// parsed then are returned without a directory read, a stat of any file or a parse: registrations are written by rename into
    /// the directory, which changes its stat. Once the database holds them, the sessions change token plays that part. The rows are
    /// shared, so callers only read them.
    /// </summary>
    public IReadOnlyList<JsonObject> EvidenceRegistrations()
    {
        var all = AllRegistrations();
        if (sessions is not { IsDatabase: true } store)
        {
            return all;
        }

        var token = store.ChangeToken();
        lock (_jsonCacheGate)
        {
            if (_endedRegistrationRows is { } held && _endedRegistrationsToken == token)
            {
                return [.. all, .. held];
            }
        }

        List<JsonObject> ended = [.. store.ListEnded().Select(row => RegistrationOf(ParseOrNull(Utf8Text.Decode(row.Value.Span)))).OfType<JsonObject>()];
        lock (_jsonCacheGate)
        {
            _endedRegistrationRows = ended;
            _endedRegistrationsToken = token;
        }

        return [.. all, .. ended];
    }

    private List<JsonObject>? _endedRegistrationRows;

    private long? _endedRegistrationsToken;

    public IReadOnlyList<JsonObject> AllRegistrations()
    {
        if (sessions is { IsDatabase: true } store)
        {
            // the token is read before the rows: a change between the two is read again next time, never missed
            var token = store.ChangeToken();
            lock (_jsonCacheGate)
            {
                if (_storedRegistrationRows is { } held && _storedRegistrationsToken == token)
                {
                    return held;
                }
            }

            List<JsonObject> stored = [.. store.ListStored().Select(row => RegistrationOf(ParseOrNull(Utf8Text.Decode(row.Value.Span)))).OfType<JsonObject>()];
            lock (_jsonCacheGate)
            {
                _storedRegistrationRows = stored;
                _storedRegistrationsToken = token;
            }

            return stored;
        }

        var directory = RegistrationsDirectory;
        var readAt = NowMs;
        FileStat? directoryStat;
        try
        {
            directoryStat = files.Stat(directory);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        lock (_jsonCacheGate)
        {
            if (_registrationsDirGate.Unchanged(directoryStat) && _registrationRows is { } known)
            {
                return known;
            }
        }

        if (ReadRegistrationTexts(directory) is not { } texts)
        {
            return [];
        }

        var all = new List<JsonObject>();
        foreach (var text in texts)
        {
            if (RegistrationOf(ParseOrNull(text)) is { } r)
            {
                all.Add(r);
            }
        }

        lock (_jsonCacheGate)
        {
            _registrationRows = all;
            _registrationsDirGate.Read(directoryStat, readAt);
        }

        return all;
    }

    private List<string>? ReadRegistrationTexts(string directory)
    {
        List<string> names;
        try
        {
            names = Names(directory);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var texts = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            var path = NodePath.Join(directory, name);
            if (name.EndsWith(".json", StringComparison.Ordinal) && ReadTextCached(path) is { } text)
            {
                texts.Add(text);
                _ = seen.Add(path);
            }
        }

        ForgetUnseen(directory, seen);
        return texts;
    }

    public IReadOnlyList<JsonObject> LiveRegistrations() => AllRegistrations().Where(RegistrationLive).ToList();

    /// <summary>Hands a registration write to the status deltas (H3).</summary>
    internal void StatusDelta(OpCall operation, OpResult result) => hierarchy.StatusRefresher.Delta("mailbox", operation, result, null);

    public CallerPrincipal PrincipalOf(int? callerPid)
    {
        if (processes is not { } table)
        {
            return CallerPrincipal.Anonymous;
        }

        // ah2-32: the session the user designated as admin acts as the user, so nothing refuses it. ah2-50: it also holds its own
        // session, the registrations of the granted harness process itself, so a verb that acts as a session can still name it. Newest
        // registration (`at`) first: a /clear gives the same process a new session and leaves the old one live, and the user's choice
        // takes the first when no hint names one
        if (AdminGrant.Holds(table, callerPid, homeDocs, environment.AhHome))
        {
            var harness = AdminGrant.HarnessOf(table, callerPid);
            var own = Resolved(table, harness).Sessions.Where(s => s.Pid == harness)
                .OrderByDescending(s => ReadRegistration(s.SessionId)?.Get("at") is { IsText: true } at && Timestamps.TryParseMs(at.AsString(), out var ms) ? ms : double.MinValue)
                .ToList();
            return own.Count == 0 ? CallerPrincipal.User : CallerPrincipal.User with { Sessions = own };
        }

        return Resolved(table, callerPid);
    }

    /// <summary>The principal <paramref name="callerPid"/>'s own process tree proves, with no admin grant considered.</summary>
    private CallerPrincipal Resolved(IProcessTable table, int? callerPid)
    {
        var bySession = LiveRegistrations()
            .Where(r => r.Get("session_id") is { IsText: true })
            .GroupBy(r => r.Get("session_id")!.Value.AsString(), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        return CallerIdentity.Resolve(
            table,
            callerPid,
            CallerIdentity.LiveSessions(this),
            (session, team) => bySession.TryGetValue(session.SessionId, out var reg)
                && RegistrationTeams.For(reg, $"{AncestorSearch.OrchestratorName}@{team}") is { } teamFile
                && CallerIdentity.IsRecordedOrchestrator(table, roster.ReadTeamFileAt(teamFile), session.Pid));
    }

    public bool WriteRegistration(RegistrationWrite registration) =>
        RegistrationArgs(registration) is { } args && Invoke("register", args).Member("registered") is { IsTruthy: true };

    public RegisterOutcome RegisterGuarded(RegistrationWrite registration, RegistrationHeld? held = null)
    {
        if (RegistrationArgs(registration) is not { } args)
        {
            return RegisterOutcome.Invalid;
        }

        args["guard"] = true;
        if (held is not null)
        {
            args["held_pid"] = held.Pid;
            args["held_alive"] = held.Alive;
            if (held.Start is not null)
            {
                args["held_start"] = held.Start;
            }
        }

        var result = Invoke("register", args);
        if (result.Member("registered") is { IsTruthy: true })
        {
            return RegisterOutcome.Written;
        }

        return result.Member("refused") is { IsText: true } why
            ? why.AsString() == "bound" ? RegisterOutcome.BoundExists : RegisterOutcome.HeldByAnotherProcess
            : RegisterOutcome.Invalid;
    }

    /// <summary>
    /// Whether the pid a registration records is shown to be the session's harness: true for a harness-looking command line or a
    /// non-claude harness, false for a readable command line that is not a harness (a shell wrapper that can exit while the session goes
    /// on), null when there is no pid or its command line cannot be read. Only a false is written to the registration (an absent flag
    /// already lets the pid prove a death), and only the liveness verdict reads it, so a wrapper's exit never proves a live member
    /// gone; the pid itself is recorded as given.
    /// </summary>
    private bool? PidProof(RegistrationWrite registration)
    {
        if (registration.Pid is not { } pid)
        {
            return null;
        }

        if (registration.Harness is not (null or "claude"))
        {
            return true;
        }

        return processes?.Command((int)pid) is { } command ? MeshModule.IsHarnessCommand(command) : null;
    }

    private JsonObject? RegistrationArgs(RegistrationWrite registration)
    {
        if (!IsName(registration.SessionId) || registration.Boxes.Count == 0)
        {
            return null;
        }

        var boxes = new JsonArray();
        foreach (var box in registration.Boxes)
        {
            boxes.Add(new JsonObject { ["address"] = box.Address, ["dir"] = box.Dir });
        }

        var args = new JsonObject
        {
            ["session_id"] = registration.SessionId,
            ["boxes"] = boxes,
            ["team_file"] = registration.TeamFile is null ? JsonValue.Null : registration.TeamFile,
            ["harness"] = registration.Harness is null ? JsonValue.Null : registration.Harness,
            ["pid"] = registration.Pid is null ? JsonValue.Null : registration.Pid.Value,
            ["pane_id"] = registration.PaneId is null ? JsonValue.Null : registration.PaneId,
            ["cwd"] = registration.Cwd is null ? JsonValue.Null : registration.Cwd,
            ["bound"] = registration.Bound,
        };
        if (registration.TeamFiles is { Count: > 0 } teamFiles)
        {
            var byAddress = new JsonObject();
            foreach (var (address, file) in teamFiles)
            {
                byAddress[address] = file;
            }

            args["team_files"] = byAddress;
        }

        if (registration.Pid is not null && !string.IsNullOrEmpty(registration.PidStart))
        {
            args["pid_start"] = registration.PidStart;
        }

        if (PidProof(registration) == false)
        {
            args["pid_proof"] = false;
        }

        if (!string.IsNullOrEmpty(registration.AhVersion))
        {
            args["ah_version"] = registration.AhVersion;
        }

        if (registration.HerdrFlavour is Ah.Core.Mux.HerdrFlavour.Tui or Ah.Core.Mux.HerdrFlavour.Core)
        {
            args[Ah.Core.Mux.HerdrFlavour.Field] = registration.HerdrFlavour;
        }

        if (!string.IsNullOrEmpty(registration.TeamsStamp))
        {
            args["teams_stamp"] = registration.TeamsStamp;
        }

        return args;
    }

    public bool RemoveRegistration(string? sessionId)
    {
        var id = sessionId ?? string.Empty;
        return IsName(id) && Invoke("unregister", new JsonObject { ["session_id"] = id }).Member("removed") is { IsTruthy: true };
    }

    // ---------------------------------------------------------------- entries

    /// <summary>
    /// Parsed entries from byte <paramref name="from"/> with the byte offset just past each one, oldest first, and the cursor after
    /// the last complete line. A line that is not valid JSON, or a final line with no newline yet, is skipped and the file is never repaired.
    /// </summary>
    internal ScanResult ScanEntries(string dir, long from = 0)
    {
        if (Stored is { } store)
        {
            return from == 0 ? HeldStoredScan(store, dir) : ScanStored(store, dir, from);
        }

        var path = NodePath.Join(dir, "entries.jsonl");
        try
        {
            return from == 0
                ? CachedByStat(_scanCache, path, () => ParseEntries(files.ReadBytes(path), 0))
                : ParseEntries(files.ReadBytes(path), from);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new ScanResult([], from);
        }
    }

    private readonly Dictionary<string, (ChangedSince Gate, ScanResult Value)> _scanCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (ChangedSince Gate, IReadOnlyList<JsonObject> Value)> _attemptsCache = new(StringComparer.Ordinal);

    /// <summary>
    /// What <paramref name="build"/> parses from the file at <paramref name="path"/>, kept with the stat the file was read under so a
    /// log nobody has appended to costs one <c>stat</c> instead of a read and a parse. A file written within the last second is parsed
    /// again each time (see <see cref="ChangedSince"/>). The kept value is shared by every caller and is never changed.
    /// </summary>
    private T CachedByStat<T>(Dictionary<string, (ChangedSince Gate, T Value)> cache, string path, Func<T> build)
    {
        var readAt = NowMs;
        if (files.Stat(path) is not { } stat)
        {
            lock (_jsonCacheGate)
            {
                _ = cache.Remove(path);
            }

            return build();
        }

        lock (_jsonCacheGate)
        {
            if (cache.TryGetValue(path, out var held) && held.Gate.Unchanged(stat))
            {
                return held.Value;
            }
        }

        var value = build();
        var gate = new ChangedSince();
        gate.Read(stat, readAt);
        lock (_jsonCacheGate)
        {
            cache[path] = (gate, value);
        }

        return value;
    }

    private static ScanResult ParseEntries(byte[] buffer, long from)
    {
        var rows = new List<ScannedEntry>();
        var pos = from;
        while (pos < buffer.Length)
        {
            var newline = Array.IndexOf(buffer, (byte)0x0a, (int)pos);
            if (newline < 0)
            {
                break;
            }

            var text = JsText.Trim(Utf8Text.Decode(buffer.AsSpan((int)pos, newline - (int)pos)));
            pos = newline + 1;
            if (text.Length == 0)
            {
                continue;
            }

            try
            {
                var entry = JsonText.Parse(text);
                if (entry.IsMap && entry.AsObject().Get("id") is { IsText: true })
                {
                    rows.Add(new ScannedEntry(entry, pos));
                }
            }
            catch (JsonParseException)
            {
                // a torn line costs only itself
            }
        }

        return new ScanResult(rows, pos);
    }

    // ---------------------------------------------------------------- heartbeats, acks

    /// <summary>Every heartbeat in box <paramref name="dir"/>: one file per session under <c>adapters/</c>, then a box-wide <c>adapter.json</c> an older adapter may still write.</summary>
    private List<JsonObject> Heartbeats(string dir)
    {
        if (Stored is { } store)
        {
            return StoredHeartbeats(store, dir);
        }

        var all = new List<JsonObject>();
        try
        {
            var adapters = NodePath.Join(dir, "adapters");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var name in Names(adapters))
            {
                if (!name.EndsWith(".json", StringComparison.Ordinal))
                {
                    continue;
                }

                var path = NodePath.Join(adapters, name);
                _ = seen.Add(path);
                if (AsMap(ReadJsonCached(path)) is { } hb && hb.Get("at") is { IsText: true })
                {
                    var copy = hb.Clone();
                    if (hb.Get("session_id") is not { IsNull: false })
                    {
                        copy["session_id"] = name[..^".json".Length];
                    }

                    all.Add(copy);
                }
            }

            ForgetUnseen(adapters, seen);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // no heartbeat directory yet
        }

        if (AsMap(ReadJsonCached(NodePath.Join(dir, "adapter.json"))) is { } legacy && legacy.Get("at") is { IsText: true })
        {
            all.Add(legacy);
        }

        return all;
    }

    public double? HeartbeatAgeMs(string dir, string sessionId, double pollMs)
    {
        var now = NowMs;
        return ReadHeartbeat(dir, sessionId, now) is { } hb && hb.Get("poll_ms") is { IsNumber: true } p && p.AsNumber() == pollMs
            && ParseMs(hb.Get("at")) is var at && double.IsFinite(at) && at <= now
            ? now - at
            : null;
    }

    public JsonObject? ReadHeartbeat(string dir, string? sessionId = null, double? nowMs = null)
    {
        if (string.IsNullOrEmpty(sessionId))
        {
            return ReadHeartbeatOf(dir, null, nowMs ?? NowMs);
        }

        return ReadHeartbeatOf(dir, JsonValue.FromString(sessionId), nowMs ?? NowMs);
    }

    /// <summary>Reads the heartbeat for a session id as it stands in a registration: a falsy id means the whole box, any other value must match a heartbeat's id strictly.</summary>
    private JsonObject? ReadHeartbeatOf(string dir, JsonValue? sessionId, double nowMs)
    {
        var all = Heartbeats(dir);
        if (sessionId is { IsTruthy: true } wanted)
        {
            all = all.Where(h => StrictEquals(h.Get("session_id"), wanted)).ToList();
        }

        if (all.Count == 0)
        {
            return null;
        }

        var fresh = all.Where(h => FreshOf(h, nowMs)).ToList();
        var pool = fresh.Count > 0 ? fresh : all;
        var best = pool[0];
        foreach (var next in pool.Skip(1))
        {
            if (ParseMs(next.Get("at")) > ParseMs(best.Get("at")))
            {
                best = next;
            }
        }

        return best;
    }

    private static double ParseMs(JsonValue? at) =>
        at is { IsText: true } t && Timestamps.TryParseMs(t.AsString(), out var ms) ? ms : double.NaN;

    private static bool StrictEquals(JsonValue? a, JsonValue? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }

        var (x, y) = (a.Value, b.Value);
        if (x.IsNull || y.IsNull)
        {
            return x.IsNull && y.IsNull;
        }

        if (x.IsText && y.IsText)
        {
            return string.Equals(x.AsString(), y.AsString(), StringComparison.Ordinal);
        }

        if (x.IsNumber && y.IsNumber)
        {
            return x.AsNumber() == y.AsNumber();
        }

        if (x.IsBool && y.IsBool)
        {
            return x.AsBool() == y.AsBool();
        }

        return false;
    }

    public bool HeartbeatFresh(JsonValue? heartbeat, double? nowMs = null) => FreshOf(AsMap(heartbeat), nowMs ?? NowMs);

    public bool PullFresh(string boxDir, string sessionId, double nowMs)
    {
        var heartbeat = ReadHeartbeat(boxDir, sessionId, nowMs);
        return HeartbeatFresh(heartbeat is null ? (JsonValue?)null : JsonValue.FromObject(heartbeat), nowMs);
    }

    public bool DoorbellLive(string boxDir, string address, string? toSession, double nowMs) => DoorbellSessions(boxDir, address, toSession, nowMs).Any();

    public string? DoorbellGoneUnread(string boxDir, string address, string? toSession, double nowMs) =>
        DoorbellSessions(boxDir, address, toSession, nowMs).FirstOrDefault(session => MailDoorbell.RingsGoUnread(this, session, nowMs));

    public void MarkDoorbellUncarried(string session, double nowMs, string reason) => MailDoorbell.MarkUncarried(this, session, nowMs, reason);

    public bool DoorbellSessionLive(string session) =>
        ReadRegistration(session) is { } registration && RegistrationLive(registration) && MailDoorbell.ServesSession(this, environment, session);

    public double DoorbellLastPoll(string boxDir, string session) =>
        Heartbeats(boxDir)
            .Where(h => h.Get("adapter") is { IsText: true } adapter && adapter.AsString() == MailDue.Adapter
                && h.Get("session_id") is { IsText: true } id && id.AsString() == session)
            .Select(h => ParseMs(h.Get("at")))
            .Where(double.IsFinite)
            .DefaultIfEmpty(0)
            .Max();

    private IEnumerable<string> DoorbellSessions(string boxDir, string address, string? toSession, double nowMs) =>
        !address.StartsWith("orchestrator@", StringComparison.Ordinal)
            ? []
            : Heartbeats(boxDir)
                .Where(h =>
                    h.Get("adapter") is { IsText: true } adapter && adapter.AsString() == MailDue.Adapter
                    && h.Get("session_id") is { IsText: true } session && (toSession is null || session.AsString() == toSession)
                    && nowMs - ParseMs(h.Get("at")) <= _core.PullTimeoutMs(JsonValue.FromObject(h))
                    && MailDoorbell.ServesSession(this, environment, session.AsString()))
                .Select(static h => h.Get("session_id")!.Value.AsString());

    public bool OrchestratorPullFresh(string sessionId, double nowMs)
    {
        var registration = ReadRegistration(sessionId);
        return RegistrationBoxes(registration is null ? JsonValue.Null : registration)
            .Where(b => b.Address.StartsWith("orchestrator@", StringComparison.Ordinal))
            .Any(b => PullFresh(b.Dir, sessionId, nowMs));
    }

    private static bool FreshOf(JsonObject? hb, double nowMs)
    {
        if (hb is null)
        {
            return false;
        }

        var t = ParseMs(hb.Get("at"));
        var poll = hb.Get("poll_ms") is { IsNumber: true } p && double.IsFinite(p.AsNumber()) && p.AsNumber() > 0 ? p.AsNumber() : IMailCore.DefaultPollMs;
        return double.IsFinite(t) && nowMs - t <= HeartbeatFreshFactor * poll;
    }

    public long AckedThrough(string dir)
    {
        long through = 0;
        var isAcked = AckedIn(dir);
        foreach (var row in ScanEntries(dir).Rows)
        {
            if (!isAcked(row.Entry.AsObject().Get("id")!.Value.AsString()))
            {
                break;
            }

            through = row.End;
        }

        return through;
    }

    public void WriteHeartbeat(string dir, string adapter, string? sessionId, double pollMs) =>
        Invoke(
            "heartbeat",
            new JsonObject
            {
                ["dir"] = dir,
                ["adapter"] = adapter,
                ["session_id"] = sessionId is null ? JsonValue.Null : sessionId,
                ["poll_ms"] = pollMs,
            });

    internal static string AckFile(string dir, string id) => NodePath.Join(dir, "acks", id);

    public bool IsMemberDigestNote(JsonValue entry, string boxAddress) => MailDoorbell.IsMemberDigestNote(entry, boxAddress, environment);

    public bool IsAcked(string dir, string id) =>
        IsName(id) && (Stored is { } store ? store.Ack(BoxKey(dir), id) is not null : files.Exists(AckFile(dir, id)));

    public Func<string, bool> AckedIn(string dir)
    {
        HashSet<string> names;
        try
        {
            names = Stored is { } store
                ? new HashSet<string>(store.AckIds(BoxKey(dir)), StringComparer.Ordinal)
                : new HashSet<string>(files.List(NodePath.Join(dir, "acks")).Select(NodePath.Basename), StringComparer.Ordinal);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            names = [];
        }

        return id => IsName(id) && names.Contains(id);
    }

    public JsonValue? ReadAck(string dir, string id)
    {
        if (!IsName(id))
        {
            return null;
        }

        return Stored is { } store
            ? store.Ack(BoxKey(dir), id) is { } raw ? ParseOrNull(Utf8Text.Decode(raw.Span)) : null
            : ReadJson(AckFile(dir, id));
    }

    public AckIdsResult AckIds(string dir, IReadOnlyList<string> ids, string by, string? sessionId = null, string? reason = null)
    {
        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in ScanEntries(dir).Rows)
        {
            present.Add(row.Entry.AsObject().Get("id")!.Value.AsString());
        }

        var acked = new List<string>();
        var unknown = new List<string>();
        var fresh = new List<string>();
        foreach (var id in ids)
        {
            if (!IsName(id) || !present.Contains(id))
            {
                unknown.Add(id);
                continue;
            }

            var args = new JsonObject
            {
                ["dir"] = dir,
                ["id"] = id,
                ["by"] = by,
                ["session_id"] = sessionId is null ? JsonValue.Null : sessionId,
            };
            if (reason is not null)
            {
                args["reason"] = reason;
            }

            if (Invoke("ack", args).Member("fresh") is { IsTruthy: true })
            {
                fresh.Add(id);
            }

            acked.Add(id);
        }

        return new AckIdsResult(acked, unknown, fresh);
    }

    public PasteAck AckPaste(PasteEntry? entry)
    {
        if (entry is null || string.IsNullOrEmpty(entry.Box) || IsAcked(entry.Box, entry.Id))
        {
            return PasteAck.Already;
        }

        try
        {
            AckIds(entry.Box, [entry.Id], "paste");
            return PasteAck.Paste;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return PasteAck.Failed;
        }
    }

    public void LogPasteAttempt(PasteEntry? entry, JsonObject row)
    {
        try
        {
            var dir = entry is { Box: { Length: > 0 } box } ? box : NodePath.Join(AhHome, "run", "paste");
            JsonValue id = entry is null ? JsonValue.Null : entry.Id;
            var record = new JsonObject
            {
                ["id"] = id,
                ["rung"] = 4,
                ["state"] = "paste-" + JsConvert.ToJsString(row.Get("outcome")),
                ["entry"] = id,
            };
            foreach (var key in row.Keys)
            {
                record[key] = row[key];
            }

            AppendAttempt(dir, record);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // the attempt log never changes the outcome
        }
    }

    public void AppendAttempt(string dir, JsonObject row) =>
        Invoke("attempt", new JsonObject { ["dir"] = dir, ["row"] = row });

    public IReadOnlyList<JsonObject> ReadAttempts(string dir)
    {
        if (Stored is { } store)
        {
            // b459: not kept; a box's whole routing history was 57k parsed rows held. The hot readers go through AttemptsOf and LadderSweep.
            return [.. store.Attempts(BoxKey(dir)).Select(static raw => StoredMap(raw)).OfType<JsonObject>()];
        }

        var path = NodePath.Join(dir, "attempts.jsonl");
        try
        {
            return CachedByStat<IReadOnlyList<JsonObject>>(_attemptsCache, path, () => ParseAttempts(files.ReadText(path)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public IReadOnlyList<JsonObject> AttemptsOf(string dir, JsonValue? id) =>
        Stored is { } store && id is { IsText: true } text
            ? [.. store.Attempts(BoxKey(dir), text.AsString()).Select(static raw => StoredMap(raw)).OfType<JsonObject>()]
            : ReadAttempts(dir);

    public LadderSweepRows LadderSweep(string dir)
    {
        if (Stored is not { } store)
        {
            var open = new List<LadderEntryRows>();
            var settled = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var e in LadderRowsByEntry(dir))
            {
                if (e.Rows[^1].Get("state") is { IsText: true } s && DrainedState.IsSettled(s.AsString()))
                {
                    settled[e.Id] = s.AsString();
                }
                else
                {
                    open.Add(e);
                }
            }

            return new LadderSweepRows(open, settled);
        }

        return CachedByToken(_storedSweeps, dir, store, () =>
        {
            var (rows, done) = store.LadderSweep(BoxKey(dir), DrainedState.Settled);
            var settled = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (id, state) in done)
            {
                // an attempt with no text id is stored under ''; no entry's ladder is keyed so
                if (id.Length > 0)
                {
                    settled[id] = state;
                }
            }

            return new LadderSweepRows(ByEntry([.. rows.Select(static raw => StoredMap(raw)).OfType<JsonObject>()]), settled);
        });
    }

    private readonly Dictionary<string, (long Token, LadderSweepRows Value)> _storedSweeps = new(StringComparer.Ordinal);

    private static List<JsonObject> ParseAttempts(string text)
    {
        var rows = new List<JsonObject>();
        foreach (var line in text.Split('\n'))
        {
            try
            {
                if (JsonText.Parse(line) is { IsMap: true } v)
                {
                    rows.Add(v.AsObject());
                }
            }
            catch (JsonParseException)
            {
                // a torn line costs only itself
            }
        }

        return rows;
    }

    public IStateDomain StateDomain() => new MailboxStateDomain(this, files, environment, clock, sessions);

    // ---------------------------------------------------------------- state writes

    private JsonValue Invoke(string op, JsonObject args, TimeSpan? lockWait = null)
    {
        var result = state.Invoke(
            "mailbox",
            op,
            JsonText.ToUtf8(args, 0),
            NoContext,
            lockWait is null ? null : new StateCallOptions(LockWait: lockWait));
        if (!result.Ok)
        {
            throw new MailboxError(result.ErrorCode == "lock_timeout" ? "busy" : result.ErrorCode ?? "op_failed", result.Error ?? $"mailbox.{op} failed");
        }

        return result.ResultJson.IsEmpty ? JsonValue.Null : JsonText.Parse(result.ResultJson.Span);
    }
}
