using System.Diagnostics;
using System.Runtime.Versioning;
using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Core.Json;
using Ah.Hierarchy;
using Ah.Platform;
using Ah.TestKit;

namespace Ah.Cli.Tests;

/// <summary>A job that spawns a process hands the daemon loop back at once, so it cannot hold up the other jobs or the watch tick.</summary>
[UnsupportedOSPlatform("windows")]
public sealed class OffLoopJobsTests : IDisposable
{
    private readonly Sandbox _sandbox = new();

    public void Dispose() => _sandbox.Dispose();

    private sealed class Jobs : IJobRegistry
    {
        public Dictionary<string, Func<CancellationToken, Task>> Handlers { get; } = new(StringComparer.Ordinal);

        public void Add(string name, TimeSpan period, Func<CancellationToken, Task> handler) => Handlers[name] = handler;
    }

    private sealed class Routes : IRouteRegistry
    {
        public void Add(string pattern, Func<RouteRequest, CancellationToken, Task<RouteResponse>> handler)
        {
        }
    }

    /// <summary>
    /// Counts and blocks on the adapter's runs only. The liveness reads a registration takes (<c>ps</c>, the boot time) are not the
    /// adapter: <c>ps</c> is answered as <see cref="FakeRunner.KnowingSelf"/> does, anything else at once with no output.
    /// </summary>
    private sealed class BlockingRunner(TimeSpan delay) : IProcessRunner
    {
        public int Runs;

        public ProcessResult Run(ProcessRequest request)
        {
            var argv = request.Argv.ToArray();
            if (argv.Length == 0 || argv[0] != "fake-adapter")
            {
                var ps = argv.Length > 0 && Path.GetFileName(argv[0]) == "ps" ? FakeRunner.SelfOnly(argv) : null;
                return new ProcessResult(0, null, false, false, System.Text.Encoding.UTF8.GetBytes(ps ?? string.Empty), default);
            }

            _ = Interlocked.Increment(ref Runs);
            Thread.Sleep(delay);
            return new ProcessResult(0, null, false, false, default, default);
        }
    }

    [Fact]
    public async Task MemoryForwardReturnsAtOnceWhileTheAdapterRunsAndCompletesWhenItExits()
    {
        var variables = new Dictionary<string, string?> { ["AH_TEST"] = "1", ["AH_HOME"] = _sandbox.AhHome, ["HOME"] = _sandbox.Home };
        var env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables, new PluginInfo(_sandbox.PluginRoot, "wrangl", "1.0.0", "1"));
        var runner = new BlockingRunner(TimeSpan.FromSeconds(2));
        var graph = ServiceGraph.Build(PlatformModule.CreateIo(null), env, runner, new FakeDaemonClient());

        var dir = _sandbox.Home + "/hier";
        _ = Directory.CreateDirectory(dir + "/teams");
        _ = Directory.CreateDirectory(_sandbox.Home + "/.claude/ah2");
        File.WriteAllText(_sandbox.Home + "/.claude/ah2/agent-hierarchy.json", """{"memory":{"adapter":{"command":["fake-adapter"]}}}""");
        var box = _sandbox.Home + "/box";
        _ = Directory.CreateDirectory(box);
        var registered = graph.Writer.Invoke(
            "mailbox",
            "register",
            JsonText.ToUtf8(
                new JsonObject
                {
                    ["session_id"] = "s1",
                    ["pid"] = Environment.ProcessId,
                    ["team_file"] = dir + "/teams/tm.json",
                    ["boxes"] = new JsonArray { new JsonObject { ["address"] = "orchestrator@tm", ["dir"] = box } },
                },
                0),
            new RequestContext(dir, dir, string.Empty, string.Empty));
        Assert.True(registered.Ok, registered.Error);
        Assert.True(graph.Hierarchy.Progress.AppendLedger(dir, "t", new LedgerEvent("decision", Text: "one")).Appended);

        var services = new DaemonServices(new Lazy<ServiceGraph>(graph), env, tmuxTmpdir: Path.Combine(_sandbox.Home, "tmuxtmp"));
        services.RegisterRoutes(new Routes());
        var jobs = new Jobs();
        services.RegisterJobs(jobs);

        var clock = Stopwatch.StartNew();
        var running = jobs.Handlers["memory-forward"](CancellationToken.None);
        var returned = clock.Elapsed;

        Assert.True(returned < TimeSpan.FromMilliseconds(100), $"the job held the caller for {returned.TotalMilliseconds} ms");
        Assert.False(running.IsCompleted);
        await running.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(1, runner.Runs);
    }
}
