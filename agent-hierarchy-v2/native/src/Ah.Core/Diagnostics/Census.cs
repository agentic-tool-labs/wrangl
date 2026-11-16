using Ah.Core.Json;

namespace Ah.Core.Diagnostics;

/// <summary>
/// The census of long-lived caches (b459: a Native AOT heap has no gcdump). A cache adds itself once, with how to count its entries and,
/// where it is cheap, its approximate bytes; a read asks each live owner. Owners are held weakly, so one that goes away leaves the census.
/// A read costs one call per cache: nothing walks the heap.
/// </summary>
internal static class Census
{
    private sealed record Entry(WeakReference<object> Owner, string Name, Func<object, long> Count, Func<object, long>? Bytes);

    private static readonly object Gate = new();
    private static readonly List<Entry> Entries = [];

    internal static void Add(object owner, string name, Func<object, long> count, Func<object, long>? bytes)
    {
        lock (Gate)
        {
            Entries.RemoveAll(static e => !e.Owner.TryGetTarget(out _));
            Entries.Add(new Entry(new WeakReference<object>(owner), name, count, bytes));
        }
    }

    /// <summary>Per cache name: <c>count</c> and <c>owners</c> summed over its live owners, and <c>bytes</c> when the cache can say.</summary>
    internal static JsonObject Read()
    {
        List<(object Owner, Entry Entry)> live;
        lock (Gate)
        {
            Entries.RemoveAll(static e => !e.Owner.TryGetTarget(out _));
            live = [.. Entries.Select(static e => (e.Owner.TryGetTarget(out var o) ? o : null, e)).Where(static p => p.Item1 is not null).Select(static p => (p.Item1!, p.e))];
        }

        var sums = new SortedDictionary<string, (long Count, long? Bytes, long Owners)>(StringComparer.Ordinal);
        foreach (var (owner, entry) in live)
        {
            long count, bytes = 0;
            try
            {
                count = entry.Count(owner);
                bytes = entry.Bytes?.Invoke(owner) ?? 0;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // a count that fails skips its entry, so health still answers with the others
                continue;
            }

            var held = sums.GetValueOrDefault(entry.Name);
            sums[entry.Name] = (held.Count + count, entry.Bytes is null ? held.Bytes : (held.Bytes ?? 0) + bytes, held.Owners + 1);
        }

        var result = new JsonObject();
        foreach (var (name, (count, bytes, owners)) in sums)
        {
            var row = new JsonObject { ["count"] = count, ["owners"] = owners };
            if (bytes is { } b)
            {
                row["bytes"] = b;
            }

            result[name] = row;
        }

        return result;
    }
}
