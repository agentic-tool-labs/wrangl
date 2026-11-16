using Ah.Core.Json;

namespace Ah.Hooks.Plumbing;

/// <summary>Object shapes the advisory hooks write: a record spread with overrides, as <c>{...rec, ts, status}</c> does.</summary>
internal static class RecordShapes
{
    /// <summary>
    /// A copy of <paramref name="record"/> with the given members set: an existing key keeps its position, a new one is appended.
    /// </summary>
    public static JsonObject With(JsonValue record, params (string Key, JsonValue Value)[] overrides)
    {
        var copy = new JsonObject();
        if (record.IsMap)
        {
            foreach (var key in record.AsObject().Keys)
            {
                copy[key] = record.AsObject()[key];
            }
        }

        foreach (var (key, value) in overrides)
        {
            copy[key] = value;
        }

        return copy;
    }

    /// <summary>A template-literal rendering of a member: JavaScript's string conversion, <c>undefined</c> for a missing one.</summary>
    public static string Js(JsonValue? value) => JsConvert.ToJsString(value);
}
