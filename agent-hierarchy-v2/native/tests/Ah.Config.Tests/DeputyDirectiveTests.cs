namespace Ah.Config.Tests;

/// <summary>The Deputy-Chief's directive: the Orchestrator's lines minus the user-facing ones, plus what the deputy may not do.</summary>
public sealed class DeputyDirectiveTests
{
    private static readonly string[] OrchestratorLines =
    [
        "Agent hierarchy ACTIVE. You are the Orchestrator of this team.",
        "0. Handoff gate — ask the user.",
        "1. Gate: binds the top-level Orchestrator only.",
        "6. Review loop — findings still open after that → escalate to Ultra-Advisor, not another loop, then surface its verdict to the user.",
        "7. Ultra-Advisor — escalation apex.",
        "9. Friction loop (first-class) — report it.",
        "10. Flow control — handoffs are auto.",
        "Decide operational calls yourself; never ask the user.",
    ];

    [Fact]
    public void TheDeputyIsToldItsRefusedVerbsThatOrdersComeThroughItAndThatItShipsNothing()
    {
        var text = DirectiveText.DeputyLines(OrchestratorLines);

        foreach (var verb in new[] { "quit", "stop", "disband", "untrack", "adopt", "duty", "lane-caps", "resume", "create" })
        {
            Assert.Contains(verb, DirectiveText.DeputyDutyLine, StringComparison.Ordinal);
        }

        Assert.Contains(DirectiveText.DeputyDutyLine, text, StringComparison.Ordinal);
        Assert.Contains("orders to the team come through you", text, StringComparison.Ordinal);
        Assert.Contains("ship batch", text, StringComparison.Ordinal);
        Assert.DoesNotContain("0. Handoff gate", text, StringComparison.Ordinal);
        Assert.DoesNotContain("10. Flow control", text, StringComparison.Ordinal);
        Assert.DoesNotContain("surface its verdict to the user", text, StringComparison.Ordinal);
    }

    // ah2-510 L6: the same Orchestrator lines, rewritten for a sub-orchestrator
    [Fact]
    public void ASubOrchestratorIsToldItMayParkAPickOnNeedsYouAndTheOrchestratorLineIsUnchanged()
    {
        var text = DirectiveText.SubOrchestratorLines(OrchestratorLines);

        Assert.Contains("A pick inside your charter may be parked on Needs-you with `ah roster checklist set <item> --state waiting-user", text, StringComparison.Ordinal);
        Assert.Contains("the answer comes to you and your parent", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Decide operational calls yourself; never ask the user.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeARewordedAnchorCutsTheDeputyDirectiveToItsDutyLines()
    {
        var text = DirectiveText.DeputyLines(OrchestratorLines.Where(l => !l.StartsWith("0. Handoff gate", StringComparison.Ordinal)).ToList());

        Assert.Contains(DirectiveText.DeputyDutyLine, text, StringComparison.Ordinal);
        Assert.DoesNotContain("1. Gate:", text, StringComparison.Ordinal);
    }
}
