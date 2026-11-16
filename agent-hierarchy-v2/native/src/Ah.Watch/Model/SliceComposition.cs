using Ah.Core.Json;

namespace Ah.Watch.Model;

/// <summary>
/// The last validated truth of one lane key, kept by the projector so slices that depend on several lanes can be recomposed without a
/// read. Immutable by contract: a lane builds it once from a re-read and nothing mutates <see cref="Value"/> afterwards. Keep it small
/// (identity, ids, timestamps, state), never bodies or free text.
/// </summary>
/// <param name="Value">The validated fields of the key.</param>
/// <param name="AsOfMs">The latest of the record's own timestamps in epoch ms, else 0; never the file mtime and never the clock.</param>
internal sealed record LaneRecord(JsonValue Value, long AsOfMs)
{
    /// <summary>The key has no record any more (the file or entry is gone). A lane returns this to drop the stored record.</summary>
    public static LaneRecord Absent { get; } = new(JsonValue.Null, 0);

    public bool IsAbsent => ReferenceEquals(this, Absent);
}

/// <summary>Read-only access to the stored lane records, for composers. It performs no reads of truth.</summary>
internal interface IRecordView
{
    /// <summary>The stored record of (<paramref name="lane"/>, <paramref name="key"/>), or null when none is stored.</summary>
    LaneRecord? Get(string lane, string key);

    /// <summary>The keys of <paramref name="lane"/> that have a stored record, in ordinal order.</summary>
    IReadOnlyList<string> Keys(string lane);
}

/// <summary>
/// One composer per slice type: a pure function from lane records to a slice. It does no I/O, reads no clock and keeps no state, so the
/// incremental path and a from-scratch rebuild produce the same bytes from the same records.
/// </summary>
internal interface ISliceComposer
{
    /// <summary>
    /// The head of the ids this composer owns: <c>team</c> for <c>team:&lt;t&gt;</c>, <c>list:exchanges:team</c> for
    /// <c>list:exchanges:team:&lt;t&gt;</c>. It owns an id that equals the prefix or starts with the prefix and a <c>:</c>; when several prefixes match an
    /// id the longest one composes it. Two composers may not claim the same prefix.
    /// </summary>
    string SlicePrefix { get; }

    /// <summary>
    /// The lanes this composer reads, at least one. Every (<see cref="SlicePrefix"/>, lane) pair must be claimed by a row of
    /// <see cref="SliceDependencies"/> (<see cref="SliceDependencies.Claims"/>): the registry refuses a composer that names an edge the table does not have.
    /// </summary>
    IReadOnlyList<string> Lanes { get; }

    /// <summary>
    /// The ids of this composer's slices that depend on the changed key. <paramref name="previous"/> is the record before the change and
    /// <paramref name="current"/> the one now stored (null when the lane keeps none, <see cref="LaneRecord.Absent"/> when it is gone);
    /// <paramref name="records"/> already holds <paramref name="current"/>. Name every slice the old or the new record touches.
    /// </summary>
    IReadOnlyList<string> Affected(LaneKey changed, LaneRecord? previous, LaneRecord? current, IRecordView records);

    /// <summary>Every slice id of this composer the stored records produce (for a from-scratch rebuild).</summary>
    IReadOnlyList<string> AllIds(IRecordView records);

    /// <summary>
    /// The slice <paramref name="sliceId"/> composed from <paramref name="records"/>: a change with the body and its <c>as_of</c> (the
    /// latest of the inputs' own timestamps, else 0), or with a null body when the records no longer produce the slice.
    /// </summary>
    SliceChange Compose(string sliceId, IRecordView records);
}

/// <summary>The lane records the projector keeps, written only by the projector thread.</summary>
internal sealed class RecordStore : IRecordView
{
    private readonly Dictionary<string, SortedDictionary<string, LaneRecord>> _lanes = new(StringComparer.Ordinal);

    public LaneRecord? Get(string lane, string key) =>
        _lanes.TryGetValue(lane, out var keys) && keys.TryGetValue(key, out var record) ? record : null;

    public IReadOnlyList<string> Keys(string lane) =>
        _lanes.TryGetValue(lane, out var keys) ? [.. keys.Keys] : [];

    /// <summary>Stores <paramref name="record"/> for the key, or drops the stored one for <see cref="LaneRecord.Absent"/>.</summary>
    public void Set(string lane, string key, LaneRecord record)
    {
        if (record.IsAbsent)
        {
            if (_lanes.TryGetValue(lane, out var keys))
            {
                _ = keys.Remove(key);
            }

            return;
        }

        if (!_lanes.TryGetValue(lane, out var target))
        {
            target = new SortedDictionary<string, LaneRecord>(StringComparer.Ordinal);
            _lanes[lane] = target;
        }

        target[key] = record;
    }
}

/// <summary>
/// The composers of the read model, registered against <see cref="SliceDependencies"/>. The projector asks which slices a changed key
/// affects and recomposes each with the composer whose prefix is the longest match for the id.
/// </summary>
internal sealed class ComposerRegistry
{
    private readonly Dictionary<string, ISliceComposer> _byPrefix = new(StringComparer.Ordinal);

    public IReadOnlyCollection<ISliceComposer> Composers => _byPrefix.Values;

    /// <summary>Registers <paramref name="composer"/>. Throws when its prefix is taken, it reads no lane, or it names a lane edge the dependency table lacks.</summary>
    public ComposerRegistry Register(ISliceComposer composer)
    {
        ArgumentNullException.ThrowIfNull(composer);
        if (composer.Lanes.Count == 0)
        {
            throw new InvalidOperationException($"the composer of {composer.SlicePrefix} reads no lane");
        }

        if (_byPrefix.ContainsKey(composer.SlicePrefix))
        {
            throw new InvalidOperationException($"a composer for {composer.SlicePrefix} is already registered");
        }

        foreach (var lane in composer.Lanes)
        {
            if (!SliceDependencies.Rows.Any(r => r.Lane == lane && SliceDependencies.Claims(composer.SlicePrefix, r.Slice)))
            {
                throw new InvalidOperationException($"{composer.SlicePrefix} reads lane {lane}, which SliceDependencies does not list as feeding it");
            }
        }

        _byPrefix.Add(composer.SlicePrefix, composer);
        return this;
    }

    /// <summary>The slice ids across all composers that depend on the changed key, each once, in ordinal order.</summary>
    public IReadOnlyList<string> Affected(LaneKey changed, LaneRecord? previous, LaneRecord? current, IRecordView records)
    {
        var ids = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var composer in _byPrefix.Values.Where(c => c.Lanes.Contains(changed.Lane, StringComparer.Ordinal)))
        {
            ids.UnionWith(composer.Affected(changed, previous, current, records));
        }

        return [.. ids];
    }

    /// <summary>Composes <paramref name="sliceId"/> with the composer whose prefix is the longest match for it.</summary>
    public SliceChange Compose(string sliceId, IRecordView records) => ComposerOf(sliceId).Compose(sliceId, records);

    /// <summary>Every composed slice the stored records produce, for a from-scratch rebuild.</summary>
    public IReadOnlyList<SliceChange> ComposeAll(IRecordView records)
    {
        var pass = records is IPassMemo ? records : new OverlayRecordView(records);
        return [.. _byPrefix.Values.SelectMany(c => c.AllIds(pass)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(id => Compose(id, pass))];
    }

    private ISliceComposer ComposerOf(string sliceId)
    {
        ISliceComposer? best = null;
        foreach (var (prefix, composer) in _byPrefix)
        {
            if ((sliceId == prefix || sliceId.StartsWith(prefix + ":", StringComparison.Ordinal)) && (best is null || prefix.Length > best.SlicePrefix.Length))
            {
                best = composer;
            }
        }

        return best ?? throw new InvalidOperationException("no composer for slice " + sliceId);
    }
}

/// <summary>
/// A view that holds still for the length of one composition pass, so a composer may keep something it derived from the records
/// for the other slices of the same pass. The memo lives and dies with the view; a view whose records can change must not offer it.
/// </summary>
internal interface IPassMemo
{
    /// <summary>The value kept under <paramref name="key"/> for this pass, made by <paramref name="make"/> the first time it is asked for.</summary>
    T GetOrAdd<T>(string key, Func<T> make)
        where T : class;
}

/// <summary>The stored records with a batch of pending ones laid over them, for composers that run before the batch is committed.</summary>
internal sealed class OverlayRecordView(IRecordView inner) : IRecordView, IPassMemo
{
    private readonly Dictionary<(string Lane, string Key), LaneRecord> _over = [];
    private readonly Dictionary<string, object> _memo = new(StringComparer.Ordinal);

    public T GetOrAdd<T>(string key, Func<T> make)
        where T : class
    {
        if (!_memo.TryGetValue(key, out var held))
        {
            held = make();
            _memo[key] = held;
        }

        return (T)held;
    }

    // The lanes with a pending record, and each one's merged keys until the next Set on it: Keys is asked per changed key of a batch.
    private readonly HashSet<string> _overLanes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<string>> _keys = new(StringComparer.Ordinal);

    public void Set(string lane, string key, LaneRecord record)
    {
        _over[(lane, key)] = record;
        _ = _overLanes.Add(lane);
        _ = _keys.Remove(lane);
    }

    public LaneRecord? Get(string lane, string key) =>
        _over.TryGetValue((lane, key), out var record) ? (record.IsAbsent ? null : record) : inner.Get(lane, key);

    public IReadOnlyList<string> Keys(string lane)
    {
        if (!_overLanes.Contains(lane))
        {
            return inner.Keys(lane);
        }

        if (_keys.TryGetValue(lane, out var held))
        {
            return held;
        }

        var keys = new SortedSet<string>(inner.Keys(lane), StringComparer.Ordinal);
        foreach (var ((l, k), record) in _over.Where(o => o.Key.Lane == lane))
        {
            _ = record.IsAbsent ? keys.Remove(k) : keys.Add(k);
        }

        IReadOnlyList<string> merged = [.. keys];
        _keys[lane] = merged;
        return merged;
    }
}
