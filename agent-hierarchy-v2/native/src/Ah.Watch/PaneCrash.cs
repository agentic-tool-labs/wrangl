using System.Text.RegularExpressions;

namespace Ah.Watch;

/// <summary>
/// Names why a session ended from what its pane shows. It answers from a fixed vocabulary and never quotes the pane: the name goes into
/// a daemon alarm the Orchestrator trusts, and a pane holds whatever its session printed.
/// </summary>
internal static partial class PaneCrash
{
    // ponytail: one known crash; add a pattern when another one is seen in a pane.
    [GeneratedRegex(@"\bReact error #?(\d{1,6})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReactError();

    /// <summary>The crash named by the last one <paramref name="pane"/> shows, or null when it shows none.</summary>
    public static string? Cause(string? pane)
    {
        var last = ReactError().Matches(pane ?? string.Empty).LastOrDefault();
        return last is null ? null : $"React error {last.Groups[1].Value}";
    }
}
