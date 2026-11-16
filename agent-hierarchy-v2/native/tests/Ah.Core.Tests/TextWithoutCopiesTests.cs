using System.Text;
using Ah.Core.Json;
using Ah.Core.Text;

namespace Ah.Core.Tests;

/// <summary>The copy-saving text paths of ah2-1 give what the paths they replace gave.</summary>
public sealed class TextWithoutCopiesTests
{
    [Theory]
    [InlineData("")]
    [InlineData("---\nto: a\n---\nbody\n")]
    [InlineData("---\nto: a\n---")]
    [InlineData("one\ntwo\n\nfour")]
    [InlineData("\n\n")]
    public void TheOffsetAfterLinesIsWhereSplitSkipJoinStarts(string text)
    {
        for (var lines = 0; lines < 6; lines++)
        {
            Assert.Equal(string.Join('\n', text.Split('\n').Skip(lines)), text[MessageFront.OffsetAfterLines(text, lines)..]);
        }
    }

    [Fact]
    public void FrontMatterEndsAtItsClosingFenceAndNegativeAnUnclosedOneIsNone()
    {
        var parsed = MessageFront.ParseFrontmatter("---\nto: architect\nslug: x  # note\n---\nbody: not a field\n");
        Assert.Equal(4, parsed!.Value.End);
        Assert.Equal("architect", parsed.Value.Fields.Get("to")!.Value.AsString());
        Assert.Null(parsed.Value.Fields.Get("body"));
        Assert.Null(MessageFront.ParseFrontmatter("---\nto: architect\n"));
        Assert.Null(MessageFront.ParseFrontmatter("to: architect\n---\n"));
    }

    [Fact]
    public void ABuildersBytesAreItsStringsBytesInOneRunOrMany()
    {
        // one run, many runs (appends past a chunk), and a lone surrogate, which both paths replace alike
        foreach (var text in new[] { "plain", "é😀", "x\ud83dy", new string('a', 40_000) })
        {
            var one = new StringBuilder(text.Length + 8).Append(text);
            Assert.Equal(Utf8Text.Encode(text), Utf8Text.Encode(one));
            var many = new StringBuilder(4);
            foreach (var c in text)
            {
                _ = many.Append(c);
            }

            Assert.Equal(Utf8Text.Encode(text), Utf8Text.Encode(many));
        }
    }

    [Fact]
    public void ToUtf8GivesTheSameBytesOnAReusedBuilder()
    {
        var big = JsonText.Parse("{\"a\":\"" + new string('x', 20_000) + "\",\"b\":[1,2,3]}");
        var small = JsonText.Parse("{\"k\":\"v\"}");
        foreach (var value in new[] { big, small, big, small })
        {
            Assert.Equal(Utf8Text.Encode(JsonText.Stringify(value, 0)), JsonText.ToUtf8(value, 0));
            Assert.Equal(Utf8Text.Encode(JsonText.Stringify(value, 2)), JsonText.ToUtf8(value, 2));
        }
    }
}
