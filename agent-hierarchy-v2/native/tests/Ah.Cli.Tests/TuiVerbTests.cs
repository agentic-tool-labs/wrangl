namespace Ah.Cli.Tests;

/// <summary><c>ahd tui</c> replaces itself with the <c>ahd-tui</c> installed beside it, once its contract sidecar matches.</summary>
public sealed class TuiVerbTests
{
    private static string Sidecar(string contract) => "{\"version\":\"0.0.0\",\"contract\":" + contract + "}\n";

    private static string Install(Harness harness, string? contract)
    {
        var sibling = harness.Sandbox.Executable("install/ahd-tui");
        if (contract is not null)
        {
            File.WriteAllText(sibling + ".contract", Sidecar(contract));
        }

        return sibling;
    }

    [Fact]
    public void ItExecsTheSiblingWithTheArgumentTailUnchanged()
    {
        using var harness = new Harness();
        var sibling = Install(harness, BuildInfo.Contract);

        var (code, _, stderr) = harness.Run("tui", "--once", "--path", "/o/x");

        Assert.Equal(0, code);
        Assert.Empty(stderr);
        Assert.Equal([sibling, "--once", "--path", "/o/x"], harness.Exec.Argv);
        Assert.Null(harness.Exec.Environment);
        Assert.Empty(harness.Runner.Calls);
    }

    private static string LinkIn(Harness harness, string directory, string target)
    {
        var dir = Path.Combine(harness.Sandbox.Home, directory);
        Directory.CreateDirectory(dir);
        var link = Path.Combine(dir, "ahd");
        File.CreateSymbolicLink(link, target);
        return link;
    }

    [Fact]
    public void AnAhdLinkInAnotherDirectoryFindsTheSiblingBesideTheRealTarget()
    {
        using var harness = new Harness();
        var sibling = Install(harness, BuildInfo.Contract);
        var link = LinkIn(harness, "linkdir", harness.Context.ProcessPath);

        var (code, _, stderr) = harness.RunAs(link, "tui", "--once");

        Assert.Equal(0, code);
        Assert.Empty(stderr);
        Assert.Equal([sibling, "--once"], harness.Exec.Argv);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(link)!, "ahd-tui")));
    }

    [Fact]
    public void AChainOfLinksIsFollowedToTheFinalTarget()
    {
        using var harness = new Harness();
        var sibling = Install(harness, BuildInfo.Contract);
        var inner = LinkIn(harness, "inner", harness.Context.ProcessPath);
        var outer = LinkIn(harness, "outer", inner);

        var (code, _, _) = harness.RunAs(outer, "tui");

        Assert.Equal(0, code);
        Assert.Equal([sibling], harness.Exec.Argv);
    }

    [Fact]
    public void AnAhdTuiBesideTheLinkButNotBesideTheRealTargetIsNotUsed()
    {
        using var harness = new Harness();
        var link = LinkIn(harness, "linkdir", harness.Context.ProcessPath);
        var decoy = Path.Combine(Path.GetDirectoryName(link)!, "ahd-tui");
        File.WriteAllText(decoy, "#!/bin/sh\n");
        File.WriteAllText(decoy + ".contract", Sidecar(BuildInfo.Contract));

        var (code, _, stderr) = harness.RunAs(link, "tui");

        Assert.Equal(70, code);
        Assert.Contains("ahd-tui", stderr, StringComparison.Ordinal);
        Assert.False(harness.Exec.Called);
    }

    [Fact]
    public void WithNoSiblingItSaysSoAndExecsNothing()
    {
        using var harness = new Harness();

        var (code, stdout, stderr) = harness.Run("tui", "--once");

        Assert.Equal(70, code);
        Assert.Empty(stdout);
        Assert.Contains("ahd-tui", stderr, StringComparison.Ordinal);
        Assert.Contains("install.sh", stderr, StringComparison.Ordinal);
        Assert.False(harness.Exec.Called);
    }

    [Fact]
    public void ASiblingOfAnotherContractIsRefusedByNameWithTheInstallHint()
    {
        using var harness = new Harness();
        var other = (int.Parse(BuildInfo.Contract, System.Globalization.CultureInfo.InvariantCulture) + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        Install(harness, other);

        var (code, _, stderr) = harness.Run("tui", "--once");

        Assert.Equal(70, code);
        Assert.Contains($"ahd-tui is contract {other} and this ahd is contract {BuildInfo.Contract}", stderr, StringComparison.Ordinal);
        Assert.Contains("install.sh", stderr, StringComparison.Ordinal);
        Assert.False(harness.Exec.Called);
    }

    [Fact]
    public void ASiblingWithNoSidecarIsRefused()
    {
        using var harness = new Harness();
        Install(harness, null);

        var (code, _, stderr) = harness.Run("tui", "--once");

        Assert.Equal(70, code);
        Assert.Contains("no readable contract sidecar", stderr, StringComparison.Ordinal);
        Assert.False(harness.Exec.Called);
    }

    [Fact]
    public void ASidecarThatIsNotAnIntegerContractIsRefused()
    {
        using var harness = new Harness();
        var sibling = Install(harness, null);
        File.WriteAllText(sibling + ".contract", "{\"version\":\"0.0.0\",\"contract\":\"one\"}\n");

        var (code, _, stderr) = harness.Run("tui");

        Assert.Equal(70, code);
        Assert.Contains("no readable contract sidecar", stderr, StringComparison.Ordinal);
    }
}
