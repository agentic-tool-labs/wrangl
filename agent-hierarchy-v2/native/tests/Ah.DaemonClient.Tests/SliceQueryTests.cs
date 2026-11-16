namespace Ah.DaemonClient.Tests;

public sealed class SliceQueryTests
{
    [Fact]
    public void APollsQueryEncodesEachPartAndLeavesOutAnAbsentEpochAndEmptyFresh()
    {
        Assert.Equal("&since=7&epoch=e+1&wait=25&sub=team%3Ad%2Ft%2Cmember%3Ad%2Ft%2Fa&fresh=team%3Ad%2Ft", DaemonClientModule.SliceEventsQuery(["team:d/t", "member:d/t/a"], ["team:d/t"], 7, "e 1", 25));
        Assert.Equal("&since=0&wait=0&sub=a", DaemonClientModule.SliceEventsQuery(["a"], [], 0, null, 0));
    }
}
