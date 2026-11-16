using Ah.Cli.Composition;
using Ah.Commands;
using Ah.Launch;

namespace Ah.Cli.Tests;

/// <summary>The pane questions: one listing per transport, one process table, and a harness found anywhere below a pane's root.</summary>
public sealed class PaneStateReaderTests
{
    private static PaneListing Listing(params MuxPane[] panes) => new(panes, null);

    private static MuxPane Tmux(string id, int? pid) => new("tmux", "/tmp/tmux-1/default", id, pid, null);

    private static PaneStateReader Reader(PaneListing listing, IReadOnlyList<ProcessRow>? rows, Action? onList = null, Action? onProcesses = null) =>
        new(
            _ =>
            {
                onList?.Invoke();
                return listing;
            },
            () =>
            {
                onProcesses?.Invoke();
                return rows;
            });

    // ---------------------------------------------------------------- the multiplexer listing

    [Fact]
    public void APaneInTheListingIsPresentWithItsRootPidAndOneNotInItIsAbsent()
    {
        var reader = Reader(Listing(Tmux("%1", 100)), null);

        Assert.Equal(new PaneReading(PaneKnowledge.Present, 100), reader.Pane("tmux", "%1"));
        Assert.Equal(new PaneReading(PaneKnowledge.Absent, null), reader.Pane("tmux", "%2"));
    }

    [Fact]
    public void APaneListedWithNoPidIsPresentWithNoPidAndNeverAbsent()
    {
        var reader = Reader(Listing(Tmux("%1", null)), null);

        Assert.Equal(new PaneReading(PaneKnowledge.Present, null), reader.Pane("tmux", "%1"));
    }

    [Fact]
    public void AListingThatCouldNotBeHadIsUnknownAndNeverAbsent()
    {
        var reader = Reader(new PaneListing([], "herdr pane enumeration failed"), null);

        Assert.Equal(PaneKnowledge.Unknown, reader.Pane("herdr", "p_1").State);
        Assert.Null(reader.TabPanes("herdr", "t_1"));
    }

    [Fact]
    public void AListingNoInstanceAnsweredIsUnknownEvenWithNoFailureAndAnAnsweringEmptyOneIsAbsent()
    {
        var silent = Reader(new PaneListing([], null, Answered: false), null);
        var empty = Reader(new PaneListing([], null), null);

        Assert.Equal(PaneKnowledge.Unknown, silent.Pane("herdr", "p_1").State);
        Assert.Null(silent.TabPanes("herdr", "t_1"));
        Assert.Equal(PaneKnowledge.Absent, empty.Pane("herdr", "p_1").State);
        Assert.Empty(empty.TabPanes("herdr", "t_1")!);
    }

    [Fact]
    public void TheMultiplexerIsListedOncePerTransportHoweverManyPanesAreAskedAbout()
    {
        var listed = new List<string>();
        var reader = new PaneStateReader(
            transport =>
            {
                listed.Add(transport);
                return Listing(Tmux("%1", 100), Tmux("%2", 200));
            },
            () => null);

        _ = reader.Pane("tmux", "%1");
        _ = reader.Pane("tmux", "%2");
        _ = reader.Pane("tmux", "%9");
        _ = reader.TabPanes("tmux", "t_1");
        _ = reader.Pane("herdr", "p_1");
        _ = reader.Pane("herdr", "p_2");

        Assert.Equal(["tmux", "herdr"], listed);
    }

    [Fact]
    public void ATabsPanesAreTheOnesTheListingPutsInThatTab()
    {
        var listing = Listing(new MuxPane("herdr", "/s", "p_1", 10, "t_1"), new MuxPane("herdr", "/s", "p_2", 20, "t_1"), new MuxPane("herdr", "/s", "p_3", 30, "t_2"));
        var reader = Reader(listing, null);

        Assert.Equal([new TabPane("p_1", 10), new TabPane("p_2", 20)], reader.TabPanes("herdr", "t_1"));
        Assert.Empty(reader.TabPanes("herdr", "t_9")!);
    }

    [Fact]
    public void APaneInATruncatedListingIsPresentAndOneMissingFromItIsUnknownWithTheRemedy()
    {
        var reader = Reader(new PaneListing([Tmux("%1", 100)], null, Answered: false, Truncated: true), null);

        Assert.Equal(PaneKnowledge.Present, reader.Pane("tmux", "%1").State);
        var missing = reader.Pane("tmux", "%9");
        Assert.Equal(PaneKnowledge.Unknown, missing.State);
        Assert.Contains("ah roster recover now <member>", missing.Reason);
    }

    [Fact]
    public void AMissingPaneInAWholeListingIsStillAbsent()
    {
        var reader = Reader(new PaneListing([Tmux("%1", 100)], null), null);

        Assert.Equal(PaneKnowledge.Absent, reader.Pane("tmux", "%9").State);
    }

    // ---------------------------------------------------------------- the harness below a pane

    private static IReadOnlyList<ProcessRow> Table() =>
    [
        new(1, 0, "/sbin/launchd"),
        new(100, 1, "-zsh"),
        new(110, 100, "/bin/bash -c make"),
        new(120, 110, "/usr/local/bin/claude --resume abc"),
        new(200, 1, "-zsh"),
        new(210, 200, "sleep 120"),
        new(300, 1, "/usr/local/bin/claude --resume other"),
    ];

    [Fact]
    public void AHarnessTwoLevelsBelowThePanesRootIsRunning()
    {
        Assert.True(Reader(Listing(), Table()).HarnessRunning(100));
    }

    [Fact]
    public void APlainShellWithAChildThatIsNotAHarnessIsNotRunningOne()
    {
        Assert.False(Reader(Listing(), Table()).HarnessRunning(200));
    }

    [Fact]
    public void AHarnessOutsideThePanesSubtreeDoesNotCount()
    {
        Assert.False(Reader(Listing(), Table()).HarnessRunning(210));
    }

    [Fact]
    public void ThePanesRootItselfBeingAHarnessCounts()
    {
        Assert.True(Reader(Listing(), Table()).HarnessRunning(300));
    }

    [Fact]
    public void AHarnessStartedByAPathWithASpaceIsFoundThroughTheExecutableReader()
    {
        IReadOnlyList<ProcessRow> rows = [new(100, 1, "-zsh"), new(110, 100, "/Users/someone else/.local/bin/claude --resume abc")];
        var withReader = new PaneStateReader(_ => Listing(), () => rows, () => new Dictionary<int, string> { [110] = "/Users/someone else/.local/bin/claude" });
        var withoutReader = new PaneStateReader(_ => Listing(), () => rows);

        Assert.True(withReader.HarnessRunning(100));
        Assert.False(withoutReader.HarnessRunning(100));
    }

    [Fact]
    public void ASpacedPathThatIsNotClaudeIsNotAHarness()
    {
        IReadOnlyList<ProcessRow> rows = [new(100, 1, "-zsh"), new(110, 100, "/Users/someone else/.local/bin/vim notes")];
        var reader = new PaneStateReader(_ => Listing(), () => rows, () => new Dictionary<int, string> { [110] = "/Users/someone else/.local/bin/vim" });

        Assert.False(reader.HarnessRunning(100));
    }

    [Fact]
    public void TheExecutablePathsAreReadOncePerRunAndWhenTheyCannotBeReadTheHarnessAnswerIsNull()
    {
        IReadOnlyList<ProcessRow> rows = [new(100, 1, "-zsh"), new(110, 100, "/bin/sleep 5")];
        var reads = 0;
        var reader = new PaneStateReader(_ => Listing(), () => rows, () =>
        {
            reads++;
            return null;
        });

        Assert.Null(reader.HarnessRunning(100));
        Assert.Null(reader.HarnessRunning(110));
        Assert.Equal(1, reads);
    }

    [Fact]
    public void ACommPsLineGivesPidAndTheWholePathIncludingSpaces()
    {
        var paths = PaneStateReader.ParseExecutables("  120 /Users/someone else/.local/bin/claude\nnot a row\n    7 -zsh\n   x bad\n");

        Assert.Equal("/Users/someone else/.local/bin/claude", paths[120]);
        Assert.Equal("-zsh", paths[7]);
        Assert.Equal(2, paths.Count);
    }

    [Fact]
    public void APaneMissingFromAListingWhoseOtherInstanceDidNotAnswerIsUnknownButAPaneInItIsPresent()
    {
        var partial = Reader(new PaneListing([new MuxPane("herdr", "/s1", "p_1", 10, null)], null, Answered: false), null);

        Assert.Equal(PaneKnowledge.Unknown, partial.Pane("herdr", "p_2").State);
        Assert.Equal(new PaneReading(PaneKnowledge.Present, 10), partial.Pane("herdr", "p_1"));
    }

    [Fact]
    public void AProcessTableThatCouldNotBeReadSaysNothingAndIsReadOncePerRun()
    {
        var reads = 0;
        var reader = Reader(Listing(), null, onProcesses: () => reads++);

        Assert.Null(reader.HarnessRunning(100));
        Assert.Null(reader.HarnessRunning(200));

        Assert.Equal(1, reads);
    }

    [Fact]
    public void ACycleInTheTableDoesNotLoopForever()
    {
        IReadOnlyList<ProcessRow> rows = [new(10, 20, "-zsh"), new(20, 10, "-zsh")];

        Assert.False(Reader(Listing(), rows).HarnessRunning(10));
    }

    // ---------------------------------------------------------------- the ps reading

    [Fact]
    public void APsLineGivesPidParentAndTheWholeCommandAndAnUnreadableLineIsSkipped()
    {
        var rows = PaneStateReader.Parse("  120   110 /usr/local/bin/claude --resume abc def\nnot a row\n    7     1 -zsh\n   x     1 bad\n");

        Assert.Equal([new ProcessRow(120, 110, "/usr/local/bin/claude --resume abc def"), new ProcessRow(7, 1, "-zsh")], rows);
    }
}
