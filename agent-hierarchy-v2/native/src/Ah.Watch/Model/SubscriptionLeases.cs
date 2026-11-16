namespace Ah.Watch.Model;

/// <summary>
/// Which slice ids are subscribed right now, by any <c>/events</c> connection. A token (a view name or a slice id) is live while a poll holds
/// it open and for <see cref="LapseMs"/> after the last poll let it go. A lane that is expensive per key asks <see cref="IsSubscribed"/> before it
/// reads, and learns of a new subscription through <see cref="Started"/>.
/// </summary>
internal sealed class SubscriptionLeases(Func<long> nowMs, long lapseMs = 30_000)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (int Open, long LastMs)> _tokens = new(StringComparer.Ordinal);

    public long LapseMs => lapseMs;

    /// <summary>Raised on the polling thread when a token goes from not live to live. Handlers must only queue work.</summary>
    public event Action<string>? Started;

    /// <summary>Marks <paramref name="tokens"/> live until the returned handle is disposed, and for the lapse time after that.</summary>
    public IDisposable Hold(IReadOnlyCollection<string>? tokens)
    {
        var held = tokens is null ? [] : tokens.Distinct(StringComparer.Ordinal).ToArray();
        var fresh = new List<string>();
        lock (_gate)
        {
            var now = nowMs();
            foreach (var token in held)
            {
                var entry = _tokens.GetValueOrDefault(token);
                if (!Live(entry, now))
                {
                    fresh.Add(token);
                }

                _tokens[token] = (entry.Open + 1, now);
            }
        }

        foreach (var token in fresh)
        {
            Started?.Invoke(token);
        }

        return new Handle(this, held);
    }

    /// <summary>Whether any live token covers <paramref name="sliceId"/>: the id itself, or a view whose prefix it carries.</summary>
    public bool IsSubscribed(string sliceId)
    {
        var live = LiveTokens();
        return live.Count > 0 && ModelViews.Subscription(live)!(sliceId);
    }

    /// <summary>The tokens live now, in ordinal order.</summary>
    public IReadOnlyList<string> LiveTokens()
    {
        lock (_gate)
        {
            var now = nowMs();
            foreach (var dead in _tokens.Where(kv => !Live(kv.Value, now)).Select(static kv => kv.Key).ToList())
            {
                _ = _tokens.Remove(dead);
            }

            return [.. _tokens.Keys.Order(StringComparer.Ordinal)];
        }
    }

    private bool Live((int Open, long LastMs) entry, long now) => entry.Open > 0 || (entry.LastMs != 0 && now - entry.LastMs < lapseMs);

    private void Release(string[] tokens)
    {
        lock (_gate)
        {
            var now = nowMs();
            foreach (var token in tokens)
            {
                if (_tokens.TryGetValue(token, out var entry))
                {
                    _tokens[token] = (Math.Max(0, entry.Open - 1), now);
                }
            }
        }
    }

    private sealed class Handle(SubscriptionLeases owner, string[] tokens) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
            {
                owner.Release(tokens);
            }
        }
    }
}
