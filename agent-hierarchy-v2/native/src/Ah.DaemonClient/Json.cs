using Ah.Core.Json;

namespace Ah.DaemonClient;

/// <summary>Small builders and readers over <see cref="JsonObject"/> for the client's wire and verb bodies.</summary>
internal static class Jx
{
    internal static JsonObject Obj(params (string Key, JsonValue Value)[] members)
    {
        var o = new JsonObject();
        foreach (var (key, value) in members)
        {
            o[key] = value;
        }

        return o;
    }

    internal static JsonArray Arr(IEnumerable<JsonValue> items)
    {
        var a = new JsonArray();
        foreach (var item in items)
        {
            a.Add(item);
        }

        return a;
    }

    internal static JsonObject? AsObject(this JsonValue v) => v.Kind == JsonKind.Map ? v.AsObject() : null;

    internal static string? Str(this JsonObject o, string key) =>
        o.TryGetValue(key, out var v) && v.Kind == JsonKind.Text ? v.AsString() : null;

    /// <summary>The member as a whole number (<c>Number.isInteger</c>), else null.</summary>
    internal static long? Int(this JsonObject o, string key) =>
        o.TryGetValue(key, out var v) && v.Kind == JsonKind.Number && Math.Floor(v.AsNumber()) == v.AsNumber() && !double.IsInfinity(v.AsNumber())
            ? (long)v.AsNumber()
            : null;

    internal static JsonObject? Map(this JsonObject o, string key) =>
        o.TryGetValue(key, out var v) && v.Kind == JsonKind.Map ? v.AsObject() : null;

    /// <summary>A shallow copy of <paramref name="source"/> in its property order.</summary>
    internal static JsonObject Copy(JsonObject source)
    {
        var o = new JsonObject();
        foreach (var key in source.Keys)
        {
            o[key] = source[key];
        }

        return o;
    }

    /// <summary><c>body.error.code</c> when the value is an error body.</summary>
    internal static string? ErrorCode(JsonValue body) =>
        body.AsObject()?.Map("error")?.Str("code");
}
