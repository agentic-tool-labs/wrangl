using System.Text;
using Ah.Abstractions;
using Ah.Cli.Verbs;
using Ah.TestKit;

namespace Ah.Cli.Tests;

/// <summary><c>ah view open --kind tui</c>: the herdr calls it makes, in what order, how many, and what it keeps.</summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class ViewTuiTests : IDisposable
{
    private const string Here = "w1:p1";
    private const string Made = "w1:p7";

    private readonly Sandbox _sandbox = new();

    public void Dispose() => _sandbox.Dispose();

    private sealed class Herdr(Func<IReadOnlyList<string>, (int Code, string Out, string Err)> answer) : IProcessRunner
    {
        public List<string> Calls { get; } = [];

        public ProcessResult Run(ProcessRequest request)
        {
            Calls.Add(string.Join(' ', request.Argv));
            var (code, o, e) = answer(request.Argv);
            return new ProcessResult(code, null, false, false, Encoding.UTF8.GetBytes(o), Encoding.UTF8.GetBytes(e));
        }
    }

    private sealed record Outcome(int Code, string Out, string Err, Herdr Herdr);

    private string Record(string session) => Path.Combine(_sandbox.AhHome, "run", "view-tui", session);

    private Outcome Run(Herdr herdr, string? pane = Here, string session = "sess-1", bool withAhd = true)
    {
        var variables = new Dictionary<string, string?> { ["AH_TEST"] = "1", ["AH_HOME"] = _sandbox.AhHome, ["HOME"] = _sandbox.Home, ["HERDR_PANE_ID"] = pane };
        var env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables, new PluginInfo(_sandbox.PluginRoot, "wrangl", "1.0.0", "1"));
        var ah = _sandbox.Executable("install/ah");
        if (withAhd)
        {
            _sandbox.Executable("install/ahd");
        }

        var context = new CliContext(env, new RecordingExec(), herdr, "ah", ah, _sandbox.Home, 501);
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = ViewVerb.Run(context, new FakeDaemonClient(), ["open", "--kind", "tui", "--session", session], stdout, stderr);
        return new Outcome(code, stdout.ToString().Replace("\r\n", "\n", StringComparison.Ordinal), stderr.ToString(), herdr);
    }

    private static string Split(string id = Made) => $"{{\"id\":\"cli:pane:split\",\"result\":{{\"pane\":{{\"agent_status\":\"unknown\",\"pane_id\":\"{id}\",\"tab_id\":\"w1:t1\",\"terminal_id\":\"term_1\",\"workspace_id\":\"w1\"}},\"type\":\"pane_info\"}}}}";

    private static (int, string, string) Ok(string o = "{}") => (0, o, string.Empty);

    private void Keep(string session, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Record(session))!);
        File.WriteAllText(Record(session), content);
    }

    [Fact]
    public void NoRecordSplitsRunsAndKeepsThePaneIdForTheUserAlone()
    {
        var herdr = new Herdr(a => a[2] == "split" ? Ok(Split()) : Ok());
        var r = Run(herdr);

        Assert.Equal(0, r.Code);
        Assert.Equal("TUI opened\n", r.Out);
        Assert.Equal(2, herdr.Calls.Count);
        Assert.Equal($"herdr pane split --pane {Here} --direction right --focus", herdr.Calls[0]);
        Assert.Equal($"herdr pane run {Made} {_sandbox.Root}/install/ahd tui", herdr.Calls[1]);
        Assert.Equal(Made + "\n", File.ReadAllText(Record("sess-1")));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Record("sess-1")));
    }

    [Fact]
    public void AnAliveRecordedPaneIsGotThenFocusedAndNothingIsSplit()
    {
        Keep("sess-1", Made + "\n");
        var herdr = new Herdr(_ => Ok("{\"id\":\"cli:pane:get\",\"result\":{\"pane\":{\"pane_id\":\"w1:p7\",\"tab_id\":\"w1:t1\"},\"type\":\"pane_info\"}}"));
        var r = Run(herdr);

        Assert.Equal(0, r.Code);
        Assert.Equal("TUI focused\n", r.Out);
        Assert.Equal([$"herdr pane get {Made}", $"herdr agent focus {Made}"], herdr.Calls);
    }

    [Fact]
    public void APaneThatIsNotAnAgentFallsBackToItsTabWithinThreeCalls()
    {
        Keep("sess-1", Made);
        var herdr = new Herdr(a => a[1] == "agent" ? (1, string.Empty, "{\"error\":{\"code\":\"agent_not_found\"}}") : Ok("{\"id\":\"cli:pane:get\",\"result\":{\"pane\":{\"pane_id\":\"w1:p7\",\"tab_id\":\"w1:t3\"},\"type\":\"pane_info\"}}"));
        var r = Run(herdr);

        Assert.Equal(0, r.Code);
        Assert.Equal([$"herdr pane get {Made}", $"herdr agent focus {Made}", "herdr tab focus w1:t3"], herdr.Calls);
    }

    [Fact]
    public void AFocusThatFailsForAnotherReasonIsNotRetriedAndTheTabIsNotTried()
    {
        Keep("sess-1", Made);
        var herdr = new Herdr(a => a[1] == "agent" ? (1, string.Empty, "{\"error\":{\"code\":\"boom\"}}") : Ok("{\"id\":\"cli:pane:get\",\"result\":{\"pane\":{\"tab_id\":\"w1:t3\"},\"type\":\"pane_info\"}}"));
        var r = Run(herdr);

        Assert.Equal(3, r.Code);
        Assert.Equal(2, herdr.Calls.Count);
        Assert.Equal("TUI did not open; run ahd tui in a terminal", r.Out.Trim());
        Assert.Contains("herdr could not focus the pane", r.Err, StringComparison.Ordinal);
    }

    [Fact]
    public void ADeadRecordedPaneIsGotThenSplitAndRunAndTheRecordIsRewritten()
    {
        Keep("sess-1", "w1:p3\n");
        var herdr = new Herdr(a => a[2] == "get" ? (1, string.Empty, "{\"error\":{\"code\":\"pane_not_found\"}}") : a[2] == "split" ? Ok(Split()) : Ok());
        var r = Run(herdr);

        Assert.Equal(0, r.Code);
        Assert.Equal(3, herdr.Calls.Count);
        Assert.Equal("herdr pane get w1:p3", herdr.Calls[0]);
        Assert.StartsWith("herdr pane split ", herdr.Calls[1], StringComparison.Ordinal);
        Assert.StartsWith($"herdr pane run {Made} ", herdr.Calls[2], StringComparison.Ordinal);
        Assert.Equal(Made + "\n", File.ReadAllText(Record("sess-1")));
        Assert.DoesNotContain(herdr.Calls, c => c.Contains("close", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("term_abc")]
    [InlineData("w1:p1; rm -rf /")]
    public void OutsideHerdrNothingIsCalledAndTheCommandIsNamed(string? pane)
    {
        var herdr = new Herdr(_ => Ok());
        var r = Run(herdr, pane);

        Assert.Equal(3, r.Code);
        Assert.Equal("Run ahd tui in a terminal\n", r.Out);
        Assert.Empty(herdr.Calls);
        Assert.False(File.Exists(Record("sess-1")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"result\":{\"pane\":{}}}")]
    [InlineData("{\"result\":{}}")]
    [InlineData("{\"result\":[]}")]
    [InlineData("{\"result\":{\"pane\":{\"pane_id\":7}}}")]
    [InlineData("{\"result\":{\"pane\":{\"pane_id\":\"\"}}}")]
    [InlineData("{\"result\":{\"pane\":{\"pane_id\":\"term_x\"}}}")]
    [InlineData("{\"result\":{\"pane\":{\"pane_id\":\"w1:p7 extra\"}}}")]
    [InlineData("{\"pane\":{\"pane_id\":\"w1:p7\"}}")]
    [InlineData("{\"error\":{\"code\":\"pane_not_found\",\"message\":\"x\"},\"id\":\"cli:pane:split\"}")]
    public void SplitOutputWithoutAPaneIdIsRefusedAndNothingIsRunOrKept(string output)
    {
        var herdr = new Herdr(_ => Ok(output));
        var r = Run(herdr);

        Assert.Equal(3, r.Code);
        Assert.Equal("TUI did not open; run ahd tui in a terminal", r.Out.Trim());
        Assert.Contains("unexpected herdr output", r.Err, StringComparison.Ordinal);
        Assert.Single(herdr.Calls);
        Assert.False(File.Exists(Record("sess-1")));
    }

    [Fact]
    public void ASplitThatExitsNonZeroIsExitThreeWithNoRunAndNoRecord()
    {
        var herdr = new Herdr(_ => (1, string.Empty, "x"));
        var r = Run(herdr);

        Assert.Equal(3, r.Code);
        Assert.Single(herdr.Calls);
        Assert.False(File.Exists(Record("sess-1")));
    }

    [Fact]
    public void ARunThatExitsNonZeroIsExitThreeWithNoRecordAndNoClose()
    {
        var herdr = new Herdr(a => a[2] == "split" ? Ok(Split()) : (1, string.Empty, "x"));
        var r = Run(herdr);

        Assert.Equal(3, r.Code);
        Assert.Equal(2, herdr.Calls.Count);
        Assert.False(File.Exists(Record("sess-1")));
    }

    [Fact]
    public void WithNoAhdBesideTheBinaryTheNewPaneIsNotRunAndNothingIsKept()
    {
        var herdr = new Herdr(_ => Ok(Split()));
        var r = Run(herdr, withAhd: false);

        Assert.Equal(3, r.Code);
        Assert.Single(herdr.Calls);
        Assert.Contains("ahd not found", r.Err, StringComparison.Ordinal);
        Assert.False(File.Exists(Record("sess-1")));
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("a/b")]
    [InlineData(".hidden")]
    [InlineData("")]
    [InlineData("a b")]
    public void ASessionThatIsNotAPlainNameIsAUsageErrorAndNothingIsCalled(string session)
    {
        var herdr = new Herdr(_ => Ok());
        var r = Run(herdr, session: session);

        Assert.Equal(2, r.Code);
        Assert.Empty(herdr.Calls);
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("w1:p7 w1:p8")]
    [InlineData("")]
    public void ARecordThatIsNotAPaneIdIsIgnoredAndTheSplitPathRuns(string content)
    {
        Keep("sess-1", content);
        var herdr = new Herdr(a => a[2] == "split" ? Ok(Split()) : Ok());
        var r = Run(herdr);

        Assert.Equal(0, r.Code);
        Assert.DoesNotContain(herdr.Calls, c => c.StartsWith("herdr pane get", StringComparison.Ordinal));
    }
}
