namespace Ah.Core.Json;

/// <summary>One JSON value with JavaScript semantics: numbers are IEEE doubles, strings are UTF-16 and may hold lone surrogates.</summary>
public readonly struct JsonValue
{
    private readonly object? _ref;
    private readonly double _number;

    private JsonValue(JsonKind kind, object? reference, double number)
    {
        Kind = kind;
        _ref = reference;
        _number = number;
    }

    /// <summary>The default value is <see cref="Null"/>.</summary>
    public JsonKind Kind { get; }

    public static JsonValue Null => default;

    public static JsonValue True => new(JsonKind.Bool, null, 1);

    public static JsonValue False => new(JsonKind.Bool, null, 0);

    public bool IsNull => Kind == JsonKind.Null;

    public bool IsBool => Kind == JsonKind.Bool;

    public bool IsNumber => Kind == JsonKind.Number;

    public bool IsText => Kind == JsonKind.Text;

    public bool IsArray => Kind == JsonKind.Array;

    public bool IsMap => Kind == JsonKind.Map;

    /// <summary>JavaScript truthiness: false for null, false, 0, NaN and the empty string; true for every array and object.</summary>
    public bool IsTruthy => Kind switch
    {
        JsonKind.Null => false,
        JsonKind.Bool => _number != 0,
        JsonKind.Number => _number != 0 && !double.IsNaN(_number),
        JsonKind.Text => ((string)_ref!).Length > 0,
        _ => true,
    };

    /// <summary>The text when this is a non-empty string, else null.</summary>
    public string? NonEmptyText => Kind == JsonKind.Text && ((string)_ref!).Length > 0 ? (string)_ref! : null;

    /// <summary><c>value.name</c>: the property when this is an object that has it, else null (undefined).</summary>
    public JsonValue? Member(string name) =>
        Kind == JsonKind.Map && ((JsonObject)_ref!).TryGetValue(name, out var v) ? (JsonValue?)v : null;

    public static JsonValue FromBool(bool value) => value ? True : False;

    public static JsonValue FromNumber(double value) => new(JsonKind.Number, null, value);

    public static JsonValue FromString(string value) => new(JsonKind.Text, value ?? throw new ArgumentNullException(nameof(value)), 0);

    public static JsonValue FromArray(JsonArray value) => new(JsonKind.Array, value ?? throw new ArgumentNullException(nameof(value)), 0);

    public static JsonValue FromObject(JsonObject value) => new(JsonKind.Map, value ?? throw new ArgumentNullException(nameof(value)), 0);

    public static implicit operator JsonValue(string value) => FromString(value);

    public static implicit operator JsonValue(bool value) => FromBool(value);

    public static implicit operator JsonValue(double value) => FromNumber(value);

    public static implicit operator JsonValue(int value) => FromNumber(value);

    public static implicit operator JsonValue(long value) => FromNumber(value);

    public static implicit operator JsonValue(JsonArray value) => FromArray(value);

    public static implicit operator JsonValue(JsonObject value) => FromObject(value);

    public bool AsBool() => Kind == JsonKind.Bool ? _number != 0 : throw Wrong(JsonKind.Bool);

    public double AsNumber() => Kind == JsonKind.Number ? _number : throw Wrong(JsonKind.Number);

    public string AsString() => Kind == JsonKind.Text ? (string)_ref! : throw Wrong(JsonKind.Text);

    public JsonArray AsArray() => Kind == JsonKind.Array ? (JsonArray)_ref! : throw Wrong(JsonKind.Array);

    public JsonObject AsObject() => Kind == JsonKind.Map ? (JsonObject)_ref! : throw Wrong(JsonKind.Map);

    private InvalidOperationException Wrong(JsonKind wanted) =>
        new($"JSON value is {Kind}, not {wanted}.");
}
