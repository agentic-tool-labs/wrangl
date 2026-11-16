namespace Ah.Watch.Tests;

/// <summary>The worktrees-piling-up nudge (spec worktree-cleanup §5): raise at 30, once per episode, clear quietly below 20.</summary>
public sealed class WorktreeWatchTests
{
    [Fact]
    public void RaisesOncePerEpisodeAndAgainAfterFallingBelowTheClearLine()
    {
        string? saved = null;
        var at = DateTimeOffset.Parse("2026-11-09T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var episodes = new AlarmEpisodes(() => saved, text => saved = text, () => at);
        var watch = new WorktreeWatch(episodes, () => at);

        Assert.Null(watch.Sample("/r", 29));
        Assert.Equal("/r: 30 work trees. Run Tidy C: branch-sweep.sh -C /r --target <integration branch>.", watch.Sample("/r", 30));
        Assert.Null(watch.Sample("/r", 31));
        Assert.Null(new WorktreeWatch(episodes, () => at).Sample("/r", 31)); // a daemon restart does not tell it again
        Assert.Null(watch.Sample("/r", 20)); // still the same episode
        Assert.Null(watch.Sample("/r", 19)); // cleared, quietly
        Assert.NotNull(watch.Sample("/r", 30));
        Assert.Equal(("/main", 2), WorktreeWatch.Parse("worktree /main\nHEAD a\n\nworktree /a\n\nworktree /b\n"));
    }
}
