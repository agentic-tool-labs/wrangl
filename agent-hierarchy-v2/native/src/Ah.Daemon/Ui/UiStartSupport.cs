using Ah.Abstractions;

namespace Ah.Daemon;

/// <summary>What the daemon needs to start the browser page itself, as `ahd ui start` does for the user.</summary>
/// <param name="Spawner">Starts the background holder in its own session.</param>
/// <param name="ProcessPath">The <c>ahd</c> executable the holder is run from.</param>
/// <param name="StartTime">The start time of a process, as text (null when it cannot be read); the daemon's own is the one the holder's parent is checked against.</param>
/// <param name="Environment">The environment the holder and the opener are started with.</param>
/// <param name="Opener">The system's URL opener (<c>open</c> or <c>xdg-open</c>).</param>
/// <param name="Signal">Sends a signal to a pid; the daemon's reopen ends with it a holder it spawned when the daemon stops first. Null sends none.</param>
public sealed record UiStartSupport(
    IDetachedSpawner Spawner,
    string ProcessPath,
    Func<int, string?> StartTime,
    Func<IReadOnlyDictionary<string, string?>> Environment,
    string Opener,
    Func<int, int, bool>? Signal = null);
