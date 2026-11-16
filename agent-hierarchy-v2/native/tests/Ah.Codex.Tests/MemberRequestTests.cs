using Ah.Core.Json;

namespace Ah.Codex.Tests;

public sealed class MemberRequestTests
{
    private const string Member = "tm.alpha";

    private static JsonValue GoodReport() => JsonText.Parse(
        "{\"thread\":{\"id\":\"th-1\"},\"sandbox\":{\"type\":\"workspaceWrite\",\"networkAccess\":false,\"excludeSlashTmp\":true,\"excludeTmpdirEnvVar\":true},\"approvalPolicy\":\"on-request\"}");

    private static async Task<(CodexAdapter Adapter, FakeDependencies Deps, ScriptedClient Client)> Served()
    {
        var deps = new FakeDependencies();
        _ = deps.AddMember();
        var client = new ScriptedClient { Answer = (method, _) => method == "turn/start" ? JsonText.Parse("{\"turn\":{\"id\":\"tu-2\"}}") : new JsonObject() };
        var adapter = new CodexAdapter(deps, _ => Task.FromResult<IAppServerClient>(client), (_, _) => Task.FromResult<JsonValue?>(GoodReport()));
        await adapter.TickAsync(TestContext.Current.CancellationToken);
        return (adapter, deps, client);
    }

    private static void TurnStarted(ScriptedClient client) =>
        client.Push("turn/started", JsonText.Parse("{\"threadId\":\"th-1\",\"turn\":{\"id\":\"tu-1\"}}"));

    private static void TurnCompleted(ScriptedClient client) =>
        client.Push("turn/completed", JsonText.Parse("{\"threadId\":\"th-1\",\"turn\":{\"id\":\"tu-1\",\"status\":\"completed\"}}"));

    private static JsonValue Id(double n) => n;

    private static readonly IReadOnlyList<KeyValuePair<string, string>> NoAnswers = [];

    private const string Command = "{\"threadId\":\"th-1\",\"turnId\":\"tu-1\",\"itemId\":\"it\",\"command\":\"ls\",\"cwd\":\"/w\"}";

    [Theory]
    [InlineData("item/commandExecution/requestApproval", "command-approval")]
    [InlineData("item/fileChange/requestApproval", "file-change-approval")]
    [InlineData("item/tool/requestUserInput", "question")]
    [InlineData("mcpServer/elicitation/request", "elicitation")]
    public async Task EachKnownRequestIsHeldMarksTheMemberBlockedAndIsNotAnswered(string method, string kind)
    {
        var (_, deps, client) = await Served();

        client.ServerRequest(Id(7), method, "{\"threadId\":\"th-1\",\"questions\":[{\"id\":\"q1\",\"header\":\"H\",\"question\":\"Q?\",\"isOther\":false,\"isSecret\":false}]}");

        var raised = Assert.Single(deps.Raised);
        Assert.StartsWith($"{Member}|7|{kind}|False|True|", raised);
        Assert.Contains($"{Member}:blocked:{kind}", deps.Activities);
        Assert.Empty(client.Results);
        Assert.Empty(client.Errors);
    }

    [Fact]
    public async Task ARequestWithItsVerbatimTextOmitsTheThreadIdentifiers()
    {
        var (_, deps, client) = await Served();

        client.ServerRequest(Id(7), "item/commandExecution/requestApproval", Command);

        var raised = Assert.Single(deps.Raised);
        Assert.Contains("\"command\":\"ls\"", raised);
        Assert.DoesNotContain("threadId", raised);
        Assert.DoesNotContain("itemId", raised);
    }

    [Fact]
    public async Task AnApprovalAnswerRepliesWithTheMappedDecisionOnTheRequestsOwnId()
    {
        var (adapter, _, client) = await Served();
        client.ServerRequest(Id(7), "item/commandExecution/requestApproval", Command);

        var result = await adapter.AnswerAsync(Member, "7", "decline", NoAnswers, null);

        Assert.Equal("answered", result.Status);
        var (id, reply) = Assert.Single(client.Results);
        Assert.Equal(7, id.AsNumber());
        Assert.Equal("decline", reply.AsObject()["decision"].AsString());
        Assert.Empty(adapter.PendingRequests());
    }

    [Theory]
    [InlineData("grant")]
    [InlineData("")]
    [InlineData(null)]
    public async Task AnApprovalChoiceThatIsNotOneOfTheOffersSendsNothingAndKeepsTheRequestHeld(string? choice)
    {
        var (adapter, _, client) = await Served();
        client.ServerRequest(Id(7), "item/commandExecution/requestApproval", Command);

        var result = await adapter.AnswerAsync(Member, "7", choice, NoAnswers, null);

        Assert.Equal("bad-choice", result.Status);
        Assert.Empty(client.Results);
        Assert.Single(adapter.PendingRequests());
    }

    [Fact]
    public async Task AnAnswerForARequestThatWasNeverHeldIsUnknownAndSendsNothing()
    {
        var (adapter, _, client) = await Served();
        client.ServerRequest(Id(7), "item/commandExecution/requestApproval", Command);

        var result = await adapter.AnswerAsync(Member, "8", "accept", NoAnswers, null);

        Assert.Equal("unknown-request", result.Status);
        Assert.Empty(client.Results);
    }

    [Fact]
    public async Task AnAnswerForAMemberWithNoConnectionSendsNothing()
    {
        var (adapter, _, client) = await Served();
        client.ServerRequest(Id(7), "item/commandExecution/requestApproval", Command);

        var result = await adapter.AnswerAsync("tm.nobody", "7", "accept", NoAnswers, null);

        Assert.Equal("disconnected", result.Status);
        Assert.Empty(client.Results);
    }

    private const string TwoQuestions =
        "{\"threadId\":\"th-1\",\"isBlocking\":true,\"questions\":[{\"id\":\"a\",\"header\":\"Colour\",\"question\":\"Which?\",\"isOther\":true,\"isSecret\":false,\"options\":[{\"label\":\"red\"}]},{\"id\":\"b\",\"header\":\"Size\",\"question\":\"How big?\",\"isOther\":false,\"isSecret\":false}]}";

    [Fact]
    public async Task AQuestionAnswerRepliesWithEveryQuestionsAnswers()
    {
        var (adapter, _, client) = await Served();
        client.ServerRequest(Id(9), "item/tool/requestUserInput", TwoQuestions);

        var result = await adapter.AnswerAsync(Member, "9", null, [new("a", "red"), new("b", "large")], null);

        Assert.Equal("answered", result.Status);
        var (_, reply) = Assert.Single(client.Results);
        var answers = reply.AsObject()["answers"].AsObject();
        Assert.Equal("red", answers["a"].AsObject()["answers"].AsArray()[0].AsString());
        Assert.Equal("large", answers["b"].AsObject()["answers"].AsArray()[0].AsString());
    }

    [Fact]
    public async Task AQuestionAnswerThatMissesAQuestionOrNamesAnUnknownOneSendsNothing()
    {
        var (adapter, _, client) = await Served();
        client.ServerRequest(Id(9), "item/tool/requestUserInput", TwoQuestions);

        var missing = await adapter.AnswerAsync(Member, "9", null, [new("a", "red")], null);
        var stray = await adapter.AnswerAsync(Member, "9", null, [new("a", "red"), new("b", "x"), new("zzz", "y")], null);

        Assert.Equal("bad-choice", missing.Status);
        Assert.Equal("bad-choice", stray.Status);
        Assert.Empty(client.Results);
        Assert.Single(adapter.PendingRequests());
    }

    [Fact]
    public async Task AnElicitationDeclineRepliesWithNullContentAndAcceptOrAnythingElseSendsNothing()
    {
        var (adapter, _, client) = await Served();
        client.ServerRequest(Id(3), "mcpServer/elicitation/request", "{\"threadId\":\"th-1\",\"message\":\"m\"}");

        var bad = await adapter.AnswerAsync(Member, "3", "acceptForSession", NoAnswers, null);
        var accept = await adapter.AnswerAsync(Member, "3", "accept", NoAnswers, "secret value");
        Assert.Empty(client.Results);
        var ok = await adapter.AnswerAsync(Member, "3", "decline", NoAnswers, "ignored");

        Assert.Equal("bad-choice", bad.Status);
        Assert.Equal("needs-evidence", accept.Status);
        Assert.Equal("answered", ok.Status);
        var (_, reply) = Assert.Single(client.Results);
        Assert.Equal("decline", reply.AsObject()["action"].AsString());
        Assert.True(reply.AsObject()["content"].IsNull);
    }

    [Fact]
    public async Task APermissionsRequestIsRefusedAtOnceWithMethodNotFoundNotedAndNeverHeld()
    {
        var (adapter, deps, client) = await Served();

        client.ServerRequest(Id(4), "item/permissions/requestApproval", "{\"threadId\":\"th-1\",\"itemId\":\"i\",\"reason\":\"needs the network\"}");

        var (id, code, _) = Assert.Single(client.Errors);
        Assert.Equal(4, id.AsNumber());
        Assert.Equal(-32601, code);
        var note = Assert.Single(deps.PermissionNotes);
        Assert.Contains("needs the network", note);
        Assert.DoesNotContain("threadId", note);
        Assert.Empty(deps.Raised);
        Assert.Empty(deps.Unsupported);
        Assert.Empty(adapter.PendingRequests());
        Assert.DoesNotContain(deps.Activities, a => a.Contains("blocked"));
    }

    [Fact]
    public async Task ANumericIdAndAStringIdAreEchoedUnchangedAndOneIsNeverTakenForTheOther()
    {
        var (adapter, _, client) = await Served();
        client.ServerRequest(Id(12), "item/commandExecution/requestApproval", Command);
        client.ServerRequest("abc", "item/commandExecution/requestApproval", Command);

        _ = await adapter.AnswerAsync(Member, "abc", "decline", NoAnswers, null);
        _ = await adapter.AnswerAsync(Member, "12", "cancel", NoAnswers, null);
        var again = await adapter.AnswerAsync(Member, "\"abc\"", "decline", NoAnswers, null);

        Assert.True(client.Results[0].Id.IsText);
        Assert.Equal("abc", client.Results[0].Id.AsString());
        Assert.True(client.Results[1].Id.IsNumber);
        Assert.Equal(12, client.Results[1].Id.AsNumber());
        Assert.Equal("unknown-request", again.Status);
    }

    [Fact]
    public async Task AFileChangeRequestCarriesThePathsOfItsCachedItemAndItsGrantRoot()
    {
        var (adapter, _, client) = await Served();
        TurnStarted(client);
        client.Push("item/started", JsonText.Parse("{\"threadId\":\"th-1\",\"item\":{\"type\":\"fileChange\",\"id\":\"fc1\",\"changes\":[{\"path\":\"/w/a.txt\"},{\"path\":\"/w/b/c.txt\"}]}}"));
        client.ServerRequest(Id(20), "item/fileChange/requestApproval", "{\"threadId\":\"th-1\",\"itemId\":\"fc1\",\"grantRoot\":\"/w\"}");

        var held = Assert.Single(adapter.PendingRequests());

        Assert.Equal("fc1", held.ItemId);
        Assert.Equal(["/w/a.txt", "/w/b/c.txt"], held.Paths);
        Assert.Equal("/w", held.GrantRoot);
    }

    [Fact]
    public async Task AFileChangeRequestWithNoCachedItemOrAForgottenOneHasNoPaths()
    {
        var (adapter, _, client) = await Served();
        TurnStarted(client);
        client.Push("item/started", JsonText.Parse("{\"threadId\":\"th-1\",\"item\":{\"type\":\"fileChange\",\"id\":\"fc1\",\"changes\":[{\"path\":\"/w/a.txt\"}]}}"));
        client.ServerRequest(Id(20), "item/fileChange/requestApproval", "{\"threadId\":\"th-1\",\"itemId\":\"other\"}");
        TurnCompleted(client);
        TurnStarted(client);
        client.ServerRequest(Id(21), "item/fileChange/requestApproval", "{\"threadId\":\"th-1\",\"itemId\":\"fc1\"}");

        var held = adapter.PendingRequests();

        Assert.All(held, h => Assert.Null(h.Paths));
        Assert.All(held, h => Assert.Null(h.GrantRoot));
    }

    [Fact]
    public async Task ACommandRequestCarriesItsCwd()
    {
        var (adapter, _, client) = await Served();
        client.ServerRequest(Id(7), "item/commandExecution/requestApproval", Command);

        Assert.Equal("/w", Assert.Single(adapter.PendingRequests()).Cwd);
    }

    [Fact]
    public async Task AnAgentMessagesAttachedQuestionsJoinTheTurnEndNote()
    {
        var (_, deps, client) = await Served();
        TurnStarted(client);
        client.Push("item/completed", JsonText.Parse("{\"threadId\":\"th-1\",\"item\":{\"type\":\"agentMessage\",\"id\":\"m\",\"text\":\"done\",\"questions\":[{\"id\":\"q\",\"question\":\"Which branch?\"}]}}"));

        TurnCompleted(client);

        var note = Assert.Single(deps.TurnEnds)!;
        Assert.StartsWith("done", note);
        Assert.Contains("Which branch?", note);
    }

    [Fact]
    public async Task AnUnknownMethodIsRefusedAtOnceWithMethodNotFoundAndReportedNotHeld()
    {
        var (adapter, deps, client) = await Served();

        client.ServerRequest(Id(5), "item/somethingNew/request", "{}");

        var (id, code, _) = Assert.Single(client.Errors);
        Assert.Equal(5, id.AsNumber());
        Assert.Equal(-32601, code);
        Assert.Equal($"{Member}|item/somethingNew/request", Assert.Single(deps.Unsupported));
        Assert.Empty(deps.Raised);
        Assert.Empty(adapter.PendingRequests());
        Assert.DoesNotContain(deps.Activities, a => a.Contains("blocked"));
    }

    [Theory]
    [InlineData("item/tool/call")]
    [InlineData("account/chatgptAuthTokens/refresh")]
    [InlineData("attestation/generate")]
    [InlineData("currentTime/read")]
    public async Task TheRequestsThatAreNotUserFacingAreNeitherHeldNorRefused(string method)
    {
        var (adapter, deps, client) = await Served();

        client.ServerRequest(Id(6), method, "{}");

        Assert.Empty(client.Errors);
        Assert.Empty(client.Results);
        Assert.Empty(deps.Raised);
        Assert.Empty(deps.Unsupported);
        Assert.Empty(adapter.PendingRequests());
    }

    [Fact]
    public async Task AHeldRequestIsDroppedAndReportedWhenItsTurnEnds()
    {
        var (adapter, deps, client) = await Served();
        TurnStarted(client);
        client.ServerRequest(Id(7), "item/commandExecution/requestApproval", Command);

        TurnCompleted(client);

        Assert.Equal($"{Member}|7|the turn ended", Assert.Single(deps.Dropped));
        Assert.Empty(adapter.PendingRequests());
        Assert.Empty(client.Results);
    }

    [Fact]
    public async Task AHeldRequestIsDroppedAndReportedWhenTheMemberLeaves()
    {
        var (adapter, deps, client) = await Served();
        client.ServerRequest(Id(7), "item/commandExecution/requestApproval", Command);

        client.Close();

        Assert.Equal($"{Member}|7|the connection to the member ended", Assert.Single(deps.Dropped));
        Assert.Empty(adapter.PendingRequests());
    }

    [Fact]
    public async Task ARequestAnsweredBeforeTheTurnEndsIsNotReportedDropped()
    {
        var (adapter, deps, client) = await Served();
        TurnStarted(client);
        client.ServerRequest(Id(7), "item/commandExecution/requestApproval", Command);
        _ = await adapter.AnswerAsync(Member, "7", "accept", NoAnswers, null);

        TurnCompleted(client);

        Assert.Empty(deps.Dropped);
    }

    private const string Secret =
        "{\"threadId\":\"th-1\",\"questions\":[{\"id\":\"k\",\"header\":\"Key\",\"question\":\"Paste the API token hunter2\",\"isOther\":true,\"isSecret\":true}]}";

    [Fact]
    public async Task ASecretQuestionIsHeldWithNoTextAnywhereAhWritesAndIsNotAnswerable()
    {
        var (adapter, deps, client) = await Served();

        client.ServerRequest(Id(11), "item/tool/requestUserInput", Secret);
        var result = await adapter.AnswerAsync(Member, "11", null, [new("k", "x")], null);

        var raised = Assert.Single(deps.Raised);
        Assert.StartsWith($"{Member}|11|question|True|True|", raised);
        Assert.DoesNotContain("hunter2", raised);
        Assert.DoesNotContain("token", raised);
        Assert.All(deps.Logs, l => Assert.DoesNotContain("hunter2", l));
        Assert.Equal("secret", result.Status);
        Assert.Empty(client.Results);
    }

    [Fact]
    public async Task ADroppedSecretQuestionStaysSecretAndALateAnswerIsNeverSteeredIntoTheMember()
    {
        var (adapter, deps, client) = await Served();
        TurnStarted(client);
        client.ServerRequest(Id(11), "item/tool/requestUserInput", Secret);
        TurnCompleted(client);
        TurnStarted(client);

        var late = await adapter.AnswerAsync(Member, "11", null, [new("k", "hunter2-token-value")], null);

        Assert.Equal("secret", late.Status);
        Assert.DoesNotContain(client.Requests, r => r.Method == "turn/steer");
        Assert.All(deps.Logs, l => Assert.DoesNotContain("hunter2", l));
    }

    [Fact]
    public async Task AnIdOfBothANumberAndAStringRequestIsAmbiguousUnlessTheExactEntryIsNamed()
    {
        var (adapter, _, client) = await Served();
        client.ServerRequest(Id(12), "item/commandExecution/requestApproval", Command);
        client.ServerRequest("12", "item/commandExecution/requestApproval", Command);
        var keys = adapter.PendingRequests().Select(p => p.IdKey!).ToArray();

        var ambiguous = await adapter.AnswerAsync(Member, "12", "decline", NoAnswers, null);
        Assert.Empty(client.Results);
        var exact = await adapter.AnswerAsync(Member, "12", "decline", NoAnswers, null, "s:12");

        Assert.Equal(["n:12", "s:12"], keys);
        Assert.Equal("ambiguous", ambiguous.Status);
        Assert.Equal("answered", exact.Status);
        var (id, _) = Assert.Single(client.Results);
        Assert.True(id.IsText);
        Assert.Single(adapter.PendingRequests());
    }

    [Fact]
    public async Task AQuestionWithOneNonSecretAndOneSecretPartIsTreatedAsSecretWhole()
    {
        var (_, deps, client) = await Served();

        client.ServerRequest(
            Id(12),
            "item/tool/requestUserInput",
            "{\"questions\":[{\"id\":\"a\",\"header\":\"H\",\"question\":\"visible?\",\"isSecret\":false},{\"id\":\"b\",\"header\":\"S\",\"question\":\"hidden\",\"isSecret\":true}]}");

        var raised = Assert.Single(deps.Raised);
        Assert.DoesNotContain("visible", raised);
        Assert.DoesNotContain("hidden", raised);
    }

    [Fact]
    public async Task ANonBlockingQuestionIsRelayedAtOnceAsNonBlocking()
    {
        var (_, deps, client) = await Served();

        client.ServerRequest(Id(13), "item/tool/requestUserInput", "{\"isBlocking\":false,\"questions\":[{\"id\":\"a\",\"header\":\"H\",\"question\":\"q\",\"isSecret\":false}]}");

        Assert.StartsWith($"{Member}|13|question|False|False|", Assert.Single(deps.Raised));
    }

    [Fact]
    public async Task ALateQuestionAnswerIsDeliveredAsASteerAndALateApprovalIsNot()
    {
        var (adapter, _, client) = await Served();
        TurnStarted(client);
        client.ServerRequest(Id(14), "item/tool/requestUserInput", TwoQuestions);
        client.ServerRequest(Id(15), "item/commandExecution/requestApproval", Command);
        TurnCompleted(client);
        TurnStarted(client);

        var late = await adapter.AnswerAsync(Member, "14", null, [new("a", "red"), new("b", "large")], null);
        var lateApproval = await adapter.AnswerAsync(Member, "15", "accept", NoAnswers, null);

        Assert.Equal("steered", late.Status);
        Assert.Equal("unknown-request", lateApproval.Status);
        var steer = Assert.Single(client.Requests, r => r.Method == "turn/steer");
        var text = steer.Params!.Value.AsObject()["input"].AsArray()[0].AsObject()["text"].AsString();
        Assert.Contains("Answer to your earlier question Colour: red", text);
        Assert.Contains("Answer to your earlier question Size: large", text);
        Assert.Empty(client.Results);
    }

    private static void AgentSaid(ScriptedClient client, string text) =>
        client.Push("item/completed", JsonText.Parse($"{{\"threadId\":\"th-1\",\"item\":{{\"type\":\"agentMessage\",\"id\":\"m\",\"text\":{JsonText.Stringify(text, 0)}}}}}"));

    [Fact]
    public async Task ATurnEndCarriesTheTurnsFinalAgentMessageOnce()
    {
        var (_, deps, client) = await Served();
        TurnStarted(client);
        AgentSaid(client, "first");
        AgentSaid(client, "Which database should I use?");

        TurnCompleted(client);
        TurnCompleted(client);

        Assert.Equal(["Which database should I use?", null], deps.TurnEnds);
    }

    [Fact]
    public async Task ATurnWithNoAgentMessageEndsWithNullAndAnEarlierTurnsMessageDoesNotLeakIn()
    {
        var (_, deps, client) = await Served();
        TurnStarted(client);
        AgentSaid(client, "old");
        TurnCompleted(client);
        TurnStarted(client);

        TurnCompleted(client);

        Assert.Equal(["old", null], deps.TurnEnds);
    }

    [Fact]
    public async Task ALongFinalMessageIsCutAtTwoKilobytes()
    {
        var (_, deps, client) = await Served();
        TurnStarted(client);
        AgentSaid(client, new string('x', 5000));

        TurnCompleted(client);

        var last = Assert.Single(deps.TurnEnds)!;
        Assert.StartsWith(new string('x', 2048), last);
        Assert.True(last.Length < 2100);
    }
}
