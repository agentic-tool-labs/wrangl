namespace Ah.Platform.Tests;

/// <summary>The descendants of a process, read from the table's rows.</summary>
public sealed class ProcessDescendantsTests
{
    private const string Rows =
        "  100     1 ??       Wed Oct 21 08:00:00 2026 claude\n"
        + "  200   100 ttys001  Wed Oct 21 08:00:05 2026 /bin/zsh -c dotnet test Core.csproj\n"
        + "  300   200 ttys001  Wed Oct  7 08:00:06 2026 dotnet testhost\n"
        + "  400     1 ??       Wed Oct 21 08:00:07 2026 unrelated daemon\n";

    [Fact]
    public void ChildrenAndTheirChildrenAreListedWithTheirStartAndCommandAndOthersAreNot()
    {
        var found = ProcessTable.DescendantsIn(ProcessTable.RowsOf(Rows), 100);

        Assert.Equal([200, 300], found.Select(static n => n.Pid).Order());
        Assert.Equal(new DateTimeOffset(2026, 10, 21, 8, 0, 5, TimeSpan.Zero), found.Single(static n => n.Pid == 200).Started);
        Assert.Equal("dotnet testhost", found.Single(static n => n.Pid == 300).Command);
    }

    [Fact]
    public void AProcessWithNoChildrenHasNoDescendants() =>
        Assert.Empty(ProcessTable.DescendantsIn(ProcessTable.RowsOf(Rows), 300));

    private const string GroupRows =
        "  100     1   100 Wed Oct 21 08:00:00 2026 claude\n"
        + "  200   100   200 Wed Oct 21 08:00:05 2026 /bin/zsh -c nohup sleep 300\n"
        + "  300   200   200 Wed Oct 21 08:00:06 2026 sleep 300\n"
        + "  400     1   200 Wed Oct 21 08:00:07 2026 sleep 300\n";

    [Fact]
    public void TheGroupColumnIsReadAndCarriedOnEachNodeAndAReparentedChildOfTheGroupIsStillInIt()
    {
        var rows = ProcessTable.RowsOf(GroupRows, groups: true);

        var found = ProcessTable.DescendantsIn(rows, 100);

        Assert.Equal([200, 200], found.Select(static n => n.Group));
        Assert.Equal(200, found.Single(static n => n.Pid == 300).Parent);
        Assert.Equal([200, 300, 400], rows.Where(static r => r.Value.Group == 200).Select(static r => r.Key).Order());
        Assert.DoesNotContain(rows.Where(static r => r.Value.Group == 200).Select(static r => r.Key), static pid => pid == 100);
        Assert.Empty(ProcessTable.RowsOf("  100 1 ??  Wed Oct 21 08:00:00 2026 claude\n", groups: true));
    }
}
