using System.Text;
using Ah.Watch.Model;

namespace Ah.Watch.Tests;

public sealed class TranscriptParserTests
{
    private static TranscriptEntry Parse(string json) => new TranscriptParser(new TestUsage()).Parse(Encoding.UTF8.GetBytes(json), 0, json.Length + 1);

    private static string Message(string type, string content) => "{\"type\":\"" + type + "\",\"message\":{\"content\":" + content + "}}";

    [Fact]
    public void AUserEntryWithTextStartsATurn()
    {
        Assert.True(Parse(Message("user", "\"hello\"")).TurnStart);
        Assert.True(Parse(Message("user", "[{\"type\":\"text\",\"text\":\"hi\"},{\"type\":\"tool_result\",\"tool_use_id\":\"t\",\"content\":\"x\"}]")).TurnStart);
    }

    [Fact]
    public void AUserEntryOfOnlyToolResultsAnAssistantEntryAndAnEmptyUserEntryStartNoTurn()
    {
        Assert.False(Parse(Message("user", "[{\"type\":\"tool_result\",\"tool_use_id\":\"t\",\"content\":\"x\"}]")).TurnStart);
        Assert.False(Parse(Message("assistant", "[{\"type\":\"text\",\"text\":\"hi\"}]")).TurnStart);
        Assert.False(Parse("{\"type\":\"user\"}").TurnStart);
    }

    [Theory]
    [InlineData("\"a\\nb\\nc\"", 2)]
    [InlineData("\"one line\"", 0)]
    [InlineData("[{\"type\":\"text\",\"text\":\"a\\nb\"},{\"type\":\"text\",\"text\":\"c\\n\"}]", 2)]
    [InlineData("[]", 0)]
    public void AToolResultCountsTheNewlinesOfItsContent(string content, int lines)
    {
        var block = Assert.Single(Parse(Message("user", "[{\"type\":\"tool_result\",\"tool_use_id\":\"t\",\"content\":" + content + "}]")).Blocks);

        Assert.Equal(lines, block.Lines);
    }

    private static (int Add, int Del)? DiffStatOf(string tool, string input) =>
        Assert.Single(Parse(Message("assistant", "[{\"type\":\"tool_use\",\"id\":\"u\",\"name\":\"" + tool + "\",\"input\":" + input + "}]")).Blocks).DiffStat;

    [Fact]
    public void AnEditCountsTheLinesOfTheNewAndTheOldText()
    {
        Assert.Equal((1, 2), DiffStatOf("Edit", "{\"old_string\":\"a\\nb\",\"new_string\":\"c\"}"));
        Assert.Equal((0, 0), DiffStatOf("Edit", "{\"old_string\":\"\",\"new_string\":\"\"}"));
    }

    [Fact]
    public void AMultiEditSumsItsEditsAndAWriteCountsItsContentWithNothingDeleted()
    {
        Assert.Equal((3, 2), DiffStatOf("MultiEdit", "{\"edits\":[{\"old_string\":\"a\",\"new_string\":\"x\\ny\"},{\"old_string\":\"b\",\"new_string\":\"z\"}]}"));
        Assert.Equal((2, 0), DiffStatOf("Write", "{\"content\":\"x\\ny\\n\"}"));
    }

    [Fact]
    public void AToolOtherThanTheEditToolsAndAnEditWithNoInputCarryNoDiffStat()
    {
        Assert.Null(DiffStatOf("Bash", "{\"command\":\"ls\"}"));
        var edit = Assert.Single(Parse(Message("assistant", "[{\"type\":\"tool_use\",\"id\":\"u\",\"name\":\"Edit\"}]")).Blocks);
        Assert.Null(edit.DiffStat);
    }

    [Fact]
    public void ATurnDurationEntryReportsItsMillisecondsAndAnotherSystemEntryDoesNot()
    {
        Assert.Equal(1234, Parse("{\"type\":\"system\",\"subtype\":\"turn_duration\",\"durationMs\":1234}").DurationMs);
        Assert.Null(Parse("{\"type\":\"system\",\"subtype\":\"other\",\"durationMs\":1234}").DurationMs);
        Assert.Null(Parse("{\"type\":\"system\",\"subtype\":\"turn_duration\",\"durationMs\":-5}").DurationMs);
        Assert.Null(Parse("{\"type\":\"system\",\"subtype\":\"turn_duration\"}").DurationMs);
    }

    [Fact]
    public void TheRowCarriesTheDerivedFieldsAndTurnMsOnlyOnATurnStart()
    {
        var start = Parse(Message("user", "\"go\""));
        var result = Parse(Message("user", "[{\"type\":\"tool_result\",\"tool_use_id\":\"t\",\"content\":\"a\\nb\"}]"));

        var startJson = Ah.Core.Json.JsonText.Stringify(start.ToJson(750), 0);
        var resultJson = Ah.Core.Json.JsonText.Stringify(result.ToJson(750), 0);

        Assert.Contains("\"turn_start\":true", startJson, StringComparison.Ordinal);
        Assert.Contains("\"turn_ms\":750", startJson, StringComparison.Ordinal);
        Assert.Contains("\"turn_ms\":null", Ah.Core.Json.JsonText.Stringify(start.ToJson(), 0), StringComparison.Ordinal);
        Assert.DoesNotContain("turn_start", resultJson, StringComparison.Ordinal);
        Assert.DoesNotContain("turn_ms", resultJson, StringComparison.Ordinal);
        Assert.Contains("\"lines\":1", resultJson, StringComparison.Ordinal);
    }
}
