using System.Globalization;
using System.Text;
using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Commands;
using Ah.Config;
using Ah.Core.Json;
using Ah.Hierarchy;
using Ah.Launch;
using Ah.Platform;
using Ah.Roster;
using Ah.TestKit;

namespace Ah.Cli.Tests;

/// <summary><c>roster answer --request</c>: a thin client of the daemon route, which decides who may make which choice.</summary>
[Collection("RosterServices")]
public sealed class RosterAnswerRequestTests : IDisposable
{
    private readonly Sandbox _sandbox = new();
    private readonly FakeAnswers _answers = new();

    public RosterAnswerRequestTests()
    {
        Directory.CreateDirectory(Path.Combine(Project, ".git"));
        Directory.CreateDirectory(Path.Combine(Project, ".claude", "ah2", "teams"));
        File.WriteAllText(
            Path.Combine(Project, ".claude", "ah2", "teams", "proj.json"),
            "{\"version\":1,\"team_id\":\"t1\",\"transport\":\"herdr\",\"orchestrator\":{\"session_id\":null,\"pid\":" + Environment.ProcessId + "},\"members\":[{\"name\":\"alpha\",\"role\":\"implementor\",\"kind\":\"codex\"}]}");
    }

    private string Project => Path.Combine(_sandbox.Root, "proj");

    public void Dispose()
    {
        CommandsModule.ComposeRoster(() => null!);
        _sandbox.Dispose();
    }

    private sealed class FakeAnswers : IRosterCodexAnswers
    {
        internal List<(string Team, string Member, string RequestId, string? Choice, string Answers, string? Text)> Calls { get; } = [];

        internal CodexAnswerReply Reply { get; set; } = new(new JsonObject { ["status"] = "answered" }, null);

        public CodexAnswerReply Answer(string team, string member, string requestId, string? choice, IReadOnlyList<KeyValuePair<string, string>> answers, string? text)
        {
            Calls.Add((team, member, requestId, choice, string.Join("|", answers.Select(a => $"{a.Key}={a.Value}")), text));
            return Reply;
        }
    }

    private sealed class Services(ServiceGraph graph, IFileReader files, IClock clock, IAhEnvironment environment, string cwd, IRosterCodexAnswers answers) : IRosterCliServices
    {
        public IConfigCore Config => graph.Config;

        public IRosterCore Roster => graph.Roster;

        public HierarchyServices Hierarchy => graph.Hierarchy;

        public IAhEnvironment Environment => environment;

        public IClock Clock => clock;

        public IFileReader Files => files;

        public string CurrentDirectory => cwd;

        public int ParentPid => 4242;

        public ITeardown? Teardown => graph.Launch.Teardown;

        public IRosterLaunch? Launch => graph.Launch;

        public IRosterCallerRegistry? Callers => null;

        public IRosterCodexAnswers? CodexAnswers => answers;
    }

    private (int Code, string Stdout, string Stderr) Roster(params string[] args)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        var variables = new Dictionary<string, string?>
        {
            ["AH_TEST"] = "1",
            ["AH_HOME"] = _sandbox.AhHome,
            ["HOME"] = _sandbox.Home,
            ["PATH"] = _sandbox.Bin,
            ["WRANGL_HIERARCHY_DIR"] = Path.Combine(Project, ".claude", "ah2"),
        };
        var env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables, new PluginInfo(_sandbox.PluginRoot, "wrangl", "1.2.3", "1"));
        var io = PlatformModule.CreateIo(null);
        var graph = ServiceGraph.Build(io, env, new FakeRunner(), new FakeDaemonClient());
        var services = new Services(graph, io.Files, io.Clock, env, Project, _answers);
        CommandsModule.ComposeRoster(() => services);
        var verb = CommandsModule.Registry.Verbs.Single(v => v.Name == "roster");
        var stdout = new StringWriter(CultureInfo.InvariantCulture);
        var stderr = new StringWriter(CultureInfo.InvariantCulture);
        var code = verb.Run([.. args, "--cwd", Project, "--team", "proj"], stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private static JsonObject Parse(string text) => JsonText.Parse(Encoding.UTF8.GetBytes(text)).AsObject();

    [Fact]
    public void AnApprovalChoiceGoesToTheDaemonWithTheTeamTheMemberAndTheRequest()
    {
        var run = Roster("answer", "alpha", "--request", "7", "--choice", "decline");

        Assert.Equal(0, run.Code);
        var call = Assert.Single(_answers.Calls);
        Assert.Equal(("proj", "alpha", "7", "decline", string.Empty, null), call);
        var output = Parse(run.Stdout);
        Assert.Equal("answered", output["status"].AsString());
        Assert.Equal("alpha", output["name"].AsString());
    }

    [Fact]
    public void QuestionAnswersAreSplitAtTheFirstEqualsAndKeepEverythingAfterIt()
    {
        var run = Roster("answer", "alpha", "--request", "9", "--answer", "a=red", "--answer", "b=x=y", "--text", "hello");

        Assert.Equal(0, run.Code);
        var call = Assert.Single(_answers.Calls);
        Assert.Equal("a=red|b=x=y", call.Answers);
        Assert.Null(call.Choice);
        Assert.Equal("hello", call.Text);
    }

    [Fact]
    public void AReplyOfNeedsUserCarriesTheReasonAndTheRelayRule()
    {
        _answers.Reply = new CodexAnswerReply(new JsonObject { ["status"] = "needs-user", ["reason"] = "outside the tree" }, null);

        var run = Roster("answer", "alpha", "--request", "7", "--choice", "accept");

        var output = Parse(run.Stdout);
        Assert.Equal("needs-user", output["status"].AsString());
        Assert.Equal("outside the tree", output["reason"].AsString());
        Assert.Contains("never answer Yes or Allow for a member on your own", output["message"].AsString());
        Assert.Contains("codex_orchestrator_approvals", output["message"].AsString());
    }

    [Fact]
    public void AnAnsweredReplyCarriesNoRelayRule()
    {
        var run = Roster("answer", "alpha", "--request", "7", "--choice", "decline");

        Assert.False(Parse(run.Stdout).ContainsKey("message"));
    }

    [Theory]
    [InlineData("noChoiceOrAnswer")]
    [InlineData("answerWithoutEquals")]
    [InlineData("answerWithEmptyId")]
    [InlineData("withCancel")]
    [InlineData("withPrompt")]
    [InlineData("noMember")]
    [InlineData("noRequestId")]
    public void AMalformedCallFailsAndNeverReachesTheDaemon(string which)
    {
        string[] args = which switch
        {
            "noChoiceOrAnswer" => ["answer", "alpha", "--request", "7"],
            "answerWithoutEquals" => ["answer", "alpha", "--request", "7", "--answer", "red"],
            "answerWithEmptyId" => ["answer", "alpha", "--request", "7", "--answer", "=red"],
            "withCancel" => ["answer", "alpha", "--request", "7", "--choice", "decline", "--cancel"],
            "withPrompt" => ["answer", "alpha", "--request", "7", "--choice", "decline", "--prompt", "codex-trust"],
            "noMember" => ["answer", "--request", "7", "--choice", "decline"],
            _ => ["answer", "alpha", "--request", "--choice", "decline"],
        };

        var run = Roster(args);

        Assert.NotEqual(0, run.Code);
        Assert.Empty(_answers.Calls);
    }

    [Fact]
    public void UserChoiceBelongsToTheTrustScreenAndIsRefusedByNameWithARequest()
    {
        var run = Roster("answer", "alpha", "--request", "7", "--choice", "decline", "--user-choice");

        Assert.Equal(2, run.Code);
        Assert.Contains("answer: --request cannot be combined with --user-choice", run.Stderr, StringComparison.Ordinal);
        Assert.Empty(_answers.Calls);
    }

    [Fact]
    public void ADaemonThatCannotBeReachedFailsTheVerbAndSaysWhy()
    {
        _answers.Reply = new CodexAnswerReply(null, "daemon-unavailable: down");

        var run = Roster("answer", "alpha", "--request", "7", "--choice", "decline");

        Assert.NotEqual(0, run.Code);
        Assert.Contains("daemon-unavailable", run.Stderr + run.Stdout);
    }
}
