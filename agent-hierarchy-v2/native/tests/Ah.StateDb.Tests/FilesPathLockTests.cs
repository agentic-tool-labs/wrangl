using System.Runtime.Versioning;
using System.Text;
using Ah.Abstractions;
using Ah.Platform;
using Ah.State;

namespace Ah.StateDb.Tests;

/// <summary>A files-path op with a database locks under the home and replays from applied_writes; no state dir under the hierarchy (files-off-disk §13).</summary>
[UnsupportedOSPlatform("windows")]
public sealed class FilesPathLockTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("ahfileslock").FullName;
    private int _runs;

    public FilesPathLockTests() => Directory.CreateDirectory(Hierarchy);

    public void Dispose() => Directory.Delete(_home, recursive: true);

    private string Hierarchy => Path.Join(_home, "proj", ".claude", "ah2");

    private sealed class Domain(FilesPathLockTests owner, string stateDirectory) : IStateDomain
    {
        public string Name => "files-probe";

        public IReadOnlyList<string> StoreNames => [];

        public string StateDirectory(RequestContext context) => stateDirectory;

        public IReadOnlyList<StateOp> Ops =>
        [
            new("put", OpKind.Write, OpStyle.Overwrite, false, null, true, (call, ctx) => new OpResult(true, Encoding.UTF8.GetBytes($"{{\"run\":{++owner._runs}}}"))),
            new("big", OpKind.Write, OpStyle.Overwrite, false, null, true, (call, ctx) => new OpResult(true, Encoding.UTF8.GetBytes($"{{\"run\":{++owner._runs},\"pad\":\"{new string('x', 300)}\"}}"))),
        ];

        public void AfterWrite(OpCall operation, OpResult result)
        {
        }
    }

    private StateResult Put(string stateDirectory, string opId, TimeSpan wait, string op = "put")
    {
        var io = PlatformModule.CreateIo(null);
        var engine = StateModule.CreateEngine(io.Files, io.Writer, io.Appender, io.Locks, io.Clock, [new Domain(this, stateDirectory)], 4242, StateDbModule.CreateOpRunner(_home), StateDbModule.CreateModeReader(_home), "direct");
        return engine("files-probe", op, "{}"u8.ToArray(), new RequestContext(_home, Hierarchy, string.Empty, string.Empty), opId, wait);
    }

    [Fact]
    public void ARetryAfterResultTooLargeIsAnsweredWithTheErrorAndDoesNotRerunTheBody()
    {
        var dir = Path.Join(Hierarchy, ".state-files-probe");
        var first = Put(dir, "op-big", TimeSpan.FromSeconds(1), "big");
        var again = Put(dir, "op-big", TimeSpan.FromSeconds(1), "big");

        Assert.Equal("result_too_large", first.ErrorCode);
        Assert.Equal("result_too_large", again.ErrorCode);
        Assert.Equal(1, _runs);

        // negative: an in-cap result under another id still replays as ok
        Assert.True(Put(dir, "op-small", TimeSpan.FromSeconds(1)).Ok);
        Assert.True(Put(dir, "op-small", TimeSpan.FromSeconds(1)).Ok);
        Assert.Equal(2, _runs);
    }

    [Fact]
    public void ARepeatedOpIdIsAnsweredFromAppliedWritesAndNoStateDirIsMade()
    {
        var dir = Path.Join(Hierarchy, ".state-files-probe");
        var first = Put(dir, "op-1", TimeSpan.FromSeconds(1));
        var again = Put(dir, "op-1", TimeSpan.FromSeconds(1));

        Assert.True(again.Ok, again.Error);
        Assert.Equal("{\"run\":1}", Encoding.UTF8.GetString(first.ResultJson.Span));
        Assert.Equal("{\"run\":1}", Encoding.UTF8.GetString(again.ResultJson.Span));
        Assert.Equal(1, _runs);
        Assert.Empty(Directory.GetDirectories(Hierarchy, ".state-*"));

        // negative: another op id is not answered from the row
        Assert.Equal("{\"run\":2}", Encoding.UTF8.GetString(Put(dir, "op-2", TimeSpan.FromSeconds(1)).ResultJson.Span));
    }

    [Fact]
    public void TheSameOpIdInTwoStateDirsAppliesTwiceAndAnUnscopedRowNeverAnswers()
    {
        Assert.Equal("{\"run\":1}", Encoding.UTF8.GetString(Put(Path.Join(Hierarchy, ".state-a"), "park:q1", TimeSpan.FromSeconds(1)).ResultJson.Span));
        Assert.Equal("{\"run\":2}", Encoding.UTF8.GetString(Put(Path.Join(_home, "other", ".state-a"), "park:q1", TimeSpan.FromSeconds(1)).ResultJson.Span));

        // a pre-upgrade row under the unscoped key <mode key>:<op id> is not used to answer
        using (var raw = SqliteConnection.Open(Path.Join(_home, "state", "ah.db"), SqliteOpenMode.ReadWrite, 50))
        {
            raw.Exec("INSERT INTO applied_writes(write_id, applied_at, path, result) VALUES ('files-probe:park:q5', 0, 'direct', '{\"run\":0}')");
        }

        Assert.Equal("{\"run\":3}", Encoding.UTF8.GetString(Put(Path.Join(Hierarchy, ".state-a"), "park:q5", TimeSpan.FromSeconds(1)).ResultJson.Span));
        Assert.Equal(3, _runs);
    }

    [Fact]
    public void TwoOpsOnOneDomainAndDirContendOnOneHomeLockFileAndAnotherDirDoesNot()
    {
        var dir = Path.Join(Hierarchy, ".state-files-probe");
        Directory.CreateDirectory(Path.Join(Hierarchy, "x"));
        var lockPath = StateDbModule.StateLockPath(_home, "files-probe", Path.Join(Hierarchy, "x", "..", ".state-files-probe"), ".domain.lock");
        Assert.StartsWith(StateDbModule.StateLockDirectory(_home), lockPath, StringComparison.Ordinal);
        Assert.True(Put(dir, "op-a", TimeSpan.FromSeconds(1)).Ok);

        var locks = PlatformModule.CreateIo(null).Locks;
        using (locks.Acquire(lockPath, TimeSpan.FromSeconds(1)))
        {
            Assert.Equal("lock_timeout", Put(dir, "op-b", TimeSpan.FromMilliseconds(100)).ErrorCode);

            // negative: another state dir is another lock, so it runs while this one is held
            Assert.True(Put(Path.Join(Hierarchy, ".state-other"), "op-c", TimeSpan.FromMilliseconds(100)).Ok);
        }
    }
}
