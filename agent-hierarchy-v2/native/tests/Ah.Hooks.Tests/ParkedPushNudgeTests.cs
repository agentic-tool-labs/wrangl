using Ah.Core.Json;

namespace Ah.Hooks.Tests;

/// <summary>fewer-turns S7.6 presence rule: a later answered dialog in the same session silences the push nudge (rows B-14b to B-14e).</summary>
public sealed partial class HooksHostTests
{
    private const string AskRecordWriter = "posttooluse-ask-record";

    /// <summary>A reworded re-ask of q7 (<c>ParkedAs</c>: "Alarm?", header "Alarm", options yes/no) keeping its header and labels: it carries q7 (S7-R2).</summary>
    private const string CarriesQ7ByHeader = "[{\"question\":\"Should the alarm go off now\",\"header\":\"  alarm \",\"options\":[{\"label\":\"No\"},{\"label\":\" YES \"}]}]";

    /// <summary>A question that shares nothing with q7: other text, header and labels.</summary>
    private const string Unrelated = "[{\"question\":\"Other?\",\"header\":\"h\",\"options\":[{\"label\":\"go\"}]}]";

    /// <summary>Appends one asks.jsonl row beside the parked store, as <c>posttooluse-ask-record</c> writes it.</summary>
    private static void AskedInDialog(Rig rig, string session, string at, string answersJson = "{\"x\":\"sure\"}", string writer = AskRecordWriter, string questionsJson = CarriesQ7ByHeader)
    {
        var questions = JsonText.Parse(questionsJson);
        var response = JsonText.Parse("{\"answers\":" + answersJson + "}");
        var row = Ah.Hierarchy.AskRecord.Row(at, writer, JsonValue.FromString(session), questions, response, null);
        _ = Directory.CreateDirectory(StoreDir(rig));
        File.AppendAllText(StoreDir(rig) + "/asks.jsonl", StoreLine(row));
    }

    private static JsonObject ParkedWith(string id, string header, params string[] labels) =>
        Ah.Hierarchy.ParkedQuestions.ParkedLine(id, "2026-10-11T11:00:00.000Z", "tm", "sess-1", "toolu_0", 0, Q(id + " text?", header, labels), "ah:R8", "reserved");

    private static IReadOnlyList<Ah.Hierarchy.ParkedQuestion> Folded(Rig rig) =>
        Ah.Hierarchy.ParkedQuestions.Fold(File.ReadAllText(Ah.Hierarchy.ParkedQuestions.PathIn(StoreDir(rig))));

    [Fact]
    public void B14bALaterDialogCarryingTheParkedQuestionsHeaderAndLabelsSilencesTheNudgeAndLeavesTheQuestionOpen()
    {
        var rig = new Rig();
        Seed(rig, ParkedAs("q7", "sess-1"));
        AskedInDialog(rig, "sess-1", "2026-10-11T12:00:00.000Z");

        Assert.Null(Nudge(rig));
        Assert.True(Assert.Single(Folded(rig)).Open);
    }

    [Fact]
    public void B14b2AHeaderAndLabelSetSharedByTwoParkedQuestionsSilencesBothAndAnswersNeither()
    {
        var rig = new Rig();
        Seed(rig, ParkedAs("q7", "sess-1"), ParkedWith("q9", "Alarm", "yes", "no"));
        AskedInDialog(rig, "sess-1", "2026-10-11T12:00:00.000Z");

        Assert.Null(Nudge(rig));
        Assert.All(Folded(rig), q => Assert.True(q.Open));
    }

    [Fact]
    public void NegativeTheSameHeaderWithOtherLabelsDoesNotSilenceTheOtherQuestion()
    {
        var rig = new Rig();
        Seed(rig, ParkedAs("q7", "sess-1"), ParkedWith("q8", "Alarm", "a", "b"));
        AskedInDialog(rig, "sess-1", "2026-10-11T12:00:00.000Z", questionsJson: "[{\"question\":\"Reworded q8?\",\"header\":\"Alarm\",\"options\":[{\"label\":\"a\"},{\"label\":\"b\"}]}]");

        var nudge = Nudge(rig);
        Assert.Contains("q7 is parked but not pushed", nudge, StringComparison.Ordinal);
        Assert.DoesNotContain("q8 is parked", nudge, StringComparison.Ordinal);
    }

    private static string Dialog(params (string Header, string[] Labels)[] questions) =>
        "[" + string.Join(',', questions.Select((q, i) => "{\"question\":\"Q" + i + "?\",\"header\":\"" + q.Header + "\",\"options\":["
            + string.Join(',', q.Labels.Select(l => "{\"label\":\"" + l + "\"}")) + "]}")) + "]";

    private static string? NudgeAfter(string dialog)
    {
        var rig = new Rig();
        Seed(rig, ParkedWith("q7", "Merge", "Yes", "No"));
        AskedInDialog(rig, "sess-1", "2026-10-11T12:00:00.000Z", questionsJson: dialog);
        return Nudge(rig);
    }

    [Fact]
    public void B14b6NegativeTheSameHeaderWithOtherLabelsIsNudged() =>
        Assert.Contains("q7 is parked but not pushed", NudgeAfter(Dialog(("Merge", ["Now", "Later"]))), StringComparison.Ordinal);

    [Fact]
    public void B14b7NegativeTheSameLabelsUnderAnotherHeaderAreNudged() =>
        Assert.Contains("q7 is parked but not pushed", NudgeAfter(Dialog(("Deploy", ["Yes", "No"]))), StringComparison.Ordinal);

    [Fact]
    public void B14b8NegativeHeaderOnOneQuestionAndLabelsOnAnotherIsNudged() =>
        Assert.Contains("q7 is parked but not pushed", NudgeAfter(Dialog(("Merge", ["Now", "Later"]), ("Deploy", ["Yes", "No"]))), StringComparison.Ordinal);

    [Fact]
    public void B14b8HeaderAndLabelsOnTheSameQuestionIsSilenced() =>
        Assert.Null(NudgeAfter(Dialog(("Deploy", ["Now"]), ("merge", ["no", "YES"]))));

    [Fact]
    public void B14b3NegativeALaterDialogThatCarriedADifferentQuestionDoesNotSilenceIt()
    {
        var rig = new Rig();
        Seed(rig, ParkedAs("q7", "sess-1"));
        AskedInDialog(rig, "sess-1", "2026-10-11T12:00:00.000Z", questionsJson: Unrelated);

        Assert.Contains("q7 is parked but not pushed", Nudge(rig), StringComparison.Ordinal);
    }

    [Fact]
    public void B14b4OnlyTheQuestionTheDialogCarriedIsSilenced()
    {
        var rig = new Rig();
        Seed(rig, ParkedAs("q7", "sess-1"), ParkedWith("q8", "Deploy", "a", "b"));
        AskedInDialog(rig, "sess-1", "2026-10-11T12:00:00.000Z");

        var nudge = Nudge(rig);
        Assert.Contains("q8 is parked but not pushed", nudge, StringComparison.Ordinal);
        Assert.DoesNotContain("q7 is parked", nudge, StringComparison.Ordinal);
    }

    [Fact]
    public void B14b5TheSecondQuestionOfAThreeQuestionDialogCarryingTheLabelsSilencesIt()
    {
        var rig = new Rig();
        Seed(rig, ParkedAs("q7", "sess-1"));
        AskedInDialog(
            rig, "sess-1", "2026-10-11T12:00:00.000Z",
            questionsJson: "[{\"question\":\"A?\",\"header\":\"a\",\"options\":[{\"label\":\"1\"}]},{\"question\":\"B?\",\"header\":\"Alarm\",\"options\":[{\"label\":\"no\"},{\"label\":\"yes\"}]},{\"question\":\"C?\",\"header\":\"c\",\"options\":[{\"label\":\"3\"}]}]");

        Assert.Null(Nudge(rig));
    }

    [Fact]
    public void NegativeALabelSubsetIsNotTheSameLabelSet()
    {
        var rig = new Rig();
        Seed(rig, ParkedAs("q7", "sess-1"));
        AskedInDialog(rig, "sess-1", "2026-10-11T12:00:00.000Z", questionsJson: "[{\"question\":\"R?\",\"header\":\"Alarm\",\"options\":[{\"label\":\"yes\"}]}]");

        Assert.NotNull(Nudge(rig));
    }

    [Fact]
    public void B14cAParkedQuestionWithNoLaterDialogIsNudged()
    {
        var rig = new Rig();
        Seed(rig, ParkedAs("q7", "sess-1"));

        Assert.Contains("q7 is parked but not pushed", Nudge(rig), StringComparison.Ordinal);
    }

    [Fact]
    public void B14dNegativeADialogAnsweredInAnotherSessionDoesNotSilenceIt()
    {
        var rig = new Rig();
        Seed(rig, ParkedAs("q7", "sess-1"));
        AskedInDialog(rig, "sess-2", "2026-10-11T12:00:00.000Z");

        Assert.NotNull(Nudge(rig));
    }

    [Fact]
    public void B14eAPushedQuestionIsNotNudged()
    {
        var rig = new Rig();
        Seed(rig, ParkedAs("q7", "sess-1"), Ah.Hierarchy.ParkedQuestions.PushedLine("q7", "2026-10-11T11:01:00.000Z"));

        Assert.Null(Nudge(rig));
    }

    [Fact]
    public void NegativeADialogAnsweredBeforeTheQuestionWasParkedDoesNotSilenceIt()
    {
        var rig = new Rig();
        Seed(rig, ParkedAs("q7", "sess-1"));
        AskedInDialog(rig, "sess-1", "2026-10-11T10:59:59.000Z");

        Assert.NotNull(Nudge(rig));
    }

    [Fact]
    public void NegativeARowFromAnotherWriterDoesNotSilenceIt()
    {
        var rig = new Rig();
        Seed(rig, ParkedAs("q7", "sess-1"));
        AskedInDialog(rig, "sess-1", "2026-10-11T12:00:00.000Z", writer: "decision-answer");

        Assert.NotNull(Nudge(rig));
    }

    [Fact]
    public void NegativeADialogWithNoAnswersDoesNotSilenceIt()
    {
        var rig = new Rig();
        Seed(rig, ParkedAs("q7", "sess-1"));
        AskedInDialog(rig, "sess-1", "2026-10-11T12:00:00.000Z", answersJson: "{}");

        Assert.NotNull(Nudge(rig));
    }
}
