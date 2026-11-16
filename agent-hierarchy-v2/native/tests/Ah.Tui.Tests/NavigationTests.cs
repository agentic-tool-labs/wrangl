using Ah.Tui.Core;

namespace Ah.Tui.Tests;

/// <summary>Decoding terminal input, and what the environment asks of the terminal.</summary>
public sealed class NavigationTests
{
    [Fact]
    public void InputParsingNamesKeysAndMouseReports()
    {
        var events = TuiNav.ParseInput("\u001b[A\u001b[B\u001b[C\u001b[D\u001b[H\u001b[5~\u001b[6~\r\u007fqj\u0003\u001b[<0;12;5M\u001b[<0;12;5m\u001b[<64;1;1M\u001b[<65;1;1M\u001b[<32;2;2M");

        var keys = string.Join(",", events.OfType<KeyEvent>().Select(e => e.Name));
        var mice = events.OfType<MouseEvent>().ToList();
        Assert.Equal("up,down,right,left,home,pageup,pagedown,enter,backspace,q,j,ctrl-c", keys);
        Assert.Equal(5, mice.Count);
        Assert.Equal(new MouseEvent(11, 4, "click"), mice[0]);
        Assert.Equal("other", mice[1].Kind);
        Assert.Equal("wheelup", mice[2].Kind);
        Assert.Equal("wheeldown", mice[3].Kind);
        Assert.Equal("drag", mice[4].Kind);
    }

    [Fact]
    public void ALoneEscapeIsTheEscKeyAndAnUnknownSequenceIsDroppedWhole()
    {
        Assert.Equal(new KeyEvent("esc"), Assert.Single(TuiNav.ParseInput("\u001b")));
        Assert.Empty(TuiNav.ParseInput("\u001b[99z"));
    }

    [Theory]
    [InlineData("off", false)]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("OFF", true)]
    [InlineData("motion", true)]
    public void TheMouseIsOnUnlessExactlyOffIsAsked(string? value, bool on) =>
        Assert.Equal(on, TuiCaps.Detect(k => k == "AH_TUI_MOUSE" ? value : null).Mouse != MouseLevel.Off);

    [Theory]
    [InlineData("dumb", true)]
    [InlineData("xterm-256color", false)]
    [InlineData(null, false)]
    public void OnlyADumbTerminalIsDumb(string? term, bool expected) =>
        Assert.Equal(expected, TuiCaps.Detect(k => k == "TERM" ? term : null).Dumb);
}
