using Ah.Hierarchy;
using Ah.Mesh;

namespace Ah.Cli.Composition;

/// <summary>
/// What the sessions of one watch tick read in common: the pools of a team set, the exchange index over a pool list, and the
/// teams whose links were already swept (successfully). Every session of a tick is handed the same <c>now</c>, so that instant names the tick;
/// the snapshot is also dropped once it is older than <see cref="MaxAgeMs"/>, so a caller that repeats one instant reads afresh.
/// </summary>
internal sealed class TickSnapshot(double nowMs, long createdMs)
{
    internal const long MaxAgeMs = 10_000;

    private readonly Dictionary<string, IReadOnlyList<string>> _pools = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IExchangeIndex> _indexes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _swept = new(StringComparer.Ordinal);

    public double NowMs { get; } = nowMs;

    public long CreatedMs { get; } = createdMs;

    public IReadOnlyList<string> PoolsOf(string key, Func<IReadOnlyList<string>> enumerate)
    {
        lock (_pools)
        {
            if (_pools.TryGetValue(key, out var held))
            {
                return held;
            }
        }

        var made = enumerate();
        lock (_pools)
        {
            return _pools.TryAdd(key, made) ? made : _pools[key];
        }
    }

    public IExchangeIndex IndexOf(string key, Func<IExchangeIndex> create)
    {
        lock (_indexes)
        {
            if (_indexes.TryGetValue(key, out var held))
            {
                return held;
            }
        }

        var made = create();
        lock (_indexes)
        {
            return _indexes.TryAdd(key, made) ? made : _indexes[key];
        }
    }

    /// <summary>The exchanges listed over a pool list, listed once per tick.</summary>
    public Ah.Core.Json.JsonArray ExchangesOf(string key, Func<Ah.Core.Json.JsonArray> list)
    {
        lock (_exchanges)
        {
            if (_exchanges.TryGetValue(key, out var held))
            {
                return held;
            }
        }

        var made = list();
        lock (_exchanges)
        {
            return _exchanges.TryAdd(key, made) ? made : _exchanges[key];
        }
    }

    private readonly Dictionary<string, Ah.Core.Json.JsonArray> _exchanges = new(StringComparer.Ordinal);

    private readonly object _withdrawnGate = new();
    private IReadOnlySet<string>? _withdrawn;

    /// <summary>The withdrawn request files, built once per tick: the set does not depend on the session.</summary>
    public IReadOnlySet<string> WithdrawnOnce(Func<IReadOnlySet<string>> build)
    {
        lock (_withdrawnGate)
        {
            return _withdrawn ??= build();
        }
    }

    private readonly Dictionary<string, object> _once = new(StringComparer.Ordinal);

    /// <summary>What <paramref name="build"/> returns, built once per tick for <paramref name="key"/>.</summary>
    public T Once<T>(string key, Func<T> build)
        where T : notnull
    {
        lock (_once)
        {
            if (_once.TryGetValue(key, out var held))
            {
                return (T)held;
            }
        }

        var made = build();
        lock (_once)
        {
            return _once.TryAdd(key, made) ? made : (T)_once[key];
        }
    }

    private readonly object _reachGate = new();
    private IReachTick? _reach;

    /// <summary>The scope the sessions' reached checks of this tick share, started once.</summary>
    public IReachTick ReachOnce(Func<IReachTick> begin)
    {
        lock (_reachGate)
        {
            return _reach ??= begin();
        }
    }

    /// <summary>Whether the team's links were already swept, successfully, in this tick.</summary>
    public bool Swept(string dir, string team)
    {
        lock (_swept)
        {
            return _swept.Contains(dir + "\0" + team);
        }
    }

    /// <summary>Records that the team's links were swept in this tick, so a later session of the tick does not sweep them again.</summary>
    public void MarkSwept(string dir, string team)
    {
        lock (_swept)
        {
            _ = _swept.Add(dir + "\0" + team);
        }
    }
}
