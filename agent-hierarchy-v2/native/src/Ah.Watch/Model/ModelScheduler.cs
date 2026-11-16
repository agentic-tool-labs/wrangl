namespace Ah.Watch.Model;

/// <summary>The knobs a fixture turns; the defaults are the shipped values.</summary>
internal sealed record ModelOptions
{
    /// <summary>How long a real notice waits to merge with others for the same key.</summary>
    public int CoalesceMs { get; init; } = 100;

    /// <summary>The widest the coalescing window grows to under the duty cap.</summary>
    public int MaxCoalesceMs { get; init; } = 1600;

    /// <summary>A client counts as connected for this long after its last request.</summary>
    public int ClientGraceMs { get; init; } = 60_000;

    /// <summary>The projector's share of one core over the duty window.</summary>
    public double DutyCap { get; init; } = 0.25;

    public int DutyWindowMs { get; init; } = 10_000;

    /// <summary>
    /// The periodic reconcile of a lane runs every max(this, 10 x its last cost). It is what heals a hint the file watcher dropped or delayed, which a
    /// loaded machine does (at load 30 a bare watcher never delivered 7 of 24 writes), so this bounds how stale a slice can be.
    /// </summary>
    public int ReconcileFloorMs { get; init; } = 60_000;

    /// <summary>Whether the lanes are enumerated and read once at start, with no client connected.</summary>
    public bool SeedAtStart { get; init; } = true;
}

/// <summary>
/// Who is listening and how hard the projector may work. A client is connected while a long-poll is pending or within a grace
/// period of its last request; with none, only hot-lane event application and the start seed run. The duty cap measures the busy
/// time of the projector over a window and widens the coalescing when it is over the cap.
/// </summary>
internal sealed class ModelScheduler(ModelOptions options, Func<long> nowMs)
{
    private readonly object _gate = new();
    private readonly List<(long EndMs, long DurMs)> _busy = [];
    private int _pendingPolls;
    private long _lastRequestMs = long.MinValue / 2;
    private int _coalesceMs = options.CoalesceMs;
    private long _lastAdjustMs;

    public bool ClientConnected
    {
        get
        {
            lock (_gate)
            {
                return _pendingPolls > 0 || nowMs() - _lastRequestMs < options.ClientGraceMs;
            }
        }
    }

    public int CoalesceMs
    {
        get
        {
            lock (_gate)
            {
                return _coalesceMs;
            }
        }
    }

    /// <summary>A request arrived: the client stays connected for the grace period.</summary>
    public void Touch()
    {
        lock (_gate)
        {
            _lastRequestMs = nowMs();
        }
    }

    /// <summary>A long-poll is pending until the returned scope is disposed.</summary>
    public IDisposable BeginPoll()
    {
        lock (_gate)
        {
            _pendingPolls++;
            _lastRequestMs = nowMs();
        }

        return new Poll(this);
    }

    /// <summary>Records <paramref name="durMs"/> of projector work that ended now, and widens or narrows the coalescing window.</summary>
    public void RecordBusy(long durMs)
    {
        lock (_gate)
        {
            var now = nowMs();
            _busy.Add((now, durMs));
            _busy.RemoveAll(b => now - b.EndMs > options.DutyWindowMs);
            if (now - _lastAdjustMs < options.DutyWindowMs / 4)
            {
                return;
            }

            var share = _busy.Sum(static b => b.DurMs) / (double)options.DutyWindowMs;
            if (share > options.DutyCap)
            {
                _coalesceMs = Math.Min(options.MaxCoalesceMs, _coalesceMs * 2);
                _lastAdjustMs = now;
            }
            else if (share < options.DutyCap / 2 && _coalesceMs > options.CoalesceMs)
            {
                _coalesceMs = Math.Max(options.CoalesceMs, _coalesceMs / 2);
                _lastAdjustMs = now;
            }
        }
    }

    private long Now() => nowMs();

    private sealed class Poll(ModelScheduler owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                lock (owner._gate)
                {
                    owner._pendingPolls--;
                    owner._lastRequestMs = owner.Now();
                }
            }
        }
    }
}
