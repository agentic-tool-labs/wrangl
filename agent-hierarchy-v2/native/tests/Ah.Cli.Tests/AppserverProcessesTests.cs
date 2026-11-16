using System.Globalization;
using System.Runtime.Versioning;
using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Platform;

namespace Ah.Cli.Tests;

/// <summary>
/// A member's own Codex app-server as the launcher starts and ends it: a private socket directory, a detached process that is
/// found by its socket, and a stop that only ever reaches the one process whose command line is that server's.
/// </summary>
[UnsupportedOSPlatform("windows")]
public sealed class AppserverProcessesTests : IDisposable
{
    private readonly Sandbox _sandbox = new();

    public void Dispose() => _sandbox.Dispose();

    private sealed class RecordingSpawner(Func<DetachedSpawnRequest, int> spawn) : IDetachedSpawner
    {
        public List<DetachedSpawnRequest> Requests { get; } = [];

        public int SpawnDetached(DetachedSpawnRequest request)
        {
            Requests.Add(request);
            return spawn(request);
        }
    }

    private sealed class FixedInspector(string? commandLine) : IProcessInspector
    {
        public bool PidAlive(int pid) => true;

        public bool PidOwned(int pid) => true;

        public string? CommandLine(int pid) => commandLine;
    }

    private string Socket => Path.Combine(_sandbox.AhHome, "run", "codex", "myrepo-member.sock");

    private string[] ServerArgs => ["-c", "approval_policy=\"on-request\"", "app-server", "--listen", $"unix://{Socket}"];

    private string Codex => Path.Combine(_sandbox.Bin, "codex");

    private (LaunchComposition.AppserverProcesses Launcher, FakeRunner Runner) Build(
        RecordingSpawner spawner, string? commandLine = null, int polls = 2, IReadOnlyDictionary<string, string?>? inherited = null)
    {
        var io = PlatformModule.CreateIo(null);
        var runner = new FakeRunner();
        var env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, new Dictionary<string, string?> { ["PATH"] = _sandbox.Bin });
        var launcher = new LaunchComposition.AppserverProcesses(
            runner, new FixedInspector(commandLine), env, io.Files, io.Writer, spawner, PlatformModule.CurrentUid(), polls, 1, inherited);
        return (launcher, runner);
    }

    private void InstallCodex(bool executable)
    {
        Directory.CreateDirectory(_sandbox.Bin);
        File.WriteAllText(Codex, "#!/bin/sh\n");
        File.SetUnixFileMode(Codex, executable ? (UnixFileMode)0x1C0 : (UnixFileMode)0x180);
    }

    [Fact]
    public void StartMakesThePrivateSocketDirectoryRemovesAStaleSocketAndReturnsThePidOnceTheSocketAppears()
    {
        InstallCodex(executable: true);
        var dir = Path.GetDirectoryName(Socket)!;
        Directory.CreateDirectory(dir);
        File.SetUnixFileMode(dir, (UnixFileMode)0x1ED);
        File.WriteAllText(Socket, "stale");
        var seen = new List<string>();
        var spawner = new RecordingSpawner(_ =>
        {
            seen.Add($"{File.Exists(Socket)}|{Convert.ToString((int)File.GetUnixFileMode(dir), 8)}");
            File.WriteAllText(Socket, "listening");
            return 4242;
        });

        var pid = Build(spawner).Launcher.Start(Socket, ServerArgs, _sandbox.Root);

        Assert.Equal(4242, pid);
        Assert.Equal(["False|700"], seen);
        var request = Assert.Single(spawner.Requests);
        Assert.Equal([Codex, .. ServerArgs], request.Argv);
        Assert.Equal(_sandbox.Root, request.WorkingDirectory);
        Assert.Equal("/dev/null", request.LogPath);
    }

    [Fact]
    public void TheServerStartsWithTheCallersEnvironmentWithoutItsSessionIdentityTestSettingsAndPane()
    {
        InstallCodex(executable: true);
        var inherited = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["PATH"] = _sandbox.Bin,
            ["HOME"] = _sandbox.Home,
            ["OPENAI_API_KEY"] = "k",
            ["AH_HOME"] = _sandbox.AhHome,
            ["CLAUDECODE"] = "1",
            ["CLAUDE_CODE_SESSION_ID"] = "s",
            ["AH_TEST"] = "1",
            ["AHD_IDLE_EXIT_MS"] = "1",
            ["HERDR_PANE_ID"] = "w1:p1",
            ["TMUX_PANE"] = "%1",
        };
        var spawner = new RecordingSpawner(_ =>
        {
            File.WriteAllText(Socket, "listening");
            return 4242;
        });

        var pid = Build(spawner, inherited: inherited).Launcher.Start(Socket, ServerArgs, _sandbox.Root);

        Assert.Equal(4242, pid);
        var environment = Assert.Single(spawner.Requests).Environment;
        Assert.Equal(["AH_HOME", "HOME", "OPENAI_API_KEY", "PATH"], environment.Keys.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void StartNeedsAnExecutableCodexOnThePath(bool executable, bool started)
    {
        InstallCodex(executable);
        var spawner = new RecordingSpawner(_ =>
        {
            File.WriteAllText(Socket, "listening");
            return 7;
        });

        var pid = Build(spawner).Launcher.Start(Socket, ServerArgs, _sandbox.Root);

        Assert.Equal(started, pid is not null);
        Assert.Equal(started ? 1 : 0, spawner.Requests.Count);
    }

    [Fact]
    public void StartWithNoCodexOnThePathSpawnsNothing()
    {
        var spawner = new RecordingSpawner(_ => 7);

        Assert.Null(Build(spawner).Launcher.Start(Socket, ServerArgs, _sandbox.Root));
        Assert.Empty(spawner.Requests);
    }

    [Fact]
    public void AServerThatNeverOpensItsSocketIsStoppedOnlyWhenItsCommandLineIsThatServers()
    {
        InstallCodex(executable: true);
        var spawner = new RecordingSpawner(_ => 4242);
        var unrelated = Build(spawner, commandLine: "/usr/bin/vim notes.txt");

        Assert.Null(unrelated.Launcher.Start(Socket, ServerArgs, _sandbox.Root));
        Assert.Empty(unrelated.Runner.Calls);

        var ours = Build(spawner, commandLine: $"{Codex} -c x app-server --listen unix://{Socket}");

        Assert.Null(ours.Launcher.Start(Socket, ServerArgs, _sandbox.Root));
        Assert.Equal(["/bin/kill", "-TERM", "4242"], Assert.Single(ours.Runner.Calls));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(4194304, true)]
    [InlineData(4194305, false)]
    [InlineData(-5, false)]
    public void ThePidIsSignalledOnlyWhenItIsAProcessIdWhoseCommandLineIsTheServerOfThatSocket(int pid, bool signalled)
    {
        var launcher = Build(new RecordingSpawner(_ => 0), commandLine: $"codex app-server --listen unix://{Socket}");

        Assert.Equal(signalled, launcher.Launcher.Terminate(pid, Socket));
        Assert.Equal(signalled ? 1 : 0, launcher.Runner.Calls.Count);
    }

    [Theory]
    [InlineData("codex app-server --listen unix://{0}", true)]
    [InlineData("codex app-server --listen unix://{0}.other", false)]
    [InlineData("codex app-server --listen unix:///elsewhere.sock", false)]
    [InlineData("vim {0}", false)]
    public void AReusedPidWhoseCommandLineIsNotThisSocketsServerIsLeftAlone(string template, bool signalled)
    {
        var launcher = Build(
            new RecordingSpawner(_ => 0), commandLine: string.Format(CultureInfo.InvariantCulture, template, Socket));

        Assert.Equal(signalled, launcher.Launcher.Terminate(4242, Socket));
        Assert.Equal(signalled ? 1 : 0, launcher.Runner.Calls.Count);
    }

    [Fact]
    public void TheMemberTempDirectoryIsMadeUnderTheAhHomeAndNothingOutsideItIsAccepted()
    {
        var launcher = Build(new RecordingSpawner(_ => 0)).Launcher;
        var inside = Path.Combine(_sandbox.AhHome, "run", "codex", "tmp", "myrepo-member");
        var outside = Path.Combine(_sandbox.Root, "elsewhere", "tmp");

        Assert.Equal(inside, launcher.EnsureMemberTmp(inside));
        Assert.True(Directory.Exists(inside));
        Assert.Null(launcher.EnsureMemberTmp(outside));
        Assert.False(Directory.Exists(outside));
    }
}
