using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Ah.Core.Json;
using Ah.Core.Text;

namespace Ah.Config;

/// <summary>
/// The agent-file frontmatter readers (lenient for a user's file, strict for a pack's) and the finding records they
/// produce. Pure text in, JSON values out: the one place those texts are read.
/// </summary>
internal static partial class PackAgentText
{
    internal static readonly string[] PackAgentKeys = ["name", "description", "model", "tools", "disallowedTools", "color", "effort", "maxTurns"];

    internal static readonly string[] FlaggedTools = ["Bash", "Agent", "Write", "Edit"];

    internal const string McpToolPrefix = "mcp__";

    private static readonly string[] YamlNullBool = ["~", "null", "true", "false", "yes", "no", "on", "off", "y", "n"];

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_-]*\\z", RegexOptions.CultureInvariant)]
    private static partial Regex PackToolNameRegex();

    [GeneratedRegex("^mcp__[A-Za-z0-9_-]+__\\*\\z", RegexOptions.CultureInvariant)]
    private static partial Regex ServerGrantRegex();

    [GeneratedRegex("^(\\*|[A-Za-z_][A-Za-z0-9_.-]*(\\([^()]*\\))?)\\z", RegexOptions.CultureInvariant)]
    private static partial Regex ToolTokenRegex();

    [GeneratedRegex("^[|>][-+]?\\z", RegexOptions.CultureInvariant)]
    private static partial Regex BlockStyleRegex();

    [GeneratedRegex("^-?(0|[1-9][0-9]*)\\z", RegexOptions.CultureInvariant)]
    private static partial Regex IntegerRegex();

    [GeneratedRegex("^([A-Za-z_][A-Za-z0-9_.-]*)\\(", RegexOptions.CultureInvariant)]
    private static partial Regex ScopedToolRegex();

    internal static bool IsLineTerminator(char c) => c is '\n' or '\r' or '\u2028' or '\u2029';

    private static bool IsAscii(char c, bool letters, bool digits) =>
        (letters && c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z')) || (digits && c is >= '0' and <= '9');

    /// <summary>The words YAML (1.1 or 1.2) reads as null or a boolean, in any ASCII case.</summary>
    internal static bool IsYamlNullBool(string v) =>
        v.All(c => c < 128) && Array.IndexOf(YamlNullBool, ConfigRegistry.LowerForAsciiPatterns(v)) >= 0;

    internal static bool IsPackToolName(string e) => (PackToolNameRegex().IsMatch(e) && !IsYamlNullBool(e)) || IsServerGrant(e);

    /// <summary><c>mcp__&lt;server&gt;__*</c>: every tool of one MCP server.</summary>
    internal static bool IsServerGrant(string e) => ServerGrantRegex().IsMatch(e);

    /// <summary><c>mcp__&lt;server&gt;</c> with no tool part: Claude Code's <c>tools</c> field matches nothing by it.</summary>
    internal static bool IsBareServerName(string e) =>
        e.StartsWith(McpToolPrefix, StringComparison.Ordinal) && e.Length > McpToolPrefix.Length && !e[McpToolPrefix.Length..].Contains("__", StringComparison.Ordinal);

    /// <summary>The number of leading characters <c>trimStart</c> would remove.</summary>
    internal static int IndentOf(string line)
    {
        var i = 0;
        while (i < line.Length && JsText.IsSpace(line[i]))
        {
            i++;
        }

        return i;
    }

    internal static string Unquote(string v)
    {
        var t = JsText.Trim(v);
        if (t.Length >= 2 && t[0] == '"' && t[^1] == '"')
        {
            var inner = t[1..^1];
            var sb = new StringBuilder(inner.Length);
            for (var i = 0; i < inner.Length; i++)
            {
                if (inner[i] == '\\' && i + 1 < inner.Length && inner[i + 1] is '"' or '\\' or 'n' or 't')
                {
                    var c = inner[i + 1];
                    sb.Append(c == 'n' ? '\n' : c == 't' ? '\t' : c);
                    i++;
                }
                else
                {
                    sb.Append(inner[i]);
                }
            }

            return sb.ToString();
        }

        if (t.Length >= 2 && t[0] == '\'' && t[^1] == '\'')
        {
            return t[1..^1].Replace("''", "'", StringComparison.Ordinal);
        }

        return t;
    }

    /// <summary>JavaScript's <c>String.prototype.split(/\r?\n/)</c>.</summary>
    internal static List<string> SplitLines(string text)
    {
        var pieces = text.Split('\n').ToList();
        for (var i = 0; i < pieces.Count - 1; i++)
        {
            if (pieces[i].EndsWith('\r'))
            {
                pieces[i] = pieces[i][..^1];
            }
        }

        return pieces;
    }

    /// <summary>
    /// <c>/^([A-Za-z][A-Za-z0-9_-]*):(?:\s(.*)|$)/</c>: the key, and the rest of the line after the separator
    /// (null when the line ends at the colon; <c>.</c> stops at a line terminator).
    /// </summary>
    internal static bool TryKeyLine(string line, out string key, out string? rest)
    {
        key = string.Empty;
        rest = null;
        if (line.Length == 0 || !IsAscii(line[0], true, false))
        {
            return false;
        }

        var i = 1;
        while (i < line.Length && (IsAscii(line[i], true, true) || line[i] is '_' or '-'))
        {
            i++;
        }

        if (i >= line.Length || line[i] != ':')
        {
            return false;
        }

        key = line[..i];
        i++;
        if (i == line.Length)
        {
            return true;
        }

        if (!JsText.IsSpace(line[i]))
        {
            return false;
        }

        var j = i + 1;
        while (j < line.Length && !IsLineTerminator(line[j]))
        {
            j++;
        }

        rest = line[(i + 1)..j];
        return true;
    }

    /// <summary><c>/^\s*-\s+(\S.*)$/</c>.</summary>
    private static bool TryListItem(string line, out string item)
    {
        item = string.Empty;
        var i = IndentOf(line);
        if (i >= line.Length || line[i] != '-')
        {
            return false;
        }

        i++;
        var j = i;
        while (j < line.Length && JsText.IsSpace(line[j]))
        {
            j++;
        }

        if (j == i || j >= line.Length)
        {
            return false;
        }

        for (var k = j; k < line.Length; k++)
        {
            if (IsLineTerminator(line[k]))
            {
                return false;
            }
        }

        item = line[j..];
        return true;
    }

    /// <summary><c>line.replace(/^\s*-\s+/, "")</c>, and whether the line had that shape followed by a non-space character.</summary>
    private static (string Stripped, bool Item) StripListMarker(string line)
    {
        var i = IndentOf(line);
        if (i >= line.Length || line[i] != '-')
        {
            return (line, false);
        }

        var j = i + 1;
        var start = j;
        while (j < line.Length && JsText.IsSpace(line[j]))
        {
            j++;
        }

        return j == start ? (line, false) : (line[j..], j < line.Length);
    }

    private static bool IsContinuation(string line) =>
        line.Length > 0 && (JsText.IsSpace(line[0]) || (line[0] == '-' && (line.Length == 1 || JsText.IsSpace(line[1]))));

    internal static string? YamlValueProblem(string value)
    {
        if (value[0] is '"' or '\'')
        {
            var quote = value[0];
            var ok = value.Length >= 2 && value[^1] == quote && !value.AsSpan(1, value.Length - 2).Contains(quote)
                     && (quote == '\'' || !value.AsSpan(1, value.Length - 2).Contains('\\'));
            return ok ? null : "a quoted value must close at the end of the line, with no escape inside (no \\ or '')";
        }

        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == ':' && (i + 1 == value.Length || JsText.IsSpace(value[i + 1])))
            {
                return "a plain value can't hold \": \" or end in \":\" (quote it)";
            }
        }

        if (IsYamlNullBool(value))
        {
            return "a plain value YAML reads as null or a boolean (quote it)";
        }

        if ("@`%!|>[]{},?".Contains(value[0]) || (value[0] == '-' && (value.Length == 1 || JsText.IsSpace(value[1]))))
        {
            return "a plain value can't start with a YAML indicator (quote it)";
        }

        return null;
    }

    private static int CodePointAt(string s, int index) =>
        char.IsHighSurrogate(s[index]) && index + 1 < s.Length && char.IsLowSurrogate(s[index + 1])
            ? char.ConvertToUtf32(s[index], s[index + 1])
            : s[index];

    /// <summary><c>U+XXXX at line L, column C</c> for the character at <paramref name="index"/> of <paramref name="line"/> (columns in code points).</summary>
    internal static string CharWhere(int lineNo, string line, int index)
    {
        var column = 1;
        for (var i = 0; i < index; i++)
        {
            if (char.IsHighSurrogate(line[i]) && i + 1 < index && char.IsLowSurrogate(line[i + 1]))
            {
                i++;
            }

            column++;
        }

        return $"U+{CodePointAt(line, index).ToString("X4", CultureInfo.InvariantCulture)} at line {lineNo}, column {column}";
    }

    /// <summary>The index of the first character in <paramref name="s"/> outside printable ASCII, or -1.</summary>
    private static int FirstOutsideAscii(string s, int length)
    {
        for (var i = 0; i < length; i++)
        {
            if (s[i] is < ' ' or > '~')
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// A UTF-16 index of the first code point a pack agent's description may not hold (U+FFFE, U+FFFF, a private-use character, or a space separator other than U+0020), or -1.
    /// </summary>
    private static int FirstBadDescriptionChar(string s, int from)
    {
        for (var i = from; i < s.Length; i++)
        {
            var cp = CodePointAt(s, i);
            var space = cp != 0x20 && cp is not (>= 0xD800 and <= 0xDFFF) && CharUnicodeInfo.GetUnicodeCategory(cp) == UnicodeCategory.SpaceSeparator;
            var bad = cp is 0xFFFE or 0xFFFF or (>= 0xE000 and <= 0xF8FF) or (>= 0xF0000 and <= 0x10FFFF) || space;
            if (bad)
            {
                return i;
            }

            if (cp > 0xFFFF)
            {
                i++;
            }
        }

        return -1;
    }

    // ---------------------------------------------------------------- findings

    internal static JsonObject Fix(string kind, string detail) => new() { ["kind"] = kind, ["detail"] = detail };

    internal static JsonObject Finding(string level, string code, string? path, string? field, string message, JsonArray fix) => new()
    {
        ["level"] = level,
        ["code"] = code,
        ["path"] = string.IsNullOrEmpty(path) ? JsonValue.Null : path,
        ["field"] = string.IsNullOrEmpty(field) ? JsonValue.Null : field,
        ["message"] = message,
        ["fix"] = fix,
    };

    internal static JsonArray Fixes(params JsonObject[] fixes)
    {
        var a = new JsonArray();
        foreach (var f in fixes)
        {
            a.Add(f);
        }

        return a;
    }

    private static JsonObject EmptyFrontmatter() => new()
    {
        ["frontmatter"] = false,
        ["name"] = JsonValue.Null,
        ["description"] = JsonValue.Null,
        ["model"] = JsonValue.Null,
        ["tools"] = JsonValue.Null,
        ["disallowedTools"] = JsonValue.Null,
        ["parseErrors"] = new JsonArray(),
    };

    private static JsonValue TextOrNull(string? s) => string.IsNullOrEmpty(s) ? JsonValue.Null : s;

    // ---------------------------------------------------------------- the strict reader

    private sealed class Pending
    {
        public string Key = string.Empty;
        public string Kind = "none";
        public string Value = string.Empty;
        public string Style = string.Empty;
        public int? Indent;
        public List<string> Items = [];
        public List<string> Block = [];
    }

    /// <summary>
    /// <c>packAgentParse</c>: a pack agent's frontmatter read strictly, so that it reads exactly what YAML would. Returns
    /// the findings (every one an error) and the frontmatter in the shape <see cref="ParseAgentFrontmatter"/> returns.
    /// </summary>
    internal static (JsonArray Findings, JsonObject Fm) PackAgentParse(string text, string? path)
    {
        var findings = new JsonArray();
        var fm = EmptyFrontmatter();
        void Err(string code, string message) => findings.Add(Finding("error", code, path, null, message,
            Fixes(Fix("edit-frontmatter", $"a pack agent's frontmatter may hold only {string.Join(", ", PackAgentKeys)}, as plain unquoted keys"))));

        var lines = text.Split('\n');
        var end = -1;
        if (lines[0] == "---")
        {
            for (var k = 1; k < lines.Length; k++)
            {
                if (lines[k] == "---")
                {
                    end = k;
                    break;
                }
            }
        }

        if (end == -1)
        {
            Err("pack-agent-frontmatter", "no --- frontmatter block");
            return (findings, fm);
        }

        fm["frontmatter"] = true;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        Pending? cur = null;

        void ToolEntries(string key, IEnumerable<string> entries)
        {
            var list = entries.ToList();
            foreach (var e in list.Where(e => !IsPackToolName(e)))
            {
                var wildcard = e.StartsWith(McpToolPrefix, StringComparison.Ordinal) && e.Contains('*', StringComparison.Ordinal)
                    ? "; the only wildcard accepted is mcp__<server>__*"
                    : string.Empty;
                Err("pack-agent-tools", $"{key} entry {JsonText.Stringify(e, 0)} isn't a plain tool name (letters, digits, _ and -, and not a word YAML reads as null or a boolean){wildcard}");
            }

            if (key == "tools")
            {
                foreach (var e in list.Where(e => IsPackToolName(e) && IsBareServerName(e)))
                {
                    findings.Add(Finding("warn", "pack-agent-tools", path, key,
                        $"tools entry {JsonText.Stringify(e, 0)} matches no tools in Claude Code's tools field; write {e}__*", Fixes(Fix("edit-frontmatter", $"write {e}__*"))));
                }
            }

            fm[key] = ConfigRegistry.StringArray(list.Where(IsPackToolName));
        }

        void Finish()
        {
            if (cur is null)
            {
                return;
            }

            var c = cur;
            cur = null;
            if (c.Key is "tools" or "disallowedTools")
            {
                ToolEntries(c.Key, c.Kind == "list" ? c.Items : c.Value.Split(',').Select(JsText.Trim));
            }
            else if (c.Key == "description")
            {
                if (c.Kind == "block")
                {
                    var block = new List<string>(c.Block);
                    while (block.Count > 0 && block[^1].Length == 0)
                    {
                        block.RemoveAt(block.Count - 1);
                    }

                    fm["description"] = c.Style[0] == '|' ? string.Join('\n', block) : string.Join(' ', block.Where(l => l.Length > 0));
                }
                else
                {
                    fm["description"] = TextOrNull(Unquote(c.Value));
                }
            }
            else if (c.Key is "name" or "model")
            {
                fm[c.Key] = TextOrNull(Unquote(c.Value));
            }
        }

        for (var i = 0; i < end - 1; i++)
        {
            var line = lines[i + 1];
            var content = cur is { Kind: "block" } && line.Length > 0 && line[0] == ' '
                ? IndentOfSpaces(line)
                : line.StartsWith("description: ", StringComparison.Ordinal) ? "description: ".Length : line.Length;
            var outside = FirstOutsideAscii(line, content);
            if (outside >= 0)
            {
                Err("pack-agent-line", $"{CharWhere(i + 2, line, outside)} is outside printable ASCII, which a pack agent's frontmatter keeps to outside its description");
            }

            var inside = FirstBadDescriptionChar(line, content);
            if (inside >= 0)
            {
                Err("pack-agent-line", $"{CharWhere(i + 2, line, inside)} can't be in a pack agent's description (a space other than U+0020, a private-use character, U+FFFE or U+FFFF)");
            }

            if (JsText.Trim(line).Length == 0)
            {
                if (cur is { Kind: "block" })
                {
                    cur.Block.Add(string.Empty);
                }

                continue;
            }

            if (IndentOfSpaces(line) < line.Length && line[IndentOfSpaces(line)] == '\t')
            {
                Err("pack-agent-line", $"line {JsonText.Stringify(line, 0)} is indented with a tab");
                continue;
            }

            if (HasComment(line))
            {
                Err("pack-agent-line", $"line {JsonText.Stringify(line, 0)} holds a # comment, which YAML would drop");
                continue;
            }

            if (IsContinuation(line))
            {
                if (cur is null)
                {
                    Err("pack-agent-line", $"line {JsonText.Stringify(line, 0)} continues no key");
                }
                else if (cur.Kind == "list")
                {
                    var isItem = TryListItem(line, out var item);
                    if (isItem)
                    {
                        cur.Indent ??= IndentOf(line);
                    }

                    if (!isItem)
                    {
                        Err("pack-agent-line", $"line {JsonText.Stringify(line, 0)} isn't a \"- item\" of the list above");
                    }
                    else if (IndentOf(line) != cur.Indent)
                    {
                        Err("pack-agent-line", $"line {JsonText.Stringify(line, 0)} isn't indented like the list's first item");
                    }
                    else if (item[0] is '&' or '*' || HasMergeKey(item))
                    {
                        Err("pack-agent-yaml", $"YAML anchor, alias or merge key: {JsonText.Stringify(JsText.Trim(line), 0)}");
                    }
                    else
                    {
                        cur.Items.Add(JsText.Trim(item));
                    }
                }
                else if (cur.Kind == "block")
                {
                    var spaced = JsText.IsSpace(line[0]);
                    if (spaced)
                    {
                        cur.Indent ??= IndentOf(line);
                    }

                    if (!spaced)
                    {
                        Err("pack-agent-line", $"line {JsonText.Stringify(line, 0)} isn't indented inside the block above");
                    }
                    else if (IndentOf(line) < cur.Indent)
                    {
                        Err("pack-agent-line", $"line {JsonText.Stringify(line, 0)} is indented less than the block's first line, which ends the block, yet it isn't a key");
                    }
                    else
                    {
                        cur.Block.Add(line[cur.Indent!.Value..]);
                    }
                }
                else
                {
                    Err("pack-agent-line", $"line {JsonText.Stringify(line, 0)} continues a one-line value — use \"- item\" lines under an empty tools or disallowedTools, or a | or > block under description");
                }

                continue;
            }

            Finish();
            if (line.StartsWith("<<", StringComparison.Ordinal) && HasMergeKeyAtStart(line))
            {
                Err("pack-agent-yaml", $"YAML merge key: {JsonText.Stringify(line, 0)}");
                continue;
            }

            if (line[0] is '"' or '\'')
            {
                Err("pack-agent-quoted-key", $"quoted key: {JsonText.Stringify(line, 0)}");
                continue;
            }

            if (!TryKeyLine(line, out var key, out var rest))
            {
                Err("pack-agent-line", $"line {JsonText.Stringify(line, 0)} can't be classified");
                continue;
            }

            var value = JsText.Trim(rest ?? string.Empty);
            if (Array.IndexOf(PackAgentKeys, key) < 0)
            {
                Err("pack-agent-key", $"key {key} isn't allowed in a pack agent");
            }

            if (!seen.Add(key))
            {
                Err("pack-agent-duplicate-key", $"key {key} appears twice");
            }

            if (value.Length > 0 && value[0] is '&' or '*')
            {
                Err("pack-agent-yaml", $"YAML anchor or alias: {JsonText.Stringify(line, 0)}");
            }

            var toolKey = key is "tools" or "disallowedTools";
            if (BlockStyleRegex().IsMatch(value))
            {
                if (key == "description")
                {
                    cur = new Pending { Key = key, Kind = "block", Style = value };
                }
                else
                {
                    Err("pack-agent-line", $"only description may use a | or > block, not {key}");
                    cur = new Pending { Key = key, Kind = "none" };
                }
            }
            else if (value.Length == 0 && toolKey)
            {
                cur = new Pending { Key = key, Kind = "list" };
            }
            else
            {
                var why = value.Length > 0 ? YamlValueProblem(value) : null;
                if (why is null && key == "maxTurns" && !IntegerRegex().IsMatch(value))
                {
                    why = "maxTurns must be a plain decimal integer";
                }

                if (why is not null)
                {
                    Err("pack-agent-line", $"line {JsonText.Stringify(line, 0)}: {why}");
                }

                cur = new Pending { Key = key, Kind = "none", Value = value };
            }
        }

        Finish();
        if (!seen.Contains("tools"))
        {
            Err("pack-agent-tools", "tools is required: without it the agent inherits every tool, MCP tools and Agent included");
        }
        else if (fm["tools"].IsNull || fm["tools"].AsArray().Count == 0)
        {
            Err("pack-agent-tools", "tools must be a non-empty list");
        }

        return (findings, fm);
    }

    /// <summary><c>/^ */</c>: the length of the leading run of U+0020.</summary>
    private static int IndentOfSpaces(string line)
    {
        var i = 0;
        while (i < line.Length && line[i] == ' ')
        {
            i++;
        }

        return i;
    }

    /// <summary><c>/(^|\s)#/.test(line)</c>.</summary>
    private static bool HasComment(string line)
    {
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '#' && (i == 0 || JsText.IsSpace(line[i - 1])))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary><c>/&lt;&lt;\s*:/.test(s)</c>.</summary>
    private static bool HasMergeKey(string s)
    {
        for (var i = s.IndexOf("<<", StringComparison.Ordinal); i >= 0; i = s.IndexOf("<<", i + 1, StringComparison.Ordinal))
        {
            var j = i + 2;
            while (j < s.Length && JsText.IsSpace(s[j]))
            {
                j++;
            }

            if (j < s.Length && s[j] == ':')
            {
                return true;
            }
        }

        return false;
    }

    /// <summary><c>/^&lt;&lt;\s*:/.test(s)</c>.</summary>
    private static bool HasMergeKeyAtStart(string s)
    {
        var j = 2;
        while (j < s.Length && JsText.IsSpace(s[j]))
        {
            j++;
        }

        return j < s.Length && s[j] == ':';
    }

    // ---------------------------------------------------------------- the lenient reader

    private static List<string>? ParseToolList(string value, List<string>? blockItems)
    {
        if (blockItems is not null)
        {
            var items = blockItems.Select(Unquote).ToList();
            return items.All(i => ToolTokenRegex().IsMatch(i)) ? items : null;
        }

        var t = JsText.Trim(value);
        if (t.StartsWith('['))
        {
            if (!t.EndsWith(']'))
            {
                return null;
            }

            t = t[1..^1];
            if (JsText.Trim(t).Length == 0)
            {
                return [];
            }
        }
        else if (t.Length > 0 && t[0] is '"' or '\'')
        {
            t = Unquote(t);
        }

        var list = t.Split(',').Select(Unquote).Where(i => i.Length > 0).ToList();
        return list.Count > 0 && list.All(i => ToolTokenRegex().IsMatch(i)) ? list : null;
    }

    /// <summary>
    /// <c>parseAgentFrontmatter</c>: reads <c>name</c>, <c>description</c>, <c>model</c>, <c>tools</c> and
    /// <c>disallowedTools</c> without a YAML library. Never throws: a present but unparseable field is named in <c>parseErrors</c>.
    /// </summary>
    internal static JsonObject ParseAgentFrontmatter(string text)
    {
        var o = EmptyFrontmatter();
        var lines = SplitLines(text);
        if (JsText.Trim(lines[0]) != "---")
        {
            return o;
        }

        var end = -1;
        for (var k = 1; k < lines.Count; k++)
        {
            if (JsText.Trim(lines[k]) == "---")
            {
                end = k;
                break;
            }
        }

        if (end == -1)
        {
            return o;
        }

        o["frontmatter"] = true;
        var body = lines.GetRange(1, end - 1);
        var parseErrors = o["parseErrors"].AsArray();
        for (var i = 0; i < body.Count; i++)
        {
            if (!TryKeyLine(body[i], out var key, out var rawRest))
            {
                continue;
            }

            var raw = rawRest ?? string.Empty;
            var cont = new List<string>();
            var j = i + 1;
            while (j < body.Count && (IsContinuation(body[j]) || JsText.Trim(body[j]).Length == 0))
            {
                cont.Add(body[j++]);
            }

            i = j - 1;
            if (key is not ("name" or "description" or "model" or "tools" or "disallowedTools"))
            {
                continue;
            }

            var v = JsText.Trim(raw);
            if (key is "tools" or "disallowedTools")
            {
                var items = cont.Where(l => JsText.Trim(l).Length > 0).ToList();
                var isBlock = v.Length == 0 && items.Count > 0 && items.All(l => StripListMarker(l).Item);
                List<string>? list;
                if (v.Length == 0 && items.Count == 0)
                {
                    list = null;
                }
                else
                {
                    list = ParseToolList(v, isBlock ? items.Select(l => StripListMarker(l).Stripped).ToList() : null);
                }

                if (list is null)
                {
                    parseErrors.Add(key);
                }
                else
                {
                    o[key] = ConfigRegistry.StringArray(list);
                }

                continue;
            }

            if (key == "description")
            {
                if (BlockStyleRegex().IsMatch(v))
                {
                    o["description"] = BlockText(v, cont);
                }
                else
                {
                    var folded = new List<string> { Unquote(v) };
                    folded.AddRange(cont.Select(JsText.Trim));
                    var joined = string.Join(' ', folded.Where(l => l.Length > 0));
                    o["description"] = joined.Length == 0 ? JsonValue.Null : joined;
                }

                continue;
            }

            if (BlockStyleRegex().IsMatch(v))
            {
                o[key] = BlockText(v, cont);
                continue;
            }

            var unq = Unquote(v);
            if (v.Length == 0 || v[0] is '[' or '{' || unq.Any(c => JsText.IsSpace(c) || c == ','))
            {
                parseErrors.Add(key);
                continue;
            }

            o[key] = unq;
        }

        return o;
    }

    private static string BlockText(string style, List<string> cont)
    {
        var block = cont.Select(l => l[IndentOf(l)..]).ToList();
        while (block.Count > 0 && block[^1].Length == 0)
        {
            block.RemoveAt(block.Count - 1);
        }

        return style[0] == '|' ? string.Join('\n', block) : string.Join(' ', block.Where(l => l.Length > 0));
    }

    // ---------------------------------------------------------------- descriptions and model names

    /// <summary>A frontmatter description as one directive-safe line (at most 160 characters), or null when nothing is left.</summary>
    internal static string? NormalizeDescription(JsonValue? value)
    {
        if (value is not { IsText: true } v)
        {
            return null;
        }

        var s = StripExamples(v.AsString()).Replace("\\n", " ", StringComparison.Ordinal).Replace("`", string.Empty, StringComparison.Ordinal);
        var sb = new StringBuilder(s.Length);
        var inSpace = false;
        foreach (var c in s)
        {
            if (JsText.IsSpace(c))
            {
                inSpace = true;
                continue;
            }

            if (inSpace)
            {
                sb.Append(' ');
                inSpace = false;
            }

            sb.Append(c);
        }

        if (inSpace)
        {
            sb.Append(' ');
        }

        var t = JsText.Trim(sb.ToString());
        if (t.Length == 0)
        {
            return null;
        }

        for (var p = 0; p < t.Length; p++)
        {
            if (t[p] is '.' or '!' or '?' && (p + 1 == t.Length || JsText.IsSpace(t[p + 1])))
            {
                if (p + 1 <= ConfigRegistry.OneLineMax)
                {
                    return t[..(p + 1)];
                }

                break;
            }
        }

        return t.Length <= ConfigRegistry.OneLineMax ? t : t[..(ConfigRegistry.OneLineMax - 3)] + "…";
    }

    /// <summary><c>s.replace(/&lt;example&gt;[\s\S]*?&lt;\/example&gt;/gi, " ")</c> with ASCII case folding.</summary>
    private static string StripExamples(string s)
    {
        const string open = "<example>";
        const string close = "</example>";
        var sb = new StringBuilder(s.Length);
        var i = 0;
        while (i < s.Length)
        {
            var start = IndexOfAsciiIgnoreCase(s, open, i);
            if (start < 0)
            {
                break;
            }

            var stop = IndexOfAsciiIgnoreCase(s, close, start + open.Length);
            if (stop < 0)
            {
                break;
            }

            sb.Append(s, i, start - i).Append(' ');
            i = stop + close.Length;
        }

        sb.Append(s, i, s.Length - i);
        return sb.ToString();
    }

    private static int IndexOfAsciiIgnoreCase(string s, string needle, int from)
    {
        for (var i = from; i + needle.Length <= s.Length; i++)
        {
            var ok = true;
            for (var k = 0; k < needle.Length && ok; k++)
            {
                var a = s[i + k];
                var b = needle[k];
                ok = a == b || (a is >= 'A' and <= 'Z' && a + 32 == b);
            }

            if (ok)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>A model value's family alias (<c>claude-opus-5-5</c> → <c>opus</c>); <c>inherit</c> and unknown names map to themselves.</summary>
    internal static string? ModelAlias(string? model)
    {
        if (model is null)
        {
            return null;
        }

        if (model == "inherit")
        {
            return model;
        }

        var tier = ConfigRegistry.TierOf(model);
        return tier is { } t ? ConfigRegistry.TierNames[t - 1] : model;
    }

    /// <summary>The first plain tool entry's scope check: <c>Name(</c>.</summary>
    internal static string? ScopedToolName(string entry)
    {
        var m = ScopedToolRegex().Match(entry);
        return m.Success ? m.Groups[1].Value : null;
    }
}
