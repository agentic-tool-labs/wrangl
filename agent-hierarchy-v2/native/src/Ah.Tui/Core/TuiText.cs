using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Ah.Core.Json;
using Ah.Core.Time;

namespace Ah.Tui.Core;

/// <summary>
/// The text rules of the terminal client, ported from the Node client. Width is counted in display cells, a grapheme at a time, so a wide
/// character takes two and is never cut in half; ASCII output stays byte for byte what the Node client drew.
/// </summary>
internal static partial class TuiText
{
    [GeneratedRegex("\u001b(?:\\[[0-?]*[ -/]*[@-~]|\\][^\u0007\u001b]*(?:\u0007|\u001b\\\\)?|[PX^_][^\u001b]*(?:\u001b\\\\)?|[@-Z\\\\-_])?", RegexOptions.CultureInvariant)]
    private static partial Regex EscapeRe();

    [GeneratedRegex("[\u0000-\u0008\u000b-\u001f\u007f-\u009f‪-‮⁦-⁩]", RegexOptions.CultureInvariant)]
    private static partial Regex ControlRe();

    /// <summary>What a script's string conversion makes of a JSON value used as text: nothing for a missing value or null.</summary>
    internal static string Text(JsonValue? value) => value is null || value.Value.IsNull ? string.Empty : JsConvert.ToJsString(value);

    /// <summary>The text a template literal makes of a value: a missing value prints <c>undefined</c>.</summary>
    internal static string Template(JsonValue? value) => value is null ? "undefined" : JsConvert.ToJsString(value);

    /// <summary>Plain, single-line text: no escape sequence, no control character, no bidi override.</summary>
    internal static string Plain(string? value)
    {
        var s = EscapeRe().Replace(value ?? string.Empty, string.Empty);
        s = ControlRe().Replace(s, string.Empty);
        return s.Replace('\t', ' ').Replace('\n', ' ');
    }

    internal static string Plain(JsonValue? value) => Plain(Text(value));

    /// <summary>The code points of <paramref name="s"/>, each as its own string.</summary>
    internal static List<string> CodePoints(string s)
    {
        var list = new List<string>(s.Length);
        var i = 0;
        while (i < s.Length)
        {
            var take = char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]) ? 2 : 1;
            list.Add(s.Substring(i, take));
            i += take;
        }

        return list;
    }

    /// <summary>The ranges of code points that take two cells: Unicode 17 East Asian Wide and Fullwidth, assigned code points.</summary>
    private static readonly (int Lo, int Hi)[] Wide =
    [
        (0x1100, 0x115F), (0x231A, 0x231B), (0x2329, 0x232A), (0x23E9, 0x23EC), (0x23F0, 0x23F0), (0x23F3, 0x23F3), (0x25FD, 0x25FE), (0x2614, 0x2615),
        (0x2630, 0x2637), (0x2648, 0x2653), (0x267F, 0x267F), (0x268A, 0x268F), (0x2693, 0x2693), (0x26A1, 0x26A1), (0x26AA, 0x26AB), (0x26BD, 0x26BE),
        (0x26C4, 0x26C5), (0x26CE, 0x26CE), (0x26D4, 0x26D4), (0x26EA, 0x26EA), (0x26F2, 0x26F3), (0x26F5, 0x26F5), (0x26FA, 0x26FA), (0x26FD, 0x26FD),
        (0x2705, 0x2705), (0x270A, 0x270B), (0x2728, 0x2728), (0x274C, 0x274C), (0x274E, 0x274E), (0x2753, 0x2755), (0x2757, 0x2757), (0x2795, 0x2797),
        (0x27B0, 0x27B0), (0x27BF, 0x27BF), (0x2B1B, 0x2B1C), (0x2B50, 0x2B50), (0x2B55, 0x2B55), (0x2E80, 0x2E99), (0x2E9B, 0x2EF3), (0x2F00, 0x2FD5),
        (0x2FF0, 0x303E), (0x3041, 0x3096), (0x3099, 0x30FF), (0x3105, 0x312F), (0x3131, 0x318E), (0x3190, 0x31E5), (0x31EF, 0x321E), (0x3220, 0x3247),
        (0x3250, 0xA48C), (0xA490, 0xA4C6), (0xA960, 0xA97C), (0xAC00, 0xD7A3), (0xF900, 0xFA6D), (0xFA70, 0xFAD9), (0xFE10, 0xFE19), (0xFE30, 0xFE52),
        (0xFE54, 0xFE66), (0xFE68, 0xFE6B), (0xFF01, 0xFF60), (0xFFE0, 0xFFE6), (0x16FE0, 0x16FE4), (0x16FF0, 0x16FF6), (0x17000, 0x18CD5), (0x18CFF, 0x18D1E),
        (0x18D80, 0x18DF2), (0x1AFF0, 0x1AFF3), (0x1AFF5, 0x1AFFB), (0x1AFFD, 0x1AFFE), (0x1B000, 0x1B122), (0x1B132, 0x1B132), (0x1B150, 0x1B152), (0x1B155, 0x1B155),
        (0x1B164, 0x1B167), (0x1B170, 0x1B2FB), (0x1D300, 0x1D356), (0x1D360, 0x1D376), (0x1F004, 0x1F004), (0x1F0CF, 0x1F0CF), (0x1F18E, 0x1F18E), (0x1F191, 0x1F19A),
        (0x1F200, 0x1F202), (0x1F210, 0x1F23B), (0x1F240, 0x1F248), (0x1F250, 0x1F251), (0x1F260, 0x1F265), (0x1F300, 0x1F320), (0x1F32D, 0x1F335), (0x1F337, 0x1F37C),
        (0x1F37E, 0x1F393), (0x1F3A0, 0x1F3CA), (0x1F3CF, 0x1F3D3), (0x1F3E0, 0x1F3F0), (0x1F3F4, 0x1F3F4), (0x1F3F8, 0x1F43E), (0x1F440, 0x1F440), (0x1F442, 0x1F4FC),
        (0x1F4FF, 0x1F53D), (0x1F54B, 0x1F54E), (0x1F550, 0x1F567), (0x1F57A, 0x1F57A), (0x1F595, 0x1F596), (0x1F5A4, 0x1F5A4), (0x1F5FB, 0x1F64F), (0x1F680, 0x1F6C5),
        (0x1F6CC, 0x1F6CC), (0x1F6D0, 0x1F6D2), (0x1F6D5, 0x1F6D8), (0x1F6DC, 0x1F6DF), (0x1F6EB, 0x1F6EC), (0x1F6F4, 0x1F6FC), (0x1F7E0, 0x1F7EB), (0x1F7F0, 0x1F7F0),
        (0x1F90C, 0x1F93A), (0x1F93C, 0x1F945), (0x1F947, 0x1F9FF), (0x1FA70, 0x1FA7C), (0x1FA80, 0x1FA8A), (0x1FA8E, 0x1FAC6), (0x1FAC8, 0x1FAC8), (0x1FACD, 0x1FADC),
        (0x1FADF, 0x1FAEA), (0x1FAEF, 0x1FAF8), (0x20000, 0x2A6DF), (0x2A700, 0x2B81D), (0x2B820, 0x2CEAD), (0x2CEB0, 0x2EBE0), (0x2EBF0, 0x2EE5D), (0x2F800, 0x2FA1D),
        (0x30000, 0x3134A), (0x31350, 0x33479),
    ];

    private static bool IsWide(int point)
    {
        var (lo, hi) = (0, Wide.Length - 1);
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (point > Wide[mid].Hi)
            {
                lo = mid + 1;
            }
            else if (point < Wide[mid].Lo)
            {
                hi = mid - 1;
            }
            else
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The cells one grapheme takes: its first code point decides, except that an emoji variation selector or a pair of regional indicators (a flag) makes two.</summary>
    private static int CellsOf(string grapheme)
    {
        if (!Rune.TryGetRuneAt(grapheme, 0, out var first))
        {
            return 1;
        }

        if (grapheme.Contains('️', StringComparison.Ordinal) || (first.Value is >= 0x1F1E6 and <= 0x1F1FF && grapheme.Length >= 4))
        {
            return 2;
        }

        return Rune.GetUnicodeCategory(first) is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format ? 0
            : IsWide(first.Value) ? 2
            : 1;
    }

    private static IEnumerable<(string Text, int Cells)> Graphemes(string s)
    {
        var e = StringInfo.GetTextElementEnumerator(s);
        while (e.MoveNext())
        {
            var text = e.GetTextElement();
            yield return (text, CellsOf(text));
        }
    }

    internal static int Width(string s) => Ascii.IsValid(s) ? s.Length : Graphemes(s).Sum(static g => g.Cells);

    /// <summary>The start of <paramref name="s"/> that fits in <paramref name="n"/> cells; a wide character that would straddle the edge is left out.</summary>
    internal static string Take(string s, int n)
    {
        var kept = new StringBuilder();
        var at = 0;
        foreach (var (text, cells) in Graphemes(s))
        {
            if (at + cells > n)
            {
                break;
            }

            _ = kept.Append(text);
            at += cells;
        }

        return kept.ToString();
    }

    /// <summary>What of <paramref name="s"/> lies past its first <paramref name="n"/> cells; a wide character the edge cuts leaves a space for its second cell.</summary>
    internal static string Skip(string s, int n)
    {
        var kept = new StringBuilder();
        var at = 0;
        foreach (var (text, cells) in Graphemes(s))
        {
            if (at >= n)
            {
                _ = kept.Append(text);
            }
            else if (at + cells > n)
            {
                _ = kept.Append(' ', at + cells - n);
            }

            at += cells;
        }

        return kept.ToString();
    }

    /// <summary><paramref name="s"/> cut to at most <paramref name="n"/> cells, ending in an ellipsis when it was cut.</summary>
    internal static string Fit(string s, int n)
    {
        if (n <= 0)
        {
            return string.Empty;
        }

        if (Width(s) <= n)
        {
            return s;
        }

        return n == 1 ? "…" : Take(s, n - 1) + "…";
    }

    /// <summary><paramref name="s"/> in exactly <paramref name="n"/> cells, cut or filled on the right.</summary>
    internal static string Pad(string s, int n)
    {
        var fit = Width(s) > n ? Take(s, n) : s;
        return fit + new string(' ', Math.Max(0, n - Width(fit)));
    }

    /// <summary><paramref name="s"/> in exactly <paramref name="n"/> cells, cut on the right or filled on the left.</summary>
    internal static string PadLeft(string s, int n)
    {
        var fit = Width(s) > n ? Take(s, n) : s;
        return new string(' ', Math.Max(0, n - Width(fit))) + fit;
    }

    /// <summary><paramref name="text"/> broken into lines of at most <paramref name="n"/> cells; a line always takes at least one grapheme.</summary>
    internal static List<string> Wrap(string text, int n)
    {
        var lines = new List<string>();
        var line = new StringBuilder();
        var at = 0;
        foreach (var (grapheme, cells) in Graphemes(text))
        {
            if (at > 0 && at + cells > n)
            {
                lines.Add(line.ToString());
                _ = line.Clear();
                at = 0;
            }

            _ = line.Append(grapheme);
            at += cells;
        }

        lines.Add(line.ToString());
        return lines;
    }

    // ---------------------------------------------------------------- numbers and times

    /// <summary>A number, or <c>–</c> when the value is not a finite number.</summary>
    internal static string Num(JsonValue? value) =>
        value is { IsNumber: true } n && double.IsFinite(n.AsNumber()) ? JsConvert.NumberToString(n.AsNumber()) : "–";

    /// <summary>A script's <c>Math.round</c>: halves go toward positive infinity.</summary>
    internal static double Round(double x) => Math.Floor(x + 0.5);

    /// <summary>A script's <c>Date.parse</c> of a text value: epoch milliseconds, or NaN.</summary>
    internal static double ParseDate(JsonValue? value) =>
        value is { IsText: true } t && Timestamps.TryParseMs(t.AsString(), out var ms) ? ms : double.NaN;

    internal static double ParseDate(string? text) => text is not null && Timestamps.TryParseMs(text, out var ms) ? ms : double.NaN;

    /// <summary>A short age: seconds to <c>Ns</c>, minutes to <c>Nm</c>, hours to <c>Nh</c>, days to <c>Nd</c>; <c>–</c> when unknown.</summary>
    internal static string Age(JsonValue? at, string? now)
    {
        var a = ParseDate(at);
        var n = ParseDate(now);
        if (!double.IsFinite(a) || !double.IsFinite(n))
        {
            return "–";
        }

        var s = Math.Max(0, Round((n - a) / 1000));
        return s < 90
            ? Count(s) + "s"
            : s < 5400 ? Count(Round(s / 60)) + "m" : s < 129600 ? Count(Round(s / 3600)) + "h" : Count(Round(s / 86400)) + "d";
    }

    private static string Count(double d) => d.ToString("0", CultureInfo.InvariantCulture);

    /// <summary>A span of whole seconds as <c>Ns</c>, <c>Nm</c> or <c>Nh</c>; <c>–</c> when unknown.</summary>
    internal static string Span(double sec) =>
        !double.IsFinite(sec) ? "–" : sec < 90 ? Count(sec) + "s" : sec < 5400 ? Count(Round(sec / 60)) + "m" : Count(Round(sec / 3600)) + "h";

    /// <summary>Whole minutes from a span in milliseconds, rounded toward the present: an age floors, a time left rounds up.</summary>
    internal static string Minutes(double ms, bool up) =>
        double.IsFinite(ms) ? Count(Math.Max(0, up ? Math.Ceiling(ms / 60000) : Math.Floor(ms / 60000))) + "m" : "–";

    private static readonly Dictionary<string, string> StreamGlyphs = new(StringComparer.Ordinal)
    {
        ["working"] = "◐",
        ["idle"] = "○",
        ["blocked"] = "×",
        ["done"] = "✓",
        ["none"] = "·",
    };

    internal static string Glyph(JsonValue? state) =>
        state is { IsText: true } s && StreamGlyphs.TryGetValue(s.AsString(), out var g) ? g : "·";
}
