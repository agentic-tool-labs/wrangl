using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Ah.Core.Json;
using Ah.Core.Text;

namespace Ah.Roster;

/// <summary>
/// Reading a harness's terminal screen: whether it is an idle, empty composer, which prompt it ends with, and which
/// answers may be relayed. Pure functions of the screen text and the harness tables.
/// </summary>
internal sealed partial class ScreenRules : IScreenRules
{
    private const string Lt = "\\n\\r\\u2028\\u2029";
    private const string Sp = JsText.SpaceClass;

    private static string TrimEndJs(string s)
    {
        var end = s.Length;
        while (end > 0 && JsText.IsSpace(s[end - 1]))
        {
            end--;
        }

        return s[..end];
    }

    private static string TrimStartJs(string s)
    {
        var start = 0;
        while (start < s.Length && JsText.IsSpace(s[start]))
        {
            start++;
        }

        return s[start..];
    }

    /// <summary>A screen as lines, trailing whitespace cut from each and trailing blank lines dropped.</summary>
    private static List<string> ScreenLines(string? screen)
    {
        var lines = (screen ?? string.Empty).Split('\n').Select(TrimEndJs).ToList();
        while (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return lines;
    }

    // ---------------------------------------------------------------- composer

    /// <summary>
    /// Whether <paramref name="screen"/> is the kind's idle, empty composer: among the final four lines (braille cells blanked) the
    /// last prompt row, with a glyph, a space and a placeholder, followed by at most one blank row, the status line and optionally
    /// the shortcuts line or one of the kind's hint lines.
    /// </summary>
    public bool IsComposer(string kind, string? screen)
    {
        var c = KindHarness.Get(kind)?.Composer;
        if (c is null)
        {
            return false;
        }

        var lines = (screen ?? string.Empty).Split('\n').ToList();
        while (lines.Count > 0 && JsText.Trim(lines[^1]).Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        var tail = lines.Skip(Math.Max(0, lines.Count - 4))
            .Select(l => string.Create(l.Length, l, static (span, src) =>
            {
                for (var i = 0; i < src.Length; i++)
                {
                    span[i] = src[i] is >= '⠀' and <= '⣿' ? ' ' : src[i];
                }
            }))
            .ToList();

        bool IsRow(string row)
        {
            if (row.Length < 2 || row[1] != ' ' || !c.Glyphs.Contains(row[0].ToString()))
            {
                return false;
            }

            return c.Placeholders.Contains(row[2..].TrimEnd(' '));
        }

        var at = -1;
        for (var i = 0; i < tail.Count; i++)
        {
            if (IsRow(tail[i]))
            {
                at = i;
            }
        }

        if (at < 0)
        {
            return false;
        }

        var below = tail.Skip(at + 1).Select(TrimEndJs).ToList();
        if (below.Count > 0 && below[0].Length == 0)
        {
            below.RemoveAt(0);
        }

        if (below.Count is < 1 or > 2)
        {
            return false;
        }

        return c.Status.IsMatch(below[0]) && (below.Count == 1 || c.Shortcuts.IsMatch(below[1]) || c.Hints.Contains(below[1]));
    }

    // ---------------------------------------------------------------- Claude Code

    [GeneratedRegex("^─{4,}", RegexOptions.CultureInvariant)]
    private static partial Regex ClaudeRule();

    [GeneratedRegex("^[ \\t]*[0-9]+\\.[ \\t]", RegexOptions.CultureInvariant)]
    private static partial Regex ClaudeNumberedRow();

    [GeneratedRegex("^Try \"[^" + Lt + "]*\"\\z", RegexOptions.CultureInvariant)]
    private static partial Regex ClaudePlaceholder();

    [GeneratedRegex("^[^" + Sp + "]+ [A-Za-z][A-Za-z0-9_'-]*… \\([0-9]+s(?![A-Za-z0-9_])", RegexOptions.CultureInvariant)]
    private static partial Regex ClaudeSpinner();

    [GeneratedRegex("(?<![A-Za-z0-9_])esc to interrupt(?![A-Za-z0-9_])", RegexOptions.CultureInvariant)]
    private static partial Regex ClaudeBusyFooter();

    [GeneratedRegex("^[" + Sp + "]*Do you want to [^" + Lt + "]+\\?[" + Sp + "]*\\z", RegexOptions.CultureInvariant)]
    private static partial Regex PermissionHeading();

    [GeneratedRegex("^[" + Sp + "]*Esc to cancel(?![A-Za-z0-9_])", RegexOptions.CultureInvariant)]
    private static partial Regex PermissionFooter();

    [GeneratedRegex("^[" + Sp + "]*☐ ", RegexOptions.CultureInvariant)]
    private static partial Regex PickerHeading();

    [GeneratedRegex("^[" + Sp + "]*Enter to select(?![A-Za-z0-9_])[^" + Lt + "]*(?<![A-Za-z0-9_])Esc to cancel(?![A-Za-z0-9_])", RegexOptions.CultureInvariant)]
    private static partial Regex PickerFooter();

    [GeneratedRegex("Quick safety check", RegexOptions.CultureInvariant)]
    private static partial Regex TrustHeading();

    [GeneratedRegex("^[" + Sp + "]*Enter to confirm(?![A-Za-z0-9_])[^" + Lt + "]*(?<![A-Za-z0-9_])Esc to cancel(?![A-Za-z0-9_])", RegexOptions.CultureInvariant)]
    private static partial Regex TrustFooter();

    [GeneratedRegex("^[" + Sp + "]*(❯)?[" + Sp + "]*([0-9]+)\\. ([^" + Lt + "]+)\\z", RegexOptions.CultureInvariant)]
    private static partial Regex NumberedOption();

    [GeneratedRegex("^[" + Sp + "]*(❯)?[" + Sp + "]*([^" + Sp + "][^" + Lt + "]*)\\z", RegexOptions.CultureInvariant)]
    private static partial Regex PlainOption();

    private sealed record ClaudePrompt(string Name, Regex Heading, Regex Footer, string Marker, bool Numbered);

    private static readonly ClaudePrompt[] ClaudePrompts =
    [
        new("permission", PermissionHeading(), PermissionFooter(), "❯", true),
        new("picker", PickerHeading(), PickerFooter(), "❯", true),
        new("trust-dialog", TrustHeading(), TrustFooter(), "❯", false),
    ];

    private const string ClaudeGlyph = "❯";

    /// <summary>The most rows the statusline and footer may take under the input box's bottom rule.</summary>
    private const int ClaudeRowsBelowRule = 6;

    private static List<(double N, bool Marked, string Label)> ClaudeOptions(List<string> lines, int from, int to, ClaudePrompt spec)
    {
        var result = new List<(double, bool, string)>();
        if (!spec.Numbered)
        {
            var marked = -1;
            for (var i = from; i < to && i < lines.Count; i++)
            {
                if (TrimStartJs(lines[i]).StartsWith(spec.Marker, StringComparison.Ordinal))
                {
                    marked = i;
                    break;
                }
            }

            if (marked < 0)
            {
                return result;
            }

            from = marked;
        }

        for (var i = from; i < to; i++)
        {
            var m = (spec.Numbered ? NumberedOption() : PlainOption()).Match(lines[i]);
            if (!m.Success)
            {
                continue;
            }

            var isMarked = m.Groups[1].Success && m.Groups[1].Value == "❯";
            if (spec.Numbered)
            {
                result.Add((double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture), isMarked, JsText.Trim(m.Groups[3].Value)));
            }
            else
            {
                result.Add((result.Count + 1, isMarked, JsText.Trim(m.Groups[2].Value)));
            }
        }

        return result;
    }

    private static JsonArray BlockJson(IEnumerable<(double N, bool Marked, string Label)> block)
    {
        var array = new JsonArray();
        foreach (var (n, marked, label) in block)
        {
            array.Add(new JsonObject { ["n"] = n, ["marked"] = marked, ["label"] = label });
        }

        return array;
    }

    private static JsonObject ClaudeResult(bool recognized, string? prompt, JsonArray block, bool composer, bool draft, bool busy, string? text = null)
    {
        var o = new JsonObject
        {
            ["recognized"] = recognized,
            ["prompt"] = prompt is null ? JsonValue.Null : prompt,
            ["block"] = block,
            ["composer"] = composer,
            ["draft"] = draft,
            ["busy"] = busy,
        };
        if (text is not null)
        {
            o["text"] = text;
        }

        return o;
    }

    /// <summary>
    /// What a Claude Code screen shows: <c>prompt</c> (<c>permission</c>, <c>picker</c>, <c>trust-dialog</c> or null) with its
    /// option <c>block</c>; <c>composer</c>, true only for an idle, empty composer; <c>draft</c>, true when the composer holds text;
    /// <c>busy</c>, true while a turn runs; <c>recognized</c>, false when the screen has no composer shape and no known prompt.
    /// </summary>
    public JsonObject RecognizeClaude(string? screen)
    {
        var lines = ScreenLines(screen);
        foreach (var spec in ClaudePrompts)
        {
            var footer = -1;
            for (var i = lines.Count - 1; i >= 0 && i >= lines.Count - 12; i--)
            {
                if (spec.Footer.IsMatch(lines[i]))
                {
                    footer = i;
                    break;
                }
            }

            if (footer < 0)
            {
                continue;
            }

            var head = -1;
            for (var i = footer - 1; i >= 0; i--)
            {
                if (spec.Heading.IsMatch(lines[i]))
                {
                    head = i;
                    break;
                }
            }

            if (head < 0)
            {
                continue;
            }

            var block = ClaudeOptions(lines, head + 1, footer, spec);
            if (block.Count < 2 || block.Count(o => o.Marked) != 1)
            {
                continue;
            }

            if (spec.Numbered && block.Where((o, i) => o.N != i + 1).Any())
            {
                continue;
            }

            return ClaudeResult(true, spec.Name, BlockJson(block), false, false, false);
        }

        var none = ClaudeResult(false, null, new JsonArray(), false, false, false);
        var bottom = -1;
        for (var i = lines.Count - 2; i >= 1 && i >= lines.Count - ClaudeRowsBelowRule - 1; i--)
        {
            if (ClaudeRule().IsMatch(lines[i]))
            {
                bottom = i;
                break;
            }
        }

        if (bottom < 0 || lines.Skip(bottom + 1).Any(l => JsText.Trim(l).StartsWith(ClaudeGlyph, StringComparison.Ordinal) || ClaudeNumberedRow().IsMatch(l)))
        {
            return none;
        }

        var top = -1;
        for (var i = bottom - 1; i >= 0 && i >= bottom - 60; i--)
        {
            if (ClaudeRule().IsMatch(lines[i]))
            {
                top = i;
                break;
            }
        }

        if (top < 0 || top + 1 >= bottom || !lines[top + 1].StartsWith(ClaudeGlyph, StringComparison.Ordinal))
        {
            return none;
        }

        var rows = lines.GetRange(top + 1, bottom - top - 1);
        var parts = new List<string> { rows[0][ClaudeGlyph.Length..] };
        parts.AddRange(rows.Skip(1));
        var content = string.Join(' ', parts.Select(JsText.Trim).Where(p => p.Length > 0));
        var empty = content.Length == 0 || ClaudePlaceholder().IsMatch(content);
        var footerText = string.Join('\n', lines.Skip(bottom + 1));
        var above = lines.Skip(Math.Max(0, top - 4)).Take(top - Math.Max(0, top - 4));
        var busy = ClaudeBusyFooter().IsMatch(footerText) || above.Any(l => ClaudeSpinner().IsMatch(l));
        return ClaudeResult(true, null, new JsonArray(), empty && !busy, !empty, busy, empty ? string.Empty : content);
    }

    // ---------------------------------------------------------------- other harnesses

    /// <summary>The marker line of an option block: <c>&lt;marker or space&gt; &lt;n&gt;. &lt;text&gt;</c>.</summary>
    private static (double N, bool Marked, string Text)? OptionStart(string line, string marker)
    {
        var m = OptionStartRegex().Match(line);
        if (!m.Success)
        {
            return null;
        }

        var lead = m.Groups[1].Value;
        if (lead != marker && lead != " ")
        {
            return null;
        }

        return (double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture), lead == marker, m.Groups[3].Value);
    }

    [GeneratedRegex("^([^" + Lt + "]|[\ud800-\udbff][\udc00-\udfff]) ([0-9]+)\\. ([^" + Lt + "]*)\\z", RegexOptions.CultureInvariant)]
    private static partial Regex OptionStartRegex();

    [GeneratedRegex("^ {5,}[^" + Sp + "]", RegexOptions.CultureInvariant)]
    private static partial Regex Continuation();

    [GeneratedRegex("^[^" + Sp + "]", RegexOptions.CultureInvariant)]
    private static partial Regex WrapStart();

    private sealed record Opt(double N, bool Marked, List<string> Parts);

    private static List<(double N, bool Marked, string Label)>? PromptBlock(PromptSpec spec, List<string> lines, string? agentStatus)
    {
        var herdrAgrees = spec.Herdr == "blocked" ? agentStatus == "blocked" : agentStatus != "blocked" && agentStatus != "working";
        if (!herdrAgrees || lines.Count == 0 || !spec.Footer.IsMatch(lines[^1]))
        {
            return null;
        }

        var end = lines.Count - 2;
        while (end >= 0 && lines[end].Length == 0)
        {
            end--;
        }

        var top = -1;
        for (var j = end; j >= 0; j--)
        {
            if (spec.OptionsAdjacent)
            {
                if (OptionStart(lines[j], spec.Marker) is { N: 1 })
                {
                    top = j;
                    break;
                }

                continue;
            }

            if (lines[j].Length != 0)
            {
                continue;
            }

            var below = OptionStart(lines[j + 1], spec.Marker);
            if (below is { N: 1 })
            {
                top = j + 1;
                break;
            }

            if (!(spec.OptionsSpaced && below is not null))
            {
                return null;
            }
        }

        if (top < 0)
        {
            return null;
        }

        var options = new List<Opt>();
        for (var k = top; k <= end; k++)
        {
            var line = lines[k];
            var start = OptionStart(line, spec.Marker);
            if (start is { } s)
            {
                options.Add(new Opt(s.N, s.Marked, [s.Text]));
            }
            else if (line.Length == 0)
            {
                continue;
            }
            else if (Continuation().IsMatch(line))
            {
                options[^1].Parts.Add(JsText.Trim(line));
            }
            else if (spec.Onboarding && WrapStart().IsMatch(line))
            {
                options[^1].Parts.Add(JsText.Trim(line));
            }
            else
            {
                return null;
            }
        }

        if (options.Where((o, i) => o.N != i + 1).Any() || options.Count(o => o.Marked) != 1)
        {
            return null;
        }

        var block = options
            .Select(o => (o.N, o.Marked, JsText.Trim(spec.JoinLabels ? string.Join(' ', o.Parts) : o.Parts[0])))
            .ToList();
        if (!block.All(o => spec.Labels.Any(l => l.Matches(o.Item3))))
        {
            return null;
        }

        if (!lines.Take(top).Any(l => l.Contains(spec.Heading, StringComparison.Ordinal)))
        {
            return null;
        }

        return block;
    }

    private static string? BlockingAnchor(KindHarness harness, List<string> lines)
    {
        foreach (var (name, anchors) in harness.BlockingAnchors)
        {
            for (var i = 0; i + anchors.Count <= lines.Count; i++)
            {
                var all = true;
                for (var k = 0; k < anchors.Count && all; k++)
                {
                    all = anchors[k].IsMatch(JsText.Trim(lines[i + k]));
                }

                if (all)
                {
                    return name;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// What a kind's screen shows, given Herdr's status for it: <c>composer</c> (the idle, empty composer while Herdr says neither
    /// working nor blocked); <c>prompt</c>, the one prompt whose layout it ends with when no other known heading is on the screen;
    /// <c>block</c>, that prompt's options.
    /// </summary>
    public JsonObject RecognizeScreen(string kind, string? screen, string? agentStatus)
    {
        var settled = agentStatus != "working" && agentStatus != "blocked";
        if (kind == "claude")
        {
            var c = RecognizeClaude(screen);
            var text = c.Get("text") is { IsText: true } t ? t.AsString() : string.Empty;
            return new JsonObject
            {
                ["composer"] = c["composer"].AsBool() && settled,
                ["prompt"] = c["prompt"],
                ["block"] = c["block"],
                ["busy"] = c["busy"],
                ["draft"] = c["draft"],
                ["recognized"] = c["recognized"],
                ["text"] = text,
            };
        }

        var h = KindHarness.Get(kind);
        var composer = IsComposer(kind, screen) && settled;
        var none = new JsonObject { ["composer"] = composer, ["prompt"] = JsonValue.Null, ["block"] = new JsonArray() };
        if (h is null)
        {
            return none;
        }

        var lines = ScreenLines(screen);
        var matched = h.Prompts
            .Select(p => (p.Key, Block: PromptBlock(p.Value, lines, agentStatus)))
            .Where(m => m.Block is not null)
            .ToList();
        if (matched.Count == 0 && BlockingAnchor(h, lines) is { } blocking)
        {
            return new JsonObject { ["composer"] = composer, ["prompt"] = blocking, ["block"] = new JsonArray() };
        }

        if (matched.Count != 1)
        {
            return none;
        }

        var (name, block) = matched[0];
        var full = screen ?? string.Empty;
        var others = h.Prompts.Where(p => p.Key != name).Select(p => p.Value.Heading).Concat(h.OtherHeadings);
        if (others.Any(heading => full.Contains(heading, StringComparison.Ordinal)))
        {
            return none;
        }

        return new JsonObject { ["composer"] = composer, ["prompt"] = name, ["block"] = BlockJson(block!) };
    }

    // ---------------------------------------------------------------- answers

    private static bool RowOffered(AnswerRow row, JsonValue? block)
    {
        string? digit = row.Keys.FirstOrDefault(k => k.Length == 1 && k[0] is >= '0' and <= '9');
        if (block is not { IsArray: true } list)
        {
            return false;
        }

        foreach (var o in list.AsArray())
        {
            var n = o.Member("n");
            var label = o.Member("label");
            var nOk = digit is null || (n is { IsNumber: true } nn && nn.AsNumber() == double.Parse(digit, System.Globalization.CultureInfo.InvariantCulture));
            if (nOk && label is { IsText: true } l && row.OnScreen.Matches(l.AsString()))
            {
                return true;
            }
        }

        return false;
    }

    public IReadOnlyList<string> RelayedPrompts(string kind) =>
        KindHarness.Get(kind)?.Options.Select(o => o.Key).ToList() ?? [];

    public IReadOnlyList<PromptRow>? PromptRows(string kind, string blockedBy) =>
        KindHarness.Get(kind)?.Options.FirstOrDefault(o => o.Key == blockedBy).Value?.Select(r => new PromptRow(r.Id, r.Keys, r.Grants)).ToList();

    public bool RowOffered(string kind, string blockedBy, string id, JsonValue? block) =>
        KindHarness.Get(kind)?.Options.FirstOrDefault(o => o.Key == blockedBy).Value?.FirstOrDefault(r => r.Id == id) is { } row
        && RowOffered(row, block);

    /// <summary>
    /// The answers to offer for <paramref name="blockedBy"/> while its option block is <paramref name="block"/>: each row the block offers,
    /// as <c>{id, label, description, grants}</c> with <c>&lt;cwd&gt;</c>, <c>&lt;config path&gt;</c>, <c>&lt;CODEX_HOME&gt;</c> and
    /// <c>&lt;trust root&gt;</c> filled from <paramref name="context"/>.
    /// </summary>
    public JsonArray PromptOptions(string kind, string blockedBy, JsonValue? block, JsonValue? context)
    {
        var result = new JsonArray();
        var rows = KindHarness.Get(kind)?.Options.FirstOrDefault(o => o.Key == blockedBy).Value;
        if (rows is null)
        {
            return result;
        }

        foreach (var row in rows.Where(r => RowOffered(r, block)))
        {
            var description = row.DescriptionFor(context);
            description = Fill(description, "<cwd>", context?.Member("cwd"));
            description = Fill(description, "<config path>", context?.Member("configPath"));
            description = Fill(description, "<CODEX_HOME>", context?.Member("codexHome"));
            description = Fill(description, "<trust root>", context?.Member("trustRoot"));
            result.Add(new JsonObject
            {
                ["id"] = row.Id,
                ["label"] = row.Label,
                ["description"] = description,
                ["grants"] = row.Grants,
            });
        }

        return result;
    }

    /// <summary><c>text.split(token).join(value)</c>: a missing value joins with a comma, as <c>Array.prototype.join(undefined)</c> does.</summary>
    private static string Fill(string text, string token, JsonValue? value) =>
        string.Join(value is null ? "," : JsConvert.ToJsString(value), text.Split(token));

    // ---------------------------------------------------------------- hash

    private const string CountdownSeparator = "[" + Sp + "\\u2500-\\u257f]+";

    /// <summary>
    /// The auto-deny sentence and its time value: clock (<c>1:39</c>) or seconds (<c>45s</c>, <c>9 seconds</c>), words matched
    /// with ASCII case folding only.
    /// </summary>
    [GeneratedRegex(
        "^[aA][uU][tT][oO][mM][aA][tT][iI][cC][aA][lL][lL][yY]" + CountdownSeparator
        + "[dD][eE][nN][yY]" + CountdownSeparator
        + "[tT][hH][iI][sS]" + CountdownSeparator
        + "[rR][eE][qQ][uU][eE][sS][tT]" + CountdownSeparator
        + "[iI][nN]" + CountdownSeparator
        + "([0-9]+:[0-9]{2}|[0-9]+[ \\t]*[sS](?:[eE][cC](?:[oO][nN][dD])?[sS]?)?(?![A-Za-z0-9_]))",
        RegexOptions.CultureInvariant)]
    private static partial Regex CountdownRegex();

    private const int CountdownMaxLines = 6;

    private static List<string> MaskCountdown(List<string> lines)
    {
        var output = new List<string>(lines);
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            for (var at = 0; at <= line.Length - "automatically".Length; at++)
            {
                if (string.Compare(line, at, "automatically", 0, "automatically".Length, StringComparison.OrdinalIgnoreCase) != 0
                    || !IsAsciiFoldEqual(line.AsSpan(at, "automatically".Length), "automatically"))
                {
                    continue;
                }

                var text = line[at..];
                var origins = new List<(int Line, int Pos, int Col)> { (i, 0, at) };
                for (var j = i + 1; j < lines.Count && j < i + CountdownMaxLines && lines[j].Length != 0; j++)
                {
                    origins.Add((j, text.Length + 1, 0));
                    text += "\n" + lines[j];
                }

                var m = CountdownRegex().Match(text);
                if (!m.Success)
                {
                    continue;
                }

                var from = m.Groups[1].Index;
                var to = from + m.Groups[1].Length;
                var o = origins.Last(x => x.Pos <= from);
                if (to > o.Pos + lines[o.Line].Length - o.Col)
                {
                    continue;
                }

                var col = o.Col + from - o.Pos;
                var target = output[o.Line];
                output[o.Line] = target[..col] + "<time>" + target[(col + (to - from))..];
            }
        }

        return output;
    }

    private static bool IsAsciiFoldEqual(ReadOnlySpan<char> a, string b)
    {
        for (var i = 0; i < a.Length; i++)
        {
            if (!char.IsAscii(a[i]) || char.ToLowerInvariant(a[i]) != b[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>SHA-256 hex of a screen read with trailing whitespace cut from each line, a countdown's time masked, and trailing blank lines dropped.</summary>
    public string ScreenHash(string? screen)
    {
        var lines = MaskCountdown((screen ?? string.Empty).Split('\n').Select(TrimEndJs).ToList());
        while (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return Convert.ToHexStringLower(SHA256.HashData(Utf8Text.Encode(string.Join('\n', lines))));
    }

}
