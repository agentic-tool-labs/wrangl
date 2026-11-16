using System.Text;

namespace Ah.Core.Text;

/// <summary>
/// A team member's dispatch name, <c>&lt;prefix&gt;-&lt;role&gt;</c> or <c>&lt;prefix&gt;-&lt;role&gt;-&lt;n&gt;</c>, held to the longest
/// name herdr accepts. A name over the limit has its prefix cut and ended with <c>-&lt;4 hex&gt;</c>, a hash of the whole prefix, so
/// two prefixes that share their start still give different names, the role and number stay, and the name still reads as
/// <c>^(.+)-&lt;role&gt;(-\d+)?$</c>.
/// </summary>
public static class MemberNames
{
    /// <summary>The longest member name herdr accepts.</summary>
    public const int MaxLength = 32;

    private const int DigestLength = 5;

    /// <summary>
    /// The name of the <paramref name="ordinal"/>th member of <paramref name="role"/> under <paramref name="prefix"/> (the first
    /// has no number). Unchanged when it fits; otherwise the prefix is cut to leave room for the digest. When the role and number
    /// leave no room for even one prefix character beside the digest, the name is returned uncut: <see cref="Fits"/> says so.
    /// </summary>
    public static string Fit(string prefix, string role, int ordinal = 1) => Build(prefix, role, ordinal > 1 ? "-" + ordinal : string.Empty);

    /// <summary>Whether the name <see cref="Fit"/> gives is within <see cref="MaxLength"/>.</summary>
    public static bool Fits(string prefix, string role, int ordinal) => Fit(prefix, role, ordinal).Length <= MaxLength;

    /// <summary>
    /// The number <paramref name="name"/> carries as a member of <paramref name="role"/> under <paramref name="prefix"/>: 1 for the
    /// first, its number for a later one, 0 when it is not a name <see cref="Fit"/> makes for that prefix and role.
    /// </summary>
    public static int Ordinal(string name, string prefix, string role)
    {
        if (name == Fit(prefix, role))
        {
            return 1;
        }

        var marker = "-" + role + "-";
        var at = name.LastIndexOf(marker, StringComparison.Ordinal);
        if (at < 1)
        {
            return 0;
        }

        var digits = name.AsSpan(at + marker.Length);
        if (digits.Length == 0 || digits.IndexOfAnyExceptInRange('0', '9') >= 0 || !int.TryParse(digits, out var n))
        {
            return 0;
        }

        return name == Build(prefix, role, "-" + digits.ToString()) ? n : 0;
    }

    private static string Build(string prefix, string role, string number)
    {
        var tail = "-" + role + number;
        var room = MaxLength - tail.Length;
        if (prefix.Length <= room)
        {
            return prefix + tail;
        }

        var keep = room - DigestLength;
        return keep < 1 ? prefix + tail : prefix[..keep].TrimEnd('-') + "-" + Digest(prefix) + tail;
    }

    /// <summary>Four lower-case hex digits of the FNV-1a hash of the prefix: stable across runs and machines.</summary>
    private static string Digest(string prefix)
    {
        var hash = 2166136261u;
        foreach (var b in Encoding.UTF8.GetBytes(prefix))
        {
            hash = (hash ^ b) * 16777619u;
        }

        return ((hash >> 16) ^ (hash & 0xFFFF)).ToString("x4", System.Globalization.CultureInfo.InvariantCulture);
    }
}
