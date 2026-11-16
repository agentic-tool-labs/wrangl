using Ah.Abstractions;
using Ah.TestKit;

namespace Ah.Hierarchy.Tests;

/// <summary>Message reads inside a share open the store once; outside one, or on another core, each read still opens its own.</summary>
public sealed class MsgReadShareTests
{
    private sealed class CountingModes(IMessageModeReader inner) : IMessageModeReader
    {
        internal int Reads { get; private set; }

        public string Read(string domain)
        {
            Reads++;
            return inner.Read(domain);
        }
    }

    private static (IHierarchyCore Core, CountingModes Modes) Make()
    {
        var route = TestMsgStore.Route(UnitSandbox.Create().Home);
        var modes = new CountingModes(route.Modes);
        return (HierarchyReadOpsTests.Make(msgsRoute: route with { Modes = modes }).Services.Core, modes);
    }

    [Fact]
    public void ReadsInsideAShareOpenTheStoreOnce()
    {
        var (core, modes) = Make();

        using (core.ShareMsgReads())
        {
            _ = core.ReadMsgFile("/nowhere/a.md");
            _ = core.ReadMsgFile("/nowhere/b.md");
        }

        Assert.Equal(1, modes.Reads);
    }

    [Fact]
    public void ReadsOutsideAShareOpenTheStoreEachTime()
    {
        var (core, modes) = Make();

        _ = core.ReadMsgFile("/nowhere/a.md");
        using (core.ShareMsgReads())
        {
            _ = core.ReadMsgFile("/nowhere/b.md");
        }

        _ = core.ReadMsgFile("/nowhere/c.md");

        Assert.Equal(3, modes.Reads);
    }

    [Fact]
    public void AnInnerShareLeavesTheOuterOneOpen()
    {
        var (core, modes) = Make();

        using (core.ShareMsgReads())
        {
            _ = core.ReadMsgFile("/nowhere/a.md");
            using (core.ShareMsgReads())
            {
                _ = core.ReadMsgFile("/nowhere/b.md");
            }

            _ = core.ReadMsgFile("/nowhere/c.md");
        }

        Assert.Equal(1, modes.Reads);
    }

    [Fact]
    public void AnotherCoreDoesNotUseTheShare()
    {
        var (core, _) = Make();
        var (other, otherModes) = Make();

        using (core.ShareMsgReads())
        {
            _ = core.ReadMsgFile("/nowhere/a.md");
            _ = other.ReadMsgFile("/nowhere/b.md");
            _ = other.ReadMsgFile("/nowhere/c.md");
        }

        Assert.Equal(2, otherModes.Reads);
    }

    [Fact]
    public void AnotherThreadDoesNotUseTheShare()
    {
        var (core, modes) = Make();

        using (core.ShareMsgReads())
        {
            _ = core.ReadMsgFile("/nowhere/a.md");
            var before = modes.Reads;
            var other = new Thread(() => _ = core.ReadMsgFile("/nowhere/b.md"));
            other.Start();
            other.Join();
            Assert.Equal(before + 1, modes.Reads);
        }
    }

    [Fact]
    public void AfterTheShareIsDisposedAReadOpensTheStoreAgain()
    {
        var (core, modes) = Make();

        using (core.ShareMsgReads())
        {
            _ = core.ReadMsgFile("/nowhere/a.md");
        }

        var before = modes.Reads;
        _ = core.ReadMsgFile("/nowhere/b.md");

        Assert.Equal(before + 1, modes.Reads);
    }
}
