using System.Globalization;
using System.Runtime.InteropServices;

namespace Ah.Platform;

/// <summary>
/// A process's parent and start time read straight from the kernel (macOS <c>proc_pidinfo</c>), with no <c>ps</c> to start. The
/// daemon asks this about every caller, and callers are short-lived processes a <c>ps</c> reading taken a moment ago cannot hold.
/// </summary>
internal static unsafe partial class NativeProcInfo
{
    private const int ProcPidTbsdInfo = 3;
    private const int BsdInfoSize = 136;
    private const int PpidOffset = 16;
    private const int StartSecondsOffset = 120;
    private const int StartMicrosOffset = 128;

    // sysctl(CTL_KERN, KERN_PROC, KERN_PROC_PID) answers for any user's process, where proc_pidinfo refuses another user's (the
    // root-owned login every terminal session descends from): a kinfo_proc of 648 bytes, start seconds at 0, e_ppid at 560.
    private const int KinfoProcSize = 648;
    private const int KinfoParentOffset = 560;
    private const int KinfoStartSecondsOffset = 0;
    private const int KinfoStartMicrosOffset = 8;

    /// <summary>
    /// True where the kernel can be asked; false means callers fall back to <c>ps</c>. <c>AH_PROC_KERNEL=off</c> forces the
    /// fallback, so a test that scripts <c>ps</c> is answered by the script and not by the machine it runs on.
    /// </summary>
    internal static bool Supported => OperatingSystem.IsMacOS() && Environment.GetEnvironmentVariable("AH_PROC_KERNEL") != "off";

    [LibraryImport("/usr/lib/libSystem.dylib", EntryPoint = "proc_pidinfo")]
    private static partial int ProcPidInfo(int pid, int flavor, ulong arg, byte* buffer, int size);

    [LibraryImport("/usr/lib/libSystem.dylib", EntryPoint = "sysctl")]
    private static partial int Sysctl(int* name, uint length, byte* old, nuint* oldLength, byte* newValue, nuint newLength);

    /// <summary>
    /// The parent and the start time (UTC, spaced as <c>ps -o lstart=</c> prints it) of <paramref name="pid"/>. False when no such
    /// process exists, or the answer was not the whole record.
    /// </summary>
    internal static bool TryLookup(int pid, out int parent, out string start)
    {
        start = string.Empty;
        if (!TryLookupMicros(pid, out parent, out long micros))
        {
            return false;
        }

        var at = DateTimeOffset.FromUnixTimeMilliseconds(micros / 1000).UtcDateTime;
        start = string.Create(CultureInfo.InvariantCulture, $"{at:ddd MMM} {at.Day,2} {at:HH:mm:ss} {at:yyyy}");
        return true;
    }

    /// <summary>The parent and the start time, in microseconds since the epoch, of <paramref name="pid"/>; false as for the text form.</summary>
    internal static bool TryLookupMicros(int pid, out int parent, out long startMicros)
    {
        parent = 0;
        startMicros = 0;
        if (!Supported || pid <= 0)
        {
            return false;
        }

        var buffer = stackalloc byte[BsdInfoSize];
        var kinfo = stackalloc byte[KinfoProcSize];
        long seconds;
        long micros;
        try
        {
            if (ProcPidInfo(pid, ProcPidTbsdInfo, 0, buffer, BsdInfoSize) >= BsdInfoSize)
            {
                parent = (int)*(uint*)(buffer + PpidOffset);
                seconds = (long)*(ulong*)(buffer + StartSecondsOffset);
                micros = (long)*(ulong*)(buffer + StartMicrosOffset);
            }
            else
            {
                var name = stackalloc int[] { 1, 14, 1, pid };
                nuint length = KinfoProcSize;
                if (Sysctl(name, 4, kinfo, &length, null, 0) != 0 || length != KinfoProcSize)
                {
                    return false;
                }

                parent = *(int*)(kinfo + KinfoParentOffset);
                seconds = *(long*)(kinfo + KinfoStartSecondsOffset);
                micros = *(int*)(kinfo + KinfoStartMicrosOffset);
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }

        startMicros = (seconds * 1_000_000) + micros;
        return true;
    }
}
