using System.Globalization;
using Ah.Abstractions;
using Ah.Platform.Interop;

namespace Ah.Platform;

/// <summary>
/// The host's 1-minute load average (<c>getloadavg</c>) and available memory: on macOS free + inactive + speculative pages as
/// <c>vm_stat</c> reports them, on Linux <c>MemAvailable</c>. Every read is fresh; any failure leaves that field null.
/// </summary>
internal sealed class SystemHostProbe : IHostProbe
{
    private const double Gib = 1024.0 * 1024.0 * 1024.0;
    private const string MeminfoPath = "/proc/meminfo";

    private readonly Lazy<uint> _hostPort = new(() => Libc.MachHostSelf(), LazyThreadSafetyMode.ExecutionAndPublication);

    public HostSnapshot Read() => new(ReadLoad(), Environment.ProcessorCount, ReadMemory());

    private static unsafe double? ReadLoad()
    {
        try
        {
            double sample = 0;
            return Libc.GetLoadAvg(&sample, 1) >= 1 ? Valid(sample) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private double? ReadMemory()
    {
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                return ReadMacMemory();
            }

            return OperatingSystem.IsLinux() ? ReadLinuxMemory() : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private double? ReadMacMemory()
    {
        var host = _hostPort.Value;
        if (Libc.HostPageSize(host, out var pageSize) != 0)
        {
            return null;
        }

        uint count = Libc.HostVmInfo64Count;
        if (Libc.HostStatistics64(host, Libc.HostVmInfo64, out var info, ref count) != 0)
        {
            return null;
        }

        var pages = (double)info.FreeCount + info.InactiveCount + info.SpeculativeCount;
        return Valid(pages * (double)pageSize / Gib);
    }

    private static double? ReadLinuxMemory()
    {
        foreach (var line in File.ReadLines(MeminfoPath))
        {
            if (!line.StartsWith("MemAvailable:", StringComparison.Ordinal))
            {
                continue;
            }

            var fields = line["MemAvailable:".Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length == 2 && fields[1] == "kB" && ulong.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var kb))
            {
                return Valid(kb * 1024.0 / Gib);
            }

            return null;
        }

        return null;
    }

    private static double? Valid(double value) => double.IsFinite(value) && value >= 0 ? value : null;
}
