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

    public double? Load1() => PlatformModule.MachineLoad1();

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
