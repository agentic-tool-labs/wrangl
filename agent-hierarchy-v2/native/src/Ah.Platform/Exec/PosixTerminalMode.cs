using System.Runtime.Versioning;
using Ah.Abstractions;
using Ah.Platform.Interop;

namespace Ah.Platform;

/// <summary>Raw mode by <c>tcgetattr</c>, <c>cfmakeraw</c> and <c>tcsetattr</c>, over an opaque settings buffer so no system's <c>struct termios</c> layout is assumed.</summary>
[UnsupportedOSPlatform("windows")]
internal sealed class PosixTerminalMode : ITerminalMode
{
    /// <summary>Larger than <c>struct termios</c> on macOS (72 bytes) and Linux (60 bytes).</summary>
    private const int SettingsBytes = 256;

    private const int TcsaNow = 0;

    public bool IsTerminal(int fd) => Libc.IsATty(fd) == 1;

    public unsafe IDisposable? EnterRaw(int fd)
    {
        var saved = new byte[SettingsBytes];
        fixed (byte* p = saved)
        {
            if (Libc.TcGetAttr(fd, p) != 0)
            {
                return null;
            }
        }

        var raw = new byte[SettingsBytes];
        Array.Copy(saved, raw, SettingsBytes);
        fixed (byte* r = raw)
        {
            Libc.CfMakeRaw(r);
            if (Libc.TcSetAttr(fd, TcsaNow, r) != 0)
            {
                return null;
            }
        }

        return new Restore(fd, saved);
    }

    private sealed class Restore(int fd, byte[] saved) : IDisposable
    {
        private int _done;

        public unsafe void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) != 0)
            {
                return;
            }

            fixed (byte* p = saved)
            {
                _ = Libc.TcSetAttr(fd, TcsaNow, p);
            }
        }
    }
}
