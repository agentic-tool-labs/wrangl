namespace Ah.Hierarchy.Tests;

/// <summary>Spec ah-ui-focus §2 and §8: the one classifier, first match winning, against the nearest input that must not match.</summary>
public sealed class ChecklistWorkTests
{
    private const double Now = 10 * HierarchyModule.ChecklistStaleMs;
    private const double Fresh = Now - 1000;
    private const double Old = Now - (2 * HierarchyModule.ChecklistStaleMs);

    private static readonly ChecklistLiveness Live = new(new HashSet<string> { "busy" }, new HashSet<string> { "open-to-running" });

    private static string? Work(string state, string? holder = null, string[]? refs = null, double? last = Fresh, ChecklistLiveness? live = null) =>
        HierarchyModule.ChecklistWork(state, holder, refs, live ?? Live, last, Now);

    [Theory]
    [InlineData("not_started")]
    [InlineData("next")]
    [InlineData("done")]
    [InlineData("dropped")]
    public void AnItemThatIsNotOpenWorkHasNoWork(string state) => Assert.Null(Work(state, last: Old));

    [Fact]
    public void AWaitingItemWaitsAtAnyAgeAndIsNeverStale()
    {
        Assert.Equal("waiting_you", Work("waiting_user", last: Old));
        Assert.False(HierarchyModule.ChecklistIsStale("waiting_user", Old, Now));
    }

    [Fact]
    public void InProgressWithItsHolderWorkingIsWorkingEvenIfOld() => Assert.Equal("working", Work("in_progress", "busy", last: Old));

    [Fact]
    public void NegativeInProgressWithItsHolderIdleAndNoOpenExchangeIsNoOne() => Assert.Equal("no_one", Work("in_progress", "idle", ["closed"]));

    [Fact]
    public void InProgressWithAnOpenExchangeToARunningMemberIsWorkingWhoeverHoldsIt() => Assert.Equal("working", Work("in_progress", "gone", ["closed", "open-to-running"]));

    [Fact]
    public void InProgressWithNobodyOnItGoesStaleAfterADay() => Assert.Equal("stale", Work("in_progress", "gone", last: Old));

    [Fact]
    public void NegativeWithNoLivenessInputNobodyIsWorking() =>
        Assert.Equal("no_one", HierarchyModule.ChecklistWork("in_progress", "busy", ["open-to-running"], null, Fresh, Now));

    [Theory]
    [InlineData("in_review")]
    [InlineData("approved")]
    [InlineData("waiting_orch")]
    public void ReportedAndWaitingOnTheLeadIsWithLeadAndNegativeNeverWorkingWhileItsHolderWorks(string state) => Assert.Equal("with_lead", Work(state, "busy", ["open-to-running"]));

    [Fact]
    public void BlockedStaysBlockedAtAnyAge()
    {
        Assert.Equal("blocked", Work("blocked", "busy"));
        Assert.Equal("blocked", Work("blocked", "busy", last: Old));
    }

    [Theory]
    [InlineData("in_review")]
    [InlineData("approved")]
    [InlineData("waiting_orch")]
    public void ReportInStaysWithTheLeadAtAnyAge(string state) => Assert.Equal("with_lead", Work(state, last: Old));

    [Theory]
    [InlineData("make-the-b14-ship", "make the b14 ship")]
    [InlineData("_run/make-the-ship", "make the ship")]
    [InlineData("_run:make-the-ship", "make the ship")]
    [InlineData("Keep the well-known name", "Keep the well-known name")] // negative: a sentence
    [InlineData("Fix-it", "Fix-it")] // negative: not a slug
    [InlineData("word", "word")] // negative: nothing to space
    public void ATitleThatIsASlugReadsWithSpaces(string title, string read) => Assert.Equal(read, HierarchyModule.ChecklistTitle(title));

    [Fact]
    public void ADispatchWhoseExchangesAllClosedIsWithTheLeadAndNegativeNotWhenOneIsOpenOrItHasNone()
    {
        var open = Live with { Open = new HashSet<string> { "open-to-gone" } };

        Assert.Equal("with_lead", Work("in_progress", "idle", ["closed"], live: open));
        Assert.Equal("no_one", Work("in_progress", "idle", ["open-to-gone"], live: open));
        Assert.Equal("no_one", Work("in_progress", "idle", [], live: open));
        Assert.Equal("with_lead", Work("in_progress", "idle", ["closed"], last: Old, live: open));
        Assert.Equal("stale", Work("in_progress", "idle", ["open-to-gone"], last: Old, live: open));
    }

    [Fact]
    public void NegativeAnItemWithNoKnownActivityIsNotStale() => Assert.Equal("no_one", Work("in_progress", "gone", last: null));
}
