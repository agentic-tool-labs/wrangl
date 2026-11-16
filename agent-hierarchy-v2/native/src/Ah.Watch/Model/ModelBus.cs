namespace Ah.Watch.Model;

/// <summary>Where a notice came from. A reconcile notice is the lowest priority and is served only when no real notice is due.</summary>
internal enum NoticeOrigin
{
    Routed,
    Hook,
    Hint,
    Reconcile,
}

internal readonly record struct LaneKey(string Lane, string Key)
{
    /// <summary>The key of a notice that asks for the lane's key set to be enumerated from truth, not for one key to be read.</summary>
    public const string KeySet = "*";

    public bool IsKeySet => Key == KeySet;
}

/// <summary>A notice taken for processing: whether a real (non-reconcile) event stood behind it, and the bus seq when it was taken.</summary>
internal sealed record TakenNotice(LaneKey Key, bool Real, long Seq, int Attempt);

/// <summary>
/// The event bus: <c>{lane, key, seq}</c> notices, never deltas. Notices coalesce per key. A key's dirty mark is cleared when the key
/// is taken, before its re-read starts, so an event that arrives during the read leaves it dirty and schedules another re-read.
/// </summary>
internal sealed class ModelBus(Func<long> nowMs) : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<LaneKey, Pending> _pending = [];
    private readonly SemaphoreSlim _signal = new(0);

    // Every dirty key, by the seq at which it went dirty (unique per key), split by real or reconcile standing. TryTake reads
    // the front of one of these instead of scanning every pending key, which is quadratic across a long batch.
    private readonly SortedDictionary<long, LaneKey> _realReady = [];
    private readonly SortedDictionary<long, LaneKey> _otherReady = [];
    private long _seq;

    public long Seq
    {
        get
        {
            lock (_gate)
            {
                return _seq;
            }
        }
    }

    /// <summary>The number of keys with a notice waiting or being read.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _pending.Count;
            }
        }
    }

    /// <summary>Queues a notice for <paramref name="key"/>; <paramref name="coalesceMs"/> is the window real notices wait to merge.</summary>
    public long Emit(LaneKey key, NoticeOrigin origin, int coalesceMs)
    {
        long seq;
        lock (_gate)
        {
            seq = ++_seq;
            var now = nowMs();
            if (!_pending.TryGetValue(key, out var p))
            {
                p = new Pending();
                _pending[key] = p;
            }

            Unindex(p);
            var wasDirty = p.Dirty;
            p.Dirty = true;
            if (origin != NoticeOrigin.Reconcile)
            {
                p.Real = true;
            }

            if (!wasDirty)
            {
                p.DueMs = origin == NoticeOrigin.Reconcile && !p.Real ? now : now + coalesceMs;
                p.Attempt = 0;
                p.RetryAtMs = 0;
            }
            else if (origin != NoticeOrigin.Reconcile && p.DueMs < now)
            {
                p.DueMs = now;
            }

            p.FirstSeq = p.FirstSeq == 0 || !wasDirty ? seq : p.FirstSeq;
            Index(key, p);
        }

        _ = _signal.Release();
        return seq;
    }

    /// <summary>
    /// The next notice that is due: real notices first (oldest first), then a reconcile notice when no real notice is due. One still in its
    /// coalesce window does not hold a reconcile back: on a loaded machine one nearly always is, and the reconcile heals what the watcher lost.
    /// The key's dirty mark is cleared here, before the caller reads.
    /// </summary>
    public TakenNotice? TryTake()
    {
        lock (_gate)
        {
            var now = nowMs();
            LaneKey? FirstDue(SortedDictionary<long, LaneKey> ready)
            {
                foreach (var (_, key) in ready)
                {
                    var p = _pending[key];
                    if (!p.InFlight && p.DueMs <= now && p.RetryAtMs <= now)
                    {
                        return key;
                    }
                }

                return null;
            }

            if ((FirstDue(_realReady) ?? FirstDue(_otherReady)) is not { } chosen)
            {
                return null;
            }

            var taken = _pending[chosen];
            var real = taken.Real;
            Unindex(taken);
            taken.Dirty = false;
            taken.Real = false;
            taken.InFlight = true;
            return new TakenNotice(chosen, real, _seq, taken.Attempt);
        }
    }

    /// <summary>The read of <paramref name="key"/> finished. A key that went dirty during the read stays queued; otherwise it is dropped.</summary>
    public void Complete(LaneKey key, int coalesceMs)
    {
        lock (_gate)
        {
            if (!_pending.TryGetValue(key, out var p))
            {
                return;
            }

            p.InFlight = false;
            if (p.Dirty)
            {
                p.DueMs = nowMs() + (p.Real ? coalesceMs : 0);
                _ = _signal.Release();
            }
            else
            {
                _ = _pending.Remove(key);
            }
        }
    }

    /// <summary>
    /// The read failed to parse or validate: the key stays dirty and is retried after 50 ms (three times), then every second. The
    /// notice keeps its real or reconcile standing.
    /// </summary>
    public void Fail(LaneKey key, bool real)
    {
        lock (_gate)
        {
            if (!_pending.TryGetValue(key, out var p))
            {
                return;
            }

            Unindex(p);
            p.InFlight = false;
            p.Dirty = true;
            p.Real |= real;
            p.Attempt++;
            p.RetryAtMs = nowMs() + (p.Attempt <= 3 ? 50 : 1000);
            Index(key, p);
        }

        _ = _signal.Release();
    }

    /// <summary>True while a real notice for <paramref name="key"/> is queued or arrived during its read.</summary>
    public bool RealPending(LaneKey key)
    {
        lock (_gate)
        {
            return _pending.TryGetValue(key, out var p) && p.Dirty && p.Real;
        }
    }

    /// <summary>The keys of a lane that have a notice waiting (for the manifest's pending list).</summary>
    public IReadOnlyList<string> PendingKeys(string lane)
    {
        lock (_gate)
        {
            return [.. _pending.Where(kv => kv.Key.Lane == lane && !kv.Key.IsKeySet && (kv.Value.Dirty || kv.Value.InFlight)).OrderBy(static kv => kv.Value.FirstSeq).Select(static kv => kv.Key.Key)];
        }
    }

    /// <summary>Whether <see cref="PendingKeys"/> of <paramref name="lane"/> would list any key; the read model's tick asks it per lane without listing them (ah2-52).</summary>
    public bool AnyPendingKey(string lane)
    {
        lock (_gate)
        {
            foreach (var (key, p) in _pending)
            {
                if (key.Lane == lane && !key.IsKeySet && (p.Dirty || p.InFlight))
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>True while a key-set notice of <paramref name="lane"/> is queued or being served.</summary>
    public bool HasKeySet(string lane)
    {
        lock (_gate)
        {
            return _pending.TryGetValue(new LaneKey(lane, LaneKey.KeySet), out var p) && (p.Dirty || p.InFlight);
        }
    }

    /// <summary>Milliseconds until the next queued key is due (0 when one is due now), or null when nothing is queued.</summary>
    public long? NextDueInMs()
    {
        lock (_gate)
        {
            var now = nowMs();
            long? next = null;
            var anyReal = _pending.Values.Any(static p => p.Dirty && p.Real);
            foreach (var p in _pending.Values)
            {
                if (!p.Dirty || p.InFlight || (!p.Real && anyReal))
                {
                    continue;
                }

                var due = Math.Max(0, Math.Max(p.DueMs, p.RetryAtMs) - now);
                next = next is null ? due : Math.Min(next.Value, due);
            }

            return next;
        }
    }

    /// <summary>Takes a dirty <paramref name="p"/> out of the ready index; call before changing its dirty, real or first-seq state.</summary>
    private void Unindex(Pending p)
    {
        if (p.Dirty)
        {
            _ = (p.Real ? _realReady : _otherReady).Remove(p.FirstSeq);
        }
    }

    /// <summary>Puts a dirty <paramref name="p"/> into the ready index under its current standing.</summary>
    private void Index(LaneKey key, Pending p)
    {
        if (p.Dirty)
        {
            (p.Real ? _realReady : _otherReady).Add(p.FirstSeq, key);
        }
    }

    public bool Wait(TimeSpan timeout, CancellationToken ct) => _signal.Wait(timeout, ct);

    public void Poke() => _ = _signal.Release();

    public void Dispose() => _signal.Dispose();

    private sealed class Pending
    {
        public bool Dirty { get; set; }

        public bool Real { get; set; }

        public bool InFlight { get; set; }

        public long DueMs { get; set; }

        public long RetryAtMs { get; set; }

        public long FirstSeq { get; set; }

        public int Attempt { get; set; }
    }
}
