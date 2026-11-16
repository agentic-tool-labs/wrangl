using Ah.Abstractions;
using Ah.Config;
using Ah.Platform;
using Ah.TestKit;

namespace Ah.Roster.Tests;

/// <summary>Per-dir-files-to-db S11a-holds: the hold predicate reads the holds table and fails closed when it cannot be read.</summary>
public sealed class HoldSourceTests
{
    private sealed class Holds(HoldRead answer) : IHoldSource
    {
        public HoldRead Read(string teamHome, string team) => answer;

        public void Change(string teamHome, string team, Func<string?, HoldWrite?> decide) => throw new NotSupportedException();
    }

    private static (IRosterCore Roster, string Dir) Core(HoldRead answer)
    {
        var sandbox = UnitSandbox.Create();
        var dir = Path.Combine(sandbox.Root, ".claude", "ah2");
        Directory.CreateDirectory(Path.Combine(dir, "teams"));
        var env = new FakeEnvironment(new Dictionary<string, string> { ["HOME"] = sandbox.Home });
        var io = PlatformModule.CreateIo(null);
        IRosterCore roster = null!;
        var config = ConfigModule.CreateCore(io.Files, env, () => roster, new ProcessFacts(sandbox.Root, 1));
        roster = RosterModule.CreateCore(io.Files, env, config, io.Processes, holds: new Holds(answer));
        return (roster, dir);
    }

    [Fact]
    public void AnUnreadableStoreHoldsTheTeamAsAQuitKeyedByTheOldPath()
    {
        var (roster, dir) = Core(new HoldRead(null, "the state database cannot be read: busy"));

        var hold = roster.HoldOf(dir, "t");

        Assert.NotNull(hold?.ParseError);
        Assert.Equal(("quit", "held", roster.HoldPath(dir, "t")), (hold!.Kind, hold.Phase, hold.Path));
    }

    [Fact]
    public void NoRowIsRunningAndARowIsItsHold()
    {
        // the negative: a store that answers with no row leaves the team running
        var (running, at) = Core(new HoldRead(null, null));
        Assert.Null(running.HoldOf(at, "t"));

        var (roster, dir) = Core(new HoldRead("{\"kind\": \"stop\"}\n", null));
        Assert.Equal(("stop", "stopped", (string?)null), (roster.HoldOf(dir, "t")?.Kind, roster.HoldOf(dir, "t")?.Phase, roster.HoldOf(dir, "t")?.ParseError));
    }
}
