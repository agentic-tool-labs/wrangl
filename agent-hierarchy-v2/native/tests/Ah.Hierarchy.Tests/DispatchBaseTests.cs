using System.Diagnostics;

namespace Ah.Hierarchy.Tests;

public sealed class DispatchBaseTests
{
    private static readonly string A = new('a', 40);
    private static readonly string B = new('b', 40);

    private static string? Base(string? slug, Dictionary<string, string> files) =>
        HierarchyModule.DispatchBaseOfWorkTree("/r", slug, p => files.GetValueOrDefault(p));

    private static Dictionary<string, string> Repo() => new()
    {
        ["/r/.git/HEAD"] = "ref: refs/heads/main\n",
        ["/r/.git/refs/heads/main"] = A + "\n",
    };

    [Fact]
    public void WithNoSlugItIsTheCommitAtHead() => Assert.Equal(A, Base(null, Repo()));

    [Fact]
    public void ALooseBranchOfTheSlugWinsAndNegativeAMissingOneIsHead()
    {
        var files = Repo();
        Assert.Equal(A, Base("work", files));
        files["/r/.git/refs/heads/ah/work"] = B + "\n";
        Assert.Equal(B, Base("work", files));
        Assert.Equal(A, Base("other", files));
    }

    [Fact]
    public void APackedBranchIsReadAndNegativeAnotherBranchsRowIsNot()
    {
        var files = new Dictionary<string, string>
        {
            ["/r/.git/HEAD"] = "ref: refs/heads/main\n",
            ["/r/.git/packed-refs"] = $"# pack-refs with: peeled fully-peeled sorted\n{A} refs/heads/main\n{B} refs/heads/ah/work\n",
        };
        Assert.Equal(B, Base("work", files));
        Assert.Equal(A, Base("work2", files));
    }

    [Fact]
    public void ALooseRefBeatsAPackedOne()
    {
        var files = Repo();
        files["/r/.git/packed-refs"] = $"{B} refs/heads/main\n";
        Assert.Equal(A, Base(null, files));
    }

    [Fact]
    public void ADetachedHeadIsItsOwnCommitAndNegativeAShortOneIsNotSettled()
    {
        Assert.Equal(A, Base(null, new() { ["/r/.git/HEAD"] = A + "\n" }));
        Assert.Null(Base(null, new() { ["/r/.git/HEAD"] = "aaaaaaa\n" }));
    }

    [Fact]
    public void ALinkedWorkTreeReadsItsHeadThereAndItsRefsFromTheCommonDir()
    {
        var files = new Dictionary<string, string>
        {
            ["/r/.git"] = "gitdir: /m/.git/worktrees/r\n",
            ["/m/.git/worktrees/r/HEAD"] = "ref: refs/heads/side\n",
            ["/m/.git/worktrees/r/commondir"] = "../..\n",
            ["/m/.git/refs/heads/side"] = A + "\n",
        };
        Assert.Equal(A, Base(null, files));
    }

    [Fact]
    public void NegativeWhatTheFilesDoNotSettleIsLeftToGit()
    {
        Assert.Null(Base(null, []));
        Assert.Null(Base(null, new() { ["/r/.git"] = "not a pointer\n" }));
        Assert.Null(Base(null, new() { ["/r/.git/HEAD"] = "ref: refs/heads/.invalid\n" }));

        var symbolic = Repo();
        symbolic["/r/.git/refs/heads/ah/work"] = "ref: refs/heads/main\n";
        Assert.Null(Base("work", symbolic));
        Assert.Null(Base("../../main", Repo()));
        Assert.Null(Base("a b", Repo()));
    }

    [Fact]
    public void ItAgreesWithGitInARealRepositoryAndWorkTree()
    {
        var root = Directory.CreateTempSubdirectory("dispatch-base").FullName;
        try
        {
            var main = Path.Join(root, "m");
            Directory.CreateDirectory(main);
            Git(main, "init", "-q", "-b", "main");
            Git(main, "-c", "user.email=t@example.test", "-c", "user.name=t", "commit", "-q", "--allow-empty", "-m", "one");
            Git(main, "branch", "ah/packed");
            Git(main, "pack-refs", "--all");
            Git(main, "-c", "user.email=t@example.test", "-c", "user.name=t", "commit", "-q", "--allow-empty", "-m", "two");
            Git(main, "branch", "ah/loose");
            var tree = Path.Join(root, "w");
            Git(main, "worktree", "add", "-q", "-b", "side", tree, "ah/packed");

            static string? Read(string p) => File.Exists(p) ? File.ReadAllText(p) : null;
            foreach (var at in new[] { main, tree })
            {
                Assert.Equal(Git(at, "rev-parse", "HEAD"), HierarchyModule.DispatchBaseOfWorkTree(at, null, Read));
                Assert.Equal(Git(at, "rev-parse", "ah/packed"), HierarchyModule.DispatchBaseOfWorkTree(at, "packed", Read));
                Assert.Equal(Git(at, "rev-parse", "ah/loose"), HierarchyModule.DispatchBaseOfWorkTree(at, "loose", Read));
                Assert.Equal(Git(at, "rev-parse", "HEAD"), HierarchyModule.DispatchBaseOfWorkTree(at, "absent", Read));
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static string Git(string dir, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
        {
            start.ArgumentList.Add(a);
        }

        using var p = Process.Start(start)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
        return output.Trim();
    }
}
