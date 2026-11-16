using Ah.Core.Json;

namespace Ah.StateDb.Tests;

/// <summary>The <c>recover_records</c> table (schema 43): a read tells "no row" apart from "cannot read".</summary>
public sealed class RecoverRecordsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ahrecover").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void AMissingRowIsNullButAnUnreadableDatabaseThrows()
    {
        var home = Path.Join(_root, "home");
        using (StateDbModule.Open(home, mayCreate: true))
        {
        }

        var records = StateDbModule.CreateRecoverRecords(home);
        records.Write("k", "t", "m", false, "{\"state\": \"stopping\"}");

        Assert.Equal("{\"state\": \"stopping\"}", records.Read("k", "t", "m", false));
        Assert.Null(records.Read("k", "t", "other", false));

        File.WriteAllText(StateDbBackups.DatabasePath(home), "not a database");

        Assert.Throws<StoreUnavailableException>(() => records.Read("k", "t", "m", false));
    }
}
