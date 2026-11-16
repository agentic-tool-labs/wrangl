using Ah.Cli.Composition;
using Ah.Core.Json;
using Ah.Platform;
using Ah.TestKit;

namespace Ah.Cli.Tests;

/// <summary>
/// The mailbox state ops take a caller-supplied box directory, so the daemon's <c>/v1/state</c> route does not serve them: the
/// dedicated routes derive the box from an address, and the in-process writer is the only client of these ops.
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class MailboxStateRouteTests : IDisposable
{
    private readonly Sandbox _sandbox = new();

    public void Dispose() => _sandbox.Dispose();

    private ServiceGraph Build()
    {
        var variables = new Dictionary<string, string?> { ["AH_TEST"] = "1", ["AH_HOME"] = _sandbox.AhHome, ["HOME"] = _sandbox.Home };
        var env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables);
        return ServiceGraph.Build(PlatformModule.CreateIo(null), env, new FakeRunner(), new FakeDaemonClient());
    }

    [Fact]
    public async Task NoMailboxOpIsServedOnTheDaemonRouteAndNothingIsCreatedUnderTheNamedDirectory()
    {
        var graph = Build();
        var ops = graph.Domains.Single(d => d.Name == "mailbox").Ops.Select(o => o.Name).ToList();
        Assert.NotEmpty(ops);
        var target = Path.Combine(_sandbox.Root, "outside-box");
        Directory.CreateDirectory(target);
        var args = JsonValue.FromObject(new JsonObject
        {
            ["dir"] = target,
            ["entry"] = new JsonObject { ["id"] = "e1", ["kind"] = "note" },
            ["id"] = "e1",
            ["ids"] = new JsonArray { "e1" },
            ["session_id"] = "s1",
            ["adapter"] = "x",
        });

        foreach (var op in ops)
        {
            var reply = await graph.State.InvokeAsync(
                "mailbox", op, viaPost: true, args, context: null, opId: $"op-{op}", static (_, work) => Task.FromResult(work()));

            Assert.Equal(400, reply.Status);
            Assert.Contains("is not served by the daemon", System.Text.Encoding.UTF8.GetString(reply.Body.Span), StringComparison.Ordinal);
        }

        Assert.Empty(Directory.GetFileSystemEntries(target));
    }

    [Fact]
    public async Task AnOpTheDomainNeverHadIsNotFound()
    {
        var graph = Build();

        var unknown = await graph.State.InvokeAsync(
            "mailbox", "no-such-op", viaPost: true, JsonValue.FromObject(new JsonObject()), context: null, opId: "op-x", static (_, work) => Task.FromResult(work()));

        Assert.Equal(404, unknown.Status);
    }
}
