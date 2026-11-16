using Ah.Core.Json;

namespace Ah.Hooks.Tests;

public sealed partial class HooksHostTests
{
    private static string DialogAnswer(Rig rig, string question, string answer, string? source = null, string extra = "", string options = "[{\"label\":\"yes\"},{\"label\":\"no\"}]")
    {
        var metadata = source is null ? string.Empty : $"\"metadata\":{{\"source\":{System.Text.Json.JsonSerializer.Serialize(source)}}},";
        var q = System.Text.Json.JsonSerializer.Serialize(question);
        var a = System.Text.Json.JsonSerializer.Serialize(answer);
        return "{\"hook_event_name\":\"PostToolUse\",\"session_id\":\"sess-1\",\"cwd\":" + System.Text.Json.JsonSerializer.Serialize(ProjOf(rig)) + extra
            + ",\"tool_use_id\":\"toolu_7\",\"tool_name\":\"AskUserQuestion\",\"tool_input\":{" + metadata
            + "\"questions\":[{\"question\":" + q + ",\"header\":\"h\",\"multiSelect\":false,\"options\":" + options + "}]},\"tool_response\":{\"answers\":{" + q + ":" + a + "}}}";
    }

    private static List<JsonValue> AnsweredWrites(Rig rig) =>
        [.. rig.Writer.Arguments.Select(a => JsonText.Parse(a))
            .Where(a => a.Member("path") is { IsText: true } p && p.AsString().EndsWith("/parked-questions.jsonl", StringComparison.Ordinal))
            .Select(a => a.Member("row")!.Value)
            .Where(row => row.Member("kind")!.Value.AsString() == "answered")];

    [Fact]
    public void ADialogAnswerToTheParkedQuestionsOwnTextResolvesIt()
    {
        var rig = new Rig();
        Seed(rig, ParkedAs("q1", "sess-1"));

        _ = rig.Run("posttooluse-ask-record", DialogAnswer(rig, "Alarm?", "Yes"), services: AsOrchestrator(rig));

        var answered = Assert.Single(AnsweredWrites(rig));
        Assert.Equal("q1", answered.Member("id")!.Value.AsString());
        Assert.Equal(["yes"], answered.Member("labels")!.Value.AsArray().Select(l => l.AsString()));
        Assert.Equal("posttooluse-ask-record", answered.Member("writer")!.Value.AsString());
        Assert.Contains("answered:q1", rig.Writer.OperationIds);
    }

    [Fact]
    public void ADialogAnswerWithTheRefTagAndRewordedTextButTheSameOptionsResolvesIt()
    {
        var rig = new Rig();
        Seed(rig, ParkedAs("q1", "sess-1"));

        _ = rig.Run("posttooluse-ask-record", DialogAnswer(rig, "Something else?", "no", "ah:R8;ref=q1", options: "[{\"label\":\" NO \"},{\"label\":\"Yes\"}]"), services: AsOrchestrator(rig));

        Assert.Equal("q1", Assert.Single(AnsweredWrites(rig)).Member("id")!.Value.AsString());
    }

    [Fact]
    public void ADialogAnswerWithTheRefTagButOtherTextAndOtherOptionsLeavesItParked()
    {
        var rig = new Rig();
        Seed(rig, ParkedAs("q1", "sess-1"));

        _ = rig.Run("posttooluse-ask-record", DialogAnswer(rig, "Something else?", "yes", "ah:R8;ref=q1", options: "[{\"label\":\"yes\"},{\"label\":\"later\"}]"), services: AsOrchestrator(rig));

        Assert.Empty(AnsweredWrites(rig));
    }

    [Fact]
    public void ADialogAnswerWithTheRefTagAndASubsetOfTheOptionsLeavesItParked()
    {
        var rig = new Rig();
        Seed(rig, ParkedAs("q1", "sess-1"));

        _ = rig.Run("posttooluse-ask-record", DialogAnswer(rig, "Something else?", "yes", "ah:R8;ref=q1", options: "[{\"label\":\"yes\"}]"), services: AsOrchestrator(rig));

        Assert.Empty(AnsweredWrites(rig));
    }

    [Fact]
    public void AReAskOfAnotherQuestionWithTheSameOptionsAndARefToQ3ResolvesNothingAndLogsIt()
    {
        var rig = new Rig();
        Seed(
            rig,
            ParkedAs("q3", "sess-1"),
            Ah.Hierarchy.ParkedQuestions.ParkedLine("q4", "2026-10-11T11:00:00.000Z", "tm", "sess-1", "toolu_0", 1, Q("Other alarm?", "Alarm", "yes", "no"), "ah:R8", "reserved"));

        _ = rig.Run("posttooluse-ask-record", DialogAnswer(rig, "Re-ask of the second one?", "yes", "ah:R8;ref=q3"), services: AsOrchestrator(rig));

        Assert.Empty(AnsweredWrites(rig));
        Assert.Contains(rig.Errors.Records, r => r.Message.Contains("matched no open parked question", StringComparison.Ordinal));
    }

    [Fact]
    public void ARephrasedReAskWithAUniqueOptionSetAndItsRefResolvesIt()
    {
        var rig = new Rig();
        Seed(
            rig,
            ParkedAs("q3", "sess-1"),
            Ah.Hierarchy.ParkedQuestions.ParkedLine("q4", "2026-10-11T11:00:00.000Z", "tm", "sess-1", "toolu_0", 1, Q("Other alarm?", "Alarm", "now", "later"), "ah:R8", "reserved"));

        _ = rig.Run("posttooluse-ask-record", DialogAnswer(rig, "Rephrased?", "later", "ah:R8;ref=q4", options: "[{\"label\":\"now\"},{\"label\":\"later\"}]"), services: AsOrchestrator(rig));

        Assert.Equal("q4", Assert.Single(AnsweredWrites(rig)).Member("id")!.Value.AsString());
    }

    [Fact]
    public void ADecisionRefTagLeavesNoHookErrorRow()
    {
        var rig = new Rig();
        Seed(rig, ParkedAs("q3", "sess-1"));

        _ = rig.Run("posttooluse-ask-record", DialogAnswer(rig, "Unrelated?", "yes", "ah:R8;ref=d3"), services: AsOrchestrator(rig));

        Assert.DoesNotContain(rig.Errors.Records, r => r.Message.Contains("matched no open parked question", StringComparison.Ordinal));
    }

    [Fact]
    public void ADialogAnswerWithTheRefTagAndTheSameTextButOtherOptionsResolvesIt()
    {
        var rig = new Rig();
        Seed(rig, ParkedAs("q1", "sess-1"));

        _ = rig.Run("posttooluse-ask-record", DialogAnswer(rig, " alarm? ", "yes", "ah:R8;ref=q1", options: "[{\"label\":\"yes\"},{\"label\":\"later\"}]"), services: AsOrchestrator(rig));

        Assert.Equal("q1", Assert.Single(AnsweredWrites(rig)).Member("id")!.Value.AsString());
    }

    [Fact]
    public void ADialogAnswerThatMatchesNoParkedQuestionLeavesItParked()
    {
        var rig = new Rig();
        Seed(rig, ParkedAs("q1", "sess-1"));

        _ = rig.Run("posttooluse-ask-record", DialogAnswer(rig, "Unrelated?", "yes"), services: AsOrchestrator(rig));

        Assert.Empty(AnsweredWrites(rig));
    }

    [Fact]
    public void ARefTagForAnotherQuestionLeavesTheTextMatchParked()
    {
        var rig = new Rig();
        Seed(rig, ParkedAs("q1", "sess-1"));

        _ = rig.Run("posttooluse-ask-record", DialogAnswer(rig, "Alarm?", "yes", "ah:R8;ref=q10"), services: AsOrchestrator(rig));

        Assert.Empty(AnsweredWrites(rig));
    }

    [Fact]
    public void B14aAFreeTextAnswerToTheSameTextResolvesItVerbatimWithFreeText()
    {
        var rig = new Rig();
        Seed(rig, ParkedAs("q1", "sess-1"));

        _ = rig.Run("posttooluse-ask-record", DialogAnswer(rig, "Alarm?", "yes please"), services: AsOrchestrator(rig));

        var answered = Assert.Single(AnsweredWrites(rig));
        Assert.Equal(["yes please"], answered.Member("labels")!.Value.AsArray().Select(l => l.AsString()));
        Assert.True(answered.Member("free_text")!.Value.AsBool());
        Assert.Equal("dialog", answered.Member("via")!.Value.AsString());
    }

    [Fact]
    public void B14aNegativeAFreeTextAnswerMatchedOnlyByRefAndOptionSetLeavesItParked()
    {
        var rig = new Rig();
        Seed(rig, ParkedAs("q1", "sess-1"));

        _ = rig.Run("posttooluse-ask-record", DialogAnswer(rig, "Something else?", "maybe later", "ah:R8;ref=q1"), services: AsOrchestrator(rig));

        Assert.Empty(AnsweredWrites(rig));
    }

    [Fact]
    public void B14AReAskWithCaseSpaceAndTrailingPunctuationChangedResolvesItViaDialog()
    {
        var rig = new Rig();
        Seed(rig, Ah.Hierarchy.ParkedQuestions.ParkedLine("q7", "2026-10-11T11:00:00.000Z", "tm", "sess-1", "toolu_0", 0, Q("Merge lane-ops now?", "Merge", "yes", "no"), "ah:R8", "reserved"));

        _ = rig.Run("posttooluse-ask-record", DialogAnswer(rig, "merge  lane-ops   now", "yes"), services: AsOrchestrator(rig));

        var answered = Assert.Single(AnsweredWrites(rig));
        Assert.Equal("q7", answered.Member("id")!.Value.AsString());
        Assert.Equal(["yes"], answered.Member("labels")!.Value.AsArray().Select(l => l.AsString()));
        Assert.Equal("dialog", answered.Member("via")!.Value.AsString());
        Assert.Null(answered.Member("free_text"));
    }

    [Fact]
    public void B14NegativeADifferenceInsideTheTextIsNotNormalisedAway()
    {
        var rig = new Rig();
        Seed(rig, Ah.Hierarchy.ParkedQuestions.ParkedLine("q7", "2026-10-11T11:00:00.000Z", "tm", "sess-1", "toolu_0", 0, Q("Merge lane-ops now?", "Merge", "yes", "no"), "ah:R8", "reserved"));

        _ = rig.Run("posttooluse-ask-record", DialogAnswer(rig, "Merge lane ops now?", "yes please"), services: AsOrchestrator(rig));

        Assert.Empty(AnsweredWrites(rig));
    }

    [Fact]
    public void AResolutionByRefAndOptionSetAlsoCarriesViaDialog()
    {
        var rig = new Rig();
        Seed(rig, ParkedAs("q1", "sess-1"));

        _ = rig.Run("posttooluse-ask-record", DialogAnswer(rig, "Something else?", "no", "ah:R8;ref=q1"), services: AsOrchestrator(rig));

        var answered = Assert.Single(AnsweredWrites(rig));
        Assert.Equal("dialog", answered.Member("via")!.Value.AsString());
        Assert.Null(answered.Member("free_text"));
    }

    [Fact]
    public void AnAlreadyAnsweredParkedQuestionIsNotAnsweredAgain()
    {
        var rig = new Rig();
        Seed(rig, ParkedAs("q1", "sess-1"), Ah.Hierarchy.ParkedQuestions.AnsweredLine("q1", "2026-10-11T11:30:00.000Z", ["no"], "decision-answer", "ans:q1"));

        _ = rig.Run("posttooluse-ask-record", DialogAnswer(rig, "Alarm?", "yes"), services: AsOrchestrator(rig));

        Assert.Empty(AnsweredWrites(rig));
    }

    [Fact]
    public void ASubagentsDialogAnswerResolvesNothing()
    {
        var rig = new Rig();
        Seed(rig, ParkedAs("q1", "sess-1"));

        _ = rig.Run("posttooluse-ask-record", DialogAnswer(rig, "Alarm?", "yes", extra: ",\"agent_id\":\"a1\",\"agent_type\":\"general-purpose\""), services: AsOrchestrator(rig));

        Assert.Empty(AnsweredWrites(rig));
    }

    [Fact]
    public void AQuestionAnsweredInTheDialogOwesNoPushNudgeAndAnUnansweredOneStillDoes()
    {
        var rig = new Rig();
        Seed(rig, ParkedAs("q1", "sess-1"));
        Assert.NotNull(Nudge(rig));

        _ = rig.Run("posttooluse-ask-record", DialogAnswer(rig, "Alarm?", "yes"), services: AsOrchestrator(rig));
        var store = Ah.Hierarchy.ParkedQuestions.PathIn(StoreDir(rig));
        File.AppendAllText(store, string.Concat(AnsweredWrites(rig).Select(w => w.Member("id")!.Value.AsString() == "q1" ? StoreLine(Ah.Hierarchy.ParkedQuestions.AnsweredLine("q1", "2026-10-11T12:00:00.000Z", ["yes"], "posttooluse-ask-record", "rec:toolu_7")) : string.Empty)));

        Assert.Null(Nudge(rig));
    }
}
