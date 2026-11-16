using Ah.Core.Json;

namespace Ah.Hooks.Tests;

/// <summary>The native command form the gates print and accept (<c>&lt;root&gt;/bin/ah roster|msg</c>, <c>&lt;root&gt;/bin/ahd</c>).</summary>
public sealed partial class HooksHostTests
{
    [Theory]
    [InlineData("{root}/bin/ahd status --plain --cwd /repo", "allow")]
    [InlineData("{root}/bin/ahd inspect entries --address a@t --all -n 5", "allow")]
    [InlineData("\"{root}/bin/ahd\" drain --require", "allow")]
    [InlineData("{root}/bin/ahd status; ls", "silent")]
    [InlineData("{root}/bin/ahd logs -n $(id)", "silent")]
    [InlineData("{root}/bin/ahd", "silent")]
    [InlineData("/elsewhere/bin/ahd status", "silent")]
    [InlineData("{root}/bin/ahd-other status", "silent")]
    [InlineData("bin/ahd status", "silent")]
    public void TheAllowHookLetsThePluginsOwnAhdCommandThroughAndNothingElse(string command, string expected)
    {
        var root = MakePluginRoot();
        var rig = new Rig(new Dictionary<string, string> { ["AH_TEST"] = "0" }, root);

        Assert.Equal(expected, AllowHookAnswer(rig, command.Replace("{root}", root, StringComparison.Ordinal)));
    }

    [Fact]
    public void AnAhdOfAnotherExistingDirectoryIsNotThePluginsOwn()
    {
        var root = MakePluginRoot();
        var other = MakePluginRoot();
        var rig = new Rig(new Dictionary<string, string> { ["AH_TEST"] = "0" }, root);

        Assert.True(File.Exists($"{other}/bin/ahd"));
        Assert.Equal("silent", AllowHookAnswer(rig, $"{other}/bin/ahd status"));
        Assert.Equal("allow", AllowHookAnswer(rig, $"{root}/bin/ahd status"));
    }

    [Theory]
    [InlineData("{root}/bin/ah roster show --cwd /repo", "ah: plugin-shipped CLI \u2014 ah roster show")]
    [InlineData("{root}/bin/ah msg list --open", "ah: plugin-shipped CLI \u2014 ah msg list")]
    [InlineData("{root}/bin/ahd inspect timers", "ah: plugin-shipped CLI \u2014 ahd inspect")]
    [InlineData("{root}/bin/ah rules next-step implement-done", "ah: plugin-shipped CLI \u2014 ah rules next-step")]
    [InlineData("{root}/bin/ah rules who-may-message a@tm b@tm --slug lock", "ah: plugin-shipped CLI \u2014 ah rules who-may-message")]
    public void TheAllowReasonNamesTheNativeCommandAndNoScript(string command, string reason)
    {
        var root = MakePluginRoot();
        var rig = new Rig(new Dictionary<string, string> { ["AH_TEST"] = "0" }, root);

        var outcome = rig.Run(
            "pretooluse-ah-cli",
            Event("Bash", "wrangl:orchestrator", new JsonObject { ["command"] = command.Replace("{root}", root, StringComparison.Ordinal) }));

        Assert.Equal(reason, Reason(outcome));
        Assert.DoesNotContain(".mjs", Reason(outcome), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{root}/bin/ah rules next-step implement-done; rm x")]
    [InlineData("{root}/bin/ah rules next-step implement-done && rm x")]
    [InlineData("{root}/bin/ah rules next-step $(rm x)")]
    public void NegativeAChainedCommandAfterTheRulesQueryIsNotAllowed(string command)
    {
        var root = MakePluginRoot();
        var rig = new Rig(new Dictionary<string, string> { ["AH_TEST"] = "0" }, root);

        Assert.Equal("silent", AllowHookAnswer(rig, command.Replace("{root}", root, StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("wrangl:implementor")]
    [InlineData("wrangl:reviewer")]
    [InlineData("wrangl:qa")]
    public void AnyRoleMayRunTheRulesQueryWithoutAPrompt(string agent)
    {
        var root = MakePluginRoot();
        var rig = new Rig(new Dictionary<string, string> { ["AH_TEST"] = "0" }, root);

        var outcome = rig.Run("pretooluse-ah-cli", Event("Bash", agent, new JsonObject { ["command"] = $"{root}/bin/ah rules next-step implement-done" }));

        Assert.Equal("ah: plugin-shipped CLI \u2014 ah rules next-step", Reason(outcome));
    }
}
