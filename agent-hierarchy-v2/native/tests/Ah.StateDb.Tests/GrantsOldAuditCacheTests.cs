namespace Ah.StateDb.Tests;

/// <summary>b459: the old worktree audit log is loaded once per (path, length, mtime), not on every grants read.</summary>
public sealed class GrantsOldAuditCacheTests : IDisposable
{
    private const string Row1 = "{\"team\":\"t1\",\"action\":\"allow\",\"doc\":true}";
    private const string Row2 = "{\"team\":\"t1\",\"action\":\"block\",\"doc\":true}";
    private readonly string _home = Directory.CreateTempSubdirectory("ahgrantsoldaudit").FullName;

    public void Dispose() => Directory.Delete(_home, recursive: true);

    /// <summary>A taken t1 document with no table audit row, so every read falls back to the old log.</summary>
    private string Taken()
    {
        using (StateDbModule.Open(_home, mayCreate: true))
        {
        }

        var hier = Directory.CreateDirectory(Path.Join(_home, "repo", ".claude", "ah2")).FullName;
        File.WriteAllText(Path.Join(Directory.CreateDirectory(Path.Join(hier, "grants")).FullName, "t1.json"), "{\"mode\":\"log\"}");
        _ = StateDbModule.ImportHierDirFiles(_home, hier, force: true);
        return hier;
    }

    private string? AuditRowOf(string hier, string audit)
    {
        Assert.True(StateDbModule.CreateGrants(_home, audit).Read(hier)!.TryDocuments(out var docs));
        return Assert.Single(docs).AuditRow;
    }

    [Fact]
    public void TwoReadsOfTheSameLogLoadItOnceAndReturnTheSameRow()
    {
        var hier = Taken();
        var audit = Path.Join(_home, "audit.jsonl");
        File.WriteAllText(audit, Row1 + "\n{\"team\":\"t1\",\"doc\":false}\nnot json\n");

        Assert.Equal(Row1, AuditRowOf(hier, audit));
        Assert.Equal(Row1, AuditRowOf(hier, audit));
        Assert.Equal(1, StateDbGrants.OldAuditLoads(audit));
    }

    [Fact]
    public void ALogRewrittenToANewLengthOrMtimeIsLoadedAgain()
    {
        var hier = Taken();
        var audit = Path.Join(_home, "audit.jsonl");
        File.WriteAllText(audit, Row1 + "\n");
        File.SetLastWriteTimeUtc(audit, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(Row1, AuditRowOf(hier, audit));

        // length changes
        File.WriteAllText(audit, Row1 + "\n" + Row2 + "\n");
        File.SetLastWriteTimeUtc(audit, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(Row2, AuditRowOf(hier, audit));

        // same length, mtime changes
        File.WriteAllText(audit, Row2 + "\n" + Row1 + "\n");
        File.SetLastWriteTimeUtc(audit, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(Row1, AuditRowOf(hier, audit));
        Assert.Equal(3, StateDbGrants.OldAuditLoads(audit));
    }

    [Fact]
    public void NoLogReadsAsNoAuditRow()
    {
        var hier = Taken();
        Assert.Null(AuditRowOf(hier, Path.Join(_home, "none.jsonl")));
    }
}
