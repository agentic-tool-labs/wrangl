using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Core.Json;
using Ah.Platform;
using Ah.TestKit;

namespace Ah.Cli.Tests;

/// <summary>spawn-one returns before its member registers, so a brief can be sent first: it waits in the member's box for the member.</summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class MailBeforeRegistrationTests : IDisposable
{
    private readonly Sandbox _sandbox = Sandbox.PlantedFiles();

    public void Dispose() => _sandbox.Dispose();

    [Fact]
    public void ABriefSentBeforeTheMemberRegistersIsInItsBoxOnceItDoes()
    {
        var hierarchy = Path.Combine(_sandbox.Root, "hier");
        var teamFile = Path.Combine(hierarchy, "teams", "tm.json");
        _sandbox.PlantTeam(
            teamFile,
            "{\"team_id\":\"t-tm\",\"orchestrator\":{\"pid\":1,\"session_id\":\"o\"},\"members\":[{\"role\":\"implementor\",\"name\":\"tm-implementor\",\"kind\":\"claude\",\"transport_id\":\"t7\"}]}\n");
        var variables = new Dictionary<string, string?> { ["AH_TEST"] = "1", ["AH_HOME"] = _sandbox.AhHome, ["HOME"] = _sandbox.Home, ["WRANGL_HIERARCHY_DIR"] = hierarchy };
        var env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables, new PluginInfo(_sandbox.PluginRoot, "wrangl", "1.0.0", "1"));
        var graph = ServiceGraph.Build(PlatformModule.CreateIo(null), env, FakeRunner.KnowingSelf(), new FakeDaemonClient { Reply = _ => new DaemonReply(0, default, "down") });
        var box = graph.Mailbox.BoxDirFor(teamFile, "tm-implementor");

        var sent = graph.Mailbox.Send(new JsonObject
        {
            ["to"] = "tm-implementor@tm",
            ["from"] = "orchestrator@tm",
            ["kind"] = "note",
            ["text"] = "[hierarchy-msg 20261107-000001-aaaa] read it",
            ["id"] = "20261107-000001-aaaa",
            ["cwd"] = _sandbox.Root,
            ["hierarchy_dir"] = hierarchy,
        });
        Assert.True(sent.Failure is null, sent.Failure?.Message);

        var registered = graph.Writer.Invoke(
            "mailbox",
            "register",
            JsonText.ToUtf8(
                new JsonObject
                {
                    ["session_id"] = "s-i",
                    ["boxes"] = new JsonArray { new JsonObject { ["address"] = "tm-implementor@tm", ["dir"] = box } },
                    ["team_file"] = teamFile,
                    ["harness"] = "claude",
                    ["pid"] = Environment.ProcessId,
                    ["pid_start"] = FakeRunner.SelfStart,
                },
                0),
            new RequestContext(_sandbox.Root, _sandbox.Root, string.Empty, string.Empty));
        Assert.True(registered.Ok, registered.Error);

        Assert.Contains(graph.Mailbox.Inbox(box).Entries, e => JsonText.Stringify(e, 0).Contains("20261107-000001-aaaa", StringComparison.Ordinal));
    }
}
