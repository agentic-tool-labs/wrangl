using System.Text;
using Ah.Cli.Composition;

namespace Ah.Cli.Tests;

/// <summary>Every write but an activity record reaches the view service, whether or not a read model is attached yet.</summary>
public sealed class ReadModelWriteRelayWrittenTests
{
    private static readonly byte[] Empty = Encoding.UTF8.GetBytes("{}");

    [Fact]
    public void EveryWriteTellsTheSubscriberEvenWithNoModelAttached()
    {
        var relay = new ReadModelWriteRelay(static () => 0);
        var told = 0;
        relay.Written = () => told++;

        relay.Wrote("any-domain", "any-op", Empty, Empty);

        Assert.Equal(1, told);
    }

    [Theory]
    [InlineData("record")]
    [InlineData("sweep")]
    public void NegativeAnActivityRecordTellsNoSubscriber(string op)
    {
        var relay = new ReadModelWriteRelay(static () => 0);
        var told = 0;
        relay.Written = () => told++;

        relay.Wrote("activity", op, Empty, Empty);

        Assert.Equal(0, told);
    }

    [Fact]
    public void NegativeAWriteWithNoSubscriberIsHarmless()
    {
        var relay = new ReadModelWriteRelay(static () => 0);

        var thrown = Record.Exception(() => relay.Wrote("any-domain", "any-op", Empty, Empty));

        Assert.Null(thrown);
    }
}
