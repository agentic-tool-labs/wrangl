using Ah.Core.Json;

namespace Ah.StateDb;

/// <summary>One step of the schema: what takes a database from the version before <paramref name="To"/> to <paramref name="To"/>.</summary>
/// <param name="To">The version the database has once the step has run.</param>
/// <param name="Run">Changes the schema or data; it runs inside one transaction, so a failure leaves the database as it was.</param>
/// <param name="Prepare">Reads whatever the step needs from outside the database, before its transaction starts, and returns what <paramref name="Run"/> would be; the step itself then only writes.</param>
internal sealed record MigrationStep(int To, Action<SqliteConnection>? Run, Func<SqliteConnection, string, Action<SqliteConnection>>? Prepare = null);

/// <summary>The schema a database is opened against: the version this binary reads and writes, the steps that reach it from version 1, and the clock.</summary>
/// <param name="Version">The version this binary reads and writes.</param>
/// <param name="Steps">The steps, in order, each one version above the last.</param>
/// <param name="Now">The time backups and quarantined files are named for.</param>
internal sealed record SchemaPlan(int Version, IReadOnlyList<MigrationStep> Steps, Func<DateTimeOffset> Now)
{
    /// <summary>How the connection is configured; the daemon and record writers keep the default, the liveness hooks and the CLI ask for less.</summary>
    internal StateDbOpenOptions Options { get; init; } = StateDbOpenOptions.Default;

    /// <summary>The plan of this binary.</summary>
    internal static SchemaPlan Production { get; } = new(StateDbModule.SchemaVersion, StateDbMigrations.Steps, () => DateTimeOffset.UtcNow);
}

/// <summary>The migrations of the state database, one per schema version above 1; a fresh database is made at version 1 and migrated up.</summary>
internal static class StateDbMigrations
{
    /// <summary>
    /// Version 2, which has shipped and is never edited: the tables the team and session identity move into. <c>doc</c> keeps the whole
    /// team record as written, so the exported team file can be rebuilt without loss; the other columns are the projection that
    /// queries use. <c>ah_version</c> is the build that registered the session, which the cut-over guard compares.
    /// </summary>
    internal static IReadOnlyList<string> V2Statements { get; } =
    [
        "CREATE TABLE teams(team TEXT PRIMARY KEY, team_id TEXT, parent_team TEXT, orchestrator_name TEXT, gate_mode TEXT, doc TEXT NOT NULL, created_at INTEGER NOT NULL, updated_at INTEGER NOT NULL) WITHOUT ROWID",
        "CREATE TABLE sessions(session_id TEXT PRIMARY KEY, team TEXT, member TEXT, pid INTEGER, pid_start TEXT, cwd TEXT, pane_id TEXT, transcript_path TEXT, source TEXT, ah_version TEXT, started_at INTEGER NOT NULL, last_seen_at INTEGER NOT NULL, ended_at INTEGER) WITHOUT ROWID",
        "CREATE INDEX sessions_member ON sessions(team, member)",
        "CREATE TABLE members(team TEXT NOT NULL, name TEXT NOT NULL, role TEXT, kind TEXT, route TEXT, model TEXT, effort TEXT, auto_mode TEXT, args TEXT, stream TEXT, expected_root TEXT, pane_id TEXT, session_id TEXT, transcript_path TEXT, cwd TEXT, pid INTEGER, pid_start TEXT, state TEXT NOT NULL DEFAULT 'active' CHECK(state IN ('active','dismissed','untracked')), last_seen_at INTEGER, updated_at INTEGER NOT NULL, PRIMARY KEY(team, name)) WITHOUT ROWID",
    ];

    /// <summary>Version 3: the ids of writes already applied, which make a write that reaches both the daemon and the direct path land once.</summary>
    internal static IReadOnlyList<string> V3Statements { get; } =
    [
        "CREATE TABLE applied_writes(write_id TEXT PRIMARY KEY, applied_at INTEGER NOT NULL, path TEXT NOT NULL CHECK(path IN ('daemon','direct')), result TEXT) WITHOUT ROWID",
        "CREATE INDEX applied_writes_at ON applied_writes(applied_at)",
    ];

    /// <summary>
    /// Version 4, schema part: a team is told apart by the hierarchy directory its file lives in as well as by its name, so the same
    /// team name in two directories is two teams. Teams, members and sessions are rebuilt with <c>hier_dir</c>, which must be an
    /// absolute path, so an empty or relative one can never be stored. The rows of version 3 are held in <c>*_v3</c> tables until
    /// <see cref="Version4"/> has placed or quarantined every one.
    /// </summary>
    internal static IReadOnlyList<string> V4Statements { get; } =
    [
        "DROP INDEX sessions_member",
        "ALTER TABLE teams RENAME TO teams_v3",
        "ALTER TABLE members RENAME TO members_v3",
        "ALTER TABLE sessions RENAME TO sessions_v3",
        "CREATE TABLE teams(hier_dir TEXT NOT NULL CHECK(hier_dir <> '' AND substr(hier_dir, 1, 1) = '/'), team TEXT NOT NULL, team_id TEXT, parent_team TEXT, orchestrator_name TEXT, gate_mode TEXT, doc TEXT NOT NULL, created_at INTEGER NOT NULL, updated_at INTEGER NOT NULL, PRIMARY KEY(hier_dir, team)) WITHOUT ROWID",
        "CREATE TABLE members(hier_dir TEXT NOT NULL CHECK(hier_dir <> '' AND substr(hier_dir, 1, 1) = '/'), team TEXT NOT NULL, name TEXT NOT NULL, role TEXT, kind TEXT, route TEXT, model TEXT, effort TEXT, auto_mode TEXT, args TEXT, stream TEXT, expected_root TEXT, pane_id TEXT, session_id TEXT, transcript_path TEXT, cwd TEXT, pid INTEGER, pid_start TEXT, state TEXT NOT NULL DEFAULT 'active' CHECK(state IN ('active','dismissed','untracked')), last_seen_at INTEGER, updated_at INTEGER NOT NULL, PRIMARY KEY(hier_dir, team, name)) WITHOUT ROWID",
        "CREATE TABLE sessions(session_id TEXT PRIMARY KEY, hier_dir TEXT NOT NULL CHECK(hier_dir <> '' AND substr(hier_dir, 1, 1) = '/'), team TEXT, member TEXT, pid INTEGER, pid_start TEXT, cwd TEXT, pane_id TEXT, transcript_path TEXT, source TEXT, ah_version TEXT, started_at INTEGER NOT NULL, last_seen_at INTEGER NOT NULL, ended_at INTEGER) WITHOUT ROWID",
        "CREATE INDEX sessions_member ON sessions(hier_dir, team, member)",
        "CREATE TABLE teams_unplaced AS SELECT * FROM teams_v3 WHERE 0",
        "CREATE TABLE members_unplaced AS SELECT * FROM members_v3 WHERE 0",
        "CREATE TABLE sessions_unplaced(session_id TEXT PRIMARY KEY, reason TEXT NOT NULL, team TEXT, member TEXT, pid INTEGER, pid_start TEXT, cwd TEXT, pane_id TEXT, transcript_path TEXT, source TEXT, ah_version TEXT, started_at INTEGER NOT NULL, last_seen_at INTEGER NOT NULL, ended_at INTEGER) WITHOUT ROWID",
    ];

    /// <summary>
    /// Version 5: the message stores. Each table mirrors a file format and keeps the original bytes in <c>raw</c> where the format has
    /// rows, so an export is byte-identical and a row that does not parse is kept. <c>domain_seq</c> counts the write transactions of a
    /// domain, which readers use as a change token.
    /// </summary>
    internal static IReadOnlyList<string> V5Statements { get; } =
    [
        "CREATE TABLE msg_messages(pk INTEGER PRIMARY KEY, pool_dir TEXT NOT NULL, id TEXT NOT NULL, type TEXT NOT NULL, \"to\" TEXT NOT NULL DEFAULT '', \"from\" TEXT, to_name TEXT, from_name TEXT, slug TEXT NOT NULL DEFAULT '', team TEXT, parent TEXT, created TEXT, created_ms INTEGER, state TEXT NOT NULL CHECK(state IN ('draft','sealed','archived')), draft_path TEXT, export_path TEXT, body_rev INTEGER, export_sha TEXT)",
        "CREATE UNIQUE INDEX msg_messages_key ON msg_messages(pool_dir, id, type, \"to\", slug)",
        "CREATE INDEX msg_messages_id ON msg_messages(id)",
        "CREATE INDEX msg_messages_draft ON msg_messages(state) WHERE state = 'draft'",
        "CREATE INDEX msg_messages_export_pending ON msg_messages(export_sha) WHERE export_sha IS NULL",
        "CREATE TABLE msg_bodies(msg_pk INTEGER NOT NULL, rev INTEGER NOT NULL, bytes BLOB NOT NULL CHECK(length(bytes) <= 262144), sha256 TEXT NOT NULL, sealed_at_ms INTEGER NOT NULL, sealed_by TEXT, PRIMARY KEY(msg_pk, rev)) WITHOUT ROWID",
        "CREATE TABLE msg_exchanges(pool_dir TEXT NOT NULL, id TEXT NOT NULL, request_pk INTEGER NOT NULL, response_pk INTEGER, state TEXT NOT NULL CHECK(state IN ('open','answered','closed','retired','archived')), opened_ms INTEGER, closed_ms INTEGER, closed_by TEXT, reason TEXT, team TEXT, \"to\" TEXT, \"from\" TEXT, PRIMARY KEY(pool_dir, id)) WITHOUT ROWID",
        "CREATE INDEX msg_exchanges_live ON msg_exchanges(state) WHERE state IN ('open','answered')",
        "CREATE INDEX msg_exchanges_id ON msg_exchanges(id)",
        "CREATE INDEX msg_exchanges_team ON msg_exchanges(team, state)",
        "CREATE TABLE mail_entries(pk INTEGER PRIMARY KEY, box TEXT NOT NULL, id TEXT NOT NULL, kind TEXT, \"from\" TEXT, \"to\" TEXT, to_session TEXT, ref TEXT, ref_sha256 TEXT, created_ms INTEGER, raw BLOB NOT NULL)",
        "CREATE UNIQUE INDEX mail_entries_id ON mail_entries(box, id)",
        "CREATE UNIQUE INDEX mail_entries_report ON mail_entries(box, \"from\", ref, ref_sha256) WHERE kind = 'report'",
        "CREATE INDEX mail_entries_created ON mail_entries(box, created_ms)",
        "CREATE TABLE mail_acks(box TEXT NOT NULL, id TEXT NOT NULL, at TEXT, by TEXT, reason TEXT, session_id TEXT, raw BLOB NOT NULL, PRIMARY KEY(box, id)) WITHOUT ROWID",
        "CREATE TABLE mail_attempts(pk INTEGER PRIMARY KEY, box TEXT NOT NULL, id TEXT NOT NULL, rung TEXT, state TEXT, ladder INTEGER NOT NULL DEFAULT 0, ladder_seq INTEGER, at_ms INTEGER, raw BLOB NOT NULL)",
        "CREATE INDEX mail_attempts_box ON mail_attempts(box, id, pk)",
        "CREATE UNIQUE INDEX mail_attempts_ladder ON mail_attempts(box, id, ladder_seq) WHERE ladder <> 0",
        "CREATE TABLE mail_claims(box TEXT NOT NULL, id TEXT NOT NULL, state TEXT, at_ms INTEGER, raw BLOB NOT NULL, PRIMARY KEY(box, id)) WITHOUT ROWID",
        "CREATE TABLE peer_records(seq INTEGER PRIMARY KEY AUTOINCREMENT, type TEXT, session_id TEXT, request_id TEXT, task TEXT, \"from\" TEXT, agent_id TEXT, op_id TEXT, ts_ms INTEGER, raw BLOB NOT NULL, type_truthy INTEGER NOT NULL DEFAULT 0, peer_key TEXT NOT NULL DEFAULT '' COLLATE BINARY, from_name TEXT)",
        "CREATE UNIQUE INDEX peer_records_op ON peer_records(op_id) WHERE op_id IS NOT NULL",
        "CREATE INDEX peer_records_type_truthy ON peer_records(type_truthy)",
        "CREATE INDEX peer_records_key ON peer_records(peer_key)",
        "CREATE INDEX peer_records_from_name ON peer_records(from_name) WHERE from_name IS NOT NULL",
        "CREATE INDEX peer_records_request ON peer_records(request_id)",
        "CREATE INDEX peer_records_session ON peer_records(type, session_id)",
        "CREATE INDEX peer_records_ts ON peer_records(ts_ms)",
        "CREATE TABLE roster_records(seq INTEGER PRIMARY KEY, pool_dir TEXT NOT NULL, session_id TEXT, at_ms INTEGER, raw BLOB NOT NULL)",
        "CREATE INDEX roster_records_pool ON roster_records(pool_dir, session_id, seq)",
        "CREATE TABLE domain_seq(domain TEXT PRIMARY KEY, seq INTEGER NOT NULL, changed_ms INTEGER NOT NULL) WITHOUT ROWID",
    ];

    /// <summary>
    /// Version 7: the whole registration as written, so a session's registration file can be regenerated from its row byte for byte.
    /// A row stored before this version has no document until the next import or write of that session fills it.
    /// </summary>
    internal static IReadOnlyList<string> V7Statements { get; } =
    [
        "ALTER TABLE sessions ADD COLUMN doc TEXT",
        "ALTER TABLE sessions_unplaced ADD COLUMN doc TEXT",

        // the live registrations, which a cut-over judges and the import's end pass looks through: only the rows without an end are in the index
        "CREATE INDEX IF NOT EXISTS sessions_live ON sessions(last_seen_at) WHERE ended_at IS NULL",
        "CREATE INDEX IF NOT EXISTS sessions_unplaced_live ON sessions_unplaced(last_seen_at) WHERE ended_at IS NULL AND doc IS NOT NULL",
    ];

    /// <summary>Why a stored session cannot be placed.</summary>
    internal readonly record struct Placement(string? HierDir, string Reason);

    /// <summary>
    /// Version 4, reading part, run before the step's transaction: each stored session is placed from its own data, the registration
    /// file of its session id and the team file in it, run through the same path resolution as every other key. A session id that
    /// is not a plain file name is never turned into a path.
    /// </summary>
    private static Dictionary<string, Placement> PlaceStoredSessions(SqliteConnection connection, string ahHome)
    {
        Dictionary<string, Placement> placements = new(StringComparer.Ordinal);
        foreach (var row in StateDbRows.Run(connection, "SELECT session_id FROM sessions", []))
        {
            var id = (string)row[0]!;
            var plain = id.Length > 0 && id.IndexOfAny(['/', '\\']) < 0 && !id.Contains("..", StringComparison.Ordinal);
            var registration = plain ? StateDbSessions.ReadNames(ahHome, [id + ".json"]).FirstOrDefault(r => r.SessionId == id) : null;
            placements[id] = registration is null
                ? new Placement(null, StateDbSessions.NoRegistration)
                : registration.HierDir.Length == 0 ? new Placement(null, StateDbSessions.NoTeamFile) : new Placement(registration.HierDir, string.Empty);
        }

        return placements;
    }

    /// <summary>
    /// Version 4, writing part. A session with no registration, or one whose registration names no team file, is copied to
    /// <c>sessions_unplaced</c> with its reason, never dropped and never given a guess. Version 3 teams and members had no writer, so
    /// any that exist are copied to their <c>*_unplaced</c> tables. The counts are kept in <c>meta</c> for the doctor. It only writes.
    /// </summary>
    private static void Version4(SqliteConnection connection, Dictionary<string, Placement> placements)
    {
        Run(connection, V4Statements);
        var unplacedSessions = 0;
        foreach (var row in StateDbRows.Run(connection, "SELECT session_id FROM sessions_v3", []))
        {
            var id = (string)row[0]!;
            var placement = placements.TryGetValue(id, out var found) ? found : new Placement(null, StateDbSessions.NoRegistration);
            if (placement.HierDir is not { Length: > 0 } hierDir)
            {
                _ = StateDbRows.Run(
                    connection,
                    "INSERT INTO sessions_unplaced(session_id, reason, team, member, pid, pid_start, cwd, pane_id, transcript_path, source, ah_version, started_at, last_seen_at, ended_at) " +
                    "SELECT session_id, ?2, team, member, pid, pid_start, cwd, pane_id, transcript_path, source, ah_version, started_at, last_seen_at, ended_at FROM sessions_v3 WHERE session_id = ?1",
                    [id, placement.Reason]);
                unplacedSessions++;
                continue;
            }

            _ = StateDbRows.Run(
                connection,
                "INSERT INTO sessions(session_id, hier_dir, team, member, pid, pid_start, cwd, pane_id, transcript_path, source, ah_version, started_at, last_seen_at, ended_at) " +
                "SELECT session_id, ?2, team, member, pid, pid_start, cwd, pane_id, transcript_path, source, ah_version, started_at, last_seen_at, ended_at FROM sessions_v3 WHERE session_id = ?1",
                [id, hierDir]);
        }

        _ = StateDbRows.Run(connection, "INSERT INTO teams_unplaced SELECT * FROM teams_v3", []);
        _ = StateDbRows.Run(connection, "INSERT INTO members_unplaced SELECT * FROM members_v3", []);
        var unplacedTeams = (long)StateDbRows.Run(connection, "SELECT count(*) FROM teams_unplaced", [])[0][0]!;
        var unplacedMembers = (long)StateDbRows.Run(connection, "SELECT count(*) FROM members_unplaced", [])[0][0]!;
        _ = StateDbRows.Run(
            connection,
            "INSERT OR REPLACE INTO meta(key, value) VALUES ('migration_v4_unplaced', ?1)",
            [FormattableString.Invariant($"sessions={unplacedSessions},teams={unplacedTeams},members={unplacedMembers}")]);
        foreach (var table in new[] { "teams_v3", "members_v3", "sessions_v3" })
        {
            connection.Exec("DROP TABLE " + table);
        }
    }

    /// <summary>
    /// Composite indexes that let a key-range read also filter on the untyped flag or on the type: without table statistics the planner
    /// otherwise picks the single-column flag or type index and filters the key range row by row.
    /// </summary>
    internal static IReadOnlyList<string> V6Statements { get; } =
    [
        "CREATE INDEX IF NOT EXISTS peer_records_truthy_key ON peer_records(type_truthy, peer_key)",
        "CREATE INDEX IF NOT EXISTS peer_records_type_key ON peer_records(type, peer_key)",
    ];

    /// <summary>The steps from version 1 to <see cref="StateDbModule.SchemaVersion"/>.</summary>
    internal static IReadOnlyList<MigrationStep> Steps { get; } =
    [
        new MigrationStep(2, c => Run(c, V2Statements)),
        new MigrationStep(3, c => Run(c, V3Statements)),
        new MigrationStep(4, null, (c, ahHome) => { var placements = PlaceStoredSessions(c, ahHome); return c => Version4(c, placements); }),
        new MigrationStep(5, c => Run(c, V5Statements)),
        new MigrationStep(6, c => Run(c, V6Statements)),
        new MigrationStep(7, c => Run(c, V7Statements)),
        new MigrationStep(8, c => Run(c, V8Statements)),
        new MigrationStep(9, Version9),
        new MigrationStep(10, Version10),
        new MigrationStep(11, c => Run(c, V11Statements)),
        new MigrationStep(12, c => Run(c, V12Statements)),
        new MigrationStep(13, c => Run(c, V13Statements)),
        new MigrationStep(14, c => Run(c, V14Statements)),
        new MigrationStep(15, c => Run(c, V15Statements)),
        new MigrationStep(16, c => Run(c, V16Statements)),
        new MigrationStep(17, c => Run(c, V17Statements)),
        new MigrationStep(18, c => Run(c, V18Statements)),
        new MigrationStep(19, c => Run(c, V19Statements)),
        new MigrationStep(20, Version20),
        new MigrationStep(21, c => Run(c, V21Statements)),
        new MigrationStep(22, c => Run(c, V22Statements)),
        new MigrationStep(23, c => Run(c, V23Statements)),
        new MigrationStep(25, c => Run(c, V25Statements)),
        new MigrationStep(26, Version26),
        new MigrationStep(27, c => Run(c, V27Statements)),
        new MigrationStep(28, c => Run(c, V28Statements)),
        new MigrationStep(29, c => Run(c, V29Statements)),
        new MigrationStep(30, c => Run(c, V30Statements)),
        new MigrationStep(31, c => Run(c, V31Statements)),
        new MigrationStep(33, c => Run(c, V33Statements)),
        new MigrationStep(35, c => Run(c, V35Statements)),
        new MigrationStep(36, c => Run(c, V36Statements)),
        new MigrationStep(37, c => Run(c, V37Statements)),
        new MigrationStep(38, c => Run(c, V38Statements)),
        new MigrationStep(39, c => Run(c, V39Statements)),
        new MigrationStep(40, c => Run(c, V40Statements)),
        new MigrationStep(41, c => Run(c, V41Statements)),
        new MigrationStep(42, c => Run(c, V42Statements)),
        new MigrationStep(43, c => Run(c, V43Statements)),
        new MigrationStep(44, c => Run(c, V44Statements)),
    ];

    // V40 b397
    /// <summary>
    /// Version 40 (b397): the QA job queue, one row per job (the queue-epoch row <c>_queue</c> included): its latest snapshot as JSON text,
    /// its revision, its end time (Unix ms, set only once done or failed) and <c>seq</c>, the order it was first written. The old
    /// <c>agent-hierarchy.qa.jsonl</c> is not imported.
    /// </summary>
    internal static IReadOnlyList<string> V40Statements =>
    [
        "CREATE TABLE IF NOT EXISTS qa_jobs(id TEXT PRIMARY KEY, seq INTEGER NOT NULL, rev INTEGER NOT NULL, body TEXT NOT NULL, ended_at INTEGER)",
    ];

    // V41 b397 group 2
    /// <summary>Version 41: one row per finished subagent (the old <c>agent-hierarchy.usage.jsonl</c> line), the JSON text verbatim, in append order.</summary>
    internal static IReadOnlyList<string> V41Statements =>
    [
        "CREATE TABLE IF NOT EXISTS usage_rows(seq INTEGER PRIMARY KEY, body TEXT NOT NULL)",
    ];

    /// <summary>Version 42 (b397 group 3): the stall journal, one row per line the old <c>stall/journal.jsonl</c> held; pruned by count, not rotated.</summary>
    internal static IReadOnlyList<string> V42Statements =>
    [
        "CREATE TABLE IF NOT EXISTS stall_journal(seq INTEGER PRIMARY KEY, at_ms INTEGER NOT NULL, row TEXT NOT NULL)",
    ];

    /// <summary>
    /// Version 44 (b397): the watch timeline's transitions, which lived in their own <c>timeline.db</c>, as rows of the state database.
    /// Nothing is imported; the old file is ignored. Rows older than the retention window are pruned.
    /// </summary>
    internal static IReadOnlyList<string> V44Statements =>
    [
        "CREATE TABLE IF NOT EXISTS timeline_events(seq INTEGER PRIMARY KEY AUTOINCREMENT, dir_id TEXT NOT NULL, subject TEXT NOT NULL, from_state TEXT, to_state TEXT NOT NULL, at INTEGER NOT NULL)",
        "CREATE INDEX IF NOT EXISTS timeline_dir_at ON timeline_events(dir_id, at, seq)",
        "CREATE INDEX IF NOT EXISTS timeline_dir_seq ON timeline_events(dir_id, seq)",
        "CREATE INDEX IF NOT EXISTS timeline_subject_seq ON timeline_events(dir_id, subject, seq)",
        "CREATE UNIQUE INDEX IF NOT EXISTS timeline_transition ON timeline_events(dir_id, subject, at, to_state)",
    ];

    // V39 F8a
    /// <summary>
    /// Version 39 (files-off-disk §12.2): a team home's history document (<c>team-history.json</c>) as one row, the text verbatim, and the
    /// team snapshots (<c>snapshots/&lt;team&gt;-&lt;ts&gt;.json</c>) as one row per (home, file). <c>home</c> is the real path of the team home.
    /// </summary>
    internal static IReadOnlyList<string> V39Statements =>
    [
        "CREATE TABLE IF NOT EXISTS team_history(home TEXT PRIMARY KEY, body TEXT NOT NULL, updated_ms INTEGER NOT NULL)",
        "CREATE TABLE IF NOT EXISTS team_snapshots(home TEXT NOT NULL, file TEXT NOT NULL, team TEXT NOT NULL, ts TEXT NOT NULL, body TEXT NOT NULL, created_ms INTEGER NOT NULL, PRIMARY KEY(home, file))",
        "CREATE INDEX IF NOT EXISTS team_snapshots_team ON team_snapshots(home, team, ts)",
    ];

    /// <summary>
    /// Version 36: ADR records (adr-records §4), one row per decision under the repo's hierarchy dir, and its typed links. No row is ever deleted.
    /// <c>external</c> names the user's own document, which is authoritative over the row's text; a path's hash and read time detect a change. Additive.
    /// </summary>
    internal static IReadOnlyList<string> V36Statements =>
    [
        "CREATE TABLE IF NOT EXISTS adrs(hier_dir TEXT NOT NULL, n INTEGER NOT NULL, title TEXT NOT NULL, status TEXT NOT NULL, context TEXT, decision TEXT, consequences TEXT, decided_by TEXT, source TEXT, external TEXT, external_sha256 TEXT, external_read_ms INTEGER, by TEXT, created_ms INTEGER NOT NULL, updated_ms INTEGER NOT NULL, PRIMARY KEY(hier_dir, n))",
        "CREATE TABLE IF NOT EXISTS adr_links(hier_dir TEXT NOT NULL, adr_n INTEGER NOT NULL, kind TEXT NOT NULL, target TEXT NOT NULL, by TEXT, ts_ms INTEGER NOT NULL, PRIMARY KEY(hier_dir, adr_n, kind, target))",
        "CREATE INDEX IF NOT EXISTS adr_links_target ON adr_links(hier_dir, kind, target)",
        AdrHistoryTable,
    ];

    /// <summary>
    /// The ADR history table, added to V36 after homes already stood at V36: a writable open at the current version also creates it when missing (<c>StateDbFile</c>).
    /// </summary>
    internal const string AdrHistoryTable =
        "CREATE TABLE IF NOT EXISTS adr_history(hier_dir TEXT NOT NULL, n INTEGER NOT NULL, seq INTEGER NOT NULL, at INTEGER NOT NULL, by TEXT, from_state TEXT, to_state TEXT, note TEXT, PRIMARY KEY(hier_dir, n, seq))";

    /// <summary>Version 23: the gate log (per-dir-files-to-db S3), append-only, one row per line of the old <c>gates.jsonl</c>, in its order (<c>seq</c> per dir).</summary>
    internal static IReadOnlyList<string> V23Statements =>
    [
        "CREATE TABLE IF NOT EXISTS gates(hier_dir TEXT NOT NULL, seq INTEGER NOT NULL, body TEXT NOT NULL, at TEXT NOT NULL, PRIMARY KEY(hier_dir, seq))",
    ];

    /// <summary>Version 20, applied idempotently: a column the rules table already has is skipped, as every earlier step's <c>IF NOT EXISTS</c> is, so a database already carrying it upgrades too.</summary>
    private static void Version20(SqliteConnection connection)
    {
        foreach (var statement in V20Statements)
        {
            var column = statement.Split(' ')[5];
            if (connection.ScalarText($"SELECT COUNT(*) FROM pragma_table_info('rules') WHERE name = '{column}'") == "0")
            {
                connection.Exec(statement);
            }
        }
    }

    /// <summary>Version 20: standing decision rules (spec-b19 slice 3) are rows of the rules table with <c>kind = 'decision'</c>; a null <c>accepted_at</c> (unix ms) is a proposed rule. Additive.</summary>
    internal static IReadOnlyList<string> V20Statements =>
    [
        "ALTER TABLE rules ADD COLUMN kind TEXT NOT NULL DEFAULT 'guide'",
        "ALTER TABLE rules ADD COLUMN applies TEXT",
        "ALTER TABLE rules ADD COLUMN choose TEXT",
        "ALTER TABLE rules ADD COLUMN accepted_at INTEGER",
        "ALTER TABLE rules ADD COLUMN accepted_by TEXT",
    ];

    /// <summary>
    /// Version 22: the activity records (per-dir-files-to-db S2), one row per session or pane under a hierarchy dir, overwritten on each write. The
    /// <c>activity</c> status entries were keyed by file name and are now keyed by subject, so they are dropped to be rebuilt.
    /// </summary>
    internal static IReadOnlyList<string> V22Statements =>
    [
        "CREATE TABLE IF NOT EXISTS activity(hier_dir TEXT NOT NULL, session_id TEXT NOT NULL, body TEXT NOT NULL, updated_at INTEGER NOT NULL, PRIMARY KEY(hier_dir, session_id))",
        "DELETE FROM status_entries WHERE kind = 'activity'",
    ];

    /// <summary>
    /// Version 21: the session pids (per-dir-files-to-db S1), one row per pid a session has had under a hierarchy dir, its <c>at</c> moved when the pid
    /// comes back. The <c>session</c> status entries were keyed by file line and are now keyed by session id, so they are dropped to be rebuilt.
    /// </summary>
    internal static IReadOnlyList<string> V21Statements =>
    [
        "CREATE TABLE IF NOT EXISTS session_pids(hier_dir TEXT NOT NULL, session_id TEXT NOT NULL, pid INTEGER NOT NULL, at TEXT NOT NULL, PRIMARY KEY(hier_dir, session_id, pid))",
        "DELETE FROM status_entries WHERE kind = 'session'",
    ];

    /// <summary>Version 19: the rules of engagement (rules-of-engagement §3), one row per key per scope. The table is their only home. Additive.</summary>
    internal static IReadOnlyList<string> V19Statements =>
    [
        "CREATE TABLE IF NOT EXISTS rules(hier_dir TEXT NOT NULL, level TEXT NOT NULL, scope TEXT NOT NULL, key TEXT NOT NULL, text TEXT NOT NULL, off INTEGER NOT NULL, \"by\" TEXT NOT NULL, said TEXT, created_ms INTEGER NOT NULL, updated_ms INTEGER NOT NULL, PRIMARY KEY(hier_dir, level, scope, key)) WITHOUT ROWID",
    ];

    /// <summary>
    /// Version 18: whether this home imports its files by itself (db-only-stores §5). A home where a domain is already on the database was cut over by hand and is
    /// left to <c>ah db import</c>. Msgs says nothing: a first <c>ah msg</c> write arms it, and the upgrade import writes its mode, on a stable home too. Nor does
    /// backlog: an earlier migration writes <c>mode.backlog</c> on every home.
    /// </summary>
    internal static IReadOnlyList<string> V18Statements =>
    [
        "INSERT OR IGNORE INTO meta(key, value) SELECT 'import.files', CASE WHEN EXISTS(SELECT 1 FROM meta WHERE value = 'db' AND (key LIKE 'authority.%' OR key LIKE 'mode.%') AND key NOT IN ('authority.msgs', 'mode.msgs', 'authority.backlog', 'mode.backlog')) THEN 'manual' ELSE 'auto' END",
    ];

    /// <summary>Version 17: the two mail stores that were files with no table, heartbeats and notes. <c>domain_seq['mail']</c>, which every mail op bumps, is the change token. Additive.</summary>
    internal static IReadOnlyList<string> V17Statements =>
    [
        "CREATE TABLE IF NOT EXISTS mail_adapters(box TEXT NOT NULL, session TEXT NOT NULL, at_ms INTEGER, raw BLOB NOT NULL, PRIMARY KEY(box, session)) WITHOUT ROWID",
        "CREATE TABLE IF NOT EXISTS mail_notes(kind TEXT NOT NULL, name TEXT NOT NULL, raw BLOB NOT NULL, PRIMARY KEY(kind, name)) WITHOUT ROWID",
    ];

    /// <summary>
    /// Version 16: <c>domain_seq['sessions']</c> counts every change to a session row, placed or not, as version 15 does for teams. A sighting is
    /// not a change: an update trigger on <c>last_seen_at</c> would also make the liveness connection, which may write nothing else, refuse its own write. Additive.
    /// </summary>
    internal static IReadOnlyList<string> V16Statements =>
    [
        ChangedTrigger("sessions", "sessions", "insert", "INSERT"),
        ChangedTrigger("sessions", "sessions", "update", "UPDATE OF doc, ended_at"),
        ChangedTrigger("sessions", "sessions", "delete", "DELETE"),
        ChangedTrigger("sessions_unplaced", "sessions", "insert", "INSERT"),
        ChangedTrigger("sessions_unplaced", "sessions", "update", "UPDATE OF doc, ended_at"),
        ChangedTrigger("sessions_unplaced", "sessions", "delete", "DELETE"),
    ];

    /// <summary>
    /// Version 15: <c>domain_seq['teams']</c> counts every change to a team row, whichever process and path made it, so a reader in another
    /// process can tell a team changed without a file to stat. Additive.
    /// </summary>
    internal static IReadOnlyList<string> V15Statements =>
    [
        ChangedTrigger("teams", "teams", "insert", "INSERT"),
        ChangedTrigger("teams", "teams", "update", "UPDATE"),
        ChangedTrigger("teams", "teams", "delete", "DELETE"),
    ];

    private static string ChangedTrigger(string table, string domain, string name, string verb) =>
        $"CREATE TRIGGER IF NOT EXISTS {table}_changed_{name} AFTER {verb} ON {table} BEGIN INSERT INTO domain_seq(domain, seq, changed_ms) VALUES ('{domain}', 1, CAST((julianday('now') - 2440587.5) * 86400000 AS INTEGER)) ON CONFLICT(domain) DO UPDATE SET seq = seq + 1, changed_ms = excluded.changed_ms; END";

    /// <summary>
    /// Version 13: which <c>domain_seq['msgs']</c> last changed each message, so the view re-reads only those instead of a whole pool. A write
    /// bumps the counter after its body runs, so a row changed in the write takes the value the counter is about to reach. Rows from before
    /// this version hold 0, which no reader's starting point is below. Additive.
    /// </summary>
    internal static IReadOnlyList<string> V13Statements =>
    [
        "ALTER TABLE msg_messages ADD COLUMN changed_seq INTEGER NOT NULL DEFAULT 0",
        "CREATE INDEX IF NOT EXISTS msg_messages_changed ON msg_messages(pool_dir, changed_seq)",
        "CREATE TRIGGER IF NOT EXISTS msg_messages_changed_insert AFTER INSERT ON msg_messages BEGIN UPDATE msg_messages SET changed_seq = COALESCE((SELECT seq FROM domain_seq WHERE domain = 'msgs'), 0) + 1 WHERE pk = NEW.pk; END",
        "CREATE TRIGGER IF NOT EXISTS msg_messages_changed_update AFTER UPDATE ON msg_messages WHEN NEW.changed_seq = OLD.changed_seq BEGIN UPDATE msg_messages SET changed_seq = COALESCE((SELECT seq FROM domain_seq WHERE domain = 'msgs'), 0) + 1 WHERE pk = NEW.pk; END",
    ];

    /// <summary>
    /// Version 14: work items (ah-work-items §4). A registry row names an item's placement (the backlog, or one team's stream), and the plan
    /// log stays the owner of its state. Each table is empty until the repo's first use; the log, links and attachments ship here to save migrations.
    /// </summary>
    internal static IReadOnlyList<string> V14Statements =>
    [
        "CREATE TABLE IF NOT EXISTS work_repos(hier_dir TEXT PRIMARY KEY, prefix TEXT NOT NULL, seq INTEGER NOT NULL, changed_ms INTEGER NOT NULL)",
        "CREATE TABLE IF NOT EXISTS work_prefixes(hier_dir TEXT NOT NULL, prefix TEXT NOT NULL, set_ms INTEGER NOT NULL, PRIMARY KEY(hier_dir, prefix))",
        "CREATE TABLE IF NOT EXISTS work_items(hier_dir TEXT NOT NULL, n INTEGER NOT NULL, team TEXT NOT NULL, stream TEXT NOT NULL, item TEXT NOT NULL, added_at TEXT, ext_id TEXT, ext_url TEXT, updated_ms INTEGER NOT NULL, PRIMARY KEY(hier_dir, n))",
        "CREATE UNIQUE INDEX IF NOT EXISTS work_items_placement ON work_items(hier_dir, team, stream, item)",
        "CREATE UNIQUE INDEX IF NOT EXISTS work_items_ext ON work_items(hier_dir, ext_id) WHERE ext_id IS NOT NULL",
        "CREATE TABLE IF NOT EXISTS work_log(id INTEGER PRIMARY KEY, hier_dir TEXT NOT NULL, n INTEGER NOT NULL, ts_ms INTEGER NOT NULL, type TEXT NOT NULL, title TEXT NOT NULL, body TEXT, by TEXT, session TEXT)",
        "CREATE INDEX IF NOT EXISTS work_log_item ON work_log(hier_dir, n, id)",
        "CREATE TABLE IF NOT EXISTS work_links(hier_dir TEXT NOT NULL, blocker_n INTEGER NOT NULL, blocked_n INTEGER NOT NULL, by TEXT, ts_ms INTEGER NOT NULL, PRIMARY KEY(hier_dir, blocker_n, blocked_n))",
        "CREATE INDEX IF NOT EXISTS work_links_blocked ON work_links(hier_dir, blocked_n)",
        "CREATE TABLE IF NOT EXISTS work_attachments(hier_dir TEXT NOT NULL, n INTEGER NOT NULL, ref TEXT NOT NULL, kind TEXT, title TEXT, sha TEXT, by TEXT, ts_ms INTEGER NOT NULL, PRIMARY KEY(hier_dir, n, ref))",
    ];

    /// <summary>
    /// Version 12: the persisted status inputs (ah-msgs-fast-2 §3.8). Each row is one input a status assembly reads, kept under the dir the
    /// input lives in; <c>(kind, key)</c> serves the reverse lookup from a pool to the status dirs that list it. Derived state: a missing or
    /// stale <c>status.version</c> meta row makes the next start rebuild it, so the table starts empty. Exchanges closed before closed_ms was
    /// written (R3-3) get their answer time, else the upgrade time (database clock): prune then keeps them a full window, never prunes them at once.
    /// </summary>
    internal static IReadOnlyList<string> V12Statements =>
    [
        "CREATE TABLE IF NOT EXISTS status_entries(pool_dir TEXT NOT NULL, kind TEXT NOT NULL, key TEXT NOT NULL, json TEXT NOT NULL, changed_ms INTEGER NOT NULL, PRIMARY KEY(pool_dir, kind, key)) WITHOUT ROWID",
        "CREATE INDEX IF NOT EXISTS status_entries_kind_key ON status_entries(kind, key)",
        "UPDATE msg_exchanges SET closed_ms = COALESCE(answered_ms, " + StatusDirty.NowMsSql + ") WHERE state = 'closed' AND closed_ms IS NULL",
    ];

    /// <summary>
    /// Version 11: the backlog, the one plan stream kept in the database. <c>plan_events</c> holds each event row as the plan log would;
    /// <c>plan_items</c> holds each item folded (<c>doc</c>) with the columns the hot reads filter and order on. The backlog domain starts
    /// in db mode: it has no files store.
    /// </summary>
    internal static IReadOnlyList<string> V11Statements =>
    [
        "CREATE TABLE IF NOT EXISTS plan_events(id INTEGER PRIMARY KEY, hier_dir TEXT NOT NULL, stream TEXT NOT NULL, ts_ms INTEGER NOT NULL, by TEXT, row TEXT NOT NULL)",
        "CREATE INDEX IF NOT EXISTS plan_events_stream ON plan_events(hier_dir, stream, id)",
        "CREATE TABLE IF NOT EXISTS plan_items(hier_dir TEXT NOT NULL, stream TEXT NOT NULL, item TEXT NOT NULL, state TEXT NOT NULL, wave TEXT, rank INTEGER, holder TEXT, ext TEXT, updated_ms INTEGER NOT NULL, doc TEXT NOT NULL, PRIMARY KEY(hier_dir, stream, item)) WITHOUT ROWID",
        "CREATE INDEX IF NOT EXISTS plan_items_next ON plan_items(hier_dir, stream, state, wave, rank)",
        "CREATE UNIQUE INDEX IF NOT EXISTS plan_items_ext ON plan_items(hier_dir, stream, ext) WHERE ext IS NOT NULL",
        "CREATE INDEX IF NOT EXISTS plan_items_updated ON plan_items(hier_dir, stream, updated_ms)",
        "INSERT OR IGNORE INTO meta(key, value) VALUES ('mode.backlog', 'db')",
    ];

    /// <summary>Version 10: the indexes the per-lane charter query and the downstream walk read (ah-msgs-fast-2). Additive.</summary>
    internal static IReadOnlyList<string> V10Statements =>
    [
        "CREATE INDEX IF NOT EXISTS msg_exchanges_to_opened ON msg_exchanges(pool_dir, \"to\", opened_ms)",
        "CREATE INDEX IF NOT EXISTS msg_messages_request_parent ON msg_messages(pool_dir, parent) WHERE type = 'request'",
        // team first: led by pool_dir the planner took it for the live scope's pool-only queries (71 ms against 28 ms at x53)
        "CREATE INDEX IF NOT EXISTS msg_exchanges_team_id ON msg_exchanges(ifnull(team, ''), pool_dir, id)",
    ];

    // sub-orchestrator requests now follow the live rule of every other request, so the backfill runs again over the answered
    // exchanges version 9 kept live
    private static void Version10(SqliteConnection connection)
    {
        Run(connection, V10Statements);
        BackfillLive(connection);
    }

    /// <summary>
    /// Version 9: whether an exchange can still be open (<c>live</c>, see <see cref="MsgRows.Live(MsgExchangePair)"/>) and when its response was
    /// sealed (<c>answered_ms</c>), with the indexes the bounded readers use. Additive: every row starts live, which is never wrong, only slower.
    /// </summary>
    internal static IReadOnlyList<string> V9Statements =>
    [
        "ALTER TABLE msg_exchanges ADD COLUMN live INTEGER NOT NULL DEFAULT 1",
        "ALTER TABLE msg_exchanges ADD COLUMN answered_ms INTEGER",
        "CREATE INDEX IF NOT EXISTS msg_exchanges_hot ON msg_exchanges(pool_dir) WHERE live = 1",
        "CREATE INDEX IF NOT EXISTS msg_exchanges_answered ON msg_exchanges(pool_dir, answered_ms) WHERE answered_ms IS NOT NULL",
        "CREATE INDEX IF NOT EXISTS msg_exchanges_team_opened ON msg_exchanges(team, opened_ms)",
        "CREATE INDEX IF NOT EXISTS msg_messages_request_slug ON msg_messages(slug) WHERE type = 'request'",
    ];

    private static void Version9(SqliteConnection connection)
    {
        Run(connection, V9Statements);
        BackfillLive(connection);
    }

    /// <summary>
    /// <c>answered_ms</c> and <c>live</c> of every exchange from its rows: the archived and the answered orchestrator exchanges in SQL, then each
    /// remaining exchange with a stored response body by the predicate the write path keeps.
    /// </summary>
    internal static void BackfillLive(SqliteConnection connection)
    {
        connection.Exec("UPDATE msg_exchanges SET answered_ms = (SELECT b.sealed_at_ms FROM msg_bodies b WHERE b.msg_pk = msg_exchanges.response_pk ORDER BY b.rev DESC LIMIT 1) WHERE response_pk IS NOT NULL");
        connection.Exec("UPDATE msg_exchanges SET live = 0 WHERE state = 'archived' OR (response_pk IS NOT NULL AND \"to\" = 'orchestrator')");
        var closed = new List<(string Pool, string Id)>();
        using (var read = connection.Prepare(
            "SELECT e.pool_dir, e.id, e.\"to\", (SELECT b.bytes FROM msg_bodies b WHERE b.msg_pk = e.response_pk ORDER BY b.rev DESC LIMIT 1) FROM msg_exchanges e JOIN msg_messages q ON q.pk = e.request_pk WHERE e.live = 1 AND e.response_pk IS NOT NULL AND q.state <> 'archived'"))
        {
            while (read.Step())
            {
                byte[]? body = read.ColumnType(3) == 4 ? read.Blob(3) : null;
                if (!MsgRows.Live(false, read.Text(2), read.Text(1), true, body))
                {
                    closed.Add((read.Text(0), read.Text(1)));
                }
            }
        }

        using var write = connection.Prepare("UPDATE msg_exchanges SET live = 0 WHERE pool_dir = ?1 AND id = ?2");
        foreach (var (pool, id) in closed)
        {
            write.Bind(1, pool);
            write.Bind(2, id);
            _ = write.Step();
            write.Reset();
        }
    }

    /// <summary>
    /// Version 8: the indexes the message readers need. Every statement is independent of the other tables, so the step is correct
    /// on a database of any earlier version. A message is found by either of its paths; each index holds only the rows that have one.
    /// </summary>
    internal static IReadOnlyList<string> V8Statements =>
    [
        "CREATE INDEX IF NOT EXISTS msg_messages_draft_path ON msg_messages(draft_path) WHERE draft_path IS NOT NULL",
        "CREATE INDEX IF NOT EXISTS msg_messages_export_path ON msg_messages(export_path) WHERE export_path IS NOT NULL",
    ];

    private static void Run(SqliteConnection connection, IReadOnlyList<string> statements)
    {
        foreach (var statement in statements)
        {
            connection.Exec(statement);
        }
    }

    // V25 S6

    /// <summary>Version 25: the user's question log (per-dir-files-to-db S6), one row per old <c>qa-log.jsonl</c> line in its order (<c>seq</c> per dir); <c>at</c> is epoch ms, null when the row has none.</summary>
    internal static IReadOnlyList<string> V25Statements =>
    [
        "CREATE TABLE IF NOT EXISTS qa_log(hier_dir TEXT NOT NULL, seq INTEGER NOT NULL, id TEXT, at INTEGER, body TEXT NOT NULL, PRIMARY KEY(hier_dir, seq), UNIQUE(hier_dir, id))",
    ];

    // V26 S7
    /// <summary>
    /// Version 26: the peer roster (per-dir-files-to-db S7) lives in V5's <c>roster_records</c>, one row per line of the old
    /// <c>peers.jsonl</c> (<c>pool_dir</c> the hierarchy dir, <c>raw</c> the JSON line, <c>seq</c> the append order); this adds the write's
    /// <c>op_id</c>, unique per dir, and the read index. Nothing wrote the table before, so the column add meets no rows. Applied
    /// idempotently, as version 20 is: a table that already has the column is not altered again.
    /// </summary>
    private static void Version26(SqliteConnection connection)
    {
        if (connection.ScalarText("SELECT COUNT(*) FROM pragma_table_info('roster_records') WHERE name = 'op_id'") == "0")
        {
            connection.Exec("ALTER TABLE roster_records ADD COLUMN op_id TEXT");
        }

        Run(connection, V26Statements);
    }

    internal static IReadOnlyList<string> V26Statements =>
    [
        "CREATE UNIQUE INDEX IF NOT EXISTS roster_records_op ON roster_records(pool_dir, op_id) WHERE op_id IS NOT NULL",
        "CREATE INDEX IF NOT EXISTS roster_records_dir ON roster_records(pool_dir, seq)",
    ];

    // V27 S8
    /// <summary>Version 27: the tracked-job records (per-dir-files-to-db S8), one row per old <c>jobs/&lt;team&gt;/&lt;id&gt;.json</c>: the record as JSON text and its end (Unix ms, null while open).</summary>
    internal static IReadOnlyList<string> V27Statements =>
    [
        "CREATE TABLE IF NOT EXISTS jobs(team_home TEXT NOT NULL, team TEXT NOT NULL, id TEXT NOT NULL, body TEXT NOT NULL, ended_at INTEGER, PRIMARY KEY(team_home, team, id))",
    ];

    // V28 S9
    /// <summary>
    /// Version 28 (per-dir-files-to-db S9): each pipeline run's decision log as rows keyed by (hier_dir, run, seq) in append order, and
    /// the decisions domain flipped to the database (<c>mode.decisions</c>). A run's <c>decisions.jsonl</c> is taken into the table on its
    /// first read or write (a <c>decisions.taken:</c> meta key), so no log is stranded by the flip.
    /// </summary>
    internal static IReadOnlyList<string> V28Statements =>
    [
        "CREATE TABLE IF NOT EXISTS decisions(hier_dir TEXT NOT NULL, run TEXT NOT NULL, seq INTEGER NOT NULL, body TEXT NOT NULL, PRIMARY KEY(hier_dir, run, seq))",
        "INSERT INTO meta(key, value) VALUES ('mode.decisions', 'db') ON CONFLICT(key) DO NOTHING",
        "INSERT INTO meta(key, value) VALUES ('authority.decisions', 'db') ON CONFLICT(key) DO NOTHING",
    ];

    // V38 S11e
    /// <summary>
    /// Version 38 (per-dir-files-to-db S11e): a hierarchy dir's grants documents (<c>grants/&lt;team&gt;.json</c>) as one row per
    /// (hier_dir, team), the document text byte for byte, and the worktree audit rows written since the upgrade. There is no mode row.
    /// A dir's documents are taken into the table on its first read (a <c>grants.taken:</c> meta key); old audit rows are not imported.
    /// </summary>
    internal static IReadOnlyList<string> V38Statements =>
    [
        "CREATE TABLE IF NOT EXISTS worktree_grants(hier_dir TEXT NOT NULL, team TEXT NOT NULL, body TEXT NOT NULL, digest TEXT NOT NULL, updated_at INTEGER NOT NULL, PRIMARY KEY(hier_dir, team))",
        "CREATE TABLE IF NOT EXISTS worktree_grant_audit(seq INTEGER PRIMARY KEY, at TEXT NOT NULL, hier_dir TEXT NOT NULL, team TEXT NOT NULL, body TEXT NOT NULL)",
    ];

    // V29 S11a-ckpt
    /// <summary>Version 29: the checkpoint records (per-dir-files-to-db S11a-ckpt), keyed by the absolute path the record file had or would have had.</summary>
    internal static IReadOnlyList<string> V29Statements =>
    [
        "CREATE TABLE IF NOT EXISTS checkpoints(key TEXT PRIMARY KEY, hier_dir TEXT NOT NULL, team TEXT NOT NULL, ts TEXT NOT NULL, body TEXT NOT NULL, created_at INTEGER NOT NULL)",
        "CREATE INDEX IF NOT EXISTS checkpoints_team ON checkpoints(hier_dir, team, ts)",
    ];

    // V30 S11b
    /// <summary>Version 30: the mesh links (per-dir-files-to-db S11b), the whole <c>{v, links, denied}</c> document of each team home and team, as the old <c>links/&lt;team&gt;.json</c> held it.</summary>
    internal static IReadOnlyList<string> V30Statements =>
    [
        "CREATE TABLE IF NOT EXISTS links(team_home TEXT NOT NULL, team TEXT NOT NULL, body TEXT NOT NULL, updated_at TEXT NOT NULL, PRIMARY KEY(team_home, team))",
    ];

    // V31 S11c
    /// <summary>
    /// Version 31: the progress logs (per-dir-files-to-db S11c), append-only. <c>log</c> is the old file's name under
    /// <c>progress/&lt;team&gt;/</c> (<c>ledger.jsonl</c> or <c>&lt;stream&gt;.plan.jsonl</c>), one row per line, in its order (<c>seq</c> per log).
    /// </summary>
    internal static IReadOnlyList<string> V31Statements =>
    [
        "CREATE TABLE IF NOT EXISTS progress_logs(hier_dir TEXT NOT NULL, team TEXT NOT NULL, log TEXT NOT NULL, seq INTEGER NOT NULL, body TEXT NOT NULL, PRIMARY KEY(hier_dir, team, log, seq))",
    ];

    // V33 S10
    /// <summary>
    /// Version 33 (per-dir-files-to-db S10): a hierarchy dir's asks logs (<c>asks.jsonl</c>, <c>asks-gate.jsonl</c>,
    /// <c>parked-questions.jsonl</c>) as rows keyed by (hier_dir, log, seq) in append order, each line stored byte for byte, and the asks
    /// write flipped to the database (<c>mode.asks</c>). A log file is taken into the table on its first read (an <c>asks.taken:</c> meta key).
    /// </summary>
    internal static IReadOnlyList<string> V33Statements =>
    [
        "CREATE TABLE IF NOT EXISTS asks(hier_dir TEXT NOT NULL, log TEXT NOT NULL, seq INTEGER NOT NULL, body TEXT NOT NULL, PRIMARY KEY(hier_dir, log, seq))",
        "INSERT INTO meta(key, value) VALUES ('mode.asks', 'db') ON CONFLICT(key) DO NOTHING",
        "INSERT INTO meta(key, value) VALUES ('authority.asks', 'db') ON CONFLICT(key) DO NOTHING",
    ];

    // V35 S11a-holds
    /// <summary>Version 35: each team's own hold (per-dir-files-to-db S11a-holds), keyed by its team home and its folder name; the body verbatim.</summary>
    internal static IReadOnlyList<string> V35Statements =>
    [
        "CREATE TABLE IF NOT EXISTS holds(team_home TEXT NOT NULL, team TEXT NOT NULL, body TEXT NOT NULL, updated_at INTEGER NOT NULL, PRIMARY KEY(team_home, team))",
    ];

    // V37 S11d
    /// <summary>
    /// Version 37: the progress documents (per-dir-files-to-db S11d). <c>name</c> is the old file's name under <c>progress/&lt;team&gt;/</c>,
    /// <c>kind</c> what it holds (<c>checklist</c>, <c>journal</c>, <c>rollup</c>, <c>closing</c>, <c>nudged</c>, <c>sessions</c>).
    /// </summary>
    internal static IReadOnlyList<string> V37Statements =>
    [
        "CREATE TABLE IF NOT EXISTS progress_docs(pool TEXT NOT NULL, team TEXT NOT NULL, name TEXT NOT NULL, kind TEXT NOT NULL, body TEXT NOT NULL, updated_ms INTEGER NOT NULL, PRIMARY KEY(pool, team, name))",
    ];

    /// <summary>
    /// Version 43: bare-member recovery records and the owner's requests for them (<c>recover/</c> files before), and archived holds
    /// (<c>&lt;stamp&gt;-hold.json</c> before). No import: the old files are ignored.
    /// </summary>
    internal static IReadOnlyList<string> V43Statements =>
    [
        "CREATE TABLE IF NOT EXISTS recover_records(dir_key TEXT NOT NULL, team TEXT NOT NULL, member TEXT NOT NULL, kind TEXT NOT NULL CHECK(kind IN ('record','control')), body TEXT NOT NULL, updated_at INTEGER NOT NULL, PRIMARY KEY(dir_key, team, member, kind))",
        "CREATE TABLE IF NOT EXISTS hold_archive(team_home TEXT NOT NULL, team TEXT NOT NULL, stamp TEXT NOT NULL, body TEXT NOT NULL, created_at INTEGER NOT NULL, PRIMARY KEY(team_home, team, stamp))",
    ];
}
