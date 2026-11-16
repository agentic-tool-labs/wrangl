using Ah.Core.Json;

namespace Ah.Commands.Tests;

/// <summary>
/// The rendered HTML of an issue and its title as the snapshot shows them. Each row is what the Node script printed for the same
/// input: the verdict, and for an eligible issue the body lines behind the gutter or the TITLE line.
/// </summary>
public sealed class IssueIntakeVisibleTextTests
{
    private static JsonObject Item(string stdout) => JsonText.Parse(stdout).AsObject()["items"].AsArray()[0].AsObject();

    private static string BodyOf(string snapshot)
    {
        var at = snapshot.IndexOf("\nBODY:\n", StringComparison.Ordinal) + "\nBODY:\n".Length;
        return snapshot[at..];
    }

    [Theory]
    [InlineData("<p>&#x10FFFF;|&#x110000;|&#xE000;|&#xDFFF;|&#xD7FF;|&#1;</p>", "eligible", "| \uDBFF\uDFFF|\uFFFD|\uE000|\uFFFD|\uD7FF|\u0001\n")]
    [InlineData("<p>\uD83D\uDC69\u200Dx</p>", "hidden-content", "")]
    [InlineData("<p>\u00A9\u200D\u00AE</p>", "eligible", "| \u00A9\u200D\u00AE\n")]
    [InlineData("<p>x\u200D\uD83D\uDC69</p>", "hidden-content", "")]
    [InlineData("<p>Hello</p>", "eligible", "| Hello\n")]
    [InlineData("<p>a</p><p>b</p>", "eligible", "| a\n|\n| b\n")]
    [InlineData("<h2>Goal</h2><p>x</p>", "eligible", "| ## Goal\n|\n| x\n")]
    [InlineData("<ul><li>one</li><li>two</li></ul>", "eligible", "| - one\n| - two\n")]
    [InlineData("<ol><li>one</li><li>two</li></ol>", "eligible", "| 1. one\n| 2. two\n")]
    [InlineData("<ul><li>a<ul><li>b</li></ul></li></ul>", "eligible", "| - a\n|\n| - b\n")]
    [InlineData("<table><tr><th>h1</th><th>h2</th></tr><tr><td>1</td><td>2</td></tr></table>", "eligible", "| h1 | h2\n| 1 | 2\n")]
    [InlineData("<p><input type=\"checkbox\" checked> done <input type=checkbox> todo</p>", "eligible", "| [x] done [ ] todo\n")]
    [InlineData("<p>x<br>y<br/>z</p>", "eligible", "| x\n| y\n| z\n")]
    [InlineData("<pre>  keep\n  spaces\n</pre>", "eligible", "|   keep\n|   spaces\n")]
    [InlineData("<p>a   b\n\tc</p>", "eligible", "| a b c\n")]
    [InlineData("<p>a &amp; b &lt; c &gt; d &quot;e&quot; &apos;f&apos;&nbsp;g</p>", "eligible", "| a & b < c > d \"e\" 'f' g\n")]
    [InlineData("<p>&#65;&#x42;&#X43;&#0;&#xD800;&#1114112;&bogus;&amp</p>", "eligible", "| ABC\uFFFD\uFFFD\uFFFD&bogus;&amp\n")]
    [InlineData("<!-- hidden --><p>shown</p>", "eligible", "| shown\n")]
    [InlineData("<p>a</p><!-- unterminated", "eligible", "| a\n")]
    [InlineData("<p>a <script>evil()</script>b</p>", "eligible", "| a b\n")]
    [InlineData("<p>a <span hidden>secret</span> b</p>", "eligible", "| a b\n")]
    [InlineData("<div HIDDEN>x</div><p>y</p>", "eligible", "| y\n")]
    [InlineData("<template><p>t</p></template><noscript>n</noscript>ok", "eligible", "| ok\n")]
    [InlineData("<p title='a>b'>quoted</p>", "eligible", "| quoted\n")]
    [InlineData("<p>x<img src=a alt=SECRET title=T>y</p>", "eligible", "| x[image]y\n")]
    [InlineData("<p>a</p><hr><p>b</p>", "eligible", "| a\n|\n| b\n")]
    [InlineData("<blockquote><p>q</p></blockquote>", "eligible", "| q\n")]
    [InlineData("<div>a</div><div>b</div>", "eligible", "| a\n| b\n")]
    [InlineData("<dl><dt>t</dt><dd>d</dd></dl>", "eligible", "| t\n| d\n")]
    [InlineData("<p>unterminated <a href=\"x", "eligible", "| unterminated\n")]
    [InlineData("<p>a</p>\n\n\n\n<p>b</p>", "eligible", "| a\n|\n| b\n")]
    [InlineData("text only", "eligible", "| text only\n")]
    [InlineData("<p>\uD83D\uDC69\u200D\uD83D\uDCBB ok</p>", "eligible", "| \uD83D\uDC69\u200D\uD83D\uDCBB ok\n")]
    [InlineData("<p>a\u200Db</p>", "hidden-content", "")]
    [InlineData("<details><summary>s</summary>x</details>", "hidden-content", "")]
    [InlineData("<p>bidi \u202E x</p>", "hidden-content", "")]
    [InlineData("<P CLASS=x>caps</P>", "eligible", "| caps\n")]
    [InlineData("<h1>a</h1><h6>f</h6>", "eligible", "| # a\n|\n| ###### f\n")]
    [InlineData("<li>orphan</li>", "eligible", "| orphan\n")]
    [InlineData("<tr><td>a</td><td>b</td></tr>", "eligible", "| a | b\n")]
    [InlineData("<td>lone</td>", "eligible", "| | lone\n")]
    [InlineData("<pre><code>x\ny</code></pre><p>z</p>", "eligible", "| x\n| y\n|\n| z\n")]
    [InlineData("<p>line1\r\nline2\rline3\u000Bline4</p>", "eligible", "| line1 line2 line3\n| line4\n")]
    [InlineData("<pre>a\r\nb\rc\u000Bd\u000Ce\u0085f</pre>", "eligible", "| a\n| b\n| c\n| d\n| e\n| f\n")]
    [InlineData("<p>one</p>  \n  <p>two</p>", "eligible", "| one\n|\n| two\n")]
    [InlineData("<a href=x>link</a> text", "eligible", "| link text\n")]
    [InlineData("<ul><li>a</li></ul>trailing", "eligible", "| - a\n|\n| trailing\n")]
    [InlineData("<h2>t</h2>\n<ul>\n<li>x</li>\n</ul>", "eligible", "| ## t\n|\n| - x\n")]
    [InlineData("<p>\uD83D\uDC4D\uFE0F\u200D\uD83E\uDDBA</p>", "eligible", "| \uD83D\uDC4D\uFE0F\u200D\uD83E\uDDBA\n")]
    [InlineData("<p>\uD83D\uDC68\uD83C\uDFFB\u200D\uD83E\uDDB0</p>", "eligible", "| \uD83D\uDC68\uD83C\uDFFB\u200D\uD83E\uDDB0\n")]
    [InlineData("<p>\u2764\uFE0F\u200D</p>", "hidden-content", "")]
    [InlineData("<p>\u200D\uD83D\uDE00</p>", "hidden-content", "")]
    [InlineData("<p>\uDB40\uDC41</p>", "hidden-content", "")]
    [InlineData("<p>x\uFEFFy</p>", "hidden-content", "")]
    [InlineData("<p>\u061C</p>", "hidden-content", "")]
    [InlineData("<p>a\u2028b</p>", "hidden-content", "")]
    [InlineData("<p>a\u00A0b</p>", "eligible", "| a\u00A0b\n")]
    [InlineData("<p><div>nested</div></p>", "eligible", "| nested\n")]
    [InlineData("<span hidden>a<span>b</span>c</span>d", "eligible", "| d\n")]
    [InlineData("<p hidden>a</p><p>b</p><p>c</p>", "eligible", "| b\n|\n| c\n")]
    [InlineData("<input type=CHECKBOX CHECKED>", "eligible", "| [x]\n")]
    [InlineData("<input>", "eligible", "")]
    [InlineData("<img>", "eligible", "| [image]\n")]
    [InlineData("<pre>\n\nx</pre>", "eligible", "| x\n")]
    [InlineData("</p>stray", "eligible", "| stray\n")]
    [InlineData("<>x", "eligible", "| x\n")]
    [InlineData("< p>y", "eligible", "| y\n")]
    [InlineData("a < b", "eligible", "| a\n")]
    [InlineData("a > b", "eligible", "| a > b\n")]
    public void TheVisibleTextOfARenderedBodyIsWhatTheReaderSees(string html, string verdict, string body)
    {
        using var rig = new IntakeRig();
        rig.Issues[1] = IntakeIssue.Eligible(html);

        var item = Item(rig.Run("--issues", "1").Stdout);

        if (verdict == "eligible")
        {
            Assert.True(item["eligible"].AsBool());
            Assert.Equal(body, BodyOf(File.ReadAllText(rig.SnapshotPath(1))));
        }
        else
        {
            Assert.False(item["eligible"].AsBool());
            Assert.Equal(verdict, item["reason"].AsString());
            Assert.False(File.Exists(rig.SnapshotPath(1)));
        }
    }

    [Theory]
    [InlineData("Plain", "eligible", "Plain")]
    [InlineData("A\r\nB", "eligible", "A B")]
    [InlineData("A\u0085B", "eligible", "A B")]
    [InlineData("A\u000BB\u000CC", "eligible", "A B C")]
    [InlineData("A\rB", "eligible", "A B")]
    [InlineData("A\nB", "eligible", "A B")]
    [InlineData("<b>x</b> &amp; y", "eligible", "<b>x</b> &amp; y")]
    [InlineData("emoji \uD83D\uDC69\u200D\uD83D\uDCBB", "eligible", "emoji \uD83D\uDC69\u200D\uD83D\uDCBB")]
    [InlineData("trail  ", "eligible", "trail  ")]
    [InlineData("  lead", "eligible", "  lead")]
    [InlineData("zwj a\u200Db", "hidden-content", "zwj a\u200Db")]
    [InlineData("bidi \u202Ex", "hidden-content", "bidi \u202Ex")]
    public void ATitleIsPlainTextOnOneLine(string title, string verdict, string shown)
    {
        using var rig = new IntakeRig();
        rig.Issues[1] = IntakeIssue.Eligible("<p>b</p>", title);

        var item = Item(rig.Run("--issues", "1").Stdout);

        if (verdict == "eligible")
        {
            var line = File.ReadAllText(rig.SnapshotPath(1)).Split('\n').Single(l => l.StartsWith("TITLE: ", StringComparison.Ordinal));
            Assert.Equal("TITLE: " + shown, line);
        }
        else
        {
            Assert.Equal(verdict, item["reason"].AsString());
        }
    }
}
