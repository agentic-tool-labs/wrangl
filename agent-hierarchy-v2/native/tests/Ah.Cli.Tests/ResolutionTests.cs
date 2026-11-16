using Ah.Cli.Delegation;

namespace Ah.Cli.Tests;

public sealed class ResolutionTests
{
    private static string Record(Sandbox sandbox, string name, string version, string scope, bool withMarker = true)
    {
        var path = Path.Combine(sandbox.Root, "cache", name, version);
        Directory.CreateDirectory(path);
        if (withMarker)
        {
            sandbox.Write($"cache/{name}/{version}/bin/ah-hook", "//\n");
        }

        return $"{{\"installPath\":\"{path}\",\"version\":\"{version}\",\"scope\":\"{scope}\"}}";
    }

    private static void Installed(Sandbox sandbox, string key, params string[] records) =>
        sandbox.Write("home/.claude/plugins/installed_plugins.json", $"{{\"plugins\":{{\"{key}\":[{string.Join(",", records)}]}}}}");

    private static ResolvedPlugin? Resolve(Harness harness) =>
        PluginResolver.Resolve(harness.Context.Env, "agent-hierarchy", null);

    [Fact]
    public void TheNewestRecordWithTheHookFileWinsOverANewerOneWithoutIt()
    {
        using var harness = new Harness();
        var older = Record(harness.Sandbox, "ah", "1.0.0", "user");
        var current = Record(harness.Sandbox, "ah", "1.2.0", "user");
        var missing = Record(harness.Sandbox, "ah", "2.0.0", "user", withMarker: false);
        Installed(harness.Sandbox, "agent-hierarchy@market", older, current, missing);

        var plugin = Resolve(harness);

        Assert.Equal(Path.Combine(harness.Sandbox.Root, "cache", "ah", "1.2.0"), plugin?.Root);
        Assert.Equal("installed", plugin?.Source);
    }

    [Fact]
    public void ADottedNumericCompareIsNotAStringCompare()
    {
        using var harness = new Harness();
        var nine = Record(harness.Sandbox, "ah", "1.9.0", "user");
        var ten = Record(harness.Sandbox, "ah", "1.10.0", "user");
        Installed(harness.Sandbox, "agent-hierarchy@market", ten, nine);

        Assert.EndsWith("1.10.0", Resolve(harness)?.Root);
    }

    [Fact]
    public void AProjectScopeRecordIsUsedOnlyWhenNoUserRecordSurvives()
    {
        using var harness = new Harness();
        var user = Record(harness.Sandbox, "ah", "1.0.0", "user");
        var project = Record(harness.Sandbox, "ah", "9.0.0", "project");
        Installed(harness.Sandbox, "agent-hierarchy@market", user, project);
        Assert.EndsWith("1.0.0", Resolve(harness)?.Root);

        var userWithoutMarker = Record(harness.Sandbox, "ah", "1.5.0", "user", withMarker: false);
        Installed(harness.Sandbox, "agent-hierarchy@market", userWithoutMarker, project);
        Assert.EndsWith("9.0.0", Resolve(harness)?.Root);
    }

    [Fact]
    public void ARecordOfAnotherPluginIsIgnored()
    {
        using var harness = new Harness();
        var other = Record(harness.Sandbox, "other", "9.0.0", "user");
        Installed(harness.Sandbox, "other-plugin@market", other);

        Assert.Null(Resolve(harness));
    }

    [Fact]
    public void TheEnvironmentOverrideWinsOverAnInstalledRecord()
    {
        using var harness = new Harness();
        var installed = Record(harness.Sandbox, "ah", "1.0.0", "user");
        Installed(harness.Sandbox, "agent-hierarchy@market", installed);
        harness.Sandbox.InstallPlugin();
        harness.Variables["AH_PLUGIN_ROOT"] = harness.Sandbox.PluginRoot;
        harness.Variables["AH_TEST"] = "1";

        var plugin = Resolve(harness);

        Assert.Equal(harness.Sandbox.PluginRoot, plugin?.Root);
        Assert.Equal("env", plugin?.Source);
    }

    [Fact]
    public void AnOverrideWithoutTheHookFileIsIgnored()
    {
        using var harness = new Harness();
        Directory.CreateDirectory(harness.Sandbox.PluginRoot);
        harness.Variables["AH_PLUGIN_ROOT"] = harness.Sandbox.PluginRoot;
        harness.Variables["AH_TEST"] = "1";

        Assert.Null(Resolve(harness));
    }

    [Fact]
    public void TheStampsRootIsTheLastResort()
    {
        using var harness = new Harness();
        harness.Sandbox.InstallPlugin();

        var plugin = PluginResolver.Resolve(harness.Context.Env, "agent-hierarchy", harness.Sandbox.PluginRoot);

        Assert.Equal("stamp", plugin?.Source);
    }

    [Fact]
    public void AReleaseIsPreferredToItsPrereleaseWhicheverIsListedFirst()
    {
        using var harness = new Harness();
        var pre = Record(harness.Sandbox, "ah", "2.5.0-pre.1", "user");
        var release = Record(harness.Sandbox, "ah", "2.5.0", "user");
        Installed(harness.Sandbox, "agent-hierarchy@market", pre, release);
        Assert.EndsWith("2.5.0", Resolve(harness)?.Root);

        Installed(harness.Sandbox, "agent-hierarchy@market", release, pre);
        Assert.EndsWith("2.5.0", Resolve(harness)?.Root);
    }

    [Fact]
    public void ADogfoodBuildOfAPrereleaseBeatsThePrereleaseAndLosesToTheRelease()
    {
        using var harness = new Harness();
        var pre = Record(harness.Sandbox, "ah", "2.5.0-pre.1", "user");
        var build = Record(harness.Sandbox, "ah", "2.5.0-pre.1.10", "user");
        var older = Record(harness.Sandbox, "ah", "2.5.0-pre.1.9", "user");
        Installed(harness.Sandbox, "agent-hierarchy@market", pre, older, build);
        Assert.EndsWith("2.5.0-pre.1.10", Resolve(harness)?.Root);
    }
}
