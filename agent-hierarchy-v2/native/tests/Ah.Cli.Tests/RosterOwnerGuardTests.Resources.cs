using System.Globalization;

namespace Ah.Cli.Tests;

/// <summary>The resource verb (ah2-509), driven through the roster command line from a plain shell.</summary>
public sealed partial class RosterOwnerGuardTests
{
    /// <summary>A live process whose scripted start is the one the callers fake records, so the shared liveness rule keeps its hold.</summary>
    private string LiveHolder()
    {
        var pid = LivePidOfAnotherProcess().ToString(CultureInfo.InvariantCulture);
        _starts[pid] = StartedCallers.Started;
        return pid;
    }

    private static Ah.Core.Json.JsonObject Owner(string pid) => new() { ["pid"] = long.Parse(pid, CultureInfo.InvariantCulture), ["start"] = StartedCallers.Started };

    [Fact]
    public void AClaimWithWaitZeroOnAHeldNameExits75WithTheHolderLineAndOnAFreeNameIsGranted()
    {
        var first = LiveHolder();
        _env["CLAUDE_PID"] = first;
        var free = Run("resource", "claim", "Blender", "--wait", "0");
        Assert.True(free.Code == 0, free.Stderr + free.Stdout);
        Assert.StartsWith("resource blender is yours (waited ", free.Stdout, StringComparison.Ordinal);

        _env["CLAUDE_PID"] = LiveHolder();
        var held = Run("resource", "claim", "blender", "--wait", "0");

        Assert.True(held.Code == 75, held.Stderr + held.Stdout);
        // Amendment 2 X3: not in the queue, so only how many wait
        Assert.Matches(@"^resource blender held by user since \d\d:\d\d \(\d+s\); 0 waiting; waited \d+s\n$", held.Stderr);

        // R10, nearest input that must not be a name: an underscore
        Assert.Equal(2, Run("resource", "claim", "my_editor", "--wait", "0").Code);

        // Amendment 2 X2: a wait past what the clock can add is a usage error
        Assert.Equal(2, Run("resource", "claim", "blender", "--wait", "99999999999999999999").Code);

        // Amendment 2 X4: a live waiter on a name whose holder has just gone is listed, under the empty line
        var waiter = Owner(LiveHolder());
        _ = StoreGraph().Hierarchy.StateCalls.Resources("claim", new Ah.Core.Json.JsonObject { ["name"] = "blender", ["holder"] = waiter, ["waiter"] = waiter, ["who"] = "user", ["queue"] = true });
        var holder = _children.Single(p => p.Id.ToString(CultureInfo.InvariantCulture) == first);
        holder.Kill();
        holder.WaitForExit();
        var list = Run("resource", "list");
        Assert.True(list.Code == 0, list.Stderr);
        Assert.Matches(@"^no resource is held\nblender: free\n  waiting: user \d+s\n$", list.Stdout);
    }
}
