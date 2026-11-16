using System.Text;
using Ah.Core.Json;

namespace Ah.Hooks.Tests;

/// <summary>The SubagentStop nudge for a subagent that was briefed with a request file.</summary>
public sealed class SubagentStopMsgNudgeTests
{
    private const string Id = "20260101-000000-abcd";

    private sealed class Run
    {
        internal Run(string? response)
        {
            Rig = new HooksHostTests.Rig();
            var proj = Rig.Sandbox.Root + "/proj";
            var config = proj + "/.claude/ah2";
            _ = Directory.CreateDirectory(config);
            File.WriteAllText(config + "/agent-hierarchy.json", """{"version":1}""");
            var msgs = Rig.Services.Config.HierarchyDir(proj) + "/msgs";
            _ = Directory.CreateDirectory(msgs);
            Request = $"{msgs}/{Id}--implementor--demo--request.md";
            File.WriteAllText(Request, $"---\nid: {Id}\ntype: request\nto: implementor\nfrom: orchestrator\nslug: demo\ncreated: 2026-01-01T00:00:00-00:00\neta: small\n---\n\n## [0] tldr\n- [1] goal: x\n");
            Response = $"{msgs}/{Id}--orchestrator--demo--response.md";
            if (response is not null)
            {
                File.WriteAllText(Response, response);
            }

            Transcript = Rig.Sandbox.Root + "/agent.jsonl";
            File.WriteAllText(
                Transcript,
                "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"[hierarchy-msg " + Request + "]\"}}\n");
        }

        internal HooksHostTests.Rig Rig { get; }

        internal string Request { get; }

        internal string Response { get; }

        internal string Transcript { get; }

        internal string Stop(string lastMessage)
        {
            var e = new JsonObject
            {
                ["hook_event_name"] = "SubagentStop",
                ["session_id"] = "sess-1",
                ["cwd"] = Rig.Sandbox.Root + "/proj",
                ["agent_id"] = "a-1",
                ["agent_type"] = "ah:implementor",
                ["last_assistant_message"] = lastMessage,
                ["agent_transcript_path"] = Transcript,
            };
            var services = Rig.Services with { Advisory = StopOrchestratorLivenessTests.Advisory(Rig, []) };
            return Encoding.UTF8.GetString(Rig.Run("subagentstop-msg-nudge", Encoding.UTF8.GetString(JsonText.ToUtf8(e, 0)), services: () => services).Stdout.Span);
        }
    }

    private const string Authored = "---\nid: 20260101-000000-abcd\ntype: response\n---\n\n## [0] tldr\n- [1] status: DONE\n";
    private const string Stub = "---\nid: 20260101-000000-abcd\ntype: response\n---\n";

    [Fact]
    public void AFilledResponseInTheStoreWithNoFileMakesAMissingPointerSilent()
    {
        var run = new Run(Authored);
        Ah.TestKit.TestMsgStore.DropFiles(run.Rig.Sandbox.Root + "/ah", run.Rig.Sandbox.Root);

        Assert.Equal(string.Empty, run.Stop("all done"));
    }

    [Fact]
    public void AFilledResponseFileMakesAMissingPointerSilent()
    {
        Assert.Equal(string.Empty, new Run(Authored).Stop("all done"));
    }

    [Fact]
    public void NoResponseFileBlocksOnceNamingTheRequestAndTheCommand()
    {
        var run = new Run(null);

        var output = run.Stop("all done");

        Assert.Contains("\"decision\":\"block\"", output, StringComparison.Ordinal);
        Assert.Contains("please file your response", output, StringComparison.Ordinal);
        Assert.Contains("ah msg show " + Ah.Core.Json.MsgPointer.Id(run.Request), output, StringComparison.Ordinal);
    }

    [Fact]
    public void AnActiveChainDownRowOfTheSessionSilencesTheNudge()
    {
        var run = new Run(null);
        StopPeerNudgeReportTests.ChainDownRow(run.Rig, "sess-1");

        Assert.Equal(string.Empty, run.Stop("all done"));
    }

    [Theory]
    [InlineData("sess-other", "active")]
    [InlineData("sess-1", "cleared")]
    public void NegativeAChainDownRowThatIsNotThisSessionsActiveRowLeavesTheNudgeFiring(string session, string state)
    {
        var run = new Run(null);
        StopPeerNudgeReportTests.ChainDownRow(run.Rig, session, state);

        Assert.Contains("please file your response", run.Stop("all done"), StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyStubOfTheResponseFileStillBlocks()
    {
        Assert.Contains("please file your response", new Run(Stub).Stop("all done"), StringComparison.Ordinal);
    }

    [Fact]
    public void APointerToAMissingFileBlocksUnlessTheRequestsOwnResponseFileIsFilled()
    {
        var empty = new Run(null);
        var filled = new Run(Authored);

        Assert.Contains("please file your response", empty.Stop($"[hierarchy-msg {empty.Response}.missing]"), StringComparison.Ordinal);
        Assert.Equal(string.Empty, filled.Stop($"[hierarchy-msg {filled.Response}.missing]"));
    }

    [Fact]
    public void ATokenNamingTheBlankTemplateStillBlocks()
    {
        const string Template = "---\nid: 20260101-000000-abcd\ntype: response\n---\n\n## [0] tldr\n\n## [1] status\n- Tag decisions, deviations, roadblocks, outcomes and details as \"Decision: …\", \"Deviation: …\", \"Roadblock: …\", \"Outcome: …\", \"Detail: …\"; tagged lines are journaled.\n";
        var run = new Run(Template);

        Assert.Contains("please file your response", run.Stop($"[hierarchy-msg {run.Response}]\n- status: done"), StringComparison.Ordinal);
    }

    [Fact]
    public void ThePointerToTheFilledFileAllowsTheStop()
    {
        var run = new Run(Authored);

        Assert.Equal(string.Empty, run.Stop($"[hierarchy-msg {run.Response}]\n- status: done"));
    }
}
