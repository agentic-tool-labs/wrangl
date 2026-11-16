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

/// <summary>
/// <c>deliver</c> and <c>answer</c> over the real libraries, a sandbox repository with one pane member, and a scripted herdr:
/// every refusal beside the nearest accepted input, and that nothing is typed into a member that is working, blocked or gone.
/// </summary>
[Collection("RosterServices")]
public sealed class RosterPaneVerbTests : IDisposable
{
    private const string Member = "proj-pi";
    private const string Request = "20261011-120000-abcd--implementor--task-one--request.md";

    private readonly Sandbox _sandbox = Sandbox.PlantedFiles();
    private readonly Scripted _runner = new();

    public RosterPaneVerbTests()
    {
        Directory.CreateDirectory(Path.Combine(Project, ".git"));
        Directory.CreateDirectory(Path.Combine(Project, ".claude", "ah2", "teams"));
        _sandbox.Executable("bin/herdr");
    }

    private string? _session = "sess-test";

    private string Project => Path.Combine(_sandbox.Root, "proj");

    private string MsgsDir => Path.Combine(Project, ".claude", "ah2", "msgs");

    private string RequestPath => Path.Combine(MsgsDir, Request);

    public void Dispose()
    {
        CommandsModule.ComposeRoster(() => null!);
        _sandbox.Dispose();
    }

    private sealed class Scripted : IProcessRunner
    {
        internal List<string[]> Calls { get; } = [];

        internal Func<string[], (int Exit, string Stdout)> Script { get; set; } = _ => (0, string.Empty);

        public ProcessResult Run(ProcessRequest request)
        {
            var argv = request.Argv.ToArray();
            Calls.Add(argv);
            var (exit, stdout) = Script(argv);
            return new ProcessResult(exit, null, false, false, Encoding.UTF8.GetBytes(stdout), default);
        }
    }

    internal sealed class Services(ServiceGraph graph, IFileReader files, IClock clock, IAhEnvironment environment, string cwd) : IRosterCliServices
    {
        private IRosterPanes? _panes;

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

        public IHomeDocs? HomeDocs => graph.HomeDocs;

        public IRosterPanes? Panes => _panes ??= new PaneComposition(graph, environment, clock);
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
            ["AH_DELIVER_POLL_MS"] = "5",
            ["AH_PASTE_STATUS_WAIT_MS"] = "50",
            ["AH_PASTE_POLL_MS"] = "5",
            ["AH_COMPOSER_REREAD_MS"] = "0",
        };
        if (_session is not null)
        {
            variables["CLAUDE_CODE_SESSION_ID"] = _session;
        }

        var env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables, new PluginInfo(_sandbox.PluginRoot, "wrangl", "1.2.3", "1"));
        var io = PlatformModule.CreateIo(null);
        var graph = ServiceGraph.Build(io, env, _runner, new FakeDaemonClient());
        var services = new Services(graph, io.Files, io.Clock, env, Project);
        CommandsModule.ComposeRoster(() => services);
        var verb = CommandsModule.Registry.Verbs.Single(v => v.Name == "roster");
        var stdout = new StringWriter(CultureInfo.InvariantCulture);
        var stderr = new StringWriter(CultureInfo.InvariantCulture);
        var code = _sandbox.Mirrored(() => verb.Run([.. args, "--cwd", Project, "--team", "proj"], stdout, stderr));
        return (code, stdout.ToString(), stderr.ToString());
    }

    private static JsonObject Parse(string text) => JsonText.Parse(Encoding.UTF8.GetBytes(text)).AsObject();

    private void WriteTeam(string memberJson = "", string teamId = "t1")
    {
        var row = memberJson.Length > 0
            ? memberJson
            : "{\"name\":\"" + Member + "\",\"role\":\"implementor\",\"kind\":\"pi\",\"route\":\"pane\",\"transport_id\":\"w1:p1\"}";
        _sandbox.PlantTeam(
            Path.Combine(Project, ".claude", "ah2", "teams", "proj.json"),
            "{\"version\":1,\"team_id\":\"" + teamId + "\",\"transport\":\"herdr\",\"orchestrator\":{\"session_id\":null,\"pid\":" + Environment.ProcessId + "},\"members\":[" + row + "]}");
    }

    private void WriteRequest()
    {
        Directory.CreateDirectory(MsgsDir);
        File.WriteAllText(
            RequestPath,
            "---\nid: 20261011-120000-abcd\ntype: request\nto: implementor\nfrom: orchestrator\nslug: task-one\n---\n\n## [0] tldr\n- do it\n");
    }

    internal static (int Exit, string Stdout) Herdr(string[] argv, string status, string screen = "", bool? ready = null)
    {
        if (argv.Length > 2 && argv[1] == "agent" && argv[2] == "get")
        {
            return (0, "{\"result\":{\"agent\":{\"agent_status\":\"" + status + "\",\"interactive_ready\":" + (ready ?? status == "idle" ? "true" : "false") + "}}}");
        }

        if (argv.Length > 2 && argv[1] == "agent" && argv[2] == "read")
        {
            return (0, screen);
        }

        if (argv.Length > 2 && argv[1] == "agent" && argv[2] is "prompt" or "wait")
        {
            return (0, "{\"result\":{\"agent\":{\"agent_status\":\"idle\"}}}");
        }

        return (0, "{}");
    }

    // ---------------------------------------------------------------- deliver

    [Fact]
    public void DeliverRefusesAFlagItDoesNotTakeAndANameThatIsNoMember()
    {
        WriteTeam();
        WriteRequest();

        var flag = Roster("deliver", Member, "--req", RequestPath, "--bogus");
        var unknown = Roster("deliver", "nobody", "--req", RequestPath);
        var missingName = Roster("deliver", "--req", RequestPath);

        Assert.Equal(2, flag.Code);
        Assert.Contains("deliver: unrecognized flag --bogus (use --req, --ping, --wait-only, --timeout, --team, or --cwd)", flag.Stderr, StringComparison.Ordinal);
        Assert.Equal(2, unknown.Code);
        Assert.Contains("has no member named \"nobody\" — its pane members are: proj-pi", unknown.Stderr, StringComparison.Ordinal);
        Assert.Equal(2, missingName.Code);
        Assert.Contains("deliver: name the member — deliver <member name>", missingName.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(_runner.Calls, c => c.Contains("prompt"));
    }

    [Fact]
    public void DeliverNeedsAnExistingRequestFileOfTheRightShape()
    {
        WriteTeam();

        var none = Roster("deliver", Member);
        var missing = Roster("deliver", Member, "--req", RequestPath);
        var notARequest = Roster("deliver", Member, "--req", Path.Combine(MsgsDir, "notes.md"));

        Assert.Equal(2, none.Code);
        Assert.Contains("deliver: --req <abs request path> is required", none.Stderr, StringComparison.Ordinal);
        Assert.Equal(2, missing.Code);
        Assert.Contains("--req must be the absolute path of an existing request file", missing.Stderr, StringComparison.Ordinal);
        Assert.Equal(2, notARequest.Code);
        Assert.Contains("--req must be the absolute path of an existing request file", notARequest.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--ping", "0", "deliver: --ping takes 1, 2 or 3, got \"0\"")]
    [InlineData("--ping", "4", "deliver: --ping takes 1, 2 or 3, got \"4\"")]
    [InlineData("--ping", "x", "deliver: --ping takes 1, 2 or 3, got \"x\"")]
    [InlineData("--timeout", "0", "deliver: --timeout takes a whole number of seconds, got \"0\"")]
    [InlineData("--timeout", "1.5", "deliver: --timeout takes a whole number of seconds, got \"1.5\"")]
    public void DeliverRefusesAPingOrTimeoutOutOfRange(string flag, string value, string message)
    {
        WriteTeam();
        WriteRequest();

        var refused = Roster("deliver", Member, "--req", RequestPath, flag, value);

        Assert.Equal(2, refused.Code);
        Assert.Contains(message, refused.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(_runner.Calls, c => c.Contains("prompt"));
    }

    [Fact]
    public void DeliverWithoutASessionIdIsRefusedBeforeAnythingIsSent()
    {
        WriteTeam();
        WriteRequest();

        _session = null;
        var anonymous = Roster("deliver", Member, "--req", RequestPath);
        var sentByAnonymous = _runner.Calls.Where(c => c.Contains("prompt") || c.Contains("send-keys") || c.Contains("paste-buffer")).ToList();
        _session = "sess-test";
        var identified = Roster("deliver", Member, "--req", RequestPath);

        Assert.Equal(2, anonymous.Code);
        Assert.Contains("this session's id is unknown (CLAUDE_CODE_SESSION_ID is unset)", anonymous.Stderr, StringComparison.Ordinal);
        Assert.Empty(sentByAnonymous);
        Assert.DoesNotContain("this session's id is unknown", identified.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void DeliverRefusesAPingWithWaitOnly()
    {
        WriteTeam();
        WriteRequest();

        var both = Roster("deliver", Member, "--req", RequestPath, "--ping", "2", "--wait-only");

        Assert.Equal(2, both.Code);
        Assert.Contains("--ping and --wait-only cannot be combined", both.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void DeliverToAMemberWithNoPaneSendsNothingAndSaysWhy()
    {
        WriteTeam("{\"name\":\"" + Member + "\",\"role\":\"implementor\",\"kind\":\"pi\",\"route\":\"pane\"}");
        WriteRequest();

        var (code, stdout, _) = Roster("deliver", Member, "--req", RequestPath);

        Assert.Equal(0, code);
        var result = Parse(stdout);
        Assert.Equal("not-sent", result["status"].AsString());
        Assert.False(result["sent"].AsBool());
        Assert.Equal("no addressable pane (transport herdr)", result["message"].AsString());
        Assert.False(File.Exists(Path.Combine(MsgsDir, "20261011-120000-abcd--orchestrator--task-one--response.md")));
    }

    [Fact]
    public void DeliverRespectsAMemberWhosePasteIsDisabled()
    {
        WriteTeam("{\"name\":\"" + Member + "\",\"role\":\"implementor\",\"kind\":\"pi\",\"route\":\"pane\",\"transport_id\":\"w1:p1\",\"paste\":false}");
        WriteRequest();
        _runner.Script = argv => Herdr(argv, "idle");

        var result = Parse(Roster("deliver", Member, "--req", RequestPath).Stdout);

        Assert.Equal("not-sent", result["status"].AsString());
        Assert.Equal("paste disabled for this member", result["message"].AsString());
        Assert.DoesNotContain(_runner.Calls, c => c.Contains("prompt"));
    }

    [Fact]
    public void DeliverToAMemberHerdrDoesNotKnowIsNotLiveWithTheCommandThatStandsItUp()
    {
        WriteTeam();
        WriteRequest();
        _runner.Script = argv => argv.Length > 2 && argv[2] == "get" ? (1, "{\"error\":{\"code\":\"agent_not_found\"}}") : (0, "{}");

        var result = Parse(Roster("deliver", Member, "--req", RequestPath).Stdout);

        Assert.Equal("not-live", result["status"].AsString());
        Assert.False(result["sent"].AsBool());
        Assert.Contains("spawn-ad-hoc implementor", result["spawn"].AsString(), StringComparison.Ordinal);
        Assert.Contains("--kind pi", result["spawn"].AsString(), StringComparison.Ordinal);
        Assert.DoesNotContain(_runner.Calls, c => c.Contains("prompt"));
    }

    /// <summary>b353: a fresh pane herdr finds but gives no agent_status is re-read, then left to mail; nothing is typed.</summary>
    [Fact]
    public void DeliverToAPaneHerdrGivesNoStatusIsMailedAndTypesNothing()
    {
        WriteTeam();
        WriteRequest();
        _runner.Script = argv => argv.Length > 2 && argv[2] == "get" ? (0, "{\"result\":{\"agent\":{\"name\":\"x\"}}}") : (0, "{}");

        var result = Parse(Roster("deliver", Member, "--req", RequestPath, "--timeout", "1").Stdout);

        Assert.Equal("mailed", result["status"].AsString());
        Assert.False(result["sent"].AsBool());
        Assert.True(_runner.Calls.Count(c => c.Contains("get")) > 1);
        Assert.DoesNotContain(_runner.Calls, c => c.Contains("prompt") || c.Contains("send-keys"));
    }

    [Fact]
    public void DeliverWaitsOutAWorkingMemberUntilTheTimeoutAndTypesNothing()
    {
        WriteTeam();
        WriteRequest();
        _runner.Script = argv => Herdr(argv, "working");

        var result = Parse(Roster("deliver", Member, "--req", RequestPath, "--timeout", "1").Stdout);

        Assert.Equal("busy", result["status"].AsString());
        Assert.False(result["sent"].AsBool());
        Assert.Equal("working", result["agent_status"].AsString());
        Assert.DoesNotContain(_runner.Calls, c => c.Contains("prompt"));
    }

    [Fact]
    public void DeliverTypesNothingIntoAWorkingMemberEvenWhenHerdrCallsItReady()
    {
        WriteTeam();
        WriteRequest();
        _runner.Script = argv => Herdr(argv, "working", ready: true);

        var result = Parse(Roster("deliver", Member, "--req", RequestPath, "--timeout", "1").Stdout);

        Assert.Equal("busy", result["status"].AsString());
        Assert.DoesNotContain(_runner.Calls, c => c.Contains("prompt"));
    }

    [Fact]
    public void DeliverReportsAMemberStoppedAtAPromptAndTypesNothing()
    {
        WriteTeam();
        WriteRequest();
        _runner.Script = argv => Herdr(argv, "blocked", "Do you want to proceed?\n");

        var result = Parse(Roster("deliver", Member, "--req", RequestPath).Stdout);

        Assert.Equal("blocked", result["status"].AsString());
        Assert.False(result["sent"].AsBool());
        Assert.Equal("harness-prompt", result["blocked_by"].AsString());
        Assert.Contains("is stopped at a pi prompt with no answer to relay", result["message"].AsString(), StringComparison.Ordinal);
        Assert.DoesNotContain(_runner.Calls, c => c.Contains("prompt"));
    }

    [Theory]
    [InlineData("codex-normal-turn.txt")]
    [InlineData("codex-interrupt.txt")]
    public void DeliverTypesTheBriefIntoAnIdleCodexComposerAsCapturedFromLivePanes(string capture)
    {
        WriteTeam("{\"name\":\"" + Member + "\",\"role\":\"implementor\",\"kind\":\"codex\",\"route\":\"pane\",\"transport_id\":\"w1:p3\"}");
        WriteRequest();
        var screen = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", capture));
        _runner.Script = argv => Herdr(argv, "idle", screen);

        var result = Parse(Roster("deliver", Member, "--req", RequestPath).Stdout);

        Assert.True(result["sent"].AsBool());
        Assert.NotEqual("blocked", result["status"].AsString());
        Assert.NotEqual("not-sent", result["status"].AsString());
        Assert.Contains(_runner.Calls, c => c.Length > 2 && c[1] == "agent" && c[2] == "prompt");
    }

    [Fact]
    public void DeliverReportsAQueuedCodexQuestionAsBlockedAndTypesNothing()
    {
        WriteTeam("{\"name\":\"" + Member + "\",\"role\":\"implementor\",\"kind\":\"codex\",\"route\":\"pane\",\"transport_id\":\"w1:p3\"}");
        WriteRequest();
        var screen = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "codex-queued-question.txt"));
        _runner.Script = argv => Herdr(argv, "idle", screen);

        var result = Parse(Roster("deliver", Member, "--req", RequestPath).Stdout);

        Assert.Equal("blocked", result["status"].AsString());
        Assert.False(result["sent"].AsBool());
        Assert.Equal("queued-question", result["blocked_by"].AsString());
        Assert.Empty(result["options"].AsArray());
        Assert.DoesNotContain(_runner.Calls, c => c.Length > 2 && c[1] == "agent" && c[2] == "prompt");
    }

    [Fact]
    public void DeliverTypesTheBriefIntoAnIdleClaudeComposerWithAStatuslineBoxUnderIt()
    {
        WriteTeam("{\"name\":\"" + Member + "\",\"role\":\"implementor\",\"kind\":\"claude\",\"route\":\"pane\",\"transport_id\":\"w1:p1\"}");
        WriteRequest();
        var screen = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "claude-idle-statusline.txt"));
        _runner.Script = argv => Herdr(argv, "idle", screen);

        var result = Parse(Roster("deliver", Member, "--req", RequestPath).Stdout);

        Assert.True(result["sent"].AsBool());
        Assert.Contains(_runner.Calls, c => c.Length > 2 && c[1] == "agent" && c[2] == "prompt");
    }

    [Fact]
    public void DeliverReportsAnUnrecognisedClaudeScreenAsIndeterminateAndTypesNothing()
    {
        WriteTeam("{\"name\":\"" + Member + "\",\"role\":\"implementor\",\"kind\":\"claude\",\"route\":\"pane\",\"transport_id\":\"w1:p1\"}");
        WriteRequest();
        var rule = new string('─', 40);
        var screen = string.Join('\n', ["  earlier output", string.Empty, rule, "❯ ", rule, .. Enumerable.Range(1, 7).Select(i => "  status row " + i)]) + "\n";
        _runner.Script = argv => Herdr(argv, "idle", screen);

        var result = Parse(Roster("deliver", Member, "--req", RequestPath).Stdout);

        Assert.Equal("indeterminate", result["status"].AsString());
        Assert.False(result["sent"].AsBool());
        Assert.Equal("unrecognised-screen", result["reason"].AsString());
        Assert.Null(result.Get("blocked_by"));
        Assert.DoesNotContain(_runner.Calls, c => c.Length > 2 && c[1] == "agent" && c[2] == "prompt");
    }

    [Fact]
    public void DeliverTypesTheBriefIntoAnIdleMemberAndCreatesOnlyTheResponseFile()
    {
        WriteTeam();
        WriteRequest();
        _runner.Script = argv => Herdr(argv, "idle");

        var (code, stdout, stderr) = Roster("deliver", Member, "--req", RequestPath);

        Assert.True(code == 0, stderr);
        var result = Parse(stdout);
        Assert.True(result["sent"].AsBool());
        Assert.Equal("no-report", result["status"].AsString());
        Assert.Contains("report is its response file only", result["message"].AsString(), StringComparison.Ordinal);
        var typed = _runner.Calls.Single(c => c.Length > 2 && c[1] == "agent" && c[2] == "prompt");
        Assert.Equal(Member, typed[3]);
        Assert.StartsWith("[hierarchy-msg " + RequestPath + "] · Report to: ", typed[4], StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(MsgsDir, "20261011-120000-abcd--orchestrator--task-one--response.md")));
    }

    [Fact]
    public void ALineTypedUnderTheStatusTagRuleMakesTheResponseAReport()
    {
        WriteTeam();
        WriteRequest();
        _runner.Script = argv => Herdr(argv, "idle");
        Roster("deliver", Member, "--req", RequestPath);
        var response = Path.Combine(MsgsDir, "20261011-120000-abcd--orchestrator--task-one--response.md");
        File.WriteAllText(response, File.ReadAllText(response).Replace("## [1] status\n", "## [1] status\n- Decision: x\n", StringComparison.Ordinal));

        var second = Roster("deliver", Member, "--req", RequestPath);
        Assert.True(second.Code == 0, second.Stderr);
        var result = Parse(second.Stdout);

        Assert.NotEqual("no-report", result["status"].AsString());
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("a/b")]
    public void DeliverToATeamWhoseIdCouldLeaveTheLockDirectoryTypesNothingAndMakesNoLock(string teamId)
    {
        WriteTeam(teamId: teamId);
        WriteRequest();
        _runner.Script = argv => Herdr(argv, "idle");

        var (code, _, stderr) = Roster("deliver", Member, "--req", RequestPath);

        Assert.NotEqual(0, code);
        Assert.Contains("lock file name", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(_runner.Calls, c => c.Length > 2 && c[1] == "agent" && c[2] == "prompt");
        // the state database's own lock, which the sandbox made before the test, is no deliver lock
        Assert.DoesNotContain(Directory.EnumerateFiles(_sandbox.Root, "*.lock", SearchOption.AllDirectories), p => !p.StartsWith(Path.Combine(_sandbox.AhHome, "state") + "/", StringComparison.Ordinal));
    }

    [Fact]
    public void DeliverWaitOnlyWithNothingDeliveredSendsNothing()
    {
        WriteTeam();
        WriteRequest();
        _runner.Script = argv => Herdr(argv, "idle");

        var result = Parse(Roster("deliver", Member, "--req", RequestPath, "--wait-only").Stdout);

        Assert.Equal("not-sent", result["status"].AsString());
        Assert.Contains("Nothing was delivered for this request", result["message"].AsString(), StringComparison.Ordinal);
        Assert.DoesNotContain(_runner.Calls, c => c.Contains("prompt"));
        Assert.False(File.Exists(Path.Combine(MsgsDir, "20261011-120000-abcd--orchestrator--task-one--response.md")));
    }

    // ---------------------------------------------------------------- answer

    private static readonly string Hash = new('a', 64);

    [Fact]
    public void AnswerRefusesAFlagItDoesNotTakeAndEveryMissingArgument()
    {
        WriteTeam();

        var flag = Roster("answer", Member, "--keys", "y");
        var noPrompt = Roster("answer", Member);
        var noChoice = Roster("answer", Member, "--prompt", "codex-trust", "--screen-hash", Hash);
        var cancelWithChoice = Roster("answer", Member, "--cancel", "--choice", "yes", "--screen-hash", Hash);
        var cancelNoHash = Roster("answer", Member, "--cancel");
        var shortHash = Roster("answer", Member, "--cancel", "--screen-hash", "abc");

        Assert.Equal(2, flag.Code);
        Assert.Contains("answer: unrecognized flag --keys (use --prompt, --choice, --cancel, --screen-hash, --request, --answer, --text, --user-choice, --team, or --cwd)", flag.Stderr, StringComparison.Ordinal);
        Assert.Equal(2, noPrompt.Code);
        Assert.Contains("answer: --prompt <blocked_by> is required — pi prompts that can be answered: (none)", noPrompt.Stderr, StringComparison.Ordinal);
        Assert.Equal(2, noChoice.Code);
        Assert.Contains("has no answers to relay for prompt \"codex-trust\"", noChoice.Stderr, StringComparison.Ordinal);
        Assert.Equal(2, cancelWithChoice.Code);
        Assert.Contains("answer: --cancel cannot be combined with --choice or --prompt; it only ever sends Esc", cancelWithChoice.Stderr, StringComparison.Ordinal);
        Assert.Equal(2, cancelNoHash.Code);
        Assert.Contains("answer --cancel: --screen-hash <hash> is required", cancelNoHash.Stderr, StringComparison.Ordinal);
        Assert.Equal(2, shortHash.Code);
        Assert.DoesNotContain(_runner.Calls, c => c.Contains("send-keys"));
    }

    [Fact]
    public void AnswerCancelRefusesAScreenHashEndingInANewline()
    {
        WriteTeam();

        var padded = Roster("answer", Member, "--cancel", "--screen-hash", Hash + "\n");

        Assert.Equal(2, padded.Code);
        Assert.Contains("answer --cancel: --screen-hash <hash> is required", padded.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(_runner.Calls, c => c.Contains("send-keys"));
    }

    [Fact]
    public void AnswerToAClaudeMemberIsAUsageErrorButItsCancelIsAnnouncedUnsupported()
    {
        WriteTeam("{\"name\":\"proj-claude\",\"role\":\"architect\",\"route\":\"peer\"}");

        var relay = Roster("answer", "proj-claude", "--prompt", "x", "--choice", "y", "--screen-hash", Hash);
        var cancel = Roster("answer", "proj-claude", "--cancel", "--screen-hash", Hash);

        Assert.Equal(2, relay.Code);
        Assert.Contains("answer: proj-claude is a Claude session — SendMessage it", relay.Stderr, StringComparison.Ordinal);
        Assert.Equal(0, cancel.Code);
        var result = Parse(cancel.Stdout);
        Assert.Equal("cancel-unsupported", result["status"].AsString());
        Assert.Equal("claude", result["kind"].AsString());
        Assert.DoesNotContain(_runner.Calls, c => c.Contains("send-keys"));
    }

    // ---------------------------------------------------------------- answer --cancel

    [Theory]
    [InlineData("-x")]
    [InlineData("w1 p2")]
    public void AnswerCancelSendsNothingToAHerdrMemberWhosePaneIdIsNotAPaneId(string paneId)
    {
        WriteTeam("{\"name\":\"proj-claude\",\"role\":\"architect\",\"route\":\"peer\",\"transport\":\"herdr\",\"transport_id\":\"" + paneId + "\"}");

        var (code, stdout, stderr) = Roster("answer", "proj-claude", "--cancel", "--screen-hash", Hash);

        Assert.True(code == 0, stderr);
        Assert.Equal("cancel-unsupported", Parse(stdout)["status"].AsString());
        Assert.DoesNotContain(_runner.Calls, c => c.Contains("send-keys"));
    }

    [Theory]
    [InlineData("-x")]
    [InlineData("w1 p1")]
    public void DeliverToAMemberWhosePaneIdIsNotAPaneIdSendsNothing(string paneId)
    {
        WriteTeam("{\"name\":\"" + Member + "\",\"role\":\"implementor\",\"kind\":\"pi\",\"route\":\"pane\",\"transport_id\":\"" + paneId + "\"}");
        WriteRequest();
        _runner.Script = argv => Herdr(argv, "idle");

        var result = Parse(Roster("deliver", Member, "--req", RequestPath).Stdout);

        Assert.Equal("not-sent", result["status"].AsString());
        Assert.Equal("no addressable pane (transport herdr)", result["message"].AsString());
        Assert.DoesNotContain(_runner.Calls, c => c.Contains("prompt"));
    }

    private const string Dialog = "Do you want to make this edit?\n 1. Yes\n 2. No\n";

    private static string HashOf(string screen) => RosterModule.CreateScreenRules().ScreenHash(screen);

    private void ClaudeAtADialog(Func<bool> pressed, string statusAfter)
    {
        WriteTeam("{\"name\":\"proj-claude\",\"role\":\"architect\",\"route\":\"peer\",\"transport\":\"herdr\",\"transport_id\":\"w1:p2\"}");
        _runner.Script = argv => Herdr(argv, pressed() ? statusAfter : "blocked", Dialog);
    }

    [Fact]
    public void AnswerCancelPressesOneEscOnABlockedClaudeMemberWhoseScreenIsTheOneSeen()
    {
        var pressed = false;
        ClaudeAtADialog(() => pressed, "idle");
        var baseScript = _runner.Script;
        _runner.Script = argv =>
        {
            if (argv.Length > 2 && argv[2] == "send-keys")
            {
                pressed = true;
                return (0, "{}");
            }

            return baseScript(argv);
        };

        var (code, stdout, stderr) = Roster("answer", "proj-claude", "--cancel", "--screen-hash", HashOf(Dialog));

        Assert.True(code == 0, stderr);
        var result = Parse(stdout);
        Assert.Equal("cancelled", result["status"].AsString());
        Assert.Equal("idle", result["agent_status"].AsString());
        Assert.True(result["live"].AsBool());
        var keys = _runner.Calls.Where(c => c.Length > 2 && c[2] == "send-keys").ToList();
        Assert.Single(keys);
        Assert.Equal(["herdr", "agent", "send-keys", "proj-claude", "esc"], keys[0]);
    }

    [Fact]
    public void AnswerCancelSendsNothingWhenTheScreenChangedOrTheMemberIsNotBlocked()
    {
        ClaudeAtADialog(() => false, "idle");

        var changed = Parse(Roster("answer", "proj-claude", "--cancel", "--screen-hash", HashOf("another screen")).Stdout);
        Assert.Equal("screen-changed", changed["status"].AsString());
        Assert.Equal("blocked", changed["agent_status"].AsString());

        _runner.Script = argv => Herdr(argv, "working", Dialog);
        var notBlocked = Parse(Roster("answer", "proj-claude", "--cancel", "--screen-hash", HashOf(Dialog)).Stdout);
        Assert.Equal("not-blocked", notBlocked["status"].AsString());
        Assert.Equal("working", notBlocked["agent_status"].AsString());

        Assert.DoesNotContain(_runner.Calls, c => c.Length > 2 && c[2] == "send-keys");
    }

    [Fact]
    public void AnswerCancelReportsAMemberStillBlockedAfterTheEscWithTheRelayMessage()
    {
        ClaudeAtADialog(() => false, "blocked");

        var result = Parse(Roster("answer", "proj-claude", "--cancel", "--screen-hash", HashOf(Dialog)).Stdout);

        Assert.Equal("still-blocked", result["status"].AsString());
        Assert.Contains("never answer Yes or Allow for a member on your own", result["message"].AsString(), StringComparison.Ordinal);
        Assert.Single(_runner.Calls, c => c.Length > 2 && c[2] == "send-keys");
    }

    // ---------------------------------------------------------------- answer a recognised prompt

    private const string TrustScreen =
        "  Do you trust the contents of this directory? Working with untrusted contents comes with higher risk.\n\n"
        + "› 1. Yes, continue\n  2. No, quit\n\n  Press enter to continue\n";

    private void CodexAtTheTrustDialog(Func<bool> pressed)
    {
        WriteTeam("{\"name\":\"proj-codex\",\"role\":\"implementor\",\"kind\":\"codex\",\"route\":\"pane\",\"transport_id\":\"w1:p3\"}");
        _runner.Script = argv =>
        {
            if (argv.Length > 2 && argv[2] == "send-keys")
            {
                return (0, "{}");
            }

            return Herdr(argv, "idle", pressed() ? string.Empty : TrustScreen);
        };
    }

    [Fact]
    public void AnswerSendsOnlyTheChosenRowsKeysWhenTheScreenStillShowsThePrompt()
    {
        var pressed = false;
        CodexAtTheTrustDialog(() => pressed);
        var baseScript = _runner.Script;
        _runner.Script = argv =>
        {
            if (argv.Length > 2 && argv[2] == "send-keys")
            {
                pressed = true;
            }

            return baseScript(argv);
        };

        var (code, stdout, stderr) = Roster(
            "answer", "proj-codex", "--prompt", "trust-dialog", "--choice", "trust", "--screen-hash", HashOf(TrustScreen));

        Assert.True(code == 0, stderr);
        var result = Parse(stdout);
        Assert.Equal("answered", result["status"].AsString());
        Assert.Equal("trust", result["choice"].AsString());
        var sent = _runner.Calls.Where(c => c.Length > 2 && c[2] == "send-keys").Select(c => c[^1]).ToList();
        Assert.Equal(["1", "Enter"], sent);
    }

    private string FolderScreen() =>
        $"  Folder access\n  {Project}\n"
        + "  Trust this folder? Codex can read, edit, and run files here, subject to your permission settings. Folder settings can run code automatically, even\n"
        + "  without a model request. Continue only if you trust these files. Your trust decision will be saved.\n"
        + "› 1. Trust and continue\n  2. Quit\n  enter continue · esc quit\n";

    /// <summary>Where the team's grants record the member's work tree; null writes no record.</summary>
    private string? _recordedWorkRoot;

    private bool _recordedWorkRootSet;

    private void RecordWorkRoot(string? root)
    {
        _recordedWorkRoot = root;
        _recordedWorkRootSet = true;
    }

    private void AnswerFolderPromptWith(string screen, string choice)
    {
        WriteGrants(_recordedWorkRootSet ? _recordedWorkRoot : Project);
        _lastAnswer = Roster(["answer", "proj-codex", "--prompt", "trust-folder", "--choice", choice, "--screen-hash", HashOf(screen)]);
    }

    private List<string> AnswerFolderPrompt(string choice, params string[] extra)
    {
        WriteGrants(_recordedWorkRootSet ? _recordedWorkRoot : Project);
        var screen = FolderScreen();
        WriteTeam("{\"name\":\"proj-codex\",\"role\":\"implementor\",\"kind\":\"codex\",\"route\":\"pane\",\"transport_id\":\"w1:p3\"}");
        var sent = false;
        _runner.Script = argv =>
        {
            if (argv.Length > 2 && argv[2] == "send-keys")
            {
                sent = true;
                return (0, "{}");
            }

            return Herdr(argv, "idle", sent ? CodexComposer : screen);
        };
        _lastAnswer = Roster(["answer", "proj-codex", "--prompt", "trust-folder", "--choice", choice, "--screen-hash", HashOf(screen), .. extra]);
        return _runner.Calls.Where(c => c.Length > 2 && c[2] == "send-keys").Select(c => c[^1]).ToList();
    }

    private void WriteGrants(string? workRoot)
    {
        if (workRoot is not null)
        {
            var grants = Path.Combine(Project, ".claude", "ah2", "grants");
            Directory.CreateDirectory(grants);
            File.WriteAllText(
                Path.Combine(grants, "proj.json"),
                JsonText.Stringify(new JsonObject
                {
                    ["version"] = 1,
                    ["mode"] = "log",
                    ["members"] = new JsonObject { ["proj-codex"] = new JsonObject { ["work_root"] = workRoot } },
                }, 0));
        }
    }

    private (int Code, string Stdout, string Stderr) _lastAnswer;

    private void Preapprove(string dir)
    {
        var (code, stdout, stderr) = Roster("codex-trust", "--preapprove", dir, "--yes");
        Assert.True(code == 0, stdout + stderr);
    }

    [Fact]
    public void ATrustAnswerOnTheFolderScreenWithNeitherAPreapprovalNorTheUsersPickIsRefusedAndSendsNothing()
    {
        var sent = AnswerFolderPrompt("trust");

        Assert.Equal("needs-user", Parse(_lastAnswer.Stdout)["status"].AsString());
        Assert.Empty(sent);
    }

    [Fact]
    public void ATrustAnswerThatTheUserPickedIsSentAsOneEnter()
    {
        var sent = AnswerFolderPrompt("trust", "--user-choice");

        Assert.Equal("answered", Parse(_lastAnswer.Stdout)["status"].AsString());
        Assert.Equal(["Enter"], sent);
    }

    [Fact]
    public void ATrustAnswerUnderAPreapprovalCoveringTheRootNeedsNoUserPick()
    {
        Preapprove(Project);
        Assert.Equal(0, Roster("codex-trust", "--yes").Code);

        var sent = AnswerFolderPrompt("trust");

        Assert.Equal("answered", Parse(_lastAnswer.Stdout)["status"].AsString());
        Assert.Equal(["Enter"], sent);
    }

    private string AutoAnswerLog => Path.Combine(_sandbox.AhHome, "codex-trust-autoanswer.jsonl");

    [Fact]
    public void ATrustAnswerThatRestsOnAPreapprovalIsLoggedAsOneRowAndReportedInTheResult()
    {
        Preapprove(Project);

        AnswerFolderPrompt("trust");

        var row = JsonText.Parse(Assert.Single(File.ReadAllLines(AutoAnswerLog)));
        Assert.Equal("proj-codex", row.Member("member")!.Value.AsString());
        Assert.Equal(HashOf(FolderScreen()), row.Member("screen_hash")!.Value.AsString());
        Assert.Equal(row.Member("result")!.Value.AsString(), Parse(_lastAnswer.Stdout)["trust_autoanswer"].Member("result")!.Value.AsString());
    }

    [Fact]
    public void ATrustAnswerThatRestsOnTheUsersPickOrIsRefusedOrQuitsLogsNothing()
    {
        Preapprove(Project);

        AnswerFolderPrompt("trust", "--user-choice");
        AnswerFolderPrompt("distrust");

        Assert.False(File.Exists(AutoAnswerLog));
        Assert.Null(Parse(_lastAnswer.Stdout).Get("trust_autoanswer"));
    }

    [Fact]
    public void ATrustAnswerIsDecidedOnTheMembersWorkTreeNotTheCallersDirectory()
    {
        var elsewhere = Path.Combine(_sandbox.Root, "member-tree");
        Directory.CreateDirectory(elsewhere);
        Preapprove(Project);
        RecordWorkRoot(elsewhere);

        var sent = AnswerFolderPrompt("trust");

        Assert.Equal("needs-user", Parse(_lastAnswer.Stdout)["status"].AsString());
        Assert.Empty(sent);
        Assert.False(File.Exists(AutoAnswerLog));
    }

    [Fact]
    public void ATrustAnswerForAMemberWhoseWorkTreeIsPreapprovedIsAnsweredWhateverTheCallersDirectory()
    {
        var elsewhere = Path.Combine(_sandbox.Root, "member-tree");
        Directory.CreateDirectory(elsewhere);
        File.WriteAllText(Path.Combine(elsewhere, ".git"), "gitdir: x\n");
        Preapprove(Project);
        RecordWorkRoot(Project);

        var sent = AnswerFolderPrompt("trust");

        Assert.Equal(["Enter"], sent);
    }

    [Fact]
    public void ATrustAnswerForAMemberWithNoWorkTreeRecordIsLeftToTheUser()
    {
        Preapprove(Project);
        RecordWorkRoot(null);

        var sent = AnswerFolderPrompt("trust");

        Assert.Equal("needs-user", Parse(_lastAnswer.Stdout)["status"].AsString());
        Assert.Empty(sent);
    }

    [Fact]
    public void ATrustAnswerThatCodexDidNotSaveIsNotReportedAsAnswered()
    {
        Preapprove(Project);

        AnswerFolderPrompt("trust");

        var result = Parse(_lastAnswer.Stdout);
        Assert.Equal("verify-failed", result["status"].AsString());
        Assert.Equal("verify-failed", result["trust_autoanswer"].Member("result")!.Value.AsString());
    }

    [Fact]
    public void ATrustAnswerWaitsForCodexToSettleBeforeCheckingTheTrustWasSaved()
    {
        Preapprove(Project);
        Assert.Equal(0, Roster("codex-trust", "--yes").Code);
        var screen = FolderScreen();
        WriteTeam("{\"name\":\"proj-codex\",\"role\":\"implementor\",\"kind\":\"codex\",\"route\":\"pane\",\"transport_id\":\"w1:p3\"}");
        RecordWorkRoot(Project);
        var sent = false;
        var readsSinceSend = 0;
        _runner.Script = argv =>
        {
            if (argv.Length > 2 && argv[2] == "send-keys")
            {
                sent = true;
                return (0, "{}");
            }

            if (sent && argv.Length > 2 && argv[1] == "agent" && argv[2] == "get")
            {
                return Herdr(argv, ++readsSinceSend <= 2 ? "working" : "idle", CodexComposer);
            }

            return Herdr(argv, "idle", sent ? CodexComposer : screen);
        };

        AnswerFolderPromptWith(screen, "trust");

        var result = Parse(_lastAnswer.Stdout);
        Assert.Equal("answered", result["status"].AsString());
        Assert.True(readsSinceSend > 2);
    }

    private const string CodexComposer = "  Some earlier output\n\n› Ask Codex to do anything\n\n  gpt-5 · ~/repo\n  ? for shortcuts";

    /// <summary>Codex's trust, as <c>codex-trust --yes</c> saves it, taken out of the config so a test can save it at the moment Codex would.</summary>
    private string TakeOutSavedTrust()
    {
        Assert.Equal(0, Roster("codex-trust", "--yes").Code);
        var config = Path.Combine(_sandbox.Home, ".codex", "config.toml");
        var saved = File.ReadAllText(config);
        File.Delete(config);
        return saved;
    }

    [Fact]
    public void ATrustAnswerKeepsWaitingWhileHerdrIsReadyButTheScreenStillShowsTheTrustPrompt()
    {
        Preapprove(Project);
        var saved = TakeOutSavedTrust();
        var screen = FolderScreen();
        WriteTeam("{\"name\":\"proj-codex\",\"role\":\"implementor\",\"kind\":\"codex\",\"route\":\"pane\",\"transport_id\":\"w1:p3\"}");
        RecordWorkRoot(Project);
        var sent = false;
        var readsSinceSend = 0;
        _runner.Script = argv =>
        {
            if (argv.Length > 2 && argv[2] == "send-keys")
            {
                sent = true;
                return (0, "{}");
            }

            if (sent && argv.Length > 2 && argv[1] == "agent" && argv[2] == "read" && ++readsSinceSend >= 2)
            {
                var config = Path.Combine(_sandbox.Home, ".codex", "config.toml");
                Directory.CreateDirectory(Path.GetDirectoryName(config)!);
                File.WriteAllText(config, saved);
                return Herdr(argv, "idle", CodexComposer);
            }

            return Herdr(argv, "idle", screen);
        };

        AnswerFolderPromptWith(screen, "trust");

        var result = Parse(_lastAnswer.Stdout);
        Assert.Equal("answered", result["status"].AsString());
        Assert.Equal("answered", result["trust_autoanswer"].Member("result")!.Value.AsString());
    }

    [Fact]
    public void AFailedSendUnderAPreapprovalIsLoggedAsSendFailedAndThenSurfaced()
    {
        Preapprove(Project);
        var screen = FolderScreen();
        WriteTeam("{\"name\":\"proj-codex\",\"role\":\"implementor\",\"kind\":\"codex\",\"route\":\"pane\",\"transport_id\":\"w1:p3\"}");
        RecordWorkRoot(Project);
        _runner.Script = argv => argv.Length > 2 && argv[2] == "send-keys" ? (1, "{\"error\":\"pane gone\"}") : Herdr(argv, "idle", screen);

        AnswerFolderPromptWith(screen, "trust");

        Assert.NotEqual(0, _lastAnswer.Code);
        var row = JsonText.Parse(Assert.Single(File.ReadAllLines(AutoAnswerLog)));
        Assert.Equal("send-failed", row.Member("result")!.Value.AsString());
    }

    [Fact]
    public void AnUncoveredTrustAnswerLogsNothing()
    {
        AnswerFolderPrompt("trust");

        Assert.False(File.Exists(AutoAnswerLog));
    }

    [Fact]
    public void APreapprovalForAnotherRootDoesNotLetATrustAnswerThrough()
    {
        var other = Path.Combine(_sandbox.Root, "other");
        Directory.CreateDirectory(other);
        Preapprove(other);

        var sent = AnswerFolderPrompt("trust");

        Assert.Equal("needs-user", Parse(_lastAnswer.Stdout)["status"].AsString());
        Assert.Empty(sent);
    }

    [Fact]
    public void QuittingTheFolderScreenIsNotAGrantAndNeedsNothing()
    {
        var sent = AnswerFolderPrompt("distrust");

        Assert.Equal("answered", Parse(_lastAnswer.Stdout)["status"].AsString());
        Assert.Equal(["esc"], sent);
    }

    [Fact]
    public void AnswerReportsAChangedScreenAndRefusesAChoiceThePromptDoesNotTake()
    {
        CodexAtTheTrustDialog(() => false);

        var changed = Parse(Roster("answer", "proj-codex", "--prompt", "trust-dialog", "--choice", "trust", "--screen-hash", Hash).Stdout);
        var notAnAnswer = Roster("answer", "proj-codex", "--prompt", "trust-dialog", "--choice", "yes", "--screen-hash", HashOf(TrustScreen));
        var wrongPrompt = Parse(Roster("answer", "proj-codex", "--prompt", "login", "--choice", "sign-in", "--screen-hash", HashOf(TrustScreen)).Stdout);

        Assert.Equal("screen-changed", changed["status"].AsString());
        Assert.Equal("trust-dialog", changed["blocked_by"].AsString());
        Assert.Equal(2, notAnAnswer.Code);
        Assert.Contains("\"yes\" is not an answer to trust-dialog — the ids are: trust, distrust", notAnAnswer.Stderr, StringComparison.Ordinal);
        Assert.Equal("screen-changed", wrongPrompt["status"].AsString());
        Assert.DoesNotContain(_runner.Calls, c => c.Length > 2 && c[2] == "send-keys");
    }
}
