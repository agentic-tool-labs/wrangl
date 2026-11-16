using System.Collections;

namespace Ah.Core.Json;

public sealed class JsonArray : IReadOnlyList<JsonValue>
{
    private readonly List<JsonValue> _items;

    public JsonArray() => _items = [];

    /// <summary>An empty array with room for <paramref name="capacity"/> items, for a reader that knows the count.</summary>
    internal JsonArray(int capacity) => _items = new(capacity);

    public int Count => _items.Count;

    public JsonValue this[int index]
    {
        get => _items[index];
        set => _items[index] = value;
    }

    public void Add(JsonValue value) => _items.Add(value);

    public void AddRange(IEnumerable<JsonValue> values) => _items.AddRange(values);

    public IEnumerator<JsonValue> GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
