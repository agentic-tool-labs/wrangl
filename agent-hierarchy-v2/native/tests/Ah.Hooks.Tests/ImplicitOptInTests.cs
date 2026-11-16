using Ah.Core.Json;

namespace Ah.Hooks.Tests;

/// <summary>Any ah2 act by the session opts it in once, and nothing else does. One row per trigger and per negative case of the spec.</summary>
public sealed class ImplicitOptInTests
{
    private const string Lead = "this session is now the Orchestrator (opted in by";

    private static OrchestrateCommandTests.Fixture Bare() => new();

    private static OrchestrateCommandTests.Fixture Owning(OrchestrateCommandTests.Fixture f)
    {
        OrchestrateCommandTests.OwnedTeam(f, "s1", HarnessSessions.Pid, HarnessSessions.Start);
        return f;
    }

    private static JsonObject Bash(string command) => new() { ["command"] = command };

    private static JsonObject Skill(string skill, string? args = null)
    {
        var input = new JsonObject { ["skill"] = skill };
        if (args is not null)
        {
            input["args"] = args;
        }

        return input;
    }

    // ---------------------------------------------------------------- typed

    [Theory]
    [InlineData("/wrangl:agent-team")]
    [InlineData("/wrangl:hierarchy status")]
    [InlineData("/hierarchy status")]
    [InlineData("/wrangl:hierarchy always-on on")]
    [InlineData("/agent-team")]
    [InlineData("<command-name>/wrangl:pipeline</command-name>")]
    [InlineData("<command-name>/wrangl:hierarchy</command-name><command-args>status</command-args>")]
    public void ATypedAh2CommandOptsIn(string prompt)
    {
        var f = Bare();

        Assert.Contains(Lead, f.Prompt(prompt), StringComparison.Ordinal);
        Assert.True(f.Flagged("s1"));
    }

    [Theory]
    [InlineData("please run /wrangl:agent-team for me")]
    [InlineData("/foo:bar")]
    [InlineData("/foo:agent-team")]
    [InlineData("/not-a-command-of-this-plugin")]
    [InlineData("/../agent-team")]
    [InlineData("/wrangl:hierarchy off")]
    [InlineData("/hierarchy off")]
    [InlineData("/wrangl:hierarchy always-on off")]
    [InlineData("<command-name>/wrangl:hierarchy</command-name><command-args>off</command-args>")]
    [InlineData("<command-name>/wrangl:hierarchy")]
    [InlineData("hello")]
    public void AnythingElseTypedDoesNot(string prompt)
    {
        var f = Bare();

        Assert.Equal(string.Empty, f.Prompt(prompt));
        Assert.False(f.Flagged("s1"));
    }

    [Fact]
    public void TurningOffByCommandIsNoTrigger()
    {
        var f = Bare();

        _ = f.Prompt("/wrangl:orchestrator off");

        Assert.False(f.Flagged("s1"));
    }

    [Fact]
    public void APeerDeliveryIsNoTrigger()
    {
        var f = Bare();

        Assert.Equal(string.Empty, f.Prompt("<cross-session-message from=\"x\">/wrangl:agent-team [hierarchy-msg 1]</cross-session-message>"));
        Assert.False(f.Flagged("s1"));
    }

    // ---------------------------------------------------------------- Skill

    [Fact]
    public void AModelSkillCallOfThisPluginOptsIn()
    {
        var f = Bare();

        Assert.Contains(Lead, f.After("Skill", Skill("wrangl:agent-team")), StringComparison.Ordinal);
        Assert.True(f.Flagged("s1"));
    }

    [Theory]
    [InlineData("foo:agent-team", null)]
    [InlineData("agent-team", null)]
    [InlineData("wrangl:hierarchy", "off")]
    public void AnotherSkillDoesNot(string skill, string? args)
    {
        var f = Bare();

        Assert.Equal(string.Empty, f.After("Skill", Skill(skill, args)));
        Assert.False(f.Flagged("s1"));
    }

    // ---------------------------------------------------------------- the ah CLI

    [Theory]
    [InlineData("{root}/bin/ah roster show")]
    [InlineData("cd x && {root}/bin/ah roster show")]
    [InlineData("{root}/bin/ah backlog list")]
    [InlineData("{root}/bin/ah proc stop 12")]
    public void ACallOfThisPluginsAhLeavesABareSessionBare(string command)
    {
        var f = Bare();

        Assert.Equal(string.Empty, f.After("Bash", Bash(command.Replace("{root}", f.PluginRoot, StringComparison.Ordinal))));
        Assert.False(f.Flagged("s1"));
        Assert.False(f.In("s1"));
    }

    [Fact]
    public void ACallThatLeavesTheSessionOwningATeamDeliversTheDirectiveOnce()
    {
        var f = Owning(Bare());

        var first = f.After("Bash", Bash(f.PluginRoot + "/bin/ah roster spawn-ad-hoc architect"));
        var second = f.After("Bash", Bash(f.PluginRoot + "/bin/ah roster show"));

        Assert.Contains(Lead, first, StringComparison.Ordinal);
        Assert.Equal(string.Empty, second);
        Assert.True(f.Flagged("s1"));
    }

    [Fact]
    public void AFailingCallOptsInThroughTheFailureEvent()
    {
        var f = Owning(Bare());

        var text = f.After("Bash", Bash(f.PluginRoot + "/bin/ah roster nope"), hookEvent: "PostToolUseFailure");

        Assert.Contains(Lead, text, StringComparison.Ordinal);
        Assert.Contains("\"hookEventName\":\"PostToolUseFailure\"", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("grep \"ah roster\" f")]
    [InlineData("echo ah msg")]
    [InlineData("/elsewhere/bin/ah roster show")]
    [InlineData("{root}/bin/ahd serve")]
    [InlineData("FOO=1 {root}/bin/ah roster show")]
    [InlineData("./bin/ah roster show")]
    public void AnotherCommandDoesNot(string command)
    {
        var f = Bare();

        Assert.Equal(string.Empty, f.After("Bash", Bash(command.Replace("{root}", f.PluginRoot, StringComparison.Ordinal))));
        Assert.False(f.Flagged("s1"));
    }

    [Fact]
    public void AnotherToolDoesNot()
    {
        var f = Bare();

        Assert.Equal(string.Empty, f.After("Read", Bash(f.PluginRoot + "/bin/ah roster show")));
    }

    // ---------------------------------------------------------------- who is left alone

    [Fact]
    public void ASecondTriggerDeliversNothing()
    {
        var f = Owning(Bare());
        var first = f.After("Bash", Bash(f.PluginRoot + "/bin/ah roster show"));

        var second = f.After("Bash", Bash(f.PluginRoot + "/bin/ah msg index 1"));
        var typed = f.Prompt("/wrangl:agent-team");

        Assert.Contains(Lead, first, StringComparison.Ordinal);
        Assert.Equal(string.Empty, second);
        Assert.DoesNotContain(Lead, typed, StringComparison.Ordinal);
    }

    [Fact]
    public void ASubagentOfABareSessionNeitherOptsItInNorHearsAnything()
    {
        var f = Owning(Bare());

        Assert.Equal(string.Empty, f.After("Bash", Bash(f.PluginRoot + "/bin/ah roster show"), tweak: e => e["agent_id"] = "a1"));
        Assert.False(f.Flagged("s1"));
    }

    [Fact]
    public void ADeclinedSessionStaysOutUntilItAsksExplicitly()
    {
        var f = Bare();
        _ = f.Typed("/wrangl:orchestrator off");

        Assert.Equal(string.Empty, f.Prompt("/wrangl:agent-team"));
        Owning(f);
        Assert.Equal(string.Empty, f.After("Bash", Bash(f.PluginRoot + "/bin/ah roster show")));
        Assert.False(f.Flagged("s1"));
    }

    [Fact]
    public void InADisabledRepoNothingHappens()
    {
        var f = Owning(new OrchestrateCommandTests.Fixture(config: """{"version":1,"enabled":false}"""));

        Assert.Equal(string.Empty, f.After("Bash", Bash(f.PluginRoot + "/bin/ah roster show")));
        Assert.False(f.Flagged("s1"));
    }

    [Fact]
    public void AnUnconfiguredRepoOptsInWithTheBody()
    {
        var f = Owning(new OrchestrateCommandTests.Fixture(configured: false));

        Assert.Contains(Lead, f.After("Bash", Bash(f.PluginRoot + "/bin/ah roster show")), StringComparison.Ordinal);
        Assert.True(f.Flagged("s1"));
    }

    [Theory]
    [InlineData("foo")]
    [InlineData("wrangl:architect")]
    [InlineData("wrangl:orchestrator")]
    public void AnAgentSessionIsLeftAlone(string agent)
    {
        var f = Owning(Bare());

        Assert.Equal(string.Empty, f.After("Bash", Bash(f.PluginRoot + "/bin/ah roster show"), tweak: e => e["agent_type"] = agent));
        Assert.False(f.Flagged("s1"));
    }

    [Fact]
    public void ATeamMemberIsLeftAlone()
    {
        var f = Owning(new OrchestrateCommandTests.Fixture(new Dictionary<string, string> { ["WRANGL_TEAM_FILE"] = "/x/t.json" }));

        Assert.Equal(string.Empty, f.After("Bash", Bash(f.PluginRoot + "/bin/ah roster show")));
        Assert.False(f.Flagged("s1"));
    }

    [Fact]
    public void ASessionAlreadyInBySettingWritesNoFlag()
    {
        var f = Owning(new OrchestrateCommandTests.Fixture(config: """{"version":1,"alwaysOrchestrator":true}"""));

        Assert.Equal(string.Empty, f.After("Bash", Bash(f.PluginRoot + "/bin/ah roster show")));
        Assert.False(f.Flagged("s1"));
    }
}
