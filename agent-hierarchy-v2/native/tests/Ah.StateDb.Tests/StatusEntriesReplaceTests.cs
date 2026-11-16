using Ah.Abstractions;

namespace Ah.StateDb.Tests;

/// <summary>b459 step 21b: a scope rewrite writes only the rows whose json changed and deletes only the keys it no longer has.</summary>
public sealed class StatusEntriesReplaceTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("ahstatusreplace").FullName;

    public void Dispose() => Directory.Delete(_home, recursive: true);

    [Fact]
    public void AnUnchangedRowKeepsItsStampAndNegativeAChangedRowIsRestampedAndADroppedOneDeleted()
    {
        StateDbModule.Open(_home, mayCreate: true).Dispose();
        var store = StateDbModule.CreateStatusEntries(_home);
        (string, string)[] scope = [("/p", "gate")];
        StatusEntryRow Row(string key, string json) => new("/p", "gate", key, json);
        Dictionary<string, (long Ms, string? Json)> Read() =>
            store.ReadNewerThan("/p", long.MinValue)!.ToDictionary(static r => r.Key, static r => (r.ChangedMs, r.Json));

        Assert.True(store.Replace(scope, [], [Row("a", "{\"count\":1}"), Row("b", "{\"count\":2}"), Row("c", "{\"count\":3}")], 100).Ok);
        Assert.True(store.Replace(scope, [], [Row("a", "{\"count\":1}"), Row("b", "{\"count\":5}")], 200).Ok);

        var rows = Read();
        Assert.Equal(100, rows["a"].Ms);
        Assert.Equal((200L, "{\"count\":5}"), rows["b"]);
        Assert.False(rows.ContainsKey("c"));
    }
}
