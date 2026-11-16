using System.Diagnostics;
using System.Globalization;
using Ah.Abstractions;

namespace Ah.StateDb.Tests;

public sealed class StateDbDirectTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("ahdirect").FullName;
    private readonly List<IDisposable> _open = [];

    public void Dispose()
    {
        foreach (var o in _open)
        {
            o.Dispose();
        }

        Directory.Delete(_home, recursive: true);
    }

    private string DbPath => Path.Join(_home, "state", "ah.db");

    private IStateDb Daemon()
    {
        var db = StateDbModule.Open(_home, mayCreate: true);
        _open.Add(db);
        return db;
    }

    private SqliteConnection Raw()
    {
        var connection = SqliteConnection.Open(DbPath, SqliteOpenMode.ReadWrite, 50);
        _open.Add(connection);
        return connection;
    }

    private StateDbDirect Direct(int busyMs = 150, int attempts = 3) => new(_home, new DirectWriteOptions(busyMs, attempts));

    private void InsertSession(string id, long seen = 1)
    {
        var result = Direct().Write(tx =>
        {
            tx.Execute("INSERT INTO sessions(session_id, hier_dir, team, member, started_at, last_seen_at) VALUES (?1, '/h', 't', 'm', ?2, ?2)", id, seen);
            return 0;
        });
        Assert.True(result.Ok, result.Message);
    }

    private long Count(string table)
    {
        var reader = StateDbModule.OpenReader(_home).Reader!;
        _open.Add(reader);
        return (long)reader.Query($"SELECT count(*) FROM {table}")[0][0]!;
    }

    private void SetVersion(int version)
    {
        using var raw = SqliteConnection.Open(DbPath, SqliteOpenMode.ReadWrite, 50);
        raw.Exec($"UPDATE meta SET value = '{version}' WHERE key = 'schema_version'");
    }

    /// <summary>The files another SQLite client's clean last close leaves: everything checkpointed, no <c>-wal</c>, no <c>-shm</c>.</summary>
    private void AnotherClientsCleanClose()
    {
        foreach (var open in _open.OfType<IStateDb>().ToList())
        {
            open.Dispose();
            _open.Remove(open);
        }

        using (var raw = SqliteConnection.Open(DbPath, SqliteOpenMode.ReadWrite, 50))
        {
            _ = raw.ScalarText("PRAGMA wal_checkpoint(TRUNCATE)");
        }

        File.Delete(DbPath + "-wal");
        File.Delete(DbPath + "-shm");
    }

    [Fact]
    public void AReadAfterAnotherClientsCleanCloseStillAnswers()
    {
        _ = Daemon();
        InsertSession("s1");
        AnotherClientsCleanClose();

        Assert.Equal(1, Count("sessions"));
    }

    [Fact]
    public void NegativeAReadOpenedAfterACleanCloseStillCannotWrite()
    {
        _ = Daemon();
        InsertSession("s1");
        AnotherClientsCleanClose();

        using var reader = SqliteConnection.Open(DbPath, SqliteOpenMode.ReadOnly, 50);

        Assert.Throws<SqliteException>(() => reader.Exec("DELETE FROM sessions"));
        Assert.Equal("1", reader.ScalarText("SELECT count(*) FROM sessions"));
    }

    [Fact]
    public void NegativeADatabaseNotInWalModeAndAMissingFileAreOpenedAsBefore()
    {
        var rollback = Path.Join(_home, "rollback.db");
        using (var made = SqliteConnection.Open(rollback, SqliteOpenMode.Create, 50))
        {
            made.Exec("CREATE TABLE t(a)");
        }

        using (var reader = SqliteConnection.Open(rollback, SqliteOpenMode.ReadOnly, 50))
        {
            Assert.Equal("0", reader.ScalarText("SELECT count(*) FROM t"));
            Assert.Throws<SqliteException>(() => reader.Exec("DELETE FROM t"));
            Assert.False(File.Exists(rollback + "-wal"));
        }

        var missing = Path.Join(_home, "missing.db");
        Assert.Throws<SqliteException>(() => SqliteConnection.Open(missing, SqliteOpenMode.ReadOnly, 50));
        Assert.False(File.Exists(missing));
    }

    [Fact]
    public void ADirectWriteCommitsOneTransactionAndReturnsWhatTheDelegateReturned()
    {
        _ = Daemon();

        var result = Direct().Write(tx =>
        {
            tx.Execute("INSERT INTO teams(hier_dir, team, doc, created_at, updated_at) VALUES ('/h', ?1, ?2, 1, 1)", "t1", "{}");
            return tx.Query("SELECT count(*) FROM teams")[0][0];
        });

        Assert.True(result.Ok, result.Message);
        Assert.Equal(1L, result.Value);
        Assert.Equal(1L, Count("teams"));
    }

    [Fact]
    public void ADelegateThatThrowsRollsBackAndTheExceptionPropagates()
    {
        _ = Daemon();

        _ = Assert.Throws<InvalidOperationException>(() => Direct().Write<int>(tx =>
        {
            tx.Execute("INSERT INTO teams(hier_dir, team, doc, created_at, updated_at) VALUES ('/h', 'gone', '{}', 1, 1)");
            throw new InvalidOperationException("no");
        }));

        Assert.Equal(0L, Count("teams"));
    }

    [Fact]
    public void ABusyDatabaseGivesUpWithinTheCapNamingTheDaemonAndLeavesTheDatabaseUntouched()
    {
        _ = Daemon();
        InsertSession("s1");
        Directory.CreateDirectory(Path.Join(_home, "run"));
        File.WriteAllText(Path.Join(_home, "run", "ahd.json"), $"{{\"pid\":{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}}}");
        var holder = Raw();
        holder.Exec("BEGIN IMMEDIATE");
        var reader = StateDbModule.OpenReader(_home).Reader!;
        _open.Add(reader);
        _ = reader.Query("SELECT count(*) FROM sessions");

        // what a command line process sets: the heal is started for the daemon the write names, and its answer joins the failure text
        List<int> hung = [];
        StateDbModule.OnDirectWriteHung(pid =>
        {
            hung.Add(pid);
            return "ahd heal started (pid 31337)";
        });
        var clock = Stopwatch.StartNew();
        DirectWriteResult<int> result;
        try
        {
            result = Direct(150, 3).Write(tx =>
            {
                tx.Execute("UPDATE sessions SET team = 'changed'");
                return 0;
            });
        }
        finally
        {
            StateDbModule.OnDirectWriteHung(null);
        }

        var waited = clock.Elapsed;

        Assert.False(result.Ok);
        Assert.Equal(Environment.ProcessId, result.DaemonPid);
        Assert.Equal([Environment.ProcessId], hung);
        Assert.Contains($"ahd pid {Environment.ProcessId.ToString(CultureInfo.InvariantCulture)} may be hung; ahd heal started (pid 31337)", result.Message, StringComparison.Ordinal);
        Assert.InRange(waited.TotalMilliseconds, 300, 3000);

        var read = Stopwatch.StartNew();
        var rows = reader.Query("SELECT team FROM sessions");
        Assert.True(read.ElapsedMilliseconds < 150, $"a read-only reader waited {read.ElapsedMilliseconds} ms beside a writer");
        Assert.Equal("t", rows[0][0]);

        holder.Exec("COMMIT");
        var after = Direct().Write(tx =>
        {
            tx.Execute("UPDATE sessions SET team = 'changed'");
            return 0;
        });
        Assert.True(after.Ok, after.Message);
        Assert.Equal("changed", reader.Query("SELECT team FROM sessions")[0][0]);
    }

    [Fact]
    public void ABusyDatabaseWithNoDaemonRunningSaysSoAndNamesNoPid()
    {
        _ = Daemon();
        var holder = Raw();
        holder.Exec("BEGIN IMMEDIATE");

        var result = Direct(100, 2).Write(tx => 0);

        Assert.False(result.Ok);
        Assert.Null(result.DaemonPid);
        Assert.Contains("no ahd is running", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("may be hung", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDefaultCapIsThreeAttemptsOfTwoSecondsEach()
    {
        Assert.Equal(new DirectWriteOptions(2000, 3), DirectWriteOptions.Default);
    }

    [Fact]
    public void ADatabaseNewerThanTheBinaryIsRefusedAndNothingIsWritten()
    {
        _ = Daemon();
        InsertSession("s1");
        SetVersion(StateDbModule.SchemaVersion + 1);

        var result = Direct().Write(tx =>
        {
            tx.Execute("DELETE FROM sessions");
            return 0;
        });

        Assert.False(result.Ok);
        Assert.Contains("newer than this binary's", result.Message, StringComparison.Ordinal);
        Assert.Equal(1L, Count("sessions"));
    }

    [Fact]
    public void ADatabaseOlderThanTheBinaryIsRefusedWithoutMigratingOrBackingUp()
    {
        _ = Daemon();
        _open.OfType<IStateDb>().ToList().ForEach(d => d.Dispose());
        SetVersion(StateDbModule.SchemaVersion - 1);
        var backups = StateDbBackups.List(_home).Count;

        var result = Direct().Write(tx => 0);

        Assert.False(result.Ok);
        Assert.Contains("ahd restart", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("ah db migrate", result.Message, StringComparison.Ordinal); // no such verb exists
        using var raw = SqliteConnection.Open(DbPath, SqliteOpenMode.ReadOnly, 50);
        Assert.Equal((StateDbModule.SchemaVersion - 1).ToString(CultureInfo.InvariantCulture), raw.ScalarText("SELECT value FROM meta WHERE key = 'schema_version'"));
        Assert.Equal(backups, StateDbBackups.List(_home).Count);
    }

    [Fact]
    public void OnlyAnOlderDatabaseCountsAsBehind()
    {
        Assert.Null(StateDbModule.SchemaBehind(_home)); // no database yet
        _ = Daemon();
        _open.OfType<IStateDb>().ToList().ForEach(d => d.Dispose());
        Assert.Null(StateDbModule.SchemaBehind(_home)); // current

        SetVersion(StateDbModule.SchemaVersion + 1);
        Assert.Null(StateDbModule.SchemaBehind(_home)); // newer is read only, and no daemon replacement fixes it

        SetVersion(StateDbModule.SchemaVersion - 1);
        Assert.Equal(StateDbModule.SchemaVersion - 1, StateDbModule.SchemaBehind(_home));
    }

    [Fact]
    public void AMissingDatabaseIsRefusedAndNoFileIsCreated()
    {
        Directory.CreateDirectory(Path.Join(_home, "state"));

        var result = Direct().Write(tx => 0);

        Assert.False(result.Ok);
        Assert.Contains("start ahd", result.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(DbPath));
    }

    [Fact]
    public void AReaderCannotWriteAndAMissingDatabaseGivesNoReader()
    {
        Directory.CreateDirectory(Path.Join(_home, "state"));
        Assert.Null(StateDbModule.OpenReader(_home).Reader);
        _ = Daemon();
        var reader = StateDbModule.OpenReader(_home).Reader!;
        _open.Add(reader);

        _ = Assert.Throws<SqliteException>(() => reader.Query("INSERT INTO teams(hier_dir, team, doc, created_at, updated_at) VALUES ('/h', 'x', '{}', 1, 1)"));
    }

    [Fact]
    public void TheLivenessConnectionMayWriteLastSeenAndNothingElse()
    {
        _ = Daemon();
        InsertSession("s1");
        var liveness = SqliteConnection.Open(DbPath, SqliteOpenMode.ReadWrite, 50);
        _open.Add(liveness);
        liveness.RestrictToLiveness();

        liveness.Exec("UPDATE sessions SET last_seen_at = 99 WHERE session_id = 's1'");
        Assert.Equal("99", liveness.ScalarText("SELECT last_seen_at FROM sessions WHERE session_id = 's1'"));

        // the activity table is a table of record since per-dir-files-to-db S2
        _ = Assert.Throws<SqliteException>(() => liveness.Exec("INSERT INTO activity(hier_dir, session_id, body, updated_at) VALUES ('/h', 's1', '{}', 99)"));
        _ = Assert.Throws<SqliteException>(() => liveness.Exec("DELETE FROM activity"));

        _ = Assert.Throws<SqliteException>(() => liveness.Exec("UPDATE sessions SET pid = 5"));
        _ = Assert.Throws<SqliteException>(() => liveness.Exec("INSERT INTO teams(hier_dir, team, doc, created_at, updated_at) VALUES ('/h', 'x', '{}', 1, 1)"));
        _ = Assert.Throws<SqliteException>(() => liveness.Exec("DELETE FROM members"));
        _ = Assert.Throws<SqliteException>(() => liveness.Exec("DELETE FROM sessions"));
        _ = Assert.Throws<SqliteException>(() => liveness.Exec("CREATE TABLE sneaky(x)"));
        _ = Assert.Throws<SqliteException>(() => liveness.Exec("UPDATE meta SET value = '1' WHERE key = 'schema_version'"));
    }

    [Fact]
    public void ALivenessMarkUpdatesTheSessionWhenTheLockIsFree()
    {
        _ = Daemon();
        InsertSession("s1");
        var at = DateTimeOffset.FromUnixTimeMilliseconds(5000);

        var outcome = StateDbModule.OpenLiveness(_home).Touch("s1", at);

        Assert.Equal(LivenessOutcome.Written, outcome);
        Assert.Equal(5000L, StateDbModule.OpenReader(_home).Reader!.Query("SELECT last_seen_at FROM sessions")[0][0]);
        Assert.False(File.Exists(Path.Join(_home, "state", "fallback.jsonl")));
    }

    [Fact]
    public void ALivenessMarkBehindAHeldLockExitsInTheBudgetWithOneFallbackLineThatTheNextOpenImports()
    {
        _ = Daemon();
        InsertSession("s1");
        var liveness = StateDbModule.OpenLiveness(_home);
        _ = liveness.Touch("s1", DateTimeOffset.FromUnixTimeMilliseconds(2000));
        var holder = Raw();
        holder.Exec("BEGIN IMMEDIATE");

        var clock = Stopwatch.StartNew();
        var outcome = liveness.Touch("s1", DateTimeOffset.FromUnixTimeMilliseconds(7000));
        var took = clock.ElapsedMilliseconds;

        Assert.Equal(LivenessOutcome.Deferred, outcome);
        Assert.True(took < 400, $"the liveness mark took {took} ms");
        var fallback = Path.Join(_home, "state", "fallback.jsonl");
        var line = Assert.Single(File.ReadAllLines(fallback));
        Assert.Contains("\"session\":\"s1\"", line, StringComparison.Ordinal);

        holder.Exec("COMMIT");
        _open.Remove(holder);
        holder.Dispose();
        _open.OfType<IStateDb>().ToList().ForEach(d => d.Dispose());
        _open.RemoveAll(o => o is IStateDb);
        _ = Daemon();

        Assert.Equal(7000L, StateDbModule.OpenReader(_home).Reader!.Query("SELECT last_seen_at FROM sessions")[0][0]);
        Assert.False(File.Exists(fallback));
        _ = Assert.Single(Directory.GetFiles(Path.Join(_home, "state", "replaced"), "fallback-*.jsonl"));
    }

    [Fact]
    public void ALivenessMarkForASessionThePlainDatabaseDoesNotKnowIsKeptForALaterImport()
    {
        _ = Daemon();

        var outcome = StateDbModule.OpenLiveness(_home).Touch("not-yet-imported", DateTimeOffset.FromUnixTimeMilliseconds(1));

        Assert.Equal(LivenessOutcome.Deferred, outcome);
        _ = Assert.Single(File.ReadAllLines(Path.Join(_home, "state", "fallback.jsonl")));
    }

    [Fact]
    public void ALivenessMarkWithNoStateFolderIsLostRatherThanCreatingOne()
    {
        var outcome = StateDbModule.OpenLiveness(_home).Touch("s1", DateTimeOffset.FromUnixTimeMilliseconds(1));

        Assert.Equal(LivenessOutcome.Lost, outcome);
        Assert.False(Directory.Exists(Path.Join(_home, "state")));
    }

    [Fact]
    public void AMalformedFallbackLineIsSkippedAndTheGoodOnesAreApplied()
    {
        var first = Daemon();
        InsertSession("s1");
        first.Dispose();
        _open.RemoveAll(o => o is IStateDb);
        File.WriteAllText(Path.Join(_home, "state", "fallback.jsonl"), "not json\n{\"kind\":\"other\",\"session\":\"s1\",\"at\":9}\n{\"kind\":\"liveness\",\"session\":\"s1\",\"at\":4000}\n");

        _ = Daemon();

        Assert.Equal(4000L, StateDbModule.OpenReader(_home).Reader!.Query("SELECT last_seen_at FROM sessions")[0][0]);
    }

    [Fact]
    public void DirectAndLivenessConnectionsNeverCheckpointAndTheWalGrowsUntilTheDaemonStarts()
    {
        var daemon = Daemon();
        InsertSession("s1");
        daemon.Dispose();
        _open.RemoveAll(o => o is IStateDb);
        var anotherProcess = SqliteConnection.Open(DbPath, SqliteOpenMode.ReadOnly, 50);
        _open.Add(anotherProcess);
        _ = anotherProcess.ScalarText("SELECT count(*) FROM sessions");
        var megabyte = new string('x', 1_000_000);
        for (var i = 0; i < 8; i++)
        {
            var written = Direct().Write(tx =>
            {
                tx.Execute("INSERT OR REPLACE INTO members(hier_dir, team, name, args, updated_at) VALUES ('/h', 't', ?1, ?2, 1)", "m" + i.ToString(CultureInfo.InvariantCulture), megabyte);
                return tx.Query("PRAGMA wal_autocheckpoint")[0][0];
            });
            Assert.Equal(0L, written.Value);
        }

        var wal = new FileInfo(DbPath + "-wal");
        Assert.True(wal.Length > 5_000_000, $"the log is {wal.Length} bytes, so something checkpointed it");

        anotherProcess.Dispose();
        _open.Remove(anotherProcess);
        _ = Daemon();

        Assert.True(!File.Exists(DbPath + "-wal") || new FileInfo(DbPath + "-wal").Length == 0);
    }

    [Fact]
    public void TheDaemonsCheckpointFoldsTheLogBackAndTruncateEmptiesIt()
    {
        var daemon = Daemon();
        var megabyte = new string('x', 1_000_000);
        for (var i = 0; i < 3; i++)
        {
            var written = Direct().Write(tx =>
            {
                tx.Execute("INSERT OR REPLACE INTO members(hier_dir, team, name, args, updated_at) VALUES ('/h', 't', ?1, ?2, 1)", "m" + i.ToString(CultureInfo.InvariantCulture), megabyte);
                return 0;
            });
            Assert.True(written.Ok, written.Message);
        }

        var passive = daemon.Checkpoint(truncate: false);
        Assert.True(passive.Ok);
        Assert.False(passive.Blocked);
        Assert.Equal(passive.LogFrames, passive.CheckpointedFrames);

        var truncated = daemon.Checkpoint(truncate: true);
        Assert.True(truncated.Ok);
        Assert.Equal(0, new FileInfo(DbPath + "-wal").Length);
    }

    private string Fallback => Path.Join(_home, "state", "fallback.jsonl");

    private string Hold => Path.Join(_home, "state", "fallback.hold");

    private static string Mark(string session, long at) =>
        $"{{\"kind\":\"liveness\",\"session\":\"{session}\",\"at\":{at.ToString(CultureInfo.InvariantCulture)}}}";

    private long LastSeen(string session) => (long)StateDbModule.OpenReader(_home).Reader!.Query("SELECT last_seen_at FROM sessions WHERE session_id = ?1", session)[0][0]!;

    [Fact]
    public void ABusyLockDuringImportLeavesTheLinesForTheNextImport()
    {
        var daemon = Daemon();
        InsertSession("s1");
        File.WriteAllText(Fallback, Mark("s1", 9000) + "\n");
        var holder = Raw();
        holder.Exec("BEGIN IMMEDIATE");

        Assert.Equal(0, daemon.ImportFallback());
        Assert.True(File.Exists(Fallback + ".importing"));

        holder.Exec("COMMIT");
        Assert.Equal(1, daemon.ImportFallback());
        Assert.False(File.Exists(Fallback + ".importing"));
        Assert.Equal(9000L, LastSeen("s1"));
    }

    [Fact]
    public void AnImportingFileLeftByACrashIsAppliedBeforeAnythingElse()
    {
        var daemon = Daemon();
        InsertSession("s1");
        File.WriteAllText(Fallback + ".importing", Mark("s1", 4000) + "\n");

        Assert.Equal(1, daemon.ImportFallback());

        Assert.Equal(4000L, LastSeen("s1"));
        Assert.False(File.Exists(Fallback + ".importing"));
    }

    [Fact]
    public void AHookWritingANewFallbackFileDuringAFailedImportBreaksNothingAndBothFilesAreAppliedLater()
    {
        var daemon = Daemon();
        InsertSession("s1");
        File.WriteAllText(Fallback + ".importing", Mark("s1", 5000) + "\n");
        File.WriteAllText(Fallback, Mark("s1", 6000) + "\n");
        var holder = Raw();
        holder.Exec("BEGIN IMMEDIATE");

        Assert.Equal(0, daemon.ImportFallback());
        Assert.True(File.Exists(Fallback + ".importing"));
        Assert.True(File.Exists(Fallback));

        holder.Exec("COMMIT");
        Assert.Equal(2, daemon.ImportFallback());
        Assert.Equal(6000L, LastSeen("s1"));
        Assert.False(File.Exists(Fallback));
        Assert.Equal(2, Directory.GetFiles(Path.Join(_home, "state", "replaced"), "fallback-*.jsonl").Length);
    }

    [Fact]
    public void ALineAppendedByAWriterHoldingTheOldFileDuringTheImportIsAppliedBeforeTheFileIsMoved()
    {
        _ = Daemon();
        InsertSession("s1");
        InsertSession("s2");
        File.WriteAllText(Fallback, Mark("s1", 5000) + "\n");
        var raw = Raw();

        var applied = StateDbFallback.Import(raw, _home, DateTimeOffset.UtcNow, () => File.AppendAllText(Fallback + ".importing", Mark("s2", 7000) + "\n"));

        Assert.Equal(2, applied);
        Assert.Equal(7000L, LastSeen("s2"));
        Assert.False(File.Exists(Fallback + ".importing"));
    }

    [Fact]
    public void ALineForASessionNotYetKnownIsKeptForALaterImportUntilItIsADayOldAndAStaleOneForAKnownSessionIsDropped()
    {
        var daemon = Daemon();
        InsertSession("s1", seen: 100);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        File.WriteAllText(Fallback, string.Join("\n", Mark("later", now - 3_600_000), Mark("ancient", now - 48L * 3_600_000), Mark("s1", 50), string.Empty));

        Assert.Equal(0, daemon.ImportFallback());

        var kept = Assert.Single(File.ReadAllLines(Hold));
        Assert.Contains("\"later\"", kept, StringComparison.Ordinal);
        Assert.False(File.Exists(Fallback));
        Assert.False(File.Exists(Fallback + ".importing"));
        InsertSession("later");
        Assert.Equal(1, daemon.ImportFallback());
        Assert.Equal(now - 3_600_000, LastSeen("later"));
    }

    [Fact]
    public async Task ASchemaChangeCommittedWhileTheWriterWaitedForTheLockIsSeenInsideTheTransaction()
    {
        _ = Daemon();
        var holder = Raw();
        holder.Exec("BEGIN IMMEDIATE");
        holder.Exec($"UPDATE meta SET value = '{(StateDbModule.SchemaVersion + 1).ToString(CultureInfo.InvariantCulture)}' WHERE key = 'schema_version'");
        var release = Task.Run(
            async () =>
            {
                await Task.Delay(300, TestContext.Current.CancellationToken);
                holder.Exec("COMMIT");
            },
            TestContext.Current.CancellationToken);

        var result = Direct(2000, 3).Write(tx =>
        {
            tx.Execute("INSERT INTO teams(hier_dir, team, doc, created_at, updated_at) VALUES ('/h', 'late', '{}', 1, 1)");
            return 0;
        });
        await release;

        Assert.False(result.Ok);
        Assert.Contains("newer than this binary's", result.Message, StringComparison.Ordinal);
        Assert.Equal(0L, Count("teams"));
    }

    [Fact]
    public async Task TheDaemonCommittingAfterTheClientFellBackLeavesOneApplicationAndBothPathsReturnItsResult()
    {
        var daemon = Daemon();
        var applications = 0;
        var started = new ManualResetEventSlim();
        var inDaemon = Task.Run(
            () => daemon.WriteOnce("w-1", DateTimeOffset.UtcNow.AddMilliseconds(500), tx =>
            {
                _ = Interlocked.Increment(ref applications);
                started.Set();
                Thread.Sleep(600);
                return "daemon-result";
            }),
            TestContext.Current.CancellationToken);
        Assert.True(started.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        var viaDirect = Direct(2000, 3).WriteOnce("w-1", tx =>
        {
            _ = Interlocked.Increment(ref applications);
            return "direct-result";
        });
        var viaDaemon = await inDaemon;

        Assert.Equal(1, applications);
        Assert.Equal(OnceStatus.Applied, viaDaemon.Status);
        Assert.Equal(OnceStatus.Already, viaDirect.Status);
        Assert.Equal("daemon", viaDirect.By);
        Assert.Equal(viaDaemon.Result, viaDirect.Result);
        Assert.Equal(1L, Count("applied_writes"));
    }

    [Fact]
    public void ADirectWriteThatCommittedFirstMakesTheLateDaemonRequestChangeNothing()
    {
        var daemon = Daemon();
        var applications = 0;

        var first = Direct().WriteOnce("w-2", tx =>
        {
            applications++;
            tx.Execute("INSERT INTO teams(hier_dir, team, doc, created_at, updated_at) VALUES ('/h', 'once', '{}', 1, 1)");
            return "direct-result";
        });
        var late = daemon.WriteOnce("w-2", DateTimeOffset.UtcNow.AddSeconds(10), tx =>
        {
            applications++;
            tx.Execute("INSERT INTO teams(hier_dir, team, doc, created_at, updated_at) VALUES ('/h', 'twice', '{}', 1, 1)");
            return "daemon-result";
        });

        Assert.Equal(OnceStatus.Applied, first.Status);
        Assert.Equal(OnceStatus.Already, late.Status);
        Assert.Equal("direct", late.By);
        Assert.Equal("direct-result", late.Result);
        Assert.Equal(1, applications);
        Assert.Equal(1L, Count("teams"));
    }

    [Fact]
    public void ARequestThatReachesTheDaemonAfterItsDeadlineIsExpiredAndAppliesNothingNorRecordsTheId()
    {
        var daemon = Daemon();
        var ran = false;

        var outcome = daemon.WriteOnce("w-3", DateTimeOffset.UtcNow.AddSeconds(-1), tx =>
        {
            ran = true;
            return "x";
        });

        Assert.Equal(OnceStatus.Expired, outcome.Status);
        Assert.False(ran);
        Assert.Equal(0L, Count("applied_writes"));
    }

    [Fact]
    public void ADifferentIdIsAppliedEvenWhenAnotherWasAlreadyRecorded()
    {
        var daemon = Daemon();
        _ = Direct().WriteOnce("w-4", tx => "a");

        var other = daemon.WriteOnce("w-5", DateTimeOffset.UtcNow.AddSeconds(10), tx => "b");

        Assert.Equal(OnceStatus.Applied, other.Status);
        Assert.Equal(2L, Count("applied_writes"));
    }

    [Fact]
    public void AWriteOnceThatThrowsRecordsNoIdSoARetryApplies()
    {
        _ = Daemon();

        _ = Assert.Throws<InvalidOperationException>(() => Direct().WriteOnce("w-6", tx => throw new InvalidOperationException("no")));
        var retry = Direct().WriteOnce("w-6", tx => "ok");

        Assert.Equal(OnceStatus.Applied, retry.Status);
        Assert.Equal(1L, Count("applied_writes"));
    }

    [Fact]
    public void AWriteOnceOnADatabaseTheDirectWriterRefusesIsRefusedWithTheReason()
    {
        _ = Daemon();
        SetVersion(StateDbModule.SchemaVersion + 1);

        var outcome = Direct().WriteOnce("w-7", tx => "x");

        Assert.Equal(OnceStatus.Refused, outcome.Status);
        Assert.Contains("newer than this binary's", outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutAnIdTheSameWriteArrivingOnBothPathsLandsTwice()
    {
        var daemon = Daemon();
        var applications = 0;

        _ = Direct().Write(tx =>
        {
            applications++;
            return 0;
        });
        _ = Direct().Write(tx =>
        {
            applications++;
            return 0;
        });

        Assert.Equal(2, applications);
        Assert.Equal(0L, Count("applied_writes"));
        Assert.Equal(StateDbAvailability.Ready, daemon.Status.Availability);
    }

    [Fact]
    public void PruningRemovesRowsOlderThanADayAndKeepsTheRest()
    {
        var daemon = Daemon();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var raw = Raw();
        raw.Exec($"INSERT INTO applied_writes(write_id, applied_at, path, result) VALUES ('old', {(now - 25L * 3_600_000).ToString(CultureInfo.InvariantCulture)}, 'direct', 'x')");
        raw.Exec($"INSERT INTO applied_writes(write_id, applied_at, path, result) VALUES ('edge', {(now - 23L * 3_600_000).ToString(CultureInfo.InvariantCulture)}, 'direct', 'x')");
        raw.Exec($"INSERT INTO applied_writes(write_id, applied_at, path, result) VALUES ('new', {now.ToString(CultureInfo.InvariantCulture)}, 'daemon', 'x')");

        Assert.Equal(1, daemon.PruneAppliedWrites(TimeSpan.FromHours(24)));

        Assert.Equal(2L, Count("applied_writes"));
        Assert.Equal("0", raw.ScalarText("SELECT count(*) FROM applied_writes WHERE write_id = 'old'"));
    }

    [Fact]
    public void AHeldLineIsRetriedOnEveryImportWithoutWritingAnotherArchiveFile()
    {
        var daemon = Daemon();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        File.WriteAllText(Fallback, Mark("later", now - 60_000) + "\n");
        _ = daemon.ImportFallback();
        var archived = Directory.GetFiles(Path.Join(_home, "state", "replaced"), "fallback-*.jsonl").Length;

        for (var i = 0; i < 5; i++)
        {
            _ = daemon.ImportFallback();
        }

        Assert.Equal(1, archived);
        Assert.Equal(archived, Directory.GetFiles(Path.Join(_home, "state", "replaced"), "fallback-*.jsonl").Length);
        Assert.Single(File.ReadAllLines(Hold));
        Assert.False(File.Exists(Fallback));
    }

    [Fact]
    public void TheHoldKeepsOnlyTheNewestMarkPerSessionAndDropsTheHoldWhenItsLineApplies()
    {
        var daemon = Daemon();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        File.WriteAllText(Fallback, string.Join("\n", Mark("later", now - 5000), Mark("later", now - 1000), Mark("later", now - 9000), string.Empty));

        _ = daemon.ImportFallback();

        var held = Assert.Single(File.ReadAllLines(Hold));
        Assert.Contains((now - 1000).ToString(CultureInfo.InvariantCulture), held, StringComparison.Ordinal);
        InsertSession("later");
        Assert.Equal(1, daemon.ImportFallback());
        Assert.Equal(now - 1000, LastSeen("later"));
        Assert.False(File.Exists(Hold));
    }

    [Fact]
    public void ArchivedFallbackFilesAreKeptAWeekAndNothingElseInTheReplacedFolderIsTouched()
    {
        var daemon = Daemon();
        var replaced = Path.Join(_home, "state", "replaced");
        Directory.CreateDirectory(replaced);
        var old = Path.Join(replaced, "fallback-old.jsonl");
        var recent = Path.Join(replaced, "fallback-recent.jsonl");
        var other = Path.Join(replaced, "ah-old.db");
        File.WriteAllText(old, "x");
        File.WriteAllText(recent, "x");
        File.WriteAllText(other, "x");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-8));
        File.SetLastWriteTimeUtc(recent, DateTime.UtcNow.AddDays(-6));
        File.SetLastWriteTimeUtc(other, DateTime.UtcNow.AddDays(-30));

        _ = daemon.ImportFallback();

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
        Assert.True(File.Exists(other));
    }

    private void Registration(string id, string address, string? version, string at = "2026-10-21T10:00:00.000Z", string pid = "4242", string? extra = null, bool placed = true)
    {
        Directory.CreateDirectory(Path.Join(_home, "registrations"));
        var versionPart = version is null ? string.Empty : $",\"ah_version\":\"{version}\"";
        var team = address[(address.IndexOf('@', StringComparison.Ordinal) + 1)..];
        var teamFile = placed ? $",\"team_file\":\"{Path.Join(_home, "h", "teams", team + ".json")}\"" : string.Empty;
        File.WriteAllText(Path.Join(_home, "registrations", id + ".json"), $"{{\"v\":1,\"session_id\":\"{id}\",\"address\":\"{address}\",\"pid\":{pid},\"cwd\":\"/w\"{versionPart},\"at\":\"{at}\"{teamFile}{extra}}}\n");
    }

    [Fact]
    public void TheRegistrationTeamFileLookupAgreesWithTheMailboxLookupOnTheSharedCorpusExceptWhereTheCorpusSaysItDiffers()
    {
        var corpus = System.Text.Json.JsonDocument.Parse(File.ReadAllText(CorpusPath()));
        foreach (var testCase in corpus.RootElement.EnumerateArray())
        {
            var registration = Ah.Core.Json.JsonText.Parse(System.Text.Encoding.UTF8.GetBytes(testCase.GetProperty("registration").GetRawText()));
            var expected = testCase.GetProperty("state_db").GetString();

            Assert.True(expected == StateDbSessions.TeamFileOf(registration, testCase.GetProperty("address").GetString()), testCase.GetProperty("name").GetString());
        }
    }

    private static string CorpusPath()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Join(dir.FullName, "tests", "corpus", "registration-team-files.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("registration-team-files.json");
    }

    [Fact]
    public void ARegistrationOfSeveralTeamsTakesTheDirectoryOfThePrimaryAddressTeamNotAnother()
    {
        var a = Path.Join(_home, "ha", "teams", "alpha.json");
        var b = Path.Join(_home, "hb", "teams", "beta.json");
        Registration("s-a", "alpha-orch@alpha", "2.5.0", placed: false, extra: $",\"addresses\":[\"beta-orch@beta\",\"alpha-orch@alpha\"],\"team_files\":{{\"alpha-orch@alpha\":\"{a}\",\"beta-orch@beta\":\"{b}\"}},\"team_file\":\"{b}\"");

        var row = Assert.Single(StateDbSessions.Read(_home).Rows);

        Assert.Equal("alpha", row.Team);
        Assert.Equal(StateDbTeamKey.Of(a)!.Value.HierDir, row.HierDir);
    }

    [Fact]
    public void ARegistrationOfSeveralTeamsWithNoEntryForItsPrimaryAddressIsUnplacedRatherThanGivenAnothersDirectory()
    {
        var b = Path.Join(_home, "hb", "teams", "beta.json");
        Registration("s-a", "alpha-orch@alpha", "2.5.0", placed: false, extra: $",\"team_files\":{{\"beta-orch@beta\":\"{b}\"}},\"team_file\":\"{b}\"");

        Assert.Empty(Assert.Single(StateDbSessions.Read(_home).Rows).HierDir);
    }
}
