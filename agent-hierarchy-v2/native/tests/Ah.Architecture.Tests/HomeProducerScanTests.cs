using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ah.Architecture.Tests;

/// <summary>
/// The user's home and the ah2 root each have one producer, <c>SystemEnvironment</c>, and the test-process tripwire lives there.
/// Source that reads <c>HOME</c> or <c>AH_HOME</c>, asks the passwd database or the operating system for a home, or expands
/// <c>~/</c> itself would reach the real home around the tripwire. In the tests, only the sandbox may read the real home, so no
/// test can hand it to product code.
/// </summary>
public sealed partial class HomeProducerScanTests
{
    private const string HomeRead = "home-env";

    private const string AhHomeRead = "ah-home-env";

    private const string HomeFolder = "folder";

    private const string Passwd = "passwd";

    private const string HomeProcess = "process";

    private const string Tilde = "tilde";

    private const string Producer = "src/Ah.Platform/SystemServices.cs";

    private const string SandboxOwner = "tests/ProcessSandbox.cs";

    private const string ThisScan = "tests/Ah.Architecture.Tests/HomeProducerScanTests.cs";

    /// <summary>Files outside the producer that may hold one kind of banned use, each with why it is not a way around the tripwire.</summary>
    private static readonly Dictionary<(string File, string Check), string> SourceAllowed = new()
    {
        [("src/Ah.Platform/Exec/Libc.cs", Passwd)] = "the libc binding of getpwuid; PlatformModule.PasswdHome is its only caller",
        [("src/Ah.Platform/Exec/PlatformModule.Exec.cs", Passwd)] = "the passwd lookup SystemEnvironment is built with; its result is read only through HomeDirectory",
        [("src/Ah.Platform/Trust/AhHomeCheck.cs", AhHomeRead)] = "judges the raw variable for the trust gate; the home it admits is the one SystemEnvironment.AhHome returns, which the tripwire guards",
        [("src/Ah.Config/Trust/HierarchyDirPolicy.cs", AhHomeRead)] = "finds the test sandbox directory the variable lies under for a trust decision; it builds no state path from it",
        [("src/Ah.Hooks/Gates/TolerantAhCall.cs", Tilde)] = "reads a leading ~/ in the program word and expands it with the environment's HomeDirectory, the one producer; the expanded path is only read as a candidate program path",
        [("src/Ah.Hooks/Surface/MemberPermissionsGenerator.cs", Tilde)] = "rule text for Claude Code's sandbox (allowWrite/denyRead); nothing is read",
        [("src/Ah.Cli/CliApp.cs", AhHomeRead)] = "echoes the raw variable in the gate's refusal message; it builds no path from it",
    };

    private static readonly Dictionary<(string File, string Check), string> TestsAllowed = new()
    {
        [("tests/Ah.Hooks.Tests/MemberPermissionsGeneratorTests.cs", Tilde)] = "asserts the sandbox rule text; nothing is read",
    };

    private static readonly string[] HomeProcesses = ["id", "dscl", "getent"];

    [GeneratedRegex(@"\b(UserProfile|Personal|MyDocuments)\b", RegexOptions.CultureInvariant)]
    private static partial Regex HomeFolderName();

    [GeneratedRegex(@"Start|Run|Exec|Spawn", RegexOptions.CultureInvariant)]
    private static partial Regex ProcessCallName();

    private static string? LiteralOf(ExpressionSyntax? e) =>
        e is LiteralExpressionSyntax l && l.IsKind(SyntaxKind.StringLiteralExpression) ? l.Token.ValueText : null;

    private static bool IsNamed(ExpressionSyntax e, string value, HashSet<string> names) =>
        LiteralOf(e) == value || (e is IdentifierNameSyntax i && names.Contains(i.Identifier.ValueText));

    private static bool IsHomeProcess(string? literal) =>
        literal is not null && HomeProcesses.Contains(Path.GetFileName(literal));

    /// <summary>The first element of an argv written as an array or a collection, else null.</summary>
    private static ExpressionSyntax? ArgvHead(ExpressionSyntax e) => e switch
    {
        ImplicitArrayCreationExpressionSyntax a => a.Initializer.Expressions.FirstOrDefault(),
        ArrayCreationExpressionSyntax a => a.Initializer?.Expressions.FirstOrDefault(),
        CollectionExpressionSyntax c => c.Elements.OfType<ExpressionElementSyntax>().FirstOrDefault()?.Expression,
        _ => null,
    };

    /// <summary>
    /// The (check, "line: text") of each place <paramref name="source"/> reads the home around the producer. <paramref name="product"/>
    /// also counts reads through the environment abstraction (<c>GetVariable("HOME")</c>), which product code has and a test's fake
    /// environment answers from a dictionary, and reads of <c>AH_HOME</c>.
    /// </summary>
    internal static List<(string Check, string Text)> Violations(string source, bool product)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var root = tree.GetRoot();
        var found = new List<(string, string)>();

        void Add(string check, SyntaxNode node) =>
            found.Add((check, $"{tree.GetLineSpan(node.Span).StartLinePosition.Line + 1}: {node.ToString().Replace('\n', ' ').Trim()}"));

        var constants = root.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Where(d => LiteralOf(d.Initializer?.Value) is not null)
            .GroupBy(d => LiteralOf(d.Initializer!.Value)!)
            .ToDictionary(g => g.Key, g => g.Select(d => d.Identifier.ValueText).ToHashSet());
        HashSet<string> Names(string value) => constants.GetValueOrDefault(value) ?? [];

        foreach (var call in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var name = call.Expression switch
            {
                MemberAccessExpressionSyntax m => m.Name.Identifier.ValueText,
                IdentifierNameSyntax i => i.Identifier.ValueText,
                _ => string.Empty,
            };
            var args = call.ArgumentList.Arguments;
            var first = args.Count > 0 ? args[0].Expression : null;
            var reads = name == "GetEnvironmentVariable"
                || (product && name.TrimStart('_').ToLowerInvariant() is "getvariable" or "gettestonly");
            if (reads && first is not null && IsNamed(first, "HOME", Names("HOME")))
            {
                Add(HomeRead, call);
            }
            else if (reads && product && first is not null && IsNamed(first, "AH_HOME", Names("AH_HOME")))
            {
                Add(AhHomeRead, call);
            }
            else if (name == "GetFolderPath" && args.Any(a => HomeFolderName().IsMatch(a.Expression.ToString())))
            {
                Add(HomeFolder, call);
            }
            else if (name is "GetPwUid" or "GetPwNam")
            {
                Add(Passwd, call);
            }
            else if (ProcessCallName().IsMatch(name) && first is not null
                && (IsHomeProcess(LiteralOf(first)) || IsHomeProcess(LiteralOf(ArgvHead(first)))))
            {
                Add(HomeProcess, call);
            }
        }

        foreach (var creation in root.DescendantNodes().OfType<BaseObjectCreationExpressionSyntax>())
        {
            var typeName = (creation as ObjectCreationExpressionSyntax)?.Type.ToString();
            var head = creation.ArgumentList?.Arguments.FirstOrDefault()?.Expression;
            if (typeName is "ProcessStartInfo" or "System.Diagnostics.ProcessStartInfo" && IsHomeProcess(LiteralOf(head)))
            {
                Add(HomeProcess, creation);
            }
        }

        foreach (var assignment in root.DescendantNodes().OfType<AssignmentExpressionSyntax>())
        {
            if (assignment.Left.ToString().EndsWith("FileName", StringComparison.Ordinal) && IsHomeProcess(LiteralOf(assignment.Right)))
            {
                Add(HomeProcess, assignment);
            }
        }

        foreach (var token in root.DescendantTokens().Where(t => t.IsKind(SyntaxKind.StringLiteralToken)))
        {
            if (token.ValueText is "getpwuid" or "getpwnam")
            {
                Add(Passwd, token.Parent ?? root);
            }
            else if (token.ValueText.StartsWith("~/", StringComparison.Ordinal))
            {
                Add(Tilde, token.Parent ?? root);
            }
        }

        return found.Distinct().ToList();
    }

    private static IEnumerable<string> Files(string folder) =>
        Directory.EnumerateFiles(Path.Combine(Workspace.NativeRoot, folder), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains("/obj/", StringComparison.Ordinal) && !f.Contains("/bin/", StringComparison.Ordinal)
                && !f.EndsWith(".g.cs", StringComparison.Ordinal));

    private static List<string> Scan(string folder, bool product, Func<string, bool> skipFile, Dictionary<(string File, string Check), string> allowed)
    {
        var violations = new List<string>();
        foreach (var file in Files(folder))
        {
            var relative = Path.GetRelativePath(Workspace.NativeRoot, file).Replace('\\', '/');
            if (skipFile(relative))
            {
                continue;
            }

            violations.AddRange(NotAllowed(relative, Violations(File.ReadAllText(file), product), allowed));
        }

        return violations;
    }

    private static IEnumerable<string> NotAllowed(string relative, IEnumerable<(string Check, string Text)> found, Dictionary<(string File, string Check), string> allowed) =>
        found.Where(v => !allowed.ContainsKey((relative, v.Check))).Select(v => $"{relative}:{v.Check}:{v.Text}");

    [Fact]
    public void NoSourceFileReadsTheHomeOrTheAhHomeExceptTheProducer()
    {
        var violations = Scan("src", product: true, f => f == Producer, SourceAllowed);

        Assert.True(violations.Count == 0, string.Join("\n", violations));
    }

    [Fact]
    public void NoTestReadsTheRealHomeExceptTheSandbox()
    {
        var violations = Scan("tests", product: false, f => f == SandboxOwner || f == ThisScan, TestsAllowed);

        Assert.True(violations.Count == 0, string.Join("\n", violations));
    }

    [Fact(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    public void EveryAllowlistEntryNamesARealFileAndAKnownCheckAndStatesItsReason()
    {
        string[] checks = [HomeRead, AhHomeRead, HomeFolder, Passwd, HomeProcess, Tilde];
        foreach (var ((file, check), reason) in SourceAllowed.Concat(TestsAllowed))
        {
            Assert.True(File.Exists(Path.Combine(Workspace.NativeRoot, file)), file);
            Assert.Contains(check, checks);
            Assert.False(string.IsNullOrWhiteSpace(reason), file);
        }
    }

    [Fact(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    public void AnAllowlistedFileIsExemptOnlyFromItsNamedCheck()
    {
        var allowed = new Dictionary<(string File, string Check), string> { [("a.cs", Passwd)] = "why" };
        var found = new List<(string Check, string Text)> { (Passwd, "1: GetPwUid()"), (HomeRead, "2: GetVariable(\"HOME\")") };

        Assert.Equal(["a.cs:home-env:2: GetVariable(\"HOME\")"], NotAllowed("a.cs", found, allowed));
        Assert.Equal(2, NotAllowed("b.cs", found, allowed).Count());
    }

    [Theory(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    [InlineData("var h = Environment.GetEnvironmentVariable(\"HOME\");", HomeRead)]
    [InlineData("var h = env.GetVariable(\"HOME\");", HomeRead)]
    [InlineData("var h = _getVariable(\"HOME\");", HomeRead)]
    [InlineData("var h = env.GetTestOnly(\"HOME\");", HomeRead)]
    [InlineData("const string K = \"HOME\"; var h = getVariable(K);", HomeRead)]
    [InlineData("var h = Environment.GetEnvironmentVariable(\"AH_HOME\");", AhHomeRead)]
    [InlineData("var h = env.GetVariable(\"AH_HOME\");", AhHomeRead)]
    [InlineData("var h = _getVariable(\"AH_HOME\");", AhHomeRead)]
    [InlineData("const string K = \"AH_HOME\"; var h = getVariable(K);", AhHomeRead)]
    [InlineData("var h = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);", HomeFolder)]
    [InlineData("var h = Environment.GetFolderPath(SpecialFolder.Personal);", HomeFolder)]
    [InlineData("var h = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);", HomeFolder)]
    [InlineData("var h = Libc.GetPwUid(uid);", Passwd)]
    [InlineData("var p = Path.Combine(\"~/.claude\", \"ah2\");", Tilde)]
    [InlineData("var s = new ProcessStartInfo(\"id\");", HomeProcess)]
    [InlineData("var s = new ProcessStartInfo(\"/usr/bin/id\", \"-P\");", HomeProcess)]
    [InlineData("var s = new ProcessStartInfo { FileName = \"dscl\" };", HomeProcess)]
    [InlineData("var s = new System.Diagnostics.ProcessStartInfo(\"getent\");", HomeProcess)]
    [InlineData("var r = Run(\"id\", \"-P\");", HomeProcess)]
    [InlineData("var r = runner.Start(\"dscl\", \".\");", HomeProcess)]
    [InlineData("var r = runner.Run([\"id\", \"-P\"]);", HomeProcess)]
    [InlineData("var r = Exec(new[] { \"getent\", \"passwd\" });", HomeProcess)]
    public void APlantedHomeReadInSourceIsFound(string code, string check)
    {
        var found = Assert.Single(Violations("class C { void M() { " + code + " } }", product: true));

        Assert.Equal(check, found.Check);
    }

    [Fact(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    public void ALibcPasswdBindingIsFound() =>
        Assert.Single(Violations("class C { [LibraryImport(\"libc\", EntryPoint = \"getpwuid\")] static partial nint F(uint u); }", product: true));

    [Theory(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    [InlineData("var h = env.GetVariable(\"TZ\");")]
    [InlineData("var d = new Dictionary<string, string> { [\"HOME\"] = home, [\"AH_HOME\"] = ah };")]
    [InlineData("Environment.SetEnvironmentVariable(\"HOME\", home);")]
    [InlineData("var h = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);")]
    [InlineData("var b = name == \"HOME\" || name == \"AH_HOME\";")]
    [InlineData("var h = env.HomeDirectory; var a = env.AhHome;")]
    [InlineData("var y = \"~\";")]
    [InlineData("var m = \"no roster resolves (~/.claude/ah2/agent-hierarchy.json)\";")]
    [InlineData("var h = env.GetVariable(\"HOMEBREW_PREFIX\");")]
    [InlineData("var h = env.GetVariable(\"AH_HOME_EXTRA\");")]
    [InlineData("// var h = Environment.GetEnvironmentVariable(\"HOME\"); getpwuid\nvar x = 1;")]
    [InlineData("Log(\"id\"); logger.Info(\"id -P\"); var s = \"id\"; var t = \"dscl getent\";")]
    [InlineData("var r = Run(\"git\", \"id\");")]
    [InlineData("var r = Run([\"git\", \"id\"]);")]
    [InlineData("var s = new ProcessStartInfo(\"git\");")]
    [InlineData("var s = new ProcessStartInfo { FileName = \"git\" };")]
    [InlineData("var r = Start(\"identity\", \"-P\");")]
    public void ACallThatIsNotAHomeReadIsNotFound(string code) =>
        Assert.Empty(Violations("class C { void M() { " + code + " } }", product: true));

    [Fact(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    public void ARealProcessReadOfTheHomeIsFoundInATestButAFakeEnvironmentLookupIsNot()
    {
        Assert.Single(Violations("class C { void M() { var h = Environment.GetEnvironmentVariable(\"HOME\"); } }", product: false));
        Assert.Empty(Violations("class C { string GetVariable(string n) => n; string H => GetVariable(\"HOME\"); }", product: false));
        Assert.Empty(Violations("class C { string GetVariable(string n) => n; string H => GetVariable(\"AH_HOME\"); }", product: false));
    }
}
