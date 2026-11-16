namespace Ah.Platform;

public static partial class PlatformModule
{
    private const int ClockThreadCpuTimeLinux = 3;
    private const int ClockThreadCpuTimeMac = 16;

    /// <summary>A meter that adds up the thread CPU of the stretches it is told about; see <see cref="Ah.Abstractions.ICpuMeter"/>.</summary>
    public static Ah.Abstractions.ICpuMeter CreateCpuMeter() => new ThreadCpuMeter();

    /// <summary>The calling thread's CPU nanoseconds (<c>clock_gettime(CLOCK_THREAD_CPUTIME_ID)</c>), or null on a platform without that clock or when the call fails.</summary>
    public static long? ThreadCpuNs()
    {
        var clock = OperatingSystem.IsMacOS() ? ClockThreadCpuTimeMac : OperatingSystem.IsLinux() ? ClockThreadCpuTimeLinux : 0;
        return clock != 0 && Interop.Libc.ClockGetTime(clock, out var time) == 0 ? (time.Seconds * 1_000_000_000L) + time.Nanoseconds : null;
    }
}
