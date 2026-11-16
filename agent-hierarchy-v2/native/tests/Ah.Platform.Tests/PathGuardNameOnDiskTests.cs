using Ah.Core.Text;
using Ah.Platform.Interop;

namespace Ah.Platform.Tests;

/// <summary>What the platform's one-call spelling lookup answers, and that <see cref="PathGuard"/> resolves through it as it does by listing the parent.</summary>
public sealed class PathGuardNameOnDiskTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("nameondisk-").FullName;

    public PathGuardNameOnDiskTests() => _ = PlatformModule.CreateIo(null);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private bool VolumeIgnoresCase()
    {
        var probe = Path.Join(_root, "CaseProbe");
        _ = Directory.CreateDirectory(probe);
        var ignores = Directory.Exists(Path.Join(_root, "caseprobe"));
        Directory.Delete(probe);
        return ignores && OperatingSystem.IsMacOS();
    }

    [Theory]
    [InlineData("Mixed.TXT", "mixed.txt")]
    [InlineData("star*name", "STAR*NAME")]
    [InlineData("änderung", "ÄNDERUNG")]
    public void TheLookupAndTheGuardBothGiveTheCaseOnDisk(string onDisk, string asked)
    {
        if (!VolumeIgnoresCase())
        {
            return;
        }

        File.WriteAllText(Path.Join(_root, onDisk), "x");

        Assert.Equal(onDisk, Libc.NameOnDisk(Path.Join(_root, asked)));
        Assert.Equal(Path.Join(PathGuard.RealPath(_root, strict: false), onDisk), PathGuard.RealPath(Path.Join(_root, asked), strict: false));
    }

    [Fact]
    public void ALinkAnswersWithItsOwnNameNotItsTargets()
    {
        if (!VolumeIgnoresCase())
        {
            return;
        }

        _ = Directory.CreateDirectory(Path.Join(_root, "Target-Dir"));
        _ = Directory.CreateSymbolicLink(Path.Join(_root, "Link-Name"), Path.Join(_root, "Target-Dir"));
        // a link whose target is gone still has a name of its own
        _ = File.CreateSymbolicLink(Path.Join(_root, "Dangling"), Path.Join(_root, "gone"));

        Assert.Equal("Link-Name", Libc.NameOnDisk(Path.Join(_root, "LINK-NAME")));
        Assert.Equal("Dangling", Libc.NameOnDisk(Path.Join(_root, "dangling")));
        Assert.Equal(Path.Join(PathGuard.RealPath(_root, strict: false), "Target-Dir"), PathGuard.RealPath(Path.Join(_root, "link-name"), strict: false));
    }

    [Fact]
    public void NegativeAMissingPartOrAPathThroughAFileHasNoName()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        File.WriteAllText(Path.Join(_root, "file"), "x");

        Assert.Null(Libc.NameOnDisk(Path.Join(_root, "no-such")));
        Assert.Null(Libc.NameOnDisk(Path.Join(_root, "file", "below")));
        Assert.Equal(Path.Join(PathGuard.RealPath(_root, strict: false), "No-Such"), PathGuard.RealPath(Path.Join(_root, "No-Such"), strict: false));
    }

    [Fact]
    public void NegativeAnExactSpellingIsAnsweredAsItIs()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        _ = Directory.CreateDirectory(Path.Join(_root, "Exact"));

        Assert.Equal("Exact", Libc.NameOnDisk(Path.Join(_root, "Exact")));
    }
}
