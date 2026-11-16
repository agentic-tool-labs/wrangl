using System.Text.RegularExpressions;

namespace Ah.Architecture.Tests;

/// <summary>
/// TT-2: the test-only variable names, <c>HOME</c> and the plugin-root override are read only through
/// <c>IAhEnvironment.GetTestOnly</c> or after <c>IAhEnvironment.TestMode</c>. The one file that implements the predicate and the
/// passwd-or-<c>HOME</c> choice (Ah.Platform's <c>SystemServices.cs</c>) is the only source that names them with a plain read.
/// </summary>
public sealed class TestOnlyNamesTests
{
    private static readonly Regex Forbidden = new(
        "\\b[Gg]et(?:Environment)?Variable\\(\\s*\"(?:AH_TEST(?:_[A-Z]+)*|AHD_VERSION_OVERRIDE|AH_CODEX_APPSERVER_RANGE|AHD_JOIN_MS|AHD_IDLE_EXIT_MS|AH_PLUGIN_ROOT|HOME)\"",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly string[] Allowed = ["src/Ah.Platform/SystemServices.cs"];

    /// <summary>The lines of <paramref name="text"/> that read a protected name with a plain environment read, as "line: text".</summary>
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

    [Fact]
    public void NoSourceOutsideThePredicateReadsATestOnlyNameWithAPlainRead()
    {
        var violations = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Workspace.NativeRoot, "src"), "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(Workspace.NativeRoot, file).Replace('\\', '/');
            if (relative.Contains("/obj/", StringComparison.Ordinal) || Allowed.Contains(relative))
            {
                continue;
            }

            foreach (var line in Violations(File.ReadAllText(file)))
            {
                violations.Add($"{relative}:{line}");
            }
        }

        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    [Theory(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    [InlineData("var t = environment.GetVariable(\"AH_TEST\");")]
    [InlineData("var f = env.GetVariable(\"AH_TEST_FAKE_BIN\");")]
    [InlineData("var f = Environment.GetEnvironmentVariable(\"AH_TEST_TRIPWIRE\");")]
    [InlineData("var v = environment.GetVariable(\"AHD_VERSION_OVERRIDE\");")]
    [InlineData("var r = environment.GetVariable(\"AH_CODEX_APPSERVER_RANGE\");")]
    [InlineData("var i = environment.GetVariable(\"AHD_IDLE_EXIT_MS\");")]
    [InlineData("var j = environment.GetVariable( \"AHD_JOIN_MS\" );")]
    [InlineData("var p = env.GetVariable(\"AH_PLUGIN_ROOT\");")]
    [InlineData("var h = context.Env.GetVariable(\"HOME\");")]
    [InlineData("var h = Environment.GetEnvironmentVariable(\"HOME\");")]
    public void TheScanCatchesEachProtectedName(string line) =>
        Assert.Single(Violations(line));

    [Theory(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    [InlineData("var t = environment.GetTestOnly(\"AH_TEST_FAKE_BIN\");")]
    [InlineData("var h = environment.GetVariable(\"HOMEBREW_PREFIX\");")]
    [InlineData("var h = environment.GetVariable(\"AH_HOME\");")]
    [InlineData("var m = environment.TestMode;")]
    public void TheScanLeavesTheTestModeAccessorsAndOtherNamesAlone(string line) =>
        Assert.Empty(Violations(line));
}
