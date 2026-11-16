using System.Text.RegularExpressions;

namespace Ah.Hooks.Gates;

/// <summary>The branch helpers left from the push guard after ah-simplify removed the guard itself.</summary>
internal static partial class PushGuard
{
    [GeneratedRegex("^refs/heads/", RegexOptions.CultureInvariant)]
    private static partial Regex HeadsPrefix();

    internal static string ShortName(string reference) => HeadsPrefix().Replace(reference, string.Empty);
}
