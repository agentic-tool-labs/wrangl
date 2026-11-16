using Ah.Core.Json;

namespace Ah.Hooks.Plumbing;

/// <summary>Values read from files on disk, made safe to place inside text a hook injects into a session.</summary>
internal static class InjectedText
{
    /// <summary>The value without control characters, cut to <paramref name="cap"/> characters.</summary>
    public static string Clean(string value, int cap = 200)
    {
        ArgumentNullException.ThrowIfNull(value);
        var kept = new System.Text.StringBuilder(Math.Min(value.Length, cap));
        foreach (var ch in value)
        {
            if (!char.IsControl(ch) && ch != '\u2028' && ch != '\u2029')
            {
                kept.Append(ch);
            }
        }

        if (kept.Length > cap)
        {
            kept.Length = cap;
            if (char.IsHighSurrogate(kept[^1]))
            {
                kept.Length--;
            }
        }

        return kept.ToString();
    }

    /// <summary>The value cleaned and quoted as a JSON string, so nothing in it reads as the end of the quoted part.</summary>
    public static string Quote(string value, int cap = 200) => JsonText.Quote(Clean(value, cap));
}
