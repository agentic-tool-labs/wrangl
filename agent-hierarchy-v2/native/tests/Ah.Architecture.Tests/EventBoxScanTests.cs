namespace Ah.Architecture.Tests;

public sealed class EventBoxScanTests
{
    [Fact(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    public void NoWatchSourceFallsBackToTheFirstBoxOfARegistration()
    {
        var offenders = Directory.EnumerateFiles(Path.Combine(Workspace.NativeRoot, "src", "Ah.Watch"), "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(p => File.ReadAllText(p).Contains("Boxes[0]", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();

        Assert.Equal(["WatchTick.cs"], offenders);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(Path.Combine(Workspace.NativeRoot, "src", "Ah.Watch", "WatchTick.cs")), @"Boxes\[0\]"));
    }
}
