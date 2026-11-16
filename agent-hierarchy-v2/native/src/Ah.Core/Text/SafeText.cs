using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Ah.Core.Json;

namespace Ah.Core.Text;

/// <summary>
/// Renders text the product did not write (an environment value, an exception message, a command the model typed) so it can
/// be placed in text a person or a model reads: no escape sequence, no control character and no bidirectional override
/// survives, the result is JSON-quoted, and it is at most <see cref="DefaultCap"/> characters.
/// </summary>
public static partial class SafeText
{
    public const int DefaultCap = 200;

    [GeneratedRegex("\\x1b(?:\\[[0-?]*[ -/]*[@-~]|\\][^\\x07\\x1b]*(?:\\x07|\\x1b\\\\)?|[PX^_][^\\x1b]*(?:\\x1b\\\\)?|[@-Z\\\\-_])?", RegexOptions.CultureInvariant)]
    private static partial Regex EscapeSequence();

    [GeneratedRegex("[\\x00-\\x08\\x0b-\\x1f\\x7f-\\x9f\\u202a-\\u202e\\u2066-\\u2069]", RegexOptions.CultureInvariant)]
    private static partial Regex ControlOrBidi();

    /// <summary>
    /// <paramref name="value"/> with its escape sequences and control characters removed (a tab or newline becomes a space),
    /// written as a JSON string, and cut to <paramref name="cap"/> characters with a closing ellipsis when it was longer.
    /// </summary>
    public static string Render(string? value, int cap = DefaultCap) => Cut(JsonText.Stringify(Line(value), 0), cap);

    /// <summary><paramref name="value"/> as one line of inert text, unquoted and uncut: what <see cref="Render"/> quotes.</summary>
    public static string Line(string? value) =>
        ControlOrBidi().Replace(EscapeSequence().Replace(value ?? string.Empty, string.Empty), string.Empty).Replace('\t', ' ').Replace('\n', ' ');

    private static string Cut(string text, int cap)
    {
        if (cap <= 0)
        {
            return string.Empty;
        }

        var points = new List<string>();
        var e = StringInfo.GetTextElementEnumerator(text);
        while (e.MoveNext())
        {
            points.Add((string)e.Current);
        }

        if (points.Count <= cap)
        {
            return text;
        }

        var sb = new StringBuilder();
        foreach (var p in points.Take(cap - 1))
        {
            sb.Append(p);
        }

        return sb.Append('…').ToString();
    }
}
