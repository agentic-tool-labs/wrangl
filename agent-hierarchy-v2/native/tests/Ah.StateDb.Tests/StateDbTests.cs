using Ah.Abstractions;

namespace Ah.StateDb.Tests;

public sealed class StateDbTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("ahstatedb").FullName;
    private readonly List<IDisposable> _open = [];

    private string DbPath => Path.Join(_home, "state", "ah.db");

    public void Dispose()
    {
        foreach (var d in _open)
        {
            d.Dispose();
        }

        Directory.Delete(_home, recursive: true);
    }

    private static FileStat Stat(long size = 10, long mtime = 1_000, long ctime = 2_000, ulong inode = 77) =>
        new(FileKind.File, size, 0, inode, 0, 0, 0, mtime, ctime);

    private IStateDb Open(bool mayCreate = true)
    {
        var db = StateDbModule.Open(_home, mayCreate);
        _open.Add(db);
        return db;
    }

    private SqliteConnection Raw()
    {
        var c = SqliteConnection.Open(DbPath, create: false, 1000);
        _open.Add(c);
        return c;
    }

    [Fact]
    public void AStoredFileComesBackForTheSameStatTuple()
    {
        var db = Open();
        Assert.Equal(StateDbAvailability.Ready, db.Status.Availability);
        db.FileCache.Store("/a/msgs/x.md", Stat(), [1, 2, 3]);
        Assert.Equal(new byte[] { 1, 2, 3 }, db.FileCache.Lookup("/a/msgs/x.md", Stat()));
    }

    [Theory]
    [InlineData("size")]
    [InlineData("mtime")]
    [InlineData("ctime")]
    [InlineData("inode")]
    [InlineData("path")]
    public void ADifferenceInAnyOneKeyFieldIsAMiss(string field)
    {
        var db = Open();
        db.FileCache.Store("/a/msgs/x.md", Stat(), [1, 2, 3]);
        var path = field == "path" ? "/a/msgs/y.md" : "/a/msgs/x.md";
        var stat = field switch
        {
            "size" => Stat(size: 11),
            "mtime" => Stat(mtime: 1_001),
            "ctime" => Stat(ctime: 2_001),
            "inode" => Stat(inode: 78),
            _ => Stat(),
        };
        Assert.Null(db.FileCache.Lookup(path, stat));
    }

    [Fact]
    public void StoringAgainReplacesWhatWasKeptAndTheOldTupleMisses()
    {
        var db = Open();
        db.FileCache.Store("/p", Stat(ctime: 1), [1]);
        db.FileCache.Store("/p", Stat(ctime: 2), [2]);
        Assert.Equal(new byte[] { 2 }, db.FileCache.Lookup("/p", Stat(ctime: 2)));
        Assert.Null(db.FileCache.Lookup("/p", Stat(ctime: 1)));
    }

    [Fact]
    public void AnEmptyFileIsKeptAsEmptyNotAsAMiss()
    {
        var db = Open();
        db.FileCache.Store("/empty", Stat(size: 0), []);
        Assert.NotNull(db.FileCache.Lookup("/empty", Stat(size: 0)));
        Assert.Empty(db.FileCache.Lookup("/empty", Stat(size: 0))!);
        Assert.Null(db.FileCache.Lookup("/other", Stat(size: 0)));
    }

    [Fact]
    public void ABinaryContentWithNulBytesRoundTripsUnchanged()
    {
        var db = Open();
        byte[] content = [0, 255, 0, 10, 13, 0];
        db.FileCache.Store("/bin", Stat(), content);
        Assert.Equal(content, db.FileCache.Lookup("/bin", Stat()));
    }

    [Fact]
    public void TheDatabaseIsInWriteAheadLogMode()
    {
        _ = Open();
        Assert.Equal("wal", Raw().ScalarText("PRAGMA journal_mode"));
    }

    [Fact]
    public void TheStateDirectoryIs0700AndTheDatabaseAndItsLogFilesAre0600()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var db = Open();
        db.FileCache.Store("/a", Stat(), [1]);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Path.Join(_home, "state")));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(DbPath));
        foreach (var sidecar in new[] { DbPath + "-wal", DbPath + "-shm" })
        {
            Assert.True(File.Exists(sidecar));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(sidecar));
        }
    }

    [Fact]
    public void AWiderExistingStateDirectoryIsNarrowedTo0700()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var dir = Path.Join(_home, "state");
        Directory.CreateDirectory(dir);
        File.SetUnixFileMode(dir, (UnixFileMode)0b111_111_111);
        _ = Open();
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(dir));
    }

    [Fact]
    public void AProcessThatMayNotCreateFindsNoDatabaseAndCreatesNothing()
    {
        var db = Open(mayCreate: false);
        Assert.Equal(StateDbAvailability.Unavailable, db.Status.Availability);
        Assert.False(Directory.Exists(Path.Join(_home, "state")));
        db.FileCache.Store("/a", Stat(), [1]);
        Assert.Null(db.FileCache.Lookup("/a", Stat()));
        Assert.False(Directory.Exists(Path.Join(_home, "state")));
    }

    [Fact]
    public void AProcessThatMayNotCreateReadsWhatTheDaemonStored()
    {
        Open().FileCache.Store("/a", Stat(), [9]);
        var reader = Open(mayCreate: false);
        Assert.Equal(StateDbAvailability.Ready, reader.Status.Availability);
        Assert.Equal(new byte[] { 9 }, reader.FileCache.Lookup("/a", Stat()));
    }

    [Fact]
    public void AnEmptyDatabaseFileIsNotGivenASchemaByAProcessThatMayNotCreate()
    {
        Directory.CreateDirectory(Path.Join(_home, "state"));
        File.WriteAllBytes(DbPath, []);
        var db = Open(mayCreate: false);
        Assert.Equal(StateDbAvailability.Unavailable, db.Status.Availability);
        Assert.Empty(File.ReadAllBytes(DbPath));
    }

    [Theory]
    [InlineData(1, 1, "Normal")]
    [InlineData(0, 1, "DbOlder")]
    [InlineData(2, 1, "DbNewer")]
    public void TheSchemaGateDecidesInAllThreeDirections(int db, int binary, string expected) =>
        Assert.Equal(expected, StateDbFile.Decide(db, binary).ToString());

    [Fact]
    public void ADatabaseOfANewerSchemaIsReadOnlyAndIsNeverWritten()
    {
        Open().FileCache.Store("/a", Stat(), [1]);
        _open.Clear();
        var raw = SqliteConnection.Open(DbPath, create: false, 1000);
        raw.Exec("UPDATE meta SET value = '99' WHERE key = 'schema_version'");
        raw.Dispose();

        var db = Open();
        Assert.Equal(StateDbAvailability.ReadOnly, db.Status.Availability);
        Assert.Equal(new byte[] { 1 }, db.FileCache.Lookup("/a", Stat()));
        db.FileCache.Store("/b", Stat(), [2]);
        Assert.Null(db.FileCache.Lookup("/b", Stat()));
        Assert.Equal("99", Raw().ScalarText("SELECT value FROM meta WHERE key = 'schema_version'"));
    }

    [Fact]
    public void ADatabaseOfAnOlderSchemaIsUsedAsItIsAndNeverMigratedByAProcessThatMayNotCreate()
    {
        Open().FileCache.Store("/a", Stat(), [1]);
        _open.Clear();
        var raw = SqliteConnection.Open(DbPath, create: false, 1000);
        raw.Exec("UPDATE meta SET value = '0' WHERE key = 'schema_version'");
        raw.Dispose();

        var db = Open(mayCreate: false);
        Assert.Equal(StateDbAvailability.Ready, db.Status.Availability);
        Assert.Equal(new byte[] { 1 }, db.FileCache.Lookup("/a", Stat()));
        Assert.Equal("0", Raw().ScalarText("SELECT value FROM meta WHERE key = 'schema_version'"));
    }

    [Fact]
    public void AMissingLibraryMakesTheDatabaseUnavailableWithTheInstallMessageAndCreatesNothing()
    {
        var db = StateDbFile.Open(_home, mayCreate: true, () => throw new DllNotFoundException("libsqlite3"));
        Assert.Equal(StateDbAvailability.LibraryMissing, db.Status.Availability);
        Assert.StartsWith("SQLite >= 3.24 not found: ", db.Status.Detail, StringComparison.Ordinal);
        db.Store("/a", Stat(), [1]);
        Assert.Null(db.Lookup("/a", Stat()));
        Assert.False(Directory.Exists(Path.Join(_home, "state")));
    }

    [Fact]
    public void ALibraryOneVersionBelowTheMinimumIsTooOldAndTheMinimumItselfIsAccepted()
    {
        var old = StateDbFile.Open(_home, mayCreate: true, () => StateDbModule.MinimumLibraryVersion - 1);
        Assert.Equal(StateDbAvailability.LibraryTooOld, old.Status.Availability);
        Assert.False(Directory.Exists(Path.Join(_home, "state")));

        var exact = StateDbFile.Open(_home, mayCreate: true, () => StateDbModule.MinimumLibraryVersion);
        _open.Add(exact);
        Assert.Equal(StateDbAvailability.Ready, exact.Status.Availability);
    }

    [Fact]
    public void TheLinuxResolverTriesTheVersionedNameBeforeTheBareOne()
    {
        Assert.Equal(["libsqlite3.so.0", "libsqlite3.so"], Sqlite.Candidates("linux"));
        Assert.Equal(["libsqlite3.dylib"], Sqlite.Candidates("macos"));
        Assert.Empty(Sqlite.Candidates("other"));
    }

    [Fact]
    public void ADamagedDatabaseIsMovedToTheDamagedFolderAndNothingIsOpenedByAProcessThatMayCreate()
    {
        Directory.CreateDirectory(Path.Join(_home, "state"));
        var garbage = new string('x', 4096);
        File.WriteAllText(DbPath, garbage);
        var db = Open();
        Assert.Equal(StateDbAvailability.Damaged, db.Status.Availability);
        Assert.False(File.Exists(DbPath));
        db.FileCache.Store("/a", Stat(), [5]);
        Assert.Null(db.FileCache.Lookup("/a", Stat()));
        var kept = Assert.Single(Directory.GetFiles(Path.Join(_home, "state", "damaged")));
        Assert.Equal(garbage, File.ReadAllText(kept));
    }

    [Fact]
    public void ADamagedDatabaseIsLeftAloneByAProcessThatMayNotCreate()
    {
        Directory.CreateDirectory(Path.Join(_home, "state"));
        var garbage = new string('x', 4096);
        File.WriteAllText(DbPath, garbage);
        var db = Open(mayCreate: false);
        Assert.Equal(StateDbAvailability.Unavailable, db.Status.Availability);
        Assert.Equal(garbage, File.ReadAllText(DbPath));
    }

    [Fact]
    public void ADroppedCacheTableIsRebuiltOnTheNextStoreWithoutAnError()
    {
        var db = Open();
        db.FileCache.Store("/a", Stat(), [1]);
        Raw().Exec("DROP TABLE cache_files");
        Assert.Null(db.FileCache.Lookup("/a", Stat()));
        db.FileCache.Store("/a", Stat(), [2]);
        Assert.Equal(new byte[] { 2 }, db.FileCache.Lookup("/a", Stat()));
    }

    [Fact]
    public void ADatabaseFileDeletedUnderTheDaemonIsStoreMissingAndNotRecreated()
    {
        var db = Open();
        db.FileCache.Store("/a", Stat(), [1]);
        File.Delete(DbPath);
        File.Delete(DbPath + "-wal");
        File.Delete(DbPath + "-shm");

        Assert.Null(db.FileCache.Lookup("/a", Stat()));
        db.FileCache.Store("/b", Stat(), [2]);

        Assert.False(File.Exists(DbPath));
        Assert.Null(db.FileCache.Lookup("/b", Stat()));
        Assert.Equal(StateDbAvailability.StoreMissing, db.Status.Availability);
        Assert.Contains("ah db restore", db.Status.Detail, StringComparison.Ordinal);
        Assert.Contains("ah db init --accept-empty", db.Status.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// Prune's closed set by query (ah-msgs-fast-2 §3.8 item 7, ruling Q5): not live and not answered since the cutoff, a never-answered one
    /// included. Negatives: answered after the cutoff, still live, archived.
    /// </summary>
    [Fact]
    public void TheClosedSetIsNotLiveAndNotAnsweredSinceTheCutoff()
    {
        _ = Open();
        var pool = Ah.Core.Json.MsgRows.PoolKey("/p/.claude/ah2");
        foreach (var (id, state, live, answered, opened) in new[]
        {
            ("a-old", "answered", 0, "100", "50"), ("b-recent", "answered", 0, "900", "50"), ("c-live", "open", 1, "100", "50"),
            ("d-never-old", "closed", 0, "NULL", "100"), ("e-archived", "archived", 0, "100", "50"), ("f-never-young", "retired", 0, "NULL", "900"),
        })
        {
            using var insert = Raw().Prepare($"INSERT INTO msg_exchanges(pool_dir, id, request_pk, state, live, answered_ms, opened_ms) VALUES ('{pool}', '{id}', 1, '{state}', {live}, {answered}, {opened})");
            _ = insert.Step();
        }

        // F6 negative: f, retired and never answered, younger than the cutoff, is kept
        Assert.Equal(["a-old", "d-never-old"], StateDbModule.CreateMsgSource(_home).ClosedIdsAnsweredBefore("/p/.claude/ah2", 500));
    }

    [Fact]
    public void AHomeWithNoDatabaseYetHasNoStatusEntriesButADatabaseLostAfterACutOverStillThrows()
    {
        var store = StateDbModule.CreateStatusEntries(_home);
        Assert.Empty(store.StatusDirs());
        Assert.Empty(store.Listing("/p"));
        Assert.False(File.Exists(DbPath));

        // negative: an unreadable authority marker beside no database is store-missing, an alert rather than an empty store
        _ = Directory.CreateDirectory(Path.Join(_home, "state"));
        File.WriteAllText(Path.Join(_home, "state", "ah.db.authority.json"), "{not json");
        Assert.Contains("store-missing", Assert.Throws<InvalidOperationException>(() => store.StatusDirs()).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryTableIsNamedAsEitherDerivedOrAuthoritative()
    {
        _ = Open();
        var tables = new List<string>();
        using (var statement = Raw().Prepare("SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name"))
        {
            while (statement.Step())
            {
                tables.Add(statement.Text(0));
            }
        }

        // teams, sessions and members are imports of files that still own that data, so deleting the database loses nothing yet.
        // Once a domain is cut over its table holds the only copy and moves to the authoritative list, and a missing database must
        // raise an alert rather than be recreated empty. A new table fails this test until it is placed in one list or the other.
        Assert.Empty(StateDbModule.DerivedTables.Intersect(StateDbModule.AuthoritativeTables));
        Assert.Equal(tables, StateDbModule.DerivedTables.Concat(StateDbModule.AuthoritativeTables).Order(StringComparer.Ordinal).ToList());
        Assert.Equal(["activity", "cache_files", "members", "session_pids", "status_entries", "teams", "timeline_events"], StateDbModule.DerivedTables);
    }

    [Fact]
    public async Task AStatementThatFailsOnOneThreadNeverDisposesOneAnotherThreadIsStepping()
    {
        var db = StateDbFile.Open(_home, mayCreate: true);
        _open.Add(db);
        db.Store("/hot", Stat(), [7]);
        Assert.NotNull(db.Lookup("/hot", Stat()));
        Raw().Exec("DROP TABLE cache_files");

        using var inHook = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var calls = 0;
        db.BeforeForget = () =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                inHook.Set();
                _ = release.Wait(TimeSpan.FromSeconds(10));
            }
        };

        var failing = Task.Run(() => db.Lookup("/hot", Stat()), TestContext.Current.CancellationToken);
        Assert.True(inHook.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken), "the failing lookup reached the point before it drops its statements");
        var other = Task.Run(() => db.Lookup("/other", Stat()), TestContext.Current.CancellationToken);

        var finishedFirst = await Task.WhenAny(other, Task.Delay(300, TestContext.Current.CancellationToken));

        Assert.NotSame(other, finishedFirst);
        release.Set();
        await Task.WhenAll(failing, other);
    }

    [Fact]
    public void ADatabaseThatCannotBeReopenedAfterItsFileIsRemovedIsLoggedOnceAndTheCacheGoesQuiet()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = new List<string>();
        var db = StateDbModule.Open(_home, mayCreate: true, lines.Add);
        _open.Add(db);
        db.FileCache.Store("/a", Stat(), [1]);
        var directory = Path.Join(_home, "state");
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            File.Delete(DbPath + suffix);
        }

        // without its marker the home reads as never created, so the daemon tries to reopen; with it, the loss is store-missing (ALostDatabaseIsLoggedOnceAsStoreMissing)
        File.Delete(StateDbAuthority.MarkerPath(_home));
        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            db.FileCache.Store("/b", Stat(), [2]);
            db.FileCache.Store("/c", Stat(), [3]);
            Assert.Null(db.FileCache.Lookup("/b", Stat()));
        }
        finally
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        Assert.Single(lines);
        Assert.Contains("state database", lines[0], StringComparison.Ordinal);
        Assert.Equal(StateDbAvailability.Unavailable, db.Status.Availability);
    }

    [Fact]
    public void ALostDatabaseIsLoggedOnceAsStoreMissing()
    {
        var lines = new List<string>();
        var db = StateDbModule.Open(_home, mayCreate: true, lines.Add);
        _open.Add(db);
        db.FileCache.Store("/a", Stat(), [1]);
        File.Delete(DbPath);

        db.FileCache.Store("/b", Stat(), [2]);
        db.FileCache.Store("/c", Stat(), [3]);

        Assert.Single(lines);
        Assert.Contains("store-missing", lines[0], StringComparison.Ordinal);
        Assert.Equal(StateDbAvailability.StoreMissing, db.Status.Availability);
    }

    [Fact]
    public void AStateDirectoryWithNoGroupOrOtherBitsIsLeftAsItIs()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Open().FileCache.Store("/a", Stat(), [1]);
        _open.Clear();
        var directory = Path.Join(_home, "state");
        var readOnly = UnixFileMode.UserRead | UnixFileMode.UserExecute;
        File.SetUnixFileMode(directory, readOnly);
        try
        {
            _ = StateDbModule.Open(_home, mayCreate: true);
            Assert.Equal(readOnly, File.GetUnixFileMode(directory));
        }
        finally
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void ADatabaseFileDeletedUnderAProcessThatMayNotCreateIsNotRecreated()
    {
        Open().FileCache.Store("/a", Stat(), [1]);
        var reader = Open(mayCreate: false);
        File.Delete(DbPath);
        File.Delete(DbPath + "-wal");
        File.Delete(DbPath + "-shm");

        reader.FileCache.Store("/b", Stat(), [2]);

        Assert.False(File.Exists(DbPath));
    }

    [Fact]
    public void TwoConnectionsToOneDatabaseBothSeeEachOthersStores()
    {
        var first = Open();
        var second = Open();
        first.FileCache.Store("/a", Stat(), [1]);
        second.FileCache.Store("/b", Stat(), [2]);
        Assert.Equal(new byte[] { 2 }, first.FileCache.Lookup("/b", Stat()));
        Assert.Equal(new byte[] { 1 }, second.FileCache.Lookup("/a", Stat()));
    }
}
