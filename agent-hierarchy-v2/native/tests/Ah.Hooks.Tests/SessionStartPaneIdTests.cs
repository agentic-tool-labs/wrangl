using Ah.Hooks.Lifecycle;

namespace Ah.Hooks.Tests;

/// <summary>herdr desktop: a session in a core terminal records its terminal address as its pane id.</summary>
public sealed class SessionStartPaneIdTests
{
    private static string? PaneId(params (string Name, string Value)[] vars)
    {
        var services = new HooksHostTests.Rig(vars.ToDictionary(v => v.Name, v => v.Value)).Services;
        var id = SessionStart.PaneIdEnv(services);
        return id.IsText ? id.AsString() : null;
    }

    [Fact]
    public void UnderCoreThePaneIdIsTheTerminalAddress() => Assert.Equal("t7", PaneId(("HC_TERMINAL_ID", "t7")));

    [Fact]
    public void UnderTuiThePaneIdIsUnchanged() =>
        Assert.Equal("w1:p2", PaneId(("HERDR_ENV", "1"), ("HERDR_PANE_ID", "w1:p2"), ("HC_TERMINAL_ID", "t7")));

    [Fact]
    public void AnEmptyTerminalIdIsNotCore() => Assert.Null(PaneId(("HC_TERMINAL_ID", "")));
}
