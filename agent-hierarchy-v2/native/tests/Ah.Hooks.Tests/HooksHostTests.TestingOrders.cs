using Ah.Config;
using Ah.Core.Json;

namespace Ah.Hooks.Tests;

/// <summary>The durable testing orders: defaults, per-key merge across level files, and the lines the member directive prints.</summary>
public sealed partial class HooksHostTests
{
    private static JsonObject ResolvedWithTesting(JsonObject block, JsonObject? roles = null)
    {
        var resolved = new JsonObject { [ITestingOrders.Key] = block };
        if (roles is not null)
        {
            resolved["roles"] = roles;
        }

        return resolved;
    }

    private static void WriteLevelFile(string path, string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
    }

    [Fact]
    public void TestingOrdersDefaultWithNoConfigAndABlockOverridesOnlyItsKeys()
    {
        var defaults = ITestingOrders.Resolve(null);
        Assert.Equal("touched", ITestingOrders.Mode(defaults.Get("implementor")!.Value.AsString()));
        Assert.Equal("touched", ITestingOrders.Mode(defaults.Get("ux-engineer")!.Value.AsString()));
        Assert.Equal("none", ITestingOrders.Mode(defaults.Get("reviewer")!.Value.AsString()));
        Assert.Equal("full", ITestingOrders.Mode(defaults.Get("qa")!.Value.AsString()));
        Assert.StartsWith("background", defaults.Get("e4")!.Value.AsString(), StringComparison.Ordinal);

        var merged = ITestingOrders.Resolve(ResolvedWithTesting(new JsonObject { ["implementor"] = "full: small repo", ["qa"] = "  " }));
        Assert.Equal("full: small repo", merged.Get("implementor")!.Value.AsString());
        Assert.Equal("none", ITestingOrders.Mode(merged.Get("reviewer")!.Value.AsString()));
        Assert.Equal("full", ITestingOrders.Mode(merged.Get("qa")!.Value.AsString()));
    }

    /// <summary>A testing policy alone never makes a config layer: the hierarchy stays off, and the order still resolves, merged per key.</summary>
    [Fact]
    public void ATestingOnlyLevelFileLeavesTheHierarchyOffAndItsOrdersMergePerKey()
    {
        var rig = new Rig();
        var proj = rig.Sandbox.Root + "/proj";
        WriteLevelFile(rig.Services.Config.UserConfigPath(), """{"testing":{"implementor":"project: a","reviewer":"touched: x"}}""");

        var resolved = rig.Services.Config.ResolveConfig(proj);
        Assert.False(resolved.Get("configured")!.Value.AsBool());
        Assert.Empty(resolved.Get("layers")!.Value.AsArray());
        var orders = ITestingOrders.Resolve(resolved);
        Assert.Equal("project: a", orders.Get("implementor")!.Value.AsString());
        Assert.Equal("touched: x", orders.Get("reviewer")!.Value.AsString());

        Directory.CreateDirectory(proj + "/.git");
        var repo = rig.Services.Config.RosterLevelPaths(proj).Get("repo")!.Value.AsString();
        WriteLevelFile(repo, """{"testing":{"implementor":"full: b"}}""");
        var both = rig.Services.Config.ResolveConfig(proj);
        Assert.False(both.Get("configured")!.Value.AsBool());
        var merged = ITestingOrders.Resolve(both);
        Assert.Equal("full: b", merged.Get("implementor")!.Value.AsString());
        Assert.Equal("touched: x", merged.Get("reviewer")!.Value.AsString());
    }

    /// <summary>Negative: a global file with testing and a real config key is still a layer, as before.</summary>
    [Fact]
    public void ALevelFileWithTestingAndAConfigKeyIsStillALayer()
    {
        var rig = new Rig();
        WriteLevelFile(rig.Services.Config.UserConfigPath(), """{"enabled":true,"testing":{"implementor":"full: b"}}""");
        Assert.True(rig.Services.Config.ResolveConfig(rig.Sandbox.Root + "/proj").Get("configured")!.Value.AsBool());
    }

    /// <summary>Negative: a leading word that is not a mode, or a mode word not before the colon, names no mode.</summary>
    [Fact]
    public void ATestingOrderNamesAModeOnlyByItsLeadingWord()
    {
        Assert.Null(ITestingOrders.Mode("background: E4"));
        Assert.Null(ITestingOrders.Mode("run touched tests"));
        Assert.Equal("project", ITestingOrders.Mode("project"));
    }

    [Fact]
    public void TheMemberDirectivePrintsTheTestingOrderWithTheBriefOverride()
    {
        var rig = new Rig();
        var directive = DirectiveText(rig);
        var plugin = rig.Services.Config.PluginName;

        var implementor = directive.BuildRoleSessionNotice("implementor", plugin + ":implementor");
        Assert.Contains("Your testing order (implementor): touched: only the test classes", implementor, StringComparison.Ordinal);
        Assert.Contains("A `Tests:` line in a brief overrides it for that task.", implementor, StringComparison.Ordinal);
        Assert.Contains("Tests: run them yourself", implementor, StringComparison.Ordinal);
        Assert.Contains(" E4: background", directive.BuildRoleSessionNotice("ux-engineer", plugin + ":ux-engineer"), StringComparison.Ordinal);
        Assert.Contains("Your testing order (qa): full:", directive.BuildRoleSessionNotice("qa", plugin + ":qa"), StringComparison.Ordinal);
        Assert.Contains("Your testing order (implementor): full: small repo.",
            directive.BuildRoleSessionNotice("implementor", plugin + ":implementor", ResolvedWithTesting(new JsonObject { ["implementor"] = "full: small repo" })),
            StringComparison.Ordinal);
    }

    /// <summary>A role whose order is `none` gets no run-it-yourself line; the nearest non-match, the same role with a touched order, does.</summary>
    [Fact]
    public void ANoneOrderDropsTheRunItYourselfLine()
    {
        var rig = new Rig();
        var directive = DirectiveText(rig);
        var plugin = rig.Services.Config.PluginName;

        var reviewer = directive.BuildRoleSessionNotice("reviewer", plugin + ":reviewer");
        Assert.Contains("Your testing order (reviewer): none:", reviewer, StringComparison.Ordinal);
        Assert.DoesNotContain("Tests: run them yourself", reviewer, StringComparison.Ordinal);
        Assert.Contains("Tests: run them yourself",
            directive.BuildRoleSessionNotice("reviewer", plugin + ":reviewer", ResolvedWithTesting(new JsonObject { ["reviewer"] = "touched: y" })),
            StringComparison.Ordinal);
    }

    /// <summary>A custom role without its own key takes its class's order (implement → implementor); an advise-class one gets none; its own key wins.</summary>
    [Fact]
    public void ACustomRoleFallsBackToItsClassOrder()
    {
        var rig = new Rig();
        var directive = DirectiveText(rig);
        var roles = new JsonObject
        {
            ["ui-impl"] = new JsonObject { ["class"] = "implement" },
            ["sage"] = new JsonObject { ["class"] = "advise" },
        };

        Assert.Contains("Your testing order (ui-impl): touched: only the test classes",
            directive.BuildRoleSessionNotice("ui-impl", "acme:ui-impl", ResolvedWithTesting(new JsonObject(), roles)), StringComparison.Ordinal);
        Assert.DoesNotContain("Your testing order",
            directive.BuildRoleSessionNotice("sage", "acme:sage", ResolvedWithTesting(new JsonObject(), roles)), StringComparison.Ordinal);
        Assert.Contains("Your testing order (ui-impl): full: own.",
            directive.BuildRoleSessionNotice("ui-impl", "acme:ui-impl", ResolvedWithTesting(new JsonObject { ["ui-impl"] = "full: own" }, roles)), StringComparison.Ordinal);
    }

    /// <summary>Negative: a built-in role with no testing order (architect, task-runner) gets no testing line.</summary>
    [Fact]
    public void ARoleWithNoTestingOrderGetsNoTestingLine()
    {
        var rig = new Rig();
        var directive = DirectiveText(rig);
        var plugin = rig.Services.Config.PluginName;
        Assert.DoesNotContain("Your testing order", directive.BuildRoleSessionNotice("architect", plugin + ":architect"), StringComparison.Ordinal);
        Assert.DoesNotContain("Your testing order", directive.BuildRoleSessionNotice("task-runner", plugin + ":task-runner"), StringComparison.Ordinal);
    }

    private static IDirectiveText DirectiveText(Rig rig) => WithDirective(rig).Advisory!.Directive;
}
