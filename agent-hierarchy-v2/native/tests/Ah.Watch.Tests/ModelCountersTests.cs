using Ah.Watch.Model;

namespace Ah.Watch.Tests;

public sealed class ModelCountersTests
{
    /// <summary>b459: every drift is counted, and none is kept (negative: no per-drift record in the census).</summary>
    [Fact]
    public void ADriftIsCountedAndNotKept()
    {
        var counters = new ModelCounters();
        for (var i = 0; i < 1000; i++)
        {
            counters.Drifted("messages", "k" + i);
        }

        Assert.Equal(1000, counters.Drift);
        Assert.False(Ah.Core.CoreModule.CensusRead().ContainsKey("watch.drift_keys"));
        GC.KeepAlive(counters);
    }
}
