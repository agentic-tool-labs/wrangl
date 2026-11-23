using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.StateDb;

/// <summary>What a state database can be used for in this process.</summary>
public enum StateDbAvailability
{
    /// <summary>Reads and writes.</summary>
    Ready,

    /// <summary>Reads only: the database was written by a newer schema than this binary knows.</summary>
    ReadOnly,

    /// <summary>The system SQLite library could not be loaded.</summary>
    LibraryMissing,

    /// <summary>The system SQLite library is older than the minimum version.</summary>
    LibraryTooOld,

    /// <summary>The database is absent and this process may not create it, or it could not be opened.</summary>
    Unavailable,

    /// <summary>The database file was damaged: it was moved to <c>state/damaged</c> and nothing is open.</summary>
    Damaged,

    /// <summary>The database is gone and the authority marker says a domain was flipped to it, so it is not recreated empty (<c>store-missing</c>).</summary>
    StoreMissing,
}

/// <summary>How a state database opened, with a sentence that says why when it is not <see cref="StateDbAvailability.Ready"/>.</summary>
/// <param name="Availability">What the database can be used for.</param>
/// <param name="Detail">The reason, ready to print; empty when ready.</param>
public sealed record StateDbStatus(StateDbAvailability Availability, string Detail);

/// <summary>The state database of one ah home, or the reason there is none.</summary>
public interface IStateDb : IDisposable
{
    /// <summary>How the database opened.</summary>
    StateDbStatus Status { get; }

    /// <summary>The file-content cache; every call is a quiet no-op when the database is unavailable.</summary>
    IFileCache FileCache { get; }

    /// <summary>
    /// Folds the write-ahead log back into the database. The daemon calls it on a timer with <paramref name="truncate"/> false, and with
    /// true when it starts and when nothing is waiting, because the hook and CLI connections never checkpoint themselves.
    /// </summary>
    CheckpointOutcome Checkpoint(bool truncate);

    /// <summary>Applies what the hooks left in <c>state/fallback.jsonl</c> while the database was busy; returns the rows updated.</summary>
    int ImportFallback();

    /// <summary>
    /// The daemon's side of <see cref="IStateDbDirect.WriteOnce"/>. A transaction that starts after <paramref name="deadlineAt"/> applies
    /// nothing and returns <see cref="OnceStatus.Expired"/>, because the client has already written the same id itself.
    /// </summary>
    OnceOutcome WriteOnce(string writeId, DateTimeOffset deadlineAt, Func<IStateDbTransaction, string> work);

    /// <summary>Deletes the recorded write ids older than <paramref name="age"/>; returns how many.</summary>
    int PruneAppliedWrites(TimeSpan age);

}

/// <summary>What a checkpoint found: <paramref name="Blocked"/> is true when a reader held part of the log back; the frame counts are SQLite's.</summary>
public sealed record CheckpointOutcome(bool Ok, bool Blocked, int LogFrames, int CheckpointedFrames);

/// <summary>The stores of a long-lived process, whose reads keep their connections for the next one.</summary>
public sealed record PooledStores(ITeamAuthority Teams, ISessionAuthority Sessions, IMailStore Mail, IMsgSource Msgs, IStatusEntryStore StatusEntries, IMessageModeReader Modes, Func<StateDbReaderOpen> OpenReader);

/// <summary>
/// The state database: SQLite in WAL mode, bound to the system library and opened at <c>&lt;ah home&gt;/state/ah.db</c>.
/// SQLite features used, none newer than the minimum version: WAL journal, <c>INSERT ... ON CONFLICT DO UPDATE</c> (3.24),
/// <c>WITHOUT ROWID</c> tables, <c>BEGIN IMMEDIATE</c> transactions, <c>sqlite3_busy_timeout</c>.
/// </summary>
public static class StateDbModule
{
    /// <summary>The oldest SQLite version this code runs on, as <c>sqlite3_libversion_number</c> reports it (3.24.0).</summary>
    public const int MinimumLibraryVersion = 3_024_000;

    /// <summary>The schema version this binary reads and writes.</summary>
    public const int SchemaVersion = 47;

    /// <summary>
    /// The tables whose rows can be rebuilt from files that still own the data (the caches and the teams and members imports), so a
    /// deleted database loses nothing. A cut-over moves its domain's tables from here to <see cref="AuthoritativeTables"/>, and a
    /// test fails until both lists name every table. <c>session_pids</c> and <c>activity</c> are here because the next hook call writes them again.
    /// </summary>
    public static IReadOnlyList<string> DerivedTables { get; } = ["activity", "cache_files", "members", "session_pids", "status_entries", "teams", "timeline_events"];

    /// <summary>The <c>sessions_unplaced</c> rows with no document: every other row is a live registration or an ended session's history, and its document the only copy.</summary>
    public const string UnplacedSessionIsDead = "doc IS NULL";

    /// <summary>
    /// The tables that hold data no file holds: database identity, the applied write ids, rows set aside because they could not be
    /// placed, sessions, whose ended rows are history that exists in no file, and the message stores, which hold bodies, mail and peer
    /// rows that are not derivable from anything else once a domain is cut over. <c>sqlite_sequence</c> holds the highest
    /// <c>peer_records.seq</c> ever given out, which a cursor reader depends on.
    /// </summary>
    public static IReadOnlyList<string> AuthoritativeTables { get; } =
    [
        "adr_history", "adr_links", "adrs", "applied_writes", "asks", "checkpoints", "decisions", "domain_seq", "gates", "hold_archive", "holds", "jobs", "lessons", "links", "mail_acks", "mail_adapters", "mail_attempts", "mail_claims", "mail_entries", "mail_notes", "members_unplaced", "meta", "msg_bodies", "msg_exchanges", "msg_messages",
        "peer_records", "plan_events", "plan_items", "progress_docs", "progress_logs", "qa_jobs", "qa_log", "recover_records", "resources", "roster_records", "rules", "sessions", "sessions_unplaced", "sqlite_sequence", "stall_journal", "team_history", "team_snapshots", "teams_unplaced", "usage_rows",
        "work_attachments", "work_items", "work_links", "work_log", "work_prefixes", "work_repos", "worktree_grant_audit", "worktree_grants",
    ];

    /// <summary>How long a call waits for another connection's lock before it gives up, in milliseconds.</summary>
    public const int BusyTimeoutMs = 50;

    /// <summary>Opens the state database of <paramref name="ahHome"/>.</summary>
    /// <param name="ahHome">The ah home directory.</param>
    /// <param name="mayCreate">
    /// Whether this process may create or rebuild the database file and its schema. Only the daemon passes true; a hook or a CLI verb never
    /// creates or changes the schema.
    /// </param>
    /// <param name="log">Receives one line if the database file is removed while the process runs and cannot be reopened, after which the cache does nothing.</param>
    /// <returns>The database; its status says whether it is usable and, if not, why.</returns>
    public static IStateDb Open(string ahHome, bool mayCreate, Action<string>? log = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(ahHome);
        return StateDbFile.Create(ahHome, mayCreate, log);
    }

    /// <summary>
    /// Creates the state database when it does not exist yet, as the first message verb does (ah-msgs-db-only §13): null when it exists or
    /// was created, else why it could not be, naming its path.
    /// </summary>
    public static string? EnsureDatabase(string ahHome)
    {
        var path = StateDbBackups.DatabasePath(ahHome);
        if (File.Exists(path))
        {
            return null;
        }

        using var db = Open(ahHome, mayCreate: true);
        return db.Status.Availability == StateDbAvailability.Ready ? null : $"cannot create the state database at {path}: {db.Status.Detail}";
    }

    /// <summary>The schema of a database older than this binary's; null for a current, newer, absent or unreadable one. It reads, and never migrates.</summary>
    public static int? SchemaBehind(string ahHome)
    {
        var path = StateDbBackups.DatabasePath(ahHome);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var connection = SqliteConnection.Open(path, SqliteOpenMode.ReadOnly, 250);
            return StateDbFile.ReadVersion(connection) is { } version && version < SchemaVersion ? version : null;
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    /// <summary>The backups of the state database.</summary>
    public static IStateDbBackups CreateBackups() => new StateDbBackupService();

    /// <summary>
    /// Takes in every draft that has no body yet: its file's bytes become revision 1. A draft with no readable file stays without a body and is
    /// logged. The file is left on disk. A second run finds nothing to do (ah-msgs-db-only §10).
    /// </summary>
    public static void ImportMsgDrafts(string ahHome, Action<string> log) => ImportMsgDrafts(ahHome, log, null);

    /// <summary>
    /// <see cref="ImportMsgDrafts(string, Action{string})"/>, except that with <paramref name="overCap"/> a file over the body limit is stored cut to it, with a
    /// note saying so, and reported there, so the message still reads by id. The schema holds no longer body; the file itself stays for the archive.
    /// </summary>
    public static void ImportMsgDrafts(string ahHome, Action<string> log, Action<string>? overCap)
    {
        ArgumentNullException.ThrowIfNull(log);
        var direct = OpenDirect(ahHome);
        var drafts = direct.Write(tx => tx.Query("SELECT pk, id, type, draft_path FROM msg_messages WHERE state = 'draft' AND body_rev IS NULL"));
        if (!drafts.Ok)
        {
            log("msgs import: " + drafts.Message);
            return;
        }

        // The files are read here, outside any transaction: a write transaction may not wait on a file.
        var read = new List<(object? Pk, byte[] Bytes, long MtimeMs)>();
        foreach (var row in drafts.Value!)
        {
            var path = row[3] as string ?? string.Empty;
            try
            {
                var bytes = File.Exists(path) ? File.ReadAllBytes(path) : null;
                if (bytes is not null && bytes.Length > MsgRows.BodyCap && overCap is not null)
                {
                    overCap($"message {row[1]} {row[2]} at {path} is {bytes.Length} bytes, over the {MsgRows.BodyCap} limit; the store holds it cut, and the whole file goes to msgs.files-*");
                    var note = System.Text.Encoding.UTF8.GetBytes($"\n\n[cut at import: {bytes.Length} bytes, over the {MsgRows.BodyCap} limit; the whole file is in a msgs.files-* folder beside msgs]\n");
                    var end = MsgRows.BodyCap - note.Length;
                    while (end > 0 && (bytes[end] & 0xC0) == 0x80)
                    {
                        end--; // not inside a UTF-8 sequence
                    }

                    bytes = [.. bytes.AsSpan(0, end), .. note];
                }

                if (bytes is null || bytes.Length > MsgRows.BodyCap)
                {
                    log(bytes is null
                        ? $"msgs import: draft {row[1]} {row[2]} has no file at {path}; left empty"
                        : $"msgs import: draft {row[1]} {row[2]} at {path} is {bytes.Length} bytes, over the {MsgRows.BodyCap} limit; left empty");
                    continue;
                }

                read.Add((row[0], bytes, new DateTimeOffset(File.GetLastWriteTimeUtc(path)).ToUnixTimeMilliseconds()));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                log($"msgs import: draft {row[1]} {row[2]} cannot read {path}: {e.Message}");
            }
        }

        if (read.Count == 0)
        {
            return;
        }

        var written = direct.Write(tx =>
        {
            var imported = 0;
            foreach (var (pk, bytes, mtimeMs) in read)
            {
                // A fill since the list above has given the draft its own body, and that body wins.
                if (tx.Query("SELECT 1 FROM msg_messages WHERE pk = ?1 AND state = 'draft' AND body_rev IS NULL", pk).Count == 0)
                {
                    continue;
                }

                tx.Execute("INSERT INTO msg_bodies(msg_pk, rev, bytes, sha256, sealed_at_ms, sealed_by) VALUES (?1, 1, ?2, ?3, ?4, 'import')", pk, bytes, MsgRows.Sha256Hex(bytes), mtimeMs);
                tx.Execute("UPDATE msg_messages SET body_rev = 1 WHERE pk = ?1", pk);
                imported++;
            }

            if (imported > 0)
            {
                tx.Execute("INSERT INTO domain_seq(domain, seq, changed_ms) VALUES ('msgs', 1, ?1) ON CONFLICT(domain) DO UPDATE SET seq = seq + 1, changed_ms = ?1", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            }

            return imported;
        });
        if (!written.Ok)
        {
            log("msgs import: " + written.Message);
        }
    }

    /// <summary>The writer for a process that cannot hand its write to the daemon.</summary>
    public static IStateDbDirect OpenDirect(string ahHome) => new StateDbDirect(ahHome, DirectWriteOptions.Default);

    private static Func<int, string?>? s_directWriteHung;

    /// <summary>
    /// What a direct write does when it gave up on a busy database while an ahd (the pid given) is recorded as running: the
    /// composition root of a command line process sets it to start <c>ahd heal</c>, and its answer is added to the write's failure
    /// text. Unset (a hook, the daemon, a test) nothing happens. The write fails either way and is never retried here.
    /// </summary>
    public static void OnDirectWriteHung(Func<int, string?>? react) => Volatile.Write(ref s_directWriteHung, react);

    internal static string? DirectWriteHung(int daemonPid)
    {
        try
        {
            return Volatile.Read(ref s_directWriteHung)?.Invoke(daemonPid);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return null; // the write's own failure is what the caller must see
        }
    }

    /// <summary>
    /// Refreshes the query planner's statistics of the state database of <paramref name="ahHome"/> with a bounded <c>ANALYZE</c>:
    /// <c>analyze</c>, or <c>failed: reason</c>. Never throws; a database that does not exist is a failure, not a creation.
    /// </summary>
    /// <param name="ahHome">The ah home directory.</param>
    /// <returns>The work done, or the failure.</returns>
    public static string RefreshStats(string ahHome) => StateDbStats.TryRefresh(new StateDbDirect(ahHome, DirectWriteOptions.Message));

    /// <summary>The runner of DB-backed message writes over a direct connection with the message busy wait.</summary>
    public static IDbOpRunner CreateOpRunner(string ahHome, bool forHook = false) =>
        new StateDbOpRunner(new StateDbDirect(ahHome, forHook ? DirectWriteOptions.Hook : DirectWriteOptions.Message), ahHome);

    /// <summary>The lock file a files-path write of <paramref name="domain"/> takes for <paramref name="stateDirectory"/> and <paramref name="lockName"/>, under <c>&lt;AH_HOME&gt;/state/locks/</c>.</summary>
    public static string StateLockPath(string ahHome, string domain, string stateDirectory, string lockName) => StateLocks.PathFor(ahHome, domain, stateDirectory, lockName);

    /// <summary>The directory holding every state-op lock of <paramref name="ahHome"/>.</summary>
    public static string StateLockDirectory(string ahHome) => StateLocks.Directory(ahHome);

    /// <summary>The reader of a message domain's mode row.</summary>
    public static IMessageModeReader CreateModeReader(string ahHome) => new StateDbModeReader(ahHome);

    /// <summary>The peer log read from the database; it answers with the rows and the rules the file does.</summary>
    public static IPeerRecordSource CreatePeerRecords(string ahHome) => new StateDbPeerRecords(ahHome);

    /// <summary>The messages read from the database.</summary>
    public static IMsgSource CreateMsgSource(string ahHome) => new StateDbMsgReader(ahHome);

    /// <summary>The session pids (<c>session_pids</c>) of the state database of <paramref name="ahHome"/>.</summary>
    public static ISessionPidSource CreateSessionPids(string ahHome) => new StateDbSessionPids(ahHome);

    /// <summary>The activity records (<c>activity</c>) of the state database of <paramref name="ahHome"/>.</summary>
    public static IActivitySource CreateActivity(string ahHome) => new StateDbActivity(ahHome);

    /// <summary>The gate log (<c>gates</c>) of the state database of <paramref name="ahHome"/>.</summary>
    public static IGateSource CreateGates(string ahHome) => new StateDbGates(ahHome);

    /// <summary>The tracked-job records (<c>jobs</c>) of the state database of <paramref name="ahHome"/>.</summary>
    public static IJobSource CreateJobs(string ahHome) => new StateDbJobs(ahHome);

    /// <summary>
    /// The write counter of <paramref name="domain"/> in the state database of <paramref name="ahHome"/> (0 before any write), or null when the
    /// database cannot be read: a cache keyed on it then reads afresh.
    /// </summary>
    public static long? DomainSeq(string ahHome, string domain)
    {
        if (StateDbAuthority.NeverCreated(ahHome))
        {
            return null;
        }

        try
        {
            return StateDbStoreReads.Reading(ahHome, connection => StateDbRows.Run(connection, "SELECT seq FROM domain_seq WHERE domain = ?1", [domain]) is { Count: > 0 } rows ? (long)rows[0][0]! : 0L);
        }
        catch (Exception e) when (e is StoreUnavailableException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The QA job queue (<c>qa_jobs</c>) of the state database of <paramref name="ahHome"/>: each job's JSON text, in first-written order.</summary>
    public static IReadOnlyList<string> QaJobBodies(string ahHome) => StateDbHomeRows.QaJobBodies(ahHome);

    /// <summary>The named-resource rows (<c>resources</c>, adr-36) of the state database of <paramref name="ahHome"/>, dead ones included, oldest first.</summary>
    public static IReadOnlyList<ResourceRow> Resources(string ahHome) => StateDbHomeRows.Resources(ahHome);

    /// <summary>The known-dirs index (<c>meta</c> rows) of the state database of <paramref name="ahHome"/>: each dir and its last-seen ISO time, oldest first.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> KnownDirs(string ahHome) => StateDbHomeRows.KnownDirs(ahHome);

    /// <summary>The decision logs (<c>decisions</c>, per-dir-files-to-db S9) of the state database of <paramref name="ahHome"/>.</summary>
    public static IDecisionSource CreateDecisions(string ahHome) => new StateDbDecisions(ahHome);

    /// <summary>The asks logs (<c>asks</c>, per-dir-files-to-db S10) of the state database of <paramref name="ahHome"/>.</summary>
    public static IAskSource CreateAsks(string ahHome) => new StateDbAsks(ahHome);

    /// <summary>
    /// The grants documents and worktree audit rows (<c>worktree_grants</c>, <c>worktree_grant_audit</c>, per-dir-files-to-db S11e) of
    /// the state database of <paramref name="ahHome"/>; <paramref name="oldAuditFile"/> is the pre-upgrade audit log, read for a team
    /// with no audit row in the table.
    /// </summary>
    public static IGrantSource CreateGrants(string ahHome, string oldAuditFile) => new StateDbGrants(ahHome, oldAuditFile);

    /// <summary>Whether the state database of <paramref name="ahHome"/> was ever created (its file or its marker exists).</summary>
    public static bool DatabaseCreated(string ahHome) => !StateDbAuthority.NeverCreated(ahHome);

    /// <summary>The persisted status inputs (<c>status_entries</c>) of the state database of <paramref name="ahHome"/>.</summary>
    public static IStatusEntryStore CreateStatusEntries(string ahHome) => new StateDbStatusEntries(ahHome);

    /// <summary>Whether the database records <paramref name="domain"/> as flipped to it; a database that cannot be read says yes, because nothing then proves it was not.</summary>
    public static bool DomainFlipped(string ahHome, string domain) => StateDbAuthority.FlipRecorded(ahHome, domain);

    /// <summary>The hierarchy directories the session registrations under <paramref name="ahHome"/> name, through their team files.</summary>
    public static IReadOnlyList<string> RegisteredHierarchyDirs(string ahHome) =>
        [.. StateDbSessions.Read(ahHome).Rows.SelectMany(static r => r.TeamFiles ?? []).Select(StateDbTeamKey.Of).OfType<(string HierDir, string Name)>().Select(static k => k.HierDir).Distinct(StringComparer.Ordinal)];

    /// <summary>The dirs the files import pass runs over: <paramref name="listed"/> plus the sessions' dirs, deduped by real path, gone dirs left out (files-off-disk §3.3).</summary>
    public static IReadOnlyList<string> FilesImportDirs(string ahHome, IEnumerable<string> listed) => StateDbFilesImport.Dirs(ahHome, listed);

    /// <summary>
    /// The files import pass over <paramref name="hierDir"/> (files-off-disk §3): the record it stored, or null when the dir is gone, there is
    /// no database, or (without <paramref name="force"/>) the dir is already complete.
    /// </summary>
    public static string? ImportHierDirFiles(string ahHome, string hierDir, bool force = false) => StateDbFilesImport.Import(ahHome, hierDir, force);

    /// <summary>The import, verify and export of message files into and out of the message tables; it never flips the mode.</summary>
    public static IMsgsCutover CreateMsgsCutover() => new StateDbMsgsCutover();

    /// <summary>The team domain's authority over the database for a process that writes it directly; it never migrates and never queues.</summary>
    public static ITeamAuthority OpenTeamAuthority(string ahHome, bool forHook = false) =>
        new StateDbTeamAuthority(ahHome, new StateDbDirect(ahHome, forHook ? DirectWriteOptions.Hook : DirectWriteOptions.Default));

    /// <summary>The mail tables as the mailbox reads them.</summary>
    public static IMailStore OpenMailStore(string ahHome) => new StateDbMailStore(ahHome);

    private sealed record SharedReads(string Path, StateDbReadPool Pool);

    // b434: the pool OpenPooledStores composed, which the reads naming no pool of that one database use; one per process, so its descriptors stay bounded
    private static SharedReads? s_sharedReads;

    internal static StateDbReadPool? SharedReadPool(string databasePath) =>
        Volatile.Read(ref s_sharedReads) is { } shared && shared.Path == databasePath ? shared.Pool : null;

    /// <summary>The stores and the reader of a long-lived process, sharing one <see cref="StateDbReadPool"/>; <see cref="PooledStores.OpenReader"/> keeps <see cref="OpenReader"/>'s contract.</summary>
    public static PooledStores OpenPooledStores(string ahHome)
    {
        var reads = new StateDbReadPool();
        Interlocked.Exchange(ref s_sharedReads, new SharedReads(StateDbBackups.DatabasePath(ahHome), reads))?.Pool.Close();
        return new PooledStores(
            new StateDbTeamAuthority(ahHome, new StateDbDirect(ahHome, DirectWriteOptions.Default), reads),
            new StateDbSessionAuthority(ahHome, new StateDbDirect(ahHome, DirectWriteOptions.Default), reads),
            new StateDbMailStore(ahHome, reads),
            new StateDbMsgReader(ahHome, reads),
            new StateDbStatusEntries(ahHome, reads),
            new StateDbModeReader(ahHome, reads),
            () => StateDbReader.Open(ahHome, reads));
    }

    /// <summary>Makes the mail domain the database's, with the mode row and the authority marker agreeing; no data moves. Why not, or null when it is done.</summary>
    public static string? FlipMailToDatabase(string ahHome)
    {
        if (EnsureDatabase(ahHome) is { } why)
        {
            return why;
        }

        var flipped = new StateDbDirect(ahHome, DirectWriteOptions.Default).Write(tx =>
        {
            StateDbCutoverKit.Flip(tx, "mail", DateTimeOffset.UtcNow);
            return 0;
        });
        return flipped.Ok ? null : flipped.Message;
    }

    /// <summary>The session domain's authority over the database for a process that writes it directly; it never migrates and never queues.</summary>
    public static ISessionAuthority OpenSessionAuthority(string ahHome, bool forHook = false) =>
        new StateDbSessionAuthority(ahHome, new StateDbDirect(ahHome, forHook ? DirectWriteOptions.Hook : DirectWriteOptions.Default));

    /// <summary>A reader that never waits for a writer; its <see cref="StateDbReaderOpen.Reader"/> is null when the database is absent or unreadable.</summary>
    public static StateDbReaderOpen OpenReader(string ahHome) => StateDbReader.Open(ahHome);

    /// <summary>The writer the liveness hooks use, held to the hook's 100 ms budget.</summary>
    public static ILivenessWriter OpenLiveness(string ahHome) => new StateDbLiveness(ahHome, StateDbLiveness.HookBudgetMs);

    /// <summary>The opener of other SQLite files through the same binding, for a store that keeps its own file.</summary>
    /// <summary>At ahd start-up: when the authority marker says the store is gone or was replaced, restores the newest backup that passes <c>integrity_check</c> and carries the marker's <c>db_id</c>.</summary>
    public static RecoveryResult RecoverStore(string ahHome, DateTimeOffset now) => StateDbRecovery.Recover(ahHome, now);

    /// <summary>Whether <see cref="RecoverStore"/> would look for a backup to restore, the same store check, changing nothing.</summary>
    public static bool StoreNeedsRecovery(string ahHome) => StateDbRecovery.Needed(StateDbAuthority.Check(ahHome, 4));

    public static ISqliteOpener CreateOpener() => new SqliteOpener();

    private sealed class SqliteOpener : ISqliteOpener
    {
        public ISqliteConnection Open(string path, SqliteOpenMode flags, int busyTimeoutMs) => SqliteConnection.Open(path, flags, busyTimeoutMs);

        public bool IsDamage(Exception failure) => failure is SqliteException { IsDamage: true };

        public bool IsBusy(Exception failure) => failure is SqliteException { IsBusy: true };
    }

    // per-dir-files-to-db S6

    /// <summary>The user's question log (<c>qa_log</c>) of the state database of <paramref name="ahHome"/>.</summary>
    public static IQaLogSource CreateQaLog(string ahHome) => new StateDbQaLog(ahHome);

    // per-dir-files-to-db S7
    /// <summary>The peer roster (<c>roster_records</c>) of the state database of <paramref name="ahHome"/>.</summary>
    public static IRosterRecordSource CreateRosterRecords(string ahHome) => new StateDbRosterRecords(ahHome);

    // per-dir-files-to-db S11c

    /// <summary>The progress logs (<c>progress_logs</c>) of the state database of <paramref name="ahHome"/>.</summary>
    public static IProgressLogSource CreateProgressLogs(string ahHome) => new StateDbProgressLogs(ahHome);

    // per-dir-files-to-db S11a-ckpt

    /// <summary>The checkpoint records (<c>checkpoints</c>) of the state database of <paramref name="ahHome"/>.</summary>
    public static ICheckpointSource CreateCheckpoints(string ahHome) => new StateDbCheckpoints(ahHome);

    // per-dir-files-to-db S11b
    /// <summary>The mesh links (<c>links</c>) of the state database of <paramref name="ahHome"/>.</summary>
    public static ILinkStore CreateLinks(string ahHome) => new StateDbLinks(ahHome);

    // per-dir-files-to-db S11a-holds

    /// <summary>The team holds (<c>holds</c>) of the state database of <paramref name="ahHome"/>.</summary>
    public static IHoldSource CreateHolds(string ahHome) => new StateDbHolds(ahHome);

    /// <summary>The bare-member recovery records (<c>recover_records</c>) of the state database of <paramref name="ahHome"/>.</summary>
    public static IRecoverRecordStore CreateRecoverRecords(string ahHome) => new StateDbRecoverRecords(ahHome);

    // per-dir-files-to-db S11d

    /// <summary>The progress documents (<c>progress_docs</c>) of the state database of <paramref name="ahHome"/>.</summary>
    public static IProgressDocSource CreateProgressDocs(string ahHome) => new StateDbProgressDocs(ahHome);

    // b397 group 2

    /// <summary>The home documents (<c>meta</c> rows) of the state database of <paramref name="ahHome"/>.</summary>
    public static IHomeDocs CreateHomeDocs(string ahHome) => new StateDbHomeDocs(ahHome);

    /// <summary>The subagent usage rows (<c>usage_rows</c>) of the state database of <paramref name="ahHome"/>.</summary>
    public static IUsageRows CreateUsageRows(string ahHome) => new StateDbUsageRows(ahHome);
}
