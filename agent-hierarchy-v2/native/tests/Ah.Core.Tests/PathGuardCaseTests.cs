using Ah.Core.Text;

namespace Ah.Core.Tests;

/// <summary>A part's on-disk spelling is kept for a few seconds so a read does not list a directory per path part; links are followed fresh on every read.</summary>
public sealed class PathGuardCaseTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("pathguard-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private bool VolumeIgnoresCase()
    {
        var probe = Path.Join(_root, "CaseProbe");
        _ = Directory.CreateDirectory(probe);
        var ignores = Directory.Exists(Path.Join(_root, "caseprobe"));
        Directory.Delete(probe);
        return ignores && (OperatingSystem.IsMacOS() || OperatingSystem.IsWindows());
    }

    private static void RenameCaseOnly(string from, string to)
    {
        var middle = from + "-moving";
        Directory.Move(from, middle);
        Directory.Move(middle, to);
    }

    [Fact]
    public void ALinkKeepsItsOwnSpellingBeforeItIsFollowedAndAMissingPartIsKeptAsWritten()
    {
        if (!VolumeIgnoresCase())
        {
            return;
        }

        var target = Path.Join(_root, "Target-Dir");
        _ = Directory.CreateDirectory(target);
        File.WriteAllText(Path.Join(target, "Mixed.TXT"), "x");
        _ = Directory.CreateSymbolicLink(Path.Join(_root, "Link-Name"), target);
        var real = PathGuard.RealPath(_root, strict: false)!;

        // the link's name resolves through it, whatever case it is read in, and the file below takes its own case
        Assert.Equal(Path.Join(real, "Target-Dir", "Mixed.TXT"), PathGuard.RealPath(Path.Join(_root, "LINK-NAME", "mixed.txt"), strict: false));
        Assert.Equal(Path.Join(real, "No-Such", "Part"), PathGuard.RealPath(Path.Join(_root, "No-Such", "Part"), strict: false));
    }

    [Theory]
    [InlineData("star*name", "STAR*NAME")]
    [InlineData("q?mark", "Q?MARK")]
    [InlineData("änderung", "ÄNDERUNG")]
    public void ANameThatWouldBeAPatternOrNotAsciiTakesItsOnDiskCase(string onDisk, string asked)
    {
        if (!VolumeIgnoresCase())
        {
            return;
        }

        _ = Directory.CreateDirectory(Path.Join(_root, onDisk));
        var real = PathGuard.RealPath(_root, strict: false)!;

        Assert.Equal(Path.Join(real, onDisk), PathGuard.RealPath(Path.Join(_root, asked), strict: false));
    }

    [Fact]
    public void NegativeAnExactSpellingAndADifferentNameAreLeftAlone()
    {
        _ = Directory.CreateDirectory(Path.Join(_root, "Exact"));
        var real = PathGuard.RealPath(_root, strict: false)!;

        Assert.Equal(Path.Join(real, "Exact"), PathGuard.RealPath(Path.Join(_root, "Exact"), strict: false));
        Assert.Equal(Path.Join(real, "Exac"), PathGuard.RealPath(Path.Join(_root, "Exac"), strict: false));
    }

    [Fact]
    public void NegativeWithNoScopeASpellingIsNotKept()
    {
        if (!VolumeIgnoresCase())
        {
            return;
        }

        var lower = Path.Join(_root, "unkept");
        var upper = Path.Join(_root, "UNKEPT");
        _ = Directory.CreateDirectory(lower);
        Assert.EndsWith("/unkept", PathGuard.RealPath(upper, strict: false)!, StringComparison.Ordinal);

        RenameCaseOnly(lower, upper);

        Assert.EndsWith("/UNKEPT", PathGuard.RealPath(upper, strict: false)!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AThreadAndATaskStartedInTheScopeShareItsSpellings()
    {
        if (!VolumeIgnoresCase())
        {
            return;
        }

        using var keep = PathGuard.KeepCases();
        var lower = Path.Join(_root, "shared");
        var upper = Path.Join(_root, "SHARED");
        _ = Directory.CreateDirectory(lower);
        _ = PathGuard.RealPath(upper, strict: false);
        RenameCaseOnly(lower, upper);

        string? onThread = null;
        var thread = new Thread(() => onThread = PathGuard.RealPath(upper, strict: false));
        thread.Start();
        thread.Join();
        var onTask = await Task.Run(() => PathGuard.RealPath(upper, strict: false), TestContext.Current.CancellationToken);

        Assert.EndsWith("/shared", onThread!, StringComparison.Ordinal);
        Assert.EndsWith("/shared", onTask!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASpellingIsKeptForTheWindowAndReadAgainAfterIt()
    {
        if (!VolumeIgnoresCase())
        {
            return;
        }

        using var keep = PathGuard.KeepCases();
        var lower = Path.Join(_root, "kept");
        var upper = Path.Join(_root, "KEPT");
        _ = Directory.CreateDirectory(lower);
        var first = PathGuard.RealPath(upper, strict: false)!;
        Assert.EndsWith("/kept", first, StringComparison.Ordinal);

        RenameCaseOnly(lower, upper);
        Assert.EndsWith("/kept", PathGuard.RealPath(upper, strict: false)!, StringComparison.Ordinal);

        await Task.Delay(5100, TestContext.Current.CancellationToken);
        Assert.EndsWith("/KEPT", PathGuard.RealPath(upper, strict: false)!, StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeARetargetedLinkIsFollowedAtOnce()
    {
        using var keep = PathGuard.KeepCases();
        var a =Directory.CreateDirectory(Path.Join(_root, "a")).FullName;
        var b = Directory.CreateDirectory(Path.Join(_root, "b")).FullName;
        var link = Path.Join(_root, "link");
        _ = Directory.CreateSymbolicLink(link, a);
        Assert.EndsWith("/a", PathGuard.RealPath(link, strict: false)!, StringComparison.Ordinal);

        Directory.Delete(link);
        _ = Directory.CreateSymbolicLink(link, b);

        Assert.EndsWith("/b", PathGuard.RealPath(link, strict: false)!, StringComparison.Ordinal);
    }

    [Fact]
    public void AScopeEnteredFromASharedSetKeepsWhatAnEarlierEntryFound()
    {
        if (!VolumeIgnoresCase())
        {
            return;
        }

        var enter = PathGuard.ShareCases();
        var lower = Path.Join(_root, "entered");
        var upper = Path.Join(_root, "ENTERED");
        _ = Directory.CreateDirectory(lower);
        using (enter())
        {
            _ = PathGuard.RealPath(upper, strict: false);
        }

        RenameCaseOnly(lower, upper);

        using (enter())
        {
            Assert.EndsWith("/entered", PathGuard.RealPath(upper, strict: false)!, StringComparison.Ordinal);
        }

        // negative: another set has found nothing yet
        using (PathGuard.ShareCases()())
        {
            Assert.EndsWith("/ENTERED", PathGuard.RealPath(upper, strict: false)!, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void NegativeAPartNotOnDiskIsNotKeptSoItsLaterSpellingIsRead()
    {
        if (!VolumeIgnoresCase())
        {
            return;
        }

        using var keep = PathGuard.KeepCases();
        var asked = Path.Join(_root, "LATER");
        Assert.EndsWith("/LATER", PathGuard.RealPath(asked, strict: false)!, StringComparison.Ordinal);

        _ = Directory.CreateDirectory(Path.Join(_root, "later"));

        Assert.EndsWith("/later", PathGuard.RealPath(asked, strict: false)!, StringComparison.Ordinal);
    }
}
