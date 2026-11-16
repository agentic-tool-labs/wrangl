namespace Ah.Abstractions;

/// <summary>The terminal's line discipline, for a client that reads keys and mouse reports as bytes.</summary>
public interface ITerminalMode
{
    /// <summary>Whether file descriptor <paramref name="fd"/> is a terminal.</summary>
    bool IsTerminal(int fd);

    /// <summary>
    /// Puts the terminal on <paramref name="fd"/> in raw mode (no echo, no line editing, no signal keys, no output post-processing)
    /// and returns the handle that puts back exactly the settings it found; null when it is not a terminal or cannot be changed.
    /// </summary>
    IDisposable? EnterRaw(int fd);
}
