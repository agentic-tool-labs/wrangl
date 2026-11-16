using System.Globalization;
using System.Text;
using System.Text.Json;
using Ah.Core.Text;

namespace Ah.Core.Json;

internal static class JsonReaderImpl
{
    internal static JsonValue Read(ReadOnlySpan<byte> utf8)
    {
        var reader = new Utf8JsonReader(utf8, new JsonReaderOptions
        {
            MaxDepth = JsonText.MaxDepth,
            CommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false,
        });
        if (!reader.Read())
        {
            throw new JsonException("Empty input.");
        }

        var root = ReadValue(ref reader);
        if (reader.Read())
        {
            throw new JsonException("Data after the top-level value.");
        }

        return root;
    }

    /// <summary>
    /// What one parse works in, kept per thread so a parse allocates only what it returns: the pending members of the open containers,
    /// their keys, and the short ASCII property names and strings seen lately, which most documents repeat (ah2-1). Emptied after every
    /// parse; the name table holds strings only, never a value.
    /// </summary>
    private sealed class Scratch
    {
        public readonly List<JsonValue> Values = [];
        public readonly List<string> Keys = [];
        public readonly Stack<(bool Map, int Values, int Keys)> Open = new();
        public readonly string?[] Names = new string?[1024];
        public bool Busy;
    }

    [ThreadStatic]
    private static Scratch? _scratch;

    /// <summary>Reads one value iteratively, so nesting depth costs heap and not stack. Leaves the reader on the value's last token.</summary>
    private static JsonValue ReadValue(ref Utf8JsonReader reader)
    {
        var scratch = _scratch ??= new Scratch();
        if (scratch.Busy)
        {
            scratch = new Scratch(); // never re-entered today; a second parse on the stack gets its own
        }

        scratch.Busy = true;
        try
        {
            return ReadValue(ref reader, scratch);
        }
        finally
        {
            scratch.Values.Clear();
            scratch.Keys.Clear();
            scratch.Open.Clear();
            scratch.Busy = false;
            if (scratch.Values.Capacity > 65536)
            {
                _scratch = null; // one huge document does not keep its lists
            }
        }
    }

    private static JsonValue ReadValue(ref Utf8JsonReader reader, Scratch scratch)
    {
        var (values, keys, open) = (scratch.Values, scratch.Keys, scratch.Open);
        while (true)
        {
            JsonValue value;
            switch (reader.TokenType)
            {
                case JsonTokenType.PropertyName:
                    keys.Add(Text(reader.ValueSpan, reader.ValueIsEscaped, scratch.Names));
                    reader.Read();
                    continue;
                case JsonTokenType.StartArray:
                    open.Push((false, values.Count, keys.Count));
                    reader.Read();
                    continue;
                case JsonTokenType.StartObject:
                    open.Push((true, values.Count, keys.Count));
                    reader.Read();
                    continue;
                case JsonTokenType.EndArray:
                case JsonTokenType.EndObject:
                {
                    // the container is made at its end, at its exact size, from the members read since it opened
                    var (map, from, keysFrom) = open.Pop();
                    var count = values.Count - from;
                    if (map)
                    {
                        var made = new JsonObject(count);
                        for (var i = 0; i < count; i++)
                        {
                            made[keys[keysFrom + i]] = values[from + i];
                        }

                        keys.RemoveRange(keysFrom, count);
                        value = made;
                    }
                    else
                    {
                        var made = new JsonArray(count);
                        for (var i = 0; i < count; i++)
                        {
                            made.Add(values[from + i]);
                        }

                        value = made;
                    }

                    values.RemoveRange(from, count);
                    break;
                }

                default:
                    value = Scalar(ref reader, scratch.Names);
                    break;
            }

            if (open.Count == 0)
            {
                return value;
            }

            values.Add(value);
            reader.Read();
        }
    }

    private static JsonValue Scalar(ref Utf8JsonReader reader, string?[] names) => reader.TokenType switch
    {
        JsonTokenType.Null => JsonValue.Null,
        JsonTokenType.True => JsonValue.True,
        JsonTokenType.False => JsonValue.False,
        JsonTokenType.Number => double.Parse(reader.ValueSpan, NumberStyles.Float, CultureInfo.InvariantCulture),
        JsonTokenType.String => Text(reader.ValueSpan, reader.ValueIsEscaped, names),
        _ => throw new JsonException("Unexpected token " + reader.TokenType + "."),
    };

    private const int SharedNameBytes = 40;

    /// <summary>
    /// A string token's text. A short unescaped ASCII one is looked up in <paramref name="names"/> first, so the property names and
    /// short values a document repeats are one string each; any other is decoded as before. The table is a cache: a miss costs a decode.
    /// </summary>
    private static string Text(ReadOnlySpan<byte> raw, bool escaped, string?[] names)
    {
        if (escaped || raw.Length is 0 or > SharedNameBytes)
        {
            return Unescape(raw, escaped);
        }

        var hash = 2166136261;
        foreach (var b in raw)
        {
            hash = unchecked((hash ^ b) * 16777619);
        }

        ref var slot = ref names[hash & (uint)(names.Length - 1)];
        if (slot is { } held && held.Length == raw.Length)
        {
            var same = true;
            for (var i = 0; i < raw.Length; i++)
            {
                if (held[i] != raw[i])
                {
                    same = false;
                    break;
                }
            }

            if (same)
            {
                return held;
            }
        }

        var made = Utf8Text.Decode(raw);
        if (System.Text.Ascii.IsValid(raw))
        {
            slot = made; // only ASCII is kept, so a held string equals its bytes exactly when every char equals its byte
        }

        return made;
    }

    /// <summary>Decodes a string token by hand: a lone surrogate escape survives, and invalid UTF-8 becomes U+FFFD.</summary>
    private static string Unescape(ReadOnlySpan<byte> raw, bool escaped)
    {
        if (!escaped)
        {
            return Utf8Text.Decode(raw);
        }

        var sb = new StringBuilder(raw.Length);
        var i = 0;
        while (i < raw.Length)
        {
            var j = raw[i..].IndexOf((byte)'\\');
            if (j < 0)
            {
                sb.Append(Utf8Text.Decode(raw[i..]));
                break;
            }

            sb.Append(Utf8Text.Decode(raw.Slice(i, j)));
            var c = raw[i + j + 1];
            i += j + 2;
            switch (c)
            {
                case (byte)'"': sb.Append('"'); break;
                case (byte)'\\': sb.Append('\\'); break;
                case (byte)'/': sb.Append('/'); break;
                case (byte)'b': sb.Append('\b'); break;
                case (byte)'f': sb.Append('\f'); break;
                case (byte)'n': sb.Append('\n'); break;
                case (byte)'r': sb.Append('\r'); break;
                case (byte)'t': sb.Append('\t'); break;
                case (byte)'u':
                    sb.Append((char)int.Parse(raw.Slice(i, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture));
                    i += 4;
                    break;
                default:
                    throw new JsonException("Invalid escape.");
            }
        }

        return sb.ToString();
    }
}
