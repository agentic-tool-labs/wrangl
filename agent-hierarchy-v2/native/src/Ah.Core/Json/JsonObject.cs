using System.Globalization;

namespace Ah.Core.Json;

/// <summary>
/// An object with JavaScript property order: keys that are canonical array indices (0 to 2^32-2) come first in
/// ascending numeric order, then every other key in the order it was first set. Setting an existing key keeps its
/// position; removing a key and setting it again moves it to the end.
/// </summary>
public sealed class JsonObject
{
    private readonly Dictionary<string, JsonValue> _values;
    private readonly List<string> _insertion;
    private string[]? _ordered;

    public JsonObject()
    {
        _values = new(StringComparer.Ordinal);
        _insertion = [];
    }

    /// <summary>An empty object with room for <paramref name="capacity"/> keys, for a writer that knows the count: nothing else differs.</summary>
    public JsonObject(int capacity)
    {
        _values = new(capacity, StringComparer.Ordinal);
        _insertion = new(capacity);
    }

    public int Count => _values.Count;

    public JsonValue this[string key]
    {
        get => _values[key];
        set
        {
            ArgumentNullException.ThrowIfNull(key);
            if (!_values.ContainsKey(key))
            {
                _insertion.Add(key);
                _ordered = null;
            }

            _values[key] = value;
        }
    }

    /// <summary>The keys in JavaScript property order.</summary>
    public IReadOnlyList<string> Keys => _ordered ??= Order();

    public bool ContainsKey(string key) => _values.ContainsKey(key);

    /// <summary>The value for <paramref name="key"/>, or null when there is none (JavaScript <c>undefined</c>).</summary>
    public JsonValue? Get(string key) => _values.TryGetValue(key, out var v) ? (JsonValue?)v : null;

    /// <summary>A shallow copy with the same key order, as <c>{ ...obj }</c> gives.</summary>
    public JsonObject Clone()
    {
        var copy = new JsonObject();
        foreach (var key in Keys)
        {
            copy[key] = _values[key];
        }

        return copy;
    }

    public bool TryGetValue(string key, out JsonValue value) => _values.TryGetValue(key, out value);

    public bool Remove(string key)
    {
        if (!_values.Remove(key))
        {
            return false;
        }

        _insertion.Remove(key);
        _ordered = null;
        return true;
    }

    internal static bool IsArrayIndex(string key, out uint index)
    {
        index = 0;
        if (key.Length is 0 or > 10 || (key.Length > 1 && key[0] == '0'))
        {
            return false;
        }

        foreach (var c in key)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        return uint.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out index) && index != uint.MaxValue;
    }

    private string[] Order()
    {
        // most objects have no array-index key: their order is the insertion order, and one copy is all it takes
        var anyIndex = false;
        foreach (var key in _insertion)
        {
            if (key.Length > 0 && key[0] is >= '0' and <= '9' && IsArrayIndex(key, out _))
            {
                anyIndex = true;
                break;
            }
        }

        if (!anyIndex)
        {
            return [.. _insertion];
        }

        var indexed = new List<(uint Index, string Key)>();
        var named = new List<string>();
        foreach (var key in _insertion)
        {
            if (IsArrayIndex(key, out var index))
            {
                indexed.Add((index, key));
            }
            else
            {
                named.Add(key);
            }
        }

        indexed.Sort((a, b) => a.Index.CompareTo(b.Index));
        var result = new string[_insertion.Count];
        var at = 0;
        foreach (var (_, key) in indexed)
        {
            result[at++] = key;
        }

        foreach (var key in named)
        {
            result[at++] = key;
        }

        return result;
    }
}
