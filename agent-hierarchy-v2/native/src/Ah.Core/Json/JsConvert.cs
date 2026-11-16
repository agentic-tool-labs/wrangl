
using System.Text;

namespace Ah.Core.Json;

/// <summary>JavaScript's implicit conversions of a JSON value, for ported code that interpolates or compares loosely.</summary>
public static class JsConvert
{
    /// <summary><c>String(value)</c>; <paramref name="value"/> null is <c>undefined</c>.</summary>
    public static string ToJsString(JsonValue? value)
    {
        if (value is null)
        {
            return "undefined";
        }

        var v = value.Value;
        switch (v.Kind)
        {
            case JsonKind.Null:
                return "null";
            case JsonKind.Bool:
                return v.AsBool() ? "true" : "false";
            case JsonKind.Number:
                return NumberToString(v.AsNumber());
            case JsonKind.Text:
                return v.AsString();
            case JsonKind.Array:
                var parts = new StringBuilder();
                var first = true;
                foreach (var item in v.AsArray())
                {
                    if (!first)
                    {
                        parts.Append(',');
                    }

                    first = false;
                    parts.Append(item.IsNull ? string.Empty : ToJsString(item));
                }

                return parts.ToString();
            default:
                return "[object Object]";
        }
    }

    /// <summary><c>Number::toString</c>, including <c>NaN</c> and the infinities that JSON text cannot hold.</summary>
    public static string NumberToString(double d)
    {
        if (double.IsNaN(d))
        {
            return "NaN";
        }

        if (double.IsPositiveInfinity(d))
        {
            return "Infinity";
        }

        if (double.IsNegativeInfinity(d))
        {
            return "-Infinity";
        }

        return JsonWriterImpl.Number(d);
    }

    /// <summary><c>Number(value)</c>; <paramref name="value"/> null is <c>undefined</c>, which gives NaN.</summary>
    public static double ToNumber(JsonValue? value)
    {
        if (value is null)
        {
            return double.NaN;
        }

        var v = value.Value;
        switch (v.Kind)
        {
            case JsonKind.Null:
                return 0;
            case JsonKind.Bool:
                return v.AsBool() ? 1 : 0;
            case JsonKind.Number:
                return v.AsNumber();
            case JsonKind.Text:
                return StringToNumber(v.AsString());
            case JsonKind.Array:
                return StringToNumber(ToJsString(v));
            default:
                return double.NaN;
        }
    }

    private static double StringToNumber(string text)
    {
        var s = Ah.Core.Text.JsText.Trim(text);
        if (s.Length == 0)
        {
            return 0;
        }

        if (s is "Infinity" or "+Infinity")
        {
            return double.PositiveInfinity;
        }

        if (s == "-Infinity")
        {
            return double.NegativeInfinity;
        }

        if (s.Length > 2 && s[0] == '0' && (s[1] is 'x' or 'X' or 'o' or 'O' or 'b' or 'B'))
        {
            var radix = s[1] is 'x' or 'X' ? 16 : s[1] is 'o' or 'O' ? 8 : 2;
            double acc = 0;
            foreach (var c in s.AsSpan(2))
            {
                var digit = c is >= '0' and <= '9' ? c - '0' : c is >= 'a' and <= 'f' ? c - 'a' + 10 : c is >= 'A' and <= 'F' ? c - 'A' + 10 : -1;
                if (digit < 0 || digit >= radix)
                {
                    return double.NaN;
                }

                acc = (acc * radix) + digit;
            }

            return acc;
        }

        foreach (var c in s)
        {
            if (!(c is (>= '0' and <= '9') or '.' or 'e' or 'E' or '+' or '-'))
            {
                return double.NaN;
            }
        }

        return double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : double.NaN;
    }

    /// <summary>The integer value of a number that <c>Number.isInteger</c> accepts, else null.</summary>
    public static long? AsInteger(JsonValue? value)
    {
        if (value is { IsNumber: true } n && double.IsFinite(n.AsNumber()) && n.AsNumber() == Math.Floor(n.AsNumber()) && Math.Abs(n.AsNumber()) <= 9007199254740991d)
        {
            return (long)n.AsNumber();
        }

        return null;
    }

}
