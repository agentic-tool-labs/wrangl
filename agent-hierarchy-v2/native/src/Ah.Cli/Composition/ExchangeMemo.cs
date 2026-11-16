using Ah.Abstractions;
using Ah.Core.Files;
using Ah.Core.Json;

namespace Ah.Cli.Composition;

/// <summary>
/// Keeps the exchanges listed over an ordered list of pool directories across ticks. A kept listing is reused while every path the
/// listing consulted is unchanged (existence, kind, size, mtime, inode, device); anything else rebuilds it. A listing whose newest
/// consulted mtime is within <see cref="TrustWindowMs"/> of its build, or one that consulted nothing a later stat could check, is not kept; nor is one built while a listing scope is open, since the scope serves directory listings from its own cache while their stats are read live.
/// Messages live in the database, which no stat sees: with a <c>version</c>, a listing also needs the store's write counter unchanged, and may have consulted no file at all.
/// The kept arrays are shared with every caller and must not be changed.
/// </summary>
/// <param name="files">The reader the validation stats go through.</param>
/// <param name="nowMs">The current time in epoch milliseconds.</param>
/// <param name="capacity">The most listings kept; the least recently used is dropped first.</param>
/// <param name="version">The message store's write counter, or null when it cannot be read.</param>
internal sealed class ExchangeMemo(IFileReader files, Func<double> nowMs, int capacity = ExchangeMemo.DefaultCapacity, Func<long?>? version = null)
{
    internal const int DefaultCapacity = 64;

    /// <summary>A consulted file this young at build time may still be mid-write, so the listing is rebuilt next time.</summary>
    internal const double TrustWindowMs = StatTrust.TrustWindowMs;

    /// <summary>A listing unused this long is dropped.</summary>
    internal const double IdleMs = StatTrust.IdleMs;

    private readonly StatCache<Entry> _entries = new(nowMs, capacity);
    private long _hits;
    private long _builds;

    /// <summary>How many listings were answered from the memo.</summary>
    internal long Hits => Interlocked.Read(ref _hits);

    /// <summary>How many listings were built.</summary>
    internal long Builds => Interlocked.Read(ref _builds);

    /// <summary>How many listings are held.</summary>
    internal int Held => _entries.Count;

    /// <summary>The keys held, oldest use first.</summary>
    internal IReadOnlyList<string> Keys() => _entries.Keys();

    private int _counted;

    /// <summary>The listing over <paramref name="dirs"/>: the kept one when it still holds, else the one <paramref name="build"/> makes.</summary>
    internal JsonArray Get(IReadOnlyList<string> dirs, Func<JsonArray> build)
    {
        ArgumentNullException.ThrowIfNull(dirs);
        if (Interlocked.Exchange(ref _counted, 1) == 0)
        {
            Ah.Core.CoreModule.CensusAdd(this, "cli.exchange_memo", static m => m.Held);
        }

        ArgumentNullException.ThrowIfNull(build);
        var key = string.Join('\0', dirs);
        // read before the build: a write during the build leaves the entry on the older counter, so the next call rebuilds
        var versionNow = version?.Invoke();
        if (_entries.TryGet(key, (entry, now) => entry.Version == versionNow && Holds(entry, now)) is { } kept)
        {
            _ = Interlocked.Increment(ref _hits);
            return kept.Result;
        }

        var log = new FileAccessLog();
        JsonArray result;
        using (RecordingFileReader.Begin(log))
        {
            result = build();
        }

        _ = Interlocked.Increment(ref _builds);
        var seen = log.Entries();
        var builtAt = nowMs();
        if (ListingScope.IsOpen || log.Unverifiable || (seen.Length == 0 && versionNow is null) || seen.Any(s => !StatTrust.Settled(s.Stat, builtAt)))
        {
            _entries.Remove(key);
            return result;
        }

        _entries.Put(key, new Entry(result, seen, versionNow), builtAt);
        return result;
    }

    private bool Holds(Entry entry, double now)
    {
        foreach (var (path, follow, before) in entry.Seen)
        {
            FileStat? current;
            try
            {
                current = files.Stat(path, follow);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return false;
            }

            if (!StatTrust.Holds(before, current, now))
            {
                return false;
            }
        }

        return true;
    }

    private sealed record Entry(JsonArray Result, (string Path, bool Follow, FileStat? Stat)[] Seen, long? Version);
}
