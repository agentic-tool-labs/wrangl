using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Platform;

namespace Ah.Launch.Tests;

/// <summary>The headless transport: the shape carries an argv, and the launch starts that argv directly and records the pid.</summary>
public sealed class HeadlessLaunchTests
{
    private const string MemberName = "alpha-implementor";

    private sealed class FakeSpawner(int pid) : IDetachedSpawner
    {
        internal List<DetachedSpawnRequest> Requests { get; } = [];

        public int SpawnDetached(DetachedSpawnRequest request)
        {
            Requests.Add(request);
            return pid;
        }
    }

    private sealed class FakeTable(string? start) : IProcessTable
    {
        public IReadOnlyList<int>? Ancestors(int pid) => null;

        public bool? HasAncestor(int pid, int candidate) => null;

        public string? StartTime(long pid) => start;
    }

    private static JsonObject Member() => new() { ["role"] = "implementor", ["name"] = MemberName };

    /// <summary>A rig with an executable <c>claude</c> on its PATH, which a headless launch resolves to an absolute path.</summary>
    private static ScenarioRig Rig()
    {
        var rig = ScenarioRig.Create();
        var claude = rig.Sandbox.Root + "/bin/claude";
        File.WriteAllText(claude, "#!/bin/sh\n");
        File.SetUnixFileMode(claude, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return rig;
    }

    private static string Text(JsonValue? v) => v is { } value ? JsConvert.ToJsString(value) : "undefined";

    private static List<string> Argv(JsonObject shape) =>
        shape.Get("argv")!.Value.AsArray().Select(x => Text(x)).ToList();

    private static JsonObject Shaped(ScenarioRig rig, string transport, string? prompt)
    {
        var outcome = rig.Shapes.SpawnShape(Member(), transport, rig.Context);
        Assert.Null(outcome.Fail);
        var member = Member();
        member["spawn"] = outcome.Shape;
        if (prompt is not null)
        {
            member["headless_prompt"] = prompt;
        }

        return member;
    }

    private static LaunchContext WithTmp(ScenarioRig rig) =>
        rig.Context with { MemberTmp = new Dictionary<string, string> { [MemberName] = rig.Sandbox.Root + "/membertmp" } };

    [Fact]
    public void TheHeadlessShapeCarriesAnArgvThatEndsInDashPWithTheSettingsUnquoted()
    {
        var rig = Rig();

        var argv = Argv(rig.Shapes.SpawnShape(Member(), "headless", rig.Context).Shape);

        Assert.Equal("claude", argv[0]);
        Assert.Equal("-p", argv[^1]);
        Assert.Equal("--agent", argv[1]);
        var settings = argv[argv.IndexOf("--settings") + 1];
        Assert.DoesNotContain('\'', settings[..1]);
    }

    [Fact]
    public void TheHeadlessArgvIsTheTmuxFlagListWithOnlyTheSettingsQuotedThere()
    {
        var rig = Rig();
        var argv = Argv(rig.Shapes.SpawnShape(Member(), "headless", rig.Context).Shape);
        var tmux = Text(rig.Shapes.SpawnShape(Member(), "tmux", rig.Context).Shape.Get("launch")!.Value.AsArray()[0]);
        var quoted = tmux[(tmux.IndexOf("<TARGET> ", StringComparison.Ordinal) + "<TARGET> ".Length)..^" Enter".Length];
        Assert.True(quoted.StartsWith('\'') && quoted.EndsWith('\''), quoted);
        var claudeCmd = quoted[1..^1].Replace("'\\''", "'", StringComparison.Ordinal);

        var flags = argv.Skip(1).SkipLast(1).Chunk(2)
            .Select(pair => pair[0] + " " + (pair[0] == "--settings" ? "'" + pair[1].Replace("'", "'\\''", StringComparison.Ordinal) + "'" : pair[1]));

        Assert.Equal("claude " + string.Join(' ', flags), claudeCmd);
    }

    [Fact]
    public void TheHeadlessTransportIdIsNotAPane()
    {
        Assert.True(HeadlessTransport.IsId(HeadlessTransport.Id(4242)));
        Assert.Null(HeadlessTransport.PaneOf(HeadlessTransport.Id(4242)));
        Assert.Equal("w1:p9", HeadlessTransport.PaneOf("w1:p9"));
        Assert.Null(HeadlessTransport.PaneOf(""));
        Assert.Null(HeadlessTransport.PaneOf(null));
        Assert.False(HeadlessTransport.IsId("headlessness"));
        Assert.False(HeadlessTransport.IsId("%7"));
    }

    [Fact]
    public void TheTmuxShapeHasNoArgv()
    {
        var rig = Rig();

        var shape = rig.Shapes.SpawnShape(Member(), "tmux", rig.Context).Shape;

        Assert.Null(shape.Get("argv"));
        Assert.Contains("send-keys", Text(shape.Get("launch")!.Value.AsArray()[0]), StringComparison.Ordinal);
    }

    [Fact]
    public void AHeadlessLaunchStartsTheArgvWithThePromptLastAndRecordsThePid()
    {
        var rig = Rig();
        var spawner = new FakeSpawner(777);
        var launcher = rig.Launcher with { Spawner = spawner, Processes = new FakeTable("Mon Oct 26 12:00:00 2026") };
        var member = Shaped(rig, "headless", "ah mail from a to b (brief): [hierarchy-msg /x]");

        var result = launcher.LaunchMemberCore(member, "headless", WithTmp(rig));

        Assert.Equal("dispatched", Text(result.Get("launch_status")));
        Assert.Equal("headless:777", Text(result.Get("transport_id")));
        Assert.Equal("777", Text(result.Get("pid")));
        Assert.Equal("Mon Oct 26 12:00:00 2026", Text(result.Get("pid_start")));
        var request = Assert.Single(spawner.Requests);
        Assert.Equal(rig.Sandbox.Root + "/bin/claude", request.Argv[0]);
        Assert.Equal(["-p", "ah mail from a to b (brief): [hierarchy-msg /x]"], request.Argv.TakeLast(2));
        Assert.Equal(rig.Sandbox.Root + "/membertmp/headless.log", request.LogPath);
    }

    [Fact]
    public void AHeadlessLaunchWithoutAStartTimeStillRecordsThePidButNoPidStart()
    {
        var rig = Rig();
        var launcher = rig.Launcher with { Spawner = new FakeSpawner(5), Processes = new FakeTable(null) };

        var result = launcher.LaunchMemberCore(Shaped(rig, "headless", "go"), "headless", WithTmp(rig));

        Assert.Equal("5", Text(result.Get("pid")));
        Assert.Null(result.Get("pid_start"));
    }

    [Fact]
    public void AHeadlessLaunchWithNoPromptFailsAndStartsNothing()
    {
        var rig = Rig();
        var spawner = new FakeSpawner(1);
        var launcher = rig.Launcher with { Spawner = spawner };

        var result = launcher.LaunchMemberCore(Shaped(rig, "headless", null), "headless", WithTmp(rig));

        Assert.Equal("failed", Text(result.Get("launch_status")));
        Assert.Empty(spawner.Requests);
    }

    [Fact]
    public void TheSpawnRequestCarriesPathHomeAndAnAbsoluteExecutable()
    {
        var rig = Rig();
        var spawner = new FakeSpawner(9);
        var launcher = rig.Launcher with { Spawner = spawner };

        _ = launcher.LaunchMemberCore(Shaped(rig, "headless", "go"), "headless", WithTmp(rig));

        var request = Assert.Single(spawner.Requests);
        Assert.Equal(rig.Variables["PATH"], request.Environment["PATH"]);
        Assert.Equal(rig.Variables["HOME"], request.Environment["HOME"]);
        Assert.StartsWith("/", request.Argv[0], StringComparison.Ordinal);
        Assert.True(File.Exists(request.Argv[0]));
    }

    [Fact]
    public void TheProcessEnvironmentIsPassedOnAndTheRigsPathDoesNotOverrideIt()
    {
        var rig = Rig();
        var spawner = new FakeSpawner(9);
        var launcher = rig.Launcher with
        {
            Spawner = spawner,
            ProcessEnvironment = new Dictionary<string, string?> { ["PATH"] = rig.Sandbox.Root + "/bin", ["ANTHROPIC_API_KEY"] = "k" },
        };

        _ = launcher.LaunchMemberCore(Shaped(rig, "headless", "go"), "headless", WithTmp(rig));

        var request = Assert.Single(spawner.Requests);
        Assert.Equal("k", request.Environment["ANTHROPIC_API_KEY"]);
        Assert.Equal(rig.Sandbox.Root + "/bin", request.Environment["PATH"]);
    }

    [Fact]
    public void TheCallersSessionAndMemberIdentityDoNotLeakIntoTheNewMembersEnvironment()
    {
        var rig = Rig();
        var spawner = new FakeSpawner(9);
        var launcher = rig.Launcher with
        {
            Spawner = spawner,
            ProcessEnvironment = new Dictionary<string, string?>
            {
                ["CLAUDECODE"] = "1",
                ["CLAUDE_CODE_ENTRYPOINT"] = "cli",
                ["AH_MEMBER_NAME"] = "someone-else",
                ["WRANGL_TEAM_FILE"] = "/other/team.json",
                ["WRANGL_EXPECTED_ROOT"] = "/other/root",
                ["CLAUDE_CONFIG_NOTE"] = "kept",
                ["ANTHROPIC_API_KEY"] = "kept",
            },
        };

        _ = launcher.LaunchMemberCore(Shaped(rig, "headless", "go"), "headless", WithTmp(rig));

        var env = Assert.Single(spawner.Requests).Environment;
        foreach (var gone in new[] { "CLAUDECODE", "CLAUDE_CODE_ENTRYPOINT", "AH_MEMBER_NAME", "WRANGL_TEAM_FILE", "WRANGL_EXPECTED_ROOT" })
        {
            Assert.DoesNotContain(gone, env.Keys);
        }

        Assert.Equal("kept", env["CLAUDE_CONFIG_NOTE"]);
        Assert.Equal("kept", env["ANTHROPIC_API_KEY"]);
    }

    [Fact]
    public void AHeadlessLaunchWithNoClaudeOnThePathFailsAndStartsNothing()
    {
        var rig = ScenarioRig.Create();
        var spawner = new FakeSpawner(1);
        var launcher = rig.Launcher with { Spawner = spawner };

        var result = launcher.LaunchMemberCore(Shaped(rig, "headless", "go"), "headless", WithTmp(rig));

        Assert.Equal("failed", Text(result.Get("launch_status")));
        Assert.Contains("is not on PATH", Text(result.Get("error")), StringComparison.Ordinal);
        Assert.Empty(spawner.Requests);
    }

    [Fact]
    public void TheRealSpawnerStartsTheArgvAndLogsItsOutput()
    {
        var rig = Rig();
        var launcher = rig.Launcher with { Spawner = PlatformModule.CreateDetachedSpawner() };
        var member = Member();
        member["spawn"] = new JsonObject { ["argv"] = JsonText.Parse("[\"/bin/echo\"]"), ["launch_cwd"] = rig.Cwd };
        member["headless_prompt"] = "prompt-reached-the-child";

        var result = launcher.LaunchMemberCore(member, "headless", WithTmp(rig));

        Assert.Equal("dispatched", Text(result.Get("launch_status")));
        var log = rig.Sandbox.Root + "/membertmp/headless.log";
        for (var i = 0; i < 100 && !(File.Exists(log) && File.ReadAllText(log).Contains("prompt-reached-the-child", StringComparison.Ordinal)); i++)
        {
            Thread.Sleep(50);
        }

        Assert.Contains("prompt-reached-the-child", File.ReadAllText(log), StringComparison.Ordinal);
    }

    [Fact]
    public void AHeadlessLaunchWithNoSpawnerFails()
    {
        var rig = Rig();

        var result = rig.Launcher.LaunchMemberCore(Shaped(rig, "headless", "go"), "headless", WithTmp(rig));

        Assert.Equal("failed", Text(result.Get("launch_status")));
    }

    [Fact]
    public void AHeadlessLaunchWithNoMemberTempDirFailsAndStartsNothing()
    {
        var rig = Rig();
        var spawner = new FakeSpawner(1);
        var launcher = rig.Launcher with { Spawner = spawner };

        var result = launcher.LaunchMemberCore(Shaped(rig, "headless", "go"), "headless", rig.Context);

        Assert.Equal("failed", Text(result.Get("launch_status")));
        Assert.Empty(spawner.Requests);
    }

    [Fact]
    public void LayoutAndLaunchOnHeadlessMakesNoPaneAndLeavesTheTransportIdForTheLaunch()
    {
        var rig = Rig();
        var spawner = new FakeSpawner(42);
        var launcher = rig.Launcher with { Spawner = spawner, Processes = new FakeTable("s") };

        var batch = launcher.LayoutAndLaunch(rig.Cwd, [Shaped(rig, "headless", "go")], "headless", "auto", rig.Cwd, "spawn-one", WithTmp(rig));

        Assert.Null(batch.Fail);
        Assert.Null(batch.Partial);
        Assert.Equal("headless:42", Text(batch.Members[0].Get("transport_id")));
        Assert.Single(spawner.Requests);
    }
}
