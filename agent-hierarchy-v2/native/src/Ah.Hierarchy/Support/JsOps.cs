using Ah.Core.Json;

namespace Ah.Hierarchy;

/// <summary>The few JavaScript operators the ported libraries apply to values whose type is only known at run time.</summary>
internal static class JsOps
{
    /// <summary><c>a || 0</c>.</summary>
    internal static JsonValue OrZero(JsonValue? value) => value is { IsTruthy: true } v ? v : JsonValue.FromNumber(0);

    /// <summary>
    /// <c>a + b</c>: string concatenation when either side is a string, array or object, else a numeric sum
    /// (null, undefined as 0 or NaN as <see cref="JsConvert.ToNumber"/> gives).
    /// </summary>
    internal static JsonValue Add(JsonValue? left, JsonValue? right)
    {
        var concatenates = left is { IsText: true } or { IsArray: true } or { IsMap: true } || right is { IsText: true } or { IsArray: true } or { IsMap: true };
        if (concatenates)
        {
            return JsConvert.ToJsString(left) + JsConvert.ToJsString(right);
        }

        return JsConvert.ToNumber(left) + JsConvert.ToNumber(right);
    }

    /// <summary><c>Math.round</c>: halves go toward positive infinity.</summary>
    internal static double Round(double x)
    {
        if (!double.IsFinite(x))
        {
            return x;
        }

        var floor = Math.Floor(x);
        return x - floor >= 0.5 ? floor + 1 : floor;
    }

    /// <summary><c>text.slice(0, end)</c> for a number <paramref name="end"/> (NaN reads as 0, a fraction is cut toward zero).</summary>
    internal static string SliceTo(string text, double end)
    {
        var stop = double.IsNaN(end) ? 0 : Math.Truncate(end);
        if (stop < 0)
        {
            stop = Math.Max(text.Length + stop, 0);
        }

        return text[..(int)Math.Min(stop, text.Length)];
    }

    /// <summary>A value as an object spread (<c>{...value}</c>) would copy it: an array by index, a string by UTF-16 unit, a scalar to nothing.</summary>
    internal static JsonObject Spread(JsonValue? value)
    {
        var copy = new JsonObject();
        if (value is not { } v)
        {
            return copy;
        }

        if (v.IsMap)
        {
            foreach (var key in v.AsObject().Keys)
            {
                copy[key] = v.AsObject()[key];
            }
        }
        else if (v.IsArray)
        {
            var i = 0;
            foreach (var item in v.AsArray())
            {
                copy[i.ToString(System.Globalization.CultureInfo.InvariantCulture)] = item;
                i++;
            }
        }
        else if (v.IsText)
        {
            var s = v.AsString();
            for (var i = 0; i < s.Length; i++)
            {
                copy[i.ToString(System.Globalization.CultureInfo.InvariantCulture)] = s[i].ToString();
            }
        }

        return copy;
    }

    /// <summary>A key for a <c>Set</c> of arbitrary values: primitives by type and value, objects never equal to another.</summary>
    internal static string? SetKey(JsonValue value) => value.Kind switch
    {
        JsonKind.Text => "s:" + value.AsString(),
        JsonKind.Number => "n:" + JsConvert.ToJsString(value),
        JsonKind.Bool => value.AsBool() ? "b:1" : "b:0",
        JsonKind.Null => "null",
        _ => null,
    };

    /// <summary>
    /// <c>new Date(ms).toISOString()</c> for any valid time value (|ms| up to 8.64e15): a year outside 0000..9999 is written
    /// with a sign and six digits.
    /// </summary>
    internal static string IsoString(double ms)
    {
        var total = (long)Math.Truncate(ms);
        var days = Math.DivRem(total, 86_400_000L, out var inDay);
        if (inDay < 0)
        {
            inDay += 86_400_000L;
            days--;
        }

        var z = days + 719_468;
        var era = (z >= 0 ? z : z - 146_096) / 146_097;
        var doe = z - (era * 146_097);
        var yoe = (doe - (doe / 1460) + (doe / 36_524) - (doe / 146_096)) / 365;
        var year = yoe + (era * 400);
        var doy = doe - ((365 * yoe) + (yoe / 4) - (yoe / 100));
        var mp = ((5 * doy) + 2) / 153;
        var day = doy - (((153 * mp) + 2) / 5) + 1;
        var month = mp < 10 ? mp + 3 : mp - 9;
        if (month <= 2)
        {
            year++;
        }

        var y = year is >= 0 and <= 9999
            ? year.ToString("D4", System.Globalization.CultureInfo.InvariantCulture)
            : (year < 0 ? "-" : "+") + Math.Abs(year).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{y}-{month:00}-{day:00}T{inDay / 3_600_000:00}:{inDay / 60_000 % 60:00}:{inDay / 1000 % 60:00}.{inDay % 1000:000}Z");
    }
}
