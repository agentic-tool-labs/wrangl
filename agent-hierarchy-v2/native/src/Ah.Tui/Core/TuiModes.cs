namespace Ah.Tui.Core;

/// <summary>The terminal modes the raw client sets on the way in and restores on the way out: the alternate screen, the cursor, and presses and the wheel as SGR reports.</summary>
internal static class TuiModes
{
    internal const string ScreenOn = "\u001b[?1049h\u001b[?25l";
    internal const string ScreenOff = "\u001b[?25h\u001b[?1049l";
    internal const string MouseOn = "\u001b[?1000h\u001b[?1006h";
    internal const string MouseOff = "\u001b[?1006l\u001b[?1000l";

    internal static string Enter(bool mouse) => mouse ? ScreenOn + MouseOn : ScreenOn;

    internal static string Leave(bool mouse) => mouse ? MouseOff + ScreenOff : ScreenOff;
}
