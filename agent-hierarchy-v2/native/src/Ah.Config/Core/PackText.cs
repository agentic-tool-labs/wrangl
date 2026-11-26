using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Ah.Core.Text;

namespace Ah.Config;

/// <summary>Text rules of role packs: hidden characters, terminal escaping and the agent-file <c>name</c> reader.</summary>
internal static partial class PackText
{
    private const string Ws = JsText.SpaceClass;
    private const string NotLineEnd = "[^\\n\\r\\u2028\\u2029]";

    [GeneratedRegex("^([A-Za-z0-9][A-Za-z0-9_.-]*)(?:@([^:@" + Ws + "]+))?:([^:@" + Ws + "]+)\\z", RegexOptions.CultureInvariant)]
    private static partial Regex FromRegex();

    [GeneratedRegex("^[" + Ws + "]*#", RegexOptions.CultureInvariant)]
    private static partial Regex CommentLine();

    [GeneratedRegex("^[" + Ws + "]*(?:-[" + Ws + "]+)*", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingItems();

    [GeneratedRegex("^[&*!]", RegexOptions.CultureInvariant)]
    private static partial Regex AnchorStart();

    [GeneratedRegex("^(?:\"(?:[^\"\\\\]|\\\\" + NotLineEnd + ")*\"|'(?:[^']|'')*'|[^" + Ws + "\"'#][^:]*?)[" + Ws + "]*:[" + Ws + "]+[&*!]", RegexOptions.CultureInvariant)]
    private static partial Regex AnchorValue();

    [GeneratedRegex("^<<[" + Ws + "]*:", RegexOptions.CultureInvariant)]
    private static partial Regex MergeKey();

    [GeneratedRegex("^[" + Ws + "]", RegexOptions.CultureInvariant)]
    private static partial Regex StartsWithSpace();

    [GeneratedRegex("^-(?:[" + Ws + "]|\\z)", RegexOptions.CultureInvariant)]
    private static partial Regex ListItem();

    [GeneratedRegex("^(?:\\?|[{\\[])", RegexOptions.CultureInvariant)]
    private static partial Regex ComplexKey();

    [GeneratedRegex("^\"((?:[^\"\\\\]|\\\\" + NotLineEnd + ")*)\"[" + Ws + "]*:(?:[" + Ws + "]+(" + NotLineEnd + "*))?\\z", RegexOptions.CultureInvariant)]
    private static partial Regex DoubleQuotedKey();

    [GeneratedRegex("^'((?:[^']|'')*)'[" + Ws + "]*:(?:[" + Ws + "]+(" + NotLineEnd + "*))?\\z", RegexOptions.CultureInvariant)]
    private static partial Regex SingleQuotedKey();

    [GeneratedRegex("^(" + NotLineEnd + "*?)[" + Ws + "]*:(?:[" + Ws + "]+(" + NotLineEnd + "*))?\\z", RegexOptions.CultureInvariant)]
    private static partial Regex PlainKey();

    [GeneratedRegex("^'((?:[^']|'')*)'(?:[" + Ws + "]+#" + NotLineEnd + "*)?\\z", RegexOptions.CultureInvariant)]
    private static partial Regex SingleQuotedValue();

    [GeneratedRegex("^[|>\\[{]", RegexOptions.CultureInvariant)]
    private static partial Regex BlockOrFlow();

    [GeneratedRegex("^(?:~|null|Null|NULL)\\z", RegexOptions.CultureInvariant)]
    private static partial Regex NullWord();

    [GeneratedRegex("[" + Ws + "]+#" + NotLineEnd + "*\\z", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingComment();

    /// <summary>The parts of <c>&lt;plugin&gt;[@&lt;marketplace&gt;]:&lt;role&gt;</c>, or null.</summary>
    internal static (string Plugin, string? Marketplace, string Role)? ParseFrom(string? from)
    {
        if (from is null)
        {
            return null;
        }

        var m = FromRegex().Match(from);
        return m.Success ? (m.Groups[1].Value, m.Groups[2].Success ? m.Groups[2].Value : null, m.Groups[3].Value) : null;
    }

    /// <summary>A roster <c>packs</c> entry <c>&lt;plugin&gt;@&lt;marketplace&gt;</c> as <c>"plugin@marketplace"</c>, the parts <see cref="ParseFrom"/> accepts, or null.</summary>
    internal static string? ParsePack(string? pack) =>
        pack is not null && ParseFrom(pack + ":r") is { Marketplace: { } marketplace } f ? $"{f.Plugin}@{marketplace}" : null;

    private static bool IsHidden(int cp) =>
        cp is (>= 0x00 and <= 0x08) or (>= 0x0B and <= 0x1F) or (>= 0x7F and <= 0x9F)
            or (>= 0xE0000 and <= 0xE007F) or (>= 0xFE00 and <= 0xFE0F) or (>= 0xE0100 and <= 0xE01EF)
            or 0x2028 or 0x2029 or 0x034F or 0x115F or 0x1160 or 0x3164 or 0xFFA0 or 0x2800
        || (cp is >= 0 and <= 0x10FFFF && !(cp is >= 0xD800 and <= 0xDFFF) && CharUnicodeInfo.GetUnicodeCategory(cp) == UnicodeCategory.Format);

    private static IEnumerable<(int Index, int CodePoint, int Length)> CodePointsWithIndex(string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                yield return (i, char.ConvertToUtf32(s[i], s[i + 1]), 2);
                i++;
            }
            else
            {
                yield return (i, s[i], 1);
            }
        }
    }

    /// <summary><c>hiddenCharAt</c>: the first hidden character as <c>U+XXXX at line L, column C</c> (columns in code points), or null.</summary>
    internal static string? HiddenCharAt(string text)
    {
        foreach (var (index, cp, _) in CodePointsWithIndex(text))
        {
            if (!IsHidden(cp))
            {
                continue;
            }

            var before = text[..index];
            var lineStart = before.LastIndexOf('\n') + 1;
            var line = text[lineStart..];
            var lineIndex = index - lineStart;
            var column = CodePointsWithIndex(line[..lineIndex]).Count() + 1;
            var lineNo = before.Split('\n').Length;
            return $"U+{cp.ToString("X", CultureInfo.InvariantCulture).PadLeft(4, '0')} at line {lineNo}, column {column}";
        }

        return null;
    }

    /// <summary><c>escapeTerminal</c>: every hidden character shown as <c>\u{...}</c>.</summary>
    internal static string EscapeTerminal(string text)
    {
        var sb = new StringBuilder();
        foreach (var (index, cp, length) in CodePointsWithIndex(text))
        {
            if (IsHidden(cp))
            {
                sb.Append("\\u{").Append(cp.ToString("x", CultureInfo.InvariantCulture)).Append('}');
            }
            else
            {
                sb.Append(text, index, length);
            }
        }

        return sb.ToString();
    }

    /// <summary>What an agent file's frontmatter says its <c>name</c> is: none, a name, or unsure.</summary>
    internal readonly record struct Declared(bool None, bool Unsure, string? Name)
    {
        internal static Declared NoneDeclared => new(true, false, null);

        internal static Declared UnsureDeclared => new(false, true, null);
    }

    /// <summary><c>declaredAgentName</c>.</summary>
    internal static Declared DeclaredAgentName(string text)
    {
        var lines = text.Split("\r\n").SelectMany(chunk => chunk.Split('\n')).ToArray();
        var first = lines[0].StartsWith('﻿') ? lines[0][1..] : lines[0];
        if (JsText.Trim(first) != "---")
        {
            return Declared.NoneDeclared;
        }

        var end = -1;
        for (var i = 1; i < lines.Length; i++)
        {
            if (JsText.Trim(lines[i]) == "---")
            {
                end = i;
                break;
            }
        }

        if (end == -1)
        {
            return Declared.NoneDeclared;
        }

        string? value = null;
        var hasValue = false;
        var count = 0;
        for (var i = 1; i < end; i++)
        {
            var line = lines[i];
            if (JsText.Trim(line).Length == 0 || CommentLine().IsMatch(line))
            {
                continue;
            }

            var node = LeadingItems().Replace(line, string.Empty, 1);
            if (AnchorStart().IsMatch(node) || AnchorValue().IsMatch(node) || MergeKey().IsMatch(node))
            {
                return Declared.UnsureDeclared;
            }

            if (StartsWithSpace().IsMatch(line) || ListItem().IsMatch(line))
            {
                continue;
            }

            if (ComplexKey().IsMatch(line))
            {
                return Declared.UnsureDeclared;
            }

            string key;
            string? rest;
            Match m;
            if ((m = DoubleQuotedKey().Match(line)).Success)
            {
                if (m.Groups[1].Value.Contains('\\', StringComparison.Ordinal))
                {
                    return Declared.UnsureDeclared;
                }

                key = m.Groups[1].Value;
                rest = m.Groups[2].Success ? m.Groups[2].Value : null;
            }
            else if ((m = SingleQuotedKey().Match(line)).Success)
            {
                if (m.Groups[1].Value.Contains("''", StringComparison.Ordinal))
                {
                    return Declared.UnsureDeclared;
                }

                key = m.Groups[1].Value;
                rest = m.Groups[2].Success ? m.Groups[2].Value : null;
            }
            else if ((m = PlainKey().Match(line)).Success)
            {
                key = m.Groups[1].Value;
                rest = m.Groups[2].Success ? m.Groups[2].Value : null;
            }
            else
            {
                continue;
            }

            if (key != "name")
            {
                continue;
            }

            count++;
            string? next = null;
            for (var j = i + 1; j < end; j++)
            {
                if (JsText.Trim(lines[j]).Length > 0 && !CommentLine().IsMatch(lines[j]))
                {
                    next = lines[j];
                    break;
                }
            }

            if (next is not null && StartsWithSpace().IsMatch(next))
            {
                return Declared.UnsureDeclared;
            }

            var v = JsText.Trim(rest ?? string.Empty);
            if (v.Length > 0 && v[0] == '"')
            {
                try
                {
                    var parsed = Ah.Core.Json.JsonText.Parse(TrailingComment().Replace(v, string.Empty, 1));
                    value = parsed.IsNull ? null : Ah.Core.Json.JsConvert.ToJsString(parsed);
                    hasValue = !parsed.IsNull;
                }
                catch (Ah.Core.Json.JsonParseException)
                {
                    return Declared.UnsureDeclared;
                }
            }
            else if (v.Length > 0 && v[0] == '\'')
            {
                var q = SingleQuotedValue().Match(v);
                if (!q.Success)
                {
                    return Declared.UnsureDeclared;
                }

                value = q.Groups[1].Value.Replace("''", "'", StringComparison.Ordinal);
                hasValue = true;
            }
            else if (BlockOrFlow().IsMatch(v))
            {
                return Declared.UnsureDeclared;
            }
            else
            {
                var plain = TrailingComment().Replace(v, string.Empty, 1);
                if (plain.Length == 0 || NullWord().IsMatch(plain))
                {
                    value = null;
                    hasValue = false;
                }
                else
                {
                    value = plain;
                    hasValue = true;
                }
            }
        }

        if (count > 1)
        {
            return Declared.UnsureDeclared;
        }

        return !hasValue ? Declared.NoneDeclared : new Declared(false, false, value);
    }
}
