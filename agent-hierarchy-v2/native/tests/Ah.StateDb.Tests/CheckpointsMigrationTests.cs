namespace Ah.StateDb.Tests;

/// <summary>Migration V29 (per-dir-files-to-db S11a-ckpt): an H's old checkpoint files become rows keyed by their own paths, once.</summary>
public sealed class CheckpointsMigrationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ahckpts").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void AnUnreadableFileIsSkippedTheHIsStillTakenAndItsKeyReadsItOnceReadable()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var home = Path.Join(_root, "home");
        var hier = Path.Join(_root, "h");
        using (StateDbModule.Open(home, mayCreate: true))
        {
        }

        var folder = Path.Join(hier, "checkpoints", "t");
        Directory.CreateDirectory(folder);
        var locked = Path.Join(folder, "20261101T000000000Z.json");
        File.WriteAllText(locked, "{\"n\": 1}\n");
        File.WriteAllText(Path.Join(folder, "20261102T000000000Z.json"), "{\"n\": 2}\n");
        File.SetUnixFileMode(locked, UnixFileMode.None);
        var records = StateDbModule.CreateCheckpoints(home);
        _ = StateDbModule.ImportHierDirFiles(home, hier, force: true);

        Assert.Equal("20261102T000000000Z", records.Latest(hier, "t")?.Ts);

        // the negative: the skipped record is not a row, until its key is read once the file can be
        Assert.Null(records.Latest(hier, "t", "20261101T000000000Z"));
        File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Assert.Equal("{\"n\": 1}\n", records.ByKey(locked)?.Body);
    }

    [Fact]
    public void WithNoDatabaseYetAKeyReadsItsFileAndAMissingFileIsNoRecord()
    {
        var folder = Path.Join(_root, "h", "checkpoints", "t");
        Directory.CreateDirectory(folder);
        var old = Path.Join(folder, "20261101T000000000Z.json");
        File.WriteAllText(old, "{\"team\": \"t\"}\n");
        var records = StateDbModule.CreateCheckpoints(Path.Join(_root, "no-db-home"));

        Assert.Equal("{\"team\": \"t\"}\n", records.ByKey(old)?.Body);
        Assert.Null(records.ByKey(Path.Join(folder, "20261102T000000000Z.json")));
    }

    [Fact]
    public void TheImportPassTakesAnHsRecordsVerbatimKeyedByTheirPathsAndAReadAloneTakesNothing()
    {
        var home = Path.Join(_root, "home");
        var hier = Path.Join(_root, "h");
        using (StateDbModule.Open(home, mayCreate: true))
        {
        }

        var folder = Path.Join(hier, "checkpoints", "t");
        Directory.CreateDirectory(folder);
        var old = Path.Join(folder, "20261101T000000000Z.json");
        File.WriteAllText(old, "{\"team\": \"t\"}\n");
        var records = StateDbModule.CreateCheckpoints(home);
        Assert.Null(records.Latest(hier, "t"));
        _ = StateDbModule.ImportHierDirFiles(home, hier, force: true);

        var latest = records.Latest(hier, "t");
        Assert.Equal(("20261101T000000000Z", "{\"team\": \"t\"}\n"), (latest?.Ts, latest?.Body));
        Assert.Equal(latest?.Key, records.ByKey(old)?.Key);

        // the negatives: a key no record has is not found, and a file added to the H after its take is not read by the team's read
        Assert.Null(records.ByKey(Path.Join(folder, "20261102T000000000Z.json")));
        File.WriteAllText(Path.Join(folder, "20261103T000000000Z.json"), "{}\n");
        Assert.Equal("20261101T000000000Z", records.Latest(hier, "t")?.Ts);
    }
}
