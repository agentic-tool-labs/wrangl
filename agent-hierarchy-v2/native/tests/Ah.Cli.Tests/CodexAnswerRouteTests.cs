using System.Runtime.Versioning;
using System.Text;
using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Codex;
using Ah.Core.Json;
using Ah.Mesh;
using Ah.Platform;
using Ah.TestKit;

namespace Ah.Cli.Tests;

/// <summary>
/// <c>POST /v1/codex/answer</c> end to end but for the process tree: who may call it, what the Orchestrator may grant, and that every
/// Orchestrator grant is on record before it is sent.
/// </summary>
[UnsupportedOSPlatform("windows")]
public sealed class CodexAnswerRouteTests : IDisposable
{
    private readonly Sandbox _sandbox = new();
    private readonly string _tree;
    private readonly string _outside;
    private readonly string _hierarchy;
    private readonly ServiceGraph _graph;
    private readonly FakeRelay _relay;
    private CallerPrincipal _caller = CallerPrincipal.Anonymous;

    public CodexAnswerRouteTests()
    {
        _tree = Path.Combine(_sandbox.Root, "tree");
        _outside = Path.Combine(_sandbox.Root, "elsewhere");
        _ = Directory.CreateDirectory(_tree);
        _ = Directory.CreateDirectory(_outside);
        _hierarchy = Path.Combine(_sandbox.Root, "proj", ".claude", "ah2");
        _ = Directory.CreateDirectory(Path.Combine(_hierarchy, "teams"));
        _ = Directory.CreateDirectory(Path.Combine(_hierarchy, "grants"));
        File.WriteAllText(Path.Combine(_hierarchy, "grants", "proj.json"), "{\"mode\":\"enforce\",\"members\":{\"alpha\":{\"work_root\":\"" + _tree + "\"}}}");
        Ah.TestKit.TestImport.Planted(_sandbox.AhHome, [Path.Combine(_hierarchy, "grants", "proj.json")]);
        var variables = new Dictionary<string, string?> { ["AH_TEST"] = "1", ["AH_HOME"] = _sandbox.AhHome, ["HOME"] = _sandbox.Home };
        var env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables, new PluginInfo(_sandbox.PluginRoot, "wrangl", "1.0.0", "1"));
        _graph = ServiceGraph.Build(PlatformModule.CreateIo(null), env, new FakeRunner(), new FakeDaemonClient());
        var member = new CodexMember("proj.alpha", "proj", "alpha", "alpha@proj", Path.Combine(_hierarchy, "teams", "proj.json"), 1, "/s", "th", new JsonObject());
        _relay = new FakeRelay(member, GrantLogPath);
    }

    public void Dispose() => _sandbox.Dispose();

    private string GrantLogPath => Path.Combine(_sandbox.AhHome, CodexAnswerRoute.GrantLogName);

    private sealed class FakeRelay(CodexMember member, string logPath) : ICodexRelay
    {
        internal List<CodexPending> Held { get; } = [];

        internal List<(string Key, string RequestId, string? Choice)> Answered { get; } = [];

        internal List<string> LogAtAnswer { get; } = [];

        internal List<string?> IdKeys { get; } = [];

        internal CodexAnswerResult Result { get; set; } = new("answered");

        public IReadOnlyList<CodexPending> Pending() => Held;

        public CodexMember? MemberOf(string memberKey) => memberKey == member.Key ? member : null;

        public Task<CodexAnswerResult> AnswerAsync(string memberKey, string requestId, string? choice, IReadOnlyList<KeyValuePair<string, string>> answers, string? text, string? idKey = null)
        {
            Answered.Add((memberKey, requestId, choice));
            IdKeys.Add(idKey);
            LogAtAnswer.Add(File.Exists(logPath) ? File.ReadAllText(logPath) : string.Empty);
            return Task.FromResult(Result);
        }
    }

    private CodexPending Command(string? cwd = null, string idKey = "n:7") =>
        new("proj.alpha", "7", "command-approval", "item/commandExecution/requestApproval", "{\"command\":\"make\",\"cwd\":\"x\"}", false, true, ["accept", "acceptForSession", "decline", "cancel"], "i", cwd ?? _tree, IdKey: idKey);

    private static string BodyOf(string team, string member) =>
        "{\"member\":\"" + member + "\",\"team\":\"" + team + "\",\"request_id\":\"7\",\"choice\":\"decline\"}";

    private static CallerPrincipal Session(params string[] addresses) =>
        new(PrincipalKind.Session, [new SessionRegistration("s1", 100, "start", addresses)]);

    private async Task<(int Status, JsonObject Body)> Call(string choice, string? session = null)
    {
        var body = new JsonObject { ["member"] = "alpha", ["team"] = "proj", ["request_id"] = "7", ["choice"] = choice };
        if (session is not null)
        {
            body["session_id"] = session;
        }

        var request = new RouteRequest("POST", "/v1/codex/answer", string.Empty, new Dictionary<string, string>(), Encoding.UTF8.GetBytes(JsonText.Stringify(body, 0)), new Dictionary<string, string>(), 4242);
        var response = await new CodexAnswerRoute(_graph, _relay, _ => _caller).HandleAsync(request, TestContext.Current.CancellationToken);
        return (response.Status, JsonText.Parse(response.Body.ToArray()).AsObject());
    }

    private string[] LogLines() => File.Exists(GrantLogPath) ? File.ReadAllLines(GrantLogPath) : [];

    [Fact]
    public async Task TheOrchestratorsInBoundsAcceptIsSentAndWasOnRecordBeforeItWasSent()
    {
        _relay.Held.Add(Command());
        _caller = Session("orchestrator@proj");

        var (status, body) = await Call("accept");

        Assert.Equal(200, status);
        Assert.Equal("answered", body["status"].AsString());
        Assert.Equal(("proj.alpha", "7", "accept"), Assert.Single(_relay.Answered));
        var row = JsonText.Parse(Assert.Single(LogLines())).AsObject();
        Assert.Equal("orchestrator", row["by"].AsString());
        Assert.Equal("accept", row["choice"].AsString());
        Assert.Equal("make", row["command"].AsString());
        Assert.Contains("\"by\":\"orchestrator\"", Assert.Single(_relay.LogAtAnswer));
        Assert.Equal("orchestrator", body["grant"].AsObject()["by"].AsString());
    }

    [Fact]
    public async Task AnAcceptOutsideTheWorkTreeNeedsTheUserAndSendsAndLogsNothing()
    {
        _relay.Held.Add(Command(_outside));
        _caller = Session("orchestrator@proj");

        var (_, body) = await Call("accept");

        Assert.Equal("needs-user", body["status"].AsString());
        Assert.Empty(_relay.Answered);
        Assert.Empty(LogLines());
    }

    [Fact]
    public async Task ASessionThatIsNotTheTeamsOrchestratorIsRefusedAndNothingIsSent()
    {
        _relay.Held.Add(Command());
        _caller = Session("alpha@proj");

        var (status, body) = await Call("accept");

        Assert.Equal(403, status);
        Assert.Equal("not-team-owner", body["error"].AsObject()["code"].AsString());
        Assert.Empty(_relay.Answered);
        Assert.Empty(LogLines());
    }

    [Fact]
    public async Task AnotherTeamsOrchestratorIsRefusedToo()
    {
        _relay.Held.Add(Command());
        _caller = Session("orchestrator@other");

        var (status, body) = await Call("decline");

        Assert.Equal(403, status);
        Assert.Equal("not-team-owner", body["error"].AsObject()["code"].AsString());
        Assert.Empty(_relay.Answered);
    }

    [Fact]
    public async Task ACallerTheDaemonCannotIdentifyIsRefused()
    {
        _relay.Held.Add(Command());

        var (status, body) = await Call("decline");

        Assert.Equal(403, status);
        Assert.Equal("anonymous", body["error"].AsObject()["code"].AsString());
        Assert.Empty(_relay.Answered);
    }

    [Fact]
    public async Task TheUsersChoiceIsSentWhateverTheBoundsAndIsLoggedAsTheUsers()
    {
        _relay.Held.Add(Command(_outside));
        _caller = CallerPrincipal.User;

        var (status, body) = await Call("acceptForSession");

        Assert.Equal(200, status);
        Assert.Equal("answered", body["status"].AsString());
        Assert.Equal(("proj.alpha", "7", "acceptForSession"), Assert.Single(_relay.Answered));
        var row = JsonText.Parse(Assert.Single(LogLines())).AsObject();
        Assert.Equal("user", row["by"].AsString());
    }

    [Fact]
    public async Task TheOrchestratorMayAlwaysDeclineAndItIsLogged()
    {
        _relay.Held.Add(Command(_outside));
        _caller = Session("orchestrator@proj");

        var (_, body) = await Call("decline");

        Assert.Equal("answered", body["status"].AsString());
        Assert.Equal("decline", JsonText.Parse(Assert.Single(LogLines())).AsObject()["choice"].AsString());
    }

    [Fact]
    public async Task IfTheGrantCannotBeLoggedNothingIsGrantedAndTheUserDecides()
    {
        _relay.Held.Add(Command());
        _caller = Session("orchestrator@proj");
        _ = Directory.CreateDirectory(GrantLogPath);

        var (_, body) = await Call("accept");

        Assert.Equal("needs-user", body["status"].AsString());
        Assert.NotNull(body.Get("log_error"));
        Assert.Empty(_relay.Answered);
    }

    [Fact]
    public async Task AGrantTheMemberNeverGotLeavesACorrectionRowBehindTheFirst()
    {
        _relay.Held.Add(Command());
        _relay.Result = new CodexAnswerResult("disconnected");
        _caller = Session("orchestrator@proj");

        var (_, body) = await Call("accept");

        Assert.Equal("disconnected", body["status"].AsString());
        var rows = LogLines().Select(l => JsonText.Parse(l).AsObject()).ToArray();
        Assert.Equal(2, rows.Length);
        Assert.Null(rows[0].Get("not_sent"));
        Assert.Equal("disconnected", rows[1]["not_sent"].AsString());
    }

    [Fact]
    public async Task WithTheStandingApprovalOffTheOrchestratorsAcceptNeedsTheUserButItsDeclineStillGoes()
    {
        _relay.Held.Add(Command());
        _caller = Session("orchestrator@proj");
        Directory.CreateDirectory(Path.GetDirectoryName(_graph.Config.UserConfigPath())!);
        File.WriteAllText(_graph.Config.UserConfigPath(), "{\"codex_orchestrator_approvals\":false}");

        var accept = await Call("accept");
        var decline = await Call("decline");

        Assert.Equal("needs-user", accept.Body["status"].AsString());
        Assert.Equal("answered", decline.Body["status"].AsString());
    }

    [Fact]
    public async Task AnUnreadableUserConfigAlsoSendsTheAcceptToTheUser()
    {
        _relay.Held.Add(Command());
        _caller = Session("orchestrator@proj");
        Directory.CreateDirectory(Path.GetDirectoryName(_graph.Config.UserConfigPath())!);
        File.WriteAllText(_graph.Config.UserConfigPath(), "{not json");

        var (_, body) = await Call("accept");

        Assert.Equal("needs-user", body["status"].AsString());
        Assert.Empty(_relay.Answered);
    }

    private async Task<(int Status, JsonObject Body)> Post(string json)
    {
        var request = new RouteRequest("POST", "/v1/codex/answer", string.Empty, new Dictionary<string, string>(), Encoding.UTF8.GetBytes(json), new Dictionary<string, string>(), 4242);
        var response = await new CodexAnswerRoute(_graph, _relay, _ => _caller).HandleAsync(request, TestContext.Current.CancellationToken);
        return (response.Status, JsonText.Parse(response.Body.ToArray()).AsObject());
    }

    [Fact]
    public async Task AnAmbiguousGoneIdIsHandedToTheUserNotReportedAsAnAnswer()
    {
        _relay.Result = new("ambiguous", "both a number and a string request carry this id");
        _caller = Session("orchestrator@proj");

        var (_, body) = await Call("decline");

        Assert.Equal("needs-user", body["status"].AsString());
        Assert.Equal("both a number and a string request carry this id", body["reason"].AsString());
    }

    [Fact]
    public async Task AGoneIdThatIsNotAmbiguousStillReportsItsOwnStatus()
    {
        _relay.Result = new("unknown-request");
        _caller = Session("orchestrator@proj");

        var (_, body) = await Call("decline");

        Assert.Equal("unknown-request", body["status"].AsString());
    }

    [Fact]
    public async Task AnIdCarriedByBothANumberAndAStringRequestIsNeverGuessedAndNothingIsAnswered()
    {
        _relay.Held.Add(Command(idKey: "n:7"));
        _relay.Held.Add(Command(idKey: "s:7"));
        _caller = Session("orchestrator@proj");

        var (_, body) = await Call("accept");

        Assert.Equal("needs-user", body["status"].AsString());
        Assert.Empty(_relay.Answered);
        Assert.Empty(LogLines());
    }

    [Fact]
    public async Task TheRouteAnswersExactlyTheEntryItJudged()
    {
        _relay.Held.Add(Command(idKey: "s:7"));
        _caller = Session("orchestrator@proj");

        _ = await Call("accept");

        Assert.Equal("s:7", Assert.Single(_relay.IdKeys));
    }

    [Theory]
    [InlineData("a.b", "alpha")]
    [InlineData("proj", "al.pha")]
    public async Task ATeamOrMemberNameWithADotIsRefusedBeforeAnythingIsLooked(string team, string member)
    {
        _caller = CallerPrincipal.User;

        var (status, _) = await Post(BodyOf(team, member));

        Assert.Equal(400, status);
        Assert.Empty(_relay.Answered);
    }

    [Fact]
    public async Task TheGrantLogIsCreatedPrivateAndAnExistingWiderOneIsNarrowed()
    {
        _relay.Held.Add(Command());
        _caller = Session("orchestrator@proj");

        _ = await Call("decline");
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(GrantLogPath));

        File.SetUnixFileMode(GrantLogPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        _ = await Call("decline");

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(GrantLogPath));
        Assert.Equal(2, LogLines().Length);
    }

    [Fact]
    public async Task AMemberWithNoWorkTreeRecordNeedsTheUser()
    {
        _relay.Held.Add(Command());
        _caller = Session("orchestrator@proj");
        Ah.TestKit.TestGrants.Delete(_sandbox.AhHome, _hierarchy, "proj");

        var (_, body) = await Call("accept");

        Assert.Equal("needs-user", body["status"].AsString());
        Assert.Empty(_relay.Answered);
    }
}
