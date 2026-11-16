namespace Ah.Cli.Delegation;

internal static class PathLookup
{
    /// <summary>The first PATH entry holding an executable regular file with this name.</summary>
    internal static string? OnPath(string name, string? pathVariable)
    {
        foreach (var directory in (pathVariable ?? string.Empty).Split(':'))
        {
            if (directory.Length == 0)
            {
                continue;
            }

            var candidate = Path.Combine(directory, name);
            if (IsExecutable(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    internal static bool IsExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            const UnixFileMode anyExecute = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            return (File.GetUnixFileMode(path) & anyExecute) != 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
