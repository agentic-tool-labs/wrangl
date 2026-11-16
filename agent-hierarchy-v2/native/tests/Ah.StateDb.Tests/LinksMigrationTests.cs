using System.Globalization;
using Ah.Abstractions;

namespace Ah.StateDb.Tests;

/// <summary>Migration V30 (per-dir-files-to-db S11b): an older database gets <c>links</c>; a team's old <c>links/&lt;team&gt;.json</c> is taken once, verbatim, and a corrupt one never.</summary>
public sealed class LinksMigrationTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("ahlinksmig").FullName;

    public void Dispose() => Directory.Delete(_home, recursive: true);

    private string DbPath => Path.Join(_home, "state", "ah.db");

    [Fact]
    public void AnOlderDatabaseGetsTheTableAndAPlantedFileIsTakenOnceVerbatimButACorruptOneNever()
    {
        using (StateDbModule.Open(_home, mayCreate: true))
        {
        }

        using (var raw = SqliteConnection.Open(DbPath, SqliteOpenMode.ReadWrite, 1000))
        {
            raw.Exec("DROP TABLE links");
            raw.Exec("UPDATE meta SET value = '23' WHERE key = 'schema_version'");
        }

        using (StateDbModule.Open(_home, mayCreate: true))
        {
        }

        using (var raw = SqliteConnection.Open(DbPath, SqliteOpenMode.ReadWrite, 1000))
        {
            Assert.Equal(StateDbModule.SchemaVersion.ToString(CultureInfo.InvariantCulture), raw.ScalarText("SELECT value FROM meta WHERE key = 'schema_version'"));
        }

        var dir = Path.Join(_home, "h");
        Directory.CreateDirectory(Path.Join(dir, "links"));
        const string Planted = "{\"v\":1,\"links\":[{\"id\":\"lk1\"}],\"denied\":[]}";
        File.WriteAllText(Path.Join(dir, "links", "fx.json"), Planted);
        File.WriteAllText(Path.Join(dir, "links", "bad.json"), "{not json");
        var links = StateDbModule.CreateLinks(_home);

        // the negative: a read alone takes nothing (files-off-disk §4)
        Assert.Null(links.Body(dir, "fx"));
        _ = StateDbModule.ImportHierDirFiles(_home, dir, force: true);
        Assert.Equal(Planted, links.Body(dir, "fx"));

        // taken once: an edit to the file after the take is not read
        File.WriteAllText(Path.Join(dir, "links", "fx.json"), "{\"v\":1,\"links\":[],\"denied\":[]}");
        Assert.Equal(Planted, links.Body(dir, "fx"));

        // the negative: a corrupt file is not imported, reads as no record, and stays on disk
        Assert.Null(links.Body(dir, "bad"));
        Assert.Equal("{not json", File.ReadAllText(Path.Join(dir, "links", "bad.json")));
    }

    [Fact]
    public void AWriteToAnUntakenDirReadsTheOldFileFirstSoItsEntriesAreKept()
    {
        using (StateDbModule.Open(_home, mayCreate: true))
        {
        }

        var dir = Path.Join(_home, "h");
        Directory.CreateDirectory(Path.Join(dir, "links"));
        const string Planted = "{\"v\":1,\"links\":[{\"id\":\"lk1\"}],\"denied\":[]}";
        File.WriteAllText(Path.Join(dir, "links", "x.json"), Planted);
        var links = StateDbModule.CreateLinks(_home);

        // a read-modify-write before the import pass: the write's own take merges the file (files-off-disk §4 gap, atp-1518)
        string? seen = null;
        var outcome = StateDbModule.CreateOpRunner(_home).Run(new DbOpRequest("links", Guid.NewGuid().ToString(), "direct", tx =>
        {
            seen = links.Body(tx, dir, "x");
            links.Put(tx, dir, "x", seen!.Replace("}]", "},{\"id\":\"lk2\"}]", StringComparison.Ordinal));
            return new OpResult(true, "null"u8.ToArray());
        }, DbOnly: true));
        Assert.Equal(DbOpStatus.Done, outcome.Status);

        Assert.Equal(Planted, seen);
        Assert.Equal("{\"v\":1,\"links\":[{\"id\":\"lk1\"},{\"id\":\"lk2\"}],\"denied\":[]}", links.Body(dir, "x"));
    }
}
