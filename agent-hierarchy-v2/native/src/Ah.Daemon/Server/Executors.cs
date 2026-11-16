using System.Collections.Concurrent;

namespace Ah.Daemon;

/// <summary>
/// One dedicated thread running queued work in arrival order. A failed item does not stop the next one. The thread is
/// not a pool thread and carries no synchronization context, so behaviour is the same on few cores.
/// </summary>
internal sealed class SerialExecutor : IDisposable
{
    private readonly BlockingCollection<Action> _queue = [];
    private readonly Thread _thread;
    private int _pending;

    internal SerialExecutor(string name)
    {
        _thread = new Thread(Pump) { IsBackground = true, Name = name };
        _thread.Start();
    }

    internal string Name => _thread.Name!;

    /// <summary>Queued plus running items: zero means the executor is idle.</summary>
    internal int Pending => Volatile.Read(ref _pending);

    internal bool IsOnThread => Environment.CurrentManagedThreadId == _thread.ManagedThreadId;

    /// <summary>Queues <paramref name="work"/>; the task completes with its result or its exception. Continuations never run on this thread.</summary>
    internal Task<T> Run<T>(Func<T> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Interlocked.Increment(ref _pending);
        try
        {
            _queue.Add(() =>
            {
                try
                {
                    done.SetResult(work());
                }
                catch (Exception e)
                {
                    done.SetException(e);
                }
                finally
                {
                    _ = Interlocked.Decrement(ref _pending);
                }
            });
        }
        catch (InvalidOperationException)
        {
            _ = Interlocked.Decrement(ref _pending);
            done.SetCanceled();
        }

        return done.Task;
    }

    internal Task Run(Action work) => Run<bool>(() =>
    {
        work();
        return true;
    });

    private void Pump()
    {
        foreach (var item in _queue.GetConsumingEnumerable())
        {
            item();
        }
    }

    public void Dispose() => _queue.CompleteAdding();
}

/// <summary>One serial worker per state domain, created on first use. A write waiting on a lock occupies only its domain's worker.</summary>
internal sealed class DomainWorkers : IDisposable
{
    private readonly ConcurrentDictionary<string, SerialExecutor> _workers = new(StringComparer.Ordinal);

    internal SerialExecutor For(string domain) => _workers.GetOrAdd(domain, static d => new SerialExecutor($"ahd-worker-{d}"));

    /// <summary>Whether any worker has queued or running work.</summary>
    internal bool AnyBusy => _workers.Values.Any(static w => w.Pending > 0);

    /// <summary>The domains whose worker has queued or running work, comma-joined; empty when none.</summary>
    internal string BusyDomains => string.Join(",", _workers.Where(static w => w.Value.Pending > 0).Select(static w => w.Key).Order(StringComparer.Ordinal));

    public void Dispose()
    {
        foreach (var w in _workers.Values)
        {
            w.Dispose();
        }
    }
}
