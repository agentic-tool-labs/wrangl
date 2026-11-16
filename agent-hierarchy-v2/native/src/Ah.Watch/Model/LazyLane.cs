namespace Ah.Watch.Model;

/// <summary>
/// A lane that does work only while a client is connected. With none, a notice for a key reads nothing, marks the key dirty and
/// answers with the key's last result (so the slices it fed stay published), and the lane's key set is not enumerated; when a client arrives, <see cref="Arrive"/> queues the dirty keys (and the key set, if it was
/// skipped) and each is recomputed once.
/// </summary>
internal sealed class LazyLane(IModelLane inner, Func<bool> clientConnected) : IModelLane, ILeaseLane, IQuietKeys
{
    private readonly object _gate = new();
    private readonly HashSet<string> _dirty = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LaneResult> _last = new(StringComparer.Ordinal);
    private bool _keysDue;
    private long _recomputes;

    public string Name => inner.Name;

    /// <summary>The lane it gates.</summary>
    internal IModelLane Inner => inner;

    /// <summary>The keys that changed while no client was connected and have not been recomputed.</summary>
    public IReadOnlyCollection<string> DirtyKeys
    {
        get
        {
            lock (_gate)
            {
                return [.. _dirty];
            }
        }
    }

    public IReadOnlyList<string> SliceIds(string key) => inner.SliceIds(key);

    public string? KeyOfToken(string token) => (inner as ILeaseLane)?.KeyOfToken(token);

    public IReadOnlySet<string> QuietKeys() => inner is IQuietKeys named ? named.QuietKeys() : new HashSet<string>();

    public IReadOnlyList<string> LapsedKeys() => clientConnected() ? (inner as ILeaseLane)?.LapsedKeys() ?? [] : [];

    public LaneResult Reread(string key, long nowMs)
    {
        lock (_gate)
        {
            if (!clientConnected())
            {
                _ = _dirty.Add(key);
                return _last.GetValueOrDefault(key) ?? new LaneResult([]);
            }

            _ = _dirty.Remove(key);
        }

        _ = Interlocked.Increment(ref _recomputes);
        var result = inner.Reread(key, nowMs);
        lock (_gate)
        {
            _last[key] = result;
        }

        return result;
    }

    public IReadOnlyList<string> Keys()
    {
        lock (_gate)
        {
            if (!clientConnected())
            {
                _keysDue = true;
                return [];
            }

            _keysDue = false;
        }

        return inner.Keys();
    }

    /// <summary>A client arrived: queue the key-set enumeration the lane skipped and a re-read of every dirty key.</summary>
    public void Arrive(IReadModel model)
    {
        string[] dirty;
        bool keysDue;
        lock (_gate)
        {
            dirty = [.. _dirty];
            keysDue = _keysDue;
        }

        if (keysDue)
        {
            model.EmitKeySet(Name);
        }

        foreach (var key in dirty)
        {
            model.Emit(Name, key);
        }
    }
}

/// <summary>
/// A poll that runs only while a client is connected: it starts when a client arrives, ticks every <c>period</c>, and stops itself at
/// the first tick that finds no client. Each poll's duration is reported as projector work, so the duty cap sees it.
/// </summary>
internal sealed class ClientGatedPoller(Func<bool> clientConnected, TimeSpan period, Action poll, Action<long>? recordBusy = null, Func<long>? nowMs = null, Action<string>? log = null) : IDisposable
{
    private bool _faulting;
    private int _polling;

    private readonly object _gate = new();
    private readonly Func<long> _now = nowMs ?? (static () => Environment.TickCount64);
    private Timer? _timer;
    private long _polls;
    private long _faults;

    /// <summary>How many polls ran.</summary>
    public long Polls => Interlocked.Read(ref _polls);

    /// <summary>How many polls threw. A poll runs on a timer thread or a request thread, where an escaping exception would end the process, so it is counted and dropped.</summary>
    public long Faults => Interlocked.Read(ref _faults);

    public bool Running
    {
        get
        {
            lock (_gate)
            {
                return _timer is not null;
            }
        }
    }

    /// <summary>Begins ticking (a no-op while already running).</summary>
    public void Start()
    {
        lock (_gate)
        {
            _timer ??= new Timer(static s => ((ClientGatedPoller)s!).Tick(), this, period, period);
        }
    }

    /// <summary>One tick: polls when a client is connected, otherwise stops the poller.</summary>
    public void Tick()
    {
        if (!clientConnected())
        {
            Stop();
            return;
        }

        PollNow();
    }

    /// <summary>
    /// Polls now (a registration or session event), when a client is connected. A poll still running answers for this one: the timer does not wait for
    /// the last tick, so a poll longer than the period would otherwise hold one more thread every period.
    /// </summary>
    public void PollNow()
    {
        if (!clientConnected() || Interlocked.Exchange(ref _polling, 1) == 1)
        {
            return;
        }

        var started = _now();
        try
        {
            poll();
            _ = Interlocked.Increment(ref _polls);
            Volatile.Write(ref _faulting, false);
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            _ = Interlocked.Increment(ref _faults);

            // a poll that keeps failing is logged once, not every period, until one succeeds
            if (!Interlocked.Exchange(ref _faulting, true))
            {
                log?.Invoke($"poll skipped: {failure.Message}");
            }
        }
        finally
        {
            Volatile.Write(ref _polling, 0);
        }

        recordBusy?.Invoke(_now() - started);
    }

    public void Dispose() => Stop();

    private void Stop()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }
}

/// <summary>
/// The message store's change signal: a message write raises no file event, so each poll compares the store's write counter with the last one
/// seen. A higher counter re-reads what changed since the last one; the full pass is for a poll with nothing to compare to (the first, after
/// <see cref="Forget"/>, which a client's arrival calls) or a counter that went back (a restored store). A poll that throws changes nothing, so the next one retries it.
/// </summary>
internal sealed class MsgsVersionSignal(Func<long?> version, Action fullPass, Action<long> changedSince)
{
    private const long Unseen = -1;
    private long _seen = Unseen;

    public void Forget() => Interlocked.Exchange(ref _seen, Unseen);

    public void Poll()
    {
        if (version() is not { } now)
        {
            return;
        }

        var seen = Volatile.Read(ref _seen);
        if (seen == now)
        {
            return;
        }

        if (seen == Unseen || now < seen)
        {
            fullPass();
        }
        else
        {
            changedSince(seen);
        }

        _ = Interlocked.CompareExchange(ref _seen, now, seen);
    }
}

/// <summary>
/// The read model as a client sees it, plus the arrival of a client: the first request after the model was idle queues the lazy lanes'
/// dirty keys and starts the gated pollers. Everything else is the wrapped model.
/// </summary>
internal sealed class LazyAwareModel(ReadModel model, IReadOnlyList<LazyLane> lanes, IReadOnlyList<ClientGatedPoller> pollers, IReadOnlyList<IDisposable>? owned = null, IReadOnlyList<Action>? onArrive = null, Func<IEnumerable<(string Name, long Value)>>? stats = null) : IReadModel
{
    public string Epoch => model.Epoch;

    /// <summary>The read model behind <paramref name="m"/>, whether it is one or is wrapped by this type; null for any other.</summary>
    public static ReadModel? Unwrap(IReadModel? m) => m switch
    {
        ReadModel plain => plain,
        LazyAwareModel lazy => lazy.Inner,
        _ => null,
    };

    internal ReadModel Inner => model;

    /// <summary>Counters of the lazy parts: faults of the pollers and whatever the owner of the lanes reports.</summary>
    internal IEnumerable<(string Name, long Value)> Stats() =>
        [("poller_faults", pollers.Sum(static p => p.Faults)), .. stats?.Invoke() ?? []];

    public void Emit(string lane, string key) => model.Emit(lane, key);

    public void EmitKeySet(string lane) => model.EmitKeySet(lane);

    public byte[]? Manifest(string view)
    {
        Arrive();
        return model.Manifest(view);
    }

    public Task<byte[]> PollAsync(long since, string? epoch, IReadOnlyCollection<string>? subscribed, TimeSpan wait, CancellationToken ct, IReadOnlyCollection<string>? fresh = null)
    {
        Arrive();
        return model.PollAsync(since, epoch, subscribed, wait, ct, fresh);
    }

    public Task<(int Status, byte[] Body)> ListAsync(string sliceId, IReadOnlyDictionary<string, string> query, CancellationToken ct)
    {
        Arrive();
        return model.ListAsync(sliceId, query, ct);
    }

    public void Dispose() => _ = Close();

    /// <summary>Disposes the model and answers how long each part took (watcher, projector join, archiver join, timeline store close), for the daemon's stop log.</summary>
    internal string Close()
    {
        foreach (var poller in pollers)
        {
            poller.Dispose();
        }

        var parts = new List<string> { model.Close() };
        foreach (var resource in owned ?? [])
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            resource.Dispose();
            var name = resource switch
            {
                BackgroundArchiver => "archiver join",
                SqliteTimelineStore => "timeline store close",
                _ => resource.GetType().Name,
            };
            parts.Add($"{name} {clock.ElapsedMilliseconds} ms");
        }

        return string.Join(", ", parts);
    }

    /// <summary>How long a client's arrival gets to bring the lazy lanes up to date before they are believed.</summary>
    internal const long SettleMs = 60_000;

    private long _arrivedMs = long.MaxValue;

    /// <summary>Whether the lazy lanes are current: with no client they read nothing, so what they hold can be any age, or nothing.</summary>
    internal bool Settled =>
        model.Scheduler.ClientConnected && model.NowMs - Volatile.Read(ref _arrivedMs) >= SettleMs && lanes.All(static l => l.DirtyKeys.Count == 0);

    private void Arrive()
    {
        var wasConnected = model.Scheduler.ClientConnected;
        model.Scheduler.Touch();
        if (wasConnected)
        {
            return;
        }

        Volatile.Write(ref _arrivedMs, model.NowMs);
        foreach (var lane in lanes)
        {
            lane.Arrive(model);
        }

        foreach (var signal in onArrive ?? [])
        {
            signal();
        }

        foreach (var poller in pollers)
        {
            poller.Start();
            poller.PollNow();
        }
    }
}
