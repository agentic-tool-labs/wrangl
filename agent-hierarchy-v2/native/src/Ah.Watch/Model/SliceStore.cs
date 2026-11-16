using System.Collections.Immutable;
using System.Security.Cryptography;
using Ah.Core.Json;

namespace Ah.Watch.Model;

/// <summary>One slice as published: <see cref="Version"/> is the publish counter value of the publish that last changed it; <see cref="AsOfMs"/> (epoch ms) comes from the slice's truth, never from the clock.</summary>
internal sealed record Slice(string Id, long Version, long AsOfMs, byte[] Body, string Hash);

/// <summary>A change to apply: a new body for a slice id, or a removal when <see cref="Body"/> is null.</summary>
internal sealed record SliceChange(string Id, byte[]? Body, long AsOfMs = 0);

/// <summary>
/// The whole published state, immutable: one reference is one consistent view of every slice and of the resolver snapshot, the team rosters
/// (by directory id and team name) that <see cref="ExchangeFiling"/> resolves parties against. The roster is internal and never served; a route
/// thread reads it here instead of the projector's record store.
/// </summary>
internal sealed record ModelState(long Publish, ImmutableDictionary<string, Slice> Slices, ImmutableDictionary<(string DirId, string Team), TeamEntry> Rosters)
{
    public static readonly ImmutableDictionary<(string DirId, string Team), TeamEntry> NoRosters = ImmutableDictionary<(string DirId, string Team), TeamEntry>.Empty;

    public ModelState(long publish, ImmutableDictionary<string, Slice> slices)
        : this(publish, slices, NoRosters)
    {
    }
}

/// <summary>What a client that last saw <c>since</c> needs: the changed slices and removed ids, or a demand to reload the manifest.</summary>
internal sealed record ModelDelta(string Epoch, long Publish, bool Resync, IReadOnlyList<Slice> Changed, IReadOnlyList<string> Removed);

/// <summary>
/// The slice store: one immutable map swapped per publish, one monotonic publish counter, and a bounded ring of tombstones. A
/// slice's version is the publish counter value, never the seq of the event that led to it. There is one writer (the projector);
/// any number of threads read <see cref="Current"/> without a lock.
/// </summary>
internal sealed class SliceStore(string epoch, int tombstoneKeepPublishes = 10_000, long tombstoneKeepMs = 600_000)
{
    private readonly object _gate = new();
    private readonly List<(string Id, long Version, long AtMs)> _tombstones = [];
    private readonly List<TaskCompletionSource> _waiters = [];
    private ModelState _state = new(0, ImmutableDictionary<string, Slice>.Empty.WithComparers(StringComparer.Ordinal));
    private long _prunedThrough;

    public string Epoch { get; } = epoch;

    public ModelState Current => Volatile.Read(ref _state);

    private int _counted;

    /// <summary>
    /// Applies <paramref name="changes"/> as one publish: a change whose body equals the current one, and a removal of an absent id, are
    /// dropped. Returns the new publish counter, or the unchanged one when nothing differed. Every slice the publish changed gets the
    /// same version, and the map is swapped once, so a reader sees all of them or none.
    /// </summary>
    public long Publish(IEnumerable<SliceChange> changes, long nowMs, ImmutableDictionary<(string DirId, string Team), TeamEntry>? rosters = null)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (Interlocked.Exchange(ref _counted, 1) == 0)
        {
            // b459: the published read model in the health census; its bodies are the bytes
            Ah.Core.CoreModule.CensusAdd(this, "watch.slices", static s => s._state.Slices.Count, static s => s._state.Slices.Values.Sum(static v => (long)v.Body.Length));
            Ah.Core.CoreModule.CensusAdd(this, "watch.slice_tombstones", static s => { lock (s._gate) { return s._tombstones.Count; } });
        }

        var state = _state;
        var slices = state.Slices;
        var next = state.Publish + 1;
        var changed = false;
        var removed = new List<string>();
        foreach (var change in changes)
        {
            if (change.Body is null)
            {
                if (slices.ContainsKey(change.Id))
                {
                    slices = slices.Remove(change.Id);
                    removed.Add(change.Id);
                    changed = true;
                }

                continue;
            }

            var hash = Convert.ToHexString(SHA256.HashData(change.Body)) + ":" + change.AsOfMs.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (slices.TryGetValue(change.Id, out var existing) && existing.Hash == hash)
            {
                continue;
            }

            slices = slices.SetItem(change.Id, new Slice(change.Id, next, change.AsOfMs, change.Body, hash));
            changed = true;
        }

        if (!changed)
        {
            if (rosters is not null && !ReferenceEquals(rosters, state.Rosters))
            {
                // The counter counts slice changes; a roster swap alone moves the snapshot, not the counter.
                Volatile.Write(ref _state, state with { Rosters = rosters });
            }

            return state.Publish;
        }

        List<TaskCompletionSource> wake;
        lock (_gate)
        {
            foreach (var id in removed)
            {
                _tombstones.Add((id, next, nowMs));
            }

            Prune(next, nowMs);
            Volatile.Write(ref _state, new ModelState(next, slices, rosters ?? state.Rosters));
            wake = [.. _waiters];
            _waiters.Clear();
        }

        foreach (var waiter in wake)
        {
            _ = waiter.TrySetResult();
        }

        return next;
    }

    /// <summary>The changes after <paramref name="since"/> among the slice ids <paramref name="subscribed"/> accepts (all slices when null).</summary>
    public ModelDelta Since(long since, Func<string, bool>? subscribed)
    {
        ModelState state;
        bool resync;
        List<string> removed;
        lock (_gate)
        {
            state = _state;
            resync = since < _prunedThrough || since > state.Publish;
            removed = [.. _tombstones.Where(t => t.Version > since && (subscribed is null || subscribed(t.Id))).Select(static t => t.Id).Distinct(StringComparer.Ordinal)];
        }

        if (resync)
        {
            return new ModelDelta(Epoch, state.Publish, true, [], []);
        }

        var changed = state.Slices.Values.Where(s => s.Version > since && (subscribed is null || subscribed(s.Id))).OrderBy(static s => s.Version).ThenBy(static s => s.Id, StringComparer.Ordinal).ToList();
        return new ModelDelta(Epoch, state.Publish, false, changed, [.. removed.Where(id => !state.Slices.ContainsKey(id))]);
    }

    /// <summary>
    /// Completes when the publish counter exceeds <paramref name="since"/>. The waiter is registered before the counter is checked, so a
    /// publish between a caller's own check and this wait cannot be missed.
    /// </summary>
    public Task WaitAfterAsync(long since, CancellationToken ct)
    {
        TaskCompletionSource waiter;
        lock (_gate)
        {
            if (_state.Publish > since)
            {
                return Task.CompletedTask;
            }

            waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add(waiter);
        }

        if (ct.CanBeCanceled)
        {
            var registration = ct.Register(static state =>
            {
                var (store, w) = ((SliceStore, TaskCompletionSource))state!;
                store.Forget(w);
                _ = w.TrySetCanceled();
            }, (this, waiter));
            _ = waiter.Task.ContinueWith(static (_, r) => ((CancellationTokenRegistration)r!).Dispose(), registration, TaskScheduler.Default);
        }

        return waiter.Task;
    }

    /// <summary>
    /// Projector thread only, like <see cref="Publish"/>: both build the next map from the current one, so a concurrent call would lose a change.
    /// Drops slices from memory with no tombstone and no publish: evicted is not removed. The publish counter does not move, so no poll
    /// wakes and no client is told anything; a client that wants an evicted slice asks for it cold.
    /// </summary>
    public void Evict(IEnumerable<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        lock (_gate)
        {
            var state = _state;
            var slices = state.Slices;
            foreach (var id in ids)
            {
                slices = slices.Remove(id);
            }

            if (!ReferenceEquals(slices, state.Slices))
            {
                Volatile.Write(ref _state, new ModelState(state.Publish, slices, state.Rosters));
            }
        }
    }

    private void Forget(TaskCompletionSource waiter)
    {
        lock (_gate)
        {
            _ = _waiters.Remove(waiter);
        }
    }

    /// <summary>The number of polls waiting for a publish (for tests).</summary>
    internal int Waiting
    {
        get
        {
            lock (_gate)
            {
                return _waiters.Count;
            }
        }
    }

    /// <summary>The ids and versions of the tombstones now held (for tests).</summary>
    internal IReadOnlyList<(string Id, long Version)> Tombstones()
    {
        lock (_gate)
        {
            return [.. _tombstones.Select(static t => (t.Id, t.Version))];
        }
    }

    private void Prune(long publish, long nowMs)
    {
        var keep = new List<(string Id, long Version, long AtMs)>();
        foreach (var t in _tombstones)
        {
            if (publish - t.Version < tombstoneKeepPublishes || nowMs - t.AtMs < tombstoneKeepMs)
            {
                keep.Add(t);
            }
            else
            {
                _prunedThrough = Math.Max(_prunedThrough, t.Version);
            }
        }

        _tombstones.Clear();
        _tombstones.AddRange(keep);
    }

    /// <summary>The slice document the client receives: id, version, as_of (epoch ms) and the body, parsed here unless <paramref name="body"/> gives it.</summary>
    internal static JsonObject ToJson(Slice slice, JsonValue? body = null) => new()
    {
        ["id"] = slice.Id,
        ["version"] = JsonValue.FromNumber(slice.Version),
        ["as_of"] = JsonValue.FromNumber(slice.AsOfMs),
        ["body"] = body ?? JsonText.Parse(slice.Body),
    };
}
