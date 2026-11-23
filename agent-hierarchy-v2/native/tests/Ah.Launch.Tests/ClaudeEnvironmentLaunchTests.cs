using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.Launch.Tests;

/// <summary>
/// ah2-570: a Claude member's recorded <c>claude_config_dir</c> is put on its own launch, per transport: a path sets
/// <c>CLAUDE_CONFIG_DIR</c>, an empty value unsets it, an absent value changes nothing.
/// </summary>
public sealed class ClaudeEnvironmentLaunchTests
{
    private const string MemberName = "alpha-implementor";

    /// <summary>A path with a space and a single quote, which must reach the member as one word.</summary>
    private const string Odd = "/cfg/a b'c";

    private sealed class FakeSpawner : IDetachedSpawner
    {
        internal List<DetachedSpawnRequest> Requests { get; } = [];

        public int SpawnDetached(DetachedSpawnRequest request)
        {
            Requests.Add(request);
            return 9;
        }
    }

    private static JsonObject Member(string? claudeConfig, string? secureStorage = null)
    {
        var member = new JsonObject { ["role"] = "implementor", ["name"] = MemberName };
        if (claudeConfig is not null)
        {
            member[LaunchModule.ClaudeConfigField] = claudeConfig;
        }

        if (secureStorage is not null)
        {
            member[LaunchModule.ClaudeEnvironment[1].Field] = secureStorage;
        }

        return member;
    }

    private static ScenarioRig Rig(Action<Dictionary<string, string>>? env = null)
    {
        var rig = ScenarioRig.Create(env);
        var claude = rig.Sandbox.Root + "/bin/claude";
        File.WriteAllText(claude, "#!/bin/sh\n");
        File.SetUnixFileMode(claude, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return rig;
    }

    private static string Launch(ScenarioRig rig, string? claudeConfig, string transport, string? secureStorage = null) =>
        rig.Shapes.SpawnShape(Member(claudeConfig, secureStorage), transport, rig.Context).Shape.Get("launch")!.Value.AsArray()[0].AsString();

    /// <summary>
    /// The command tmux types into the pane: the single-quoted word between <c>&lt;TARGET&gt; </c> and <c> Enter</c>, unquoted the way
    /// <c>/bin/sh</c> reads it (the only escape inside is <c>'\''</c>, so nothing in it is expanded).
    /// </summary>
    private static string TmuxTyped(ScenarioRig rig, string? claudeConfig)
    {
        var line = Launch(rig, claudeConfig, "tmux");
        var word = line[(line.IndexOf("<TARGET> ", StringComparison.Ordinal) + "<TARGET> ".Length)..^" Enter".Length];
        Assert.True(word.StartsWith('\'') && word.EndsWith('\''), word);
        return word[1..^1].Replace("'\\''", "'", StringComparison.Ordinal);
    }

    /// <summary>A value a shell could split or expand: a space, a single quote, a variable, a command substitution and a backtick.</summary>
    private const string Expandable = "/cfg/a b'c/$x/$(id)/`id`";

    /// <summary>Runs <paramref name="script"/> with <c>/bin/sh -c</c> (with <c>x</c> set, so an expanded <c>$x</c> would show) and returns its stdout.</summary>
    private static string Sh(string script)
    {
        var start = new System.Diagnostics.ProcessStartInfo("/bin/sh", ["-c", script]) { RedirectStandardOutput = true, UseShellExecute = false };
        start.Environment["x"] = "EXPANDED";
        using var sh = System.Diagnostics.Process.Start(start)!;
        var output = sh.StandardOutput.ReadToEnd();
        Assert.True(sh.WaitForExit(10000));
        return output;
    }

    /// <summary>What a shell running <paramref name="line"/> gives <c>claude</c> as CLAUDE_CONFIG_DIR: the words before <paramref name="claude"/>, then printenv.</summary>
    private static string SeenByClaude(string line, string claude = " claude ") =>
        Sh(line[..line.IndexOf(claude, StringComparison.Ordinal)] + " printenv CLAUDE_CONFIG_DIR").TrimEnd('\n');

    private static JsonObject Shaped(ScenarioRig rig, string? claudeConfig, string transport, string? secureStorage = null)
    {
        var outcome = rig.Shapes.SpawnShape(Member(claudeConfig, secureStorage), transport, rig.Context);
        Assert.Null(outcome.Fail);
        var member = Member(claudeConfig, secureStorage);
        member["spawn"] = outcome.Shape;
        return member;
    }

    // ---------------------------------------------------------------- tmux: an export or unset, then claude; the --bg fallback: env

    [Fact]
    public void TmuxCarriesAPathAsOneQuotedExportWord()
    {
        Assert.StartsWith("export CLAUDE_CONFIG_DIR='/cfg/a b'\\''c'; claude --agent ", TmuxTyped(Rig(), Odd), StringComparison.Ordinal);
    }

    [Fact]
    public void TmuxRunsAShellFunctionNamedClaude()
    {
        var rig = Rig();

        Assert.Equal("function /cfg/a b'c", Sh("claude() { echo \"function $CLAUDE_CONFIG_DIR\"; }; " + TmuxTyped(rig, Odd)).TrimEnd('\n'));
        Assert.Equal("function ", Sh("CLAUDE_CONFIG_DIR=/x; export CLAUDE_CONFIG_DIR; claude() { echo \"function $CLAUDE_CONFIG_DIR\"; }; " + TmuxTyped(rig, string.Empty)).TrimEnd('\n'));
    }

    [Fact]
    public void TmuxPassesDollarsAndBackticksToThePaneUnexpanded()
    {
        var rig = Rig();
        var line = Launch(rig, Expandable, "tmux");

        // the launch line as /bin/sh runs it, with printf standing in for tmux: what it prints is exactly what tmux receives
        var typed = Sh("printf %s " + line["tmux send-keys -t <TARGET> ".Length..^" Enter".Length]);

        Assert.StartsWith("export CLAUDE_CONFIG_DIR='/cfg/a b'\\''c/$x/$(id)/`id`'; claude --agent ", typed, StringComparison.Ordinal);
        Assert.Equal(TmuxTyped(rig, Expandable), typed);
        // and the pane's shell, running what tmux typed, hands claude the value unexpanded
        Assert.Equal(Expandable, SeenByClaude(typed));
    }

    [Fact]
    public void TheBackgroundLineHandsClaudeTheValueUnexpanded()
    {
        Assert.Equal(Expandable, SeenByClaude(Launch(Rig(), Expandable, "terminal")));
    }

    [Fact]
    public void TheTuiLineGivesThePaneShellTheValueUnexpanded()
    {
        Assert.Equal(Expandable, Sh(MemberLauncher.ClaudeConfigShellLine([("CLAUDE_CONFIG_DIR", Expandable)]) + "; printenv CLAUDE_CONFIG_DIR").TrimEnd('\n'));
        Assert.Equal("unset", Sh("CLAUDE_CONFIG_DIR=/x; export CLAUDE_CONFIG_DIR; " + MemberLauncher.ClaudeConfigShellLine([("CLAUDE_CONFIG_DIR", string.Empty)]) + "; printenv CLAUDE_CONFIG_DIR || echo unset").TrimEnd('\n'));
    }

    [Fact]
    public void TmuxUnsetsWhenTheValueIsEmpty()
    {
        Assert.StartsWith("unset CLAUDE_CONFIG_DIR; claude --agent ", TmuxTyped(Rig(), string.Empty), StringComparison.Ordinal);
    }

    [Fact]
    public void TmuxWithNoRecordedValueTypesClaudeAsBefore()
    {
        var typed = TmuxTyped(Rig(), null);

        Assert.StartsWith("claude --agent ", typed, StringComparison.Ordinal);
        Assert.DoesNotContain("CLAUDE_CONFIG_DIR", typed, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBackgroundFallbackCarriesSetUnsetAndNothing()
    {
        var rig = Rig();

        Assert.StartsWith("env CLAUDE_CONFIG_DIR='/cfg/a b'\\''c' claude ", Launch(rig, Odd, "terminal"), StringComparison.Ordinal);
        Assert.StartsWith("env -u CLAUDE_CONFIG_DIR claude ", Launch(rig, string.Empty, "terminal"), StringComparison.Ordinal);
        Assert.StartsWith("claude ", Launch(rig, null, "terminal"), StringComparison.Ordinal);
    }

    [Fact]
    public void TheBackgroundLineWithOneSetAndOneUnsetPutsTheUnsetFirstAndRuns()
    {
        var line = Launch(Rig(), Odd, "terminal", secureStorage: string.Empty);

        // env reads options only before its first assignment: `env A=x -u B claude` runs a command named -u
        Assert.StartsWith("env -u CLAUDE_SECURESTORAGE_CONFIG_DIR CLAUDE_CONFIG_DIR='/cfg/a b'\\''c' claude ", line, StringComparison.Ordinal);
        Assert.Equal(Odd, SeenByClaude(line));
    }

    [Fact]
    public void TheBackgroundLineWithBothSetKeepsListOrder()
    {
        Assert.StartsWith("env CLAUDE_CONFIG_DIR='/a' CLAUDE_SECURESTORAGE_CONFIG_DIR='/b' claude ", Launch(Rig(), "/a", "terminal", secureStorage: "/b"), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- headless: the child's environment

    private static DetachedSpawnRequest Headless(string? claudeConfig, string? callerValue)
    {
        var rig = Rig();
        var spawner = new FakeSpawner();
        var caller = new Dictionary<string, string?> { ["PATH"] = rig.Sandbox.Root + "/bin" };
        if (callerValue is not null)
        {
            caller["CLAUDE_CONFIG_DIR"] = callerValue;
        }

        var launcher = rig.Launcher with { Spawner = spawner, ProcessEnvironment = caller };
        var member = Shaped(rig, claudeConfig, "headless");
        member["headless_prompt"] = "go";
        _ = launcher.LaunchMemberCore(member, "headless", rig.Context with { MemberTmp = new Dictionary<string, string> { [MemberName] = rig.Sandbox.Root + "/membertmp" } });
        return Assert.Single(spawner.Requests);
    }

    [Fact]
    public void AHeadlessChildGetsTheRecordedPathNotTheCallers()
    {
        Assert.Equal(Odd, Headless(Odd, "/caller/cfg").Environment["CLAUDE_CONFIG_DIR"]);
    }

    [Fact]
    public void AHeadlessChildHasNoValueWhenTheRecordIsEmptyEvenIfTheCallerHasOne()
    {
        Assert.False(Headless(string.Empty, "/caller/cfg").Environment.ContainsKey("CLAUDE_CONFIG_DIR"));
    }

    [Fact]
    public void AHeadlessChildWithNoRecordedValueKeepsTheCallersAsBefore()
    {
        Assert.Equal("/caller/cfg", Headless(null, "/caller/cfg").Environment["CLAUDE_CONFIG_DIR"]);
    }

    // ---------------------------------------------------------------- herdr TUI: one line typed into the pane before agent start

    /// <summary>A TUI launch into pane 1-2 (not the caller's own, 1-1); <paramref name="foreground"/> is what runs in that pane, its shell when null.</summary>
    private static List<string> TuiCalls(string? claudeConfig, (int Pid, string[]? Argv)[]? foreground = null) => TuiLaunch(claudeConfig, foreground, out _);

    private static List<string> TuiLaunch(string? claudeConfig, (int Pid, string[]? Argv)[]? foreground, out JsonObject result)
    {
        var rig = Rig();
        rig.Mux.Panes.Add("1-2");
        rig.Mux.Rects["1-2"] = (100, 0, 100, 50);
        if (foreground is not null)
        {
            rig.Mux.Foreground["1-2"] = foreground;
        }

        rig.Mux.Override = request => request.Argv is ["herdr", "pane", "run", ..] ? FakeMux.Out("{}") : null;
        var member = Shaped(rig, claudeConfig, "herdr");
        member["transport_id"] = "1-2";
        result = rig.Launcher.LaunchMemberCore(member, "herdr", rig.Context);
        return rig.Mux.Calls;
    }

    [Fact]
    public void ATuiPaneWhoseAgentStillRunsIsNotTypedIntoAndTheLaunchFailsAsBusy()
    {
        var calls = TuiLaunch(Odd, [(FakeMux.ShellPid, ["-zsh"]), (4321, ["claude", "--agent", "wrangl:implementor"])], out var result);

        Assert.Equal("failed", result.Get("launch_status")?.AsString());
        Assert.Equal("pane-busy", result.Get("launch_result")?.Member("reason")?.AsString());
        Assert.Contains("pane 1-2 is busy", result.Get("error")?.AsString(), StringComparison.Ordinal);
        Assert.DoesNotContain(calls, c => c.StartsWith("herdr pane run", StringComparison.Ordinal) || c.Contains("agent start", StringComparison.Ordinal));
    }

    [Fact]
    public void TheCallersOwnPaneIsNeverTypedInto()
    {
        var rig = Rig();
        var member = Shaped(rig, Odd, "herdr");
        member["transport_id"] = "1-1";

        var result = rig.Launcher.LaunchMemberCore(member, "herdr", rig.Context);

        Assert.Equal("pane-busy", result.Get("launch_result")?.Member("reason")?.AsString());
        Assert.DoesNotContain(rig.Mux.Calls, c => c.StartsWith("herdr pane run", StringComparison.Ordinal));
    }

    [Fact]
    public void TheTuiPaneGetsAnExportBeforeTheAgentStart()
    {
        var calls = TuiCalls(Odd);

        var export = calls.FindIndex(c => c == "herdr pane run 1-2 export CLAUDE_CONFIG_DIR='/cfg/a b'\\''c'");
        var start = calls.FindIndex(c => c.Contains("herdr agent start", StringComparison.Ordinal));
        Assert.True(export >= 0 && start > export, string.Join(" | ", calls));
    }

    [Fact]
    public void TheTuiPaneGetsAnUnsetWhenTheValueIsEmpty()
    {
        Assert.Contains("herdr pane run 1-2 unset CLAUDE_CONFIG_DIR", TuiCalls(string.Empty));
    }

    [Fact]
    public void TheTuiPaneGetsNothingTypedWithNoRecordedValue()
    {
        var calls = TuiCalls(null);

        Assert.DoesNotContain(calls, c => c.StartsWith("herdr pane run", StringComparison.Ordinal));
        Assert.Contains(calls, c => c.Contains("herdr agent start", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- herdr core: the hidden terminal's command is env … claude

    private static string CoreRun(string? claudeConfig, string? secureStorage = null) => CoreCall(claudeConfig, pane: null, out _, secureStorage);

    /// <summary>
    /// One launch under herdr core: into <paramref name="pane"/> when given (the line <c>hc send</c> types comes back), else a hidden
    /// terminal (its argv after <c>--</c> comes back, joined by U+0001). <paramref name="root"/> is the rig's sandbox root.
    /// </summary>
    private static string CoreCall(string? claudeConfig, string? pane, out string root, string? secureStorage = null)
    {
        var rig = Rig(env =>
        {
            env.Remove("HERDR_ENV");
            env.Remove("HERDR_PANE_ID");
            env["HC_TERMINAL_ID"] = "t1";
        });
        rig.Mux.Override = request => request.Argv switch
        {
            ["hc", "ls", ..] => FakeMux.Out("{\"hidden_finished\":0,\"terminals\":[]}"),
            ["hc", "run", ..] => FakeMux.Out("{\"address\":\"t7\",\"agent\":\"claude\",\"state\":\"live\"}"),
            ["hc", ..] => FakeMux.Out("{}"),
            _ => null,
        };
        root = rig.Sandbox.Root;
        var member = Shaped(rig, claudeConfig, "herdr", secureStorage);
        if (pane is not null)
        {
            member["transport_id"] = pane;
        }

        _ = rig.Launcher.LaunchMemberCore(member, "herdr", rig.Context);
        if (pane is not null)
        {
            return Assert.Single(rig.Mux.Requests, r => r.Argv is ["hc", "send", ..]).Argv[^1];
        }

        var request = Assert.Single(rig.Mux.Requests, r => r.Argv is ["hc", "run", ..]);
        var argv = request.Argv.ToList();
        return string.Join('\u0001', argv.Skip(argv.IndexOf("--") + 1));
    }

    [Fact]
    public void TheCoreHiddenTerminalRunsEnvWithThePathAsOneArgument()
    {
        var words = CoreRun(Odd).Split('\u0001');

        Assert.Equal("env", words[0]);
        Assert.StartsWith("PATH=", words[1], StringComparison.Ordinal);
        Assert.Equal(["env", "CLAUDE_CONFIG_DIR=" + Odd], words[2..4]);
        Assert.EndsWith("/bin/claude", words[4], StringComparison.Ordinal);
    }

    [Fact]
    public void TheCoreHiddenTerminalUnsetsWhenTheValueIsEmpty()
    {
        var words = CoreRun(string.Empty).Split('\u0001');

        Assert.Equal(["env", "-u", "CLAUDE_CONFIG_DIR"], words[2..5]);
        Assert.EndsWith("/bin/claude", words[5], StringComparison.Ordinal);
    }

    [Fact]
    public void TheCoreHiddenTerminalWithOneSetAndOneUnsetPutsTheUnsetFirst()
    {
        var words = CoreRun(Odd, secureStorage: string.Empty).Split('\u0001');

        Assert.Equal(["env", "-u", "CLAUDE_SECURESTORAGE_CONFIG_DIR", "CLAUDE_CONFIG_DIR=" + Odd], words[2..6]);
        Assert.EndsWith("/bin/claude", words[6], StringComparison.Ordinal);
    }

    [Fact]
    public void TheCoreHiddenTerminalWithBothSetKeepsListOrder()
    {
        var words = CoreRun("/a", secureStorage: "/b").Split('\u0001');

        Assert.Equal(["env", "CLAUDE_CONFIG_DIR=/a", "CLAUDE_SECURESTORAGE_CONFIG_DIR=/b"], words[2..5]);
    }

    [Fact]
    public void TheCoreHiddenTerminalWithNoRecordedValueRunsClaudeAsBefore()
    {
        var words = CoreRun(null).Split('\u0001');

        Assert.Equal("claude", words[2]);
        Assert.DoesNotContain(words, w => w.Contains("CLAUDE_CONFIG_DIR", StringComparison.Ordinal));
    }

    [Fact]
    public void TheCoreLineTypedIntoAPaneStartsWithEnvAndThePathAsOneQuotedWord()
    {
        var typed = CoreCall(Odd, "t5", out var root);

        Assert.StartsWith("/usr/bin/env 'CLAUDE_CONFIG_DIR=/cfg/a b'\\''c' " + root + "/bin/claude --agent ", typed, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCoreLineTypedIntoAPaneHandsClaudeTheValueUnexpanded()
    {
        var typed = CoreCall(Expandable, "t5", out var root);

        Assert.Equal(Expandable, SeenByClaude(typed, " " + root + "/bin/claude "));
    }

    [Fact]
    public void TheCoreLineTypedIntoAPaneUnsetsWhenTheValueIsEmpty()
    {
        var typed = CoreCall(string.Empty, "t5", out var root);

        Assert.StartsWith("/usr/bin/env -u CLAUDE_CONFIG_DIR " + root + "/bin/claude --agent ", typed, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCoreLineTypedIntoAPaneWithNoRecordedValueIsClaudeAsBefore()
    {
        var typed = CoreCall(null, "t5", out var root);

        Assert.StartsWith(root + "/bin/claude --agent ", typed, StringComparison.Ordinal);
        Assert.DoesNotContain("CLAUDE_CONFIG_DIR", typed, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- the launch rule reads settings.json from the resolved directory

    private static string StatusCommand(ScenarioRig rig, string? claudeConfig)
    {
        var argv = rig.Shapes.SpawnShape(Member(claudeConfig), "headless", rig.Context).Shape.Get("argv")!.Value.AsArray().Select(x => x.AsString()).ToList();
        return JsonText.Parse(argv[argv.IndexOf("--settings") + 1]).Member("statusLine")!.Value.Member("command")!.Value.AsString();
    }

    private static void StatusLineIn(string dir, string command)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(dir + "/settings.json", "{\"statusLine\":{\"type\":\"command\",\"command\":\"" + command + "\"}}");
    }

    [Fact]
    public void TheStatusLineIsReadFromTheRecordedConfigurationDirectory()
    {
        var rig = Rig();
        var alt = rig.Sandbox.Root + "/alt claude";
        StatusLineIn(alt, "alt-line");
        StatusLineIn(rig.Sandbox.Home + "/.claude", "home-line");

        Assert.Contains("alt-line", StatusCommand(rig, alt), StringComparison.Ordinal);
    }

    /// <summary>A rig whose own process has <c>CLAUDE_CONFIG_DIR</c> set to a directory holding <c>caller-line</c>; home holds <c>home-line</c>.</summary>
    private static ScenarioRig CallerWithItsOwnDirectory()
    {
        var rig = Rig();
        var callers = rig.Sandbox.Root + "/caller";
        StatusLineIn(callers, "caller-line");
        StatusLineIn(rig.Sandbox.Home + "/.claude", "home-line");
        rig.Variables["CLAUDE_CONFIG_DIR"] = callers;
        return rig;
    }

    [Fact]
    public void AnEmptyRecordedValueReadsTheStatusLineFromHomeNotTheCallersDirectory()
    {
        var command = StatusCommand(CallerWithItsOwnDirectory(), string.Empty);

        Assert.Contains("home-line", command, StringComparison.Ordinal);
        Assert.DoesNotContain("caller-line", command, StringComparison.Ordinal);
    }

    [Fact]
    public void NoRecordedValueReadsTheStatusLineFromHomeAsBefore()
    {
        var command = StatusCommand(CallerWithItsOwnDirectory(), null);

        Assert.Contains("home-line", command, StringComparison.Ordinal);
        Assert.DoesNotContain("caller-line", command, StringComparison.Ordinal);
    }
}
