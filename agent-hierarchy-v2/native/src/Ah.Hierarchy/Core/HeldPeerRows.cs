using Ah.Core.Json;

namespace Ah.Hierarchy;

/// <summary>
/// The peer log's records, parsed once: an unchanged log costs one read of its counter, and an appended one parses only the new rows. A count
/// that does not add up means rows went (a compaction), so everything is read again. The list handed out is never changed; callers share its records.
/// </summary>
internal sealed class HeldPeerRows
{
    private readonly object _gate = new();
    private long _version;
    private long _cursor;
    private IReadOnlyList<JsonValue>? _rows;

    public IReadOnlyList<JsonValue> Read(IPeerRecordSource source)
    {
        lock (_gate)
        {
            // read first: a write during the reads below leaves the older counter, so the next call looks again
            var version = source.Version();
            if (_rows is { } held)
            {
                if (version == _version)
                {
                    return held;
                }

                var added = source.Since(_cursor);
                if (source.Count() == held.Count + added.Count)
                {
                    return Keep(version, [.. held, .. added.Select(static r => r.Row)], added);
                }
            }

            var all = source.Since(0);
            return Keep(version, [.. all.Select(static r => r.Row)], all);
        }
    }

    private JsonValue[] Keep(long version, JsonValue[] rows, IReadOnlyList<PeerRowAt> read)
    {
        _version = version;
        _cursor = read.Count > 0 ? read[^1].Seq : _cursor;
        _rows = rows;
        return rows;
    }
}
