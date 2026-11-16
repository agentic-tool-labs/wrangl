namespace Ah.Abstractions;

/// <summary>The caller's directories. Ah.Config builds it; the state engine only receives it.</summary>
public sealed record RequestContext(string Cwd, string HierarchyDirectory, string MainHierarchyDirectory, string SocketDirectory);

/// <summary>
/// How a state call is made. <see cref="Hook"/> is budget mode: <see cref="Budget"/> (default 1.5 s) is the whole time the
/// write may take, the daemon gets at most 300 ms of it, and a direct write waits for the lock only for what is left.
/// <see cref="OperationId"/> names the write (a fresh UUID when omitted); repeating a write with the same id is safe.
/// <see cref="LockWait"/> bounds how long a direct (non-hook) write waits for the domain lock; the default is the stale age plus 2 s.
/// </summary>
public sealed record StateCallOptions(string? OperationId = null, bool Hook = false, TimeSpan? Budget = null, TimeSpan? LockWait = null);

/// <summary>
/// The outcome of a state call. On success <see cref="ResultJson"/> is the canonical JSON of the op's result. On failure
/// <see cref="ErrorCode"/> is the route's code (<c>op_failed</c> carries the op's own <c>error_code</c>), for example
/// <c>lock_timeout</c>, <c>state_timeout</c>, <c>conflict</c>, <c>too_large</c>, <c>bad_request</c>.
/// </summary>
public sealed record StateResult(bool Ok, ReadOnlyMemory<byte> ResultJson, string? Error = null, string? ErrorCode = null);

/// <summary>A document read for an update: its bytes (null when there is no file) and the digest of those bytes (<c>absent</c> for none).</summary>
public sealed record StateDocument(ReadOnlyMemory<byte>? Bytes, string Digest);

/// <summary>One op call: the op name and its canonical JSON args.</summary>
public sealed record StateCallSpec(string Op, ReadOnlyMemory<byte> ArgsJson);

/// <summary>
/// One read-modify-write of an op's target file. <see cref="Read"/> gives the document and its digest from one read;
/// <see cref="Mutate"/> is a pure function of it that returns the outcome, or null for no change; <see cref="Build"/> turns
/// an outcome and the digest into the call (its args carry <c>expect: digest</c>).
/// </summary>
public sealed record StateUpdatePlan(
    Func<StateDocument> Read,
    Func<StateDocument, ReadOnlyMemory<byte>?> Mutate,
    Func<ReadOnlyMemory<byte>, string, StateCallSpec> Build);

/// <summary>The result of an update: whether anything was written, and the last call's result.</summary>
public sealed record StateUpdateResult(bool Changed, StateResult Result);

public interface IStateWriter
{
    /// <summary>Runs one op through the stamp's writer. Reads are run in-process by the caller's runtime.</summary>
    StateResult Invoke(
        string domain,
        string op,
        ReadOnlyMemory<byte> argsJson,
        RequestContext context,
        StateCallOptions? options = null);

    /// <summary>
    /// Whether <paramref name="domain"/> keeps its state under HOME, so a write to it needs no project hierarchy directory. The
    /// domain declares it (<see cref="IStateDomain.HomeScoped"/>); a writer that does not know the domain says false.
    /// </summary>
    bool IsHomeScoped(string domain) => false;

    /// <summary>Whether <paramref name="domain"/> declares <see cref="IStateDomain.DbOnly"/>; a writer that does not know the domain says false.</summary>
    bool IsDbOnly(string domain) => false;

    /// <summary>Read, mutate, and write with the digest as a precondition; a conflict re-reads and retries, up to five attempts.</summary>
    StateUpdateResult Update(
        string domain,
        StateUpdatePlan plan,
        RequestContext context,
        StateCallOptions? options = null);
}

/// <summary>The only way a domain writes a hub store. The state engine hands one to each write op.</summary>
public interface IStoreWriteContext
{
    void Write(string path, string tmpPath, ReadOnlySpan<byte> bytes, UnixFileMode? mode = null);

    /// <summary>Returns false when <paramref name="path"/> already exists.</summary>
    bool CreateExclusive(string path, ReadOnlySpan<byte> bytes, UnixFileMode? mode = null);

    void Append(string path, ReadOnlySpan<byte> record);

    void Rename(string fromPath, string toPath);

    bool Remove(string path);

    void Mkdir(string path, UnixFileMode? mode = null);
}

public enum OpKind
{
    Read,
    Write,
}

public enum OpStyle
{
    Append,
    Overwrite,
    Replace,
}

/// <summary>
/// <paramref name="OperationId"/> names the write; an append op records it as data in its row (null for a read).
/// <paramref name="Locked"/> is false only for a lock-optional write that went ahead without the domain lock.
/// </summary>
public sealed record OpCall(string Op, ReadOnlyMemory<byte> ArgsJson, RequestContext Context, string? OperationId = null, bool Locked = true);

/// <summary>
/// The outcome of one op body. <paramref name="ErrorCode"/> is the op's own error code on a failure (null reads as
/// <c>op_failed</c>): the engine records it in the executed-op row and returns it on the wire.
/// </summary>
// Value (ah2-1): set by a read op that also hands out the value ResultJson was written from, so the daemon's own reader need not
// parse the bytes again. The op gives it only for a value it built for this call and keeps no reference to.
// Anything that is not a JsonValue travels beside the result instead (b459 C1: status.read's witness); it never reaches the wire.
public sealed record OpResult(bool Ok, ReadOnlyMemory<byte> ResultJson, string? Error = null, string? ErrorCode = null, object? Value = null);

/// <summary>
/// One entry of a domain's op table. <paramref name="Body"/> receives a null write context for reads.
/// <paramref name="Style"/> applies to writes only. <paramref name="DigestPrecondition"/> marks an op whose args must carry
/// the target <c>path</c> and the <c>expect</c> digest of the file as the caller read it (or <c>absent</c>).
/// <paramref name="FileDigestApplies"/>, when set, is asked at write time whether the engine compares that file digest itself: false
/// means another authority checks the digest inside the op (null: the engine always compares).
/// <paramref name="PathArgs"/> names the args that are paths: each must be absolute and inside the request's hierarchy
/// directories (null or empty: none).
/// <paramref name="BusyAfter"/>, when set, bounds each of the two waits of a write, the domain's worker and then its lock: past
/// either the daemon refuses with <c>op_failed</c> / <c>busy</c> instead of leaving the caller to its own timeout. The worst case
/// is a write that starts just before the first deadline and then waits out the second, so a refusal can take about twice
/// <paramref name="BusyAfter"/> plus the time the write itself runs.
/// <paramref name="DbBody"/>, when set, is the op's body for a domain whose mode is <c>db</c>: it runs inside the database
/// transaction the engine opens, and <paramref name="Body"/> stays the files-mode body. An op with no <paramref name="DbBody"/>
/// has no database implementation.
/// <paramref name="ModeKey"/>, when set, is the mode key of this op in place of its domain's: the op follows that key's mode, so the
/// domain's own mode never gates it. A DbBody on such an op needs the key in the database's known message domains.
/// <paramref name="DbAfterCommit"/>, when set, runs once after that transaction committed, with a writer
/// for the files the committed rows are exported to; a failure in it never fails the op, because a file left unwritten is
/// written later from the rows. <paramref name="DbBodyWithFiles"/> is a <paramref name="DbBody"/> that also gets the writer, for an op
/// that must put a file in place inside its transaction and undo it when the transaction fails; it takes the place of
/// <paramref name="DbBody"/> when both are set. A commit that fails after the body returned leaves the file in place with no row;
/// the body cannot undo it.
/// <paramref name="BeforeEveryRun"/>, when set, runs on the files path under the op's lock on every call, a replay of an applied op id included; if
/// it throws the op fails.
/// <paramref name="LeavesSeq"/> keeps the write from moving the domain's <c>domain_seq</c>: for a write no reader keys a cache or a watch on.
/// </summary>
public sealed record StateOp(
    string Name,
    OpKind Kind,
    OpStyle? Style,
    bool LockOptional,
    string? LockName,
    bool Daemon,
    Func<OpCall, IStoreWriteContext?, OpResult> Body,
    bool DigestPrecondition = false,
    IReadOnlyList<string>? PathArgs = null,
    TimeSpan? BusyAfter = null,
    Func<bool>? FileDigestApplies = null,
    Func<OpCall, IDbOpTransaction, OpResult>? DbBody = null,
    string? ModeKey = null,
    Action<OpCall, IStoreWriteContext>? DbAfterCommit = null,
    Func<OpCall, IDbOpTransaction, IStoreWriteContext, OpResult>? DbBodyWithFiles = null,
    Action<OpCall, IStoreWriteContext>? BeforeEveryRun = null,
    bool LeavesSeq = false);

/// <summary>The statements a database-backed op body may run; they all belong to the one open transaction.</summary>
public interface IDbOpTransaction
{
    /// <summary>Runs one statement; <paramref name="args"/> bind to ?1, ?2, … and may be null, integers, bool, text or bytes.</summary>
    void Execute(string sql, params object?[] args);

    /// <summary>Runs one query; each row holds null, a long, a double, a string or bytes.</summary>
    IReadOnlyList<object?[]> Query(string sql, params object?[] args);
}

/// <summary>One database-backed write: the domain, the write's id (null or empty: no dedup), the path recorded with it, the body to run in the transaction, whether it leaves <c>domain_seq</c> as it is, whether the domain is db-only (no mode row is read), and the state directory the write id is scoped to (null: unscoped).</summary>
public sealed record DbOpRequest(string Domain, string? OperationId, string Path, Func<IDbOpTransaction, OpResult> Body, bool LeavesSeq = false, bool DbOnly = false, string? StateDirectory = null);

/// <summary>One <c>session_pids</c> row: a pid the hook recorded for a session under a hierarchy dir, and when (ISO-8601) it last became that session's pid.</summary>
public sealed record SessionPidEntry(string SessionId, long Pid, string At);

/// <summary>The <c>session_pids</c> table, read.</summary>
public interface ISessionPidSource
{
    /// <summary>The rows of <paramref name="hierDir"/>, oldest <c>at</c> first; none when the database holds none or cannot be read.</summary>
    IReadOnlyList<SessionPidEntry> Rows(string hierDir);
}

/// <summary>One <c>activity</c> row: a session's (or pane's) activity record under a hierarchy dir, as JSON text, and when it was last written (Unix ms).</summary>
public sealed record ActivityEntry(string Subject, string Body, long UpdatedAtMs);

/// <summary>The <c>activity</c> table, read.</summary>
public interface IActivitySource
{
    /// <summary>The rows of <paramref name="hierDir"/>, by subject; none when the database holds none or cannot be read.</summary>
    IReadOnlyList<ActivityEntry> Rows(string hierDir);

    /// <summary>The row of <paramref name="subject"/> under <paramref name="hierDir"/>, or null.</summary>
    ActivityEntry? Row(string hierDir, string subject);
}

/// <summary>The <c>gates</c> table, read.</summary>
public interface IGateSource
{
    /// <summary>The bodies (JSON text) of <paramref name="hierDir"/>'s gate rows, in append order; none when the database holds none or cannot be read.</summary>
    IReadOnlyList<string> Bodies(string hierDir);

    /// <summary>As <see cref="Bodies"/>, but null, with the cause in <paramref name="why"/>, when the database cannot be read, so a caller can tell a failed read from no rows.</summary>
    IReadOnlyList<string>? BodiesOrNull(string hierDir, out string? why);

    /// <summary>
    /// A text that changes whenever <paramref name="hierDir"/>'s rows change: their count and last position, as rows are only ever added
    /// (appended, or imported before the first); null when the database cannot be read.
    /// </summary>
    string? Stamp(string hierDir);

    /// <summary>
    /// <paramref name="hierDir"/>'s row count and its rows after position <paramref name="seq"/>, in append order, read in one snapshot
    /// (b459 step 13); null when this source reads no deltas. Throws when the database cannot be read.
    /// </summary>
    GateRowsAfter? After(string hierDir, long seq) => null;
}

/// <summary>A gate row's body and its position in its dir's log.</summary>
public readonly record struct GateRowAt(long Seq, string Body);

/// <summary>A dir's gate row count and the rows after a position, from one snapshot.</summary>
public sealed record GateRowsAfter(long Count, IReadOnlyList<GateRowAt> Rows);

/// <summary>How a database-backed write ended: done (or answered from the stored result), left to the files backend, refused before it ran, or failed in its body.</summary>
public enum DbOpStatus
{
    Done,
    Redirect,
    Refused,
    Failed,
}

/// <summary>The outcome of a <see cref="DbOpRequest"/>; <paramref name="ResultJson"/> is the canonical result on <see cref="DbOpStatus.Done"/>.</summary>
public sealed record DbOpOutcome(DbOpStatus Status, ReadOnlyMemory<byte> ResultJson, string? Error = null, string? ErrorCode = null);

/// <summary>Runs one write in one database transaction: the domain's mode is read in it, then dedup, the body, the applied-write row and the domain's change counter.</summary>
public interface IDbOpRunner
{
    DbOpOutcome Run(DbOpRequest request);

    /// <summary>
    /// The lock file a files-path write of <paramref name="domain"/> takes for <paramref name="stateDirectory"/> and <paramref name="lockName"/>,
    /// under the home's <c>state/locks</c>, once the database is ensured as <see cref="Run"/> ensures it; null when there is no database to
    /// use, so the lock, and the op-id replay, stay in the state directory.
    /// </summary>
    string? FilesLockPath(string domain, string stateDirectory, string lockName) => null;

    /// <summary>The stored result of <paramref name="operationId"/> in <paramref name="stateDirectory"/> in <c>applied_writes</c>, or null when there is none.</summary>
    byte[]? AppliedResult(string modeKey, string stateDirectory, string operationId) => null;

    /// <summary>Records <paramref name="operationId"/> of <paramref name="stateDirectory"/> with its canonical result in <c>applied_writes</c>; false when the row could not be written.</summary>
    bool RecordApplied(string modeKey, string stateDirectory, string operationId, string path, ReadOnlyMemory<byte> result) => false;
}

/// <summary>
/// The mode of a message domain, <c>files</c> or <c>db</c>, as the database holds it now. A domain with no database state is
/// <c>files</c>. The reader throws when the write must be refused: the database cannot be read, its schema is newer than this build,
/// or a mode row names an unknown domain or mode.
/// </summary>
public interface IMessageModeReader
{
    string Read(string domain);
}

/// <summary>One persisted status input (ah-msgs-fast-2 §3.8): the dir the input lives in, its kind, its key in the kind, its JSON.</summary>
public sealed record StatusEntryRow(string PoolDir, string Kind, string Key, string Json)
{
    /// <summary>
    /// The dirs whose write sequence one <see cref="IStatusEntryStore"/> Replace bumps: each dir it writes (a scope, a delete, a row) and
    /// each of <paramref name="touched"/>, once each. The store bumps them; a rebuild counts the bumps its own process made.
    /// </summary>
    public static IReadOnlyList<string> WrittenDirs(
        IReadOnlyList<(string PoolDir, string Kind)> scopes, IReadOnlyList<(string PoolDir, string Kind, string Key)> deletes, IReadOnlyList<StatusEntryRow> rows, IReadOnlyList<string> touched) =>
        [.. scopes.Select(static s => s.PoolDir).Concat(deletes.Select(static d => d.PoolDir)).Concat(rows.Select(static r => r.PoolDir)).Concat(touched).Distinct(StringComparer.Ordinal)];
}

/// <summary>The rows of a status read written after a mark, with the row count and the sum of the write times of the whole read (b459 step 27).</summary>
public sealed record StatusEntriesDelta(long Count, long SumMs, IReadOnlyList<StatusEntryStamped> Rows);

/// <summary>A status entry row with the time it was written; <see cref="Json"/> is null when the reader asked to leave the text out.</summary>
public sealed record StatusEntryStamped(string PoolDir, string Kind, string Key, long ChangedMs, string? Json);

/// <summary>The <c>status_entries</c> table: the persisted inputs a status is assembled from.</summary>
public interface IStatusEntryStore
{
    /// <summary>The rows status dir <paramref name="statusDir"/> assembles from: its own, every row of the dirs its <c>view</c> rows key, and the global <c>origin</c> rows of their exchanges.</summary>
    IReadOnlyList<StatusEntryRow> Read(string statusDir);

    /// <summary>The rows of <see cref="Read"/> with their write times, the text left out of each written at or before <paramref name="sinceMs"/>; null when the store cannot.</summary>
    IReadOnlyList<StatusEntryStamped>? ReadNewerThan(string statusDir, long sinceMs) => null;

    /// <summary>
    /// The rows of <see cref="Read"/> written after <paramref name="sinceMs"/>, with the row count and write-time sum of the whole read, taken
    /// in one snapshot; null when the store cannot (or has no database). A moved count is a row deleted or added; a sum the changed rows do
    /// not account for is a row written with an earlier stamp.
    /// </summary>
    StatusEntriesDelta? ReadChangedSince(string statusDir, long sinceMs) => null;

    /// <summary>
    /// The revision of the rows of <see cref="Read"/>, taken without reading the rows: their latest write time and count, then the write
    /// counters of the dirs they come from (<c>maxChangedMs:rowCount:counters</c>), so every write moves it; empty when there are none,
    /// null when the store cannot.
    /// </summary>
    string? Revision(string statusDir) => null;

    /// <summary>The status dirs that hold a <c>meta</c> row, in order.</summary>
    IReadOnlyList<string> StatusDirs();

    /// <summary>The rows of one kind under one dir, by key; one row when <paramref name="key"/> is given.</summary>
    IReadOnlyList<StatusEntryRow> ReadKind(string poolDir, string kind, string? key = null);

    /// <summary>The status dirs whose <c>view</c> rows key <paramref name="poolDir"/>: where a delta under that dir publishes (fan-out).</summary>
    IReadOnlyList<string> Listing(string poolDir);

    /// <summary>The dirs that hold a row of <paramref name="kind"/> keyed <paramref name="key"/>.</summary>
    IReadOnlyList<string> PoolsWith(string kind, string key);

    /// <summary>
    /// In one transaction, deletes every row of each <paramref name="scopes"/> (pool dir, kind) and each of <paramref name="deletes"/>
    /// (pool dir, kind, key), then upserts <paramref name="rows"/>; false with the reason when the database refuses.
    /// </summary>
    (bool Ok, string Message) Replace(IReadOnlyList<(string PoolDir, string Kind)> scopes, IReadOnlyList<(string PoolDir, string Kind, string Key)> deletes, IReadOnlyList<StatusEntryRow> rows, long changedMs);

    /// <summary>
    /// <see cref="Replace(IReadOnlyList{ValueTuple{string, string}}, IReadOnlyList{ValueTuple{string, string, string}}, IReadOnlyList{StatusEntryRow}, long)"/>
    /// that also bumps the write sequence of each dir it writes and of each of <paramref name="touched"/>, and that changes nothing and
    /// answers false when a dir of <paramref name="expected"/> no longer holds that sequence (a missing one holds 0): a rebuild's commit.
    /// </summary>
    (bool Ok, string Message) Replace(
        IReadOnlyList<(string PoolDir, string Kind)> scopes, IReadOnlyList<(string PoolDir, string Kind, string Key)> deletes, IReadOnlyList<StatusEntryRow> rows, long changedMs,
        IReadOnlyList<string> touched, IReadOnlyDictionary<string, long>? expected) => Replace(scopes, deletes, rows, changedMs);

    /// <summary>The write sequence of every dir that has one, by dir.</summary>
    IReadOnlyDictionary<string, long> Sequences() => new Dictionary<string, long>();

    /// <summary>
    /// The <c>Ah.Core.Json.StatusDirty</c> marks a database write left whose status delta has not run, set at least
    /// <paramref name="olderThanMs"/> ago by the database's clock: (meta key, pool dir or empty).
    /// </summary>
    IReadOnlyList<(string Key, string PoolDir)> DirtyMarks(long olderThanMs = 0) => [];

    /// <summary>Sets one dirty mark <paramref name="key"/> (a <c>StatusDirty</c> prefix key) for <paramref name="poolDir"/>, at the database's clock.</summary>
    void MarkDirty(string key, string poolDir)
    {
    }

    /// <summary>Deletes the dirty marks <paramref name="keys"/>; one that is not there is no change.</summary>
    void ClearDirty(IReadOnlyList<string> keys)
    {
    }
}

public interface IStateDomain
{
    string Name { get; }

    /// <summary>The canonical store names this domain writes.</summary>
    IReadOnlyList<string> StoreNames { get; }

    /// <summary>
    /// Whether the domain's state lives under the user's home and none of its ops read the request's hierarchy directories.
    /// The daemon then checks only the request's <c>cwd</c> and socket directory, so a missing project directory cannot refuse
    /// a write that never touches it.
    /// </summary>
    bool HomeScoped => false;

    /// <summary>
    /// Whether the domain's writes live only in the database: each write op runs its DbBody with no mode row consulted, in the daemon
    /// and in the in-process replay alike, and never takes the files path.
    /// </summary>
    bool DbOnly => false;

    /// <summary>Resolves the directory the domain's state lives in for a request.</summary>
    string StateDirectory(RequestContext context);

    /// <summary>
    /// The same directory as the call's own args name it (the pool dir or the log path an op targets); the engine asks this form,
    /// before it takes the lock, with the args it will pass to the op. Defaults to the args-independent form.
    /// </summary>
    string StateDirectory(RequestContext context, ReadOnlyMemory<byte> argsJson) => StateDirectory(context);

    IReadOnlyList<StateOp> Ops { get; }

    /// <summary>Runs after a write op has completed and its lock has been released.</summary>
    void AfterWrite(OpCall operation, OpResult result);
}

// per-dir-files-to-db S6

/// <summary>The <c>qa_log</c> table (the user's question log), read.</summary>
public interface IQaLogSource
{
    /// <summary>The bodies (JSON text) of <paramref name="hierDir"/>'s question-log rows, in append order; none when the database holds none or cannot be read.</summary>
    IReadOnlyList<string> Bodies(string hierDir);
}

// per-dir-files-to-db S7
/// <summary>The peer roster (<c>roster_records</c>), read.</summary>
public interface IRosterRecordSource
{
    /// <summary>The bodies (JSON text) of <paramref name="hierDir"/>'s roster rows, in append order; none when the database holds none or cannot be read.</summary>
    IReadOnlyList<string> Bodies(string hierDir);

    /// <summary>
    /// A text that differs whenever <paramref name="hierDir"/>'s roster rows may have changed: the <c>peers</c> write counter and the
    /// dir's row count (the import pass adds rows without moving the counter). Null when the source cannot tell.
    /// </summary>
    string? Stamp(string hierDir) => null;
}

// per-dir-files-to-db S8
/// <summary>One <c>jobs</c> row: a tracked-job record of a team (its <c>jobs/</c> folder name) under a hierarchy dir, as JSON text.</summary>
public sealed record JobEntry(string Team, string Id, string Body);

/// <summary>The <c>jobs</c> table, read.</summary>
public interface IJobSource
{
    /// <summary>The job rows of <paramref name="hierDir"/>, every team, by team then id; none when the database holds none or cannot be read.
    /// With <paramref name="mustAnswer"/>, a database that exists but cannot be read (still busy after the one retry, newer) throws
    /// <see cref="InvalidOperationException"/> instead: a caller that creates a job when it finds none must not act on that empty list.</summary>
    IReadOnlyList<JobEntry> Rows(string hierDir, bool mustAnswer = false);
}

// per-dir-files-to-db S9

/// <summary>The <c>decisions</c> table: each pipeline run's decision log, a run's <c>pipeline/&lt;run&gt;/decisions.jsonl</c> taken into it once.</summary>
public interface IDecisionSource
{
    /// <summary>
    /// The bodies (JSON text) of <paramref name="run"/>'s rows in append order (the import pass takes its file). Null when the decisions
    /// domain reads its files (no database, or <c>mode.decisions</c> is <c>files</c>); none when the database cannot be read.
    /// </summary>
    IReadOnlyList<string>? Bodies(string hierDir, string run);

    /// <summary>The runs of <paramref name="hierDir"/> with rows, in order; null as <see cref="Bodies"/>.</summary>
    IReadOnlyList<string>? Runs(string hierDir);
}

/// <summary>The <c>recover_records</c> table (schema 43): a bare member's recovery record (<c>control</c> false) and the owner's requests for it (<c>control</c> true), keyed by the hierarchy dir's key (<c>WatchModule.RecoverDirKey</c>), team and member.</summary>
public interface IRecoverRecordStore
{
    /// <summary>The stored JSON text, or null when there is none. Throws <c>StoreUnavailableException</c> when the database cannot be read.</summary>
    string? Read(string dirKey, string team, string member, bool control);

    /// <summary>Replaces the row. Throws <see cref="InvalidOperationException"/> when the database refuses the write.</summary>
    void Write(string dirKey, string team, string member, bool control, string body);
}

// per-dir-files-to-db S11e

/// <summary>The <c>worktree_grants</c> and <c>worktree_grant_audit</c> tables: a hierarchy dir's <c>grants/&lt;team&gt;.json</c> documents, taken into them once per dir.</summary>
public interface IGrantSource
{
    /// <summary>
    /// One read of <paramref name="hierDir"/>'s grants (§6c G5): the rows only (the import pass takes the documents).
    /// <see cref="GrantsLog.Unreadable"/> when the store cannot be read. Null when
    /// there is no database, so the grants are read from their files.
    /// </summary>
    GrantsLog? Read(string hierDir);

    /// <summary>
    /// Inside a grants write's transaction: takes <paramref name="hierDir"/>'s documents into the table when the dir is untaken, and
    /// returns the dir's key (<c>hier_dir</c>). Throws when a document file cannot be read, or when a file changed after the take with
    /// bytes that are not its row's (the read's skew rule), so the write does not happen.
    /// </summary>
    string Take(IDbOpTransaction tx, string hierDir);

    /// <summary>Every worktree audit row: the table's in write order, then the old audit log's lines; none from a store that cannot be read.</summary>
    IReadOnlyList<string> AuditRows();
}

// per-dir-files-to-db S11c

/// <summary>One row of a progress log: its place in the log and its body (one JSON object, as the old file line held it).</summary>
/// <param name="Seq">The row's place in its log, from 1.</param>
/// <param name="Body">The row as JSON text.</param>
public sealed record ProgressLogRow(long Seq, string Body);

/// <summary>A team's ledger cursor and the rows after it, read together.</summary>
/// <param name="Cursor">The seq of the last row the memory adapter was given, or 0.</param>
/// <param name="Rows">The rows after it, in order.</param>
public sealed record LedgerSince(long Cursor, IReadOnlyList<ProgressLogRow> Rows);

/// <summary>
/// The <c>progress_logs</c> table: a team's <c>ledger.jsonl</c> and <c>&lt;stream&gt;.plan.jsonl</c>, by the old file name (<c>log</c>). Rows
/// only: the import pass takes the old files.
/// </summary>
public interface IProgressLogSource
{
    /// <summary>The <c>hier_dir</c> a row of <paramref name="hierDir"/> is stored under: the path with its links followed, so a pool named two ways is one log.</summary>
    string Key(string hierDir);

    /// <summary>The rows of <paramref name="log"/> after <paramref name="after"/>, in order; none when the database holds none or cannot be read.</summary>
    IReadOnlyList<ProgressLogRow> Rows(string hierDir, string team, string log, long after = 0);

    /// <summary>The logs of the team that hold a row, by name, in order.</summary>
    IReadOnlyList<string> Logs(string hierDir, string team);

    /// <summary>The teams of <paramref name="hierDir"/> whose log <paramref name="log"/> holds a row, in order; with a null <paramref name="log"/>, those with a row in any log.</summary>
    IReadOnlyList<string> Teams(string hierDir, string? log);

    /// <summary>The seq of the last ledger row the memory adapter was given, or 0.</summary>
    long LedgerCursor(string hierDir, string team);

    /// <summary>The ledger cursor and the rows after it, in one read; null when the database cannot be read, so nothing is forwarded.</summary>
    LedgerSince? LedgerAfterCursor(string hierDir, string team);

    /// <summary>Inside a write's transaction: takes the old file of <paramref name="log"/> into the table, once (a write builds on the log's rows; files-off-disk §4, atp-1518).</summary>
    void Take(IDbOpTransaction tx, string hierDir, string team, string log);

    /// <summary>Inside a write's transaction: sets the ledger cursor of the team to <paramref name="seq"/>.</summary>
    void SetLedgerCursor(IDbOpTransaction tx, string hierDir, string team, long seq);

    /// <summary>The release watch's prior tips of <paramref name="hierDir"/> (<c>&lt;team&gt; &lt;ref&gt; &lt;sha&gt;</c> lines), or null when none is stored or there is no database; throws when the database cannot be read.</summary>
    string? ReleaseSeen(string hierDir);

    /// <summary>Inside a write's transaction: stores the release watch's prior tips of <paramref name="hierDir"/>.</summary>
    void SetReleaseSeen(IDbOpTransaction tx, string hierDir, string text);
}

// per-dir-files-to-db S11d

/// <summary>One progress document of a team: a checklist, journal, rollup, closing note, <c>.nudged</c> or <c>sessions.jsonl</c>, by its old file name.</summary>
/// <param name="Pool">The pool the team's progress lives in.</param>
/// <param name="Team">The team.</param>
/// <param name="Name">The old file name, for example <c>main.md</c> or <c>main.journal.md</c>.</param>
/// <param name="Kind"><c>checklist</c>, <c>journal</c>, <c>rollup</c>, <c>closing</c>, <c>nudged</c> or <c>sessions</c>.</param>
/// <param name="Body">The document's text.</param>
/// <param name="UpdatedMs">When it was last written, in Unix milliseconds.</param>
/// <param name="Raw">The exact bytes, when they are not valid UTF-8 (<paramref name="Body"/> is then their lenient decoding); else null.</param>
public sealed record ProgressDoc(string Pool, string Team, string Name, string Kind, string Body, long UpdatedMs, byte[]? Raw = null)
{
    /// <summary>The document's key: the path its file had, <c>&lt;pool&gt;/progress/&lt;team&gt;/&lt;name&gt;</c>.</summary>
    public string Key => Pool.TrimEnd('/') + "/progress/" + Team + "/" + Name;
}

/// <summary>
/// The <c>progress_docs</c> table: a team's progress documents, by old file name. A read gives rows only (the import pass takes the
/// team's old files); with no database yet, a read gives the files.
/// </summary>
public interface IProgressDocSource
{
    /// <summary>The team's documents, in name order; none when the database holds none or cannot be read.</summary>
    IReadOnlyList<ProgressDoc> Docs(string pool, string team);

    /// <summary>The team's document <paramref name="name"/>, or null.</summary>
    ProgressDoc? Doc(string pool, string team, string name);

    /// <summary>The teams of <paramref name="pool"/> that hold a document, in order.</summary>
    IReadOnlyList<string> Teams(string pool);

    /// <summary>The document whose key (<see cref="ProgressDoc.Key"/>) is <paramref name="key"/>, or null.</summary>
    ProgressDoc? ByKey(string key);

    /// <summary>Inside a write's transaction: the body of the team's document <paramref name="name"/>, the team's old files taken first; null when there is none.</summary>
    string? Body(IDbOpTransaction tx, string pool, string team, string name);

    /// <summary>
    /// Inside a write's transaction: sets the team's document <paramref name="name"/> to <paramref name="body"/>, or with
    /// <paramref name="createOnly"/> only when it is absent; whether it was written. A name that is no progress document throws.
    /// </summary>
    bool Put(IDbOpTransaction tx, string pool, string team, string name, string body, bool createOnly = false);

    /// <summary>Inside a write's transaction: appends <paramref name="text"/> to the team's document <paramref name="name"/> at the byte level (creating it when absent).</summary>
    void Append(IDbOpTransaction tx, string pool, string team, string name, string text);

    /// <summary>
    /// How many document rows <paramref name="pool"/> holds (only <paramref name="team"/>'s when given), or null when the database cannot say
    /// (b459 C3): a document name is only ever added by an insert, so a set of names read at an unchanged count is the set now.
    /// </summary>
    string? Stamp(string pool, string? team = null) => null;

    /// <summary>Whether the team's progress is off: its <c>.off</c> row is present (files-off-disk §6); false when the database cannot answer.</summary>
    bool IsOff(string pool, string team) => false;

    /// <summary>Inside a write's transaction: whether the team's <c>.off</c> row is present. Takes no old file.</summary>
    bool IsOff(IDbOpTransaction tx, string pool, string team) => false;

    /// <summary>Inside a write's transaction: sets or clears the team's <c>.off</c> row, idempotently.</summary>
    void SetOff(IDbOpTransaction tx, string pool, string team, bool off) => throw new NotSupportedException("progress off needs the state database");
}

// per-dir-files-to-db S11a-ckpt

/// <summary>One checkpoint record: its key (the absolute path the record file had or would have had), its stamp and its body (JSON text).</summary>
/// <param name="Key">The record's key.</param>
/// <param name="Ts">The record's stamp.</param>
/// <param name="Body">The record as JSON text.</param>
public sealed record CheckpointRow(string Key, string Ts, string Body);

/// <summary>The <c>checkpoints</c> table, rows only (the import pass takes the H's old checkpoint files).</summary>
public interface ICheckpointSource
{
    /// <summary>The key of the record at <paramref name="path"/>: the path with its links followed.</summary>
    string Key(string path);

    /// <summary>The team's newest record, or the one stamped <paramref name="ts"/>; null when there is none or the database cannot be read.</summary>
    CheckpointRow? Latest(string hierDir, string team, string? ts = null);

    /// <summary>The record with <paramref name="key"/> (normalized as <see cref="Key"/> does), or null.</summary>
    CheckpointRow? ByKey(string key);
}

/// <summary>The <c>links</c> table (per-dir-files-to-db S11b): one record per hierarchy dir and team, the JSON the old <c>links/&lt;team&gt;.json</c> held.</summary>
public interface ILinkStore
{
    /// <summary>The record of <paramref name="team"/> under <paramref name="hierDir"/> (JSON text), or null; rows only (the import pass takes <c>links/&lt;team&gt;.json</c>).</summary>
    string? Body(string hierDir, string team);

    /// <summary>The same read inside a write's transaction, the one take of the file included.</summary>
    string? Body(IDbOpTransaction tx, string hierDir, string team);

    /// <summary>Stores <paramref name="body"/> (JSON text) as the record of <paramref name="team"/> under <paramref name="hierDir"/>.</summary>
    void Put(IDbOpTransaction tx, string hierDir, string team, string body);
}

// per-dir-files-to-db S10

/// <summary>The <c>asks</c> table: a hierarchy dir's <c>asks.jsonl</c>, <c>asks-gate.jsonl</c> and <c>parked-questions.jsonl</c>, each file taken into it once.</summary>
public interface IAskSource
{
    /// <summary>Whether <paramref name="path"/> names one of the logs the table holds.</summary>
    bool Holds(string path);

    /// <summary>
    /// One read of the log (§6c G5): its stored lines in append order, rows only (the import pass takes its file). <see cref="AskLog.Unreadable"/> when the store cannot be read, with no lines.
    /// Null when the log is read from its file: <paramref name="path"/> is no asks log, there is no database, or <c>mode.asks</c> is
    /// <c>files</c>.
    /// </summary>
    AskLog? Read(string path);

    /// <summary>
    /// Whether the table has taken <paramref name="path"/>'s file, from the take record alone: no rows read, nothing taken. True too
    /// when the store cannot be read, so a file the table may hold is never read as current.
    /// </summary>
    bool Taken(string path);
}

// per-dir-files-to-db S11a-holds

/// <summary>
/// A team's own hold as the store answers: a row (<paramref name="Body"/> set), no row (both null), or unreadable
/// (<paramref name="Unreadable"/> set: the database could not answer). Unreadable is never no row: it holds the team (fail closed).
/// </summary>
/// <param name="Body">The hold document as JSON text, verbatim; it may be corrupt.</param>
/// <param name="Unreadable">Why the store could not answer, or null.</param>
public sealed record HoldRead(string? Body, string? Unreadable);

/// <summary>What a hold change writes: <paramref name="Body"/> replaces the team's hold, null deletes it.</summary>
/// <param name="Body">The hold document as JSON text, or null to delete the row.</param>
/// <param name="ArchiveStamp">With <paramref name="ArchiveBody"/>, the <c>hold_archive</c> row (schema 43) written in the same transaction, keyed by this stamp.</param>
/// <param name="ArchiveBody">The archived hold as JSON text.</param>
public sealed record HoldWrite(string? Body, string? ArchiveStamp = null, string? ArchiveBody = null);

/// <summary>The <c>holds</c> table, keyed by (team home, team); the team home's old <c>holds/&lt;team&gt;/hold.json</c> files are taken in once.</summary>
public interface IHoldSource
{
    /// <summary>The team's own hold. <paramref name="team"/> is its folder name (<c>_default</c> for the default team).</summary>
    HoldRead Read(string teamHome, string team);

    /// <summary>
    /// The one write: in one database transaction, in-process, <paramref name="decide"/> gets the row's body (null: no row) and returns
    /// what to write, or null to write nothing. An exception from it propagates with nothing written. Throws
    /// <see cref="InvalidOperationException"/> when the database refuses the write (busy, missing, newer).
    /// </summary>
    void Change(string teamHome, string team, Func<string?, HoldWrite?> decide);

    /// <summary>The member's pause (b456), or null when it was never paused. A read that cannot answer is null: the member is not held quiet.</summary>
    MemberPause? PauseOf(string teamHome, string team, string member) => null;

    /// <summary>Records the member paused, or resumed, at <paramref name="atMs"/>. Throws <see cref="InvalidOperationException"/> when it is not written.</summary>
    void SetPause(string teamHome, string team, string member, bool paused, long atMs) =>
        throw new InvalidOperationException("the member pause needs the state database, which this composition does not provide");
}

// b397 group 2

/// <summary>The home's documents kept as <c>meta</c> rows, one JSON text per key: the ultra-gate choices, the session roles, the permissions ledger.</summary>
public interface IHomeDocs
{
    /// <summary>The text stored under <paramref name="key"/>; null when there is none or the database cannot be read.</summary>
    string? Read(string key);

    /// <summary>The same read, failing closed: false when the database cannot answer; true with null <paramref name="body"/> when there is no row or no database.</summary>
    bool TryRead(string key, out string? body);

    /// <summary>Stores <paramref name="body"/> under <paramref name="key"/> in its own transaction; null deletes it. Throws <see cref="InvalidOperationException"/> when the write did not land.</summary>
    void Write(string key, string? body);
}

/// <summary>The <c>usage_rows</c> table: one row per finished subagent.</summary>
public interface IUsageRows
{
    /// <summary>The rows' JSON text, oldest first; none when the database holds none or cannot be read.</summary>
    IReadOnlyList<string> Bodies();
}

/// <summary>A member's pause (b456): <paramref name="Paused"/> since <paramref name="AtMs"/>, or resumed at it.</summary>
/// <param name="Paused">True while paused; false once resumed.</param>
/// <param name="AtMs">When it was paused or resumed, in Unix ms.</param>
public sealed record MemberPause(bool Paused, long AtMs);
