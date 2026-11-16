namespace Ah.StateDb.Tests;

/// <summary>Migration V31 (per-dir-files-to-db S11c): a team's old <c>ledger.jsonl</c> and plan logs are taken into <c>progress_logs</c> once, on the first read.</summary>
public sealed class ProgressLogsMigrationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ahproglogs").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void TheImportPassTakesTheOldFilesOnceSkippingBadLinesAndCarriesTheByteCursorOver()
    {
        var home = Path.Join(_root, "home");
        var hier = Path.Join(_root, "h");
        using (StateDbModule.Open(home, mayCreate: true))
        {
        }

        var folder = Path.Join(hier, "progress", "t");
        Directory.CreateDirectory(folder);
        const string First = "{\"kind\":\"decision\",\"n\":1}\n";
        File.WriteAllText(Path.Join(folder, "ledger.jsonl"), First + "not json\n{\"kind\":\"decision\",\"n\":2}\n");
        File.WriteAllText(Path.Join(folder, "ledger.cursor"), First.Length + "\n");
        // a whole last object with no newline is taken; a torn fragment is not
        File.WriteAllText(Path.Join(folder, "main.plan.jsonl"), "{\"item\":\"a\"}\n{\"item\":\"b\"}");
        File.WriteAllText(Path.Join(folder, "torn.plan.jsonl"), "{\"item\":\"a\"}\n{\"item\":\"b");
        var logs = StateDbModule.CreateProgressLogs(home);

        // the negative: a read alone takes nothing (files-off-disk §4)
        Assert.Empty(logs.Logs(hier, "t"));
        _ = StateDbModule.ImportHierDirFiles(home, hier, force: true);

        Assert.Equal(["ledger.jsonl", "main.plan.jsonl", "torn.plan.jsonl"], logs.Logs(hier, "t"));
        Assert.Equal(2, logs.Rows(hier, "t", "main.plan.jsonl").Count);
        Assert.Single(logs.Rows(hier, "t", "torn.plan.jsonl"));
        Assert.Equal((1L, 1), logs.LedgerAfterCursor(hier, "t") is { } since ? (since.Cursor, since.Rows.Count) : (-1L, -1));
        Assert.Equal(["{\"kind\":\"decision\",\"n\":1}", "{\"kind\":\"decision\",\"n\":2}"], logs.Rows(hier, "t", "ledger.jsonl").Select(static r => r.Body));
        Assert.Equal(1, logs.LedgerCursor(hier, "t"));

        // the negative: a line added to the file after the take is never read
        File.AppendAllText(Path.Join(folder, "ledger.jsonl"), "{\"kind\":\"decision\",\"n\":3}\n");
        Assert.Equal(2, logs.Rows(hier, "t", "ledger.jsonl").Count);
        Assert.Empty(logs.Rows(hier, "other", "ledger.jsonl"));
    }
}
