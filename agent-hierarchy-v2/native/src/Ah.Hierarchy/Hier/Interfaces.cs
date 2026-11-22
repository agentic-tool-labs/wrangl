using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.Hierarchy;

/// <summary>One session-pids row: the session that the hook recorded for a pid, and when.</summary>
public sealed record SessionPidRow(string SessionId, DateTimeOffset At);

/// <summary>Creates the layout of a hierarchy directory (<c>msgs/</c>, <c>msgs/archive/</c>, <c>specs/</c>) and its <c>.gitignore</c>.</summary>
public interface IHierarchyBootstrap
{
    /// <summary>Idempotent and lock-free; an existing <c>.gitignore</c> is kept. Returns <paramref name="dir"/>.</summary>
    string EnsureHierarchyDir(string dir);
}

/// <summary>Message files, exchanges, tokens and reports of the hierarchy runtime directory (the message half of <c>lib-hier</c>).</summary>
public interface IHierarchyMessages
{
    /// <summary>The team-file guide the plugin ships, which every message that names a team file points at.</summary>
    string TeamGuide { get; }

    /// <summary>
    /// The message pools the exchanges of <paramref name="teams"/> live in: <paramref name="dir"/> first, then the pool of each
    /// member's recorded work root and of each of its live extra grants, of each stream work tree, and of each tree in
    /// <paramref name="trees"/>. A pool is listed once; one that does not exist or has no <c>msgs</c> directory is left out.
    /// </summary>
    IReadOnlyList<string> PoolDirs(string dir, IReadOnlyList<string> teams, IReadOnlyList<string>? trees = null);

    /// <summary>A team file's recorded pools, home first, de-duplicated by real path; empty for a missing or unusable team file.</summary>
    IReadOnlyList<string> Family(string teamFile);

    /// <summary>The first matching message as {path, pool, team_file}, or null; ordered team families precede fallback pools.</summary>
    JsonValue Locate(string id, string type, IReadOnlyList<string> teamFiles, IReadOnlyList<string> fallbackDirs);

    /// <summary>An allowed {pool, team_file}, refused {reason}, or no-team result, discriminated by status.</summary>
    JsonObject ResponsePool(string requestPath);

    /// <summary>The hierarchy directory that holds the team file a message to <paramref name="toName"/> (or, failing a holder, to <paramref name="team"/>) names, or null when no team file applies. Every message of a team is filed in this pool.</summary>
    string? TeamHome(string dir, string? toName, string? team);

    /// <summary>
    /// The git work trees <see cref="PoolDirs"/> is made from, in the order it meets them: the checkout whose hierarchy directory
    /// <paramref name="dir"/> is, each member's recorded root, the extra trees a grant allows, the team streams' work trees, then
    /// <paramref name="trees"/>. Each is reduced to its git top and listed once by real path. A tree that does not exist, or whose
    /// pool has no <c>msgs</c> directory, is still listed.
    /// </summary>
    IReadOnlyList<string> Trees(string dir, IReadOnlyList<string> teams, IReadOnlyList<string>? trees = null);

    /// <summary>The pool a request file sits in and, when the request names a team, the pools of that team's members and streams.</summary>
    IReadOnlyList<string> PoolsOfRequest(string requestPath);

    /// <summary>The exchanges of every pool <see cref="PoolDirs"/> lists, as <see cref="IHierarchyCore.ListExchanges(IReadOnlyList{string})"/> returns them.</summary>
    JsonArray ListExchangesAcross(string dir, IReadOnlyList<string> teams, IReadOnlyList<string>? trees = null);

    /// <summary>The exchanges of every pool of every team recorded in <paramref name="dir"/>, for a reader that is not scoped to one team.</summary>
    JsonArray ListExchangesAllTeams(string dir);

    /// <summary><see cref="ListExchangesAcross"/> in the live scope (<see cref="IHierarchyCore.ListExchanges(IReadOnlyList{string}, bool)"/>): every exchange it has open, fewer closed ones.</summary>
    JsonArray ListLiveExchangesAcross(string dir, IReadOnlyList<string> teams) => ListExchangesAcross(dir, teams);

    /// <summary><see cref="ListExchangesAllTeams"/> in the live scope.</summary>
    JsonArray ListLiveExchangesAllTeams(string dir) => ListExchangesAllTeams(dir);

    /// <summary>Prune's closed set over every team pool of <paramref name="dir"/> by query.</summary>
    IReadOnlySet<string> ClosedExchangeIdsAllTeams(string dir, long answeredBeforeMs);

    /// <summary>Whether the response to <paramref name="requestId"/> has been filed in any of <paramref name="poolDirs"/>: a response file whose body is no longer the template <c>ah msg new</c> writes.</summary>
    bool ResponseFiled(IReadOnlyList<string> poolDirs, string requestId);

    /// <summary>A request's eta as one of <c>small|medium|large</c>; an absent or unrecognised one reads as <c>small</c>.</summary>
    string EtaOf(JsonValue? eta);

    /// <summary>The check-in threshold of an eta, in seconds.</summary>
    double ThresholdFor(JsonValue? eta);

    string MsgFilename(JsonValue? parts);

    string MsgsDir(string dir);

    string ArchiveDir(string dir);

    string SpecsDir(string dir);

    string PeersPath(string dir);

    string GatesPath(string dir);

    JsonArray ReadJsonl(string path);

    string LocalIso(DateTimeOffset? now = null);

    string FmtAge(JsonValue? seconds);

    double AgeSecOf(JsonValue? timestamp, double? now = null);

    /// <summary><c>{fields, end}</c> for text with a closed frontmatter fence, else null.</summary>
    JsonValue ParseFrontmatter(JsonValue? text);

    JsonArray IndexAnchors(string text);

    string? MessageHome(string messagePath, JsonValue? frontmatter);

    /// <summary><c>{path, meta}</c> of the message with that id and type in the pool, else null.</summary>
    JsonValue FindByIdAndType(string dir, string id, string type);

    /// <summary>
    /// The messages of the pool <paramref name="pool"/>, of the types in <paramref name="kinds"/> (every type when null), with those of
    /// <c>msgs/archive</c> when <paramref name="includeArchive"/> is set: the rows in db mode, the files otherwise. Each is
    /// <c>{path, meta: {id, to, slug, type}, archived}</c>; live before archived, then by id, type, recipient and slug.
    /// </summary>
    JsonArray ListMessages(string pool, bool includeArchive, IReadOnlyList<string>? kinds);

    /// <summary><c>orchestrator</c> plus every registry role of a cwd: what <c>--to</c> and <c>--from</c> accept.</summary>
    JsonArray MsgRoles(string? cwd);

    bool IsSlug(string value);

    bool IsMessageId(string value);

    /// <summary>Creates a message file through the state writer. Throws with a one-line message on bad arguments, writing nothing.</summary>
    JsonValue CreateMessage(string dir, JsonObject options);

    /// <summary>
    /// Retires an open request by writing a response marked <c>closed: true</c> where a reply would go. No caller proof is checked here: the
    /// caller decides who may close. <paramref name="by"/> names the closer in the body. Returns <c>closed</c>/<c>response</c>, plus
    /// <c>already</c> when the exchange was already closed or withdrawn, or <c>refused: "already_reported"</c> (nothing written) when it has a real report.
    /// </summary>
    JsonObject CloseExchange(string dir, JsonValue exchange, string requestPath, string reason, string? note, string by, string cwd);

    /// <summary>The request context of a write into pool <paramref name="dir"/>: its checkout's context, with <paramref name="dir"/> as the hierarchy dir.</summary>
    RequestContext PoolContext(string dir);

    /// <summary>Where the response to <paramref name="requestPath"/> lands and the frontmatter it needs, or null.</summary>
    JsonValue ResponsePlan(JsonValue? requestPath);

    string ResponseFrontmatter(JsonObject fields, DateTimeOffset? now = null);

    /// <summary>The checks of a fill and the new text: <c>{path, content}</c>. Throws with nothing written.</summary>
    JsonValue FillPlan(JsonObject args);

    /// <summary>
    /// Stores the request or response at <paramref name="path"/> as a sealed message before a pointer to it is sent; it does nothing for
    /// any other file. Throws when the message cannot be sealed.
    /// </summary>
    void SealForSend(string path);

    /// <summary>Whether <paramref name="text"/> holds anything beyond the generated skeleton of a message, judged from the text alone.</summary>
    bool HasAuthoredContent(string text);

    /// <summary>The root of <paramref name="startId"/>'s parent chain: <c>{id, fm}</c>, or null when it never reaches a root.</summary>
    JsonValue RootOf(IReadOnlyDictionary<string, JsonValue> byId, string startId);

    /// <summary>
    /// Whether the <c>parent</c> chain of the message file <paramref name="messagePath"/> (the file itself included) reaches the request
    /// <paramref name="taskId"/>, looking in the pools of the message's team. A file or task that cannot be read does not reach.
    /// </summary>
    bool ChainReaches(string messagePath, string taskId, string dir, string team);

    JsonArray ListDownstreamDispatches(string dir);

    /// <summary>The dispatches of the live scope (<paramref name="newest"/> 0) or of the newest <paramref name="newest"/> requests.</summary>
    JsonArray ListDownstreamDispatches(string dir, int newest) => ListDownstreamDispatches(dir);

    /// <summary>Open exchanges; a <paramref name="team"/> of null (undefined) keeps every one, a JSON null or a name keeps that team's.</summary>
    JsonArray OpenExchanges(string dir, JsonValue? team, JsonArray? exchanges = null);

    bool PipelineRunLive(string? cwd);

    /// <summary>Archives closed exchanges older than <paramref name="days"/> and deletes stale activity records; the number of pairs moved.</summary>
    int Sweep(string dir, double? days = null, double? now = null, bool hook = false);


    double ExchangeAgeSec(JsonValue exchange, double? now = null);

    /// <summary>The title of an exchange from its request file, as <c>msgs.read</c> gives it: the frontmatter title, else the goal line, else the first heading of the first 4096 characters of the body; both null when there is none.</summary>
    (string? Title, string? Source) ExchangeTitle(JsonValue exchange);

    /// <summary>A <c>--title</c> a person can read, made from the first line of free <paramref name="text"/> that is not a message-file pointer; null when none can be made.</summary>
    string? AutoTitle(string text);

    /// <summary>Why a <c>--title</c> value would be refused, or null when it would be stored.</summary>
    string? TitleRefusal(string raw);

    JsonValue ValidateRequestToken(JsonValue? text, string dir, JsonValue? expectedTo);

    JsonValue ValidateResponseToken(JsonValue? text, string dir, JsonValue? expectedFrom, JsonValue? expectedId, JsonValue? requestPath = null);

    string RealCwd(string path);

    JsonArray MissingIntent(JsonValue parsed);

    JsonValue ReviewIntentGap(string path, JsonObject? resolved);

    string ReviewIntentReason(JsonValue? path, JsonArray missing, JsonValue? again);

    string ReportStatus(string path, string id);

    JsonArray OutcomeTokens(JsonValue? text);

    string FirstSection(JsonValue? text);

    JsonValue StatusFollowUp(JsonValue? text);

    string FollowUpRule(JsonValue? status, JsonValue? id);

    JsonArray FollowUpRules(JsonValue? options);

    JsonValue ReportFollowUp(JsonValue? path);

    bool HasResponseToken(JsonValue? text, JsonValue? id);

    bool ResponseLanded(string path, double? now = null);

    /// <summary><see cref="ResponseLanded"/>, or null when the response could not be read: a failed read, not an answer (b459 step 25b).</summary>
    bool? TryResponseLanded(string path, double? now = null) => ResponseLanded(path, now);

    /// <summary>Whether the response file holds authored content and is no withdrawal, without waiting for the file to be quiet.</summary>
    bool ResponseAuthored(string path);

    /// <summary>
    /// Whether a response created or filled beside its request may lie in another pool than the request context names: <c>req</c>
    /// is an existing request file in the same msgs/ dir as <c>path</c>, with the response's id.
    /// </summary>
    bool PathException(string domain, string op, JsonValue args);
}

/// <summary>Gates, routing, the peer roster, liveness and the HIERARCHY STATE block (the roster half of <c>lib-hier</c>).</summary>
public interface IHierarchyRoster
{
    /// <summary>Drops every dir's held gate rows, so the next read of each is in full (the on-demand rebuild).</summary>
    void ForgetGateHolds()
    {
    }

    /// <summary>Drops every dir's held latest roster, so the next read parses the rows again (the on-demand rebuild).</summary>
    void ForgetLatestRosters()
    {
    }

    /// <summary>The newest time on the activity record of <paramref name="sessionId"/> or of the pane of member <paramref name="name"/> (<c>at</c>, <c>tool_at</c>, <c>working_at</c>); null when there is no record.</summary>
    DateTimeOffset? ActivityLifeOf(string dir, string? sessionId, string name);

    /// <summary>The tree facts of every member of <paramref name="team"/>, from its team file, its work-root grants and the live registrations; empty when the team has no file.</summary>
    IReadOnlyList<TreeFacts> TreeFactsOfTeam(string dir, string team);

    JsonArray ReadGates(string dir);

    /// <summary>As <see cref="ReadGates"/>, but null, with the cause in <paramref name="why"/>, when the database cannot be read, so a caller can tell a failed read from no rows.</summary>
    JsonArray? ReadGatesOrNull(string dir, out string? why);

    bool HasGate(string dir, Func<JsonValue, bool> predicate);

    /// <summary>Appends one record to the gate log (<c>gates.jsonl</c>) through the state writer, on the hook budget; a failed write throws.</summary>
    void AppendGateRouted(string dir, JsonObject record);

    JsonValue SessionRouteRecord(string dir, JsonValue? sessionId);

    void RecordRoute(string dir, JsonValue sessionId, JsonValue value);

    /// <summary>The dispatch route in effect, <c>{value, source}</c>: the session's answer, else the config's, else <c>peers</c>.</summary>
    JsonObject EffectiveRoute(string dir, JsonObject? resolved, JsonValue? sessionId);

    string? RoleForPeerName(JsonValue? name, JsonObject resolved, string repoBasename);

    string? RoleForAnyPeerName(string dir, JsonValue? name, JsonObject resolved, string repoBasename);

    JsonArray ReadRoster(string dir);

    JsonArray LatestRoster(string dir);

    JsonArray AttributedRoster(string dir);

    bool HealFalseDown(string dir, JsonValue? sessionId, JsonValue? pid);

    JsonValue UpRecordFor(string dir, JsonValue? sessionId);

    /// <summary><c>{live, how, ageSec}</c> of a peers row.</summary>
    JsonObject RecordLiveness(JsonValue record, double? now = null);

    /// <summary>Whether the process a peers row names is shown by the shared verdict not to be the session: only that takes a row out of a count.</summary>
    bool RecordProvenDead(JsonValue record, double? now = null);

    JsonObject AttributedLiveness(JsonArray roster, JsonValue? name, string? team = null);

    /// <summary>As the roster form, for the peers rows of <paramref name="dir"/>, with the member's activity record counted as a sign of life a down row is set against.</summary>
    JsonObject AttributedLivenessIn(string dir, JsonValue? name, string? team = null);

    JsonValue SynthesizedPeerName(JsonValue record);

    JsonArray LivePeerSlots(string dir, JsonValue? team = null, bool noTeamScope = false, double? now = null);

    JsonObject Roster(string dir, JsonObject resolved, string repoBasename, double? now = null);

    string DescribeInstance(JsonValue instance);

    string RosterLine(JsonObject roster);

    JsonValue SessionModel(JsonValue? input, string? dir);

    void CacheSessionModel(string dir, JsonValue? sessionId, JsonValue? model);

    JsonValue RoleTier(string role, JsonObject resolved, JsonValue? sessionTier);

    string RoleModelLabel(string role, JsonObject resolved);

    string RouteLine(JsonValue? route);

    /// <summary>The <c>tier:</c> line of the HIERARCHY STATE block.</summary>
    string TierLine(JsonObject resolved, JsonValue? model);

    /// <summary>The HIERARCHY STATE block appended after the directive on every SessionStart matcher.</summary>
    string BuildStateBlock(string dir, JsonObject resolved, string repoBasename, JsonValue? model, JsonValue? sessionId = null, JsonValue? route = null, double? now = null);
}

/// <summary>The peer records of one read, indexed for the questions callers ask of them.</summary>
public interface IPeerReach
{
    /// <summary>The request ids acknowledged by any session for which <paramref name="counts"/> is true, or by the Orchestrator of one of <paramref name="teams"/>; the returned test reads nothing.</summary>
    Func<JsonValue?, bool> AckedBy(Func<string, bool> counts, IReadOnlyCollection<string>? teams = null);

    /// <summary>
    /// Whether a response file was named by a <c>sent</c> row that no <c>reopened</c> row cut and that resolved the request's obligation or
    /// went to an address <paramref name="toIsOrchestrator"/> accepts. The paths compare equal when they are the same file after links are resolved.
    /// </summary>
    Func<string, bool> SentFor(Func<string, bool> toIsOrchestrator);
}

/// <summary>
/// What a hook asks of the peer log. Each answer is exactly what <c>IHierarchyPeers.PendingFor</c> returns over the log at the moment of the call,
/// whichever backing answers. The rows are read-only: a caller may copy them and must never assign into them.
/// </summary>
public interface IPeerLatest
{
    /// <summary>The rows pending for <paramref name="sessionId"/>, in latest-by-key order.</summary>
    IReadOnlyList<JsonValue> PendingFor(JsonValue? sessionId);

    /// <summary>Whether <see cref="PendingFor"/> has any row.</summary>
    bool HasPending(JsonValue? sessionId);
}

/// <summary>The peer report-back records of <c>~/.claude/ah2/agent-hierarchy.peer-pending.jsonl</c> (<c>lib-peer</c>).</summary>
public interface IHierarchyPeers
{
    string PeerPendingPath();

    string StripRef(string value);

    JsonValue ParseSentinel(JsonValue? text);

    JsonValue ParseWrapper(JsonValue? text);

    JsonValue UnwrapCrossSession(JsonValue? text);

    JsonValue ExtractPendingRecord(JsonValue? text, JsonValue? role = null);

    JsonArray LatestByKey(IReadOnlyList<JsonValue> records);

    void AppendPeerRecord(IStoreWriteContext writer, JsonValue record);

    bool CliRootAlreadyEmitted(JsonValue? sessionId, JsonValue? line);

    void RecordCliRootEmitted(JsonValue? sessionId, JsonValue? line);

    /// <summary>The backlog groom due line for an Orchestrator session's prompt, once per due episode; null when nothing is due or it was said.</summary>
    string? TakeBacklogDueLine(string dir, JsonValue? sessionId);

    void RecordPeerRow(JsonValue record);

    /// <summary>
    /// Rewrites the peer log without the rows no reader can still need and returns how many went. A request's dispatch, seen, surfaced and
    /// report-acked rows go together, once the request is one of <paramref name="closedRequestIds"/>, its report was shown or acked, it was not reopened and its newest
    /// row is more than <paramref name="retentionDays"/> old; a turn, cli-root or subagent row goes when a later row of its key exists and it is more than a day old.
    /// A request not named in <paramref name="closedRequestIds"/> or <paramref name="openRequestIds"/> goes at twice that; a closed request never shown stays. A session silent for three days loses its
    /// rows without a request id, except those naming an open message and the outage rows.
    /// </summary>
    int CompactPeerRecords(IReadOnlyCollection<string> closedRequestIds, IReadOnlyCollection<string> openRequestIds, double nowMs, int retentionDays = 14);

    /// <summary>How many rows <see cref="CompactPeerRecords"/> would drop now, without taking any lock or writing anything; <paramref name="idleRule"/> false leaves out the rows only the idle-session rule drops.</summary>
    int CountPeerRecordsToCompact(IReadOnlyCollection<string> closedRequestIds, IReadOnlyCollection<string> openRequestIds, double nowMs, int retentionDays, bool idleRule = true);

    /// <summary>
    /// Rewrites the QA job store to one line per job, without the done or failed jobs that ended more than seven days before
    /// <paramref name="nowMs"/>, and returns how many jobs went. Queued and claimed jobs are never dropped.
    /// </summary>
    int CompactQa(double nowMs);

    bool AppendRelayRecord(JsonValue? sessionId, JsonValue relay, DateTimeOffset? now = null, string? voidNote = null);

    JsonArray PendingFor(JsonValue? sessionId);

    void AppendTurnMarker(JsonValue? sessionId, JsonValue? status);

    JsonValue LatestTurnMarker(JsonValue? sessionId);

    /// <summary>Notes that the user typed a prompt into <paramref name="sessionId"/>: its id and time, never its text.</summary>
    void AppendUserPromptRecord(string sessionId, string promptId);

    /// <summary>The newest user-prompt row of the session (<c>prompt_id</c>, <c>ts</c>); null when none was recorded.</summary>
    JsonValue? LatestUserPrompt(string sessionId);

    /// <summary>Notes that the Orchestrator <paramref name="sessionId"/> ended a turn without being kept going: its time only.</summary>
    void AppendTurnEnd(string sessionId);

    /// <summary>The newest turn-end row of the session (<c>ts</c>); null when none was recorded.</summary>
    JsonValue? LatestTurnEnd(string sessionId);

    void AppendDispatchRecord(JsonValue? sessionId, JsonValue? requestId, JsonValue? recipient, JsonValue? path = null, JsonValue? toAddr = null);

    void AppendReportRecord(JsonValue? type, JsonValue? sessionId, JsonValue? requestId);

    bool ReportShown(JsonValue? sessionId, JsonValue? requestId);

    /// <summary>True once <paramref name="sessionId"/> acknowledged the response to <paramref name="requestId"/> with <c>ah msg ack</c>.</summary>
    bool ReportAcked(JsonValue? sessionId, JsonValue? requestId);

    /// <summary>
    /// The acknowledged request ids of <paramref name="sessionId"/>, read once; the returned test reads nothing. With <paramref name="teams"/>,
    /// an ack the Orchestrator of one of those teams filed from any session also counts.
    /// </summary>
    Func<JsonValue?, bool> ReportAckedFor(JsonValue? sessionId, IReadOnlyCollection<string>? teams = null);

    /// <summary>Records that the Orchestrator of <paramref name="team"/>, from <paramref name="sessionId"/>, acknowledged the response to <paramref name="requestId"/>.</summary>
    void AppendOrchestratorAck(JsonValue? sessionId, JsonValue? requestId, string team);

    /// <summary>The request ids acknowledged by any session for which <paramref name="counts"/> is true, read once; the returned test reads nothing.</summary>
    Func<JsonValue?, bool> ReportAckedBy(Func<string, bool> counts, IReadOnlyCollection<string>? teams = null);

    /// <summary>
    /// <see cref="ReportShown"/> for one session over the peer records read once now: a caller that asks about many requests reads the
    /// records once instead of once per request.
    /// </summary>
    Func<JsonValue?, bool> ReportShownFor(JsonValue? sessionId);

    /// <summary><see cref="ReportShownFor(JsonValue?)"/> over peer records the caller already read, so a caller that also needs the dispatch rows reads the file once.</summary>
    Func<JsonValue?, bool> ReportShownFor(JsonValue? sessionId, IReadOnlyList<JsonValue> records);

    /// <summary>
    /// Whether a response file was named by a <c>sent</c> row, the record a SendMessage that carried its token leaves at send time, in
    /// any session. The paths compare equal when they are the same file after links are resolved. The rows are read once now.
    /// </summary>
    Func<string, bool> ResponseSentFor(Func<string, bool> toIsOrchestrator);

    /// <summary>
    /// The acknowledgements and <c>sent</c> rows of <paramref name="records"/>, indexed in one pass, so callers that differ only in whose
    /// acknowledgements count and which addresses are the Orchestrator's share that pass. <paramref name="realPath"/> resolves a path
    /// to its real path, or null when it cannot be resolved.
    /// </summary>
    IPeerReach PeerReach(IReadOnlyList<JsonValue> records, Func<string, string?> realPath);

    JsonArray LatestDispatchRows(JsonValue? sessionId);

    /// <summary><see cref="LatestDispatchRows(JsonValue?)"/> over peer records the caller already read.</summary>
    JsonArray LatestDispatchRows(JsonValue? sessionId, IReadOnlyList<JsonValue> records);

    void AppendSubagentRecord(JsonValue? sessionId, JsonValue? agentId, JsonValue? status, JsonValue? kind = null, JsonValue? agentType = null);

    /// <summary>The latest <c>subagent</c> row per agent id of the session, in first-seen order.</summary>
    JsonArray LatestSubagentRecords(JsonValue? sessionId);

    bool HasInflightSubagent(JsonValue? sessionId, double? now = null);

    JsonArray DispatchRecordsFor(JsonValue? sessionId);

    /// <summary>For each request id, the <c>created</c> of its earliest dispatch row (or the request's own when none parses), else null.</summary>
    Dictionary<string, JsonValue> DispatchOrigin(JsonArray requests);

    bool TargetSatisfiesRecord(JsonValue? target, JsonValue record);

    JsonArray UnconsumedWatchEvents(JsonValue? sessionId);
}

/// <summary>The hierarchy status document (served from <c>status_entries</c>) and the activity records beside it (<c>lib-status</c>).</summary>
public interface IHierarchyStatus
{
    /// <summary>Drops the status sources held per dir and stamp, so each is built from its entries next time (the on-demand rebuild).</summary>
    void ForgetStatusHolds()
    {
    }

    int StatusSchema { get; }

    /// <summary>Recovery (ah-msgs-fast-2 §3.8 item 6): rebuilds status dir <paramref name="dir"/>'s persisted status entries from the stores.</summary>
    (bool Ok, string Message) RebuildStatusEntriesOf(string dir);

    /// <summary>Rebuilds <paramref name="dir"/>'s status entries when their <c>meta</c> is missing or another version; true when it rebuilt.</summary>
    bool EnsureStatusEntries(string dir);

    /// <summary>
    /// <see cref="EnsureStatusEntries"/> off the caller's thread: a dir whose <c>meta</c> is missing or another version gets one background
    /// rebuild (the daemon's; elsewhere none); true when one was scheduled. The watch's first sight of a status dir uses it.
    /// </summary>
    bool ScheduleStatusEntries(string dir) => false;

    /// <summary>The status dirs whose entries are persisted (those with a <c>meta</c> row); empty with no state database.</summary>
    IReadOnlyList<string> StatusEntryDirs();

    int StatusSizeCap { get; }

    int RoundCap { get; }

    /// <summary>A string safe to display (escape sequences, control characters and bidi overrides stripped), cut at <paramref name="cap"/> code points; null when not a string.</summary>
    string? Clean(JsonValue? value, JsonValue? cap = null);

    JsonValue ReadActivity(string dir, string file);

    /// <summary>The tracked-job rows of a hierarchy directory (the <c>jobs</c> table), every team; none without a state database. <paramref name="mustAnswer"/>: see <see cref="IJobSource.Rows"/>.</summary>
    IReadOnlyList<JobEntry> JobRows(string dir, bool mustAnswer = false);

    /// <summary>The hierarchy directories an ah2-enabled session has noted in the user's index, oldest first.</summary>
    IReadOnlyList<string> KnownHierarchyDirs();

    /// <summary>Whether the repository a hierarchy directory belongs to is configured and has ah2 enabled (from its config, not a status override).</summary>
    bool DirEnabled(string dir);

    /// <summary>The sessions recorded in <c>session_pids</c> under a directory as session id and pid, the latest row of each.</summary>
    IReadOnlyList<KeyValuePair<string, long>> SessionPidsOf(string dir);

    JsonValue ReadActivityRecord(string dir, JsonValue? subject);

    JsonArray MemberFills(string dir, JsonValue team, string? teamName = null);

    JsonArray DescribeMembers(string dir, JsonValue team, JsonArray roster, string? teamName = null);

    void AppendSessionPid(string dir, JsonValue? sessionId, JsonValue? pid);

    /// <summary>
    /// The session ids the session_pids table under <paramref name="dir"/> records for <paramref name="pid"/> at or after
    /// <paramref name="notBefore"/> (the process's start), newest first. A row whose time cannot be parsed matches nothing.
    /// </summary>
    IReadOnlyList<SessionPidRow> SessionsOfPid(string dir, int pid, DateTimeOffset notBefore);

    JsonObject ComputeStatus(string? cwd, double? nowMs = null, string? dir = null, bool? enabledOverride = null);

    /// <summary>The queue's newest dispatch per member of <paramref name="team"/> from <paramref name="pool"/>, from the stored entries; null when no status dir lists the pool (the queue then reads the full listing).</summary>
    IReadOnlyList<(string Name, double Ms)>? LastDispatches(string pool, string team) => null;

    /// <summary>Rebuilds the status dirs a crashed database write left marked dirty, then clears the marks; no mark is no work.</summary>
    void HealDirtyStatus()
    {
    }

    /// <summary>The status a reader sees: assembled from the stored status entries when current, else <see cref="ComputeStatus"/>.</summary>
    JsonObject ReadStatus(string? cwd, double? nowMs = null, string? dir = null, bool? enabledOverride = null) => ComputeStatus(cwd, nowMs, dir, enabledOverride);

    void RefreshStatus(string dir, JsonObject? options = null);

    bool RecordActivityRouted(string dir, JsonValue subject, JsonObject fields, bool hook = false);

    /// <summary>
    /// Records what the watcher read in the existing activity record of <paramref name="subject"/> under <paramref name="dir"/> and
    /// <paramref name="mirrorDir"/>, and nothing else: <paramref name="clear"/> sets <c>screen_clear_at</c> and removes <c>screen_prompt_at</c>,
    /// else the reverse; the mark set is <paramref name="at"/>, and a mark already set is kept. False when no row changed or the write failed.
    /// </summary>
    bool MarkScreenClearRouted(string dir, JsonValue subject, bool clear, string at, string? mirrorDir = null);

    void ClearActivityRouted(string dir, JsonValue subject, bool hook = false, string? mirrorDir = null);

    int SweepActivityRouted(string dir, double cutoffMs, bool hook = false, Action<string>? pruned = null);

    string PlainStatus(JsonValue document, double nowMs);
}

/// <summary>A pipeline run's decision log <c>&lt;hier&gt;/pipeline/&lt;run&gt;/decisions.jsonl</c> (<c>lib-decisions</c>).</summary>
public interface IHierarchyDecisions
{
    string DecisionLogPath(string dir, string runId);

    /// <summary>The object rows of <paramref name="dir"/>'s question log (the <c>qa_log</c> table) in append order.</summary>
    IReadOnlyList<JsonValue> QaLogRows(string dir);

    /// <summary>The live run of <paramref name="team"/>: <c>{id}</c>, or <c>{error}</c> for none or several.</summary>
    JsonObject OpenRunAnchor(string dir, JsonValue? team, JsonArray? exchanges = null);

    /// <summary>Drops every dir's held run-anchor exchanges, so the next read lists them again (the on-demand rebuild).</summary>
    void ForgetAnchorHolds()
    {
    }

    bool KnownRun(string dir, JsonValue? runId);

    /// <summary>
    /// The newest close time (ISO, UTC) of a closed run anchor of <paramref name="team"/> (any team when null; an anchor that names no team
    /// counts for every team) in the pools of <paramref name="dir"/>, among those that closed at or before <paramref name="beforeIso"/>; null when none.
    /// </summary>
    string? LastAnchorCloseTime(string dir, string? team, string? beforeIso);

    /// <summary><c>{decisions, skipped}</c>: the lines that parse to an object, and how many lines did not.</summary>
    JsonObject ReadDecisions(string path);

    /// <summary>The counts of a log as <c>{decided, flagged, parked, by_item, ...}</c>; <paramref name="asksAnswered"/>, when given, is added as <c>asks_answered</c>.</summary>
    JsonObject DecisionSummary(JsonArray decisions, JsonValue? skipped, JsonValue? asksAnswered = null);

    /// <summary>The asks log <c>&lt;hier&gt;/asks.jsonl</c>: the user's answers to AskUserQuestion, one row per call.</summary>
    string AsksPath(string dir);

    /// <summary>An RFC 3339 instant with <c>Z</c> or an explicit offset, normalised to UTC. A bare date or time is not one.</summary>
    bool TryParseInstant(string text, out DateTimeOffset instant);

    /// <summary><c>{since, until}</c> (ISO instants) of the time a run's records span, or <c>{}</c> when none carries a time. A still-parked question keeps it open to now.</summary>
    JsonObject DecisionRunSpan(JsonArray records);

    /// <summary>
    /// <c>{answered, unreadable}</c>: the questions asked of the user inside [<paramref name="since"/>, <paramref name="until"/>) that no parked or
    /// answered record of <paramref name="records"/> already counts, and the asks rows that could not be read whole.
    /// </summary>
    JsonObject AsksAnswered(string dir, JsonArray records, DateTimeOffset since, DateTimeOffset until);

    /// <summary><c>{line}</c>: the computed decisions line for a run (<paramref name="runId"/>) or for the window of every run (<paramref name="since"/> up to <paramref name="until"/>, default now).</summary>
    JsonObject DecisionLine(string dir, string? runId, DateTimeOffset? since, DateTimeOffset? until);

    string QuestionKey(JsonValue? item, JsonValue? question);

    /// <summary>Validates <paramref name="input"/> against the log and appends it: <c>{refused}</c> with nothing written, or <c>{id, path, counts}</c>.</summary>
    JsonObject AppendDecision(IStoreWriteContext writer, string path, JsonValue? runId, JsonValue? input, string? operationId = null);

    /// <summary>
    /// The one answer path for a parked decision <paramref name="id"/>, shared by every front end: finds the team's open run, checks it is
    /// <paramref name="expectedRun"/> when one is given, asks <paramref name="prove"/> (run id, log path) for the caller's
    /// <c>{answered_by, recorded_by, caller, surrogate_class?}</c>, builds the <c>answered</c> input, appends it through
    /// <paramref name="append"/> (log path, run id, input) and, when the user answered and the Orchestrator did not record it, tells the
    /// Orchestrator through <paramref name="notify"/> (to, event, text; returns an error or null). <paramref name="choices"/> are sent as
    /// the multi-select <c>choices</c> when <paramref name="several"/>, else the one entry as <c>choice</c>. Every item of the team waiting on the user that the
    /// decision names moves to <c>waiting_orch</c> with the answer through <paramref name="progress"/>; a failed row joins <paramref name="eventError"/>. Returns <c>{refused}</c>,
    /// the append's result with <c>answers</c> and <c>decision</c>, or null when <paramref name="prove"/> refused.
    /// </summary>
    JsonObject? AnswerParked(
        string dir,
        JsonValue? team,
        string? expectedRun,
        string id,
        IReadOnlyList<string> choices,
        bool several,
        Func<string, string, JsonObject?> prove,
        Func<string, string, JsonObject, JsonObject> append,
        Func<string, string, string, string?>? notify,
        IHierarchyProgress? progress,
        out string? eventError);

    /// <summary>
    /// The one answer path for a question parked for the user (<c>q&lt;k&gt;</c>, in <c>parked-questions.jsonl</c> of <paramref name="dir"/>), shared by
    /// the terminal verb and the UI route; it needs no run. Finds the open question <paramref name="id"/> (of <paramref name="team"/> when given),
    /// asks <paramref name="prove"/> for the caller's <c>{writer, by_user, relayed_prompt?}</c>, takes the answer from <paramref name="given"/>
    /// (labels or option text, and whether it is a free-text answer), writes the asks row and the answered line, refreshes the status, appends
    /// the ruling to the ledger of <paramref name="pool"/>, and tells the Orchestrator (when the user answered) and the member it was parked for
    /// through <paramref name="notify"/> (to, event, text; returns an error or null). Returns <c>{refused: {reason, detail}}</c> (reasons
    /// <c>store_unreadable</c>, <c>unknown_question</c>, <c>already_answered</c>, <c>unmatched_label</c>), null when <paramref name="prove"/> refused,
    /// or <c>{id, team, header, labels, relayed_prompt?, event_error?}</c>; a skipped or failed event to the asker is in <paramref name="notes"/>.
    /// <paramref name="answered"/>, when given, runs once the answer is written and before any event (question, chosen labels, free text); the text it
    /// returns is appended to the events' text (b195: the ADR settle).
    /// </summary>
    JsonObject? AnswerQuestion(
        IFileReader files,
        StateCalls.IHierarchyStateCalls calls,
        IHierarchyProgress progress,
        string pool,
        string dir,
        string? team,
        string id,
        Func<ParkedQuestion, JsonObject?> prove,
        Func<ParkedQuestion, (IReadOnlyList<string> Given, bool FreeText)> given,
        string? note,
        DateTimeOffset now,
        Func<string, string, string, string?> notify,
        Func<string?, JsonValue> readTeam,
        Func<ParkedQuestion, IReadOnlyList<string>, bool, string?>? answered,
        out IReadOnlyList<string> notes);

    JsonObject ConstraintLines(JsonValue? text);

    JsonValue LiveRun(string? cwd);

    string MergeCommand(JsonValue? pr, JsonValue? sha, JsonValue? method, JsonValue? draft);

    JsonValue ParseMergeForm(JsonValue? command);

    void AppendMergeRecord(IStoreWriteContext writer, string path, JsonValue merge, string? operationId = null);

    JsonObject ReadDecisionInput(string dir, string runId, string inputPath);

    /// <summary><c>live</c>, <c>not</c> or <c>unknown</c>.</summary>
    string RunLiveness(string? cwd);
}
