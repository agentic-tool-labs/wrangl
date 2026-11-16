using Ah.Abstractions;

namespace Ah.StateDb;

/// <summary>How a database's schema version relates to this binary's.</summary>
internal enum SchemaAccess
{
    /// <summary>Equal: read and write.</summary>
    Normal,

    /// <summary>The database is older: used as it is, never migrated here.</summary>
    DbOlder,

    /// <summary>The database is newer: read only, never written.</summary>
    DbNewer,
}

internal sealed class StateDbFile : IStateDb, IFileCache
{
    private static readonly string[] SchemaStatements =
    [
        "CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT NOT NULL) WITHOUT ROWID",
        "CREATE TABLE IF NOT EXISTS cache_files(path TEXT PRIMARY KEY, size INTEGER NOT NULL, mtime_ns INTEGER NOT NULL, " +
        "ctime_ns INTEGER NOT NULL, inode INTEGER NOT NULL, content BLOB NOT NULL) WITHOUT ROWID",
    ];

    private const string GetSql = "SELECT content FROM cache_files WHERE path = ?1 AND size = ?2 AND mtime_ns = ?3 AND ctime_ns = ?4 AND inode = ?5";

    private const string PutSql =
        "INSERT INTO cache_files(path, size, mtime_ns, ctime_ns, inode, content) VALUES(?1, ?2, ?3, ?4, ?5, ?6) " +
        "ON CONFLICT(path) DO UPDATE SET size = excluded.size, mtime_ns = excluded.mtime_ns, ctime_ns = excluded.ctime_ns, " +
        "inode = excluded.inode, content = excluded.content";

    private readonly object _gate = new();
    private readonly string? _directory;
    private readonly string? _path;
    private readonly bool _mayCreate;
    private readonly SchemaPlan _plan;
    private Action<string>? _log;
    private SqliteConnection? _connection;
    private bool _canWrite;
    private SqliteStatement? _get;
    private SqliteStatement? _put;

    private StateDbFile(StateDbStatus status, SqliteConnection? connection, bool canWrite, string? directory = null, string? path = null, bool mayCreate = false, SchemaPlan? plan = null)
    {
        _plan = plan ?? SchemaPlan.Production;
        Status = status;
        _connection = connection;
        _canWrite = canWrite;
        _directory = directory;
        _path = path;
        _mayCreate = mayCreate;
    }

    public StateDbStatus Status { get; private set; }

    /// <summary>Runs, with the gate held, just before a failed lookup drops its prepared statements; lets a test hold that moment open.</summary>
    internal Action? BeforeForget { get; set; }

    public IFileCache FileCache => this;

    public CheckpointOutcome Checkpoint(bool truncate)
    {
        TryReidentify();
        lock (_gate)
        {
            if (_connection is null || !_canWrite)
            {
                return new CheckpointOutcome(false, false, 0, 0);
            }

            try
            {
                using var statement = _connection.Prepare(truncate ? "PRAGMA wal_checkpoint(TRUNCATE)" : "PRAGMA wal_checkpoint(PASSIVE)");
                return statement.Step()
                    ? new CheckpointOutcome(true, statement.Int64(0) != 0, (int)statement.Int64(1), (int)statement.Int64(2))
                    : new CheckpointOutcome(false, false, 0, 0);
            }
            catch (SqliteException)
            {
                return new CheckpointOutcome(false, false, 0, 0);
            }
        }
    }

    public OnceOutcome WriteOnce(string writeId, DateTimeOffset deadlineAt, Func<IStateDbTransaction, string> work)
    {
        lock (_gate)
        {
            if (_connection is null || !_canWrite)
            {
                return new OnceOutcome(OnceStatus.Refused, null, null, "the state database is not writable here", null);
            }

            try
            {
                _connection.Exec("BEGIN IMMEDIATE");
            }
            catch (SqliteException e)
            {
                return new OnceOutcome(OnceStatus.Refused, null, null, $"the state database could not take the write: {e.Message}", null);
            }

            try
            {
                var outcome = StateDbOnce.Apply(new StateDbTx(_connection), writeId, "daemon", deadlineAt, _plan.Now, work);
                _connection.Exec(outcome.Status == OnceStatus.Applied ? "COMMIT" : "ROLLBACK");
                return outcome;
            }
            catch
            {
                Rollback(_connection);
                throw;
            }
        }
    }

    public int PruneAppliedWrites(TimeSpan age)
    {
        TryReidentify();
        lock (_gate)
        {
            if (_connection is null || !_canWrite)
            {
                return 0;
            }

            try
            {
                using var statement = _connection.Prepare("DELETE FROM applied_writes WHERE applied_at < ?1");
                statement.Bind(1, (_plan.Now() - age).ToUnixTimeMilliseconds());
                _ = statement.Step();
                return _connection.Changes;
            }
            catch (SqliteException)
            {
                return 0;
            }
        }
    }

    public int ImportFallback()
    {
        lock (_gate)
        {
            return _directory is null ? 0 : ImportFallbackLocked(Path.GetDirectoryName(_directory)!);
        }
    }

    private int ImportFallbackLocked(string ahHome)
    {
        if (_connection is null || !_canWrite)
        {
            return 0;
        }

        try
        {
            return StateDbFallback.Import(_connection, ahHome, _plan.Now());
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException)
        {
            // the lines stay where they are for the next try
            return 0;
        }
    }

    /// <summary>What the creating process does once after it opens: applies the lines the hooks left in the fallback file, then truncates the log.</summary>
    private void OnStart(string ahHome)
    {
        lock (_gate)
        {
            if (_connection is null || !_canWrite)
            {
                return;
            }

            _ = ImportFallbackLocked(ahHome);
        }

        _ = Checkpoint(truncate: true);
    }

    /// <summary>The first column of the first row of <paramref name="sql"/> on this database's connection, or null; for tests to observe the connection's settings.</summary>
    internal string? Scalar(string sql)
    {
        lock (_gate)
        {
            return _connection?.ScalarText(sql);
        }
    }

    /// <summary>The access a database at <paramref name="dbVersion"/> gives a binary at <paramref name="binaryVersion"/>.</summary>
    internal static SchemaAccess Decide(int dbVersion, int binaryVersion) =>
        dbVersion == binaryVersion ? SchemaAccess.Normal : dbVersion < binaryVersion ? SchemaAccess.DbOlder : SchemaAccess.DbNewer;

    internal static string MissingLibraryMessage() =>
        "SQLite >= 3.24 not found: " + (OperatingSystem.IsMacOS()
            ? "install it (for example: brew install sqlite) so that libsqlite3.dylib can be loaded"
            : "install your distribution's SQLite library package (libsqlite3.so.0)");

    internal static StateDbFile Open(string ahHome, bool mayCreate) => Open(ahHome, mayCreate, Sqlite.LibVersionNumber, SchemaPlan.Production);

    /// <summary>Opens the database; <paramref name="log"/> receives one line when the database goes away for good while the process runs.</summary>
    internal static StateDbFile Create(string ahHome, bool mayCreate, Action<string>? log)
    {
        var db = Open(ahHome, mayCreate, Sqlite.LibVersionNumber, SchemaPlan.Production);
        db._log = log;
        return db;
    }

    internal static StateDbFile Open(string ahHome, bool mayCreate, Func<int> libraryVersion) => Open(ahHome, mayCreate, libraryVersion, SchemaPlan.Production);

    internal static StateDbFile Open(string ahHome, bool mayCreate, Func<int> libraryVersion, SchemaPlan plan)
    {
        if (LibraryProblem(libraryVersion) is { } problem)
        {
            return Without(problem);
        }

        var directory = Path.Join(ahHome, "state");
        var path = Path.Join(directory, "ah.db");
        if (!File.Exists(path) && StateDbAuthority.Check(ahHome, 4) is { Problem: StoreProblem.Missing } missing)
        {
            return Without(new StateDbStatus(StateDbAvailability.StoreMissing, missing.Detail));
        }

        if (!File.Exists(path) && !mayCreate)
        {
            return Without(new StateDbStatus(StateDbAvailability.Unavailable, "the state database does not exist yet; the daemon creates it"));
        }

        var opened = TryOpen(directory, path, mayCreate, plan);
        if (mayCreate)
        {
            opened.Identify(ahHome);
            opened.OnStart(ahHome);
        }

        return opened;
    }

    private bool _unverified;

    /// <summary>
    /// Gives a writable database its identity and makes the authority marker match it; a database the marker does not name is kept read
    /// only. A settle that fails (a busy database, a marker being rewritten) is retried with a growing wait before the database is kept read
    /// only as unverified, and the periodic jobs try again.
    /// </summary>
    private void Identify(string ahHome, int attempts = 4)
    {
        if (_connection is null || (!_canWrite && !_unverified))
        {
            return;
        }

        Exception? last = null;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            try
            {
                var swapped = StateDbAuthority.Settle(_connection, ahHome);
                _unverified = false;
                if (swapped is not null)
                {
                    _canWrite = false;
                    Status = new StateDbStatus(StateDbAvailability.ReadOnly, swapped);
                }
                else if (!_canWrite)
                {
                    _canWrite = true;
                    Status = new StateDbStatus(StateDbAvailability.Ready, string.Empty);
                }

                return;
            }
            catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException)
            {
                last = e;
                if (attempt + 1 < attempts)
                {
                    Thread.Sleep(50 << attempt);
                }
            }
        }

        _canWrite = false;
        _unverified = true;
        Status = new StateDbStatus(StateDbAvailability.ReadOnly, "store-unverified: the authority marker could not be settled (" + last!.Message + "); writes are refused until a later job settles it");
        _log?.Invoke("state database: " + Status.Detail);
    }

    /// <summary>Called by the periodic jobs: a database kept read only as unverified is settled again.</summary>
    private void TryReidentify()
    {
        lock (_gate)
        {
            if (_unverified && _directory is not null)
            {
                Identify(Path.GetDirectoryName(_directory)!, attempts: 1);
            }
        }
    }

    public byte[]? Lookup(string path, FileStat stat)
    {
        if (_connection is null)
        {
            return null;
        }

        lock (_gate)
        {
            try
            {
                EnsureFile();
                if (_connection is null)
                {
                    return null;
                }

                var statement = _get ??= _connection.Prepare(GetSql);
                statement.Reset();
                BindKey(statement, path, stat);
                return statement.Step() ? statement.Blob(0) : null;
            }
            catch (SqliteException)
            {
                BeforeForget?.Invoke();
                Forget();
                return null;
            }
        }
    }

    public void Store(string path, FileStat stat, byte[] content)
    {
        if (_connection is null || !_canWrite)
        {
            return;
        }

        lock (_gate)
        {
            try
            {
                EnsureFile();
                if (_connection is null)
                {
                    return;
                }

                try
                {
                    Write(path, stat, content);
                }
                catch (SqliteException e) when (e.Message.Contains("no such table", StringComparison.Ordinal))
                {
                    Forget();
                    CreateTables(_connection);
                    Write(path, stat, content);
                }
            }
            catch (SqliteException)
            {
                Forget();
            }
        }
    }

    public void Dispose()
    {
        if (_connection is null)
        {
            return;
        }

        lock (_gate)
        {
            Forget();
            _connection?.Dispose();
        }
    }

    private static StateDbFile Without(StateDbStatus status) => new(status, null, false);

    /// <summary>A process that may create the database replaces a file removed from under it; others keep what they have. Called with the gate held.</summary>
    private void EnsureFile()
    {
        if (_mayCreate && _connection is not null && !File.Exists(_path))
        {
            if (StateDbAuthority.Check(Path.GetDirectoryName(_directory)!) is { Problem: StoreProblem.Missing } missing)
            {
                Forget();
                _connection.Dispose();
                _connection = null;
                _canWrite = false;
                Status = new StateDbStatus(StateDbAvailability.StoreMissing, missing.Detail);
                _log?.Invoke("state database: " + missing.Detail);
                return;
            }

            Reopen();
            Identify(Path.GetDirectoryName(_directory)!);
        }
    }

    /// <summary>Opens a fresh database where the file was removed from under this connection, which would otherwise keep writing to the unlinked file; called with the connection's gate held.</summary>
    private void Reopen()
    {
        Forget();
        _connection!.Dispose();
        var fresh = TryOpen(_directory!, _path!, _mayCreate, _plan);
        _connection = fresh._connection;
        _canWrite = fresh._canWrite;
        if (fresh._connection is null)
        {
            Status = fresh.Status;
            _log?.Invoke("state database: " + fresh.Status.Detail);
        }
    }

    private static StateDbStatus? LibraryProblem(Func<int> libraryVersion)
    {
        try
        {
            Sqlite.InstallResolver();
            return libraryVersion() >= StateDbModule.MinimumLibraryVersion
                ? null
                : new StateDbStatus(StateDbAvailability.LibraryTooOld, MissingLibraryMessage());
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return new StateDbStatus(StateDbAvailability.LibraryMissing, MissingLibraryMessage());
        }
    }

    private static StateDbFile TryOpen(string directory, string path, bool mayCreate, SchemaPlan plan)
    {
        try
        {
            return OpenFile(directory, path, mayCreate, plan);
        }
        catch (SqliteException e) when (e.IsDamage && mayCreate)
        {
            return Quarantine(directory, plan, e);
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException)
        {
            return Without(new StateDbStatus(StateDbAvailability.Unavailable, "the state database could not be opened: " + e.Message));
        }
    }

    /// <summary>
    /// Moves a damaged database, with its journal files, to <c>state/damaged</c> and opens nothing: the file holds state that cannot be
    /// rebuilt, so it is kept for a restore or a look and never deleted. The next start creates a new empty database.
    /// </summary>
    private static StateDbFile Quarantine(string directory, SchemaPlan plan, SqliteException failure)
    {
        var ahHome = Path.GetDirectoryName(directory)!;
        try
        {
            var moved = StateDbBackups.MoveAside(ahHome, StateDbBackups.DamagedDirectory(ahHome), plan.Now());
            return Without(new StateDbStatus(
                StateDbAvailability.Damaged,
                $"the state database is damaged ({failure.Message}) and was moved to {moved}; restore a backup with: ah db restore"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Without(new StateDbStatus(StateDbAvailability.Unavailable, $"the state database is damaged ({failure.Message}) and could not be moved aside: {e.Message}"));
        }
    }

    private static StateDbFile OpenFile(string directory, string path, bool mayCreate, SchemaPlan plan)
    {
        var existed = File.Exists(path);
        if (mayCreate)
        {
            if (!existed)
            {
                MoveStrayJournalsAside(directory, plan);
            }

            PrepareFiles(directory, path);
        }

        var connection = SqliteConnection.Open(path, create: false, StateDbModule.BusyTimeoutMs);
        try
        {
            if (mayCreate)
            {
                connection.Exec("PRAGMA journal_mode=WAL");
            }

            // The database holds state that cannot be rebuilt, so a commit is on disk before it returns.
            connection.Exec(plan.Options.Synchronous == SyncLevel.Full ? "PRAGMA synchronous=FULL" : "PRAGMA synchronous=NORMAL");
            if (plan.Options.WalAutocheckpoint is { } pages)
            {
                connection.Exec(FormattableString.Invariant($"PRAGMA wal_autocheckpoint={pages}"));
            }

            connection.Exec("PRAGMA foreign_keys=ON");
            if (mayCreate && existed)
            {
                CheckIntegrity(connection);
            }

            var version = ReadVersion(connection);
            if (version is null)
            {
                if (!mayCreate)
                {
                    connection.Dispose();
                    return Without(new StateDbStatus(StateDbAvailability.Unavailable, "the state database has no schema yet; the daemon creates it"));
                }

                CreateSchema(connection);
                return Migrated(connection, 1, directory, path, plan, backup: false);
            }

            if (Decide(version.Value, plan.Version) == SchemaAccess.DbNewer)
            {
                return new StateDbFile(
                    new StateDbStatus(StateDbAvailability.ReadOnly, $"the state database is schema {version}, newer than this binary's {plan.Version}; it is read only here"),
                    connection,
                    false,
                    directory,
                    path,
                    mayCreate,
                    plan);
            }

            if (mayCreate && version.Value == plan.Version)
            {
                EnsureAdrHistory(connection);
            }

            return mayCreate && version.Value < plan.Version
                ? Migrated(connection, version.Value, directory, path, plan, backup: true)
                : new StateDbFile(new StateDbStatus(StateDbAvailability.Ready, string.Empty), connection, true, directory, path, mayCreate, plan);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>
    /// A write-ahead log or shared-memory file left without its database belongs to a database that is gone, and a new database created
    /// beside it must not meet it, so both go to <c>state/damaged</c> (nothing is deleted) before the new file is made.
    /// </summary>
    private static void MoveStrayJournalsAside(string directory, SchemaPlan plan)
    {
        var ahHome = Path.GetDirectoryName(directory)!;
        _ = StateDbBackups.MoveAside(ahHome, StateDbBackups.DamagedDirectory(ahHome), plan.Now());
    }

    /// <summary>A home that reached V36 before <c>adr_history</c> joined it gets the table here; one that has it is only read.</summary>
    private static void EnsureAdrHistory(SqliteConnection connection)
    {
        using (var statement = connection.Prepare("SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'adr_history'"))
        {
            if (statement.Step())
            {
                return;
            }
        }

        connection.Exec(StateDbMigrations.AdrHistoryTable);
    }

    /// <summary>Raises a damage error when <c>quick_check</c> finds the file damaged, which the caller treats as a file that is not a database.</summary>
    private static void CheckIntegrity(SqliteConnection connection)
    {
        using var statement = connection.Prepare("PRAGMA quick_check");
        List<string> rows = [];
        while (statement.Step() && rows.Count < 5)
        {
            rows.Add(statement.Text(0));
        }

        if (rows is not ["ok"])
        {
            throw new SqliteException(Sqlite.Corrupt, "quick_check: " + string.Join("; ", rows));
        }
    }

    /// <summary>
    /// Runs the steps above <paramref name="from"/> in order, each in one transaction that also moves the stored version, after one
    /// <c>pre-migrate-v&lt;target&gt;</c> backup of an existing database. A step that fails rolls back and leaves the database at the
    /// version before it, opened read only.
    /// </summary>
    private static StateDbFile Migrated(SqliteConnection connection, int from, string directory, string path, SchemaPlan plan, bool backup)
    {
        var pending = plan.Steps.Where(step => step.To > from).OrderBy(step => step.To).ToList();
        if (pending.Count > 0 && backup)
        {
            var ahHome = Path.GetDirectoryName(directory)!;
            _ = StateDbBackups.Take(connection, StateDbBackups.BackupDirectory(ahHome), plan.Now(), StateDbBackups.PreMigratePrefix + "v" + plan.Version);
            _ = StateDbBackups.Prune(ahHome, BackupSettings.Default);
        }


        var at = from;
        foreach (var step in pending)
        {
            var run = step.Prepare is { } prepare ? prepare(connection, Path.GetDirectoryName(directory)!) : step.Run!;
            connection.Exec("BEGIN IMMEDIATE");
            try
            {
                run(connection);
                connection.Exec($"UPDATE meta SET value = '{step.To}' WHERE key = 'schema_version'");
                connection.Exec("COMMIT");
                at = step.To;
            }
            catch (Exception e) when (e is SqliteException or InvalidOperationException)
            {
                Rollback(connection);
                return new StateDbFile(
                    new StateDbStatus(StateDbAvailability.ReadOnly, $"the migration to schema {step.To} failed and was rolled back, so the state database stays at schema {at}: {e.Message}"),
                    connection,
                    false,
                    directory,
                    path,
                    mayCreate: true,
                    plan);
            }
        }

        return new StateDbFile(new StateDbStatus(StateDbAvailability.Ready, string.Empty), connection, true, directory, path, mayCreate: true, plan);
    }

    private static void PrepareFiles(string directory, string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(directory);
            return;
        }

        const UnixFileMode DirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        const UnixFileMode Mode0600 = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        Directory.CreateDirectory(directory, DirectoryMode);
        const UnixFileMode GroupAndOther = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute | UnixFileMode.SetGroup | UnixFileMode.SetUser | UnixFileMode.StickyBit;
        var existing = File.GetUnixFileMode(directory);
        if ((existing & GroupAndOther) != 0)
        {
            File.SetUnixFileMode(directory, existing & ~GroupAndOther);
        }

        if (!File.Exists(path))
        {
            using var created = new FileStream(
                path,
                new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.ReadWrite, UnixCreateMode = Mode0600 });
        }

        File.SetUnixFileMode(path, Mode0600);
    }

    internal static int? ReadVersion(SqliteConnection connection)
    {
        try
        {
            return int.TryParse(connection.ScalarText("SELECT value FROM meta WHERE key = 'schema_version'"), out var v) ? v : null;
        }
        catch (SqliteException e) when (e.Message.Contains("no such table", StringComparison.Ordinal))
        {
            return null;
        }
    }

    private static void CreateTables(SqliteConnection connection)
    {
        foreach (var sql in SchemaStatements)
        {
            connection.Exec(sql);
        }
    }

    private static void CreateSchema(SqliteConnection connection)
    {
        connection.Exec("BEGIN IMMEDIATE");
        try
        {
            CreateTables(connection);
            connection.Exec("INSERT OR IGNORE INTO meta(key, value) VALUES('schema_version', '1')");
            connection.Exec("COMMIT");
        }
        catch
        {
            Rollback(connection);
            throw;
        }
    }

    private static void Rollback(SqliteConnection connection)
    {
        try
        {
            connection.Exec("ROLLBACK");
        }
        catch (SqliteException)
        {
            // no transaction is open: the failure that brought us here already ended it
        }
    }

    private static void BindKey(SqliteStatement statement, string path, FileStat stat)
    {
        statement.Bind(1, path);
        statement.Bind(2, stat.Size);
        statement.Bind(3, stat.MtimeNs);
        statement.Bind(4, stat.CtimeNs);
        statement.Bind(5, unchecked((long)stat.Inode));
    }

    private void Write(string path, FileStat stat, byte[] content)
    {
        var connection = _connection!;
        connection.Exec("BEGIN IMMEDIATE");
        try
        {
            var statement = _put ??= connection.Prepare(PutSql);
            statement.Reset();
            BindKey(statement, path, stat);
            statement.Bind(6, content);
            statement.Step();
            connection.Exec("COMMIT");
        }
        catch
        {
            Rollback(connection);
            throw;
        }
    }

    private void Forget()
    {
        _get?.Dispose();
        _put?.Dispose();
        _get = null;
        _put = null;
    }
}
