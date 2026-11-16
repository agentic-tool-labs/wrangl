using System.Text;
using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.StateDb.Tests;

/// <summary><c>MessageNames</c> reads only the paths, and gives the <c>Name</c> of each row <c>Messages</c> gives, in the same order (ah2-52).</summary>
public sealed class MsgNamesParityTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("ahnames").FullName;

    public void Dispose() => Directory.Delete(_home, recursive: true);

    private string Pool => Path.Combine(_home, "proj", ".claude", "ah2");

    private void Seed()
    {
        Assert.Equal(DbOpStatus.Done, StateDbModule.CreateOpRunner(_home).Run(new DbOpRequest("msgs", "op-1", "direct", _ => new OpResult(true, Encoding.UTF8.GetBytes("{\"ok\":true}")))).Status);
        using var db = SqliteConnection.Open(StateDbBackups.DatabasePath(_home), SqliteOpenMode.ReadWrite, 1000);
        var pool = MsgRows.PoolKey(Pool);
        var other = MsgRows.PoolKey(Path.Combine(_home, "other", ".claude", "ah2"));
        object?[][] rows =
        [
            // id, type, to, state, draft_path, export_path, pool: inserted out of key order
            ["20261115T000003Z-c", "request", "qa", "sealed", null, "/x/msgs/c.request.md", pool],
            ["20261115T000001Z-a", "response", "orchestrator", "draft", "/x/drafts/a.response.md", null, pool],
            ["20261115T000001Z-a", "request", "implementor", "sealed", "/x/drafts/a.draft.md", "/x/msgs/a.request.md", pool],
            ["20261115T000002Z-b", "request", "reviewer", "sealed", null, null, pool],
            ["20261115T000004Z-d", "request", "qa", "archived", null, "/x/msgs/d.request.md", pool],
            ["20261115T000005Z-e", "request", "qa", "sealed", null, "/x/msgs/e.request.md", other],
        ];
        foreach (var r in rows)
        {
            StateDbRows.Run(db, "INSERT INTO msg_messages(pool_dir, id, type, \"to\", slug, state, draft_path, export_path) VALUES (?1, ?2, ?3, ?4, 's', ?5, ?6, ?7)", [r[6], r[0], r[1], r[2], r[3], r[4], r[5]]);
        }
    }

    [Fact]
    public void TheNamesAreTheNamesOfTheMessagesInTheirOrderAndNegativeNoArchivedOrOtherPoolRow()
    {
        Seed();
        var source = StateDbModule.CreateMsgSource(_home);
        var expected = source.Messages(Pool).Select(static m => m.Name).ToList();

        Assert.Equal(["a.draft.md", "a.response.md", "", "c.request.md"], expected);
        Assert.Equal(expected, source.MessageNames(Pool));
        using var scope = source.Scope();
        Assert.Equal(expected, scope.MessageNames(Pool));
        Assert.DoesNotContain("d.request.md", source.MessageNames(Pool));
        Assert.DoesNotContain("e.request.md", source.MessageNames(Pool));
    }
}
