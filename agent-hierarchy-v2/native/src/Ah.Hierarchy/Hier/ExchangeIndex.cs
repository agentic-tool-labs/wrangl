using Ah.Core.Json;

namespace Ah.Hierarchy;

/// <summary>
/// The exchanges of a fixed list of pools keyed by request id, listed once so a reader that looks up one exchange per dispatch
/// row does not list the pools again for every row. A row whose own pool is not in the list is looked up in the list plus that
/// pool, listed once per pool.
/// </summary>
public interface IExchangeIndex
{
    /// <summary>The pools the index was listed over.</summary>
    IReadOnlyList<string> Pools { get; }

    /// <summary>
    /// The exchange of <paramref name="requestId"/>, looked up over the pools and, when <paramref name="rowPool"/> is not one of
    /// them, that pool as well; <see cref="JsonValue.Null"/> when none has a request with that id.
    /// </summary>
    JsonValue Find(string requestId, string? rowPool = null);
}

internal sealed class ExchangeIndex : IExchangeIndex
{
    private readonly Func<IReadOnlyList<string>, JsonArray> _list;
    private readonly Dictionary<string, JsonValue> _byId;
    private readonly Dictionary<string, Dictionary<string, JsonValue>> _withPool = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, JsonValue>> _alone = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public ExchangeIndex(IHierarchyCore core, IReadOnlyList<string> pools, Func<IReadOnlyList<string>, JsonArray>? lister = null)
    {
        ArgumentNullException.ThrowIfNull(core);
        ArgumentNullException.ThrowIfNull(pools);
        _list = lister ?? core.ListExchanges;
        Pools = pools;
        _byId = Index(pools);
    }

    public IReadOnlyList<string> Pools { get; }

    public JsonValue Find(string requestId, string? rowPool = null)
    {
        ArgumentNullException.ThrowIfNull(requestId);
        if (string.IsNullOrEmpty(rowPool) || Pools.Contains(rowPool))
        {
            return _byId.TryGetValue(requestId, out var own) ? own : JsonValue.Null;
        }

        var inOwn = _byId.TryGetValue(requestId, out var listed);
        if (inOwn && Settled(listed))
        {
            return listed;
        }

        Dictionary<string, JsonValue>? alone, extended;
        lock (_gate)
        {
            if (!_alone.TryGetValue(rowPool, out alone))
            {
                alone = Index([rowPool]);
                _alone[rowPool] = alone;
            }
        }

        if (!inOwn && alone.TryGetValue(requestId, out var cheap) && Settled(cheap))
        {
            return cheap;
        }

        lock (_gate)
        {
            if (!_withPool.TryGetValue(rowPool, out extended))
            {
                extended = Index([.. Pools, rowPool]);
                _withPool[rowPool] = extended;
            }
        }

        return extended.TryGetValue(requestId, out var found) ? found : JsonValue.Null;
    }

    /// <summary>
    /// Whether a listing over some of the pools already holds the whole exchange: a request and its response, and not the charter of
    /// a lane, whose open state is read from team files across every pool listed. Anything else may still gain a half from another pool.
    /// </summary>
    private static bool Settled(JsonValue exchange) =>
        exchange.Member("request") is { IsMap: true } && exchange.Member("response") is { IsMap: true }
        && !(exchange.Member("to") is { IsText: true } to && to.AsString() == "sub-orchestrator");

    private Dictionary<string, JsonValue> Index(IReadOnlyList<string> pools)
    {
        var byId = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
        foreach (var exchange in _list(pools))
        {
            if (exchange.Member("id") is { IsText: true } id)
            {
                _ = byId.TryAdd(id.AsString(), exchange);
            }
        }

        return byId;
    }
}
