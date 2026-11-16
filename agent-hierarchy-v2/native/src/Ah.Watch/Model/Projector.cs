using Ah.Core.Json;
using System.Diagnostics;

namespace Ah.Watch.Model;

/// <summary>
/// The single writer of the slice store. It takes due notices from the bus one at a time, re-reads the key's truth through its lane,
/// and publishes the slices that changed. Real notices go first; a reconcile notice is served only when no real one is queued. A
/// failed read keeps the previous slice and retries. <see cref="Step"/> does one unit of work and is what the tests drive; the thread
/// started by <see cref="Start"/> only calls it.
/// </summary>
internal sealed class Projector(
    ModelBus bus,
    SliceStore store,
    IReadOnlyDictionary<string, IModelLane> lanes,
    ModelScheduler scheduler,
    ModelCounters counters,
    Func<long> nowMs,
    IProjectorHooks? hooks = null,
    Action? onIdle = null,
    ComposerRegistry? composers = null,
    Func<bool>? seeding = null,
    Action<string>? keySetTaken = null,
    Func<long>? laneNowMs = null) : IDisposable
{
    private readonly RecordStore _records = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ParkedSlice> _parked = new(StringComparer.Ordinal);

    /// <summary>The committed records. Projector thread only: the projector writes them, so read them from elsewhere only when it is idle (tests, after a drain).</summary>
    internal IRecordView Records => _records;
    private readonly Dictionary<LaneKey, HashSet<string>> _feeds = [];
    private readonly HashSet<LaneKey> _settled = [];
    private readonly HashSet<LaneKey> _batchKeys = [];
    private readonly HashSet<string> _deferred = new(StringComparer.Ordinal);
    private long _lastListMs;
    private readonly CancellationTokenSource _stop = new();
    private Thread? _thread;
    private int _disposed;

    /// <summary>Whether the projector thread is running.</summary>
    internal bool Alive => _thread is { IsAlive: true };


    /// <summary>
    /// Processes what is due as one batch, in two phases. The read phase re-reads each distinct lane key once and stops taking notices
    /// once it has run for the coalescing window, so a long seed is several batches; the rest stays due for the next step. The compose
    /// phase composes each affected slice exactly once and publishes the batch with one map swap. Returns false when nothing was due.
    /// </summary>
    public bool Step()
    {
        var worked = false;
        _settled.Clear();
        var reads = new List<(TakenNotice Taken, LaneResult Result)>();
        var seen = _batchKeys;
        seen.Clear();
        var started = Stopwatch.GetTimestamp();
        var begin = nowMs();
        using var scope = Ah.Core.Files.ListingScope.Begin();
        while (!_stop.IsCancellationRequested && bus.TryTake() is { } taken)
        {
            worked = true;
            var key = taken.Key;
            if (!lanes.TryGetValue(key.Lane, out var lane))
            {
                bus.Complete(key, scheduler.CoalesceMs);
                continue;
            }

            if (key.IsKeySet)
            {
                keySetTaken?.Invoke(key.Lane);
                try
                {
                    EnumerateKeys(lane, taken.Real);
                    bus.Complete(key, scheduler.CoalesceMs);
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    counters.ProjectorFault(e);
                    bus.Fail(key, taken.Real);
                }

                if (nowMs() - begin >= scheduler.CoalesceMs)
                {
                    break;
                }

                continue;
            }

            try
            {
                var child = taken.Real ? Ah.Core.Files.ListingScope.Begin() : null;
                try
                {
                    if (seen.Add(key) && ReadKey(lane, taken) is { } result)
                    {
                        reads.Add((taken, result));
                    }
                }
                finally
                {
                    if (child is not null)
                    {
                        counters.Listed(child.Counts.ListingMisses + child.Counts.ListingNotStored);
                        child.Dispose();
                    }
                }
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                counters.ProjectorFault(e);
                Settle(taken, failed: true);
            }

            if (nowMs() - begin >= scheduler.CoalesceMs)
            {
                break;
            }
        }

        counters.Listed(scope.Counts.ListingMisses + scope.Counts.ListingNotStored);
        scope.Dispose();
        if (reads.Count == 0 && !(HasDeferred && ListsDue()))
        {
            return worked;
        }

        try
        {
            ApplyBatch(reads);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            counters.ProjectorFault(e);
            foreach (var (taken, _) in reads.Where(r => !_settled.Contains(r.Taken.Key)))
            {
                Settle(taken, failed: true);
            }
        }
        finally
        {
            scheduler.RecordBusy((long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }

        return true;
    }

    private LaneResult? ReadKey(IModelLane lane, TakenNotice taken)
    {
        var key = taken.Key;
        hooks?.BeforeReread(key);
        LaneResult result;
        try
        {
            counters.Reread();
            result = lane.Reread(key.Key, (laneNowMs ?? nowMs)());
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            counters.RereadRetry();
            Settle(taken, failed: true);
            return null;
        }

        hooks?.AfterReread(key);
        return result;
    }

    public void Start()
    {
        if (_thread is not null)
        {
            return;
        }

        _thread = new Thread(Run) { IsBackground = true, Name = "ah-read-model", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _stop.Cancel();
        bus.Poke();
        _thread?.Join(TimeSpan.FromSeconds(5));
        _stop.Dispose();
    }

    private void Run()
    {
        var token = _stop.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (Step())
                {
                    continue;
                }

                onIdle?.Invoke();
                var wait = bus.NextDueInMs();
                if (HasDeferred)
                {
                    wait = Math.Min(wait ?? ListThrottleMs, ListThrottleMs);
                }
                _ = bus.Wait(TimeSpan.FromMilliseconds(Math.Clamp(wait ?? 1000, 1, 1000)), token);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
            {
                // a disposed bus is a stop too: never throw on this thread
                return;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                counters.ProjectorFault(e);
                try
                {
                    _ = bus.Wait(TimeSpan.FromMilliseconds(100), token);
                }
                catch (Exception stop) when (stop is OperationCanceledException or ObjectDisposedException)
                {
                    return;
                }
            }
        }
    }

    private const long ListThrottleMs = 250;

    private void Settle(TakenNotice taken, bool failed)
    {
        _ = _settled.Add(taken.Key);
        if (failed)
        {
            bus.Fail(taken.Key, taken.Real);
        }
        else
        {
            bus.Complete(taken.Key, scheduler.CoalesceMs);
        }
    }

    /// <summary>The team rosters of <paramref name="records"/> by (directory id, team name); the first of two files naming one team wins, as <see cref="TreeData.FindTeam"/> picks.</summary>
    private static System.Collections.Immutable.ImmutableDictionary<(string DirId, string Team), TeamEntry> RostersOf(OverlayRecordView records)
    {
        var map = ModelState.NoRosters.ToBuilder();
        foreach (var key in records.Keys(TeamLane.LaneName))
        {
            if (records.Get(TeamLane.LaneName, key) is not { IsAbsent: false } record)
            {
                continue;
            }

            TeamEntry team;
            try
            {
                team = TreeData.Entry(key, record);
            }
            catch (ArgumentException)
            {
                // A key that does not split into (directory, file) names no team file.
                continue;
            }

            _ = map.TryAdd((team.DirId, team.Name), team);
        }

        return map.ToImmutable();
    }

    private void ApplyBatch(List<(TakenNotice Taken, LaneResult Result)> reads)
    {
        OverlayRecordView overlay;
        List<(LaneKey Key, LaneRecord? Previous, LaneRecord Current, TakenNotice Owner)> recordChanges;
        List<SliceChange> direct;
        List<string> ids;
        while (true)
        {
            overlay = new OverlayRecordView(_records);
            recordChanges = [];
            direct = [];
            var directOwners = new Dictionary<string, List<LaneKey>>(StringComparer.Ordinal);
            foreach (var (taken, result) in reads)
            {
                var key = taken.Key;
                foreach (var change in DirectChanges(key, result))
                {
                    direct.Add(change);
                    if (!directOwners.TryGetValue(change.Id, out var owners))
                    {
                        owners = [];
                        directOwners[change.Id] = owners;
                    }

                    owners.Add(key);
                }

                if (composers is null)
                {
                    continue;
                }

                if (result.Record is { } record && !SameRecord(_records.Get(key.Lane, key.Key), record))
                {
                    recordChanges.Add((key, _records.Get(key.Lane, key.Key), record, taken));
                    overlay.Set(key.Lane, key.Key, record);
                }

                foreach (var gone in result.EvictRecords)
                {
                    if (_records.Get(key.Lane, gone) is { } was)
                    {
                        recordChanges.Add((new LaneKey(key.Lane, gone), was, LaneRecord.Absent, taken));
                        overlay.Set(key.Lane, gone, LaneRecord.Absent);
                    }
                }
            }

            var failedKeys = new HashSet<LaneKey>();
            ids = [];
            var idOwners = new Dictionary<string, List<LaneKey>>(StringComparer.Ordinal);
            foreach (var (key, previous, current, owner) in recordChanges)
            {
                try
                {
                    foreach (var id in composers!.Affected(key, previous, current, overlay))
                    {
                        if (!idOwners.TryGetValue(id, out var owners))
                        {
                            owners = [];
                            idOwners[id] = owners;
                            ids.Add(id);
                        }

                        owners.Add(owner.Key);
                    }
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    counters.ProjectorFault(e);
                    _ = failedKeys.Add(owner.Key);
                }
            }

            foreach (var id in ids.Where(directOwners.ContainsKey))
            {
                counters.ProjectorFault(new InvalidOperationException($"{id} is returned by a lane and also composed"));
                failedKeys.UnionWith(directOwners[id]);
                failedKeys.UnionWith(idOwners[id]);
            }

            if (failedKeys.Count == 0)
            {
                break;
            }

            foreach (var (taken, _) in reads.Where(r => failedKeys.Contains(r.Taken.Key)))
            {
                Settle(taken, failed: true);
            }

            _ = reads.RemoveAll(r => failedKeys.Contains(r.Taken.Key));
        }

        var listsDue = ListsDue();
        var composedIds = ids.Where(id => !IsList(id) || listsDue).ToList();
        foreach (var id in ids.Where(id => IsList(id) && !listsDue))
        {
            _ = _deferred.Add(id);
        }

        if (listsDue)
        {
            composedIds.AddRange(_deferred.Where(id => !composedIds.Contains(id)));
        }

        var composed = new List<SliceChange>();
        foreach (var id in composedIds)
        {
            _ = _deferred.Remove(id);
            if (TryCompose(id, overlay) is { } change)
            {
                composed.Add(change);
            }
        }

        if (composedIds.Any(IsList))
        {
            _lastListMs = nowMs();
        }

        var changedKeys = reads.Where(r => r.Result.Changes.Any(Differs)
            || recordChanges.Any(c => c.Owner.Key == r.Taken.Key)).Select(static r => r.Taken.Key).ToHashSet();
        var before = store.Current.Publish;
        var rosters = recordChanges.Any(static c => c.Key.Lane == TeamLane.LaneName) ? RostersOf(overlay) : null;
        var after = store.Publish([.. direct, .. composed], nowMs(), rosters);
        foreach (var (taken, result) in reads)
        {
            var key = taken.Key;
            if (result.Record is { } stored)
            {
                _records.Set(key.Lane, key.Key, stored);
            }

            foreach (var gone in result.EvictRecords)
            {
                _records.Set(key.Lane, gone, LaneRecord.Absent);
                _ = _feeds.Remove(new LaneKey(key.Lane, gone));
            }

            if (result.Evict.Count > 0)
            {
                store.Evict(result.Evict);
            }
        }

        if (after != before)
        {
            counters.Publish();
        }

        foreach (var (taken, result) in reads)
        {
            var key = taken.Key;
            counters.Apply();
            if (changedKeys.Contains(key) && !taken.Real && !bus.RealPending(key))
            {
                counters.Drifted(key.Lane, key.Key);
            }

            var fed = new HashSet<string>(result.Changes.Where(static c => c.Body is not null).Select(static c => c.Id), StringComparer.Ordinal);
            fed.ExceptWith(result.Evict);
            if (fed.Count == 0)
            {
                _ = _feeds.Remove(key);
            }
            else
            {
                _feeds[key] = fed;
            }

            Settle(taken, failed: false);
        }
    }

    private IEnumerable<SliceChange> DirectChanges(LaneKey key, LaneResult result)
    {
        var changes = result.Changes;
        if (_feeds.GetValueOrDefault(key) is { Count: > 0 } fedBefore)
        {
            var returned = new HashSet<string>(changes.Select(static c => c.Id), StringComparer.Ordinal);
            changes = [.. changes, .. fedBefore.Where(id => !returned.Contains(id) && !result.Evict.Contains(id)).Select(static id => new SliceChange(id, null))];
        }

        return changes;
    }

    private bool Differs(SliceChange change) =>
        store.Current.Slices.TryGetValue(change.Id, out var held)
            ? change.Body is null || !change.Body.AsSpan().SequenceEqual(held.Body)
            : change.Body is not null;

    private static bool IsList(string id) => id.StartsWith("list:", StringComparison.Ordinal);

    /// <summary>Whether list slices may be composed now: always after the seed, at most once per throttle period during it.</summary>
    private bool ListsDue() => !(seeding?.Invoke() ?? false) || nowMs() - _lastListMs >= ListThrottleMs;

    /// <summary>Whether list slices wait for the throttle or the end of the seed.</summary>
    internal bool HasDeferred => _deferred.Count > 0;

    /// <summary>
    /// Composes one slice. A composer that throws is counted once and its slice is parked: the previous slice stays published, nothing retries,
    /// and the slice is composed again only when a record it depends on changes. A success un-parks it.
    /// </summary>
    private SliceChange? TryCompose(string id, IRecordView view)
    {
        try
        {
            var change = composers!.Compose(id, view);
            _ = _parked.TryRemove(id, out _);
            return change;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            counters.ProjectorFault(e);
            _parked[id] = new ParkedSlice(id, e.GetType().Name, nowMs());
            return null;
        }
    }

    /// <summary>Whether a lane's new record is the one stored: the same object at once (a lane that kept its record, ah2-52), else equal text.</summary>
    internal static bool SameRecord(LaneRecord? previous, LaneRecord current) =>
        ReferenceEquals(previous, current)
        || previous is not null && previous.IsAbsent == current.IsAbsent && previous.AsOfMs == current.AsOfMs
        && JsonText.Stringify(previous.Value, 0) == JsonText.Stringify(current.Value, 0);

    /// <summary>The slices whose composer threw and has not succeeded since, oldest first.</summary>
    internal IReadOnlyList<ParkedSlice> Parked => [.. _parked.Values.OrderBy(static p => p.SinceMs).ThenBy(static p => p.SliceId, StringComparer.Ordinal)];

    private void EnumerateKeys(IModelLane lane, bool real)
    {
        counters.KeySetEnumeration();
        IReadOnlyList<string> keys;
        try
        {
            keys = lane.Keys();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            counters.RereadRetry();
            bus.Fail(new LaneKey(lane.Name, LaneKey.KeySet), real);
            return;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var all = new List<string>(keys.Where(seen.Add));
        all.AddRange(_records.Keys(lane.Name).Where(seen.Add));
        all.AddRange(_feeds.Keys.Where(k => k.Lane == lane.Name && seen.Add(k.Key)).Select(static k => k.Key));

        // While the model seeds, who a team is and what its plan holds are read ahead of everything else. As reconcile notices they wait behind every real notice and every message of a
        // large dir, and a silent team or a member with no write since a restart then reads as nothing there. A later pass is an ordinary reread.
        var ahead = lane.Name is RegistrationLane.LaneName or TeamLane.LaneName or ProgressLane.LaneName && (seeding?.Invoke() ?? false);

        // Only a key still listed can be quiet: one gone from truth is read so its slice is dropped.
        var quiet = !real && lane is IQuietKeys named ? named.QuietKeys() : null;
        var listed = quiet is { Count: > 0 } ? new HashSet<string>(keys, StringComparer.Ordinal) : null;
        foreach (var k in all)
        {
            if (!real && _batchKeys.Contains(new LaneKey(lane.Name, k)))
            {
                continue;
            }

            if (listed is not null && listed.Contains(k) && quiet!.Contains(k))
            {
                continue;
            }

            bus.Emit(new LaneKey(lane.Name, k), real || ahead ? NoticeOrigin.Routed : NoticeOrigin.Reconcile, real ? scheduler.CoalesceMs : 0);
            counters.ReconcileNotice();
        }
    }
}

/// <summary>A slice whose composer threw: the type name of the exception and when (ms) it was parked.</summary>
internal sealed record ParkedSlice(string SliceId, string ErrorType, long SinceMs);
