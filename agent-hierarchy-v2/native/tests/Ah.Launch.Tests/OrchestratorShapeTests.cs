using Ah.Core.Json;

namespace Ah.Launch.Tests;

/// <summary><c>SpawnShape</c> in orchestrator mode (<c>ahd new</c>): no member env, no team-prefix check, <c>inherit</c> drops <c>--model</c>.</summary>
public sealed class OrchestratorShapeTests
{
    private static JsonObject Orchestrator(string name = "jim", string model = "inherit") => new()
    {
        ["name"] = name,
        ["role"] = "orchestrator",
        ["kind"] = "claude",
        ["model"] = model,
        ["autoMode"] = "auto",
    };

    private static List<string> Argv(ShapeOutcome outcome) =>
        outcome.Shape.Get("argv")!.Value.AsArray().Select(x => JsConvert.ToJsString(x)).ToList();

    [Fact]
    public void TheForegroundShapeIsThePlainOrchestratorArgvWithNoSettings()
    {
        var rig = ScenarioRig.Create();

        var outcome = rig.Shapes.SpawnShape(Orchestrator(), "foreground", rig.Context with { TeamFile = null, Orchestrator = true });

        Assert.Null(outcome.Fail);
        Assert.Equal(["claude", "--agent", "wrangl:orchestrator", "--name", "jim", "--permission-mode", "auto"], Argv(outcome));
    }

    [Fact]
    public void AModelOtherThanInheritIsPassed()
    {
        var rig = ScenarioRig.Create();

        var argv = Argv(rig.Shapes.SpawnShape(Orchestrator(model: "sonnet"), "foreground", rig.Context with { TeamFile = null, Orchestrator = true }));

        Assert.Equal("sonnet", argv[argv.IndexOf("--model") + 1]);
    }

    [Fact]
    public void OrchestratorModeSkipsTheTeamPrefixCheckAndTheMemberEnv()
    {
        var rig = ScenarioRig.Create();

        // A name outside any team prefix, with a team file in the context: a member launch refuses this, an orchestrator launch does not.
        var outcome = rig.Shapes.SpawnShape(Orchestrator("unprefixed"), "herdr", rig.Context with { TeamFile = "proj", Orchestrator = true });

        Assert.Null(outcome.Fail);
        var line = JsConvert.ToJsString(outcome.Shape.Get("launch")!.Value.AsArray()[0]);
        Assert.DoesNotContain("--settings", line, StringComparison.Ordinal);
        Assert.DoesNotContain("AH_MEMBER_NAME", line, StringComparison.Ordinal);
        Assert.DoesNotContain("WRANGL_TEAM_FILE", line, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutOrchestratorModeTheSameUnprefixedNameIsStillRefused()
    {
        var rig = ScenarioRig.Create();

        var outcome = rig.Shapes.SpawnShape(Orchestrator("unprefixed"), "herdr", rig.Context with { TeamFile = "proj" });

        Assert.NotNull(outcome.Fail);
    }

    [Fact]
    public void WithoutOrchestratorModeTheForegroundTransportShapesNoArgv()
    {
        var rig = ScenarioRig.Create();

        var outcome = rig.Shapes.SpawnShape(Orchestrator(), "foreground", rig.Context with { TeamFile = null });

        Assert.Null(outcome.Shape.Get("argv"));
        Assert.Contains("--settings", JsConvert.ToJsString(outcome.Shape.Get("launch")!.Value.AsArray()[0]), StringComparison.Ordinal);
    }
}
