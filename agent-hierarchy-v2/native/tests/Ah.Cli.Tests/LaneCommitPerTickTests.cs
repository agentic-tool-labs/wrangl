using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Platform;
using Ah.TestKit;

namespace Ah.Cli.Tests;

/// <summary>b459 step 19: a lane work tree's latest commit is read once per tick and read again by the next tick.</summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class LaneCommitPerTickTests : IDisposable
{
    private readonly Sandbox _sandbox = Sandbox.PlantedFiles();

    public void Dispose() => _sandbox.Dispose();

    [Fact]
    public void ATickReadsAWorkTreesCommitOnceAndNegativeTheNextTickSeesANewCommit()
    {
        var variables = new Dictionary<string, string?> { ["AH_TEST"] = "1", ["AH_HOME"] = _sandbox.AhHome, ["HOME"] = _sandbox.Home };
        var env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables, new PluginInfo(_sandbox.PluginRoot, "wrangl", "1.0.0", "1"));
        var graph = ServiceGraph.Build(PlatformModule.CreateIo(null), env, FakeRunner.KnowingSelf(), new FakeDaemonClient { Reply = _ => new DaemonReply(0, default, "down") });
        var host = new WatchHostAdapter(graph, Ah.Roster.RosterModule.CreateScreenRules(), graph.Io.Clock, WatchHostSeams.Unavailable);
        var reads = 0;
        double? commit = 1000;
        double? Read()
        {
            reads++;
            return commit;
        }

        host.BeginTick(1000);
        Assert.Equal(1000, host.CommitPerTick("/w", Read));
        commit = 2000;
        Assert.Equal(1000, host.CommitPerTick("/w", Read));
        Assert.Equal(1, reads);
        Assert.Equal(2000, host.CommitPerTick("/other", Read));

        host.BeginTick(2000);
        Assert.Equal(2000, host.CommitPerTick("/w", Read));
        Assert.Equal(3, reads);
    }
}
