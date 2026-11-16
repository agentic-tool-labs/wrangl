using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Mux;
using Ah.TestKit;

namespace Ah.Launch.Tests;

/// <summary>
/// herdr desktop P1: the core translator behind <see cref="IHerdr"/>, one test per call-map row, against a fake runner that records each
/// <c>hc</c> / <c>herdr-app</c> argv and answers in the shapes herdr-core 0.1.18 / herdr-app 0.1.13 printed (implementor-109's evidence).
/// </summary>
public sealed class HerdrCoreTests
{
    private const string Path = "/nonexistent-ah/usr/bin:/nonexistent-ah/node/bin";
    private const string AppPane = "pa294aaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Tab = "tb7376b36fffa60df81c78b2c4a2745a41";
    private const string Space = "sp853e986c7c4d35d9eab014a6e4a8c546";

    private sealed class Fake(Func<string, string[], MuxResult> answer) : IMuxRunner
    {
        public List<(string Bin, string[] Args, IReadOnlyDictionary<string, string?>? Env)> Calls { get; } = [];

        public MuxResult Run(string binary, IReadOnlyList<string> args, double timeoutMs, int outputCapBytes = 1 << 20, string? workingDirectory = null, IReadOnlyDictionary<string, string?>? environment = null)
        {
            Calls.Add((binary, [.. args], environment));
            return answer(binary, [.. args]);
        }

        public string[] Argv(Index i) => [Calls[i].Bin, .. Calls[i].Args];
    }

    private sealed class NoFiles : IFileReader
    {
        public string ReadText(string path) => string.Empty;

        public byte[] ReadBytes(string path) => [];

        public FileTail ReadTail(string path, int maxBytes) => new([], 0, false);

        public FileRange ReadRange(string path, long offset, int maxBytes) => new([], 0);

        public NoFollowRead ReadNoFollow(string path, int maxBytes) => new(FileKind.File, 0, []);

        public FileStat? Stat(string path, bool followSymlinks = true) => null;

        public bool Exists(string path) => false;

        public string RealPath(string path) => path;

        public IReadOnlyList<string> List(string directory) => [];

        public bool IsExecutable(string path) => path.EndsWith("/hc", StringComparison.Ordinal) || path.EndsWith("/herdr", StringComparison.Ordinal);
    }

    private static MuxResult Out(string stdout) => new(true, stdout, string.Empty, 0, false, null);

    private static readonly MuxResult Fail = new(false, string.Empty, "boom", 1, false, "Command failed: boom");

    private static readonly MuxResult NoBinary = new(false, string.Empty, string.Empty, null, false, "spawnSync hc ENOENT");

    private static FakeEnvironment Env(params (string, string)[] extra)
    {
        var vars = new Dictionary<string, string> { ["PATH"] = "/bin", ["HC_TERMINAL_ID"] = "t1" };
        foreach (var (k, v) in extra)
        {
            vars[k] = v;
        }

        return new FakeEnvironment(vars);
    }

    private static Herdr Core(Fake fake, FakeEnvironment? env = null, Func<DateTimeOffset>? now = null)
    {
        var e = env ?? Env(("PATH", Path));
        var clock = now ?? (() => DateTimeOffset.UnixEpoch);
        return new Herdr(fake, e, new NoFiles(), HerdrFlavour.Core, new HerdrCore(fake, e, clock, _ => { }, () => new string('a', 32)));
    }

    private static string Ls(params string[] addresses) =>
        "{\"hidden_finished\":0,\"terminals\":[" + string.Join(",", addresses.Select(a => $"{{\"address\":\"{a}\",\"agent\":\"claude\",\"state\":\"live\",\"turn\":0}}")) + "]}";

    private static string Get(string address, string status = "{\"app\":\"claude-code\",\"state\":\"idle\"}", string state = "live", string labels = "{\"ah.name\":\"alpha\"}", int cols = 55, int rows = 29) =>
        $"{{\"address\":\"{address}\",\"argv\":[\"/bin/zsh\",\"-l\"],\"cols\":{cols},\"rows\":{rows},\"keep\":true,\"labels\":{labels},\"reported\":{(status == "null" ? "false" : "true")},\"status\":{status},\"state\":\"{state}\",\"pid\":4242,\"cwd\":\"/w\",\"foreground\":{{\"pid\":4300,\"name\":\"claude\",\"argv\":[\"claude\",\"--x\"],\"cwd\":\"/w\"}}}}";

    private const string AppStatus = "{\"space\":\"" + Space + "\",\"tab\":\"" + Tab + "\",\"focused_pane\":\"" + AppPane + "\",\"zoom\":1.5,\"layout\":\"tiles\"}";

    private static string AppLs(params (string Terminal, string Pane)[] terms) =>
        "{\"spaces\":[{\"id\":\"" + Space + "\",\"name\":\"~\",\"tabs\":[{\"id\":\"" + Tab + "\",\"name\":\"1\",\"terminals\":["
        + string.Join(",", terms.Select(t => $"{{\"terminal\":\"{t.Terminal}\",\"pane\":\"{t.Pane}\",\"status\":\"idle\"}}")) + "]}]}]}";

    private static JsonValue Doc(string text) => JsonText.Parse(System.Text.Encoding.UTF8.GetBytes(text));

    // --- tui identity -------------------------------------------------------------------------------------------------------------

    [Fact]
    public void UnderTuiEveryCallIsTheSameHerdrArgvWithTheSameOutput()
    {
        const string Answer = "{\"result\":{\"agents\":[{\"name\":\"a\",\"pane_id\":\"w1:p2\",\"agent_status\":\"idle\"}]}}";
        var fake = new Fake((_, _) => Out(Answer));
        var env = new Dictionary<string, string?> { ["HERDR_SOCKET_PATH"] = "/s.sock" };
        var tui = new Herdr(fake, Env(("HERDR_ENV", "1")), new NoFiles());

        var raw = tui.Run(["pane", "read", "w1:p2", "--source", "visible"], 2000, 99, env);
        var listed = tui.Invoke(["agent", "list"]);

        Assert.Equal(HerdrFlavour.Tui, tui.Flavour);
        Assert.Equal(["herdr", "pane", "read", "w1:p2", "--source", "visible"], fake.Argv(0));
        Assert.Same(env, fake.Calls[0].Env);
        Assert.Equal(Answer, raw.Stdout);
        Assert.Equal(["herdr", "agent", "list"], fake.Argv(1));
        Assert.Equal(Answer, JsonText.Stringify(listed, 0));
        Assert.True(tui.AppPresent());
    }

    [Fact]
    public void TheEnvDecidesTheFlavourOfTheComposedInstance()
    {
        var fake = new Fake((_, _) => Out(Ls()));
        Assert.Equal(HerdrFlavour.Core, new Herdr(fake, Env(), new NoFiles()).Flavour);
        Assert.Equal(HerdrFlavour.Tui, new Herdr(fake, Env(("HERDR_ENV", "1")), new NoFiles()).Flavour);
        Assert.Equal(HerdrFlavour.Tui, new Herdr(fake, new FakeEnvironment(new Dictionary<string, string>()), new NoFiles()).Flavour);
        Assert.Equal(HerdrFlavour.Core, new Herdr(fake, new FakeEnvironment(new Dictionary<string, string>()), new NoFiles()).ForFlavour(HerdrFlavour.Core).Flavour);
    }

    // --- unsupported ------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("pane", "send-text")]
    [InlineData("workspace", "list")]
    public void AnUnsupportedVerbFailsByNameAndRunsNothing(string a, string b)
    {
        var fake = new Fake((_, _) => Out("{}"));

        var error = Assert.Throws<InvalidOperationException>(() => Core(fake).Invoke([a, b, "x"]));

        Assert.Contains($"`{a} {b}` is not supported under herdr core", error.Message, StringComparison.Ordinal);
        Assert.Empty(fake.Calls);
    }

    // --- agent wait (P2) ----------------------------------------------------------------------------------------------------------

    /// <summary>hc get answers <paramref name="states"/> in turn (the last repeats); each hc wait moves the clock 10 s on.</summary>
    private static (MuxResult Result, Fake Fake) Wait(string[] states, params string[] args)
    {
        var at = DateTimeOffset.UnixEpoch;
        var gets = 0;
        var fake = new Fake((bin, a) => (bin, a[0]) switch
        {
            ("hc", "ls") => Out(Ls("t3")),
            ("hc", "get") => Out(Get("t3", $"{{\"app\":\"claude-code\",\"state\":\"{states[Math.Min(gets++, states.Length - 1)]}\"}}")),
            ("hc", "wait") => Tick(),
            _ => Fail,
        });
        var result = Core(fake, now: () => at).Run(["agent", "wait", "alpha", .. args], 60000);
        return (result, fake);

        MuxResult Tick()
        {
            at += TimeSpan.FromSeconds(10);
            return Out("{}");
        }
    }

    private static string? WaitStatus(MuxResult r) => Doc(r.Stdout).Member("result")?.Member("agent")?.Member("agent_status")?.AsString();

    [Fact]
    public void AgentWaitWithoutUntilStopsAtTheFirstStatusThatIsNotWorking()
    {
        var (r, fake) = Wait(["working", "idle"], "--timeout", "30000");

        Assert.True(r.Ok);
        Assert.Equal("idle", WaitStatus(r));
        Assert.Contains(fake.Calls, c => c.Args is ["wait", "t3", "--timeout", "30", "--json"]);
    }

    [Fact]
    public void AgentWaitUntilPassesOverOtherStatusesAndStopsAtS()
    {
        var (r, fake) = Wait(["working", "idle", "blocked"], "--until", "blocked", "--timeout", "30000");

        Assert.Equal("blocked", WaitStatus(r));
        Assert.Equal(2, fake.Calls.Count(c => c.Args[0] == "wait"));
    }

    [Fact]
    public void AgentWaitAtTheDeadlineAnswersTheTimeoutError()
    {
        var (r, _) = Wait(["working"], "--timeout", "25000");

        Assert.False(r.Ok);
        Assert.Equal("timeout", Doc(r.Stdout).Member("error")!.Value.Member("code")!.Value.AsString());
    }

    // --- agent get and status mapping ---------------------------------------------------------------------------------------------

    private static Fake AgentGetFake(string ls, string get) => new((bin, args) => (bin, args[0]) switch
    {
        ("hc", "ls") => Out(ls),
        ("hc", "get") => Out(get),
        _ => Fail,
    });

    [Fact]
    public void AgentGetLooksUpTheNameLabelThenReadsTheTerminal()
    {
        var fake = AgentGetFake(Ls("t3"), Get("t3"));

        var doc = Core(fake).Invoke(["agent", "get", "alpha"]);

        Assert.Equal(["hc", "ls", "--json", "--label", "ah.name=alpha"], fake.Argv(0));
        Assert.Equal(["hc", "get", "t3", "--json"], fake.Argv(1));
        var agent = doc.Member("result")!.Value.Member("agent")!.Value;
        Assert.Equal("t3", agent.Member("pane_id")!.Value.AsString());
        Assert.Equal("idle", agent.Member("agent_status")!.Value.AsString());
        Assert.Equal("claude", agent.Member("agent")!.Value.AsString());
    }

    [Theory]
    [InlineData("{\"app\":\"claude-code\",\"state\":\"working\"}", true, false, false, "working")]
    [InlineData("{\"app\":\"claude-code\",\"state\":\"blocked\"}", true, false, false, "blocked")]
    [InlineData("{\"app\":\"claude-code\",\"state\":\"idle\"}", true, false, true, "idle")]
    [InlineData("{\"app\":\"claude-code\",\"state\":\"done\"}", true, false, true, "done")]
    [InlineData("null", true, false, false, "unknown")]
    [InlineData("{\"app\":\"claude-code\",\"state\":\"error\"}", false, true, false, "error")]
    public void StatusMapping(string status, bool live, bool indeterminate, bool ready, string agentStatus)
    {
        var state = Core(AgentGetFake(Ls("t3"), Get("t3", status))).AgentState("alpha");

        Assert.Equal((live, indeterminate, ready, agentStatus), (state.Live, state.Indeterminate, state.Ready, state.AgentStatus));
    }

    [Fact]
    public void AFinishedTerminalIsNotFoundNotItsLastStatus()
    {
        var state = Core(AgentGetFake(Ls("t3"), Get("t3", state: "finished"))).AgentState("alpha");

        Assert.False(state.Live);
        Assert.False(state.Indeterminate);
        Assert.Equal("agent_not_found", state.Why);
    }

    [Fact]
    public void ANameOnNoLiveTerminalIsNotFound()
    {
        var fake = AgentGetFake(Ls(), Get("t3"));

        var state = Core(fake).AgentState("alpha");

        Assert.Equal((false, false), (state.Live, state.Indeterminate));
        Assert.Single(fake.Calls);
    }

    [Fact]
    public void ANameOnTwoLiveTerminalsIsAmbiguousAndIndeterminateNeverNotLive()
    {
        var fake = AgentGetFake(Ls("t3", "t4"), Get("t3"));

        var state = Core(fake).AgentState("alpha");
        var raw = Core(fake).InvokeAllowFailure(["agent", "get", "alpha"]);

        Assert.True(state.Indeterminate);
        Assert.False(state.Live);
        Assert.Contains("agent_name_ambiguous", state.Why, StringComparison.Ordinal);
        var error = Doc(raw.Stdout).Member("error")!.Value;
        Assert.Equal("agent_name_ambiguous", error.Member("code")!.Value.AsString());
        Assert.Contains("t3, t4", error.Member("message")!.Value.AsString(), StringComparison.Ordinal);
    }

    [Fact]
    public void AnAmbiguousNameRefusesASpawnInsteadOfStartingASecondAgent()
    {
        var fake = AgentGetFake(Ls("t3", "t4"), Get("t3"));

        var raw = Core(fake).InvokeAllowFailure(["agent", "start", "alpha", "--kind", "claude"]);

        Assert.False(raw.Ok);
        Assert.DoesNotContain(fake.Calls, c => c.Args[0] is "run" or "send");
    }

    [Fact]
    public void HcMissingIsTheExecFailurePathAndNeverCallsHerdr()
    {
        var fake = new Fake((_, _) => NoBinary);

        var state = Core(fake).AgentState("alpha");

        Assert.True(state.Indeterminate);
        Assert.DoesNotContain(fake.Calls, c => c.Bin == "herdr");
    }

    [Fact]
    public void UnderCoreOnPathLooksForHcNotHerdr()
    {
        var env = new FakeEnvironment(new Dictionary<string, string> { ["PATH"] = "/x", ["HC_TERMINAL_ID"] = "t1" });
        var core = new Herdr(new Fake((_, _) => Fail), env, new OnlyHerdr());

        Assert.False(core.OnPath());
        Assert.True(core.ForFlavour(HerdrFlavour.Tui).OnPath());
    }

    private sealed class OnlyHerdr : IFileReader
    {
        public string ReadText(string path) => string.Empty;

        public byte[] ReadBytes(string path) => [];

        public FileTail ReadTail(string path, int maxBytes) => new([], 0, false);

        public FileRange ReadRange(string path, long offset, int maxBytes) => new([], 0);

        public NoFollowRead ReadNoFollow(string path, int maxBytes) => new(FileKind.File, 0, []);

        public FileStat? Stat(string path, bool followSymlinks = true) => null;

        public bool Exists(string path) => false;

        public string RealPath(string path) => path;

        public IReadOnlyList<string> List(string directory) => [];

        public bool IsExecutable(string path) => path == "/x/herdr";
    }

    [Theory]
    [InlineData("claude-code", "claude")]
    [InlineData("codex", "codex")]
    public void AgentTypeComesFromStatusApp(string app, string type) =>
        Assert.Equal(type, HerdrCore.MapAgentType(Doc($"{{\"app\":\"{app}\",\"state\":\"idle\"}}")));

    [Fact]
    public void ANullAppGivesNoAgentType() => Assert.Null(HerdrCore.MapAgentType(Doc("{\"app\":null,\"state\":\"idle\"}")));

    // --- agent list ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void AgentListNamesEachLiveTerminalByItsLabelWithTheAddressAsPaneId()
    {
        var fake = new Fake((bin, args) => (bin, args[0], args.Length > 1 ? args[1] : null) switch
        {
            ("hc", "ls", _) => Out(Ls("t3", "t4")),
            ("hc", "get", "t3") => Out(Get("t3", labels: $"{{\"ah.name\":\"alpha\",\"herdr.tab\":\"{Tab}\"}}")),
            ("hc", "get", "t4") => Out(Get("t4", state: "finished")),
            _ => Fail,
        });

        var agents = Core(fake).Agents();

        var row = Assert.Single(agents);
        Assert.Equal("alpha", row.Member("name")!.Value.AsString());
        Assert.Equal("t3", row.Member("pane_id")!.Value.AsString());
        Assert.Equal(Tab, row.Member("tab_id")!.Value.AsString());
        Assert.Equal("idle", row.Member("agent_status")!.Value.AsString());
    }

    // --- prompt, keys, read, close, rename --------------------------------------------------------------------------------------

    private static Fake Named(string address = "t3") => new((bin, args) => (bin, args[0]) switch
    {
        ("hc", "ls") => Out(Ls(address)),
        ("hc", "get") => Out(Get(address)),
        ("hc", _) => Out("{\"started\":true,\"status\":\"idle\",\"turn\":0}\nscreen text\n"),
        _ => Fail,
    });

    [Fact]
    public void AgentPromptIsHcSendToTheNamedTerminal()
    {
        var fake = Named();

        Core(fake).Invoke(["agent", "prompt", "alpha", "do the thing", "--wait"]);

        Assert.Equal(["hc", "send", "t3", "do the thing"], fake.Argv(^1));
        Assert.DoesNotContain(fake.Calls, c => c.Args.Contains("--force"));
    }

    [Fact]
    public void ASendRefusalIsPassedUpNotForced()
    {
        var fake = new Fake((bin, args) => (bin, args[0]) switch
        {
            ("hc", "ls") => Out(Ls("t3")),
            ("hc", "send") => new MuxResult(false, string.Empty, "terminal is blocked; pass --force", 1, false, "Command failed"),
            _ => Fail,
        });

        var error = Assert.Throws<InvalidOperationException>(() => Core(fake).Invoke(["agent", "prompt", "alpha", "x"]));

        Assert.Contains("blocked", error.Message, StringComparison.Ordinal);
        Assert.Equal(2, fake.Calls.Count);
    }

    [Fact]
    public void PaneRunIsHcSendToTheAddress()
    {
        var fake = Named();

        Core(fake).Invoke(["pane", "run", "t9", "ls"]);

        Assert.Equal(["hc", "send", "t9", "ls"], fake.Argv(0));
    }

    [Theory]
    [InlineData("Enter", "enter")]
    [InlineData("C-c", "ctrl+c")]
    [InlineData("Escape", "esc")]
    public void SendKeysMapsTheKeyName(string tui, string core)
    {
        var fake = Named();

        Core(fake).Invoke(["agent", "send-keys", "alpha", tui]);

        Assert.Equal(["hc", "keys", "t3", core], fake.Argv(^1));
    }

    [Fact]
    public void AnUnmappedKeyFailsAndPressesNothing()
    {
        var fake = Named();

        Assert.Throws<InvalidOperationException>(() => Core(fake).Invoke(["agent", "send-keys", "alpha", "Down"]));

        Assert.Empty(fake.Calls);
    }

    [Theory]
    [InlineData(new[] { "agent", "read", "alpha", "--source", "visible" }, "0")]
    [InlineData(new[] { "pane", "read", "t3", "--source", "visible", "--ansi" }, "0")]
    [InlineData(new[] { "pane", "read", "t3", "--source", "recent-unwrapped", "--lines", "40" }, "40")]
    public void ReadIsHcReadWithTheLineCount(string[] tui, string lines)
    {
        var fake = Named();

        var read = Core(fake).Run(tui, 2000);

        Assert.Equal(["hc", "read", "t3", "--lines", lines], fake.Argv(^1));
        Assert.Contains("screen text", read.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void PaneCloseIsHcKill()
    {
        var fake = Named();

        Core(fake).Invoke(["pane", "close", "t3"]);

        Assert.Equal(["hc", "kill", "t3"], fake.Argv(0));
    }

    [Fact]
    public void PaneRenameSucceedsAndRunsNothing()
    {
        var fake = Named();

        Core(fake).Invoke(["pane", "rename", "t3", "claude", "-", "alpha"]);

        Assert.Empty(fake.Calls);
    }

    [Fact]
    public void AgentRenameClearRemovesTheNameLabel()
    {
        var fake = Named();

        Core(fake).Invoke(["agent", "rename", "t3", "--clear"]);

        Assert.Equal(["hc", "label", "t3", "ah.name="], fake.Argv(0));
    }

    [Fact]
    public void AgentFocusIsAppPaneFocusByTerminal()
    {
        var fake = new Fake((_, _) => Out("{\"ok\":true}"));

        Core(fake).Invoke(["agent", "focus", "t3"]);

        Assert.Equal(["herdr-app", "do", "pane.focus", "--terminal", "t3"], fake.Argv(0));
    }

    [Fact]
    public void ProcessInfoMapsTheForegroundProcess()
    {
        var fake = Named();

        var doc = Core(fake).Invoke(["pane", "process-info", "--pane", "t3"]);

        var processes = BareDetector.ProcessesOf(doc)!;
        Assert.Equal(4300, Assert.Single(processes).Pid);
        Assert.Equal(["claude", "--x"], processes[0].Argv);
        Assert.Equal(4242, doc.Member("result")!.Value.Member("process_info")!.Value.Member("shell_pid")!.Value.AsNumber());
    }

    // --- pane list, get, session list, instance env -----------------------------------------------------------------------------

    private static Fake App(Func<string[], MuxResult>? more = null) => new((bin, args) => (bin, args[0]) switch
    {
        ("herdr-app", "status") => Out(AppStatus),
        ("herdr-app", "ls") => Out(AppLs(("t1", AppPane), ("t3", "pc7be"))),
        _ => more?.Invoke([bin, .. args]) ?? Out("{\"ok\":true}"),
    });

    [Fact]
    public void PaneListAndGetComeFromTheAppListing()
    {
        var fake = App();

        var panes = Core(fake).Invoke(["pane", "list"]).Member("result")!.Value.Member("panes")!.Value.AsArray();
        var pane = Core(fake).Invoke(["pane", "get", "t3"]).Member("result")!.Value.Member("pane")!.Value;

        Assert.Equal(["t1", "t3"], panes.Select(p => p.Member("pane_id")!.Value.AsString()));
        Assert.Equal(Tab, pane.Member("tab_id")!.Value.AsString());
        Assert.Equal(Space, pane.Member("workspace_id")!.Value.AsString());
    }

    [Fact]
    public void WithoutTheAppPaneListIsHcLs()
    {
        var fake = new Fake((bin, args) => bin == "hc" ? Out(Ls("t4")) : Fail);

        var panes = Core(fake).Invoke(["pane", "list"]).Member("result")!.Value.Member("panes")!.Value.AsArray();

        Assert.Equal("t4", Assert.Single(panes).Member("pane_id")!.Value.AsString());
    }

    [Fact]
    public void ThePinnedInstanceBecomesHcSocket()
    {
        var fake = Named();

        Core(fake).Run(["pane", "close", "t3"], 2000, 1 << 20, new Dictionary<string, string?> { ["HERDR_SOCKET_PATH"] = "/core.sock", ["HOME"] = "/h" });

        var env = fake.Calls[0].Env!;
        Assert.Equal("/core.sock", env["HC_SOCKET"]);
        Assert.False(env.ContainsKey("HERDR_SOCKET_PATH"));
        Assert.Equal("/h", env["HOME"]);
    }

    [Fact]
    public void SessionListNamesTheCoreSocket()
    {
        var fake = Named();

        var listed = Core(fake, Env(("HC_TERMINAL_SOCKET", "/s/default.sock"))).Run(["session", "list"], 2000);

        Assert.Equal("core /s/default.sock\n", listed.Stdout);
        Assert.Empty(fake.Calls);
    }

    // --- layout, split, zoom ---------------------------------------------------------------------------------------------------

    [Fact]
    public void LayoutHasSizesNoPositionsAndMarksTheDefaultSizeUnknown()
    {
        var fake = App(argv => argv is ["hc", "get", "t3", ..] ? Out(Get("t3", cols: 200, rows: 120)) : argv is ["hc", "get", ..] ? Out(Get("t1", cols: 114, rows: 29)) : Fail);

        var layout = Core(fake).Invoke(["pane", "layout", "--current"]).Member("result")!.Value.Member("layout")!.Value;

        Assert.False(layout.Member("positions")!.Value.AsBool());
        Assert.False(LayoutRules.ReportsPositions(Doc($"{{\"result\":{{\"layout\":{JsonText.Stringify(layout, 0)}}}}}")));
        Assert.Equal(Tab, layout.Member("tab_id")!.Value.AsString());
        Assert.Equal("t1", layout.Member("focused_pane_id")!.Value.AsString());
        var panes = layout.Member("panes")!.Value.AsArray();
        Assert.Equal(114, panes[0].Member("rect")!.Value.Member("width")!.Value.AsNumber());
        Assert.True(panes[0].Member("size_known")!.Value.AsBool());
        Assert.False(panes[1].Member("size_known")!.Value.AsBool());
    }

    [Fact]
    public void CoreZoomIsAlwaysReportedOffEvenWhenTheWindowZoomIsSet()
    {
        var fake = App(argv => argv is ["hc", "get", ..] ? Out(Get("t1")) : Fail);

        var layout = Core(fake).Invoke(["pane", "layout", "--pane", "t1"]).Member("result")!.Value.Member("layout")!.Value;

        Assert.False(layout.Member("zoomed")!.Value.AsBool());
    }

    [Fact]
    public void ZoomOffIsANoOpAndZoomOnIsOneToggle()
    {
        var fake = App();

        Core(fake).Invoke(["pane", "zoom", "t1", "--off"]);
        Assert.Empty(fake.Calls);

        Core(fake).Invoke(["pane", "zoom", "t1", "--on"]);
        Assert.Equal(["herdr-app", "do", "pane.zoom", "--pane", AppPane], fake.Argv(^1));
        Assert.Single(fake.Calls, c => c.Args is ["do", "pane.zoom", ..]);
    }

    [Fact]
    public void SplitIsAppPaneSplitThenCdThenFocusBack()
    {
        var fake = App(argv => argv switch
        {
            ["herdr-app", "do", "pane.split", ..] => Out("{ \"pane\": \"pb8bd\", \"terminal\": \"t5\" }"),
            ["hc", "get", ..] => Out(Get("t1")),
            _ => Out("{\"ok\":true}"),
        });

        var doc = Core(fake).Invoke(["pane", "split", "--pane", "t1", "--direction", "right", "--cwd", "/other", "--no-focus"]);

        Assert.Equal("t5", doc.Member("result")!.Value.Member("pane")!.Value.Member("pane_id")!.Value.AsString());
        var argvs = fake.Calls.Select(c => string.Join(' ', [c.Bin, .. c.Args])).ToList();
        var split = argvs.IndexOf($"herdr-app do pane.split --pane {AppPane} --direction right");
        var cd = argvs.IndexOf("hc send t5 cd /other");
        var focus = argvs.IndexOf($"herdr-app do pane.focus --pane {AppPane}");
        Assert.True(split >= 0 && cd > split && focus > split, string.Join(" | ", argvs));
    }

    [Fact]
    public void SplitIntoTheSameFolderSendsNoCd()
    {
        var fake = App(argv => argv switch
        {
            ["herdr-app", "do", "pane.split", ..] => Out("{ \"pane\": \"pb8bd\", \"terminal\": \"t5\" }"),
            ["hc", "get", ..] => Out(Get("t1")),
            _ => Out("{\"ok\":true}"),
        });

        Core(fake).Invoke(["pane", "split", "--pane", "t1", "--direction", "down", "--cwd", "/w"]);

        Assert.DoesNotContain(fake.Calls, c => c.Args is ["send", ..]);
        Assert.DoesNotContain(fake.Calls, c => c.Args is ["do", "pane.focus", ..]);
    }

    [Fact]
    public void SplitWithoutTheAppIsRefusedByName()
    {
        var fake = new Fake((_, _) => Fail);

        var error = Assert.Throws<InvalidOperationException>(() => Core(fake).Invoke(["pane", "split", "--pane", "t1", "--direction", "right"]));

        Assert.Contains("needs herdr-app", error.Message, StringComparison.Ordinal);
    }

    // --- spawn ------------------------------------------------------------------------------------------------------------------

    private static Fake Spawn(string? runReply = null) => new((bin, args) => (bin, args[0]) switch
    {
        ("hc", "ls") => Out(Ls()),
        ("hc", "run") => Out(runReply ?? "{\"address\":\"t4\",\"agent\":\"claude\",\"cwd\":\"/w\",\"name\":null,\"state\":\"live\",\"turn\":0}"),
        _ => Out("{\"started\":true}"),
    });

    [Fact]
    public void StartIntoAPaneLabelsItWithTheNameAndNonceThenTypesTheLaunchLineWithoutPath()
    {
        var fake = Spawn();

        var doc = Core(fake).Invoke(["agent", "start", "alpha", "--kind", "claude", "--pane", "t5", "--spawn", "0123456789abcdef", "--", "--model", "x y"]);

        Assert.Equal(["hc", "label", "t5", "ah.name=alpha", "ah.spawn=0123456789abcdef"], fake.Argv(1));
        Assert.Equal(["hc", "send", "t5", "claude --model 'x y'"], fake.Argv(3));
        Assert.Equal("t5", doc.Member("result")!.Value.Member("agent")!.Value.Member("pane_id")!.Value.AsString());
    }

    [Fact]
    public void ALongPathStaysOffTheTypedLineAndTheBinaryIsNamedByItsAbsolutePath()
    {
        var dir = Directory.CreateTempSubdirectory("ah-onpath-").FullName;
        try
        {
            var claude = System.IO.Path.Combine(dir, "claude");
            File.WriteAllText(claude, "#!/bin/sh\n");
            File.SetUnixFileMode(claude, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var longPath = string.Join(':', Enumerable.Range(0, 60).Select(i => $"/nowhere/{i:D3}/some/long/bin")) + ":" + dir;
            var fake = Spawn();

            Core(fake, Env(("PATH", longPath))).Invoke(["agent", "start", "alpha", "--kind", "claude", "--pane", "t5", "--", "--x"]);

            var line = fake.Calls.Single(c => c.Args is ["send", ..]).Args[^1];
            Assert.Equal($"{claude} --x", line);
            Assert.DoesNotContain("PATH=", line, StringComparison.Ordinal);
            Assert.True(longPath.Length > 1024 && line.Length < 200);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ANonExecutableFileOnPathIsNotTakenForTheBinary()
    {
        var dir = Directory.CreateTempSubdirectory("ah-onpath-").FullName;
        try
        {
            File.WriteAllText(System.IO.Path.Combine(dir, "claude"), "not a program");
            File.SetUnixFileMode(System.IO.Path.Combine(dir, "claude"), UnixFileMode.UserRead | UnixFileMode.UserWrite);

            Assert.Null(HerdrCore.OnPath("claude", dir));
            Assert.Null(HerdrCore.OnPath("bin/claude", dir));
            Assert.Null(HerdrCore.OnPath("claude", "relative/bin"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Theory]
    [InlineData("--", "x\ny")]
    [InlineData("--cwd", "/w\u001b")]
    public void ALaunchWordWithAControlCharacterIsRefusedBeforeAnyCall(string flag, string word)
    {
        var fake = Spawn();

        var raw = Core(fake).InvokeAllowFailure(["agent", "start", "alpha", "--pane", "t5", flag, word]);

        Assert.False(raw.Ok);
        Assert.Contains("control character", raw.Stderr, StringComparison.Ordinal);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public void APathWithAControlCharacterIsRefusedBeforeAnyCall()
    {
        var fake = Spawn();

        var raw = Core(fake, Env(("PATH", "/usr/bin\n"))).InvokeAllowFailure(["agent", "start", "alpha", "--pane", "t5"]);

        Assert.False(raw.Ok);
        Assert.Contains("control character", raw.Stderr, StringComparison.Ordinal);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public void NegativeTheTargetPaneHoldingTheNameItselfIsNotAClash()
    {
        var fake = new Fake((bin, args) => args[0] == "ls" ? Out(Ls("t5")) : Out("{}"));

        var raw = Core(fake).InvokeAllowFailure(["agent", "start", "alpha", "--pane", "t5"]);

        Assert.True(raw.Ok);
        Assert.Contains(fake.Calls, c => c.Args[0] == "send");
    }

    /// <summary>A spawn whose pre-check finds the name free and whose post-check, after it labels t4, lists <paramref name="after"/>.</summary>
    private static Fake PostCheck(params string[] after)
    {
        var listed = 0;
        return new Fake((bin, args) => (bin, args[0]) switch
        {
            ("hc", "ls") => Out(listed++ == 0 ? Ls() : Ls(after)),
            ("hc", "run") => Out("{\"address\":\"t4\",\"state\":\"live\"}"),
            _ => Out("{}"),
        });
    }

    [Fact]
    public void OverlappingSpawnsBothYieldWhenThePostCheckShowsAnotherHolder()
    {
        // either side of the overlap sees the other in its post-check; each drops its own label, so both are refused
        var fake = PostCheck("t3", "t4");

        var raw = Core(fake).InvokeAllowFailure(["agent", "start", "alpha", "--cwd", "/w", "--", "--x"]);

        Assert.Equal("agent_name_taken", Doc(raw.Stdout).Member("error")!.Value.Member("code")!.Value.AsString());
        Assert.Equal(["hc", "label", "t4", "ah.name="], fake.Argv(^2));
        Assert.Equal(["hc", "kill", "t4"], fake.Argv(^1));
        Assert.DoesNotContain(fake.Calls, c => c.Args[0] == "get");
    }

    [Fact]
    public void NegativeAYieldIntoAnExistingPaneDropsItsLabelButKillsNothingAndLaunchesNothing()
    {
        var listed = 0;
        var fake = new Fake((bin, args) => args[0] == "ls" ? Out(listed++ == 0 ? Ls() : Ls("t3", "t5")) : Out("{}"));

        var raw = Core(fake).InvokeAllowFailure(["agent", "start", "alpha", "--pane", "t5"]);

        Assert.Equal("agent_name_taken", Doc(raw.Stdout).Member("error")!.Value.Member("code")!.Value.AsString());
        Assert.DoesNotContain(fake.Calls, c => c.Args[0] is "kill" or "send");
    }

    [Fact]
    public void AnUnreadablePostCheckIsAYieldWithItsOwnTryAgainDetailNotNameInUse()
    {
        var listed = 0;
        var fake = new Fake((bin, args) => (bin, args[0]) switch
        {
            ("hc", "ls") => listed++ == 0 ? Out(Ls()) : Fail,
            ("hc", "run") => Out("{\"address\":\"t4\",\"state\":\"live\"}"),
            _ => Out("{}"),
        });

        var raw = Core(fake).InvokeAllowFailure(["agent", "start", "alpha", "--cwd", "/w", "--", "--x"]);

        var error = Doc(raw.Stdout).Member("error")!.Value;
        Assert.Equal("agent_name_unchecked", error.Member("code")!.Value.AsString());
        Assert.Equal("could not check the name; try again", error.Member("message")!.Value.AsString());
        Assert.Equal(["hc", "kill", "t4"], fake.Argv(^1));
    }

    [Fact]
    public void AgentListSkipsATerminalThatVanishedBetweenTheListingAndItsGet()
    {
        var fake = new Fake((bin, args) => (bin, args[0]) switch
        {
            ("hc", "ls") => Out(Ls("t3", "t4")),
            ("hc", "get") when args[1] == "t3" => Fail,
            ("hc", "get") => Out(Get("t4", labels: "{\"ah.name\":\"beta\"}")),
            _ => Out("{}"),
        });

        var doc = Core(fake).Invoke(["agent", "list"]);

        var agents = doc.Member("result")!.Value.Member("agents")!.Value.AsArray();
        Assert.Equal("t4", Assert.Single(agents).Member("pane_id")!.Value.AsString());
    }

    [Fact]
    public void NegativeASpawnWhosePostCheckShowsOnlyItselfKeepsTheName()
    {
        var fake = PostCheck("t4");

        var raw = Core(fake).InvokeAllowFailure(["agent", "start", "alpha", "--cwd", "/w", "--", "--x"]);

        Assert.True(raw.Ok);
        Assert.DoesNotContain(fake.Calls, c => c.Args[0] == "label");
    }

    [Fact]
    public void ASpawnNonceFindsItsOneLiveTerminalAndNothingWhenTwoCarryIt()
    {
        var one = new Fake((_, args) => Out(Ls("t9")));
        var two = new Fake((_, args) => Out(Ls("t8", "t9")));

        Assert.Equal(new SpawnLookup("t9", null), Core(one).SpawnAddress("0123456789abcdef"));
        Assert.Equal(["hc", "ls", "--json", "--label", "ah.spawn=0123456789abcdef"], one.Argv(0));
        Assert.Null(Core(two).SpawnAddress("0123456789abcdef").Address);
        Assert.NotNull(Core(two).SpawnAddress("0123456789abcdef").Error);
    }

    /// <summary>A core whose <c>ah.spawn</c> label is on <paramref name="spawnHolders"/> and whose <c>ah.name=alpha</c> is on <paramref name="nameHolders"/>.</summary>
    private static Herdr Labelled(string[] spawnHolders, string[] nameHolders) =>
        Core(new Fake((_, args) =>
            args[0] == "ls" && args.Any(a => a.StartsWith("ah.spawn=", StringComparison.Ordinal)) ? Out(Ls(spawnHolders))
            : args[0] == "ls" ? Out(Ls(nameHolders))
            : args[0] == "get" ? Out(Get(args[1]))
            : Out("{}")));

    [Fact]
    public void APaneIsTiedToAMemberByItsSpawnNonceOrByItsOneNameLabel()
    {
        Assert.True(PaneTie.IsMembers(Labelled(["t9"], []), "t9", "0123456789abcdef", "alpha"));
        Assert.True(PaneTie.IsMembers(Labelled([], ["t9"]), "t9", "0123456789abcdef", "alpha"));
        Assert.True(PaneTie.IsMembers(Labelled([], ["t9"]), "t9", null, "alpha"));
    }

    [Fact]
    public void NegativeAPaneWhoseLabelsNameAnotherTerminalOrSeveralOrNoneIsNotTied()
    {
        Assert.False(PaneTie.IsMembers(Labelled(["t8"], ["t8"]), "t9", "0123456789abcdef", "alpha"));
        Assert.False(PaneTie.IsMembers(Labelled([], ["t8", "t9"]), "t9", "0123456789abcdef", "alpha"));
        Assert.False(PaneTie.IsMembers(Labelled([], []), "t9", "0123456789abcdef", "alpha"));
        Assert.False(PaneTie.IsMembers(Labelled(["t9"], ["t9"]), "t9", null, null));
    }

    [Fact]
    public void NegativeATuiPaneIsNeverTied()
    {
        var tui = new Herdr(new Fake((_, args) => Out(args.Contains("get") ? "{\"result\":{\"agent\":{\"pane_id\":\"t9\"}}}" : Ls("t9"))), Env(("HERDR_ENV", "1")), new NoFiles());

        Assert.False(PaneTie.IsMembers(tui, "t9", "0123456789abcdef", "alpha"));
    }

    [Fact]
    public void TheSpawnLookupAndTheAppProbePassNoSessionVariableToCore()
    {
        var saved = Environment.GetEnvironmentVariable("CLAUDE_CODE_CHILD_SESSION");
        Environment.SetEnvironmentVariable("CLAUDE_CODE_CHILD_SESSION", "1");
        try
        {
            var fake = new Fake((_, args) => Out(Ls("t9")));
            _ = Core(fake).SpawnAddress("0123456789abcdef");
            _ = Core(fake).AppPresent();

            Assert.Equal(2, fake.Calls.Count);
            Assert.All(fake.Calls, c => Assert.False(c.Env is null || c.Env.ContainsKey("CLAUDE_CODE_CHILD_SESSION")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLAUDE_CODE_CHILD_SESSION", saved);
        }
    }

    [Fact]
    public void StartWithNoPaneIsAHiddenHcRunWithNoParent()
    {
        var fake = Spawn();

        var doc = Core(fake).Invoke(["agent", "start", "alpha", "--kind", "claude", "--cwd", "/w", "--", "--x"]);

        Assert.Equal(["hc", "run", "--bg", "--json", "--no-parent", "--label", "ah.name=alpha", "--cwd", "/w", "--", "env", $"PATH={Path}", "claude", "--x"], fake.Argv(1));
        Assert.Equal("t4", doc.Member("result")!.Value.Member("agent")!.Value.Member("pane_id")!.Value.AsString());
        Assert.DoesNotContain(fake.Calls, c => c.Args[0] == "label");
    }

    [Fact]
    public void StartOnANameAlreadyLiveIsTheTuiNameCollision()
    {
        var fake = new Fake((bin, args) => args[0] == "ls" ? Out(Ls("t3")) : Out("{}"));

        var raw = Core(fake).InvokeAllowFailure(["agent", "start", "alpha", "--pane", "t5"]);

        Assert.Equal("agent_name_taken", Doc(raw.Stdout).Member("error")!.Value.Member("code")!.Value.AsString());
        Assert.Single(fake.Calls);
    }

    // --- move -------------------------------------------------------------------------------------------------------------------

    private const string NewTab = "tb25424cc8fccd622c2f5b9ec98b0a1912";

    /// <summary>
    /// The app as a move sees it: t5 sits in tab <c>tbold</c> until it is parked; a <c>keep --place right|down</c> puts it in the shown tab,
    /// <c>--place tab</c> in a new tab, and a relabel in the labelled tab. The user's view is tab <c>tbold</c>, pane p1.
    /// </summary>
    private sealed class MoveWorld
    {
        public bool Parked { get; set; }

        public string? InTab { get; set; }

        public string ShownTab { get; set; } = "tbold";

        public int GetsAfterPark { get; set; }

        public bool Adopts { get; set; } = true;

        public bool KeepFails { get; set; }

        public bool ParkFails { get; set; }
    }

    private static Fake MoveFake(MoveWorld w) => new((bin, args) =>
    {
        switch (bin, args[0], args.Length > 1 ? args[1] : null)
        {
            case ("herdr-app", "status", _):
                return Out("{\"space\":\"" + Space + "\",\"tab\":\"tbold\",\"focused_pane\":\"p1\",\"zoom\":1}");
            case ("herdr-app", "ls", _):
                var home = w.Parked ? string.Empty : ",{\"terminal\":\"t5\",\"pane\":\"p5\"}";
                string Here(string tab) => w.Adopts && w.InTab == tab ? ",{\"terminal\":\"t5\",\"pane\":\"p6\"}" : string.Empty;
                return Out("{\"spaces\":[{\"id\":\"" + Space + "\",\"tabs\":["
                    + "{\"id\":\"tbold\",\"terminals\":[{\"terminal\":\"t1\",\"pane\":\"p1\"}" + home + "]},"
                    + "{\"id\":\"" + Tab + "\",\"terminals\":[{\"terminal\":\"t2\",\"pane\":\"p2\"}" + Here(Tab) + "]},"
                    + "{\"id\":\"" + NewTab + "\",\"terminals\":[" + Here(NewTab).TrimStart(',') + "]}]},"
                    + "{\"id\":\"sp" + new string('a', 32) + "\",\"tabs\":[{\"id\":\"tb" + new string('a', 32) + "\",\"terminals\":[" + Here("tb" + new string('a', 32)).TrimStart(',') + "]}]}]}");
            case ("herdr-app", "do", "terminal.park"):
                if (w.ParkFails)
                {
                    return new MuxResult(false, string.Empty, "no such terminal", 1, false, "Command failed");
                }

                w.Parked = true;
                return Out("{\"pending\":true,\"terminal\":\"t5\"}");
            case ("herdr-app", "do", "tab.select"):
                w.ShownTab = args[3];
                return Out("{\"ok\":true}");
            case ("herdr-app", "do", "terminal.keep"):
                if (w.KeepFails && args.Contains("--place"))
                {
                    return new MuxResult(false, string.Empty, "change pending", 1, false, "Command failed");
                }

                if (args.Contains("--place"))
                {
                    w.InTab = args[^1] == "tab" ? NewTab : w.ShownTab;
                }

                return Out("{\"pending\":true,\"terminal\":\"t5\",\"pane\":\"p6\"" + (args.Contains("tab") ? ",\"tab\":\"" + NewTab + "\"" : string.Empty) + "}");
            case ("hc", "get", _):
                // the park lands on the second read after it
                var landed = w.Parked && ++w.GetsAfterPark >= 2;
                return Out(Get("t5", labels: landed ? "{\"ah.name\":\"alpha\"}" : $"{{\"ah.name\":\"alpha\",\"herdr.space\":\"{Space}\"}}"));
            case ("hc", "label", _):
                w.InTab = args.First(a => a.StartsWith("herdr.tab=", StringComparison.Ordinal))["herdr.tab=".Length..];
                return Out(string.Empty);
            default:
                return Out("{\"ok\":true}");
        }
    });

    private static List<string> Argvs(Fake fake) => fake.Calls.Select(c => string.Join(' ', [c.Bin, .. c.Args])).ToList();

    private static string Changed(JsonValue doc) => doc.Member("result")!.Value.Member("move_result")!.Value.Member("changed")!.Value.AsString();

    /// <summary>The view comes back last: its space, its tab, then its pane.</summary>
    private static void ViewRestored(List<string> argvs, int after)
    {
        var space = argvs.FindLastIndex(a => a == $"herdr-app do space.select --id {Space}");
        var tab = argvs.FindLastIndex(a => a == "herdr-app do tab.select --id tbold");
        var pane = argvs.FindLastIndex(a => a == "herdr-app do pane.focus --pane p1");
        Assert.True(space > after && tab > space && pane > tab, string.Join(" | ", argvs));
    }

    [Fact]
    public void MoveToATabSplitDownShowsTheTargetKeepsBelowItsFocusedPaneAndRestoresTheView()
    {
        var world = new MoveWorld();
        var fake = MoveFake(world);

        var doc = Core(fake).Invoke(["pane", "move", "t5", "--tab", Tab, "--split", "down"]);

        Assert.Equal("true", Changed(doc));
        var argvs = Argvs(fake);
        var park = argvs.IndexOf("herdr-app do terminal.park --terminal t5");
        var blankSeen = argvs.FindIndex(park, a => a == "hc get t5 --json") + 1; // the second get after the park is the blank one
        var showTab = argvs.IndexOf($"herdr-app do tab.select --id {Tab}");
        var keep = argvs.IndexOf("herdr-app do terminal.keep --terminal t5 --place down");
        Assert.True(park >= 0 && blankSeen > park && showTab > blankSeen && keep > showTab, string.Join(" | ", argvs));
        ViewRestored(argvs, keep);
        Assert.DoesNotContain(argvs, a => a.StartsWith("hc label", StringComparison.Ordinal));
    }

    [Fact]
    public void AFailedParkIsAMissThatReKeepsTheTerminalAndRestoresTheView()
    {
        var fake = MoveFake(new MoveWorld { ParkFails = true });

        var doc = Core(fake).Invoke(["pane", "move", "t5", "--tab", Tab, "--split", "down"]);

        Assert.Equal("false", Changed(doc));
        var argvs = Argvs(fake);
        var rekeep = argvs.IndexOf("herdr-app do terminal.keep --terminal t5");
        Assert.True(rekeep >= 0, string.Join(" | ", argvs));
        ViewRestored(argvs, rekeep);
    }

    [Fact]
    public void MoveToANewTabSelectsOnlyTheSpaceAndLetsTheAppMakeTheTab()
    {
        var world = new MoveWorld();
        var fake = MoveFake(world);

        var doc = Core(fake).Invoke(["pane", "move", "t5", "--new-tab"]);

        Assert.Equal("true", Changed(doc));
        Assert.Equal(NewTab, doc.Member("result")!.Value.Member("move_result")!.Value.Member("tab_id")!.Value.AsString());
        var argvs = Argvs(fake);
        var keep = argvs.IndexOf("herdr-app do terminal.keep --terminal t5 --place tab");
        Assert.True(keep > argvs.IndexOf($"herdr-app do space.select --id {Space}"));
        Assert.DoesNotContain(argvs.Take(keep), a => a.StartsWith("herdr-app do tab.select", StringComparison.Ordinal));
        Assert.DoesNotContain(argvs, a => a.StartsWith("hc label", StringComparison.Ordinal));
        ViewRestored(argvs, keep);
    }

    [Fact]
    public void MoveToANewWorkspaceParksThenRelabelsIntoMintedIdsAndMovesNoFocus()
    {
        var world = new MoveWorld();
        var fake = MoveFake(world);

        var doc = Core(fake).Invoke(["pane", "move", "t5", "--new-workspace"]);

        Assert.Equal("true", Changed(doc));
        var argvs = Argvs(fake);
        var park = argvs.IndexOf("herdr-app do terminal.park --terminal t5");
        var blankSeen = argvs.FindIndex(park, a => a == "hc get t5 --json") + 1;
        var label = argvs.IndexOf($"hc label t5 herdr.space=sp{new string('a', 32)} herdr.tab=tb{new string('a', 32)}");
        Assert.True(park >= 0 && blankSeen > park && label > blankSeen, string.Join(" | ", argvs));
        Assert.DoesNotContain(argvs, a => a.Contains(".select", StringComparison.Ordinal) || a.Contains("pane.focus", StringComparison.Ordinal) || a.Contains("terminal.keep", StringComparison.Ordinal));
    }

    [Fact]
    public void AMoveWhoseKeepFailsMidwayRestoresTheViewAndKeepsTheTerminalAgain()
    {
        var world = new MoveWorld { KeepFails = true };
        var fake = MoveFake(world);

        var doc = Core(fake).Invoke(["pane", "move", "t5", "--tab", Tab, "--split", "right"]);

        Assert.Equal("false", Changed(doc));
        var argvs = Argvs(fake);
        var rekeep = argvs.IndexOf("herdr-app do terminal.keep --terminal t5");
        Assert.True(rekeep > 0, string.Join(" | ", argvs));
        ViewRestored(argvs, rekeep);
    }

    [Fact]
    public void AMoveWhoseAdoptionMissesTheDeadlineKeepsTheTerminalAgainAndRestoresTheView()
    {
        var world = new MoveWorld { Adopts = false };
        var fake = MoveFake(world);
        var now = DateTimeOffset.UnixEpoch;

        var doc = Core(fake, now: () => now += TimeSpan.FromSeconds(1)).Invoke(["pane", "move", "t5", "--tab", Tab, "--split", "down"]);

        Assert.Equal("false", Changed(doc));
        var argvs = Argvs(fake);
        var rekeep = argvs.IndexOf("herdr-app do terminal.keep --terminal t5");
        Assert.True(rekeep > argvs.IndexOf("herdr-app do terminal.keep --terminal t5 --place down"), string.Join(" | ", argvs));
        ViewRestored(argvs, rekeep);
    }

    [Fact]
    public void AnAdoptionInsideTheDeadlineIsAMove()
    {
        var world = new MoveWorld();
        var now = DateTimeOffset.UnixEpoch;

        // 0.4 s per poll, as E-10 measured 0.3-0.5 s for the whole adoption
        var doc = Core(MoveFake(world), now: () => now += TimeSpan.FromMilliseconds(400)).Invoke(["pane", "move", "t5", "--tab", Tab, "--split", "right"]);

        Assert.Equal("true", Changed(doc));
    }

    [Fact]
    public void MintedIdsAreTheAppsFormat()
    {
        Assert.True(MuxIds.IsHerdrTabId("tb" + new string('a', 32)));
        Assert.True(MuxIds.IsHerdrAppId("sp" + new string('a', 32), "sp"));
    }

    // --- stream tabs ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void AStreamTabIsCreatedSplitAndClosed()
    {
        var fake = App(argv => argv switch
        {
            ["hc", "get", "t1", ..] => Out(Get("t1", labels: $"{{\"herdr.space\":\"{Space}\",\"herdr.tab\":\"{Tab}\"}}")),
            ["herdr-app", "do", "tab.new", ..] => Out($"{{ \"space\": \"{Space}\", \"tab\": \"tb25424cc8fccd622c2f5b9ec98b0a1912\", \"terminal\": \"t9\" }}"),
            _ => Out("{\"ok\":true}"),
        });
        var core = Core(fake);

        var made = core.Invoke(["tab", "create", "--label", "s1", "--cwd", "/wt", "--no-focus"]).Member("result")!.Value;
        core.Invoke(["tab", "close", "tb25424cc8fccd622c2f5b9ec98b0a1912"]);

        Assert.Equal("tb25424cc8fccd622c2f5b9ec98b0a1912", made.Member("tab")!.Value.Member("tab_id")!.Value.AsString());
        Assert.Equal("t9", made.Member("root_pane")!.Value.Member("pane_id")!.Value.AsString());
        var argvs = fake.Calls.Select(c => string.Join(' ', [c.Bin, .. c.Args])).ToList();
        Assert.Contains($"herdr-app do tab.new --space {Space} --name s1 --folder /wt", argvs);
        Assert.Contains($"herdr-app do space.select --id {Space}", argvs);
        Assert.Contains($"herdr-app do tab.select --id {Tab}", argvs);
        Assert.Equal("herdr-app do tab.close --id tb25424cc8fccd622c2f5b9ec98b0a1912", argvs[^1]);
        Assert.DoesNotContain(argvs, a => a.Contains("confirmed", StringComparison.Ordinal));
    }

    [Fact]
    public void ATabCloseTheAppRefusesIsReported()
    {
        var fake = new Fake((_, _) => new MuxResult(false, string.Empty, "last tab: use space.close", 1, false, "Command failed"));

        var error = Assert.Throws<InvalidOperationException>(() => Core(fake).Invoke(["tab", "close", Tab]));

        Assert.Contains("space.close", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(fake.Calls, c => c.Args.Contains("space.close"));
    }

    [Fact]
    public void AStreamTabWithoutTheAppIsRefusedByName()
    {
        var fake = new Fake((_, _) => Fail);

        var error = Assert.Throws<InvalidOperationException>(() => Core(fake).Invoke(["tab", "create", "--label", "s1", "--cwd", "/wt"]));

        Assert.Contains("`tab create` is not supported under herdr core", error.Message, StringComparison.Ordinal);
        Assert.Single(fake.Calls);
    }

    [Fact]
    public void TabListMapsTheAppTabs()
    {
        var tabs = Core(App()).Invoke(["tab", "list"]).Member("result")!.Value.Member("tabs")!.Value.AsArray();

        Assert.Equal(Tab, Assert.Single(tabs).Member("tab_id")!.Value.AsString());
        Assert.Equal("1", tabs[0].Member("label")!.Value.AsString());
    }

    // --- ids ---------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("t7", true)]
    [InlineData("t19", true)]
    [InlineData("w1:p2", true)]
    [InlineData("t2x", true)]
    [InlineData("t7a", true)]
    [InlineData("t12ab", true)]
    [InlineData("tab", false)]
    [InlineData("test", false)]
    [InlineData("tb0123456789abcdef0123456789abcdef", false)]
    [InlineData("t", false)]
    [InlineData("T7", false)]
    [InlineData("t-7", false)]
    [InlineData("machine/t7", false)]
    [InlineData("p12", false)]
    public void PaneIdsAcceptTheCoreAddress(string id, bool ok)
    {
        Assert.Equal(ok, MuxIds.IsHerdrPaneId(id));
        Assert.Equal(ok, LaunchModule.IsHerdrPaneId(id));
    }

    [Theory]
    [InlineData("tb7376b36fffa60df81c78b2c4a2745a41", true)]
    [InlineData("w1:t2", true)]
    [InlineData("tb7376b36fffa60df81c78b2c4a2745a4", false)]
    [InlineData("tb7376b36fffa60df81c78b2c4a2745a41f", false)]
    [InlineData("TB7376b36fffa60df81c78b2c4a2745a41", false)]
    [InlineData("tb7376b36fffa60df81c78b2c4a2745aZZ", false)]
    [InlineData("sp853e986c7c4d35d9eab014a6e4a8c546", false)]
    public void TabIdsAcceptTheAppForm(string id, bool ok) => Assert.Equal(ok, MuxIds.IsHerdrTabId(id));

    // --- layout rule with unknown sizes -------------------------------------------------------------------------------------------

    private static JsonArray Panes(params (string Id, double W, double H, bool Known)[] panes)
    {
        var a = new JsonArray();
        foreach (var p in panes)
        {
            a.Add(new JsonObject
            {
                ["pane_id"] = p.Id,
                ["rect"] = new JsonObject { ["x"] = 0, ["y"] = 0, ["width"] = p.W, ["height"] = p.H },
                ["size_known"] = p.Known,
            });
        }

        return a;
    }

    [Fact]
    public void AnUnknownSizeMakesTheSplitRightFromTheLargestKnownPane()
    {
        var geometry = Panes(("t1", 55, 29, true), ("t2", 200, 120, false), ("t3", 55, 13, true));

        var decision = new LayoutRules().NextSplit("grid", 4, "t1", ["t2", "t3"], geometry, positions: false);

        Assert.Equal(new SplitDecision("t1", "right"), decision);
    }

    [Fact]
    public void WithEveryCandidateUnknownTheSplitTargetsTheTabsLastPane()
    {
        var geometry = Panes(("t1", 200, 120, false), ("t9", 30, 30, true));

        Assert.Equal(new SplitDecision("t9", "right"), new LayoutRules().NextSplit("grid", 3, "t1", [], geometry, positions: false));
    }

    [Fact]
    public void KnownSizesStillTakeTheAspectRule()
    {
        var geometry = Panes(("t1", 55, 29, true));

        Assert.Equal(new SplitDecision("t1", "down"), new LayoutRules().NextSplit("grid", 3, "t1", [], geometry, positions: false));
    }
}

public sealed class HerdrCoreEnvironmentTests
{
    [Fact]
    public void NoSessionOrMemberIdentityReachesACoreCallButTheUsersConfigDirDoes()
    {
        var env = HerdrCore.CoreEnvironment(new Dictionary<string, string?>
        {
            ["PATH"] = "/bin", ["HC_TERMINAL_ID"] = "t1", ["CLAUDECODE"] = "1", ["CLAUDE_CODE_CHILD_SESSION"] = "1",
            ["CLAUDE_CODE_MESSAGING_TOKEN"] = "secret", ["CLAUDE_PID"] = "42", ["AH_MEMBER_NAME"] = "m",
            ["CLAUDE_CONFIG_DIR"] = "/cfg",
        });

        Assert.DoesNotContain(env.Keys, k => k is "CLAUDECODE" or "CLAUDE_PID" or "AH_MEMBER_NAME" || k.StartsWith("CLAUDE_CODE_", StringComparison.Ordinal));
        Assert.Equal("t1", env["HC_TERMINAL_ID"]);
        Assert.Equal("/cfg", env["CLAUDE_CONFIG_DIR"]);
    }

    [Fact]
    public void AnInheritedEnvironmentAlsoDropsThisProcesssTuiPane()
    {
        string[] names = ["AH_MEMBER_NAME", "HERDR_PANE_ID", "HERDR_SOCKET_PATH", "CLAUDE_CODE_CHILD_SESSION"];
        var saved = names.ToDictionary(n => n, Environment.GetEnvironmentVariable);
        Environment.SetEnvironmentVariable("AH_MEMBER_NAME", "ah2-x-implementor");
        Environment.SetEnvironmentVariable("HERDR_PANE_ID", "w1:p2");
        Environment.SetEnvironmentVariable("HERDR_SOCKET_PATH", "/tmp/tui.sock");
        Environment.SetEnvironmentVariable("CLAUDE_CODE_CHILD_SESSION", "1");
        try
        {
            var env = HerdrCore.CoreEnvironment(null);

            Assert.False(env.ContainsKey("AH_MEMBER_NAME"));
            Assert.False(env.ContainsKey("HERDR_PANE_ID"));
            Assert.False(env.ContainsKey("HC_SOCKET"));
            Assert.False(env.ContainsKey("CLAUDE_CODE_CHILD_SESSION"));
            Assert.True(env.ContainsKey("PATH"));
        }
        finally
        {
            foreach (var (k, v) in saved)
            {
                Environment.SetEnvironmentVariable(k, v);
            }
        }
    }

    [Fact]
    public void APinnedTuiSocketStillBecomesHcSocket()
    {
        var env = HerdrCore.CoreEnvironment(new Dictionary<string, string?> { ["HERDR_SOCKET_PATH"] = "/s.sock" });

        Assert.Equal("/s.sock", env["HC_SOCKET"]);
    }
}
