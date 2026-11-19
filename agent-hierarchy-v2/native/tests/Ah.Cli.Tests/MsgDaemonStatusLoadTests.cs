using System.Text;
using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Cli.Verbs.Msg;
using Ah.Commands;
using Ah.Core.Json;
using Ah.Platform;
using Ah.TestKit;

namespace Ah.Cli.Tests;

/// <summary>The machine load in <c>msg daemon status</c>, and the spawn verbs' route to the daemon's pacing.</summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class MsgDaemonStatusLoadTests : IDisposable
{
    private readonly Sandbox _sandbox = new();

    public void Dispose() => _sandbox.Dispose();

    private const string Health =
        "{\"pid\":7,\"version\":\"1\",\"root\":\"/r\",\"started_at\":\"2026-10-25T00:00:00.000Z\",\"owns\":{\"mailbox\":true,\"status\":true,\"timers\":[]},"
        + "\"ladder\":{\"pending\":0},\"timer_state\":{},\"load\":{\"state\":\"overloaded\",\"since\":\"2026-10-25T01:00:00.000Z\",\"triggers\":[\"cpu busy 0.94 for 180 s (threshold 0.90)\"],"
        + "\"cpu_busy\":0.94,\"load1_per_cpu\":1.9,\"cpus\":10,\"mem_pressure\":\"warn\",\"harnesses\":7,\"api_errors_10m\":{\"transport\":1,\"rate_limit\":0,\"server\":0,\"other\":0,\"total\":1,\"sessions\":1},"
        + "\"thresholds\":{\"cpu_busy\":0.9},\"problem\":\"ahd-load.json: ticks must be a whole number of at least 1; using the defaults\"}}";

    private ServiceGraph Graph(FakeDaemonClient daemon)
    {
        var variables = new Dictionary<string, string?> { ["AH_TEST"] = "1", ["AH_HOME"] = _sandbox.AhHome, ["HOME"] = _sandbox.Home };
        var env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables, new PluginInfo(_sandbox.PluginRoot, "wrangl", "1.0.0", "1"));
        return ServiceGraph.Build(PlatformModule.CreateIo(null), env, new FakeRunner(), daemon);
    }

    private (int Exit, string Out) Status(FakeDaemonClient daemon, params string[] flags)
    {
        var graph = Graph(daemon);
        var variables = new Dictionary<string, string?> { ["AH_TEST"] = "1", ["AH_HOME"] = _sandbox.AhHome, ["HOME"] = _sandbox.Home };
        var env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables, new PluginInfo(_sandbox.PluginRoot, "wrangl", "1.0.0", "1"));
        var context = new CliContext(
            env, new RecordingExec(), new FakeRunner(), "ahd", _sandbox.Executable("install/ahd"), _sandbox.Home, 501,
            () => Encoding.UTF8.GetBytes(string.Empty), new Lazy<ServiceGraph>(graph));
        var stdout = new StringWriter();
        var exit = MsgVerb.Run(context, ["daemon", "status", .. flags], stdout, new StringWriter());
        return (exit, stdout.ToString());
    }

    private static FakeDaemonClient Up() => new() { Reply = r => r.PathAndQuery == "/v1/health" ? new DaemonReply(200, Encoding.UTF8.GetBytes(Health)) : new DaemonReply(200, default) };

    [Fact]
    public void AnUpDaemonsStatusCarriesTheLoadObjectWithEveryKeyInJsonAndALineInText()
    {
        var (_, json) = Status(Up(), "--json");
        var load = JsonText.Parse(json).Member("load")!.Value;

        foreach (var key in new[] { "state", "since", "triggers", "cpu_busy", "load1_per_cpu", "cpus", "mem_pressure", "harnesses", "api_errors_10m", "thresholds", "problem" })
        {
            Assert.True(load.Member(key) is not null, key);
        }

        var (_, text) = Status(Up());
        Assert.Contains("load: overloaded since 2026-10-25T01:00:00.000Z; cpu_busy 0.94 (threshold 0.9), load1_per_cpu 1.9 of 10 cpus", text, StringComparison.Ordinal);
        Assert.Contains("problem: ahd-load.json", text, StringComparison.Ordinal);
    }

    [Fact]
    public void APlatformWithoutCpuTimesSaysSoInTheTextLine()
    {
        var health = Health.Replace("\"cpu_busy\":0.94,", "\"cpu_busy\":null,\"cpu_note\":\"cpu use not read on this platform\",", StringComparison.Ordinal);
        var daemon = new FakeDaemonClient { Reply = r => r.PathAndQuery == "/v1/health" ? new DaemonReply(200, Encoding.UTF8.GetBytes(health)) : new DaemonReply(200, default) };

        var text = Status(daemon).Out;

        Assert.Contains("; cpu use not read on this platform, load1_per_cpu 1.9 of 10 cpus", text, StringComparison.Ordinal);
        Assert.DoesNotContain("cpu_busy", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(100, 1000, 150, 1100, 0.5)]
    [InlineData(100, 1000, 200, 1100, 1.0)]
    [InlineData(100, 1000, 100, 1100, 0.0)]
    [InlineData(100, 1000, 150, 1000, null)]
    [InlineData(100, 1000, 90, 1100, null)]
    [InlineData(100, 1000, 220, 1100, null)]
    public void TheCpuShareIsNullWhenAnyCounterWentBackwards(long busy0, long total0, long busy1, long total1, double? expected) =>
        Assert.Equal(expected, LoadSource.BusyShare((busy0, total0), (busy1, total1)));

    [Fact]
    public void ADownDaemonsStatusShowsLoadNull()
    {
        var (_, json) = Status(new FakeDaemonClient { Reply = _ => new DaemonReply(0, default, "down") }, "--json");

        Assert.True(JsonText.Parse(json).Member("load")!.Value.IsNull);
    }

    [Fact]
    public void AnUpDaemonWithoutALoadObjectShowsNullAndUnknown()
    {
        var bare = new FakeDaemonClient { Reply = r => r.PathAndQuery == "/v1/health" ? new DaemonReply(200, Encoding.UTF8.GetBytes("{\"pid\":7,\"load\":null}")) : new DaemonReply(200, default) };

        Assert.True(JsonText.Parse(Status(bare, "--json").Out).Member("load")!.Value.IsNull);
        Assert.Contains("load: unknown", Status(bare).Out, StringComparison.Ordinal);
    }

    private SpawnAdmissionReply Ask(Func<DaemonRequest, DaemonReply> reply, out FakeDaemonClient daemon)
    {
        daemon = new FakeDaemonClient { Reply = reply };
        return new SpawnAdmissionComposition(Graph(daemon)).Ask("t", "m", dryRun: true);
    }

    [Fact]
    public void TheAdmissionClientReadsAnAnswerAndFailsOpenWhenThereIsNone()
    {
        var refused = Ask(_ => new DaemonReply(200, Encoding.UTF8.GetBytes("{\"admitted\":false,\"next_at\":\"2026-10-25T02:00:00.000Z\",\"triggers\":[\"a\",\"b\"]}")), out var daemon);
        Assert.False(refused.Admitted);
        Assert.Equal("2026-10-25T02:00:00.000Z", refused.NextAt);
        Assert.Equal(["a", "b"], refused.Triggers);
        Assert.Null(refused.Unknown);
        var sent = daemon.Requests.Single();
        Assert.Equal("/v1/load/admit-spawn", sent.PathAndQuery);
        Assert.Contains("\"dry_run\":true", Encoding.UTF8.GetString(sent.Body.Span), StringComparison.Ordinal);

        Assert.NotNull(Ask(_ => new DaemonReply(0, default, "down"), out _).Unknown);
        Assert.NotNull(Ask(_ => new DaemonReply(500, default), out _).Unknown);
        Assert.NotNull(Ask(_ => new DaemonReply(200, Encoding.UTF8.GetBytes("not json")), out _).Unknown);
        Assert.All(new[] { Ask(_ => new DaemonReply(0, default, "down"), out _), Ask(_ => new DaemonReply(500, default), out _) }, r => Assert.True(r.Admitted));
    }
}
