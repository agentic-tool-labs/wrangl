namespace Ah.Abstractions;

/// <summary>
/// One reading of the host's load and free memory. A field that could not be read is null; a caller must never substitute a value
/// for it. <see cref="LogicalCores"/> follows <see cref="Environment.ProcessorCount"/>.
/// </summary>
public sealed record HostSnapshot(double? LoadAverage1, int LogicalCores, double? AvailableMemoryGiB);

public interface IHostProbe
{
    /// <summary>A fresh reading on every call. Never throws: a field whose read fails or is out of range is null.</summary>
    HostSnapshot Read();
}
