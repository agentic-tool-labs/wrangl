namespace Ah.Hierarchy;

/// <summary>
/// Bounds how often the status document of one hierarchy directory is recomputed. A request only marks the directory dirty; one worker
/// thread refreshes it at once when it has been quiet for the gap, otherwise when the gap has passed. However many writes arrive, a directory is refreshed at most once per gap, and
/// the last write is always followed by a refresh, so a quiet period never leaves the document stale. After each refresh the worker rests four
/// times what it took, up to <see cref="MaxRestMs"/>, so many busy directories cost at most about a fifth of a core until the rest hits that limit.
/// </summary>
internal sealed class StatusRefreshCoalescer : IDisposable
{
    internal const long MaxRestMs = 30_000;

    private long _restUntilMs;
    private readonly Action<string> _refresh;
    private readonly long _gapMs;
    private readonly Func<long> _nowMs;
    private readonly object _gate = new();
    private readonly Dictionary<string, long> _lastMs = new(StringComparer.Ordinal);
    private readonly HashSet<string> _dirty = new(StringComparer.Ordinal);
    private Thread? _worker;
    private readonly bool _ownWorker;
    private bool _disposed;

    // ownWorker false leaves the trailing refreshes to RunDue, which a test with its own clock calls.
    public StatusRefreshCoalescer(Action<string> refresh, TimeSpan gap, Func<long>? nowMs = null, bool ownWorker = true)
    {
        _ownWorker = ownWorker;
        _refresh = refresh;
        _gapMs = (long)gap.TotalMilliseconds;
        _nowMs = nowMs ?? (static () => Environment.TickCount64);
    }

    /// <summary>Asks for the directory's status to be refreshed: now when it has been quiet for the gap, otherwise by the worker at the end of the gap.</summary>
    public void Request(string dir)
    {
        var runNow = false;
        lock (_gate)
        {
            if (_disposed)
            {
                runNow = true;
            }
            else
            {
                // never on the caller's thread: a full recompute must not hold up the write that asked for it
                _ = _dirty.Add(dir);
                if (_ownWorker)
                {
                    _worker ??= StartWorker();
                }

                Monitor.PulseAll(_gate);
            }
        }

        if (runNow)
        {
            _refresh(dir);
        }
    }

    /// <summary>
    /// Stops the worker and drops the refreshes still waiting: status is derived, and the next daemon's first tick recomputes it, so a
    /// shutdown never waits on them.
    /// </summary>
    public void Dispose()
    {
        Thread? worker;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _dirty.Clear();
            worker = _worker;
            Monitor.PulseAll(_gate);
        }

        _ = worker?.Join(TimeSpan.FromSeconds(5));
    }

    /// <summary>Refreshes each directory whose gap has passed and that has a write waiting; the worker's pass, run by hand. Returns how many ran.</summary>
    internal int RunDue()
    {
        var ran = 0;
        while (TakeDue(out _) is { } due)
        {
            RefreshQuietly(due);
            ran++;
        }

        return ran;
    }

    private string? TakeDue(out long waitMs)
    {
        lock (_gate)
        {
            var now = _nowMs();
            if (now < _restUntilMs)
            {
                waitMs = _restUntilMs - now;
                return null;
            }

            long? wait = null;
            foreach (var dir in _dirty)
            {
                var dueIn = _lastMs.TryGetValue(dir, out var last)
                    ? last + _gapMs - now
                    : 0;
                if (dueIn <= 0)
                {
                    _ = _dirty.Remove(dir);
                    _lastMs[dir] = now;
                    waitMs = 0;
                    return dir;
                }

                wait = wait is null ? dueIn : Math.Min(wait.Value, dueIn);
            }

            waitMs = wait ?? -1;
            return null;
        }
    }

    private void RefreshQuietly(string dir)
    {
        var started = _nowMs();
        try
        {
            _refresh(dir);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // a refresh never fails the write that asked for it
        }

        var ended = _nowMs();
        lock (_gate)
        {
            _restUntilMs = ended + Math.Min(4 * (ended - started), MaxRestMs);
        }
    }

    private Thread StartWorker()
    {
        var worker = new Thread(Run) { IsBackground = true, Name = "ah-status-refresh" };
        worker.Start();
        return worker;
    }

    private void Run()
    {
        while (true)
        {
            string? due;
            lock (_gate)
            {
                while (true)
                {
                    if (_disposed)
                    {
                        return;
                    }

                    due = TakeDue(out var wait);
                    if (due is not null)
                    {
                        break;
                    }

                    _ = wait < 0 ? Monitor.Wait(_gate) : Monitor.Wait(_gate, (int)Math.Min(wait, int.MaxValue));
                }
            }

            RefreshQuietly(due);
        }
    }
}
