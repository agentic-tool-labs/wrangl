namespace Ah.Tui.Core;

/// <summary>The colour rule.</summary>
internal static class TuiTheme
{
    /// <summary>Whether colour is used: <c>NO_COLOR</c> set and non-empty turns it off; an empty value keeps it.</summary>
    internal static bool UseColor(string? noColor) => string.IsNullOrEmpty(noColor);
}
