using System.Collections.Concurrent;

using Ah.Abstractions;
using Ah.Core.Text;

namespace Ah.Core.Files;

/// <summary>
/// A scope inside which <see cref="ScopedListingFileReader"/> answers each message pool directory (<c>.../msgs</c>) and each message
/// archive (<c>.../msgs/archive</c>) once, and each file inside them once: the first listing, read or stat of a path is real and later
/// ones inside the scope reuse its result. The scope flows with the calling context, so a read that is dispatched in-process inside it is
/// covered too. Nothing is shared between scopes, and outside any scope every call is real.
/// </summary>
public sealed class ListingScope : IDisposable
{
    private static readonly AsyncLocal<ListingScope?> CurrentScope = new();

    private readonly ListingScope? _outer;
    private readonly ConcurrentDictionary<string, IReadOnlyList<string>> _listed = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _texts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte[]> _bytes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(string Path, bool Follow), FileStat?> _stats = new();
    private readonly ConcurrentDictionary<string, object> _values = new(StringComparer.Ordinal);
    private int _cacheHits;
    private int _cacheMisses;
    private int _uncacheable;
    private int _memoHits;
    private int _listingHits;
    private int _listingMisses;
    private int _listingNotStored;

    private volatile bool _closed;

    private ListingScope(ListingScope? outer) => _outer = outer;

    /// <summary>What this scope has answered from the content cache, the listing cache, the disk and its own memory so far.</summary>
    public ListingCounts Counts => new(
        Volatile.Read(ref _cacheHits),
        Volatile.Read(ref _cacheMisses),
        Volatile.Read(ref _uncacheable),
        Volatile.Read(ref _memoHits),
        Volatile.Read(ref _listingHits),
        Volatile.Read(ref _listingMisses),
        Volatile.Read(ref _listingNotStored));


    internal void CountListingHit() => Interlocked.Increment(ref _listingHits);

    internal void CountListingMiss() => Interlocked.Increment(ref _listingMisses);

    internal void CountListingNotStored() => Interlocked.Increment(ref _listingNotStored);

    internal void CountCacheHit() => Interlocked.Increment(ref _cacheHits);

    internal void CountCacheMiss() => Interlocked.Increment(ref _cacheMisses);

    internal void CountUncacheable() => Interlocked.Increment(ref _uncacheable);

    internal void CountMemoHit() => Interlocked.Increment(ref _memoHits);

    internal static ListingScope? Current => CurrentScope.Value is { _closed: false } open ? open : null;

    /// <summary>Whether a scope is open in the current context. A scope its opener has disposed is closed for every context that still holds it, a task started inside the build included.</summary>
    public static bool IsOpen => Current is not null;

    /// <summary>Opens a scope for the current context; a scope already open becomes the outer one again when this is disposed, and is not shared with it.</summary>
    /// <returns>The scope; dispose it to end it.</returns>
    public static ListingScope Begin()
    {
        var scope = new ListingScope(Current);
        CurrentScope.Value = scope;
        return scope;
    }

    /// <summary>Whether a listing of <paramref name="directory"/> is one the scope answers once: a pool's <c>msgs</c> or its <c>msgs/archive</c>.</summary>
    /// <param name="directory">The directory listed.</param>
    /// <returns>True for those two path shapes.</returns>
    public static bool Covers(string directory) =>
        directory.EndsWith("/msgs", StringComparison.Ordinal) || directory.EndsWith("/msgs/archive", StringComparison.Ordinal);

    /// <summary>Whether <paramref name="path"/> is a file directly inside a pool, an archive, or a <c>registrations</c> directory.</summary>
    public static bool CoversFile(string path) =>
        path.LastIndexOf('/') is > 0 and var slash && (Covers(path[..slash]) || path[..slash].EndsWith("/registrations", StringComparison.Ordinal));

    /// <summary>The listing of <paramref name="directory"/> from this scope, made by <paramref name="list"/> the first time; a listing that throws is not kept.</summary>
    internal IReadOnlyList<string> Listing(string directory, Func<string, IReadOnlyList<string>> list)
    {
        if (_listed.TryGetValue(directory, out var kept))
        {
            CountMemoHit();
            return kept;
        }

        var fresh = list(directory);
        return _listed.GetOrAdd(directory, fresh);
    }

    /// <summary>The text of <paramref name="path"/> from this scope, made by <paramref name="read"/> the first time; a read that throws is not kept.</summary>
    internal string Text(string path, Func<string, string> read) => OnceIn(_texts, path, read);

    /// <summary>The bytes of <paramref name="path"/> from this scope, made by <paramref name="read"/> the first time; a read that throws is not kept. The caller gets a copy.</summary>
    internal byte[] Bytes(string path, Func<string, byte[]> read) => (byte[])OnceIn(_bytes, path, read).Clone();

    /// <summary>The stat of <paramref name="path"/> from this scope, made by <paramref name="stat"/> the first time; a stat that throws is not kept.</summary>
    internal FileStat? Stat(string path, bool follow, Func<string, bool, FileStat?> stat)
    {
        if (_stats.TryGetValue((path, follow), out var kept))
        {
            CountMemoHit();
            return kept;
        }

        return _stats.GetOrAdd((path, follow), stat(path, follow));
    }

    /// <summary>
    /// The value <paramref name="make"/> returns for <paramref name="key"/>, made the first time inside the open scope and reused for the
    /// rest of it; made every time when no scope is open. A call that throws is not kept. The value is shared by every caller in the scope,
    /// so it must not be changed after it is returned.
    /// </summary>
    /// <typeparam name="T">The kind of value.</typeparam>
    /// <param name="key">What the value answers; two callers that ask the same question use the same key.</param>
    /// <param name="make">Makes the value.</param>
    /// <returns>The value.</returns>
    public static T Once<T>(string key, Func<T> make)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(make);
        if (Current is not { } scope)
        {
            return make();
        }

        if (scope._values.TryGetValue(key, out var kept))
        {
            scope.CountMemoHit();
            return (T)kept;
        }

        return (T)scope._values.GetOrAdd(key, make()!);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _closed = true;
        CurrentScope.Value = _outer;
    }

    private TValue OnceIn<TValue>(ConcurrentDictionary<string, TValue> map, string key, Func<string, TValue> make)
    {
        if (map.TryGetValue(key, out var kept))
        {
            CountMemoHit();
            return kept;
        }

        return map.GetOrAdd(key, make(key));
    }
}

/// <summary>How a <see cref="ListingScope"/> answered its calls: files served by the content cache, files read from the disk because the cache had none, files the cache could not key, and calls answered from the scope's own memory.</summary>
/// <param name="CacheHits">Files whose content came from the cache.</param>
/// <param name="CacheMisses">Files read from the disk and stored in the cache.</param>
/// <param name="Uncacheable">Files read from the disk because no stat could key them.</param>
/// <param name="MemoHits">Reads, stats, listings and state reads answered from the scope's memory.</param>
/// <param name="ListingHits">Directory listings inside the scope answered by the cross-scope listing cache.</param>
/// <param name="ListingMisses">Directory listings inside the scope made by the disk and then kept in the listing cache.</param>
/// <param name="ListingNotStored">Directory listings inside the scope made by the disk and not kept, because the directory's stat could not vouch for them.</param>
public sealed record ListingCounts(int CacheHits, int CacheMisses, int Uncacheable, int MemoHits, int ListingHits = 0, int ListingMisses = 0, int ListingNotStored = 0);

/// <summary>
/// An <see cref="IFileReader"/> that answers the listings, reads and stats a <see cref="ListingScope"/> covers from the open scope, and
/// passes everything else through. Given a <see cref="IFileCache"/>, the first read of a covered file in a scope looks the file up there
/// by its stat before it reads the disk, and stores what it read. Inside a scope, the listing of any directory is kept across scopes (at
/// most <see cref="ListingCacheCapacity"/> directories) while the directory's stat is unchanged; outside a scope every listing is real.
/// </summary>
/// <param name="inner">The reader every call is forwarded to.</param>
/// <param name="cache">The cross-build content cache, or null for none.</param>
/// <param name="nowMs">The clock the listing cache dates its entries by, in epoch milliseconds; the system clock when null.</param>
public sealed class ScopedListingFileReader(IFileReader inner, IFileCache? cache = null, Func<double>? nowMs = null) : IFileReader
{
    /// <summary>The most directory listings the cross-scope listing cache keeps.</summary>
    public const int ListingCacheCapacity = 512;

    private readonly Func<double> _now = nowMs ?? SystemNowMs;
    private readonly StatCache<KeptListing> _listings = new(nowMs ?? SystemNowMs, ListingCacheCapacity);

    /// <inheritdoc />
    public string ReadText(string path) =>
        Covered(path) is { } scope ? scope.Text(path, ReadTextThroughCache) : inner.ReadText(path);

    /// <inheritdoc />
    public byte[] ReadBytes(string path) =>
        Covered(path) is { } scope ? scope.Bytes(path, ReadBytesThroughCache) : inner.ReadBytes(path);

    /// <inheritdoc />
    public FileTail ReadTail(string path, int maxBytes) => inner.ReadTail(path, maxBytes);

    public FileRange ReadRange(string path, long offset, int maxBytes) => inner.ReadRange(path, offset, maxBytes);

    /// <inheritdoc />
    public NoFollowRead ReadNoFollow(string path, int maxBytes) => inner.ReadNoFollow(path, maxBytes);

    /// <inheritdoc />
    public FileStat? Stat(string path, bool followSymlinks = true) =>
        Covered(path) is { } scope ? scope.Stat(path, followSymlinks, inner.Stat) : inner.Stat(path, followSymlinks);

    /// <inheritdoc />
    public bool Exists(string path) => inner.Exists(path);

    /// <inheritdoc />
    public string RealPath(string path) => inner.RealPath(path);

    /// <inheritdoc />
    public string? ReadLink(string path) => inner.ReadLink(path);

    /// <inheritdoc />
    public IReadOnlyList<string> List(string directory)
    {
        if (ListingScope.Current is not { } scope)
        {
            return inner.List(directory);
        }

        return ListingScope.Covers(directory) ? scope.Listing(directory, d => ListKept(scope, d)) : ListKept(scope, directory);
    }

    private static double SystemNowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>
    /// The listing of <paramref name="directory"/> from the listing cache while the directory's stat is what it was when the listing
    /// was made; otherwise from the disk, kept when that stat is settled. A directory whose stat cannot be read or is not a directory is listed on the disk and not kept.
    /// </summary>
    private IReadOnlyList<string> ListKept(ListingScope scope, string directory)
    {
        FileStat? stat;
        try
        {
            stat = inner.Stat(directory, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            scope.CountListingNotStored();
            return inner.List(directory);
        }

        if (stat is not { Kind: FileKind.Directory } current)
        {
            scope.CountListingNotStored();
            return inner.List(directory);
        }

        if (_listings.TryGet(directory, (kept, now) => StatTrust.Holds(kept.Stat, current, now)) is { } hit)
        {
            scope.CountListingHit();
            return hit.Names;
        }

        var names = Array.AsReadOnly([.. inner.List(directory)]);
        var builtAt = _now();
        if (StatTrust.Settled(current, builtAt))
        {
            _listings.Put(directory, new KeptListing(current, names), builtAt);
            scope.CountListingMiss();
        }
        else
        {
            _listings.Remove(directory);
            scope.CountListingNotStored();
        }

        return names;
    }

    private sealed record KeptListing(FileStat Stat, IReadOnlyList<string> Names);

    /// <inheritdoc />
    public bool IsExecutable(string path) => inner.IsExecutable(path);

    private static ListingScope? Covered(string path) => ListingScope.Current is { } scope && ListingScope.CoversFile(path) ? scope : null;

    private string ReadTextThroughCache(string path) =>
        cache is not null && CachedBytes(path) is { } bytes ? Utf8Text.Decode(bytes) : inner.ReadText(path);

    private byte[] ReadBytesThroughCache(string path) =>
        cache is not null && CachedBytes(path) is { } bytes ? bytes : inner.ReadBytes(path);

    /// <summary>The file's bytes from the cache, or from the disk (and then kept in the cache); null when the file cannot be keyed, so the caller reads it as it always did.</summary>
    private byte[]? CachedBytes(string path)
    {
        FileStat? stat;
        try
        {
            stat = Stat(path);
        }
        catch (Exception e) when (e is PlatformNotSupportedException or IOException or UnauthorizedAccessException)
        {
            ListingScope.Current?.CountUncacheable();
            return null;
        }

        if (stat is not { Kind: FileKind.File } keyed)
        {
            ListingScope.Current?.CountUncacheable();
            return null;
        }

        if (cache!.Lookup(path, keyed) is { } hit)
        {
            ListingScope.Current?.CountCacheHit();
            return hit;
        }

        ListingScope.Current?.CountCacheMiss();
        var read = inner.ReadBytes(path);
        cache.Store(path, keyed, read);
        return read;
    }
}
