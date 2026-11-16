using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Ah.Core.Text;

namespace Ah.Core.Json;

/// <summary>
/// The one reader and writer for every JSON byte that reaches a file, stdout or an HTTP body. Output equals
/// <c>JSON.stringify(JSON.parse(text), null, indent)</c>; the caller appends any trailing newline.
/// </summary>
public static class JsonText
{
    /// <summary>The deepest nesting that is read or written.</summary>
    public const int MaxDepth = 4096;

    private const int MaxIndent = 10;

    /// <summary>Parses UTF-8 JSON as <c>JSON.parse</c> does. A BOM, comments, trailing commas and several top-level values are rejected.</summary>
    public static JsonValue Parse(ReadOnlySpan<byte> utf8)
    {
        try
        {
            return JsonReaderImpl.Read(utf8);
        }
        catch (JsonException e)
        {
            throw new JsonParseException(e.Message, e);
        }
        catch (FormatException e)
        {
            throw new JsonParseException(e.Message, e);
        }
    }

    /// <summary>Parses text that was decoded the way <c>readFileSync(path, "utf8")</c> decodes it.</summary>
    public static JsonValue Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > PooledParseChars)
        {
            return Parse(Utf8Text.Encode(text));
        }

        // the bytes are only read while parsing (every string of the result is decoded anew), so a rented buffer serves
        var rented = System.Buffers.ArrayPool<byte>.Shared.Rent(Utf8Text.MaxBytes(text.Length));
        try
        {
            return Parse(rented.AsSpan(0, Utf8Text.Encode(text, rented)));
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private const int PooledParseChars = 256 * 1024;

    public static bool TryParse(ReadOnlySpan<byte> utf8, out JsonValue value)
    {
        try
        {
            value = Parse(utf8);
            return true;
        }
        catch (JsonParseException)
        {
            value = default;
            return false;
        }
    }

    /// <summary>The text <c>JSON.stringify(value, null, indent)</c> gives. <paramref name="indent"/> is clamped to 0..10 as JavaScript does.</summary>
    public static string Stringify(JsonValue value, int indent)
    {
        var sb = new StringBuilder();
        JsonWriterImpl.Write(value, sb, Math.Clamp(indent, 0, MaxIndent));
        return sb.ToString();
    }

    /// <summary>The text <see cref="Stringify(JsonValue, int)"/> writes for the number <paramref name="value"/>.</summary>
    public static string NumberText(double value) => JsonWriterImpl.Number(value);

    /// <summary><paramref name="text"/> as a JSON string literal: <c>JSON.stringify(text)</c>.</summary>
    public static string Quote(string text) => Stringify(JsonValue.FromString(text), 0);

    /// <summary>The builder <see cref="ToUtf8(JsonValue, int)"/> writes into, kept per thread between calls (ah2-1): a call then allocates its bytes and no text.</summary>
    [ThreadStatic]
    private static StringBuilder? _writer;

    /// <summary>A builder that grew past this many characters is not kept.</summary>
    private const int KeptWriterChars = 1 << 20;

    public static byte[] ToUtf8(JsonValue value, int indent)
    {
        // taken while in use, so a call made from inside a write would get a builder of its own
        var sb = _writer ?? new StringBuilder();
        _writer = null;
        try
        {
            JsonWriterImpl.Write(value, sb, Math.Clamp(indent, 0, MaxIndent));
            return Utf8Text.Encode(sb);
        }
        finally
        {
            if (sb.Capacity <= KeptWriterChars)
            {
                _writer = sb.Clear();
            }
        }
    }

    /// <summary>
    /// A typed internal DTO as canonical bytes: the source-generated serializer produces the value, and the canonical
    /// writer produces the bytes. The only route from a typed value to an output path.
    /// </summary>
    public static byte[] ToUtf8<T>(T value, JsonTypeInfo<T> typeInfo, int indent)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        return ToUtf8(Parse(JsonSerializer.SerializeToUtf8Bytes(value, typeInfo)), indent);
    }

    public static string Stringify<T>(T value, JsonTypeInfo<T> typeInfo, int indent) =>
        Utf8Text.Decode(ToUtf8(value, typeInfo, indent));
}
