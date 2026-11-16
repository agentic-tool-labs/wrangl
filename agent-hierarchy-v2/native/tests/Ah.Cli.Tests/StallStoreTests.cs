using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Core.Json;
using Ah.Platform;
using Ah.TestKit;

namespace Ah.Cli.Tests;

/// <summary>The stall, overdue and wake state lives in the state database: <c>meta</c> keys and the <c>stall_journal</c> rows.</summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class StallStoreTests : IDisposable
{
    private readonly Sandbox _sandbox = new();

    public void Dispose() => _sandbox.Dispose();

    private ServiceGraph Graph()
    {
        var variables = new Dictionary<string, string?> { ["AH_TEST"] = "1", ["AH_HOME"] = _sandbox.AhHome, ["HOME"] = _sandbox.Home };
        var env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables, new PluginInfo(_sandbox.PluginRoot, "wrangl", "1.0.0", "1"));
        return ServiceGraph.Build(PlatformModule.CreateIo(null), env, new FakeRunner(), new FakeDaemonClient());
    }

    [Fact]
    public void OnlyAStallOverdueOrWakeKeyWithANameIsWrittenAndTicksMergePerJob()
    {
        var g = Graph();
        StallStore.Put(g, StallStore.Overdue("s1"), "{}");
        Assert.Equal("{}", StallStore.Read(_sandbox.AhHome, "overdue.s1"));
        StallStore.Put(g, StallStore.Overdue("s1"), null);
        Assert.Null(StallStore.Read(_sandbox.AhHome, "overdue.s1"));

        // the nearest keys that must not match: a bare prefix, and another domain's key
        _ = Assert.Throws<IOException>(() => StallStore.Put(g, "stall.", "x"));
        _ = Assert.Throws<IOException>(() => StallStore.Put(g, "schema_version", "1"));

        StallStore.Merge(g, StallStore.Ticks, "a", new JsonObject { ["n"] = 1 });
        StallStore.Merge(g, StallStore.Ticks, "b", new JsonObject { ["n"] = 2 });
        var ticks = JsonText.Parse(StallStore.Read(_sandbox.AhHome, StallStore.Ticks)!);
        Assert.Equal(1, ticks.Member("a")!.Value.Member("n")!.Value.AsNumber());
        Assert.Equal(2, ticks.Member("b")!.Value.Member("n")!.Value.AsNumber());
    }

    [Theory]
    [InlineData(StallStore.JournalKeep, true)]
    [InlineData(StallStore.JournalKeep - 1, false)]
    public void AnAppendPrunesTheJournalToItsKeepCountAndNoSooner(int planted, bool pruned)
    {
        Assert.True(Ah.StateDb.StateDbModule.OpenDirect(_sandbox.AhHome).Write(tx =>
        {
            tx.Execute("WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < ?1) INSERT INTO stall_journal(seq, at_ms, row) SELECT i, 0, '{\"i\":' || i || '}' FROM n", (long)planted);
            return true;
        }).Ok);

        StallStore.Journal(Graph(), new JsonObject { ["kind"] = "last" });

        var rows = StallStore.JournalRows(_sandbox.AhHome);
        Assert.Equal(pruned ? StallStore.JournalKeep : planted + 1, rows.Count);
        Assert.Equal(pruned ? "{\"i\":2}" : "{\"i\":1}", rows[0]);
        Assert.Contains("last", rows[^1], StringComparison.Ordinal);
    }
}
