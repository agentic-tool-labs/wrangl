using System.Text.RegularExpressions;
using Ah.Core.Json;
using Ah.Core.Text;

namespace Ah.Hierarchy;

/// <summary>The pure text rules of message files: file names, frontmatter, anchors and the message token.</summary>
internal static partial class MessageText
{
    private const string Ws = JsText.SpaceClass;

    /// <summary>The line a response skeleton carries under <c>## [1] status</c>; it is replaced when the section is filled.</summary>
    internal const string StatusTagRule = MessageFront.StatusTagRule;

    /// <summary>The line a checkpoint response carries under <c>## [1] status</c> until the member replaces it with its answer.</summary>
    internal const string CheckpointAnswerPlaceholder = MessageFront.CheckpointAnswerPlaceholder;

    /// <summary>The line a release response carries under <c>## [1] status</c> until the member replaces it with its answer.</summary>
    internal const string ReleaseAnswerPlaceholder = MessageFront.ReleaseAnswerPlaceholder;

    [GeneratedRegex("^([0-9]{8}-[0-9]{6}-[0-9a-z]{4})--(" + Ah.Config.ConfigModule.RoleNamePattern + ")--([a-z0-9-]{1,32})--(request|response|note|link-note)\\.md\\z", RegexOptions.CultureInvariant)]
    private static partial Regex FilenameRegex();

    [GeneratedRegex("\\[hierarchy-msg[" + Ws + "]+([^\\]" + Ws + "]+)(?:[" + Ws + "]+(request|response|note))?[" + Ws + "]*\\]", RegexOptions.CultureInvariant)]
    private static partial Regex TokenRegex();

    [GeneratedRegex("^[0-9]{8}-[0-9]{6}-[0-9a-z]{4}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex IdRegex();

    /// <summary>Whether a token's first word is a message id rather than a legacy path key.</summary>
    internal static bool IsId(string word) => IdRegex().IsMatch(word);

    /// <summary>Every <c>[hierarchy-msg &lt;word&gt; [type]]</c> token as its first word and type (<c>request</c> when the token names none), in order.</summary>
    internal static IReadOnlyList<(string Word, string Type)> TokenParts(string? text) =>
        string.IsNullOrEmpty(text) ? [] : [.. TokenRegex().Matches(text).Select(static m => (m.Groups[1].Value, m.Groups[2].Success ? m.Groups[2].Value : "request"))];

    /// <summary>The longest <c>--title</c>, in Unicode code points: a few words.</summary>
    internal const int TitleMax = 60;

    /// <summary>The longest title shown for an exchange whose title is derived or read from a file, in Unicode code points.</summary>
    internal const int DerivedTitleMax = 80;

    [GeneratedRegex("^[" + Ws + "]*-[" + Ws + "]*\\[1\\][" + Ws + "]*goal:[" + Ws + "]*(.*)\\z", RegexOptions.CultureInvariant)]
    private static partial Regex GoalLineRegex();

    [GeneratedRegex("^#{1,6}[" + Ws + "]+([^" + Ws + "].*)\\z", RegexOptions.CultureInvariant)]
    private static partial Regex HeadingLineRegex();

    [GeneratedRegex("[" + Ws + "]#", RegexOptions.CultureInvariant)]
    private static partial Regex SpaceHashRegex();

    [GeneratedRegex("[" + Ws + "]+", RegexOptions.CultureInvariant)]
    private static partial Regex SpaceRunRegex();

    /// <summary>
    /// The title a <c>--title</c> value is stored as, or the reason it is refused. A value is accepted only when <see cref="ParseFrontmatter"/> reads the
    /// written line back as the trimmed value.
    /// </summary>
    internal static (string? Title, string? Refusal) CheckTitle(string raw)
    {
        var t = JsText.Trim(raw);
        if (t.Length == 0)
        {
            return (null, "--title needs text");
        }

        if (t.Any(IsLineBreakOrControl))
        {
            return (null, "--title must be one line of plain text");
        }

        if (SpaceHashRegex().IsMatch(t))
        {
            return (null, "--title cannot contain \" #\" (the frontmatter reader treats it as a comment)");
        }

        if (t.Contains("---", StringComparison.Ordinal))
        {
            return (null, "--title cannot contain ---");
        }

        if (t == "null")
        {
            return (null, "--title cannot be the word null");
        }

        var points = t.EnumerateRunes().Count();
        if (points > TitleMax)
        {
            return (null, $"title-too-long: keep it to a few words, like 'Make UI slick' (at most {TitleMax} characters, got {points})");
        }

        return IsReadable(t) ? (t, null) : (null, "title-not-readable: write the title in plain words, verb first, like 'Fix engram backup appetite'");
    }

    private static bool IsLineBreakOrControl(char c) => char.IsControl(c) || c is '\u2028' or '\u2029';

    /// <summary>A title a person can read: at least two tokens, one of them a plain word (letters and apostrophes only, so no digits, hyphens, slashes or equals signs).</summary>
    private static bool IsReadable(string title) =>
        SpaceRunRegex().Split(title).Where(static t => t.Length > 0).ToArray() is { Length: >= 2 } tokens
        && tokens.Any(static t => PlainWordRegex().IsMatch(EdgeTrim(t)));

    private static string EdgeTrim(string token)
    {
        var start = 0;
        var end = token.Length;
        while (start < end && !char.IsLetterOrDigit(token[start]))
        {
            start++;
        }

        while (end > start && !char.IsLetterOrDigit(token[end - 1]))
        {
            end--;
        }

        return token[start..end];
    }

    [GeneratedRegex("^\\p{L}[\\p{L}'\u2019]*\\p{L}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex PlainWordRegex();

    /// <summary>A title for a request made from free text: the first line flattened and cut like a derived title, with what <see cref="CheckTitle"/> refuses taken out; null when no readable title is left.</summary>
    internal static string? AutoTitle(string text)
    {
        var first = text.Split('\n').FirstOrDefault(static l => JsText.Trim(l).Length > 0 && !JsText.Trim(l).StartsWith("[hierarchy-", StringComparison.Ordinal)) ?? string.Empty;
        var line = Derived(first, TitleMax);
        line = HashAfterSpaceRegex().Replace(line, " ").Replace("---", "-", StringComparison.Ordinal);
        return CheckTitle(line).Title;
    }

    [GeneratedRegex("[" + Ws + "]#", RegexOptions.CultureInvariant)]
    private static partial Regex HashAfterSpaceRegex();

    /// <summary>
    /// The title of an exchange: the request's frontmatter <c>title</c>; else the goal line of the request excerpt; else its first heading
    /// (skeleton section heads excepted); else null. A derived title has its whitespace collapsed and is cut to <see cref="DerivedTitleMax"/> code points.
    /// </summary>
    internal static (string? Title, string? Source) ResolveTitle(string? frontmatterTitle, string? requestExcerpt)
    {
        if (!string.IsNullOrEmpty(frontmatterTitle))
        {
            var flat = Derived(frontmatterTitle);
            if (flat.Length > 0)
            {
                return (flat, "title");
            }
        }

        if (string.IsNullOrEmpty(requestExcerpt))
        {
            return (null, null);
        }

        var lines = requestExcerpt.Split('\n').Select(static l => l.TrimEnd('\r')).ToArray();
        if (lines.Select(static l => GoalLineRegex().Match(l)).FirstOrDefault(static m => m.Success) is { } goal)
        {
            var text = JsText.Trim(goal.Groups[1].Value);
            if (text.Length > 0 && !text.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                return (Derived(text), "goal");
            }
        }

        foreach (var line in lines)
        {
            if (!line.StartsWith("## [", StringComparison.Ordinal) && HeadingLineRegex().Match(line) is { Success: true } heading)
            {
                return (Derived(heading.Groups[1].Value), "heading");
            }
        }

        return (null, null);
    }

    private static string Derived(string text, int max = DerivedTitleMax)
    {
        var flat = JsText.Trim(SpaceRunRegex().Replace(new string([.. text.Select(static c => IsLineBreakOrControl(c) ? ' ' : c)]), " "));
        var cut = new System.Text.StringBuilder();
        var n = 0;
        foreach (var rune in flat.EnumerateRunes())
        {
            if (n++ == max)
            {
                break;
            }

            _ = cut.Append(rune.ToString());
        }

        return cut.ToString();
    }

    /// <summary><c>parseMsgFilename</c>: the parts of <c>&lt;id&gt;--&lt;to&gt;--&lt;slug&gt;--&lt;type&gt;.md</c> (any directory prefix is ignored), or null.</summary>
    internal static MsgFilenameParts? ParseFilename(string name)
    {
        var m = FilenameRegex().Match(NodePath.Basename(name));
        return m.Success ? new MsgFilenameParts(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value, m.Groups[4].Value) : null;
    }

    /// <summary>
    /// <c>parseFrontmatter</c>: flat <c>key: value</c> fields between <c>---</c> fences. A field line is
    /// <c>[A-Za-z_][A-Za-z0-9_-]*:</c>, white space, then a value that holds no line terminator other than leading
    /// white space; a trailing <c>\s+#...</c> is a comment; <c>null</c> and empty read as null. Null when there is no
    /// closing fence.
    /// </summary>
    internal static (JsonObject Fields, int End)? ParseFrontmatter(string text) => MessageFront.ParseFrontmatter(text);

    /// <summary>The <c>## [N] key</c> anchors at column 0 as <c>{line, n, key, text}</c>, line numbers 1-based.</summary>
    internal static JsonArray IndexAnchors(string text)
    {
        var anchors = new JsonArray();
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (!line.StartsWith("## [", StringComparison.Ordinal))
            {
                continue;
            }

            var p = 4;
            while (p < line.Length && line[p] is >= '0' and <= '9')
            {
                p++;
            }

            if (p == 4 || p + 1 >= line.Length || line[p] != ']' || line[p + 1] != ' ')
            {
                continue;
            }

            var keyStart = p + 2;
            var keyEnd = keyStart;
            while (keyEnd < line.Length && !JsText.IsSpace(line[keyEnd]))
            {
                keyEnd++;
            }

            if (keyEnd == keyStart)
            {
                continue;
            }

            anchors.Add(new JsonObject
            {
                ["line"] = i + 1,
                ["n"] = double.Parse(line.AsSpan(4, p - 4), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture),
                ["key"] = line[keyStart..keyEnd],
                ["text"] = line,
            });
        }

        return anchors;
    }

    /// <summary><c>bodyAfterFrontmatter</c> followed by <c>hasAuthoredContent</c>: whether anything beyond the skeleton was typed.</summary>
    internal static bool HasAuthoredContent(string text) => MessageFront.HasAuthoredContent(text);

    /// <summary><c>hasAuthoredContent</c>: whether any line of an already-extracted body is not a generated skeleton line.</summary>
    internal static bool HasAuthoredBody(string body) => MessageFront.HasAuthoredBody(body);
}
