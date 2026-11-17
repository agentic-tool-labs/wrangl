using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Text;
using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Platform;
using Ah.TestKit;
using Ah.Watch;

namespace Ah.Cli.Tests;

/// <summary>The peer-records mode behind the heard notice is read once per reuse window, and the daemon wires it.</summary>
[UnsupportedOSPlatform("windows")]
public sealed class ReadModelWriteRelayModeTests : IDisposable
{
    private readonly Sandbox _sandbox = new();

    public void Dispose() => _sandbox.Dispose();

    private sealed class Spy : IReadModel
    {
        internal List<(string Lane, string Key)> Emitted { get; } = [];

        public string Epoch => "e";

        public void Emit(string lane, string key) => Emitted.Add((lane, key));

        public void EmitKeySet(string lane)
        {
        }

        public byte[]? Manifest(string view) => null;

        public Task<(int Status, byte[] Body)> ListAsync(string sliceId, IReadOnlyDictionary<string, string> query, CancellationToken ct) => Task.FromResult((404, Array.Empty<byte>()));

        public Task<byte[]> PollAsync(long since, string? epoch, IReadOnlyCollection<string>? subscribed, TimeSpan wait, CancellationToken ct, IReadOnlyCollection<string>? fresh = null) => Task.FromResult(Array.Empty<byte>());

        public void Dispose()
        {
        }
    }

    private sealed class Mode(bool inDb)
    {
        internal bool InDb { get; set; } = inDb;

        internal int Reads { get; private set; }

        internal bool Read()
        {
            Reads++;
            return InDb;
        }
    }

    private static void Append(ReadModelWriteRelay relay) =>
        relay.Wrote("peer-records", "append", Encoding.UTF8.GetBytes("{}"), Encoding.UTF8.GetBytes("{}"));

    private static List<(string Lane, string Key)> EmittedBy(string op, string args)
    {
        var relay = new ReadModelWriteRelay(() => 0);
        var spy = new Spy();
        relay.Attach(spy);
        relay.Wrote("progress", op, Encoding.UTF8.GetBytes(args), Encoding.UTF8.GetBytes("{}"));
        return spy.Emitted;
    }

    // ah2-74 G4: a screen-clear mark or unmark that changed a row is told to the activity lane as a hook's record is; negatives: one that
    // changed nothing (sent every blocked tick) is not, nor is put, which no hook sends
    [Theory]
    [InlineData("mark", true, true)]
    [InlineData("unmark", true, true)]
    [InlineData("mark", false, false)]
    [InlineData("unmark", false, false)]
    [InlineData("put", true, false)]
    public void AScreenClearMarkReReadsTheMembersActivitySlice(string op, bool written, bool told)
    {
        var relay = new ReadModelWriteRelay(() => 0);
        var spy = new Spy();
        relay.Attach(spy);
        relay.Wrote("activity", op, Encoding.UTF8.GetBytes("""{"dir":"/w/.claude/ah2","mirror_dir":"/m/.claude/ah2","subject":"s1"}"""), Encoding.UTF8.GetBytes(written ? """{"written":true}""" : """{"written":false}"""));

        Assert.Equal(told, spy.Emitted.Contains(("activity", WatchModule.ActivityKey("/m/.claude/ah2", "s1"))));
    }

    /// <summary>
    /// per-dir-files-to-db S11e: a grants write changes only rows, so no grants file event re-reads the Worktrees lane; the daemon-served
    /// write itself names the lane's key (dir and team).
    /// </summary>
    [Theory]
    [InlineData("allow")]
    [InlineData("gate")]
    public void AGrantsWriteRefreshesTheWorktreesLaneOfItsTeam(string op)
    {
        var relay = new ReadModelWriteRelay(() => 0);
        var spy = new Spy();
        relay.Attach(spy);

        relay.Wrote("worktree-grants", op, Encoding.UTF8.GetBytes("{\"dir\":\"/d\",\"team\":\"tm\"}"), Encoding.UTF8.GetBytes("{}"));
        // the negative: a write naming no team names no one key
        relay.Wrote("worktree-grants", op, Encoding.UTF8.GetBytes("{\"dir\":\"/d\"}"), Encoding.UTF8.GetBytes("{}"));

        Assert.Equal([("worktrees", WatchModule.WorktreeKey("/d", "tm"))], spy.Emitted);
    }

    [Fact]
    public void AWorkWriteRefreshesTheBacklogTheHeldLogsAndTheTeamItNames()
    {
        var emitted = EmittedBy(Ah.Hierarchy.HierarchyModule.WorkWriteOp, "{\"dir\":\"/d\",\"team\":\"tm\"}");

        Assert.Contains(("backlog", "/d"), emitted);
        Assert.Contains(("item-log", "/d"), emitted);
        Assert.Contains(("progress", WatchModule.ProgressKey("/d", "tm")), emitted);
    }

    [Fact]
    public void ARegisterRefreshesEachTeamItNamesOnce()
    {
        var emitted = EmittedBy(Ah.Hierarchy.HierarchyModule.WorkWriteOp, "{\"dir\":\"/d\",\"register\":[{\"team\":\"a\"},{\"team\":\"a\"},{\"team\":\"b\"},{\"team\":\"\"}]}");

        Assert.Equal([WatchModule.ProgressKey("/d", "a"), WatchModule.ProgressKey("/d", "b")], emitted.Where(static e => e.Lane == "progress").Select(static e => e.Key));
    }

    [Theory]
    [InlineData("{\"dir\":\"/d\",\"team\":\"\"}")]
    [InlineData("{\"dir\":\"/d\"}")]
    public void ABacklogRowsWorkWriteRefreshesNoTeam(string args) =>
        Assert.DoesNotContain(EmittedBy(Ah.Hierarchy.HierarchyModule.WorkWriteOp, args), static e => e.Lane == "progress");

    [Theory]
    [InlineData("{\"team\":\"tm\"}")]
    [InlineData("{\"dir\":\"\",\"team\":\"tm\"}")]
    [InlineData("{\"dir\":3,\"team\":\"tm\"}")]
    public void AWorkWriteWithNoDirEmitsNothing(string args) => Assert.Empty(EmittedBy(Ah.Hierarchy.HierarchyModule.WorkWriteOp, args));

    [Fact]
    public void ABacklogWriteRefreshesTheHeldLogs() =>
        Assert.Contains(("item-log", "/d"), EmittedBy(Ah.Hierarchy.HierarchyModule.BacklogWriteOp, "{\"dir\":\"/d\"}"));

    [Fact]
    public void ABurstOfDatabaseModeAppendsReadsTheModeOnce()
    {
        long now = 1_000;
        var relay = new ReadModelWriteRelay(() => now);
        var spy = new Spy();
        var mode = new Mode(true);
        relay.Attach(spy, null, mode.Read);

        for (var i = 0; i < 5; i++)
        {
            now += 100;
            Append(relay);
        }

        Assert.Equal(1, mode.Reads);
        Assert.Equal(5, spy.Emitted.Count(static e => e.Lane == "messages"));
    }

    [Fact]
    public void TheModeIsReadAgainOnceTheReuseWindowHasPassedAndAFlipShows()
    {
        long now = 1_000;
        var relay = new ReadModelWriteRelay(() => now);
        var spy = new Spy();
        var mode = new Mode(false);
        relay.Attach(spy, null, mode.Read);

        Append(relay);
        mode.InDb = true;
        now += (long)ReadModelWriteRelay.ModeReuse.TotalMilliseconds - 1;
        Append(relay);
        Assert.DoesNotContain(spy.Emitted, static e => e.Lane == "messages");

        now += 1;
        Append(relay);

        Assert.Equal(2, mode.Reads);
        Assert.Single(spy.Emitted, static e => e.Lane == "messages");
    }

    [Fact]
    public void AFilesModeAppendNeverEmitsTheHeardKeyAndAWriteOfAnotherDomainNeverReadsTheMode()
    {
        var relay = new ReadModelWriteRelay(() => 0);
        var spy = new Spy();
        var mode = new Mode(false);
        relay.Attach(spy, null, mode.Read);

        Append(relay);
        relay.Wrote("session-pids", "append", Encoding.UTF8.GetBytes("{}"), Encoding.UTF8.GetBytes("{}"));

        Assert.DoesNotContain(spy.Emitted, static e => e.Lane == "messages");
        Assert.Equal(1, mode.Reads);
    }

    [Fact]
    public void ABacklogWriteEmitsTheBacklogOfItsDirAndAPlanAppendOrADirlessWriteDoesNot()
    {
        var relay = new ReadModelWriteRelay(() => 0);
        var spy = new Spy();
        relay.Attach(spy, null, () => false);

        relay.Wrote("progress", Ah.Hierarchy.HierarchyModule.BacklogWriteOp, Encoding.UTF8.GetBytes("""{"dir":"/r/.claude/ah2","item":"b1"}"""), Encoding.UTF8.GetBytes("{}"));
        relay.Wrote("progress", Ah.Hierarchy.HierarchyModule.BacklogWriteOp, Encoding.UTF8.GetBytes("""{"item":"b2"}"""), Encoding.UTF8.GetBytes("{}"));
        relay.Wrote("progress", "plan-append", Encoding.UTF8.GetBytes("""{"dir":"/r/.claude/ah2","team":"t"}"""), Encoding.UTF8.GetBytes("{}"));

        Assert.Equal([("backlog", "/r/.claude/ah2")], spy.Emitted.Where(static e => e.Lane == "backlog"));
    }

    [Fact]
    public void AChecklistRowWriteEmitsItsTeamsProgressAndARollupWriteDoesNot()
    {
        var relay = new ReadModelWriteRelay(() => 0);
        var spy = new Spy();
        relay.Attach(spy, null, () => false);
        var args = Encoding.UTF8.GetBytes("""{"dir":"/r/.claude/ah2","team":"t"}""");

        relay.Wrote("progress", "rollup-block-upsert", args, Encoding.UTF8.GetBytes("{}"));
        Assert.DoesNotContain(spy.Emitted, static e => e.Lane == "progress");

        relay.Wrote("progress", "state-cas", args, Encoding.UTF8.GetBytes("{}"));
        Assert.Single(spy.Emitted, static e => e.Lane == "progress");
    }

    [Fact]
    public void ABacklogWriteFromAWorkTreeEmitsTheMainCheckoutsCanonicalDirAndOneFromAnotherRepoDoesNot()
    {
        var main = Path.Combine(_sandbox.Root, "main");
        var worktree = Path.Combine(main, ".claude", "worktrees", "w");
        var other = Path.Combine(_sandbox.Root, "other");
        Directory.CreateDirectory(main);
        MessagePoolResolutionTests.Git(main, "init", "-q");
        MessagePoolResolutionTests.Git(main, "-c", "user.name=Relay Test", "-c", "user.email=relay@example.invalid", "-c", "commit.gpgSign=false", "commit", "--allow-empty", "-qm", "fixture");
        MessagePoolResolutionTests.Git(main, "worktree", "add", "-q", "-b", "member", worktree);
        Directory.CreateDirectory(other);
        MessagePoolResolutionTests.Git(other, "init", "-q");
        var (_, graph) = Graph();
        var relay = new ReadModelWriteRelay(() => 0);
        var spy = new Spy();
        relay.Attach(spy, null, () => false);

        foreach (var cwd in new[] { worktree, other })
        {
            relay.Wrote("progress", Ah.Hierarchy.HierarchyModule.BacklogWriteOp, Encoding.UTF8.GetBytes(Ah.Core.Json.JsonText.Stringify(Ah.Core.Json.JsonValue.FromObject(new Ah.Core.Json.JsonObject { ["dir"] = Verbs.BacklogVerb.DirOf(graph, cwd), ["item"] = "b1" }), 0)), Encoding.UTF8.GetBytes("{}"));
        }

        // A known-dirs entry, one of the lane's canonical keys, is the real path of main's hierarchy dir.
        var canonical = Ah.Core.Text.PathGuard.RealPath(main, true) + "/.claude/ah2";
        var keys = spy.Emitted.Where(static e => e.Lane == "backlog").Select(static e => e.Key).ToList();
        Assert.Equal(2, keys.Count);
        Assert.Equal(canonical, keys[0]);
        Assert.NotEqual(canonical, keys[1]);
    }

    private static string Here([CallerFilePath] string here = "") => here;

    private (FakeEnv Env, ServiceGraph Graph) Graph()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Here())!, "..", "..", ".."));
        var env = new FakeEnv(
            _sandbox.Home,
            _sandbox.AhHome,
            new Dictionary<string, string?> { ["AH_HOME"] = _sandbox.AhHome, ["HOME"] = _sandbox.Home },
            new PluginInfo(root, "wrangl", "0.0.0", "1"));
        return (env, ServiceGraph.Build(PlatformModule.CreateIo(null), env, new FakeRunner(), new FakeDaemonClient(), inDaemon: true));
    }

    [Fact]
    public void TheDaemonAttachesTheModeReadToTheRelayWhenItBuildsTheReadModel()
    {
        var (env, graph) = Graph();
        var relay = new ReadModelWriteRelay();
        var services = new DaemonServices(new Lazy<ServiceGraph>(graph), env, tmuxTmpdir: Path.Combine(_sandbox.Home, "tmuxtmp"), writeRelay: relay);
        try
        {
            Assert.False(relay.PeerRecordsModeAttached);

            services.RegisterRoutes(new Routes());

            Assert.True(relay.PeerRecordsModeAttached);
        }
        finally
        {
            services.Stop();
        }
    }

    private sealed class Routes : IRouteRegistry
    {
        public void Add(string pattern, Func<RouteRequest, CancellationToken, Task<RouteResponse>> handler)
        {
        }
    }
}
