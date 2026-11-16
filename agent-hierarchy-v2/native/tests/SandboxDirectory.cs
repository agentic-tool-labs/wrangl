// Linked into every *.Tests project by Directory.Build.targets and into Ah.TestKit: the one removal of an ahprobe.* sandbox directory.
namespace Ah.TestProcess;

internal static class SandboxDirectory
{
    internal const string Prefix = "ahprobe.";

    /// <summary>Removes <paramref name="root"/> when it is an <c>ahprobe.*</c> directory directly under /tmp or /private/tmp; anything else is left alone.</summary>
    internal static void Remove(string root)
    {
        if (!root.StartsWith("/private/tmp/" + Prefix, StringComparison.Ordinal) && !root.StartsWith("/tmp/" + Prefix, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            MakeWritable(root);
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // a child still holds a file open; the directory is a temporary one
        }
        catch (UnauthorizedAccessException)
        {
            // a mode the process cannot open; the directory is a temporary one
        }
    }

    /// <summary>Gives the owner rwx on <paramref name="directory"/> and every directory under it (a test may leave one read-only), never following a link.</summary>
    internal static void MakeWritable(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.SetUnixFileMode(directory, File.GetUnixFileMode(directory) | UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        foreach (var sub in Directory.EnumerateDirectories(directory))
        {
            if (new DirectoryInfo(sub).LinkTarget is null)
            {
                MakeWritable(sub);
            }
        }
    }
}
