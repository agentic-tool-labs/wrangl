namespace Ah.Cli.Tests;

public sealed partial class MessagePoolResolutionTests
{
    /// <summary>A pool path without a trailing slash and without macOS's <c>/private</c> alias of <c>/tmp</c>, so the real and the written spelling compare equal.</summary>
    private static string Key(string path)
    {
        var full = Path.GetFullPath(path).TrimEnd('/');
        return full.StartsWith("/private/", StringComparison.Ordinal) ? full["/private".Length..] : full;
    }

    [Fact]
    public void ThePoolsOfAWorktreeCwdIncludeTheMainCheckoutsPool()
    {
        WriteMessage(Home, TeamFile);

        var pools = Messages().PoolDirs(Legacy, ["t"]).Select(Key).ToList();

        Assert.Contains(Key(Home), pools);
        Assert.Equal(pools.Count, pools.Distinct().Count());
    }

    [Fact]
    public void AMainPoolIsListedForAWorktreeCwdWithNoMessageFiles()
    {
        Directory.CreateDirectory(Home);

        var pools = Messages().PoolDirs(Legacy, ["t"]).Select(Key).ToList();

        Assert.Contains(Key(Home), pools);
    }

    [Fact]
    public void ThePoolsOfTheMainCheckoutListItsPoolOnce()
    {
        WriteMessage(Home, TeamFile);

        var pools = Messages().PoolDirs(Home, ["t"]).Select(Key).ToList();

        Assert.Single(pools, p => p == Key(Home));
    }

    [Fact]
    public void ARequestFiledInTheMainPoolIsFoundFromAWorktreeCwdWithItsTree()
    {
        WriteMessage(Home, TeamFile);
        var graph = Graph();

        var pools = graph.Hierarchy.Messages.PoolDirs(Legacy, ["t"], [Worktree]);
        var exchanges = graph.Hierarchy.Core.ListExchanges(pools);

        Assert.Contains(exchanges, e => e.Member("id") is { IsText: true } id && id.AsString() == Id);
    }

    [Fact]
    public void ARequestInAnUnrelatedCheckoutsPoolIsNotFoundFromAWorktreeCwd()
    {
        WriteMessage(OtherPool, TeamFile);
        var graph = Graph();

        var pools = graph.Hierarchy.Messages.PoolDirs(Legacy, ["t"], [Worktree]);

        Assert.DoesNotContain(graph.Hierarchy.Core.ListExchanges(pools), e => e.Member("id") is { IsText: true } id && id.AsString() == Id);
    }
}
