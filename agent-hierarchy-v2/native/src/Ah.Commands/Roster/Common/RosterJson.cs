using Ah.Core.Json;

namespace Ah.Commands;

/// <summary>The few JavaScript idioms the roster verbs build their output with.</summary>
internal static class RosterJson
{
    /// <summary>A string, or JSON null for none.</summary>
    internal static JsonValue Text(string? value) => value is null ? JsonValue.Null : JsonValue.FromString(value);

    /// <summary><c>value ?? null</c>: the value unless it is missing or JSON null.</summary>
    internal static JsonValue OrNull(JsonValue? value) => value is { IsNull: false } v ? v : JsonValue.Null;

    /// <summary><c>value || null</c>: the value when it is truthy, else JSON null.</summary>
    internal static JsonValue Truthy(JsonValue? value) => value is { IsTruthy: true } v ? v : JsonValue.Null;

    /// <summary><c>{ ...target, ...source }</c> in place: the keys of <paramref name="source"/> are set on <paramref name="target"/>.</summary>
    internal static void Spread(JsonObject target, JsonObject? source)
    {
        if (source is null)
        {
            return;
        }

        foreach (var key in source.Keys)
        {
            target[key] = source[key];
        }
    }

    /// <summary>The member of an object value, or null when the value is not an object or lacks it (JavaScript <c>undefined</c>).</summary>
    internal static JsonValue? Member(JsonValue? value, string name) => value is { } v ? v.Member(name) : null;

    /// <summary>Whether a value is a number holding an integer (<c>Number.isInteger</c>).</summary>
    internal static bool IsInteger(JsonValue? value) =>
        value is { IsNumber: true } v && double.IsFinite(v.AsNumber()) && Math.Floor(v.AsNumber()) == v.AsNumber();
}
