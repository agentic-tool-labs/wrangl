using System.Reflection;
using Ah.Abstractions;
using Ah.Platform;
using Ah.Roster;
using Ah.TestKit;

namespace Ah.Config.Tests;

/// <summary><see cref="IConfigCore.ConfigEnabled"/> reads the same layers as <see cref="IConfigCore.ResolveConfig"/> and gives its <c>enabled</c>.</summary>
public sealed class ConfigEnabledTests
{
    private static readonly string PluginRoot = Path.GetFullPath(Path.Combine(typeof(ConfigEnabledTests).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "AhNativeRoot").Value!, ".."));

    [Theory]
    [InlineData(null, null, true)] // no config at all: on
    [InlineData("{\"version\":1,\"enabled\":false}", null, false)]
    [InlineData("{\"version\":1,\"enabled\":false}", "{\"version\":1,\"enabled\":true}", true)] // the project layer comes last and wins
    [InlineData("{\"version\":1,\"enabled\":true}", "{\"version\":1,\"enabled\":false}", false)]
    [InlineData("{\"version\":1,\"enabled\":false}", "{\"version\":1,\"enabled\":\"no\"}", false)] // a layer without a bool decides nothing
    [InlineData("{\"version\":1,\"teamLayout\":\"tabs\"}", null, true)] // a preference-only user file is no layer
    [InlineData("not json", null, true)]
    [InlineData("{\"version\":99,\"enabled\":false}", null, true)] // a version this plugin does not understand is ignored
    public void ItIsTheEnabledOfTheWholeResolveAndNegativeALaterLayerOverridesAnEarlierOne(string? user, string? project, bool expected)
    {
        var sandbox = Directory.CreateTempSubdirectory("ah-cfg-enabled-").FullName;
        try
        {
            var home = Path.Combine(sandbox, "home");
            var repo = Path.Combine(sandbox, "proj");
            Directory.CreateDirectory(Path.Combine(home, ".claude", "ah2"));
            Directory.CreateDirectory(Path.Combine(repo, ".git"));
            Directory.CreateDirectory(Path.Combine(repo, ".claude", "ah2"));
            if (user is not null)
            {
                File.WriteAllText(Path.Combine(home, ".claude", "ah2", "agent-hierarchy.json"), user);
            }

            if (project is not null)
            {
                File.WriteAllText(Path.Combine(repo, ".claude", "ah2", "agent-hierarchy.json"), project);
            }

            var env = new FakeEnvironment(new Dictionary<string, string> { ["HOME"] = home }, new PluginInfo(PluginRoot, "wrangl", "0.0.0", "1"));
            var io = PlatformModule.CreateIo(null);
            IRosterCore roster = null!;
            var config = ConfigModule.CreateCore(io.Files, env, () => roster, new ProcessFacts(repo, 4242));
            roster = RosterModule.CreateCore(io.Files, env, config, io.Processes);

            Assert.Equal(expected, config.ResolveConfig(repo).Get("enabled")!.Value.AsBool());
            Assert.Equal(expected, config.ConfigEnabled(repo));
        }
        finally
        {
            Directory.Delete(sandbox, recursive: true);
        }
    }
}
