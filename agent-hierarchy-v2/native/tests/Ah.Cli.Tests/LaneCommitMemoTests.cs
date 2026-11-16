using System.Diagnostics;
using Ah.Cli.Composition;
using Ah.Platform;

namespace Ah.Cli.Tests;

/// <summary>b459 step 19, Amendment 1: a lane work tree's latest commit is held while its HEAD and reflog are unchanged.</summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class LaneCommitMemoTests : IDisposable
{
    private static readonly string[] Identity = ["-c", "user.name=t", "-c", "user.email=t@t", "-c", "commit.gpgsign=false"];

    private readonly string _root = Directory.CreateTempSubdirectory("lane-commit-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static void Git(string tree, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = tree, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in Identity.Concat(args))
        {
            start.ArgumentList.Add(a);
        }

        using var p = Process.Start(start)!;
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
    }

    [Fact]
    public void AnUnchangedTreeRunsNoReadAndNegativeACommitIsSeenOnTheNextRead()
    {
        var tree = Path.Combine(_root, "w");
        Directory.CreateDirectory(tree);
        Git(tree, "init", "-q");
        Git(tree, "commit", "-q", "--allow-empty", "-m", "one");
        var memo = new LaneCommitMemo(PlatformModule.CreateIo(null).Files);
        var reads = 0;
        double? Read()
        {
            reads++;
            return reads;
        }

        Assert.Equal(1, memo.Of(tree, Read));
        Assert.Equal(1, memo.Of(tree, Read));
        Assert.Equal(1, reads);

        // a commit appends to the reflog: the next read runs git again
        Git(tree, "commit", "-q", "--allow-empty", "-m", "two");
        Assert.Equal(2, memo.Of(tree, Read));

        memo.Forget();
        Assert.Equal(3, memo.Of(tree, Read));
    }

    [Fact]
    public void NegativeATreeWithNoReflogIsReadEveryTime()
    {
        var tree = Path.Combine(_root, "plain");
        Directory.CreateDirectory(tree);
        var memo = new LaneCommitMemo(PlatformModule.CreateIo(null).Files);
        var reads = 0;
        double? Read() => ++reads;

        _ = memo.Of(tree, Read);
        _ = memo.Of(tree, Read);
        Assert.Equal(2, reads);
    }
}
