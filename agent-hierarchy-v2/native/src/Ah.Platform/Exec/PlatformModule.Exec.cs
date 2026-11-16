using System.Runtime.InteropServices;
using Ah.Abstractions;
using Ah.Platform.Interop;

namespace Ah.Platform;

public static partial class PlatformModule
{
    public static IExec CreateExec() => new PosixExec();

    /// <summary>The terminal's line discipline: whether a descriptor is a terminal, and raw mode with the settings put back on dispose.</summary>
    public static ITerminalMode CreateTerminalMode() => new PosixTerminalMode();

    /// <summary>
    /// The process runner: a fixed argv started with <c>posix_spawn</c> in its own session, output captured and capped, the process group
    /// killed on timeout or overrun, the child's environment exactly the request's map (this process's own when none is given).
    /// </summary>
    public static IProcessRunner CreateProcessRunner() => new ProcessRunner();

    /// <summary>The effective user id of this process.</summary>
    public static uint CurrentUid() => Libc.GetUid();

    /// <summary>The process id of this process's parent (<c>process.ppid</c> in Node): the session that ran the hook.</summary>
    public static int ParentProcessId() => Libc.GetPpid();

    /// <summary>Sends <paramref name="signal"/> to process <paramref name="pid"/>; true when it was delivered.</summary>
    public static bool SignalProcess(int pid, int signal) => Libc.Kill(pid, signal) == 0;

    /// <summary>The home directory the password database lists for the current user, or null when it has none.</summary>
    public static unsafe string? PasswdHome()
    {
        var entry = Libc.GetPwUid(Libc.GetUid());
        if (entry == 0)
        {
            return null;
        }

        // struct passwd: pw_dir follows pw_class and pw_change on macOS, and pw_gecos alone on Linux.
        var offset = OperatingSystem.IsMacOS() ? 48 : 32;
        var dir = *(byte**)((byte*)entry + offset);
        return dir == null ? null : Marshal.PtrToStringUTF8((nint)dir);
    }
}
