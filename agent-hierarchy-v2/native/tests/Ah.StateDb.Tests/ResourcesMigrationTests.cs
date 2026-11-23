using Ah.Abstractions;

namespace Ah.StateDb.Tests;

/// <summary>Migration V46 (ah2-509): the <c>resources</c> table, whose partial unique index lets one hold exist per name.</summary>
public sealed class ResourcesMigrationTests : IDisposable
{
    // the grant statement of resources.claim (ResourceStore.cs)
    private const string Grant = "INSERT OR IGNORE INTO resources(name, kind, pid, pid_start, who, session, note, since_ms) VALUES ('x', 'hold', ?1, NULL, 'w', NULL, NULL, 1)";

    private readonly string _home = Directory.CreateTempSubdirectory("ahresmig").FullName;

    public void Dispose() => Directory.Delete(_home, recursive: true);

    [Fact]
    public void TwoGrantsForOneFreeNameFromTwoConnectionsMakeOneHoldAndWaitersStillStack()
    {
        using (StateDbModule.Open(_home, mayCreate: true))
        {
        }

        var path = Path.Join(_home, "state", "ah.db");
        using var first = SqliteConnection.Open(path, SqliteOpenMode.ReadWrite, 1000);
        using var second = SqliteConnection.Open(path, SqliteOpenMode.ReadWrite, 1000);
        first.Exec("BEGIN IMMEDIATE");
        first.Exec(Grant.Replace("?1", "101", StringComparison.Ordinal));
        second.Exec("BEGIN");
        first.Exec("COMMIT");
        second.Exec(Grant.Replace("?1", "202", StringComparison.Ordinal));
        Assert.Equal("0", second.ScalarText("SELECT changes()"));
        second.Exec("COMMIT");

        Assert.Equal("101", first.ScalarText("SELECT group_concat(pid) FROM resources WHERE kind = 'hold'"));

        // nearest input that must not match the index: waiters on the same name
        first.Exec("INSERT INTO resources(name, kind, pid, who, since_ms) VALUES ('x', 'wait', 303, 'w', 2), ('x', 'wait', 404, 'w', 3)");
        Assert.Equal("3", first.ScalarText("SELECT count(*) FROM resources WHERE name = 'x'"));
    }
}
