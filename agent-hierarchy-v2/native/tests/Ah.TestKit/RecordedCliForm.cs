using System.Text.RegularExpressions;
using Ah.Core.Json;

namespace Ah.TestKit;

/// <summary>
/// Maps the native command spellings (<c>&lt;root&gt;/bin/ah roster|msg|gate</c>, <c>&lt;root&gt;/bin/ahd</c>, <c>ah roster …</c>) back
/// to the Node spellings the unit tables recorded, so a table compares on everything except the ruled command form. The rules are
/// not copied: this reads the <c>cli-form-*</c> rules of <c>tests/contracts/normalize.json</c> — the same ones the Node-side lanes
/// apply — in file order, and the patterns are written so that .NET and JavaScript read them alike (ASCII classes and lookarounds,
/// no group that may not participate).
/// </summary>
public static class RecordedCliForm
{
    private const string Prefix = "cli-form-";

    private static readonly Lazy<IReadOnlyList<(string Name, Regex Pattern, string Replace)>> Rules = new(Load);

    /// <summary>The names of the family's rules, in the order they apply.</summary>
    public static IReadOnlyList<string> RuleNames => Rules.Value.Select(r => r.Name).ToList();

    /// <summary>The text with every native command spelling in it written as the Node recordings write it.</summary>
    public static string Map(string text)
    {
        if (!text.Contains("ah", StringComparison.Ordinal))
        {
            return text;
        }

        var s = text;
        foreach (var rule in Rules.Value)
        {
            s = rule.Pattern.Replace(s, rule.Replace);
        }

        return s;
    }

    /// <summary>The rules file of the checkout this assembly was built in.</summary>
    public static string RulesFile()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Ah.sln")))
        {
            dir = dir.Parent;
        }

        return dir is null
            ? throw new FileNotFoundException("no Ah.sln above " + AppContext.BaseDirectory)
            : Path.GetFullPath(Path.Combine(dir.FullName, "..", "tests", "contracts", "normalize.json"));
    }

    private static List<(string Name, Regex Pattern, string Replace)> Load()
    {
        var doc = JsonText.Parse(File.ReadAllBytes(RulesFile())).AsObject();
        var rules = new List<(string, Regex, string)>();
        foreach (var node in doc["rules"].AsArray())
        {
            var rule = node.AsObject();
            var name = rule["name"].AsString();
            if (!name.StartsWith(Prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var flags = rule.Get("flags") is { IsText: true } f ? f.AsString() : "g";
            var options = RegexOptions.CultureInvariant | (flags.Contains('m', StringComparison.Ordinal) ? RegexOptions.Multiline : RegexOptions.None);
            rules.Add((name, new Regex(rule["pattern"].AsString(), options), rule["replace"].AsString()));
        }

        return rules;
    }
}
