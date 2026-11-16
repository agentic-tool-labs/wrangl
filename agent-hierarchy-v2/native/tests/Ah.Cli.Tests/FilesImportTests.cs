using Ah.Cli.Verbs.Db;
using Ah.Core.Json;
using Ah.StateDb;

namespace Ah.Cli.Tests;

/// <summary>files-off-disk F1 smoke (§9): <c>ah db import --dir H</c> takes an untaken <c>asks-gate.jsonl</c> and a dead team's links file, records the dir complete, and a second run takes nothing.</summary>
public sealed class FilesImportTests : IDisposable
{
    private readonly Sandbox _sandbox = new();

    public void Dispose() => _sandbox.Dispose();

    private JsonValue Import(string hier)
    {
        var stdout = new StringWriter();
        Assert.Equal(0, DbVerb.Import(_sandbox.AhHome, hier, stdout));
        return JsonText.Parse(stdout.ToString().Trim());
    }

    private static double Rows(JsonValue line, string group) => line.Member("groups")!.Value.Member(group)!.Value.Member("rows")!.Value.AsNumber();

    [Fact]
    public void ImportTakesAsksGateAndADeadTeamsLinksOnceAndRecordsTheDirComplete()
    {
        var hier = Path.Combine(_sandbox.Root, "repo", ".claude", "ah2");
        Directory.CreateDirectory(Path.Combine(hier, "links"));
        File.WriteAllText(Path.Combine(hier, "asks-gate.jsonl"), "{\"at\":\"2026-11-10T00:00:00Z\",\"q\":\"gate\"}\n");
        File.WriteAllText(Path.Combine(hier, "links", "dead.json"), "{\"v\":1,\"links\":[],\"denied\":[]}");

        var first = Import(hier);
        Assert.Equal(1, Rows(first, "asks"));
        Assert.Equal(1, Rows(first, "links"));
        Assert.True(StateDbModule.CreateAsks(_sandbox.AhHome).Read(Path.Combine(hier, "asks-gate.jsonl"))!.TryLines(out var lines));
        Assert.Equal(["{\"at\":\"2026-11-10T00:00:00Z\",\"q\":\"gate\"}"], lines);
        Assert.Equal("{\"v\":1,\"links\":[],\"denied\":[]}", StateDbModule.CreateLinks(_sandbox.AhHome).Body(hier, "dead"));

        // complete: the start/admit caller skips it
        Assert.Null(StateDbModule.ImportHierDirFiles(_sandbox.AhHome, hier));

        // negative: a second explicit run takes nothing again
        var second = Import(hier);
        Assert.Equal(0, Rows(second, "asks"));
        Assert.Equal(0, Rows(second, "links"));
    }

    /// <summary>files-off-disk F8c smoke (§9): planted history and snapshot files become verbatim rows once; a name that is not a snapshot is left.</summary>
    [Fact]
    public void ImportTakesTeamHistoryAndSnapshotsOnceAndLeavesAnOddFile()
    {
        var hier = Path.Combine(_sandbox.Root, "repo", ".claude", "ah2");
        Directory.CreateDirectory(Path.Combine(hier, "snapshots"));
        File.WriteAllText(Path.Combine(hier, "team-history.json"), "not json{{{");
        File.WriteAllText(Path.Combine(hier, "snapshots", "proj-20260101T120000000Z.json"), "{\"kind\":\"ah-team-snapshot\"}\n");
        File.WriteAllText(Path.Combine(hier, "snapshots", "notes.json"), "{}");

        var first = Import(hier);
        Assert.Equal(1, Rows(first, "team-history"));
        Assert.Equal(1, Rows(first, "team-snapshots"));
        var rows = StateDbModule.OpenTeamAuthority(_sandbox.AhHome);
        Assert.Equal("not json{{{", rows.ReadHistory(hier));
        Assert.Equal("{\"kind\":\"ah-team-snapshot\"}\n", rows.ReadSnapshot(hier, "proj-20260101T120000000Z.json"));
        Assert.Equal(["proj-20260101T120000000Z.json"], rows.SnapshotFiles(hier, "proj"));
        Assert.Null(rows.ReadSnapshot(hier, "notes.json"));
        Assert.Null(StateDbModule.ImportHierDirFiles(_sandbox.AhHome, hier));

        var second = Import(hier);
        Assert.Equal(0, Rows(second, "team-history"));
        Assert.Equal(0, Rows(second, "team-snapshots"));
        Assert.True(File.Exists(Path.Combine(hier, "team-history.json")));
    }

    [Fact]
    public void AnAsksLogInFilesModeIsNotTakenAndTheNextPassAfterTheFlipToDbTakesIt()
    {
        var hier = Path.Combine(_sandbox.Root, "repo", ".claude", "ah2");
        Directory.CreateDirectory(hier);
        var log = Path.Combine(hier, "asks.jsonl");
        File.WriteAllText(log, "{\"q\":\"one\"}\n");
        void Mode(string mode) => Assert.True(StateDbModule.OpenDirect(_sandbox.AhHome).Write(tx =>
        {
            tx.Execute("INSERT INTO meta(key, value) VALUES ('mode.asks', ?1) ON CONFLICT(key) DO UPDATE SET value = excluded.value", mode);
            return 0;
        }).Ok);
        Mode("files");

        // negative: files mode takes nothing and leaves the dir incomplete
        var first = Import(hier);
        Assert.NotNull(first.Member("groups")!.Value.Member("asks")!.Value.Member("failed"));
        Mode("db");
        Assert.False(StateDbModule.CreateAsks(_sandbox.AhHome).Taken(log));
        Assert.NotNull(StateDbModule.ImportHierDirFiles(_sandbox.AhHome, hier));
        Assert.True(StateDbModule.CreateAsks(_sandbox.AhHome).Read(log)!.TryLines(out var lines));
        Assert.Equal(["{\"q\":\"one\"}"], lines);
    }

    /// <summary>files-off-disk F2a smoke (§9): a read no longer takes a file; only the import pass does.</summary>
    [Fact]
    public void AGatesFilePlantedInACompleteDirIsNotReadUntilAhDbImportTakesIt()
    {
        var hier = Path.Combine(_sandbox.Root, "repo", ".claude", "ah2");
        Directory.CreateDirectory(hier);
        _ = Import(hier);
        File.WriteAllText(Path.Combine(hier, "gates.jsonl"), "{\"n\":1}\n");

        // negative: the dir's record is complete, so neither the read nor the start/admit pass takes the planted line
        Assert.Empty(StateDbModule.CreateGates(_sandbox.AhHome).Bodies(hier));
        Assert.Null(StateDbModule.ImportHierDirFiles(_sandbox.AhHome, hier));
        Assert.Empty(StateDbModule.CreateGates(_sandbox.AhHome).Bodies(hier));

        Assert.Equal(1, Rows(Import(hier), "gates"));
        Assert.Equal(["{\"n\":1}"], StateDbModule.CreateGates(_sandbox.AhHome).Bodies(hier));
    }

    /// <summary>files-off-disk F2b smoke (§9): the same for a links file and a team file.</summary>
    [Fact]
    public void ALinksAndATeamFilePlantedInACompleteDirAreNotReadUntilAhDbImportTakesThem()
    {
        const string Links = "{\"v\":1,\"links\":[{\"id\":\"lk1\"}],\"denied\":[]}";
        const string Team = "{\"team_id\":\"t\",\"orchestrator\":{\"name\":\"orch\"},\"members\":[{\"name\":\"orch\",\"role\":\"orchestrator\"}]}\n";
        var hier = Path.Combine(_sandbox.Root, "repo", ".claude", "ah2");
        Directory.CreateDirectory(hier);
        _ = Import(hier);
        Directory.CreateDirectory(Path.Combine(hier, "links"));
        Directory.CreateDirectory(Path.Combine(hier, "teams"));
        File.WriteAllText(Path.Combine(hier, "links", "x.json"), Links);
        var teamFile = Path.Combine(hier, "teams", "t.json");
        File.WriteAllText(teamFile, Team);
        var teams = StateDbModule.OpenTeamAuthority(_sandbox.AhHome);

        // negative: the dir's record is complete, so neither a read, a listing nor the start/admit pass takes the planted files
        // (a by-path team read that misses still takes its file: every team write builds on it, atp-1518)
        Assert.Null(StateDbModule.CreateLinks(_sandbox.AhHome).Body(hier, "x"));
        Assert.Empty(teams.ListTeams(hier));
        Assert.Null(StateDbModule.ImportHierDirFiles(_sandbox.AhHome, hier));
        Assert.Null(StateDbModule.CreateLinks(_sandbox.AhHome).Body(hier, "x"));

        var line = Import(hier);
        Assert.Equal(1, Rows(line, "links"));
        Assert.Equal(1, Rows(line, "teams"));
        Assert.Equal(Links, StateDbModule.CreateLinks(_sandbox.AhHome).Body(hier, "x"));
        Assert.Equal(["t"], teams.ListTeams(hier));
    }
}
