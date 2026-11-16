using Ah.Core.Json;

namespace Ah.Cli.Composition;

/// <summary>
/// Each mailbox's brief addressees by request path (b459 step 20): held per box while its entry count and newest entry id read the same,
/// so a tick resolves the paths of only the boxes that changed (it resolved every brief of every box, about 13% of the probe's allocation).
/// Entries are only appended or pruned, and an entry's kind and ref never change. <see cref="Forget"/> is the on-demand rebuild.
/// </summary>
internal sealed class BriefAddresseeMemo
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (int Count, string? Last, IReadOnlyList<(string Key, string Name)> Briefs)> _held = new(StringComparer.Ordinal);
    private long _builds;

    /// <summary>How many boxes were read afresh rather than answered from the hold, for the tests.</summary>
    internal long Builds => Interlocked.Read(ref _builds);

    /// <summary>The (request path key, addressee name) of each brief in the box, in entry order: a later brief of the same request wins.</summary>
    internal IReadOnlyList<(string Key, string Name)> Of(string dir, string address, IReadOnlyList<JsonValue> entries)
    {
        var last = entries.Count > 0 && entries[^1].Member("id") is { IsText: true } id ? id.AsString() : null;
        lock (_gate)
        {
            if (_held.TryGetValue(dir, out var held) && held.Count == entries.Count && held.Last == last)
            {
                return held.Briefs;
            }
        }

        _ = Interlocked.Increment(ref _builds);
        var briefs = new List<(string, string)>();
        foreach (var entry in entries)
        {
            if (entry.IsMap && entry.Member("kind") is { IsText: true } kind && kind.AsString() == "brief"
                && entry.Member("ref") is { IsText: true } reference && reference.AsString().Length > 0
                && address.Split('@')[0] is { Length: > 0 } name)
            {
                briefs.Add((MsgRows.PathKey(reference.AsString()), name)); // keyed as the store keys a message: by its real path
            }
        }

        lock (_gate)
        {
            _held[dir] = (entries.Count, last, briefs);
        }

        return briefs;
    }

    internal void Forget()
    {
        lock (_gate)
        {
            _held.Clear();
        }
    }
}
