using System.Globalization;
using Ah.Abstractions;

namespace Ah.StateDb.Tests;

/// <summary>The second DB-move ship (V33 asks, V35 holds, V36 ADRs, V37 progress documents, V38 grants): a version 31 database upgrades to 38 with every new table.</summary>
public sealed class DbMoveBatch2UpgradeTests : IDisposable
{
    private static readonly string[] NewTables = ["adr_history", "adr_links", "adrs", "asks", "holds", "progress_docs", "worktree_grant_audit", "worktree_grants"];

    private readonly string _home = Directory.CreateTempSubdirectory("ahbatch2mig").FullName;

    public void Dispose() => Directory.Delete(_home, recursive: true);

    private string DbPath => Path.Join(_home, "state", "ah.db");

    private static List<string> Tables(SqliteConnection c) =>
        NewTables.Where(t => c.ScalarText($"SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = '{t}'") == "1").ToList();

    [Fact]
    public void AVersion31DatabaseUpgradesTo38WithEveryNewTableAndAsksInTheDatabase()
    {
        using (StateDbModule.Open(_home, mayCreate: true))
        {
        }

        using (var raw = SqliteConnection.Open(DbPath, SqliteOpenMode.ReadWrite, 1000))
        {
            foreach (var table in NewTables)
            {
                raw.Exec($"DROP TABLE {table}");
            }

            raw.Exec("DELETE FROM meta WHERE key IN ('mode.asks', 'authority.asks')");
            raw.Exec("UPDATE meta SET value = '31' WHERE key = 'schema_version'");

            // the negative: the setup really is the old shape
            Assert.Empty(Tables(raw));
        }

        using (var open = StateDbModule.Open(_home, mayCreate: true))
        {
            Assert.Equal(StateDbAvailability.Ready, open.Status.Availability);
        }

        using var after = SqliteConnection.Open(DbPath, SqliteOpenMode.ReadOnly, 1000);
        Assert.Equal(StateDbModule.SchemaVersion.ToString(CultureInfo.InvariantCulture), after.ScalarText("SELECT value FROM meta WHERE key = 'schema_version'"));
        Assert.Equal(NewTables, Tables(after));
        Assert.Equal("db", after.ScalarText("SELECT value FROM meta WHERE key = 'mode.asks'"));
    }
}
