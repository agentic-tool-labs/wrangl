namespace Ah.Tui.Core;

/// <summary>Whether the terminal is asked for mouse reports (1000/1006).</summary>
internal enum MouseLevel
{
    Off,
    Basic,
}

/// <summary>What the client asks of the terminal, decided once at start from the environment.</summary>
internal sealed record TuiCaps(MouseLevel Mouse, bool Dumb = false)
{
    /// <summary><see cref="Dumb"/> is <c>TERM=dumb</c>. The mouse is on unless <c>AH_TUI_MOUSE</c> is <c>off</c>.</summary>
    internal static TuiCaps Detect(Func<string, string?> env)
    {
        ArgumentNullException.ThrowIfNull(env);
        return new TuiCaps(env("AH_TUI_MOUSE") == "off" ? MouseLevel.Off : MouseLevel.Basic, env("TERM") == "dumb");
    }
}
