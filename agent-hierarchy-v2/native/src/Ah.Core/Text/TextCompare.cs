namespace Ah.Core.Text;

public static class TextCompare
{
    /// <summary>
    /// The order of every name sort: ASCII letters folded to lower case first, then UTF-16 code units, then the
    /// original strings as the tie-break. Independent of culture. Returns -1, 0 or 1.
    /// </summary>
    public static int CompareText(string a, string b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        var folded = string.CompareOrdinal(FoldAscii(a), FoldAscii(b));
        return Math.Sign(folded != 0 ? folded : string.CompareOrdinal(a, b));
    }

    private static string FoldAscii(string s)
    {
        var at = s.AsSpan().IndexOfAnyInRange('A', 'Z');
        if (at < 0)
        {
            return s;
        }

        return string.Create(s.Length, s, static (span, source) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                var c = source[i];
                span[i] = c is >= 'A' and <= 'Z' ? (char)(c + 32) : c;
            }
        });
    }
}
