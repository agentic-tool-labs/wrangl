namespace Ah.StateDb.Tests;

/// <summary>Reads through a pool keep one connection between them, and never read anything older for it.</summary>
public sealed class StateDbStoreReadsTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("ahreads").FullName;
    private readonly StateDbReadPool _pool = new();

    public void Dispose()
    {
        // a birth no kept connection has closes each one
        _ = _pool.Rent(DateTime.MinValue);
        Directory.Delete(_home, recursive: true);
    }

    private string Path_ => StateDbBackups.DatabasePath(_home);

    private static void Make(string path, long value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var writer = SqliteConnection.Open(path, create: true, 1000);
        _ = StateDbRows.Run(writer, "CREATE TABLE t(v INTEGER)", []);
        _ = StateDbRows.Run(writer, "INSERT INTO t VALUES (?1)", [value]);
    }

    private (SqliteConnection Connection, long Value) Read(StateDbReadPool? pool) =>
        StateDbStoreReads.Reading(_home, connection => (connection, (long)StateDbRows.Run(connection, "SELECT MAX(v) FROM t", [])[0][0]!), pool);

    [Fact]
    public void ReadsThroughAPoolReuseTheirConnectionAndReadsWithoutOneDoNot()
    {
        Make(Path_, 1);

        var unpooled = Read(null);
        Assert.NotSame(unpooled.Connection, Read(null).Connection);
        Assert.Equal(0, _pool.IdleCount);

        var first = Read(_pool);
        Assert.Same(first.Connection, Read(_pool).Connection);
        Assert.Equal(1, _pool.IdleCount);
    }

    [Fact]
    public void APooledConnectionPreparesAQueryOnceAndReadsEachBindingAndAnUnpooledOneDoesNotCache()
    {
        Make(Path_, 1);
        using (var writer = SqliteConnection.Open(Path_, create: false, 1000))
        {
            _ = StateDbRows.Run(writer, "INSERT INTO t VALUES (?1)", [7L]);
        }

        const string Sql = "SELECT COUNT(*) FROM t WHERE v = ?1";
        long Count(SqliteConnection c, long v) => (long)StateDbRows.Run(c, Sql, [v])[0][0]!;

        var (connection, before) = StateDbStoreReads.Reading(_home, c => (c, c.PrepareCount), _pool);
        Assert.Equal(1, StateDbStoreReads.Reading(_home, c => Count(c, 7), _pool));
        Assert.Equal(0, StateDbStoreReads.Reading(_home, c => Count(c, 99), _pool));
        Assert.Equal(1, StateDbStoreReads.Reading(_home, c => Count(c, 1), _pool));
        Assert.Equal(1, StateDbStoreReads.Reading(_home, c => c.PrepareCount, _pool) - before);
        Assert.Same(connection, StateDbStoreReads.Reading(_home, c => c, _pool));

        using var unpooled = SqliteConnection.Open(Path_, create: false, 1000);
        _ = Count(unpooled, 7);
        _ = Count(unpooled, 7);
        Assert.Equal(2, unpooled.PrepareCount);
    }

    [Fact]
    public void ReadsWithoutAPoolReuseTheSharedPoolOfTheirDatabaseOnlyAndManyHomesHoldBoundedDescriptors()
    {
        // b434: the daemon's pooled stores share their pool with the reads naming none; a pool per path that never closed leaked until EBADF
        static long Value(SqliteConnection c) => (long)StateDbRows.Run(c, "SELECT MAX(v) FROM t", [])[0][0]!;
        static int Fds() => Directory.GetFileSystemEntries("/dev/fd").Length;
        var homes = Enumerable.Range(0, 500).Select(i => Path.Join(_home, "h" + i)).ToList();
        foreach (var home in homes)
        {
            Make(StateDbBackups.DatabasePath(home), 1);
        }

        var before = Fds();
        foreach (var home in homes)
        {
            _ = StateDbModule.OpenPooledStores(home);
            var first = StateDbStoreReads.Reading(home, c => (c, Value(c)));
            Assert.Same(first.Item1, StateDbStoreReads.Reading(home, c => c));
        }

        var other = Path.Join(_home, "other");
        Make(StateDbBackups.DatabasePath(other), 1);
        Assert.NotSame(StateDbStoreReads.Reading(other, c => c), StateDbStoreReads.Reading(other, c => c));
        Assert.InRange(Fds() - before, int.MinValue, 40);
    }

    [Fact]
    public void AKeptConnectionSeesAWriteMadeAfterItsLastRead()
    {
        Make(Path_, 1);
        Assert.Equal(1, Read(_pool).Value);

        using (var writer = SqliteConnection.Open(Path_, create: false, 1000))
        {
            _ = StateDbRows.Run(writer, "INSERT INTO t VALUES (2)", []);
        }

        Assert.Equal(2, Read(_pool).Value);
    }

    [Fact]
    public void AFileReplacedAtThePathIsReadNotTheOneTheKeptConnectionHolds()
    {
        Make(Path_, 1);
        var before = Read(_pool);

        var restored = Path_ + ".restored";
        Thread.Sleep(20);
        Make(restored, 7);
        File.Move(restored, Path_, overwrite: true);

        var after = Read(_pool);
        Assert.NotSame(before.Connection, after.Connection);
        Assert.Equal(7, after.Value);
    }

    [Fact]
    public void APooledReaderReusesTheKeptConnectionAndReadsAFileReplacedAtThePath()
    {
        Make(Path_, 1);
        long ReadPooled()
        {
            using var reader = StateDbReader.Open(_home, _pool).Reader!;
            Assert.Equal(0, _pool.IdleCount);
            return (long)reader.Query("SELECT MAX(v) FROM t")[0][0]!;
        }

        Assert.Equal(1, ReadPooled());
        Assert.Equal(1, _pool.IdleCount);
        Assert.Equal(1, ReadPooled());
        Assert.Equal(1, _pool.IdleCount);

        var restored = Path_ + ".restored";
        Thread.Sleep(20);
        Make(restored, 7);
        File.Move(restored, Path_, overwrite: true);

        Assert.Equal(7, ReadPooled());
    }

    [Fact]
    public void AReadThatFailsDoesNotKeepItsConnection()
    {
        Make(Path_, 1);

        _ = Assert.Throws<InvalidOperationException>(() => StateDbStoreReads.Reading<int>(_home, _ => throw new InvalidOperationException("no"), _pool));

        Assert.Equal(0, _pool.IdleCount);
    }

    [Fact]
    public void NoMoreThanAFewIdleConnectionsAreKept()
    {
        Make(Path_, 1);

        Parallel.For(0, 64, new ParallelOptions { MaxDegreeOfParallelism = 16 }, _ => Read(_pool));

        Assert.InRange(_pool.IdleCount, 1, 16 + 4);
    }
}
