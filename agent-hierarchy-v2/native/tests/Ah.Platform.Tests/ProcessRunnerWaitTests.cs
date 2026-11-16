using System.Diagnostics;
using System.Text;
using Ah.Abstractions;

namespace Ah.Platform.Tests;

/// <summary>The wait for a child wakes when its stdout closes; a child that closes stdout and runs on is still waited for, and still timed out.</summary>
public sealed class ProcessRunnerWaitTests
{
    private static ProcessResult Sh(string script, TimeSpan? timeout = null) =>
        PlatformModule.CreateProcessRunner().Run(new ProcessRequest(["/bin/sh", "-c", script], Timeout: timeout));

    [Fact]
    public void NegativeAChildThatClosesStdoutAndRunsOnIsWaitedForAndItsCodeKept()
    {
        var clock = Stopwatch.StartNew();

        var result = Sh("echo out; exec >&-; sleep 0.3; echo err >&2; exit 7");

        Assert.Equal(7, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.Equal("out\n", Encoding.UTF8.GetString(result.Stdout.Span));
        Assert.Equal("err\n", Encoding.UTF8.GetString(result.Stderr.Span));
        Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(250), clock.Elapsed.ToString());
    }

    [Fact]
    public void NegativeAChildThatClosesStdoutAndHangsStillTimesOut()
    {
        var result = Sh("exec >&- 2>&-; exec sleep 30", TimeSpan.FromMilliseconds(200));

        Assert.True(result.TimedOut);
        Assert.Null(result.ExitCode);
    }

    [Fact]
    public void AChildWithOpenStdoutThatRunsPastTheTimeoutTimesOut() =>
        Assert.True(Sh("exec sleep 30", TimeSpan.FromMilliseconds(200)).TimedOut);

    [Fact]
    public void AShortChildReturnsWithoutWaitingOutThePoll()
    {
        var fast = Enumerable.Range(0, 20).Select(_ =>
        {
            var clock = Stopwatch.StartNew();
            Assert.Equal(0, Sh("exit 0").ExitCode);
            return clock.Elapsed;
        }).Order().ElementAt(10);

        // a median, so one slow spawn on a busy machine does not decide it; the poll it replaced slept up to 10 ms at a time from a first 1 + 2 + 4 + 8
        Assert.True(fast < TimeSpan.FromSeconds(1), fast.ToString());
    }
}
