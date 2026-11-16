namespace Ah.Hierarchy.Tests;

public sealed class StatusRefreshCoalescerTests
{
    [Theory]
    [InlineData(1_000, 4_000)]
    [InlineData(60_000, StatusRefreshCoalescer.MaxRestMs)]
    public void AfterARefreshTheWorkerRestsFourTimesWhatItTookUpToTheLimit(long took, long rest)
    {
        long now = 0;
        using var coalescer = new StatusRefreshCoalescer(_ => now += took, TimeSpan.FromSeconds(1), () => now, ownWorker: false);
        coalescer.Request("a");
        coalescer.Request("b");

        Assert.Equal(1, coalescer.RunDue());

        now += rest - 1;
        Assert.Equal(0, coalescer.RunDue());

        now += 1;
        Assert.Equal(1, coalescer.RunDue());
    }

    [Fact]
    public void ARefreshThatTakesNoTimeLeavesNoRest()
    {
        long now = 0;
        using var coalescer = new StatusRefreshCoalescer(static _ => { }, TimeSpan.FromSeconds(1), () => now, ownWorker: false);
        coalescer.Request("a");
        coalescer.Request("b");

        Assert.Equal(2, coalescer.RunDue());
    }

    [Fact]
    public void ACheapRefreshKeepsTheConfiguredGap()
    {
        long now = 0;
        using var coalescer = new StatusRefreshCoalescer(_ => now += 10, TimeSpan.FromSeconds(1), () => now, ownWorker: false);
        coalescer.Request("a");
        Assert.Equal(1, coalescer.RunDue());

        coalescer.Request("a");
        now = 999;
        Assert.Equal(0, coalescer.RunDue());

        now = 1_000;
        Assert.Equal(1, coalescer.RunDue());
    }
}
