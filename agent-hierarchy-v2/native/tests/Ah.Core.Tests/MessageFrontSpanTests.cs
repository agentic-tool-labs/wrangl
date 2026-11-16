using System.Text.RegularExpressions;
using Ah.Core.Json;
using Ah.Core.Text;

namespace Ah.Core.Tests;

/// <summary>
/// The front matter read over characters (ah2-1: a line is a slice, a usual field name is one shared string, the object is made at its
/// size) gives what the documented rule gives, here written the slow way: split every line, match each with the rule's own patterns.
/// </summary>
public sealed class MessageFrontSpanTests
{
    private const string Ws = JsText.SpaceClass;
    private static readonly Regex Field = new("^([A-Za-z_][A-Za-z0-9_-]*):[" + Ws + "]*([^\n\r\u2028\u2029]*)$", RegexOptions.CultureInvariant);
    private static readonly Regex Comment = new("[" + Ws + "]+#.*$", RegexOptions.CultureInvariant | RegexOptions.Singleline);

    private static (string Fields, int End)? Reference(string text)
    {
        var lines = text.Split('\n');
        if (JsText.Trim(lines[0]) != "---")
        {
            return null;
        }

        var fields = new JsonObject();
        for (var i = 1; i < lines.Length; i++)
        {
            if (JsText.Trim(lines[i]) == "---")
            {
                return (JsonText.Stringify(JsonValue.FromObject(fields), 0), i + 1);
            }

            if (Field.Match(lines[i]) is not { Success: true } m)
            {
                continue;
            }

            var value = JsText.Trim(Comment.Replace(m.Groups[2].Value, string.Empty));
            if (m.Groups[1].Value != "__proto__")
            {
                fields[m.Groups[1].Value] = value is "null" or "" ? JsonValue.Null : value;
            }
        }

        return null;
    }

    public static TheoryData<string> Texts => new()
    {
        "---\nid: 20261114-0001\ntype: request\nto: implementor\nfrom: orchestrator\nslug: fix-it\ntitle: Fix the thing\nparent: null\nreason:\neta: medium\nto_name: ah2-implementor-3\nteam: ah2\ncreated: 2026-11-14T10:00:00.000Z\nitem: _run/fix-it\n---\n## [0] tldr\n- body\n",
        "---\n---\n",
        "---\n---",
        "---",
        "---\nto: a\n",                                  // no closing fence
        "to: a\n---\n",                                 // no opening fence
        "",
        " --- \t\nto: a   # a comment\nslug: x#y\nnote: a #b # c\n\t---  \nto: not a field\n",
        "---\nto: a\nto: b\ncustom_field-1: v\n_x: 1\n9x: no\nno colon\n: empty name\nkey:\nkey2:   \nkey3: null\n__proto__: p\n---\nbody",
        "---\na: one\u2028two\nb: tab\tinside\nc:\u00a0nbsp value\u00a0\nd: é😀\ne: ---\n---\nx: y",
        "---\na: 1\n\n\n---\n\n---\nb: 2\n",
        "---\r\nto: a\r\n---\r\nbody\r\n",                  // a stray carriage return stays in the fence line's trim and in no value
    };

    [Theory]
    [MemberData(nameof(Texts))]
    public void TheFieldsAndTheEndAreWhatTheLineByLineRuleGivesForAStringAndForItsCharacters(string text)
    {
        var expected = Reference(text);
        foreach (var parsed in new[] { MessageFront.ParseFrontmatter(text), MessageFront.ParseFrontmatter(text.AsSpan()) })
        {
            Assert.Equal(expected is null, parsed is null);
            if (expected is { } e)
            {
                Assert.Equal(e.Fields, JsonText.Stringify(JsonValue.FromObject(parsed!.Value.Fields), 0));
                Assert.Equal(e.End, parsed.Value.End);
                Assert.Equal(string.Join('\n', text.Split('\n').Skip(e.End)), text.AsSpan()[MessageFront.OffsetAfterLines(text.AsSpan(), e.End)..].ToString());
            }
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("plain")]
    [InlineData("a\r\nb\r\n")]
    [InlineData("\r\n")]
    [InlineData("\r\r\n\n\r")]
    [InlineData("a\rb\nc\r\n\r\nd\r")]
    public void CollapsingInPlaceIsWhatReplaceGives(string text)
    {
        var buffer = text.ToCharArray();
        var length = MessageFront.CollapseCrLf(buffer);
        Assert.Equal(text.Replace("\r\n", "\n", StringComparison.Ordinal), new string(buffer, 0, length));
    }
}
