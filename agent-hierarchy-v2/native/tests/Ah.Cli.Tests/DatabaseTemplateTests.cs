using Ah.StateDb;

namespace Ah.Cli.Tests;

/// <summary>A copied database has to be what a new home would have made, or every test standing on it tests something else.</summary>
public sealed class DatabaseTemplateTests
{
    private static string[] Listing(string home) =>
        [.. Directory.EnumerateFileSystemEntries(home, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(home, p)).Order(StringComparer.Ordinal)];

    /// <summary>The schema, and each table's rows; a row's time differs between two homes, so <c>meta</c> gives only its keys.</summary>
    private static string[] Contents(string home)
    {
        using var reader = StateDbModule.OpenReader(home).Reader!;
        var schema = reader.Query("SELECT type, name, sql FROM sqlite_master ORDER BY type, name").Select(static r => string.Join('|', r)).ToList();
        foreach (var table in reader.Query("SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name").Select(static r => (string)r[0]!))
        {
            var rows = reader.Query(table == "meta" ? "SELECT key FROM meta ORDER BY key" : $"SELECT * FROM \"{table}\"");
            schema.Add(table + ": " + string.Join(';', rows.Select(static r => string.Join('|', r)).Order(StringComparer.Ordinal)));
        }

        return [.. schema];
    }

    [Fact]
    public void ACopyHasTheFilesTheSchemaAndTheRowsOfANewHome()
    {
        using var copied = new Sandbox();
        using var fresh = new Sandbox();
        var home = Path.Combine(fresh.Root, "made-here");
        Assert.Null(StateDbModule.EnsureDatabase(home));

        Assert.Equal(Listing(home), Listing(copied.AhHome));
        var contents = Contents(copied.AhHome);
        Assert.Equal(Contents(home), contents);
        Assert.Contains(contents, static line => line.StartsWith("table|work_log|", StringComparison.Ordinal));
        Assert.DoesNotContain(fresh.Root, string.Join('\n', Contents(home)), StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeAWriteInOneSandboxReachesNoOtherAndNotTheTemplate()
    {
        using var one = new Sandbox();
        Assert.True(StateDbModule.OpenDirect(one.AhHome).Write(static tx =>
        {
            tx.Execute("INSERT OR REPLACE INTO meta(key, value) VALUES ('only-here', 'x')");
            return 0;
        }).Ok);
        using var two = new Sandbox();

        Assert.Contains("only-here", string.Join('\n', Contents(one.AhHome)), StringComparison.Ordinal);
        Assert.DoesNotContain("only-here", string.Join('\n', Contents(two.AhHome)), StringComparison.Ordinal);
    }
}
