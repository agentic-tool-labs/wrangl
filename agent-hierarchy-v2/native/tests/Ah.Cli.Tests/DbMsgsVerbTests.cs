using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Cli.Verbs.Db;
using Ah.Platform;
using Ah.StateDb;
using Ah.TestKit;

namespace Ah.Cli.Tests;

/// <summary>The one msgs verb left, <c>ah db test-seed msgs</c>, and the graph the import tests build over a sandbox.</summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class DbMsgsVerbTests : IDisposable
{
    private readonly Sandbox _sandbox = Sandbox.NotCutOver();

    public void Dispose() => _sandbox.Dispose();

    internal static ServiceGraph GraphOf(Sandbox sandbox)
    {
        var variables = new Dictionary<string, string?> { ["AH_TEST"] = "1", ["AH_HOME"] = sandbox.AhHome, ["HOME"] = sandbox.Home };
        var env = new FakeEnv(sandbox.Home, sandbox.AhHome, variables, new PluginInfo(sandbox.PluginRoot, "wrangl", "1.0.0", "1"));
        return ServiceGraph.Build(PlatformModule.CreateIo(null), env, new FakeRunner(), new FakeDaemonClient());
    }

    private (int Code, string Err) Seed(bool testMode)
    {
        var graph = GraphOf(_sandbox);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var pool = Directory.CreateDirectory(Path.Combine(_sandbox.Root, "proj", ".claude", "ah2")).FullName;
        var code = MsgsVerb.Run(_sandbox.AhHome, graph.Hierarchy.Core, graph.Hierarchy.Messages, graph.Roster, graph.Io.Locks, StateDbModule.CreateMsgsCutover(), DateTimeOffset.UtcNow, ["test-seed", "msgs", "--pool", pool], stdout, stderr, testMode);
        return (code, stderr.ToString());
    }

    [Fact]
    public void TheSeedIsAUsageErrorOutsideTestMode()
    {
        var (code, err) = Seed(testMode: false);

        Assert.Equal(2, code);
        Assert.Contains("usage", err, StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeTheSeedRunsInTestMode()
    {
        var (code, err) = Seed(testMode: true);

        Assert.True(code == 0, err);
    }
}
