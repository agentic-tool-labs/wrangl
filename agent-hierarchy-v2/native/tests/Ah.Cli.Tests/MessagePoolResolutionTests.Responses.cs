using Ah.Cli.Composition;
using Ah.Cli.Verbs.Msg;
using Ah.Commands;
using Ah.Core.Json;
using Ah.Mesh;

namespace Ah.Cli.Tests;

public sealed partial class MessagePoolResolutionTests
{
    private sealed class RecordingLinkEffects : ILinkEffects, ILinkMirror
    {
        internal List<bool> TargetContexts { get; } = [];

        internal List<string> LinkDirs { get; } = [];

        public Task<JsonObject> LinksAsync(string op, JsonObject args, string? opId, CancellationToken cancellation)
        {
            LinkDirs.Add(args.Get("dir")!.Value.AsString());
            return Task.FromResult(new JsonObject());
        }

        public Task<bool> CreateMessageAsync(string dir, string path, string content, string opId, CancellationToken cancellation, bool ownPoolContext = false)
        {
            TargetContexts.Add(ownPoolContext);
            return Task.FromResult(true);
        }

        public Task NotifyAsync(LinkNotice notice, CancellationToken cancellation) => Task.CompletedTask;

        public Task MirrorAsync(LinkMirror mirror, CancellationToken cancellation) => Task.CompletedTask;
    }

    private static string WriteLinkRequest(string pool, string? teamFile)
    {
        var old = WriteMessage(pool, teamFile, extra: "team: t\nsubject: link\nrequester: m@t\npeers: reviewer@t\nparent: 20261018-115959-task\n", store: false);
        var path = old.Replace("--implementor--", "--orchestrator--", StringComparison.Ordinal);
        File.WriteAllText(old, File.ReadAllText(old).Replace("to: implementor\nfrom: orchestrator", "to: orchestrator\nfrom: implementor", StringComparison.Ordinal));
        File.Move(old, path);
        Ah.TestKit.TestMsgStore.Store(path);
        return path;
    }

    private ServiceGraph RegisteredGraph()
    {
        var graph = Graph(routed: true);
        FakeRunner.RegisterSelf(graph.Mailbox, "o", TeamFile, "orchestrator@t");
        return graph;
    }

    private (int Code, string Out, string Error) Deliver(string request)
    {
        var runner = FakeRunner.KnowingSelf();
        runner.Output = argv => RosterPaneVerbTests.Herdr(argv, "idle").Stdout;
        var graph = Graph(routed: true, runner: runner);
        CommandsModule.ComposeRoster(() => new RosterPaneVerbTests.Services(graph, graph.Io.Files, graph.Io.Clock, graph.Environment, Main));
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = _sandbox.Mirrored(() => CommandsModule.Registry.Verbs.Single(v => v.Name == "roster").Run(
            ["deliver", "m", "--req", request, "--cwd", Main, "--team", "t"], stdout, stderr));
        return (code, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public void DeliverFromMainCreatesTheResponseInTheRecordedWorktreePool()
    {
        var request = WriteMessage(Legacy, TeamFile);

        var result = Deliver(request);

        Assert.True(result.Code == 0, result.Error);
        Assert.True(JsonText.Parse(result.Out).Member("sent")!.Value.AsBool(), result.Out);
        Assert.Single(Directory.GetFiles(Legacy, "*--response.md", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(Home, "*--response.md", SearchOption.AllDirectories));
    }

    [Fact]
    public void DeliverRefusesARequestPlantedOutsideItsTeamsFamilyWithoutWriting()
    {
        var request = WriteMessage(OtherPool, TeamFile);

        var result = Deliver(request);

        Assert.Contains("outside the request's hierarchy directories", result.Out + result.Error, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(OtherPool, "*--response.md", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(Home, "*--response.md", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("grant")]
    [InlineData("deny")]
    public void LinkDecisionWritesBesideTheLegacyRequestAndKeepsTheLinkStoreAtHome(string decision)
    {
        WriteLinkRequest(Legacy, TeamFile);
        var graph = RegisteredGraph();
        var context = new CliContext(graph.Environment, new RecordingExec(), FakeRunner.KnowingSelf(), "ahd",
            _sandbox.Executable("install/ahd"), _sandbox.Home, 501, null, new Lazy<ServiceGraph>(graph));
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var code = _sandbox.Mirrored(() => MsgVerb.Run(context, ["link", decision, Id, "--team", "t", "--session", "o", "--why", "test", "--cwd", Main, "--json"], stdout, stderr));

        Assert.True(code == 0, stderr.ToString());
        Assert.Single(Directory.GetFiles(Legacy, "*--response.md", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(Home, "*--response.md", SearchOption.AllDirectories));
        if (decision == "grant")
        {
            Assert.NotNull(Ah.TestKit.TestLinks.Text(graph.Environment.AhHome, Home, "t"));
            Assert.Null(Ah.TestKit.TestLinks.Text(graph.Environment.AhHome, Legacy, "t"));
        }
    }

    [Fact]
    public void MsgNewCanAnswerAGeneratedTeamlessRequestFromItsOwnCwd()
    {
        var graph = RegisteredGraph();
        FakeRunner.RegisterSelf(graph.Mailbox, "m", TeamFile, "m@t");
        var request = graph.Hierarchy.Messages.CreateMessage(Home, new JsonObject
        {
            ["type"] = "request", ["id"] = Id, ["to"] = "implementor", ["from"] = "orchestrator", ["slug"] = "no-team",
        }).Member("path")!.Value.AsString();
        Assert.Equal("no-team", graph.Hierarchy.Messages.ResponsePool(request).Get("status")!.Value.AsString());
        var context = new CliContext(graph.Environment, new RecordingExec(), FakeRunner.KnowingSelf(), "ahd",
            _sandbox.Executable("install/ahd"), _sandbox.Home, 501, null, new Lazy<ServiceGraph>(graph));
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var code = _sandbox.Mirrored(() => MsgVerb.Run(context, ["new", "--type", "response", "--id", Id, "--req", request, "--from", "implementor", "--team", "t", "--session", "m", "--cwd", Main], stdout, stderr));

        Assert.True(code == 0, stderr.ToString());
        Assert.Equal(Path.Combine(Home, "msgs"), Path.GetDirectoryName(PathOf(stdout.ToString(), "response")));
        Assert.Single(Directory.GetFiles(Home, "*--response.md", SearchOption.AllDirectories));
    }

    private (int Code, string Out, string Error) Msg(ServiceGraph graph, params string[] args)
    {
        var context = new CliContext(graph.Environment, new RecordingExec(), FakeRunner.KnowingSelf(), "ahd",
            _sandbox.Executable("install/ahd"), _sandbox.Home, 501, null, new Lazy<ServiceGraph>(graph));
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = _sandbox.Mirrored(() => MsgVerb.Run(context, args, stdout, stderr));
        return (code, stdout.ToString(), stderr.ToString());
    }

    private ServiceGraph WorktreeGraph()
    {
        var graph = Graph(Legacy, routed: true);
        FakeRunner.RegisterSelf(graph.Mailbox, "o", TeamFile, "orchestrator@t");
        return graph;
    }

    /// <summary>The file of the message whose id a <c>msg new --json</c> output names.</summary>
    private string PathOf(string json, string type = "request") => _sandbox.MsgFile(JsonText.Parse(json).Member("id")!.Value.AsString(), type);

    private static string Real(string path) => path.StartsWith("/private/", StringComparison.Ordinal) ? path[8..] : path;

    private string NewRequestTo(string toName, string cwd, params string[] more)
    {
        var graph = cwd == Worktree ? WorktreeGraph() : RegisteredGraph();
        var result = Msg(graph, ["new", "--type", "request", "--to", "implementor", "--to-name", toName, "--from", "orchestrator", "--slug", "s", "--title", "Fix test titles", "--session", "o", "--cwd", cwd, "--json", .. more]);
        Assert.True(result.Code == 0, result.Error);
        return PathOf(result.Out);
    }

    [Fact]
    public void ASecondRequestWhileTheAccountIsAt99PercentGoesOutUnchanged()
    {
        var tap = Path.Combine(_sandbox.Home, ".claude", "ah2", "statusline");
        _ = Directory.CreateDirectory(tap);
        File.WriteAllText(Path.Combine(tap, "s1.json"), "{\"session_id\":\"s1\",\"rate_limits\":{\"seven_day\":{\"used_percentage\":99.5,\"resets_at\":" + DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds() + "}}}");

        _ = NewRequestTo("m", Main, "--team", "t");
        var second = NewRequestTo("m", Main, "--team", "t");

        Assert.DoesNotContain("Usage is at", File.ReadAllText(second), StringComparison.Ordinal);
    }

    [Fact]
    public void ARequestToAMemberRootedInAWorktreeIsFiledInTheTeamHomeAndDeliverAcceptsIt()
    {
        var request = NewRequestTo("m", Main, "--team", "t");

        Assert.Equal(Path.Combine(Home, "msgs"), Real(Path.GetDirectoryName(request)!));
        Assert.Empty(Directory.GetFiles(Legacy, "*--request.md", SearchOption.AllDirectories));
        var delivered = Deliver(request);
        Assert.True(delivered.Code == 0, delivered.Error);
        Assert.Single(Directory.GetFiles(Home, "*--response.md", SearchOption.AllDirectories));
    }

    [Fact]
    public void ARequestNamingATeamWithNoTeamFileStaysInTheCallersOwnPool()
    {
        var graph = WorktreeGraph();

        var result = Msg(graph, "new", "--type", "request", "--to", "implementor", "--team", "nosuch", "--from", "orchestrator", "--slug", "s", "--title", "Fix test titles", "--session", "o", "--cwd", Worktree, "--json");

        Assert.True(result.Code == 0, result.Error);
        Assert.Equal(Path.Combine(Legacy, "msgs"), Real(Path.GetDirectoryName(PathOf(result.Out))!));
    }

    [Fact]
    public void ARequestNamingNoMemberAndNoTeamFromAMembersWorktreeStaysInTheCallersPoolWithNoNote()
    {
        var graph = Graph(routed: true);
        FakeRunner.RegisterSelf(graph.Mailbox, "m", TeamFile, "implementor@t");

        var result = Msg(graph, "new", "--type", "request", "--to", "orchestrator", "--to-name", "nobody", "--from", "implementor", "--slug", "s", "--title", "Fix test titles", "--session", "m", "--cwd", Worktree, "--json");

        Assert.True(result.Code == 0, result.Error);
        Assert.Equal(Path.Combine(Legacy, "msgs"), Real(Path.GetDirectoryName(PathOf(result.Out))!));
        Assert.DoesNotContain("is a member of a Team", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void ARequestNamingNoMemberFromAMembersWorktreeIsStillFiledInTheTeamHome()
    {
        var graph = Graph(routed: true);
        FakeRunner.RegisterSelf(graph.Mailbox, "m", TeamFile, "implementor@t");

        var result = Msg(graph, "new", "--type", "request", "--to", "orchestrator", "--to-name", "nobody", "--from", "implementor", "--team", "t", "--slug", "s", "--session", "m", "--cwd", Worktree, "--json");

        Assert.True(result.Code == 0, result.Error);
        Assert.Equal(Path.Combine(Home, "msgs"), Real(Path.GetDirectoryName(PathOf(result.Out))!));
    }

    [Fact]
    public void ASendAcceptsTheRefThatMsgNewJustWroteForAWorktreeMember()
    {
        var request = NewRequestTo("m", Main, "--team", "t");
        var graph = RegisteredGraph();
        FakeRunner.RegisterSelf(graph.Mailbox, "m", TeamFile, "m@t");

        var sent = Msg(graph, "send", "m@t", "--kind", "brief", "--from", "orchestrator@t", "--session", "o", "--ref", request, "--id", "e-1", "--cwd", Main, "--json");

        Assert.True(sent.Code == 0, sent.Error + sent.Out);
    }

    [Fact]
    public void AResponseFiledFromTheWorktreeWithoutReqLandsBesideTheRequestInTheTeamHome()
    {
        var request = NewRequestTo("m", Main, "--team", "t");
        var id = Path.GetFileName(request).Split("--")[0];
        var graph = Graph(routed: true);
        FakeRunner.RegisterSelf(graph.Mailbox, "m", TeamFile, "m@t");

        var result = Msg(graph, "new", "--type", "response", "--id", id, "--team", "t", "--session", "m", "--cwd", Worktree, "--json");

        Assert.True(result.Code == 0, result.Error);
        Assert.Equal(Path.Combine(Home, "msgs"), Real(Path.GetDirectoryName(PathOf(result.Out, "response"))!));
        Assert.Empty(Directory.GetFiles(Legacy, "*--response.md", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("allowed", "grant")]
    [InlineData("allowed", "deny")]
    [InlineData("no-team", "grant")]
    [InlineData("no-team", "deny")]
    [InlineData("planted", "grant")]
    [InlineData("planted", "deny")]
    public async Task LinkResponsesPassTheVerifiedContextChoiceToTheirEffect(string scenario, string decision)
    {
        WriteLinkRequest(scenario == "planted" ? OtherPool : Legacy, scenario == "no-team" ? null : TeamFile);
        var graph = RegisteredGraph();
        var effects = new RecordingLinkEffects();
        var commands = MeshModule.CreateLinkCommands(MeshModule.CreateMeshRule(graph.Links, graph.Environment), graph.Links,
            graph.Hierarchy.Core, graph.Hierarchy.Messages, graph.Io.Files, graph.Io.Clock, effects, effects);
        var cwd = scenario == "planted" ? Other : Main;
        var context = graph.Callers.RequestContext(cwd);

        var command = new LinkCommandRequest("link", [decision, Id],
            new Dictionary<string, string> { ["team"] = "t", ["why"] = "test" },
            new LinkContext(context.HierarchyDirectory, context.MainHierarchyDirectory), graph.Mailbox.TeamLookup(context), ["orchestrator@t"]);

        if (scenario == "planted")
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => commands.RunAsync(command, CancellationToken.None));
            Assert.Contains("outside the request's hierarchy directories", error.Message, StringComparison.Ordinal);
            Assert.Empty(effects.TargetContexts);
            Assert.Empty(effects.LinkDirs);
        }
        else
        {
            var result = await commands.RunAsync(command, CancellationToken.None);
            Assert.True(result.Failure is null, result.Failure?.Message);
            Assert.Equal(scenario == "allowed", Assert.Single(effects.TargetContexts));
            if (decision == "grant")
            {
                Assert.Equal(Home, Assert.Single(effects.LinkDirs));
            }
        }
    }

    [Fact]
    public void ALinkDecisionSkipsAHomeRequestWithoutALinkSubjectAndAnswersTheSameIdInTheRecordedPool()
    {
        WriteMessage(Home, TeamFile);
        WriteLinkRequest(Legacy, TeamFile);
        var graph = RegisteredGraph();
        var context = new CliContext(graph.Environment, new RecordingExec(), FakeRunner.KnowingSelf(), "ahd",
            _sandbox.Executable("install/ahd"), _sandbox.Home, 501, null, new Lazy<ServiceGraph>(graph));
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var code = _sandbox.Mirrored(() => MsgVerb.Run(context, ["link", "deny", Id, "--team", "t", "--session", "o", "--why", "test", "--cwd", Worktree, "--json"], stdout, stderr));

        Assert.True(code == 0, stderr.ToString());
        Assert.Single(Directory.GetFiles(Legacy, "*--response.md", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(Home, "*--response.md", SearchOption.AllDirectories));
    }
}
