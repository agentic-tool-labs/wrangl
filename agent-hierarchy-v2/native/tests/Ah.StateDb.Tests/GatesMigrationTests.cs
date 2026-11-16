using System.Globalization;
using Ah.Abstractions;

namespace Ah.StateDb.Tests;

/// <summary>Migration V23 (per-dir-files-to-db S3): a version 22 database gets <c>gates</c>; the source reads one dir's rows in append order.</summary>
public sealed class GatesMigrationTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("ahgatesmig").FullName;

    public void Dispose() => Directory.Delete(_home, recursive: true);

    private string DbPath => Path.Join(_home, "state", "ah.db");

    [Fact]
    public void AFailedTakeIsHeldBackForAMinuteAndThenTriedAgain()
    {
        long now = 1_000;
        var backoff = new StateDbTakeBackoff(() => now);
        Assert.False(backoff.HoldsBack("/h/gates.jsonl"));

        backoff.Failed("/h/gates.jsonl");
        now += StateDbTakeBackoff.RetryAfterMs - 1;
        Assert.True(backoff.HoldsBack("/h/gates.jsonl"));
        // the negative: another file is not held back by this one's failure
        Assert.False(backoff.HoldsBack("/other/gates.jsonl"));

        now += 1;
        Assert.False(backoff.HoldsBack("/h/gates.jsonl"));
    }

    [Fact]
    public void AVersion22DatabaseGetsTheTableAndTheSourceReadsOneDirsRowsInSeqOrder()
    {
        using (StateDbModule.Open(_home, mayCreate: true))
        {
        }

        using (var raw = SqliteConnection.Open(DbPath, SqliteOpenMode.ReadWrite, 1000))
        {
            raw.Exec("DROP TABLE gates");
            raw.Exec("UPDATE meta SET value = '22' WHERE key = 'schema_version'");
        }

        using (StateDbModule.Open(_home, mayCreate: true))
        {
        }

        using (var raw = SqliteConnection.Open(DbPath, SqliteOpenMode.ReadWrite, 1000))
        {
            Assert.Equal(StateDbModule.SchemaVersion.ToString(CultureInfo.InvariantCulture), raw.ScalarText("SELECT value FROM meta WHERE key = 'schema_version'"));
            raw.Exec("INSERT INTO gates(hier_dir, seq, body, at) VALUES ('/h', 2, '{\"n\":2}', 't'), ('/h', 1, '{\"n\":1}', 't'), ('/other', 1, '{\"n\":9}', 't')");
        }

        Assert.Equal(["{\"n\":1}", "{\"n\":2}"], StateDbModule.CreateGates(_home).Bodies("/h"));

        // the negative: a dir with no rows reads none, not another dir's
        Assert.Empty(StateDbModule.CreateGates(_home).Bodies("/none"));
    }
}
