using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Core.Json;
using Ah.Platform;
using Ah.TestKit;

namespace Ah.Cli.Tests;

/// <summary>The tick's peer records are read once per version of the peer log (b459): shared while nothing is written, read again after a write.</summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class PeerRecordsPerTickTests : IDisposable
{
    private readonly Sandbox _sandbox = Sandbox.PlantedFiles();

    public void Dispose() => _sandbox.Dispose();

    [Fact]
    public void ATickSharesOneReadAndNegativeAWriteDuringTheTickIsSeen()
    {
        var variables = new Dictionary<string, string?> { ["AH_TEST"] = "1", ["AH_HOME"] = _sandbox.AhHome, ["HOME"] = _sandbox.Home };
        var env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables, new PluginInfo(_sandbox.PluginRoot, "wrangl", "1.0.0", "1"));
        var graph = ServiceGraph.Build(PlatformModule.CreateIo(null), env, FakeRunner.KnowingSelf(), new FakeDaemonClient { Reply = _ => new DaemonReply(0, default, "down") });
        var host = new WatchHostAdapter(graph, Ah.Roster.RosterModule.CreateScreenRules(), graph.Io.Clock, WatchHostSeams.Unavailable);
        void Heard(string session) => graph.Hierarchy.Peers.RecordPeerRow(JsonValue.FromObject(new JsonObject { ["type"] = "heard", ["session_id"] = session, ["ts"] = "2026-11-11T00:00:00.000Z" }));

        _sandbox.Mirrored(() =>
        {
            Heard("s1");
            host.BeginTick(1000);
            var first = host.PeerRecords();

            Assert.Same(first, host.PeerRecords());

            Heard("s2");
            var after = host.PeerRecords();
            Assert.NotSame(first, after);
            Assert.Contains(after, r => r.Get("session_id") is { IsText: true } s && s.AsString() == "s2");
            return 0;
        });
    }
}
