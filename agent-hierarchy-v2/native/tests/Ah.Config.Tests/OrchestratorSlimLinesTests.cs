namespace Ah.Config.Tests;

/// <summary>The top-level Orchestrator's resident lines (ah2-68): item 10 is cut to one line only when its mode can be read from it.</summary>
public sealed class OrchestratorSlimLinesTests
{
    [Theory]
    [InlineData("auto")]
    [InlineData("confirm")]
    public void ItemTenIsCutToOneLineNamingItsMode(string mode)
    {
        var slim = DirectiveText.SlimLines([$"10. Flow control — handoffs are currently \"{mode}\" (more)."]);

        Assert.Equal($"Handoffs are currently {mode}; the user may flip it at any time: `ah rules show flow`.", slim[0]);
    }

    /// <summary>Amendment 3: the index lists routing only when custom routes exist; every other entry either way.</summary>
    [Fact]
    public void TheIndexListsRoutingOnlyWithRoutes()
    {
        var with = DirectiveText.IndexLines(routing: true);
        var without = DirectiveText.IndexLines(routing: false);

        Assert.Contains(with, l => l.EndsWith(": ah rules show routing", StringComparison.Ordinal));
        Assert.DoesNotContain(without, l => l.Contains("ah rules show routing", StringComparison.Ordinal));
        Assert.Equal(with.Count - 1, without.Count);
        Assert.StartsWith("No custom implement-class routes are configured", DirectiveText.NoRoutingText, StringComparison.Ordinal);
    }

    /// <summary>Amendment 3 F5, negative: a reworded item 10 stays whole and resident; nothing says "auto" in its place.</summary>
    [Theory]
    [InlineData("10. Flow control — the handoff mode is confirm.")]
    [InlineData("10. Flow control — handoffs are currently \"confirm")]
    public void AnItemTenWithoutTheMarkerStaysWhole(string line)
    {
        var slim = DirectiveText.SlimLines([line]);

        Assert.Equal(line, slim[0]);
        Assert.DoesNotContain(slim, l => l.Contains("auto", StringComparison.Ordinal));
    }
}
