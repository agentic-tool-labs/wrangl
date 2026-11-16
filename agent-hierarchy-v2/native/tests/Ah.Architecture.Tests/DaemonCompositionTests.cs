namespace Ah.Architecture.Tests;

/// <summary>
/// The daemon's watch host is composed with the launch layer's herdr reads and the delivery ladder. The host without them
/// (<c>WatchHostSeams.Unavailable</c>, whose ladder never runs) is for the inspection verbs only; naming it in the daemon's
/// composition would silently stop every unacked entry at rung 2.
/// </summary>
public sealed class DaemonCompositionTests
{
    private const string Unavailable = "WatchHostSeams.Unavailable";

    internal static bool NamesTheHostWithoutALadder(string source) => source.Contains(Unavailable, StringComparison.Ordinal);

    [Fact]
    public void TheDaemonsCompositionDoesNotUseTheSeamsWhoseLadderNeverRuns()
    {
        var source = File.ReadAllText(Path.Combine(Workspace.NativeRoot, "src", "Ah.Cli", "Composition", "DaemonServices.cs"));

        Assert.False(NamesTheHostWithoutALadder(source), "DaemonServices composes the watch host with WatchHostSeams.Unavailable");
        Assert.Contains("WatchHostSeams.FromLaunch(", source, StringComparison.Ordinal);
        Assert.Contains("new MeshLadderSeam(", source, StringComparison.Ordinal);
    }

    [Theory(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    [InlineData("internal static WatchHostSeams Seams => WatchHostSeams.Unavailable;", true)]
    [InlineData("var host = new WatchHostAdapter(g, screens, clock, WatchHostSeams.Unavailable, log);", true)]
    [InlineData("return WatchHostSeams.FromLaunch(runner, env, files, appender, config, screens, pid, report, new MeshLadderSeam(ladder));", false)]
    [InlineData("// the host with no ladder is built by the Unavailable seams", false)]
    public void TheCheckSeesTheStubAndOnlyTheStub(string line, bool expected) =>
        Assert.Equal(expected, NamesTheHostWithoutALadder(line));
}
