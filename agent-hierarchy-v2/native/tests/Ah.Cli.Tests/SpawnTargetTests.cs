using Ah.Cli.Composition;

namespace Ah.Cli.Tests;

/// <summary>A plain build run as <c>dotnet ahd.dll</c> spawns its apphost as the daemon, never the dotnet host.</summary>
public sealed class SpawnTargetTests
{
    [Fact]
    public void TheDotnetHostSpawnsTheApphostBesideTheEntryAssembly() =>
        Assert.Equal("/b/ahd", DaemonComposition.SpawnTarget("/usr/local/share/dotnet/dotnet", "/b/ahd.dll", p => p == "/b/ahd"));

    [Fact]
    public void ANativeAhdSpawnsItself() =>
        Assert.Equal("/b/ahd", DaemonComposition.SpawnTarget("/b/ahd", "/b/ahd", _ => true));

    [Fact]
    public void TheDotnetHostWithNoApphostIsLeftAsItIs() =>
        Assert.Equal("/d/dotnet", DaemonComposition.SpawnTarget("/d/dotnet", "/b/ahd.dll", _ => false));
}
