using Ah.Abstractions;
using Ah.Platform.Spawn;

namespace Ah.Platform;

public static partial class PlatformModule
{
    /// <summary>Starts processes in a new session with their output appended to a log file.</summary>
    public static IDetachedSpawner CreateDetachedSpawner() => new PosixSpawner();

    /// <summary>The same, for a long-lived parent: each child is waited for, so one that ends leaves no zombie while this process lives.</summary>
    public static IDetachedSpawner CreateReapingDetachedSpawner() => new PosixSpawner(reap: true);
}
