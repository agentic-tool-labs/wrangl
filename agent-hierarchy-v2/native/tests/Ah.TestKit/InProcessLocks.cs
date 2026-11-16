using System.Collections.Concurrent;
using Ah.Abstractions;

namespace Ah.TestKit;

/// <summary>A lock provider for one process: a path is held by one taker at a time and a second taker waits. It records every path it granted.</summary>
public sealed class InProcessLocks : ILockProvider
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _granted = new();

    /// <summary>The paths granted so far, in order.</summary>
    public IReadOnlyCollection<string> Granted => [.. _granted];

    public IDisposable? TryAcquire(string lockPath) => Acquire(lockPath, TimeSpan.Zero);

    public IDisposable? Acquire(string lockPath, TimeSpan wait)
    {
        var gate = _locks.GetOrAdd(lockPath, static _ => new SemaphoreSlim(1, 1));
        if (!gate.Wait(wait))
        {
            return null;
        }

        _granted.Enqueue(lockPath);
        return new Held(gate);
    }

    private sealed class Held(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                _ = gate.Release();
            }
        }
    }
}
