using Ah.Platform;
using Ah.Roster;
using Ah.TestKit;

namespace Ah.Config.Tests;

/// <summary>A worktree's main hierarchy dir is looked up once per dir; a dir that had none is looked up again.</summary>
public sealed class MainDirCacheTests
{
    [Fact]
    public void AFoundMainDirIsKeptAndNegativeADirWithNoneIsAskedAgain()
    {
        var sandbox = Directory.CreateTempSubdirectory("ah-maindir-").FullName;
        try
        {
            var home = Path.Combine(sandbox, "home");
            var main = Path.Combine(sandbox, "main");
            var tree = Path.Combine(sandbox, "tree");
            var gitdir = Path.Combine(main, ".git", "worktrees", "tree");
            Directory.CreateDirectory(home);
            Directory.CreateDirectory(gitdir);
            Directory.CreateDirectory(tree);
            File.WriteAllText(Path.Combine(gitdir, "gitdir"), Path.Combine(tree, ".git") + "\n");
            File.WriteAllText(Path.Combine(gitdir, "commondir"), "../..\n");
            var env = new FakeEnvironment(new Dictionary<string, string> { ["HOME"] = home });
            var io = PlatformModule.CreateIo(null);
            IRosterCore roster = null!;
            var config = ConfigModule.CreateCore(io.Files, env, () => roster, new ProcessFacts(tree, 4242));
            roster = RosterModule.CreateCore(io.Files, env, config, io.Processes);

            Assert.Null(config.MainHierarchyDir(tree));

            File.WriteAllText(Path.Combine(tree, ".git"), "gitdir: " + gitdir + "\n");
            var found = config.MainHierarchyDir(tree);
            Assert.NotNull(found);

            File.Delete(Path.Combine(tree, ".git"));
            Assert.Equal(found, config.MainHierarchyDir(tree));
        }
        finally
        {
            Directory.Delete(sandbox, recursive: true);
        }
    }

    [Fact]
    public void AMainCheckoutsNoneIsKeptAndNegativeANewGitIsAskedAgain()
    {
        var sandbox = Directory.CreateTempSubdirectory("ah-maindir-").FullName;
        try
        {
            var home = Path.Combine(sandbox, "home");
            var main = Path.Combine(sandbox, "main");
            var dotgit = Path.Combine(main, ".git");
            Directory.CreateDirectory(home);
            Directory.CreateDirectory(dotgit);
            var env = new FakeEnvironment(new Dictionary<string, string> { ["HOME"] = home });
            var io = PlatformModule.CreateIo(null);
            var files = new CountingFiles(io.Files);
            IRosterCore roster = null!;
            var config = ConfigModule.CreateCore(files, env, () => roster, new ProcessFacts(main, 4242));
            roster = RosterModule.CreateCore(io.Files, env, config, io.Processes);
            int Asked() => files.TextReads.Where(p => p.Key.EndsWith("/main/.git", StringComparison.Ordinal)).Sum(static p => p.Value);

            long ms = 0;
            ((Ah.Config.ConfigCore)config).MonotonicMs = () => ms;

            Assert.Null(config.MainHierarchyDir(main));
            Assert.Null(config.MainHierarchyDir(main));
            Assert.Equal(1, Asked());

            // negative: a new .git (another inode) is judged again
            Directory.Delete(dotgit);
            Directory.CreateDirectory(dotgit);
            Assert.Null(config.MainHierarchyDir(main));
            Assert.Equal(2, Asked());

            // negative: the same .git past the TTL is judged again (a pointer repaired in place)
            ms += Ah.Config.ConfigCore.NoMainTtlMs;
            Assert.Null(config.MainHierarchyDir(main));
            Assert.Equal(3, Asked());
        }
        finally
        {
            Directory.Delete(sandbox, recursive: true);
        }
    }
}
