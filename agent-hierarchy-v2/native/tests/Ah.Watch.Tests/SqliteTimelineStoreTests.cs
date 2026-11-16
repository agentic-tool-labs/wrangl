using Ah.Abstractions;
using Ah.StateDb;
using Ah.Watch.Model;

namespace Ah.Watch.Tests;

/// <summary>A migrated state database in a fresh temp home, for a timeline store to open.</summary>
internal static class TimelineDb
{
    internal static readonly ISqliteOpener Opener = StateDbModule.CreateOpener();

    /// <summary>The path of a new <c>ah.db</c>, created and migrated by the state database itself.</summary>
    internal static string NewPath()
    {
        var home = Directory.CreateTempSubdirectory("timeline-home-").FullName;
        Assert.Null(StateDbModule.EnsureDatabase(home));
        return Path.Combine(home, "state", "ah.db");
    }

    internal static SqliteTimelineStore Open(string path) => new(Opener, path, StateDbModule.BusyTimeoutMs);
}

/// <summary>The timeline store over the state database against the contract, over the real SQLite binding.</summary>
public sealed class SqliteTimelineStoreContract : TimelineStoreContract
{
    internal override ITimelineStore Create() => TimelineDb.Open(TimelineDb.NewPath());

    [Fact]
    public void TheTimelineIsATableOfTheStateDatabase()
    {
        var path = TimelineDb.NewPath();
        using (var store = TimelineDb.Open(path))
        {
            store.Append([new TimelineRow(0, "d", "a", null, "busy", 1)]);
        }

        using var connection = TimelineDb.Opener.Open(path, SqliteOpenMode.ReadOnly, 50);
        Assert.Equal("1", connection.ScalarText("SELECT COUNT(*) FROM timeline_events"));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "timeline*"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnAppendThatFindsTheDatabaseBusyIsRetriedAndNegativeAWriterThatNeverLetsGoStillFails(bool released)
    {
        var path = TimelineDb.NewPath();
        using var holder = TimelineDb.Opener.Open(path, SqliteOpenMode.ReadWrite, 50);
        holder.Exec("BEGIN IMMEDIATE");

        // The holder lets go the moment the store first finds the database busy, not after a wall-clock delay that a loaded machine overruns.
        var opener = new ReleasingOpener(TimelineDb.Opener, released ? () => holder.Exec("COMMIT") : null);
        using var store = new SqliteTimelineStore(opener, path, StateDbModule.BusyTimeoutMs);

        var failure = Record.Exception(() => store.Append([new TimelineRow(0, "d", "a", null, "busy", 1)]));

        Assert.True(opener.BusySeen > 0, "the first try found the database busy");
        if (released)
        {
            Assert.Null(failure);
            Assert.Equal(1, store.Count("d", 0, long.MaxValue));
        }
        else
        {
            Assert.True(failure is not null && TimelineDb.Opener.IsBusy(failure), "the last busy failure propagates, to be counted");
            holder.Exec("COMMIT");
        }
    }

    /// <summary>The real opener, running <c>onFirstBusy</c> once when the store first asks whether a failure is busy and it is.</summary>
    private sealed class ReleasingOpener(ISqliteOpener inner, Action? onFirstBusy) : ISqliteOpener
    {
        internal int BusySeen { get; private set; }

        public ISqliteConnection Open(string path, SqliteOpenMode flags, int busyTimeoutMs) => inner.Open(path, flags, busyTimeoutMs);

        public bool IsDamage(Exception failure) => inner.IsDamage(failure);

        public bool IsBusy(Exception failure)
        {
            var busy = inner.IsBusy(failure);
            if (busy && ++BusySeen == 1)
            {
                onFirstBusy?.Invoke();
            }

            return busy;
        }
    }

    [Fact]
    public void AStoreBuiltBeforeTheDatabaseExistsWorksOnceItAppears()
    {
        var home = Directory.CreateTempSubdirectory("timeline-home-").FullName;
        var path = Path.Combine(home, "state", "ah.db");
        using var store = TimelineDb.Open(path);
        store.Append([new TimelineRow(0, "d", "a", null, "busy", 1)]);
        Assert.False(store.Available);
        Assert.False(File.Exists(path));

        Assert.Null(StateDbModule.EnsureDatabase(home));
        store.Append([new TimelineRow(0, "d", "a", null, "busy", 2)]);

        Assert.True(store.Available);
        Assert.Equal(1, store.Count("d", 0, long.MaxValue));
    }

    [Fact]
    public void NegativeAMissingOrDamagedDatabaseIsNeverCreatedMovedOrReset()
    {
        var dir = Directory.CreateTempSubdirectory("timeline-").FullName;
        var missing = Path.Combine(dir, "ah.db");
        using (var none = TimelineDb.Open(missing))
        {
            Assert.False(none.Available);
            Assert.Equal(0, none.Count("d", 0, long.MaxValue));
        }

        Assert.False(File.Exists(missing));

        var junk = Path.Combine(dir, "junk.db");
        byte[] bytes = [.. Enumerable.Range(0, 8192).Select(static i => (byte)((i * 31) + 7))];
        File.WriteAllBytes(junk, bytes);
        using (var damaged = TimelineDb.Open(junk))
        {
            damaged.Append([new TimelineRow(0, "d", "a", null, "busy", 1)]);
            Assert.Equal(0, damaged.Count("d", 0, long.MaxValue));
            Assert.Empty(damaged.Page("d", 0, long.MaxValue, 0, older: true, 10));
        }

        Assert.Equal(bytes, File.ReadAllBytes(junk));
        Assert.Equal(["junk.db"], Directory.GetFiles(dir).Select(Path.GetFileName));
    }
}

public sealed class SqliteHandleTests
{
    private static readonly ISqliteOpener Opener = StateDbModule.CreateOpener();

    private static string NewFile()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("sqlite-").FullName, "a.db");
        using var connection = Opener.Open(path, SqliteOpenMode.Create, 50);
        connection.Exec("CREATE TABLE t(a INTEGER)");
        connection.Exec("INSERT INTO t VALUES(1)");
        return path;
    }

    [Fact]
    public void AReadOnlyOpenReadsAndRejectsEveryWrite()
    {
        var path = NewFile();
        using var connection = Opener.Open(path, SqliteOpenMode.ReadOnly, 50);

        Assert.Equal("1", connection.ScalarText("SELECT a FROM t"));
        var failure = Record.Exception(() => connection.Exec("INSERT INTO t VALUES(2)"));
        Assert.NotNull(failure);
        Assert.False(Opener.IsDamage(failure));
        Assert.Equal("1", connection.ScalarText("SELECT COUNT(*) FROM t"));
    }

    [Fact]
    public void AReadOnlyOpenOfAMissingFileFailsAndDoesNotCreateIt()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("sqlite-").FullName, "none.db");

        Assert.NotNull(Record.Exception(() => Opener.Open(path, SqliteOpenMode.ReadOnly | SqliteOpenMode.Create, 50)));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void ARoAttachReadsTheOtherFileAndRefusesWritesToIt()
    {
        var other = NewFile();
        var main = Path.Combine(Directory.CreateTempSubdirectory("sqlite-").FullName, "main.db");
        using var connection = Opener.Open(main, SqliteOpenMode.Create | SqliteOpenMode.Uri, 50);
        connection.Exec("CREATE TABLE m(a INTEGER)");
        connection.Exec("ATTACH DATABASE 'file:" + other + "?mode=ro' AS arch");

        Assert.Equal("1", connection.ScalarText("SELECT COUNT(*) FROM arch.t"));
        Assert.NotNull(Record.Exception(() => connection.Exec("INSERT INTO arch.t VALUES(9)")));
        connection.Exec("INSERT INTO m VALUES(1)");
        Assert.Equal("1", connection.ScalarText("SELECT COUNT(*) FROM arch.t"));
    }

    [Fact]
    public void NegativeAPlainOpenIsReadWriteAndNotADatabaseIsDamage()
    {
        var path = NewFile();
        using (var connection = Opener.Open(path, SqliteOpenMode.ReadWrite, 50))
        {
            connection.Exec("INSERT INTO t VALUES(2)");
            Assert.Equal("2", connection.ScalarText("SELECT COUNT(*) FROM t"));
        }

        var junk = Path.Combine(Directory.CreateTempSubdirectory("sqlite-").FullName, "junk.db");
        File.WriteAllBytes(junk, [.. Enumerable.Range(0, 8192).Select(static i => (byte)((i * 31) + 7))]);
        using var bad = Opener.Open(junk, SqliteOpenMode.ReadWrite, 50);
        var failure = Record.Exception(() => bad.ScalarText("SELECT * FROM sqlite_master"));
        Assert.NotNull(failure);
        Assert.True(Opener.IsDamage(failure));
    }
}
