using Ah.Core.Json;
using Ah.Core.Text;

namespace Ah.Hooks.Tests;

public sealed partial class HooksHostTests
{
    private const string ReviewerGate = "pretooluse-reviewer-write-gate";

    // ---------------------------------------------------------------- the shared resolver

    private static string RealRoot(Rig rig) => rig.Services.Files.RealPath(rig.Sandbox.Root);

    [Fact(Skip = "pruned 2026-11-14: attacker already runs as the user; not applicable to a local daemon")]
    public void ADanglingLinkIsFollowedToWhereItsTargetWouldBe()
    {
        var rig = new Rig();
        var root = RealRoot(rig);
        var protectedDir = Path.Combine(root, "main", ".claude");
        Directory.CreateDirectory(protectedDir);
        Directory.CreateDirectory(Path.Combine(root, "wt"));
        File.CreateSymbolicLink(Path.Combine(root, "wt", "out"), "/ahprobe-nowhere/x");
        File.CreateSymbolicLink(Path.Combine(root, "wt", "prot"), Path.Combine(protectedDir, "settings.json"));
        File.CreateSymbolicLink(Path.Combine(root, "wt", "rel"), "../main/.claude/new/f");

        Assert.Equal("/ahprobe-nowhere/x", CanonicalPath.Resolve(rig.Services.Files, Path.Combine(root, "wt", "out")));
        Assert.Equal(Path.Combine(protectedDir, "settings.json"), CanonicalPath.Resolve(rig.Services.Files, Path.Combine(root, "wt", "prot")));
        Assert.Equal(Path.Combine(protectedDir, "new", "f"), CanonicalPath.Resolve(rig.Services.Files, Path.Combine(root, "wt", "rel")));
    }

    [Fact]
    public void AChainOfLinksIsFollowedAndOnlyTheRemainderIsLiteral()
    {
        var rig = new Rig();
        var root = RealRoot(rig);
        var real = Path.Combine(root, "real");
        Directory.CreateDirectory(real);
        File.CreateSymbolicLink(Path.Combine(root, "l3"), real);
        File.CreateSymbolicLink(Path.Combine(root, "l2"), "l3");
        File.CreateSymbolicLink(Path.Combine(root, "l1"), Path.Combine(root, "l2"));

        Assert.Equal(Path.Combine(real, "new", "f"), CanonicalPath.Resolve(rig.Services.Files, Path.Combine(root, "l1", "new", "f")));
        Assert.Equal(real, CanonicalPath.Resolve(rig.Services.Files, Path.Combine(root, "l1")));
    }

    [Fact(Skip = "pruned 2026-11-14: attacker already runs as the user; not applicable to a local daemon")]
    public void ALoopAndAnOverlongChainAreUnresolvableAndAShorterChainIsNot()
    {
        var rig = new Rig();
        var root = RealRoot(rig);
        File.CreateSymbolicLink(Path.Combine(root, "a"), "b");
        File.CreateSymbolicLink(Path.Combine(root, "b"), "a");
        File.CreateSymbolicLink(Path.Combine(root, "self"), "self");
        var end = Path.Combine(root, "end");
        Directory.CreateDirectory(end);
        for (var i = 0; i <= CanonicalPath.MaxLinks; i++)
        {
            var target = i == 0 ? end : Path.Combine(root, $"c{i - 1}");
            File.CreateSymbolicLink(Path.Combine(root, $"c{i}"), target);
        }

        Assert.Null(CanonicalPath.Resolve(rig.Services.Files, Path.Combine(root, "a", "x")));
        Assert.Null(CanonicalPath.Resolve(rig.Services.Files, Path.Combine(root, "self")));
        Assert.Null(CanonicalPath.Resolve(rig.Services.Files, Path.Combine(root, $"c{CanonicalPath.MaxLinks}")));
        Assert.Equal(end, CanonicalPath.Resolve(rig.Services.Files, Path.Combine(root, $"c{CanonicalPath.MaxLinks - 1}")));
    }

    [Fact(Skip = "pruned 2026-11-14: attacker already runs as the user; not applicable to a local daemon")]
    public void ADotDotAfterALinkNamesTheParentOfTheTargetAndAfterAMissingNameIsUnresolvable()
    {
        var rig = new Rig();
        var root = RealRoot(rig);
        Directory.CreateDirectory(Path.Combine(root, "deep", "er"));
        File.CreateSymbolicLink(Path.Combine(root, "lnk"), Path.Combine(root, "deep", "er"));

        Assert.Equal(Path.Combine(root, "deep", "x"), CanonicalPath.Resolve(rig.Services.Files, Path.Combine(root, "lnk", "..", "x")));
        Assert.Equal(Path.Combine(root, "new", "a"), CanonicalPath.Resolve(rig.Services.Files, Path.Combine(root, "new", ".", "a")));
        Assert.Null(CanonicalPath.Resolve(rig.Services.Files, Path.Combine(root, "missing", "..", "x")));
    }

    [Fact]
    public void ATmpPathResolvesToTheRealTempDirectory()
    {
        var rig = new Rig();
        var files = rig.Services.Files;

        var resolved = CanonicalPath.Resolve(files, "/tmp/ahprobe-not-there/y");

        Assert.Equal(files.RealPath("/tmp") + "/ahprobe-not-there/y", resolved);
        if (OperatingSystem.IsMacOS())
        {
            Assert.Equal("/private/tmp/ahprobe-not-there/y", resolved);
        }
    }

    [Fact]
    public void AStoredRootIsCanonicalOnlyWhileItStillNamesItsOwnRealPath()
    {
        var rig = new Rig();
        var files = rig.Services.Files;
        var root = RealRoot(rig);
        var tree = Path.Combine(root, "wt");
        Directory.CreateDirectory(tree);
        var moved = Path.Combine(root, "wt-moved");
        var elsewhere = Path.Combine(root, "elsewhere");
        Directory.CreateDirectory(elsewhere);

        Assert.True(CanonicalPath.StillCanonical(files, tree));

        Directory.Delete(tree);
        File.CreateSymbolicLink(tree, elsewhere);
        Assert.False(CanonicalPath.StillCanonical(files, tree));

        File.Delete(tree);
        Assert.False(CanonicalPath.StillCanonical(files, tree));
        Directory.Move(elsewhere, moved);
        Assert.True(CanonicalPath.StillCanonical(files, moved));
    }

    [Theory]
    [InlineData("/m/wt/.GIT/hooks/x", ".git", true)]
    [InlineData("/m/wt/.git", ".git", true)]
    [InlineData("/m/wt/gitx/x", ".git", false)]
    [InlineData("/m/wt/x.git/y", ".git", false)]
    public void ASegmentIsNamedIgnoringCase(string path, string name, bool expected) =>
        Assert.Equal(expected, CanonicalPath.HasSegment(path, name));

    [Theory]
    [InlineData("/m/.Claude/SETTINGS.JSON", "/m/.claude", true)]
    [InlineData("/m/.claude", "/m/.claude", true)]
    [InlineData("/a/wt2/f", "/a/wt", false)]
    [InlineData("/a", "/a/wt", false)]
    [InlineData("/r/cafe/.git", "/r/caf\u00e9", false)]
    public void ARootContainsAPathBySegmentIgnoringCaseAndNormalisationForm(string path, string root, bool expected) =>
        Assert.Equal(expected, CanonicalPath.IsUnder(path, root));

    // ---------------------------------------------------------------- the reviewer's trust-file rule uses the shared resolver

    [Fact(Skip = "pruned 2026-11-14: attacker already runs as the user; not applicable to a local daemon")]
    public void ALinkLoopInFrontOfATrustFileNameIsNotTheTrustFile()
    {
        var rig = new Rig();
        var link = Path.Combine(rig.Sandbox.Home, "loop");
        File.CreateSymbolicLink(link, "loop");

        var outcome = rig.Run(ReviewerGate, Event("Write", "wrangl:implementor", new JsonObject { ["file_path"] = link }));

        Assert.False(IsDenied(outcome));
    }
}
