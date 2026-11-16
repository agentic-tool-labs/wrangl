using System.Text;
using Ah.Abstractions;

namespace Ah.StateDb.Tests;

/// <summary>A one-shot msgs write creates the state database as a message verb does; another domain, or a database the marker says should exist, does not (ah-msgs-db-only §13.1).</summary>
public sealed class OpRunnerCreatesMsgsDatabaseTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("ahoprun").FullName;

    public void Dispose() => Directory.Delete(_home, recursive: true);

    private string Db => StateDbBackups.DatabasePath(_home);

    private DbOpOutcome Run(string domain) =>
        StateDbModule.CreateOpRunner(_home).Run(new DbOpRequest(domain, "op-1", "direct", _ => new OpResult(true, Encoding.UTF8.GetBytes("{\"ok\":true}"))));

    [Fact]
    public void AMsgsWriteOnANeverCreatedDatabaseCreatesItAndRuns()
    {
        var outcome = Run("msgs");

        Assert.True(outcome.Status == DbOpStatus.Done, outcome.Status + ": " + outcome.Error);
        Assert.True(File.Exists(Db));
    }

    private long BusyMsUnderAHeldLock(IDbOpRunner runner)
    {
        Assert.Equal(DbOpStatus.Done, Run("msgs").Status);
        using var holder = SqliteConnection.Open(Db, SqliteOpenMode.ReadWrite, 1000);
        holder.Exec("BEGIN IMMEDIATE");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var outcome = runner.Run(new DbOpRequest("msgs", "op-2", "direct", _ => new OpResult(true, Encoding.UTF8.GetBytes("{\"ok\":true}"))));
        clock.Stop();
        holder.Exec("ROLLBACK");
        Assert.NotEqual(DbOpStatus.Done, outcome.Status);
        return clock.ElapsedMilliseconds;
    }

    private long Seq()
    {
        using var connection = SqliteConnection.Open(Db, SqliteOpenMode.ReadOnly, 1000);
        return (long)StateDbRows.Run(connection, "SELECT seq FROM domain_seq WHERE domain = 'msgs'", [])[0][0]!;
    }

    [Fact]
    public void AWriteThatLeavesTheSeqDoesNotMoveItAndNegativeAnOrdinaryWriteDoes()
    {
        Assert.Equal(DbOpStatus.Done, Run("msgs").Status);
        var before = Seq();

        var quiet = StateDbModule.CreateOpRunner(_home).Run(new DbOpRequest("msgs", "op-q", "direct", _ => new OpResult(true, Encoding.UTF8.GetBytes("{\"ok\":true}")), LeavesSeq: true));
        Assert.Equal(DbOpStatus.Done, quiet.Status);
        Assert.Equal(before, Seq());

        Assert.Equal(DbOpStatus.Done, StateDbModule.CreateOpRunner(_home).Run(new DbOpRequest("msgs", "op-o", "direct", _ => new OpResult(true, Encoding.UTF8.GetBytes("{\"ok\":true}")))).Status);
        Assert.Equal(before + 1, Seq());
    }

    [Fact]
    public void AHookRunnerGivesUpAfterOneTryOf300Ms() => Assert.InRange(BusyMsUnderAHeldLock(StateDbModule.CreateOpRunner(_home, forHook: true)), 250, 595);

    [Fact]
    public void NegativeAMessageRunnerKeepsItsThreeTries() => Assert.True(BusyMsUnderAHeldLock(StateDbModule.CreateOpRunner(_home)) >= 595);

    private void LoseTheDatabase()
    {
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            File.Delete(Db + suffix);
        }
    }

    [Fact]
    public void ADatabaseLostAfterOneWriteErrorsOnReadAndRefusesTheNextWrite()
    {
        Assert.Equal(DbOpStatus.Done, Run("msgs").Status);
        LoseTheDatabase();

        var read = Assert.Throws<InvalidOperationException>(() => StateDbModule.CreateMsgSource(_home).Scope().Messages(_home));
        Assert.Contains("store-missing", read.Message, StringComparison.Ordinal);

        var write = Run("msgs");
        Assert.Equal(DbOpStatus.Refused, write.Status);
        Assert.Contains("store-missing", write.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(Db));
    }

    [Fact]
    public void NegativeADatabaseLostBeforeMsgsWasArmedIsStillAnErrorAndNotRecreated()
    {
        StateDbModule.Open(_home, mayCreate: true).Dispose(); // marker with a db_id and no domains
        LoseTheDatabase();

        var read = Assert.Throws<InvalidOperationException>(() => StateDbModule.CreateMsgSource(_home).Scope().Messages(_home));
        Assert.Contains("store-missing", read.Message, StringComparison.Ordinal);
        Assert.Equal(DbOpStatus.Refused, Run("msgs").Status);
        Assert.False(File.Exists(Db));
    }

    [Fact]
    public void AWriteOfAnotherDomainCreatesTheDatabaseToo()
    {
        var outcome = Run("mail");

        Assert.Equal(DbOpStatus.Done, outcome.Status);
        Assert.True(File.Exists(Db));
    }

    [Fact]
    public void NegativeAMsgsWriteDoesNotRecreateADatabaseTheMarkerNames()
    {
        StateDbModule.Open(_home, mayCreate: true).Dispose();
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            File.Delete(Db + suffix);
        }

        var outcome = Run("msgs");

        Assert.NotEqual(DbOpStatus.Done, outcome.Status);
        Assert.Contains("store-missing", outcome.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(Db));
    }
}
