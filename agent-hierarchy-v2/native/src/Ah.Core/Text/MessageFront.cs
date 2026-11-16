using System.Text.RegularExpressions;
using Ah.Core.Json;

namespace Ah.Core.Text;

/// <summary>The front matter of a message file and whether its body holds anything beyond the generated skeleton.</summary>
public static partial class MessageFront
{
    private const string Ws = JsText.SpaceClass;

    private const char LineSeparator = (char)0x2028;

    private const char ParagraphSeparator = (char)0x2029;

    /// <summary>The line a response skeleton carries under <c>## [1] status</c>; it is replaced when the section is filled.</summary>
    public const string StatusTagRule = "- Tag decisions, deviations, roadblocks, outcomes and details as \"Decision: …\", \"Deviation: …\", \"Roadblock: …\", \"Outcome: …\", \"Detail: …\"; tagged lines are journaled.";

    /// <summary>The line a checkpoint response carries under <c>## [1] status</c> until the member replaces it with its answer.</summary>
    public const string CheckpointAnswerPlaceholder = "- Checkpoint: <ready branch=<branch> sha=<sha> inflight=none | not-ready reason=<one line>>  (replace this line with exactly one of the two forms; ready only after your wip(resume) commit and with every background process of yours stopped)";

    /// <summary>The line a release response carries under <c>## [1] status</c> until the member replaces it with its answer.</summary>
    public const string ReleaseAnswerPlaceholder = "- Release: <ready branch=<branch> sha=<sha> inflight=none | not-ready reason=<one line>>  (replace this line with exactly one of the two forms; ready only if nothing is in flight and your work is committed)";

    [GeneratedRegex("^##[" + Ws + "]*\\[[0-9]+\\][" + Ws + "]*[^" + Ws + "]", RegexOptions.CultureInvariant)]
    private static partial Regex SkeletonHeadingRegex();

    [GeneratedRegex("^-[" + Ws + "]*(?:none|\\[[0-9]+\\][" + Ws + "]*[^:]+:[" + Ws + "]*)\\z", RegexOptions.CultureInvariant)]
    private static partial Regex SkeletonBulletRegex();

    /// <summary>
    /// <c>parseFrontmatter</c>: flat <c>key: value</c> fields between <c>---</c> fences. A field line is
    /// <c>[A-Za-z_][A-Za-z0-9_-]*:</c>, white space, then a value that holds no line terminator other than leading
    /// white space; a trailing <c>\s+#...</c> is a comment; <c>null</c> and empty read as null. Null when there is no
    /// closing fence.
    /// </summary>
    public static (JsonObject Fields, int End)? ParseFrontmatter(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return ParseFrontmatter(text.AsSpan());
    }

    /// <summary><see cref="ParseFrontmatter(string)"/> over characters that are not a string (a decoded window in a rented buffer): the fields own their text.</summary>
    public static (JsonObject Fields, int End)? ParseFrontmatter(ReadOnlySpan<char> text)
    {
        // ah2-1: the lines are taken one at a time up to the closing fence; the body after it is never split, and a line is a slice,
        // so the only text made is each field's value (and a name that is not one of the usual ones)
        var at = 0;
        if (!IsFence(NextLine(text, ref at)))
        {
            return null;
        }

        // the closing fence first: no fence, no fields; and the object is made at the size of the lines between the fences
        var scan = at;
        var lines = 0;
        var closed = false;
        while (scan <= text.Length)
        {
            if (IsFence(NextLine(text, ref scan)))
            {
                closed = true;
                break;
            }

            lines++;
        }

        if (!closed)
        {
            return null;
        }

        var fields = new JsonObject(Math.Min(lines, 64)); // capped: a long fenced block must not size a huge object
        for (var i = 1; ; i++)
        {
            var line = NextLine(text, ref at);
            if (i > lines)
            {
                return (fields, i + 1); // the closing fence found above
            }

            if (!TryField(line, out var key, out var raw))
            {
                continue;
            }

            var value = Trim(StripComment(raw));
            JsonValue stored = value.Length == 0 || value.SequenceEqual("null") ? JsonValue.Null : value.ToString();

            // `fields["__proto__"] = <string or null>` sets a prototype, not an own property.
            if (!key.SequenceEqual("__proto__"))
            {
                fields[Name(key)] = stored;
            }
        }
    }

    private static bool IsFence(ReadOnlySpan<char> line) => Trim(line).SequenceEqual("---");

    private static ReadOnlySpan<char> Trim(ReadOnlySpan<char> s)
    {
        var start = 0;
        var end = s.Length;
        while (start < end && JsText.IsSpace(s[start]))
        {
            start++;
        }

        while (end > start && JsText.IsSpace(s[end - 1]))
        {
            end--;
        }

        return s[start..end];
    }

    /// <summary>A field name as a string: the names every message carries are one shared string each, any other is made.</summary>
    private static string Name(ReadOnlySpan<char> key) => key switch
    {
        "id" => "id", "type" => "type", "to" => "to", "from" => "from", "slug" => "slug", "title" => "title", "parent" => "parent",
        "reason" => "reason", "eta" => "eta", "to_name" => "to_name", "from_name" => "from_name", "team" => "team",
        "team_file" => "team_file", "team_guide" => "team_guide", "created" => "created", "item" => "item", "status" => "status",
        "closed" => "closed", "closed_by" => "closed_by", "closed_reason" => "closed_reason", "partial" => "partial",
        "withdrawn" => "withdrawn", "sha" => "sha", "session" => "session", "review" => "review", "stream" => "stream",
        _ => key.ToString(),
    };

    /// <summary>The line of <paramref name="text"/> starting at <paramref name="at"/>, as <c>Split('\n')</c> would give it; <paramref name="at"/> moves past its line feed, or past the end after the last line.</summary>
    private static ReadOnlySpan<char> NextLine(ReadOnlySpan<char> text, ref int at)
    {
        var rest = text[at..];
        var end = rest.IndexOf('\n');
        at = end < 0 ? text.Length + 1 : at + end + 1;
        return end < 0 ? rest : rest[..end];
    }

    /// <summary>Makes every <c>\r\n</c> of <paramref name="text"/> a <c>\n</c> in place, as <c>Replace("\r\n", "\n")</c> gives; returns the new length.</summary>
    public static int CollapseCrLf(Span<char> text)
    {
        var first = text.IndexOf("\r\n");
        if (first < 0)
        {
            return text.Length;
        }

        var to = first;
        for (var i = first; i < text.Length; i++)
        {
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                continue;
            }

            text[to++] = text[i];
        }

        return to;
    }

    /// <summary>
    /// Where the text after the first <paramref name="lines"/> lines of <paramref name="text"/> starts: <c>text[offset..]</c> is what
    /// <c>string.Join('\n', text.Split('\n').Skip(lines))</c> gives, without splitting the text. The text's length when it has no more lines.
    /// </summary>
    public static int OffsetAfterLines(string text, int lines)
    {
        ArgumentNullException.ThrowIfNull(text);
        return OffsetAfterLines(text.AsSpan(), lines);
    }

    /// <summary><see cref="OffsetAfterLines(string, int)"/> over characters that are not a string.</summary>
    public static int OffsetAfterLines(ReadOnlySpan<char> text, int lines)
    {
        var at = 0;
        for (var i = 0; i < lines; i++)
        {
            var end = text[at..].IndexOf('\n');
            if (end < 0)
            {
                return text.Length;
            }

            at += end + 1;
        }

        return at;
    }

    /// <summary><c>bodyAfterFrontmatter</c> followed by <c>hasAuthoredContent</c>: whether anything beyond the skeleton was typed.</summary>
    public static bool HasAuthoredContent(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var fm = ParseFrontmatter(text);
        var body = fm is null ? JsText.Trim(text) : JsText.Trim(string.Join('\n', text.Split('\n').Skip(fm.Value.End)));
        return HasAuthoredBody(body);
    }

    /// <summary><c>hasAuthoredContent</c>: whether any line of an already-extracted body is not a generated skeleton line.</summary>
    public static bool HasAuthoredBody(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        foreach (var raw in body.Split('\n'))
        {
            var line = JsText.Trim(raw);
            if (line.Length == 0 || SkeletonHeadingRegex().IsMatch(line) || SkeletonBulletRegex().IsMatch(line) || line == StatusTagRule || line == ReleaseAnswerPlaceholder || line == CheckpointAnswerPlaceholder)
            {
                continue;
            }

            return true;
        }

        return false;
    }

    /// <summary>Matches <c>^([A-Za-z_][A-Za-z0-9_-]*):\s*(.*)$</c> where <c>.</c> excludes the four line terminators.</summary>
    private static bool TryField(ReadOnlySpan<char> line, out ReadOnlySpan<char> key, out ReadOnlySpan<char> value)
    {
        key = default;
        value = default;
        if (line.Length == 0 || !(IsKeyStart(line[0])))
        {
            return false;
        }

        var p = 1;
        while (p < line.Length && (IsKeyStart(line[p]) || line[p] is (>= '0' and <= '9') or '-'))
        {
            p++;
        }

        if (p >= line.Length || line[p] != ':')
        {
            return false;
        }

        var restStart = p + 1;
        var valueStart = restStart;
        while (valueStart < line.Length && JsText.IsSpace(line[valueStart]))
        {
            valueStart++;
        }

        for (var i = valueStart; i < line.Length; i++)
        {
            if (line[i] is '\n' or '\r' or LineSeparator or ParagraphSeparator)
            {
                return false;
            }
        }

        key = line[..p];
        value = line[valueStart..];
        return true;
    }

    private static bool IsKeyStart(char c) => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or '_';

    /// <summary><c>value.replace(/\s+#.*$/, "")</c>: cuts at the first white-space run that is followed by <c>#</c>.</summary>
    private static ReadOnlySpan<char> StripComment(ReadOnlySpan<char> value)
    {
        var i = 0;
        while (i < value.Length)
        {
            if (!JsText.IsSpace(value[i]))
            {
                i++;
                continue;
            }

            var j = i;
            while (j < value.Length && JsText.IsSpace(value[j]))
            {
                j++;
            }

            if (j < value.Length && value[j] == '#')
            {
                return value[..i];
            }

            i = j;
        }

        return value;
    }
}
