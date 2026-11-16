using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Platform;
using Ah.TestKit;

namespace Ah.State.Tests;

/// <summary>The path-argument containment of a state request resolves each distinct path once.</summary>
public sealed class PathArgsCheckTests
{
    private sealed class CountingPolicy(IHierarchyDirPolicy inner) : IHierarchyDirPolicy
    {
        internal Dictionary<string, int> Calls { get; } = new(StringComparer.Ordinal);

        public string DerivedHierarchyDir(string cwd) => inner.DerivedHierarchyDir(cwd);

        public string? DerivedMainHierarchyDir(string cwd) => inner.DerivedMainHierarchyDir(cwd);

        public RequestContextProblem? CheckClaim(RequestContext context) => inner.CheckClaim(context);

        public string? Realish(string path)
        {
            Calls[path] = Calls.GetValueOrDefault(path) + 1;
            return inner.Realish(path);
        }

        public HierarchyOverride Override() => inner.Override();
    }

    private static (StateRequestChecks Checks, CountingPolicy Policy, string Dir) Rig()
    {
        var root = UnitSandbox.Create().Root;
        var dir = root + "/proj/.claude/ah2";
        Directory.CreateDirectory(dir);
        var env = new FakeEnvironment(new Dictionary<string, string> { ["AH_HOME"] = root + "/ah" }, null);
        var files = PlatformModule.CreateIo(null).Files;
        var policy = new CountingPolicy(Ah.Config.ConfigModule.CreateHierarchyDirPolicy(files, env, null, () => PlatformModule.CurrentUid()));
        return (new StateRequestChecks(files, () => PlatformModule.CurrentUid(), policy), policy, dir);
    }

    [Fact]
    public void AnInProcessReadNamingOneDirectoryThreeTimesResolvesItOnce()
    {
        var (checks, policy, dir) = Rig();

        var problem = checks.CheckPathArgs(["dir"], JsonValue.FromObject(new JsonObject { ["dir"] = dir }), new RequestContext(dir, dir, string.Empty, string.Empty));

        Assert.Null(problem);
        Assert.Equal(1, policy.Calls[dir]);
    }

    [Fact]
    public void APathOutsideTheDirectoryIsStillResolvedOnItsOwnAndRefused()
    {
        var (checks, policy, dir) = Rig();
        var outside = Path.GetDirectoryName(dir)! + "/other";

        var problem = checks.CheckPathArgs(["dir"], JsonValue.FromObject(new JsonObject { ["dir"] = outside }), new RequestContext(dir, dir, string.Empty, string.Empty));

        Assert.Equal("dir", problem!.Field);
        Assert.Equal(1, policy.Calls[outside]);
    }

    [Fact]
    public void AMissingLinkedTreeHierarchyDirPassesOnlyForADbOnlyDomainAndNegativeNotElsewhereOrForFiles()
    {
        var (checks, policy, dir) = Rig();
        var root = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(dir)))!;
        var gitdir = root + "/main/.git/worktrees/wt";
        Directory.CreateDirectory(gitdir);
        Directory.CreateDirectory(root + "/main/.claude/ah2");
        Directory.CreateDirectory(root + "/wt");
        File.WriteAllText(gitdir + "/commondir", "../..\n");
        File.WriteAllText(gitdir + "/gitdir", root + "/wt/.git\n");
        File.WriteAllText(root + "/wt/.git", "gitdir: " + gitdir + "\n");
        Directory.CreateDirectory(root + "/plain/.git");

        JsonValue Context(string cwd, string hier, string? main) => JsonValue.FromObject(new JsonObject
        {
            ["cwd"] = cwd,
            ["hierarchy_dir"] = hier,
            ["main_hierarchy_dir"] = main is null ? JsonValue.Null : main,
        });

        var tree = Context(root + "/wt", root + "/wt/.claude/ah2", root + "/main/.claude/ah2");
        Assert.Null(checks.CheckContext(tree, dbOnly: true));
        Assert.Equal("hierarchy_dir", checks.CheckContext(tree)!.Field);
        Assert.Equal("hierarchy_dir", checks.CheckContext(Context(root + "/plain", root + "/plain/.claude/ah2", null), dbOnly: true)!.Field);

        // A tree root owned by another uid is not accepted: the hierarchy_dir falls through to the existence check.
        var foreign = new StateRequestChecks(PlatformModule.CreateIo(null).Files, () => PlatformModule.CurrentUid() + 1, policy);
        Assert.Contains("does not exist", foreign.CheckContext(tree, dbOnly: true)!.Message, StringComparison.Ordinal);
    }
}
