using Ah.Cli.Composition;
using Ah.Hierarchy;

namespace Ah.Cli.Tests;

/// <summary>b459 step 11: the processes lane's checklist fold moves only on the plan counter or the on-demand rebuild, never on a timer.</summary>
public sealed class ChecklistMemoTests
{
    [Fact]
    public void AFoldIsHeldWhileThePlanCounterIsFrozenAndFoldedAgainWhenItMovesOrTheRebuildRuns()
    {
        long? seq = 1;
        var memo = new ChecklistMemo(() => seq, (_, _) => new List<ChecklistEntry>());

        var first = memo.Of("d", "t");
        Assert.Same(first, memo.Of("d", "t"));
        Assert.NotSame(first, memo.Of("d", "u"));

        memo.Forget();
        var rebuilt = memo.Of("d", "t");
        Assert.NotSame(first, rebuilt);

        seq = 2;
        Assert.NotSame(rebuilt, memo.Of("d", "t"));
    }

    [Fact]
    public void ALastShipIsHeldWhileThePlanCounterIsFrozenAndAShipLineWrittenSinceIsSeenByTheNextAudit()
    {
        long? seq = 1;
        var log = "ship-1";
        var reads = 0;
        var memo = new ChecklistMemo(() => seq, (_, _) => new List<ChecklistEntry>());
        string? Read()
        {
            reads++;
            return log;
        }

        Assert.Equal("ship-1", memo.LastShip("d", "t", "s", Read));
        log = "ship-2";
        Assert.Equal("ship-1", memo.LastShip("d", "t", "s", Read));
        Assert.Equal(1, reads);

        // the plan-log write moves the plan counter: the next skip check reads the new ship
        seq = 2;
        Assert.Equal("ship-2", memo.LastShip("d", "t", "s", Read));

        memo.Forget();
        Assert.Equal("ship-2", memo.LastShip("d", "t", "s", Read));
        Assert.Equal(3, reads);
    }

    [Fact]
    public void NegativeWithNoPlanCounterEveryPollFolds()
    {
        var memo = new ChecklistMemo(() => null, (_, _) => new List<ChecklistEntry>());
        Assert.NotSame(memo.Of("d", "t"), memo.Of("d", "t"));
    }
}
