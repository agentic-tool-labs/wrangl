using System.Globalization;
using System.Text;

namespace Ah.Hierarchy;

/// <summary><c>String.prototype.toLowerCase</c>: the default Unicode lower-casing, with U+0130 and the final sigma as the standard defines them.</summary>
internal static class JsLower
{
    internal static string Lower(string text)
    {
        var sb = new StringBuilder(text.Length);
        var runes = new List<Rune>();
        foreach (var r in text.EnumerateRunes())
        {
            runes.Add(r);
        }

        for (var i = 0; i < runes.Count; i++)
        {
            var r = runes[i];
            if (r.Value == 0x0130)
            {
                sb.Append("i̇");
            }
            else if (r.Value == 0x03A3)
            {
                sb.Append(FinalSigma(runes, i) ? 'ς' : 'σ');
            }
            else
            {
                sb.Append(Rune.ToLowerInvariant(r).ToString());
            }
        }

        return sb.ToString();
    }

    private static bool FinalSigma(List<Rune> runes, int index)
    {
        var before = false;
        for (var i = index - 1; i >= 0; i--)
        {
            if (CaseIgnorable(runes[i]))
            {
                continue;
            }

            before = Cased(runes[i]);
            break;
        }

        if (!before)
        {
            return false;
        }

        for (var i = index + 1; i < runes.Count; i++)
        {
            if (CaseIgnorable(runes[i]))
            {
                continue;
            }

            return !Cased(runes[i]);
        }

        return true;
    }

    private static bool Cased(Rune r)
    {
        var category = Rune.GetUnicodeCategory(r);
        return category is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter;
    }

    private static bool CaseIgnorable(Rune r)
    {
        switch (Rune.GetUnicodeCategory(r))
        {
            case UnicodeCategory.NonSpacingMark:
            case UnicodeCategory.EnclosingMark:
            case UnicodeCategory.Format:
            case UnicodeCategory.ModifierLetter:
            case UnicodeCategory.ModifierSymbol:
                return true;
            default:
                return r.Value is 0x27 or 0x2E or 0x3A or 0xB7 or 0x2018 or 0x2019 or 0x2024 or 0x2027 or 0xFE13 or 0xFE52 or 0xFE55 or 0xFF07 or 0xFF0E or 0xFF1A or 0x5F4;
        }
    }
}
