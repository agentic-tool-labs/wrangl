using System.Reflection;
using Ah.Core.Json;
using Ah.TestKit;

namespace Ah.Launch.Tests;

/// <summary>
/// Runs the unit tables of the launch core against the native port. The same tables run on Node through
/// <c>tests/contract-tools/units-run.mjs</c>; a case passes only when its recorded Node result is reproduced.
/// </summary>
public sealed class LaunchUnitTableTests
{
    private static readonly string NativeRoot = typeof(LaunchUnitTableTests).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "AhNativeRoot").Value!;

    private static string UnitsDirectory => Path.Combine(NativeRoot, "..", "tests", "contracts", "units");

    private static string KeyOf(string file) =>
        Path.GetRelativePath(UnitsDirectory, file).Replace('\\', '/')[..^".json".Length];

    public static TheoryData<string, string> Cases()
    {
        var data = new TheoryData<string, string>();
        foreach (var file in UnitTable.TableFiles(NativeRoot))
        {
            var key = KeyOf(file);
            if (!LaunchTableFns.Tables.Contains(key))
            {
                continue;
            }

            foreach (var c in UnitTable.Load(file).Cases)
            {
                if (key == "roster-launch/create-plan" && !Replayable(c))
                {
                    continue;
                }

                data.Add(key, c.Id);
            }
        }

        return data;
    }

    /// <summary>A plan case R0 replays: one the CLI answered, or a custom member's refusal that the launch shape itself produces.</summary>
    private static bool Replayable(UnitCase c)
    {
        var exit = c.Expect?.Member("exit") is { IsNumber: true } e ? (int)e.AsNumber() : -1;
        var verb = c.Args.Count > 0 && c.Args[0].IsArray && c.Args[0].AsArray().Count > 0 ? c.Args[0].AsArray()[0].AsString() : string.Empty;
        return exit == 0 || verb == "spawn-ad-hoc";
    }

    [Fact]
    public void EveryDispatchedTableExists()
    {
        var present = UnitTable.TableFiles(NativeRoot).Select(KeyOf).ToHashSet();
        Assert.All(LaunchTableFns.Tables, t => Assert.Contains(t, present));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void CaseMatchesNode(string key, string caseId)
    {
        var table = UnitTable.Load(Path.Combine(UnitsDirectory, key + ".json"));
        var c = table.Cases.Single(x => x.Id == caseId);
        var sandbox = UnitSandbox.Create();
        sandbox.WriteFiles(c);
        JsonValue? result = null;
        string? error = null;
        var threw = false;
        try
        {
            result = LaunchTableFns.Dispatch(key, c, sandbox);
        }
        catch (Exception err) when (err is InvalidOperationException or ArgumentException or KeyNotFoundException)
        {
            threw = true;
            error = err.Message;
        }

        if (c.ExpectThrows)
        {
            Assert.True(threw, $"{key} / {caseId}: expected the call to throw");
            if (c.ExpectError is { } want)
            {
                Assert.Equal(want, ContractOf(sandbox, error!));
            }

            return;
        }

        Assert.False(threw, $"{key} / {caseId}: the call threw: {error}");
        if (key == "roster-launch/create-plan" && c.Expect is { } expected)
        {
            var wantJson = JsonText.Stringify(LaunchTableFns.ProjectExpected(key, expected), 0);
            var gotJson = LaunchTableFns.WithoutMemberMarker(JsonText.Stringify(sandbox.Encode(result), 0));
            Assert.True(wantJson == gotJson, $"{key} / {caseId}:\nwant {wantJson}\n got {gotJson}");
            return;
        }

        if (key == "roster-launch/next-split" && c.Expect is { } nextExpected)
        {
            var wantJson = JsonText.Stringify(nextExpected, 0);
            var gotJson = JsonText.Stringify(sandbox.Encode(result), 0);
            Assert.True(wantJson == gotJson, $"{key} / {caseId}:\nwant {wantJson}\n got {gotJson}");
            return;
        }

        Assert.True(sandbox.Verify(c, result, threw) is null, $"{key} / {caseId}: {sandbox.Verify(c, result, threw)}");
    }

    private static string ContractOf(UnitSandbox sandbox, string message) =>
        sandbox.Encode(message).AsString();
}
