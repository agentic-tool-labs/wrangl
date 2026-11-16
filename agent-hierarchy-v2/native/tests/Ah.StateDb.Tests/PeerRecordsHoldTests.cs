using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.StateDb.Tests;

/// <summary>b459 step 13: a type set's peer rows are held parsed and read again only as a delta, with a count guard for deletes.</summary>
public sealed class PeerRecordsHoldTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("ahpeerhold").FullName;

    public void Dispose() => Directory.Delete(_home, recursive: true);

    [Fact]
    public void AHeldTypeSetTakesANewRowAndDropsAPrunedOneOnTheNextRead()
    {
        using (StateDbModule.Open(_home, mayCreate: true))
        {
        }

        void Sql(string statement)
        {
            using var raw = SqliteConnection.Open(Path.Join(_home, "state", "ah.db"), SqliteOpenMode.ReadWrite, 1000);
            raw.Exec(statement);
        }

        void Add(string type, string id) => Sql($"INSERT INTO peer_records(type, raw) VALUES ('{type}', CAST('{{\"type\":\"{type}\",\"id\":\"{id}\"}}' AS BLOB))");
        var source = new StateDbPeerRecords(_home);
        var heard = new PeerQuery(Types: new HashSet<string>(StringComparer.Ordinal) { "heard" });
        string Ids() => string.Join(',', source.Rows(heard).Select(static r => r.Member("id")!.Value.AsString()));
        Add("heard", "a");
        Add("turn", "x");
        Add("heard", "b");
        Assert.Equal("a,b", Ids());

        Add("heard", "c");
        Assert.Equal("a,b,c", Ids());

        // the negative: a pruned row is not kept by the hold (the count guard reads the set again)
        Sql("DELETE FROM peer_records WHERE CAST(raw AS TEXT) LIKE '%\"b\"%'");
        Assert.Equal("a,c", Ids());
    }
}
