using System.Text.RegularExpressions;

namespace Ah.TestKit;

/// <summary>The wording ah's own recurring text must not carry into a session that has to continue a step: it reads as a refusal to get around.</summary>
public static partial class RefusalVocabulary
{
    /// <summary>The first banned phrase found in <paramref name="text"/>, or null when it is clean.</summary>
    public static string? FirstHit(string text) =>
        Pattern().Match(text) is { Success: true } m ? m.Value : null;

    [GeneratedRegex(@"classifier|\brefused\b|\bdenied\b|work(?:ing)? around|\bbypass\b|retry what was blocked|apply the refused edit", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
