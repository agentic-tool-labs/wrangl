using System.Text.RegularExpressions;
using Ah.Abstractions;
using Ah.Core.Text;

namespace Ah.Config;

/// <summary>The phrases that show a user wants a team, for the nudge. They are read from the agent-team skill's description.</summary>
public interface ITeamIntent
{
    /// <summary>The quoted phrases of the skill's <c>description</c>, or none when the file or the field cannot be read.</summary>
    IReadOnlyList<string> Phrases();

    /// <summary>The first phrase the prompt contains, ignoring case, or null.</summary>
    string? MatchedPhrase(string prompt);
}

/// <summary>
/// Reads <c>skills/agent-team/SKILL.md</c> of the plugin at <paramref name="PluginRoot"/> at call time, so the list has one source.
/// </summary>
public sealed partial record TeamIntent(IFileReader Files, string PluginRoot) : ITeamIntent
{
    private static readonly string Lt = "\n\r" + (char)0x2028 + (char)0x2029;

    [GeneratedRegex("\"([^\"]+)\"", RegexOptions.CultureInvariant)]
    private static partial Regex Quoted();

    private string SkillPath => NodePath.Join(PluginRoot, "skills", "agent-team", "SKILL.md");

    public IReadOnlyList<string> Phrases()
    {
        string body;
        try
        {
            body = Files.ReadText(SkillPath);
        }
        catch (Exception)
        {
            return [];
        }

        var description = DescriptionOf(body);
        return description is null ? [] : Quoted().Matches(description).Select(m => m.Groups[1].Value).ToList();
    }

    /// <summary><c>/^---\n([\s\S]*?)\n---/</c>, then <c>/^description:\s*(.*)$/m</c> on the front matter.</summary>
    private static string? DescriptionOf(string body)
    {
        if (!body.StartsWith("---\n", StringComparison.Ordinal))
        {
            return null;
        }

        var end = body.IndexOf("\n---", 4, StringComparison.Ordinal);
        if (end < 0)
        {
            return null;
        }

        var front = body[4..end];
        for (var i = 0; i < front.Length; i++)
        {
            var atLineStart = i == 0 || Lt.Contains(front[i - 1]);
            if (!atLineStart || string.CompareOrdinal(front, i, "description:", 0, 12) != 0)
            {
                continue;
            }

            var p = i + 12;
            while (p < front.Length && JsText.IsSpace(front[p]))
            {
                p++;
            }

            var stop = p;
            while (stop < front.Length && !Lt.Contains(front[stop]))
            {
                stop++;
            }

            return front[p..stop];
        }

        return null;
    }

    public string? MatchedPhrase(string prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        var lower = JsLower(prompt);
        return Phrases().FirstOrDefault(phrase => lower.Contains(JsLower(phrase), StringComparison.Ordinal));
    }

    /// <summary><c>toLowerCase</c>: the invariant mapping, with U+0130 as an <c>i</c> and a combining dot, as JavaScript writes it.</summary>
    private static string JsLower(string s) => s.Replace("İ", "i̇", StringComparison.Ordinal).ToLowerInvariant();
}
