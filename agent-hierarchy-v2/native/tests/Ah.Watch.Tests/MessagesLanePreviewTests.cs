using System.Text;
using System.Text.RegularExpressions;
using Ah.Core.Json;
using Ah.Watch.Model;

namespace Ah.Watch.Tests;

/// <summary>
/// An exchange's preview (ah2-1) is made from the head of the excerpt alone, and is what flattening the whole excerpt, trimming it and
/// cutting it gave: <c>Regex.Replace(text, "[\f\n\r\t\v\u0085\p{Z}]+", " ").Trim()</c>, then the first 160 characters, never half a pair.
/// </summary>
public sealed class MessagesLanePreviewTests
{
    private static readonly Regex Whitespace = new(@"[\f\n\r\t\v\u0085\p{Z}]+");

    private static string Reference(string text)
    {
        var flat = Whitespace.Replace(text, " ").Trim();
        if (flat.Length <= MessagesLane.PreviewChars)
        {
            return flat;
        }

        return flat[..(char.IsHighSurrogate(flat[MessagesLane.PreviewChars - 1]) ? MessagesLane.PreviewChars - 1 : MessagesLane.PreviewChars)];
    }

    private static string Preview(string text) => MessagesLane.PreviewOf(JsonValue.FromString(text)).AsString();

    [Theory]
    [InlineData("")]
    [InlineData("   \n\t ")]
    [InlineData("one  two\n\nthree\tfour")]
    [InlineData("\u001c lead and tail kept by the class but dropped by Trim \u001f")]
    [InlineData("a\u00a0b\u2028c\u2029d\u0085e\u3000f")]
    public void AShortExcerptIsFlattenedAndTrimmed(string text) => Assert.Equal(Reference(text), Preview(text));

    [Fact]
    public void TheCutAndEveryLengthAroundItMatchIncludingAPairOnTheCutAndWhiteSpaceAcrossIt()
    {
        var n = MessagesLane.PreviewChars;
        var cases = new List<string>();
        for (var length = n - 3; length <= n + 3; length++)
        {
            cases.Add(new string('x', length));
            cases.Add(new string('x', length) + "   ");
            cases.Add("  " + new string('x', length - 1) + " \n ");
            cases.Add(new string('x', length - 1) + "😀");               // the pair straddles or meets the cut
            cases.Add(new string('x', length - 2) + "😀y");
            cases.Add(new string('x', length - 1) + " \n\t " + "y");       // one flattened space at the cut, text after it
            cases.Add(new string('x', length - 1) + " \n\t ");            // white space to the end: trimmed, not cut
            cases.Add(new string('x', length) + "\u001c\u001cz");          // white space the class does not flatten, past the cut
        }

        foreach (var text in cases)
        {
            Assert.Equal(Reference(text), Preview(text));
        }
    }

    [Fact]
    public void RandomExcerptsMatch()
    {
        // a fixed seed: letters, the flattened class, white space outside it, and pairs, at lengths on both sides of the cut
        var random = new Random(20261114);
        var alphabet = new[] { "a", "b", "Z", " ", " ", "\n", "\t", "\r\n", "\u00a0", "\u2028", "\u0085", "\u001c", "\u001f", "😀", "é", "\ud83d", "." };
        for (var i = 0; i < 3000; i++)
        {
            var sb = new StringBuilder();
            var parts = random.Next(0, 420);
            for (var p = 0; p < parts; p++)
            {
                sb.Append(alphabet[random.Next(alphabet.Length)]);
            }

            var text = sb.ToString();
            Assert.Equal(Reference(text), Preview(text));
        }
    }

    [Fact]
    public void NegativeNoExcerptIsNoPreview() => Assert.True(MessagesLane.PreviewOf(JsonValue.Null).IsNull);
}
