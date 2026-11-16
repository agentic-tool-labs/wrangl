using System.Runtime.InteropServices;

namespace Ah.Platform.Interop;

internal static partial class Libc
{
    internal const int SigPipe = 13;

    /// <summary>A buffer at least as large as <c>struct sigaction</c> on any supported system; all zero bytes is the default action.</summary>
    internal const int SigactionBytes = 256;

    /// <summary>SIGUSR1's number on this platform.</summary>
    internal static int SigUsr1 { get; } = OperatingSystem.IsMacOS() ? 30 : 10;

    /// <summary>
    /// The signals a child process must start with at their default action, whichever way it is started: an ignored signal survives
    /// exec (SIGPIPE, which the runtime ignores), and on macOS a signal whose handler exec reset keeps SA_SIGINFO (SIGUSR1, the one a
    /// child .NET runtime chains its thread-suspension handler to).
    /// </summary>
    internal static IReadOnlyList<int> ChildDefaultSignals { get; } = [SigPipe, SigUsr1];

    [LibraryImport("libc", EntryPoint = "execve", SetLastError = true)]
    internal static unsafe partial int Execve(byte* path, byte** argv, byte** envp);

    /// <summary><c>sigaction</c> over opaque <see cref="SigactionBytes"/> buffers; either may be null.</summary>
    [LibraryImport("libc", EntryPoint = "sigaction", SetLastError = true)]
    internal static unsafe partial int Sigaction(int signal, byte* action, byte* previous);

    [LibraryImport("libc", EntryPoint = "isatty")]
    internal static partial int IsATty(int fd);

    /// <summary><c>tcgetattr</c> into an opaque buffer that is at least as large as <c>struct termios</c> on any supported system.</summary>
    [LibraryImport("libc", EntryPoint = "tcgetattr")]
    internal static unsafe partial int TcGetAttr(int fd, byte* termios);

    [LibraryImport("libc", EntryPoint = "tcsetattr")]
    internal static unsafe partial int TcSetAttr(int fd, int optionalActions, byte* termios);

    [LibraryImport("libc", EntryPoint = "cfmakeraw")]
    internal static unsafe partial void CfMakeRaw(byte* termios);

    [LibraryImport("libc", EntryPoint = "getuid")]
    internal static partial uint GetUid();

    [LibraryImport("libc", EntryPoint = "getppid")]
    internal static partial int GetPpid();

    [LibraryImport("libc", EntryPoint = "getpwuid")]
    internal static partial nint GetPwUid(uint uid);
}
