using System.Reflection;
using Ah.Core.Json;
using Ah.TestKit;

namespace Ah.Roster.Tests;

/// <summary>
/// Runs the unit tables of the roster library against the native port. The same tables run on Node through
/// <c>tests/contract-tools/units-run.mjs</c>; a case passes only when its recorded Node result is reproduced.
/// </summary>
public sealed class RosterUnitTableTests
{
    private static readonly string NativeRoot = typeof(RosterUnitTableTests).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "AhNativeRoot").Value!;

    private static readonly string UnitsRoot = Path.Combine(NativeRoot, "..", "tests", "contracts", "units");

    /// <summary>Every case of every table this assembly dispatches, as the table's path under the units directory and the case id.</summary>
    private static List<(string File, string Id)> OwnedCases()
    {
        var cases = new List<(string, string)>();
        foreach (var file in UnitTable.TableFiles(NativeRoot))
        {
            var table = UnitTable.Load(file);
            if (!RosterTableFns.OwnsTable(table.Lib, table.Fn))
            {
                continue;
            }

            foreach (var c in table.Cases)
            {
                cases.Add((Path.GetRelativePath(UnitsRoot, file), c.Id));
            }
        }

        return cases;
    }

    public static TheoryData<string, string> Cases()
    {
        var data = new TheoryData<string, string>();
        foreach (var (file, id) in OwnedCases())
        {
            data.Add(file, id);
        }

        return data;
    }

    [Fact]
    public void EveryDispatchedFunctionHasATable()
    {
        var present = UnitTable.TableFiles(NativeRoot)
            .Select(f => UnitTable.Load(f))
            .Where(t => RosterTableFns.Libs.Contains(t.Lib))
            .Select(t => RosterTableFns.KeyOf(t.Lib, t.Fn))
            .ToHashSet();
        Assert.All(RosterTableFns.All, f => Assert.Contains(f, present));
    }

    [Fact]
    public void ATableOfAnotherAssemblyIsNotRunEvenWhenItsFunctionNameRepeatsOurs()
    {
        var files = OwnedCases().Select(c => c.File).Distinct().ToList();

        Assert.DoesNotContain(files, f => f.StartsWith("lib-peer", StringComparison.Ordinal));
        Assert.DoesNotContain(files, f => f.StartsWith("lib-decisions", StringComparison.Ordinal));
        Assert.DoesNotContain(files, f => f.StartsWith("lib-hier", StringComparison.Ordinal));
        Assert.DoesNotContain(files, f => f.StartsWith("lib-status", StringComparison.Ordinal));
        Assert.DoesNotContain(files, f => f.StartsWith("lib-usage", StringComparison.Ordinal));
        Assert.False(RosterTableFns.OwnsTable("lib-peer", "read"));
        Assert.False(RosterTableFns.OwnsTable("lib-status", "clear"));
    }

    [Fact]
    public void EveryRosterTableOfADispatchedFunctionIsRun()
    {
        var files = OwnedCases().Select(c => c.File).ToHashSet(StringComparer.Ordinal);
        var expected = UnitTable.TableFiles(NativeRoot)
            .Select(f => (File: f, Table: UnitTable.Load(f)))
            .Where(x => RosterTableFns.Libs.Contains(x.Table.Lib) && RosterTableFns.All.Contains(RosterTableFns.KeyOf(x.Table.Lib, x.Table.Fn)) && x.Table.Cases.Count > 0)
            .Select(x => Path.GetRelativePath(Path.Combine(NativeRoot, "..", "tests", "contracts", "units"), x.File))
            .ToList();

        Assert.NotEmpty(expected);
        Assert.Contains(files, f => f == Path.Combine("lib-roster", "read.json"));
        Assert.Contains(files, f => f == Path.Combine("lib-links", "read.json"));
        Assert.All(expected, f => Assert.Contains(f, files));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void CaseMatchesNode(string tableFile, string caseId)
    {
        var table = UnitTable.Load(Path.Combine(NativeRoot, "..", "tests", "contracts", "units", tableFile));
        var c = table.Cases.Single(x => x.Id == caseId);
        var sandbox = UnitSandbox.Create();
        sandbox.WriteFiles(c);
        JsonValue? result = null;
        var threw = false;
        try
        {
            result = RosterTableFns.Dispatch(table.Lib, table.Fn, c, sandbox);
        }
        catch (Exception)
        {
            threw = true;
        }

        // a team's links record lives in the links table (per-dir-files-to-db S11b): an expected links/<team>.json is compared against the row
        foreach (var rel in c.ExpectFiles?.Keys ?? [])
        {
            if (Path.GetFileName(Path.GetDirectoryName(rel)) != "links" || Path.GetExtension(rel) != ".json")
            {
                continue;
            }

            var file = Path.Combine(sandbox.Root, sandbox.ReplaceRoot(rel));
            var dir = Path.GetDirectoryName(Path.GetDirectoryName(file))!;
            if (Ah.StateDb.StateDbModule.CreateLinks(new FakeEnvironment(sandbox.Environment(c)).AhHome).Body(dir, Path.GetFileNameWithoutExtension(file)) is { } body)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, body);
            }
        }

        Assert.True(sandbox.Verify(c, result, threw) is null, $"{table.Fn} / {c.Id}: {sandbox.Verify(c, result, threw)}");
    }
}
