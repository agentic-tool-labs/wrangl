using System.Text;

namespace Ah.StateDb.Tests;

/// <summary>A draft stored before messages moved to the database holds no body: its file's bytes become revision 1 at daemon start (ah-msgs-db-only §10).</summary>
public sealed class MsgDraftImportTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("ah-import").FullName;

    public void Dispose() => Directory.Delete(_home, recursive: true);

    private string Seed(string id, string state, string? fileText, long? bodyRev = null)
    {
        using (var open = StateDbModule.Open(_home, mayCreate: true))
        {
            Assert.Equal(StateDbAvailability.Ready, open.Status.Availability);
        }

        var path = Path.Combine(_home, id + "--impl--s--request.md");
        if (fileText is not null)
        {
            File.WriteAllText(path, fileText);
            File.SetLastWriteTimeUtc(path, new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        }

        Assert.True(StateDbModule.OpenDirect(_home).Write(tx =>
        {
            tx.Execute("INSERT INTO msg_messages(pool_dir, id, type, \"to\", slug, state, draft_path, body_rev) VALUES ('/p', ?1, 'request', 'impl', 's', ?2, ?3, ?4)", id, state, path, bodyRev);
            return 0;
        }).Ok);
        return path;
    }

    private IReadOnlyList<object?[]> Q(string sql, params object?[] args)
    {
        using var reader = StateDbModule.OpenReader(_home).Reader!;
        return reader.Query(sql, args);
    }

    private List<string> Import()
    {
        var lines = new List<string>();
        StateDbModule.ImportMsgDrafts(_home, lines.Add);
        return lines;
    }

    [Fact]
    public void ADraftsFileBecomesRevisionOneAndStaysOnDisk()
    {
        var path = Seed("a", "draft", "---\nid: a\n---\nbody\n");

        Assert.Empty(Import());

        var row = Assert.Single(Q("SELECT b.rev, b.bytes, b.sealed_at_ms, b.sealed_by, m.body_rev, m.state FROM msg_bodies b JOIN msg_messages m ON m.pk = b.msg_pk"));
        Assert.Equal(1L, row[0]);
        Assert.Equal("---\nid: a\n---\nbody\n", Encoding.UTF8.GetString((byte[])row[1]!));
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero).ToUnixTimeMilliseconds(), row[2]);
        Assert.Equal("import", row[3]);
        Assert.Equal(1L, row[4]);
        Assert.Equal("draft", row[5]);
        Assert.True(File.Exists(path));
    }

    /// <summary>The import reads files outside its write; a draft with no file logs after the read of the one before it, which is where the fill lands.</summary>
    [Fact]
    public void AFillBetweenTheImportsReadAndItsWriteKeepsTheFill()
    {
        _ = Seed("a", "draft", "the file");
        _ = Seed("b", "draft", null);
        var filled = 0;

        StateDbModule.ImportMsgDrafts(_home, _ =>
        {
            filled++;
            Assert.True(StateDbModule.OpenDirect(_home).Write(tx =>
            {
                tx.Execute("INSERT INTO msg_bodies(msg_pk, rev, bytes, sha256, sealed_at_ms, sealed_by) SELECT pk, 1, ?1, 'x', 1, NULL FROM msg_messages WHERE id = 'a'", Encoding.UTF8.GetBytes("the fill"));
                tx.Execute("UPDATE msg_messages SET body_rev = 1 WHERE id = 'a'");
                return 0;
            }).Ok);
        });

        Assert.Equal(1, filled);
        Assert.Equal("the fill", Encoding.UTF8.GetString((byte[])Assert.Single(Q("SELECT b.bytes FROM msg_bodies b JOIN msg_messages m ON m.pk = b.msg_pk WHERE m.id = 'a'"))[0]!));
    }

    [Fact]
    public void NegativeASecondRunChangesNothing()
    {
        var path = Seed("a", "draft", "first");
        _ = Import();
        File.WriteAllText(path, "edited later");
        var version = Q("SELECT seq FROM domain_seq WHERE domain = 'msgs'")[0][0];

        Assert.Empty(Import());

        Assert.Equal("first", Encoding.UTF8.GetString((byte[])Assert.Single(Q("SELECT bytes FROM msg_bodies"))[0]!));
        Assert.Equal(version, Q("SELECT seq FROM domain_seq WHERE domain = 'msgs'")[0][0]);
    }

    [Fact]
    public void ADraftWithNoFileIsLoggedAndLeftEmptyAndTheOthersStillImport()
    {
        var path = Seed("gone", "draft", null);
        _ = Seed("here", "draft", "text");

        var line = Assert.Single(Import());

        Assert.Equal($"msgs import: draft gone request has no file at {path}; left empty", line);
        Assert.Null(Q("SELECT body_rev FROM msg_messages WHERE id = 'gone'")[0][0]);
        Assert.Equal(1L, Q("SELECT body_rev FROM msg_messages WHERE id = 'here'")[0][0]);
    }

    [Fact]
    public void ADraftOverTheBodyCapIsLoggedAndLeftEmpty()
    {
        _ = Seed("big", "draft", new string('x', Ah.Core.Json.MsgRows.BodyCap + 1));

        Assert.Contains("over the 262144 limit", Assert.Single(Import()), StringComparison.Ordinal);
        Assert.Empty(Q("SELECT rev FROM msg_bodies"));
    }

    [Fact]
    public void NegativeASealedRowOrADraftThatHasABodyIsNotImported()
    {
        _ = Seed("sealed", "sealed", "file of a sealed row");
        _ = Seed("held", "draft", "file of a draft with a body", bodyRev: 1);

        Assert.Empty(Import());

        Assert.Empty(Q("SELECT rev FROM msg_bodies"));
    }
}
