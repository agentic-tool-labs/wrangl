using System.Globalization;
using System.Text.RegularExpressions;
using Ah.Core.Json;

namespace Ah.Watch;

/// <summary>
/// How a member's context reads, as both front ends draw it: the percentage of the model's window, floored, and the text and hover title that go with it.
/// The id is matched lowercased and the first rule wins: a "[1m]" suffix or a "1m" segment is the 1M window; a Claude id or alias is 200k; anything else is unknown and
/// the text is the token count. A 200k window that the count has outgrown is read as the 1M variant, because the model id a transcript reports does not say which is running.
/// </summary>
internal static partial class ContextReading
{
    private const long Window1M = 1_000_000;
    private const long Window200K = 200_000;

    [GeneratedRegex("^(opus|sonnet|haiku|fable)(?![a-z])")]
    private static partial Regex Alias();

    public static long? WindowOf(string? model)
    {
        var id = (model ?? string.Empty).Trim().ToLowerInvariant();
        return id.EndsWith("[1m]", StringComparison.Ordinal) || id.Split('-').Skip(1).Contains("1m") ? Window1M
            : id.StartsWith("claude-", StringComparison.Ordinal) || Alias().IsMatch(id) ? Window200K
            : null;
    }

    private static string Grouped(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

    private static string Compact(long n) =>
        n >= 1_000_000 ? (Math.Floor(n / 100_000d) / 10).ToString(CultureInfo.InvariantCulture) + "M" : n >= 1000 ? n / 1000 + "k" : n.ToString(CultureInfo.InvariantCulture);

    /// <summary>The window <paramref name="used"/> tokens are read against, or null when the model names none.</summary>
    public static long? WindowFor(string? model, long used)
    {
        var window = WindowOf(model);
        return window == Window200K && used > Window200K ? Window1M : window;
    }

    /// <summary>Adds <c>context_pct</c> (null with no window or no reading), <c>context_text</c>, <c>context_title</c>, <c>context_over</c> and <c>context_at</c>, when it was read, to a member's body.</summary>
    public static void Apply(JsonObject body, string? model, long? used, string? at = null)
    {
        var (pct, text, title, over) = Of(model, used);
        body["context_at"] = used is not null && at is not null ? at : JsonValue.Null;
        body["context_pct"] = pct is { } p ? JsonValue.FromNumber(p) : JsonValue.Null;
        body["context_text"] = text;
        body["context_title"] = title;
        body["context_over"] = over;
    }

    public static (long? Pct, string Text, string Title, bool Over) Of(string? model, long? used)
    {
        if (used is not { } n)
        {
            return (null, "—", "context not reported", false);
        }

        var window = WindowFor(model, n);
        var inferred = window != WindowOf(model);
        if (window is not { } w)
        {
            return (null, Compact(n), Grouped(n) + " tokens · window unknown", false);
        }

        var exact = $"{Grouped(n)} of {Grouped(w)} tokens · {model ?? "model not named"}{(inferred ? " · window inferred" : string.Empty)}";
        var pct = n * 100 / w;
        return n > w
            ? (pct, "100%+", exact + ". Above the assumed window: the model may have a larger window than this table knows", true)
            : (pct, n > 0 && pct < 1 ? "<1%" : pct.ToString(CultureInfo.InvariantCulture) + "%", exact, false);
    }
}
