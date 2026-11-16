using System.Globalization;
using Ah.Abstractions;

namespace Ah.StateDb.Tests;

/// <summary>Migration V22 (per-dir-files-to-db S2): a version 21 database gets <c>activity</c>, and only its <c>activity</c> status entries are dropped to be rebuilt.</summary>
public sealed class ActivityMigrationTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("ahactmig").FullName;

    public void Dispose() => Directory.Delete(_home, recursive: true);

    private string DbPath => Path.Join(_home, "state", "ah.db");

    [Fact]
    public void AVersion21DatabaseGetsTheTableAndLosesOnlyItsActivityStatusEntries()
    {
        using (StateDbModule.Open(_home, mayCreate: true))
        {
        }

        using (var raw = SqliteConnection.Open(DbPath, SqliteOpenMode.ReadWrite, 1000))
        {
            raw.Exec("DROP TABLE activity");
            raw.Exec("INSERT INTO status_entries(pool_dir, kind, key, json, changed_ms) VALUES ('/h', 'activity', 's1.json', '{}', 1)");
            raw.Exec("INSERT INTO status_entries(pool_dir, kind, key, json, changed_ms) VALUES ('/h', 'roster', 'r1', '{}', 1)");
            raw.Exec("UPDATE meta SET value = '21' WHERE key = 'schema_version'");
        }

        using (StateDbModule.Open(_home, mayCreate: true))
        {
        }

        using var reader = StateDbModule.OpenReader(_home).Reader!;
        Assert.Equal(StateDbModule.SchemaVersion.ToString(CultureInfo.InvariantCulture), (string)reader.Query("SELECT value FROM meta WHERE key = 'schema_version'")[0][0]!);
        Assert.Equal(0L, (long)reader.Query("SELECT COUNT(*) FROM activity")[0][0]!);
        Assert.Empty(reader.Query("SELECT 1 FROM status_entries WHERE kind = 'activity'"));

        // the negative: another kind's entries stay
        Assert.Single(reader.Query("SELECT 1 FROM status_entries WHERE kind = 'roster'"));
    }
}
