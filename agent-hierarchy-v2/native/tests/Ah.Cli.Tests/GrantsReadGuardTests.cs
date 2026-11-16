using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Config;
using Ah.Platform;
using Ah.TestKit;

namespace Ah.Cli.Tests;

/// <summary>
/// The grants read of per-dir-files-to-db S11e (§6c G5): one typed read through the graph's reader, fail-closed when the store cannot be
/// read, and the grants files shut to raw reads once a database exists.
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class GrantsReadGuardTests : IDisposable
{
    private readonly Sandbox _sandbox = new();

    public void Dispose() => _sandbox.Dispose();

    private string Hier => Path.Combine(_sandbox.Root, "proj", ".claude", "ah2");

    private string Extra => Path.Combine(_sandbox.Root, "extra");

    private static string Document(string members) => "{\"version\":1,\"team\":\"tm\",\"mode\":\"log\",\"audit_hash\":null,\"members\":{" + members + "}}";

    private string Granting => Document("\"m\":{\"work_root\":\"" + Extra + "\"}");

    private string PlantDocument()
    {
        var path = Path.Combine(Hier, "grants", "tm.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Granting);
        return path;
    }

    /// <summary>A grants store the test sets: unreadable, or the given documents; a database exists whenever it is set.</summary>
    private sealed class FakeGrants : IGrantSource
    {
        internal GrantsLog Log { get; set; } = GrantsLog.Unreadable;

        public GrantsLog? Read(string hierDir) => Log;

        public string Take(IDbOpTransaction tx, string hierDir) => hierDir;

        public IReadOnlyList<string> AuditRows() => [];
    }

    private AskLogFileReader Reader(IGrantSource? source) => new(PlatformModule.CreateIo(null).Files, StateDbModuleAsks(), source, static () => true);

    private IWorktreeGrants Grants(IGrantSource? source) =>
        ConfigModule.CreateWorktreeGrants(
            Reader(source),
            new FakeEnv(_sandbox.Home, _sandbox.AhHome, []));

    private IAskSource StateDbModuleAsks() => Ah.StateDb.StateDbModule.CreateAsks(_sandbox.AhHome);

    [Fact]
    public void AnUnreadableStoreReadsAsNoGrantsAndAReadableOneLetsTheRowDecide()
    {
        PlantDocument();
        // the negative: with no store the planted file grants the extra root
        Assert.Equal(new[] { Extra }, Grants(null).MemberRoots(Hier).Select(static r => r.Root));

        var store = new FakeGrants();
        var view = Grants(store).Read(Hier, "tm");
        Assert.False(view.Present);
        Assert.Empty(view.Members);
        Assert.Equal("the grants store could not be read", view.Problem);
        Assert.Empty(Grants(store).MemberRoots(Hier));

        // readable again: the row (the extra root revoked) decides, not the file
        store.Log = GrantsLog.Of([new GrantDocument("tm", Document(string.Empty), null)]);
        var revoked = Grants(store).Read(Hier, "tm");
        Assert.True(revoked.Present);
        Assert.Null(revoked.Problem);
        Assert.Empty(revoked.Members);
    }

    [Fact]
    public async Task AGrantWrittenThroughTheStoreIsWhatTheGraphsReaderSees()
    {
        Assert.Null(Ah.StateDb.StateDbModule.EnsureDatabase(_sandbox.AhHome));
        var hier = Directory.CreateDirectory(Path.Combine(_sandbox.Root, "repo", ".claude", "ah2")).FullName;
        var variables = new Dictionary<string, string?> { ["AH_TEST"] = "1", ["AH_HOME"] = _sandbox.AhHome, ["HOME"] = _sandbox.Home, ["WRANGL_HIERARCHY_DIR"] = hier };
        var env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables);
        var graph = ServiceGraph.Build(PlatformModule.CreateIo(null), env, FakeRunner.KnowingSelf(), new FakeDaemonClient { Reply = _ => new DaemonReply(0, default, "down") });
        var args = Ah.Core.Json.JsonValue.FromObject(new Ah.Core.Json.JsonObject { ["dir"] = hier, ["team"] = "t1", ["mode"] = "enforce" });

        var context = Ah.Core.Json.JsonValue.FromObject(new Ah.Core.Json.JsonObject { ["cwd"] = hier, ["hierarchy_dir"] = hier });

        var reply = await graph.State.InvokeAsync("worktree-grants", "gate", viaPost: true, args, context, opId: "op-gate", static (_, work) => Task.FromResult(work()));

        Assert.True(reply.Status == 200, System.Text.Encoding.UTF8.GetString(reply.Body.Span));
        var view = ConfigModule.CreateWorktreeGrants(graph.Io.Files, env).Read(hier, "t1");
        Assert.True(view is { Present: true, Mode: "enforce", Consistent: true, Problem: null }, view.ToString());
        // the negative: nothing went to the files, which the graph's reader now refuses to read raw
        Assert.False(File.Exists(Path.Combine(hier, "grants", "t1.json")));
        Assert.Throws<InvalidOperationException>(() => graph.Io.Files.List(Path.Combine(hier, "grants")));
    }

    [Fact]
    public void AGrantsFileCannotBeReadRawOnceADatabaseExists()
    {
        var path = PlantDocument();
        var reader = Reader(new FakeGrants());

        Assert.Throws<InvalidOperationException>(() => reader.ReadText(path));
        Assert.Throws<InvalidOperationException>(() => reader.List(Path.Combine(Hier, "grants")));
        // the negatives: a file beside the grants dir, and the grants file with no database
        var beside = Path.Combine(Hier, "grantsx.json");
        File.WriteAllText(beside, "{}");
        Assert.Equal("{}", reader.ReadText(beside));
        // a user's grants folder outside a hierarchy dir is never shut
        var userDoc = Path.Combine(_sandbox.Root, "repo", "docs", "grants", "plan.md");
        Directory.CreateDirectory(Path.GetDirectoryName(userDoc)!);
        File.WriteAllText(userDoc, "plan");
        Assert.Equal("plan", reader.ReadText(userDoc));
        Assert.Equal(["plan.md"], reader.List(Path.GetDirectoryName(userDoc)!).Select(Path.GetFileName));
        Assert.Equal(Granting, new AskLogFileReader(PlatformModule.CreateIo(null).Files, StateDbModuleAsks(), new FakeGrants(), static () => false).ReadText(path));
    }
}
