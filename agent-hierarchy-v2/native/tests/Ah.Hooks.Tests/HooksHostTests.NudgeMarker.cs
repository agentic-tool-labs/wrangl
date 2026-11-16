using System.Text.RegularExpressions;
using Ah.Hooks.Plumbing;

namespace Ah.Hooks.Tests;

public sealed partial class HooksHostTests
{
    [Fact]
    public void TheNudgeMarkerIsTheLiteralTheModPins()
    {
        Assert.Equal("[ah-nudge] ", HookEmit.NudgeMarker);
    }

    [Fact]
    public void NudgeIsCalledOnlyByTheSoftGate()
    {
        var gates = GatesDirectory();
        var found = Directory.GetFiles(gates, "*.cs")
            .Select(file => (Name: Path.GetFileName(file), Count: Regex.Count(File.ReadAllText(file), @"HookEmit\.Nudge\(")))
            .Where(x => x.Count > 0)
            .OrderBy(x => x.Name, StringComparer.Ordinal)
            .Select(x => $"{x.Name}:{x.Count}")
            .ToList();

        Assert.Equal(["SoftGate.cs:1"], found);
        Assert.DoesNotContain(Directory.GetFiles(gates, "*.cs"), f => File.ReadAllText(f).Contains("NudgeMarker", StringComparison.Ordinal));
    }

    private static string GatesDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Ah.Hooks", "Gates")))
        {
            dir = dir.Parent;
        }

        return dir is null ? throw new DirectoryNotFoundException("src/Ah.Hooks/Gates") : Path.Combine(dir.FullName, "src", "Ah.Hooks", "Gates");
    }
}
