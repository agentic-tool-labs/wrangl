using System.Runtime.CompilerServices;
using Ah.Abstractions;
using Ah.Config;
using Ah.Core.Json;
using Ah.Mesh;
using Ah.Platform;
using Ah.TestKit;

namespace Ah.Daemon.Tests;

/// <summary>The mesh routes as a client sees them: the participation contract's two renderings, byte for byte Node's.</summary>
public sealed class MeshRouteTests
{
    private static string Here([CallerFilePath] string here = "") => here;

    private static string PluginRoot() => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Here())!, "..", "..", ".."));

    private static JsonObject Goldens() =>
        JsonText.Parse(File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(Here())!, "..", "Ah.Mesh.Tests", "Fixtures", "contract-goldens.json"))).AsObject();

    private static JsonArray RoleGoldens() =>
        JsonText.Parse(Ah.TestKit.RecordedNamespace.ReadAllBytes(Path.Combine(Path.GetDirectoryName(Here())!, "..", "Ah.Config.Tests", "Fixtures", "contract-text-goldens.json")))
            .AsObject()["roles"].AsArray();

    private static Task<Harness> Start(Sandbox box)
    {
        var root = PluginRoot();
        var env = new FakeEnvironment(new Dictionary<string, string>(), new PluginInfo(root, "wrangl", "0.0.0", "1"));
        var files = PlatformModule.CreateIo(null).Files;
        var config = ConfigModule.CreateCore(files, env, () => throw new InvalidOperationException("no roster"), new ProcessFacts("/", 1));
        var contract = MeshModule.CreateContract(files, env);
        var roles = MeshModule.CreateContractRoles(config, ConfigModule.CreateRoleContracts(config, files, env));
        return Harness.StartAsync(box, o =>
        {
            o.PluginRoot = root;
            o.RegisterRoutes = routes => MeshModule.RegisterContractRoutes(routes, contract, roles);
        });
    }

    [Fact]
    public async Task TheContractDocumentIsServedAsNodeRendersIt()
    {
        var box = Sandbox.Create();
        await using var daemon = await Start(box);

        var reply = await daemon.GetAsync("/v1/contract");

        Assert.Equal(200, reply.Status);
        Assert.Equal("application/json", reply.Headers["Content-Type"]);
        Assert.Equal(Goldens()["json"].AsString(), Ah.TestKit.RecordedCliForm.Map(reply.Body));
    }

    [Fact]
    public async Task TheGuideIsServedAsMarkdownAsNodeRendersIt()
    {
        var box = Sandbox.Create();
        await using var daemon = await Start(box);

        var reply = await daemon.GetAsync("/v1/contract/guide");

        Assert.Equal(200, reply.Status);
        Assert.Equal("text/markdown; charset=utf-8", reply.Headers["Content-Type"]);
        Assert.Equal(Goldens()["guide"].AsString(), Ah.TestKit.RecordedCliForm.Map(reply.Body));
    }

    [Fact]
    public async Task TheContractRoutesAreReadOnlyAndUnknownPathsAreNotFound()
    {
        var box = Sandbox.Create();
        await using var daemon = await Start(box);

        var post = await daemon.PostAsync("/v1/contract", "{}");
        var missing = await daemon.GetAsync("/v1/contract/nothing");

        Assert.Equal(405, post.Status);
        Assert.Equal("GET", post.Headers["Allow"]);
        Assert.Equal(404, missing.Status);
        Assert.Equal("not_found", missing.Json["error"].AsObject()["code"].AsString());
    }

    [Fact]
    public async Task ARoleContractIsServedAsNodeAssemblesItAndNothingElseResolves()
    {
        var box = Sandbox.Create();
        await using var daemon = await Start(box);
        var root = PluginRoot();

        foreach (var item in RoleGoldens())
        {
            var want = item.AsObject();
            var role = want["role"].AsString();

            var reply = await daemon.GetAsync("/v1/contract/role?role=" + Uri.EscapeDataString(role));

            if (want["result"].IsMap)
            {
                Assert.True(reply.Status == 200, role);
                // The harness reads the socket as Latin-1; the daemon's bytes are UTF-8.
                var served = JsonText.Parse(reply.Body.Select(c => (byte)c).ToArray()).AsObject();
                Assert.Equal(role, served["role"].AsString());
                Assert.Equal(want["result"].AsObject()["text"].AsString(), Ah.TestKit.RecordedCliForm.Map(served["text"].AsString().Replace(root, "<PLUGIN>", StringComparison.Ordinal)));
            }
            else
            {
                Assert.True(reply.Status == 404, role);
                Assert.Equal("not_found", reply.Json["error"].AsObject()["code"].AsString());
                Assert.Equal(
                    $"no behavioural contract for role {JsonText.Stringify(role, 0)}",
                    reply.Json["error"].AsObject()["message"].AsString());
            }
        }
    }

    [Fact]
    public async Task ARoleQueryWithoutARoleIsNotFoundNamingNull()
    {
        var box = Sandbox.Create();
        await using var daemon = await Start(box);

        var reply = await daemon.GetAsync("/v1/contract/role");

        Assert.Equal(404, reply.Status);
        Assert.Equal("no behavioural contract for role null", reply.Json["error"].AsObject()["message"].AsString());
    }
}
