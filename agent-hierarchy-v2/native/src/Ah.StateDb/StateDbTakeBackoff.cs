using System.Collections.Concurrent;

namespace Ah.StateDb;

/// <summary>
/// The files whose take into the database failed (busy, unwritable), held back from another try for <see cref="RetryAfterMs"/> so a
/// read does not take the write lock on every call; after that the next read tries again, so one busy moment never hides a file's
/// rows for the rest of a long-lived process.
/// </summary>
internal sealed class StateDbTakeBackoff(Func<long>? nowMs = null)
{
    internal const long RetryAfterMs = 60_000;

    private readonly ConcurrentDictionary<string, long> _failedAt = new(StringComparer.Ordinal);

    private long Now => (nowMs ?? (static () => Environment.TickCount64))();

    /// <summary>Records that taking <paramref name="file"/> failed now.</summary>
    internal void Failed(string file) => _failedAt[file] = Now;

    /// <summary>Whether a take of <paramref name="file"/> failed less than <see cref="RetryAfterMs"/> ago.</summary>
    internal bool HoldsBack(string file)
    {
        if (!_failedAt.TryGetValue(file, out var at))
        {
            return false;
        }

        if (Now - at < RetryAfterMs)
        {
            return true;
        }

        _ = _failedAt.TryRemove(file, out _);
        return false;
    }
}
