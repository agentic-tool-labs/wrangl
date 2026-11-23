using System.Globalization;
using Ah.Abstractions;

namespace Ah.StateDb.Tests;

/// <summary>Migration V45 (ah2-54): a version-44 database gains the <c>lessons</c> table.</summary>
public sealed class LessonsMigrationTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("ahlessonsmig").FullName;

    public void Dispose() => Directory.Delete(_home, recursive: true);

    private string DbPath => Path.Join(_home, "state", "ah.db");

    [Fact]
    public void AVersion44DatabaseGetsTheLessonsTable()
    {
        using (StateDbModule.Open(_home, mayCreate: true))
        {
        }

        using (var raw = SqliteConnection.Open(DbPath, SqliteOpenMode.ReadWrite, 1000))
        {
            raw.Exec("DROP TABLE lessons");
            raw.Exec("UPDATE meta SET value = '44' WHERE key = 'schema_version'");
        }

        using (StateDbModule.Open(_home, mayCreate: true))
        {
        }

        using var after = SqliteConnection.Open(DbPath, SqliteOpenMode.ReadWrite, 1000);
        Assert.Equal("47", StateDbModule.SchemaVersion.ToString(CultureInfo.InvariantCulture));
        Assert.Equal("47", after.ScalarText("SELECT value FROM meta WHERE key = 'schema_version'"));
        after.Exec("INSERT INTO lessons(hier_dir, scope, lesson, applies, pointer, \"by\", created_ms, updated_ms) VALUES ('/h', 'team', 'l', 'w', 'p', 'user', 1, 1)");
        Assert.Equal("open", after.ScalarText("SELECT state FROM lessons"));
    }
}
