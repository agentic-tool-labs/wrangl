using System.Text.RegularExpressions;
using Ah.Abstractions;

namespace Ah.Daemon.Tests;

/// <summary>A job tick the daemon skips logs why, once per change of reason (ah2-620).</summary>
public sealed class JobSkipLogTests
{
    private static int Count(Harness h, string pattern) => Regex.Count(h.Log.ToString(), pattern);

    [Fact]
    public async Task AJobWhoseRunIsStillOutLogsOneSkipLineAndAQuickJobLogsNone()
    {
        var stuck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var quickRuns = 0;
        await using var h = await Harness.StartAsync(Sandbox.Create(), o =>
        {
            o.Jobs.Add(new JobRegistration("stuck", TimeSpan.FromMilliseconds(20), _ => stuck.Task));
            o.Jobs.Add(new JobRegistration("quick", TimeSpan.FromMilliseconds(20), cancellation =>
            {
                _ = Interlocked.Increment(ref quickRuns);
                return Task.CompletedTask;
            }));
        });
        try
        {
            await Until.TrueAsync(() => Volatile.Read(ref quickRuns) >= 20, because: () => h.Log.ToString());

            Assert.Equal(1, Count(h, @"job stuck skipped: its previous run, started \d+ ms ago, is still running"));
            Assert.Equal(0, Count(h, "job quick skipped")); // negative: a job whose every run fits its period is never skipped
        }
        finally
        {
            stuck.SetResult();
        }
    }

    [Fact]
    public async Task AJobQueuedBehindABlockedJobThreadNamesTheJobThatHoldsIt()
    {
        using var held = new ManualResetEventSlim();
        using var holding = new ManualResetEventSlim();
        await using var h = await Harness.StartAsync(Sandbox.Create(), o =>
        {
            o.Jobs.Add(new JobRegistration("hog", TimeSpan.FromMilliseconds(20), _ =>
            {
                holding.Set();
                held.Wait(TimeSpan.FromSeconds(60), CancellationToken.None);
                return Task.CompletedTask;
            }));
            o.Jobs.Add(new JobRegistration("victim", TimeSpan.FromMilliseconds(20), _ => Task.CompletedTask));
        });
        try
        {
            await Until.TrueAsync(() => Count(h, "job victim skipped") > 0, because: () => h.Log.ToString());
            await Task.Delay(200, TestContext.Current.CancellationToken);

            Assert.Equal(1, Count(h, @"job victim skipped: its previous run, queued \d+ ms ago, has not started \(the job thread is running hog\)"));
            Assert.Equal(0, Count(h, "job victim skipped: its previous run, started")); // negative: a run that never began is not called running
        }
        finally
        {
            held.Set();
        }
    }
}
