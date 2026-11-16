namespace Ah.Cli.Composition;

/// <summary>
/// Facts derived from stored messages, held while the message store's version reads the same (b459 step 20): any msgs write moves it and
/// drops every held fact. A null version (no store version) derives every time. <see cref="Forget"/> is the on-demand rebuild.
/// </summary>
internal sealed class MsgsVersionMemo<T>
{
    private readonly object _gate = new();
    private readonly Dictionary<string, T> _held = new(StringComparer.Ordinal);
    private long? _version;

    // holds: whether a derived value may be held; one it rejects (a read that failed) is derived again next call
    internal T Of(long? version, string key, Func<T> make, Func<T, bool>? holds = null)
    {
        if (version is null)
        {
            return make();
        }

        lock (_gate)
        {
            if (_version != version)
            {
                _held.Clear();
                _version = version;
            }
            else if (_held.TryGetValue(key, out var held))
            {
                return held;
            }
        }

        // the version was read by the caller before this fact: a write in between leaves the older version held, so the next read derives again
        var made = make();
        if (holds is not null && !holds(made))
        {
            return made;
        }

        lock (_gate)
        {
            if (_version == version)
            {
                _held[key] = made;
            }
        }

        return made;
    }

    internal void Forget()
    {
        lock (_gate)
        {
            _held.Clear();
            _version = null;
        }
    }
}
