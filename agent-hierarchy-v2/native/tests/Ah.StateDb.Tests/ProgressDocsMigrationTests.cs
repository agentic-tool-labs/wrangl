using Ah.Abstractions;

namespace Ah.StateDb.Tests;

/// <summary>Migration V37 (per-dir-files-to-db S11d): a team's old progress documents are taken into <c>progress_docs</c> once, on the first read, byte for byte.</summary>
public sealed class ProgressDocsMigrationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ahprogdocs").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void TheImportPassTakesEveryDocumentVerbatimOnceAndLeavesTheLogsAndTheOffMarkerOut()
    {
        var home = Path.Join(_root, "home");
        var pool = Path.Join(_root, "h");
        using (StateDbModule.Open(home, mayCreate: true))
        {
        }

        var folder = Path.Join(pool, "progress", "t");
        Directory.CreateDirectory(folder);
        var planted = new Dictionary<string, string>
        {
            ["main.md"] = "﻿# main\r\nState: open\n",
            ["main.journal.md"] = "## entry\nno trailing newline",
            ["rollup.md"] = "# rollup\n",
            ["closing.md"] = "closing\n",
            [".nudged"] = "main r1\n",
            ["sessions.jsonl"] = "{\"event\":\"orchestrator-start\"}\n",
        };
        foreach (var (name, body) in planted)
        {
            File.WriteAllBytes(Path.Join(folder, name), System.Text.Encoding.UTF8.GetBytes(body));
        }

        // the negatives: neither the logs nor the .off switch nor a temp file is a document
        File.WriteAllText(Path.Join(folder, "ledger.jsonl"), "{}\n");
        File.WriteAllText(Path.Join(folder, "main.plan.jsonl"), "{}\n");
        File.WriteAllText(Path.Join(folder, "main.md.1.tmp"), "x");
        var docs = StateDbModule.CreateProgressDocs(home);

        // the negative: a read alone takes nothing (files-off-disk §4)
        Assert.Empty(docs.Docs(pool, "t"));
        Assert.Empty(docs.Teams(pool));
        _ = StateDbModule.ImportHierDirFiles(home, pool, force: true);

        var read = docs.Docs(pool, "t");
        Assert.Equal(planted.Keys.Order(StringComparer.Ordinal), read.Select(static d => d.Name));
        Assert.All(read, d => Assert.Equal(planted[d.Name], d.Body));
        Assert.Equal(["nudged", "closing", "journal", "checklist", "rollup", "sessions"], read.Select(static d => d.Kind));
        Assert.Equal(["t"], docs.Teams(pool));
        Assert.Equal("# rollup\n", docs.ByKey(Path.Join(folder, "rollup.md"))?.Body);

        // the negative: a file changed after the take is never read again; another team, or a key outside a progress folder, is nothing
        File.WriteAllText(Path.Join(folder, "rollup.md"), "changed\n");
        Assert.Equal("# rollup\n", docs.Doc(pool, "t", "rollup.md")?.Body);
        Assert.Empty(docs.Docs(pool, "other"));
        Assert.Null(docs.ByKey(Path.Join(pool, "elsewhere", "t", "rollup.md")));
    }

    [Fact]
    public void AFileThatIsNotValidUtf8IsKeptByteForByte()
    {
        var home = Path.Join(_root, "home");
        var pool = Path.Join(_root, "h");
        using (StateDbModule.Open(home, mayCreate: true))
        {
        }

        var folder = Path.Join(pool, "progress", "t");
        Directory.CreateDirectory(folder);
        byte[] bytes = [(byte)'#', 0xFF, 0xC3, (byte)'\n'];
        File.WriteAllBytes(Path.Join(folder, "main.md"), bytes);
        File.WriteAllText(Path.Join(folder, "rollup.md"), "ok\n");
        _ = StateDbModule.ImportHierDirFiles(home, pool, force: true);

        var docs = StateDbModule.CreateProgressDocs(home).Docs(pool, "t");

        Assert.Equal(bytes, docs.Single(d => d.Name == "main.md").Raw);
        // the negative: valid UTF-8 is stored as text, with no bytes kept beside it
        Assert.Null(docs.Single(d => d.Name == "rollup.md").Raw);
    }

    [Fact]
    public void ATakenTeamWhoseReadCannotAnswerIsBusyNeverItsStaleFiles()
    {
        var home = Path.Join(_root, "home");
        var pool = Path.Join(_root, "h");
        using (StateDbModule.Open(home, mayCreate: true))
        {
        }

        var folder = Path.Join(pool, "progress", "t");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Join(folder, "rollup.md"), "# rollup\n");
        var docs = StateDbModule.CreateProgressDocs(home);
        _ = StateDbModule.ImportHierDirFiles(home, pool, force: true);
        Assert.Equal("# rollup\n", docs.Doc(pool, "t", "rollup.md")?.Body);

        // a database this binary cannot read (here: a newer schema) after the take
        using (var writer = SqliteConnection.Open(StateDbBackups.DatabasePath(home), SqliteOpenMode.ReadWrite, 1000))
        {
            writer.Exec("UPDATE meta SET value = '999' WHERE key = 'schema_version'");
        }

        Assert.Throws<Ah.Core.Json.StoreUnavailableException>(() => docs.Docs(pool, "t"));
        Assert.Empty(docs.Teams(pool));
    }
}
