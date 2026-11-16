using Ah.Core.Json;

namespace Ah.Hierarchy.Tests;

/// <summary>progress.plan-append keeps the fold of the plan it last saw, and folds a log changed behind it as it is now.</summary>
public sealed class PlanAppendFoldTests
{
    private static string Add(string item) => $$"""{"ts":"2026-11-01T10:00:00Z","item":"{{item}}","event":"add","by":"o","title":"T"}""";

    private static string? Refused(HierarchyReadOpsTests.Rig rig, string row, string stream = "s")
    {
        var args = new JsonObject { ["dir"] = rig.Pool, ["team"] = "t", ["stream"] = stream, ["rows"] = new JsonArray { JsonText.Parse(row) } };
        return HierarchyReadOpsTests.Parsed(HierarchyReadOpsTests.Read(rig, "progress", "plan-append", args)).Member("refused") is { IsText: true } r ? r.AsString() : null;
    }

    // per-dir-files-to-db S11c: the log lives in progress_logs, so no row is written behind the op; an old file is taken by the first append
    [Fact]
    public void AnAppendSeesTheRowsBeforeItAndTheOldFileTakenFirst()
    {
        var rig = HierarchyReadOpsTests.Make();
        Directory.CreateDirectory(Path.Combine(rig.Pool, "progress", "t"));
        File.WriteAllText(Path.Combine(rig.Pool, "progress", "t", "s.plan.jsonl"), Add("b") + "\n");

        Assert.Null(Refused(rig, Add("a")));
        Assert.Equal("item a already exists", Refused(rig, Add("a")));
        Assert.Equal("item b already exists", Refused(rig, Add("b")));

        // negative: another stream's log does not hold a
        Assert.Null(Refused(rig, Add("a"), "other"));
    }
}
