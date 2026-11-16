using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Ah.StateDb;

/// <summary>
/// Reads a team file whose path a session may have written into its registration. The open never blocks (a FIFO would hold the
/// daemon), never follows a final symlink, accepts only a seekable file, and stops at a size cap, so a special file or a huge one
/// is a refused read, not a hang or an allocation.
/// </summary>
internal static partial class StateDbBoundedFile
{
    internal const int MaxTeamFileBytes = 1 << 20;

    private const int ReadOnly = 0;

    private static int NonBlock => OperatingSystem.IsMacOS() ? 0x4 : 0x800;

    private static int NoFollow =>
        OperatingSystem.IsMacOS() ? 0x100
        : RuntimeInformation.ProcessArchitecture is Architecture.Arm64 or Architecture.Arm ? 0x8000
        : 0x20000;

    private static int CloseOnExec => OperatingSystem.IsMacOS() ? 0x1000000 : 0x80000;

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int Open(string path, int flags);

    /// <summary>The file's bytes, or an <see cref="IOException"/> saying why they were not read.</summary>
    internal static byte[] Read(string path, int cap = MaxTeamFileBytes)
    {
        if (new FileInfo(path).LinkTarget is not null)
        {
            throw new IOException($"{path} is a symbolic link");
        }

        var fd = Open(path, ReadOnly | NonBlock | NoFollow | CloseOnExec);
        if (fd < 0)
        {
            throw new IOException($"{path} could not be opened as a plain file (errno {Marshal.GetLastPInvokeError()})");
        }

        try
        {
            var handle = new SafeFileHandle(fd, ownsHandle: false);
            var buffer = new byte[cap + 1];
            var filled = 0;
            while (filled < buffer.Length)
            {
                var n = RandomAccess.Read(handle, buffer.AsSpan(filled), filled);
                if (n == 0)
                {
                    break;
                }

                filled += n;
            }

            return filled > cap ? throw new IOException($"{path} is larger than {cap} bytes") : buffer[..filled];
        }
        catch (Exception e) when (e is UnauthorizedAccessException or NotSupportedException)
        {
            throw new IOException($"{path} is not a readable regular file: {e.Message}", e);
        }
        finally
        {
            _ = Close(fd);
        }
    }

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    private static partial int Close(int fd);
}
