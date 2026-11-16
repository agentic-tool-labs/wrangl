using Ah.Abstractions;

namespace Ah.StateDb.Tests;

/// <summary>Per-dir-files-to-db S11e test 3: a hierarchy dir's grants documents are taken into the table once, byte for byte.</summary>
public sealed class GrantsTakeTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("ahgrantstake").FullName;

    public void Dispose() => Directory.Delete(_home, recursive: true);

    [Fact]
    public void ThePlantedDocumentIsTakenByteForByteWithItsOldAuditRowAndASecondReadTakesNothing()
    {
        using (StateDbModule.Open(_home, mayCreate: true))
        {
        }

        var hier = Directory.CreateDirectory(Path.Join(_home, "repo", ".claude", "ah2")).FullName;
        var grants = Directory.CreateDirectory(Path.Join(hier, "grants")).FullName;
        const string Row = "{\"at\":\"2026-11-01T00:00:00Z\",\"team\":\"t1\",\"action\":\"allow\",\"doc\":true}";
        var body = "{ \"version\": 1, \"team\": \"t1\", \"mode\": \"enforce\",\n  \"audit_hash\": \"x\" }\n";
        File.WriteAllText(Path.Join(grants, "t1.json"), body);
        // the negatives: a later refused row of the same team, and a later document row of another team, are not t1's latest document row
        var audit = Path.Join(_home, "audit.jsonl");
        File.WriteAllText(audit, Row + "\n{\"team\":\"t1\",\"result\":\"refused\",\"doc\":false}\n{\"team\":\"t2\",\"doc\":true}\n");

        // the negative: a read alone takes nothing (files-off-disk §4)
        Assert.True(StateDbModule.CreateGrants(_home, audit).Read(hier)!.TryDocuments(out var untaken));
        Assert.Empty(untaken);
        _ = StateDbModule.ImportHierDirFiles(_home, hier, force: true);
        Assert.True(StateDbModule.CreateGrants(_home, audit).Read(hier)!.TryDocuments(out var first));
        var doc = Assert.Single(first);
        Assert.Equal(("t1", body, Row), (doc.Team, doc.Body, doc.AuditRow));

        // taken: a changed file is not read again, by a new source as by the same one (dated before the take, so no skew)
        File.WriteAllText(Path.Join(grants, "t1.json"), "{}");
        File.WriteAllText(Path.Join(grants, "t3.json"), "{}");
        File.SetLastWriteTimeUtc(Path.Join(grants, "t1.json"), Old);
        File.SetLastWriteTimeUtc(Path.Join(grants, "t3.json"), Old);
        Assert.True(StateDbModule.CreateGrants(_home, audit).Read(hier)!.TryDocuments(out var second));
        Assert.Equal(body, Assert.Single(second).Body);
    }

    private static readonly DateTime Old = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private string Planted(params (string Team, byte[] Bytes)[] files)
    {
        using (StateDbModule.Open(_home, mayCreate: true))
        {
        }

        var hier = Directory.CreateDirectory(Path.Join(_home, "repo", ".claude", "ah2")).FullName;
        var grants = Directory.CreateDirectory(Path.Join(hier, "grants")).FullName;
        foreach (var (team, bytes) in files)
        {
            File.WriteAllBytes(Path.Join(grants, team + ".json"), bytes);
        }

        return hier;
    }

    [Fact]
    public void AFileThatIsNotValidUtf8IsStoredAsItsExactBytesWithTheirDigest()
    {
        byte[] invalid = [0x7B, 0xFF, 0xFE, 0x7D, 0x0A];
        var hier = Planted(("bad", invalid), ("good", "{\"mode\":\"off\"}\n"u8.ToArray()));
        _ = StateDbModule.ImportHierDirFiles(_home, hier, force: true);

        using var raw = SqliteConnection.Open(Path.Join(_home, "state", "ah.db"), SqliteOpenMode.ReadOnly, 1000);
        var rows = StateDbRows.Run(raw, "SELECT team, body, digest FROM worktree_grants ORDER BY team", []);
        Assert.Equal(invalid, Assert.IsType<byte[]>(rows[0][1]));
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(invalid)), rows[0][2]);
        // the negative: valid UTF-8 stays text
        Assert.Equal("{\"mode\":\"off\"}\n", Assert.IsType<string>(rows[1][1]));
    }

    [Fact]
    public void AGrantsFileWrittenAfterTheTakeMakesTheGrantsUnreadable()
    {
        var hier = Planted(("t1", "{\"mode\":\"enforce\"}"u8.ToArray()));
        var file = Path.Join(hier, "grants", "t1.json");
        var source = StateDbModule.CreateGrants(_home, Path.Join(_home, "none.jsonl"));
        _ = StateDbModule.ImportHierDirFiles(_home, hier, force: true);
        Assert.True(source.Read(hier)!.TryDocuments(out _));

        // the negative: a touch after the take (a checkout, a restore) with the row's bytes reads the row as normal
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(1));
        Assert.True(source.Read(hier)!.TryDocuments(out var touched));
        Assert.Equal("{\"mode\":\"enforce\"}", Assert.Single(touched).Body);

        // an older binary rewrites the file (a revoke) after the take
        File.WriteAllText(file, "{\"mode\":\"off\"}");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(1));
        Assert.False(source.Read(hier)!.TryDocuments(out var none));
        Assert.Empty(none);

        // the negative: a file dated before the take is ignored and the row decides
        File.SetLastWriteTimeUtc(file, Old);
        Assert.True(source.Read(hier)!.TryDocuments(out var docs));
        Assert.Equal("{\"mode\":\"enforce\"}", Assert.Single(docs).Body);
    }

    [Fact]
    public void TheTakeIsStampedBeforeTheFilesAreReadSoAFileWrittenDuringItIsTaken()
    {
        var hier = Planted(("t1", "{}"u8.ToArray()));
        // an older binary writes t2 at the moment the take stamps its time: read after the stamp, so taken, never newer-yet-unseen
        var source = new StateDbGrants(_home, Path.Join(_home, "none.jsonl"), () =>
        {
            File.WriteAllText(Path.Join(hier, "grants", "t2.json"), "{\"late\":true}");
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        });

        Assert.True(source.Take(hier));
        Assert.True(source.Read(hier)!.TryDocuments(out var docs));
        Assert.Equal(["t1", "t2"], docs.Select(static d => d.Team));
    }
}
