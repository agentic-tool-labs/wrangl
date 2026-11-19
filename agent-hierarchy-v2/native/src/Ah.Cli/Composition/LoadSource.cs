using Ah.Abstractions;
using Ah.Mesh;
using Ah.Platform;
using Ah.Watch;

namespace Ah.Cli.Composition;

/// <summary>What the daemon's load watch reads: the machine through the platform layer, the thresholds file and its state file under the ah home.</summary>
internal sealed class LoadSource(string ahHome, IProcessTable processes) : ILoadSource
{
    /// <summary>The user's thresholds file under the ah home.</summary>
    public const string ConfigName = "ahd-load.json";

    private string StatePath => Path.Combine(ahHome, "run", "load-state.json");

    public int Cpus => Environment.ProcessorCount;

    private (long Busy, long Total)? _lastCpu;

    public double? Load1() => PlatformModule.MachineLoad1();

    public (bool Readable, double? Share) CpuBusy()
    {
        var current = PlatformModule.MachineCpuTimes();
        var previous = _lastCpu;
        _lastCpu = current;
        if (current is not { } now)
        {
            return (false, null);
        }

        return (true, previous is { } before ? BusyShare(before, now) : null);
    }

    /// <summary>Δbusy / Δtotal between two readings; null when total did not advance, when busy or idle went backwards, or outside 0..1.</summary>
    internal static double? BusyShare((long Busy, long Total) before, (long Busy, long Total) now)
    {
        if (now.Total <= before.Total || now.Busy < before.Busy || now.Total - now.Busy < before.Total - before.Busy)
        {
            return null;
        }

        var share = (double)(now.Busy - before.Busy) / (now.Total - before.Total);
        return share is >= 0 and <= 1 ? share : null;
    }

    public string? MemoryPressure() => PlatformModule.MachineMemoryPressure();

    public int Harnesses() => processes.All() is { } all ? all.Count(p => MeshModule.IsHarnessCommand(p.Command)) : 0;

    public string? ConfigText()
    {
        try
        {
            return File.ReadAllText(Path.Combine(ahHome, ConfigName));
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    public string? ReadState()
    {
        try
        {
            return File.ReadAllText(StatePath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void WriteState(string text)
    {
        var temp = StatePath + "." + Environment.ProcessId + ".tmp";
        _ = Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        File.WriteAllText(temp, text);
        File.Move(temp, StatePath, overwrite: true);
    }
}
