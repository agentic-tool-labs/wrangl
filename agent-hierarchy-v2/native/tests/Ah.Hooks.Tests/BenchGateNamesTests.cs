using System.Runtime.CompilerServices;
using Ah.Hooks.Gates;

namespace Ah.Hooks.Tests;

/// <summary>The latency bench runs its gates against a real home, where a name the binary lacks adds a row to the hook-errors log.</summary>
public sealed class BenchGateNamesTests
{
    private static string[] BenchGates([CallerFilePath] string here = "")
    {
        var bench = Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "..", "bench", "ah-call-latency.sh");
        var line = File.ReadAllLines(bench).Single(static l => l.StartsWith("gates=\"", StringComparison.Ordinal));
        return line["gates=\"".Length..^1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    [Fact]
    public void EveryGateTheBenchTimesExists()
    {
        var known = GateUnits.All.Select(static u => u.Name).ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(BenchGates());
        Assert.All(BenchGates(), gate => Assert.Contains(gate, known));
    }

    [Theory]
    [InlineData("pretooluse-push-guard")]
    [InlineData("pretooluse-trust-gate")]
    public void NegativeARemovedGateIsNotAKnownUnit(string gate) =>
        Assert.DoesNotContain(GateUnits.All, u => u.Name == gate);
}
