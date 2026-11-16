using Ah.Core.Json;

namespace Ah.Hierarchy;

internal static class JsonValueExtensions
{
    /// <summary><c>value[name]</c> of an object value: the property, or JSON null when it has none.</summary>
    internal static JsonValue Prop(this JsonValue value, string name) =>
        value.IsMap ? value.AsObject().Get(name) ?? JsonValue.Null : throw new InvalidOperationException($"JSON value is {value.Kind}, not Map.");
}
