using Ah.Core.Json;
using Ah.Hierarchy;

namespace Ah.Watch;

/// <summary>One reading of <c>herdr agent list</c>: <c>Agents</c> is null when it could not be read, and <c>Reason</c> says why.</summary>
public sealed record HerdrReading(JsonArray? Agents, string? Reason);

/// <summary>What a session's activity record says about when its state began and which minutes it was busy in.</summary>
/// <param name="StateSince">The ISO instant the recorded activity last changed, or null when the record predates the field.</param>
/// <param name="Minutes">UTC epoch minutes with busy activity, ascending, at most the last 60.</param>
/// <param name="ContextUsed">The tokens the record's context reading says the session holds, or null when it carries none.</param>
/// <param name="Activity">The recorded activity (<c>working</c>, <c>idle</c>, <c>blocked</c>, <c>failed</c>), or null.</param>
/// <param name="ContextModel">The model the record's context reading names, or null.</param>
/// <param name="ContextAt">When the context reading was taken, or null.</param>
public sealed record ActivityHistory(string? StateSince, IReadOnlyList<long> Minutes, long? ContextUsed = null, string? Activity = null, string? ContextModel = null, string? ContextAt = null);

/// <summary>One subagent run the in-flight records hold.</summary>
/// <param name="SessionId">The session that started it.</param>
/// <param name="AgentId">The subagent's id.</param>
/// <param name="Status"><c>started</c> while running, <c>stopped</c> once a stop was recorded, <c>lapsed</c> when it started more than an hour ago and no stop was recorded.</param>
/// <param name="StartedAt">The ISO instant of the start record.</param>
/// <param name="EndedAt">The ISO instant of the stop record, or null.</param>
/// <param name="AgentType">The subagent type the run was started as, as its start payload named it, or null when the record has none.</param>
public sealed record SubagentRun(string SessionId, string AgentId, string Status, string StartedAt, string? EndedAt, string? AgentType = null);

/// <summary>The leading text of a file a mail entry names.</summary>
/// <param name="Text">The file's text decoded as UTF-8, at most the cap in bytes, not yet cleaned for a terminal.</param>
/// <param name="Truncated">True when the file held more than the cap.</param>
public sealed record FileText(string Text, bool Truncated);

/// <summary>
/// What the view builder reads, as the oracle's own readers: the registrations, the team files, the status computation,
/// the mailbox counts, the pending joins, the links and the mesh policy of a team, and a member's placement. The daemon
/// composes an implementation from the services that own each one; the builder only reads.
/// </summary>
public interface IViewSource
{
    /// <summary>The hierarchy directories named by every registration file, live or stale.</summary>
    IReadOnlyList<string> RegisteredHierarchyDirs();

    /// <summary>Drops the held registered-dir list, so the next read derives it again (the on-demand rebuild).</summary>
    void ForgetHeldDirs()
    {
    }

    bool DirExists(string path);

    /// <summary>The path with its symlinks resolved, or the path itself when that is not possible.</summary>
    string RealPath(string path);

    /// <summary>The named teams of a hierarchy directory (the default team is not among them).</summary>
    IReadOnlyList<string> TeamNames(string dir);

    /// <summary>A team file's document, or null; a null name is the default team.</summary>
    JsonObject? ReadTeam(string dir, string? name);

    string TeamPath(string dir, string? name);

    string BoxDirFor(string teamFile, string member);

    bool PidAlive(long pid);

    /// <summary>
    /// Whether <paramref name="pid"/> is alive and, when a start time was recorded for it and the process table can name the start
    /// of the process now holding it, that process started when recorded. With no recorded start or no table this is <see cref="PidAlive"/>.
    /// </summary>
    bool OrchestratorAlive(long pid, string? recordedStart);

    /// <summary>
    /// How <paramref name="recordedStart"/> stands against the process now holding <paramref name="pid"/>:
    /// <see cref="Ah.Core.Time.StartMatch.Unknown"/> when the process is gone, its start cannot be read, or there is no process table.
    /// </summary>
    Ah.Core.Time.StartMatch ProcessStartMatch(long pid, string recordedStart) => Ah.Core.Time.StartMatch.Unknown;

    /// <summary>
    /// Reads the registrations once and returns the lookup of a session id to what its registration records: its pid, and the nearest
    /// ancestor of its registered working directory that holds a <c>.git</c> entry. Either is null when unknown. When several
    /// registrations name one session, the live one is used, else the last listed. Reads only.
    /// </summary>
    Func<string, (long? Pid, string? CheckoutRoot, string? Cwd)> RegistrationIndex();

    /// <summary>
    /// The sessions recorded for the directory in the <c>session_pids</c> table whose process is alive, as session id and pid: the
    /// latest row of each session. Sessions that registered with no Team are found only here.
    /// </summary>
    IReadOnlyList<KeyValuePair<string, long>> LiveSessions(string dir);

    /// <summary>
    /// Whether the directory's repository has ah2 enabled in its config. The status document's <c>enabled</c> is a view
    /// override and says yes for every directory, so it cannot answer this.
    /// </summary>
    bool IsEnabled(string dir);

    /// <summary>Whether the team's Orchestrator is provably running: <c>live</c>, <c>orphaned</c>, or <c>unknown</c> when the process table cannot say.</summary>
    string TeamState(JsonObject doc);

    /// <summary>The live status document of a hierarchy directory (<c>computeStatus(dir, now, dir, true)</c>).</summary>
    JsonObject ComputeStatus(string dir, double nowMs);

    /// <summary>
    /// The team summaries the read model has published, the ones the web reads, by slice id (<c>team-summary:&lt;dirId&gt;/&lt;team&gt;</c>);
    /// null when there is no read model. One published read, no state read.
    /// </summary>
    IReadOnlyDictionary<string, JsonObject>? PublishedTeamSummaries() => null;

    /// <summary>The one sanitizer for anything that came from a file or an agent; an empty result is returned as "".</summary>
    string Clean(string text, int cap);

    /// <summary>The unacked entries of a box and how many of them are overdue at <paramref name="nowMs"/>; throws for an unreadable box.</summary>
    (int Unacked, int Overdue) BoxCounts(string boxDir, double nowMs);

    IReadOnlyList<JsonObject> PendingJoins(string dir, string? team, double nowMs);

    /// <summary><c>{accept, deny, ...}</c> command texts of a join id.</summary>
    JsonObject JoinDecisionCommands(string id);

    /// <summary>A team's links; throws when the links file is unreadable.</summary>
    IReadOnlyList<JsonObject> Links(string dir, string team, double nowMs);

    double LinkLapseMs(JsonObject link);

    /// <summary>The mesh policy name of a team document, or null.</summary>
    string? MeshPolicy(JsonObject teamDoc);

    /// <summary><c>{transport, pane, pasteable, reason}</c> of a member row on a team's transport.</summary>
    JsonObject Placement(JsonObject memberRow, string? transport);

    /// <summary>The stream rules (<c>streamsOf</c>, members, statuses, glyph state, facts, open exchanges).</summary>
    IStreamRules Streams { get; }

    /// <summary>
    /// The <c>state_since</c> (ISO instant) and <c>minutes</c> (UTC epoch minutes with busy activity, ascending) of a session's
    /// activity record in a hierarchy directory, or null when the record is absent. A record written before either field existed
    /// carries neither. Reads only.
    /// </summary>
    ActivityHistory? ActivityOf(string dir, string sessionId) => null;

    /// <summary>
    /// The newest <paramref name="limit"/> entries of a box, oldest first. Each is <c>{id, kind, from, to, created, acked, ref,
    /// changed, spec}</c>: <c>changed</c> is true when the file <c>ref</c> names no longer hashes to the digest recorded when the
    /// entry was sent, and <c>spec</c> is the first <c>.claude/ah2/specs/*.md</c> path the file's text names. With
    /// <paramref name="index"/> the entries carry only <c>{id, kind, from, to, created, acked, ref}</c> and no request file is read,
    /// which is what a read that runs often or over many boxes uses. A box that cannot be read gives none. Reads only.
    /// </summary>
    IReadOnlyList<JsonObject> BoxEntries(string boxDir, int limit, bool index = false) => [];

    /// <summary>
    /// The first <paramref name="maxBytes"/> bytes of the regular file at <paramref name="path"/> as text, or null when it is not a regular
    /// file (a link or a directory), is outside the message store (<see cref="IsMessageFile"/>), is missing or cannot be read. Reads only.
    /// </summary>
    FileText? RequestText(string path, int maxBytes) => null;

    /// <summary>
    /// Whether <paramref name="path"/> names a file directly inside a hierarchy <c>msgs</c> directory (or its <c>archive</c>), the
    /// directory resolved through any links, so a mail entry cannot make the text document serve a file from elsewhere. The file itself
    /// need not exist.
    /// </summary>
    bool IsMessageFile(string path) => false;

    /// <summary>One entry of a box by its id, shaped as <see cref="BoxEntries"/> shapes them, or null when the box has none. Reads only.</summary>
    JsonObject? BoxEntry(string boxDir, string id, bool index = false) => null;

    /// <summary>
    /// The subagent runs the in-flight records hold, oldest start first, as of <paramref name="nowMs"/>. Only records written with kind
    /// <c>subagent</c> count: a background task or a record written before kinds existed is not one. Reads only.
    /// </summary>
    IReadOnlyList<SubagentRun> SubagentRuns(double nowMs) => [];

    /// <summary>
    /// A short text that differs whenever a box gained an entry or had one acknowledged since it was last asked for, without reading
    /// the box: the size and mtime of its entry index and the mtime of its acknowledgement directory. A source that cannot tell
    /// answers the same text always.
    /// </summary>
    string BoxStamp(string boxDir) => string.Empty;

    /// <summary>The open exchanges of a team (or of every team) with the names they carry, as the stream rules list them.</summary>
    Ah.Core.Json.JsonArray OpenExchanges(string dir, string? team, bool created) => Streams.OpenTeamExchanges(dir, team, false, created);

    /// <summary>A short text that differs whenever the file at <paramref name="path"/> was rewritten, from its size and mtime alone.</summary>
    string FileStamp(string path) => string.Empty;

    /// <summary>
    /// The dispatch candidates of an Orchestrator session, as the watch reads them (the same read, not a second one), open ones
    /// and landed ones alike. Reads only.
    /// </summary>
    IReadOnlyList<DispatchCandidate> OpenDispatches(string sessionId, double nowMs) => [];

    /// <summary>The persisted peer-record rows whose <c>type</c> is one of <paramref name="types"/>, oldest first. The one peer-row read of a detail build. Reads only.</summary>
    IReadOnlyList<JsonObject> PeerRows(IReadOnlySet<string> types) => [];
}
