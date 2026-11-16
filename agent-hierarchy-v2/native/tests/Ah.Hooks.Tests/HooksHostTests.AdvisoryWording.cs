using System.Text;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Hooks.Plumbing;

namespace Ah.Hooks.Tests;

/// <summary>An advisory-converted denial says the call ran; a real denial keeps the gate's words.</summary>
public sealed partial class HooksHostTests
{
    private const string NotRanReason = "ah: this is guarded. This call was BLOCKED and did not run. It is denied; nothing ran. Report BLOCKED to the orchestrator.";

    private static HookOutcome AdvisoriseWith(string permissionMode)
    {
        var rig = StuckRig();
        var unit = new GateUnit("pretooluse-wording", static _ => HookEmit.Deny(NotRanReason), GateClass.Advisory);
        var context = new GateContext(rig.Services, JsonText.Parse(Encoding.UTF8.GetBytes(AdvisoryInput(rig, permissionMode))), "pretooluse-wording");
        return HookHost.Advisorise(unit, context, unit.Body(context), rig.Errors);
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("default")]
    public void AnAdvisoryConvertedDenialSaysThePermissionFlowDecidesAndQuotesTheReasonUnchanged(string mode)
    {
        var text = Text(AdvisoriseWith(mode));

        Assert.Contains("not stopped by this gate (advisory); the permission flow decides. Gate said: ", text, StringComparison.Ordinal);
        Assert.Contains(NotRanReason, text, StringComparison.Ordinal);
        Assert.DoesNotContain("permissionDecision", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ran anyway", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ARealDenialOutsideTheClassifierKeepsTheGatesWordsUnchanged()
    {
        var outcome = AdvisoriseWith("bypassPermissions");

        Assert.True(IsDenied(outcome));
        Assert.Contains("BLOCKED and did not run", Text(outcome), StringComparison.Ordinal);
        Assert.DoesNotContain("ran anyway", Text(outcome), StringComparison.Ordinal);
    }

    [Fact]
    public void TheReasonIsQuotedWordForWordWhateverItSays()
    {
        Assert.Equal(
            "ah advisory (g): not stopped by this gate (advisory); the permission flow decides. Gate said: report BLOCKED in /x/denied/did not run",
            HookHost.AdvisoryText("g", "report BLOCKED in /x/denied/did not run"));
    }
}
