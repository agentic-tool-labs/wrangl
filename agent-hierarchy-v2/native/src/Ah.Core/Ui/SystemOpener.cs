using System.Diagnostics.CodeAnalysis;
using Ah.Abstractions;

namespace Ah.Core.Ui;

/// <summary>Hands a URL to the system's opener (<c>open</c> or <c>xdg-open</c>).</summary>
public static class SystemOpener
{
    /// <summary>
    /// Starts <paramref name="opener"/> (found on the <c>PATH</c> in <paramref name="environment"/>) in its own session and does not
    /// wait for it or kill it: an opener may stay in the foreground for as long as the browser it starts runs. Only a failure to start
    /// one counts as having no opener.
    /// </summary>
    public static bool Open(IDetachedSpawner spawner, IReadOnlyDictionary<string, string?> environment, string opener, string url)
    {
        ArgumentNullException.ThrowIfNull(spawner);
        ArgumentNullException.ThrowIfNull(environment);
        if (FindOnPath(opener, environment.GetValueOrDefault("PATH")) is not { } path)
        {
            return false;
        }

        try
        {
            _ = spawner.SpawnDetached(new DetachedSpawnRequest([path, url], "/", environment, "/dev/null"));
            return true;
        }
        catch (Exception e) when (e is IOException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>The opener this machine has: <c>open</c> on macOS, <c>xdg-open</c> elsewhere.</summary>
    public static string Name => OperatingSystem.IsMacOS() ? "open" : "xdg-open";

    [SuppressMessage("Interoperability", "CA1416:Validate platform compatibility", Justification = "The executable runs on macOS and Linux only and nothing here is built for Windows.")]
    private static string? FindOnPath(string name, string? path)
    {
        const UnixFileMode Execute = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
        foreach (var directory in (path ?? string.Empty).Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, name);
            try
            {
                if (Path.IsPathRooted(candidate) && File.Exists(candidate) && (File.GetUnixFileMode(candidate) & Execute) != 0)
                {
                    return candidate;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // a directory that cannot be read has no opener in it
            }
        }

        return null;
    }
}
