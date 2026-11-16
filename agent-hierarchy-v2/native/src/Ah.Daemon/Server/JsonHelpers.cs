using Ah.Core.Json;

namespace Ah.Daemon;

/// <summary>Small readers and builders over <see cref="JsonObject"/> for the host's own bodies.</summary>
internal static class Jx
{
    internal static JsonArray Arr(IEnumerable<JsonValue> items)
    {
        var a = new JsonArray();
        foreach (var item in items)
        {
            a.Add(item);
        }

        return a;
    }

    internal static JsonObject Ok() => new() { ["ok"] = true };

    internal static JsonObject? AsObject(this JsonValue v) => v.Kind == JsonKind.Map ? v.AsObject() : null;

    internal static string? Str(this JsonObject o, string key) =>
        o.TryGetValue(key, out var v) && v.Kind == JsonKind.Text ? v.AsString() : null;

    /// <summary><c>body.error.code</c> when the value is an error body.</summary>
    internal static string? ErrorCode(JsonValue body) => ErrorField(body, "code");

    /// <summary>A text member of the body's <c>error</c> object, or null.</summary>
    internal static string? ErrorField(JsonValue body, string name) =>
        body.IsMap && body.AsObject().TryGetValue("error", out var e) && e.IsMap ? e.AsObject().Str(name) : null;
}
