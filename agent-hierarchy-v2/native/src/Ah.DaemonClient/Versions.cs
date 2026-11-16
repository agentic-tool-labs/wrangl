using System.Globalization;

namespace Ah.DaemonClient;

internal static class Versions
{
    /// <summary>
    /// -1, 0 or 1 by semantic-version precedence: the dotted numeric core first (a part that is not numeric, or a missing one,
    /// counts as 0), then a version with a prerelease ranks below the same core without one, then the prerelease identifiers
    /// left to right (numeric ones numerically, the rest in ASCII order, numeric below the rest, more identifiers higher when
    /// all else is equal). Build metadata after <c>+</c> is ignored. tests/fixtures/version-order.txt is the shared table.
    /// </summary>
    internal static int Compare(string a, string b)
    {
        var (coreA, preA) = Split(a);
        var (coreB, preB) = Split(b);
        for (var i = 0; i < Math.Max(coreA.Length, coreB.Length); i++)
        {
            var d = (i < coreA.Length ? coreA[i] : 0) - (i < coreB.Length ? coreB[i] : 0);
            if (d != 0)
            {
                return d < 0 ? -1 : 1;
            }
        }

        if (preA.Length == 0 || preB.Length == 0)
        {
            return preA.Length == preB.Length ? 0 : preA.Length == 0 ? 1 : -1;
        }

        for (var i = 0; i < Math.Min(preA.Length, preB.Length); i++)
        {
            var c = CompareIdentifier(preA[i], preB[i]);
            if (c != 0)
            {
                return c;
            }
        }

        return preA.Length.CompareTo(preB.Length);
    }

    private static (long[] Core, string[] Pre) Split(string version)
    {
        var plus = version.IndexOf('+', StringComparison.Ordinal);
        var text = plus >= 0 ? version[..plus] : version;
        var dash = text.IndexOf('-', StringComparison.Ordinal);
        var core = dash >= 0 ? text[..dash] : text;
        var pre = dash >= 0 ? text[(dash + 1)..] : string.Empty;
        return ([.. core.Split('.').Select(static x => LeadingInt(x))], pre.Length == 0 ? [] : pre.Split('.'));
    }

    private static int CompareIdentifier(string a, string b)
    {
        var numericA = IsNumeric(a);
        var numericB = IsNumeric(b);
        if (numericA && numericB)
        {
            var x = a.TrimStart('0');
            var y = b.TrimStart('0');
            return x.Length != y.Length ? (x.Length < y.Length ? -1 : 1) : Math.Sign(string.CompareOrdinal(x, y));
        }

        if (numericA != numericB)
        {
            return numericA ? -1 : 1;
        }

        return Math.Sign(string.CompareOrdinal(a, b));
    }

    private static bool IsNumeric(string s) => s.Length > 0 && s.All(static ch => ch is >= '0' and <= '9');

    /// <summary><c>parseInt(x, 10) || 0</c>: the leading decimal digits, 0 when there are none.</summary>
    private static long LeadingInt(string text)
    {
        var s = text.TrimStart();
        var at = 0;
        var negative = false;
        if (at < s.Length && (s[at] == '+' || s[at] == '-'))
        {
            negative = s[at] == '-';
            at++;
        }

        var start = at;
        while (at < s.Length && s[at] is >= '0' and <= '9')
        {
            at++;
        }

        if (at == start || !long.TryParse(s.AsSpan(start, Math.Min(at - start, 15)), NumberStyles.None, CultureInfo.InvariantCulture, out var n))
        {
            return 0;
        }

        return negative ? -n : n;
    }
}
