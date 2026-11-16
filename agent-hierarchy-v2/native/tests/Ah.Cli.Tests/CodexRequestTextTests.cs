using Ah.Cli.Composition;
using Ah.Codex;
using Ah.Core.Json;

namespace Ah.Cli.Tests;

/// <summary>The text the Orchestrator is mailed for a held Codex request.</summary>
public sealed class CodexRequestTextTests
{
    private static readonly CodexMember Member = new("tm.alpha", "tm", "alpha", "alpha@tm", "/h/teams/tm.json", 1, "/s", "th", new JsonObject { ["name"] = "alpha", ["cwd"] = "/work/tree" });

    private static CodexPending Approval(string kind = "command-approval", string verbatim = "{\"command\":\"rm -rf build\",\"cwd\":\"/work/tree\"}") =>
        new("tm.alpha", "7", kind, "m", verbatim, false, true, ["accept", "acceptForSession", "decline", "cancel"]);

    [Fact]
    public void AnApprovalShowsTheRequestVerbatimItsChoicesTheWorkTreeAndTheAnswerCommand()
    {
        var text = CodexDependencies.RequestText(Member, Approval(), "/work/tree");

        Assert.StartsWith("alpha@tm is waiting on a Codex command-approval (request 7).", text);
        Assert.Contains("{\"command\":\"rm -rf build\",\"cwd\":\"/work/tree\"}", text);
        Assert.Contains("Choices: accept, acceptForSession, decline, cancel", text);
        Assert.Contains("inside /work/tree", text);
        Assert.Contains("ah roster answer alpha --request 7 --choice <choice> --cwd <cwd>", text);
        Assert.Contains("needs-user", text);
    }

    private static CodexMember WithLead(string json) =>
        new("tm.alpha", "tm", "alpha", "alpha@tm", "/h/teams/tm.json", 1, "/s", "th", JsonText.Parse(json));

    private const string LaneRecord = "{\"parent\":{\"team\":\"tm\",\"stream\":\"ws1\"},\"orchestrator\":{\"pid\":7,\"pid_start\":\"x\"}}";
    private const string ManagerRecord = "{\"members\":[{\"name\":\"so\",\"role\":\"sub-orchestrator\",\"stream\":\"ws1\"}],\"streams\":{\"ws1\":{\"sub_orchestrator\":\"so\",\"state\":\"open\"}}}";

    private static JsonValue Team(string name) =>
        name switch
        {
            "tm" => JsonText.Parse(ManagerRecord),
            "tm-ws1" => JsonText.Parse(LaneRecord),
            _ => JsonValue.Null,
        };

    [Theory]
    [InlineData(true, "orchestrator@tm-ws1", "/h/teams/tm-ws1.json")]
    [InlineData(false, "orchestrator@tm", "/h/teams/tm.json")]
    public void ANoticeGoesToTheOrchestratorOfAValidLiveLeadsTeamAndOtherwiseToTheMembersOwn(bool live, string address, string teamFile)
    {
        var member = WithLead("{\"name\":\"alpha\",\"lead\":{\"team\":\"tm-ws1\",\"name\":\"so\"}}");

        Assert.Equal((address, teamFile), CodexDependencies.NoticeTarget(member, Team, name => $"/h/teams/{name}.json", (_, _) => live));
    }

    [Fact]
    public void ADissolvedLanesMemberNoticesTheManagersOrchestratorAtTheManagersOwnTeamFile()
    {
        var member = new CodexMember("tm-ws1.alpha", "tm-ws1", "alpha", "alpha@tm-ws1", "/h/teams/tm-ws1.json", 1, "/s", "th", JsonText.Parse("{\"name\":\"alpha\"}"));
        JsonValue Dissolved(string name) => name switch
        {
            "tm" => JsonText.Parse("{\"streams\":{\"ws1\":{\"state\":\"done\",\"sub_orchestrator\":\"so\"}}}"),
            "tm-ws1" => JsonText.Parse("{\"parent\":{\"team\":\"tm\",\"stream\":\"ws1\"},\"workstream\":{\"state\":\"dissolved\"}}"),
            _ => JsonValue.Null,
        };

        Assert.Equal(("orchestrator@tm", "/h/team.json"), CodexDependencies.NoticeTarget(member, Dissolved, _ => "/h/team.json", (_, _) => true));
    }

    [Fact]
    public void AMemberWithNoLeadNoticesItsOwnTeamsOrchestrator() =>
        Assert.Equal(("orchestrator@tm", "/h/teams/tm.json"), CodexDependencies.NoticeTarget(WithLead("{\"name\":\"alpha\"}"), Team, name => $"/h/teams/{name}.json", (_, _) => true));

    private const string Questions =
        "{\"questions\":[{\"id\":\"a\",\"header\":\"Colour\",\"question\":\"Which?\",\"isOther\":true,\"options\":[{\"label\":\"red\",\"description\":\"warm\"},{\"label\":\"blue\",\"description\":\"cool\"}]},{\"id\":\"b\",\"header\":\"Size\",\"question\":\"How big?\",\"isOther\":false}]}";

    [Fact]
    public void AQuestionListsEachQuestionWithItsOptionsAndWhetherOtherIsAllowed()
    {
        var pending = new CodexPending("tm.alpha", "9", "question", "m", Questions, false, true, []);

        var text = CodexDependencies.RequestText(Member, pending, "/work/tree");

        Assert.StartsWith("alpha@tm asks (request 9):", text);
        Assert.Contains("[a] Colour: Which? — options: red (warm), blue (cool); other allowed: yes", text);
        Assert.Contains("[b] Size: How big? — options: none; other allowed: no", text);
        Assert.Contains("ah roster answer alpha --request 9 --answer <qid>=<text>", text);
    }

    [Fact]
    public void ASecretQuestionCarriesNoQuestionTextAndNoAnswerCommand()
    {
        var pending = new CodexPending("tm.alpha", "11", "question", "m", null, true, true, []);

        var text = CodexDependencies.RequestText(Member, pending, "/work/tree");

        Assert.StartsWith("alpha@tm asks a secret question (request 11). The user answers it in the member's pane.", text);
        Assert.Contains("Do not ask for the secret in chat or mail.", text);
        Assert.DoesNotContain("ah roster answer", text);
    }
}
