using System.Diagnostics;
using Ah.Cli.Verbs;

namespace Ah.Cli.Tests;

/// <summary>No hook that is not a gate may hold a session: past its bound it is skipped, with one line on stderr.</summary>
public sealed class HookBoundTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(300);

    private static (int Code, string Err, TimeSpan Took) Run(string[] rest, Func<CliContext> makeContext)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var clock = Stopwatch.StartNew();
        var code = HookVerb.Run(null, rest, stdout, stderr, makeContext: makeContext, fallbackLog: () => null, bound: Short);
        return (code, stderr.ToString(), clock.Elapsed);
    }

    private static CliContext Hangs()
    {
        Thread.Sleep(TimeSpan.FromSeconds(30));
        throw new InvalidOperationException("unreachable");
    }

    [Theory]
    [InlineData("sessionstart")]
    [InlineData("job-track")]
    public void AHookThatHangsIsSkippedAtItsBoundAndExitsZero(string name)
    {
        var (code, err, took) = Run([name], Hangs);

        Assert.Equal(0, code);
        Assert.Contains($"the {name} hook gave up", err, StringComparison.Ordinal);
        Assert.True(took < TimeSpan.FromSeconds(5), $"took {took}");
    }

    [Fact]
    public void AHookThatFinishesInTimeIsNotReportedAsSkipped()
    {
        var (_, err, _) = Run(["job-track"], () => throw new InvalidOperationException("boom"));

        Assert.DoesNotContain("gave up", err, StringComparison.Ordinal);
    }

    [Fact]
    public void AGateIsNeverCutByTheBound()
    {
        var started = new ManualResetEventSlim();
        var worker = new Thread(() => Run(["pretooluse-chain"], () =>
        {
            started.Set();
            Thread.Sleep(TimeSpan.FromSeconds(2));
            throw new InvalidOperationException("late");
        })) { IsBackground = true };
        worker.Start();

        Assert.True(started.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.False(worker.Join(Short * 3), "the gate returned at the hook bound");
        Assert.True(worker.Join(TimeSpan.FromSeconds(10)));
    }
}
