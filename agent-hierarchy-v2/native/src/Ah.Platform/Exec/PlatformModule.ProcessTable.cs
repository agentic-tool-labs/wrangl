using Ah.Abstractions;

namespace Ah.Platform;

public static partial class PlatformModule
{
    /// <summary>The process table as the system <c>ps</c> reports it, run through <paramref name="runner"/>; each <c>ps</c> may take <paramref name="psLimit"/> (default 2 s).</summary>
    public static IProcessTable CreateProcessTable(IProcessRunner runner, TimeSpan? psLimit = null) => new ProcessTable(runner, psLimit);

    /// <summary>When the machine last booted, or null when it cannot be read.</summary>
    public static DateTimeOffset? BootTime(IProcessRunner runner) => Ah.Platform.BootTime.Read(runner);
}
