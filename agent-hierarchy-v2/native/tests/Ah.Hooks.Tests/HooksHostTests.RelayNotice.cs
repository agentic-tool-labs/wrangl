using System.Text;

namespace Ah.Hooks.Tests;

public sealed partial class HooksHostTests
{
    private const string RunId = "20261021-100000-aa01";

    /// <summary>Opens a pipeline run in the Rig's project.</summary>
    private static void OpenRun(Rig rig)
    {
        Directory.CreateDirectory(Path.Combine(ProjOf(rig), ".git"));
        var pool = rig.Services.Config.HierarchyDir(ProjOf(rig));
        Directory.CreateDirectory(Path.Combine(pool, "msgs"));
        File.WriteAllText(
            Path.Combine(pool, "msgs", $"{RunId}--orchestrator--pipeline-run-anchor--request.md"),
            $"---\nid: {RunId}\ntype: request\nto: orchestrator\nfrom: orchestrator\nslug: pipeline-run-anchor\ncreated: 2026-10-21T10:00:00.000Z\n---\n\n## constraints\nbranch: ah/x\n");
    }

    private const string AnsweredStdout ="{\"stdout\":\"{\\\"id\\\":\\\"q1\\\",\\\"answered\\\":true,\\\"labels\\\":\\\"yes\\\"}\\n\",\"stderr\":\"\",\"interrupted\":false}";

    private static string RelayLine(string command, string? response, bool recorded = true, string? agentId = null)
    {
        var rig = new Rig();
        Seed(rig, ParkedAs("q1", "sess-1"));
        var outcome = rig.Run("posttooluse-merge-record", BashDone(rig, command, response, agentId: agentId), services: AsOrchestrator(rig, recorded));
        return Encoding.UTF8.GetString(outcome.Stdout.Span);
    }

    [Fact]
    public void AnOrchestratorsAnsweredRelayShowsTheUserOneLineNamingTheQuestionAndTheLabel()
    {
        var line = RelayLine("ah msg decision answer q1 yes", AnsweredStdout);

        Assert.Contains("\"systemMessage\":", line, StringComparison.Ordinal);
        Assert.Contains("answered q1 \\u0022Alarm\\u0022 for you: yes", line.Replace("\\\"", "\\u0022", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("decision answer q1 --help", line, StringComparison.Ordinal);
        Assert.DoesNotContain("\"decision\"", line, StringComparison.Ordinal);
    }

    [Fact]
    public void ARelayedAnswerSaysItRelayedTheChatAnswerAndAUserAnswerKeepsTheOldText()
    {
        const string relayed = "{\"stdout\":\"{\\\"id\\\":\\\"q1\\\",\\\"answered\\\":true,\\\"labels\\\":\\\"yes\\\",\\\"relayed_prompt\\\":\\\"p9\\\"}\\n\",\"stderr\":\"\",\"interrupted\":false}";

        var withField = Ah.Core.Json.JsonText.Parse(Encoding.UTF8.GetBytes(RelayLine("ah msg decision answer q1 yes", relayed))).Member("systemMessage")!.Value.AsString();
        var without = Ah.Core.Json.JsonText.Parse(Encoding.UTF8.GetBytes(RelayLine("ah msg decision answer q1 yes", AnsweredStdout))).Member("systemMessage")!.Value.AsString();

        Assert.Equal("ah: relayed your chat answer to q1 \"Alarm\": yes. Wrong? Run in a terminal: ah msg decision answer q1 --help", withField);
        Assert.Equal("ah: answered q1 \"Alarm\" for you: yes. Wrong? Run: ah msg decision answer q1 --help", without);
    }

    [Fact]
    public void ARelayedPromptIdHoldingAnEscapedQuoteStillReadsAsRelayed()
    {
        const string relayed = "{\"stdout\":\"{\\\"id\\\":\\\"q1\\\",\\\"answered\\\":true,\\\"labels\\\":\\\"yes\\\",\\\"relayed_prompt\\\":\\\"p\\\\\\\"9\\\"}\\n\",\"stderr\":\"\",\"interrupted\":false}";

        var message = Ah.Core.Json.JsonText.Parse(Encoding.UTF8.GetBytes(RelayLine("ah msg decision answer q1 yes", relayed))).Member("systemMessage")!.Value.AsString();

        Assert.StartsWith("ah: relayed your chat answer to q1", message, StringComparison.Ordinal);
    }

    private static string TwoAnswered(string firstLabels, string secondLabels) =>
        "{\"stdout\":\"{\\\"id\\\":\\\"q1\\\",\\\"answered\\\":true,\\\"labels\\\":\\\"" + firstLabels
        + "\\\"}\\n{\\\"id\\\":\\\"q2\\\",\\\"answered\\\":true,\\\"labels\\\":\\\"" + secondLabels + "\\\"}\\n\",\"stderr\":\"\",\"interrupted\":false}";

    [Fact]
    public void AChainedCommandShowsOneLinePerAnsweredQuestionEachWithItsOwnLabels()
    {
        var rig = new Rig();
        Seed(rig, ParkedAs("q1", "sess-1"));
        var outcome = rig.Run(
            "posttooluse-merge-record",
            BashDone(rig, "ah msg decision answer q1 yes && ah msg decision answer q2 no", TwoAnswered("yes", "no")),
            services: AsOrchestrator(rig, true));

        var message = Ah.Core.Json.JsonText.Parse(outcome.Stdout.Span).Member("systemMessage")!.Value.AsString().Split('\n');

        Assert.Equal(2, message.Length);
        Assert.Contains("answered q1 \"Alarm\" for you: yes.", message[0], StringComparison.Ordinal);
        Assert.Contains("answered q2 \"Question\" for you: no.", message[1], StringComparison.Ordinal);
    }

    [Fact]
    public void AChainedCommandWhoseSecondAnswerWasRefusedShowsOnlyTheFirstLine()
    {
        var rig = new Rig();
        Seed(rig, ParkedAs("q1", "sess-1"));
        var response = "{\"stdout\":\"{\\\"id\\\":\\\"q1\\\",\\\"answered\\\":true,\\\"labels\\\":\\\"yes\\\"}\\n{\\\"refused\\\":\\\"unmatched_label\\\"}\\n\",\"stderr\":\"\",\"interrupted\":false}";
        var outcome = rig.Run("posttooluse-merge-record", BashDone(rig, "ah msg decision answer q1 yes; ah msg decision answer q2 x", response), services: AsOrchestrator(rig, true));

        var message = Ah.Core.Json.JsonText.Parse(outcome.Stdout.Span).Member("systemMessage")!.Value.AsString();

        Assert.DoesNotContain('\n', message);
        Assert.Contains("answered q1", message, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePinnedMergeCommandStillWritesTheMergeRecordAndShowsNoRelayLine()
    {
        var rig = new Rig();
        OpenRun(rig);

        var outcome = rig.Run("posttooluse-merge-record", BashDone(rig, "gh pr merge 12 --match-head-commit " + new string('a', 40) + " --squash"), services: AsOrchestrator(rig, true));

        Assert.Contains("decisions.append", rig.Writer.Calls);
        Assert.Empty(outcome.Stdout.ToArray());
    }

    [Fact]
    public void ARelayCallWritesNoMergeRecord()
    {
        var rig = new Rig();
        OpenRun(rig);
        Seed(rig, ParkedAs("q1", "sess-1"));

        var outcome = rig.Run("posttooluse-merge-record", BashDone(rig, "ah msg decision answer q1 yes", AnsweredStdout), services: AsOrchestrator(rig, true));

        Assert.DoesNotContain("decisions.append", rig.Writer.Calls);
        Assert.Contains("answered q1", Encoding.UTF8.GetString(outcome.Stdout.Span), StringComparison.Ordinal);
    }

    [Fact]
    public void ARefusedRelayShowsNoLine() =>
        Assert.Equal(string.Empty, RelayLine("ah msg decision answer q1 yes", "{\"stdout\":\"{\\\"refused\\\":\\\"answer_no_user_prompt\\\"}\",\"stderr\":\"\",\"interrupted\":false}"));

    [Fact]
    public void AnAnswerCallThatAnsweredNothingShowsNoLine() =>
        Assert.Equal(string.Empty, RelayLine("ah msg decision answer q1 yes", "{\"stdout\":\"{\\\"id\\\":\\\"q1\\\",\\\"answered\\\":false}\",\"stderr\":\"\",\"interrupted\":false}"));

    [Fact]
    public void AnotherCommandShowsNoLine() =>
        Assert.Equal(string.Empty, RelayLine("ah msg decision list", AnsweredStdout));

    [Fact]
    public void ASubagentsAnswerCallShowsNoLine() =>
        Assert.Equal(string.Empty, RelayLine("ah msg decision answer q1 yes", AnsweredStdout, agentId: "agent-7"));

    [Fact]
    public void ASessionThatIsNotAnOrchestratorShowsNoLine() =>
        Assert.Equal(string.Empty, RelayLine("ah msg decision answer q1 yes", AnsweredStdout, recorded: false));
}
