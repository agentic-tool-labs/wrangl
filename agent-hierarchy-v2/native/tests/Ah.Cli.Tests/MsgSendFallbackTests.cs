using System.Globalization;
using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Cli.Verbs.Msg;
using Ah.Platform;
using Ah.TestKit;

namespace Ah.Cli.Tests;

/// <summary>
/// What <c>msg send</c> does when the daemon does not answer: the files take the send, the fallback is announced and recorded with
/// the reason the daemon client gave.
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class MsgSendFallbackTests : IDisposable
{
    private readonly Sandbox _sandbox = new();

    public void Dispose() => _sandbox.Dispose();

    private (int Exit, string Out, string Err, List<(string Verb, string Reason)> Fallbacks) Send(DaemonReply reply)
    {
        var hierarchy = Path.Combine(_sandbox.Root, "hier");
        var teamFile = Path.Combine(hierarchy, "teams", "tm.json");
        _sandbox.PlantTeam(
            teamFile,
            "{\"team_id\":\"t-tm\",\"orchestrator\":{\"pid\":" + System.Environment.ProcessId.ToString(CultureInfo.InvariantCulture)
            + ",\"session_id\":\"o\"},\"members\":[{\"role\":\"implementor\",\"name\":\"tm-implementor\",\"kind\":\"claude\",\"transport_id\":\"p1\"}]}\n");

        var variables = new Dictionary<string, string?>
        {
            ["AH_TEST"] = "1",
            ["AH_HOME"] = _sandbox.AhHome,
            ["HOME"] = _sandbox.Home,
            ["WRANGL_HIERARCHY_DIR"] = hierarchy,
        };
        var env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables, new PluginInfo(_sandbox.PluginRoot, "wrangl", "1.0.0", "1"));
        var io = PlatformModule.CreateIo(null);
        var daemon = new FakeDaemonClient { Reply = _ => reply };
        var graph = ServiceGraph.Build(io, env, new FakeRunner(), daemon);
        var context = new CliContext(env, new RecordingExec(), new FakeRunner(), "ahd", _sandbox.Executable("install/ahd"), _sandbox.Home, 501, null, new Lazy<ServiceGraph>(graph));
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = MsgVerb.Run(
            context,
            ["send", "tm-implementor@tm", "--text", "hello", "--id", "e-fallback", "--json", "--cwd", _sandbox.Root],
            stdout,
            stderr);
        return (exit, stdout.ToString(), stderr.ToString(), daemon.Fallbacks);
    }

    private static DaemonReply TimedOut(int sentMsAgo) =>
        new(0, default, "timeout", TimedOut: true, SentAt: DateTimeOffset.UtcNow.AddMilliseconds(-sentMsAgo));

    [Fact]
    public void ADaemonThatIsDownLetsTheFilesTakeTheSendAndSaysSoOnce()
    {
        var (exit, stdout, stderr, fallbacks) = Send(new DaemonReply(0, default, "down"));

        Assert.Equal(0, exit);
        Assert.Contains("\"id\":\"e-fallback\"", stdout, StringComparison.Ordinal);
        Assert.Equal("ah msg: warning: ahd unavailable (down); using files\n", stderr);
        Assert.Equal([("send", "down")], fallbacks);
    }

    [Fact]
    public void ARequestThatTimedOutIsAnnouncedAsATimeout()
    {
        var (exit, _, stderr, fallbacks) = Send(TimedOut(sentMsAgo: 10));

        Assert.Equal(0, exit);
        Assert.Equal("ah msg: warning: ahd unavailable (timeout); using files\n", stderr);
        Assert.Equal([("send", "timeout")], fallbacks);
    }

    [Fact]
    public void AnHttpFallbackReasonIsKeptVerbatim()
    {
        var (_, _, stderr, fallbacks) = Send(new DaemonReply(0, default, "http 503 busy"));

        Assert.Equal("ah msg: warning: ahd unavailable (http 503 busy); using files\n", stderr);
        Assert.Equal([("send", "http 503 busy")], fallbacks);
    }
}
