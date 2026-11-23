namespace Ah.Watch.Tests;

/// <summary>A job with eta <c>server</c> is meant to run until stopped: while open it owes no overdue alarm, failsafe item or blocked-on-job.</summary>
public sealed class JobServerEtaTests
{
    private static readonly double Now = new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    private static JobFact Job(string eta) =>
        new("20261121-120000-srv1", "tm", "impl", "sess-impl", "bg", "npm run preview", 4321, eta, Now - (24 * 3600 * 1000.0), null, null, false, true, []);

    private static JobOutcome Run(JobFact job) =>
        JobEvents.Evaluate("s1", Now, 30000, ["tm"], _ => [job], static _ => 600, new HashSet<string>(StringComparer.Ordinal), out _);

    [Fact]
    public void AServerJobOpenForADayOwesNoAlarmAndNegativeALargeOneOwesJobOverdue()
    {
        var server = Job("server");
        Assert.Empty(Run(server).Events);
        Assert.Empty(Run(server).OwnerMails);
        Assert.DoesNotContain(WatchModule.OverdueJobItems([server], static _ => 600, Now, new MapEnv()), i => double.IsFinite(i.AtMs));
        Assert.Null(WatchModule.JobOverdueOf([server], "impl", static _ => 600, Now, 30000));

        var large = Job("large");
        Assert.Equal("job-overdue", Assert.Single(Run(large).Events).Name);
        Assert.NotNull(WatchModule.JobOverdueOf([large], "impl", static _ => 600, Now, 30000));
    }
}
