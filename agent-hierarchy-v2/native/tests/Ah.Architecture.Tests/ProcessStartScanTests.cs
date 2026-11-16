using System.Text.RegularExpressions;

namespace Ah.Architecture.Tests;

/// <summary>
/// A process start time that is stored or compared is printed under <c>TZ=UTC</c>, so a change of the machine's time zone does
/// not make a live process look like a reused pid. Every source that asks <c>ps</c> for <c>lstart=</c> is listed here with
/// the reason it is allowed to.
/// </summary>
public sealed class ProcessStartScanTests
{
    private static readonly Dictionary<string, string> Allowed = new(StringComparer.Ordinal)
    {
        ["src/Ah.Platform/Exec/ProcessTable.cs"] = "PsUtcEnvironment",
        ["src/Ah.Launch/Snapshot/SnapshotBuilder.cs"] = "LC_ALL",
    };

    [Fact]
    public void EverySourceThatAsksPsForAStartTimeIsListedAndPrintsUtcWhereItStoresOrCompares()
    {
        var problems = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Workspace.NativeRoot, "src"), "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(Workspace.NativeRoot, file).Replace('\\', '/');
            if (relative.Contains("/obj/", StringComparison.Ordinal))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            if (!text.Contains("\"lstart=\"", StringComparison.Ordinal))
            {
                continue;
            }

            if (!Allowed.TryGetValue(relative, out var marker))
            {
                problems.Add($"{relative} asks ps for lstart= and is not listed");
            }
            else if (!text.Contains(marker, StringComparison.Ordinal))
            {
                problems.Add($"{relative} no longer carries {marker}");
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    /// <summary>
    /// A start time is compared through <c>ProcessStart.Compare</c>, which accepts both the UTC text and the legacy local text.
    /// A direct <c>==</c> or <c>!=</c> against <c>StartTime(...)</c> or <c>ProcessStart(...)</c>, or against a name just bound
    /// from one, treats every legacy record as a reused pid.
    /// </summary>
    [Fact]
    public void NoSourceComparesAStartTimeWithAnOperatorOutsideTheComparator()
    {
        var direct = new Regex(
            @"(?:(?:StartTime|ProcessStart)\([^)]*\)[ \t]*(?:==|!=))"
            + @"|(?:(?:==|!=)[ \t]*[A-Za-z0-9_.]*(?:StartTime|ProcessStart)\()"
            + @"|(?:(?:StartTime|ProcessStart)\([^)]*\) is \{ \} ([A-Za-z0-9_]+).*\b\1[ \t]*(?:==|!=))",
            RegexOptions.CultureInvariant);
        var problems = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Workspace.NativeRoot, "src"), "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(Workspace.NativeRoot, file).Replace('\\', '/');
            if (relative.Contains("/obj/", StringComparison.Ordinal) || relative.EndsWith("/ProcessStart.cs", StringComparison.Ordinal))
            {
                continue;
            }

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (direct.IsMatch(lines[i]))
                {
                    problems.Add($"{relative}:{i + 1} compares a start time with an operator: {lines[i].Trim()}");
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }
}
