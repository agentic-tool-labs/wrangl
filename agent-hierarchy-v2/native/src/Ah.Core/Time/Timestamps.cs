using System.Globalization;
using Ah.Abstractions;

namespace Ah.Core.Time;

public static class Timestamps
{
    private const double MaxTimeValue = 8.64e15;

    /// <summary>
    /// A process start time as <c>ps</c> prints it (<c>Tue Oct 14 19:00:00 2026</c>, local time, second granularity) as an instant.
    /// False when the text is not that form or names a local time that is ambiguous or does not exist (a DST change).
    /// </summary>
    public static bool TryParseProcessStart(string? text, out DateTimeOffset started)
    {
        started = default;
        if (text is null
            || !DateTime.TryParseExact(
                text.Trim(),
                ["ddd MMM d HH:mm:ss yyyy", "ddd MMM  d HH:mm:ss yyyy"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowInnerWhite | DateTimeStyles.AssumeLocal,
                out var local)
            || TimeZoneInfo.Local.IsAmbiguousTime(local)
            || TimeZoneInfo.Local.IsInvalidTime(local))
        {
            return false;
        }

        started = new DateTimeOffset(local);
        return true;
    }

    /// <summary><c>Date.prototype.toISOString</c> for a year in 0000..9999: always milliseconds, always <c>Z</c>.</summary>
    public static string ToIsoString(DateTimeOffset instant) =>
        instant.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>The writer's local time with its UTC offset and no fraction, e.g. <c>2026-08-16T14:32:01-04:00</c>.</summary>
    public static string LocalIso(IClock clock, DateTimeOffset instant)
    {
        ArgumentNullException.ThrowIfNull(clock);
        var offset = clock.LocalOffset(instant);
        var local = instant.UtcDateTime + offset;
        var minutes = (int)(offset.Ticks / TimeSpan.TicksPerMinute);
        var sign = minutes >= 0 ? '+' : '-';
        var abs = Math.Abs(minutes);
        return string.Create(CultureInfo.InvariantCulture,
            $"{local:yyyy-MM-dd'T'HH:mm:ss}{sign}{abs / 60:00}:{abs % 60:00}");
    }

    /// <summary>
    /// The instant a <c>ps -o lstart=</c> line (<c>Wed Oct 14 18:30:00 2026</c>, printed in the C locale) names, read as local time
    /// in <paramref name="zone"/>, in milliseconds since the epoch. False for text of another shape and for a local time the zone
    /// makes ambiguous (the repeated hour of a fall-back) or non-existent (the skipped hour of a spring-forward).
    /// </summary>
    public static bool TryParseProcessStart(string? text, TimeZoneInfo zone, out double epochMs)
    {
        ArgumentNullException.ThrowIfNull(zone);
        epochMs = double.NaN;
        if (text is null
            || !DateTime.TryParseExact(
                text.Trim(), ["ddd MMM d HH:mm:ss yyyy", "ddd MMM  d HH:mm:ss yyyy"], CultureInfo.InvariantCulture,
                DateTimeStyles.AllowInnerWhite, out var parsed))
        {
            return false;
        }

        var local = DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified);
        if (zone.IsAmbiguousTime(local) || zone.IsInvalidTime(local))
        {
            return false;
        }

        epochMs = new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUnixTimeMilliseconds();
        return true;
    }

    public static string LocalIso(IClock clock) => LocalIso(clock, (clock ?? throw new ArgumentNullException(nameof(clock))).UtcNow);

    /// <summary>
    /// What <c>Date.parse</c> returns, in milliseconds since the Unix epoch, for the two forms ah writes:
    /// <c>YYYY-MM-DDTHH:mm:ss</c> with an optional fraction of one or more digits, then <c>Z</c> or <c>±HH:MM</c>.
    /// Every other form is invalid. Day 29 to 31 of a short month rolls into the next month, as it does in JavaScript.
    /// </summary>
    public static bool TryParseMs(string? text, out double epochMs)
    {
        epochMs = double.NaN;
        if (text is null || text.Length < 20 || !Shape(text))
        {
            return false;
        }

        var year = Digits(text, 0, 4);
        var month = Digits(text, 5, 2);
        var day = Digits(text, 8, 2);
        var hour = Digits(text, 11, 2);
        var minute = Digits(text, 14, 2);
        var second = Digits(text, 17, 2);
        var at = 19;
        var millis = 0;
        var fractionNonZero = false;
        if (text[at] == '.')
        {
            var start = ++at;
            while (at < text.Length && text[at] is >= '0' and <= '9')
            {
                if (at - start < 3)
                {
                    millis = (millis * 10) + (text[at] - '0');
                }

                fractionNonZero |= text[at] != '0';
                at++;
            }

            if (at == start)
            {
                return false;
            }

            for (var pad = at - start; pad < 3; pad++)
            {
                millis *= 10;
            }
        }

        var offsetMinutes = 0;
        var rest = text.Length - at;
        if (rest == 6 && text[at] is '+' or '-' && text[at + 3] == ':' && Shape(text, at + 1, 2) && Shape(text, at + 4, 2))
        {
            var oh = Digits(text, at + 1, 2);
            var om = Digits(text, at + 4, 2);
            if (oh > 23 || om > 59)
            {
                return false;
            }

            offsetMinutes = ((oh * 60) + om) * (text[at] == '-' ? -1 : 1);
        }
        else if (!(rest == 1 && text[at] == 'Z'))
        {
            return false;
        }

        if (month is < 1 or > 12 || day is < 1 or > 31 || minute > 59 || second > 59 || hour > 24
            || (hour == 24 && (minute != 0 || second != 0 || fractionNonZero)))
        {
            return false;
        }

        var days = DaysFromCivil(year, month, 1) + (day - 1);
        var ms = (((days * 24L) + hour) * 60 + minute) * 60_000L + (second * 1000L) + millis - (offsetMinutes * 60_000L);
        if (Math.Abs((double)ms) > MaxTimeValue)
        {
            return false;
        }

        epochMs = ms;
        return true;
    }

    private static bool Shape(string t) =>
        Shape(t, 0, 4) && t[4] == '-' && Shape(t, 5, 2) && t[7] == '-' && Shape(t, 8, 2) && t[10] == 'T'
        && Shape(t, 11, 2) && t[13] == ':' && Shape(t, 14, 2) && t[16] == ':' && Shape(t, 17, 2);

    private static bool Shape(string t, int start, int count)
    {
        for (var i = start; i < start + count; i++)
        {
            if (i >= t.Length || t[i] is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }

    private static int Digits(string t, int start, int count)
    {
        var n = 0;
        for (var i = start; i < start + count; i++)
        {
            n = (n * 10) + (t[i] - '0');
        }

        return n;
    }

    /// <summary>Days from 1970-01-01 to the first of the given month (proleptic Gregorian; month 1..12).</summary>
    private static long DaysFromCivil(int year, int month, int day)
    {
        long y = month <= 2 ? year - 1 : year;
        var era = (y >= 0 ? y : y - 399) / 400;
        var yoe = y - (era * 400);
        var doy = ((153 * (month + (month > 2 ? -3 : 9))) + 2) / 5 + day - 1;
        var doe = (yoe * 365) + (yoe / 4) - (yoe / 100) + doy;
        return (era * 146_097) + doe - 719_468;
    }
}
