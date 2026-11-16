using Ah.Core.Json;

namespace Ah.Core.Time;

public static class Age
{
    /// <summary>The compact age <c>45s</c>, <c>3m</c>, <c>2h</c>, <c>5d</c>; <c>?</c> for a negative or non-finite number of seconds.</summary>
    public static string Format(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0)
        {
            return "?";
        }

        var (value, unit) = seconds switch
        {
            < 60 => (seconds, 's'),
            < 3600 => (seconds / 60, 'm'),
            < 86400 => (seconds / 3600, 'h'),
            _ => (seconds / 86400, 'd'),
        };
        return JsonWriterImpl.Number(Math.Floor(value)) + unit;
    }
}
