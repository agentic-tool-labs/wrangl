using Ah.Abstractions;

namespace Ah.Core.Files;

/// <summary>The rule that decides whether something derived from file stats may be kept, and whether it still stands.</summary>
public static class StatTrust
{
    /// <summary>A path changed this recently may still be mid-write, so what rests on it is not kept.</summary>
    public const double TrustWindowMs = 1000;

    /// <summary>A kept value unused this long is dropped.</summary>
    public const double IdleMs = 3_600_000;

    /// <summary>Whether a stat read now still stands for the one recorded earlier. A stat dated after <paramref name="nowMs"/> never does.</summary>
    /// <param name="recorded">The stat recorded when the value was made.</param>
    /// <param name="current">The stat read now.</param>
    /// <param name="nowMs">The current time in epoch milliseconds.</param>
    /// <returns>True when the two are equal and the current one is not in the future.</returns>
    public static bool Holds(FileStat? recorded, FileStat? current, double nowMs) =>
        !(current is { } c && c.MtimeMs > nowMs) && recorded == current;

    /// <summary>Whether a value made at <paramref name="builtAtMs"/> may rest on <paramref name="stat"/>: a missing path always may, an existing one only when its mtime is neither in the future nor within <see cref="TrustWindowMs"/> of the build.</summary>
    /// <param name="stat">The stat the value rests on.</param>
    /// <param name="builtAtMs">When the value was made, in epoch milliseconds.</param>
    /// <returns>True when the stat is settled.</returns>
    public static bool Settled(FileStat? stat, double builtAtMs) => stat is not { } s || builtAtMs - s.MtimeMs >= TrustWindowMs;
}

/// <summary>
/// A bounded, in-memory store of values that are each valid while some file stats stay as they were when the value was made. It holds
/// the rule every such cache shares: a value is kept only when every stat it rests on is old enough to be settled, it is served only
/// while those stats are equal to the recorded ones (the whole <see cref="FileStat"/> record, a future mtime counts as changed), the
/// least recently used value is dropped first, and a value unused for <see cref="StatTrust.IdleMs"/> is dropped.
/// </summary>
/// <typeparam name="TValue">What is kept.</typeparam>
/// <param name="nowMs">The current time in epoch milliseconds.</param>
/// <param name="capacity">The most values kept.</param>
public sealed class StatCache<TValue>(Func<double> nowMs, int capacity)
    where TValue : class
{
    private readonly Dictionary<string, Slot> _slots = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private long _tick;

    /// <summary>How many values are held.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _slots.Count;
            }
        }
    }

    /// <summary>The keys held, least recently used first.</summary>
    public IReadOnlyList<string> Keys()
    {
        lock (_gate)
        {
            return [.. _slots.OrderBy(static kv => kv.Value.Used).Select(static kv => kv.Key)];
        }
    }

    /// <summary>The value kept under <paramref name="key"/> when it is not idle and <paramref name="holds"/> accepts it; a value served this way counts as used.</summary>
    /// <param name="key">What the value answers.</param>
    /// <param name="holds">Whether the value's recorded stats still stand; run outside the cache's lock.</param>
    /// <returns>The value, or null when none is kept or it no longer holds.</returns>
    public TValue? TryGet(string key, Func<TValue, double, bool> holds)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(holds);
        Slot? kept;
        lock (_gate)
        {
            _ = _slots.TryGetValue(key, out kept);
        }

        var now = nowMs();
        if (kept is null || now - kept.UsedAtMs >= StatTrust.IdleMs || !holds(kept.Value, now))
        {
            return null;
        }

        lock (_gate)
        {
            if (_slots.TryGetValue(key, out var current) && ReferenceEquals(current, kept))
            {
                _slots[key] = kept with { Used = ++_tick, UsedAtMs = now };
            }
        }

        return kept.Value;
    }

    /// <summary>Keeps <paramref name="value"/> under <paramref name="key"/>, dropping the least recently used values past the capacity.</summary>
    /// <param name="key">What the value answers.</param>
    /// <param name="value">The value.</param>
    /// <param name="builtAtMs">When the value was made, in epoch milliseconds.</param>
    public void Put(string key, TValue value, double builtAtMs)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        lock (_gate)
        {
            _slots[key] = new Slot(value, ++_tick, builtAtMs);
            while (_slots.Count > Math.Max(1, capacity))
            {
                _ = _slots.Remove(_slots.MinBy(static kv => kv.Value.Used).Key);
            }
        }
    }

    /// <summary>Drops the value kept under <paramref name="key"/>, if any.</summary>
    /// <param name="key">What the value answers.</param>
    public void Remove(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (_gate)
        {
            _ = _slots.Remove(key);
        }
    }

    private sealed record Slot(TValue Value, long Used, double UsedAtMs);
}
