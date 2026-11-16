using Ah.TestKit;

namespace Ah.Hierarchy.Tests;

/// <summary>A hold stops only the syncing on a mode read, only while it is open: a hold left on would let a test read a store that is behind its files.</summary>
public sealed class TestMsgStoreHoldSyncTests
{
    private static string Plant(StatusEntriesFixture fixture, string id)
    {
        var path = Path.Combine(StatusEntriesFixture.Pool(fixture.R), "msgs", StatusEntriesFixture.RequestName(id, "implementor", "feat"));
        File.WriteAllText(path, StatusEntriesFixture.RequestText(id, "implementor", "feat", "t", StatusEntriesFixture.T0, "t-implementor"));
        return path;
    }

    [Fact]
    public void AHoldSyncsAtOnceThenAModeReadDoesNotUntilItEnds()
    {
        var fixture = StatusEntriesFixture.Create();
        _ = fixture.Services();
        var home = TestMsgStore.HomeOf(fixture.R);
        var modes = TestMsgStore.SyncedRoute(home, fixture.Root).Modes;

        var before = Plant(fixture, "20260601-000001-hold");
        var held = TestMsgStore.HoldSync(home, fixture.Root);
        Assert.True(TestMsgStore.Holds(home, before));

        var during = Plant(fixture, "20260601-000002-hold");
        _ = modes.Read("msgs");
        Assert.False(TestMsgStore.Holds(home, during));

        held.Dispose();
        _ = modes.Read("msgs");
        Assert.True(TestMsgStore.Holds(home, during));
    }

    [Fact]
    public void NegativeWithNoHoldAModeReadSyncs()
    {
        var fixture = StatusEntriesFixture.Create();
        _ = fixture.Services();
        var home = TestMsgStore.HomeOf(fixture.R);
        var modes = TestMsgStore.SyncedRoute(home, fixture.Root).Modes;
        var path = Plant(fixture, "20260601-000003-hold");
        Assert.False(TestMsgStore.Holds(home, path));

        _ = modes.Read("msgs");

        Assert.True(TestMsgStore.Holds(home, path));
    }

    [Fact]
    public void NegativeAnEditToAFileOfThisHomeIsStillSeen()
    {
        var fixture = StatusEntriesFixture.Create();
        _ = fixture.Services();
        var home = TestMsgStore.HomeOf(fixture.R);
        var path = Plant(fixture, "20260601-000004-hold");
        TestMsgStore.Sync(home, fixture.Root);
        File.Delete(path);

        TestMsgStore.Sync(home, fixture.Root);

        Assert.False(TestMsgStore.Holds(home, path));
    }
}
