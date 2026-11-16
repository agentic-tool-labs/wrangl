using Ah.Abstractions;

namespace Ah.Mesh;

/// <summary>
/// Remembers the stat a value was read under, to say whether the file or directory it came from has changed since. A stat whose
/// modification time lies within a second of the read is never trusted, because a write in the same clock tick would leave it
/// unchanged. The caller holds a lock around it.
/// </summary>
internal sealed class ChangedSince
{
    private const double UntrustedWindowMs = 1000;

    private FileStat? _held;
    private double _readAtMs;

    /// <summary>True when <paramref name="now"/> is the stat the value was read under and that stat was already old at the read.</summary>
    public bool Unchanged(FileStat? now) =>
        _held is { } held && now is { } current && held == current && _readAtMs - held.MtimeMs >= UntrustedWindowMs;

    /// <summary>Records that the value was read at <paramref name="atMs"/> under <paramref name="stat"/>, taken before the read.</summary>
    public void Read(FileStat? stat, double atMs)
    {
        _held = stat;
        _readAtMs = atMs;
    }
}
