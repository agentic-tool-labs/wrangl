using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Hierarchy.StateCalls;
using Ah.Roster;

namespace Ah.Hierarchy;

/// <summary>The parts of a message file name <c>&lt;id&gt;--&lt;to&gt;--&lt;slug&gt;--&lt;type&gt;.md</c>.</summary>
public sealed record MsgFilenameParts(string Id, string To, string Slug, string Type);

/// <summary>
/// A <c>[hierarchy-msg …]</c> token resolved: its first word, its type, and the message key, null when the id matched no message or more
/// than one; <paramref name="Candidates"/> then lists each match's recipient and pool.
/// </summary>
public sealed record MsgTokenHit(string Word, string Type, string? Key, IReadOnlyList<(string To, string Pool)> Candidates);

/// <summary>
/// What the registration of a member's session says about the member: that session's id, whether the member is live, why, and when the
/// registration was written (null when it carries no readable time). <paramref name="ProvenDead"/> is true only when the process the
/// registration names is positively shown not to be the session; a not-live verdict without it is undecided, not dead.
/// <paramref name="PidGone"/> is true when the registered pid is shown gone even though, with no proof the pid was the harness, that alone
/// does not prove the session gone; another sign (a herdr pane with no agent) can then decide.
/// </summary>
public sealed record MemberRegistrationLiveness(string SessionId, bool Live, string Why, DateTimeOffset? At = null, string? Cwd = null, bool ProvenDead = false, bool PidGone = false)
{
    /// <summary>Whether the member still holds its claim at <paramref name="now"/>: it is live, or undecided with a registration written within the claim window.</summary>
    public bool HoldsClaim(DateTimeOffset now) =>
        Ah.Core.Time.LivenessRules.HoldsClaim(Live ? Ah.Core.Time.LivenessVerdict.Live : ProvenDead ? Ah.Core.Time.LivenessVerdict.ProvenDead : Ah.Core.Time.LivenessVerdict.Unknown, At, now);
}

/// <summary>
/// What the work tree of one team member looks like: its tree (the git top-level of its effective root), its stream, the live
/// writer that owns the tree, every other live member on it, the stream whose worktree it is when that is not the member's own
/// (<c>CrossStream</c>), and whether this writer shares the tree with another live writer (<c>WriterConflict</c>).
/// </summary>
public sealed record TreeFacts(
    string Name, string? Role, bool Live, string? Tree, string? Stream, string? Owner, IReadOnlyList<string> CoTenants, string? CrossStream, bool WriterConflict);

/// <summary>
/// Member liveness from registrations, with process identity. It is the one place that decides whether a session is running:
/// a pid that merely exists never counts.
/// </summary>
public interface IMemberLiveness
{
    /// <summary>
    /// The newest registration that names the member, decided by its process identity; null when no registration names the member,
    /// which leaves the member to the <c>peers.jsonl</c> rule. A member is named by its address in a team, <c>name@team</c>: the
    /// same name in another team is another member.
    /// </summary>
    MemberRegistrationLiveness? ForMember(string memberName, string team);

    /// <summary><see cref="ForMember(string, string)"/> over <paramref name="registrations"/> (status entries) instead of the mailbox's; the process is still probed now.</summary>
    MemberRegistrationLiveness? ForMember(string memberName, string team, IReadOnlyList<JsonObject> registrations) => ForMember(memberName, team);

    /// <summary>The team's life judged over <paramref name="registrations"/> (status entries) instead of the mailbox's; null when this liveness cannot judge it.</summary>
    Ah.Roster.TeamLife? TeamLifeOf(JsonValue team, IReadOnlyList<JsonObject> registrations) => null;

    /// <summary>Every registration record the mailbox holds, as the mailbox reads them (the status rebuild).</summary>
    IReadOnlyList<JsonObject> Registrations() => [];

    /// <summary>The one registration record of <paramref name="sessionId"/>, null when it has none (the status delta of a register).</summary>
    JsonObject? RegistrationOf(string sessionId) => null;

    /// <summary>
    /// Whether <paramref name="pid"/> is alive, is a harness session, and started when <paramref name="recordedStart"/> says (when it says).
    /// An unproven pid is <c>unknown</c>, never dead and never live: false here may withhold what needs proof, and may not say dead, close or sweep.
    /// </summary>
    bool PidIdentityLive(int pid, string? recordedStart);

    /// <summary>Whether <paramref name="pid"/> now belongs to a process that started at another time than <paramref name="recordedStart"/> says; false when either start is unknown.</summary>
    bool PidReused(int pid, string? recordedStart) => false;

    /// <summary>
    /// Whether the process a record names is running, under <paramref name="policy"/>, from one reading of the pid judged by the shared
    /// rules (one-liveness.md §3). Never reads registrations. This default, for implementations that observe no process, is
    /// <see cref="PidIdentityLive"/> for both policies.
    /// </summary>
    bool PidRunning(int pid, string? recordedStart, Ah.Core.Time.LivenessPolicy policy, bool claudeHarness = true) =>
        pid > 0 && PidIdentityLive(pid, recordedStart);

    /// <summary>
    /// The verdict for a member that has no registration, from its newest peers row and the process the row names. An implementation that can
    /// observe processes answers with the shared <see cref="Ah.Core.Time.LivenessRules"/>; this default knows only <see cref="PidIdentityLive"/>
    /// and <see cref="PidReused"/>.
    /// </summary>
    (Ah.Core.Time.LivenessVerdict Verdict, string Why) JudgePeerRow(Ah.Core.Time.PeerRowKind kind, long? pid, string? start, DateTimeOffset? rowAt, double? seenAgeSeconds, DateTimeOffset? lastLifeAt)
    {
        if (kind is Ah.Core.Time.PeerRowKind.Seen)
        {
            return seenAgeSeconds is { } age && age < Ah.Core.Time.LivenessRules.FreshSeconds
                ? (Ah.Core.Time.LivenessVerdict.Live, "seen recently")
                : (Ah.Core.Time.LivenessVerdict.Unknown, "last seen too long ago to say");
        }

        if (kind is not (Ah.Core.Time.PeerRowKind.Up or Ah.Core.Time.PeerRowKind.Down) || pid is not (> 0 and <= int.MaxValue))
        {
            return (Ah.Core.Time.LivenessVerdict.Unknown, "its peers.jsonl row names no process");
        }

        if (kind == Ah.Core.Time.PeerRowKind.Down && string.IsNullOrEmpty(start))
        {
            return (Ah.Core.Time.LivenessVerdict.Unknown, "its latest peers.jsonl row is down, but it names no process and start to prove it by");
        }

        if (PidIdentityLive((int)pid.Value, start))
        {
            return (Ah.Core.Time.LivenessVerdict.Live, "its process is running");
        }

        // Without a way to observe the process, a row is never proof of death: only an implementation that reads the process can say it is gone.
        return (Ah.Core.Time.LivenessVerdict.Unknown, "its peers.jsonl row is not live and the process it names cannot be observed here");
    }

    /// <summary>
    /// <see cref="JudgePeerRow(Ah.Core.Time.PeerRowKind, long?, string?, DateTimeOffset?, double?, DateTimeOffset?)"/> for a member whose harness kind is known:
    /// <paramref name="claudeHarness"/> is false for a member of another kind, whose process is not held to the claude command line. The default ignores the kind.
    /// </summary>
    (Ah.Core.Time.LivenessVerdict Verdict, string Why) JudgePeerRow(Ah.Core.Time.PeerRowKind kind, long? pid, string? start, DateTimeOffset? rowAt, double? seenAgeSeconds, DateTimeOffset? lastLifeAt, bool claudeHarness) =>
        JudgePeerRow(kind, pid, start, rowAt, seenAgeSeconds, lastLifeAt);

    /// <summary>
    /// What the shared liveness rules say of the session a pid and its recorded start name, for a caller that ends or removes something:
    /// only <see cref="Ah.Core.Time.LivenessVerdict.ProvenDead"/> may. The default can raise to Live and says Unknown otherwise, since
    /// only an implementation that observes the process can prove it gone.
    /// </summary>
    Ah.Core.Time.LivenessVerdict PidVerdict(int pid, string? recordedStart) =>
        PidIdentityLive(pid, recordedStart) ? Ah.Core.Time.LivenessVerdict.Live : Ah.Core.Time.LivenessVerdict.Unknown;
}

/// <summary>The core exports of the Node <c>lib-hier</c> and <c>lib-peer</c> that most callers share.</summary>
public interface IHierarchyCore
{
    /// <summary>
    /// Opens a scope for a reader that decides on what is absent (no owed report, an id not used, no open request, no live run). Inside
    /// it a read of a domain in <c>db</c> mode that fails on the store throws <see cref="Ah.Core.Json.StoreUnavailableException"/>
    /// instead of answering empty, so a caller cannot take "could not look" for "nothing there". Outside it a failed read answers
    /// empty, which suits display. Dispose it to leave.
    /// </summary>
    IDisposable DecisionReads();

    /// <summary>Where member liveness comes from. Unset, a pid counts as live when it is alive (the composition root sets it).</summary>
    IMemberLiveness? Liveness { get; set; }

    /// <summary>Whether a recorded pid is a live harness session: false for anything but a positive integer; with <see cref="Liveness"/> unset, <see cref="PidAlive"/>.</summary>
    bool IdentityLive(JsonValue pid, JsonValue? recordedStart);

    /// <summary>
    /// <see cref="IMemberLiveness.PidRunning"/> over a recorded pid and start: false for anything but a positive integer pid; with
    /// <see cref="Liveness"/> unset, <see cref="PidAlive"/> for both policies.
    /// </summary>
    bool PidRunning(JsonValue pid, JsonValue? recordedStart, Ah.Core.Time.LivenessPolicy policy, bool claudeHarness = true);

    MsgFilenameParts? ParseMsgFilename(string name);

    /// <summary>Whether request <paramref name="id"/> of pool <paramref name="dir"/> is the charter of a lane that is still running.</summary>
    bool IsOpenLaneCharter(string dir, string id);

    /// <summary>The ids of the requests of pool <paramref name="dir"/> that are the charter of a lane that is still running, from one read of the pool.</summary>
    IReadOnlySet<string> OpenLaneCharterIds(string dir);

    /// <summary>Prune's closed set by query (live = 0, not answered since the cutoff, not an open charter).</summary>
    IReadOnlySet<string> ClosedExchangeIds(IReadOnlyList<string> dirs, long answeredBeforeMs);

    /// <summary>The stored <c>team</c> header of each message at <paramref name="paths"/> that has a row.</summary>
    IReadOnlyDictionary<string, string?> MsgTeams(IReadOnlyList<string> paths);

    /// <summary><see cref="ListExchanges(IReadOnlyList{string})"/> narrowed to the exchanges of <paramref name="ids"/>, read in db mode by id only.</summary>
    JsonArray ListExchangesWithIds(IReadOnlyList<string> dirs, IReadOnlyList<string> ids)
    {
        var wanted = ids.ToHashSet(StringComparer.Ordinal);
        var kept = new JsonArray();
        foreach (var e in ListExchanges(dirs))
        {
            if (e.Member("id") is { IsText: true } id && wanted.Contains(id.AsString()))
            {
                kept.Add(e);
            }
        }

        return kept;
    }

    /// <summary>
    /// A page of exchange ids, newest first, below <paramref name="beforeId"/>, with a request row of a team in <paramref name="teams"/>
    /// (any when null; empty text = no team) to <paramref name="recipient"/> (any when null), with every pool's request row (recipient, stored
    /// team, pool key).
    /// </summary>
    IReadOnlyList<(string Id, string To, string? Team, string Pool)> ExchangeRowsBefore(
        IReadOnlyList<string> dirs, string? beforeId, int count, IReadOnlyList<string>? teams, string? recipient);

    /// <summary>One pool's exchanges as the listing pairs them before any filter, a response with no request included (status entries).</summary>
    IReadOnlyList<JsonObject> PoolPairs(string dir) => [];

    /// <summary>Whether the response at <paramref name="path"/> to request <paramref name="id"/> is a no-report body.</summary>
    bool ResponseIsNoReport(string path, string id) => false;

    /// <summary>The front matter fields of the message at <paramref name="path"/> as the charter rule reads them, or null.</summary>
    JsonValue? MessageFields(string path) => null;

    /// <summary>The listing's file row of the one message at <paramref name="path"/>, or null when it is no message (status deltas).</summary>
    JsonObject? FileRowOf(string path) => null;

    /// <summary>Whether pool <paramref name="dir"/> holds an answered request to a sub-orchestrator.</summary>
    bool AnyAnsweredSubOrchestrator(string dir) => false;

    /// <summary>The exchanges whose request slug is <paramref name="slug"/> in any of the pools, closed ones included.</summary>
    JsonArray ListSlugExchanges(IReadOnlyList<string> dirs, string slug) => ListExchanges(dirs, live: false);

    /// <summary>How many exchanges in the pools have the request slug <paramref name="slug"/>, closed ones included.</summary>
    int CountSlugExchanges(IReadOnlyList<string> dirs, string slug) =>
        ListExchanges(dirs, live: false).Count(e => e.Member("slug") is { IsText: true } s && s.AsString() == slug);

    /// <summary>How many exchange ids the pools hold with a request row matching <paramref name="teams"/> and <paramref name="recipient"/>.</summary>
    long ExchangeCount(IReadOnlyList<string> dirs, IReadOnlyList<string>? teams, string? recipient);

    /// <summary>
    /// The request rows a downstream list of pool <paramref name="dir"/> reads: the seeds (with <paramref name="newest"/> 0 the live
    /// scope's requests and their descendants, else the newest <paramref name="newest"/> requests) and every ancestor of them.
    /// </summary>
    (IReadOnlyList<MsgFileRow> Rows, IReadOnlySet<string> Seeds) DownstreamRequests(string dir, int newest);

    /// <summary>The key of the first <c>[hierarchy-msg …]</c> token (<see cref="ResolveMsgToken"/>); null when there is none or it does not resolve.</summary>
    string? ExtractMsgToken(string? text, string? contextDir = null, string? expectedTo = null);

    /// <summary>The key of every <c>[hierarchy-msg …]</c> token in <paramref name="text"/> that resolves, in order.</summary>
    IReadOnlyList<string> ExtractMsgTokens(string? text, string? contextDir = null, string? expectedTo = null);

    /// <summary>
    /// The first <c>[hierarchy-msg &lt;path|id&gt; [type]]</c> token resolved to its key, or null when the text has none. An id that
    /// matches more than one message is narrowed to <paramref name="contextDir"/>'s pool, then to <paramref name="expectedTo"/>.
    /// </summary>
    MsgTokenHit? ResolveMsgToken(string? text, string? contextDir = null, string? expectedTo = null);

    /// <summary>A fresh message id in the writer's local time (<c>YYYYMMDD-HHMMSS-xxxx</c>).</summary>
    string NewId();

    /// <summary><c>pidAlive</c>: false for anything but a positive integer; EPERM counts as alive.</summary>
    bool PidAlive(JsonValue pid);

    /// <summary><c>{path, text, fm, anchors}</c> for a message file; null when it cannot be read.</summary>
    JsonValue ReadMsgFile(string path);

    /// <summary><see cref="ReadMsgFile"/> for each path, in order.</summary>
    IReadOnlyList<JsonValue> ReadMsgFiles(IReadOnlyList<string> paths) => [.. paths.Select(ReadMsgFile)];

    /// <summary>Until disposed, message reads on this thread share one connection; null when there is nothing to dispose.</summary>
    IDisposable? ShareMsgReads() => null;

    /// <summary>The stored body of a sealed message, as stored; null for a draft or no row.</summary>
    /// <param name="path">The message file's path.</param>
    /// <returns>The stored bytes, or null.</returns>
    byte[]? StoredBytes(string path);

    /// <summary>The bytes a message hashes to: its stored body when sealed in <c>db</c> mode, else the file's; null when unreadable.</summary>
    /// <param name="path">The message file's path.</param>
    /// <returns>The bytes, or null.</returns>
    byte[]? MsgBytes(string path);

    /// <summary>Whether a message exists: its row in <c>db</c> mode or its file; a lagging export never reads as absent.</summary>
    /// <param name="path">The message file's path.</param>
    /// <returns>True when the message exists.</returns>
    bool MsgExists(string path);

    /// <summary>When a message was last written: a sealed row's seal time in <c>db</c> mode, else its file's mtime; null when neither is known.</summary>
    /// <param name="path">The message file's path.</param>
    /// <returns>Unix milliseconds, or null.</returns>
    double? MsgTimeMs(string path);

    /// <summary>Each message's stored text and <see cref="MsgTimeMs"/>, from one read over one connection; (null, null) for a message that is not there.</summary>
    /// <param name="paths">The messages' paths.</param>
    /// <returns>One pair per path, in order.</returns>
    IReadOnlyList<(string? Text, double? TimeMs)> ReadMsgTextsAndTimes(IReadOnlyList<string> paths);

    /// <summary>Every request in <c>&lt;dir&gt;/msgs</c> with its response and open flag, newest id first.</summary>
    JsonArray ListExchanges(string dir);

    /// <summary>
    /// The exchanges of several pools as one list, newest id first. A request id seen in more than one pool is one exchange: its
    /// request and its response are each taken from the first pool in <paramref name="dirs"/> that holds one, so a response filed
    /// in another pool than its request still pairs with it.
    /// </summary>
    JsonArray ListExchanges(IReadOnlyList<string> dirs);

    /// <summary>
    /// <see cref="ListExchanges(IReadOnlyList{string})"/>; with <paramref name="live"/> in db mode, only the exchanges that can still be open or
    /// were answered in the last few minutes: every exchange the full list has open, fewer closed ones.
    /// </summary>
    JsonArray ListExchanges(IReadOnlyList<string> dirs, bool live) => ListExchanges(dirs);

    /// <summary>The records of the peer-pending file, corrupt lines skipped and a repeated <c>op_id</c> kept once.</summary>
    JsonArray ReadPeerRecords();

    /// <summary>The records as <see cref="ReadPeerRecords"/> returns them, or null when the database that holds them could not be read.</summary>
    JsonArray? ReadPeerRecordsKnown();

    /// <summary>The same records as <see cref="ReadPeerRecords"/> without a copy of each: the caller must only read them.</summary>
    IReadOnlyList<JsonValue> PeerRecordsShared();

    /// <summary>The message store's write counter, or null when the store cannot be read.</summary>
    long? MsgsVersion();

    /// <summary>The peer log's change counter when the records are in the database, else null (file mode, or a store that cannot be read).</summary>
    long? PeerRecordsVersion();

    /// <summary>Drops the peer rows the store holds parsed, so each type set is read in full next time (the on-demand rebuild).</summary>
    void ForgetHeldPeerRows()
    {
    }

    /// <summary>Drops every held full listing, so the next read of each pool list lists it again (the on-demand rebuild).</summary>
    void ForgetListings()
    {
    }

    /// <summary>
    /// The records of <see cref="PeerRecordsShared"/> that pass <see cref="PeerRows.Matches"/> for <paramref name="query"/>, in log order,
    /// each distinct <c>op_id</c> once; the caller must only read them. In <c>db</c> mode the store is asked for the candidate rows alone,
    /// so a reader whose own test accepts only rows the query admits does not read the whole log; its answers are those it would get
    /// from the full read. A store that cannot be read answers an empty list, as the full read does.
    /// </summary>
    IReadOnlyList<JsonValue> PeerRecordsWhere(PeerQuery query);

    /// <summary>
    /// <see cref="PeerRows.LatestByKey"/> over the records whose key can belong to <paramref name="sessionId"/>, so a reader that
    /// then keeps only that session's rows gets the same answer as over the whole log, even where a session id and a task run
    /// together into the same key. In <c>files</c> mode it is the latest-by-key rows of the whole log. The caller must only read them.
    /// </summary>
    IReadOnlyList<JsonValue> PeerRecordsLatestByKeyFor(JsonValue? sessionId);

    /// <summary>
    /// The last record of <see cref="PeerRecordsShared"/> whose <c>session_id</c> is <paramref name="sessionId"/> and that has no <c>type</c>
    /// member; null when there is none. In files mode it is found by reading the peer-pending file backwards from its end, so a file
    /// nobody needs to scan whole is not parsed whole; a repeated <c>op_id</c> that could make that record a duplicate, and any other
    /// case the backward read cannot decide exactly, is answered from the full read.
    /// </summary>
    JsonValue? LastBriefRecord(string sessionId);

    /// <summary>Whether the peer records are in <c>db</c> mode, so the peer-log file is frozen; false when the mode is <c>files</c> or cannot be read.</summary>
    bool PeerRecordsInDb();

    /// <summary>Whether messages live in the store here, so a message path names no file.</summary>
    bool MessagesInDb();
}

public static partial class HierarchyModule
{
    /// <summary>What the doctor's <c>tree-ownership</c> row warns about, among live members: trees with several live writers, cross-stream members, and streams whose worktree has only cross-stream occupants.</summary>
    public static IReadOnlyList<string> TreeProblems(IReadOnlyList<TreeFacts> all)
    {
        var facts = all.Where(f => f.Live).ToList();
        var problems = new List<string>();
        foreach (var tree in facts.Where(f => f.WriterConflict && f.Tree is not null).GroupBy(f => f.Tree!))
        {
            problems.Add($"{tree.Key} has {tree.Count()} live writers: {string.Join(", ", tree.Select(f => f.Name))}");
        }

        problems.AddRange(facts.Where(f => f.CrossStream is not null).Select(f => $"{f.Name} (stream {f.Stream ?? "none"}) works in stream {f.CrossStream}'s tree"));
        foreach (var stream in facts.Where(f => f.CrossStream is not null).GroupBy(f => f.CrossStream!))
        {
            if (!facts.Any(f => f.Tree is not null && f.Stream == stream.Key && f.CrossStream is null))
            {
                problems.Add($"stream {stream.Key}'s worktree has only cross-stream occupants: {string.Join(", ", stream.Select(f => f.Name))}");
            }
        }

        return problems;
    }

    /// <summary>Whether a member of <paramref name="role"/> writes to its tree: every role but reviewer, architect, ultra-advisor and qa, and a member with no role.</summary>
    public static bool IsWriterRole(string? role) => TreeOwnership.IsWriter(role);

    /// <summary>The <c>{path, stream, owner, co_tenants, cross_stream}</c> block every surface prints for a member's tree.</summary>
    public static JsonObject TreeJson(TreeFacts facts)
    {
        var tenants = new JsonArray();
        foreach (var name in facts.CoTenants)
        {
            tenants.Add(name);
        }

        return new JsonObject
        {
            ["path"] = facts.Tree is null ? JsonValue.Null : facts.Tree,
            ["stream"] = facts.Stream is null ? JsonValue.Null : facts.Stream,
            ["owner"] = facts.Owner is null ? JsonValue.Null : facts.Owner,
            ["co_tenants"] = tenants,
            ["cross_stream"] = facts.CrossStream is null ? JsonValue.Null : facts.CrossStream,
        };
    }

    /// <summary>The one warning line for a member whose tree is shared with another live writer or is another stream's worktree; null when neither.</summary>
    public static string? TreeWarning(TreeFacts facts)
    {
        var lines = new List<string>();
        if (facts.WriterConflict)
        {
            lines.Add($"tree {facts.Tree} is shared with live {string.Join(", ", facts.CoTenants)}; a branch switch by one moves the HEAD of all. Ask the orchestrator for a worktree of your own.");
        }

        if (facts.CrossStream is not null)
        {
            lines.Add($"tree {facts.Tree} is stream {facts.CrossStream}'s worktree but you are {(facts.Stream is null ? "in no stream" : $"in stream {facts.Stream}")}. Ask the orchestrator to run `move {facts.Name} --stream {facts.CrossStream}`, or to respawn you in your own stream's tree.");
        }

        return lines.Count == 0 ? null : "Tree warning: " + string.Join(" ", lines);
    }

    /// <summary>Whether the managing team's document holds an unexpired grant, from its current orchestrator, of this close of the child team; see <see cref="CloseGreenLight"/>.</summary>
    public static GreenLightVerdict EvaluateCloseGreenLight(JsonValue manager, string stream, string verb, string childTeam, string token, DateTimeOffset now) =>
        CloseGreenLight.Evaluate(manager, stream, verb, childTeam, token, now);

    /// <summary>The address of the session a lead answers as: the Orchestrator of the lead's team, which for a lane is its sub-orchestrator.</summary>
    public static string LeadAddress(LeadRef lead) => OrchestratorAddress(lead.Team);

    /// <summary>
    /// The mailbox address of the session that orchestrates <paramref name="team"/>: the top Orchestrator for a top-level team, the
    /// sub-orchestrator that owns it for a child team. Both answer at <c>orchestrator@&lt;team&gt;</c>, so the team name is the whole input.
    /// </summary>
    public static string OrchestratorAddress(string? team) => $"orchestrator@{team}";

    /// <summary>The box of a team's chief while a deputy is on duty there and holds <see cref="OrchestratorAddress"/>.</summary>
    public static string ChiefAddress(string? team) => $"chief@{team}";

    /// <summary>Whether <paramref name="address"/> has the <c>name@team</c> shape a <c>--for</c> asker needs: one <c>@</c>, both sides non-empty, no whitespace.</summary>
    public static bool IsMemberAddress(string address) =>
        address.Split('@') is [{ Length: > 0 }, { Length: > 0 }] && address.Length <= 128 && !address.Any(char.IsWhiteSpace);

    /// <summary>
    /// Why <paramref name="askedFor"/> may not get the answer to a question of <paramref name="team"/>, or null when it may: it must be <c>name@team</c> for that
    /// same team, and <c>name</c> a row of the team's record (live or not). <paramref name="readTeam"/> reads a team record by name, null for the default team.
    /// </summary>
    public static string? AskerRefusal(string askedFor, string? team, Func<string?, JsonValue> readTeam)
    {
        ArgumentNullException.ThrowIfNull(readTeam);
        var name = team is null or "" or "default" or "_default" ? "default" : team;
        if (!IsMemberAddress(askedFor) || askedFor[(askedFor.IndexOf('@', StringComparison.Ordinal) + 1)..] != name)
        {
            return $"--for {askedFor} is not a member of team {name}";
        }

        var member = askedFor[..askedFor.IndexOf('@', StringComparison.Ordinal)];
        return readTeam(name == "default" ? null : name).Member("members") is { IsArray: true } members
            && members.AsArray().Any(m => m.Member("name") is { IsText: true } held && held.AsString() == member)
            ? null
            : $"--for {askedFor} is not a member of team {name}";
    }

    /// <summary>The manager team of the lane a lead answers for: the parent its child team records; null when the team names none.</summary>
    public static string? LaneManager(LeadRef lead, Func<string, JsonValue> team) => LeadResolver.ManagerOf(lead, team);

    /// <summary>The address that hears an event about a member: its effective lead's, or its home team's Orchestrator when it has no live lead. The one place events find their recipient.</summary>
    public static string EventAddress(string homeTeam, JsonValue memberRow, Func<string, JsonValue> team, Func<JsonValue, JsonValue?, bool> identityLive) =>
        LeadAddress(EffectiveLead(homeTeam, memberRow, team, identityLive));

    /// <summary>The name of the child team of lane <paramref name="stream"/> of team <paramref name="manager"/>.</summary>
    public static string LaneChildName(string manager, string stream) => LaneChildTeam.NameOf(manager, stream);

    /// <summary>The rank of a plan item kind: epic 3, deliverable 2, task (or none) 1; a parent link is valid only when the parent's rank is higher.</summary>
    public static int KindRank(string? kind) => ProgressFormat.KindRank(kind);

    /// <summary>The backlog rows that are planned (ah-epics E-D3): a checklist item names the row as parent and the row outranks the item.</summary>
    public static HashSet<string> PlannedBacklogKeys(IEnumerable<PlanItem> backlog, IEnumerable<PlanItem> checklist)
    {
        var byKey = backlog.GroupBy(static b => b.Item, StringComparer.Ordinal).ToDictionary(static g => g.Key, static g => g.First(), StringComparer.Ordinal);
        return checklist
            .Where(c => c.Parent is not null && byKey.TryGetValue(c.Parent, out var b) && KindRank(b.Kind) > KindRank(c.Kind))
            .Select(static c => c.Parent!)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Whether the record of team <paramref name="manager"/> has a stream entry, open or done, whose lane child is <paramref name="childTeam"/>; only the manager's side is read.</summary>
    public static bool ManagerNamesLane(string manager, JsonValue managerRecord, string childTeam) => LaneChildTeam.ManagerNames(manager, managerRecord, childTeam);

    /// <summary>Whether <paramref name="sessionId"/> is the orchestrating session of a confirmed lane of <paramref name="manager"/> whose sub-orchestrator is <paramref name="member"/>; see <see cref="LaneLink.IsLaneOrchestrator"/>.</summary>
    public static bool IsLaneOrchestrator(string manager, JsonValue managerRecord, string member, string sessionId, Func<string, JsonValue> team) => LaneLink.IsLaneOrchestrator(manager, managerRecord, member, sessionId, team);

    /// <summary>Whether <paramref name="childRecord"/> names the lane <paramref name="stream"/> of <paramref name="manager"/> as its parent: the child's side only, which a caller that must trust the answer does not use alone.</summary>
    public static bool IsLaneOf(JsonValue childRecord, string manager, string stream) => LaneLink.IsLaneOf(childRecord, manager, stream);

    /// <summary>The stream of <paramref name="managerName"/> that <paramref name="childName"/> is the child team of, or null: the child's file names the manager and a stream, the manager's file records that stream, and the child is named for it. Routing and the team view both ask this.</summary>
    public static string? ConfirmedLaneStream(string managerName, JsonValue managerRecord, string childName, JsonValue childRecord) => LaneLink.ConfirmedStream(managerName, managerRecord, childName, childRecord);

    /// <summary>Where the messages of a member go and why (<c>lead</c>, <c>dissolved-lane</c> or <c>none</c>), by the resolver the routing uses; null for a member with no lead in a lane that is not over.</summary>
    public static (string Team, string Name, string Via)? LaneRouteOf(string team, JsonValue memberRow, Func<string, JsonValue> record, Func<JsonValue, JsonValue?, bool> identityLive) => LaneLink.RouteOf(team, memberRow, record, identityLive);

    /// <summary>Adds to <paramref name="team"/> what makes it the child of a lane: its parent, its copy of the stream and a workstream status.</summary>
    public static void AddLaneChildRecords(JsonObject team, string parent, string stream, JsonValue streamRecord, string nowIso, string? parentTeamId = null) =>
        LaneChildTeam.AddRecords(team, parent, stream, streamRecord, nowIso, parentTeamId);

    /// <summary>
    /// Makes sure the lane's child team exists, empty and owned by <paramref name="owner"/>; an existing child is left as it is (re-owned
    /// instead when <paramref name="adoptOrphan"/> is set and its owner is gone), and a lane that cannot have one is refused in the result.
    /// </summary>
    public static LaneChildResult EnsureLaneChild(
        IHierarchyStateCalls calls,
        IRosterCore roster,
        JsonValue managerTeam,
        IHierarchyProgress? progress,
        string dir,
        string progressPool,
        string manager,
        string stream,
        LaneChildOwner owner,
        JsonValue teamId,
        string nowIso,
        StateCallOptions? options = null,
        bool adoptOrphan = false) =>
        LaneChildTeam.Ensure(calls, roster, managerTeam, progress, dir, progressPool, manager, stream, owner, teamId, nowIso, options, adoptOrphan);

    /// <summary>
    /// Re-owns the existing child team <paramref name="child"/> of stream <paramref name="stream"/> of <paramref name="manager"/> for
    /// <paramref name="owner"/> under the same rule <see cref="EnsureLaneChild"/> applies; the refusal, if any, is in the result.
    /// </summary>
    public static LaneChildResult AdoptLaneChild(IHierarchyStateCalls calls, IRosterCore roster, string dir, string manager, string stream, string child, LaneChildOwner owner, StateCallOptions? options = null) =>
        LaneChildTeam.Adopt(calls, roster, dir, manager, stream, child, owner, options);

    /// <summary>Whether a member row has a built-in role of the orchestrate class.</summary>
    public static bool IsOrchestrateRow(JsonValue row) => LeadResolver.IsOrchestrateRow(row);

    /// <summary>The lead a member row names, or null when it names none.</summary>
    public static LeadRef? LeadOf(JsonValue row) => LeadResolver.LeadOf(row);

    /// <summary>Who the member of <paramref name="row"/> answers to: its <see cref="ValidLead"/> when it has one, otherwise the Orchestrator of <paramref name="homeTeam"/>.</summary>
    public static LeadRef EffectiveLead(string homeTeam, JsonValue row, Func<string, JsonValue> team, Func<JsonValue, JsonValue?, bool> identityLive) =>
        LeadResolver.Effective(homeTeam, row, team, identityLive);

    /// <summary>The manager team of <paramref name="teamRecord"/> (named <paramref name="teamName"/>) when it is the dissolved child team of a lane whose stream is done; null otherwise.</summary>
    public static string? DissolvedParent(Func<string, JsonValue> team, JsonValue teamRecord, string teamName) => LeadResolver.DissolvedParent(team, teamRecord, teamName);

    /// <summary>
    /// The lead a member's row names when it is a valid one, else null. A lead is always the sub-orchestrator of a lane of the member's
    /// team: the lead's team records the member's team and a stream as its parent, the member's team records the lead as that stream's
    /// sub-orchestrator and the stream is not done, and the lead's team's Orchestrator process is live. Mesh rule 5 and the event
    /// recipient both ask this one question; <paramref name="team"/> returns a team's record (null when there is none).
    /// </summary>
    public static LeadRef? ValidLead(string homeTeam, JsonValue row, Func<string, JsonValue> team, Func<JsonValue, JsonValue?, bool> identityLive) =>
        LeadResolver.ValidLead(homeTeam, row, team, identityLive);

    /// <summary>Whether the Orchestrator process a team record names is live.</summary>
    public static bool OrchestratorLive(JsonValue teamRecord, Func<JsonValue, JsonValue?, bool> identityLive) => LeadResolver.OrchestratorLive(teamRecord, identityLive);

    public static ModuleRegistry Registry => GeneratedRegistry.Instance;

    /// <summary>The team progress a <c>progress.read</c> op result describes.</summary>
    public static TeamProgress ReadProgressTeam(JsonValue json) => ProgressJson.ReadTeam(json);

    /// <summary>The plan a <c>progress.plan</c> op result describes.</summary>
    public static PlanState? ReadProgressPlan(JsonValue json) => ProgressJson.ReadPlan(json);

    /// <summary>The stream description a <c>progress.describe</c> op result carries.</summary>
    public static ProgressStreamInfo ReadProgressInfo(JsonValue json) => ProgressJson.ReadInfo(json);

    public static IHierarchyCore CreateCore(IFileReader files, IAhEnvironment environment, IClock clock, IRandom random, IProcessInspector processes, MsgsRoute msgsRoute)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(random);
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(msgsRoute);
        return new HierarchyCore(files, environment, clock, random, processes, msgsRoute: msgsRoute);
    }

    /// <param name="files">Transcript reads.</param>
    /// <param name="environment">The home directory.</param>
    /// <param name="rows">The usage rows (b397 group 2) <see cref="IHierarchyUsage.ReadUsageRecords"/> reads; without, it reads none.</param>
    public static IHierarchyUsage CreateUsage(IFileReader files, IAhEnvironment environment, IUsageRows? rows = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(environment);
        return new UsageLib(files, environment, rows);
    }

    public static IHierarchyRecovery CreateRecovery(IFileReader files, IAhEnvironment environment, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(clock);
        return new RecoveryLib(files, environment, clock);
    }

    public static IHierarchyContext CreateContext(IFileReader files, IClock clock, ITeamFiles teams)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(teams);
        return new ContextLib(files, clock, teams);
    }

    /// <summary>A new, empty fold of a stream's plan log.</summary>
    public static IPlanFold CreatePlanFold() => new PlanFold();

    /// <summary>The exchanges of <paramref name="pools"/>, listed once and looked up by request id. <paramref name="lister"/> lists a pool list (the core's own listing when null), for the pools and for a foreign pool added to them.</summary>
    public static IExchangeIndex CreateExchangeIndex(IHierarchyCore core, IReadOnlyList<string> pools, Func<IReadOnlyList<string>, Ah.Core.Json.JsonArray>? lister = null) => new ExchangeIndex(core, pools, lister);

    /// <summary>
    /// The pool that holds the request file of <paramref name="exchange"/>, or <paramref name="fallback"/> when it has none. Anything
    /// written for the exchange goes there, whichever pool a response was found in.
    /// </summary>
    public static string HomeOf(JsonValue exchange, string fallback) =>
        exchange.Member("request") is { IsMap: true } request && request.Member("path") is { IsText: true } path
            ? Ah.Core.Text.NodePath.Dirname(Ah.Core.Text.NodePath.Dirname(path.AsString()))
            : fallback;

    /// <summary>The directory bootstrap over the raw writer of the platform layer.</summary>
    public static IHierarchyBootstrap CreateBootstrap(IRawFileWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        return new Bootstrap.RawHierarchyBootstrap(writer);
    }

    public const int ToolWriteIntervalSeconds = 15;
    public const int RosterFreshSeconds = 1800;

    /// <summary>
    /// Whether an <c>AttributedLiveness</c> answer counts as running for a display or a count: anything but proven dead, so an
    /// undecided member that has a registration or a peers row still counts. A member with neither has nothing to count.
    /// </summary>
    public static bool AttributedRunning(JsonObject attributed) =>
        attributed.Get("proven_dead") is not { IsTruthy: true }
        && (attributed.Get("live") is { IsTruthy: true } || attributed.Get("registered") is { IsTruthy: true } || attributed.Get("rec") is { IsMap: true });
    public const int SweepDays = 7;

    /// <summary>
    /// Removes the activity records older than <see cref="SweepDays"/> from each of <paramref name="dirs"/> (a dir that does not exist
    /// is skipped) and returns how many went. Age is the record file's own mtime, so a pass changes nothing unless a file is removed.
    /// </summary>
    public static int PruneActivity(IHierarchyStatus status, IEnumerable<string> dirs, double nowMs, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(dirs);
        var cutoff = nowMs - (SweepDays * 86400.0 * 1000);
        return dirs.Distinct(StringComparer.Ordinal).Sum(dir => status.SweepActivityRouted(dir, cutoff, false, log));
    }

    public const int FillBodyMax = 256 * 1024;

    /// <summary>The error code a write into a response that was closed is refused with.</summary>
    public const string RequestClosedCode = "request-closed";

    public const string RequestWithdrawnCode = "request-withdrawn";

    /// <summary>
    /// What a responder is told when it writes a body into the response of a request that was closed or withdrawn: the result was not
    /// recorded, and where to send it instead. Null when the response is neither. <paramref name="field"/> reads a frontmatter value.
    /// </summary>
    public static (string Code, string Message)? WriteRefusal(string id, Func<string, string?> field, string time)
    {
        if (field("closed") == "true")
        {
            return (RequestClosedCode, RequestClosedNotice(id, field("closed_by"), field("closed_reason"), time, field("team")));
        }

        if (field("withdrawn") != "true")
        {
            return null;
        }

        var sender = field("to_name") is { Length: > 0 } named ? named : field("to");
        return (RequestWithdrawnCode, $"request {id} was withdrawn by {(string.IsNullOrEmpty(sender) ? "its sender" : sender)} at {time}; your result was not recorded.{SendHint(sender, field("team"))}");
    }

    /// <summary>What a responder is told when it writes into the response of a request that was closed.</summary>
    public static string RequestClosedNotice(string id, string? closer, string? reason, string time, string? team = null) =>
        $"request {id} was closed by {(string.IsNullOrEmpty(closer) ? "its closer" : closer)} ({(string.IsNullOrEmpty(reason) ? "no reason given" : reason)}) at {time}; your result was not recorded.{SendHint(closer, team)}";

    private static string SendHint(string? who, string? team)
    {
        var address = who is not { Length: > 0 } ? null
            : who.Contains('@', StringComparison.Ordinal) ? who
            : team is { Length: > 0 } ? who + "@" + team : null;
        return address is null ? string.Empty : $" Send it with ah msg send {address} --kind note, or ask {address} to reopen.";
    }

    public const int LandedQuietMs = 120 * 1000;

    /// <summary>
    /// How long a recorded state is believed. Used for <c>blocked</c>: a session that died while parked on a question leaves it behind,
    /// and past this age a session not known to be live reads as idle. Also used by the delivery ladder for any recorded state, which holds
    /// a refused paste to a busy member only while its activity record is this fresh.
    /// </summary>
    public const int BlockedStaleMs = 30 * 60 * 1000;
    public const int DecisionItemCap = 4;
    public const int DecisionRunCap = 20;
    public const int DecisionInputMax = 64 * 1024;
    public static readonly IReadOnlyList<string> MergeMethods = ["merge", "squash", "rebase"];
    public const int MaxNudges = 2;
    public const double SubagentInflightTtlMs = 60 * 60 * 1000;
    public const string PipelineAnchorSlug = "pipeline-run-anchor";

    /// <summary>
    /// The name a dispatch is listed under: the request's <c>to_name</c>, else the member part of the address it was sent to (a request made
    /// for a role and then sent to one member has no <c>to_name</c>), else <c>(unnamed)</c>. The queue and its last-dispatch entries share it.
    /// </summary>
    public static string DispatchName(string? toName, string? toAddr)
    {
        if (!string.IsNullOrEmpty(toName))
        {
            return toName;
        }

        var at = toAddr?.IndexOf('@', StringComparison.Ordinal) ?? -1;
        var member = toAddr is null ? string.Empty : at < 0 ? toAddr : toAddr[..at];
        return member.Length > 0 ? member : "(unnamed)";
    }

    /// <summary>The grant a run anchor's body states (<c>surrogate</c> or <c>user</c>); null for no line, several, or any other value.</summary>
    public static string? AnchorAuthorityMode(string body) => AnchorAuthority.ModeOf(body);

    /// <summary>The grant this session's latest Authority answer in an asks log maps to; null when it recorded none. Other sessions' rows are ignored.</summary>
    public static string? RecordedAuthority(string asksText, string? sessionId, string? afterIso, string? anchorCreatedIso) =>
        AnchorAuthority.RecordedMode(asksText, sessionId, afterIso, anchorCreatedIso);

    /// <summary>
    /// The grant a run anchor holds now: its body's stated mode, except that <c>surrogate</c> stands only while the session's recorded
    /// Authority answer confirms it, and otherwise reads as <c>user</c>.
    /// </summary>
    public static string? EffectiveAuthority(string anchorText, string asksText, string? sessionId, string? afterIso)
    {
        var mode = AnchorAuthorityMode(anchorText);
        return mode == "surrogate" && RecordedAuthority(asksText, sessionId, afterIso, CreatedOf(anchorText)) != "surrogate" ? "user" : mode;
    }

    /// <summary>The <c>created:</c> instant of a message file's front matter, as written; null when it has none.</summary>
    public static string? CreatedOf(string messageText) => AnchorAuthority.CreatedOf(messageText);

    /// <summary>The <c>team:</c> value of a message file's front matter; null when it has none.</summary>
    public static string? TeamOf(string messageText) => AnchorAuthority.TeamOf(messageText);

    /// <summary>Why a run anchor body may not replace <paramref name="previous"/> (null for a new anchor), given the recorded grant; null when it may.</summary>
    public static string? AnchorAuthorityRefusal(string body, string? previous, string? recorded) => AnchorAuthority.Refusal(body, previous, recorded);
    public const string PeersFile = "peers.jsonl";

    /// <summary>The audit file, beside the pool's other logs, where the AskUserQuestion class gate records each decision.</summary>
    public const string AsksGateFile = HierLib.AsksGateFile;

    public static IReadOnlyList<string> MsgTypes => HierLib.MsgTypes;

    public static IReadOnlyList<string> Reasons => HierLib.Reasons;

    public static IReadOnlyList<string> Etas => HierLib.Etas;

    public static IReadOnlyList<string> RequestKeys => HierLib.RequestKeys;

    public static IReadOnlyList<string> ResponseKeys => HierLib.ResponseKeys;

    /// <summary>The seconds a dispatch of each eta may run before its first check-in is due.</summary>
    public static IReadOnlyList<KeyValuePair<string, int>> EtaThresholdSeconds { get; } =
        [new("small", 5 * 60), new("medium", 10 * 60), new("large", 20 * 60)];

    /// <summary>The member's verdict from the evidence gathered about it: gone, failed, blocked, stalled-after-compact, working, waiting, idle or unknown.</summary>
    public static string MemberVerdict(MemberEvidence evidence) => HierLib.MemberVerdictOf(evidence);

    /// <summary>Whether a verdict means the member is occupied for the delivery ladder's busy hold: working, waiting on background work, or parked on a question or permission prompt. Ah.Watch's overdue targets and the release gate keep their own narrower working/waiting tests.</summary>
    public static bool IsBusy(string? verdict) => verdict is "working" or "waiting" or "blocked";

    /// <summary>The verdict of the member an activity record belongs to; see <see cref="MemberVerdict"/>. A record that is not a map gives <c>unknown</c>.</summary>
    public static string RecordVerdict(JsonValue record, double nowMs, string? tag = null, bool dispatchOpen = false, bool codex = false, bool? live = true, double? debounceMs = null) =>
        HierLib.RecordVerdictOf(record, nowMs, tag, dispatchOpen, codex, live, debounceMs);

    /// <summary>The activity a session's own hook event signals, by hook event name.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> SelfState { get; } =
        [new("UserPromptSubmit", "working"), new("Stop", "idle"), new("Notification", "blocked")];
    public const string NoteRule = "A peer note is news: it never closes a dispatch, and it needs no reply.";

    /// <summary>Check-in gaps as multiples of a dispatch's eta threshold: the first is due after the threshold, the second half of it after that, and every later one a threshold after the previous.</summary>
    public static IReadOnlyList<double> CheckinCadence { get; } = [1, 0.5, 1];

    /// <summary>How long after the clock base a member proven busy holds its CHECK-IN and SILENT back, whatever the eta: past it the alarm fires once anyway.</summary>
    public const double BusyCeilingSeconds = 120 * 60;

    /// <summary>
    /// Makes the status refresh that follows a write in <paramref name="services"/> run at most once per <paramref name="gap"/> for each
    /// hierarchy directory, with the last write always followed by a refresh. Only a long-lived process should call this: a refresh
    /// that comes within the gap is run by a worker thread after it. Disposing the result puts the refresh back to running at once.
    /// </summary>
    public static IDisposable CoalesceStatusRefresh(HierarchyServices services, TimeSpan gap)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.Status is HierLib lib ? lib.CoalesceStatusRefresh(gap) : throw new ArgumentException("the status service is not the library's own", nameof(services));
    }

    /// <summary>The QA queue over the store of <paramref name="services"/>; its limits are read once, now.</summary>
    public static IHierarchyQa CreateQa(HierarchyServices services, QaDependencies dependencies)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(dependencies);
        return services.Peers is HierLib lib ? new QaService(lib, dependencies) : throw new ArgumentException("the peers service is not the library's own", nameof(services));
    }

    /// <summary>Every job of the QA store of <paramref name="services"/>, oldest first, read only: no stale pass, no write.</summary>
    public static IReadOnlyList<QaJob> QaJobs(HierarchyServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.Peers is HierLib lib
            ? [.. new HierQaStore(lib).Read().Entries.OrderBy(static e => e.Created).Select(QaService.Job)]
            : throw new ArgumentException("the peers service is not the library's own", nameof(services));
    }

    /// <summary>The peer-log seam answered from this process's own read of the peer log.</summary>
    public static IPeerLatest CreateLocalPeerLatest(IHierarchyPeers peers)
    {
        ArgumentNullException.ThrowIfNull(peers);
        return new LocalPeerLatest(peers);
    }

    /// <summary>The libraries of the hierarchy runtime directory, built from <paramref name="environment"/>.</summary>
    public static HierarchyServices Create(HierarchyEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var lib = new HierLib(environment);
        var peerRecordsState = Ah.Core.Text.AhPaths.UserEntry(environment.Environment.HomeDirectory, Ah.Core.Text.AhPaths.PeerRecordsStateDir);
        var usage = new UsageLib(environment.Files, environment.Environment, environment.UsageRows);
        var stateCalls = new StateCalls.HierarchyStateCalls(lib, environment.Writer, environment.Roster, environment.Environment);
        return new HierarchyServices(
            lib.Core,
            lib,
            lib,
            lib,
            lib,
            lib,
            [
                new MsgsDomain(lib),
                new PeersDomain(lib),
                new GatesDomain(lib),
                new PeerRecordsDomain(lib, peerRecordsState),
                new QaDomain(lib, Ah.Core.Text.AhPaths.UserEntry(environment.Environment.HomeDirectory, Ah.Core.Text.AhPaths.QaStateDir)),
                new DecisionsDomain(lib),
                new ActivityDomain(lib),
                new JobsDomain(lib),
                new StatusDomain(lib),
                new SessionPidsDomain(lib),
                new KnownDirsDomain(lib),
                new ProgressDomain(lib),
                new CheckpointDomain(lib),
                new UsageDomain(usage),
                new QaLogDomain(lib),
            ],
            new StatusRefresher(lib),
            stateCalls,
            new ProgressRecords(environment.Files, environment.Clock, stateCalls, environment.Roster, lib.Core, environment.Errors, () => environment.Environment.HomeDirectory, environment.ProgressLogs, environment.ProgressDocs));
    }
}
