namespace Ah.Daemon;

/// <summary>A request's query string as <c>Object.fromEntries(url.searchParams)</c> gives it.</summary>
internal static class QueryString
{
    /// <summary>The decoded pairs of <paramref name="raw"/> (the text after <c>?</c>); a repeated name keeps its last value.</summary>
    internal static Dictionary<string, string> Parse(string raw)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(raw))
        {
            return map;
        }

        foreach (var part in raw.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=', StringComparison.Ordinal);
            var name = Decode(eq < 0 ? part : part[..eq]);
            var value = eq < 0 ? string.Empty : Decode(part[(eq + 1)..]);
            map[name] = value;
        }

        return map;
    }

    private static string Decode(string text) => Uri.UnescapeDataString(text.Replace('+', ' '));
}
