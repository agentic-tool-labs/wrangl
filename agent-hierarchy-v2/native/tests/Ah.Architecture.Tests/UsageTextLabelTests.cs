using System.Reflection;
using System.Text.RegularExpressions;

namespace Ah.Architecture.Tests;

/// <summary>
/// The usage text a person reads names no internal design label: no section sign, no numbered spec, no internal project name. A usage
/// string is a string constant whose name contains <c>Usage</c> or <c>Synopsis</c>, or any string constant of a type whose name ends in
/// <c>Usage</c> or is <c>UsageText</c>.
/// </summary>
public sealed class UsageTextLabelTests
{
    private static readonly Regex Label = new(@"§|spec 00|ah-simplify", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    internal static bool IsUsageField(FieldInfo field) =>
        field.IsLiteral && field.FieldType == typeof(string)
        && (field.Name.Contains("Usage", StringComparison.Ordinal) || field.Name.Contains("Synopsis", StringComparison.Ordinal)
            || field.DeclaringType!.Name.EndsWith("Usage", StringComparison.Ordinal) || field.DeclaringType.Name == "UsageText");

    internal static List<string> Labels(string where, string text) =>
        [.. text.Split('\n').Where(line => Label.IsMatch(line)).Select(line => $"{where}: {line.Trim()}")];

    private static IEnumerable<Type> TypesOf(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            return e.Types.OfType<Type>();
        }
    }

    [Fact]
    public void NoUsageStringNamesASpecSectionOrAnInternalProjectName()
    {
        const BindingFlags all = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        var seen = 0;
        var found = new List<string>();
        foreach (var project in Layers.SourceProjects)
        {
            foreach (var field in TypesOf(Assembly.LoadFrom(Workspace.BuiltAssembly(project))).SelectMany(t => t.GetFields(all)).Where(IsUsageField))
            {
                seen++;
                found.AddRange(Labels($"{field.DeclaringType!.FullName}.{field.Name}", (string)field.GetRawConstantValue()!));
            }
        }

        // 38 usage strings when this was written; a much lower count means the scan stopped reaching them, not that they are clean
        Assert.True(seen >= 35, $"only {seen} usage strings were found");
        Assert.Empty(found);
    }

    [Fact]
    public void NegativeTheScanFlagsEachLabelAndLeavesPlainTextAndASpecWordAlone()
    {
        Assert.Single(Labels("x", "a refused edit (§1.3)"));
        Assert.Single(Labels("x", "(Spec 0044: the roster is a template)"));
        Assert.Single(Labels("x", "removed in ah-simplify"));
        Assert.Empty(Labels("x", "how long each stage took (spec, build, review)"));
        Assert.Empty(Labels("x", "config.toml.ah2-bak"));
    }

    [Fact]
    public void NegativeOnlyAUsageConstantIsScanned()
    {
        var fields = typeof(Samples).GetFields(BindingFlags.Static | BindingFlags.NonPublic);
        Assert.Equal(["Usage", "Synopsis"], fields.Where(IsUsageField).Select(f => f.Name));
    }

    internal static class Samples
    {
        internal const string Usage = "u";
        internal const string Synopsis = "s";
        internal const string Message = "m";
        internal static readonly string UsageField = "not a constant";
    }
}
