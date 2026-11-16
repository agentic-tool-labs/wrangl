using System.Runtime.Versioning;
using Ah.Cli.Composition;
using Ah.Core.Json;
using Ah.Platform;
using Ah.TestKit;
using Ah.Watch;

namespace Ah.Cli.Tests;

/// <summary>
/// The messages lane of the read model over real pool directories, read through the product's own state reader (the one the daemon uses):
/// a forced reconcile of an unchanged tree is timed by the model's own <c>last_pass_ms</c>.
/// </summary>
[UnsupportedOSPlatform("windows")]
public sealed class ReadModelProductReaderBenchTests : IDisposable
{
    private const int Pools = 32;
    private const int PerPool = 38;

    private readonly Sandbox _sandbox = new();

    public void Dispose() => _sandbox.Dispose();

    private string Pool(int pool) => _sandbox.Home + $"/proj{pool:00}/.claude/ah2";

    private static void Age(string path) => File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-1));

    private List<string> Fixture()
    {
        var dirs = new List<string>();
        for (var p = 0; p < Pools; p++)
        {
            var hier = Pool(p);
            var msgs = hier + "/msgs";
            _ = Directory.CreateDirectory(msgs);
            for (var f = 0; f < PerPool; f++)
            {
                var path = $"{msgs}/20261017-1200{p:00}-{f:x4}--implementor--r2--request.md";
                File.WriteAllText(path, $"---\nfrom: orchestrator\ncreated: 2026-10-17T12:00:00Z\n---\nrequest {p}/{f}\n");
                Age(path);
            }

            Age(msgs);
            Age(hier);
            dirs.Add(hier);
        }

        return dirs;
    }

    private static JsonObject Stats(IReadModel model) => WatchModule.ReadModelStats(model)!;

    private static double Stat(IReadModel model, string name) => Stats(model)[name].AsNumber();

    private static async Task<bool> UntilAsync(Func<bool> done, int timeoutMs)
    {
        var until = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < until)
        {
            if (done())
            {
                return true;
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        return done();
    }

    [Fact]
    public async Task AForcedReconcileOfAnUnchangedTreeThroughTheProductReaderIsTimedByLastPassMs()
    {
        var dirs = Fixture();
        var counted = new CountingFiles(PlatformModule.CreateIo(null).Files);
        var io = PlatformModule.CreateIo(null) with { Files = counted };
        var variables = new Dictionary<string, string?> { ["AH_TEST"] = "1", ["AH_HOME"] = _sandbox.AhHome, ["HOME"] = _sandbox.Home };
        var env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables);
        var graph = ServiceGraph.Build(io, env, new FakeRunner(), new FakeDaemonClient());
        using var model = WatchModule.CreateReadModel(
            new StateServiceReader(graph.InternalStateRead),
            () => dirs,
            new ReadModelOptions { MessageIdOf = name => graph.Hierarchy.Core.ParseMsgFilename(name)?.Id });
        WatchModule.StartReadModel(model);

        long Passes() => (long)Stat(model, "full_passes");
        long previous = -1;
        var settled = 0;
        Assert.True(
            await UntilAsync(
                () =>
                {
                    var now = Passes();
                    settled = now == previous && now > 0 ? settled + 1 : 0;
                    previous = now;
                    return settled >= 30;
                },
                120_000),
            "the start seed did not finish");

        var listingsBefore = (long)Stat(model, "listings");
        var times = new List<double>();
        for (var run = 0; run < 4; run++)
        {
            var before = Passes();
            WatchModule.ReconcileReadModel(model, "messages");
            Assert.True(await UntilAsync(() => Passes() > before, 60_000), "the forced reconcile did not drain");
            times.Add(Stat(model, "last_pass_ms"));
        }

        var measured = times.Skip(1).Order().ToArray();
        TestContext.Current.SendDiagnosticMessage(
            $"product reader, {Pools} pools x {PerPool} files, forced unchanged reconcile (last_pass_ms, first run is warm-up and dropped): runs {string.Join(", ", times.Select(t => t.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)))} ms, min {measured[0]:F0} median {measured[1]:F0}; listings +{(long)Stat(model, "listings") - listingsBefore}");

        Assert.Equal(0, Stat(model, "projector_faults"));
    }
}
