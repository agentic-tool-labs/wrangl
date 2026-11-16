using Ah.Core.Json;

namespace Ah.Hierarchy.Tests;

/// <summary>per-dir-files-to-db S11d D3: the rollup, <c>.nudged</c> and <c>sessions.jsonl</c> writes land in progress_docs, not in files.</summary>
public sealed class ProgressDocWritesTests
{
    private static JsonObject Op(ProgressDomainTests.Rig rig, string op, JsonObject args)
    {
        args["dir"] = rig.Pool;
        args["team"] = "t";
        return rig.Services.StateCalls.Progress(op, args).AsObject();
    }

    private static string? Body(ProgressDomainTests.Rig rig, string name) =>
        Op(rig, "get", new JsonObject { ["key"] = rig.Pool + "/progress/t/" + name }) is var got && got["found"].IsTruthy ? got["body"].AsString() : null;

    [Fact]
    public void TheRollupBlockAndAResumePointAreWrittenToTheRowAndTheResultKeepsTheOldPath()
    {
        var rig = ProgressDomainTests.Make();

        Assert.Equal("no rollup", Op(rig, "rollup-resume-point", new JsonObject { ["path"] = "/c/1.json" })["reason"].AsString());
        Assert.True(Op(rig, "rollup-block-upsert", new JsonObject { ["stream"] = "main", ["slug"] = "s1", ["status"] = "DONE", ["from"] = "m", ["time"] = "t0", ["path"] = "p" })["updated"].IsTruthy);
        var resume = Op(rig, "rollup-resume-point", new JsonObject { ["path"] = "/c/1.json" });

        Assert.Equal(rig.Pool + "/progress/t/rollup.md", resume["rollup"].AsString());
        Assert.EndsWith("main | s1 | DONE | m | t0 | p\n" + HierLib.RollupBlockEnd + "\nResume point: /c/1.json\n", Body(rig, "rollup.md"), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(rig.Folder, "rollup.md")));
    }

    [Fact]
    public void ARollupTakenAsBytesThatAreNotUtf8GetsTheBlockAndAResumePoint()
    {
        var rig = ProgressDomainTests.Make();
        Directory.CreateDirectory(rig.Folder);
        byte[] planted = [.. "# roll é"u8.ToArray()[..^1], (byte)'\n'];
        File.WriteAllBytes(Path.Combine(rig.Folder, "rollup.md"), planted);

        Assert.True(Op(rig, "rollup-resume-point", new JsonObject { ["path"] = "/c/1.json" })["appended"].IsTruthy);
        Assert.True(Op(rig, "rollup-block-upsert", new JsonObject { ["stream"] = "main", ["slug"] = "s1", ["status"] = "DONE", ["from"] = "m", ["time"] = "t0", ["path"] = "p" })["updated"].IsTruthy);

        Assert.Contains("Resume point: /c/1.json\n" + HierLib.RollupBlockBegin, Body(rig, "rollup.md"), StringComparison.Ordinal);
        Assert.Equal(planted, File.ReadAllBytes(Path.Combine(rig.Folder, "rollup.md")));
    }

    [Fact]
    public void APendingNudgeIsSkippedWhileTheDocumentsCannotBeReadAndComesBackAfter()
    {
        var rig = ProgressDomainTests.Make(DateTimeOffset.UtcNow);
        var response = Path.Combine(rig.Pool, "msgs", "20261016-120000-abcd--implementor--slice--response.md");
        Directory.CreateDirectory(Path.GetDirectoryName(response)!);
        Ah.TestKit.TestMsgStore.Plant(response, "---\nid: 20261016-120000-abcd\ntype: response\nfrom: implementor\n---\n\n## [0] tldr\n- [1] status: DONE\n");
        rig.Services.Progress.AppendReport(rig.Pool, "t", "main", new ProgressStreamInfo("—", "—", "—", null), "m1", response);
        Op(rig, "plan-append", new JsonObject { ["stream"] = "main", ["rows"] = new JsonArray { JsonText.Parse("""{"v":"1.1","ts":"2026-10-16T00:00:00Z","team":"t","stream":"main","item":"x","event":"add","title":"Do-x","by":"orchestrator@t","session":"s"}""") } });
        var ahHome = Path.Combine(rig.Home, ".claude", "ah2");
        void Exec(string sql) => Assert.True(Ah.StateDb.StateDbModule.OpenDirect(ahHome).Write(tx =>
        {
            tx.Execute(sql);
            return true;
        }).Ok);

        Assert.Single(rig.Services.Progress.PendingNudges(rig.Pool, "t"));

        // busy (the table cannot be read): this tick nudges nothing, rather than reading as "no .nudged lines"
        Exec("ALTER TABLE progress_docs RENAME TO progress_docs_away");
        Assert.Empty(rig.Services.Progress.PendingNudges(rig.Pool, "t"));
        Exec("ALTER TABLE progress_docs_away RENAME TO progress_docs");

        Assert.Single(rig.Services.Progress.PendingNudges(rig.Pool, "t"));
    }

    [Fact]
    public void ANudgeAndAnOrchestratorStartAreRecordedOnceInTheirRows()
    {
        var rig = ProgressDomainTests.Make();
        bool Nudge() => Op(rig, "nudged-append", new JsonObject { ["stream"] = "main", ["report_id"] = "r1" })["appended"].IsTruthy;
        bool Start() => Op(rig, "session-append", new JsonObject { ["row"] = new JsonObject { ["session_id"] = "s1", ["event"] = "orchestrator-start" } })["appended"].IsTruthy;

        Assert.True(Nudge());
        Assert.False(Nudge());
        Assert.True(Start());
        Assert.False(Start());

        Assert.EndsWith("\nmain r1\n", Body(rig, ".nudged"), StringComparison.Ordinal);
        Assert.Single(Body(rig, "sessions.jsonl")!.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.False(Directory.Exists(rig.Folder) && Directory.EnumerateFiles(rig.Folder).Any());
    }
}
