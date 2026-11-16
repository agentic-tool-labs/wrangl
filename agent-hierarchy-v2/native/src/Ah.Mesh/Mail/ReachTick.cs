using Ah.Core.Json;
using Ah.Hierarchy;

namespace Ah.Mesh;

/// <summary>The scope the reached checks of one watch tick share; an opaque token that goes back to <c>IMailboxHooks.ReportReached</c>.</summary>
public interface IReachTick
{
}

/// <summary>
/// What the reached checks of one watch tick share: the registrations read, the real paths resolved, and the peer-log index built
/// from one read of the peer records. A scope is made at the start of a tick and dropped at its end, so nothing it holds outlives the tick.
/// </summary>
internal sealed class ReachTick : IReachTick
{
    private readonly Func<string, string?> _resolve;
    private readonly Func<IReadOnlyList<JsonValue>> _readRecords;
    private readonly Func<IReadOnlyList<JsonValue>, Func<string, string?>, IPeerReach> _index;
    private readonly Dictionary<string, string> _real = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonValue?> _registrations = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private IReadOnlyList<JsonValue>? _records;
    private IPeerReach? _peers;

    internal ReachTick(
        Func<string, string?> resolve,
        Func<IReadOnlyList<JsonValue>> readRecords,
        Func<IReadOnlyList<JsonValue>, Func<string, string?>, IPeerReach> index)
    {
        _resolve = resolve;
        _readRecords = readRecords;
        _index = index;
    }

    /// <summary>The real path of <paramref name="path"/>, resolved once per tick; null when it cannot be resolved.</summary>
    internal string? RealPath(string path)
    {
        lock (_gate)
        {
            if (!_real.TryGetValue(path, out var resolved))
            {
                resolved = _resolve(path);
                if (resolved is not null)
                {
                    _real[path] = resolved;
                }
            }

            return resolved;
        }
    }

    /// <summary>The registration of <paramref name="sessionId"/>, read once per tick.</summary>
    internal JsonValue? Registration(string sessionId, Func<string, JsonValue?> read)
    {
        lock (_gate)
        {
            if (!_registrations.TryGetValue(sessionId, out var registration))
            {
                registration = read(sessionId);
                _registrations[sessionId] = registration;
            }

            return registration;
        }
    }

    /// <summary>The peer records, read once per tick. An empty reading is not kept: it may be a failed read, so the next question reads again.</summary>
    internal IReadOnlyList<JsonValue> Records()
    {
        lock (_gate)
        {
            return RecordsLocked();
        }
    }

    /// <summary>The peer-log index over <see cref="Records"/>, built once per tick. An index over an empty reading is not kept.</summary>
    internal IPeerReach Peers()
    {
        lock (_gate)
        {
            if (_peers is { } built)
            {
                return built;
            }

            var records = RecordsLocked();
            var index = _index(records, RealPath);
            if (records.Count > 0)
            {
                _peers = index;
            }

            return index;
        }
    }

    private IReadOnlyList<JsonValue> RecordsLocked()
    {
        if (_records is { } held)
        {
            return held;
        }

        var read = _readRecords();
        if (read.Count > 0)
        {
            _records = read;
        }

        return read;
    }
}
