using Ah.Abstractions;

namespace Ah.TestKit;

public sealed class FakeClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = now;

    public TimeSpan Offset { get; set; } = TimeSpan.Zero;

    /// <summary>When set, the offset in force at each instant (DST-aware); <see cref="Offset"/> is ignored.</summary>
    public TimeZoneInfo? Zone { get; set; }

    public TimeSpan LocalOffset(DateTimeOffset instant) => Zone?.GetUtcOffset(instant) ?? Offset;
}
