using System.Text;
using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Cli.Verbs.Msg;
using Ah.Core.Json;
using Ah.Platform;
using Ah.TestKit;

namespace Ah.Cli.Tests;

/// <summary><c>msg due --json</c>: the Claude mod's mail poll as one call, through the daemon when it answers and the files when it does not.</summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class MsgDueTests : IDisposable
{
    private const string Session = "s1";

    private readonly Sandbox _sandbox = new();

    public void Dispose() => _sandbox.Dispose();

    private string Box => Path.Combine(_sandbox.AhHome, "mailbox", "t~h", "imp");

    /// <summary>The session's registration and its box's entries, through the real <c>mailbox</c> ops.</summary>
    private void Seed(params string[] ids)
    {
        var variables = new Dictionary<string, string?> { ["AH_TEST"] = "1", ["AH_HOME"] = _sandbox.AhHome, ["HOME"] = _sandbox.Home, ["AH_DAEMON"] = "off" };
        var env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables, new PluginInfo(_sandbox.PluginRoot, "wrangl", "1.0.0", "1"));
        var graph = ServiceGraph.Build(PlatformModule.CreateIo(null), env, new FakeRunner(), new FakeDaemonClient { Reply = _ => new DaemonReply(0, default, "down") });
        Directory.CreateDirectory(Box);
        var registration = new JsonObject
        {
            ["session_id"] = Session,
            ["boxes"] = new JsonArray { new JsonObject { ["address"] = "imp@tm", ["dir"] = Box } },
        };
        Assert.True(graph.Writer.Invoke("mailbox", "register", JsonText.ToUtf8(registration, 0), new RequestContext(_sandbox.Root, _sandbox.Root, string.Empty, string.Empty)).Ok);
        foreach (var id in ids)
        {
            TestPlant.Mail(graph.Writer, Box, JsonText.Parse("{\"id\":\"" + id + "\",\"kind\":\"note\",\"from\":\"user\",\"text\":\"hi\",\"route\":\"pull\"}"), _sandbox.Root);
        }
    }

    private static string Input(string? hierarchyDir = null, string state = "null") =>
        "{\"session\":\"" + Session + "\",\"cwd\":\"/work\",\"hierarchy_dir\":" + (hierarchyDir is null ? "null" : "\"" + hierarchyDir + "\"")
        + ",\"now\":1790000000000,\"busy_at\":null,\"poll_ms\":5000,\"handed\":{},\"state\":" + state + "}";

    private (int Exit, string Out, string Err, FakeDaemonClient Daemon) Run(string stdin, string[] args, FakeDaemonClient? daemon = null, bool daemonOff = false)
    {
        var variables = new Dictionary<string, string?> { ["AH_TEST"] = "1", ["AH_HOME"] = _sandbox.AhHome, ["HOME"] = _sandbox.Home };
        if (daemonOff)
        {
            variables["AH_DAEMON"] = "off";
        }

        var env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables, new PluginInfo(_sandbox.PluginRoot, "wrangl", "1.0.0", "1"));
        var io = PlatformModule.CreateIo(null);
        daemon ??= new FakeDaemonClient { Reply = _ => new DaemonReply(0, default, "down") };
        var graph = ServiceGraph.Build(io, env, new FakeRunner(), daemon);
        var context = new CliContext(
            env, new RecordingExec(), new FakeRunner(), "ahd", _sandbox.Executable("install/ahd"), _sandbox.Home, 501,
            () => Encoding.UTF8.GetBytes(stdin), new Lazy<ServiceGraph>(graph));
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = MsgVerb.Run(context, ["due", .. args], stdout, stderr);
        return (exit, stdout.ToString(), stderr.ToString(), daemon);
    }

    private static FakeDaemonClient Daemon(Func<DaemonRequest, DaemonReply> reply) =>
        new() { Info = new DaemonInfo(1, 4242, "9.9.9", 1, "/run/ahd.sock", "/root", "2026-10-12T00:00:00.000Z"), Reply = reply };

    private static JsonObject Parse(string text) => JsonText.Parse(text.TrimEnd('\n')).AsObject();

    [Fact]
    public void WithoutTheDaemonTheFilesAnswerAndNoDaemonIsReported()
    {
        Seed("e1");

        var r = Run(Input(), ["--json"], daemonOff: true);

        Assert.Equal(0, r.Exit);
        Assert.Equal(string.Empty, r.Err);
        var a = Parse(r.Out);
        Assert.Equal(["v", "ids", "prompt", "compact", "idle", "state", "acked", "daemon"], a.Keys);
        Assert.Equal("ah mail for imp@tm (1):\n- note from user\n[ah-entry e1]", a["compact"].AsString());
        Assert.Equal("e1", a["ids"].AsArray()[0].AsString());
        Assert.True(a["daemon"].IsNull);
        Assert.Empty(r.Daemon.Requests);
        Assert.Equal(0, r.Daemon.DiscoverCalls);
    }

    [Fact]
    public void ABriefIsMailedWithItsRequestIdAndReadCommandAndNegativeAReportStaysCompact()
    {
        Seed();
        const string key = "/nowhere/.claude/ah2/msgs/20261117-154321-14ze--implementor--brief-mail--request.md";
        var writer = PlantWriter();
        TestPlant.Mail(writer, Box, JsonText.Parse("{\"id\":\"e-brief\",\"kind\":\"brief\",\"from\":\"orchestrator@tm\",\"ref\":\"" + key + "\",\"route\":\"pull\"}"), _sandbox.Root);
        TestPlant.Mail(writer, Box, JsonText.Parse("{\"id\":\"e-report\",\"kind\":\"report\",\"from\":\"reviewer@tm\",\"ref\":\"" + key + "\",\"route\":\"pull\"}"), _sandbox.Root);

        var a = Parse(Run(Input(), ["--json"], daemonOff: true).Out);

        var compact = a["compact"].AsString();
        Assert.Contains("- brief from orchestrator@tm: [hierarchy-msg 20261117-154321-14ze] read it with `ah msg show 20261117-154321-14ze`", compact, StringComparison.Ordinal);
        Assert.DoesNotContain("/msgs/", compact, StringComparison.Ordinal);
        Assert.DoesNotContain("/nowhere", a["prompt"].AsString(), StringComparison.Ordinal);
        Assert.Contains("\n- report from reviewer@tm\n", compact, StringComparison.Ordinal);
    }

    private IStateWriter PlantWriter()
    {
        var variables = new Dictionary<string, string?> { ["AH_TEST"] = "1", ["AH_HOME"] = _sandbox.AhHome, ["HOME"] = _sandbox.Home, ["AH_DAEMON"] = "off" };
        var env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables, new PluginInfo(_sandbox.PluginRoot, "wrangl", "1.0.0", "1"));
        return ServiceGraph.Build(PlatformModule.CreateIo(null), env, new FakeRunner(), new FakeDaemonClient { Reply = _ => new DaemonReply(0, default, "down") }).Writer;
    }

    [Fact]
    public void ADaemonThatServesTheDueRouteIsReportedByItsSocket()
    {
        Seed("e1");
        var daemon = Daemon(q => q.PathAndQuery.StartsWith("/v1/due", StringComparison.Ordinal) ? new DaemonReply(400, default) : new DaemonReply(0, default, "down"));

        var r = Run(Input(), ["--json"], daemon);

        Assert.Equal("/run/ahd.sock", Parse(r.Out)["daemon"].AsObject()["socket"].AsString());
        Assert.All(r.Daemon.Requests, q => Assert.False(q.StartIfDown));
    }

    [Fact]
    public void ADaemonThatDoesNotKnowTheDueRouteIsNotReported()
    {
        Seed("e1");
        var daemon = Daemon(_ => new DaemonReply(404, default));

        var r = Run(Input(), ["--json"], daemon);

        Assert.True(Parse(r.Out)["daemon"].IsNull);
    }

    [Fact]
    public void NoDaemonIsReportedWhenNoneIsUp()
    {
        Seed("e1");
        var daemon = new FakeDaemonClient { Info = null, Reply = _ => new DaemonReply(400, default) };

        var r = Run(Input(), ["--json"], daemon);

        Assert.True(Parse(r.Out)["daemon"].IsNull);
    }

    [Fact]
    public void WithAHierarchyDirTheBoxesAreReadThroughTheDaemonsInboxWithTheModsQuery()
    {
        Seed();
        var daemon = Daemon(q => q.PathAndQuery.StartsWith("/v1/inbox", StringComparison.Ordinal)
            ? new DaemonReply(200, Encoding.UTF8.GetBytes("{\"address\":\"imp@tm\",\"entries\":[{\"id\":\"d1\",\"kind\":\"note\",\"from\":\"user\",\"text\":\"hi\",\"route\":\"pull\"}],\"next\":77}"))
            : new DaemonReply(404, default));

        var r = Run(Input("/h"), ["--json"], daemon);

        var a = Parse(r.Out);
        Assert.Equal("d1", a["ids"].AsArray()[0].AsString());
        var inbox = Assert.Single(r.Daemon.Requests, q => q.PathAndQuery.StartsWith("/v1/inbox", StringComparison.Ordinal));
        Assert.Equal(
            "/v1/inbox?address=imp%40tm&adapter=claude-mod&session_id=s1&poll_ms=5000&since=0&wait=0&cwd=%2Fwork&hierarchy_dir=%2Fh",
            inbox.PathAndQuery);
        Assert.Equal(TimeSpan.FromSeconds(2), inbox.Timeout);
        Assert.False(inbox.StartIfDown);
        Assert.Equal("{\"imp@tm\":[0,0]}", a["state"].AsString());
    }

    [Fact]
    public void WithNoHierarchyDirTheDaemonInboxIsNotAsked()
    {
        Seed("e1");
        var daemon = Daemon(_ => new DaemonReply(404, default));

        var r = Run(Input(), ["--json"], daemon);

        Assert.DoesNotContain(r.Daemon.Requests, q => q.PathAndQuery.StartsWith("/v1/inbox", StringComparison.Ordinal));
        Assert.Equal("e1", Parse(r.Out)["ids"].AsArray()[0].AsString());
    }

    [Fact]
    public void ADaemonThatAnswersNothingUsableLetsTheFilesDecide()
    {
        Seed("e1");
        var daemon = Daemon(q => q.PathAndQuery.StartsWith("/v1/inbox", StringComparison.Ordinal) ? new DaemonReply(500, default) : new DaemonReply(404, default));

        var r = Run(Input("/h"), ["--json"], daemon);

        Assert.Equal("e1", Parse(r.Out)["ids"].AsArray()[0].AsString());
    }

    [Theory]
    [InlineData("--plain", "ah msg: due requires --json")]
    public void WithoutJsonItIsAUsageError(string flag, string message)
    {
        var r = Run(Input(), [flag]);

        Assert.Equal(2, r.Exit);
        Assert.Equal(string.Empty, r.Out);
        Assert.Equal(message + "\n", r.Err);
    }

    [Theory]
    [InlineData("not json", "ah msg: due: stdin is not JSON\n")]
    [InlineData("[]", "ah msg: due: in must be a JSON object\n")]
    [InlineData("{\"now\":1}", "ah msg: due: session must be a non-empty string\n")]
    public void AnInputThatIsNotARequestIsAUsageErrorNamingWhy(string stdin, string message)
    {
        var r = Run(stdin, ["--json"]);

        Assert.Equal(2, r.Exit);
        Assert.Equal(string.Empty, r.Out);
        Assert.Equal(message, r.Err);
    }

    [Fact]
    public void AStateCarriedBackGivesTheSameAnswer()
    {
        Seed("e1");

        var first = Parse(Run(Input(), ["--json"], daemonOff: true).Out);
        var again = Parse(Run(Input(state: JsonText.Stringify(first["state"], 0)), ["--json"], daemonOff: true).Out);

        Assert.Equal(first["state"].AsString(), again["state"].AsString());
        Assert.Equal(first["ids"].AsArray()[0].AsString(), again["ids"].AsArray()[0].AsString());
    }
}
