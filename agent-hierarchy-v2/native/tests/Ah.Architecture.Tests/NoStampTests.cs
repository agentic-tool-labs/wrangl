using System.Text.RegularExpressions;

namespace Ah.Architecture.Tests;

/// <summary>
/// ST-1: the native tree knows nothing of a writer stamp, a Node hand-off marker or the exit codes 69 and 77 that existed
/// for them. A cutover is one flag day, so no code or test names the other runtime's switch.
/// </summary>
public sealed class NoStampTests
{
    private static readonly Regex Forbidden = new(
        @"state-writer|writer_mismatch|StampRefusal|WriterStamp|AH_DISPATCHED|\bexit(?:s|ed)?\s+(?:69|77)\b|\breturn\s+(?:69|77)\s*;",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>The lines of <paramref name="text"/> that mention what must not appear, as "line: text".</summary>
    internal static List<string> Violations(string text)
    {
        var found = new List<string>();
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (Forbidden.IsMatch(lines[i]))
            {
                found.Add($"{i + 1}: {lines[i].Trim()}");
            }
        }

        return found;
    }

    [Fact(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    public void NoNativeSourceOrTestNamesTheWriterStampOrTheNodeHandOff()
    {
        var self = Path.GetFullPath(Path.Combine(Workspace.NativeRoot, "tests", "Ah.Architecture.Tests", "NoStampTests.cs"));
        var violations = new List<string>();
        foreach (var root in new[] { "src", "tests" })
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(Workspace.NativeRoot, root), "*.cs", SearchOption.AllDirectories))
            {
                var full = Path.GetFullPath(file);
                if (full == self || full.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (var line in Violations(File.ReadAllText(full)))
                {
                    violations.Add($"{Path.GetRelativePath(Workspace.NativeRoot, full)}:{line}");
                }
            }
        }

        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    [Theory(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    [InlineData("var stamp = Path.Combine(home, \"state-writer.json\");")]
    [InlineData("return Fail(\"writer_mismatch\", message);")]
    [InlineData("if (StampRefusal() is { } m) return m;")]
    [InlineData("env[\"AH_DISPATCHED\"] = \"native\";")]
    [InlineData("// the one-shot exits 77 for a refused write")]
    [InlineData("    return 69;")]
    public void TheScanCatchesEachForbiddenForm(string line) =>
        Assert.Single(Violations(line));

    [Theory(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    [InlineData("var wait = TimeSpan.FromSeconds(77);")]
    [InlineData("var count = 69;")]
    [InlineData("// the lock waits up to 5 s")]
    [InlineData("return 70;")]
    [InlineData("return 7;")]
    public void TheScanLeavesOrdinaryCodeAlone(string line) =>
        Assert.Empty(Violations(line));
}
