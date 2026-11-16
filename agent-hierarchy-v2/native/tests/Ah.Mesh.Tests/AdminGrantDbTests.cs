using Ah.Abstractions;

namespace Ah.Mesh.Tests;

/// <summary>ah2-46: the admin grant is a home document of the state database; a grant left in admin.json is taken in once.</summary>
public sealed class AdminGrantDbTests : IDisposable
{
    private const string Start = "Thu Nov  5 10:00:00 2026";

    private readonly string _home = Directory.CreateTempSubdirectory("ah-admin-").FullName;

    public void Dispose() => Directory.Delete(_home, recursive: true);

    /// <summary>Caller 300 under the Claude session 200, under a shell, under pid 1.</summary>
    private sealed class Line : IProcessTable
    {
        public IReadOnlyList<int>? Ancestors(int pid) => pid == 300 ? [200, 100, 1] : null;

        public bool? HasAncestor(int pid, int candidate) => pid == 300 && candidate is 200 or 100 or 1;

        public string? StartTime(long pid) => pid == 200 ? Start : null;

        public string? Command(int pid) => pid == 200 ? "claude" : "zsh";
    }

    /// <summary>Home documents in memory; <see cref="Down"/> makes every read fail the way an unreadable database does.</summary>
    private sealed class Docs : IHomeDocs
    {
        public Dictionary<string, string> Rows { get; } = [];

        public bool Down { get; set; }

        public string? Read(string key) => Down ? null : Rows.GetValueOrDefault(key);

        public bool TryRead(string key, out string? body)
        {
            body = Down ? null : Rows.GetValueOrDefault(key);
            return !Down;
        }

        public void Write(string key, string? body)
        {
            if (Down)
            {
                throw new InvalidOperationException("the database is down");
            }

            if (body is null)
            {
                Rows.Remove(key);
            }
            else
            {
                Rows[key] = body;
            }
        }
    }

    private string LegacyFile => Path.Combine(_home, "admin.json");

    [Fact]
    public void AGrantIsARowOfTheDatabaseAndARevokeRemovesIt()
    {
        var docs = new Docs();
        var table = new Line();

        Assert.Null(MeshModule.WriteAdminGrant(table, docs, _home, 200, "s1", DateTimeOffset.UnixEpoch));

        Assert.Single(docs.Rows);
        Assert.False(File.Exists(LegacyFile));
        Assert.True(MeshModule.HoldsAdminGrant(table, 300, docs, _home));
        Assert.False(MeshModule.HoldsAdminGrant(table, 100, docs, _home)); // negative: a caller that is not on the session's line

        Assert.Null(MeshModule.RevokeAdminGrant(docs, _home));
        Assert.Empty(docs.Rows);
        Assert.False(MeshModule.HoldsAdminGrant(table, 300, docs, _home));
    }

    [Fact]
    public void AGrantLeftInTheOldFileCountsAndIsTakenInOnce()
    {
        var docs = new Docs();
        var table = new Line();
        File.WriteAllText(LegacyFile, $$"""{"pid": 200, "pid_start": "{{Start}}", "session_id": null, "granted_at": "2026-11-14T02:19:00.000Z"}""");

        Assert.True(MeshModule.HoldsAdminGrant(table, 300, docs, _home)); // before the import: a read only, the file stays
        Assert.True(File.Exists(LegacyFile));
        Assert.Empty(docs.Rows);

        Assert.Null(MeshModule.ImportAdminGrant(docs, _home));
        Assert.False(File.Exists(LegacyFile));
        Assert.Single(docs.Rows);
        Assert.True(MeshModule.HoldsAdminGrant(table, 300, docs, _home));
        Assert.Null(MeshModule.ImportAdminGrant(docs, _home)); // nothing left to do
    }

    [Fact]
    public void NegativeAFileThatIsNotAGrantIsLeftAndNothingIsInvented()
    {
        var docs = new Docs();
        File.WriteAllText(LegacyFile, "not json");

        Assert.NotNull(MeshModule.ImportAdminGrant(docs, _home));
        Assert.True(File.Exists(LegacyFile));
        Assert.Empty(docs.Rows);
        Assert.False(MeshModule.HoldsAdminGrant(new Line(), 300, docs, _home));
    }

    [Fact]
    public void NegativeADatabaseThatCannotBeReadMeansNoAdminAndNothingThrows()
    {
        var docs = new Docs();
        var table = new Line();
        Assert.Null(MeshModule.WriteAdminGrant(table, docs, _home, 200, null, DateTimeOffset.UnixEpoch));
        File.WriteAllText(LegacyFile, $$"""{"pid": 200, "pid_start": "{{Start}}"}""");
        docs.Down = true;

        Assert.False(MeshModule.HoldsAdminGrant(table, 300, docs, _home)); // not even the old file counts while the database cannot answer
        Assert.False(MeshModule.TryReadAdminGrant(docs, _home, out _));
        Assert.NotNull(MeshModule.ImportAdminGrant(docs, _home));
        Assert.True(File.Exists(LegacyFile));
    }
}
