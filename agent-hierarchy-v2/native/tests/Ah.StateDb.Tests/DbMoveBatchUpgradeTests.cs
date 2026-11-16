using System.Globalization;
using Ah.Abstractions;

namespace Ah.StateDb.Tests;

/// <summary>The DB-move schema batch (per-dir-files-to-db S7-S11, V26-V31): a version 25 database upgrades to the current version with every new table.</summary>
public sealed class DbMoveBatchUpgradeTests : IDisposable
{
    private static readonly string[] NewTables = ["checkpoints", "decisions", "jobs", "links", "progress_logs"];

    private readonly string _home = Directory.CreateTempSubdirectory("ahbatchmig").FullName;

    public void Dispose() => Directory.Delete(_home, recursive: true);

    private string DbPath => Path.Join(_home, "state", "ah.db");

    /// <summary>A database as version 25 left it: none of the batch's tables, no <c>roster_records.op_id</c> or its indexes.</summary>
    private void AtVersion25()
    {
        using (StateDbModule.Open(_home, mayCreate: true))
        {
        }

        using var raw = SqliteConnection.Open(DbPath, SqliteOpenMode.ReadWrite, 1000);
        foreach (var table in NewTables)
        {
            raw.Exec($"DROP TABLE {table}");
        }

        raw.Exec("DROP INDEX roster_records_op");
        raw.Exec("DROP INDEX roster_records_dir");
        raw.Exec("ALTER TABLE roster_records DROP COLUMN op_id");
        raw.Exec("UPDATE meta SET value = '25' WHERE key = 'schema_version'");
    }

    private static List<string> Tables(SqliteConnection c) =>
        NewTables.Where(t => c.ScalarText($"SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = '{t}'") == "1").ToList();

    [Fact]
    public void AVersion25DatabaseUpgradesTo31WithEveryNewTable()
    {
        AtVersion25();
        using (var before = SqliteConnection.Open(DbPath, SqliteOpenMode.ReadOnly, 1000))
        {
            // the negative: the setup really is the old shape
            Assert.Empty(Tables(before));
            Assert.Equal("0", before.ScalarText("SELECT COUNT(*) FROM pragma_table_info('roster_records') WHERE name = 'op_id'"));
        }

        using (var open = StateDbModule.Open(_home, mayCreate: true))
        {
            Assert.Equal(StateDbAvailability.Ready, open.Status.Availability);
        }

        using var after = SqliteConnection.Open(DbPath, SqliteOpenMode.ReadOnly, 1000);
        Assert.Equal(StateDbModule.SchemaVersion.ToString(CultureInfo.InvariantCulture), after.ScalarText("SELECT value FROM meta WHERE key = 'schema_version'"));
        Assert.Equal(NewTables, Tables(after));
        Assert.Equal("1", after.ScalarText("SELECT COUNT(*) FROM pragma_table_info('roster_records') WHERE name = 'op_id'"));
    }
}
