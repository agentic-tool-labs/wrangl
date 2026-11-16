using Ah.Cli.Composition;

namespace Ah.Cli.Tests;

public sealed class ExchangeMemoVersionTests
{
    private static readonly string[] Pools = ["/pool"];

    private static (ExchangeMemo Memo, Func<int> Builds) Rig(Func<long?> version)
    {
        var builds = 0;
        // no build here consults a file, so nothing is ever stat-ed
        var memo = new ExchangeMemo(null!, () => 1_000_000, version: version);
        var built = () =>
        {
            _ = memo.Get(Pools, () =>
            {
                builds++;
                return [];
            });
            return builds;
        };
        return (memo, built);
    }

    [Fact]
    public void AListingThatReadOnlyTheDatabaseIsKeptWhileTheCounterHolds()
    {
        var (memo, get) = Rig(static () => 7);

        Assert.Equal(1, get());
        Assert.Equal(1, get());
        Assert.Equal(1, memo.Hits);
    }

    [Fact]
    public void AWriteToTheStoreRebuildsTheListing()
    {
        long version = 7;
        var (_, get) = Rig(() => version);

        Assert.Equal(1, get());
        version = 8;
        Assert.Equal(2, get());
    }

    [Fact]
    public void AStoreThatCannotBeReadKeepsNothing()
    {
        var (_, get) = Rig(static () => null);

        Assert.Equal(1, get());
        Assert.Equal(2, get());
    }

    [Theory]
    [InlineData("20261101-120000-abcd", true)]
    [InlineData("20261103-120000-abcd", false)] // Inside the horizon and its day of margin.
    [InlineData("not-a-stamped-id-at-all", false)]
    public void AnIdsStampDecidesTheHorizonWithNoFile(string id, bool older)
    {
        var now = new DateTimeOffset(2026, 11, 5, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

        Assert.Equal(older, WatchHostAdapter.IdOlderThanHorizon(id, now));
    }
}
