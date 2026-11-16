using System.Globalization;
using Ah.Abstractions;

namespace Ah.StateDb.Tests;

/// <summary>Migration V26 (per-dir-files-to-db S7): <c>roster_records</c> gains <c>op_id</c>; a dir's <c>peers.jsonl</c> is taken once, before the rows stored since.</summary>
public sealed class RosterRecordsMigrationTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("ahrostermig").FullName;

    public void Dispose() => Directory.Delete(_home, recursive: true);

    private string DbPath => Path.Join(_home, "state", "ah.db");

    [Fact]
    public void AVersion23DatabaseGetsTheOpIdColumn()
    {
        using (StateDbModule.Open(_home, mayCreate: true))
        {
        }

        using (var raw = SqliteConnection.Open(DbPath, SqliteOpenMode.ReadWrite, 1000))
        {
            raw.Exec("DROP INDEX roster_records_op");
            raw.Exec("DROP INDEX roster_records_dir");
            raw.Exec("ALTER TABLE roster_records DROP COLUMN op_id");
            raw.Exec("UPDATE meta SET value = '23' WHERE key = 'schema_version'");
        }

        using (StateDbModule.Open(_home, mayCreate: true))
        {
        }

        using var after = SqliteConnection.Open(DbPath, SqliteOpenMode.ReadWrite, 1000);
        Assert.Equal(StateDbModule.SchemaVersion.ToString(CultureInfo.InvariantCulture), after.ScalarText("SELECT value FROM meta WHERE key = 'schema_version'"));
        after.Exec("INSERT INTO roster_records(pool_dir, raw, op_id) VALUES ('/h', '{}', 'o1')");
    }

    [Fact]
    public void TheFileIsTakenOnceInFileOrderBeforeLaterRowsSkippingBadAndRepeatedLines()
    {
        using (StateDbModule.Open(_home, mayCreate: true))
        {
        }

        var dir = Directory.CreateDirectory(Path.Join(_home, "h")).FullName;
        using (var raw = SqliteConnection.Open(DbPath, SqliteOpenMode.ReadWrite, 1000))
        {
            // the key is the dir's real path
            raw.Exec($"INSERT INTO roster_records(pool_dir, raw) VALUES ('{StateDbTeamKey.RealPath(dir)}', '{{\"n\":3}}'), ('/other', '{{\"n\":9}}')");
        }

        File.WriteAllText(Path.Join(dir, "peers.jsonl"), "{\"n\":1,\"op_id\":\"a\"}\nnot json\n{\"n\":1,\"op_id\":\"a\"}\n{\"n\":2}\n");
        _ = StateDbModule.ImportHierDirFiles(_home, dir);
        Assert.Equal(["{\"n\":1,\"op_id\":\"a\"}", "{\"n\":2}", "{\"n\":3}"], StateDbModule.CreateRosterRecords(_home).Bodies(dir));

        // another spelling of the same dir (a symlink to it) reads the same roster, as it read the same file
        var alias = Path.Join(_home, "alias");
        Directory.CreateSymbolicLink(alias, dir);
        Assert.Equal(3, StateDbModule.CreateRosterRecords(_home).Bodies(alias).Count);

        // the negative: a line added to the file after the take is not read, and a dir with no rows reads none, not another dir's
        File.AppendAllText(Path.Join(dir, "peers.jsonl"), "{\"n\":4}\n");
        Assert.Equal(3, StateDbModule.CreateRosterRecords(_home).Bodies(dir).Count);
        Assert.Empty(StateDbModule.CreateRosterRecords(_home).Bodies(Path.Join(_home, "none")));
    }
}
