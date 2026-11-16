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
