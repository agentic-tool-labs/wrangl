using System.Text.RegularExpressions;

namespace Ah.Hierarchy;

public static partial class HierarchyModule
{
    [GeneratedRegex("^[ \\t]*(?:[-*][ \\t]+)?(?:\\[[0-9]+\\][ \\t]+)?(?:[Ss]tatus|STATUS)[ \\t]*:[ \\t*`]*DONE(?![A-Za-z0-9_-])", RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex DoneStatusLine();

    /// <summary>Whether a report's text carries a <c>status: DONE</c> line: the one DONE detector of the QA nudge and the unreviewed-done alarm.</summary>
    public static bool IsDoneReport(string text) => DoneStatusLine().IsMatch(text);
}
