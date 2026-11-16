namespace Ah.Architecture.Tests;

/// <summary>The grant of a run is resolved in one place; <c>whoami</c> and the decision verbs call it and read no asks log themselves.</summary>
public sealed class DecisionAuthorityPinTests
{
    private static string Src(params string[] parts) => Path.Combine([Workspace.NativeRoot, "src", .. parts]);

    private static IEnumerable<string> Sources(string project) =>
        Directory.EnumerateFiles(Src(project), "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    [Fact]
    public void OnlyTheHierarchyResolverJoinsTheAsksAndWhoamiAndTheDecisionVerbsCallIt()
    {
        var joiners = Sources("Ah.Hierarchy").Concat(Sources("Ah.Commands")).Concat(Sources("Ah.Cli"))
            .Where(p => File.ReadAllText(p).Contains("AsksOf(", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();
        var callers = Sources("Ah.Commands").Concat(Sources("Ah.Cli"))
            .Where(p => File.ReadAllText(p).Contains("HierarchyModule.ReadAuthority(", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToList();
        var effective = Sources("Ah.Commands").Concat(Sources("Ah.Cli"))
            .Where(p => File.ReadAllText(p).Contains("EffectiveAuthority(", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(["HierarchyModule.Authority.cs"], joiners);
        // A clean close does not read the authority: a release-clean close is silent under user and surrogate authority alike.
        // RunFactsReader passes the surrogate verdict to the mesh's run-scoped link grant.
        Assert.Equal(["MsgCommand.DecisionProof.cs", "RunFactsReader.cs", "WhoamiVerb.cs"], callers);
        Assert.Empty(effective);
    }

    [Fact]
    public void TheDecisionProofReadsNoAsksLogItself()
    {
        var text = File.ReadAllText(Src("Ah.Cli", "Verbs", "Msg", "MsgCommand.DecisionProof.cs"));

        Assert.DoesNotContain("AsksPath", text, StringComparison.Ordinal);
        Assert.DoesNotContain("asks.jsonl", text, StringComparison.Ordinal);
        Assert.DoesNotContain("RecordedAuthority", text, StringComparison.Ordinal);
    }
}
