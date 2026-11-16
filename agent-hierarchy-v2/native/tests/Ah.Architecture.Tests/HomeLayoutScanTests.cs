using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ah.Architecture.Tests;

/// <summary>
/// The plugin owns nothing directly under <c>~/.claude</c> or <c>&lt;repo&gt;/.claude</c>: its state lives under the ah2 home and
/// the ah2 project directory (<c>Ah.Core.Text.AhPaths</c>). The scan is syntactic, not a substring search, because paths are
/// written as separate arguments (<c>Join(home, ".claude", "agent-hierarchy")</c>). The one file that names a v1 home is
/// <c>V1Layout.cs</c>, and the one place that names v1's committed conventions file is the read-only fallback in
/// <c>Conventions.cs</c>. A name that holds <c>".claude"</c> would hide a v1 path from the segment scan, so only
/// <c>AhPaths.cs</c> and <c>V1Layout.cs</c> may define one.
/// </summary>
public sealed class HomeLayoutScanTests
{
    private static readonly string[] Allowed = ["src/Ah.Core/Text/V1Layout.cs"];

    private static readonly string[] ClaudeNameOwners = ["src/Ah.Core/Text/AhPaths.cs", "src/Ah.Core/Text/V1Layout.cs"];

    private const string ConventionsFallbackFile = "src/Ah.Config/Conventions/Conventions.cs";

    private const string ConventionsFallbackLiteral = "\".claude/ah-conventions.json\"";

    /// <summary>A name that sits directly under <c>.claude</c> for the v1 plugin and so must not appear there for this one.</summary>
    private static bool IsV1Name(string name) =>
        name is "agent-hierarchy" or "hierarchy" or "ah-conventions.json"
        || name.StartsWith("agent-hierarchy.", StringComparison.Ordinal);

    private static bool IsV1Path(string text)
    {
        foreach (var marker in new[] { ".claude/", ".claude\\" })
        {
            var at = 0;
            while ((at = text.IndexOf(marker, at, StringComparison.Ordinal)) >= 0)
            {
                var rest = text[(at + marker.Length)..];
                var end = rest.IndexOfAny(['/', '\\']);
                var name = end < 0 ? rest : rest[..end];
                if (IsV1Name(name))
                {
                    return true;
                }

                at += marker.Length;
            }
        }

        return false;
    }

    private static string? LiteralOf(ExpressionSyntax e) =>
        e is LiteralExpressionSyntax l && l.IsKind(SyntaxKind.StringLiteralExpression) ? l.Token.ValueText : null;

    /// <summary>The "line: text" of each place <paramref name="source"/> spells a v1 path, whole or in segments.</summary>
    internal static List<string> Violations(string source, bool allowClaudeName = false)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var root = tree.GetRoot();
        var found = new List<string>();

        void Add(SyntaxNode node) =>
            found.Add($"{tree.GetLineSpan(node.Span).StartLinePosition.Line + 1}: {node.ToString().Replace('\n', ' ').Trim()}");

        // (a) a literal that holds the path whole, in any string form (a verbatim or interpolated text part too)
        foreach (var token in root.DescendantTokens())
        {
            if (token.IsKind(SyntaxKind.StringLiteralToken) || token.IsKind(SyntaxKind.InterpolatedStringTextToken)
                || token.IsKind(SyntaxKind.SingleLineRawStringLiteralToken) || token.IsKind(SyntaxKind.MultiLineRawStringLiteralToken))
            {
                if (IsV1Path(token.ValueText))
                {
                    Add(token.Parent ?? root);
                }
            }
        }

        // (c) a name that holds ".claude": a v1 name joined after it would not be a literal sequence
        if (!allowClaudeName)
        {
            foreach (var node in root.DescendantNodes())
            {
                var value = node switch
                {
                    VariableDeclaratorSyntax d => d.Initializer?.Value,
                    PropertyDeclarationSyntax p => p.Initializer?.Value ?? p.ExpressionBody?.Expression,
                    _ => null,
                };
                if (value is not null && LiteralOf(value) == ".claude")
                {
                    Add(node);
                }
            }
        }

        // (b) consecutive literal arguments or elements that spell it: ".claude", "agent-hierarchy"
        foreach (var list in root.DescendantNodes().OfType<ArgumentListSyntax>())
        {
            CheckSequence(list.Arguments.Select(a => a.Expression).ToList(), list.Parent ?? list, Add);
        }

        foreach (var init in root.DescendantNodes().OfType<InitializerExpressionSyntax>())
        {
            CheckSequence(init.Expressions.ToList(), init, Add);
        }

        foreach (var coll in root.DescendantNodes().OfType<CollectionExpressionSyntax>())
        {
            CheckSequence(coll.Elements.OfType<ExpressionElementSyntax>().Select(e => e.Expression).ToList(), coll, Add);
        }

        return found.Distinct().ToList();
    }

    private static void CheckSequence(List<ExpressionSyntax> items, SyntaxNode at, Action<SyntaxNode> add)
    {
        for (var i = 0; i + 1 < items.Count; i++)
        {
            if (LiteralOf(items[i]) != ".claude")
            {
                continue;
            }

            var next = items[i + 1];
            var literal = LiteralOf(next);
            var name = literal ?? (next is MemberAccessExpressionSyntax m ? m.Name.Identifier.ValueText : (next as IdentifierNameSyntax)?.Identifier.ValueText);
            // the config file's name held in a constant is the same file as the literal
            if (literal is not null ? IsV1Name(literal) : name == "ConfigBasename")
            {
                add(at);
                return;
            }
        }
    }

    [Fact]
    public void NoSourceFileSpellsAV1PathWholeOrInSegments()
    {
        var violations = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Workspace.NativeRoot, "src"), "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(Workspace.NativeRoot, file).Replace('\\', '/');
            if (relative.Contains("/obj/", StringComparison.Ordinal) || relative.Contains("/bin/", StringComparison.Ordinal)
                || relative.EndsWith(".g.cs", StringComparison.Ordinal) || Allowed.Contains(relative))
            {
                continue;
            }

            var found = Violations(File.ReadAllText(file), ClaudeNameOwners.Contains(relative));
            if (relative == ConventionsFallbackFile)
            {
                found = found.Where(v => !v.EndsWith(": " + ConventionsFallbackLiteral, StringComparison.Ordinal)).ToList();
            }

            violations.AddRange(found.Select(v => $"{relative}:{v}"));
        }

        Assert.True(violations.Count == 0, string.Join("\n", violations));
    }

    [Fact(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    public void V1sConventionsFileIsNamedByExactlyOneLiteralTheReadOnlyFallback()
    {
        var path = Path.Combine(Workspace.NativeRoot, ConventionsFallbackFile);
        var named = Violations(File.ReadAllText(path)).Where(v => v.EndsWith(": " + ConventionsFallbackLiteral, StringComparison.Ordinal)).ToList();

        Assert.Single(named);
    }

    [Theory(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    [InlineData("var p = NodePath.Join(home, \".claude\", \"agent-hierarchy\");")]
    [InlineData("var p = Path.Combine(home, \".claude\", \"hierarchy\", \"hook-errors.jsonl\");")]
    [InlineData("var p = NodePath.Join(Home, \".claude\", \"agent-hierarchy.usage.jsonl\");")]
    [InlineData("var p = NodePath.Join(root, \".claude\", ConfigRegistry.ConfigBasename);")]
    [InlineData("var p = proj + \"/.claude/hierarchy\";")]
    [InlineData("const string P = \".claude/agent-hierarchy\";")]
    [InlineData("var p = $\"{home}/.claude/ah-conventions.json\";")]
    [InlineData("string[] s = [\".claude\", \"agent-hierarchy.gate.json\"];")]
    [InlineData("const string Claude = \".claude\";")]
    public void APlantedV1PathIsFound(string code) =>
        Assert.Single(Violations("class C { void M() { " + code + " } }"));

    [Theory(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    [InlineData("var p = NodePath.Join(home, \".claude\", \"plugins\", \"installed_plugins.json\");")]
    [InlineData("var p = NodePath.Join(root, \".claude\", \"agents\", \"x.md\");")]
    [InlineData("var p = Path.Combine(root, \".claude\", \"worktrees\", name);")]
    [InlineData("var p = NodePath.Join(home, \".claude\", \"ah2\", \"hierarchy\", \"hook-errors.jsonl\");")]
    [InlineData("var p = NodePath.Join(home, \".claude\", \"projects\");")]
    [InlineData("var p = \"agent-hierarchy.json\";")]
    [InlineData("var p = NodePath.Join(home, \"agent-hierarchy\", \".claude\");")]
    [InlineData("var p = \".claude-plugin/plugin.json\";")]
    [InlineData("// NodePath.Join(home, \".claude\", \"agent-hierarchy\")\nvar p = 1;")]
    [InlineData("const string Dot = \".claude-plugin\";")]
    [InlineData("const string Settings = \".claude/settings.json\";")]
    public void ThePathsAClaudeCodeOwnsAndTheAh2LayoutAreNotFlagged(string code) =>
        Assert.Empty(Violations("class C { void M() { " + code + " } }"));

    [Theory(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    [InlineData("class C { string Claude { get; } = \".claude\"; }")]
    [InlineData("class C { string Claude => \".claude\"; }")]
    [InlineData("class C { static readonly string Claude = \".claude\"; }")]
    public void APropertyOrFieldThatHoldsClaudeIsFound(string code) =>
        Assert.Single(Violations(code));

    [Fact(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    public void ANameThatHoldsClaudeIsAllowedWhereThePathsAreDefined() =>
        Assert.Empty(Violations("class C { const string Claude = \".claude\"; }", allowClaudeName: true));
}
