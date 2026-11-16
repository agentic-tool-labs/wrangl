
namespace Ah.Core.Text;

/// <summary>JavaScript's definition of white space, for ported patterns and <c>String.prototype.trim</c>.</summary>
public static class JsText
{
    /// <summary>
    /// The characters of JavaScript's <c>\s</c>, written as regex escapes for use inside <c>[...]</c> in a
    /// <c>[GeneratedRegex]</c> pattern. .NET's own <c>\s</c> adds U+0085 and omits U+FEFF.
    /// </summary>
    public const string SpaceClass =
        "\\t\\n\\v\\f\\r \\u00a0\\u1680\\u2000-\\u200a\\u2028\\u2029\\u202f\\u205f\\u3000\\ufeff";

    public static bool IsSpace(char c) => (int)c switch
    {
        9 or 10 or 11 or 12 or 13 or 32 or 0xA0 or 0x1680 or 0x2028 or 0x2029 or 0x202F or 0x205F or 0x3000 or 0xFEFF => true,
        >= 0x2000 and <= 0x200A => true,
        _ => false,
    };

    /// <summary>JavaScript's <c>String.prototype.trim</c>.</summary>
    public static string Trim(string s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var start = 0;
        var end = s.Length;
        while (start < end && IsSpace(s[start]))
        {
            start++;
        }

        while (end > start && IsSpace(s[end - 1]))
        {
            end--;
        }

        return s[start..end];
    }

    /// <summary>The code points of <paramref name="s"/>, with a lone surrogate as its own code point (as the <c>u</c> flag sees it).</summary>
    public static IEnumerable<int> CodePoints(string s)
    {
        ArgumentNullException.ThrowIfNull(s);
        for (var i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                yield return char.ConvertToUtf32(s[i], s[i + 1]);
                i++;
            }
            else
            {
                yield return s[i];
            }
        }
    }
}
