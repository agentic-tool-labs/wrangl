using System.Globalization;
using Ah.Abstractions;

namespace Ah.StateDb.Tests;

/// <summary>Migration V25 (per-dir-files-to-db S6): a version 23 database gets <c>qa_log</c>; the source reads one dir's rows in append order.</summary>
public sealed class QaLogMigrationTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("ahqalogmig").FullName;

    public void Dispose() => Directory.Delete(_home, recursive: true);

    private string DbPath => Path.Join(_home, "state", "ah.db");

    [Fact]
    public void AVersion23DatabaseGetsTheTableAndTheSourceReadsOneDirsRowsInSeqOrder()
    {
        using (StateDbModule.Open(_home, mayCreate: true))
        {
        }

        using (var raw = SqliteConnection.Open(DbPath, SqliteOpenMode.ReadWrite, 1000))
        {
            raw.Exec("DROP TABLE qa_log");
            raw.Exec("UPDATE meta SET value = '23' WHERE key = 'schema_version'");
        }

        using (StateDbModule.Open(_home, mayCreate: true))
        {
        }

        using (var raw = SqliteConnection.Open(DbPath, SqliteOpenMode.ReadWrite, 1000))
        {
            Assert.Equal(StateDbModule.SchemaVersion.ToString(CultureInfo.InvariantCulture), raw.ScalarText("SELECT value FROM meta WHERE key = 'schema_version'"));
            raw.Exec("INSERT INTO qa_log(hier_dir, seq, id, at, body) VALUES ('/h', 2, 'b', 2, '{\"n\":2}'), ('/h', 1, 'a', 1, '{\"n\":1}'), ('/other', 1, 'a', 1, '{\"n\":9}')");
        }

        Assert.Equal(["{\"n\":1}", "{\"n\":2}"], StateDbModule.CreateQaLog(_home).Bodies("/h"));

        // the negative: a dir with no rows reads none, not another dir's
        Assert.Empty(StateDbModule.CreateQaLog(_home).Bodies("/none"));
    }
}
