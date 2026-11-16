namespace Ah.StateDb.Tests;

/// <summary>A state database that was never created holds no messages yet; one that should exist and does not, or cannot be read, is an error (ah-msgs-db-only §13.5).</summary>
public sealed class MsgReaderAbsentDatabaseTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("ahabsent").FullName;

    public void Dispose() => Directory.Delete(_home, recursive: true);

    private string Pool => Path.Join(_home, "repo", ".claude", "ah2");

    [Fact]
    public void ANeverCreatedDatabaseReadsAsNoMessagesAndCreatesNothing()
    {
        var scope = StateDbModule.CreateMsgSource(_home).Scope();

        Assert.Empty(scope.Messages(Pool));
        Assert.Empty(scope.Exchanges([Pool]));
        Assert.Equal(0, scope.Version());
        Assert.False(File.Exists(StateDbBackups.DatabasePath(_home)));
    }

    [Fact]
    public void NegativeADatabaseGoneWithItsMarkerLeftIsStillAnError()
    {
        StateDbModule.Open(_home, mayCreate: true).Dispose();
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            File.Delete(StateDbBackups.DatabasePath(_home) + suffix);
        }

        var error = Assert.Throws<InvalidOperationException>(() => StateDbModule.CreateMsgSource(_home).Scope().Messages(Pool));
        Assert.Contains("store-missing", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeADatabaseFileThatExistsButIsNotADatabaseIsStillAnError()
    {
        _ = Directory.CreateDirectory(Path.GetDirectoryName(StateDbBackups.DatabasePath(_home))!);
        File.WriteAllText(StateDbBackups.DatabasePath(_home), "not a database");

        var error = Assert.Throws<InvalidOperationException>(() => StateDbModule.CreateMsgSource(_home).Scope().Messages(Pool));
        Assert.Contains("cannot be read", error.Message, StringComparison.Ordinal);
    }
}
