using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Core.Json;
using Ah.Platform;
using Ah.TestKit;

namespace Ah.Cli.Tests;

/// <summary>b459 step 22: a lane whose work tree is gone reads as a failed git log, without spawning git.</summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class LaneMissingTreeTests : IDisposable
{
    private readonly Sandbox _sandbox = Sandbox.PlantedFiles();

    public void Dispose() => _sandbox.Dispose();

    [Fact]
    public void AMissingTreeSpawnsNoGitAndNegativeARecreatedTreeIsReadAgain()
    {
        var variables = new Dictionary<string, string?> { ["AH_TEST"] = "1", ["AH_HOME"] = _sandbox.AhHome, ["HOME"] = _sandbox.Home };
        var env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables, new PluginInfo(_sandbox.PluginRoot, "wrangl", "1.0.0", "1"));
        var runner = FakeRunner.KnowingSelf();
        runner.Output = argv => argv.Contains("log") ? "1700000000\n" : string.Empty;
        var graph = ServiceGraph.Build(PlatformModule.CreateIo(null), env, runner, new FakeDaemonClient { Reply = _ => new DaemonReply(0, default, "down") });
        var tree = Path.Combine(_sandbox.Home, "lane-tree");
        var child = JsonValue.FromObject(new JsonObject { ["streams"] = new JsonObject { ["ws"] = new JsonObject { ["worktree"] = tree } } });
        var hdir = Path.Combine(_sandbox.Home, "proj", ".claude", "ah2");
        int Gits() => runner.Calls.Count(static c => c.Contains("log"));

        Assert.Null(LaneActivity.LatestMs(graph, hdir, "child", child, 0));
        Assert.Equal(0, Gits());

        // the tree is created again: the next read runs git and sees its commit
        Directory.CreateDirectory(tree);
        Assert.Equal(1700000000000d, LaneActivity.LatestMs(graph, hdir, "child", child, 0));
        Assert.Equal(1, Gits());
    }
}
