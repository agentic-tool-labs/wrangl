using Ah.Abstractions;
using Ah.Platform;
using Ah.TestKit;

namespace Ah.State.Tests;

/// <summary>The caller-context check the state service and other modules share.</summary>
public sealed class RequestContextCheckerTests
{
    private sealed class Rig
    {
        internal Rig()
        {
            var sandbox = UnitSandbox.Create();
            Root = sandbox.Root;
            Directory.CreateDirectory(Root + "/ah");
            Directory.CreateDirectory(Root + "/proj/.claude/ah2");
            Directory.CreateDirectory(Root + "/proj/.git");
            File.WriteAllText(Root + "/a-file", "x");
            var env = new FakeEnvironment(new Dictionary<string, string> { ["AH_HOME"] = Root + "/ah" }, null);
            var files = PlatformModule.CreateIo(null).Files;
            Checker = StateModule.CreateRequestContextChecker(
                files, PlatformModule.CurrentUid(), Ah.Config.ConfigModule.CreateHierarchyDirPolicy(files, env, null, () => PlatformModule.CurrentUid()));
        }

        internal string Root { get; }

        internal IRequestContextChecker Checker { get; }

        internal RequestContext Good => new(Root + "/proj", Root + "/proj/.claude/ah2", string.Empty, string.Empty);
    }

    [Fact]
    public void ARealOwnedContextPassesAndOptionalDirectoriesMayBeAbsent()
    {
        var rig = new Rig();

        Assert.Null(rig.Checker.Check(rig.Good));
        Assert.Null(rig.Checker.Check(rig.Good with { SocketDirectory = rig.Root + "/proj" }));
    }

    [Fact]
    public void AHierarchyDirectoryTheCwdDoesNotDeriveToIsRefusedWithItsField()
    {
        var rig = new Rig();
        Directory.CreateDirectory(rig.Root + "/elsewhere/.claude/ah2");

        var other = rig.Checker.Check(rig.Good with { HierarchyDirectory = rig.Root + "/elsewhere/.claude/ah2" });
        var main = rig.Checker.Check(rig.Good with { MainHierarchyDirectory = rig.Root + "/proj/.claude/ah2" });

        Assert.Equal("hierarchy_dir", other!.Field);
        Assert.Equal("main_hierarchy_dir", main!.Field);
        Assert.Null(rig.Checker.Check(rig.Good));
    }

    [Fact]
    public void ALinkedWorktreesMainHierarchyIsAcceptedOnlyWithItsBackPointer()
    {
        var rig = new Rig();
        var gitdir = rig.Root + "/main/.git/worktrees/wt";
        Directory.CreateDirectory(gitdir);
        Directory.CreateDirectory(rig.Root + "/main/.claude/ah2");
        Directory.CreateDirectory(rig.Root + "/wt/.claude/ah2");
        File.WriteAllText(gitdir + "/commondir", "../..\n");
        File.WriteAllText(rig.Root + "/wt/.git", "gitdir: " + gitdir + "\n");
        var claim = new RequestContext(
            rig.Root + "/wt", rig.Root + "/wt/.claude/ah2", rig.Root + "/main/.claude/ah2", string.Empty);

        var forged = rig.Checker.Check(claim);
        File.WriteAllText(gitdir + "/gitdir", rig.Root + "/wt/.git\n");
        var genuine = rig.Checker.Check(claim);

        Assert.Equal("main_hierarchy_dir", forged!.Field);
        Assert.Null(genuine);
    }

    [Fact]
    public void ARelativeOrMissingOrNonDirectoryFieldIsNamed()
    {
        var rig = new Rig();

        var relative = rig.Checker.Check(rig.Good with { Cwd = "proj" });
        var missing = rig.Checker.Check(rig.Good with { HierarchyDirectory = rig.Root + "/nowhere" });
        var file = rig.Checker.Check(rig.Good with { HierarchyDirectory = rig.Root + "/a-file" });
        var empty = rig.Checker.Check(rig.Good with { HierarchyDirectory = string.Empty });

        Assert.Equal(new RequestContextProblem("cwd must be an absolute path", "cwd"), relative);
        Assert.Equal("hierarchy_dir", missing!.Field);
        Assert.EndsWith("does not exist", missing.Message, StringComparison.Ordinal);
        Assert.EndsWith("is not a directory", file!.Message, StringComparison.Ordinal);
        Assert.Equal("hierarchy_dir must be an absolute path", empty!.Message);
    }

    [Fact]
    public void APresentOptionalDirectoryIsCheckedToo()
    {
        var rig = new Rig();

        var main = rig.Checker.Check(rig.Good with { MainHierarchyDirectory = rig.Root + "/nowhere" });
        var socket = rig.Checker.Check(rig.Good with { SocketDirectory = "relative" });

        Assert.Equal("main_hierarchy_dir", main!.Field);
        Assert.Equal("messaging_socket_dir", socket!.Field);
    }
}
