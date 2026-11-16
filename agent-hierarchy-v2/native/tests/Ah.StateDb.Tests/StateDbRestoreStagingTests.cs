using System.Runtime.Versioning;
using Ah.Abstractions;

namespace Ah.StateDb.Tests;

/// <summary>A restore is not blocked by what an earlier one left, and a failure on its way out says what state it left.</summary>
[UnsupportedOSPlatform("windows")]
public sealed class StateDbRestoreStagingTests : IDisposable
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private readonly string _home = Directory.CreateTempSubdirectory("ahrestore").FullName;

    public void Dispose() => Directory.Delete(_home, recursive: true);

    private string Live => StateDbBackups.DatabasePath(_home);

    private string Staging => Live + ".restoring";

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

    // Read-only, so a copy over it cannot open it for writing.
    private void StaleStagingFile()
    {
        File.WriteAllText(Staging, "left by an interrupted restore");
        File.SetUnixFileMode(Staging, UnixFileMode.UserRead);
    }

    [Fact]
    public void AStagingFileAnEarlierRestoreLeftIsRemovedAndTheBackupIsRestored()
    {
        DatabaseNewerThanItsBackup();
        StaleStagingFile();

        var result = StateDbBackups.Restore(_home, null, apply: true, Noon.AddMinutes(1), BackupSettings.Default);

        Assert.True(result.Ok, result.Message);
        Assert.True(result.Applied);
        Assert.Null(Probe());
        Assert.False(File.Exists(Staging));
    }

    [Fact]
    public void ABackupWhoseModeRowsSayFilesIsRefusedAndTheLiveDatabaseIsUnchanged()
    {
        using (var db = StateDbModule.Open(_home, mayCreate: true))
        {
            Assert.Equal(StateDbAvailability.Ready, db.Status.Availability);
        }

        using (var connection = SqliteConnection.Open(Live, SqliteOpenMode.ReadWrite, 2000))
        {
            _ = StateDbRows.Run(connection, "INSERT OR REPLACE INTO meta(key, value) VALUES ('mode.mail', 'files')", []);
        }

        var legacy = StateDbBackups.Take(_home, Noon, "pre-import");
        using (var connection = SqliteConnection.Open(Live, SqliteOpenMode.ReadWrite, 2000))
        {
            _ = StateDbRows.Run(connection, "UPDATE meta SET value = 'db' WHERE key = 'mode.mail'", []);
            _ = StateDbRows.Run(connection, "INSERT INTO meta(key, value) VALUES ('probe', 'after-backup')", []);
        }

        var result = StateDbBackups.Restore(_home, legacy.Name, apply: true, Noon.AddMinutes(1), BackupSettings.Default);

        Assert.False(result.Ok);
        Assert.False(result.Applied);
        Assert.Contains("predates the database store", result.Message, StringComparison.Ordinal);
        Assert.Equal("after-backup", Probe());
        Assert.True(StateDbBackups.PredatesStore(legacy.Path));
        Assert.False(StateDbBackups.PredatesStore(Live)); // negative: a db-mode database is not flagged
    }

    [Fact]
    public void ADryRunLeavesAStaleStagingFileWhereItIs()
    {
        DatabaseNewerThanItsBackup();
        StaleStagingFile();

        var result = StateDbBackups.Restore(_home, null, apply: false, Noon.AddMinutes(1), BackupSettings.Default);

        Assert.True(result.Ok, result.Message);
        Assert.False(result.Applied);
        Assert.Equal("left by an interrupted restore", File.ReadAllText(Staging));
        Assert.Equal("after-backup", Probe());
    }

    [Fact]
    public void WhenTheMoveAsideAndThenTheMarkerPutBackBothFailTheMessageSaysTheStoreReadsAsSwapped()
    {
        DatabaseNewerThanItsBackup();
        File.WriteAllText(StateDbBackups.ReplacedDirectory(_home), "a file where the directory goes");

        var result = StateDbBackups.Restore(_home, null, apply: true, Noon.AddMinutes(1), BackupSettings.Default, reinstate: (_, _, _) => throw new IOException("marker locked"));

        Assert.False(result.Ok);
        Assert.Contains("could not be moved aside", result.Message, StringComparison.Ordinal);
        Assert.Contains("the authority marker could not be put back (marker locked)", result.Message, StringComparison.Ordinal);
        Assert.Contains("the store reads as swapped until `ah db restore` is run", result.Message, StringComparison.Ordinal);
        Assert.Equal("after-backup", Probe());
    }

    [Fact]
    public void WhenOnlyTheMoveAsideFailsTheMarkerIsPutBackAndNothingChanged()
    {
        DatabaseNewerThanItsBackup();
        var marker = StateDbAuthority.Snapshot(_home);
        File.WriteAllText(StateDbBackups.ReplacedDirectory(_home), "a file where the directory goes");
        var puts = 0;

        var result = StateDbBackups.Restore(_home, null, apply: true, Noon.AddMinutes(1), BackupSettings.Default, reinstate: (home, bytes, expected) =>
        {
            puts++;
            StateDbAuthority.Reinstate(home, bytes, expected);
        });

        Assert.False(result.Ok);
        Assert.Equal(1, puts);
        Assert.Contains("nothing was changed", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("reads as swapped", result.Message, StringComparison.Ordinal);
        Assert.Equal(marker, StateDbAuthority.Snapshot(_home));
    }
}
