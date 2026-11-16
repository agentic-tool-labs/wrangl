namespace Ah.StateDb.Tests;

public sealed class StoreIdentityTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("ahidentity").FullName;

    public void Dispose() => Directory.Delete(_home, recursive: true);

    private string Db => StateDbBackups.DatabasePath(_home);

    private void Create() => StateDbModule.Open(_home, mayCreate: true).Dispose();

    private void DeleteDatabase()
    {
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            File.Delete(Db + suffix);
        }
    }

    [Fact]
    public void ADatabaseGoneWithItsMarkerLeftIsStoreMissing()
    {
        Create();
        DeleteDatabase();

        var verdict = StateDbAuthority.Check(_home);

        Assert.Equal(StoreProblem.Missing, verdict.Problem);
        Assert.StartsWith("store-missing", verdict.Detail, StringComparison.Ordinal);
        Assert.Equal(verdict.Detail, StateDbAuthority.LostDatabase(_home));
    }

    [Fact]
    public void NegativeAHomeWithNoDatabaseAndNoMarkerIsNotMissing()
    {
        Assert.Equal(StoreProblem.None, StateDbAuthority.Check(_home).Problem);
        Assert.Null(StateDbAuthority.LostDatabase(_home));
    }

    [Fact]
    public void PeerRecordsOfADatabaseNeverCreatedAreEmpty()
    {
        Assert.Empty(StateDbModule.CreatePeerRecords(_home).ReadAll());
    }

    [Fact]
    public void NegativePeerRecordsOfALostDatabaseAreAnError()
    {
        Create();
        DeleteDatabase();

        Assert.Throws<InvalidOperationException>(() => StateDbModule.CreatePeerRecords(_home).ReadAll());
    }

    [Fact]
    public void AnOpenDoesNotRecreateADatabaseItsMarkerNames()
    {
        Create();
        DeleteDatabase();

        using var db = StateDbModule.Open(_home, mayCreate: true);

        Assert.Equal(StateDbAvailability.StoreMissing, db.Status.Availability);
        Assert.False(File.Exists(Db));
    }

    [Fact]
    public void AMarkerNamingAnotherDatabaseIsStoreSwappedAndTheOpenIsReadOnly()
    {
        Create();
        StateDbAuthority.Write(_home, new AuthorityMarker("another"));

        var verdict = StateDbAuthority.Check(_home);
        using var db = StateDbModule.Open(_home, mayCreate: true);

        Assert.Equal(StoreProblem.Swapped, verdict.Problem);
        Assert.StartsWith("store-swapped", verdict.Detail, StringComparison.Ordinal);
        Assert.Equal(StateDbAvailability.ReadOnly, db.Status.Availability);
    }

    [Fact]
    public void NegativeAMarkerNamingThisDatabaseIsNotSwapped()
    {
        Create();

        using var db = StateDbModule.Open(_home, mayCreate: true);

        Assert.Equal(StoreProblem.None, StateDbAuthority.Check(_home).Problem);
        Assert.Equal(StateDbAvailability.Ready, db.Status.Availability);
    }

    [Fact]
    public void TheMarkerHoldsOnlyTheIdentityAndOneFromAnEarlierBuildStillReads()
    {
        Create();
        var id = StateDbAuthority.Read(_home).Marker!.DbId;
        Assert.DoesNotContain("domains", File.ReadAllText(StateDbAuthority.MarkerPath(_home)), StringComparison.Ordinal);

        File.WriteAllText(StateDbAuthority.MarkerPath(_home), $"{{\"db_id\":\"{id}\",\"domains\":{{\"teams\":{{\"flipped_at\":\"x\",\"schema\":4,\"pending\":true}}}}}}");

        Assert.Equal(id, StateDbAuthority.Read(_home).Marker!.DbId);
        Assert.Equal(StoreProblem.None, StateDbAuthority.Check(_home).Problem);
    }

    [Fact]
    public void AFlipIsRecordedForItsDomainOnly()
    {
        Assert.False(StateDbAuthority.FlipRecorded(_home, "mail"));
        Create();
        Assert.False(StateDbAuthority.FlipRecorded(_home, "mail"));

        Assert.Null(StateDbModule.FlipMailToDatabase(_home));

        Assert.True(StateDbAuthority.FlipRecorded(_home, "mail"));
        Assert.False(StateDbAuthority.FlipRecorded(_home, "teams"));
    }

    [Fact]
    public void ADatabaseThatCannotBeReadCountsAsFlipped()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Db)!);
        File.WriteAllText(Db, "not a database");

        Assert.True(StateDbAuthority.FlipRecorded(_home, "teams"));
    }
}
