using Ah.Abstractions;

namespace Ah.StateDb.Tests;

/// <summary>A restore that fails part way leaves the user with what its message says: the old database, whole, in one place.</summary>
public sealed class StateDbRestoreFailureTests : IDisposable
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private readonly string _home = Directory.CreateTempSubdirectory("ahrestore").FullName;

    public void Dispose() => Directory.Delete(_home, recursive: true);

    private string Live => StateDbBackups.DatabasePath(_home);

    // A database with one backup, then a row the backup does not hold, so the two can be told apart.
    private void DatabaseNewerThanItsBackup()
    {
        using (var db = StateDbModule.Open(_home, mayCreate: true))
        {
            Assert.Equal(StateDbAvailability.Ready, db.Status.Availability);
        }

        _ = StateDbBackups.Take(_home, Noon);
        using var connection = SqliteConnection.Open(Live, SqliteOpenMode.ReadWrite, 2000);
        _ = StateDbRows.Run(connection, "INSERT INTO meta(key, value) VALUES ('probe', 'after-backup')", []);
    }

    private string? Probe()
    {
        using var connection = SqliteConnection.Open(Live, SqliteOpenMode.ReadOnly, 2000);
        var rows = StateDbRows.Run(connection, "SELECT value FROM meta WHERE key = 'probe'", []);
        return rows.Count == 0 ? null : (string?)rows[0][0];
    }

    [Fact]
    public void WhenTheCurrentDatabaseCannotBeMovedAsideNothingChangesAndTheMarkerIsAsBefore()
    {
        DatabaseNewerThanItsBackup();
        var marker = StateDbAuthority.Snapshot(_home);
        File.WriteAllText(StateDbBackups.ReplacedDirectory(_home), "a file where the directory goes");

        var result = StateDbBackups.Restore(_home, null, apply: true, Noon.AddMinutes(1), BackupSettings.Default);

        Assert.False(result.Ok);
        Assert.Contains("could not be moved aside", result.Message, StringComparison.Ordinal);
        Assert.Contains("nothing was changed", result.Message, StringComparison.Ordinal);
        Assert.Equal("after-backup", Probe());
        Assert.Equal(marker, StateDbAuthority.Snapshot(_home));
    }

    [Fact]
    public void WhenTheCopyIntoPlaceFailsTheSameDatabaseAndMarkerAreBackAndNothingIsLeftAside()
    {
        DatabaseNewerThanItsBackup();
        var marker = StateDbAuthority.Snapshot(_home);
        Directory.CreateDirectory(Live + ".restoring");

        var result = StateDbBackups.Restore(_home, null, apply: true, Noon.AddMinutes(1), BackupSettings.Default);

        Assert.False(result.Ok);
        Assert.Contains("were put back", result.Message, StringComparison.Ordinal);
        Assert.Equal("after-backup", Probe());
        Assert.Equal(marker, StateDbAuthority.Snapshot(_home));
        Assert.Empty(Directory.GetFiles(StateDbBackups.ReplacedDirectory(_home), "ah-*"));
    }

    [Fact]
    public void WhenAdoptFailsEveryTryTheBackupIsInPlaceAndTheMessageWarnsOfIt()
    {
        DatabaseNewerThanItsBackup();
        // The backup is a copy of the live database; a db_id of its own shows the marker names the backup, not the database it replaced.
        using (var backup = SqliteConnection.Open(StateDbBackups.List(_home).Single().Path, SqliteOpenMode.ReadWrite, 2000))
        {
            _ = StateDbRows.Run(backup, "UPDATE meta SET value = 'backup-id' WHERE key = 'db_id'", []);
        }

        Assert.NotEqual("backup-id", DbId());
        var tries = 0;

        var result = StateDbBackups.Restore(_home, null, apply: true, Noon.AddMinutes(1), BackupSettings.Default, _ =>
        {
            tries++;
            throw new IOException("marker locked");
        });

        Assert.True(result.Ok);
        Assert.True(result.Applied);
        Assert.Equal(3, tries);
        Assert.Contains(StateDbBackups.WarningMark + "the domains the restored database holds could not be written", result.Message, StringComparison.Ordinal);
        Assert.Null(Probe());
        Assert.Equal("backup-id", DbId());
        Assert.Equal("backup-id", StateDbAuthority.Read(_home).Marker?.DbId);
    }

    [Fact]
    public void AMoveAsideThatFailsPartWayPutsEveryFileBackBesideTheDatabase()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Live)!);
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            File.WriteAllText(Live + suffix, "file" + suffix);
        }

        var aside = Path.Join(_home, "state", "aside");
        var thrown = Assert.Throws<IOException>(() => StateDbBackups.MoveAside(_home, aside, Noon, (from, to) =>
        {
            if (from.EndsWith("-shm", StringComparison.Ordinal))
            {
                throw new IOException("disk full");
            }

            File.Move(from, to);
        }));

        Assert.Equal("disk full", thrown.Message);
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            Assert.Equal("file" + suffix, File.ReadAllText(Live + suffix));
        }

        Assert.Empty(Directory.GetFiles(aside));
    }

    private string? DbId()
    {
        using var connection = SqliteConnection.Open(Live, SqliteOpenMode.ReadOnly, 2000);
        var rows = StateDbRows.Run(connection, "SELECT value FROM meta WHERE key = 'db_id'", []);
        return rows.Count == 0 ? null : (string?)rows[0][0];
    }
}
