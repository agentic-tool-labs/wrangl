using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Core.Json;
using Ah.Hierarchy;
using Ah.Platform;
using Ah.StateDb;
using Ah.TestKit;

namespace Ah.Cli.Tests;

/// <summary>b397 group 2: the session roles, the ultra-gate choices and the usage rows live only in the database; no file is written or read.</summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class HomeDocsDbTests : IDisposable
{
    private readonly Sandbox _sandbox = new();
    private readonly ServiceGraph _graph;
    private readonly RequestContext _context;

    public HomeDocsDbTests()
    {
        var dir = Path.Combine(_sandbox.Root, "proj", ".claude", "ah2");
        Directory.CreateDirectory(dir);
        using (var open = StateDbModule.Open(_sandbox.AhHome, mayCreate: true))
        {
            Assert.Equal(StateDbAvailability.Ready, open.Status.Availability);
        }

        var variables = new Dictionary<string, string?> { ["AH_TEST"] = "1", ["AH_HOME"] = _sandbox.AhHome, ["HOME"] = _sandbox.Home, ["WRANGL_HIERARCHY_DIR"] = dir };
        var env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables, new PluginInfo(_sandbox.PluginRoot, "wrangl", "1.0.0", "1"));
        _graph = ServiceGraph.Build(PlatformModule.CreateIo(null), env, FakeRunner.KnowingSelf(), new FakeDaemonClient { Reply = _ => new DaemonReply(0, default, "down") });
        _context = _graph.Hierarchy.Messages.PoolContext(dir);
    }

    public void Dispose() => _sandbox.Dispose();

    private StateResult Write(string domain, string op, JsonObject args) => _graph.Writer.Invoke(domain, op, JsonText.ToUtf8(args, 0), _context);

    private string OldFile(string name) => Path.Combine(_sandbox.Home, ".claude", "ah2", name);

    [Fact]
    public void ASessionRoleIsARowThatOnlyItsSessionReads()
    {
        var result = Write("session-roles", "set", new JsonObject { ["session_id"] = "s1", ["role"] = "implementor" });

        Assert.True(result.Ok, result.Error);
        Assert.Equal("implementor", _graph.Config.ReadSessionRole("s1").AsString());
        Assert.True(_graph.Config.ReadSessionRole("s2").IsNull);
        Assert.False(File.Exists(OldFile("agent-hierarchy.session-roles.json")));
    }

    [Fact]
    public void AnUltraGateChoiceIsARowAndAnUnknownChoiceChangesNothing()
    {
        Assert.True(Write("ultra-gate", "write", new JsonObject { ["session_id"] = "s1", ["choice"] = "each" }).Ok);

        Assert.False(Write("ultra-gate", "write", new JsonObject { ["session_id"] = "s1", ["choice"] = "always" }).Ok);

        var doc = JsonText.Parse(TestHomeDocs.Text(_sandbox.AhHome, "home.ultra-gate")!);
        Assert.Equal("each", doc.Member("sessions")!.Value.Member("s1")!.Value.Member("choice")!.Value.AsString());
        Assert.False(File.Exists(OldFile("agent-hierarchy.gate.json")));
    }

    [Fact]
    public void AUsageAppendIsARowWithItsOpId()
    {
        var result = _graph.Writer.Invoke("usage", "append", JsonText.ToUtf8(new JsonObject { ["rec"] = new JsonObject { ["agent_id"] = "a-1" } }, 0), _context, new StateCallOptions("op-1"));

        Assert.True(result.Ok, result.Error);
        var row = Assert.Single(Usage().ReadUsageRecords());
        Assert.Equal("op-1", row.Member("op_id")!.Value.AsString());
        Assert.False(File.Exists(OldFile("agent-hierarchy.usage.jsonl")));
    }

    [Theory]
    [InlineData(0, 30_000)]
    [InlineData(-1, 50_000)]
    public void AnAppendPastPruneAtKeepsOnlyTheNewestPruneToRows(int beyond, int kept)
    {
        var usage = Usage();
        TestHomeDocs.PutUsage(_sandbox.AhHome, [.. Enumerable.Repeat("{\"a\":1}", usage.PruneAt + beyond)]);

        Assert.True(Write("usage", "append", new JsonObject { ["rec"] = new JsonObject { ["a"] = 2 } }).Ok);

        var rows = usage.ReadUsageRecords();
        Assert.Equal(kept, rows.Count);
        Assert.Equal(2, rows[^1].Member("a")!.Value.AsNumber());
    }

    [Theory]
    [InlineData("home.session-roles", 0)]
    [InlineData("mode.msgs", 2)]
    public void TestSeedStoresOnlyAHomeDocumentKey(string key, int code)
    {
        var env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, new Dictionary<string, string?> { ["AH_TEST"] = "1", ["AH_HOME"] = _sandbox.AhHome, ["HOME"] = _sandbox.Home }, new PluginInfo(_sandbox.PluginRoot, "wrangl", "1.0.0", "1"));
        var context = new CliContext(env, new RecordingExec(), new FakeRunner(), "ah", _sandbox.Executable("install/ah"), _sandbox.Home, 501, null, new Lazy<ServiceGraph>(_graph)) { ReadStdin = () => "{\"v\":1}"u8.ToArray() };

        Assert.Equal(code, Ah.Cli.Verbs.Db.TestSeedVerb.Run(context, ["home-docs", "--key", key], TextWriter.Null, TextWriter.Null));

        Assert.Equal(code == 0 ? "{\"v\":1}" : null, TestHomeDocs.Text(_sandbox.AhHome, key));
    }

    private IHierarchyUsage Usage() => HierarchyModule.CreateUsage(PlatformModule.CreateIo(null).Files, _graph.Environment, StateDbModule.CreateUsageRows(_sandbox.AhHome));
}
