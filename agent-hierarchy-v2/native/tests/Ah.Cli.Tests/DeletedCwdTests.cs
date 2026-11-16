using System.Diagnostics;
using Ah.TestKit;

namespace Ah.Cli.Tests;

/// <summary>A shell whose current directory was deleted gets one line and a nonzero exit, never a stack trace.</summary>
public sealed class DeletedCwdTests
{
    private const string Line = "ahd: current directory no longer exists; cd elsewhere and retry";

    private static (int ExitCode, string Stderr) RunFromDeletedDirectory(params string[] verb)
    {
        var sandbox = UnitSandbox.Create();
        var gone = sandbox.Root + "/gone";
        Directory.CreateDirectory(gone);
        // A child cannot be started in a directory that is already gone, so a shell removes it from under itself first.
        var start = new ProcessStartInfo("/bin/sh") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-c", "cd \"$1\" && rmdir \"$1\" && shift && exec dotnet \"$@\"", "sh", gone, Path.Combine(AppContext.BaseDirectory, "ahd.dll") }.Concat(verb))
        {
            start.ArgumentList.Add(arg);
        }

        start.Environment.Clear();
        start.Environment["HOME"] = sandbox.Home;
        start.Environment["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin";
        start.Environment["AH_HOME"] = sandbox.Root + "/ah";
        start.Environment["AH_DAEMON"] = "off";
        start.Environment["AH_TEST"] = "1";
        start.Environment["DOTNET_ROOT"] = Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? string.Empty;

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(120_000), "the child did not exit");
        stdout.Wait();
        return (process.ExitCode, stderr);
    }

    [Fact]
    public void UiStartFromADeletedDirectorySaysSoInOneLineAndExitsNonZero()
    {
        var (exit, stderr) = RunFromDeletedDirectory("ui", "start");

        Assert.NotEqual(0, exit);
        Assert.Equal(Line, stderr.Trim());
    }

    [Fact]
    public void AVerbThatNeverReadsTheDirectoryStillRunsFromADeletedOne()
    {
        var (exit, stderr) = RunFromDeletedDirectory("--version");

        Assert.Equal(0, exit);
        Assert.DoesNotContain(Line, stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingFileWhileTheDirectoryIsThereIsNotReportedAsADeletedDirectory()
    {
        using var stderr = new StringWriter();

        Assert.Throws<FileNotFoundException>(() => Program.StopOnDeletedCwd(() => throw new FileNotFoundException("other"), static () => "/here", stderr));
        Assert.Empty(stderr.ToString());
    }
}
