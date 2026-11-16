using System.Globalization;
using System.Text;

namespace Ah.Core.Json;

internal static class JsonWriterImpl
{
    private sealed class Frame(JsonArray? array, JsonObject? obj)
    {
        internal JsonArray? Array { get; } = array;

        internal JsonObject? Object { get; } = obj;

        internal IReadOnlyList<string>? Keys { get; } = obj?.Keys;

        internal int Count => Array?.Count ?? Keys!.Count;

        internal int Next { get; set; }
    }

    /// <summary>Writes <paramref name="root"/> without recursion, so nesting depth costs heap and not stack.</summary>
    internal static void Write(JsonValue root, StringBuilder sb, int indent)
    {
        var open = new Stack<Frame>();
        Emit(root, sb, open);
        while (open.Count > 0)
        {
            var frame = open.Peek();
            if (frame.Next == frame.Count)
            {
                open.Pop();
                NewLine(sb, indent, open.Count);
                sb.Append(frame.Array is not null ? ']' : '}');
                continue;
            }

            if (frame.Next > 0)
            {
                sb.Append(',');
            }

            NewLine(sb, indent, open.Count);
            JsonValue child;
            if (frame.Array is { } array)
            {
                child = array[frame.Next];
            }
            else
            {
                var key = frame.Keys![frame.Next];
                String(key, sb);
                sb.Append(indent > 0 ? ": " : ":");
                child = frame.Object![key];
            }

            frame.Next++;
            Emit(child, sb, open);
        }
    }

    private static void Emit(JsonValue value, StringBuilder sb, Stack<Frame> open)
    {
        switch (value.Kind)
        {
            case JsonKind.Null:
                sb.Append("null");
                break;
            case JsonKind.Bool:
                sb.Append(value.AsBool() ? "true" : "false");
                break;
            case JsonKind.Number:
                sb.Append(Number(value.AsNumber()));
                break;
            case JsonKind.Text:
                String(value.AsString(), sb);
                break;
            case JsonKind.Array:
                Open(value.AsArray().Count == 0, "[]", '[', new Frame(value.AsArray(), null), sb, open);
                break;
            case JsonKind.Map:
                Open(value.AsObject().Count == 0, "{}", '{', new Frame(null, value.AsObject()), sb, open);
                break;
            default:
                throw new InvalidOperationException("Unknown JSON kind.");
        }
    }

    private static void Open(bool empty, string emptyText, char bracket, Frame frame, StringBuilder sb, Stack<Frame> open)
    {
        if (empty)
        {
            sb.Append(emptyText);
            return;
        }

        if (open.Count >= JsonText.MaxDepth)
        {
            throw new JsonParseException("Nesting deeper than " + JsonText.MaxDepth + ".");
        }

        sb.Append(bracket);
        open.Push(frame);
    }

    private static void NewLine(StringBuilder sb, int indent, int depth)
    {
        if (indent > 0)
        {
            sb.Append('\n').Append(' ', indent * depth);
        }
    }

    /// <summary>JSON.stringify's quoting: short escapes, other controls as lower-case \u00xx, lone surrogates as \udxxx, all else raw.</summary>
    internal static void String(string s, StringBuilder sb)
    {
        sb.Append('"');
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20)
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                    {
                        sb.Append(c).Append(s[i + 1]);
                        i++;
                    }
                    else if (char.IsSurrogate(c))
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        sb.Append('"');
    }

    /// <summary>ECMAScript Number::toString, built from .NET's shortest round-trip digits; NaN and the infinities are <c>null</c>.</summary>
    internal static string Number(double d)
    {
        if (double.IsNaN(d) || double.IsInfinity(d))
        {
            return "null";
        }

        if (d == 0)
        {
            return "0";
        }

        var r = d.ToString("R", CultureInfo.InvariantCulture);
        var negative = r[0] == '-';
        if (negative)
        {
            r = r[1..];
        }

        var exponent = 0;
        var e = r.IndexOfAny(['E', 'e']);
        if (e >= 0)
        {
            exponent = int.Parse(r.AsSpan(e + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            r = r[..e];
        }

        var dot = r.IndexOf('.', StringComparison.Ordinal);
        var digits = dot < 0 ? r : r.Remove(dot, 1);
        var n = (dot < 0 ? r.Length : dot) + exponent;
        var lead = 0;
        while (lead < digits.Length - 1 && digits[lead] == '0')
        {
            lead++;
            n--;
        }

        digits = digits[lead..].TrimEnd('0');
        if (digits.Length == 0)
        {
            digits = "0";
        }

        var k = digits.Length;
        string o;
        if (k <= n && n <= 21)
        {
            o = digits + new string('0', n - k);
        }
        else if (0 < n && n <= 21)
        {
            o = digits[..n] + "." + digits[n..];
        }
        else if (-6 < n && n <= 0)
        {
            o = "0." + new string('0', -n) + digits;
        }
        else
        {
            var x = n - 1;
            var exp = (x < 0 ? "-" : "+") + Math.Abs(x).ToString(CultureInfo.InvariantCulture);
            o = k == 1 ? digits + "e" + exp : digits[0] + "." + digits[1..] + "e" + exp;
        }

        return negative ? "-" + o : o;
    }
}
