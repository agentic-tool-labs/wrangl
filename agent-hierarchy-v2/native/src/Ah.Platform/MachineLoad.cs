using Ah.Platform.Interop;

namespace Ah.Platform;

/// <summary>Readings of how loaded this machine is.</summary>
public static partial class PlatformModule
{
    /// <summary>The 1-minute load average, or null when the system does not report it.</summary>
    public static double? MachineLoad1()
    {
        Span<double> samples = stackalloc double[1];
        return Libc.GetLoadAvg(samples, 1) >= 1 ? samples[0] : null;
    }

    private static readonly Lazy<uint> HostPort = new(Libc.MachHostSelf);

    /// <summary>
    /// The machine's cumulative CPU ticks as (busy, total), busy being total minus idle (iowait counts as idle); null where they
    /// cannot be read. macOS: <c>host_statistics(HOST_CPU_LOAD_INFO)</c>; Linux: the first <c>cpu</c> line of <c>/proc/stat</c>;
    /// elsewhere (Windows, which this layer has no interop for): null. One system call or one file read, no process.
    /// </summary>
    public static (long Busy, long Total)? MachineCpuTimes()
    {
        if (OperatingSystem.IsMacOS())
        {
            Span<uint> ticks = stackalloc uint[4];
            var count = 4u;
            if (Libc.HostStatistics(HostPort.Value, Libc.HostCpuLoadInfo, ticks, ref count) != 0 || count < 4)
            {
                return null;
            }

            long total = (long)ticks[0] + ticks[1] + ticks[2] + ticks[3];
            return (total - ticks[2], total);
        }

        if (OperatingSystem.IsLinux())
        {
            try
            {
                using var reader = new StreamReader("/proc/stat");
                return ProcStatCpu(reader.ReadLine());
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// (busy, total) from the aggregate <c>cpu</c> line of <c>/proc/stat</c>: total is the sum of its first eight counters (user, nice,
    /// system, idle, iowait, irq, softirq, steal; as many as the line has), idle is idle plus iowait; null for any other line.
    /// </summary>
    internal static (long Busy, long Total)? ProcStatCpu(string? line)
    {
        var parts = line?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts is null || parts.Length < 5 || parts[0] != "cpu")
        {
            return null;
        }

        long total = 0, idle = 0;
        for (var i = 1; i < Math.Min(parts.Length, 9); i++)
        {
            if (!long.TryParse(parts[i], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n))
            {
                return null;
            }

            total += n;
            idle += i is 4 or 5 ? n : 0;
        }

        return (total - idle, total);
    }

    /// <summary>
    /// The macOS memory-pressure level as <c>normal</c>, <c>warn</c> or <c>critical</c> (kernel levels 1, 2 and 4);
    /// null elsewhere, when it cannot be read, or for a level this mapping does not know.
    /// </summary>
    public static string? MachineMemoryPressure()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return null;
        }

        nuint length = sizeof(int);
        return Libc.SysctlByName("kern.memorystatus_vm_pressure_level", out var level, ref length, 0, 0) == 0 ? PressureLevelName(level) : null;
    }

    /// <summary>The name of a kernel memory-pressure level, or null for an unknown one.</summary>
    internal static string? PressureLevelName(int level) => level switch
    {
        1 => "normal",
        2 => "warn",
        4 => "critical",
        _ => null,
    };
}
