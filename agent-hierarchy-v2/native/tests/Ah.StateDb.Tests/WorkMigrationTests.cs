using System.Globalization;
using Ah.Abstractions;

namespace Ah.StateDb.Tests;

/// <summary>Migration V14 (ah-work-items §4): additive, so a version 13 database upgrades with every row intact and each new table empty.</summary>
public sealed class WorkMigrationTests : IDisposable
{
    private static readonly string[] WorkTables = ["work_repos", "work_prefixes", "work_items", "work_log", "work_links", "work_attachments"];

    private readonly string _home = Directory.CreateTempSubdirectory("ahworkmig").FullName;

    public void Dispose() => Directory.Delete(_home, recursive: true);

    private string DbPath => Path.Join(_home, "state", "ah.db");

    /// <summary>A database as version 13 left it: no work tables, the old version, one plan row and one plan event.</summary>
    private void AtVersion13()
    {
        using (StateDbModule.Open(_home, mayCreate: true))
        {
        }

        using var raw = SqliteConnection.Open(DbPath, SqliteOpenMode.ReadWrite, 1000);
        foreach (var table in WorkTables)
        {
            raw.Exec($"DROP TABLE {table}");
        }

        raw.Exec("INSERT INTO plan_events(hier_dir, stream, ts_ms, by, row) VALUES ('/r', 'backlog', 5, 'me', '{\"event\":\"add\",\"item\":\"b1\"}')");
        raw.Exec("INSERT INTO plan_items(hier_dir, stream, item, state, updated_ms, doc) VALUES ('/r', 'backlog', 'b1', 'not_started', 5, '{}')");
        raw.Exec("UPDATE meta SET value = '13' WHERE key = 'schema_version'");
    }

    /// <summary>A database as version 12 left it: also no <c>changed_seq</c>, its index or triggers, with one message row.</summary>
    private void AtVersion12()
    {
        AtVersion13();
        using var raw = SqliteConnection.Open(DbPath, SqliteOpenMode.ReadWrite, 1000);
        raw.Exec("DROP TRIGGER msg_messages_changed_insert");
        raw.Exec("DROP TRIGGER msg_messages_changed_update");
        raw.Exec("DROP INDEX msg_messages_changed");
        raw.Exec("ALTER TABLE msg_messages DROP COLUMN changed_seq");
        raw.Exec("INSERT INTO msg_messages(pool_dir, id, type, \"to\", slug, state) VALUES ('/r', '20261103-120000-aaaa', 'request', 'implementor', 's', 'sealed')");
        raw.Exec("UPDATE meta SET value = '12' WHERE key = 'schema_version'");
    }

    [Fact]
    public void AVersion12DatabaseUpgradesThroughBothStepsWithItsMessagesIntact()
    {
        AtVersion12();
        using (var before = SqliteConnection.Open(DbPath, SqliteOpenMode.ReadOnly, 1000))
        {
            // the negative: the setup really is the old shape
            Assert.Equal("0", before.ScalarText("SELECT COUNT(*) FROM pragma_table_info('msg_messages') WHERE name = 'changed_seq'"));
        }

        using (var open = StateDbModule.Open(_home, mayCreate: true))
        {
            Assert.Equal(StateDbAvailability.Ready, open.Status.Availability);
        }

        using var reader = StateDbModule.OpenReader(_home).Reader!;
        Assert.Equal(StateDbModule.SchemaVersion.ToString(CultureInfo.InvariantCulture), (string)reader.Query("SELECT value FROM meta WHERE key = 'schema_version'")[0][0]!);
        var row = reader.Query("SELECT id, changed_seq FROM msg_messages")[0];
        Assert.Equal("20261103-120000-aaaa", (string)row[0]!);
        Assert.Equal(0L, (long)row[1]!);
        Assert.Equal("b1", (string)reader.Query("SELECT item FROM plan_items")[0][0]!);
        foreach (var table in WorkTables)
        {
            Assert.Equal(0L, (long)reader.Query($"SELECT COUNT(*) FROM {table}")[0][0]!);
        }
    }

    [Fact]
    public void AVersion13DatabaseUpgradesWithItsDataIntactAndTheNewTablesEmpty()
    {
        AtVersion13();

        using (var open = StateDbModule.Open(_home, mayCreate: true))
        {
            Assert.Equal(StateDbAvailability.Ready, open.Status.Availability);
        }

        using var reader = StateDbModule.OpenReader(_home).Reader!;
        Assert.Equal(StateDbModule.SchemaVersion.ToString(CultureInfo.InvariantCulture), (string)reader.Query("SELECT value FROM meta WHERE key = 'schema_version'")[0][0]!);
        Assert.Equal("{\"event\":\"add\",\"item\":\"b1\"}", (string)reader.Query("SELECT row FROM plan_events")[0][0]!);
        Assert.Equal("b1", (string)reader.Query("SELECT item FROM plan_items")[0][0]!);
        foreach (var table in WorkTables)
        {
            Assert.Equal(0L, (long)reader.Query($"SELECT COUNT(*) FROM {table}")[0][0]!);
        }
    }

    [Fact]
    public void AVersion13DatabaseIsNotReadableAsTheNewSchemaBeforeItUpgrades()
    {
        // the negative: without the step the tables are simply not there
        AtVersion13();
        using var raw = SqliteConnection.Open(DbPath, SqliteOpenMode.ReadOnly, 1000);
        Assert.Equal("0", raw.ScalarText("SELECT COUNT(*) FROM sqlite_master WHERE name = 'work_items'"));
    }
}
