using System.Runtime.Versioning;
using System.Text;
using Ah.Abstractions;
using Ah.Platform;
using Ah.State;

namespace Ah.StateDb.Tests;

/// <summary>A db-only domain runs its DbBody with no mode row, through the daemon's engine and the in-process replay alike (per-dir-files-to-db S0).</summary>
[UnsupportedOSPlatform("windows")]
public sealed class DbOnlyDomainTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("ahdbonly").FullName;

    public void Dispose() => Directory.Delete(_home, recursive: true);

    private string Hierarchy => Path.Join(_home, "proj", ".claude", "ah2");

    private sealed class Domain(string hierarchy, bool dbOnly) : IStateDomain
    {
        public string Name => "dbonly-probe";

        public IReadOnlyList<string> StoreNames => [];

        public bool DbOnly => dbOnly;

        public string StateDirectory(RequestContext context) => Path.Join(hierarchy, ".state-dbonly-probe");

        public IReadOnlyList<StateOp> Ops =>
        [
            new("put", OpKind.Write, OpStyle.Overwrite, false, null, true, (call, ctx) =>
            {
                ctx!.Write(Path.Join(hierarchy, "put.json"), Path.Join(hierarchy, "put.tmp"), "{}"u8);
                return new OpResult(true, "{\"via\":\"files\"}"u8.ToArray());
            })
            {
                DbBody = (call, tx) =>
                {
                    tx.Execute("INSERT OR REPLACE INTO meta(key, value) VALUES ('probe.dbonly', ?1)", call.OperationId);
                    return new OpResult(true, "{\"via\":\"db\"}"u8.ToArray());
                },
            },
        ];

        public void AfterWrite(OpCall operation, OpResult result)
        {
        }
    }

    private StateResult Put(bool dbOnly, IDbOpRunner? runner, string path, string opId)
    {
        var io = PlatformModule.CreateIo(null);
        var engine = StateModule.CreateEngine(io.Files, io.Writer, io.Appender, io.Locks, io.Clock, [new Domain(Hierarchy, dbOnly)], 4242, runner, runner is null ? null : StateDbModule.CreateModeReader(_home), path);
        return engine("dbonly-probe", "put", "{}"u8.ToArray(), new RequestContext(_home, Hierarchy, string.Empty, string.Empty), opId, TimeSpan.FromSeconds(1));
    }

    private string? Probe()
    {
        using var connection = SqliteConnection.Open(StateDbBackups.DatabasePath(_home), SqliteOpenMode.ReadOnly, 1000);
        return StateDbRows.Run(connection, "SELECT value FROM meta WHERE key = 'probe.dbonly'", []) is [var row] ? row[0] as string : null;
    }

    [Fact]
    public void ADbOnlyDomainWritesTheDatabaseThroughTheDaemonAndTheReplayWithNoModeRowAndNoFile()
    {
        var daemon = Put(true, StateDbModule.CreateOpRunner(_home), "daemon", "op-d");
        Assert.True(daemon.Ok, daemon.Error);
        Assert.Equal("{\"via\":\"db\"}", Encoding.UTF8.GetString(daemon.ResultJson.Span));
        Assert.Equal("op-d", Probe());

        var replay = Put(true, StateDbModule.CreateOpRunner(_home, forHook: true), "direct", "op-r");
        Assert.True(replay.Ok, replay.Error);
        Assert.Equal("op-r", Probe());

        Assert.False(Directory.Exists(Hierarchy));
        using var connection = SqliteConnection.Open(StateDbBackups.DatabasePath(_home), SqliteOpenMode.ReadOnly, 1000);
        Assert.Empty(StateDbRows.Run(connection, "SELECT 1 FROM meta WHERE key = 'mode.dbonly-probe'", []));
    }

    [Fact]
    public void NegativeTheSameDomainNotDbOnlyStaysModeGatedAndTakesTheFilesPath()
    {
        Directory.CreateDirectory(Hierarchy);
        var put = Put(false, StateDbModule.CreateOpRunner(_home), "direct", "op-f");

        Assert.True(put.Ok, put.Error);
        Assert.Equal("{\"via\":\"files\"}", Encoding.UTF8.GetString(put.ResultJson.Span));
        Assert.True(File.Exists(Path.Join(Hierarchy, "put.json")));
    }

    [Fact]
    public void NegativeADbOnlyWriteWithNoDatabaseRunnerFailsAndWritesNoFile()
    {
        var put = Put(true, null, "direct", "op-n");

        Assert.False(put.Ok);
        Assert.False(Directory.Exists(Hierarchy));
    }
}
