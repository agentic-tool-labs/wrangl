using System.Reflection;
using Ah.Abstractions;
using Ah.Platform;
using Ah.Roster;
using Ah.TestKit;

namespace Ah.Config.Tests;

/// <summary>
/// The resolver's <c>qaCadence</c> (ah2-154 D1, Amendment 2): read from <c>qa.cadence</c> inside the <c>qa</c> map the QA scheduler also reads; the most
/// specific config with the key wins; an unknown value or a string <c>qa</c> is reported and is not configured.
/// </summary>
public sealed class ConfigQaTests
{
    private static readonly string PluginRoot = Path.GetFullPath(Path.Combine(typeof(ConfigQaTests).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "AhNativeRoot").Value!, ".."));

    [Theory]
    [InlineData(null, null, null, null, false)] // no config: not configured
    [InlineData("{\"version\":1}", "{\"version\":1}", null, null, false)] // configs without the key: not configured
    [InlineData("{\"version\":1,\"qa\":{\"cadence\":\"batch\"}}", null, "batch", "user", false)]
    [InlineData("{\"version\":1,\"qa\":{\"cadence\":\"item\"}}", "{\"version\":1,\"qa\":{\"cadence\":\"batch\"}}", "batch", "project", false)] // the project layer wins
    [InlineData("{\"version\":1,\"qa\":{\"cadence\":\"batch\"}}", "{\"version\":1}", "batch", "user", false)] // a layer without the key decides nothing
    [InlineData(null, "{\"version\":1,\"qa\":{\"parallel\":3,\"admit_load_ratio\":0.5,\"cadence\":\"batch\"}}", "batch", "project", false)] // beside the scheduler keys
    [InlineData("{\"version\":1,\"qa\":{\"cadence\":\"batch\"}}", "{\"version\":1,\"qa\":{\"parallel\":3}}", "batch", "user", false)] // negative: a qa map with no cadence decides nothing
    [InlineData(null, "{\"version\":1,\"qa\":{\"parallel\":3}}", null, null, false)] // negative: scheduler keys alone are not configured
    [InlineData(null, "{\"version\":1,\"qa\":{\"cadence\":\"nightly\"}}", null, null, true)] // negative: an unknown value is not configured
    [InlineData("{\"version\":1,\"qa\":{\"cadence\":\"batch\"}}", "{\"version\":1,\"qa\":{\"cadence\":\"Batch\"}}", null, null, true)] // negative: case matters, and the winning unknown value does not fall back
    [InlineData(null, "{\"version\":1,\"qa\":{\"cadence\":true}}", null, null, false)] // negative: a non-text value decides nothing
    [InlineData(null, "{\"version\":1,\"qa\":\"batch\"}", null, null, true)] // negative: a string qa is not configured and is reported
    public void QaCadenceInsideTheQaMapDecidesAndNegativeAnythingElseIsNotConfigured(string? user, string? project, string? expected, string? source, bool warned)
    {
        var sandbox = Directory.CreateTempSubdirectory("ah-cfg-qa-").FullName;
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

            var resolved = config.ResolveConfig(repo);
            Assert.Equal(expected, resolved.Get("qaCadence") is { IsText: true } qa ? qa.AsString() : null);
            Assert.Equal(source, resolved.Get("qaCadenceSource") is { IsText: true } qaSource ? qaSource.AsString() : null);
            Assert.Equal(expected is not null, resolved.ContainsKey("qaCadence")); // unconfigured resolves with no key, exactly as before
            Assert.Equal(warned, resolved.Get("warnings")!.Value.AsArray().Any(static w => w.AsString().StartsWith("ah: qa", StringComparison.Ordinal)));
        }
        finally
        {
            Directory.Delete(sandbox, recursive: true);
        }
    }
}
