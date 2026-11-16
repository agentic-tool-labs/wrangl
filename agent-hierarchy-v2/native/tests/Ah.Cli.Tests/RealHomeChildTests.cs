using System.Diagnostics;
using Ah.TestKit;

namespace Ah.Cli.Tests;

/// <summary>
/// A child the test starts inherits the real-home marker, and refuses to run when its home is that directory: a leak through a
/// child process fails the test that started it, with the reason on the child's stderr.
/// </summary>
public sealed class RealHomeChildTests
{
    private sealed record Run(int ExitCode, string Stderr);

    private static Run RunVersion(UnitSandbox sandbox, string? marker, string verb = "version", string? ahHome = null)
    {
        var binary = Path.Combine(AppContext.BaseDirectory, "ahd.dll");
        Assert.True(File.Exists(binary), $"the test host does not carry {binary}");
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(binary);
        start.ArgumentList.Add(verb);
        start.Environment.Clear();
        start.Environment["HOME"] = sandbox.Home;
        start.Environment["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin";
        start.Environment["AH_HOME"] = ahHome ?? sandbox.Root + "/ah";
        start.Environment["AH_DAEMON"] = "off";
        start.Environment["AH_TEST"] = "1";
        start.Environment["DOTNET_ROOT"] = Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? string.Empty;
        if (marker is not null)
        {
            start.Environment["AH_TEST_REAL_HOME"] = marker;
        }

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(120_000), "the child did not exit");
        stdout.Wait();
        return new Run(process.ExitCode, stderr);
    }

    [Fact]
    public void AChildWhoseHomeIsTheMarkedRealHomeExitsNonZeroAndSaysSo()
    {
        var sandbox = UnitSandbox.Create();

        var run = RunVersion(sandbox, sandbox.Home);

        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains("ah: a test process resolved the real home (" + sandbox.Home + ")", run.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void AChildWhoseAhHomeIsInsideTheMarkedRealHomeExitsNonZeroAndSaysSo()
    {
        var sandbox = UnitSandbox.Create();
        var inside = sandbox.Root + "/real/.claude/ah2";

        var run = RunVersion(sandbox, sandbox.Root + "/real", "logs", inside);

        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains("ah: a test process resolved the real home (" + inside + ") through AH_HOME", run.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSameChildWithAhHomeOutsideTheMarkedRealHomeRuns()
    {
        var sandbox = UnitSandbox.Create();

        var run = RunVersion(sandbox, sandbox.Root + "/real", "logs", sandbox.Root + "/ah");

        Assert.True(run.ExitCode == 0, $"exit {run.ExitCode}: {run.Stderr}");
    }

    [Fact]
    public void TheSameChildWithTheMarkerOnADifferentDirectoryRuns()
    {
        var sandbox = UnitSandbox.Create();

        var run = RunVersion(sandbox, sandbox.Root + "/elsewhere");

        Assert.True(run.ExitCode == 0, $"exit {run.ExitCode}: {run.Stderr}");
        Assert.DoesNotContain("resolved the real home", run.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSameChildWithoutAMarkerRuns()
    {
        var sandbox = UnitSandbox.Create();

        var run = RunVersion(sandbox, null);

        Assert.True(run.ExitCode == 0, $"exit {run.ExitCode}: {run.Stderr}");
    }
}
