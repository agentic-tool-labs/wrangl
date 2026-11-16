using System.Text;
using Ah.Core.Json;

namespace Ah.StateDb.Tests;

/// <summary>ah-msgs-fast S1: <c>msg_exchanges.live</c> follows the §3.1 table at write time, and the V9 backfill gives the same values.</summary>
public sealed class MsgsLiveTests : IDisposable
{
    private const long SealedAt = 1_780_000_000_000;
    private const string Pool = "/pool";

    private readonly string _home = Directory.CreateTempSubdirectory("ahmsgslive").FullName;
    private readonly List<IDisposable> _open = [];

    public void Dispose()
    {
        foreach (var d in _open)
        {
            d.Dispose();
        }

        Directory.Delete(_home, recursive: true);
    }

    private static string Skeleton(string id) => $"---\nid: {id}\n---\n## [1] status\n- [1] result:\n";

    private static MsgFileRow Row(string id, string type, string to, byte[]? bytes, bool archived = false, bool closed = false) =>
        new(Pool, $"{id}--{to}--s--{type}.md", id, type, to, "s", "orchestrator", null, null, "t", null, null, 1, archived,
            bytes is null ? MsgRows.Draft : MsgRows.Sealed, bytes, $"{Pool}/msgs/{id}--{to}--s--{type}.md", Closed: closed);

    /// <summary>Each case: the exchange id, its request's recipient, its response (null: none; draft: no bytes) and the live value §3.1 gives.</summary>
    private static readonly (string Id, string To, string? Response, bool Draft, bool Archived, bool Closed, bool Live)[] Cases =
    [
        ("20260101-000000-aaaa", "implementor", null, false, false, false, true), // no response
        ("20260101-000000-bbbb", "implementor", "placeholder", true, false, false, true), // draft response
        ("20260101-000000-cccc", "implementor", "placeholder", false, false, false, true), // sealed, no report: the nearest input to the next one that must stay live
        ("20260101-000000-dddd", "implementor", "authored", false, false, false, false), // sealed, authored
        ("20260101-000000-eeee", "implementor", "big", false, false, false, false), // sealed, over the 4096-byte cut
        ("20260101-000000-ffff", "implementor", "malformed", false, false, false, false), // sealed, front matter names another id
        ("20260101-000000-gggg", "orchestrator", "placeholder", true, false, false, false), // to orchestrator, even a draft
        ("20260101-000000-hhhh", "sub-orchestrator", "authored", false, false, false, false), // sub-orchestrator, authored: the normal rule (ah-msgs-fast-2 §3.2)
        ("20260101-000000-hhh2", "sub-orchestrator", "placeholder", false, false, false, true), // sub-orchestrator, sealed, no report
        ("20260101-000000-hhh3", "sub-orchestrator", "placeholder", true, false, false, true), // sub-orchestrator, draft response
        ("20260101-000000-hhh4", "sub-orchestrator", null, false, false, false, true), // sub-orchestrator, unanswered
        ("20260101-000000-iiii", "implementor", "withdrawn", false, false, true, false), // withdrawn
        ("20260101-000000-jjjj", "implementor", null, false, true, false, false), // archived request
    ];

    private static byte[] Body(string id, string kind) => Encoding.UTF8.GetBytes(kind switch
    {
        "placeholder" => Skeleton(id),
        "authored" => Skeleton(id) + "done\n",
        "big" => Skeleton(id) + "\n" + new string(' ', 4100),
        "malformed" => Skeleton("20991231-000000-zzzz") + "done\n",
        "withdrawn" => $"---\nid: {id}\nwithdrawn: true\n---\nwithdrawn\n",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    });

    [Fact]
    public void LiveAtWriteFollowsTheTableAndTheBackfillGivesTheSameValues()
    {
        WriteCases();
        var atWrite = Read();
        Assert.Equal(Cases.ToDictionary(c => c.Id, c => (c.Live, c.Response is not null && !c.Draft ? SealedAt : (long?)null)), atWrite);

        var raw = SqliteConnection.Open(Path.Join(_home, "state", "ah.db"), create: false, 1000);
        _open.Add(raw);
        raw.Exec("UPDATE msg_exchanges SET live = 1, answered_ms = NULL");
        StateDbMigrations.BackfillLive(raw);

        Assert.Equal(atWrite, Read());
    }

    /// <summary>
    /// R3-3: the V12 upgrade gives each closed exchange with no closed_ms its answer time, else the upgrade time, so prune keeps it a full
    /// window; an exchange that is not closed keeps none (the negative).
    /// </summary>
    [Fact]
    public void TheV12UpgradeDatesAClosedExchangeWithNoCloseTimeFromItsAnswerElseTheUpgrade()
    {
        WriteCases();
        var raw = SqliteConnection.Open(Path.Join(_home, "state", "ah.db"), create: false, 1000);
        _open.Add(raw);
        raw.Exec("UPDATE msg_exchanges SET closed_ms = NULL");
        raw.Exec("UPDATE msg_exchanges SET state = 'closed', answered_ms = NULL WHERE id = (SELECT MIN(id) FROM msg_exchanges)");
        var before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var statement in StateDbMigrations.V12Statements)
        {
            raw.Exec(statement);
        }

        var reader = StateDbModule.OpenReader(_home).Reader!;
        using (reader)
        {
            var rows = reader.Query("SELECT state, answered_ms, closed_ms FROM msg_exchanges ORDER BY id");
            Assert.Contains(rows, r => (string)r[0]! == "closed" && r[1] is null && (long)r[2]! >= before - 1_000);
            Assert.Contains(rows, r => (string)r[0]! != "closed");
            Assert.All(rows, r => Assert.Equal((string)r[0]! == "closed" ? r[1] ?? r[2] : null, r[2]));
        }
    }

    private void WriteCases()
    {
        _open.Add(StateDbModule.Open(_home, mayCreate: true));
        var rows = new List<MsgFileRow>();
        foreach (var c in Cases)
        {
            rows.Add(Row(c.Id, "request", c.To, null, archived: c.Archived));
            if (c.Response is { } kind)
            {
                rows.Add(Row(c.Id, "response", c.To, c.Draft ? null : Body(c.Id, kind), closed: c.Closed));
            }
        }

        var written = StateDbModule.OpenDirect(_home).Write(tx =>
        {
            long Pk(MsgFileRow r) => (long)tx.Query(MsgRows.SelectPkSql, MsgRows.KeyArgs(r))[0][0]!;
            foreach (var row in rows)
            {
                tx.Execute(MsgRows.InsertMessageSql, MsgRows.MessageArgs(row));
                if (row.Bytes is not null)
                {
                    tx.Execute(MsgRows.InsertBodySql, MsgRows.BodyArgs(Pk(row), row, SealedAt));
                }
            }

            foreach (var pair in MsgRows.Pair(rows))
            {
                tx.Execute(MsgRows.InsertExchangeSql, MsgRows.ExchangeArgs(pair, Pk(pair.Request), pair.Response is null ? null : Pk(pair.Response), pair.Response?.Bytes is null ? null : SealedAt));
            }

            return 0;
        });
        Assert.True(written.Ok, written.Message);
    }

    private Dictionary<string, (bool Live, long? AnsweredMs)> Read()
    {
        var reader = StateDbModule.OpenReader(_home).Reader!;
        using (reader)
        {
            return reader.Query("SELECT id, live, answered_ms FROM msg_exchanges", []).ToDictionary(r => (string)r[0]!, r => ((long)r[1]! == 1, r[2] as long?));
        }
    }
}
