using Ah.Hierarchy;
using Ah.Hooks.Lifecycle;

namespace Ah.Hooks.Tests;

/// <summary>The Orchestrator's turn-end reminder to park a question for the user as a Needs-you item (atp-1703 part 4).</summary>
public sealed class NeedsYouNudgeTests
{
    private const string Waiting = "Done with the build.\n\nWaiting on you:\n- b12: ship now or after QA";

    [Fact]
    public void AWaitingOnYouListWithNoItemSetThisTurnFires() =>
        Assert.True(NeedsYouNudge.Applies(Waiting, ["git status", "ah roster checklist show"]));

    [Fact]
    public void AQuestionToTheUserFires() =>
        Assert.True(NeedsYouNudge.Applies("All green.\nDo you want it merged today?", []));

    [Theory]
    [InlineData("ah roster checklist set b12 --state waiting-user --note \"Ship now?\" --class soon")]
    [InlineData("ah msg decision park --header b12 --question \"Ship now?\" --option a --option b --default a --class blocker")]
    public void NegativeAnItemSetThisTurnDoesNotFire(string command) =>
        Assert.False(NeedsYouNudge.Applies(Waiting, ["git status", command]));

    [Theory]
    [InlineData("Merged b12 at abc1234. Nothing waits.")]
    [InlineData("Is the cache warm? It is.")]
    [InlineData("")]
    public void NegativeAReplyThatAsksTheUserNothingDoesNotFire(string reply) =>
        Assert.False(NeedsYouNudge.Applies(reply, []));

    [Fact]
    public void NegativeAChecklistSetToAnotherStateStillFires() =>
        Assert.True(NeedsYouNudge.Applies(Waiting, ["ah roster checklist set b12 --state blocked --note x"]));

    [Fact]
    public void TheTranscriptScanGathersOnlyThisTurnsCommands()
    {
        var rig = new HooksHostTests.Rig();
        var path = rig.Sandbox.Root + "/t.jsonl";
        static string User(string text) => $$$"""{"type":"user","message":{"content":"{{{text}}}"}}""";
        static string Tool(string command) => $$$"""{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Bash","input":{"command":"{{{command}}}"}}]}}""";
        File.WriteAllText(path, string.Join('\n',
            User("earlier"), Tool("ah msg decision park --old"),
            User("now"), Tool("git status"),
            """{"type":"assistant","message":{"content":[{"type":"text","text":"Waiting on you: b12"}]}}""") + "\n");

        var commands = new List<string>();
        var reply = HierarchyModule.QaReplyOf(rig.Services.Files, path, commands);

        Assert.Equal(["git status"], commands);
        Assert.True(NeedsYouNudge.Applies(reply.Text, commands));
    }
}
