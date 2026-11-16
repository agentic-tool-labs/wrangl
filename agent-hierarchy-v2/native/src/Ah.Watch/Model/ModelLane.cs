namespace Ah.Watch.Model;

/// <summary>What re-reading one key produced: the slices it feeds, as changes to apply (a null body removes the slice).</summary>
internal sealed record LaneResult(IReadOnlyList<SliceChange> Changes)
{
    /// <summary>
    /// The key's validated record, for slices composed from several lanes; null when the lane feeds only its own slices and keeps no record.
    /// <see cref="LaneRecord.Absent"/> drops the stored record.
    /// </summary>
    public LaneRecord? Record { get; init; }

    /// <summary>
    /// Slice ids to drop from memory after this result is published: no tombstone and no publish for them (evicted is not removed). A client
    /// that asks for one later gets it cold. The lane must not also return one of these ids in <see cref="Changes"/>.
    /// </summary>
    public IReadOnlyList<string> Evict { get; init; } = [];

    /// <summary>
    /// Keys of this lane whose stored records are dropped after this result is published, so the slices composed from them forget them
    /// (the composers run again for those keys, with the record absent). The slices they fed directly go in <see cref="Evict"/>.
    /// </summary>
    public IReadOnlyList<string> EvictRecords { get; init; } = [];
}

/// <summary>
/// One lane of ah state. A notice names a key; the lane re-reads that key's current truth through the state reader and returns the
/// slices the key feeds. It never carries a delta, so a duplicate or reordered notice re-reads the same truth.
/// </summary>
internal interface IModelLane
{
    string Name { get; }

    /// <summary>
    /// Re-reads <paramref name="key"/> and returns the slices it feeds. The result holds one change for every slice id the key feeds now
    /// (a null body for a slice that is gone); a slice the key fed before and is missing from the result is removed. Throws when the read fails to parse or validate: the
    /// projector then keeps the previous slice and retries.
    /// </summary>
    LaneResult Reread(string key, long nowMs);

    /// <summary>The ids of the slices <paramref name="key"/> feeds, so a manifest can list them as pending before they are read.</summary>
    IReadOnlyList<string> SliceIds(string key);

    /// <summary>The lane's key set, enumerated from truth through the state reader.</summary>
    IReadOnlyList<string> Keys();
}

/// <summary>A lane that can name the listed keys a periodic reconcile need not read again, because their truth has not moved since the last pass.</summary>
internal interface IQuietKeys
{
    /// <summary>The keys to skip in this pass; empty when the lane cannot tell. Called once per reconcile, and moves the lane's mark to now.</summary>
    IReadOnlySet<string> QuietKeys();
}

/// <summary>Counters the gates assert on. All are monotonic.</summary>
internal sealed class ModelCounters
{
    private long _applied;
    private long _rereads;
    private long _rereadRetries;
    private long _publishes;
    private long _drift;
    private long _polls;
    private long _reconcileNotices;
    private long _keySetEnumerations;
    private long _unclassifiedHints;
    private long _noncanonicalHints;
    private long _projectorFaults;
    private long _transcriptResets;
    private long _listings;
    private long _fullPasses;
    private double _lastPassMs;

    public long Applied => Interlocked.Read(ref _applied);

    public long Rereads => Interlocked.Read(ref _rereads);

    public long RereadRetries => Interlocked.Read(ref _rereadRetries);

    public long Publishes => Interlocked.Read(ref _publishes);

    public long Drift => Interlocked.Read(ref _drift);

    public long Polls => Interlocked.Read(ref _polls);

    public long ReconcileNotices => Interlocked.Read(ref _reconcileNotices);

    public long KeySetEnumerations => Interlocked.Read(ref _keySetEnumerations);

    public long UnclassifiedHints => Interlocked.Read(ref _unclassifiedHints);

    public long NoncanonicalHints => Interlocked.Read(ref _noncanonicalHints);

    /// <summary>Unexpected exceptions the projector contained instead of letting them end the process.</summary>
    public long ProjectorFaults => Interlocked.Read(ref _projectorFaults);

    /// <summary>Transcript tails started over because the file shrank or was replaced.</summary>
    public long TranscriptResets => Interlocked.Read(ref _transcriptResets);

    /// <summary>How many directory listings this model's projector passed to the disk (a listing answered from a cache is not counted).</summary>
    public long Listings => Interlocked.Read(ref _listings);

    /// <summary>How many full passes (a key-set enumeration of a lane and the re-read of every key it queued) have completed.</summary>
    public long FullPasses => Interlocked.Read(ref _fullPasses);

    /// <summary>How long the last completed full pass took, in milliseconds, 0 before any.</summary>
    public double LastPassMs => Volatile.Read(ref _lastPassMs);

    /// <summary>The type name of the last contained exception, else null.</summary>
    public string? LastFault { get; private set; }

    public void Apply() => Interlocked.Increment(ref _applied);

    public void Reread() => Interlocked.Increment(ref _rereads);

    public void RereadRetry() => Interlocked.Increment(ref _rereadRetries);

    public void Publish() => Interlocked.Increment(ref _publishes);

    public void Poll() => Interlocked.Increment(ref _polls);

    public void ReconcileNotice() => Interlocked.Increment(ref _reconcileNotices);

    public void KeySetEnumeration() => Interlocked.Increment(ref _keySetEnumerations);

    public void UnclassifiedHint() => Interlocked.Increment(ref _unclassifiedHints);

    public void NoncanonicalHint() => Interlocked.Increment(ref _noncanonicalHints);

    public void TranscriptReset() => Interlocked.Increment(ref _transcriptResets);

    public void Listed(int count) => Interlocked.Add(ref _listings, count);

    public void FullPass(double ms)
    {
        Volatile.Write(ref _lastPassMs, ms);
        _ = Interlocked.Increment(ref _fullPasses);
    }

    public void ProjectorFault(Exception e)
    {
        LastFault = e.GetType().Name;
        _ = Interlocked.Increment(ref _projectorFaults);
    }

    /// <summary>Counts a drift. Its lane and key are not kept: nothing read them, and a list of every drift grew without end (b459, ~800 a minute on a probe).</summary>
    public void Drifted(string lane, string key) => _ = Interlocked.Increment(ref _drift);
}

/// <summary>Deterministic scheduling points for the race gates: a test blocks or injects an event at exactly these places.</summary>
internal interface IProjectorHooks
{
    /// <summary>After the key's dirty mark was cleared and before its re-read starts.</summary>
    void BeforeReread(LaneKey key);

    /// <summary>After the re-read returned and before its result is published.</summary>
    void AfterReread(LaneKey key);
}
