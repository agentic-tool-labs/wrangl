using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Ah.Core.Digests;

/// <summary>
/// A member resume is a mail entry with a deterministic <c>rewake-</c> id. The wake files older builds left under
/// <c>&lt;hier&gt;/rewake/</c> are not read: a member still failed is resumed again by the daemon's own recovery.
/// </summary>
public static class RewakePaths
{
    /// <summary>The id prefix of a member resume entry.</summary>
    public const string IdPrefix = "rewake-";

    /// <summary>A pending resume entry older than this is withdrawn.</summary>
    public static readonly TimeSpan Ceiling = TimeSpan.FromHours(6);

    /// <summary>The first 16 lower-case hex characters of the SHA-256 of the UTF-8 <paramref name="text"/>.</summary>
    public static string Digest16(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];

    /// <summary>The entry id of the <paramref name="k"/>th resume of one turn end of <paramref name="session"/> under <paramref name="hierarchyDir"/>.</summary>
    public static string Id(string hierarchyDir, string session, string turnEndKey, int k = 1) =>
        IdPrefix + Digest16(k <= 1 ? $"{hierarchyDir}|{session}|{turnEndKey}" : $"{hierarchyDir}|{session}|{turnEndKey}|{k.ToString(CultureInfo.InvariantCulture)}");
}
